using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    /// <summary>
    /// Write actionable hints when a query returns zero results.
    /// 0件時に実行可能なヒントを出力する。
    /// </summary>
    private static void WriteZeroResultHints(QueryCommandOptions options, DbReader reader, string? alternativeHint = null, string? filterHint = null)
    {
        var freshness = reader.GetFreshnessHint();
        if (freshness.FileCount == 0)
        {
            CommandErrorWriter.WriteStderr("Hint: the index is empty. Run 'cdidx index <projectPath>' first.");
            return;
        }

        if (options.Lang != null || options.PathPatterns.Count > 0 || options.ExcludeTests || options.ExcludeComments || options.ExcludeStrings || options.ExcludeFixtures || options.ExcludePaths.Count > 0)
            CommandErrorWriter.WriteStderr($"Hint: {filterHint ?? "try removing --lang, --path, --exclude-path, --exclude-tests, --exclude-comments, --exclude-strings, or --exclude-fixtures to broaden the search."}");

        if (alternativeHint != null)
            CommandErrorWriter.WriteStderr($"Hint: {alternativeHint}");

        if (IsBareTokenSearch(options))
            CommandErrorWriter.WriteStderr($"Hint: {BareTokenAuthAuditHint}");

        var staleAfter = ResolveStaleAfter(options, CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable));
        if (staleAfter.Error != null)
        {
            CommandErrorWriter.WriteStderr(staleAfter.Error);
            return;
        }

        if (freshness.IndexedAt.HasValue)
        {
            var age = GetUtcNow() - freshness.IndexedAt.Value;
            if (age > staleAfter.Value)
                CommandErrorWriter.WriteStderr($"Hint: the index is {FormatDuration(age)} old (threshold: {FormatDuration(staleAfter.Value)}). Run 'cdidx index <projectPath>' to refresh.");
        }
    }

    private static bool IsBareTokenSearch(QueryCommandOptions options)
        => options.RecipeName == null
           && options.NamedSearchQueries.Count == 0
           && string.Equals(options.Query?.Trim(), "token", StringComparison.OrdinalIgnoreCase);

    private static SearchQueryHint? BuildBareTokenSearchHint(QueryCommandOptions options)
        => IsBareTokenSearch(options)
            ? new SearchQueryHint
            {
                Reason = "bare_token_query_auth_noise",
                SuggestedAction = BareTokenAuthAuditHint,
                Flag = "--recipe",
                McpArgument = "recipe",
            }
            : null;

    private static SearchQueryHint? BuildSearchPathGlobHint(DbReader reader, QueryCommandOptions options)
    {
        if (options.PathPatterns.Count != 1)
            return null;
        var pattern = options.PathPatterns[0].Replace('\\', '/').TrimEnd('/');
        if (pattern.Length == 0 || ContainsGlobMeta(pattern) || pattern.EndsWith("/**", StringComparison.Ordinal))
            return null;

        var anchoredMatches = reader.ListFiles(
            query: null,
            limit: 1,
            lang: options.Lang,
            pathPatterns: [pattern],
            excludePathPatterns: options.ExcludePaths,
            excludeTests: options.ExcludeTests,
            since: options.Since);
        if (anchoredMatches.Count > 0)
            return null;

        var suggested = pattern + "/**";
        var prefixMatches = reader.ListFiles(
            query: null,
            limit: 1,
            lang: options.Lang,
            pathPatterns: [suggested],
            excludePathPatterns: options.ExcludePaths,
            excludeTests: options.ExcludeTests,
            since: options.Since);
        if (prefixMatches.Count == 0)
            return null;

        return new SearchQueryHint
        {
            Reason = "path_filter_looks_like_directory",
            SuggestedAction = $"`--path {pattern}` looks like an indexed directory prefix; use `--path {suggested}` to match files below it.",
            Flag = "--path",
            McpArgument = "path",
        };
    }

    private static bool ContainsGlobMeta(string pattern)
        => pattern.IndexOfAny(new[] { '*', '?', '[', ']' }) >= 0;

    private static void AddSearchPathHint(JsonObject payload, SearchQueryHint? pathHint)
    {
        if (pathHint != null)
            payload["path_filter_hint"] = BuildSearchQueryHintJson(pathHint);
    }

    private static void AddBareTokenSearchHint(JsonObject payload, QueryCommandOptions options)
    {
        var hint = BuildBareTokenSearchHint(options);
        if (hint != null)
            payload["token_domain_hint"] = BuildSearchQueryHintJson(hint);
    }

    private static void WriteExactSubstringHintIfNeeded(SearchQueryHint? hint)
    {
        if (hint == null)
            return;

        CommandErrorWriter.WriteStderr($"Hint: {hint.SuggestedAction}");
    }

    private static string BuildZeroResultLine(string message, QueryCommandOptions options)
    {
        var context = BuildQueryContextParts(options, includeDefaultLimit: true).ToList();
        if (context.Count == 0)
            return message + ".";

        return $"{message}. ({string.Join(", ", context)})";
    }

    private static IEnumerable<string> BuildQueryContextParts(QueryCommandOptions options, bool includeDefaultLimit)
    {
        if (!string.IsNullOrWhiteSpace(options.Query))
            yield return $"query: \"{options.Query}\"";
        if (options.PathPatterns.Count > 0)
            yield return $"path: {string.Join(", ", options.PathPatterns)}";
        if (options.ProjectFilters.Count > 0)
            yield return $"project: {string.Join(", ", options.ProjectFilters)}";
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRoot))
            yield return $"project-root: {options.ProjectFilterRoot}";
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRootFallbackReason))
            yield return $"project-root-fallback: {options.ProjectFilterRootFallbackReason}";
        if (options.ExcludePaths.Count > 0)
            yield return $"exclude-path: {string.Join(", ", options.ExcludePaths)}";
        if (options.Lang != null)
            yield return $"lang: {options.Lang}";
        if (options.Kind != null)
            yield return $"kind: {options.Kind}";
        if (options.UnusedBucket != null)
            yield return $"bucket: {options.UnusedBucket}";
        if (options.MinUnusedConfidence != null)
            yield return $"min-confidence: {options.MinUnusedConfidence}";
        if (options.UnusedActionable)
            yield return "actionable: true";
        if (options.ReferenceRankingActive || options.RankMode != ReferenceRankMode.Weighted)
            yield return $"rank-by: {FormatReferenceRankMode(options.RankMode)}";
        if (options.ExcludeTests)
            yield return "exclude-tests: true";
        if (options.ExcludeComments)
            yield return "exclude-comments: true";
        if (options.ExcludeStrings)
            yield return "exclude-strings: true";
        if (options.ExcludeFixtures)
            yield return "exclude-fixtures: true";
        if (options.Since.HasValue)
            yield return $"since: {options.Since.Value:O}";
        if (options.CountOnly)
            yield return "count: true";
        if (options.RawFts)
            yield return "fts: true";
        if (options.Exact)
            yield return "exact: true";
        if (options.Prefix)
            yield return "prefix: true";
        if (options.NoDedup)
            yield return "dedup: false";
        if (options.ContextBefore > 0)
            yield return $"before: {options.ContextBefore}";
        if (options.ContextAfter > 0)
            yield return options.ContextAfterExplicit ? $"depth: {options.ContextAfter}" : $"after: {options.ContextAfter}";
        if (includeDefaultLimit || options.Limit != 20)
            yield return $"limit: {options.Limit}";
    }

    private static JsonObject BuildQueryContextJson(QueryCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var query = new JsonObject
        {
            ["limit"] = options.Limit,
        };
        if (!string.IsNullOrWhiteSpace(options.Query))
            query["text"] = options.Query;
        if (options.PathPatterns.Count > 0)
            query["path"] = JsonSerializer.SerializeToNode(options.PathPatterns, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ProjectFilters.Count > 0)
            query["project"] = JsonSerializer.SerializeToNode(options.ProjectFilters, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRoot))
            query["project_filter_root"] = options.ProjectFilterRoot;
        if (!string.IsNullOrWhiteSpace(options.ProjectFilterRootFallbackReason))
            query["project_filter_root_fallback_reason"] = options.ProjectFilterRootFallbackReason;
        if (options.ExcludePaths.Count > 0)
            query["exclude_path"] = JsonSerializer.SerializeToNode(options.ExcludePaths, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.Lang != null)
            query["lang"] = options.Lang;
        if (options.Kind != null)
            query["kind"] = options.Kind;
        if (options.Severity != null)
            query["severity"] = options.Severity;
        if (options.UnusedBucket != null)
            query["bucket"] = options.UnusedBucket;
        if (options.MinUnusedConfidence != null)
            query["min_confidence"] = options.MinUnusedConfidence;
        if (options.UnusedActionable)
            query["actionable"] = true;
        if (options.AuditScopeExplicit)
            query["audit_scope"] = options.AuditScope;
        if (options.VisibilityFilters.Count > 0)
            query["visibility"] = JsonSerializer.SerializeToNode(options.VisibilityFilters, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ExcludeVisibilityFilters.Count > 0)
            query["exclude_visibility"] = JsonSerializer.SerializeToNode(options.ExcludeVisibilityFilters, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.MatchOrigins.Count > 0)
            query["match_origins"] = JsonSerializer.SerializeToNode(options.MatchOrigins, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ExcludeOrigins.Count > 0)
            query["exclude_origins"] = JsonSerializer.SerializeToNode(options.ExcludeOrigins, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.ResultKinds.Count > 0)
            query["result_kinds"] = JsonSerializer.SerializeToNode(options.ResultKinds, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.UnusedCursorOffset.HasValue)
        {
            query["cursor"] = options.CursorValue
                ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"unused:{options.UnusedCursorOffset.Value}");
            query["offset"] = options.UnusedCursorOffset.Value;
        }
        if (options.ReferenceRankingActive)
        {
            query["rank_by"] = FormatReferenceRankMode(options.RankMode);
            query["ranking_recipe"] = BuildReferenceRankingRecipeJson(options.RankMode);
        }
        else if (options.RankMode != ReferenceRankMode.Weighted)
            query["rank_by"] = FormatReferenceRankMode(options.RankMode);
        if (options.SymbolSortMode != SymbolSortMode.Name)
            query["sort"] = options.SymbolSortMode.ToString().ToLowerInvariant();
        if (options.ExcludeTests)
            query["exclude_tests"] = true;
        if (options.DiscoveryBaselineIncludePaths.Count > 0 || options.DiscoveryBaselineExcludePaths.Count > 0)
            query["effective_path_scope"] = BuildEffectiveDiscoveryPathScopeJson(options, jsonOptions);
        if (options.ExcludeComments)
            query["exclude_comments"] = true;
        if (options.ExcludeStrings)
            query["exclude_strings"] = true;
        if (options.ExcludeFixtures)
            query["exclude_fixtures"] = true;
        var generatedFileFilterAvailable = ActiveSqliteDiagnosticsReader.Value?.GeneratedFileFilterAvailable;
        query["include_generated"] = options.IncludeGenerated;
        query["generated_code_policy"] = options.IncludeGenerated
            ? "include"
            : generatedFileFilterAvailable == false
                ? "unavailable"
                : "exclude";
        if (generatedFileFilterAvailable.HasValue)
            query["generated_file_filter_available"] = generatedFileFilterAvailable.Value;
        if (options.Since.HasValue)
            query["since"] = options.Since.Value;
        if (options.CountOnly || options.OutputFormat == OutputFormatCount)
            query["count"] = true;
        if (options.FirstPerFile || options.SampleSize.HasValue)
            query["row_selectors"] = BuildSearchRowSelectorContextJson(options);
        if (options.All)
            query["all"] = true;
        if (options.RawFts)
            query["fts"] = true;
        if (options.Regex)
            query["regex"] = true;
        if (options.Exact)
            query["exact"] = true;
        if (options.ExactSubstring)
            query["exact_substring"] = true;
        if (options.TokenBoundary)
            query["token_boundary"] = true;
        if (options.Prefix)
            query["prefix"] = true;
        if (options.NoDedup)
            query["dedup"] = false;
        if (options.GuardFilters.Count > 0)
        {
            query["guard_filters"] = BuildSearchGuardFiltersJson(options.GuardFilters);
            query["guard_window"] = options.GuardWindow;
            query["guard_scope"] = FormatSearchGuardScope(options.GuardScope);
        }
        if (options.RawKinds)
            query["raw_kinds"] = true;
        if (options.IncludeQualifiedCommonCalls)
            query["include_qualified_common_calls"] = true;
        if (options.DependencyCycles)
        {
            query["cycles"] = true;
            query["graph_budget"] = options.DependencyCycleGraphBudget;
            query["all_cycle_nodes"] = options.IncludeAllDependencyCycleNodes;
            if (options.DependencyCycleCursor.HasValue)
            {
                query["cursor"] = FormatDependencyCycleCursor(options.DependencyCycleCursor.Value);
                query["offset"] = options.DependencyCycleCursor.Value.Offset;
            }
        }
        if (options.DependencySuppressNoise)
            query["suppress_noise"] = true;
        if (options.DependencySymbols.Count > 0)
            query["symbol"] = JsonSerializer.SerializeToNode(options.DependencySymbols, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.DependencySymbolFamilies.Count > 0)
            query["symbol_family"] = JsonSerializer.SerializeToNode(options.DependencySymbolFamilies, CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        if (options.FocusLine.HasValue)
            query["focus_line"] = options.FocusLine.Value;
        if (options.FocusColumn.HasValue)
            query["focus_column"] = options.FocusColumn.Value;
        if (options.ContextBefore > 0)
            query["before"] = options.ContextBefore;
        if (options.ContextAfter > 0)
            query[options.ContextAfterExplicit ? "depth" : "after"] = options.ContextAfter;
        return query;
    }

    private static JsonObject BuildEffectiveDiscoveryPathScopeJson(
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        var includeGroups = new JsonArray
        {
            new JsonObject
            {
                ["origin"] = "implicit_source_baseline",
                ["patterns"] = JsonSerializer.SerializeToNode(options.DiscoveryBaselineIncludePaths.ToList(), context.ListString),
            },
        };
        if (options.PathPatterns.Count > 0)
        {
            includeGroups.Add(new JsonObject
            {
                ["origin"] = "explicit_cli",
                ["patterns"] = JsonSerializer.SerializeToNode(options.PathPatterns, context.ListString),
            });
        }

        var excludeGroups = new JsonArray
        {
            new JsonObject
            {
                ["origin"] = "implicit_source_baseline",
                ["patterns"] = JsonSerializer.SerializeToNode(options.DiscoveryBaselineExcludePaths.ToList(), context.ListString),
            },
        };
        if (options.ExcludePaths.Count > 0)
        {
            excludeGroups.Add(new JsonObject
            {
                ["origin"] = "explicit_cli",
                ["patterns"] = JsonSerializer.SerializeToNode(options.ExcludePaths, context.ListString),
            });
        }

        return new JsonObject
        {
            ["include_group_operator"] = "and",
            ["patterns_within_include_group_operator"] = "or",
            ["include_groups"] = includeGroups,
            ["exclude_group_operator"] = "or",
            ["exclude_groups"] = excludeGroups,
        };
    }

    private static void AddReferenceRankingQueryContextJson(
        JsonObject payload,
        QueryCommandOptions options,
        JsonSerializerOptions jsonOptions)
    {
        payload["query_context"] = BuildQueryContextJson(options, jsonOptions);
    }

    private static JsonArray BuildSearchRowSelectorContextJson(QueryCommandOptions options)
    {
        var selectors = new JsonArray();
        if (options.FirstPerFile)
        {
            selectors.Add(new JsonObject
            {
                ["mode"] = "first_per_file",
                ["applied"] = true,
            });
        }
        if (options.SampleSize.HasValue)
        {
            selectors.Add(new JsonObject
            {
                ["mode"] = "sample",
                ["applied"] = true,
                ["sample_size"] = options.SampleSize.Value,
                ["sample_mode"] = SearchSampleMode,
                ["seed"] = SearchSampleSeed,
            });
        }
        return selectors;
    }

    private static JsonArray BuildSearchGuardFiltersJson(IReadOnlyList<SearchGuardFilter> guardFilters)
    {
        var filters = new JsonArray();
        foreach (var filter in guardFilters)
        {
            var role = FormatQueryContextSearchGuardRole(filter.Role);
            var direction = FormatQueryContextSearchGuardDirection(filter.Direction);
            var item = new JsonObject
            {
                ["name"] = $"{role}-{direction}",
                ["role"] = role,
                ["direction"] = direction,
                ["query"] = filter.Query,
            };
            if (filter.Scope.HasValue)
                item["scope"] = FormatSearchGuardScope(filter.Scope.Value);
            if (filter.EvidenceKind != SearchGuardEvidenceKind.Text)
                item["evidence_kind"] = filter.EvidenceKind switch
                {
                    SearchGuardEvidenceKind.CSharpBoundedFileRead => "csharp_bounded_file_read",
                    SearchGuardEvidenceKind.CSharpEnumerationOptions => "csharp_enumeration_options",
                    _ => "text",
                };

            filters.Add(item);
        }

        return filters;
    }

    private static string FormatQueryContextSearchGuardRole(SearchGuardRole role)
        => role == SearchGuardRole.Require ? "require" : "reject";

    private static string FormatQueryContextSearchGuardDirection(SearchGuardDirection direction)
        => direction == SearchGuardDirection.Before ? "before" : "after";

    internal static ExactZeroHintResult? BuildExactZeroHint<T>(bool shouldProbe, Func<bool> anyRelaxedMatch, Func<List<T>> relaxedSampleQuery, Func<T, string?> nameSelector)
    {
        return BuildExactZeroHint(shouldProbe, anyRelaxedMatch, relaxedCountQuery: null, relaxedSampleQuery, nameSelector);
    }

    internal static ExactZeroHintResult? BuildExactZeroHint<T>(bool shouldProbe, Func<bool> anyRelaxedMatch, Func<int>? relaxedCountQuery, Func<List<T>> relaxedSampleQuery, Func<T, string?> nameSelector)
    {
        if (!shouldProbe)
            return null;

        if (!anyRelaxedMatch())
            return null;

        int? relaxedCount = null;
        if (relaxedCountQuery != null)
        {
            relaxedCount = relaxedCountQuery();
            if (relaxedCount == 0)
                return null;
        }

        var relaxedResults = relaxedSampleQuery();
        if (relaxedResults.Count == 0)
            return null;

        var sampleNames = relaxedResults
            .Select(nameSelector)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .Select(name => name!)
            .ToList();

        return new ExactZeroHintResult
        {
            RelaxedCount = relaxedCount,
            SampleNames = sampleNames,
            Suggestion = ExactZeroHintResult.DefaultSuggestion,
        };
    }

    private static void AddFreshnessHint(JsonObject payload, DbReader reader)
    {
        var freshness = reader.GetFreshnessHint();
        payload["indexed_file_count"] = freshness.FileCount;
        payload["indexed_at"] = freshness.IndexedAt.HasValue
            ? JsonValue.Create(freshness.IndexedAt.Value)
            : null;
        payload["freshness_available"] = freshness.FreshnessAvailable;
        if (!freshness.FreshnessAvailable && freshness.FreshnessDegradedReason != null)
            payload["freshness_degraded_reason"] = freshness.FreshnessDegradedReason;
        AddReadOnlyFallbackDiagnostics(payload, reader);
    }

    internal static void AddReadOnlyFallbackDiagnostics(JsonObject payload, DbReader reader)
    {
        if (!HasReadOnlyFallbackDiagnostics(reader))
        {
            return;
        }

        payload["read_only_fallback"] = reader.ReadOnlyFallback;
        payload["wal_checkpoint_attempted"] = reader.WalCheckpointAttempted;
        payload["wal_checkpoint_succeeded"] = reader.WalCheckpointSucceeded;
        payload["read_only_immutable_fallback"] = reader.ReadOnlyImmutableFallback;
        if (reader.WalCheckpointSkippedReason != null)
            payload["wal_checkpoint_skipped_reason"] = reader.WalCheckpointSkippedReason;
        if (reader.WalCheckpointFailureReason != null)
            payload["wal_checkpoint_failure_reason"] = reader.WalCheckpointFailureReason;
        if (reader.WalCheckpointBusy != null)
            payload["wal_checkpoint_busy"] = reader.WalCheckpointBusy;
        if (reader.WalCheckpointLogPageCount != null)
            payload["wal_checkpoint_log_page_count"] = reader.WalCheckpointLogPageCount;
        if (reader.WalCheckpointCheckpointedPageCount != null)
            payload["wal_checkpoint_checkpointed_page_count"] = reader.WalCheckpointCheckpointedPageCount;
        if (reader.WalCheckpointRemainingPageCount != null)
            payload["wal_checkpoint_remaining_page_count"] = reader.WalCheckpointRemainingPageCount;
        payload["wal_stale_snapshot_risk"] = reader.WalStaleSnapshotRisk;
        if (reader.WalStaleSnapshotReason != null)
            payload["wal_stale_snapshot_reason"] = reader.WalStaleSnapshotReason;
    }

    private static bool HasReadOnlyFallbackDiagnostics(DbReader? reader)
        => reader != null
           && (reader.ReadOnlyFallback
               || reader.WalCheckpointAttempted
               || reader.ReadOnlyImmutableFallback
               || reader.WalCheckpointSkippedReason != null
               || reader.WalCheckpointFailureReason != null
               || reader.WalStaleSnapshotRisk);

    private static JsonObject BuildCountJsonPayload(
        DbReader reader,
        JsonSerializerOptions jsonOptions,
        int count,
        int? files = null,
        string? query = null,
        QueryCommandOptions? queryOptions = null,
        bool? graphTableAvailable = null,
        bool degraded = false,
        ExactQuerySignal? exactSignal = null,
        ExactZeroHintResult? exactZeroHint = null,
        FtsQueryDiagnostics? ftsQueryDiagnostics = null,
        SearchQueryHint? exactSubstringHint = null,
        Action<JsonObject>? extraFields = null,
        bool includeIndexGenerationAuthority = false,
        bool deferAuthority = false)
    {
        var payload = new JsonObject
        {
            ["count"] = count,
        };
        if (files.HasValue)
        {
            payload["files"] = files.Value;
            payload["file_count"] = files.Value;
        }
        if (query != null)
            payload["query"] = query;
        if (graphTableAvailable.HasValue)
            payload["graph_table_available"] = graphTableAvailable.Value;
        if (degraded)
            payload["degraded"] = true;
        if (exactSignal.HasValue)
            AddExactJsonFields(payload, exactSignal.Value);
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        if (ftsQueryDiagnostics is { HasDegradation: true })
        {
            payload["query_degraded_reason"] = ftsQueryDiagnostics.QueryDegradedReason;
            payload["tokens_dropped"] = JsonSerializer.SerializeToNode(ftsQueryDiagnostics.TokensDropped.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        }
        if (exactSubstringHint != null)
            payload["exact_substring_hint"] = BuildSearchQueryHintJson(exactSubstringHint);
        extraFields?.Invoke(payload);
        if (count == 0 && includeIndexGenerationAuthority)
            AddIndexGenerationAuthorityJsonFields(payload, reader, jsonOptions);
        AddCountEnvelopeJsonFields(payload, reader, jsonOptions, queryOptions, deferAuthority);
        return payload;
    }

    private static void AddCountEnvelopeJsonFields(JsonObject payload, DbReader reader, JsonSerializerOptions jsonOptions, QueryCommandOptions? queryOptions, bool deferAuthority = false)
    {
        payload["api_version"] = JsonOutputContract.ApiVersion;
        if (queryOptions != null)
            payload["query_context"] = BuildQueryContextJson(queryOptions, jsonOptions);
        AddFreshnessHint(payload, reader);
        if (!deferAuthority)
            AddCountAuthorityJsonFields(payload);
    }

    private static void AddCountAuthorityJsonFields(JsonObject payload)
    {
        var degraded =
            JsonBool(payload, "degraded") == true
            || JsonBool(payload, "graph_table_available") == false
            || JsonBool(payload, "exact_index_available") == false
            || JsonBool(payload, "sql_graph_contract_ready") == false
            || JsonBool(payload, "graph_degraded") == true
            || JsonBool(payload, "scan_truncated") == true
            || JsonBool(payload, "scan_cap_reached") == true
            || JsonBool(payload, "scan_timed_out") == true
            || JsonBool(payload, "truncated") == true
            || JsonBool(payload, "wal_stale_snapshot_risk") == true;
        payload["degraded"] = degraded;
        payload["authoritative_count"] = !degraded;
    }

    private static bool? JsonBool(JsonObject payload, string name)
    {
        return payload.TryGetPropertyValue(name, out var node)
            && node is JsonValue value
            && value.TryGetValue<bool>(out var boolValue)
            ? boolValue
            : null;
    }

    private static JsonObject BuildJsonZeroResultPayload(
        DbReader reader,
        JsonSerializerOptions jsonOptions,
        string? resultsKey = null,
        string? query = null,
        ExactZeroHintResult? exactZeroHint = null,
        FtsQueryDiagnostics? ftsQueryDiagnostics = null,
        bool includeFiles = false,
        bool? graphTableAvailable = null,
        bool? degraded = null,
        ExactQuerySignal? exactSignal = null,
        QueryCommandOptions? queryOptions = null,
        SearchQueryHint? exactSubstringHint = null,
        Action<JsonObject>? extraFields = null)
    {
        var payload = new JsonObject
        {
            ["count"] = 0,
        };

        if (query != null)
            payload["query"] = query;
        if (resultsKey != null)
            payload[resultsKey] = new JsonArray();
        if (includeFiles)
            payload["files"] = 0;
        if (graphTableAvailable.HasValue)
            payload["graph_table_available"] = graphTableAvailable.Value;
        if (degraded.HasValue)
            payload["degraded"] = degraded.Value;
        if (exactSignal.HasValue)
        {
            payload["exact_index_available"] = exactSignal.Value.ExactIndexAvailable;
            if (exactSignal.Value.DegradedReason != null)
                payload["degraded_reason"] = exactSignal.Value.DegradedReason;
        }
        if (exactZeroHint != null)
            payload["exact_zero_hint"] = JsonSerializer.SerializeToNode(exactZeroHint, CliJsonSerializerContextFactory.Create(jsonOptions).ExactZeroHintResult);
        if (ftsQueryDiagnostics is { HasDegradation: true })
        {
            payload["query_degraded_reason"] = ftsQueryDiagnostics.QueryDegradedReason;
            payload["tokens_dropped"] = JsonSerializer.SerializeToNode(ftsQueryDiagnostics.TokensDropped.ToList(), CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        }
        if (exactSubstringHint != null)
            payload["exact_substring_hint"] = BuildSearchQueryHintJson(exactSubstringHint);
        if (queryOptions != null)
            payload["query_context"] = BuildQueryContextJson(queryOptions, jsonOptions);
        extraFields?.Invoke(payload);
        AddIndexGenerationAuthorityJsonFields(payload, reader, jsonOptions);
        AddFreshnessHint(payload, reader);

        return payload;
    }

    private static void AddIndexGenerationAuthorityJsonFields(
        JsonObject payload,
        DbReader reader,
        JsonSerializerOptions jsonOptions)
    {
        var completion = reader.GetPersistedIndexCompletion();
        if (completion.IndexComplete)
            return;

        var policy = completion.SymbolKindFilterPolicy;
        payload["index_complete"] = completion.IndexComplete;
        payload["symbol_kind_filter_provenance_available"] = policy.ProvenanceAvailable;
        if (policy.ProvenanceAvailable)
        {
            payload["symbol_kind_filter"] = new JsonObject
            {
                ["include"] = JsonSerializer.SerializeToNode(
                    policy.Include.ToList(),
                    CliJsonSerializerContextFactory.Create(jsonOptions).ListString),
                ["exclude"] = JsonSerializer.SerializeToNode(
                    policy.Exclude.ToList(),
                    CliJsonSerializerContextFactory.Create(jsonOptions).ListString),
            };
        }
        if (policy.SymbolsDropped.HasValue)
            payload["symbols_dropped_by_kind_filter"] = policy.SymbolsDropped.Value;
        payload["index_incomplete_reasons"] = JsonSerializer.SerializeToNode(
            completion.IndexIncompleteReasons.ToList(),
            CliJsonSerializerContextFactory.Create(jsonOptions).ListString);
        payload["degraded"] = true;
        payload["authoritative_count"] = false;
        payload["index_generation_warning"] =
            "The persisted index generation is coverage-limited; negative symbol and graph results are not authoritative.";
    }

    private static void WriteIndexGenerationAuthorityWarningIfNeeded(DbReader reader)
    {
        var completion = reader.GetPersistedIndexCompletion();
        if (completion.IndexComplete)
            return;

        var reasons = completion.IndexIncompleteReasons.Count == 0
            ? DegradationReasonCodes.IndexIncomplete
            : string.Join(", ", completion.IndexIncompleteReasons.Take(4));
        CommandErrorWriter.WriteStderr(
            $"WARN: index generation is coverage-limited ({reasons}); negative symbol and graph results are not authoritative.");
    }

    private static JsonObject BuildSearchQueryHintJson(SearchQueryHint hint) => new()
    {
        ["reason"] = hint.Reason,
        ["suggested_action"] = hint.SuggestedAction,
        ["flag"] = hint.Flag,
        ["mcp_argument"] = hint.McpArgument,
    };

    private static JsonObject BuildGroupedHotspotsZeroJsonPayload(DbReader reader, JsonSerializerOptions jsonOptions, bool countOnly, bool graphAvailable, QueryCommandOptions? queryOptions = null)
    {
        var payload = BuildJsonZeroResultPayload(
            reader,
            jsonOptions,
            resultsKey: countOnly || queryOptions?.SummaryOnly == true ? null : "hotspots",
            includeFiles: countOnly,
            graphTableAvailable: graphAvailable,
            degraded: !graphAvailable,
            queryOptions: queryOptions,
            extraFields: static zeroPayload =>
            {
                zeroPayload["definition_site_total"] = 0;
                zeroPayload["grouped_by"] = HotspotsGroupedByNameKind;
            });
        AddHotspotsGroupingContractJsonFields(payload, HotspotsGroupedByNameKind, queryOptions, jsonOptions, countOnly);
        if (!graphAvailable)
            payload["note"] = "symbol_references table is missing in this index (legacy or read-only DB). Zero result is degraded, not authoritative.";
        return payload;
    }

    private static void WriteExactZeroHint(ExactZeroHintResult? exactZeroHint)
    {
        if (exactZeroHint == null)
            return;

        var examples = exactZeroHint.SampleNames.Count == 0
            ? string.Empty
            : $" (e.g. {string.Join(", ", exactZeroHint.SampleNames.Select(name => $"`{name}`"))})";
        if (exactZeroHint.RelaxedCount.HasValue)
            CommandErrorWriter.WriteStderr($"Hint: --exact found 0 matches, but substring matching would return {exactZeroHint.RelaxedCount}{examples}. Drop --exact or use the exact indexed name.");
        else
            CommandErrorWriter.WriteStderr($"Hint: --exact found 0 matches, but substring matching would return results{examples}. Drop --exact or use the exact indexed name.");
    }
}
