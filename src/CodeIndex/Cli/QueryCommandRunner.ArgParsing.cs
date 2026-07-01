using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static bool TryNormalizeSearchAuditScope(string value, out string scope)
    {
        scope = value.Trim().ToLowerInvariant();
        if (scope is SearchAuditRecipes.DefaultAuditScope or SearchAuditRecipes.AllAuditScope)
            return true;
        if (scope is "production" or "production-only")
        {
            scope = SearchAuditRecipes.DefaultAuditScope;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeSearchGuardScope(string value, out SearchGuardScope scope)
    {
        switch (value.Trim().ToLowerInvariant().Replace("_", "-"))
        {
            case "window":
                scope = SearchGuardScope.Window;
                return true;
            case "same-line":
            case "sameline":
                scope = SearchGuardScope.SameLine;
                return true;
            default:
                scope = SearchGuardScope.Window;
                return false;
        }
    }

    private static string FormatSearchGuardScope(SearchGuardScope scope)
        => scope == SearchGuardScope.SameLine ? "same-line" : "window";

    public static QueryCommandOptions ParseArgs(
        string[] args,
        bool jsonDefault,
        bool allowNamedQuery = false,
        bool allowStatusCheck = false,
        bool allowIssueDraftsFormat = false,
        bool validateDefaultLimit = true,
        bool validateDefaultSnippetLines = true,
        bool validateDefaultMaxLineWidth = true,
        bool applySearchSourceDefaults = false,
        bool allowOutlineSort = false)
    {
        string? dbPath = null;
        string? dataDir = null;
        bool? json = null;
        string jsonOutputFormat = JsonOutputFormatNdjson;
        bool jsonOutputFormatExplicit = false;
        int limit = ResolveDefaultPositiveInt(DefaultLimitEnvironmentVariable, DefaultQueryLimit, "--limit", out var defaultLimitError);
        int? totalLimit = null;
        string? lang = null;
        string? kind = null;
        string? unusedBucket = null;
        string? minUnusedConfidence = null;
        string? severity = null;
        string? query = null;
        bool rawFts = false;
        bool includeBody = false;
        int? bodyStartLine = null;
        int? bodyLines = null;
        bool countOnly = false;
        bool all = false;
        bool strictNotFound = false;
        int? startLine = null;
        int? endLine = null;
        int contextBefore = 0;
        int contextAfter = 0;
        int? focusLine = null;
        int? focusColumn = null;
        int focusLength = 1;
        int snippetLines = ResolveDefaultPositiveInt(DefaultSnippetLinesEnvironmentVariable, SearchSnippetFormatter.DefaultSnippetLines, "--snippet-lines", out var defaultSnippetLinesError);
        var snippetFocus = SearchSnippetFocusMode.Quality;
        int maxLineWidth = ResolveDefaultNonNegativeInt(DefaultMaxLineWidthEnvironmentVariable, LineWidthFormatter.DefaultMaxLineWidth, "--max-line-width", out var defaultMaxLineWidthError);
        bool contextAfterExplicit = false;
        var pathPatterns = new List<string>();
        var userPathPatterns = new List<string>();
        var workspaceDbPaths = new List<string>();
        var projectFilters = new List<string>();
        string? solutionFilter = null;
        var excludePaths = new List<string>();
        var visibilityFilters = new List<string>();
        var excludeVisibilityFilters = new List<string>();
        bool excludeTests = false;
        bool unusedActionable = false;
        bool includeGenerated = false;
        DateTime? since = null;
        bool noDedup = false;
        bool noVisibilityRank = false;
        bool exact = false;
        bool regex = false;
        bool prefix = false;
        var guardFilters = new List<SearchGuardFilter>();
        var guardWindow = DbReader.DefaultSearchGuardWindow;
        var guardScope = SearchGuardScope.Window;
        bool excludeComments = false;
        bool excludeStrings = false;
        bool excludeFixtures = false;
        List<string>? parseErrors = null;
        bool exactName = false;
        bool exactSubstring = false;
        bool dbPathExplicit = false;
        bool readOnly = false;
        bool dryRun = false;
        bool checkWorkspace = false;
        TimeSpan? staleAfter = null;
        HashSet<string>? statusCheckScopes = null;
        bool withPaths = false;
        string? groupBy = null;
        string? uniqueBy = null;
        string? countBy = null;
        var matchOrigins = new List<string>();
        var excludeOrigins = new List<string>();
        var resultKinds = new List<string>();
        List<string>? searchFields = null;
        List<string>? outlineFields = null;
        bool outlineFieldsExplicit = false;
        bool firstPerFile = false;
        bool resultsOnly = false;
        bool nextSteps = false;
        int groupedPerFileLimit = DefaultSearchGroupedPerFileLimit;
        int? sampleSize = null;
        int? maxJsonBytes = null;
        bool rawBytes = false;
        bool rawKinds = false;
        bool verbose = false;
        bool profile = false;
        int? slowQueryMs = null;
        bool compact = false;
        List<string>? inspectFields = null;
        double minEntrypointConfidence = 0;
        string? statusExplainField = null;
        bool statusLogPath = false;
        string outputFormat = OutputFormatText;
        bool statusConfig = false;
        bool limitExplicit = false;
        bool snippetLinesExplicit = false;
        bool maxLineWidthExplicit = false;
        bool strict = false;
        var rankMode = ReferenceRankMode.Weighted;
        var symbolSortMode = SymbolSortMode.Name;
        string? sortValue = null;
        bool sortExplicit = false;
        var extraNames = new List<string>();
        bool impactDeprecatedDepthUsed = false;
        List<string>? mapSections = null;
        bool summaryOnly = false;
        bool mapSummaryOnly = false;
        bool dependencyCycles = false;
        bool dependencySuppressNoise = false;
        var dependencySymbols = new List<string>();
        var dependencySymbolFamilies = new List<string>();
        string? recipeName = null;
        var includeRecipeQueries = new List<string>();
        var excludeRecipeQueries = new List<string>();
        bool showExcluded = false;
        bool listRecipes = false;
        bool namesOnly = false;
        string? openIssuesPath = null;
        string auditScope = SearchAuditRecipes.DefaultAuditScope;
        bool auditScopeExplicit = false;
        string? openIssuesRepository = null;
        string duplicateConfidence = IssueDuplicatePreflight.DefaultDuplicateConfidence;
        double duplicateThreshold = IssueDuplicatePreflight.DefaultDuplicateThreshold;
        bool duplicateConfidenceExplicit = false;
        bool duplicateThresholdExplicit = false;
        string? issueTitle = null;
        var issueLabels = new List<string>();
        SearchCursor? searchCursor = null;
        int? unusedCursorOffset = null;
        int? outlineCursorOffset = null;
        var namedSearchQueries = new List<SearchNamedQuery>();
        bool languagesIndexedOnly = false;
        var languageCapabilities = new List<string>();
        var languageLookups = new List<string>();
        var languageExtensionLookups = new List<string>();
        var languageAliasLookups = new List<string>();
        bool sourceOnly = false;
        bool noSemanticTokens = false;
        ProjectFilterRootResolution? projectFilterRootResolution = null;

        void AddParseError(string error)
        {
            parseErrors ??= [];
            parseErrors.Add(error);
        }

        void AddSearchGuardFilter(string optionName, SearchGuardRole role, SearchGuardDirection direction, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddParseError(BuildMissingOptionValueError(optionName));
                return;
            }
            if (value.Length > QueryLimits.MaxQueryLength)
            {
                AddParseError($"Error: {optionName} query too long (max {QueryLimits.MaxQueryLength} characters).");
                return;
            }

            guardFilters.Add(new SearchGuardFilter(role, direction, value));
        }

        void AddDependencySymbolFilter(string optionName, string value, List<string> target)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                AddParseError($"Error: {optionName} value cannot be empty.");
                return;
            }
            if (trimmed.Length > QueryLimits.MaxQueryLength)
            {
                AddParseError($"Error: {optionName} value too long (max {QueryLimits.MaxQueryLength} characters).");
                return;
            }
            if (!target.Contains(trimmed, StringComparer.Ordinal))
                target.Add(trimmed);
        }

        void AddIssueDraftLabels(string rawLabels)
        {
            if (string.IsNullOrWhiteSpace(rawLabels))
            {
                AddParseError("Error: --issue-label value cannot be empty.");
                return;
            }

            foreach (var label in rawLabels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (issueLabels.Count >= MaxIssueDraftLabelCount)
                {
                    AddParseError($"Error: search issue drafts accept at most {MaxIssueDraftLabelCount} labels.");
                    return;
                }
                if (label.Length > IssueDuplicatePreflight.MaxOpenIssueLabelLength)
                {
                    AddParseError($"Error: --issue-label value too long (max {IssueDuplicatePreflight.MaxOpenIssueLabelLength} characters).");
                    return;
                }
                if (!issueLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                    issueLabels.Add(label);
            }
        }

        void AddRecipeQuerySelectors(string optionName, string rawSelectors, List<string> selectors)
        {
            if (string.IsNullOrWhiteSpace(rawSelectors))
            {
                AddParseError($"Error: {optionName} value cannot be empty.");
                return;
            }

            foreach (var selector in rawSelectors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (selectors.Count >= MaxSearchRecipeQuerySelectorCount)
                {
                    AddParseError($"Error: search recipes accept at most {MaxSearchRecipeQuerySelectorCount} {optionName} values.");
                    return;
                }
                if (selector.Length > MaxSearchRecipeQuerySelectorLength)
                {
                    AddParseError($"Error: {optionName} value too long (max {MaxSearchRecipeQuerySelectorLength} characters).");
                    return;
                }
                if (!selectors.Contains(selector, StringComparer.OrdinalIgnoreCase))
                    selectors.Add(selector);
            }
        }

        void AddStatusCheckScopes(string rawScopes)
        {
            if (string.IsNullOrWhiteSpace(rawScopes))
            {
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
                return;
            }
            if (!ValidateCsvBounds("--check", rawScopes, MaxStatusCheckScopesCsvLength, MaxStatusCheckScopesCsvEntries, AddParseError))
                return;

            statusCheckScopes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawScope in rawScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var scope = rawScope.ToLowerInvariant();
                switch (scope)
                {
                    case "workspace":
                    case "fold":
                    case "graph":
                    case "issues":
                    case "hotspot":
                    case "csharp":
                    case "sql":
                    case "newer":
                        statusCheckScopes.Add(scope);
                        break;
                    default:
                        AddParseError($"Error: unsupported --check scope '{ConsoleUi.FormatBoundedValue(rawScope)}'. Use one or more of workspace, fold, graph, issues, hotspot, csharp, sql, newer.");
                        break;
                }
            }

            if (statusCheckScopes.Count == 0)
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
        }

        // Track non-repeatable value-taking options that have already been observed and warn on
        // subsequent occurrences. Previously `--db /A --db /B` silently used `/B`; this makes the
        // override explicit so users (and AI callers) can spot a copy/paste or scripted mistake.
        // 非 repeatable な value-taking オプションの初出を記録し、2 回目以降で警告する。以前は
        // `--db /A --db /B` が silent に `/B` を採用していたため、スクリプトやコピペのミスに
        // ユーザーや AI 呼び出し側が気付けるよう、上書きを明示化する。
        var seenSingleValueOptions = new HashSet<string>(StringComparer.Ordinal);
        void WarnIfDuplicateSingleValueOption(string canonicalName, string newValue)
        {
            if (seenSingleValueOptions.Add(canonicalName))
                return;
            var displayValue = ConsoleUi.FormatBoundedValue(newValue);
            CommandErrorWriter.WriteStderr($"Warning: {canonicalName} specified more than once; the rightmost CLI value '{displayValue}' takes precedence over earlier CLI values and any environment/config default.");
        }

        for (int i = 0; i < args.Length; i++)
        {
            var currentArg = args[i];
            if (allowStatusCheck && currentArg.StartsWith("--check=", StringComparison.Ordinal))
            {
                checkWorkspace = true;
                AddStatusCheckScopes(currentArg["--check=".Length..]);
                continue;
            }

            var inlineValue = TrySplitInlineOptionValue(currentArg, out var inlineOptionName)
                ? currentArg[(inlineOptionName!.Length + 1)..]
                : null;
            var normalizedArg = inlineOptionName ?? currentArg;

            switch (normalizedArg)
            {
                case "--":
                    if (i + 1 >= args.Length)
                    {
                        AddParseError("Error: -- requires a following literal query.");
                    }
                    else if (query == null)
                    {
                        query = args[++i];
                    }
                    else
                    {
                        extraNames.Add(args[++i]);
                    }
                    break;
                case "--db":
                    if (TryReadStringOptionValue(args, ref i, "--db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dbPathValue, out var dbPathError))
                    {
                        WarnIfDuplicateSingleValueOption("--db", dbPathValue!);
                        dbPath = dbPathValue!;
                        dbPathExplicit = true;
                    }
                    else
                        AddParseError(dbPathError!);
                    break;
                case "--read-only":
                case "--immutable":
                    readOnly = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--pretty":
                    break;
                case "--compact":
                    compact = true;
                    json = true;
                    outputFormat = OutputFormatJson;
                    break;
                case "--body-only":
                    includeBody = true;
                    inspectFields = ["definitions"];
                    json = true;
                    outputFormat = OutputFormatJson;
                    break;
                case "--outline-only":
                    inspectFields = ["file", "definitions", "nearby_symbols"];
                    json = true;
                    if (outputFormat == OutputFormatText)
                        outputFormat = OutputFormatJson;
                    break;
                case "--workspace-db":
                    if (TryReadStringOptionValue(args, ref i, "--workspace-db", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var workspaceDbPath, out var workspaceDbError))
                        workspaceDbPaths.Add(workspaceDbPath!);
                    else
                        AddParseError(workspaceDbError!);
                    break;
                case "--data-dir":
                    if (TryReadStringOptionValue(args, ref i, "--data-dir", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dataDirValue, out var dataDirError))
                    {
                        WarnIfDuplicateSingleValueOption("--data-dir", dataDirValue!);
                        dataDir = dataDirValue!;
                    }
                    else
                        AddParseError(dataDirError!);
                    break;
                case "--json":
                    if (inlineValue == null)
                    {
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else if (TryParseJsonOutputFormat(inlineValue, out var parsedJsonOutputFormat))
                    {
                        json = true;
                        jsonOutputFormat = parsedJsonOutputFormat;
                        jsonOutputFormatExplicit = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError($"Error: --json format must be one of ndjson or array, got '{ConsoleUi.FormatBoundedValue(inlineValue)}'. Hint: use `--json` or `--json=ndjson` for newline-delimited JSON, or `--json=array` for a single JSON array.");
                    }
                    break;
                case "--indexed-only":
                    languagesIndexedOnly = true;
                    break;
                case "--capability":
                    if (!TryReadStringOptionValue(args, ref i, "--capability", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var capabilityValue, out var capabilityError))
                    {
                        AddParseError(capabilityError!);
                    }
                    else if (TryNormalizeLanguageCapability(capabilityValue!, out var capability))
                    {
                        languageCapabilities.Add(capability);
                    }
                    else
                    {
                        AddParseError($"Error: unsupported --capability value '{ConsoleUi.FormatBoundedValue(capabilityValue)}'. Use graph, references, symbols, missing-graph, missing-references, missing-symbols, or search-only.");
                    }
                    break;
                case "--language":
                    if (TryReadStringOptionValue(args, ref i, "--language", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageValue, out var languageError))
                    {
                        languageLookups.Add(languageValue!);
                        lang = NormalizeLangFilterValue(languageValue);
                    }
                    else
                    {
                        AddParseError(languageError!);
                    }
                    break;
                case "--extension":
                    if (TryReadStringOptionValue(args, ref i, "--extension", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageExtensionValue, out var languageExtensionError))
                        languageExtensionLookups.Add(languageExtensionValue!);
                    else
                        AddParseError(languageExtensionError!);
                    break;
                case "--alias":
                    if (TryReadStringOptionValue(args, ref i, "--alias", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var languageAliasValue, out var languageAliasError))
                        languageAliasLookups.Add(languageAliasValue!);
                    else
                        AddParseError(languageAliasError!);
                    break;
                case "--format":
                    if (TryReadStringOptionValue(args, ref i, "--format", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var formatValue, out var formatError))
                    {
                        WarnIfDuplicateSingleValueOption("--format", formatValue!);
                        if (TryParseOutputFormat(formatValue!, out var parsedOutputFormat))
                        {
                            outputFormat = parsedOutputFormat;
                            if (parsedOutputFormat == OutputFormatCompact)
                                compact = true;
                            if (parsedOutputFormat == OutputFormatCount)
                                countOnly = true;
                            if (parsedOutputFormat != OutputFormatText &&
                                parsedOutputFormat != OutputFormatDot &&
                                parsedOutputFormat != OutputFormatGraphMl)
                                json = true;
                        }
                        else if (allowIssueDraftsFormat && string.Equals(formatValue, OutputFormatIssueDrafts, StringComparison.OrdinalIgnoreCase))
                        {
                            outputFormat = OutputFormatIssueDrafts;
                            json = true;
                        }
                        else
                        {
                            var allowedFormats = allowIssueDraftsFormat
                                ? "text, json, count, compact, csv, tsv, lsp, qf, sarif, or issue-drafts"
                                : "text, json, count, compact, csv, tsv, lsp, qf, or sarif";
                            AddParseError($"Error: --format must be one of {allowedFormats}; got '{ConsoleUi.FormatBoundedValue(formatValue)}'.");
                        }
                    }
                    else
                    {
                        AddParseError(formatError!);
                    }
                    break;
                case "--limit":
                case "--max-results":
                case "--top":
                    var limitOptionName = normalizedArg == "--top" ? "--limit" : normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, limitOptionName, inlineValue, out var limitValue, out var missingLimitError))
                        AddParseError(missingLimitError!);
                    else if (TryParsePositiveInt(limitValue!, limitOptionName, out var parsedLimit, out var limitError))
                    {
                        WarnIfDuplicateSingleValueOption("--limit", limitValue!);
                        limit = parsedLimit;
                        limitExplicit = true;
                    }
                    else
                        AddParseError(limitError!);
                    break;
                case "--total-limit":
                    if (!TryReadRawOptionValue(args, ref i, "--total-limit", inlineValue, out var totalLimitValue, out var missingTotalLimitError))
                        AddParseError(missingTotalLimitError!);
                    else if (TryParseNonNegativeInt(totalLimitValue!, "--total-limit", out var parsedTotalLimit, out var totalLimitError))
                    {
                        WarnIfDuplicateSingleValueOption("--total-limit", totalLimitValue!);
                        totalLimit = parsedTotalLimit;
                    }
                    else
                        AddParseError(totalLimitError!);
                    break;
                case "--lang":
                    if (TryReadStringOptionValue(args, ref i, "--lang", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var langValue, out var langError))
                    {
                        WarnIfDuplicateSingleValueOption("--lang", langValue!);
                        // Normalize to lowercase so '--lang Python' == '--lang python' — every LangMap key and
                        // every DB 'files.lang' row is lowercase, so the SQL filter and WriteLangHint match.
                        // Also fold common short aliases (e.g. `py`) to canonical language names so Python-heavy
                        // workflows can use familiar shorthand without silently returning zero rows.
                        // '--lang Python' と '--lang python' を同一視するため lowercase 正規化する。LangMap の key と
                        // DB の `files.lang` はすべて lowercase なので、SQL filter と WriteLangHint が一致する。
                        // さらに `py` のような短縮エイリアスを正規名へ畳み込み、Python 利用時の慣用入力で
                        // 意図せず 0 件になる事故を避ける。
                        lang = NormalizeLangFilterValue(langValue);
                    }
                    else
                        AddParseError(langError!);
                    break;
                case "--query":
                    if (!allowNamedQuery)
                    {
                        AddParseError("Error: --query is not supported by this command.");
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            i++;
                    }
                    else if (TryReadStringOptionValue(args, ref i, "--query", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var queryValue, out var queryError))
                    {
                        WarnIfDuplicateSingleValueOption("--query", queryValue!);
                        query = queryValue;
                    }
                    else
                        AddParseError(queryError!);
                    break;
                case "--recipe":
                    if (TryReadStringOptionValue(args, ref i, "--recipe", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var recipeValue, out var recipeError))
                    {
                        WarnIfDuplicateSingleValueOption("--recipe", recipeValue!);
                        recipeName = recipeValue;
                    }
                    else
                        AddParseError(recipeError!);
                    break;
                case "--include-query":
                    if (TryReadStringOptionValue(args, ref i, "--include-query", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var includeQueryValue, out var includeQueryError))
                        AddRecipeQuerySelectors("--include-query", includeQueryValue!, includeRecipeQueries);
                    else
                        AddParseError(includeQueryError!);
                    break;
                case "--exclude-query":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-query", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludeQueryValue, out var excludeQueryError))
                        AddRecipeQuerySelectors("--exclude-query", excludeQueryValue!, excludeRecipeQueries);
                    else
                        AddParseError(excludeQueryError!);
                    break;
                case "--show-excluded":
                    showExcluded = true;
                    break;
                case "--list-recipes":
                    listRecipes = true;
                    break;
                case "--names":
                    namesOnly = true;
                    break;
                case "--source-only":
                    sourceOnly = true;
                    auditScope = SearchAuditRecipes.DefaultAuditScope;
                    auditScopeExplicit = true;
                    break;
                case "--open-issues":
                    if (TryReadStringOptionValue(args, ref i, "--open-issues", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var openIssuesValue, out var openIssuesError))
                    {
                        WarnIfDuplicateSingleValueOption("--open-issues", openIssuesValue!);
                        openIssuesPath = openIssuesValue;
                    }
                    else
                        AddParseError(openIssuesError!);
                    break;
                case "--audit-scope":
                    if (!TryReadStringOptionValue(args, ref i, "--audit-scope", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var auditScopeValue, out var auditScopeError))
                    {
                        AddParseError(auditScopeError!);
                    }
                    else if (TryNormalizeSearchAuditScope(auditScopeValue!, out var normalizedAuditScope))
                    {
                        WarnIfDuplicateSingleValueOption("--audit-scope", auditScopeValue!);
                        auditScope = normalizedAuditScope;
                        auditScopeExplicit = true;
                    }
                    else
                    {
                        AddParseError($"Error: unsupported --audit-scope value '{ConsoleUi.FormatBoundedValue(auditScopeValue)}'. Use source or all.");
                    }
                    break;
                case "--repo":
                    if (TryReadStringOptionValue(args, ref i, "--repo", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var repoValue, out var repoError))
                    {
                        WarnIfDuplicateSingleValueOption("--repo", repoValue!);
                        openIssuesRepository = repoValue;
                    }
                    else
                        AddParseError(repoError!);
                    break;
                case "--duplicate-confidence":
                    if (TryReadStringOptionValue(args, ref i, "--duplicate-confidence", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var duplicateConfidenceValue, out var duplicateConfidenceError))
                    {
                        WarnIfDuplicateSingleValueOption("--duplicate-confidence", duplicateConfidenceValue!);
                        if (IssueDuplicatePreflight.TryNormalizeDuplicateConfidence(duplicateConfidenceValue!, out var normalizedDuplicateConfidence))
                        {
                            duplicateConfidence = normalizedDuplicateConfidence;
                            duplicateThreshold = IssueDuplicatePreflight.ThresholdForDuplicateConfidence(normalizedDuplicateConfidence);
                            duplicateConfidenceExplicit = true;
                        }
                        else
                        {
                            AddParseError($"Error: --duplicate-confidence must be one of low, medium, high; got '{ConsoleUi.FormatBoundedValue(duplicateConfidenceValue)}'.");
                        }
                    }
                    else
                    {
                        AddParseError(duplicateConfidenceError!);
                    }
                    break;
                case "--duplicate-threshold":
                    if (!TryReadRawOptionValue(args, ref i, "--duplicate-threshold", inlineValue, out var duplicateThresholdValue, out var missingDuplicateThresholdError))
                    {
                        AddParseError(missingDuplicateThresholdError!);
                    }
                    else if (TryParseConfidence(duplicateThresholdValue!, out var parsedDuplicateThreshold))
                    {
                        WarnIfDuplicateSingleValueOption("--duplicate-threshold", duplicateThresholdValue!);
                        duplicateThreshold = parsedDuplicateThreshold;
                        duplicateThresholdExplicit = true;
                    }
                    else
                    {
                        AddParseError($"Error: --duplicate-threshold must be a number between 0 and 1; got '{ConsoleUi.FormatBoundedValue(duplicateThresholdValue)}'.");
                    }
                    break;
                case "--issue-title":
                    if (TryReadStringOptionValue(args, ref i, "--issue-title", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var issueTitleValue, out var issueTitleError))
                    {
                        WarnIfDuplicateSingleValueOption("--issue-title", issueTitleValue!);
                        var trimmedTitle = issueTitleValue!.Trim();
                        if (trimmedTitle.Length == 0)
                            AddParseError("Error: --issue-title value cannot be empty.");
                        else if (trimmedTitle.Length > MaxIssueDraftTitleLength)
                            AddParseError($"Error: --issue-title value too long (max {MaxIssueDraftTitleLength} characters).");
                        else
                            issueTitle = trimmedTitle;
                    }
                    else
                        AddParseError(issueTitleError!);
                    break;
                case "--issue-label":
                    if (TryReadStringOptionValue(args, ref i, "--issue-label", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var issueLabelValue, out var issueLabelError))
                        AddIssueDraftLabels(issueLabelValue!);
                    else
                        AddParseError(issueLabelError!);
                    break;
                case "--cursor":
                    if (TryReadStringOptionValue(args, ref i, "--cursor", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var cursorValue, out var cursorError))
                    {
                        WarnIfDuplicateSingleValueOption("--cursor", cursorValue!);
                        if (TryParseSearchCursor(cursorValue!, out var parsedCursor))
                            searchCursor = parsedCursor;
                        else if (TryParseUnusedCursor(cursorValue!, out var parsedUnusedCursorOffset))
                            unusedCursorOffset = parsedUnusedCursorOffset;
                        else if (TryParseOutlineCursor(cursorValue!, out var parsedOutlineCursorOffset))
                            outlineCursorOffset = parsedOutlineCursorOffset;
                        else
                            AddParseError("Error: --cursor must be a search, unused, or outline pagination cursor returned as `next_cursor`.");
                    }
                    else
                    {
                        AddParseError(cursorError!);
                    }
                    break;
                case "--named-query":
                    if (!allowNamedQuery)
                    {
                        AddParseError("Error: --named-query is not supported by this command.");
                        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            i++;
                    }
                    else if (TryReadStringOptionValue(args, ref i, "--named-query", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var namedQueryValue, out var namedQueryError))
                    {
                        if (TryParseNamedSearchQuery(namedQueryValue!, out var namedQuery, out var namedQueryParseError))
                            namedSearchQueries.Add(namedQuery);
                        else
                            AddParseError(namedQueryParseError!);
                    }
                    else
                    {
                        AddParseError(namedQueryError!);
                    }
                    break;
                case "--require-before":
                    if (TryReadStringOptionValue(args, ref i, "--require-before", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var requireBeforeValue, out var requireBeforeError))
                        AddSearchGuardFilter("--require-before", SearchGuardRole.Require, SearchGuardDirection.Before, requireBeforeValue!);
                    else
                        AddParseError(requireBeforeError!);
                    break;
                case "--require-after":
                    if (TryReadStringOptionValue(args, ref i, "--require-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var requireAfterValue, out var requireAfterError))
                        AddSearchGuardFilter("--require-after", SearchGuardRole.Require, SearchGuardDirection.After, requireAfterValue!);
                    else
                        AddParseError(requireAfterError!);
                    break;
                case "--reject-before":
                    if (TryReadStringOptionValue(args, ref i, "--reject-before", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var rejectBeforeValue, out var rejectBeforeError))
                        AddSearchGuardFilter("--reject-before", SearchGuardRole.Reject, SearchGuardDirection.Before, rejectBeforeValue!);
                    else
                        AddParseError(rejectBeforeError!);
                    break;
                case "--reject-after":
                    if (TryReadStringOptionValue(args, ref i, "--reject-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var rejectAfterValue, out var rejectAfterError))
                        AddSearchGuardFilter("--reject-after", SearchGuardRole.Reject, SearchGuardDirection.After, rejectAfterValue!);
                    else
                        AddParseError(rejectAfterError!);
                    break;
                case "--guard-window":
                    if (!TryReadRawOptionValue(args, ref i, "--guard-window", inlineValue, out var guardWindowValue, out var missingGuardWindowError))
                    {
                        AddParseError(missingGuardWindowError!);
                    }
                    else if (TryParseNonNegativeInt(guardWindowValue!, "--guard-window", out var parsedGuardWindow, out var guardWindowError))
                    {
                        WarnIfDuplicateSingleValueOption("--guard-window", guardWindowValue!);
                        if (parsedGuardWindow > DbReader.MaxSearchGuardWindow)
                            AddParseError($"Error: --guard-window must be between 0 and {DbReader.MaxSearchGuardWindow}; got {parsedGuardWindow}.");
                        else
                            guardWindow = parsedGuardWindow;
                    }
                    else
                    {
                        AddParseError(guardWindowError!);
                    }
                    break;
                case "--guard-scope":
                    if (TryReadStringOptionValue(args, ref i, "--guard-scope", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var guardScopeValue, out var guardScopeError))
                    {
                        WarnIfDuplicateSingleValueOption("--guard-scope", guardScopeValue!);
                        if (TryNormalizeSearchGuardScope(guardScopeValue!, out var parsedGuardScope))
                            guardScope = parsedGuardScope;
                        else
                            AddParseError($"Error: unsupported --guard-scope value '{ConsoleUi.FormatBoundedValue(guardScopeValue!)}'. Use window or same-line.");
                    }
                    else
                        AddParseError(guardScopeError!);
                    break;
                case "--kind":
                    if (TryReadStringOptionValue(args, ref i, "--kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var kindValue, out var kindError))
                    {
                        WarnIfDuplicateSingleValueOption("--kind", kindValue!);
                        // Normalize to lowercase so '--kind FUNCTION' == '--kind function'. AllValidKinds entries
                        // and every DB 'symbols.kind' row are lowercase.
                        // '--kind FUNCTION' と '--kind function' を同一視するため lowercase 正規化する。AllValidKinds
                        // と DB の `symbols.kind` はすべて lowercase。
                        kind = kindValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(kindError!);
                    break;
                case "--bucket":
                    if (TryReadStringOptionValue(args, ref i, "--bucket", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var unusedBucketValue, out var unusedBucketError))
                    {
                        WarnIfDuplicateSingleValueOption("--bucket", unusedBucketValue!);
                        unusedBucket = unusedBucketValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(unusedBucketError!);
                    break;
                case "--confidence":
                case "--min-confidence":
                    var confidenceFlag = normalizedArg;
                    if (TryReadStringOptionValue(args, ref i, confidenceFlag, inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var minUnusedConfidenceValue, out var minUnusedConfidenceError))
                    {
                        WarnIfDuplicateSingleValueOption("--min-confidence", minUnusedConfidenceValue!);
                        minUnusedConfidence = minUnusedConfidenceValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(minUnusedConfidenceError!);
                    break;
                case "--severity":
                    if (TryReadStringOptionValue(args, ref i, "--severity", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var severityValue, out var severityError))
                    {
                        WarnIfDuplicateSingleValueOption("--severity", severityValue!);
                        severity = severityValue?.ToLowerInvariant();
                    }
                    else
                    {
                        AddParseError(severityError!);
                    }
                    break;
                case "--visibility":
                    if (TryReadStringOptionValue(args, ref i, "--visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var visibilityValue, out var visibilityError))
                        AddVisibilityFilterValues("--visibility", visibilityValue!, visibilityFilters, AddParseError);
                    else
                        AddParseError(visibilityError!);
                    break;
                case "--exclude-visibility":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-visibility", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludeVisibilityValue, out var excludeVisibilityError))
                        AddVisibilityFilterValues("--exclude-visibility", excludeVisibilityValue!, excludeVisibilityFilters, AddParseError);
                    else
                        AddParseError(excludeVisibilityError!);
                    break;
                case "--rank-by":
                    if (TryReadStringOptionValue(args, ref i, "--rank-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var rankByValue, out var rankByError))
                    {
                        WarnIfDuplicateSingleValueOption("--rank-by", rankByValue!);
                        if (TryParseReferenceRankMode(rankByValue!, out var parsedRankMode))
                            rankMode = parsedRankMode;
                        else
                            AddParseError($"Error: --rank-by must be one of weighted, count, kind; got '{rankByValue}'.");
                    }
                    else
                        AddParseError(rankByError!);
                    break;
                case "--sort":
                    if (TryReadStringOptionValue(args, ref i, "--sort", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sortRawValue, out var sortError))
                    {
                        WarnIfDuplicateSingleValueOption("--sort", sortRawValue!);
                        var normalizedSortValue = sortRawValue!;
                        if (allowOutlineSort && TryParseOutlineSortMode(normalizedSortValue, out _))
                        {
                            sortExplicit = true;
                        }
                        else if (!allowOutlineSort && TryParseSymbolSortMode(normalizedSortValue, out var parsedSortMode))
                        {
                            symbolSortMode = parsedSortMode;
                            sortExplicit = true;
                        }
                        else
                        {
                            var allowedSortValues = allowOutlineSort
                                ? "source, kind, references, size, span, complexity, path, or name"
                                : "hotspot, references, size, complexity, path";
                            AddParseError($"Error: --sort must be one of {allowedSortValues}; got '{normalizedSortValue}'.");
                        }
                        sortValue = normalizedSortValue;
                    }
                    else
                        AddParseError(sortError!);
                    break;
                case "--sections":
                    if (TryReadStringOptionValue(args, ref i, "--sections", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sectionsValue, out var sectionsError))
                    {
                        WarnIfDuplicateSingleValueOption("--sections", sectionsValue!);
                        mapSections = ParseMapSections(sectionsValue!, AddParseError);
                    }
                    else
                        AddParseError(sectionsError!);
                    break;
                case "--summary-only":
                    summaryOnly = true;
                    mapSummaryOnly = true;
                    break;
                case "--fields":
                    if (TryReadStringOptionValue(args, ref i, "--fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var fieldsValue, out var fieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--fields", fieldsValue!);
                        inspectFields = ParseInspectFields(fieldsValue!, AddParseError, out var includeBodyFromFields);
                        includeBody |= includeBodyFromFields;
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError(fieldsError!);
                    }
                    break;
                case "--fts":
                    rawFts = true;
                    break;
                case "--body":
                    includeBody = true;
                    break;
                case "--body-start":
                    if (!TryReadRawOptionValue(args, ref i, "--body-start", inlineValue, out var bodyStartValue, out var missingBodyStartError))
                        AddParseError(missingBodyStartError!);
                    else if (TryParsePositiveInt(bodyStartValue!, "--body-start", out var parsedBodyStartLine, out var bodyStartError))
                    {
                        WarnIfDuplicateSingleValueOption("--body-start", bodyStartValue!);
                        bodyStartLine = parsedBodyStartLine;
                        includeBody = true;
                    }
                    else
                        AddParseError(bodyStartError!);
                    break;
                case "--body-lines":
                case "--body-line-count":
                    var bodyLinesFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, bodyLinesFlag, inlineValue, out var bodyLinesValue, out var missingBodyLinesError))
                        AddParseError(missingBodyLinesError!);
                    else if (TryParsePositiveInt(bodyLinesValue!, bodyLinesFlag, out var parsedBodyLines, out var bodyLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--body-lines", bodyLinesValue!);
                        bodyLines = parsedBodyLines;
                        includeBody = true;
                    }
                    else
                        AddParseError(bodyLinesError!);
                    break;
                case "--count":
                    countOnly = true;
                    break;
                case "--cycles":
                    dependencyCycles = true;
                    break;
                case "--suppress-noise":
                    dependencySuppressNoise = true;
                    break;
                case "--symbol":
                    if (TryReadStringOptionValue(args, ref i, "--symbol", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dependencySymbolValue, out var dependencySymbolError))
                        AddDependencySymbolFilter("--symbol", dependencySymbolValue!, dependencySymbols);
                    else
                        AddParseError(dependencySymbolError!);
                    break;
                case "--symbol-family":
                    if (TryReadStringOptionValue(args, ref i, "--symbol-family", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var dependencySymbolFamilyValue, out var dependencySymbolFamilyError))
                        AddDependencySymbolFilter("--symbol-family", dependencySymbolFamilyValue!, dependencySymbolFamilies);
                    else
                        AddParseError(dependencySymbolFamilyError!);
                    break;
                case "--strict-not-found":
                    strictNotFound = true;
                    break;
                case "--strict":
                    strict = true;
                    break;
                case "--by-bucket":
                    break;
                case "--all":
                    all = true;
                    break;
                case "--no-dedup":
                    noDedup = true;
                    break;
                case "--no-visibility-rank":
                    noVisibilityRank = true;
                    break;
                case "--exact":
                    exact = true;
                    break;
                case "--regex":
                    regex = true;
                    break;
                case "--exact-name":
                    exactName = true;
                    break;
                case "--exact-substring":
                    exactSubstring = true;
                    break;
                case "--prefix":
                    prefix = true;
                    break;
                case "--max-hops":
                case "--depth":
                    var depthOptionName = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, depthOptionName, inlineValue, out var depthValue, out var missingDepthError))
                        AddParseError(missingDepthError!);
                    else if (TryParseNonNegativeInt(depthValue!, depthOptionName, out var parsedDepth, out var depthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-hops", depthValue!);
                        contextAfter = parsedDepth; // reused as depth for impact / impact用に再利用
                        contextAfterExplicit = true;
                        if (depthOptionName == "--depth")
                            impactDeprecatedDepthUsed = true;
                    }
                    else
                        AddParseError(depthError!);
                    break;
                case "--reverse":
                    break; // handled by specific commands / 特定コマンドで処理
                case "--group-by-name":
                    break;
                case "--group-by":
                    if (TryReadStringOptionValue(args, ref i, "--group-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var groupByValue, out var groupByError))
                    {
                        WarnIfDuplicateSingleValueOption("--group-by", groupByValue!);
                        groupBy = groupByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(groupByError!);
                    break;
                case "--unique":
                    if (TryReadStringOptionValue(args, ref i, "--unique", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var uniqueValue, out var uniqueError))
                    {
                        WarnIfDuplicateSingleValueOption("--unique", uniqueValue!);
                        uniqueBy = uniqueValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(uniqueError!);
                    break;
                case "--count-by":
                    if (TryReadStringOptionValue(args, ref i, "--count-by", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var countByValue, out var countByError))
                    {
                        WarnIfDuplicateSingleValueOption("--count-by", countByValue!);
                        countBy = countByValue?.ToLowerInvariant();
                    }
                    else
                        AddParseError(countByError!);
                    break;
                case "--origin":
                case "--match-origin":
                    var originOptionName = normalizedArg;
                    if (TryReadStringOptionValue(args, ref i, originOptionName, inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var originValue, out var originError))
                        AddSearchMatchOrigins(originOptionName, originValue!, matchOrigins, AddParseError);
                    else
                        AddParseError(originError!);
                    break;
                case "--exclude-origin":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-origin", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var excludedOriginValue, out var excludedOriginError))
                        AddSearchMatchOrigins("--exclude-origin", excludedOriginValue!, excludeOrigins, AddParseError);
                    else
                        AddParseError(excludedOriginError!);
                    break;
                case "--result-kind":
                    if (TryReadStringOptionValue(args, ref i, "--result-kind", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var resultKindValue, out var resultKindError))
                        AddSearchResultKinds(resultKindValue!, resultKinds, AddParseError);
                    else
                        AddParseError(resultKindError!);
                    break;
                case "--search-fields":
                    if (TryReadStringOptionValue(args, ref i, "--search-fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var searchFieldsValue, out var searchFieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--search-fields", searchFieldsValue!);
                        searchFields = ParseSearchProjectionFields(searchFieldsValue!, AddParseError);
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                        AddParseError(searchFieldsError!);
                    break;
                case "--first-per-file":
                    firstPerFile = true;
                    break;
                case "--results-only":
                    resultsOnly = true;
                    json = true;
                    jsonOutputFormat = JsonOutputFormatNdjson;
                    outputFormat = OutputFormatJson;
                    break;
                case "--next-steps":
                    nextSteps = true;
                    break;
                case "--sample":
                    if (!TryReadRawOptionValue(args, ref i, "--sample", inlineValue, out var sampleValue, out var missingSampleError))
                        AddParseError(missingSampleError!);
                    else if (TryParsePositiveInt(sampleValue!, "--sample", out var parsedSample, out var sampleError))
                    {
                        WarnIfDuplicateSingleValueOption("--sample", sampleValue!);
                        sampleSize = parsedSample;
                    }
                    else
                        AddParseError(sampleError!);
                    break;
                case "--per-file-limit":
                    if (!TryReadRawOptionValue(args, ref i, "--per-file-limit", inlineValue, out var perFileLimitValue, out var missingPerFileLimitError))
                        AddParseError(missingPerFileLimitError!);
                    else if (TryParsePositiveInt(perFileLimitValue!, "--per-file-limit", out var parsedPerFileLimit, out var perFileLimitError))
                    {
                        WarnIfDuplicateSingleValueOption("--per-file-limit", perFileLimitValue!);
                        groupedPerFileLimit = Math.Min(parsedPerFileLimit, MaxSearchGroupedPerFileLimit);
                    }
                    else
                        AddParseError(perFileLimitError!);
                    break;
                case "--max-json-bytes":
                    if (!TryReadRawOptionValue(args, ref i, "--max-json-bytes", inlineValue, out var maxJsonBytesValue, out var missingMaxJsonBytesError))
                        AddParseError(missingMaxJsonBytesError!);
                    else if (TryParsePositiveInt(maxJsonBytesValue!, "--max-json-bytes", out var parsedMaxJsonBytes, out var maxJsonBytesError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-json-bytes", maxJsonBytesValue!);
                        maxJsonBytes = Math.Min(parsedMaxJsonBytes, MaxSearchJsonByteLimit);
                    }
                    else
                        AddParseError(maxJsonBytesError!);
                    break;
                case "--with-paths":
                    withPaths = true;
                    break;
                case "--bytes":
                    rawBytes = true;
                    break;
                case "--raw-kinds":
                    rawKinds = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--profile":
                    profile = true;
                    break;
                case "--slow-query-ms":
                    if (!TryReadRawOptionValue(args, ref i, "--slow-query-ms", inlineValue, out var slowQueryValue, out var missingSlowQueryError))
                        AddParseError(missingSlowQueryError!);
                    else if (TryParseNonNegativeInt(slowQueryValue!, "--slow-query-ms", out var parsedSlowQueryMs, out var slowQueryError))
                    {
                        WarnIfDuplicateSingleValueOption("--slow-query-ms", slowQueryValue!);
                        slowQueryMs = parsedSlowQueryMs;
                    }
                    else
                        AddParseError(slowQueryError!);
                    break;
                case "--min-entrypoint-confidence":
                    if (!TryReadRawOptionValue(args, ref i, "--min-entrypoint-confidence", inlineValue, out var minEntrypointConfidenceValue, out var missingMinEntrypointConfidenceError))
                        AddParseError(missingMinEntrypointConfidenceError!);
                    else if (TryParseConfidence(minEntrypointConfidenceValue!, out var parsedMinEntrypointConfidence))
                    {
                        WarnIfDuplicateSingleValueOption("--min-entrypoint-confidence", minEntrypointConfidenceValue!);
                        minEntrypointConfidence = parsedMinEntrypointConfidence;
                    }
                    else
                        AddParseError($"Error: --min-entrypoint-confidence must be a number from 0.0 through 1.0; got '{ConsoleUi.FormatBoundedValue(minEntrypointConfidenceValue)}'.");
                    break;
                case "--check":
                    if (allowStatusCheck)
                    {
                        checkWorkspace = true;
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --check is not supported by this command.");
                    }
                    break;
                case "--outline-fields":
                    if (TryReadStringOptionValue(args, ref i, "--outline-fields", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var outlineFieldsValue, out var outlineFieldsError))
                    {
                        WarnIfDuplicateSingleValueOption("--outline-fields", outlineFieldsValue!);
                        outlineFields = ParseOutlineProjectionFields(outlineFieldsValue!, AddParseError);
                        outlineFieldsExplicit = true;
                        json = true;
                        outputFormat = OutputFormatJson;
                    }
                    else
                    {
                        AddParseError(outlineFieldsError!);
                    }
                    break;
                case "--stale-after":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--stale-after", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var staleAfterValue, out var staleAfterError))
                        {
                            WarnIfDuplicateSingleValueOption("--stale-after", staleAfterValue!);
                            if (TryParseStaleAfter(staleAfterValue!, out var parsedStaleAfter, out var parseStaleAfterError))
                                staleAfter = parsedStaleAfter;
                            else
                                AddParseError(parseStaleAfterError!);
                        }
                        else
                        {
                            AddParseError(staleAfterError!);
                        }
                    }
                    else
                    {
                        AddParseError("Error: --stale-after is not supported by this command.");
                    }
                    break;
                case "--explain":
                    if (allowStatusCheck)
                    {
                        if (TryReadStringOptionValue(args, ref i, "--explain", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var explainValue, out var explainError))
                        {
                            WarnIfDuplicateSingleValueOption("--explain", explainValue!);
                            statusExplainField = explainValue;
                        }
                        else
                            AddParseError(explainError!);
                    }
                    else if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError("Error: --explain is not supported by this command.");
                    }
                    break;
                case "--log-path":
                    if (allowStatusCheck)
                    {
                        statusLogPath = true;
                    }
                    else
                    {
                        AddParseError("Error: --log-path is not supported by this command.");
                    }
                    break;
                case "--config":
                    if (allowStatusCheck)
                    {
                        statusConfig = true;
                    }
                    else
                    {
                        AddParseError("Error: --config is only supported by status.");
                    }
                    break;
                case "--log-format":
                case "--log-retain-count":
                case "--log-max-size-mb":
                    if (allowNamedQuery && query == null)
                    {
                        query = currentArg;
                    }
                    else
                    {
                        AddParseError($"Error: unsupported option: {ConsoleUi.FormatBoundedValue(currentArg)}. Use `--` before a query literal that starts with `-`.");
                    }
                    break;
                case "--path":
                    if (TryReadStringOptionValue(args, ref i, "--path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var pathPattern, out var pathError))
                    {
                        pathPatterns.Add(pathPattern!); // Repeatable; multiple values OR together / 繰り返し可、複数値は OR で結合
                        userPathPatterns.Add(pathPattern!);
                    }
                    else
                        AddParseError(pathError!);
                    break;
                case "--project":
                    if (TryReadStringOptionValue(args, ref i, "--project", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var projectName, out var projectError))
                        projectFilters.Add(projectName!);
                    else
                        AddParseError(projectError!);
                    break;
                case "--solution":
                    if (TryReadStringOptionValue(args, ref i, "--solution", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var solutionValue, out var solutionError))
                    {
                        WarnIfDuplicateSingleValueOption("--solution", solutionValue!);
                        solutionFilter = solutionValue;
                    }
                    else
                        AddParseError(solutionError!);
                    break;
                case "--exclude-path":
                    if (TryReadStringOptionValue(args, ref i, "--exclude-path", inlineValue, allowSeparatedDashPrefixedLiteralValue: true, out var excludePath, out var excludePathError))
                        excludePaths.Add(excludePath!);
                    else
                        AddParseError(excludePathError!);
                    break;
                case "--exclude-tests":
                    excludeTests = true;
                    break;
                case "--no-semantic-tokens":
                    noSemanticTokens = true;
                    break;
                case "--exclude-comments":
                    excludeComments = true;
                    break;
                case "--exclude-strings":
                    excludeStrings = true;
                    break;
                case "--exclude-fixtures":
                    excludeFixtures = true;
                    break;
                case "--actionable":
                    unusedActionable = true;
                    break;
                case "--include-generated":
                    includeGenerated = true;
                    break;
                case "--since":
                    if (!TryReadStringOptionValue(args, ref i, "--since", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var sinceValue, out var sinceError))
                        AddParseError(sinceError!);
                    else if (TryParseIso8601Since(sinceValue!, out var parsedSince))
                    {
                        WarnIfDuplicateSingleValueOption("--since", sinceValue!);
                        since = parsedSince;
                    }
                    else
                        AddParseError($"Error: could not parse --since value '{ConsoleUi.FormatBoundedValue(sinceValue)}' as a date/time. Use ISO 8601 format (e.g. 2024-01-01 or 2024-01-01T00:00:00Z).");
                    break;
                case "--line":
                    if (!TryReadRawOptionValue(args, ref i, "--line", inlineValue, out var lineValue, out var missingLineError))
                        AddParseError(missingLineError!);
                    else if (TryParsePositiveInt(lineValue!, "--line", out var parsedLine, out var lineError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", lineValue!);
                        WarnIfDuplicateSingleValueOption("--end", lineValue!);
                        startLine = parsedLine;
                        endLine = parsedLine;
                    }
                    else
                        AddParseError(lineError!);
                    break;
                case "--start":
                case "--start-line":
                    var startFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, startFlag, inlineValue, out var startValue, out var missingStartError))
                        AddParseError(missingStartError!);
                    else if (TryParsePositiveInt(startValue!, startFlag, out var parsedStart, out var startError))
                    {
                        WarnIfDuplicateSingleValueOption("--start", startValue!);
                        startLine = parsedStart;
                    }
                    else
                        AddParseError(startError!);
                    break;
                case "--end":
                case "--end-line":
                    var endFlag = normalizedArg;
                    if (!TryReadRawOptionValue(args, ref i, endFlag, inlineValue, out var endValue, out var missingEndError))
                        AddParseError(missingEndError!);
                    else if (TryParsePositiveInt(endValue!, endFlag, out var parsedEnd, out var endError))
                    {
                        WarnIfDuplicateSingleValueOption("--end", endValue!);
                        endLine = parsedEnd;
                    }
                    else
                        AddParseError(endError!);
                    break;
                case "--context":
                    if (!TryReadRawOptionValue(args, ref i, "--context", inlineValue, out var contextValue, out var missingContextError))
                        AddParseError(missingContextError!);
                    else if (TryParseNonNegativeInt(contextValue!, "--context", out var parsedContext, out var contextError))
                    {
                        WarnIfDuplicateSingleValueOption("--before", contextValue!);
                        WarnIfDuplicateSingleValueOption("--after", contextValue!);
                        contextBefore = parsedContext;
                        contextAfter = parsedContext;
                        contextAfterExplicit = true;
                    }
                    else
                        AddParseError(contextError!);
                    break;
                case "--before":
                    if (!TryReadRawOptionValue(args, ref i, "--before", inlineValue, out var beforeValue, out var missingBeforeError))
                        AddParseError(missingBeforeError!);
                    else if (TryParseNonNegativeInt(beforeValue!, "--before", out var parsedBefore, out var beforeError))
                    {
                        WarnIfDuplicateSingleValueOption("--before", beforeValue!);
                        contextBefore = parsedBefore;
                    }
                    else
                        AddParseError(beforeError!);
                    break;
                case "--after":
                    if (!TryReadRawOptionValue(args, ref i, "--after", inlineValue, out var afterValue, out var missingAfterError))
                        AddParseError(missingAfterError!);
                    else if (TryParseNonNegativeInt(afterValue!, "--after", out var parsedAfter, out var afterError))
                    {
                        WarnIfDuplicateSingleValueOption("--after", afterValue!);
                        contextAfter = parsedAfter;
                    }
                    else
                        AddParseError(afterError!);
                    break;
                case "--focus-line":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-line", inlineValue, out var focusLineValue, out var missingFocusLineError))
                        AddParseError(missingFocusLineError!);
                    else if (TryParsePositiveInt(focusLineValue!, "--focus-line", out var parsedFocusLine, out var focusLineError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-line", focusLineValue!);
                        focusLine = parsedFocusLine;
                    }
                    else
                        AddParseError(focusLineError!);
                    break;
                case "--focus-column":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-column", inlineValue, out var focusColumnValue, out var missingFocusColumnError))
                        AddParseError(missingFocusColumnError!);
                    else if (TryParsePositiveInt(focusColumnValue!, "--focus-column", out var parsedFocusColumn, out var focusColumnError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-column", focusColumnValue!);
                        focusColumn = parsedFocusColumn;
                    }
                    else
                        AddParseError(focusColumnError!);
                    break;
                case "--focus-length":
                    if (!TryReadRawOptionValue(args, ref i, "--focus-length", inlineValue, out var focusLengthValue, out var missingFocusLengthError))
                        AddParseError(missingFocusLengthError!);
                    else if (TryParsePositiveInt(focusLengthValue!, "--focus-length", out var parsedFocusLength, out var focusLengthError))
                    {
                        WarnIfDuplicateSingleValueOption("--focus-length", focusLengthValue!);
                        focusLength = parsedFocusLength;
                    }
                    else
                        AddParseError(focusLengthError!);
                    break;
                case "--name":
                    if (TryReadStringOptionValue(args, ref i, "--name", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var extraName, out var nameError))
                        extraNames.Add(extraName!); // Repeatable; OR-joined with other --name values and extra positional names / 繰り返し可、他の --name や追加の positional 引数と OR 結合
                    else
                        AddParseError($"{nameError} / --name には値（シンボル名パターン）が必要です。");
                    break;
                case "--snippet-lines":
                    if (!TryReadRawOptionValue(args, ref i, "--snippet-lines", inlineValue, out var snippetLinesValue, out var missingSnippetLinesError))
                        AddParseError(missingSnippetLinesError!);
                    else if (TryParseNonNegativeInt(snippetLinesValue!, "--snippet-lines", out var parsedSnippetLines, out var snippetLinesError))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-lines", snippetLinesValue!);
                        snippetLines = parsedSnippetLines;
                        snippetLinesExplicit = true;
                    }
                    else
                        AddParseError(snippetLinesError!);
                    break;
                case "--snippet-focus":
                    if (!TryReadStringOptionValue(args, ref i, "--snippet-focus", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var snippetFocusValue, out var snippetFocusError))
                    {
                        AddParseError(snippetFocusError!);
                    }
                    else if (TryParseSnippetFocusMode(snippetFocusValue!, out var parsedSnippetFocus))
                    {
                        WarnIfDuplicateSingleValueOption("--snippet-focus", snippetFocusValue!);
                        snippetFocus = parsedSnippetFocus;
                    }
                    else
                    {
                        AddParseError($"Error: invalid --snippet-focus value '{ConsoleUi.FormatBoundedValue(snippetFocusValue)}'. Use leftmost, quality, or proximity.");
                    }
                    break;
                case "--max-line-width":
                    if (!TryReadRawOptionValue(args, ref i, "--max-line-width", inlineValue, out var maxLineWidthValue, out var missingMaxLineWidthError))
                        AddParseError(missingMaxLineWidthError!);
                    else if (TryParseNonNegativeInt(maxLineWidthValue!, "--max-line-width", out var parsedMaxLineWidth, out var maxLineWidthError))
                    {
                        WarnIfDuplicateSingleValueOption("--max-line-width", maxLineWidthValue!);
                        maxLineWidth = parsedMaxLineWidth;
                        maxLineWidthExplicit = true;
                    }
                    else
                        AddParseError(maxLineWidthError!);
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        AddParseError($"Error: unsupported option: {ConsoleUi.FormatBoundedValue(args[i])}. Use `--` before a query literal that starts with `-`.");
                        break;
                    }
                    else if (query == null)
                    {
                        query = args[i];
                    }
                    else
                    {
                        // Extra positional args become additional symbol names / 追加の positional 引数を追加の symbol name として扱う
                        extraNames.Add(args[i]);
                    }
                    break;
            }
        }

        if (unusedActionable)
        {
            unusedBucket ??= "likely_unused_private";
            minUnusedConfidence ??= "medium";
            if (visibilityFilters.Count == 0)
                visibilityFilters.Add("private");
            excludeTests = true;
        }

        var dbResolution = DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, dbPath, dataDir);
        var resolvedDbPath = dbResolution.DbPath;

        if (parseErrors == null && projectFilters.Count > 0)
        {
            try
            {
                projectFilterRootResolution = ResolveProjectFilterRoot(resolvedDbPath, dbPathExplicit);
                foreach (var glob in SolutionProjectResolver.ResolveProjectDirectoryGlobs(projectFilterRootResolution.Value.Root, projectFilters, solutionFilter))
                    pathPatterns.Add(glob);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddParseError($"Error: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            }
        }

        ValidateQueryPathOptionValues(userPathPatterns, excludePaths, AddParseError);
        if (guardFilters.Count > DbReader.MaxSearchGuardFilters)
            AddParseError($"Error: search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {guardFilters.Count}.");
        var duplicateNamedQuery = namedSearchQueries
            .GroupBy(query => query.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNamedQuery != null)
            AddParseError($"Error: duplicate --named-query name '{ConsoleUi.FormatBoundedValue(duplicateNamedQuery.Key)}'. Use unique names so grouped results are unambiguous.");
        if (duplicateConfidenceExplicit && duplicateThresholdExplicit)
            AddParseError("Error: --duplicate-confidence and --duplicate-threshold cannot be combined; use the preset or the explicit score threshold.");
        if (parseErrors == null
            && applySearchSourceDefaults
            && auditScopeExplicit
            && recipeName == null
            && !listRecipes
            && string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase))
        {
            if (pathPatterns.Count == 0)
                AddDistinct(pathPatterns, SearchAuditRecipes.DefaultSourcePathPatterns);
            AddDistinct(excludePaths, SearchAuditRecipes.DefaultSourceExcludePaths);
            AddSourceOnlyDefaultExcludeOrigin(excludeOrigins, matchOrigins, SearchMatchClassifier.Comment);
            AddSourceOnlyDefaultExcludeOrigin(excludeOrigins, matchOrigins, SearchMatchClassifier.HelpText);
            excludeTests = true;
        }

        if (validateDefaultLimit && !limitExplicit && defaultLimitError != null)
            AddParseError(defaultLimitError);
        if (validateDefaultSnippetLines && !snippetLinesExplicit && defaultSnippetLinesError != null)
            AddParseError(defaultSnippetLinesError);
        if (validateDefaultMaxLineWidth && !maxLineWidthExplicit && defaultMaxLineWidthError != null)
            AddParseError(defaultMaxLineWidthError);

        if (readOnly)
        {
            var canAppendReadOnlyFlags = !SqliteFileUri.StartsWithFileScheme(resolvedDbPath) ||
                SqliteFileUri.TryValidateBounds(resolvedDbPath, out _);
            if (canAppendReadOnlyFlags)
                resolvedDbPath = DbContext.ToReadOnlyUri(resolvedDbPath);
        }

        return new QueryCommandOptions
        {
            DbPath = resolvedDbPath,
            DbPathExplicit = dbPathExplicit,
            ReadOnly = readOnly,
            DryRun = dryRun,
            DataDir = dbResolution.DataDir,
            DataDirSource = dbResolution.DataDirSource,
            Json = json ?? jsonDefault,
            JsonOutputFormat = jsonOutputFormat,
            JsonOutputFormatExplicit = jsonOutputFormatExplicit,
            OutputFormat = outputFormat,
            Limit = limit,
            TotalLimit = totalLimit,
            LimitExplicit = limitExplicit,
            Lang = lang,
            Kind = kind,
            UnusedBucket = unusedBucket,
            MinUnusedConfidence = minUnusedConfidence,
            UnusedActionable = unusedActionable,
            Severity = severity,
            Query = query,
            RawFts = rawFts,
            IncludeBody = includeBody,
            BodyStartLine = bodyStartLine,
            BodyLines = bodyLines,
            StartLine = startLine,
            EndLine = endLine,
            ContextBefore = contextBefore,
            ContextAfter = contextAfter,
            ContextAfterExplicit = contextAfterExplicit,
            ImpactDeprecatedDepthUsed = impactDeprecatedDepthUsed,
            FocusLine = focusLine,
            FocusColumn = focusColumn,
            FocusLength = focusLength,
            SnippetLines = snippetLines,
            SnippetFocus = snippetFocus,
            MaxLineWidth = maxLineWidth,
            PathPatterns = pathPatterns,
            WorkspaceDbPaths = workspaceDbPaths,
            ProjectFilters = projectFilters,
            ProjectFilterRoot = projectFilterRootResolution?.Root,
            ProjectFilterRootFallbackReason = projectFilterRootResolution?.FallbackReason,
            SolutionFilter = solutionFilter,
            ExcludePaths = excludePaths,
            VisibilityFilters = visibilityFilters,
            ExcludeVisibilityFilters = excludeVisibilityFilters,
            ExcludeTests = excludeTests,
            IncludeGenerated = includeGenerated,
            CountOnly = countOnly,
            All = all,
            StrictNotFound = strictNotFound,
            Strict = strict,
            Since = since,
            NoDedup = noDedup,
            NoVisibilityRank = noVisibilityRank,
            Exact = exact,
            Regex = regex,
            Prefix = prefix,
            GuardFilters = guardFilters,
            GuardWindow = guardWindow,
            GuardScope = guardScope,
            ExcludeComments = excludeComments,
            ExcludeStrings = excludeStrings,
            ExcludeFixtures = excludeFixtures,
            ExactName = exactName,
            ExactSubstring = exactSubstring,
            CheckWorkspace = checkWorkspace,
            StaleAfter = staleAfter,
            StatusCheckScopes = statusCheckScopes,
            WithPaths = withPaths,
            GroupBy = groupBy,
            UniqueBy = uniqueBy,
            CountBy = countBy,
            MatchOrigins = matchOrigins,
            ExcludeOrigins = excludeOrigins,
            ResultKinds = resultKinds,
            SearchFields = searchFields,
            OutlineFields = outlineFields,
            OutlineFieldsExplicit = outlineFieldsExplicit,
            FirstPerFile = firstPerFile,
            ResultsOnly = resultsOnly,
            NextSteps = nextSteps,
            GroupedPerFileLimit = groupedPerFileLimit,
            SampleSize = sampleSize,
            MaxJsonBytes = maxJsonBytes,
            RawBytes = rawBytes,
            RawKinds = rawKinds,
            Verbose = verbose,
            Profile = profile,
            SlowQueryMs = slowQueryMs,
            Compact = compact,
            InspectFields = inspectFields,
            MinEntrypointConfidence = minEntrypointConfidence,
            StatusExplainField = statusExplainField,
            StatusLogPath = statusLogPath,
            StatusConfig = statusConfig,
            RankMode = rankMode,
            SymbolSortMode = symbolSortMode,
            SortValue = sortValue,
            SortExplicit = sortExplicit,
            ExtraNames = extraNames,
            MapSections = mapSections,
            SummaryOnly = summaryOnly,
            MapSummaryOnly = mapSummaryOnly,
            DependencyCycles = dependencyCycles,
            DependencySuppressNoise = dependencySuppressNoise,
            DependencySymbols = dependencySymbols,
            DependencySymbolFamilies = dependencySymbolFamilies,
            RecipeName = recipeName,
            IncludeRecipeQueries = includeRecipeQueries,
            ExcludeRecipeQueries = excludeRecipeQueries,
            ShowExcluded = showExcluded,
            ListRecipes = listRecipes,
            NamesOnly = namesOnly,
            OpenIssuesPath = openIssuesPath,
            AuditScope = auditScope,
            AuditScopeExplicit = auditScopeExplicit,
            OpenIssuesRepository = openIssuesRepository,
            DuplicateConfidence = duplicateThresholdExplicit ? IssueDuplicatePreflight.CustomDuplicateConfidence : duplicateConfidence,
            DuplicateThreshold = duplicateThreshold,
            DuplicatePreflightTuningExplicit = duplicateConfidenceExplicit || duplicateThresholdExplicit,
            IssueTitle = issueTitle,
            IssueLabels = issueLabels,
            SearchCursor = searchCursor,
            UnusedCursorOffset = unusedCursorOffset,
            OutlineCursorOffset = outlineCursorOffset,
            NamedSearchQueries = namedSearchQueries,
            LanguagesIndexedOnly = languagesIndexedOnly,
            LanguageCapabilities = languageCapabilities,
            LanguageLookups = languageLookups,
            LanguageExtensionLookups = languageExtensionLookups,
            LanguageAliasLookups = languageAliasLookups,
            SourceOnly = sourceOnly,
            NoSemanticTokens = noSemanticTokens,
            ParseError = parseErrors == null ? null : string.Join(Environment.NewLine, parseErrors),
        };
    }

    private static bool TryParseNamedSearchQuery(string value, out SearchNamedQuery namedQuery, out string? error)
    {
        namedQuery = new SearchNamedQuery(string.Empty, string.Empty);
        error = null;
        var separator = value.IndexOf('=');
        if (separator <= 0)
        {
            error = "Error: --named-query must use <name>=<query>.";
            return false;
        }

        var name = value[..separator].Trim();
        var query = value[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Error: --named-query name cannot be empty.";
            return false;
        }
        if (name.Length > MaxNamedSearchQueryNameLength)
        {
            error = $"Error: --named-query name '{ConsoleUi.FormatBoundedValue(name)}' exceeds the {MaxNamedSearchQueryNameLength} character limit.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            error = $"Error: --named-query '{ConsoleUi.FormatBoundedValue(name)}' query cannot be empty.";
            return false;
        }
        if (query.Length > QueryLimits.MaxQueryLength)
        {
            error = QueryLimits.FormatQueryTooLongError();
            return false;
        }

        namedQuery = new SearchNamedQuery(name, query);
        return true;
    }

    internal static ProjectFilterRootResolution ResolveProjectFilterRoot(string dbPath, bool dbPathExplicit)
    {
        var effectiveDbPath = s_batchReader != null && !string.IsNullOrWhiteSpace(s_batchDbPath)
            ? s_batchDbPath!
            : dbPath;
        var effectiveDbPathExplicit = s_batchReader != null && !string.IsNullOrWhiteSpace(s_batchDbPath)
            ? s_batchDbPathExplicit
            : dbPathExplicit;
        var projectRoot = DbPathResolver.ResolveProjectRootForQuery(effectiveDbPath, effectiveDbPathExplicit);
        if (!string.IsNullOrWhiteSpace(projectRoot))
            return new ProjectFilterRootResolution(Path.GetFullPath(projectRoot), null);

        return new ProjectFilterRootResolution(
            Path.GetFullPath(Environment.CurrentDirectory),
            ProjectFilterRootFallbackReasonCurrentDirectory);
    }

    private static List<string> ParseMapSections(string rawValue, Action<string> addParseError)
    {
        var sections = new List<string>();
        if (!ValidateCsvBounds("--sections", rawValue, MaxMapSectionsCsvLength, MaxMapSectionsCsvEntries, addParseError))
            return sections;

        foreach (var rawSection in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var section = rawSection.ToLowerInvariant();
            switch (section)
            {
                case "tree":
                case "modules":
                    sections.Add("tree");
                    break;
                case "languages":
                case "hotspots":
                case "metrics":
                    sections.Add(section);
                    break;
                default:
                    addParseError($"Error: --sections contains unsupported section '{ConsoleUi.FormatBoundedValue(rawSection)}'. Use one or more of tree, languages, hotspots, metrics.");
                    break;
            }
        }

        if (sections.Count == 0)
            addParseError("Error: --sections cannot be empty. Use one or more of tree, languages, hotspots, metrics.");
        return sections.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string>? ParseInspectFields(string rawValue, Action<string> addParseError, out bool includeBody)
    {
        includeBody = false;
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = false;

        if (!ValidateCsvBounds("--fields", rawValue, MaxInspectFieldsCsvLength, MaxInspectFieldsCsvEntries, addParseError))
            return fields;

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = rawField.ToLowerInvariant().Replace('-', '_');
            string canonical;
            switch (field)
            {
                case "all":
                    all = true;
                    continue;
                case "file":
                    canonical = "file";
                    break;
                case "metadata":
                case "workspace":
                    canonical = "workspace";
                    break;
                case "graph":
                case "trust":
                    canonical = "graph";
                    break;
                case "definition":
                case "definitions":
                case "defs":
                    canonical = "definitions";
                    break;
                case "body":
                    canonical = "definitions";
                    includeBody = true;
                    break;
                case "source":
                case "source_excerpt":
                case "excerpt":
                    canonical = "source_excerpt";
                    break;
                case "nearby":
                case "nearby_symbols":
                case "nearbysymbols":
                    canonical = "nearby_symbols";
                    break;
                case "reference":
                case "references":
                case "refs":
                    canonical = "references";
                    break;
                case "caller":
                case "callers":
                    canonical = "callers";
                    break;
                case "callee":
                case "callees":
                    canonical = "callees";
                    break;
                default:
                    addParseError($"Error: unsupported --fields value '{ConsoleUi.FormatBoundedValue(rawField)}'. Use one or more of all, file, workspace, graph, definitions, body, source_excerpt, nearby_symbols, references, callers, callees.");
                    continue;
            }

            if (seen.Add(canonical))
                fields.Add(canonical);
        }

        if (all && fields.Count > 0)
            addParseError("Error: --fields all cannot be combined with specific field names.");
        if (!all && fields.Count == 0)
            addParseError("Error: --fields requires at least one field name.");

        return all ? null : fields;
    }

    private static List<string>? ParseSearchProjectionFields(string rawValue, Action<string> addParseError)
    {
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!ValidateCsvBounds("--search-fields", rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return fields;

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = rawField.ToLowerInvariant().Replace('-', '_');
            string canonical;
            switch (field)
            {
                case "path":
                case "file":
                    canonical = "path";
                    break;
                case "line":
                case "start_line":
                    canonical = "line";
                    break;
                case "end_line":
                    canonical = "end_line";
                    break;
                case "lang":
                case "language":
                    canonical = "lang";
                    break;
                case "column":
                case "col":
                    canonical = "column";
                    break;
                case "symbol":
                case "symbol_name":
                    canonical = "symbol";
                    break;
                case "symbol_kind":
                    canonical = "symbol_kind";
                    break;
                case "origin":
                case "origins":
                case "match_origin":
                case "match_origins":
                    canonical = "origin";
                    break;
                case "kind":
                case "result_kind":
                case "result_kinds":
                    canonical = "kind";
                    break;
                case "score":
                    canonical = "score";
                    break;
                case "snippet":
                    canonical = "snippet";
                    break;
                case "query":
                case "query_name":
                    canonical = "query_name";
                    break;
                case "recipe":
                case "recipe_name":
                    canonical = "recipe";
                    break;
                default:
                    addParseError($"Error: unsupported --search-fields value '{ConsoleUi.FormatBoundedValue(rawField)}'. Use one or more of path,line,end_line,lang,column,symbol,symbol_kind,origin,kind,score,snippet,query_name,recipe.");
                    continue;
            }

            if (seen.Add(canonical))
                fields.Add(canonical);
        }

        if (fields.Count == 0)
            addParseError("Error: --search-fields requires at least one field name.");
        return fields;
    }

    private static List<string>? ParseOutlineProjectionFields(string rawValue, Action<string> addParseError)
    {
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = false;
        if (!ValidateCsvBounds("--outline-fields", rawValue, MaxOutlineProjectionFieldsCsvLength, MaxOutlineProjectionFieldsCsvEntries, addParseError))
            return fields;

        void AddField(string field)
        {
            if (seen.Add(field))
                fields.Add(field);
        }

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var field = rawField.ToLowerInvariant().Replace('-', '_');
            switch (field)
            {
                case "all":
                    all = true;
                    continue;
                case "kind":
                case "name":
                case "display_name":
                case "path":
                case "line":
                case "start_line":
                case "end_line":
                case "depth":
                case "body_start_line":
                case "body_end_line":
                case "signature":
                case "signature_truncated":
                case "signature_original_length":
                case "container_kind":
                case "container_name":
                case "visibility":
                case "return_type":
                case "sort_mode":
                case "reference_count":
                case "size_lines":
                case "complexity_score":
                    AddField(field);
                    break;
                case "refs":
                case "references":
                    AddField("reference_count");
                    break;
                case "size":
                case "span":
                    AddField("size_lines");
                    break;
                case "complexity":
                    AddField("complexity_score");
                    break;
                case "range":
                case "lines":
                    AddField("start_line");
                    AddField("end_line");
                    break;
                case "body":
                case "body_range":
                    AddField("body_start_line");
                    AddField("body_end_line");
                    break;
                case "container":
                    AddField("container_kind");
                    AddField("container_name");
                    break;
                default:
                    addParseError($"Error: unsupported --outline-fields value '{ConsoleUi.FormatBoundedValue(rawField)}'. Use one or more of all, kind, name, display_name, path, line, start_line, end_line, depth, body_start_line, body_end_line, signature, signature_truncated, signature_original_length, container_kind, container_name, visibility, return_type, sort_mode, reference_count, size_lines, complexity_score, or aliases range, lines, body, body_range, container, refs, size, span, complexity.");
                    continue;
            }
        }

        if (all && fields.Count > 0)
            addParseError("Error: --outline-fields all cannot be combined with specific field names.");
        if (!all && fields.Count == 0)
            addParseError("Error: --outline-fields requires at least one field name.");
        return all ? null : fields;
    }

    private static void AddSearchMatchOrigins(string optionName, string rawValue, List<string> origins, Action<string> addParseError)
    {
        if (!ValidateCsvBounds(optionName, rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return;
        foreach (var rawOrigin in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalizeSearchMatchOrigin(rawOrigin, out var origin))
            {
                addParseError($"Error: unsupported {optionName} value '{ConsoleUi.FormatBoundedValue(rawOrigin)}'. Use code, comment, string_literal, regex_literal, help_text, or unknown.");
                continue;
            }
            if (!origins.Contains(origin, StringComparer.Ordinal))
                origins.Add(origin);
        }
    }

    private static void AddSourceOnlyDefaultExcludeOrigin(List<string> excludeOrigins, IReadOnlyList<string> matchOrigins, string origin)
    {
        if (matchOrigins.Contains(origin, StringComparer.Ordinal))
            return;
        if (!excludeOrigins.Contains(origin, StringComparer.Ordinal))
            excludeOrigins.Add(origin);
    }

    private static bool TryNormalizeSearchMatchOrigin(string rawOrigin, out string origin)
    {
        switch (rawOrigin.ToLowerInvariant().Replace("-", "_"))
        {
            case SearchMatchClassifier.Code:
                origin = SearchMatchClassifier.Code;
                return true;
            case SearchMatchClassifier.Comment:
                origin = SearchMatchClassifier.Comment;
                return true;
            case "string":
            case SearchMatchClassifier.StringLiteral:
                origin = SearchMatchClassifier.StringLiteral;
                return true;
            case "regex":
            case SearchMatchClassifier.RegexLiteral:
                origin = SearchMatchClassifier.RegexLiteral;
                return true;
            case "help":
            case SearchMatchClassifier.HelpText:
                origin = SearchMatchClassifier.HelpText;
                return true;
            case SearchMatchClassifier.Unknown:
                origin = SearchMatchClassifier.Unknown;
                return true;
            default:
                origin = string.Empty;
                return false;
        }
    }

    private static void AddSearchResultKinds(string rawValue, List<string> resultKinds, Action<string> addParseError)
    {
        if (!ValidateCsvBounds("--result-kind", rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return;
        foreach (var rawKind in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryNormalizeSearchResultKind(rawKind, out var kind))
            {
                addParseError($"Error: unsupported --result-kind value '{ConsoleUi.FormatBoundedValue(rawKind)}'. Use call_site, declaration, identifier, code, comment, string_literal, regex_literal, help_text, or unknown.");
                continue;
            }
            if (!resultKinds.Contains(kind, StringComparer.Ordinal))
                resultKinds.Add(kind);
        }
    }

    private static bool TryNormalizeSearchResultKind(string rawKind, out string kind)
    {
        switch (rawKind.ToLowerInvariant().Replace("-", "_"))
        {
            case "call":
            case "callsite":
            case "call_site":
                kind = "call_site";
                return true;
            case "decl":
            case "declaration":
                kind = "declaration";
                return true;
            case "identifier":
            case "ident":
                kind = "identifier";
                return true;
            case SearchMatchClassifier.Code:
                kind = SearchMatchClassifier.Code;
                return true;
            case SearchMatchClassifier.Comment:
                kind = SearchMatchClassifier.Comment;
                return true;
            case "string":
            case SearchMatchClassifier.StringLiteral:
                kind = SearchMatchClassifier.StringLiteral;
                return true;
            case "regex":
            case SearchMatchClassifier.RegexLiteral:
                kind = SearchMatchClassifier.RegexLiteral;
                return true;
            case "help":
            case SearchMatchClassifier.HelpText:
                kind = SearchMatchClassifier.HelpText;
                return true;
            case SearchMatchClassifier.Unknown:
                kind = SearchMatchClassifier.Unknown;
                return true;
            default:
                kind = string.Empty;
                return false;
        }
    }

    private static bool ValidateCsvBounds(
        string optionName,
        string rawValue,
        int maxLength,
        int maxEntries,
        Action<string> addParseError)
    {
        if (rawValue.Length > maxLength)
        {
            addParseError($"Error: {optionName} value is too long ({rawValue.Length} characters; max {maxLength}).");
            return false;
        }

        var entries = CountCsvEntries(rawValue);
        if (entries > maxEntries)
        {
            addParseError($"Error: {optionName} accepts at most {maxEntries} comma-separated entries.");
            return false;
        }

        return true;
    }

    private static int CountCsvEntries(string rawValue)
    {
        if (rawValue.Length == 0)
            return 0;

        var count = 1;
        foreach (var ch in rawValue)
        {
            if (ch == ',')
                count++;
        }

        return count;
    }

    private static void ValidateQueryPathOptionValues(
        IReadOnlyList<string> pathPatterns,
        IReadOnlyList<string> excludePaths,
        Action<string> addParseError)
    {
        ValidatePathOptionValues("--path", pathPatterns, addParseError);
        ValidatePathOptionValues("--exclude-path", excludePaths, addParseError);
    }

    private static void ValidatePathOptionValues(
        string optionName,
        IReadOnlyList<string> patterns,
        Action<string> addParseError)
    {
        if (patterns.Count > MaxQueryPathFilterCount)
            addParseError($"Error: {optionName} accepts at most {MaxQueryPathFilterCount} values.");

        foreach (var pattern in patterns)
        {
            if (pattern.Length > MaxQueryPathFilterLength)
            {
                addParseError($"Error: {optionName} value is too long ({pattern.Length} characters; max {MaxQueryPathFilterLength}).");
                continue;
            }

            ValidatePathGlobPattern(optionName, pattern, addParseError);
        }
    }

    private static bool TryParseJsonOutputFormat(string rawValue, out string format)
    {
        if (string.Equals(rawValue, JsonOutputFormatArray, StringComparison.OrdinalIgnoreCase))
        {
            format = JsonOutputFormatArray;
            return true;
        }
        if (string.Equals(rawValue, JsonOutputFormatNdjson, StringComparison.OrdinalIgnoreCase))
        {
            format = JsonOutputFormatNdjson;
            return true;
        }

        format = JsonOutputFormatNdjson;
        return false;
    }

    private static bool TryParseOutputFormat(string rawValue, out string format)
    {
        switch (rawValue.ToLowerInvariant())
        {
            case OutputFormatText:
            case OutputFormatJson:
            case OutputFormatCount:
            case OutputFormatCompact:
            case OutputFormatGrouped:
            case OutputFormatCsv:
            case OutputFormatTsv:
            case OutputFormatLsp:
            case OutputFormatQf:
            case OutputFormatSarif:
                format = rawValue.ToLowerInvariant();
                return true;
            default:
                format = OutputFormatText;
                return false;
        }
    }

    private static void ValidatePathGlobPattern(string optionName, string pattern, Action<string> addParseError)
    {
        if (TryFindUnsupportedBracketGlob(pattern, out var reason))
        {
            addParseError($"Error: {optionName} '{ConsoleUi.FormatBoundedValue(pattern)}' is not a valid glob: {reason}. Hint: escape '[' or ']' with a backslash when matching literal path characters, or use only '*' and '?' wildcards.");
        }
    }

    private static bool TryFindUnsupportedBracketGlob(string pattern, out string reason)
    {
        var escaped = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '[')
            {
                reason = "character classes are not supported";
                return true;
            }

            if (ch == ']')
            {
                reason = "unmatched ']'";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static bool TryParseReferenceRankMode(string value, out ReferenceRankMode rankMode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "weighted":
                rankMode = ReferenceRankMode.Weighted;
                return true;
            case "count":
                rankMode = ReferenceRankMode.Count;
                return true;
            case "kind":
                rankMode = ReferenceRankMode.Kind;
                return true;
            default:
                rankMode = ReferenceRankMode.Weighted;
                return false;
        }
    }

    internal static bool TryParseSymbolSortMode(string value, out SymbolSortMode sortMode)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "name":
                sortMode = SymbolSortMode.Name;
                return true;
            case "hotspot":
                sortMode = SymbolSortMode.Hotspot;
                return true;
            case "references":
            case "reference":
            case "refs":
                sortMode = SymbolSortMode.References;
                return true;
            case "size":
                sortMode = SymbolSortMode.Size;
                return true;
            case "complexity":
                sortMode = SymbolSortMode.Complexity;
                return true;
            case "path":
                sortMode = SymbolSortMode.Path;
                return true;
            default:
                sortMode = SymbolSortMode.Name;
                return false;
        }
    }

    private static bool TryParseConfidence(string value, out double confidence)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out confidence) &&
            !double.IsNaN(confidence) &&
            !double.IsInfinity(confidence) &&
            confidence >= 0 &&
            confidence <= 1)
        {
            return true;
        }

        confidence = 0;
        return false;
    }

    internal static bool TryResolveHotspotsGroupBy(string? requestedGroupBy, string? lang, bool groupByName, out string groupBy, out string error)
    {
        groupBy = string.Empty;
        error = string.Empty;

        if (groupByName && requestedGroupBy != null)
        {
            error = "Error: --group-by-name cannot be combined with --group-by.";
            return false;
        }

        if (groupByName)
        {
            groupBy = HotspotsGroupedByNameKind;
            return true;
        }

        if (requestedGroupBy == null)
        {
            groupBy = IsSqlLanguageFilter(lang) ? HotspotsGroupedByStatement : HotspotsGroupedBySymbol;
            return true;
        }

        switch (requestedGroupBy)
        {
            case HotspotsGroupedBySymbol:
            case HotspotsGroupedByFile:
                groupBy = requestedGroupBy;
                return true;
            case HotspotsGroupedByStatement:
                if (IsSqlLanguageFilter(lang))
                {
                    groupBy = requestedGroupBy;
                    return true;
                }

                error = "Error: hotspots --group-by statement is only supported with --lang sql. Use --group-by symbol or --group-by file for non-SQL hotspot grouping.";
                return false;
            case "name":
            case HotspotsGroupedByNameKind:
                groupBy = HotspotsGroupedByNameKind;
                return true;
            default:
                error = $"Error: unsupported hotspots --group-by value '{ConsoleUi.FormatBoundedValue(requestedGroupBy)}'. Use symbol, file, or --lang sql --group-by statement.";
                return false;
        }
    }

    private static bool IsSqlLanguageFilter(string? lang) =>
        string.Equals(lang, "sql", StringComparison.Ordinal);

    internal static string? NormalizeLangFilterValue(string? langValue)
    {
        return DbReader.NormalizeQueryLanguage(langValue);
    }

    internal static IReadOnlyList<string> GetLanguageAliases(string lang)
        => LanguageDisplayAliases.TryGetValue(lang, out var aliases) ? aliases : [];

    internal static bool TryParseSnippetFocusMode(string value, out SearchSnippetFocusMode mode)
    {
        mode = value.Trim().ToLowerInvariant() switch
        {
            "leftmost" => SearchSnippetFocusMode.Leftmost,
            "quality" => SearchSnippetFocusMode.Quality,
            "proximity" => SearchSnippetFocusMode.Proximity,
            _ => default,
        };
        return value.Trim().Equals("leftmost", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("quality", StringComparison.OrdinalIgnoreCase)
            || value.Trim().Equals("proximity", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyCollection<string> GetCompletionLanguageAliases()
        => LanguageDisplayAliases.Values.SelectMany(aliases => aliases).ToArray();

    internal static bool TryParseStaleAfter(string value, out TimeSpan staleAfter, out string? error)
    {
        staleAfter = default;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Error: --stale-after requires a duration like 30m, 2h, or 7d.";
            return false;
        }

        var trimmed = value.Trim();
        var suffix = trimmed[^1];
        var numberText = trimmed[..^1];
        TimeSpan unit;
        switch (suffix)
        {
            case 'm':
            case 'M':
                unit = TimeSpan.FromMinutes(1);
                break;
            case 'h':
            case 'H':
                unit = TimeSpan.FromHours(1);
                break;
            case 'd':
            case 'D':
                unit = TimeSpan.FromDays(1);
                break;
            default:
                error = $"Error: could not parse stale-after value '{ConsoleUi.FormatBoundedValue(value)}'. Use a positive duration with m, h, or d suffix (e.g. 30m, 2h, 7d).";
                return false;
        }

        if (!double.TryParse(numberText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) ||
            number <= 0)
        {
            error = $"Error: could not parse stale-after value '{ConsoleUi.FormatBoundedValue(value)}'. Use a positive duration with m, h, or d suffix (e.g. 30m, 2h, 7d).";
            return false;
        }

        var ticks = number * unit.Ticks;
        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            error = $"Error: stale-after value '{ConsoleUi.FormatBoundedValue(value)}' is too large.";
            return false;
        }

        if (ticks > MaxStaleAfter.Ticks)
        {
            error = $"Error: stale-after value '{ConsoleUi.FormatBoundedValue(value)}' exceeds the maximum {MaxStaleAfterDisplay}.";
            return false;
        }

        staleAfter = TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
        return true;
    }

    private static (TimeSpan Value, string? Error) ResolveStaleAfter(QueryCommandOptions options, string? envValue)
    {
        if (options.StaleAfter.HasValue)
            return (options.StaleAfter.Value, null);

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (TryParseStaleAfter(envValue, out var parsed, out var error))
                return (parsed, null);
            return (DefaultStaleAfter, error!.Replace("--stale-after", StaleAfterEnvironmentVariable, StringComparison.Ordinal));
        }

        return (DefaultStaleAfter, null);
    }

    private static bool TryResolveSearchExactMode(QueryCommandOptions options, out bool exact, out string? error)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            return false;
        }
        if (options.ExactName)
        {
            exact = false;
            error = "Error: --exact-name applies to name-based commands (symbols/definition/references/callers/callees/inspect), not search. Use --exact-substring for search, or keep --exact for backward compatibility.";
            return false;
        }

        exact = options.Exact || options.ExactSubstring;
        error = null;
        return true;
    }

    private static bool TryResolveNameExactMode(QueryCommandOptions options, string commandName, out bool exact, out string? error)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            return false;
        }
        if (options.ExactSubstring)
        {
            exact = false;
            error = $"Error: --exact-substring only applies to search. Use --exact-name for {commandName}, or keep --exact for backward compatibility.";
            return false;
        }

        exact = options.Exact || options.ExactName;
        error = null;
        return true;
    }

    private static bool TryRejectMultipleExactFlags(QueryCommandOptions options, out string? error)
    {
        var count = (options.Exact ? 1 : 0) + (options.ExactSubstring ? 1 : 0) + (options.ExactName ? 1 : 0);
        if (count > 1)
        {
            error = "Error: pass only one of --exact, --exact-substring, --exact-name.";
            return false;
        }

        error = null;
        return true;
    }
}
