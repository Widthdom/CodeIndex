using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private sealed partial class QueryArgumentParser
    {
        private bool TryParseSearchOption(string normalizedArg, string currentArg, string? inlineValue, string[] args, ref int i)
        {
            switch (normalizedArg)
            {
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
                case "--issue-state":
                    if (TryReadStringOptionValue(args, ref i, "--issue-state", inlineValue, allowSeparatedDashPrefixedLiteralValue: false, out var issueStateValue, out var issueStateError))
                        issueState = issueStateValue!.ToLowerInvariant();
                    else
                        AddParseError(issueStateError!);
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
                    var allowSeparatedDashPrefixedCursorValue = inlineValue is null
                        && i + 1 < args.Length
                        && TryParseSearchCursor(args[i + 1], out _);
                    if (TryReadStringOptionValue(args, ref i, "--cursor", inlineValue, allowSeparatedDashPrefixedLiteralValue: allowSeparatedDashPrefixedCursorValue, out var cursorValue, out var cursorError))
                    {
                        WarnIfDuplicateSingleValueOption("--cursor", cursorValue!);
                        var parsedCursorValue = cursorValue!;
                        if (TryParseSearchCursor(parsedCursorValue, out var parsedCursor))
                            searchCursor = parsedCursor;
                        else if (TryParseUnusedCursor(parsedCursorValue, out var parsedUnusedCursorOffset))
                            unusedCursorOffset = parsedUnusedCursorOffset;
                        else if (TryParseOutlineCursor(parsedCursorValue, out var parsedOutlineCursorOffset))
                            outlineCursorOffset = parsedOutlineCursorOffset;
                        else if (TryParseDependencyCycleCursor(parsedCursorValue, out var parsedDependencyCycleCursor))
                            dependencyCycleCursor = parsedDependencyCycleCursor;
                        else if (InspectGraphCursorCodec.TryParse(parsedCursorValue, out _))
                        {
                            // inspect validates query and index-generation binding after
                            // resolving the effective path/name query inside RunInspect.
                            // inspect は有効な path/name query を確定後、RunInspect 内で
                            // query / index generation binding を検証する。
                        }
                        else
                        {
                            AddParseError("Error: --cursor must be a search, unused, outline, dependency-cycle, or inspect-graph pagination cursor returned as `next_cursor`.");
                            break;
                        }
                        rawCursorValue = parsedCursorValue;
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
                default:
                    return false;
            }

            return true;
        }
    }
}
