using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int RunGroupedSearchCount(DbReader reader, QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool exact, SearchQueryHint? exactSubstringHint)
    {
        if (options.GroupBy == "file" && !HasSearchOriginFilters(options))
        {
            var fileGroups = reader.CountSearchResultsByFile(options.Query!, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, options.GuardFilters, options.GuardWindow, options.GuardScope);
            var totalCount = fileGroups.Sum(group => group.Count);
            var fileCountGroups = fileGroups
                .Select(group => new SearchGroupedCountItemJsonResult(
                    group.Path,
                    group.Count,
                    group.Path,
                    null,
                    null,
                    null,
                    null,
                    null))
                .ToList();
            var fileGroupSelection = ApplySearchGroupOutputSelection(fileCountGroups, options);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchGroupedCountJsonResult(
                            JsonOutputContract.ApiVersion,
                            options.Query!,
                            options.GroupBy!,
                            totalCount,
                            fileGroups.Count,
                            fileGroupSelection.Groups.Count,
                            fileGroupSelection.TotalGroups,
                            fileGroupSelection.Truncated,
                            options.Limit,
                            fileGroupSelection.Groups),
                        CliJsonSerializerContextFactory.Create(jsonOptions).SearchGroupedCountJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "grouped search count",
                    "Reduce --limit or increase --max-json-bytes.");
            }
            else
            {
                WriteSearchGroupedCounts(options.GroupBy!, fileGroupSelection.Groups, totalCount, fileGroups.Count, fileGroupSelection.TotalGroups);
                WriteExactSubstringHintIfNeeded(exactSubstringHint);
            }

            return CommandExitCodes.Success;
        }

        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope);
        var displayRows = BuildSearchDisplayRows(results, options, exact);
        var groups = BuildSearchGroupedCounts(options.GroupBy!, displayRows);
        var fallbackGroupSelection = ApplySearchGroupOutputSelection(groups, options);
        var fileCount = displayRows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                    new SearchGroupedCountJsonResult(
                        JsonOutputContract.ApiVersion,
                        options.Query!,
                        options.GroupBy!,
                        displayRows.Count,
                        fileCount,
                        fallbackGroupSelection.Groups.Count,
                        fallbackGroupSelection.TotalGroups,
                        fallbackGroupSelection.Truncated,
                        options.Limit,
                        fallbackGroupSelection.Groups),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchGroupedCountJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "grouped search count",
                "Reduce --limit or increase --max-json-bytes.");
        }
        else
        {
            WriteSearchGroupedCounts(options.GroupBy!, fallbackGroupSelection.Groups, displayRows.Count, fileCount, fallbackGroupSelection.TotalGroups);
            WriteExactSubstringHintIfNeeded(exactSubstringHint);
        }

        return CommandExitCodes.Success;
    }

    private static List<SearchGroupedCountItemJsonResult> BuildSearchGroupedCounts(string groupBy, List<SearchDisplayRow> rows)
        => groupBy == "file"
            ? rows
                .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
                .Select(group => new SearchGroupedCountItemJsonResult(
                    group.Key,
                    group.Count(),
                    group.Key,
                    null,
                    null,
                    null,
                    null,
                    null))
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList()
            : groupBy == "origin"
                ? rows
                    .SelectMany(row => row.Compact.MatchOrigins.Count == 0
                        ? [SearchMatchClassifier.Unknown]
                        : row.Compact.MatchOrigins)
                    .GroupBy(origin => origin, StringComparer.Ordinal)
                    .Select(group => new SearchGroupedCountItemJsonResult(
                        group.Key,
                        group.Count(),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null))
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .ToList()
            : rows
                .GroupBy(row => BuildSearchSymbolGroupKey(row.Result), StringComparer.Ordinal)
                .Select(group =>
                {
                    var result = group.First().Result;
                    var key = BuildSearchSymbolDisplayKey(result);
                    return new SearchGroupedCountItemJsonResult(
                        key,
                        group.Count(),
                        result.Path,
                        result.EnclosingSymbolName,
                        result.EnclosingSymbolKind,
                        result.EnclosingSymbolStartLine,
                        result.EnclosingSymbolEndLine,
                        result.EnclosingContainerName);
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

    private static string BuildSearchSymbolGroupKey(SearchResult result)
        => result.EnclosingSymbolName == null
            ? string.Join('\0', result.Path, "<no-symbol>")
            : string.Join(
                '\0',
                result.Path,
                result.EnclosingSymbolKind ?? string.Empty,
                result.EnclosingSymbolName,
                result.EnclosingSymbolStartLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                result.EnclosingSymbolEndLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static string BuildSearchSymbolDisplayKey(SearchResult result)
    {
        if (result.EnclosingSymbolName == null)
            return $"{result.Path}:<no enclosing symbol>";

        var start = result.EnclosingSymbolStartLine?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var kind = result.EnclosingSymbolKind ?? "symbol";
        return $"{result.Path}:{start}:{kind}:{result.EnclosingSymbolName}";
    }

    private static void WriteSearchGroupedCounts(string groupBy, List<SearchGroupedCountItemJsonResult> groups, int totalCount, int fileCount, int? totalGroups = null)
    {
        foreach (var group in groups)
        {
            if (groupBy == "file")
            {
                Console.WriteLine($"{group.Count,8} {group.File}");
                continue;
            }
            if (groupBy == "origin")
            {
                Console.WriteLine($"{group.Count,8} {group.Key}");
                continue;
            }

            var location = group.SymbolStartLine.HasValue
                ? $"{group.File}:{group.SymbolStartLine}-{group.SymbolEndLine ?? group.SymbolStartLine}"
                : group.File ?? group.Key;
            var symbol = group.SymbolName == null
                ? "<no enclosing symbol>"
                : $"{group.SymbolKind ?? "symbol"} {group.SymbolName}";
            var container = group.ContainerName == null ? string.Empty : $" ({group.ContainerName})";
            Console.WriteLine($"{group.Count,8} {location} {symbol}{container}");
        }

        var truncation = totalGroups.HasValue && groups.Count < totalGroups.Value
            ? $"; showing {groups.Count} of {totalGroups.Value} groups"
            : string.Empty;
        CommandErrorWriter.WriteStderr($"({totalCount} results in {fileCount} files; grouped by {groupBy}{truncation})");
    }

    private static int RunSearchAggregation(DbReader reader, QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool exact, SearchQueryHint? exactSubstringHint)
    {
        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope);
        var rows = BuildSearchDisplayRows(results, options, exact);
        var groupBy = NormalizeSearchAggregationKey(options.CountBy ?? options.UniqueBy!);
        var groups = BuildSearchGroupedCounts(groupBy, rows);
        var selection = ApplySearchGroupOutputSelection(groups, options);
        var uniqueOnly = options.UniqueBy != null;
        var fileCount = rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                    new SearchAggregationJsonResult(
                        JsonOutputContract.ApiVersion,
                        options.Query!,
                        uniqueOnly ? "unique" : "count_by",
                        groupBy,
                        rows.Count,
                        fileCount,
                        uniqueOnly,
                        selection.Groups.Count,
                        selection.TotalGroups,
                        selection.Truncated,
                        options.Limit,
                        selection.Groups),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchAggregationJsonResult);
            return WriteJsonObjectWithOptionalByteLimit(
                json,
                options,
                "search aggregation",
                "Reduce --limit or increase --max-json-bytes.");
        }
        else
        {
            if (uniqueOnly)
            {
                foreach (var group in selection.Groups)
                    Console.WriteLine(group.Key);
                var truncation = selection.Truncated
                    ? $"showing {selection.Groups.Count} of {selection.TotalGroups}"
                    : selection.Groups.Count.ToString(CultureInfo.InvariantCulture);
                CommandErrorWriter.WriteStderr($"({truncation} unique {groupBy} values from {rows.Count} results in {fileCount} files)");
            }
            else
            {
                WriteSearchGroupedCounts(groupBy, selection.Groups, rows.Count, fileCount, selection.TotalGroups);
            }
            WriteExactSubstringHintIfNeeded(exactSubstringHint);
        }

        return CommandExitCodes.Success;
    }

    private static string NormalizeSearchAggregationKey(string key)
        => key == "path" ? "file" : key;

    private static SearchOutputSelection ApplySearchOutputSelection(List<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        var originalCount = rows.Count;
        if (options.FirstPerFile)
        {
            rows = rows
                .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        if (options.SampleSize.HasValue && rows.Count > options.SampleSize.Value)
            rows = SampleSearchRows(rows, options.SampleSize.Value);

        if (rows.Count > options.Limit)
            rows = rows.Take(options.Limit).ToList();

        return new SearchOutputSelection(rows, originalCount, rows.Count < originalCount);
    }

    private static List<SearchDisplayRow> SampleSearchRows(List<SearchDisplayRow> rows, int sampleSize)
    {
        if (sampleSize <= 0 || rows.Count <= sampleSize)
            return rows;
        if (sampleSize == 1)
            return [rows[0]];

        var sampled = new List<SearchDisplayRow>(sampleSize);
        var lastIndex = rows.Count - 1;
        for (var i = 0; i < sampleSize; i++)
        {
            var index = (int)Math.Round(i * (lastIndex / (double)(sampleSize - 1)), MidpointRounding.AwayFromZero);
            sampled.Add(rows[Math.Clamp(index, 0, lastIndex)]);
        }
        return sampled;
    }

    private static int WriteGroupedSearchResults(List<SearchDisplayRow> rows, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var groups = BuildSearchFileGroups(rows, options);
        var totalMatches = rows.Count;
        var json = JsonSerializer.Serialize(
                new SearchFileGroupedJsonResult(
                    JsonOutputContract.ApiVersion,
                    options.Query!,
                    totalMatches,
                    groups.Count,
                    rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count(),
                    options.GroupedPerFileLimit,
                    groups.Any(group => group.Truncated),
                    groups),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchFileGroupedJsonResult);
        return WriteJsonObjectWithOptionalByteLimit(
            json,
            options,
            "grouped search results",
            "Reduce --limit, --per-file-limit, or increase --max-json-bytes.");
    }

    private static void WriteGroupedSearchResultsHuman(List<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        foreach (var group in BuildSearchFileGroups(rows, options))
        {
            Console.WriteLine($"{group.Path} ({group.Count} results)");
            foreach (var result in group.Results)
            {
                Console.WriteLine($"  {result.Path}:{result.SnippetStartLine}-{result.SnippetEndLine}");
                var firstLine = result.Snippet.Split('\n', StringSplitOptions.None).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine))
                    Console.WriteLine($"    {firstLine.Trim()}");
            }
            if (group.Truncated)
                Console.WriteLine($"  ... {group.OmittedCount} more result(s)");
        }
    }

    private static List<SearchFileGroupJsonResult> BuildSearchFileGroups(List<SearchDisplayRow> rows, QueryCommandOptions options)
        => rows
            .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupRows = group.ToList();
                var representative = groupRows.Take(options.GroupedPerFileLimit).Select(row => row.Compact).ToList();
                return new SearchFileGroupJsonResult(
                    group.Key,
                    groupRows.Count,
                    representative,
                    groupRows.Count > representative.Count,
                    Math.Max(0, groupRows.Count - representative.Count));
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Path, StringComparer.Ordinal)
            .ToList();

    private static int WriteProjectedSearchResults(CompactSearchResult[] results, QueryCommandOptions options, JsonSerializerOptions jsonOptions, JsonSerializerOptions ndjsonOptions, out int emittedCount, out bool interrupted)
    {
        var projected = results.Select(result => BuildProjectedSearchResult(result, options.SearchFields!, queryName: null, recipeName: null)).ToArray();
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            emittedCount = projected.Length;
            interrupted = false;
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            WriteJsonArray(
                writer,
                projected,
                (writer, result) => writer.Write(result.ToJsonString(jsonOptions)),
                jsonOptions);
            return WriteJsonObjectWithOptionalByteLimit(
                writer.ToString().TrimEnd('\r', '\n'),
                options,
                "projected search result array",
                "Reduce --limit, --search-fields, or use `--json=ndjson --max-json-bytes` for streaming output.");
        }

        emittedCount = 0;
        interrupted = false;
        var bytesWritten = 0;
        foreach (var result in projected)
        {
            var line = result.ToJsonString(ndjsonOptions);
            if (WouldExceedJsonByteLimit(options, bytesWritten, line, out interrupted))
                break;
            Console.WriteLine(line);
            bytesWritten += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            emittedCount++;
        }

        return CommandExitCodes.Success;
    }

    private static JsonObject BuildProjectedSearchResult(
        CompactSearchResult result,
        IReadOnlyList<string> fields,
        string? queryName,
        string? recipeName)
    {
        var payload = new JsonObject();
        foreach (var field in fields)
        {
            switch (field)
            {
                case "path":
                    payload["path"] = result.Path;
                    break;
                case "line":
                    payload["line"] = result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine;
                    break;
                case "end_line":
                    payload["end_line"] = result.ChunkEndLine;
                    break;
                case "lang":
                    payload["lang"] = result.Lang;
                    break;
                case "column":
                    payload["column"] = result.MatchFacets.Count > 0 ? result.MatchFacets[0].Column : (int?)null;
                    break;
                case "symbol":
                    payload["symbol"] = result.EnclosingSymbolName;
                    break;
                case "symbol_kind":
                    payload["symbol_kind"] = result.EnclosingSymbolKind;
                    break;
                case "origin":
                    payload["match_origins"] = JsonSerializer.SerializeToNode(result.MatchOrigins);
                    break;
                case "kind":
                    payload["result_kinds"] = JsonSerializer.SerializeToNode(result.ResultKinds);
                    break;
                case "score":
                    payload["score"] = result.Score;
                    break;
                case "snippet":
                    payload["snippet"] = result.Snippet;
                    break;
                case "query_name":
                    payload["query_name"] = queryName ?? result.Query;
                    break;
                case "recipe":
                    payload["recipe"] = recipeName;
                    break;
            }
        }
        return payload;
    }

    private static void WriteSearchNdjsonResults(CompactSearchResult[] results, QueryCommandOptions options, JsonSerializerOptions ndjsonOptions, out int emittedCount, out bool interrupted)
    {
        emittedCount = 0;
        interrupted = false;
        var bytesWritten = 0;
        foreach (var result in results)
        {
            var line = JsonSerializer.Serialize(result, CliJsonSerializerContextFactory.Create(ndjsonOptions).CompactSearchResult);
            if (WouldExceedJsonByteLimit(options, bytesWritten, line, out interrupted))
                break;
            Console.WriteLine(line);
            bytesWritten += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            emittedCount++;
        }
    }

    private static bool WouldExceedJsonByteLimit(QueryCommandOptions options, int bytesWritten, string nextLine, out bool interrupted)
    {
        interrupted = false;
        if (!options.MaxJsonBytes.HasValue)
            return false;
        var nextBytes = Encoding.UTF8.GetByteCount(nextLine) + Environment.NewLine.Length;
        if (bytesWritten + nextBytes <= options.MaxJsonBytes.Value)
            return false;
        interrupted = true;
        return true;
    }

    private static void WriteSearchNextSteps(List<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        if (!options.NextSteps || rows.Count == 0)
            return;
        CommandErrorWriter.WriteStderr("Next steps:");
        foreach (var row in rows.Take(MaxSearchNextStepLimit))
        {
            var line = row.Compact.MatchLines.Count > 0 ? row.Compact.MatchLines[0] : row.Result.StartLine;
            CommandErrorWriter.WriteStderr($"  cdidx inspect --path \"{row.Result.Path}\" --line {line}");
            CommandErrorWriter.WriteStderr($"  cdidx excerpt --path \"{row.Result.Path}\" --start {Math.Max(1, line - 3)} --end {line + 3}");
        }
    }

    private static void AttachSearchNextSteps(CompactSearchResult[] results, QueryCommandOptions options)
    {
        if (!options.NextSteps || results.Length == 0)
            return;
        var truncated = results.Length > MaxSearchNextStepLimit;
        foreach (var result in results.Take(MaxSearchNextStepLimit))
        {
            var line = result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine;
            List<SearchCommandHint> nextSteps =
            [
                new SearchCommandHint
                {
                    Command = $"cdidx inspect --path \"{result.Path}\" --line {line}",
                    Purpose = "inspect the enclosing symbol for this search hit",
                },
                new SearchCommandHint
                {
                    Command = $"cdidx excerpt --path \"{result.Path}\" --start {Math.Max(1, line - 3)} --end {line + 3}",
                    Purpose = "read a bounded source excerpt around this search hit",
                },
            ];
            if (IsBareTokenSearch(options))
            {
                nextSteps.Add(new SearchCommandHint
                {
                    Command = "cdidx search --recipe auth-token-audit --exclude-tests",
                    Purpose = "narrow bare token search to credential and auth-token contexts",
                });
            }
            result.NextSteps = nextSteps;
            result.NextStepsTruncated = truncated;
        }
    }

    private sealed record SearchOutputSelection(List<SearchDisplayRow> Rows, int OriginalCount, bool Truncated);

    private sealed record SearchGroupOutputSelection(
        List<SearchGroupedCountItemJsonResult> Groups,
        int TotalGroups,
        bool Truncated);

    private static SearchGroupOutputSelection ApplySearchGroupOutputSelection(List<SearchGroupedCountItemJsonResult> groups, QueryCommandOptions options)
    {
        var totalGroups = groups.Count;
        if (groups.Count > options.Limit)
            groups = groups.Take(options.Limit).ToList();

        return new SearchGroupOutputSelection(groups, totalGroups, groups.Count < totalGroups);
    }

    private static bool SupportsSearchJsonByteLimit(QueryCommandOptions options)
    {
        if (!options.Json)
            return false;
        if (options.OutputFormat is OutputFormatCount or OutputFormatCompact or OutputFormatGrouped or OutputFormatIssueDrafts)
            return true;
        if (options.OutputFormat == OutputFormatJson)
            return options.JsonOutputFormat is JsonOutputFormatNdjson or JsonOutputFormatArray;
        return false;
    }

    private static bool TryWriteEmptySearchJsonWithOptionalByteLimit(QueryCommandOptions options, JsonSerializerOptions jsonOptions, out int exitCode)
    {
        exitCode = CommandExitCodes.Success;
        if (!options.MaxJsonBytes.HasValue)
            return false;

        if (options.OutputFormat == OutputFormatCompact)
        {
            exitCode = WriteJsonObjectWithOptionalByteLimit(
                "[]",
                options,
                "compact search results",
                "Increase --max-json-bytes or remove the byte cap.");
            return true;
        }

        if (options.OutputFormat == OutputFormatCount)
        {
            exitCode = WriteJsonObjectWithOptionalByteLimit(
                new JsonObject
                {
                    ["count"] = 0,
                    ["total_estimated"] = 0,
                }.ToJsonString(jsonOptions),
                options,
                "search count",
                "Increase --max-json-bytes or remove the byte cap.");
            return true;
        }

        return false;
    }

    private static int RunSearchNamedBatch(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchNamedBatchQueryResults(reader, options, userExact, out var total);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                    new SearchNamedBatchRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        queryResults.Count,
                        total,
                        queryResults),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchNamedBatchRunJsonResult);
                return WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "named-query search",
                    "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.");
            }

            Console.WriteLine("Named search batch");
            Console.WriteLine();
            foreach (var queryResult in queryResults)
            {
                Console.WriteLine($"[{queryResult.Name}] {queryResult.Query}");
                Console.WriteLine($"results: {queryResult.Count}");
                foreach (var result in queryResult.Results)
                {
                    Console.WriteLine($"{result.Path}:{result.ChunkStartLine}-{result.ChunkEndLine}");
                    foreach (var line in result.Snippet.Split('\n', StringSplitOptions.None))
                        Console.WriteLine($"  {line}");
                }
                Console.WriteLine();
            }

            CommandErrorWriter.WriteStderr($"({total} named-query results across {queryResults.Count} queries)");
            return CommandExitCodes.Success;
        });
    }

    private static List<SearchDisplayRow> BuildSearchDisplayRows(
        List<SearchResult> results,
        QueryCommandOptions options,
        bool exact,
        string? queryOverride = null,
        bool? rawFtsOverride = null,
        SearchAuditRecipeQuery? recipeQuery = null)
    {
        var rows = new List<SearchDisplayRow>(results.Count);
        var seenMatchLocations = options.NoDedup ? null : new HashSet<string>(StringComparer.Ordinal);
        var displayQuery = queryOverride ?? options.Query!;
        var rawFts = rawFtsOverride ?? options.RawFts;
        var facetFilters = BuildSearchDisplayFacetFilters(options, recipeQuery);
        var effectiveRawFts = rawFts && !exact;
        var queryContext = effectiveRawFts
            ? SearchSnippetFormatter.PrepareRawFtsQueryContext(displayQuery)
            : SearchSnippetFormatter.PrepareQueryContext(displayQuery);
        foreach (var result in results)
        {
            var compact = SearchSnippetFormatter.ToCompactResult(
                result,
                queryContext,
                options.SnippetLines,
                exact,
                options.MaxLineWidth,
                result.Lang,
                options.SnippetFocus,
                exposeLiteralHighlights: exact);
            var preferredOriginFilterLine = GetPreferredSearchOriginFilterLine(compact, facetFilters);
            if (preferredOriginFilterLine.HasValue && !IsLineWithinSnippet(compact, preferredOriginFilterLine.Value))
            {
                compact = SearchSnippetFormatter.ToCompactResult(
                    result,
                    queryContext,
                    options.SnippetLines,
                    exact,
                    options.MaxLineWidth,
                    result.Lang,
                    options.SnippetFocus,
                    exposeLiteralHighlights: exact,
                    preferredMatchLine: preferredOriginFilterLine.Value);
            }
            SearchSnippetFormatter.ApplyOutputMetadata(compact, options.SnippetLines, options.MaxLineWidth, exact, rawFts);

            if (!effectiveRawFts && compact.MatchLines.Count == 0 && compact.Highlights.Count == 0)
                continue;

            if (!ApplySearchOriginFilters(compact, facetFilters))
                continue;

            compact.ResultKinds = BuildSearchResultKinds(result, compact, displayQuery);
            if (!ApplySearchResultKindFilters(compact, facetFilters))
                continue;
            if (recipeQuery is { RiskEvidence.Count: > 0 })
                compact.RiskEvidence = [.. recipeQuery.RiskEvidence];

            if (seenMatchLocations != null && compact.MatchLines.Count > 0)
            {
                var keptLines = new List<int>(compact.MatchLines.Count);
                foreach (var line in compact.MatchLines)
                {
                    var key = result.Path + "\0" + line.ToString(CultureInfo.InvariantCulture);
                    if (seenMatchLocations.Add(key))
                        keptLines.Add(line);
                }

                if (keptLines.Count == 0)
                    continue;

                if (keptLines.Count != compact.MatchLines.Count)
                {
                    var keptSet = keptLines.ToHashSet();
                    compact.MatchLines = keptLines;
                    compact.Highlights = compact.Highlights
                        .Where(highlight => keptSet.Contains(highlight.Line))
                        .ToList();
                }
            }

            rows.Add(new SearchDisplayRow(result, compact));
        }

        return rows;
    }

    private sealed record SearchDisplayFacetFilters(
        bool ExcludeComments,
        bool ExcludeStrings,
        bool ExcludeFixtures,
        List<string> MatchOrigins,
        List<string> ExcludeOrigins,
        List<string> ResultKinds);

    private static SearchDisplayFacetFilters BuildSearchDisplayFacetFilters(QueryCommandOptions options, SearchAuditRecipeQuery? recipeQuery)
        => new(
            options.ExcludeComments,
            options.ExcludeStrings,
            options.ExcludeFixtures,
            CombineInclusiveSearchFilters(options.MatchOrigins, recipeQuery?.MatchOrigins),
            CombineExclusiveSearchFilters(options.ExcludeOrigins, recipeQuery?.ExcludeOrigins),
            CombineInclusiveSearchFilters(options.ResultKinds, recipeQuery?.ResultKinds));

    private static List<string> CombineInclusiveSearchFilters(IReadOnlyList<string> optionValues, IReadOnlyList<string>? recipeValues)
    {
        if (recipeValues is not { Count: > 0 })
            return [.. optionValues];
        if (optionValues.Count == 0)
            return [.. recipeValues];

        var intersected = optionValues
            .Where(value => recipeValues.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return intersected.Count == 0 ? [SearchFilterNoMatchSentinel] : intersected;
    }

    private static List<string> CombineExclusiveSearchFilters(IReadOnlyList<string> optionValues, IReadOnlyList<string>? recipeValues)
        => optionValues
            .Concat(recipeValues ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static int? GetPreferredSearchOriginFilterLine(CompactSearchResult compact, SearchDisplayFacetFilters filters)
    {
        if (!HasSearchOriginFilters(filters) || compact.MatchFacets.Count == 0)
            return null;

        return compact.MatchFacets
            .Where(facet => !IsSearchFacetExcluded(facet, filters))
            .Select(facet => (int?)facet.Line)
            .OrderBy(line => line)
            .FirstOrDefault();
    }

    private static bool IsLineWithinSnippet(CompactSearchResult compact, int line)
        => line >= compact.SnippetStartLine && line <= compact.SnippetEndLine;

    private static List<SearchDisplayRow> ReadSearchDisplayRows(DbReader reader, QueryCommandOptions options, bool exact)
    {
        if (!HasSearchOriginFilters(options))
            return BuildSearchDisplayRows(ReadSearchResults(reader, options, exact, GetSearchDisplayCandidateLimit(options)), options, exact);

        return ReadOriginFilteredSearchDisplayRows(reader, options, exact);
    }

    private static List<SearchDisplayRow> ReadOriginFilteredSearchDisplayRows(DbReader reader, QueryCommandOptions options, bool exact)
    {
        var requestedLimit = Math.Max(0, GetSearchDisplayCandidateLimit(options));
        if (requestedLimit == 0)
            return [];

        var candidateLimit = GetSearchOriginFilterCandidateLimit(requestedLimit);
        var batchLimit = GetSearchOriginFilterBatchLimit(requestedLimit);
        var candidates = new List<SearchResult>(Math.Min(candidateLimit, batchLimit));
        var displayRows = new List<SearchDisplayRow>();
        SearchCursor? cursor = null;
        var pagesRead = 0;
        while (displayRows.Count < requestedLimit && pagesRead < SearchOriginFilterMaxPages)
        {
            var currentOffset = Math.Max(0, cursor?.Offset ?? 0);
            if (currentOffset >= candidateLimit)
                break;

            var pageLimit = Math.Min(batchLimit, candidateLimit - currentOffset);
            if (pageLimit <= 0)
                break;

            var page = ReadSearchResults(reader, options, exact, pageLimit, cursor, requestedLimit);
            pagesRead++;
            if (page.Count == 0)
                break;

            candidates.AddRange(page);
            displayRows = BuildSearchDisplayRows(candidates, options, exact);

            var last = page[^1];
            if (last.NextOffset <= currentOffset)
                break;
            cursor = new SearchCursor(last.Score, last.ChunkId, last.NextOffset);
        }

        return displayRows.Count <= requestedLimit
            ? displayRows
            : displayRows.Take(requestedLimit).ToList();
    }

    private static int GetSearchOriginFilterBatchLimit(int requestedLimit)
    {
        var requested = Math.Max(1, requestedLimit);
        var overFetched = requested * SearchOriginFilterOverFetchFactor;
        return Math.Min(SearchOriginFilterMaxCandidates, Math.Max(SearchOriginFilterMinCandidates, overFetched));
    }

    private static int GetSearchOriginFilterCandidateLimit(int requestedLimit)
        => requestedLimit <= 0 ? 0 : SearchOriginFilterMaxCandidates;

    private static int GetSearchDisplayCandidateLimit(QueryCommandOptions options)
    {
        var requested = Math.Max(1, options.Limit);
        if (!options.FirstPerFile && !options.SampleSize.HasValue)
            return requested;
        var sampleTarget = Math.Max(requested, options.SampleSize ?? requested);
        return Math.Min(SearchOriginFilterMaxCandidates, Math.Max(requested, sampleTarget * SearchOriginFilterOverFetchFactor));
    }

    private static List<SearchResult> ReadSearchResults(DbReader reader, QueryCommandOptions options, bool exact, int limit, SearchCursor? cursor = null, int? guardRequestedLimit = null)
        => reader.Search(options.Query!, limit, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, cursor, options.GuardFilters, options.GuardWindow, guardRequestedLimit, guardScope: options.GuardScope);

    private static QueryCountResult CountFilteredSearchResults(DbReader reader, QueryCommandOptions options, bool exact)
    {
        var results = ReadSearchResults(reader, options, exact, int.MaxValue);
        var rows = BuildSearchDisplayRows(results, options, exact);
        return new QueryCountResult(
            rows.Count,
            rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count());
    }

    private static bool ApplySearchOriginFilters(CompactSearchResult compact, SearchDisplayFacetFilters filters)
    {
        if (!HasSearchOriginFilters(filters))
            return true;
        if (compact.MatchFacets.Count == 0)
            return filters.MatchOrigins.Count == 0;

        var keptFacets = compact.MatchFacets
            .Where(facet => !IsSearchFacetExcluded(facet, filters))
            .ToList();
        if (keptFacets.Count == 0)
            return false;

        compact.MatchFacets = keptFacets;
        compact.MatchOrigins = keptFacets
            .Select(facet => facet.Origin)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(origin => origin, StringComparer.Ordinal)
            .ToList();
        compact.TestFile = keptFacets.Any(facet => facet.TestFile);
        compact.TestSymbol = keptFacets.Any(facet => facet.TestSymbol);
        compact.TestFixture = keptFacets.Any(facet => facet.TestFixture);

        var keptLines = keptFacets.Select(facet => facet.Line).ToHashSet();
        compact.MatchLines = keptLines
            .OrderBy(line => line)
            .ToList();
        compact.Highlights = compact.Highlights
            .Where(highlight => keptLines.Contains(highlight.Line))
            .ToList();
        var keptFacetKeys = keptFacets
            .Select(facet => SearchFacetKey(facet.Line, facet.Column, facet.Length))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var highlight in compact.Highlights)
        {
            var lineFacets = keptFacets.Where(facet => facet.Line == highlight.Line).ToList();
            highlight.MatchOrigins = lineFacets
                .Select(facet => facet.Origin)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(origin => origin, StringComparer.Ordinal)
                .ToList();
            highlight.TestFile = lineFacets.Any(facet => facet.TestFile);
            highlight.TestSymbol = lineFacets.Any(facet => facet.TestSymbol);
            highlight.TestFixture = lineFacets.Any(facet => facet.TestFixture);
            highlight.TermOccurrences = FilterSearchOccurrences(highlight.TermOccurrences, highlight.Line, keptFacetKeys);
            if (highlight.LiteralTermOccurrences != null)
                highlight.LiteralTermOccurrences = FilterSearchOccurrences(highlight.LiteralTermOccurrences, highlight.Line, keptFacetKeys);
        }

        return keptFacets.Count > 0;
    }

    private static bool HasSearchOriginFilters(QueryCommandOptions options)
        => HasSearchOriginFilters(BuildSearchDisplayFacetFilters(options, recipeQuery: null));

    private static bool HasSearchOriginFilters(SearchDisplayFacetFilters filters)
        => filters.ExcludeComments ||
           filters.ExcludeStrings ||
           filters.ExcludeFixtures ||
           filters.MatchOrigins.Count > 0 ||
           filters.ExcludeOrigins.Count > 0 ||
           filters.ResultKinds.Count > 0;

    private static bool IsSearchFacetExcluded(SearchMatchFacet facet, SearchDisplayFacetFilters filters)
    {
        if (filters.ExcludeComments && string.Equals(facet.Origin, SearchMatchClassifier.Comment, StringComparison.Ordinal))
            return true;
        if (filters.ExcludeStrings && SearchMatchClassifier.IsStringLikeOrigin(facet.Origin))
            return true;
        if (filters.ExcludeFixtures && facet.TestFixture)
            return true;
        if (filters.MatchOrigins.Count > 0 && !filters.MatchOrigins.Contains(facet.Origin, StringComparer.Ordinal))
            return true;
        if (filters.ExcludeOrigins.Count > 0 && filters.ExcludeOrigins.Contains(facet.Origin, StringComparer.Ordinal))
            return true;
        return false;
    }

    private static bool ApplySearchResultKindFilters(CompactSearchResult compact, SearchDisplayFacetFilters filters)
        => filters.ResultKinds.Count == 0 || compact.ResultKinds.Any(kind => filters.ResultKinds.Contains(kind, StringComparer.Ordinal));

    private static List<string> BuildSearchResultKinds(SearchResult result, CompactSearchResult compact, string query)
    {
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var origin in compact.MatchOrigins)
            kinds.Add(origin);

        if (compact.MatchFacets.Any(facet => string.Equals(facet.Origin, SearchMatchClassifier.Code, StringComparison.Ordinal)))
            kinds.Add("identifier");

        var declarationLine = result.EnclosingSymbolStartLine;
        if (declarationLine.HasValue && compact.MatchLines.Contains(declarationLine.Value))
            kinds.Add("declaration");

        if (LooksLikeSearchCallSite(result, compact, query))
            kinds.Add("call_site");

        if (kinds.Count == 0)
            kinds.Add(SearchMatchClassifier.Unknown);
        return kinds.ToList();
    }

    private static bool LooksLikeSearchCallSite(SearchResult result, CompactSearchResult compact, string query)
    {
        var identifier = ExtractSearchIdentifierProbe(query);
        if (identifier.Length == 0)
            return false;

        var callPattern = identifier + "(";
        return compact.Highlights.Any(highlight =>
            highlight.Line != result.EnclosingSymbolStartLine &&
            highlight.MatchOrigins.Contains(SearchMatchClassifier.Code, StringComparer.Ordinal) &&
            highlight.Text.Contains(callPattern, StringComparison.Ordinal));
    }

    private static string ExtractSearchIdentifierProbe(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        var match = Regex.Match(trimmed, @"[A-Za-z_@][A-Za-z0-9_@]*(?:\.[A-Za-z_@][A-Za-z0-9_@]*)*$");
        if (!match.Success)
            return string.Empty;
        var value = match.Value;
        return value.StartsWith("@", StringComparison.Ordinal) ? value[1..] : value;
    }

    private static List<SearchTermOccurrence> FilterSearchOccurrences(List<SearchTermOccurrence> occurrences, int line, HashSet<string> keptFacetKeys)
        => occurrences
            .Where(occurrence => keptFacetKeys.Contains(SearchFacetKey(line, occurrence.Column, occurrence.Length)))
            .ToList();

    private static string SearchFacetKey(int line, int column, int length)
        => $"{line}:{column}:{length}";

    private readonly record struct SearchLocationSpan(int Line, int Column, int Length);

    private static bool TryGetSearchLocationSpan(SearchMatchFacet facet, out SearchLocationSpan span)
    {
        if (facet.Line <= 0 || facet.Column <= 0)
        {
            span = default;
            return false;
        }

        span = new SearchLocationSpan(facet.Line, facet.Column, Math.Max(1, facet.Length));
        return true;
    }

    private static bool TryGetPrimarySearchLocation(SearchDisplayRow row, out SearchLocationSpan span)
    {
        var focusLine = row.Compact.FocusLine.GetValueOrDefault();
        var focusColumn = row.Compact.FocusColumn.GetValueOrDefault();
        if (focusLine > 0)
        {
            var focusedFacet = row.Compact.MatchFacets
                .Where(facet => facet.Line == focusLine && facet.Column > 0)
                .OrderBy(facet => focusColumn > 0 ? Math.Abs(facet.Column - focusColumn) : 0)
                .ThenBy(facet => facet.Column)
                .ThenByDescending(facet => facet.Length)
                .FirstOrDefault();
            if (focusedFacet != null && TryGetSearchLocationSpan(focusedFacet, out span))
                return true;

            if (row.Compact.MatchLines.Contains(focusLine))
            {
                span = new SearchLocationSpan(focusLine, Math.Max(1, focusColumn), 1);
                return true;
            }
        }

        foreach (var facet in row.Compact.MatchFacets)
        {
            if (TryGetSearchLocationSpan(facet, out span))
                return true;
        }

        foreach (var line in row.Compact.MatchLines)
        {
            if (line > 0)
            {
                span = new SearchLocationSpan(line, 1, 1);
                return true;
            }
        }

        span = default;
        return false;
    }

    private static IEnumerable<SearchLocationSpan> GetSearchLocationSpans(SearchDisplayRow row, bool includeAllMatches)
    {
        if (includeAllMatches)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var emitted = false;
            foreach (var facet in row.Compact.MatchFacets)
            {
                if (!TryGetSearchLocationSpan(facet, out var span))
                    continue;
                if (!seen.Add(SearchFacetKey(span.Line, span.Column, span.Length)))
                    continue;

                emitted = true;
                yield return span;
            }

            if (emitted)
                yield break;

            foreach (var line in row.Compact.MatchLines)
            {
                if (line <= 0)
                    continue;
                var span = new SearchLocationSpan(line, 1, 1);
                if (!seen.Add(SearchFacetKey(span.Line, span.Column, span.Length)))
                    continue;

                emitted = true;
                yield return span;
            }

            if (emitted)
                yield break;
        }

        if (TryGetPrimarySearchLocation(row, out var primary))
            yield return primary;
    }

    private static IEnumerable<FormattedLocation> ToSearchFormattedLocations(SearchDisplayRow row, string query, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return new FormattedLocation(row.Result.Path, row.Result.StartLine, null, $"search match: {query}");
            yield break;
        }

        foreach (var span in spans)
            yield return new FormattedLocation(row.Result.Path, span.Line, span.Column, $"search match: {query}");
    }

    private static IEnumerable<LspLocation> ToSearchLspLocations(SearchDisplayRow row, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return ToLspLocation(row.Result);
            yield break;
        }

        foreach (var span in spans)
            yield return BuildLspLocation(row.Result.Path, span.Line, span.Column, span.Line, span.Column + span.Length);
    }

    private static IEnumerable<(string Path, int Line, int Column, string Message)> ToSearchQuickfixItems(SearchDisplayRow row, string query, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return (row.Result.Path, row.Result.StartLine, 1, $"search match: {query}");
            yield break;
        }

        foreach (var span in spans)
            yield return (row.Result.Path, span.Line, span.Column, $"search match: {query}");
    }

    private static IEnumerable<(string Path, int Line, int Column, string Message, string RuleId)> ToSearchSarifItems(SearchDisplayRow row, string query, bool useMatchLines)
    {
        if (!useMatchLines || row.Compact.MatchLines.Count == 0)
        {
            yield return (row.Result.Path, row.Result.StartLine, 1, $"search match: {query}", "search");
            yield break;
        }

        foreach (var line in row.Compact.MatchLines)
            yield return (row.Result.Path, line, 1, $"search match: {query}", "search");
    }

    private sealed record SearchDisplayRow(SearchResult Result, CompactSearchResult Compact);

    private static void AttachExactSubstringHint(IEnumerable<CompactSearchResult> results, SearchQueryHint? hint)
    {
        if (hint == null)
            return;
        var first = results.FirstOrDefault();
        if (first != null)
            first.ExactSubstringHint = hint;
    }

    private static void WriteJsonStreamDone(int count, JsonSerializerOptions jsonOptions, bool interrupted = false, DbReader? reader = null)
    {
        var includeDiagnostics = HasReadOnlyFallbackDiagnostics(reader);
        Console.WriteLine(JsonSerializer.Serialize(
            new JsonStreamDoneResult(
                Done: !interrupted,
                Count: count,
                Interrupted: interrupted,
                ReadOnlyFallback: includeDiagnostics ? reader!.ReadOnlyFallback : null,
                WalCheckpointAttempted: includeDiagnostics ? reader!.WalCheckpointAttempted : null,
                WalCheckpointSucceeded: includeDiagnostics ? reader!.WalCheckpointSucceeded : null,
                ReadOnlyImmutableFallback: includeDiagnostics ? reader!.ReadOnlyImmutableFallback : null,
                WalCheckpointSkippedReason: includeDiagnostics ? reader!.WalCheckpointSkippedReason : null,
                WalCheckpointFailureReason: includeDiagnostics ? reader!.WalCheckpointFailureReason : null,
                WalStaleSnapshotRisk: includeDiagnostics ? reader!.WalStaleSnapshotRisk : null,
                WalStaleSnapshotReason: includeDiagnostics ? reader!.WalStaleSnapshotReason : null),
            CliJsonSerializerContextFactory.Create(jsonOptions).JsonStreamDoneResult));
    }

    private static JsonSerializerOptions GetCompactJsonOptions(JsonSerializerOptions jsonOptions)
        => jsonOptions.WriteIndented ? new JsonSerializerOptions(jsonOptions) { WriteIndented = false } : jsonOptions;

    private static int WriteCompactSearchResults(IEnumerable<CompactSearchResult> results, QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCompactSearchResults(writer, results, jsonOptions);
        return WriteJsonObjectWithOptionalByteLimit(
            writer.ToString().TrimEnd('\r', '\n'),
            options,
            "compact search results",
            "Reduce --limit, --snippet-lines, or use `--json=ndjson --max-json-bytes` for streaming output.");
    }

    private static void WriteCompactSearchResults(TextWriter writer, IEnumerable<CompactSearchResult> results, JsonSerializerOptions jsonOptions)
    {
        var itemOptions = GetCompactJsonOptions(jsonOptions);
        var context = CliJsonSerializerContextFactory.Create(itemOptions);
        WriteJsonArray(
            writer,
            results,
            (writer, result) => writer.Write(JsonSerializer.Serialize(result, context.CompactSearchResult)),
            jsonOptions);
    }

    private static void WriteJsonArray<T>(IEnumerable<T> items, Action<TextWriter, T> writeItem, JsonSerializerOptions jsonOptions)
        => WriteJsonArray(Console.Out, items, writeItem, jsonOptions);

    private static void WriteJsonArray<T>(TextWriter writer, IEnumerable<T> items, Action<TextWriter, T> writeItem, JsonSerializerOptions jsonOptions)
    {
        if (!jsonOptions.WriteIndented)
        {
            writer.Write('[');
            var first = true;
            foreach (var item in items)
            {
                if (!first)
                    writer.Write(',');
                writeItem(writer, item);
                first = false;
            }
            writer.WriteLine(']');
            return;
        }

        writer.WriteLine("[");
        var wroteAny = false;
        foreach (var item in items)
        {
            if (wroteAny)
                writer.WriteLine(",");
            writer.Write("  ");
            writeItem(writer, item);
            wroteAny = true;
        }

        if (wroteAny)
            writer.WriteLine();
        writer.WriteLine("]");
    }

    private static void WriteDelimitedSearchResults(IEnumerable<SearchDisplayRow> rows, QueryCommandOptions options)
    {
        var delimiter = options.OutputFormat == OutputFormatTsv ? "\t" : ",";
        Console.WriteLine(string.Join(delimiter,
        [
            "file",
            "line",
            "column",
            "label",
            "query",
            "recipe",
            "query_name",
            "lang",
            "visibility",
            "enclosing_symbol_name",
            "enclosing_symbol_kind",
            "match_lines",
        ]));
        foreach (var row in rows)
        {
            var result = row.Result;
            var compact = row.Compact;
            var line = result.StartLine;
            var column = 1;
            if (TryGetPrimarySearchLocation(row, out var span))
            {
                line = span.Line;
                column = span.Column;
            }

            var values = new[]
            {
                result.Path,
                line.ToString(CultureInfo.InvariantCulture),
                column.ToString(CultureInfo.InvariantCulture),
                $"search match: {options.Query}",
                options.Query ?? string.Empty,
                string.Empty,
                string.Empty,
                result.Lang ?? string.Empty,
                result.Visibility ?? string.Empty,
                compact.EnclosingSymbolName ?? string.Empty,
                compact.EnclosingSymbolKind ?? string.Empty,
                string.Join(";", compact.MatchLines.Select(line => line.ToString(CultureInfo.InvariantCulture))),
            };
            Console.WriteLine(string.Join(delimiter, values.Select(value => EscapeDelimitedValue(value, options.OutputFormat))));
        }
    }

    private static string EscapeDelimitedValue(string value, string outputFormat)
    {
        if (outputFormat == OutputFormatTsv)
            return value.Replace("\t", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (!value.Contains('"', StringComparison.Ordinal) &&
            !value.Contains(',', StringComparison.Ordinal) &&
            !value.Contains('\r', StringComparison.Ordinal) &&
            !value.Contains('\n', StringComparison.Ordinal))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
