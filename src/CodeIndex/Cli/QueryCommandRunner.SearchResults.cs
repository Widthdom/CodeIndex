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
            var fileGroups = reader.CountSearchResultsByFile(options.Query!, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, options.GuardFilters, options.GuardWindow, options.GuardScope, options.TokenBoundary);
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
                    null,
                    null))
                .ToList();
            var normalizedGroupBy = NormalizeSearchAggregationKey(options.GroupBy!);
            var fileGroupSelection = ApplySearchGroupOutputSelection(fileCountGroups, options);

            if (options.Json)
            {
                var json = JsonSerializer.Serialize(
                        new SearchGroupedCountJsonResult(
                            JsonOutputContract.ApiVersion,
                            options.Query!,
                            normalizedGroupBy,
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
                    "Reduce --limit or increase --max-json-bytes.",
                    jsonOptions);
            }
            else
            {
                WriteSearchGroupedCounts(normalizedGroupBy, fileGroupSelection.Groups, totalCount, fileGroups.Count, fileGroupSelection.TotalGroups);
                WriteExactSubstringHintIfNeeded(exactSubstringHint);
            }

            return CommandExitCodes.Success;
        }

        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope, tokenBoundary: options.TokenBoundary);
        var displayRows = BuildSearchDisplayRows(results, options, exact);
        var groupBy = NormalizeSearchAggregationKey(options.GroupBy!);
        var groups = BuildSearchGroupedCounts(groupBy, displayRows);
        var fallbackGroupSelection = ApplySearchGroupOutputSelection(groups, options);
        var fileCount = displayRows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count();

        if (options.Json)
        {
            var json = JsonSerializer.Serialize(
                        new SearchGroupedCountJsonResult(
                            JsonOutputContract.ApiVersion,
                            options.Query!,
                            groupBy,
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
                "Reduce --limit or increase --max-json-bytes.",
                jsonOptions);
        }
        else
        {
            WriteSearchGroupedCounts(groupBy, fallbackGroupSelection.Groups, displayRows.Count, fileCount, fallbackGroupSelection.TotalGroups);
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
                        null,
                        null))
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .ToList()
            : groupBy == "return_type"
                ? rows
                    .GroupBy(row => NormalizeSearchReturnTypeGroupKey(row.Result.EnclosingSymbolReturnType), StringComparer.Ordinal)
                    .Select(group => new SearchGroupedCountItemJsonResult(
                        group.Key,
                        group.Count(),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        group.Key == NoSearchReturnTypeGroupKey ? null : group.Key))
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .ToList()
            : groupBy == "subsystem"
                ? rows
                    .GroupBy(row => BuildSearchSubsystemGroupKey(row.Result.Path), StringComparer.Ordinal)
                    .Select(group => new SearchGroupedCountItemJsonResult(
                        group.Key,
                        group.Count(),
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        group.Key == NoSearchSubsystemGroupKey ? null : group.Key))
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
                        result.EnclosingContainerName,
                        result.EnclosingSymbolReturnType);
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

    private const string NoSearchReturnTypeGroupKey = "<no return type>";
    private const string NoSearchSubsystemGroupKey = "<unknown subsystem>";

    private static string NormalizeSearchReturnTypeGroupKey(string? returnType)
        => string.IsNullOrWhiteSpace(returnType) ? NoSearchReturnTypeGroupKey : returnType.Trim();

    private static string BuildSearchSubsystemGroupKey(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("src/CodeIndex/", StringComparison.Ordinal))
        {
            var rest = normalized["src/CodeIndex/".Length..];
            var slashIndex = rest.IndexOf('/');
            if (slashIndex < 0)
                return "core";

            return NormalizeSearchSubsystemSegment(rest[..slashIndex]);
        }

        if (normalized.StartsWith("src/", StringComparison.Ordinal))
            return "source";
        if (normalized.StartsWith("tests/", StringComparison.Ordinal))
            return "tests";
        if (normalized.StartsWith("docs/", StringComparison.Ordinal))
            return "docs";
        if (normalized.StartsWith("tools/", StringComparison.Ordinal))
            return "tools";

        return NoSearchSubsystemGroupKey;
    }

    private static string NormalizeSearchSubsystemSegment(string segment)
        => segment switch
        {
            "Cli" => "cli",
            "Database" => "database",
            "Indexer" => "extractor",
            "Lsp" => "lsp",
            "Mcp" => "mcp",
            "Models" => "models",
            "Diagnostics" => "diagnostics",
            "Security" => "security",
            "Telemetry" => "telemetry",
            "Archives" => "archives",
            _ => string.IsNullOrWhiteSpace(segment)
                ? NoSearchSubsystemGroupKey
                : segment.Trim().ToLowerInvariant()
        };

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
            if (groupBy == "return_type")
            {
                Console.WriteLine($"{group.Count,8} {group.Key}");
                continue;
            }
            if (groupBy == "subsystem")
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
        var results = reader.Search(options.Query!, int.MaxValue, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, guardFilters: options.GuardFilters, guardWindow: options.GuardWindow, guardScope: options.GuardScope, tokenBoundary: options.TokenBoundary);
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
                "Reduce --limit or increase --max-json-bytes.",
                jsonOptions);
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
        => key switch
        {
            "path" => "file",
            "return-type" or "return_type" or "nullable-return-type" or "nullable_return_type" => "return_type",
            "sub-system" or "sub_system" => "subsystem",
            _ => key
        };

    private static bool IsSupportedSearchGroupByValue(string value)
        => value != "path" && IsSupportedSearchAggregationValue(value);

    private static bool IsSupportedSearchAggregationValue(string value)
        => NormalizeSearchAggregationKey(value) is "file" or "symbol" or "origin" or "return_type" or "subsystem";

    private static SearchOutputSelection ApplySearchOutputSelection(List<SearchDisplayRow> rows, QueryCommandOptions options)
        => ApplySearchOutputSelection(
            rows,
            options,
            options.Limit,
            options.GuardFilters.Count == 0
            && !HasSearchOriginFilters(options)
            && (!options.FirstPerFile && !options.SampleSize.HasValue
                || rows.Count < SearchOriginFilterMaxCandidates));

    private static SearchOutputSelection ApplySearchOutputSelection(
        List<SearchDisplayRow> rows,
        QueryCommandOptions options,
        int limit,
        bool sourceTotalAuthoritative = true)
    {
        var sourceTotal = rows.Count;
        var selectors = new List<SearchRowSelectorJsonResult>();
        rows = ApplySearchPostSelectors(rows, options, selectors);
        var selectedTotal = rows.Count;
        var limitTruncated = rows.Count > limit;
        if (limitTruncated)
            rows = rows.Take(limit).ToList();

        var selectionReason = selectors.FirstOrDefault(selector => selector.OmittedCount > 0)?.Mode;
        var truncationReason = selectionReason ?? (limitTruncated ? "limit" : null);
        return new SearchOutputSelection(
            rows,
            sourceTotal,
            selectedTotal,
            rows.Count,
            Math.Max(0, sourceTotal - selectedTotal),
            Math.Max(0, selectedTotal - rows.Count),
            sourceTotalAuthoritative,
            rows.Count < sourceTotal,
            limitTruncated,
            truncationReason,
            selectors);
    }

    private static List<SearchDisplayRow> ApplySearchPostSelectors(
        List<SearchDisplayRow> rows,
        QueryCommandOptions options,
        List<SearchRowSelectorJsonResult> selectors)
    {
        if (options.FirstPerFile)
        {
            var beforeFirstPerFile = rows.Count;
            rows = rows
                .GroupBy(row => row.Result.Path, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            selectors.Add(new SearchRowSelectorJsonResult(
                "first_per_file",
                true,
                beforeFirstPerFile,
                rows.Count,
                Math.Max(0, beforeFirstPerFile - rows.Count)));
        }

        if (options.SampleSize.HasValue)
        {
            var beforeSample = rows.Count;
            if (rows.Count > options.SampleSize.Value)
                rows = SampleSearchRows(rows, options.SampleSize.Value);
            selectors.Add(new SearchRowSelectorJsonResult(
                "sample",
                true,
                beforeSample,
                rows.Count,
                Math.Max(0, beforeSample - rows.Count),
                options.SampleSize.Value,
                SearchSampleMode,
                SearchSampleSeed));
        }
        return rows;
    }

    private static List<SearchDisplayRow> SampleSearchRows(List<SearchDisplayRow> rows, int sampleSize)
    {
        if (sampleSize <= 0 || rows.Count <= sampleSize)
            return rows;
        if (sampleSize == 1)
            return [rows[0]];

        return rows
            .Select((row, index) => new
            {
                Row = row,
                Index = index,
                Key = ComputeSearchSampleKey(row, index),
            })
            .OrderBy(candidate => candidate.Key)
            .ThenBy(candidate => candidate.Index)
            .Take(sampleSize)
            .OrderBy(candidate => candidate.Index)
            .Select(candidate => candidate.Row)
            .ToList();
    }

    private static ulong ComputeSearchSampleKey(SearchDisplayRow row, int index)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis ^ (uint)SearchSampleSeed;

        Add(row.Result.Path);
        Add(row.Result.ChunkId.ToString(CultureInfo.InvariantCulture));
        Add(row.Result.StartLine.ToString(CultureInfo.InvariantCulture));
        Add(row.Result.EndLine.ToString(CultureInfo.InvariantCulture));
        Add(index.ToString(CultureInfo.InvariantCulture));
        return hash;

        void Add(string value)
        {
            foreach (var character in value)
            {
                hash ^= character;
                hash *= prime;
            }
            hash ^= 0xff;
            hash *= prime;
        }
    }

    private static int WriteGroupedSearchResults(
        List<SearchDisplayRow> rows,
        QueryCountResult matchedCounts,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        var groups = BuildSearchFileGroups(rows, options);
        var groupedMatchCount = rows.Count;
        var emittedMatchCount = groups.Sum(group => group.Results.Count);
        var omittedMatchCount = Math.Max(0, matchedCounts.Count - emittedMatchCount);
        var truncated = omittedMatchCount > 0 || groups.Any(group => group.Truncated);
        var json = JsonSerializer.Serialize(
                new SearchFileGroupedJsonResult(
                    JsonOutputContract.ApiVersion,
                    options.Query!,
                    matchedCounts.Count,
                    matchedCounts.Count,
                    groupedMatchCount,
                    emittedMatchCount,
                    omittedMatchCount,
                    groups.Count,
                    matchedCounts.FileCount,
                    rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count(),
                    matchedCounts.FileCount,
                    options.GroupedPerFileLimit,
                    truncated,
                    truncated,
                    truncated ? "Increase --limit or --per-file-limit, or use a resumable JSON envelope." : null,
                    groups),
                CliJsonSerializerContextFactory.Create(jsonOptions).SearchFileGroupedJsonResult);
        return WriteJsonObjectWithOptionalByteLimit(
            json,
            options,
            "grouped search results",
            "Reduce --limit, --per-file-limit, or increase --max-json-bytes.",
            jsonOptions);
    }

    private static QueryCountResult CountSearchMatches(DbReader reader, QueryCommandOptions options, bool exact)
        => HasSearchOriginFilters(options)
            ? CountFilteredSearchResults(reader, options, exact)
            : reader.CountSearchResults(
                options.Query!,
                options.Lang,
                options.RawFts,
                options.PathPatterns,
                options.ExcludePaths,
                options.ExcludeTests,
                !options.NoDedup,
                options.Since,
                exact,
                options.Prefix,
                !options.NoVisibilityRank,
                options.GuardFilters,
                options.GuardWindow,
                options.GuardScope,
                options.TokenBoundary);

    private static AdHocSearchSarifSourceResultCount CountAdHocSearchSarifSourceResults(
        DbReader reader,
        QueryCommandOptions options,
        bool exact,
        IReadOnlyList<SearchDisplayRow> boundedSourceRows)
    {
        if (options.GuardFilters.Count > 0)
            return CountAdHocSearchSarifResultUnits(boundedSourceRows, options.Query!, exact, authoritative: false);

        if (!exact)
            return new AdHocSearchSarifSourceResultCount(CountSearchMatches(reader, options, exact).Count, Authoritative: true);

        var rows = BuildSearchDisplayRows(
            ReadSearchResults(reader, options, exact, int.MaxValue),
            options,
            exact);
        return CountAdHocSearchSarifResultUnits(rows, options.Query!, exact, authoritative: true);
    }

    private static AdHocSearchSarifSourceResultCount CountAdHocSearchSarifResultUnits(
        IReadOnlyList<SearchDisplayRow> rows,
        string query,
        bool exact,
        bool authoritative)
    {
        var count = 0L;
        foreach (var row in rows)
        {
            count += ToSearchSarifItems(row, query, exact).LongCount();
            if (count >= int.MaxValue)
                return new AdHocSearchSarifSourceResultCount(int.MaxValue, Authoritative: false);
        }
        return new AdHocSearchSarifSourceResultCount((int)count, authoritative);
    }

    private sealed record AdHocSearchSarifSourceResultCount(int Count, bool Authoritative);

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

    private static int WriteProjectedSearchResults(
        CompactSearchResult[] results,
        int totalCount,
        bool truncated,
        string? truncationReason,
        string? selectionReason,
        int? selectionOmittedCount,
        int sourceTotal,
        bool sourceTotalAuthoritative,
        int selectedTotal,
        int selectorOmittedCount,
        int limitOmittedCount,
        List<SearchRowSelectorJsonResult> selectors,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        JsonSerializerOptions ndjsonOptions,
        DbReader reader,
        out string? terminalLine)
    {
        var projected = results.Select(result => BuildProjectedSearchResult(result, options.SearchFields!, queryName: null, recipeName: null)).ToArray();
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            terminalLine = null;
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
                "Reduce --limit, --search-fields, or use `--json=ndjson --max-json-bytes` for streaming output.",
                jsonOptions);
        }

        var records = new List<NdjsonOutputRecord>(projected.Length);
        foreach (var result in projected)
        {
            AddActiveSqliteDiagnostics(result);
            records.Add(new(result.ToJsonString(ndjsonOptions)));
        }
        var stream = WriteNdjsonStream(
            records,
            totalCount,
            options,
            ndjsonOptions,
            reader,
            "search",
            truncated,
            "Increase --limit, remove selection options, or narrow the query to retrieve the remaining search results.",
            totalCountAuthoritative: false,
            truncationReason: truncationReason,
            selectionReason: selectionReason,
            selectionOmittedCount: selectionOmittedCount,
            sourceTotal: selectors.Count > 0 ? sourceTotal : null,
            sourceTotalAuthoritative: selectors.Count > 0 ? sourceTotalAuthoritative : null,
            selectedTotal: selectors.Count > 0 ? selectedTotal : null,
            selectorOmittedCount: selectors.Count > 0 ? selectorOmittedCount : null,
            limitOmittedCount: selectors.Count > 0 ? limitOmittedCount : null,
            selectors: selectors.Count > 0 ? selectors : null);
        terminalLine = stream.TerminalLine;
        return stream.ExitCode;
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

    private static int WriteSearchNdjsonResults(
        CompactSearchResult[] results,
        int totalCount,
        bool truncated,
        string? truncationReason,
        string? selectionReason,
        int? selectionOmittedCount,
        int sourceTotal,
        bool sourceTotalAuthoritative,
        int selectedTotal,
        int selectorOmittedCount,
        int limitOmittedCount,
        List<SearchRowSelectorJsonResult> selectors,
        QueryCommandOptions options,
        JsonSerializerOptions ndjsonOptions,
        DbReader reader,
        out string? terminalLine)
    {
        var context = CliJsonSerializerContextFactory.Create(ndjsonOptions);
        var records = results
            .Select(result => new NdjsonOutputRecord(SerializeQueryJson(result, context.CompactSearchResult, ndjsonOptions)))
            .ToArray();
        var stream = WriteNdjsonStream(
            records,
            totalCount,
            options,
            ndjsonOptions,
            reader,
            "search",
            truncated,
            "Increase --limit, remove selection options, or narrow the query to retrieve the remaining search results.",
            totalCountAuthoritative: false,
            truncationReason: truncationReason,
            selectionReason: selectionReason,
            selectionOmittedCount: selectionOmittedCount,
            sourceTotal: selectors.Count > 0 ? sourceTotal : null,
            sourceTotalAuthoritative: selectors.Count > 0 ? sourceTotalAuthoritative : null,
            selectedTotal: selectors.Count > 0 ? selectedTotal : null,
            selectorOmittedCount: selectors.Count > 0 ? selectorOmittedCount : null,
            limitOmittedCount: selectors.Count > 0 ? limitOmittedCount : null,
            selectors: selectors.Count > 0 ? selectors : null);
        terminalLine = stream.TerminalLine;
        return stream.ExitCode;
    }

    private static bool WouldExceedJsonByteLimit(QueryCommandOptions options, int bytesWritten, string nextLine, out bool interrupted, out int? omittedResultBytes)
    {
        interrupted = false;
        omittedResultBytes = null;
        if (!options.MaxJsonBytes.HasValue)
            return false;
        var nextBytes = Encoding.UTF8.GetByteCount(nextLine) + Environment.NewLine.Length;
        if (bytesWritten + nextBytes <= options.MaxJsonBytes.Value)
            return false;
        interrupted = true;
        omittedResultBytes = nextBytes;
        return true;
    }

    private static bool WouldExceedJsonByteLimit(QueryCommandOptions options, int bytesWritten, string nextLine, out bool interrupted)
        => WouldExceedJsonByteLimit(options, bytesWritten, nextLine, out interrupted, out _);

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

    private sealed record SearchOutputSelection(
        List<SearchDisplayRow> Rows,
        int SourceTotal,
        int SelectedTotal,
        int Returned,
        int SelectorOmittedCount,
        int LimitOmittedCount,
        bool SourceTotalAuthoritative,
        bool Truncated,
        bool LimitTruncated,
        string? TruncationReason,
        List<SearchRowSelectorJsonResult> Selectors)
    {
        public int OriginalCount => SourceTotal;
        public int SelectionOmittedCount => SelectorOmittedCount;
    }

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
        if (options.OutputFormat is OutputFormatCount
            or OutputFormatCompact
            or OutputFormatGrouped
            or OutputFormatIssueDrafts)
            return true;
        if (options.OutputFormat == OutputFormatSarif)
            return options.RecipeName != null;
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
                BuildCompactLocationsPayload([], options, jsonOptions).ToJsonString(jsonOptions),
                options,
                "compact search results",
                "Increase --max-json-bytes or remove the byte cap.",
                jsonOptions);
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
                "Increase --max-json-bytes or remove the byte cap.",
                jsonOptions);
            return true;
        }

        return false;
    }

    private static int RunSearchNamedBatchCount(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        return WithDb(options, jsonOptions, reader =>
        {
            var freshnessContext = options.Json
                ? BuildNamedSearchFreshnessContext(
                    reader,
                    options.NamedSearchQueries,
                    options,
                    userExact)
                : null;
            var queryCounts = CountSearchNamedBatchQueryResults(
                reader,
                options,
                userExact,
                freshnessContext,
                out var total,
                out var fileCount,
                out var freshnessObservations,
                out var hasFailures);

            if (options.Json)
            {
                var freshness = BuildSearchRecipeQueryFreshness(
                    freshnessContext!,
                    freshnessObservations);
                var json = JsonSerializer.Serialize(
                    new SearchNamedBatchCountSummaryRunJsonResult(
                        JsonOutputContract.ApiVersion,
                        queryCounts.Count,
                        total,
                        fileCount,
                        freshness,
                        queryCounts),
                    CliJsonSerializerContextFactory.Create(jsonOptions).SearchNamedBatchCountSummaryRunJsonResult);
                var writeExitCode = WriteJsonObjectWithOptionalByteLimit(
                    json,
                    options,
                    "named-query count summary",
                    "Use a larger --max-json-bytes value or narrow the named-query selection.",
                    jsonOptions);
                if (writeExitCode != CommandExitCodes.Success || !hasFailures)
                    return writeExitCode;

                CommandErrorWriter.WriteStderr(
                    $"Error [{CommandErrorCodes.UsageError}]: one or more named queries failed; inspect query_freshness.invalid_query_names.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(total.ToString(CultureInfo.InvariantCulture));
            CommandErrorWriter.WriteStderr($"({total} named-query results in {fileCount} files across {queryCounts.Count} queries)");
            if (!hasFailures)
                return CommandExitCodes.Success;

            CommandErrorWriter.WriteStderr(
                $"Error [{CommandErrorCodes.UsageError}]: one or more named queries failed.");
            return CommandExitCodes.UsageError;
        });
    }

    private static int RunSearchNamedBatch(QueryCommandOptions options, JsonSerializerOptions jsonOptions, bool userExact)
    {
        return WithDb(options, jsonOptions, reader =>
        {
            var queryResults = CollectSearchNamedBatchQueryResults(reader, options, userExact, out var total);

            if (options.Json)
            {
                var json = options.OutputFormat == OutputFormatCompact
                    ? BuildCompactNamedSearchBatchPayload(queryResults, total, options, jsonOptions).ToJsonString(jsonOptions)
                    : JsonSerializer.Serialize(
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
                    "Reduce --limit, use --snippet-lines 0, or increase --max-json-bytes.",
                    jsonOptions);
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

    private static JsonObject BuildCompactNamedSearchBatchPayload(
        IReadOnlyList<SearchNamedBatchQueryResultJsonResult> queryResults,
        int total,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        var queries = new JsonArray();
        foreach (var queryResult in queryResults)
        {
            var locations = queryResult.Results.Select(result => new FormattedLocation(
                result.Path,
                result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine));
            var compactLocations = BuildCompactLocationsPayload(locations, options, jsonOptions);
            queries.Add(new JsonObject
            {
                ["name"] = queryResult.Name,
                ["query"] = queryResult.Query,
                ["count"] = queryResult.Count,
                ["truncated"] = queryResult.Truncated,
                ["truncation"] = new JsonObject
                {
                    ["limit"] = options.Limit,
                    ["limit_reached"] = queryResult.Truncated,
                },
                ["results"] = compactLocations["results"]?.DeepClone() ?? new JsonArray(),
            });
        }

        return new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["format"] = OutputFormatCompact,
            ["query_count"] = queryResults.Count,
            ["result_count"] = total,
            ["truncated"] = queryResults.Any(query => query.Truncated),
            ["queries"] = queries,
        };
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
            var preferredOriginFilterMatch = GetPreferredSearchOriginFilterMatch(compact, facetFilters);
            if (preferredOriginFilterMatch != null
                && (compact.FocusLine != preferredOriginFilterMatch.Line
                    || compact.FocusColumn != preferredOriginFilterMatch.Column))
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
                    preferredMatchLine: preferredOriginFilterMatch.Line,
                    preferredMatchColumn: preferredOriginFilterMatch.Column,
                    preferredMatchLength: preferredOriginFilterMatch.Length);
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
                    compact = RefocusSearchResultAfterDedup(result, compact, queryContext, options, exact, rawFts, keptLines);
            }

            rows.Add(new SearchDisplayRow(result, compact));
        }

        return rows;
    }

    internal static CompactSearchResult RefocusSearchResultAfterDedup(
        SearchResult result,
        CompactSearchResult previousCompact,
        SearchSnippetQueryContext queryContext,
        QueryCommandOptions options,
        bool exact,
        bool rawFts,
        List<int> keptLines)
    {
        var keptSet = keptLines.ToHashSet();
        var compact = SearchSnippetFormatter.ToCompactResult(
            result,
            queryContext,
            options.SnippetLines,
            exact,
            options.MaxLineWidth,
            result.Lang,
            options.SnippetFocus,
            exposeLiteralHighlights: exact,
            preferredMatchLine: keptLines[0]);
        SearchSnippetFormatter.ApplyOutputMetadata(compact, options.SnippetLines, options.MaxLineWidth, exact, rawFts);
        compact.MatchLines = compact.MatchLines.Where(keptSet.Contains).ToList();
        compact.Highlights = compact.Highlights.Where(highlight => keptSet.Contains(highlight.Line)).ToList();
        compact.ResultKinds = previousCompact.ResultKinds;
        compact.RiskEvidence = previousCompact.RiskEvidence;
        return compact;
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

    private static SearchMatchFacet? GetPreferredSearchOriginFilterMatch(CompactSearchResult compact, SearchDisplayFacetFilters filters)
    {
        if (!HasSearchOriginFilters(filters) || compact.MatchFacets.Count == 0)
            return null;

        return compact.MatchFacets
            .Where(facet => !IsSearchFacetExcluded(facet, filters))
            .OrderBy(facet => facet.Line)
            .ThenBy(facet => facet.Column)
            .FirstOrDefault();
    }

    private static List<SearchDisplayRow> ReadSearchDisplayRows(
        DbReader reader,
        QueryCommandOptions options,
        bool exact,
        out SearchOutputSelection? boundedSelection)
    {
        boundedSelection = null;
        var responseOffset = JsonEnvelopeWrapper.GetBoundedResponseOffset("search");
        var boundedPageLimit = JsonEnvelopeWrapper.GetBoundedResponseLimit("search");
        if (boundedPageLimit.HasValue
            && (options.FirstPerFile || options.SampleSize.HasValue))
        {
            List<SearchDisplayRow> boundedRows;
            if (!HasSearchOriginFilters(options))
            {
                boundedRows = BuildSearchDisplayRows(
                    ReadSearchResults(
                        reader,
                        options,
                        exact,
                        SearchOriginFilterMaxCandidates,
                        guardRequestedLimit: options.Limit),
                    options,
                    exact);
            }
            else
            {
                boundedRows = ReadOriginFilteredSearchDisplayRows(
                    reader,
                    options,
                    exact,
                    SearchOriginFilterMaxCandidates);
            }

            var sourceTotalAuthoritative = boundedRows.Count < SearchOriginFilterMaxCandidates
                                           && options.GuardFilters.Count == 0
                                           && !HasSearchOriginFilters(options);
            var fullSelection = ApplySearchOutputSelection(
                boundedRows,
                options,
                int.MaxValue,
                sourceTotalAuthoritative);
            var pageRows = fullSelection.Rows
                .Skip(responseOffset)
                .Take(boundedPageLimit.Value)
                .ToList();
            var limitOmittedCount = Math.Max(0, fullSelection.SelectedTotal - pageRows.Count);
            var limitTruncated = limitOmittedCount > 0;
            boundedSelection = fullSelection with
            {
                Rows = pageRows,
                Returned = pageRows.Count,
                LimitOmittedCount = limitOmittedCount,
                Truncated = pageRows.Count < fullSelection.SourceTotal,
                LimitTruncated = limitTruncated,
                TruncationReason = fullSelection.TruncationReason ?? (limitTruncated ? "limit" : null),
            };
            JsonEnvelopeWrapper.ReportBoundedResponseTotal(
                "search",
                fullSelection.SelectedTotal,
                sourceTotalAuthoritative);
            return pageRows;
        }

        var requestedLimit = GetSearchDisplayCandidateLimit(options);
        var requestedThroughOffset = responseOffset > int.MaxValue - requestedLimit
            ? int.MaxValue
            : responseOffset + requestedLimit;
        List<SearchDisplayRow> rows;
        if (!HasSearchOriginFilters(options))
        {
            rows = BuildSearchDisplayRows(
                ReadSearchResults(reader, options, exact, requestedThroughOffset),
                options,
                exact);
        }
        else
        {
            rows = ReadOriginFilteredSearchDisplayRows(reader, options, exact, requestedThroughOffset);
        }

        return responseOffset == 0
            ? rows
            : rows.Skip(responseOffset).ToList();
    }

    private static List<SearchDisplayRow> ReadOriginFilteredSearchDisplayRows(
        DbReader reader,
        QueryCommandOptions options,
        bool exact,
        int requestedLimit)
    {
        requestedLimit = Math.Max(0, requestedLimit);
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

            // The extra display candidate is only a pagination probe. Guard evaluation must
            // retain the user's requested budget or its bounded candidate scan can stop before
            // the first qualifying row.
            var page = ReadSearchResults(reader, options, exact, pageLimit, cursor, options.Limit);
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
        // Guard filtering couples its bounded candidate scan to the requested result count.
        // Overfetching here can exhaust that scan before a qualifying row is reached.
        if (options.GuardFilters.Count > 0)
            return requested;
        if (!options.FirstPerFile && !options.SampleSize.HasValue)
            return requested == int.MaxValue ? requested : requested + 1;
        return SearchOriginFilterMaxCandidates;
    }

    private static List<SearchResult> ReadSearchResults(DbReader reader, QueryCommandOptions options, bool exact, int limit, SearchCursor? cursor = null, int? guardRequestedLimit = null)
        => reader.Search(options.Query!, limit, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, cursor, options.GuardFilters, options.GuardWindow, guardRequestedLimit, guardScope: options.GuardScope, tokenBoundary: options.TokenBoundary);

    private static QueryCountResult CountFilteredSearchResults(DbReader reader, QueryCommandOptions options, bool exact)
    {
        var results = ReadSearchResults(reader, options, exact, int.MaxValue);
        var rows = BuildSearchDisplayRows(results, options, exact);
        if (options.GuardFilters.Count > 0 && !options.TokenBoundary)
            return CountFilteredSearchResultUnits(rows);

        return new QueryCountResult(
            rows.Count,
            rows.Select(row => row.Result.Path).Distinct(StringComparer.Ordinal).Count());
    }

    private static QueryCountResult CountFilteredSearchResultUnits(IReadOnlyList<SearchDisplayRow> rows)
    {
        var units = new HashSet<SearchDisplayResultUnitKey>();
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!units.Add(SearchDisplayResultUnitKey.Create(row.Result)))
                continue;

            files.Add(row.Result.Path);
        }

        return new QueryCountResult(units.Count, files.Count);
    }

    private readonly record struct SearchDisplayResultUnitKey(string Path, long ChunkId, int StartLine, int EndLine)
    {
        public static SearchDisplayResultUnitKey Create(SearchResult result)
            => result.ChunkId != 0
                ? new SearchDisplayResultUnitKey(result.Path, result.ChunkId, 0, 0)
                : new SearchDisplayResultUnitKey(result.Path, 0, result.StartLine, result.EndLine);
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

    private static IEnumerable<SarifLocation> ToSearchSarifItems(SearchDisplayRow row, string query, bool useMatchLines)
    {
        var spans = GetSearchLocationSpans(row, useMatchLines).ToList();
        if (spans.Count == 0)
        {
            yield return new SarifLocation(row.Result.Path, row.Result.StartLine, 1, null, $"search match: {query}", "search");
            yield break;
        }

        foreach (var span in spans)
            yield return new SarifLocation(row.Result.Path, span.Line, span.Column, span.Column + span.Length, $"search match: {query}", "search");
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

    private static string BuildJsonStreamDoneLine(
        int count,
        int totalCount,
        JsonSerializerOptions jsonOptions,
        bool interrupted = false,
        bool truncated = false,
        DbReader? reader = null,
        int? maxJsonBytes = null,
        int? firstOmittedResultBytes = null,
        int? omittedCount = null,
        int? omittedRecordCount = null,
        int? appliedLimit = null,
        string? recoveryGuidance = null,
        bool totalCountAuthoritative = true,
        string? truncationReason = null,
        string? selectionReason = null,
        int? selectionOmittedCount = null,
        int? sourceTotal = null,
        bool? sourceTotalAuthoritative = null,
        int? selectedTotal = null,
        int? selectorOmittedCount = null,
        int? limitOmittedCount = null,
        List<SearchRowSelectorJsonResult>? selectors = null)
    {
        var includeDiagnostics = HasReadOnlyFallbackDiagnostics(reader);
        return JsonSerializer.Serialize(
            new JsonStreamDoneResult(
                TerminalRecord: true,
                Done: !interrupted,
                Count: count,
                TotalCount: totalCount,
                Interrupted: interrupted,
                Truncated: truncated,
                HasMore: truncated || interrupted,
                TotalCountAuthoritative: totalCountAuthoritative,
                TotalCountLowerBound: totalCountAuthoritative ? null : totalCount,
                SelectionReason: selectionReason,
                SelectionOmittedCount: selectionOmittedCount,
                SourceTotal: sourceTotal,
                SourceTotalAuthoritative: sourceTotalAuthoritative,
                SourceTotalLowerBound: sourceTotalAuthoritative == false ? sourceTotal : null,
                SelectedTotal: selectedTotal,
                Returned: sourceTotal.HasValue ? count : null,
                SelectorOmittedCount: selectorOmittedCount,
                LimitOmittedCount: limitOmittedCount,
                Selectors: selectors,
                InterruptionReason: interrupted ? "max_json_bytes_exceeded" : null,
                TruncationReason: interrupted ? "max_json_bytes_exceeded" : truncated ? truncationReason ?? "limit" : null,
                AppliedLimit: truncated ? appliedLimit : null,
                MaxJsonBytes: maxJsonBytes,
                FirstOmittedResultBytes: interrupted ? firstOmittedResultBytes : null,
                OmittedCount: truncated || interrupted ? omittedCount : null,
                OmittedRecordCount: omittedRecordCount > 0 ? omittedRecordCount : null,
                RecoveryGuidance: truncated || interrupted ? recoveryGuidance : null,
                ReadOnlyFallback: includeDiagnostics ? reader!.ReadOnlyFallback : null,
                WalCheckpointAttempted: includeDiagnostics ? reader!.WalCheckpointAttempted : null,
                WalCheckpointSucceeded: includeDiagnostics ? reader!.WalCheckpointSucceeded : null,
                ReadOnlyImmutableFallback: includeDiagnostics ? reader!.ReadOnlyImmutableFallback : null,
                WalCheckpointSkippedReason: includeDiagnostics ? reader!.WalCheckpointSkippedReason : null,
                WalCheckpointFailureReason: includeDiagnostics ? reader!.WalCheckpointFailureReason : null,
                WalCheckpointBusy: includeDiagnostics ? reader!.WalCheckpointBusy : null,
                WalCheckpointLogPageCount: includeDiagnostics ? reader!.WalCheckpointLogPageCount : null,
                WalCheckpointCheckpointedPageCount: includeDiagnostics ? reader!.WalCheckpointCheckpointedPageCount : null,
                WalCheckpointRemainingPageCount: includeDiagnostics ? reader!.WalCheckpointRemainingPageCount : null,
                WalStaleSnapshotRisk: includeDiagnostics ? reader!.WalStaleSnapshotRisk : null,
                WalStaleSnapshotReason: includeDiagnostics ? reader!.WalStaleSnapshotReason : null),
            CliJsonSerializerContextFactory.Create(jsonOptions).JsonStreamDoneResult);
    }

    private static JsonSerializerOptions GetCompactJsonOptions(JsonSerializerOptions jsonOptions)
        => jsonOptions.WriteIndented ? new JsonSerializerOptions(jsonOptions) { WriteIndented = false } : jsonOptions;

    private static int WriteCompactSearchResults(
        IEnumerable<CompactSearchResult> results,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SearchOutputSelection? selection = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteCompactSearchResults(writer, results, options, jsonOptions, selection);
        return WriteJsonObjectWithOptionalByteLimit(
            writer.ToString().TrimEnd('\r', '\n'),
            options,
            "compact search results",
            "Reduce --limit, --snippet-lines, or use `--json=ndjson --max-json-bytes` for streaming output.",
            jsonOptions);
    }

    private static void WriteCompactSearchResults(
        TextWriter writer,
        IEnumerable<CompactSearchResult> results,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        SearchOutputSelection? selection = null)
    {
        var locations = results.Select(result => new FormattedLocation(
            result.Path,
            result.MatchLines.Count > 0 ? result.MatchLines[0] : result.ChunkStartLine));
        var payload = BuildCompactLocationsPayload(locations, options, jsonOptions);
        if (selection is { Selectors.Count: > 0 })
            AddSearchSelectionAccounting(payload, selection);
        writer.WriteLine(payload.ToJsonString(jsonOptions));
    }

    private static void AddSearchSelectionAccounting(JsonObject payload, SearchOutputSelection selection)
    {
        payload["source_total"] = selection.SourceTotal;
        payload["source_total_authoritative"] = selection.SourceTotalAuthoritative;
        payload["source_total_lower_bound"] = selection.SourceTotalAuthoritative
            ? null
            : selection.SourceTotal;
        payload["selected_total"] = selection.SelectedTotal;
        payload["returned"] = selection.Returned;
        payload["selector_omitted_count"] = selection.SelectorOmittedCount;
        payload["limit_omitted_count"] = selection.LimitOmittedCount;
        var selectors = new JsonArray();
        foreach (var selector in selection.Selectors)
        {
            var selectorPayload = new JsonObject
            {
                ["mode"] = selector.Mode,
                ["applied"] = selector.Applied,
                ["input_total"] = selector.InputTotal,
                ["output_total"] = selector.OutputTotal,
                ["omitted_count"] = selector.OmittedCount,
            };
            if (selector.SampleSize.HasValue)
                selectorPayload["sample_size"] = selector.SampleSize.Value;
            if (selector.SampleMode is not null)
                selectorPayload["sample_mode"] = selector.SampleMode;
            if (selector.Seed.HasValue)
                selectorPayload["seed"] = selector.Seed.Value;
            selectors.Add(selectorPayload);
        }
        payload["selectors"] = selectors;
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
