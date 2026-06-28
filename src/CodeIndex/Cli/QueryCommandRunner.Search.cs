using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunSearch(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken = default)
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
        if (TryWriteUnsupportedOptionError("search", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("search"), options.Query))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "search"))
            return CommandExitCodes.UsageError;
        if (!TryResolveSearchExactMode(options, out var exact, out var exactError))
        {
            CommandErrorWriter.WriteStderr(exactError);
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesPath != null && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--open-issues can only be used with `cdidx search --format issue-drafts`.",
                GetUsageLineOrThrow("search"),
                "Use an open-issues JSON file from `gh issue list --state open --json number,title,labels,url`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OpenIssuesRepository != null && !IssueDuplicatePreflight.IsGitHubOpenIssuesSource(options.OpenIssuesPath))
        {
            WriteUsageError(
                "--repo can only be used with `--open-issues github`.",
                GetUsageLineOrThrow("search"),
                "Use `--open-issues github --repo owner/name` to fetch open issues directly from GitHub.");
            return CommandExitCodes.UsageError;
        }
        if (options.DuplicatePreflightTuningExplicit && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--duplicate-confidence and --duplicate-threshold can only be used with `cdidx search --format issue-drafts`.",
                GetUsageLineOrThrow("search"),
                "Use these controls when exporting issue draft JSON with duplicate-preflight metadata.");
            return CommandExitCodes.UsageError;
        }
        if ((options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0) && options.RecipeName == null)
        {
            WriteUsageError(
                "--include-query and --exclude-query can only be used with --recipe.",
                GetUsageLineOrThrow("search"),
                "Use `--recipe risky-code --include-query raw-diagnostic-echo` to run a child query subset.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor.HasValue && options.RecipeName == null)
        {
            WriteUsageError(
                "--cursor can only be used with --recipe.",
                GetUsageLineOrThrow("search"),
                "Use `--recipe risky-code/raw-diagnostic-echo --format compact --cursor <next_cursor>` to fetch the next page for one child query.");
            return CommandExitCodes.UsageError;
        }
        if (options.UnusedCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                GetUsageLineOrThrow("search"),
                "Use `--cursor <next_cursor>` only with `--recipe`; `unused:<offset>` cursors are for `cdidx unused`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutlineCursorOffset.HasValue)
        {
            WriteUsageError(
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                GetUsageLineOrThrow("search"),
                "`outline:<offset>` cursors are for `cdidx outline <path>`.");
            return CommandExitCodes.UsageError;
        }
        if (options.AuditScopeExplicit && options.RecipeName == null && options.ListRecipes)
        {
            WriteUsageError(
                "--audit-scope cannot be combined with `cdidx search --list-recipes`.",
                GetUsageLineOrThrow("search"),
                "Use `--query <text>` with --list-recipes to filter recipe discovery, or run an ad hoc search with `--source-only`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ShowExcluded && options.RecipeName == null)
        {
            WriteUsageError(
                "--show-excluded is only supported with `cdidx search --recipe <name>`.",
                GetUsageLineOrThrow("search"),
                "Use it with a recipe run to include the effective scope and exclusion diagnostics in JSON output.");
            return CommandExitCodes.UsageError;
        }
        if ((options.IssueTitle != null || options.IssueLabels.Count > 0) && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--issue-title and --issue-label can only be used with `cdidx search --format issue-drafts`.",
                GetUsageLineOrThrow("search"),
                "Use these hints when exporting issue draft JSON for a plain search.");
            return CommandExitCodes.UsageError;
        }
        if (options.SnippetLines == 0 && options.OutputFormat != OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--snippet-lines 0 is only supported with --format issue-drafts.",
                GetUsageLineOrThrow("search"),
                "Use `--format issue-drafts --snippet-lines 0` for path/line-only draft evidence, or pass a positive snippet line count for search output.");
            return CommandExitCodes.UsageError;
        }
        if (options.IssueTitle != null && options.RecipeName != null)
        {
            WriteUsageError(
                "--issue-title is only supported for ad hoc search issue drafts.",
                GetUsageLineOrThrow("search"),
                "Recipe issue-drafts produce one draft per recipe query, so their titles are derived from the recipe metadata.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.CountOnly)
        {
            WriteUsageError(
                "--count cannot be combined with --format issue-drafts.",
                GetUsageLineOrThrow("search"),
                "Issue-draft export needs result evidence; remove --count.");
            return CommandExitCodes.UsageError;
        }
        if (options.NamesOnly && !options.ListRecipes)
        {
            WriteUsageError(
                "--names is only supported with `cdidx recipes` or `cdidx search --list-recipes`.",
                GetUsageLineOrThrow("search"),
                "Use `cdidx recipes --names --json` for a small deterministic recipe-name list.");
            return CommandExitCodes.UsageError;
        }
        if (options.NamesOnly && options.SummaryOnly)
        {
            WriteUsageError(
                "--names cannot be combined with --summary-only.",
                GetUsageLineOrThrow("search"),
                "Use one recipe-list shape at a time.");
            return CommandExitCodes.UsageError;
        }
        if (options.SummaryOnly && !options.ListRecipes && !(options.RecipeName != null && options.CountOnly))
        {
            WriteUsageError(
                "--summary-only is only supported with `cdidx recipes` / `cdidx search --list-recipes`, or recipe count output.",
                GetUsageLineOrThrow("search"),
                "Use `cdidx recipes --summary-only --json` or `cdidx search --recipe <name> --format count --summary-only`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with --format issue-drafts because draft export is a JSON object.",
                GetUsageLineOrThrow("search"),
                "Use plain `--json` or omit --json when exporting issue drafts.");
            return CommandExitCodes.UsageError;
        }
        if (options.SearchCursor.HasValue && options.OutputFormat == OutputFormatIssueDrafts)
        {
            WriteUsageError(
                "--cursor cannot be combined with --format issue-drafts.",
                GetUsageLineOrThrow("search"),
                "Use --cursor with recipe JSON or compact output, then export issue drafts after choosing the desired query page.");
            return CommandExitCodes.UsageError;
        }
        if (exact && options.Prefix)
        {
            WriteValidationError(
                "--prefix cannot be combined with --exact / --exact-substring (exact uses instr(), not FTS5 prefix phrases).",
                "Drop --prefix to keep the exact substring path, or drop --exact to opt into FTS5 prefix matching.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null && (options.ListRecipes || options.NamedSearchQueries.Count > 0))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : "--named-query";
            WriteUsageError(
                $"--group-by is not supported with {mode}.",
                GetUsageLineOrThrow("search"),
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
                GetUsageLineOrThrow("search"),
                $"Remove {mode}, or run a plain `cdidx search <query> --format grouped`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ListRecipes)
        {
            if (options.RecipeName != null || options.NamedSearchQueries.Count > 0 || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--list-recipes cannot be combined with --recipe, --named-query, or extra positional arguments.",
                    GetUsageLineOrThrow("search"),
                    "Run `cdidx search --list-recipes --query <text>` to filter built-in audit recipes by recipe, query, label, severity, path, or search text.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCompact)
            {
                WriteUsageError(
                    "--format count/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --list-recipes.",
                    GetUsageLineOrThrow("search"),
                    "Use plain text output, `--json` / `--format json` for the full recipe list, or `--format compact` for a compact summary.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --list-recipes because recipe-list output is a JSON object.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the recipe-list object.");
                return CommandExitCodes.UsageError;
            }

            return WriteSearchRecipeList(options, jsonOptions);
        }
        if (options.NamedSearchQueries.Count > 0)
        {
            if (options.Query != null || options.RecipeName != null || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--named-query cannot be combined with a positional query, --query, --recipe, or extra positional arguments.",
                    GetUsageLineOrThrow("search"),
                    "Pass one or more `--named-query <name>=<query>` values, or run a plain `cdidx search <query>`.");
                return CommandExitCodes.UsageError;
            }
            if (options.OpenIssuesPath != null)
            {
                WriteUsageError(
                    "--open-issues can only be used with `cdidx search --recipe <name> --format issue-drafts`.",
                    GetUsageLineOrThrow("search"),
                    "Remove --open-issues for ad hoc named batches.");
                return CommandExitCodes.UsageError;
            }
            if (options.CountOnly)
            {
                WriteUsageError(
                    "--count is not supported with --named-query.",
                    GetUsageLineOrThrow("search"),
                    "Use `cdidx search --named-query <name>=<query> --json` for per-query counts.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCompact)
            {
                WriteUsageError(
                    "--format count/csv/tsv/lsp/qf/sarif/issue-drafts is not supported with --named-query.",
                    GetUsageLineOrThrow("search"),
                    "Use plain text output, `--json`, or `--format compact` for grouped ad hoc results.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --named-query because named batch output is grouped by query.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the grouped named-query object.");
                return CommandExitCodes.UsageError;
            }
            if (options.MaxJsonBytes.HasValue && !options.Json)
            {
                WriteUsageError(
                    "--max-json-bytes is only supported with JSON search output.",
                    GetUsageLineOrThrow("search"),
                    "Use `--json` or `--format compact` with --named-query when bounding named batch output.");
                return CommandExitCodes.UsageError;
            }

            return RunSearchNamedBatch(options, jsonOptions, exact);
        }
        if (options.RecipeName != null)
        {
            if (options.Query != null || options.ExtraNames.Count > 0)
            {
                WriteUsageError(
                    "--recipe expands into its own curated query set and cannot be combined with a search query.",
                    GetUsageLineOrThrow("search"),
                    "Remove the positional query, or run a plain `cdidx search <query>` without --recipe.");
                return CommandExitCodes.UsageError;
            }
            if (options.Prefix)
            {
                WriteUsageError(
                    "--prefix is not supported with --recipe because each recipe query defines its own match mode.",
                    GetUsageLineOrThrow("search"),
                    "Remove --prefix, or run the individual query from the recipe list yourself.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount and not OutputFormatCompact and not OutputFormatIssueDrafts)
            {
                WriteUsageError(
                    "--format csv/tsv/lsp/qf/sarif is not supported with --recipe.",
                    GetUsageLineOrThrow("search"),
                    "Use `--count` / `--format count` for count-only recipe output, `--json` for grouped recipe results, `--format compact` for summary-first compact JSON, or `--format issue-drafts` for draft exports.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with --recipe because recipe output is grouped by query.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the grouped recipe object.");
                return CommandExitCodes.UsageError;
            }
            if (options.MaxJsonBytes.HasValue && !SupportsSearchJsonByteLimit(options))
            {
                WriteUsageError(
                    "--max-json-bytes is only supported with JSON search output.",
                    GetUsageLineOrThrow("search"),
                    "Use `--json=ndjson`, `--format count`, `--format compact`, grouped/count-by JSON, or `--format issue-drafts` with --max-json-bytes.");
                return CommandExitCodes.UsageError;
            }
            if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
            {
                WriteUsageError(
                    "--results-only is only supported with NDJSON recipe output.",
                    GetUsageLineOrThrow("search"),
                    "Use `--recipe <name> --results-only --search-fields path,line,query_name`, or remove --results-only.");
                return CommandExitCodes.UsageError;
            }
            if (options.GroupBy != null)
            {
                if (options.GroupBy is not "file" and not "symbol" and not "origin")
                {
                    WriteUsageError(
                        "--group-by for recipe search must be one of file, symbol, or origin.",
                        GetUsageLineOrThrow("search"),
                        "Use `cdidx search --recipe <name> --group-by file --count`, `--group-by symbol --count`, or `--count-by origin`.");
                    return CommandExitCodes.UsageError;
                }
                if (!options.CountOnly)
                {
                    WriteUsageError(
                        "search --recipe --group-by requires --count.",
                        GetUsageLineOrThrow("search"),
                        "Add --count to request grouped recipe result counts, or remove --group-by to print matching snippets.");
                    return CommandExitCodes.UsageError;
                }
            }
            if (options.CountBy != null && options.CountBy is not "path" and not "file" and not "symbol" and not "origin")
            {
                WriteUsageError(
                    "--count-by for recipe search must be one of path, file, symbol, or origin.",
                    GetUsageLineOrThrow("search"),
                    "Use `--count-by path`, `--count-by symbol`, or `--count-by origin`.");
                return CommandExitCodes.UsageError;
            }
            if (options.UniqueBy != null && options.UniqueBy is not "path" and not "file" and not "symbol" and not "origin")
            {
                WriteUsageError(
                    "--unique for recipe search must be one of path, file, symbol, or origin.",
                    GetUsageLineOrThrow("search"),
                    "Use `--unique path`, `--unique symbol`, or `--unique origin`.");
                return CommandExitCodes.UsageError;
            }
            if (options.CountBy != null && options.UniqueBy != null)
            {
                WriteUsageError(
                    "--count-by cannot be combined with --unique.",
                    GetUsageLineOrThrow("search"),
                    "Run one recipe aggregation mode at a time.");
                return CommandExitCodes.UsageError;
            }
            if (options.GroupBy != null && (options.CountBy != null || options.UniqueBy != null))
            {
                WriteUsageError(
                    "--group-by cannot be combined with --count-by or --unique.",
                    GetUsageLineOrThrow("search"),
                    "Use either `--group-by <field> --count`, `--count-by <field>`, or `--unique <field>`.");
                return CommandExitCodes.UsageError;
            }
            if ((options.GroupBy != null || options.CountBy != null || options.UniqueBy != null) && (options.ResultsOnly || options.SearchFields != null))
            {
                WriteUsageError(
                    "recipe aggregation cannot be combined with --results-only or --search-fields.",
                    GetUsageLineOrThrow("search"),
                    "Run the aggregation separately, or remove --count-by/--group-by to stream projected recipe rows.");
                return CommandExitCodes.UsageError;
            }

            if (options.CountOnly)
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
            WriteUsageError(
                "search requires a query argument",
                GetUsageLineOrThrow("search"),
                BuildMissingSearchQueryHint(cmdArgs));
            return CommandExitCodes.UsageError;
        }
        if (options.Query.Length > QueryLimits.MaxQueryLength)
        {
            WriteUsageError(
                QueryLimits.FormatQueryTooLongError(),
                GetUsageLineOrThrow("search"),
                "Shorten the search text or split generated input into smaller queries before running `cdidx search`.");
            return CommandExitCodes.UsageError;
        }
        if (TryWriteUnexpectedExtraPositionals("search", options))
            return CommandExitCodes.UsageError;
        if (options.GroupBy != null)
        {
            if (options.GroupBy is not "file" and not "symbol" and not "origin")
            {
                WriteUsageError(
                    "--group-by for search must be one of file, symbol, or origin.",
                    GetUsageLineOrThrow("search"),
                    "Use `cdidx search <query> --group-by file --count`, `--group-by symbol --count`, or `--count-by origin`.");
                return CommandExitCodes.UsageError;
            }
            if (!options.CountOnly)
            {
                WriteUsageError(
                    "search --group-by requires --count.",
                    GetUsageLineOrThrow("search"),
                    "Add --count to request grouped result counts, or remove --group-by to print matching snippets.");
                return CommandExitCodes.UsageError;
            }
            if (options.OutputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount)
            {
                WriteUsageError(
                    "--group-by for search only supports plain count output or JSON.",
                    GetUsageLineOrThrow("search"),
                    "Use `--count`, optionally with `--json`, instead of compact/location formats.");
                return CommandExitCodes.UsageError;
            }
            if (options.JsonOutputFormat == JsonOutputFormatArray)
            {
                WriteUsageError(
                    "--json=array is not supported with search --group-by because grouped count output is a JSON object.",
                    GetUsageLineOrThrow("search"),
                    "Use plain `--json` for the grouped-count object.");
                return CommandExitCodes.UsageError;
            }
        }
        if (options.CountBy != null && options.UniqueBy != null)
        {
            WriteUsageError(
                "--count-by cannot be combined with --unique.",
                GetUsageLineOrThrow("search"),
                "Run one aggregation mode at a time.");
            return CommandExitCodes.UsageError;
        }
        if (options.GroupBy != null && (options.CountBy != null || options.UniqueBy != null))
        {
            WriteUsageError(
                "--group-by cannot be combined with --count-by or --unique.",
                GetUsageLineOrThrow("search"),
                "Use either `--group-by <field> --count`, `--count-by <field>`, or `--unique <field>`.");
            return CommandExitCodes.UsageError;
        }
        if ((options.CountBy != null || options.UniqueBy != null) && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with search aggregation because aggregation output is a JSON object.",
                GetUsageLineOrThrow("search"),
                "Use plain `--json` for `--count-by` or `--unique` aggregation output.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatGrouped && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            WriteUsageError(
                "--json=array is not supported with search --format grouped because grouped output is a JSON object.",
                GetUsageLineOrThrow("search"),
                "Use plain `--json` or omit --json when using `--format grouped`.");
            return CommandExitCodes.UsageError;
        }
        if (options.ResultsOnly && options.JsonOutputFormat != JsonOutputFormatNdjson)
        {
            WriteUsageError(
                "--results-only is only supported with NDJSON search output.",
                GetUsageLineOrThrow("search"),
                "Use `--results-only --json=ndjson`, or remove --results-only when using --json=array.");
            return CommandExitCodes.UsageError;
        }
        if (options.MaxJsonBytes.HasValue && !SupportsSearchJsonByteLimit(options))
        {
            WriteUsageError(
                "--max-json-bytes is only supported with JSON search output.",
                GetUsageLineOrThrow("search"),
                "Use `--json=ndjson`, `--json=array`, `--format count`, `--format compact`, grouped/count-by JSON, or `--format issue-drafts` with --max-json-bytes.");
            return CommandExitCodes.UsageError;
        }
        if (options.CountBy != null && options.CountBy is not "path" and not "file" and not "symbol" and not "origin")
        {
            WriteUsageError(
                "--count-by for search must be one of path, file, symbol, or origin.",
                GetUsageLineOrThrow("search"),
                "Use `--count-by path`, `--count-by symbol`, or `--count-by origin`.");
            return CommandExitCodes.UsageError;
        }
        if (options.UniqueBy != null && options.UniqueBy is not "path" and not "file" and not "symbol" and not "origin")
        {
            WriteUsageError(
                "--unique for search must be one of path, file, symbol, or origin.",
                GetUsageLineOrThrow("search"),
                "Use `--unique path`, `--unique symbol`, or `--unique origin`.");
            return CommandExitCodes.UsageError;
        }
        if (options.OutputFormat == OutputFormatIssueDrafts)
            return RunSearchIssueDrafts(options, jsonOptions, exact, cancellationToken);

        var exactSubstringHint = SearchQueryAdvisor.BuildExactSubstringHint(options.Query, options.RawFts, exact, options.Prefix);
        var ndjsonOptions = options.JsonOutputFormat == JsonOutputFormatNdjson ? GetCompactJsonOptions(jsonOptions) : jsonOptions;
        int? jsonDoneCount = null;
        var jsonDoneInterrupted = false;
        DbReader? jsonDoneReader = null;
        return WithDb(options, jsonOptions, reader =>
        {
            jsonDoneReader = reader;
            if (options.GroupBy != null)
            {
                return RunGroupedSearchCount(reader, options, jsonOptions, exact, exactSubstringHint);
            }
            if (options.CountBy != null || options.UniqueBy != null)
            {
                return RunSearchAggregation(reader, options, jsonOptions, exact, exactSubstringHint);
            }

            if (options.CountOnly)
            {
                var counts = HasSearchOriginFilters(options)
                    ? CountFilteredSearchResults(reader, options, exact)
                    : reader.CountSearchResults(options.Query, options.Lang, options.RawFts, options.PathPatterns, options.ExcludePaths, options.ExcludeTests, !options.NoDedup, options.Since, exact, options.Prefix, !options.NoVisibilityRank, options.GuardFilters, options.GuardWindow);
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
            var displayRows = ReadSearchDisplayRows(reader, options, exact);
            var selection = ApplySearchOutputSelection(displayRows, options);
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
                    var groupedExitCode = WriteGroupedSearchResults([], options, jsonOptions);
                    return groupedExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : groupedExitCode;
                }
                if (options.Json && TryWriteEmptySearchJsonWithOptionalByteLimit(options, jsonOptions, out var emptyJsonExitCode))
                    return emptyJsonExitCode == CommandExitCodes.Success ? ZeroResultExitCode(options) : emptyJsonExitCode;
                if (options.Json && TryWriteEmptyFormattedResult(options, jsonOptions))
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
                            if (WouldExceedJsonByteLimit(options, bytesWritten: 0, payload, out var interrupted))
                                jsonDoneInterrupted = interrupted;
                            else
                                Console.WriteLine(payload);
                        }
                        jsonDoneCount = 0;
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
                    var projectedExitCode = WriteProjectedSearchResults(compactResults, options, jsonOptions, ndjsonOptions, out var projectedDoneCount, out var projectedInterrupted);
                    jsonDoneCount = projectedDoneCount;
                    jsonDoneInterrupted = projectedInterrupted;
                    return projectedExitCode;
                }
                if (options.OutputFormat == OutputFormatCompact)
                {
                    return WriteCompactSearchResults(compactResults, options, jsonOptions);
                }
                if (options.OutputFormat == OutputFormatGrouped)
                {
                    return WriteGroupedSearchResults(displayRows, options, jsonOptions);
                }
                if (options.OutputFormat == OutputFormatCsv || options.OutputFormat == OutputFormatTsv)
                {
                    WriteDelimitedSearchResults(displayRows, options);
                    return CommandExitCodes.Success;
                }
                if (TryWriteFormattedLocations(
                    options,
                    displayRows.SelectMany(row => ToSearchFormattedLocations(row, options.Query, exact)),
                    jsonOptions))
                    return CommandExitCodes.Success;
                if (options.OutputFormat == OutputFormatLsp)
                {
                    WriteLspLocations(displayRows.SelectMany(row => ToSearchLspLocations(row, exact)), jsonOptions);
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatQf)
                {
                    WriteQuickfix(displayRows.SelectMany(row => ToSearchQuickfixItems(row, options.Query, exact)));
                    return CommandExitCodes.Success;
                }
                if (options.OutputFormat == OutputFormatSarif)
                {
                    WriteSarif(displayRows.SelectMany(row => ToSearchSarifItems(row, options.Query, exact)), jsonOptions);
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
                    WriteSearchNdjsonResults(compactResults, options, ndjsonOptions, out var emittedCount, out var interrupted);
                    jsonDoneCount = emittedCount;
                    jsonDoneInterrupted = interrupted;
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
            if (options.Json && options.JsonOutputFormat == JsonOutputFormatNdjson && jsonDoneCount.HasValue && !options.ResultsOnly)
                WriteJsonStreamDone(jsonDoneCount.Value, ndjsonOptions, jsonDoneInterrupted, jsonDoneReader);
        });
    }
}
