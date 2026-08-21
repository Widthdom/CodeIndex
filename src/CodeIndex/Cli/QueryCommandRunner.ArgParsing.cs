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
        => scope switch
        {
            SearchGuardScope.SameLine => "same-line",
            SearchGuardScope.Container => "container",
            _ => "window",
        };

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
        bool allowOutlineSort = false,
        bool positionalGlobAsPath = false)
        => new QueryArgumentParser(
            jsonDefault,
            allowNamedQuery,
            allowStatusCheck,
            allowIssueDraftsFormat,
            validateDefaultLimit,
            validateDefaultSnippetLines,
            validateDefaultMaxLineWidth,
            applySearchSourceDefaults,
            allowOutlineSort,
            positionalGlobAsPath).Parse(args);

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
        var inheritedBatchContext = s_batchDatabaseContext?.ReaderInheritedByCurrentChild == true
            ? s_batchDatabaseContext
            : null;
        var effectiveDbPath = inheritedBatchContext != null
            ? inheritedBatchContext.DbPath
            : dbPath;
        var effectiveDbPathExplicit = inheritedBatchContext != null
            ? inheritedBatchContext.DbPathExplicit
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
                case "list":
                    sections.Add("list");
                    break;
                case "summary":
                    sections.Add("summary");
                    break;
                case "tree":
                case "module":
                case "modules":
                    sections.Add("tree");
                    break;
                case "entrypoint":
                case "entrypoints":
                case "hotspot":
                    sections.Add("hotspots");
                    break;
                case "largest":
                case "largest-files":
                case "largest_files":
                    sections.Add("metrics");
                    break;
                case "languages":
                case "hotspots":
                case "metrics":
                    sections.Add(section);
                    break;
                default:
                    addParseError($"Error: --sections contains unsupported section '{ConsoleUi.FormatBoundedValue(rawSection)}'. Use one or more of summary, tree, languages, hotspots, metrics, or list.");
                    break;
            }
        }

        if (sections.Count == 0)
            addParseError("Error: --sections cannot be empty. Use one or more of summary, tree, languages, hotspots, metrics, or list.");
        return sections.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string>? ParseInspectFields(
        string rawValue,
        Action<string> addParseError,
        out bool includeBody,
        out ProjectionFieldValidationError? validationError)
    {
        includeBody = false;
        validationError = null;
        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = false;
        var invalidField = false;

        if (!ValidateCsvBounds("--fields", rawValue, MaxInspectFieldsCsvLength, MaxInspectFieldsCsvEntries, addParseError))
            return fields;

        foreach (var rawField in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ProjectionFieldRegistry.TryResolveInspectSelector(
                    rawField,
                    out var canonical,
                    out var selectorIncludesBody,
                    out var expansion,
                    out var selectorError))
            {
                invalidField = true;
                validationError ??= selectorError;
                addParseError($"Error: {selectorError!.Message}");
                continue;
            }

            includeBody |= selectorIncludesBody;
            if (expansion is not null)
            {
                foreach (var expandedField in expansion)
                {
                    if (seen.Add(expandedField))
                        fields.Add(expandedField);
                }
                continue;
            }
            if (string.Equals(canonical, "all", StringComparison.Ordinal))
            {
                all = true;
                continue;
            }

            if (seen.Add(canonical))
                fields.Add(canonical);
        }

        if (all && fields.Count > 0)
        {
            validationError ??= new ProjectionFieldValidationError(
                "The --fields selector 'all' cannot be combined with specific field names for command 'inspect'.",
                "Use `--fields all` by itself, or remove `all` and list only the required groups or collection fields.");
            addParseError("Error: --fields all cannot be combined with specific field names.");
        }
        if (fields.Contains("list", StringComparer.Ordinal) && fields.Count > 1)
        {
            validationError ??= new ProjectionFieldValidationError(
                "The --fields discovery value 'list' must be used by itself for command 'inspect'.",
                "Run `cdidx inspect --fields list` without other field names.");
            addParseError("Error: --fields list cannot be combined with specific field names.");
        }
        if (!all && fields.Count == 0 && !invalidField)
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
        var invalidFields = new List<string>();
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
                    invalidFields.Add(rawField);
                    continue;
            }
        }

        if (invalidFields.Count > 0)
        {
            var invalidValues = string.Join(", ", invalidFields.Select(field => $"'{ConsoleUi.FormatBoundedValue(field)}'"));
            var valueLabel = invalidFields.Count == 1 ? "value" : "values";
            addParseError($"Error: unsupported --outline-fields {valueLabel} {invalidValues}. Use one or more of all, kind, name, display_name, path, line, start_line, end_line, depth, body_start_line, body_end_line, signature, signature_truncated, signature_original_length, container_kind, container_name, visibility, return_type, sort_mode, reference_count, size_lines, complexity_score, or aliases range, lines, body, body_range, container, refs, size, span, complexity.");
            return all ? null : fields;
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
        var acceptedValues = CliFlagSchema.GetCanonicalValuesForCommand("search", optionName);
        foreach (var rawOrigin in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CliFlagSchema.TryNormalizeOptionValue("search", optionName, rawOrigin, out var origin))
            {
                addParseError($"Error: unsupported {optionName} value '{ConsoleUi.FormatBoundedValue(rawOrigin)}'. Use {FormatOptionValueList(acceptedValues)}.");
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

    private static void AddSearchResultKinds(string rawValue, List<string> resultKinds, Action<string> addParseError)
    {
        if (!ValidateCsvBounds("--result-kind", rawValue, MaxSearchProjectionFieldsCsvLength, MaxSearchProjectionFieldsCsvEntries, addParseError))
            return;
        var acceptedValues = CliFlagSchema.GetCanonicalValuesForCommand("search", "--result-kind");
        foreach (var rawKind in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CliFlagSchema.TryNormalizeOptionValue("search", "--result-kind", rawKind, out var kind))
            {
                addParseError($"Error: unsupported --result-kind value '{ConsoleUi.FormatBoundedValue(rawKind)}'. Use {FormatOptionValueList(acceptedValues)}.");
                continue;
            }
            if (!resultKinds.Contains(kind, StringComparer.Ordinal))
                resultKinds.Add(kind);
        }
    }

    private static string FormatOptionValueList(IReadOnlyList<string> values) =>
        values.Count switch
        {
            0 => "a documented value",
            1 => values[0],
            _ => $"{string.Join(", ", values.Take(values.Count - 1))}, or {values[values.Count - 1]}",
        };

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

    private static bool TryResolveSearchExactMode(
        QueryCommandOptions options,
        out bool exact,
        out string? error,
        out string? hint)
    {
        if (!TryRejectMultipleExactFlags(options, out error))
        {
            exact = false;
            hint = "Choose one search matching mode: --fts, --exact-substring, or --token-boundary. Use --exact only as the backward-compatible alias for --exact-substring.";
            return false;
        }
        if (options.ExactName)
        {
            exact = false;
            error = "Error: --exact-name applies to name-based commands (symbols/definition/references/callers/callees/inspect), not search. Use --exact-substring for search, or keep --exact for backward compatibility.";
            hint = "Use --exact-substring or --token-boundary for literal search matching, or remove the exact-name flag.";
            return false;
        }
        if (options.RawFts && (options.Exact || options.ExactSubstring || options.TokenBoundary))
        {
            exact = false;
            error = "Error: raw FTS mode (--fts) cannot be combined with literal search modes (--exact, --exact-substring, or --token-boundary).";
            hint = "Remove --fts to use literal/exact-substring matching, or remove the exact-mode flag to keep raw FTS5 syntax.";
            return false;
        }

        exact = options.Exact || options.ExactSubstring;
        error = null;
        hint = null;
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
        var count = (options.Exact ? 1 : 0) + (options.ExactSubstring ? 1 : 0) + (options.ExactName ? 1 : 0) + (options.TokenBoundary ? 1 : 0);
        if (count > 1)
        {
            error = "Error: pass only one of --exact, --exact-substring, --token-boundary, --exact-name.";
            return false;
        }

        error = null;
        return true;
    }
}
