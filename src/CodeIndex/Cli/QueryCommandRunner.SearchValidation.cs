namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private enum SearchAggregationTarget
    {
        Plain,
        Recipe,
    }

    private static bool TryValidateSearchOptions(
        QueryCommandOptions options,
        bool exact,
        QueryCommandInvocationContext invocationContext) =>
        TryValidateSearchIssueSourceOptions(options)
        && TryValidateSearchRecipeControlOptions(options)
        && TryValidateSearchIssueShapeOptions(options)
        && TryValidateSearchDiscoveryShapeOptions(options)
        && TryValidateSearchCrossRouteOptions(options, exact, invocationContext);

    private static bool TryValidateSearchIssueSourceOptions(QueryCommandOptions options)
    {
        if (options.OpenIssuesPath != null && options.OutputFormat != OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "--open-issues can only be used with `cdidx search --format issue-drafts`.",
                "Use an open-issues JSON file from `gh issue list --state open --json number,title,labels,url`.");
        }
        if (options.OpenIssuesRepository != null && !IssueDuplicatePreflight.IsGitHubOpenIssuesSource(options.OpenIssuesPath))
        {
            return RejectSearchUsage(
                options,
                "--repo can only be used with `--open-issues github`.",
                "Use `--open-issues github --repo owner/name` to fetch open issues directly from GitHub.");
        }
        if (options.IssueState != IssueDuplicatePreflight.DefaultIssueState && !IssueDuplicatePreflight.IsGitHubOpenIssuesSource(options.OpenIssuesPath))
            return RejectSearchUsage(options, "--issue-state can only be used with `--open-issues github`.", "Use `--open-issues github --repo owner/name --issue-state all`.");
        if (options.DuplicatePreflightTuningExplicit && options.OutputFormat != OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "--duplicate-confidence and --duplicate-threshold can only be used with `cdidx search --format issue-drafts`.",
                "Use these controls when exporting issue draft JSON with duplicate-preflight metadata.");
        }

        return true;
    }

    private static bool TryValidateSearchRecipeControlOptions(QueryCommandOptions options)
    {
        if ((options.IncludeRecipeQueries.Count > 0 || options.ExcludeRecipeQueries.Count > 0) && options.RecipeName == null)
        {
            return RejectSearchUsage(
                options,
                "--include-query and --exclude-query can only be used with --recipe.",
                "Use `--recipe risky-code --include-query raw-diagnostic-echo` to run a child query subset.");
        }
        if (options.SearchCursor.HasValue && options.RecipeName == null)
        {
            return RejectSearchUsage(
                options,
                "--cursor can only be used with --recipe.",
                "Use `--recipe risky-code/raw-diagnostic-echo --format compact --cursor <next_cursor>` to fetch the next page for one child query.");
        }
        if (options.UnusedCursorOffset.HasValue)
        {
            return RejectSearchUsage(
                options,
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                "Use `--cursor <next_cursor>` only with `--recipe`; `unused:<offset>` cursors are for `cdidx unused`.");
        }
        if (options.OutlineCursorOffset.HasValue)
        {
            return RejectSearchUsage(
                options,
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                "`outline:<offset>` cursors are for `cdidx outline <path>`.");
        }
        if (options.DependencyCycleCursor.HasValue)
        {
            return RejectSearchUsage(
                options,
                "--cursor for search must be a search pagination cursor returned by recipe search.",
                "Dependency-cycle cursors are for `cdidx deps --cycles`.");
        }
        if (options.AuditScopeExplicit && options.RecipeName == null && options.ListRecipes)
        {
            return RejectSearchUsage(
                options,
                "--audit-scope cannot be combined with `cdidx search --list-recipes`.",
                "Use `--query <text>` with --list-recipes to filter recipe discovery, or run an ad hoc search with `--source-only`.");
        }
        if (options.ShowExcluded && options.RecipeName == null)
        {
            return RejectSearchUsage(
                options,
                "--show-excluded is only supported with `cdidx search --recipe <name>`.",
                "Use it with a recipe run to include the effective scope and exclusion diagnostics in JSON output.");
        }

        return true;
    }

    private static bool TryValidateSearchIssueShapeOptions(QueryCommandOptions options)
    {
        if ((options.IssueTitle != null || options.IssueLabels.Count > 0) && options.OutputFormat != OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "--issue-title and --issue-label can only be used with `cdidx search --format issue-drafts`.",
                "Use these hints when exporting issue draft JSON for a plain search.");
        }
        if (options.SnippetLines == 0 && options.OutputFormat != OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "--snippet-lines 0 is only supported with --format issue-drafts.",
                "Use `--format issue-drafts --snippet-lines 0` for path/line-only draft evidence, or pass a positive snippet line count for search output.");
        }
        if (options.IssueTitle != null && options.RecipeName != null)
        {
            return RejectSearchUsage(
                options,
                "--issue-title is only supported for ad hoc search issue drafts.",
                "Recipe issue-drafts produce one draft per recipe query, so their titles are derived from the recipe metadata.");
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.CountOnly)
        {
            return RejectSearchUsage(
                options,
                "--count cannot be combined with --format issue-drafts.",
                "Issue-draft export needs result evidence; remove --count.");
        }

        return true;
    }

    private static bool TryValidateSearchDiscoveryShapeOptions(QueryCommandOptions options)
    {
        if (options.NamesOnly && !options.ListRecipes)
        {
            return RejectSearchUsage(
                options,
                "--names is only supported with `cdidx recipes` or `cdidx search --list-recipes`.",
                "Use `cdidx recipes --names --json` for a small deterministic recipe-name list.");
        }
        if (options.NamesOnly && options.SummaryOnly)
        {
            return RejectSearchUsage(
                options,
                "--names cannot be combined with --summary-only.",
                "Use one recipe-list shape at a time.");
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
            return RejectSearchUsage(
                options,
                "--summary-only is only supported with `cdidx recipes` / `cdidx search --list-recipes`, named-query count output, recipe count output, or recipe issue-drafts output.",
                "Use `cdidx recipes --summary-only --json`, `cdidx search --named-query <name>=<query> --summary-only --json`, `cdidx search --recipe <name> --format compact --summary-only --json`, `cdidx search --recipe <name> --format count --summary-only`, or `cdidx search --recipe <name> --format issue-drafts --summary-only`.");
        }
        if (options.OutputFormat == OutputFormatIssueDrafts && options.JsonOutputFormat == JsonOutputFormatArray)
        {
            return RejectSearchUsage(
                options,
                "--json=array is not supported with --format issue-drafts because draft export is a JSON object.",
                "Use plain `--json` or omit --json when exporting issue drafts.");
        }
        if (options.SearchCursor.HasValue && options.OutputFormat == OutputFormatIssueDrafts)
        {
            return RejectSearchUsage(
                options,
                "--cursor cannot be combined with --format issue-drafts.",
                "Use --cursor with recipe JSON or compact output, then export issue drafts after choosing the desired query page.");
        }

        return true;
    }

    private static bool TryValidateSearchCrossRouteOptions(
        QueryCommandOptions options,
        bool exact,
        QueryCommandInvocationContext invocationContext)
    {
        if ((exact || options.TokenBoundary) && options.Prefix)
            return RejectSearchValidation(
                options,
                "--prefix cannot be combined with --exact / --exact-substring / --token-boundary (exact uses instr(), not FTS5 prefix phrases).",
                "Drop --prefix to keep the exact substring path, or drop the exact-mode flag to opt into FTS5 prefix matching.");
        if (options.GroupBy != null && (options.ListRecipes || options.NamedSearchQueries.Count > 0))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : "--named-query";
            return RejectSearchUsage(
                options,
                $"--group-by is not supported with {mode}.",
                "Use `cdidx search <query> --group-by file --count` or remove --group-by for recipe-list and named-batch output.");
        }
        if (options.OutputFormat == OutputFormatGrouped && (options.ListRecipes || options.NamedSearchQueries.Count > 0 || options.RecipeName != null))
        {
            var mode = options.ListRecipes
                ? "--list-recipes"
                : options.NamedSearchQueries.Count > 0
                    ? "--named-query"
                    : "--recipe";
            return RejectSearchUsage(
                options,
                "--format grouped is only supported for plain search output.",
                invocationContext.RecipeNameIsPositional && mode == "--recipe"
                    ? "Run a plain `cdidx search <query> --format grouped`; audit recipe execution does not support grouped output."
                    : $"Remove {mode}, or run a plain `cdidx search <query> --format grouped`.");
        }

        return !TryWriteCappedJsonDiagnosticsUsageError(invocationContext.CommandName, options);
    }

    private static bool HasSearchRowSelectors(QueryCommandOptions options) =>
        options.FirstPerFile || options.SampleSize.HasValue;

    private static bool HasSearchAggregation(QueryCommandOptions options) =>
        options.GroupBy != null || options.CountBy != null || options.UniqueBy != null;

    private static bool HasSearchCountOrUniqueAggregation(QueryCommandOptions options) =>
        options.CountBy != null || options.UniqueBy != null;

    private static bool TryValidateSearchAggregationFields(
        QueryCommandOptions options,
        SearchAggregationTarget target)
    {
        var recipe = target == SearchAggregationTarget.Recipe;
        if (options.CountBy != null && !IsSupportedSearchAggregationValue(options.CountBy))
        {
            return RejectSearchUsage(
                options,
                recipe
                    ? "--count-by for recipe search must be one of path, file, symbol, origin, return-type, or subsystem."
                    : "--count-by for search must be one of path, file, symbol, origin, return-type, or subsystem.",
                "Use `--count-by path`, `--count-by symbol`, `--count-by return-type`, `--count-by subsystem`, or `--count-by origin`.");
        }
        if (options.UniqueBy != null && !IsSupportedSearchAggregationValue(options.UniqueBy))
        {
            return RejectSearchUsage(
                options,
                recipe
                    ? "--unique for recipe search must be one of path, file, symbol, origin, return-type, or subsystem."
                    : "--unique for search must be one of path, file, symbol, origin, return-type, or subsystem.",
                "Use `--unique path`, `--unique symbol`, `--unique return-type`, `--unique subsystem`, or `--unique origin`.");
        }

        return true;
    }

    private static bool TryValidateSearchAggregationConflicts(
        QueryCommandOptions options,
        SearchAggregationTarget target)
    {
        if (options.CountBy != null && options.UniqueBy != null)
        {
            return RejectSearchUsage(
                options,
                "--count-by cannot be combined with --unique.",
                target == SearchAggregationTarget.Recipe
                    ? "Run one recipe aggregation mode at a time."
                    : "Run one aggregation mode at a time.");
        }
        if (options.GroupBy != null && HasSearchCountOrUniqueAggregation(options))
        {
            return RejectSearchUsage(
                options,
                "--group-by cannot be combined with --count-by or --unique.",
                "Use either `--group-by <field> --count`, `--count-by <field>`, or `--unique <field>`.");
        }

        return true;
    }

    private static bool RejectSearchUsage(QueryCommandOptions options, string message, string hint)
    {
        WriteUsageError(message, options, hint);
        return false;
    }

    private static bool RejectSearchValidation(QueryCommandOptions options, string message, string hint)
    {
        WriteSearchValidationError(message, options, hint);
        return false;
    }
}
