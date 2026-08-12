using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int WriteEmptyPlainSearchResults(
        DbReader reader,
        SearchExecutionPlan plan,
        SearchRowExecution rows,
        SearchExecutionOutcome outcome)
    {
        var options = plan.Options;
        var jsonOptions = plan.JsonOptions;
        var ndjsonOptions = plan.NdjsonOptions;
        var exactSearch = plan.ExactSearch;
        var query = plan.Query;
        var exactSubstringHint = plan.ExactSubstringHint;
        var ftsQueryDiagnostics = rows.FtsQueryDiagnostics;
        var groupedCounts = rows.GroupedCounts;
        var displayRows = rows.DisplayRows;
        var sarifSourceRows = rows.SarifSourceRows;
        var selection = rows.Selection;
        if (options.Json && (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv))
        {
            WriteDelimitedSearchResults([], options);
            return ZeroResultExitCode(options);
        }
        if (options.Json && options.OutputFormat == OutputFormatGrouped)
        {
            var groupedExitCode = WriteGroupedSearchResults([], groupedCounts, options, jsonOptions);
            return groupedExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : groupedExitCode;
        }
        if (options.Json
            && options.OutputFormat == OutputFormatCompact
            && selection.Selectors.Count > 0)
        {
            var compactExitCode = WriteCompactSearchResults([], options, jsonOptions, selection);
            return compactExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : compactExitCode;
        }
        if (options.Json && TryWriteEmptySearchJsonWithOptionalByteLimit(options, jsonOptions, out var emptyJsonExitCode))
            return emptyJsonExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : emptyJsonExitCode;
        var emptySarifRunProperties = options.OutputFormat == OutputFormatSarif
            ? BuildAdHocSearchSarifRunProperties(
                options,
                selection,
                CountAdHocSearchSarifSourceResults(reader, options, exactSearch, sarifSourceRows),
                returnedResultCount: 0)
            : null;
        if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions, emptySarifRunProperties))
            return ZeroResultExitCode(options);
        if (options.Json)
        {
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                return WriteJsonObjectWithOptionalByteLimit(
                    JsonSerializer.Serialize(
                        Array.Empty<CompactSearchResult>(),
                        CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResultArray),
                    options,
                    "search result array",
                    "Increase --max-json-bytes or remove the byte cap.",
                    jsonOptions);
            }
            else
            {
                var pathHint = BuildSearchPathGlobHint(reader, options);
                if (!options.ResultsOnly)
                {
                    var payload = BuildJsonZeroResultPayload(
                        reader,
                        ndjsonOptions,
                        resultsKey: "results",
                        query: query,
                        ftsQueryDiagnostics: ftsQueryDiagnostics,
                        queryOptions: options,
                        exactSubstringHint: exactSubstringHint,
                        extraFields: payload =>
                        {
                            AddSearchPathHint(payload, pathHint);
                            AddBareTokenSearchHint(payload, options);
                        }).ToJsonString(ndjsonOptions);
                    var stream = WriteNdjsonStream(
                        [new NdjsonOutputRecord(payload, CountsAsResult: false)],
                        totalCount: 0,
                        options,
                        ndjsonOptions,
                        reader,
                        "search",
                        limitTruncated: false,
                        "Increase --limit or narrow the query to retrieve the remaining search results.",
                        totalCountAuthoritative: false,
                        sourceTotal: selection.Selectors.Count > 0 ? selection.SourceTotal : null,
                        sourceTotalAuthoritative: selection.Selectors.Count > 0 ? selection.SourceTotalAuthoritative : null,
                        selectedTotal: selection.Selectors.Count > 0 ? selection.SelectedTotal : null,
                        selectorOmittedCount: selection.Selectors.Count > 0 ? selection.SelectorOmittedCount : null,
                        limitOmittedCount: selection.Selectors.Count > 0 ? selection.LimitOmittedCount : null,
                        selectors: selection.Selectors.Count > 0 ? selection.Selectors : null);
                    outcome.JsonDoneTerminalLine = stream.TerminalLine;
                    return stream.ExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : stream.ExitCode;
                }
            }
        }
        else if (!options.Json)
        {
            CommandErrorWriter.WriteStderr(BuildZeroResultLine("No results found", options));
            WriteLangHint(options.Lang, reader);
            WriteExactSubstringHintIfNeeded(exactSubstringHint);
            var pathHint = BuildSearchPathGlobHint(reader, options);
            WriteZeroResultHints(options, reader, filterHint: pathHint?.SuggestedAction);
        }
        return ZeroResultExitCode(options);
    }

    private static int WritePlainSearchResults(
        DbReader reader,
        SearchExecutionPlan plan,
        SearchRowExecution rows,
        SearchExecutionOutcome outcome)
    {
        var options = plan.Options;
        var jsonOptions = plan.JsonOptions;
        var ndjsonOptions = plan.NdjsonOptions;
        var exactSearch = plan.ExactSearch;
        var query = plan.Query;
        var exactSubstringHint = plan.ExactSubstringHint;
        var groupedCounts = rows.GroupedCounts;
        var displayRows = rows.DisplayRows;
        var sarifSourceRows = rows.SarifSourceRows;
        var selection = rows.Selection;
        if (options.Json)
        {
            var compactResults = displayRows.Select(row => row.Compact).ToArray();
            AttachExactSubstringHint(compactResults, exactSubstringHint);
            AttachSearchNextSteps(compactResults, options);
            if (options.SearchFields != null)
            {
                var projectedExitCode = WriteProjectedSearchResults(
                    compactResults,
                    selection.OriginalCount,
                    selection.LimitTruncated,
                    selection.LimitTruncated ? "limit" : null,
                    selection.TruncationReason is "sample" or "first_per_file" ? selection.TruncationReason : null,
                    selection.TruncationReason is "sample" or "first_per_file"
                        ? selection.SelectionOmittedCount
                        : null,
                    selection.SourceTotal,
                    selection.SourceTotalAuthoritative,
                    selection.SelectedTotal,
                    selection.SelectorOmittedCount,
                    selection.LimitOmittedCount,
                    selection.Selectors,
                    options,
                    jsonOptions,
                    ndjsonOptions,
                    reader,
                    out var projectedTerminalLine);
                outcome.JsonDoneTerminalLine = projectedTerminalLine;
                return projectedExitCode;
            }
            if (options.OutputFormat == OutputFormatCompact)
            {
                return WriteCompactSearchResults(compactResults, options, jsonOptions, selection);
            }
            if (options.OutputFormat == OutputFormatGrouped)
            {
                return WriteGroupedSearchResults(displayRows, groupedCounts, options, jsonOptions);
            }
            if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
            {
                WriteDelimitedSearchResults(displayRows, options);
                return CommandExitCodes.Success;
            }
            if (TryWriteFormattedLocations(
                options,
                displayRows.SelectMany(row => ToSearchFormattedLocations(row, query, exactSearch)).Take(options.Limit),
                jsonOptions))
                return CommandExitCodes.Success;
            if (options.OutputFormat == OutputFormatLsp)
            {
                WriteLspLocations(displayRows.SelectMany(row => ToSearchLspLocations(row, exactSearch)).Take(options.Limit), jsonOptions);
                return CommandExitCodes.Success;
            }
            if (options.OutputFormat == OutputFormatQf)
            {
                WriteQuickfix(displayRows.SelectMany(row => ToSearchQuickfixItems(row, query, exactSearch)).Take(options.Limit));
                return CommandExitCodes.Success;
            }
            if (options.OutputFormat == OutputFormatSarif)
            {
                var sarifItems = displayRows
                    .SelectMany(row => ToSearchSarifItems(row, query, exactSearch))
                    .Take(options.Limit)
                    .ToList();
                var runProperties = BuildAdHocSearchSarifRunProperties(
                    options,
                    selection,
                    CountAdHocSearchSarifSourceResults(reader, options, exactSearch, sarifSourceRows),
                    sarifItems.Count);
                WriteSarif(sarifItems, jsonOptions, runProperties: runProperties);
                return CommandExitCodes.Success;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                return WriteJsonObjectWithOptionalByteLimit(
                    JsonSerializer.Serialize(
                    compactResults,
                        CliJsonSerializerContextFactory.Create(jsonOptions).CompactSearchResultArray),
                    options,
                    "search result array",
                    "Reduce --limit, --snippet-lines, or use `--json=ndjson --max-json-bytes` for streaming output.",
                    jsonOptions);
            }
            else
            {
                var ndjsonExitCode = WriteSearchNdjsonResults(
                    compactResults,
                    selection.OriginalCount,
                    selection.LimitTruncated,
                    selection.LimitTruncated ? "limit" : null,
                    selection.TruncationReason is "sample" or "first_per_file" ? selection.TruncationReason : null,
                    selection.TruncationReason is "sample" or "first_per_file"
                        ? selection.SelectionOmittedCount
                        : null,
                    selection.SourceTotal,
                    selection.SourceTotalAuthoritative,
                    selection.SelectedTotal,
                    selection.SelectorOmittedCount,
                    selection.LimitOmittedCount,
                    selection.Selectors,
                    options,
                    ndjsonOptions,
                    reader,
                    out var ndjsonTerminalLine);
                outcome.JsonDoneTerminalLine = ndjsonTerminalLine;
                return ndjsonExitCode;
            }
        }
        else
        {
            if (options.OutputFormat == OutputFormatGrouped)
            {
                WriteGroupedSearchResultsHuman(displayRows, options);
            }
            else
            {
                foreach (var row in displayRows)
                {
                    var r = row.Result;
                    Console.WriteLine($"{r.Path}:{r.StartLine}-{r.EndLine}{FormatSearchVisibilitySuffix(r.Visibility)}");
                    var snippetLines = row.Compact.Snippet.Split('\n', StringSplitOptions.None);
                    foreach (var line in snippetLines)
                        Console.WriteLine($"  {line}");
                    Console.WriteLine();
                }
            }
            var fileCount = displayRows.Select(row => row.Result.Path).Distinct().Count();
            CommandErrorWriter.WriteStderr($"({displayRows.Count} results in {fileCount} files)");
            WriteExactSubstringHintIfNeeded(exactSubstringHint);
            WriteSearchNextSteps(displayRows, options);
        }
        return CommandExitCodes.Success;
    }
}
