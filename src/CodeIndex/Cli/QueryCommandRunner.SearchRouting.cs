using System.Text.Json;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private enum SearchExecutionKind
    {
        RecipeList,
        NamedBatchCount,
        NamedBatchRows,
        RecipeAggregation,
        RecipeCount,
        RecipeIssueDrafts,
        RecipeRows,
        PlainIssueDrafts,
        PlainRows,
    }

    private readonly record struct SearchRoutePlan(
        SearchExecutionKind Execution,
        QueryCommandOptions Options,
        bool Exact,
        CancellationToken CancellationToken)
    {
        internal bool ExactSearch => Exact || Options.TokenBoundary;
        internal JsonSerializerOptions JsonOptions => GetSearchInvocationJsonOptions(Options);
    }

    private static bool TryCreateSearchRoutePlan(
        string[] cmdArgs,
        QueryCommandOptions options,
        bool exact,
        CancellationToken cancellationToken,
        out SearchRoutePlan route)
    {
        route = default;
        SearchExecutionKind execution;
        if (options.ListRecipes)
        {
            if (!TryValidateSearchRecipeListRoute(options))
                return false;
            execution = SearchExecutionKind.RecipeList;
        }
        else if (options.NamedSearchQueries.Count > 0)
        {
            if (!TryValidateSearchNamedBatchRoute(options))
                return false;
            execution = options.CountOnly || options.SummaryOnly
                ? SearchExecutionKind.NamedBatchCount
                : SearchExecutionKind.NamedBatchRows;
        }
        else if (options.RecipeName != null)
        {
            if (!TryValidateSearchRecipeRoute(options))
                return false;
            execution = GetSearchRecipeExecutionKind(options);
        }
        else
        {
            if (!TryValidatePlainSearchRoute(cmdArgs, options))
                return false;
            execution = options.OutputFormat == OutputFormatIssueDrafts
                ? SearchExecutionKind.PlainIssueDrafts
                : SearchExecutionKind.PlainRows;
        }

        route = new SearchRoutePlan(execution, options, exact, cancellationToken);
        return true;
    }

    private static int ExecuteSearchRoute(SearchRoutePlan route)
    {
        using var exactLanguageScope = CodeIndex.Database.DbReader.BeginExactQueryLanguageScope(route.Options.Lang);
        return route.Execution switch
        {
            SearchExecutionKind.RecipeList => WriteSearchRecipeList(
                route.Options,
                route.JsonOptions,
                route.Options.InvocationContext.CommandName),
            SearchExecutionKind.NamedBatchCount => RunSearchNamedBatchCount(
                route.Options,
                route.JsonOptions,
                route.ExactSearch),
            SearchExecutionKind.NamedBatchRows => RunSearchNamedBatch(
                route.Options,
                route.JsonOptions,
                route.ExactSearch),
            SearchExecutionKind.RecipeAggregation => RunSearchRecipeAggregation(
                route.Options,
                route.JsonOptions,
                route.Exact),
            SearchExecutionKind.RecipeCount => RunSearchRecipeCount(
                route.Options,
                route.JsonOptions,
                route.Exact),
            SearchExecutionKind.RecipeIssueDrafts => RunSearchRecipeIssueDrafts(
                route.Options,
                route.JsonOptions,
                route.Exact,
                route.CancellationToken),
            SearchExecutionKind.RecipeRows => RunSearchRecipe(
                route.Options,
                route.JsonOptions,
                route.Exact),
            SearchExecutionKind.PlainIssueDrafts => RunSearchIssueDrafts(
                route.Options,
                route.JsonOptions,
                route.ExactSearch,
                route.CancellationToken),
            SearchExecutionKind.PlainRows => ExecutePlainSearch(
                CreateSearchExecutionPlan(
                    route.Options,
                    route.JsonOptions,
                    route.ExactSearch,
                    route.Options.Query!)),
            _ => throw new InvalidOperationException("Unknown search execution route."),
        };
    }

    private static JsonSerializerOptions GetSearchInvocationJsonOptions(QueryCommandOptions options) =>
        options.InvocationJsonOptions
        ?? throw new InvalidOperationException("Search invocation JSON options were not initialized.");

    private static SearchExecutionKind GetSearchRecipeExecutionKind(QueryCommandOptions options)
    {
        if (options.CountOnly || (options.SummaryOnly && (options.Compact || options.OutputFormat == OutputFormatCompact)))
        {
            return HasSearchAggregation(options)
                ? SearchExecutionKind.RecipeAggregation
                : SearchExecutionKind.RecipeCount;
        }
        if (HasSearchCountOrUniqueAggregation(options))
            return SearchExecutionKind.RecipeAggregation;
        return options.OutputFormat == OutputFormatIssueDrafts
            ? SearchExecutionKind.RecipeIssueDrafts
            : SearchExecutionKind.RecipeRows;
    }

    private static bool TryValidateSearchRecipeListRoute(QueryCommandOptions options)
    {
        if (HasSearchRowSelectors(options))
        {
            return RejectSearchUsage(
                options,
                "row-selection controls are not supported with --list-recipes because recipe discovery does not emit search rows.",
                "Remove --first-per-file / --sample, or execute a recipe or plain search that returns rows.");
        }
        if (options.RecipeName != null || options.NamedSearchQueries.Count > 0 || options.ExtraNames.Count > 0)
        {
            return RejectSearchUsage(
                options,
                "--list-recipes cannot be combined with --recipe, --named-query, or extra positional arguments.",
                "Run `cdidx search --list-recipes --query <text>` to filter built-in audit recipes by recipe, query, label, severity, path, or search text.");
        }
        if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCompact)
        {
            return RejectSearchUsage(
                options,
                "--format count/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --list-recipes.",
                "Use plain text output, `--json` / `--format json` for the full recipe list, or `--format compact` for a compact summary.");
        }
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "--json=array is not supported with --list-recipes because recipe-list output is a JSON object.",
                "Use plain `--json` for the recipe-list object.");
        }

        return true;
    }

    private static bool TryValidateSearchNamedBatchRoute(QueryCommandOptions options)
    {
        if (HasSearchRowSelectors(options))
        {
            return RejectSearchUsage(
                options,
                "row-selection controls are not supported with --named-query because named batches do not expose selector accounting.",
                "Remove --first-per-file / --sample, or run each query as a plain search or recipe row output.");
        }
        if (options.Query != null || options.RecipeName != null || options.ExtraNames.Count > 0)
        {
            return RejectSearchUsage(
                options,
                "--named-query cannot be combined with a positional query, --query, --recipe, or extra positional arguments.",
                "Pass one or more `--named-query <name>=<query>` values, or run a plain `cdidx search <query>`.");
        }
        if (options.OpenIssuesPath != null)
        {
            return RejectSearchUsage(
                options,
                "--open-issues can only be used with `cdidx search --recipe <name> --format issue-drafts`.",
                "Remove --open-issues for ad hoc named batches.");
        }
        if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount and not OutputFormatCompact)
        {
            return RejectSearchUsage(
                options,
                "--format csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --named-query.",
                "Use plain text output, `--json`, `--format count`, or `--format compact` for grouped ad hoc results.");
        }
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "--json=array is not supported with --named-query because named batch output is grouped by query.",
                "Use plain `--json` for the grouped named-query object.");
        }
        if (options.MaxJsonBytes.HasValue && !options.Json)
        {
            return RejectSearchUsage(
                options,
                "--max-json-bytes is only supported with JSON search output.",
                "Use `--json` or `--format compact` with --named-query when bounding named batch output.");
        }

        return true;
    }

    private static bool TryValidateSearchRecipeRoute(QueryCommandOptions options) =>
        TryValidateSearchRecipeIdentityAndOutput(options)
        && TryValidateSearchRecipeRowSelection(options)
        && TryValidateSearchRecipeAggregation(options);

    private static bool TryValidateSearchRecipeIdentityAndOutput(QueryCommandOptions options)
    {
        if (options.TokenBoundary)
        {
            return RejectSearchUsage(
                options,
                "--token-boundary is only supported for ad hoc search and --named-query batches, not recipe execution.",
                "Run an individual query without --recipe if token-boundary filtering is required.");
        }
        if (options.Query != null || options.ExtraNames.Count > 0)
        {
            return RejectSearchUsage(
                options,
                "--recipe expands into its own curated query set and cannot be combined with a search query.",
                "Remove the positional query, or run a plain `cdidx search <query>` without --recipe.");
        }
        if (options.Prefix)
        {
            return RejectSearchUsage(
                options,
                "--prefix is not supported with --recipe because each recipe query defines its own match mode.",
                "Remove --prefix, or run the individual query from the recipe list yourself.");
        }
        if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount and not OutputFormatCompact and not OutputFormatSarif and not OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "--format csv/tsv/lsp/qf is not supported with --recipe.",
                "Use `--count` / `--format count` for count-only recipe output, `--json` for grouped recipe results, `--format compact` for summary-first compact JSON, `--format sarif` for audit findings, or `--format issue-drafts` for draft exports.");
        }
        if (options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "--json=array is not supported with --recipe because recipe output is grouped by query.",
                "Use plain `--json` for the grouped recipe object.");
        }
        if (options.OutputFormat == OutputFormatSarif
            && (options.CountOnly
                || options.SummaryOnly
                || HasSearchAggregation(options)
                || options.ResultsOnly
                || options.SearchFields != null
                || HasSearchRowSelectors(options)
                || options.GroupedPerFileLimitExplicit
                || (options.JsonOutputFormatExplicit && options.JsonOutputFormat == JsonOutputFormatNdjson)))
        {
            return RejectSearchUsage(
                options,
                "--format sarif cannot be combined with recipe count, summary, aggregation, projection, row-selection, or NDJSON controls.",
                "Use `--recipe <name> --format sarif` with result filters and `--limit` / `--total-limit`, or choose the JSON/count output shape instead.");
        }
        if (options.GroupedPerFileLimitExplicit)
        {
            return RejectSearchUsage(
                options,
                "--per-file-limit is not supported with --recipe because recipe execution does not produce grouped search output.",
                "Use --first-per-file for one selected recipe row per file, or remove --recipe and use grouped ad hoc search output.");
        }

        return true;
    }

    private static bool TryValidateSearchRecipeRowSelection(QueryCommandOptions options)
    {
        if (HasSearchRowSelectors(options) && options.SearchCursor.HasValue)
        {
            return RejectSearchUsage(
                options,
                "recipe row-selection controls cannot be combined with --cursor because raw recipe cursors cannot preserve selector state.",
                "Remove --cursor and rerun selection from the beginning, or remove --first-per-file / --sample to resume from the cursor.");
        }
        if (HasSearchRowSelectors(options)
            && (options.CountOnly
                || HasSearchAggregation(options)
                || options.ResultsOnly
                || (options.SummaryOnly && (options.Compact || options.OutputFormat == OutputFormatCompact))))
        {
            return RejectSearchUsage(
                options,
                "recipe row-selection controls cannot be combined with count, aggregation, results-only, or summary-only compact output.",
                "Remove --first-per-file / --sample to keep the non-row output, or choose text, JSON, compact, NDJSON, or issue-drafts row output.");
        }
        if (options.MaxJsonBytes.HasValue && !SupportsSearchJsonByteLimit(options))
        {
            return RejectSearchUsage(
                options,
                "--max-json-bytes is only supported with JSON search output.",
                "Use `--json=ndjson`, `--format count`, `--format compact`, grouped/count-by JSON, or `--format issue-drafts` with --max-json-bytes.");
        }
        if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
        {
            return RejectSearchUsage(
                options,
                "--results-only is only supported with NDJSON recipe output.",
                "Use `--recipe <name> --results-only --search-fields path,line,query_name`, or remove --results-only.");
        }

        return true;
    }

    private static bool TryValidateSearchRecipeAggregation(QueryCommandOptions options)
    {
        if (options.GroupBy != null)
        {
            if (!IsSupportedSearchGroupByValue(options.GroupBy))
            {
                return RejectSearchUsage(
                    options,
                    "--group-by for recipe search must be one of file, symbol, origin, return-type, or subsystem.",
                    $"Use `{options.InvocationContext.RecipeCommandPrefix} <name> --group-by file --count`, `--group-by symbol --count`, `--group-by return-type --count`, `--group-by subsystem --count`, or `--count-by origin`.");
            }
            if (!options.CountOnly)
            {
                return RejectSearchUsage(
                    options,
                    $"{options.InvocationContext.RecipeExecutionName} --group-by requires --count.",
                    "Add --count to request grouped recipe result counts, or remove --group-by to print matching snippets.");
            }
        }
        if (!TryValidateSearchAggregationFields(options, SearchAggregationTarget.Recipe))
            return false;
        if (!TryValidateSearchAggregationConflicts(options, SearchAggregationTarget.Recipe))
            return false;
        if (HasSearchAggregation(options) && (options.ResultsOnly || options.SearchFields != null))
        {
            return RejectSearchUsage(
                options,
                "recipe aggregation cannot be combined with --results-only or --search-fields.",
                "Run the aggregation separately, or remove --count-by/--group-by to stream projected recipe rows.");
        }

        return true;
    }

    private static bool TryValidatePlainSearchRoute(string[] cmdArgs, QueryCommandOptions options) =>
        TryValidatePlainSearchQuery(cmdArgs, options)
        && TryValidatePlainSearchRowSelection(options)
        && TryValidatePlainSearchAggregationShape(options);

    private static bool TryValidatePlainSearchQuery(string[] cmdArgs, QueryCommandOptions options)
    {
        if (TryWriteBlankQueryError(options, "search"))
            return false;
        if (options.Query == null)
        {
            CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                GetSearchInvocationJsonOptions(options),
                "search requires a query argument",
                CommandExitCodes.UsageError,
                BuildMissingSearchQueryHint(cmdArgs),
                GetUsageLineOrThrow("search"),
                CommandErrorCodes.UsageError,
                category: "usage");
            return false;
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
            return RejectSearchUsage(
                options,
                QueryLimits.FormatQueryTooLongError(),
                "Shorten the search text or split generated input into smaller queries before running `cdidx search`.");
        return !TryWriteUnexpectedExtraPositionals("search", options);
    }

    private static bool TryValidatePlainSearchRowSelection(QueryCommandOptions options)
    {
        if (HasSearchRowSelectors(options)
            && (options.CountOnly
                || HasSearchAggregation(options)
                || options.OutputFormat == OutputFormatGrouped))
        {
            return RejectSearchUsage(
                options,
                "search row-selection controls cannot be combined with count or aggregation output.",
                "Remove --first-per-file / --sample to count the full filtered population, or choose a row output that reports selector accounting.");
        }
        if (HasSearchRowSelectors(options) && options.ResultsOnly)
        {
            return RejectSearchUsage(
                options,
                "search row-selection controls cannot be combined with --results-only because that stream omits selector accounting.",
                "Remove --results-only to retain the NDJSON terminal record, or remove --first-per-file / --sample.");
        }
        if (HasSearchRowSelectors(options) && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "search row-selection controls cannot be combined with metadata-free --json=array output.",
                "Add --json-envelope to retain selector accounting, use --json=ndjson / --format compact, or remove --first-per-file / --sample.");
        }
        if (HasSearchRowSelectors(options)
            && options.OutputFormat is not OutputFormatText
                and not OutputFormatJson
                and not OutputFormatCompact
                and not OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "search row-selection controls are only supported by text, JSON, compact, and issue-drafts row output.",
                "Choose an output shape that reports selector accounting, or remove --first-per-file / --sample.");
        }

        return true;
    }

    private static bool TryValidatePlainSearchAggregationShape(QueryCommandOptions options)
    {
        if (options.GroupBy != null)
        {
            if (!IsSupportedSearchGroupByValue(options.GroupBy))
            {
                return RejectSearchUsage(
                    options,
                    "--group-by for search must be one of file, symbol, origin, return-type, or subsystem.",
                    "Use `cdidx search <query> --group-by file --count`, `--group-by symbol --count`, `--group-by return-type --count`, `--group-by subsystem --count`, or `--count-by origin`.");
            }
            if (!options.CountOnly)
            {
                return RejectSearchUsage(
                    options,
                    "search --group-by requires --count.",
                    "Add --count to request grouped result counts, or remove --group-by to print matching snippets.");
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount)
            {
                return RejectSearchUsage(
                    options,
                    "--group-by for search only supports plain count output or JSON.",
                    "Use `--count`, optionally with `--json`, instead of compact/location formats.");
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                return RejectSearchUsage(
                    options,
                    "--json=array is not supported with search --group-by because grouped count output is a JSON object.",
                    "Use plain `--json` for the grouped-count object.");
            }
        }
        if (!TryValidateSearchAggregationConflicts(options, SearchAggregationTarget.Plain))
            return false;
        if (HasSearchCountOrUniqueAggregation(options) && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "--json=array is not supported with search aggregation because aggregation output is a JSON object.",
                "Use plain `--json` for `--count-by` or `--unique` aggregation output.");
        }
        if (options.OutputFormat == OutputFormatGrouped && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "--json=array is not supported with search --format grouped because grouped output is a JSON object.",
                "Use plain `--json` or omit --json when using `--format grouped`.");
        }
        if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
        {
            return RejectSearchUsage(
                options,
                "--results-only is only supported with NDJSON search output.",
                "Use `--results-only --json=ndjson`, or remove --results-only when using --json=array.");
        }
        if (options.MaxJsonBytes.HasValue && !SupportsSearchJsonByteLimit(options))
        {
            return RejectSearchUsage(
                options,
                "--max-json-bytes is only supported with JSON search output.",
                "Use `--json=ndjson`, `--json=array`, `--format count`, `--format compact`, grouped/count-by JSON, or `--format issue-drafts` with --max-json-bytes.");
        }

        return TryValidateSearchAggregationFields(options, SearchAggregationTarget.Plain);
    }
}
