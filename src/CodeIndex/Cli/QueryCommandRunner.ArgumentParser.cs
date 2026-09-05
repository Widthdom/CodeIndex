using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private readonly bool jsonDefault;
        private readonly bool allowNamedQuery;
        private readonly bool allowStatusCheck;
        private readonly bool allowIssueDraftsFormat;
        private readonly bool validateDefaultLimit;
        private readonly bool validateDefaultSnippetLines;
        private readonly bool validateDefaultMaxLineWidth;
        private readonly bool applySearchSourceDefaults;
        private readonly bool allowOutlineSort;
        private readonly bool positionalGlobAsPath;
        private string? dbPath;
        private string? dataDir;
        private bool? json;
        private bool jsonExplicit;
        private string jsonOutputFormat = JsonOutputFormatNdjson;
        private bool jsonOutputFormatExplicit;
        private int limit;
        private readonly string? defaultLimitError;
        private int? totalLimit;
        private string? lang;
        private string? rawLang;
        private bool allowUnknownLang;
        private bool languageValidationError;
        private string? kind;
        private string? unusedBucket;
        private string? minUnusedConfidence;
        private string? severity;
        private string? query;
        private string? selector;
        private bool rawFts;
        private bool includeBody;
        private int? bodyStartLine;
        private int? bodyLines;
        private bool countOnly;
        private bool countFlagRequested;
        private bool groupPartials;
        private bool all;
        private bool strictNotFound;
        private bool allowPartial;
        private int? startLine;
        private int? endLine;
        private int? startColumn;
        private int? endColumn;
        private bool endLineExplicit;
        private int contextBefore;
        private int contextAfter;
        private int? symmetricContext;
        private int? explicitContextBefore;
        private int? explicitContextAfter;
        private int? focusLine;
        private int? focusColumn;
        private int focusLength = 1;
        private int snippetLines;
        private readonly string? defaultSnippetLinesError;
        private SearchSnippetFocusMode snippetFocus = SearchSnippetFocusMode.Quality;
        private int maxLineWidth;
        private readonly string? defaultMaxLineWidthError;
        private bool contextAfterExplicit;
        private List<string> pathPatterns = [];
        private List<string> userPathPatterns = [];
        private List<string> workspaceDbPaths = [];
        private List<string> projectFilters = [];
        private string? solutionFilter;
        private List<string> excludePaths = [];
        private List<string> visibilityFilters = [];
        private List<string> excludeVisibilityFilters = [];
        private bool excludeTests;
        private bool unusedActionable;
        private bool includeGenerated;
        private DateTime? since;
        private bool noDedup;
        private bool noVisibilityRank;
        private bool exact;
        private bool regex;
        private bool prefix;
        private List<SearchGuardFilter> guardFilters = [];
        private int guardWindow = DbReader.DefaultSearchGuardWindow;
        private SearchGuardScope guardScope = SearchGuardScope.Window;
        private bool excludeComments;
        private bool excludeStrings;
        private bool excludeFixtures;
        private List<string>? parseErrors;
        private bool exactName;
        private bool exactSubstring;
        private bool tokenBoundary;
        private bool dbPathExplicit;
        private bool readOnly;
        private bool dryRun;
        private bool showPaths;
        private bool checkWorkspace;
        private bool statusCheckExplicit;
        private TimeSpan? staleAfter;
        private HashSet<string>? statusCheckScopes;
        private bool withPaths;
        private string? groupBy;
        private string? uniqueBy;
        private string? countBy;
        private List<string> matchOrigins = [];
        private List<string> excludeOrigins = [];
        private List<string> resultKinds = [];
        private List<string>? searchFields;
        private List<string>? outlineFields;
        private bool outlineFieldsExplicit;
        private bool firstPerFile;
        private bool resultsOnly;
        private bool nextSteps;
        private int groupedPerFileLimit = DefaultSearchGroupedPerFileLimit;
        private bool groupedPerFileLimitExplicit;
        private int? sampleSize;
        private int? requestedMaxJsonBytes;
        private int? maxJsonBytes;
        private bool rawBytes;
        private bool rawKinds;
        private bool includeQualifiedCommonCalls;
        private bool includeMemberReads;
        private bool verbose;
        private bool profile;
        private int? slowQueryMs;
        private bool compact;
        private List<string>? inspectFields;
        private bool inspectFieldsIncludeBody;
        private ProjectionFieldValidationError? inspectFieldValidationError;
        private double minEntrypointConfidence;
        private string? statusExplainField;
        private bool statusLogPath;
        private string outputFormat = OutputFormatText;
        private bool countOutputFormatExplicit;
        private bool outputFormatExplicit;
        private bool outputFormatImpliesStructuredOutput;
        private bool statusConfig;
        private bool? redactPaths;
        private bool limitExplicit;
        private bool snippetLinesExplicit;
        private bool maxLineWidthExplicit;
        private bool strict;
        private ReferenceRankMode rankMode = ReferenceRankMode.Weighted;
        private SymbolSortMode symbolSortMode = SymbolSortMode.Name;
        private string? sortValue;
        private bool sortExplicit;
        private List<string> extraNames = [];
        private bool impactDeprecatedDepthUsed;
        private List<string>? mapSections;
        private bool summaryOnly;
        private bool progress;
        private bool mapSummaryOnly;
        private bool dependencyCycles;
        private int dependencyCycleGraphBudget = DefaultDependencyCycleGraphBudget;
        private bool includeAllDependencyCycleNodes;
        private bool dependencySuppressNoise;
        private List<string> dependencySymbols = [];
        private List<string> dependencySymbolFamilies = [];
        private bool dependencySymbolFilterCountExceeded;
        private string? recipeName;
        private List<string> includeRecipeQueries = [];
        private List<string> excludeRecipeQueries = [];
        private bool showExcluded;
        private bool listRecipes;
        private bool namesOnly;
        private string? openIssuesPath;
        private string auditScope = SearchAuditRecipes.DefaultAuditScope;
        private bool auditScopeExplicit;
        private string? openIssuesRepository;
        private string issueState = IssueDuplicatePreflight.DefaultIssueState;
        private string duplicateConfidence = IssueDuplicatePreflight.DefaultDuplicateConfidence;
        private double duplicateThreshold = IssueDuplicatePreflight.DefaultDuplicateThreshold;
        private bool duplicateConfidenceExplicit;
        private bool duplicateThresholdExplicit;
        private string? issueTitle;
        private List<string> issueLabels = [];
        private SearchCursor? searchCursor;
        private int? unusedCursorOffset;
        private int? outlineCursorOffset;
        private string? rawCursorValue;
        private DependencyCycleCursor? dependencyCycleCursor;
        private List<SearchNamedQuery> namedSearchQueries = [];
        private bool languagesIndexedOnly;
        private List<string> languageCapabilities = [];
        private List<string> languageLookups = [];
        private List<string> languageExtensionLookups = [];
        private List<string> languageAliasLookups = [];
        private bool sourceOnly;
        private bool noSemanticTokens;
        private ProjectFilterRootResolution? projectFilterRootResolution;
        private readonly HashSet<string> seenSingleValueOptions = new(StringComparer.Ordinal);

        internal QueryArgumentParser(
            bool jsonDefault,
            bool allowNamedQuery,
            bool allowStatusCheck,
            bool allowIssueDraftsFormat,
            bool validateDefaultLimit,
            bool validateDefaultSnippetLines,
            bool validateDefaultMaxLineWidth,
            bool applySearchSourceDefaults,
            bool allowOutlineSort,
            bool positionalGlobAsPath)
        {
            this.jsonDefault = jsonDefault;
            this.allowNamedQuery = allowNamedQuery;
            this.allowStatusCheck = allowStatusCheck;
            this.allowIssueDraftsFormat = allowIssueDraftsFormat;
            this.validateDefaultLimit = validateDefaultLimit;
            this.validateDefaultSnippetLines = validateDefaultSnippetLines;
            this.validateDefaultMaxLineWidth = validateDefaultMaxLineWidth;
            this.applySearchSourceDefaults = applySearchSourceDefaults;
            this.allowOutlineSort = allowOutlineSort;
            this.positionalGlobAsPath = positionalGlobAsPath;
            limit = ResolveDefaultPositiveInt(DefaultLimitEnvironmentVariable, DefaultQueryLimit, "--limit", out defaultLimitError);
            snippetLines = ResolveDefaultPositiveInt(DefaultSnippetLinesEnvironmentVariable, SearchSnippetFormatter.DefaultSnippetLines, "--snippet-lines", out defaultSnippetLinesError);
            maxLineWidth = ResolveDefaultNonNegativeInt(DefaultMaxLineWidthEnvironmentVariable, LineWidthFormatter.DefaultMaxLineWidth, "--max-line-width", out defaultMaxLineWidthError);
        }

        internal QueryCommandOptions Parse(string[] args)
        {
            ParseRawArguments(args);
            NormalizeOutputMode();

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

            ResolveProjectFilters(resolvedDbPath);
            ResolveLanguageFilter(resolvedDbPath);
            ValidateParsedOptions();
            ApplySearchSourceOptionDefaults();
            ValidateEnvironmentDefaults();

            if (staleAfter.HasValue)
                statusCheckScopes?.Add("workspace");

            if (readOnly)
            {
                var canAppendReadOnlyFlags = !SqliteFileUri.StartsWithFileScheme(resolvedDbPath) ||
                    SqliteFileUri.TryValidateBounds(resolvedDbPath, out _);
                if (canAppendReadOnlyFlags)
                    resolvedDbPath = DbContext.ToReadOnlyUri(resolvedDbPath);
            }

            return BuildOptions(dbResolution, resolvedDbPath, args);
        }

        private void NormalizeOutputMode()
        {
            if (countOutputFormatExplicit)
                outputFormat = OutputFormatCount;
            countOnly = countFlagRequested || outputFormat == OutputFormatCount;
            if (outputFormatImpliesStructuredOutput)
                json = true;

            if (countFlagRequested
                && outputFormatExplicit
                && outputFormat is not OutputFormatText and not OutputFormatJson and not OutputFormatCount)
            {
                AddParseError(
                    $"Error: --count cannot be combined with --format {outputFormat} because count mode supports only text, json, or count output.");
            }
            else if (countOnly && resultsOnly)
            {
                AddParseError(
                    "Error: --results-only cannot be combined with --format count because that format defines its own output schema.");
            }
            else if (countOnly && jsonOutputFormatExplicit)
            {
                AddParseError(
                    $"Error: --json={jsonOutputFormat} cannot be combined with --format count because that format defines its own output schema.");
            }
        }

        private void ResolveProjectFilters(string resolvedDbPath)
        {
            if (parseErrors != null || projectFilters.Count == 0)
                return;

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

        private void ValidateParsedOptions()
        {
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
        }

        private void ResolveLanguageFilter(string resolvedDbPath)
        {
            if (rawLang == null)
            {
                if (allowUnknownLang)
                {
                    languageValidationError = true;
                    AddParseError($"Error [{CommandErrorCodes.UsageError}]: --allow-unknown-lang requires --lang <lang>.");
                }
                return;
            }

            var input = rawLang.Trim();
            var primaryRoot = s_batchDatabaseContext != null && dbPathExplicit
                ? ResolveProjectRootForDbPath(resolvedDbPath, dbPathExplicit).Root
                : ResolveProjectFilterRoot(resolvedDbPath, dbPathExplicit).Root;
            var queryRoots = workspaceDbPaths
                .Select(path => ResolveProjectRootForDbPath(DbPathResolver.NormalizeDbPath(path), dbPathExplicit: true).Root)
                .Prepend(primaryRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var queryRoot in queryRoots)
            {
                foreach (var (alias, registeredCanonical) in DbReader.GetQueryLanguageAliases(queryRoot))
                    aliases.TryAdd(alias, registeredCanonical);
            }
            var lookupKey = DbReader.NormalizeQueryLanguageLookupKey(input);
            if (lookupKey.Length == 0)
            {
                languageValidationError = true;
                AddParseError(
                    $"Error [{CommandErrorCodes.UsageError}]: --lang must contain at least one letter or digit; got '{ConsoleUi.FormatBoundedValue(input)}'.");
                return;
            }
            if (aliases.TryGetValue(lookupKey, out var canonical))
            {
                lang = canonical;
                return;
            }

            if (allowUnknownLang)
            {
                // An unregistered plugin ID is an explicit escape hatch. Keep its spelling
                // intact (apart from surrounding whitespace) so punctuation and case still
                // match the exact value stored in files.lang.
                // 未登録 plugin ID は明示的な escape hatch として扱い、前後の空白以外は
                // 変更しない。句読点と大小文字を files.lang の保存値へ正確に一致させる。
                lang = input;
                return;
            }

            var suggestions = ConsoleUi.FindClosestMatches(lookupKey, aliases.Keys)
                .Select(candidate => aliases[candidate])
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray();
            var suggestionText = suggestions.Length == 0
                ? string.Empty
                : $" Did you mean {string.Join(", ", suggestions.Select(value => $"'{ConsoleUi.FormatBoundedValue(value)}'"))}?";
            languageValidationError = true;
            AddParseError(
                $"Error [{CommandErrorCodes.UsageError}]: unknown language identifier '{ConsoleUi.FormatBoundedValue(input)}'.{suggestionText} " +
                "Use --allow-unknown-lang only for an unregistered plugin language ID.");
        }

        private void ApplySearchSourceOptionDefaults()
        {
            if (parseErrors != null
                || !applySearchSourceDefaults
                || !auditScopeExplicit
                || recipeName != null
                || listRecipes
                || !string.Equals(auditScope, SearchAuditRecipes.DefaultAuditScope, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (pathPatterns.Count == 0)
                AddDistinct(pathPatterns, SourceScopeDefaults.IncludePaths);
            AddDistinct(excludePaths, SourceScopeDefaults.ExcludePaths);
            AddSourceOnlyDefaultExcludeOrigin(excludeOrigins, matchOrigins, SearchMatchClassifier.Comment);
            AddSourceOnlyDefaultExcludeOrigin(excludeOrigins, matchOrigins, SearchMatchClassifier.HelpText);
            AddSourceOnlyDefaultExcludeOrigin(excludeOrigins, matchOrigins, SearchMatchClassifier.SchemaDescription);
            excludeTests = true;
        }

        private void ValidateEnvironmentDefaults()
        {
            if (validateDefaultLimit && !limitExplicit && defaultLimitError != null)
                AddParseError(defaultLimitError);
            if (validateDefaultSnippetLines && !snippetLinesExplicit && defaultSnippetLinesError != null)
                AddParseError(defaultSnippetLinesError);
            if (validateDefaultMaxLineWidth && !maxLineWidthExplicit && defaultMaxLineWidthError != null)
                AddParseError(defaultMaxLineWidthError);
        }

        private QueryCommandOptions BuildOptions(
            DbPathResolution dbResolution,
            string resolvedDbPath,
            string[] invocationArgs)
        {
            return new QueryCommandOptions
            {
                InvocationArgs = [.. invocationArgs],
                DbPath = resolvedDbPath,
                DbPathExplicit = dbPathExplicit,
                ReadOnly = readOnly,
                DryRun = dryRun,
                ShowPaths = showPaths,
                DataDir = dbResolution.DataDir,
                DataDirSource = dbResolution.DataDirSource,
                Json = json ?? jsonDefault,
                JsonExplicit = jsonExplicit,
                JsonOutputFormat = jsonOutputFormat,
                JsonOutputFormatExplicit = jsonOutputFormatExplicit,
                OutputFormat = outputFormat,
                Limit = limit,
                TotalLimit = totalLimit,
                LimitExplicit = limitExplicit,
                Lang = lang,
                AllowUnknownLang = allowUnknownLang,
                LanguageValidationError = languageValidationError,
                Kind = kind,
                UnusedBucket = unusedBucket,
                MinUnusedConfidence = minUnusedConfidence,
                UnusedActionable = unusedActionable,
                Severity = severity,
                Query = query,
                Selector = selector,
                RawFts = rawFts,
                IncludeBody = includeBody || inspectFieldsIncludeBody,
                BodyStartLine = bodyStartLine,
                BodyLines = bodyLines,
                StartLine = startLine,
                EndLine = endLine,
                StartColumn = startColumn,
                EndColumn = endColumn,
                ContextBefore = contextBefore,
                ContextAfter = contextAfter,
                ContextAfterExplicit = contextAfterExplicit,
                SymmetricContext = symmetricContext,
                ExplicitContextBefore = explicitContextBefore,
                ExplicitContextAfter = explicitContextAfter,
                ImpactDeprecatedDepthUsed = impactDeprecatedDepthUsed,
                FocusLine = focusLine,
                FocusColumn = focusColumn,
                FocusLength = focusLength,
                SnippetLines = snippetLines,
                SnippetLinesExplicit = snippetLinesExplicit,
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
                GroupPartials = groupPartials,
                All = all,
                StrictNotFound = strictNotFound,
                AllowPartial = allowPartial,
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
                TokenBoundary = tokenBoundary,
                CheckWorkspace = checkWorkspace,
                StatusCheckMode = checkWorkspace
                ? statusCheckExplicit
                    ? StatusCheckModeExplicit
                    : StatusCheckModeImpliedByStaleAfter
                : null,
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
                GroupedPerFileLimitExplicit = groupedPerFileLimitExplicit,
                SampleSize = sampleSize,
                RequestedMaxJsonBytes = requestedMaxJsonBytes,
                MaxJsonBytes = maxJsonBytes,
                RawBytes = rawBytes,
                RawKinds = rawKinds,
                IncludeQualifiedCommonCalls = includeQualifiedCommonCalls,
                IncludeMemberReads = includeMemberReads,
                Verbose = verbose,
                Profile = profile,
                SlowQueryMs = slowQueryMs,
                Compact = compact,
                InspectFields = inspectFields,
                InspectFieldValidationError = inspectFieldValidationError,
                MinEntrypointConfidence = minEntrypointConfidence,
                StatusExplainField = statusExplainField,
                StatusLogPath = statusLogPath,
                StatusConfig = statusConfig,
                RedactPaths = redactPaths,
                RankMode = rankMode,
                SymbolSortMode = symbolSortMode,
                SortValue = sortValue,
                SortExplicit = sortExplicit,
                ExtraNames = extraNames,
                MapSections = mapSections,
                SummaryOnly = summaryOnly,
                Progress = progress,
                MapSummaryOnly = mapSummaryOnly,
                DependencyCycles = dependencyCycles,
                DependencyCycleGraphBudget = dependencyCycleGraphBudget,
                IncludeAllDependencyCycleNodes = includeAllDependencyCycleNodes,
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
                IssueState = issueState,
                DuplicateConfidence = duplicateThresholdExplicit ? IssueDuplicatePreflight.CustomDuplicateConfidence : duplicateConfidence,
                DuplicateThreshold = duplicateThreshold,
                DuplicatePreflightTuningExplicit = duplicateConfidenceExplicit || duplicateThresholdExplicit,
                IssueTitle = issueTitle,
                IssueLabels = issueLabels,
                SearchCursor = searchCursor,
                UnusedCursorOffset = unusedCursorOffset,
                OutlineCursorOffset = outlineCursorOffset,
                CursorValue = rawCursorValue,
                DependencyCycleCursor = dependencyCycleCursor,
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

        private void AddParseError(string error)
        {
            parseErrors ??= [];
            parseErrors.Add(error);
        }

        private void AddSearchGuardFilter(string optionName, SearchGuardRole role, SearchGuardDirection direction, string value)
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

        private void AddDependencySymbolFilter(string optionName, string value, List<string> target)
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
            if (target.Contains(trimmed, StringComparer.Ordinal))
                return;
            if (dependencySymbols.Count + dependencySymbolFamilies.Count >= MaxDependencySymbolFilterCount)
            {
                if (!dependencySymbolFilterCountExceeded)
                {
                    AddParseError($"Error: deps accepts at most {MaxDependencySymbolFilterCount} combined --symbol and --symbol-family values. / deps では --symbol と --symbol-family を合計 {MaxDependencySymbolFilterCount} 件まで指定できます。");
                    dependencySymbolFilterCountExceeded = true;
                }
                return;
            }

            target.Add(trimmed);
        }

        private void AddIssueDraftLabels(string rawLabels)
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

        private void AddRecipeQuerySelectors(string optionName, string rawSelectors, List<string> selectors)
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

        private void AddStatusCheckScopes(string rawScopes)
        {
            if (string.IsNullOrWhiteSpace(rawScopes))
            {
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
                return;
            }
            if (!ValidateCsvBounds("--check", rawScopes, MaxStatusCheckScopesCsvLength, MaxStatusCheckScopesCsvEntries, AddParseError))
                return;

            statusCheckScopes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalidScope = false;
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
                        invalidScope = true;
                        AddParseError($"Error: unsupported --check scope '{ConsoleUi.FormatBoundedValue(rawScope)}'. Use one or more of workspace, fold, graph, issues, hotspot, csharp, sql, newer.");
                        break;
                }
            }

            if (statusCheckScopes.Count == 0 && !invalidScope)
                AddParseError("Error: --check scope list cannot be empty. Use --check or --check=workspace,fold,graph,issues,hotspot,csharp,sql,newer.");
        }
        // Track non-repeatable value-taking options that have already been observed and warn on
        // subsequent occurrences. Previously `--db /A --db /B` silently used `/B`; this makes the
        // override explicit so users (and AI callers) can spot a copy/paste or scripted mistake.
        // 非 repeatable な value-taking オプションの初出を記録し、2 回目以降で警告する。以前は
        // `--db /A --db /B` が silent に `/B` を採用していたため、スクリプトやコピペのミスに
        // ユーザーや AI 呼び出し側が気付けるよう、上書きを明示化する。
        private void WarnIfDuplicateSingleValueOption(string canonicalName, string newValue)
        {
            if (seenSingleValueOptions.Add(canonicalName))
                return;
            var displayValue = ConsoleUi.FormatBoundedValue(newValue);
            CommandErrorWriter.WriteStderr($"Warning: {canonicalName} specified more than once; the rightmost CLI value '{displayValue}' takes precedence over earlier CLI values and any environment/config default.");
        }

        private void ParseRawArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var currentArg = args[i];
                if (allowStatusCheck && currentArg.StartsWith("--check=", StringComparison.Ordinal))
                {
                    checkWorkspace = true;
                    statusCheckExplicit = true;
                    AddStatusCheckScopes(currentArg["--check=".Length..]);
                    continue;
                }

                var inlineValue = TrySplitInlineOptionValue(currentArg, out var inlineOptionName)
                    ? currentArg[(inlineOptionName!.Length + 1)..]
                    : null;
                var normalizedArg = inlineOptionName ?? currentArg;

                if (TryParseGeneralOption(normalizedArg, currentArg, inlineValue, args, ref i)
                    || TryParseSearchOption(normalizedArg, currentArg, inlineValue, args, ref i)
                    || TryParseFilterOption(normalizedArg, currentArg, inlineValue, args, ref i)
                    || TryParseResultOption(normalizedArg, currentArg, inlineValue, args, ref i)
                    || TryParseStatusOption(normalizedArg, currentArg, inlineValue, args, ref i)
                    || TryParseLocationOption(normalizedArg, currentArg, inlineValue, args, ref i))
                {
                    continue;
                }

                ParsePositionalArgument(currentArg);
            }
        }

        private void ParsePositionalArgument(string argument)
        {
            if (argument.StartsWith('-'))
            {
                AddParseError($"Error: unsupported option: {ConsoleUi.FormatBoundedValue(argument)}. Use `--` before a query literal that starts with `-`.");
            }
            else if (query == null && positionalGlobAsPath && DbReader.PathLikePatternHasWildcard(argument))
            {
                pathPatterns.Add(argument);
                userPathPatterns.Add(argument);
            }
            else if (query == null)
            {
                query = argument;
            }
            else
            {
                // Extra positional args become additional symbol names / 追加の positional 引数を追加の symbol name として扱う
                extraNames.Add(argument);
            }
        }
    }
}
