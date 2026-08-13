using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed record SearchExecutionPlan(
        QueryCommandOptions Options,
        JsonSerializerOptions JsonOptions,
        JsonSerializerOptions NdjsonOptions,
        bool ExactSearch,
        string Query,
        SearchQueryHint? ExactSubstringHint);

    private sealed record SearchRowExecution(
        FtsQueryDiagnostics FtsQueryDiagnostics,
        QueryCountResult GroupedCounts,
        List<SearchDisplayRow> DisplayRows,
        IReadOnlyList<SearchDisplayRow> SarifSourceRows,
        SearchOutputSelection Selection);

    private sealed class SearchExecutionOutcome
    {
        public string? JsonDoneTerminalLine { get; set; }
    }

    private static SearchExecutionPlan CreateSearchExecutionPlan(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions,
        bool exactSearch,
        string query)
    {
        var exactSubstringHint = SearchQueryAdvisor.BuildExactSubstringHint(
            query,
            options.RawFts,
            exactSearch,
            options.Prefix);
        var ndjsonOptions = options.JsonOutputFormat == JsonOutputFormatNdjson
            ? GetCompactJsonOptions(jsonOptions)
            : jsonOptions;
        return new SearchExecutionPlan(
            options,
            jsonOptions,
            ndjsonOptions,
            exactSearch,
            query,
            exactSubstringHint);
    }

    private static int ExecutePlainSearch(SearchExecutionPlan plan)
    {
        var outcome = new SearchExecutionOutcome();
        return WithDb(
            plan.Options,
            plan.JsonOptions,
            reader => ExecutePlainSearch(reader, plan, outcome),
            _ => WritePlainSearchTerminal(plan, outcome));
    }

    private static int ExecutePlainSearch(
        DbReader reader,
        SearchExecutionPlan plan,
        SearchExecutionOutcome outcome)
    {
        var options = plan.Options;
        if (options.GroupBy != null)
        {
            return RunGroupedSearchCount(
                reader,
                options,
                plan.JsonOptions,
                plan.ExactSearch,
                plan.ExactSubstringHint);
        }
        if (options.CountBy != null || options.UniqueBy != null)
        {
            return RunSearchAggregation(
                reader,
                options,
                plan.JsonOptions,
                plan.ExactSearch,
                plan.ExactSubstringHint);
        }
        if (options.CountOnly)
            return WritePlainSearchCount(reader, plan);

        var rows = PreparePlainSearchRows(reader, plan);
        return rows.DisplayRows.Count == 0
            ? WriteEmptyPlainSearchResults(reader, plan, rows, outcome)
            : WritePlainSearchResults(reader, plan, rows, outcome);
    }

    private static int WritePlainSearchCount(DbReader reader, SearchExecutionPlan plan)
    {
        var options = plan.Options;
        var counts = CountSearchMatches(reader, options, plan.ExactSearch);
        var queryDiagnostics = DbReader.AnalyzeFtsQuery(
            plan.Query,
            options.RawFts,
            options.Prefix,
            options.Lang);
        if (options.Json)
        {
            return WriteJsonObjectWithOptionalByteLimit(
                BuildCountJsonPayload(
                    reader,
                    plan.JsonOptions,
                    counts.Count,
                    counts.FileCount,
                    query: plan.Query,
                    queryOptions: options,
                    ftsQueryDiagnostics: queryDiagnostics,
                    exactSubstringHint: plan.ExactSubstringHint).ToJsonString(plan.JsonOptions),
                options,
                "search count",
                "Narrow the query or increase --max-json-bytes.",
                plan.JsonOptions);
        }

        Console.WriteLine($"{counts.Count}");
        WriteExactSubstringHintIfNeeded(plan.ExactSubstringHint);
        return CommandExitCodes.Success;
    }

    private static SearchRowExecution PreparePlainSearchRows(
        DbReader reader,
        SearchExecutionPlan plan)
    {
        var options = plan.Options;
        var ftsQueryDiagnostics = DbReader.AnalyzeFtsQuery(
            plan.Query,
            options.RawFts,
            options.Prefix,
            options.Lang);
        var groupedCounts = options.OutputFormat == OutputFormatGrouped
            ? CountSearchMatches(reader, options, plan.ExactSearch)
            : default;
        var displayRows = ReadSearchDisplayRows(
            reader,
            options,
            plan.ExactSearch,
            out var boundedSelection);
        var sarifSourceRows = displayRows;
        var selection = boundedSelection ?? ApplySearchOutputSelection(displayRows, options);
        return new SearchRowExecution(
            ftsQueryDiagnostics,
            groupedCounts,
            selection.Rows,
            sarifSourceRows,
            selection);
    }

    private static void WritePlainSearchTerminal(
        SearchExecutionPlan plan,
        SearchExecutionOutcome outcome)
    {
        var options = plan.Options;
        if (options.Json
            && options.JsonOutputFormat == JsonOutputFormatNdjson
            && outcome.JsonDoneTerminalLine != null
            && !options.ResultsOnly)
        {
            Console.WriteLine(outcome.JsonDoneTerminalLine);
        }
    }
}
