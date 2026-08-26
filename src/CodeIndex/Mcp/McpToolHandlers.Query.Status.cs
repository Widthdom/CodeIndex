using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{

    private JsonNode ExecuteStatus(JsonNode? id, JsonNode? args)
    {
        var checkWorkspace = args?["check"]?.GetValue<bool>() ?? false;
        var staleAfterSeconds = ReadOptionalIntArgument(args, "staleAfterSeconds") ?? (int)TimeSpan.FromDays(1).TotalSeconds;
        if (staleAfterSeconds <= 0)
            return CreateToolErrorResponse(id, "staleAfterSeconds must be greater than or equal to 1");
        var explain = args?["explain"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (explain is not (null or "freshness" or "readiness" or "all"))
            return CreateToolErrorResponse(id, "explain must be one of freshness, readiness, all");
        var format = ReadResponseFormat(args);
        if (format is not ("full" or "compact"))
            return CreateToolErrorResponse(id, "format must be one of full, compact");
        if (!TryReadStatusProjectionFields(args, out var projectionFields, out var projectionError))
            return CreateToolErrorResponse(id, projectionError!);
        if (!TryReadStatusScopes(args, out var statusScopes, out var scopeError))
            return CreateToolErrorResponse(id, scopeError!);
        var includeConfig = args?["config"]?.GetValue<bool>() ?? false;
        var includeLogPath = args?["logPath"]?.GetValue<bool>() ?? false;
        var runUpdateCheck = args?["updateCheck"]?.GetValue<bool>() ?? false;

        string? unavailableProjectionError = null;
        var response = WithDbReader(id, args, reader =>
        {
            var requestToken = _currentRequestToken.Value;
            var includeDatabaseSizeAttribution =
                format == "full"
                && (projectionFields == null
                    || projectionFields.Contains("database_size_attribution", StringComparer.Ordinal));
            var status = reader.GetStatus(includeDatabaseSizeAttribution);
            QueryCommandRunner.ApplyStatusSymbolKindLimits(status, reader.GetSymbolKindCounts());
            WorkspaceMetadataEnricher.Enrich(status, _dbPath, _dbPathExplicit, requestToken);
            status.DbFileMode = DbContext.GetUnixFileModeString(
                _dbPath,
                status.DatabasePermissionPolicy,
                out var databasePermissionDiagnostic);
            if (databasePermissionDiagnostic != null)
            {
                status.DatabasePermissionDiagnostics ??= [];
                status.DatabasePermissionDiagnostics.Add(databasePermissionDiagnostic);
            }
            var macProfile = MacProfileDetector.DetectCurrentWithDiagnostics();
            status.MacProfile = macProfile.Profile;
            if (macProfile.Diagnostics.Count > 0)
                status.MacProfileDiagnostics = macProfile.Diagnostics.ToList();
            if (checkWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(
                    reader,
                    status.ProjectRoot,
                    requestToken,
                    internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(_dbPath));
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = staleAfterSeconds;
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages(status.ProjectRoot).OrderBy(l => l).ToList();
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot(status.ProjectRoot);
            status.GitExecutable = GitHelper.GetGitExecutableStatus();
            status.GitHubCliExecutable = GitHubCliExecutableResolver.GetStatus();
            var postExtractionHookSnapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();
            var postExtractionHooks = postExtractionHookSnapshot.Hooks;
            if (postExtractionHookSnapshot.Diagnostics.Count > 0)
                status.HookDiagnostics = postExtractionHookSnapshot.Diagnostics.ToList();
            var trustOverrides = ExtractorPluginRegistry.GetAcceptedTrustOverrides(status.ProjectRoot)
                .Concat(postExtractionHookSnapshot.TrustOverrides)
                .Concat(GitHelper.GetAcceptedTrustOverrides(status.GitExecutable))
                .Concat(GitHubCliExecutableResolver.GetAcceptedTrustOverrides(status.GitHubCliExecutable))
                .ToList();
            if (trustOverrides.Count > 0)
                status.TrustOverrides = trustOverrides;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
                        Id = hook.Id,
                        Name = hook.Name,
                        AssemblyPath = hook.AssemblyPath,
                        TypeName = hook.TypeName,
                        CallbackBudgetMs = (long)Math.Round(postExtractionHookSnapshot.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero),
                        LoadContextLifecycle = PostExtractionHookRunner.HookLoadContextLifecycle,
                    })
                    .ToList();
            }
            status.Version = _version;
            requestToken.ThrowIfCancellationRequested();
            status.UpdateCheck = runUpdateCheck
                ? (StatusUpdateCheckForTesting ?? UpdateChecker.Check)(_version, requestToken)
                : null;
            if (!status.FoldReady)
            {
                status.DegradedReason = DegradationReasonCodes.BuildFoldNotReadyExplanation(status.FoldReadyReason);
                status.RecommendedAction = BuildFoldBackfillCommand(_dbPath, _dbPathExplicit);
                status.AlternativeAction = BuildFoldRebuildRepairCommand(status.ProjectRoot, _dbPath, _dbPathExplicit);
            }
            status.Summary = QueryCommandRunner.BuildStatusSummary(status);
            var checkFailures = checkWorkspace
                ? BuildMcpStatusCheckFailures(status, statusScopes)
                : [];
            if (checkWorkspace)
                status.FailedChecks = checkFailures.Select(failure => failure.Name).ToList();

            var structured = JsonSerializer.SerializeToNode(status, _jsonOptions)!.AsObject();
            structured["project_root"] = status.ProjectRoot;
            structured["git_head"] = status.GitHead;
            structured["git_is_dirty"] = status.GitIsDirty;
            structured.Remove("hotspotFamilyReady");
            structured.Remove("hotspotFamilyDegradedReason");
            structured["sql_graph_contract_ready"] = status.SqlGraphContractReady;
            if (status.SqlGraphContractDegradedReason != null)
                structured["sql_graph_contract_degraded_reason"] = status.SqlGraphContractDegradedReason;
            structured["mcp_session"] = BuildMcpSessionStatus();
            var rateLimitDiagnostics = RateLimiter.SnapshotDiagnostics();
            structured["mcp"] = new JsonObject
            {
                ["limits"] = new JsonObject
                {
                    ["max_request_characters"] = MaxLineCharacterCount,
                    ["max_request_bytes"] = MaxLineByteLength,
                    ["max_response_bytes"] = GetMaxResponseBytes(),
                    ["max_configured_response_bytes"] = MaxConfiguredResponseBytes,
                    ["batch_response_bytes"] = GetBatchQueryResponseByteLimit(),
                    ["max_batch_response_bytes"] = MaxBatchQueryResponseByteLimit,
                    ["batch_query_response_bytes"] = GetBatchQueryResponseByteLimit(),
                    ["batch_query_max_response_bytes"] = MaxBatchQueryResponseByteLimit,
                    ["batch_query_max_queries"] = MaxBatchQuerySize,
                    ["max_pagination_offset"] = MaxMcpPaginationOffset,
                    ["max_query_cursor_characters"] = MaxMcpQueryCursorCharacters,
                    ["max_json_depth"] = MaxJsonDepth,
                    ["max_batch_requests"] = MaxBatchRequestCount,
                    ["json_rpc_batch_max_requests"] = MaxBatchRequestCount,
                    ["keep_alive_min_interval_s"] = MinKeepAliveIntervalSeconds,
                    ["keep_alive_max_interval_s"] = MaxKeepAliveIntervalSeconds,
                    ["rate_limit_max_rps"] = RateLimiterOptions.MaxRefillTokensPerSecond,
                    ["rate_limit_max_burst"] = RateLimiterOptions.MaxBurstCapacity,
                    ["rate_limit_max_buckets"] = RateLimiterOptions.DefaultMaxBucketCount,
                },
                ["rate_limit"] = new JsonObject
                {
                    ["enabled"] = RateLimiter.Options.IsEnabled,
                    ["rps"] = RateLimiter.Options.RefillTokensPerSecond,
                    ["burst"] = RateLimiter.Options.BurstCapacity,
                    ["bucket_count"] = rateLimitDiagnostics.BucketCount,
                    ["bucket_limit"] = rateLimitDiagnostics.MaxBucketCount,
                    ["bucket_limit_rejection_count"] = rateLimitDiagnostics.BucketLimitRejectionCount,
                    ["bucket_idle_ttl_seconds"] = rateLimitDiagnostics.BucketIdleTtlSeconds,
                    ["next_prune_in_ms"] = rateLimitDiagnostics.NextPruneInMs,
                    ["last_prune_age_ms"] = rateLimitDiagnostics.LastPruneAgeMs.HasValue ? JsonValue.Create(rateLimitDiagnostics.LastPruneAgeMs.Value) : null,
                    ["last_pruned_bucket_count"] = rateLimitDiagnostics.LastPrunedBucketCount,
                },
                ["request_timeouts"] = BuildRequestTimeoutDiagnosticsStatus(),
            };
            var effectiveConfig = includeConfig
                ? BuildMcpStatusEffectiveConfig(status, staleAfterSeconds, checkWorkspace, runUpdateCheck)
                : null;
            var logPath = includeLogPath ? GlobalToolLog.ResolveLogDirectoryForStatus() : null;
            var explainPayload = explain is null
                ? null
                : BuildMcpStatusExplain(status, checkFailures, explain);
            if (effectiveConfig is not null)
                structured["effective_config"] = effectiveConfig.DeepClone();
            if (logPath is not null)
                structured["log_path"] = logPath;
            if (explainPayload is not null)
                structured["explain"] = explainPayload.DeepClone();
            if (format == "compact")
            {
                structured = BuildMcpCompactStatusPayload(status, checkFailures);
                if (effectiveConfig is not null)
                    structured["effective_config"] = effectiveConfig;
                if (logPath is not null)
                    structured["log_path"] = logPath;
                if (explainPayload is not null)
                    structured["explain"] = explainPayload;
            }
            if (projectionFields is not null)
            {
                EnrichToolStructuredContent(structured);
                var projected = new JsonObject();
                foreach (var field in projectionFields)
                {
                    if (!structured.TryGetPropertyValue(field, out var value))
                    {
                        unavailableProjectionError =
                            $"Status field '{field}' is not available in {format} format. Use an exact top-level field name returned by that format.";
                        return new JsonObject();
                    }
                    projected[field] = value?.DeepClone();
                }
                if (!projected.ContainsKey("api_version"))
                    projected["api_version"] = structured["api_version"]!.DeepClone();
                if (!projected.ContainsKey("tool"))
                    projected["tool"] = structured["tool"]!.DeepClone();
                structured = projected;
            }
            return CreateToolResult(
                id,
                "Database stats returned.",
                structured,
                enrichStructuredContent: projectionFields is null);
        });
        return unavailableProjectionError is null
            ? response
            : CreateToolErrorResponse(id, unavailableProjectionError);
    }

    private sealed record McpStatusCheckFailure(string Name, bool IsStale, string Diagnostic);

    private static bool TryReadStatusProjectionFields(
        JsonNode? args,
        out IReadOnlyList<string>? fields,
        out string? error)
    {
        fields = null;
        error = null;
        if (args is not JsonObject argsObject || !argsObject.ContainsKey("fields"))
            return true;

        var node = argsObject["fields"];
        if (node is null)
        {
            error = "fields must be a non-empty string or string array.";
            return false;
        }
        IEnumerable<JsonNode?> values = node is JsonArray array ? array : new JsonNode?[] { node };
        if (node is JsonArray fieldsArray
            && (fieldsArray.Count == 0 || fieldsArray.Count > MaxStatusProjectionFields))
        {
            error = $"fields must contain between 1 and {MaxStatusProjectionFields} entries.";
            return false;
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalCharacters = 0;
        foreach (var value in values)
        {
            if (value is not JsonValue jsonValue
                || !jsonValue.TryGetValue<string>(out var field)
                || string.IsNullOrWhiteSpace(field))
            {
                error = "fields entries must be non-empty strings.";
                return false;
            }

            field = field.Trim();
            if (field.Length > MaxStatusProjectionFieldCharacters)
            {
                error = $"fields entries must be no longer than {MaxStatusProjectionFieldCharacters} characters.";
                return false;
            }
            if (field.Contains('.', StringComparison.Ordinal)
                || field.Contains('[', StringComparison.Ordinal)
                || field.Contains(']', StringComparison.Ordinal))
            {
                error = "fields supports exact top-level field names only; nested field paths are not supported.";
                return false;
            }

            totalCharacters += field.Length;
            if (totalCharacters > MaxStatusProjectionCharacters)
            {
                error = $"fields must contain no more than {MaxStatusProjectionCharacters} characters in total.";
                return false;
            }
            if (seen.Add(field))
                result.Add(field);
        }

        fields = result;
        return true;
    }

    private static bool TryReadStatusScopes(JsonNode? args, out HashSet<string>? scopes, out string? error)
    {
        scopes = null;
        error = null;
        if (args?["scopes"] is null)
            return true;

        var values = ReadStringOrArrayList(args, "scopes")
            .Select(scope => scope.Trim().ToLowerInvariant())
            .ToList();
        if (args["scopes"] is JsonArray array && values.Count != array.Count)
        {
            error = "scopes entries must be non-empty strings.";
            return false;
        }
        if (values.Count == 0)
        {
            error = "scopes cannot be empty or whitespace-only.";
            return false;
        }

        scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsKnownMcpStatusScope(value))
            {
                error = $"Invalid status scope '{value}'. Use one of: workspace, graph, issues, sql, hotspot, csharp, fold, newer.";
                return false;
            }
            scopes.Add(value);
        }
        return true;
    }

    private static bool IsKnownMcpStatusScope(string scope) =>
        scope is "workspace" or "graph" or "issues" or "sql" or "hotspot" or "csharp" or "fold" or "newer";

    private static IReadOnlyList<McpStatusCheckFailure> BuildMcpStatusCheckFailures(StatusResult status, IReadOnlySet<string>? scopes)
    {
        var failures = new List<McpStatusCheckFailure>();
        var checkAll = scopes is not { Count: > 0 };
        bool Includes(string scope) => checkAll || scopes!.Contains(scope);

        if (Includes("workspace"))
        {
            if (status.WorkspaceCheck?.Checked != true)
            {
                failures.Add(new McpStatusCheckFailure("workspace_unavailable", true, "[stale] workspace_check unavailable"));
            }
            else if (!status.WorkspaceCheck.MatchesWorkspace)
            {
                var check = status.WorkspaceCheck;
                failures.Add(new McpStatusCheckFailure(
                    "workspace_stale",
                    true,
                    $"[stale] workspace_check reason={check.Reason} changed={check.ChangedFileCount} missing={check.MissingFileCount} unindexed={check.UnindexedFileCount}"));
            }
        }

        if (Includes("graph") && !status.GraphTableAvailable)
            failures.Add(new McpStatusCheckFailure("graph_table_available", false, "[degraded] graph_table_available=false"));
        if (Includes("issues") && !status.IssuesTableAvailable)
            failures.Add(new McpStatusCheckFailure("issues_table_available", false, "[degraded] issues_table_available=false"));
        if (Includes("issues") && status.IssuesTableAvailable && !status.FileIssuesDataCurrent)
            failures.Add(new McpStatusCheckFailure("file_issues_data_current", false, "[degraded] file_issues_data_current=false"));
        if (Includes("workspace") && status.MigrationInProgress)
            failures.Add(new McpStatusCheckFailure("migration_in_progress", false, "[degraded] migration_in_progress=true"));
        if (Includes("sql") && !status.SqlGraphContractReady)
            failures.Add(new McpStatusCheckFailure("sql_graph_contract_ready", false, $"[degraded] sql_graph_contract_ready=false reason={status.SqlGraphContractDegradedReason ?? "unknown"}"));
        if (Includes("hotspot") && !status.HotspotFamilyReady)
            failures.Add(new McpStatusCheckFailure("hotspot_family_ready", false, $"[degraded] hotspot_family_ready=false reason={status.HotspotFamilyDegradedReason ?? "unknown"}"));
        if (Includes("csharp") && !status.CSharpSymbolNameReady)
            failures.Add(new McpStatusCheckFailure("csharp_symbol_name_ready", false, "[degraded] csharp_symbol_name_ready=false"));
        if (Includes("csharp") && !status.CSharpMetadataTargetReady)
            failures.Add(new McpStatusCheckFailure("csharp_metadata_target_ready", false, $"[degraded] csharp_metadata_target_ready=false reason={status.CSharpMetadataTargetDegradedReason ?? "unknown"}"));
        if (Includes("fold") && !status.FoldReady)
            failures.Add(new McpStatusCheckFailure("fold_ready", false, $"[degraded] fold_ready=false reason={status.FoldReadyReason ?? "unknown"}"));
        if (Includes("newer") && status.IndexNewerThanReader)
            failures.Add(new McpStatusCheckFailure("index_newer_than_reader", false, $"[degraded] index_newer_than_reader=true reason={status.IndexNewerThanReaderReason ?? "unknown"}"));

        return failures;
    }

    private JsonObject BuildMcpStatusEffectiveConfig(StatusResult status, int staleAfterSeconds, bool checkWorkspace, bool runUpdateCheck) => new()
    {
        ["db_path"] = _dbPath,
        ["db_explicit"] = _dbPathExplicit,
        ["project_root"] = status.ProjectRoot,
        ["data_dir"] = status.DataDir,
        ["data_dir_source"] = status.DataDirSource,
        ["global_tool_log_dir"] = GlobalToolLog.ResolveLogDirectoryForStatus(),
        ["stale_after_seconds"] = staleAfterSeconds,
        ["check"] = checkWorkspace,
        ["update_check_requested"] = runUpdateCheck,
        ["version"] = status.Version,
    };

    private JsonObject BuildMcpStatusExplain(StatusResult status, IReadOnlyList<McpStatusCheckFailure> failures, string explain)
    {
        var payload = new JsonObject();
        if (explain is "freshness" or "all")
        {
            payload["freshness"] = new JsonObject
            {
                ["index_matches_workspace"] = status.IndexMatchesWorkspace.HasValue ? JsonValue.Create(status.IndexMatchesWorkspace.Value) : null,
                ["stale_after_seconds"] = status.StaleAfterSeconds.HasValue ? JsonValue.Create(status.StaleAfterSeconds.Value) : null,
                ["index_age_seconds"] = status.IndexAgeSeconds.HasValue ? JsonValue.Create(status.IndexAgeSeconds.Value) : null,
                ["workspace_check"] = status.WorkspaceCheck is null ? null : JsonSerializer.SerializeToNode(status.WorkspaceCheck, _jsonOptions),
            };
        }
        if (explain is "readiness" or "all")
        {
            payload["readiness"] = BuildMcpStatusReadiness(status);
            payload["failed_check_details"] = BuildMcpStatusFailureArray(failures);
        }
        return payload;
    }

    private static JsonObject BuildMcpStatusReadiness(StatusResult status) => new()
    {
        ["graph_table_available"] = status.GraphTableAvailable,
        ["issues_table_available"] = status.IssuesTableAvailable,
        ["file_issues_data_current"] = status.FileIssuesDataCurrent,
        ["sql_graph_contract_ready"] = status.SqlGraphContractReady,
        ["hotspot_family_ready"] = status.HotspotFamilyReady,
        ["csharp_symbol_name_ready"] = status.CSharpSymbolNameReady,
        ["csharp_metadata_target_ready"] = status.CSharpMetadataTargetReady,
        ["fold_ready"] = status.FoldReady,
        ["index_newer_than_reader"] = status.IndexNewerThanReader,
        ["migration_in_progress"] = status.MigrationInProgress,
    };

    private static JsonArray BuildMcpStatusFailureArray(IReadOnlyList<McpStatusCheckFailure> failures)
    {
        var array = new JsonArray();
        foreach (var failure in failures)
        {
            array.Add(new JsonObject
            {
                ["name"] = failure.Name,
                ["is_stale"] = failure.IsStale,
                ["diagnostic"] = failure.Diagnostic,
            });
        }
        return array;
    }

    private JsonObject BuildMcpCompactStatusPayload(StatusResult status, IReadOnlyList<McpStatusCheckFailure> failures)
    {
        var payload = new JsonObject
        {
            ["format"] = "compact",
            ["summary"] = status.Summary,
            ["version"] = status.Version,
            ["project_root"] = status.ProjectRoot,
            ["files"] = status.Files,
            ["chunks"] = status.Chunks,
            ["symbols"] = status.Symbols,
            ["references"] = status.References,
            ["symbol_kinds"] = JsonSerializer.SerializeToNode(status.SymbolKinds),
            ["symbol_kind_limit"] = status.SymbolKindLimit,
            ["symbol_kind_name_limit"] = status.SymbolKindNameLimit,
            ["symbol_kind_total_count"] = status.SymbolKindTotalCount,
            ["symbol_kind_omitted_count"] = status.SymbolKindOmittedCount,
            ["symbol_kind_names_truncated"] = status.SymbolKindNamesTruncated,
            ["language_count"] = status.Languages.Count,
            ["top_languages"] = new JsonArray(status.Languages
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(5)
                .Select(kv => new JsonObject { ["lang"] = kv.Key, ["files"] = kv.Value })
                .ToArray<JsonNode?>()),
            ["git_head"] = status.GitHead,
            ["git_is_dirty"] = status.GitIsDirty.HasValue ? JsonValue.Create(status.GitIsDirty.Value) : null,
            ["index_matches_workspace"] = status.IndexMatchesWorkspace.HasValue ? JsonValue.Create(status.IndexMatchesWorkspace.Value) : null,
            ["stale_after_seconds"] = status.StaleAfterSeconds.HasValue ? JsonValue.Create(status.StaleAfterSeconds.Value) : null,
            ["index_age_seconds"] = status.IndexAgeSeconds.HasValue ? JsonValue.Create(status.IndexAgeSeconds.Value) : null,
            ["failed_checks"] = new JsonArray(failures.Select(failure => JsonValue.Create(failure.Name)).ToArray()),
            ["failed_check_details"] = BuildMcpStatusFailureArray(failures),
            ["readiness"] = BuildMcpStatusReadiness(status),
        };
        if (status.WorkspaceCheck is not null)
        {
            var workspaceCheckJsonOptions = new JsonSerializerOptions(_jsonOptions)
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            };
            var workspaceCheck = JsonSerializer.SerializeToNode(
                status.WorkspaceCheck,
                CliJsonSerializerContextFactory.Create(workspaceCheckJsonOptions).IndexFreshnessCheckResult)!.AsObject();
            payload["workspace_check"] = ProjectionFieldRegistry.ProjectCompactStatusWorkspaceCheck(workspaceCheck);
        }
        if (status.TrustOverrides is { Count: > 0 })
            payload["trust_overrides"] = JsonSerializer.SerializeToNode(status.TrustOverrides);
        if (status.GitExecutable is not null)
            payload["git_executable"] = JsonSerializer.SerializeToNode(status.GitExecutable);
        return payload;
    }

    private JsonObject BuildMcpSessionStatus()
    {
        var state = CurrentInitializeState;
        McpSessionSnapshotCapturedForTests?.Invoke();
        var roots = new JsonArray();
        foreach (var root in state.ClientRootDiagnostics)
            roots.Add(root);

        var session = new JsonObject
        {
            ["log_level"] = _mcpLogLevel,
            ["roots"] = roots,
        };
        if (state.ClientRootsTruncated)
        {
            session["roots_truncated"] = true;
            session["root_count"] = state.ClientRootCount;
            session["root_limit"] = MaxClientRootCount;
            session["root_uri_length_limit"] = MaxClientRootUriChars;
        }
        if (state.ClientName is not null || state.ClientVersion is not null)
        {
            var clientInfo = new JsonObject();
            if (state.ClientNameDisplay is not null)
            {
                clientInfo["name"] = state.ClientName;
                state.ClientNameDisplay.Value.AddMetadata(clientInfo, "name");
            }
            if (state.ClientVersionDisplay is not null)
            {
                clientInfo["version"] = state.ClientVersion;
                state.ClientVersionDisplay.Value.AddMetadata(clientInfo, "version");
            }
            session["client_info"] = clientInfo;
        }
        if (state.ClientCapabilities is not null)
        {
            session["client_capabilities_summary"] = BuildClientCapabilitiesSummary(state, state.ClientCapabilities);
            session["client_capabilities"] = state.ClientCapabilities.DeepClone();
        }
        if (state.ClientCapabilitiesTruncationReason is not null)
        {
            session["client_capabilities_truncated"] = true;
            session["client_capabilities_truncation_reason"] = state.ClientCapabilitiesTruncationReason;
            if (state.ClientCapabilitiesSerializedBytes is { } serializedBytes)
                session["client_capabilities_serialized_bytes"] = serializedBytes;
            session["client_capabilities_byte_limit"] = MaxClientCapabilitiesJsonBytes;
            session["client_capabilities_depth_limit"] = MaxClientCapabilitiesDepth;
            if (!session.ContainsKey("client_capabilities_summary"))
                session["client_capabilities_summary"] = BuildClientCapabilitiesSummary(state, state.ClientCapabilities);
        }
        if (_auditLog is not null)
            session["audit_log"] = BuildAuditLogStatus(_auditLog.SnapshotDiagnostics());
        session["metrics"] = BuildMetricsStatus(MetricsSink.SnapshotDiagnostics());
        return session;
    }

    private JsonObject BuildClientCapabilitiesSummary(InitializeSessionState state, JsonNode? capabilities)
    {
        var summary = new JsonObject
        {
            ["roots"] = state.ClientSupportsRoots,
            ["sampling"] = state.ClientSupportsSampling,
            ["truncated"] = state.ClientCapabilitiesTruncationReason is not null,
            ["truncation_reason"] = state.ClientCapabilitiesTruncationReason,
        };
        if (state.ClientCapabilitiesSerializedBytes is { } serializedBytes)
            summary["serialized_bytes"] = serializedBytes;
        if (capabilities is JsonObject obj)
        {
            summary["top_level_count"] = obj.Count;
            summary["top_level_keys"] = new JsonArray(obj
                .Select(kv => JsonValue.Create(McpBoundedText.ForDisplay(kv.Key, 64).Text))
                .Take(20)
                .ToArray<JsonNode?>());
            summary["top_level_keys_truncated"] = obj.Count > 20;
            if (obj["experimental"] is JsonObject experimental)
            {
                summary["experimental_count"] = experimental.Count;
                summary["experimental_keys"] = new JsonArray(experimental
                    .Select(kv => JsonValue.Create(McpBoundedText.ForDisplay(kv.Key, 64).Text))
                    .Take(20)
                    .ToArray<JsonNode?>());
                summary["experimental_keys_truncated"] = experimental.Count > 20;
            }
        }
        return summary;
    }

    private static bool IsAuditLogDegraded(AuditLogSink.AuditLogDiagnostics? diagnostics)
        => diagnostics is not null
            && (diagnostics.DroppedRecordCount > 0
                || diagnostics.RotationDegraded);

    private static JsonObject BuildAuditLogStatus(AuditLogSink.AuditLogDiagnostics diagnostics)
    {
        var payload = new JsonObject
        {
            ["enabled"] = true,
            ["path"] = diagnostics.Path,
            ["include_values"] = diagnostics.IncludeValues,
            ["max_bytes"] = diagnostics.MaxBytes,
            ["bytes_written"] = diagnostics.BytesWritten,
            ["disposed"] = diagnostics.Disposed,
            ["queue_capacity"] = diagnostics.QueueCapacity,
            ["queue_depth"] = diagnostics.QueueDepth,
            ["queued_record_count"] = diagnostics.QueuedRecordCount,
            ["written_record_count"] = diagnostics.WrittenRecordCount,
            ["dropped_record_count"] = diagnostics.DroppedRecordCount,
            ["queue_full_drop_count"] = diagnostics.QueueFullDropCount,
            ["serialization_failure_count"] = diagnostics.SerializationFailureCount,
            ["write_failure_count"] = diagnostics.WriteFailureCount,
            ["rotation_failure_count"] = diagnostics.RotationFailureCount,
            ["rotation_cleanup_failure_count"] = diagnostics.RotationCleanupFailureCount,
            ["rotation_degraded"] = diagnostics.RotationDegraded,
        };
        if (!string.IsNullOrWhiteSpace(diagnostics.LastDropReason))
            payload["last_drop_reason"] = diagnostics.LastDropReason;
        if (!string.IsNullOrWhiteSpace(diagnostics.LastRotationFailure))
            payload["last_rotation_failure"] = diagnostics.LastRotationFailure;
        return payload;
    }

    private static JsonObject BuildMetricsStatus(MetricsDiagnostics? diagnostics)
    {
        if (diagnostics is null)
            return new JsonObject { ["enabled"] = false };

        var payload = new JsonObject
        {
            ["enabled"] = true,
            ["path"] = diagnostics.Path,
            ["max_bytes"] = diagnostics.MaxBytes,
            ["bytes_written"] = diagnostics.BytesWritten,
            ["disposed"] = diagnostics.Disposed,
            ["degraded"] = diagnostics.Degraded,
            ["queue_capacity"] = diagnostics.QueueCapacity,
            ["queue_depth"] = diagnostics.QueueDepth,
            ["queued_event_count"] = diagnostics.QueuedEventCount,
            ["written_event_count"] = diagnostics.WrittenEventCount,
            ["dropped_event_count"] = diagnostics.DroppedEventCount,
            ["queue_full_drop_count"] = diagnostics.QueueFullDropCount,
            ["serialization_failure_count"] = diagnostics.SerializationFailureCount,
            ["write_failure_count"] = diagnostics.WriteFailureCount,
            ["rotation_failure_count"] = diagnostics.RotationFailureCount,
            ["batch_flush_count"] = diagnostics.BatchFlushCount,
            ["consecutive_failure_count"] = diagnostics.ConsecutiveFailureCount,
            ["recovery_count"] = diagnostics.RecoveryCount,
        };
        if (diagnostics.NextRetryAt is { } nextRetryAt)
            payload["next_retry_at"] = nextRetryAt.ToString("O", CultureInfo.InvariantCulture);
        if (diagnostics.LastRecoveryAt is { } lastRecoveryAt)
            payload["last_recovery_at"] = lastRecoveryAt.ToString("O", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(diagnostics.LastFailure))
            payload["last_failure"] = diagnostics.LastFailure;
        return payload;
    }

    private static string BuildFoldBackfillCommand(string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx backfill-fold";

        return $"cdidx backfill-fold --db {QuoteCommandArgument(ResolveWritableDbPathOrPlaceholder(dbPath))}";
    }

    private static string BuildFoldRebuildRepairCommand(string? projectRoot, string dbPath, bool dbPathExplicit)
    {
        if (!dbPathExplicit)
            return "cdidx index . --rebuild";

        var resolvedDbPath = ResolveWritableDbPathOrPlaceholder(dbPath);
        var targetProject = string.IsNullOrWhiteSpace(projectRoot)
            ? "<projectPath>"
            : QuoteCommandArgument(projectRoot);
        return $"cdidx index {targetProject} --db {QuoteCommandArgument(resolvedDbPath)} --rebuild";
    }

    private static string ResolveWritableDbPathOrPlaceholder(string dbPath)
        => DbPathResolver.TryResolveWritableMutationDbPath(dbPath, out var writableDbPath)
            ? writableDbPath
            : "<writable-db-path>";

    private static string QuoteCommandArgument(string value)
    {
        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            return value;

        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }

}
