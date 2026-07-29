using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunSearch(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default) =>
        RunSearchCore(
            cmdArgs,
            cmdArgs,
            QueryCommandInvocationContext.Search,
            jsonOptions,
            cancellationToken);

    internal static int RunRecipeList(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
    {
        var unexpectedPositionals = FindUnexpectedRecipePositionals(cmdArgs);
        if (unexpectedPositionals.Count > 0)
        {
            return CommandErrorWriter.Write(
                $"{ConsoleUi.Counted(unexpectedPositionals.Count, "unexpected extra positional argument")} for recipes: {string.Join(", ", unexpectedPositionals.Select(value => $"`{value}`"))}.",
                CommandExitCodes.UsageError,
                "remove the extra positional arguments, or pass a recipe-list filter with --query <text>.",
                GetUsageLineOrThrow("recipes"));
        }

        var searchArgs = new string[cmdArgs.Length + 1];
        searchArgs[0] = "--list-recipes";
        Array.Copy(cmdArgs, 0, searchArgs, 1, cmdArgs.Length);
        return RunSearchCore(
            searchArgs,
            cmdArgs,
            QueryCommandInvocationContext.Recipes,
            jsonOptions,
            cancellationToken);
    }

    private static int RunSearchCore(
        string[] cmdArgs,
        string[] validationArgs,
        QueryCommandInvocationContext invocationContext,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var previewOptionError = ValidatePreviewOptions("search", cmdArgs, allowMaxLineWidth: true, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowNamedQuery: true,
            allowIssueDraftsFormat: true,
            applySearchSourceDefaults: true);
        options.InvocationContext = invocationContext;
        options.InvocationJsonOptions = jsonOptions;
        options.InvocationMachineErrorOutputRequested = ProgramRunner.ContainsJsonOutputFlag(validationArgs);
        using var exactLanguageScope = DbReader.BeginExactQueryLanguageScope(
            options.Lang);
        if (TryWriteUnsupportedOptionError(
            invocationContext,
            validationArgs,
            CliFlagSchema.GetAcceptedFlagNamesForCommand(invocationContext.ValidationCommandName),
            options,
            options.Query,
            invocationContext.StructuredMachineUsageErrors ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(
            options,
            invocationContext,
            options.LanguageValidationError || invocationContext.StructuredMachineUsageErrors ? jsonOptions : null))
            return CommandExitCodes.UsageError;
        if (!TryResolveSearchExactMode(options, out var exact, out var exactError))
        {
            if (invocationContext.StructuredMachineUsageErrors)
            {
                WriteUsageError(
                    StripErrorPrefix(exactError!),
                    options,
                    "Use one compatible exact-search mode, or remove the exact-mode flags.");
            }
            else
            {
                CommandErrorWriter.WriteStderr(exactError);
            }
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesPath != null && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--open-issues can only be used with `cdidx search --format issue-drafts`.",
                options,
                "Use an open-issues JSON file from `gh issue list --state open --json number,title,labels,url`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesRepository != null && !IssueDuplicatePreflight.IsGitHubOpenIssuesSource(options.OpenIssuesPath))
        {
            WriteUsageError(
                "--repo can only be used with `--open-issues github`.",
                options,
                "Use `--open-issues github --repo owner/name` to fetch open issues directly from GitHub.");
            return CommandExitCodes.UsageError;
        }
        if (options.IssueState != IssueDuplicatePreflight.DefaultIssueState && !IssueDuplicatePreflight.IsGitHubOpenIssuesSource(options.OpenIssuesPath))
        {
            WriteUsageError("--issue-state can only be used with `--open-issues github`.", options, "Use `--open-issues github --repo owner/name --issue-state all`.");
            return CommandExitCodes.UsageError;
        }
        if (options.DuplicatePreflightTuningExplicit && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--duplicate-confidence and --duplicate-threshold can only be used with `cdidx search --format issue-drafts`.",
                options,
                "Use these controls when exporting issue draft JSON with duplicate-preflight metadata.");
            return CommandExitCodes.UsageError;
        }
        if ((options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0) && options.RecipeName == null)
        {
            WriteUsageError(
                "--include-query and --exclude-query can only be used with --recipe.",
                options,
                "Use `--recipe risky-code --include-query raw-diagnostic-echo` to run a child query subset.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor.HasValue && options.RecipeName == null)
        {
            WriteUsageError(
                "--cursor can only be used with --recipe.",
                options,
                "Use `--recipe risky-code/raw-diagnostic-echo --format compact --cursor <next_cursor>` to fetch the next page for one child query.");
            return CommandExitCodes.UsageError;
        }
        if (options.UnusedCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                options,
                "Use `--cursor <next_cursor>` only with `--recipe`; `unused:<offset>` cursors are for `cdidx unused`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutlineCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                options,
                "`outline:<offset>` cursors are for `cdidx outline <path>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.DependencyCycleCursor.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                options,
                "Dependency-cycle cursors are for `cdidx deps --cycles`.");
            return CommandExitCodes.UsageError;
        }
        if (options.AuditScopeExplicit && options.RecipeName == null && options.ListRecipes)
        {
            WriteUsageError(
                "--audit-scope cannot be combined with `cdidx search --list-recipes`.",
                options,
                "Use `--query <text>` with --list-recipes to filter recipe discovery, or run an ad hoc search with `--source-only`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ShowExcluded && options.RecipeName == null)
        {
            WriteUsageError(
                "--show-excluded is only supported with `cdidx search --recipe <name>`.",
                options,
                "Use it with a recipe run to include the effective scope and exclusion diagnostics in JSON output.");
            return CommandExitCodes.UsageError;
        }
        if ((options.IssueTitle != null || options.IssueLabels.Count > 0) && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--issue-title and --issue-label can only be used with `cdidx search --format issue-drafts`.",
                options,
                "Use these hints when exporting issue draft JSON for a plain search.");
            return CommandExitCodes.UsageError;
        }
        if (options.SnippetLines == 0 && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--snippet-lines 0 is only supported with --format issue-drafts.",
                options,
                "Use `--format issue-drafts --snippet-lines 0` for path/line-only draft evidence, or pass a positive snippet line count for search output.");
            return CommandExitCodes.UsageError;
        }
        if (options.IssueTitle != null && options.RecipeName != null)
        {
            WriteUsageError(
                "--issue-title is only supported for ad hoc search issue drafts.",
                options,
                "Recipe issue-drafts produce one draft per recipe query, so their titles are derived from the recipe metadata.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.CountOnly)
        {
            WriteUsageError(
                "--count cannot be combined with --format issue-drafts.",
                options,
                "Issue-draft export needs result evidence; remove --count.");
            return CommandExitCodes.UsageError;
        }
        if (options.NamesOnly && !options.ListRecipes)
        {
            WriteUsageError(
                "--names is only supported with `cdidx recipes` or `cdidx search --list-recipes`.",
                options,
                "Use `cdidx recipes --names --json` for a small deterministic recipe-name list.");
            return CommandExitCodes.UsageError;
        }
        if (options.NamesOnly && options.SummaryOnly)
        {
            WriteUsageError(
                "--names cannot be combined with --summary-only.",
                options,
                "Use one recipe-list shape at a time.");
            return CommandExitCodes.UsageError;
        }
        if (options.SummaryOnly
            && !options.ListRecipes
            && options.NamedSearchQueries.Count == 0
            && !(options.RecipeName != null
                && (options.CountOnly
                    || options.Compact
                    || options.OutputFormat == OutputFormatCompact
                    || options.OutputFormat == OutputFormatIssueDrafts)))
        {
            WriteUsageError(
                "--summary-only is only supported with `cdidx recipes` / `cdidx search --list-recipes`, named-query count output, recipe count output, or recipe issue-drafts output.",
                options,
                "Use `cdidx recipes --summary-only --json`, `cdidx search --named-query <name>=<query> --summary-only --json`, `cdidx search --recipe <name> --format compact --summary-only --json`, `cdidx search --recipe <name> --format count --summary-only`, or `cdidx search --recipe <name> --format issue-drafts --summary-only`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with --format issue-drafts because draft export is a JSON object.",
                options,
                "Use plain `--json` or omit --json when exporting issue drafts.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor.HasValue && options.OutputFormat == OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--cursor cannot be combined with --format issue-drafts.",
                options,
                "Use --cursor with recipe JSON or compact output, then export issue drafts after choosing the desired query page.");
            return CommandExitCodes.UsageError;
        }
        var exactSearch = exact || options.TokenBoundary;
        if (options.TokenBoundary && options.RawFts)
        {
            WriteSearchValidationError(
                "--token-boundary cannot be combined with --fts.",
                options,
                "Drop --fts to use exact token-boundary matching, or drop --token-boundary to keep raw FTS5 syntax.");
            return CommandExitCodes.UsageError;
        }
        if (exactSearch && options.Prefix)
        {
            WriteSearchValidationError(
                "--prefix cannot be combined with --exact / --exact-substring / --token-boundary (exact uses instr(), not FTS5 prefix phrases).",
                options,
                "Drop --prefix to keep the exact substring path, or drop the exact-mode flag to opt into FTS5 prefix matching.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null && (options.ListRecipes || options.NamedSearchQueries.Count > 0))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : "--named-query";
            WriteUsageError(
                $"--group-by is not supported with {mode}.",
                options,
                "Use `cdidx search <query> --group-by file --count` or remove --group-by for recipe-list and named-batch output.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatGrouped && (options.ListRecipes || options.NamedSearchQueries.Count > 0 || options.RecipeName != null))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : options.NamedSearchQueries.Count > 0
                    ? "--named-query"
                    : "--recipe";
            WriteUsageError(
                "--format grouped is only supported for plain search output.",
                options,
                invocationContext.RecipeNameIsPositional && mode == "--recipe"
                    ? "Run a plain `cdidx search <query> --format grouped`; audit recipe execution does not support grouped output."
                    : $"Remove {mode}, or run a plain `cdidx search <query> --format grouped`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteCappedJsonDiagnosticsUsageError(invocationContext.CommandName, options))
            return CommandExitCodes.UsageError;
        if (options.ListRecipes)
        {
            if (options.FirstPerFile || options.SampleSize.HasValue)
            {
                WriteUsageError(
                    "row-selection controls are not supported with --list-recipes because recipe discovery does not emit search rows.",
                    options,
                    "Remove --first-per-file / --sample, or execute a recipe or plain search that returns rows.");
                return CommandExitCodes.UsageError;
            }
            if (options.RecipeName != null || options.NamedSearchQueries.Count > 0 || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--list-recipes cannot be combined with --recipe, --named-query, or extra positional arguments.",
                    options,
                    "Run `cdidx search --list-recipes --query <text>` to filter built-in audit recipes by recipe, query, label, severity, path, or search text.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCompact)
            {
                WriteUsageError(
                    "--format count/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --list-recipes.",
                    options,
                    "Use plain text output, `--json` / `--format json` for the full recipe list, or `--format compact` for a compact summary.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --list-recipes because recipe-list output is a JSON object.",
                    options,
                    "Use plain `--json` for the recipe-list object.");
                return CommandExitCodes.UsageError;
            }

            return WriteSearchRecipeList(options, jsonOptions, invocationContext.CommandName);
        }
        if (options.NamedSearchQueries.Count > 0)
        {
            if (options.FirstPerFile || options.SampleSize.HasValue)
            {
                WriteUsageError(
                    "row-selection controls are not supported with --named-query because named batches do not expose selector accounting.",
                    options,
                    "Remove --first-per-file / --sample, or run each query as a plain search or recipe row output.");
                return CommandExitCodes.UsageError;
            }
            if (options.Query != null || options.RecipeName != null || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--named-query cannot be combined with a positional query, --query, --recipe, or extra positional arguments.",
                    options,
                    "Pass one or more `--named-query <name>=<query>` values, or run a plain `cdidx search <query>`.");
                return CommandExitCodes.UsageError;
            }
            if (options.OpenIssuesPath != null)
            {
                WriteUsageError(
                    "--open-issues can only be used with `cdidx search --recipe <name> --format issue-drafts`.",
                    options,
                    "Remove --open-issues for ad hoc named batches.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount and not OutputFormatCompact)
            {
                WriteUsageError(
                    "--format csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --named-query.",
                    options,
                    "Use plain text output, `--json`, `--format count`, or `--format compact` for grouped ad hoc results.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --named-query because named batch output is grouped by query.",
                    options,
                    "Use plain `--json` for the grouped named-query object.");
                return CommandExitCodes.UsageError;
            }
            if (options.MaxJsonBytes.HasValue && !options.Json)
            {
                WriteUsageError(
                    "--max-json-bytes is only supported with JSON search output.",
                    options,
                    "Use `--json` or `--format compact` with --named-query when bounding named batch output.");
                return CommandExitCodes.UsageError;
            }

            if (options.CountOnly || options.SummaryOnly)
                return RunSearchNamedBatchCount(options, jsonOptions, exactSearch);

            return RunSearchNamedBatch(options, jsonOptions, exactSearch);
        }
        if (options.RecipeName != null)
        {
            if (options.TokenBoundary)
            {
                WriteUsageError(
                    "--token-boundary is only supported for ad hoc search and --named-query batches, not recipe execution.",
                    options,
                    "Run an individual query without --recipe if token-boundary filtering is required.");
                return CommandExitCodes.UsageError;
            }
            if (options.Query != null || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--recipe expands into its own curated query set and cannot be combined with a search query.",
                    options,
                    "Remove the positional query, or run a plain `cdidx search <query>` without --recipe.");
                return CommandExitCodes.UsageError;
            }
            if (options.Prefix)
            {
                WriteUsageError(
                    "--prefix is not supported with --recipe because each recipe query defines its own match mode.",
                    options,
                    "Remove --prefix, or run the individual query from the recipe list yourself.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount and not OutputFormatCompact and not OutputFormatSarif and not OutputFormatIssueDrafts)
            {
                WriteUsageError(
                    "--format csv/tsv/lsp/qf is not supported with --recipe.",
                    options,
                    "Use `--count` / `--format count` for count-only recipe output, `--json` for grouped recipe results, `--format compact` for summary-first compact JSON, `--format sarif` for audit findings, or `--format issue-drafts` for draft exports.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --recipe because recipe output is grouped by query.",
                    options,
                    "Use plain `--json` for the grouped recipe object.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat == OutputFormatSarif
                && (options.CountOnly
                    || options.SummaryOnly
                    || options.GroupBy != null
                    || options.CountBy != null
                    || options.UniqueBy != null
                    || options.ResultsOnly
                    || options.SearchFields != null
                    || options.FirstPerFile
                    || options.SampleSize.HasValue
                    || options.GroupedPerFileLimitExplicit
                    || (options.JsonOutputFormatExplicit && options.JsonOutputFormat == JsonOutputFormatNdjson)))
            {
                WriteUsageError(
                    "--format sarif cannot be combined with recipe count, summary, aggregation, projection, row-selection, or NDJSON controls.",
                    options,
                    "Use `--recipe <name> --format sarif` with result filters and `--limit` / `--total-limit`, or choose the JSON/count output shape instead.");
                return CommandExitCodes.UsageError;
            }
            if (options.GroupedPerFileLimitExplicit)
            {
                WriteUsageError(
                    "--per-file-limit is not supported with --recipe because recipe execution does not produce grouped search output.",
                    options,
                    "Use --first-per-file for one selected recipe row per file, or remove --recipe and use grouped ad hoc search output.");
                return CommandExitCodes.UsageError;
            }
            if ((options.FirstPerFile || options.SampleSize.HasValue)
                && options.SearchCursor.HasValue)
            {
                WriteUsageError(
                    "recipe row-selection controls cannot be combined with --cursor because raw recipe cursors cannot preserve selector state.",
                    options,
                    "Remove --cursor and rerun selection from the beginning, or remove --first-per-file / --sample to resume from the cursor.");
                return CommandExitCodes.UsageError;
            }
            if ((options.FirstPerFile || options.SampleSize.HasValue)
                && (options.CountOnly
                    || options.GroupBy != null
                    || options.CountBy != null
                    || options.UniqueBy != null
                    || options.ResultsOnly
                    || (options.SummaryOnly && (options.Compact || options.OutputFormat == OutputFormatCompact))))
            {
                WriteUsageError(
                    "recipe row-selection controls cannot be combined with count, aggregation, results-only, or summary-only compact output.",
                    options,
                    "Remove --first-per-file / --sample to keep the non-row output, or choose text, JSON, compact, NDJSON, or issue-drafts row output.");
                return CommandExitCodes.UsageError;
            }
            if (options.MaxJsonBytes.HasValue && !SupportsSearchJsonByteLimit(options))
            {
                WriteUsageError(
                    "--max-json-bytes is only supported with JSON search output.",
                    options,
                    "Use `--json=ndjson`, `--format count`, `--format compact`, grouped/count-by JSON, or `--format issue-drafts` with --max-json-bytes.");
                return CommandExitCodes.UsageError;
            }
            if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
            {
                WriteUsageError(
                    "--results-only is only supported with NDJSON recipe output.",
                    options,
                    "Use `--recipe <name> --results-only --search-fields path,line,query_name`, or remove --results-only.");
                return CommandExitCodes.UsageError;
            }
            if (options.GroupBy != null)
            {
                if (!IsSupportedSearchGroupByValue(options.GroupBy))
                {
                    WriteUsageError(
                        "--group-by for recipe search must be one of file, symbol, origin, return-type, or subsystem.",
                        options,
                        "Use `cdidx search --recipe <name> --group-by file --count`, `--group-by symbol --count`, `--group-by return-type --count`, `--group-by subsystem --count`, or `--count-by origin`.");
                    return CommandExitCodes.UsageError;
                }
                if (!options.CountOnly)
                {
                    WriteUsageError(
                        "search --recipe --group-by requires --count.",
                        options,
                        "Add --count to request grouped recipe result counts, or remove --group-by to print matching snippets.");
                    return CommandExitCodes.UsageError;
                }
            }
            if (options.CountBy != null && !IsSupportedSearchAggregationValue(options.CountBy))
            {
                WriteUsageError(
                    "--count-by for recipe search must be one of path, file, symbol, origin, return-type, or subsystem.",
                    options,
                    "Use `--count-by path`, `--count-by symbol`, `--count-by return-type`, `--count-by subsystem`, or `--count-by origin`.");
                return CommandExitCodes.UsageError;
            }
            if (options.UniqueBy != null && !IsSupportedSearchAggregationValue(options.UniqueBy))
            {
                WriteUsageError(
                    "--unique for recipe search must be one of path, file, symbol, origin, return-type, or subsystem.",
                    options,
                    "Use `--unique path`, `--unique symbol`, `--unique return-type`, `--unique subsystem`, or `--unique origin`.");
                return CommandExitCodes.UsageError;
            }
            if (options.CountBy != null && options.UniqueBy != null)
            {
                WriteUsageError(
                    "--count-by cannot be combined with --unique.",
                    options,
                    "Run one recipe aggregation mode at a time.");
                return CommandExitCodes.UsageError;
            }
            if (options.GroupBy != null && (options.CountBy != null || options.UniqueBy != null))
            {
                WriteUsageError(
                    "--group-by cannot be combined with --count-by or --unique.",
                    options,
                    "Use either `--group-by <field> --count`, `--count-by <field>`, or `--unique <field>`.");
                return CommandExitCodes.UsageError;
            }
            if ((options.GroupBy != null || options.CountBy != null || options.UniqueBy != null) && (options.ResultsOnly || options.SearchFields != null))
            {
                WriteUsageError(
                    "recipe aggregation cannot be combined with --results-only or --search-fields.",
                    options,
                    "Run the aggregation separately, or remove --count-by/--group-by to stream projected recipe rows.");
                return CommandExitCodes.UsageError;
            }

            if (options.CountOnly || (options.SummaryOnly && (options.Compact || options.OutputFormat == OutputFormatCompact)))
            {
                if (options.GroupBy != null || options.CountBy != null || options.UniqueBy != null)
                    return RunSearchRecipeAggregation(options, jsonOptions, exact);

                return RunSearchRecipeCount(options, jsonOptions, exact);
            }

            if (options.CountBy != null || options.UniqueBy != null)
                return RunSearchRecipeAggregation(options, jsonOptions, exact);

            if (options.OutputFormat == OutputFormatIssueDrafts)
                return RunSearchRecipeIssueDrafts(options, jsonOptions, exact, cancellationToken);

            return RunSearchRecipe(options, jsonOptions, exact);
        }
        if (TryWriteBlankQueryError(options, "search"))
            return CommandExitCodes.UsageError;
        if (options.Query == null)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                "search requires a query argument",
                CommandExitCodes.UsageError,
                BuildMissingSearchQueryHint(cmdArgs),
                GetUsageLineOrThrow("search"),
                CommandErrorCodes.UsageError,
                category: "usage");
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
        {
            WriteUsageError(
                QueryLimits.FormatQueryTooLongError(),
                options,
                "Shorten the search text or split generated input into smaller queries before running `cdidx search`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("search", options))
            return CommandExitCodes.UsageError;
        if ((options.FirstPerFile || options.SampleSize.HasValue)
            && (options.CountOnly
                || options.GroupBy != null
                || options.CountBy != null
                || options.UniqueBy != null
                || options.OutputFormat == OutputFormatGrouped))
        {
            WriteUsageError(
                "search row-selection controls cannot be combined with count or aggregation output.",
                options,
                "Remove --first-per-file / --sample to count the full filtered population, or choose a row output that reports selector accounting.");
            return CommandExitCodes.UsageError;
        }
        if ((options.FirstPerFile || options.SampleSize.HasValue) && options.ResultsOnly)
        {
            WriteUsageError(
                "search row-selection controls cannot be combined with --results-only because that stream omits selector accounting.",
                options,
                "Remove --results-only to retain the NDJSON terminal record, or remove --first-per-file / --sample.");
            return CommandExitCodes.UsageError;
        }
        if ((options.FirstPerFile || options.SampleSize.HasValue)
            && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "search row-selection controls cannot be combined with metadata-free --json=array output.",
                options,
                "Add --json-envelope to retain selector accounting, use --json=ndjson / --format compact, or remove --first-per-file / --sample.");
            return CommandExitCodes.UsageError;
        }
        if ((options.FirstPerFile || options.SampleSize.HasValue)
            && options.OutputFormat is not OutputFormatText
                and not OutputFormatJson
                and not OutputFormatCompact
                and not OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "search row-selection controls are only supported by text, JSON, compact, and issue-drafts row output.",
                options,
                "Choose an output shape that reports selector accounting, or remove --first-per-file / --sample.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null)
        {
            if (!IsSupportedSearchGroupByValue(options.GroupBy))
            {
                WriteUsageError(
                    "--group-by for search must be one of file, symbol, origin, return-type, or subsystem.",
                    options,
                    "Use `cdidx search <query> --group-by file --count`, `--group-by symbol --count`, `--group-by return-type --count`, `--group-by subsystem --count`, or `--count-by origin`.");
                return CommandExitCodes.UsageError;
            }
            if (!options.CountOnly)
            {
                WriteUsageError(
                    "search --group-by requires --count.",
                    options,
                    "Add --count to request grouped result counts, or remove --group-by to print matching snippets.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount)
            {
                WriteUsageError(
                    "--group-by for search only supports plain count output or JSON.",
                    options,
                    "Use `--count`, optionally with `--json`, instead of compact/location formats.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with search --group-by because grouped count output is a JSON object.",
                    options,
                    "Use plain `--json` for the grouped-count object.");
                return CommandExitCodes.UsageError;
            }
        }
        if (options.CountBy != null && options.UniqueBy != null)
        {
            WriteUsageError(
                "--count-by cannot be combined with --unique.",
                options,
                "Run one aggregation mode at a time.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null && (options.CountBy != null || options.UniqueBy != null))
        {
            WriteUsageError(
                "--group-by cannot be combined with --count-by or --unique.",
                options,
                "Use either `--group-by <field> --count`, `--count-by <field>`, or `--unique <field>`.");
            return CommandExitCodes.UsageError;
        }
        if ((options.CountBy != null || options.UniqueBy != null) && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with search aggregation because aggregation output is a JSON object.",
                options,
                "Use plain `--json` for `--count-by` or `--unique` aggregation output.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatGrouped && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with search --format grouped because grouped output is a JSON object.",
                options,
                "Use plain `--json` or omit --json when using `--format grouped`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
        {
            WriteUsageError(
                "--results-only is only supported with NDJSON search output.",
                options,
                "Use `--results-only --json=ndjson`, or remove --results-only when using --json=array.");
            return CommandExitCodes.UsageError;
        }
        if (options.MaxJsonBytes.HasValue && !SupportsSearchJsonByteLimit(options))
        {
            WriteUsageError(
                "--max-json-bytes is only supported with JSON search output.",
                options,
                "Use `--json=ndjson`, `--json=array`, `--format count`, `--format compact`, grouped/count-by JSON, or `--format issue-drafts` with --max-json-bytes.");
            return CommandExitCodes.UsageError;
        }
        if (options.CountBy != null && !IsSupportedSearchAggregationValue(options.CountBy))
        {
            WriteUsageError(
                "--count-by for search must be one of path, file, symbol, origin, return-type, or subsystem.",
                options,
                "Use `--count-by path`, `--count-by symbol`, `--count-by return-type`, `--count-by subsystem`, or `--count-by origin`.");
            return CommandExitCodes.UsageError;
        }
        if (options.UniqueBy != null && !IsSupportedSearchAggregationValue(options.UniqueBy))
        {
            WriteUsageError(
                "--unique for search must be one of path, file, symbol, origin, return-type, or subsystem.",
                options,
                "Use `--unique path`, `--unique symbol`, `--unique return-type`, `--unique subsystem`, or `--unique origin`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts)
            return RunSearchIssueDrafts(options, jsonOptions, exactSearch, cancellationToken);

        var exactSubstringHint = SearchQueryAdvisor.BuildExactSubstringHint(options.Query, options.RawFts, exactSearch, options.Prefix);
        var ndjsonOptions = options.JsonOutputFormat == JsonOutputFormatNdjson ? GetCompactJsonOptions(jsonOptions) : jsonOptions;
        string? jsonDoneTerminalLine = null;
        return WithDb(options, jsonOptions, reader =>
        {
            if (options.GroupBy != null)
            {
                return RunGroupedSearchCount(reader, options, jsonOptions, exactSearch, exactSubstringHint);
            }
            if (options.CountBy != null || options.UniqueBy != null)
            {
                return RunSearchAggregation(reader, options, jsonOptions, exactSearch, exactSubstringHint);
            }

            if (options.CountOnly)
            {
                var counts = CountSearchMatches(reader, options, exactSearch);
                var queryDiagnostics = DbReader.AnalyzeFtsQuery(options.Query, options.RawFts, options.Prefix, options.Lang);
                if (counts.Count == 0)
                {
                    if (options.Json)
                    {
                        return WriteJsonObjectWithOptionalByteLimit(
                            BuildCountJsonPayload(
                                reader,
                                jsonOptions,
                                count: 0,
                                files: 0,
                                query: options.Query,
                                queryOptions: options,
                                ftsQueryDiagnostics: queryDiagnostics,
                                exactSubstringHint: exactSubstringHint).ToJsonString(jsonOptions),
                            options,
                            "search count",
                            "Narrow the query or increase --max-json-bytes.");
                    }
                    else
                    {
                        Console.WriteLine("0");
                        WriteExactSubstringHintIfNeeded(exactSubstringHint);
                    }
                    return CommandExitCodes.Success;
                }

                if (options.Json)
                {
                    return WriteJsonObjectWithOptionalByteLimit(
                        BuildCountJsonPayload(
                            reader,
                            jsonOptions,
                            counts.Count,
                            counts.FileCount,
                            query: options.Query,
                            queryOptions: options,
                            ftsQueryDiagnostics: queryDiagnostics,
                            exactSubstringHint: exactSubstringHint).ToJsonString(jsonOptions),
                        options,
                        "search count",
                        "Narrow the query or increase --max-json-bytes.");
                }
                else
                {
                    Console.WriteLine($"{counts.Count}");
                    WriteExactSubstringHintIfNeeded(exactSubstringHint);
                }
                return CommandExitCodes.Success;
            }

            var ftsQueryDiagnostics = DbReader.AnalyzeFtsQuery(options.Query, options.RawFts, options.Prefix, options.Lang);
            var groupedCounts = options.OutputFormat == OutputFormatGrouped
                ? CountSearchMatches(reader, options, exactSearch)
                : default;
            var displayRows = ReadSearchDisplayRows(reader, options, exactSearch, out var boundedSelection);
            var sarifSourceRows = displayRows;
            var selection = boundedSelection ?? ApplySearchOutputSelection(displayRows, options);
            displayRows = selection.Rows;
            if (displayRows.Count == 0)
            {
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
                            "Increase --max-json-bytes or remove the byte cap.");
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
                                query: options.Query,
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
                            jsonDoneTerminalLine = stream.TerminalLine;
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
                        out jsonDoneTerminalLine);
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
                    displayRows.SelectMany(row => ToSearchFormattedLocations(row, options.Query, exactSearch)).Take(options.Limit),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(displayRows.SelectMany(row => ToSearchLspLocations(row, exactSearch)).Take(options.Limit), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(displayRows.SelectMany(row => ToSearchQuickfixItems(row, options.Query, exactSearch)).Take(options.Limit));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    var sarifItems = displayRows
                        .SelectMany(row => ToSearchSarifItems(row, options.Query, exactSearch))
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
                        "Reduce --limit, --snippet-lines, or use `--json=ndjson --max-json-bytes` for streaming output.");
                }
                else
                {
                    return WriteSearchNdjsonResults(
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
                        out jsonDoneTerminalLine);
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
        }, exitCode =>
        {
            if (options.Json && options.JsonOutputFormat == JsonOutputFormatNdjson && jsonDoneTerminalLine != null && !options.ResultsOnly)
                Console.WriteLine(jsonDoneTerminalLine);
        });
    }

    private static List<string> FindUnexpectedRecipePositionals(string[] args)
    {
        var unexpected = new List<string>();
        var (withValues, flagOnly) = CliFlagSchema.GetParserFlagsPartitionedByValueBearing("recipes");
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                unexpected.AddRange(args[(i + 1)..]);
                break;
            }

            var equalsIndex = arg.IndexOf('=');
            var optionName = equalsIndex > 0 ? arg[..equalsIndex] : arg;
            if (withValues.Contains(optionName))
            {
                if (equalsIndex < 0 && i + 1 < args.Length)
                    i++;
                continue;
            }
            if (flagOnly.Contains(optionName) || arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            unexpected.Add(arg);
        }

        return unexpected;
    }
}
