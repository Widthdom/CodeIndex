using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunStatus(
        string[] cmdArgs,
        JsonSerializerOptions jsonOptions,
        string? appVersion = null,
        CancellationToken cancellationToken = default)
    {
        var checkUpdates = cmdArgs.Contains("--check-updates", StringComparer.Ordinal);
        if (checkUpdates)
            cmdArgs = cmdArgs.Where(arg => !string.Equals(arg, "--check-updates", StringComparison.Ordinal)).ToArray();
        var previewOptionError = ValidatePreviewOptions("status", cmdArgs, allowMaxLineWidth: false, allowFocusOptions: false);
        if (previewOptionError != null)
        {
            CommandErrorWriter.WriteStderr(previewOptionError);
            return CommandExitCodes.UsageError;
        }
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            allowStatusCheck: true,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("status", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("status")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "status", jsonOptions))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("status", options))
            return CommandExitCodes.UsageError;
        if (options.StatusConfig)
        {
            if (options.CheckWorkspace || options.StatusLogPath || options.StatusExplainField != null)
            {
                return CommandErrorWriter.WriteJsonOrHuman(
                    options.Json,
                    jsonOptions,
                    "status --config cannot be combined with --check, --stale-after, --log-path, or --explain.",
                    CommandExitCodes.UsageError,
                    "Run status --config by itself, or remove --config to use the other status mode.",
                    GetUsageLineOrThrow("status"),
                    CommandErrorCodes.UsageError,
                    category: "usage");
            }

            Console.WriteLine(BuildEffectiveConfigJson(options, cmdArgs, appVersion).ToJsonString(jsonOptions));
            return CommandExitCodes.Success;
        }
        if (options.RedactPaths.HasValue)
        {
            return CommandErrorWriter.WriteJsonOrHuman(
                options.Json,
                jsonOptions,
                "--redact-paths and --show-paths are only supported with status --config.",
                CommandExitCodes.UsageError,
                "Add --config to inspect effective path settings, or remove the path display option.",
                GetUsageLineOrThrow("status"),
                CommandErrorCodes.UsageError,
                category: "usage");
        }
        if (options.StatusLogPath)
        {
            if (options.CheckWorkspace)
            {
                CommandErrorWriter.WriteStderr("Error: status --log-path cannot be combined with --check or --stale-after.");
                return CommandExitCodes.UsageError;
            }

            var logPath = GlobalToolLog.ResolveLogDirectoryForStatus();
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(
                    new StatusLogPathJsonResult(logPath),
                    CliJsonSerializerContextFactory.Create(jsonOptions).StatusLogPathJsonResult));
            else
                Console.WriteLine(logPath);
            return CommandExitCodes.Success;
        }
        if (options.StatusExplainField != null)
        {
            if (options.StaleAfter.HasValue)
            {
                CommandErrorWriter.WriteStderr("Error: status --explain cannot be combined with --stale-after.");
                return CommandExitCodes.UsageError;
            }
            if (options.Json)
                return WriteStatusReadinessExplanationJson(options.StatusExplainField, jsonOptions);
            return WriteStatusReadinessExplanation(options.StatusExplainField, jsonOptions);
        }

        return WithDb(options, jsonOptions, reader =>
        {
            var staleAfter = (Value: DefaultStaleAfter, Error: (string?)null);
            if (options.CheckWorkspace || options.StaleAfter.HasValue)
            {
                staleAfter = ResolveStaleAfter(options, CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable));
                if (staleAfter.Error != null)
                {
                    CommandErrorWriter.WriteStderr(staleAfter.Error);
                    return CommandExitCodes.UsageError;
                }
            }

            var status = reader.GetStatus(includeDatabaseSizeAttribution: options.Json);
            WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit, cancellationToken);
            status.DataDir = options.DataDir;
            status.DataDirSource = options.DataDirSource;
            status.DataDirMode = DataDirectorySecurity.GetUnixModeString(GetDataDirectoryPath(options.DbPath));
            status.DbFileMode = DbContext.GetUnixFileModeString(
                options.DbPath,
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
            if (options.CheckWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(
                    reader,
                    status.ProjectRoot,
                    cancellationToken,
                    internalIndexDatabasePath: DbPathResolver.NormalizeDbPath(options.DbPath));
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = (long)Math.Round(staleAfter.Value.TotalSeconds, MidpointRounding.AwayFromZero);
                status.QueryContext = new StatusQueryContext
                {
                    CheckMode = options.StatusCheckMode ?? StatusCheckModeExplicit,
                    StaleAfterSeconds = status.StaleAfterSeconds.Value,
                };
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            // Attach runtime metadata / ランタイムメタデータを付加
            ApplyStatusSymbolKindLimits(status, reader.GetSymbolKindCounts());
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
            if (appVersion != null)
                status.Version = appVersion;
            var updateResult = checkUpdates && appVersion != null
                ? UpdateChecker.Check(appVersion, cancellationToken)
                : null;
            status.UpdateCheck = updateResult;

            // Build one-line summary for AI orientation / AI向けの1行サマリーを構築
            ApplyStatusDegradationGuidance(status, options);
            status.Summary = BuildStatusSummary(status);

            IReadOnlyList<StatusCheckFailure> checkFailures = options.CheckWorkspace
                ? BuildStatusCheckFailures(status, options.StatusCheckScopes)
                : Array.Empty<StatusCheckFailure>();
            if (options.CheckWorkspace)
            {
                status.FailedChecks = checkFailures.Select(f => f.Name).ToList();
                status.RepairCommands = BuildStatusRepairCommands(status, checkFailures, options);
            }

            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    status,
                    CliJsonSerializerContextFactory.Create(jsonOptions).StatusResult));
            }
            else if (options.CheckWorkspace)
            {
                if (options.StaleAfter.HasValue)
                    WriteStatusAge(status, staleAfter.Value);
                if (checkFailures.Count > 0)
                {
                    WriteStatusCheckDiagnostics(checkFailures);
                    if (status.WorkspaceCheck != null
                        && checkFailures.Any(failure => failure.Name == "workspace_stale"))
                        WriteWorkspaceCheckSampleDiagnostics(status.WorkspaceCheck);
                    WriteStatusRepairCommands(status.RepairCommands);
                }
            }
            else
            {
                if (status.Summary != null)
                    Console.WriteLine(status.Summary);
                Console.WriteLine();
                if (status.Version != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Version", $"cdidx v{status.Version}"));
                if (updateResult?.UpdateAvailable == true && updateResult.LatestVersion != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Update", $"cdidx v{updateResult.LatestVersion} is available."));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Files", $"{status.Files:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Chunks", $"{status.Chunks:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Symbols", $"{status.Symbols:N0}"));
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Refs", $"{status.References:N0}"));
                if (status.IndexedAt != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Indexed", $"{status.IndexedAt:O}"));
                if (status.LastWorkspaceFreshenedAt != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Freshened", $"{status.LastWorkspaceFreshenedAt:O}"));
                if (status.LatestModified != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Source", $"{status.LatestModified:O}"));
                if (status.GitHead != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git HEAD", status.GitHead));
                if (status.GitIsDirty != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git Dirty", status.GitIsDirty));
                if (status.MacProfile != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("MAC", status.MacProfile));
                // Latest-write and whole-workspace verification are separate provenance.
                // Render the verified HEAD when it differs so a drift warning never cites
                // the legacy full-scan stamp for a newer reconciled workspace.
                if (status.IndexedHeadSha != null)
                {
                    var branchSuffix = string.IsNullOrWhiteSpace(status.IndexedHeadBranch)
                        ? string.Empty
                        : $" (branch {status.IndexedHeadBranch})";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", $"{status.IndexedHeadSha}{branchSuffix}"));
                }
                var verifiedHead = status.WorkspaceVerifiedHeadSha ?? status.IndexedHeadCommit;
                if (verifiedHead != null
                    && !string.Equals(verifiedHead, status.IndexedHeadSha, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Verified", verifiedHead));
                }
                if (status.IndexedHeadTimestamp != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx Stamp", $"{status.IndexedHeadTimestamp:O}"));
                if (status.CommitsAheadOfIndexedHead is { } ahead && ahead > 0)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx Drift", $"workspace is {ConsoleUi.Counted(ahead, "commit")} ahead of indexed HEAD — rerun `cdidx index .` to refresh."));
                if (status.WorkspaceCheck != null)
                {
                    WriteStatusAge(status, staleAfter.Value);
                    WriteWorkspaceCheck(status.WorkspaceCheck);
                }
                if (status.Languages.Count > 0)
                {
                    Console.WriteLine("Languages:");
                    foreach (var (lang, count) in status.Languages)
                        Console.WriteLine($"  {lang,-12} {count,6}");
                }
                if (status.SymbolKinds is { Count: > 0 })
                {
                    Console.WriteLine("Kinds:");
                    foreach (var (kind, count) in status.SymbolKinds)
                        Console.WriteLine($"  {kind,-12} {count,6}");
                    if (status.SymbolKindOmittedCount is > 0)
                    {
                        Console.WriteLine(
                            $"  ... {ConsoleUi.Counted(status.SymbolKindOmittedCount.Value, "kind")} omitted (limit {status.SymbolKindLimit}, names capped at {status.SymbolKindNameLimit} chars)");
                    }
                    else if (status.SymbolKindNamesTruncated == true)
                    {
                        Console.WriteLine($"  ... kind names capped at {status.SymbolKindNameLimit} chars");
                    }
                }
                if (status.GraphSupportedLanguages is { Count: > 0 })
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Graph", $"{status.GraphSupportedLanguages.Count} languages ({string.Join(", ", status.GraphSupportedLanguages)})"));
                if (status.TrustOverrides is { Count: > 0 })
                {
                    foreach (var trustOverride in status.TrustOverrides)
                    {
                        var pathSuffix = string.IsNullOrWhiteSpace(trustOverride.Path)
                            ? string.Empty
                            : $" ({trustOverride.Path})";
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Trust", $"{trustOverride.Kind} via {trustOverride.EnvironmentVariable}{pathSuffix}"));
                    }
                }
                if (status.GitExecutable != null)
                {
                    var modeSuffix = status.GitExecutable.UnixMode == null
                        ? string.Empty
                        : $", mode {status.GitExecutable.UnixMode}";
                    var gitSummary = status.GitExecutable.Accepted
                        ? $"{status.GitExecutable.Source}: accepted{modeSuffix}"
                        : $"{status.GitExecutable.Source}: rejected ({status.GitExecutable.Reason}{modeSuffix})";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Git", gitSummary));
                }
                // #1546: surface the persisted filesystem case-sensitivity so operators can
                // diagnose phantom path collapses on case-sensitive APFS / WSL / ReFS volumes.
                // #1546: case-sensitivity を診断用に明示する。
                if (status.PathCaseSensitive != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("FS Case", status.PathCaseSensitive == true ? "case-sensitive" : "case-insensitive"));
                WriteStatusReadinessSummary(status, options);
                if (status.WorktreeHeadChanged == true)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"worktree HEAD changed since the workspace was verified ({ShortSha(verifiedHead)} -> {ShortSha(status.GitHead)}). Run `{BuildReindexRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to refresh the index for the current branch."));
                if (status.IndexNewerThanReader)
                {
                    var reason = status.IndexNewerThanReaderReason ?? "DB was written by a newer cdidx than this binary.";
                    var writerLabel = status.IndexWriterVersion is { Length: > 0 } writerVersion
                        ? $" (DB writer: cdidx v{writerVersion}; reader: cdidx v{status.Version ?? "unknown"})"
                        : status.Version is { Length: > 0 } readerVersion
                            ? $" (reader: cdidx v{readerVersion})"
                            : "";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"{reason}{writerLabel}"));
                }
                if (!status.IndexComplete)
                {
                    var firstFailure = status.LastFailedOrPartialIndexRun?.FileErrors?.FirstOrDefault();
                    var failureSuffix = firstFailure == null
                        ? string.Empty
                        : $" First failure: {ConsoleUi.FormatBoundedValue(firstFailure.File)} ({ConsoleUi.FormatBoundedValue(firstFailure.Category)}, {ConsoleUi.FormatBoundedValue(firstFailure.Phase)}): {ConsoleUi.FormatBoundedValue(firstFailure.Detail)}";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"index generation is incomplete; successful files and graph edges remain queryable.{failureSuffix}"));
                    var persistedRecoveryHint = status.LastFailedOrPartialIndexRun?.RecoveryHint;
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", persistedRecoveryHint == null
                        ? "fix the reported file/extractor failure, then rerun the same index command; a rebuild is not required."
                        : ConsoleUi.FormatBoundedValue(persistedRecoveryHint)));
                }
                if (!status.ReferenceGraphComplete)
                {
                    var reasons = status.ReferenceGraphIncompleteReasons is { Count: > 0 }
                        ? string.Join(", ", status.ReferenceGraphIncompleteReasons.Take(4))
                        : DbReader.ReferenceExtractionCapStateUnavailableReason;
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"reference graph is incomplete ({reasons}); absent callers/callees/deps/impact edges are not authoritative."));
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", GetReferenceGraphRepairSafetyNote(status)));
                }
                if (!status.GraphTableAvailable)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "symbol_references table missing — reference / caller / callee / unused counts are degraded to 0."));
                if (!status.IssuesTableAvailable)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "file_issues table missing — validate output is degraded to empty."));
                else if (!status.FileIssuesDataCurrent)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "file_issues table exists but its rows are not stamped current for this index generation."));
                if (!status.SqlGraphContractReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"SQL graph/dependency results may be stale. Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots."));
                if (!status.HotspotFamilyReady && status.HotspotFamilyDegradedReason != null)
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", status.HotspotFamilyDegradedReason));
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", "rerun `cdidx index <projectPath>` to restore authoritative cross-file hotspot families."));
                }
                if (!status.CSharpSymbolNameReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"C# exact-name for operators / conversion operators / indexers is degraded. Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical symbol names in place."));
                // #435: tell the user when deps / impact metadata-attribute edges fall back
                // to the legacy signature / name-suffix heuristic (impostor classes may be
                // silently promoted or demoted until the authoritative resolver is re-run).
                // #435: deps / impact の metadata-attribute edge が legacy heuristic に
                // 縮退しているときは明示する。
                if (!status.CSharpMetadataTargetReady)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", "C# deps / impact metadata-attribute edges fall back to the signature / name-suffix heuristic. Run `cdidx index .` to re-stamp authoritative is_metadata_target values."));
                // #86: tell the user when `--exact` is running on the ASCII NOCASE fallback.
                // #86: --exact が ASCII NOCASE fallback で動いているときは明示する。
                if (!status.FoldReady)
                {
                    if (IsFoldOnlyReadinessDegraded(status) && status.DegradedReason != null && status.RecommendedAction != null && status.AlternativeAction != null)
                    {
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", status.DegradedReason));
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", $"run `{status.RecommendedAction}` to restamp folded-name columns in place."));
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("Hint", $"or run `{status.AlternativeAction}` for a full rebuild."));
                    }
                    else
                    {
                        Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", BuildFoldNotReadyWarning(status.FoldReadyReason, BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit), BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit))));
                    }
                }
                var totalLangs = FileIndexer.GetDetectedLanguageNames(status.ProjectRoot).Count;
                var symbolLangs = SymbolExtractor.GetSupportedLanguages(status.ProjectRoot).Count;
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Support", $"{totalLangs} detected, {symbolLangs} with symbols, {status.GraphSupportedLanguages?.Count ?? 0} with graph"));
            }

            if (!options.CheckWorkspace)
                return CommandExitCodes.Success;
            return GetStatusCheckExitCode(checkFailures);
        }, cancellationToken: cancellationToken);
    }

    private const int MaxStatusExplainInputLength = 240;
    private const int MaxStatusExplainPathDepth = 4;
    private const int MaxStatusExplainKnownFields = 128;
    private const int MaxStatusExplainDependencies = 16;
    private const int MaxStatusExplainTextLength = 1024;

    private sealed record StatusJsonPathResolution(
        string CanonicalPath,
        JsonPropertyInfo TopLevelProperty,
        JsonPropertyInfo LeafProperty);

    private static int WriteStatusReadinessExplanation(string fieldName, JsonSerializerOptions jsonOptions)
    {
        var field = FindStatusFieldExplanation(fieldName, jsonOptions);
        if (field == null)
        {
            var safeFieldName = SanitizeStatusExplainInput(fieldName);
            CommandErrorWriter.WriteStderr($"Error: unknown status field `{safeFieldName}`.");
            CommandErrorWriter.WriteStderr($"Hint: {BuildStatusExplainCandidateHint(fieldName, jsonOptions)}");
            return CommandExitCodes.UsageError;
        }

        Console.WriteLine($"{field.Label} ({field.FieldName})");
        Console.WriteLine();
        Console.WriteLine($"Meaning: {BoundStatusExplainText(field.EffectiveMeaning)}");
        Console.WriteLine($"Source: {BoundStatusExplainText(field.EffectiveSource)}");
        Console.WriteLine($"Dependencies: {FormatStatusExplainDependencies(field.EffectiveDependencies)}");
        Console.WriteLine($"Interpretation: {BoundStatusExplainText(field.EffectiveInterpretation)}");
        Console.WriteLine($"Repair guidance: {BoundStatusExplainText(field.Remediation)}");
        Console.WriteLine();
        Console.WriteLine($"Ready: {field.ReadyText}");
        Console.WriteLine($"Degraded: {field.DegradedText}");
        Console.WriteLine($"Remediation: {field.Remediation}");
        return CommandExitCodes.Success;
    }

    private static int WriteStatusReadinessExplanationJson(string fieldName, JsonSerializerOptions jsonOptions)
    {
        var field = FindStatusFieldExplanation(fieldName, jsonOptions);
        if (field == null)
        {
            var safeFieldName = SanitizeStatusExplainInput(fieldName);
            return CommandErrorWriter.WriteJsonOrHuman(
                true,
                jsonOptions,
                $"unknown status field `{safeFieldName}`.",
                CommandExitCodes.UsageError,
                BuildStatusExplainCandidateHint(fieldName, jsonOptions),
                errorCode: CommandErrorCodes.UsageError,
                category: "usage");
        }

        var knownFieldNames = GetStatusExplainKnownFieldNames(jsonOptions, out var knownFieldsTruncated);
        var knownFields = new JsonArray();
        foreach (var knownField in knownFieldNames)
            knownFields.Add(knownField);
        var dependencies = new JsonArray();
        foreach (var dependency in field.EffectiveDependencies.Take(MaxStatusExplainDependencies))
            dependencies.Add(dependency);

        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["field"] = field.FieldName,
            ["label"] = field.Label,
            ["scope"] = field.FieldName.Contains('.') ? "member" : "top_level",
            ["meaning"] = BoundStatusExplainText(field.EffectiveMeaning),
            ["source"] = BoundStatusExplainText(field.EffectiveSource),
            ["dependencies"] = dependencies,
            ["dependencies_truncated"] = field.EffectiveDependencies.Count > MaxStatusExplainDependencies,
            ["interpretation"] = BoundStatusExplainText(field.EffectiveInterpretation),
            ["repair_guidance"] = BoundStatusExplainText(field.Remediation),
            ["ready"] = field.ReadyText,
            ["degraded"] = field.DegradedText,
            ["remediation"] = field.Remediation,
            ["redaction"] = new JsonObject
            {
                ["runtime_values_included"] = false,
                ["paths_included"] = false,
            },
            ["known_fields"] = knownFields,
            ["known_field_limit"] = MaxStatusExplainKnownFields,
            ["known_fields_truncated"] = knownFieldsTruncated,
        };
        CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
        return CommandExitCodes.Success;
    }

    private static StatusFieldExplanation? FindStatusFieldExplanation(
        string fieldName,
        JsonSerializerOptions jsonOptions)
    {
        var requestedName = fieldName.Trim();
        var labelMatch = StatusExplainFields.FirstOrDefault(
            field => string.Equals(field.Label, requestedName, StringComparison.OrdinalIgnoreCase));
        if (labelMatch != null)
            requestedName = labelMatch.FieldName;

        if (!TryResolveStatusJsonPath(requestedName, jsonOptions, out var resolution))
            return null;

        var explicitMatch = StatusExplainFields.FirstOrDefault(
            field => string.Equals(field.FieldName, resolution.CanonicalPath, StringComparison.OrdinalIgnoreCase));
        return explicitMatch ?? BuildGeneratedStatusFieldExplanation(resolution);
    }

    internal static IReadOnlyList<string> GetStatusSerializableFieldNames(JsonSerializerOptions jsonOptions)
        => CliJsonSerializerContextFactory.Create(jsonOptions)
            .StatusResult
            .Properties
            .Where(property => property.Get != null)
            .Select(property => property.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> GetStatusExplainKnownFieldNames(
        JsonSerializerOptions jsonOptions,
        out bool truncated)
    {
        var serializerFields = GetStatusSerializableFieldNames(jsonOptions);
        var result = new List<string>(Math.Min(MaxStatusExplainKnownFields, serializerFields.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fieldName in serializerFields)
        {
            if (result.Count == MaxStatusExplainKnownFields)
                break;
            result.Add(fieldName);
            seen.Add(fieldName);
        }

        foreach (var field in StatusMemberExplainFields)
        {
            if (result.Count == MaxStatusExplainKnownFields)
                break;
            if (TryResolveStatusJsonPath(field.FieldName, jsonOptions, out _)
                && seen.Add(field.FieldName))
            {
                result.Add(field.FieldName);
            }
        }

        truncated = false;
        foreach (var memberPath in EnumerateStatusExplainNestedPaths(jsonOptions))
        {
            if (!seen.Add(memberPath))
                continue;
            if (result.Count == MaxStatusExplainKnownFields)
            {
                truncated = true;
                break;
            }
            result.Add(memberPath);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateStatusExplainNestedPaths(
        JsonSerializerOptions jsonOptions,
        string? topLevelFilter = null)
    {
        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        foreach (var topLevelProperty in context.StatusResult.Properties)
        {
            if (topLevelProperty.Get == null
                || topLevelFilter != null
                   && !string.Equals(topLevelProperty.Name, topLevelFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nestedType = GetStatusExplainNestedType(topLevelProperty.PropertyType);
            if (nestedType == null)
                continue;
            foreach (var path in EnumerateStatusExplainNestedPaths(
                         context,
                         nestedType,
                         topLevelProperty.Name,
                         segmentCount: 1))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> EnumerateStatusExplainNestedPaths(
        CliJsonSerializerContext context,
        Type nestedType,
        string prefix,
        int segmentCount)
    {
        if (segmentCount >= MaxStatusExplainPathDepth)
            yield break;

        var typeInfo = GetStatusExplainTypeInfo(context, nestedType);
        if (typeInfo == null)
            yield break;

        foreach (var property in typeInfo.Properties)
        {
            if (property.Get == null)
                continue;

            var path = $"{prefix}.{property.Name}";
            yield return path;

            var childType = GetStatusExplainNestedType(property.PropertyType);
            if (childType == null)
                continue;
            foreach (var childPath in EnumerateStatusExplainNestedPaths(
                         context,
                         childType,
                         path,
                         segmentCount + 1))
            {
                yield return childPath;
            }
        }
    }

    private static bool TryResolveStatusJsonPath(
        string fieldName,
        JsonSerializerOptions jsonOptions,
        out StatusJsonPathResolution resolution)
    {
        resolution = null!;
        if (string.IsNullOrWhiteSpace(fieldName) || fieldName.Length > MaxStatusExplainInputLength)
            return false;

        var segments = fieldName.Split('.', StringSplitOptions.None);
        if (segments.Length == 0
            || segments.Length > MaxStatusExplainPathDepth
            || segments.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var context = CliJsonSerializerContextFactory.Create(jsonOptions);
        JsonTypeInfo? typeInfo = context.StatusResult;
        JsonPropertyInfo? topLevelProperty = null;
        JsonPropertyInfo? leafProperty = null;
        var canonicalSegments = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (typeInfo == null)
                return false;

            leafProperty = typeInfo.Properties.FirstOrDefault(
                property => property.Get != null
                            && string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (leafProperty == null)
                return false;

            topLevelProperty ??= leafProperty;
            canonicalSegments.Add(leafProperty.Name);
            var nestedType = GetStatusExplainNestedType(leafProperty.PropertyType);
            typeInfo = nestedType == null ? null : GetStatusExplainTypeInfo(context, nestedType);
        }

        resolution = new StatusJsonPathResolution(
            string.Join('.', canonicalSegments),
            topLevelProperty!,
            leafProperty!);
        return true;
    }

    private static Type? GetStatusExplainNestedType(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(string))
            return null;
        if (type.IsArray)
            return type.GetElementType();
        if (!type.IsGenericType)
            return type.IsClass ? type : null;

        var genericDefinition = type.GetGenericTypeDefinition();
        var genericArguments = type.GetGenericArguments();
        if (genericDefinition == typeof(Dictionary<,>)
            || genericDefinition == typeof(IReadOnlyDictionary<,>)
            || genericDefinition == typeof(IDictionary<,>))
        {
            return genericArguments[1];
        }

        if (genericDefinition == typeof(List<>)
            || genericDefinition == typeof(IReadOnlyList<>)
            || genericDefinition == typeof(IList<>)
            || genericDefinition == typeof(IEnumerable<>))
        {
            return genericArguments[0];
        }

        return type.IsClass ? type : null;
    }

    private static JsonTypeInfo? GetStatusExplainTypeInfo(
        CliJsonSerializerContext context,
        Type type)
    {
        try
        {
            return context.GetTypeInfo(type);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static StatusFieldExplanation BuildGeneratedStatusFieldExplanation(
        StatusJsonPathResolution resolution)
    {
        var topLevelName = resolution.TopLevelProperty.Name;
        var topLevelExplanation = StatusExplainFields.FirstOrDefault(
            field => string.Equals(field.FieldName, topLevelName, StringComparison.OrdinalIgnoreCase));
        var isNested = resolution.CanonicalPath.Contains('.');
        if (!isNested)
        {
            var label = FormatStatusExplainLabel(resolution.CanonicalPath);
            return new StatusFieldExplanation(
                resolution.CanonicalPath,
                label,
                "the field is serialized from the current status snapshot according to its documented JSON type and omission rules.",
                "an absent nullable field means the value was unavailable, not requested, or unsupported by the current database/platform.",
                "Inspect related readiness/degradation fields and rerun `cdidx status --check --json` when freshness or repair guidance is needed.",
                Meaning: $"Top-level `{resolution.CanonicalPath}` field in the source-generated status JSON contract.",
                Source: "The source-generated `StatusResult` serializer registry and the status reader/runtime enrichers.",
                Dependencies: [],
                Interpretation: $"Interpret `{resolution.CanonicalPath}` according to its serialized type and alongside the status summary/readiness fields.");
        }

        var parent = topLevelExplanation ?? BuildGeneratedStatusFieldExplanation(
            new StatusJsonPathResolution(
                topLevelName,
                resolution.TopLevelProperty,
                resolution.TopLevelProperty));
        return new StatusFieldExplanation(
            resolution.CanonicalPath,
            $"{parent.Label}: {FormatStatusExplainLabel(resolution.LeafProperty.Name)}",
            $"the `{resolution.LeafProperty.Name}` member is present in the serialized `{topLevelName}` section.",
            $"an omitted nullable member is unavailable or not applicable within `{topLevelName}`.",
            parent.Remediation,
            Meaning: $"The `{resolution.LeafProperty.Name}` member of the `{topLevelName}` status section.",
            Source: parent.EffectiveSource,
            Dependencies: [topLevelName],
            Interpretation: $"Interpret this member in the context of `{topLevelName}` and its sibling state/count/truncation fields.");
    }

    private static string FormatStatusExplainLabel(string fieldName)
    {
        var words = fieldName.Replace('_', ' ');
        return words.Length == 0
            ? "Status field"
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string BuildStatusExplainCandidateHint(
        string fieldName,
        JsonSerializerOptions jsonOptions)
    {
        var knownFields = GetStatusExplainKnownFieldNames(jsonOptions, out var truncated);
        var requestedTopLevel = fieldName.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        IReadOnlyList<string> candidates = knownFields;
        var candidatesTruncated = truncated;
        if (requestedTopLevel != null
            && TryResolveStatusJsonPath(requestedTopLevel, jsonOptions, out _))
        {
            var memberCandidates = EnumerateStatusExplainNestedPaths(jsonOptions, requestedTopLevel)
                .Take(MaxStatusExplainKnownFields + 1)
                .ToArray();
            if (memberCandidates.Length > 0)
            {
                candidatesTruncated = memberCandidates.Length > MaxStatusExplainKnownFields;
                candidates = memberCandidates.Take(MaxStatusExplainKnownFields).ToArray();
            }
        }

        var suffix = candidatesTruncated ? $" (first {MaxStatusExplainKnownFields} candidates)." : ".";
        return $"use one of: {string.Join(", ", candidates)}{suffix}";
    }

    private static string SanitizeStatusExplainInput(string value)
        => DiagnosticRedactor.RedactSensitiveText(
            DiagnosticSanitizer.ForMessage(value, MaxStatusExplainInputLength),
            redactPaths: true);

    private static string BoundStatusExplainText(string value)
        => value.Length <= MaxStatusExplainTextLength
            ? value
            : value[..(MaxStatusExplainTextLength - 3)] + "...";

    private static string FormatStatusExplainDependencies(IReadOnlyList<string> dependencies)
    {
        if (dependencies.Count == 0)
            return "none";

        var suffix = dependencies.Count > MaxStatusExplainDependencies
            ? $" (first {MaxStatusExplainDependencies})"
            : string.Empty;
        return string.Join(", ", dependencies.Take(MaxStatusExplainDependencies)) + suffix;
    }

    private static void WriteStatusReadinessSummary(StatusResult status, QueryCommandOptions options)
    {
        Console.WriteLine("Readiness:");
        foreach (var field in StatusReadinessFields)
        {
            var degraded = IsStatusReadinessFieldDegraded(status, field.FieldName);
            var state = degraded ? "degraded" : "ready";
            Console.WriteLine($"  {field.Label,-32} {state}");

            if (degraded)
            {
                Console.WriteLine($"    {BuildStatusReadinessDegradedDetail(status, options, field.FieldName, field.DegradedText)}");
                Console.WriteLine($"    {BuildStatusReadinessRemediation(status, options, field.FieldName, field.Remediation)}");
            }
        }
    }

    private static bool IsStatusReadinessFieldDegraded(StatusResult status, string fieldName)
        => fieldName switch
        {
            "graph_table_available" => !status.GraphTableAvailable,
            "graph_data_current" => !status.GraphDataCurrent,
            "reference_graph_complete" => !status.ReferenceGraphComplete,
            "index_complete" => !status.IndexComplete,
            "issues_table_available" => !status.IssuesTableAvailable,
            "file_issues_data_current" => !status.FileIssuesDataCurrent,
            "migration_in_progress" => status.MigrationInProgress,
            "sql_graph_contract_ready" => !status.SqlGraphContractReady,
            "hotspot_family_ready" => !status.HotspotFamilyReady,
            "csharp_symbol_name_ready" => !status.CSharpSymbolNameReady,
            "csharp_metadata_target_ready" => !status.CSharpMetadataTargetReady,
            "fold_ready" => !status.FoldReady,
            "index_newer_than_reader" => status.IndexNewerThanReader,
            _ => false,
        };

    private static string BuildStatusReadinessDegradedDetail(StatusResult status, QueryCommandOptions options, string fieldName, string fallback)
        => fieldName switch
        {
            "sql_graph_contract_ready" => status.SqlGraphContractDegradedReason ?? fallback,
            "hotspot_family_ready" => status.HotspotFamilyDegradedReason ?? fallback,
            "fold_ready" => BuildFoldNotReadyExplanation(status.FoldReadyReason),
            "index_newer_than_reader" => status.IndexNewerThanReaderReason ?? fallback,
            "graph_table_available" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.GraphTableMissing).HumanText,
            "graph_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.GraphDataNotCurrent).HumanText,
            "reference_graph_complete" => DegradationReasonCodes.GetMetadata(GetReferenceGraphDegradationRootCause(status)).HumanText,
            "index_complete" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.IndexIncomplete).HumanText,
            "issues_table_available" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.IssuesTableMissing).HumanText,
            "file_issues_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.FileIssuesDataStale).HumanText,
            "migration_in_progress" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.MigrationInProgress).HumanText,
            "csharp_symbol_name_ready" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.CSharpSymbolNameNotReady).HumanText,
            "csharp_metadata_target_ready" => DegradationReasonCodes.GetMetadata(status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady).HumanText,
            _ => fallback,
        };

    private static string BuildStatusReadinessRemediation(StatusResult status, QueryCommandOptions options, string fieldName, string fallback)
        => fieldName switch
        {
            "sql_graph_contract_ready" => $"Run `{BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` before trusting SQL references/callers/deps/unused/hotspots.",
            "hotspot_family_ready" => $"Run `{BuildHotspotFamilyRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to restamp authoritative hotspot families for every indexed row.",
            "csharp_symbol_name_ready" => $"Run `{BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to upgrade canonical C# symbol names in place.",
            "fold_ready" => $"Run `{BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit)}` to restamp folded-name columns in place, or `{BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` for a full rebuild.",
            "csharp_metadata_target_ready" => DegradationReasonCodes.GetMetadata(status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady).RecommendedAction,
            "file_issues_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.FileIssuesDataStale).RecommendedAction,
            "migration_in_progress" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.MigrationInProgress).RecommendedAction,
            "index_newer_than_reader" => "Run status with a current cdidx binary, or rebuild the DB with the version you intend to use.",
            "graph_data_current" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.GraphDataNotCurrent).RecommendedAction,
            "reference_graph_complete" => DegradationReasonCodes.GetMetadata(GetReferenceGraphDegradationRootCause(status)).RecommendedAction,
            "index_complete" => DegradationReasonCodes.GetMetadata(DegradationReasonCodes.IndexIncomplete).RecommendedAction,
            _ => fallback,
        };

    private static void ApplyStatusDegradationGuidance(StatusResult status, QueryCommandOptions options)
    {
        var degradations = BuildStatusReadinessDegradations(status, options);
        if (degradations.Count == 0)
            return;

        status.ReadinessDegradations = degradations;
        var primary = degradations[0];
        status.DegradedRootCause = primary.RootCause;
        status.DegradedReason = primary.DegradedReason;
        status.RecommendedAction = primary.RecommendedAction;
        status.AlternativeAction = primary.AlternativeAction;
    }

    private static List<StatusReadinessDegradation> BuildStatusReadinessDegradations(StatusResult status, QueryCommandOptions options)
    {
        var result = new List<StatusReadinessDegradation>();
        if (status.MigrationInProgress)
            result.Add(BuildStatusReadinessDegradation("migration_in_progress", DegradationReasonCodes.MigrationInProgress, options, status));
        if (!status.IndexComplete)
            result.Add(BuildStatusReadinessDegradation("index_complete", DegradationReasonCodes.IndexIncomplete, options, status));
        if (!status.GraphTableAvailable)
            result.Add(BuildStatusReadinessDegradation("graph_table_available", DegradationReasonCodes.GraphTableMissing, options, status));
        if (!status.ReferenceGraphComplete)
            result.Add(BuildStatusReadinessDegradation("reference_graph_complete", GetReferenceGraphDegradationRootCause(status), options, status));
        if (!status.GraphDataCurrent && status.IndexComplete && status.ReferenceGraphComplete)
            result.Add(BuildStatusReadinessDegradation("graph_data_current", DegradationReasonCodes.GraphDataNotCurrent, options, status));
        if (!status.IssuesTableAvailable)
            result.Add(BuildStatusReadinessDegradation("issues_table_available", DegradationReasonCodes.IssuesTableMissing, options, status));
        else if (!status.FileIssuesDataCurrent)
            result.Add(BuildStatusReadinessDegradation("file_issues_data_current", DegradationReasonCodes.FileIssuesDataStale, options, status));
        if (!status.SqlGraphContractReady)
            result.Add(BuildStatusReadinessDegradation("sql_graph_contract_ready", DegradationReasonCodes.SqlGraphContractNotReady, options, status));
        if (!status.HotspotFamilyReady)
            result.Add(BuildStatusReadinessDegradation("hotspot_family_ready", DegradationReasonCodes.HotspotFamilyNotReady, options, status));
        if (!status.CSharpSymbolNameReady)
            result.Add(BuildStatusReadinessDegradation("csharp_symbol_name_ready", DegradationReasonCodes.CSharpSymbolNameNotReady, options, status));
        if (!status.CSharpMetadataTargetReady)
            result.Add(BuildStatusReadinessDegradation("csharp_metadata_target_ready", status.CSharpMetadataTargetDegradedReason ?? DegradationReasonCodes.CSharpMetadataTargetNotReady, options, status));
        if (!status.FoldReady)
            result.Add(BuildStatusReadinessDegradation("fold_ready", DegradationReasonCodes.NormalizeFoldReason(status.FoldReadyReason), options, status));
        if (status.IndexNewerThanReader)
            result.Add(BuildStatusReadinessDegradation("index_newer_than_reader", DegradationReasonCodes.IndexNewerThanReader, options, status));
        return result;
    }

    private static string GetReferenceGraphDegradationRootCause(StatusResult status)
    {
        if (status.ReferenceGraphIncompleteReasons?.Contains(
                DbReader.DynamicReferenceGraphContractStaleReason,
                StringComparer.Ordinal) == true)
        {
            return DegradationReasonCodes.DynamicReferenceGraphContractStale;
        }

        var reasons = status.ReferenceGraphIncompleteReasons ?? [];
        if (reasons.Contains(
                DbReader.ReferenceExtractionCapStateUnavailableReason,
                StringComparer.Ordinal))
        {
            return DegradationReasonCodes.ReferenceExtractionCapStateUnavailable;
        }
        if (reasons.Contains(
                DbReader.SymbolsOnlyReferenceGraphIncompleteReason,
                StringComparer.Ordinal))
        {
            return DegradationReasonCodes.SymbolsOnlyGraphOmitted;
        }
        if (reasons.Contains(DegradationReasonCodes.GraphTableMissing, StringComparer.Ordinal))
            return DegradationReasonCodes.GraphTableMissing;
        if (reasons.Any(ReferenceExtractor.IsSafetyCapDiagnosticKind))
            return DegradationReasonCodes.ReferenceGraphIncomplete;
        return !status.IndexComplete
            ? DegradationReasonCodes.IndexIncomplete
            : DegradationReasonCodes.ReferenceGraphIncomplete;
    }

    private static StatusReadinessDegradation BuildStatusReadinessDegradation(string field, string rootCause, QueryCommandOptions options, StatusResult status)
    {
        var metadata = DegradationReasonCodes.GetMetadata(rootCause);
        return new StatusReadinessDegradation
        {
            Field = field,
            RootCause = metadata.Code,
            DegradedReason = metadata.HumanText,
            RecommendedAction = field switch
            {
                "fold_ready" => BuildFoldBackfillCommand(options.DbPath, options.DbPathExplicit),
                "sql_graph_contract_ready" => BuildSqlGraphContractRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                "hotspot_family_ready" => BuildHotspotFamilyRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                "csharp_symbol_name_ready" => BuildCSharpCanonicalNameRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit),
                _ => metadata.RecommendedAction,
            },
            AlternativeAction = field == "fold_ready"
                ? BuildFoldRebuildRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)
                : metadata.AlternativeAction,
        };
    }

    private static bool IsStatusDegraded(StatusResult status)
        => !status.GraphTableAvailable
           || !status.GraphDataCurrent
           || !status.ReferenceGraphComplete
           || !status.IssuesTableAvailable
           || !status.FileIssuesDataCurrent
           || !status.SqlGraphContractReady
           || !status.HotspotFamilyReady
           || !status.CSharpSymbolNameReady
           || !status.CSharpMetadataTargetReady
           || !status.FoldReady
           || status.IndexNewerThanReader
           || status.MigrationInProgress
           || !status.IndexComplete;

    private sealed record StatusCheckFailure(string Name, bool IsStale, string Diagnostic);

    private static IReadOnlyList<StatusCheckFailure> BuildStatusCheckFailures(StatusResult status, IReadOnlySet<string>? scopedChecks)
    {
        var failures = new List<StatusCheckFailure>();
        var checkAll = scopedChecks is not { Count: > 0 };
        bool Includes(string scope) => checkAll || scopedChecks!.Contains(scope);

        if (Includes("workspace") && !status.IndexComplete)
            failures.Add(new StatusCheckFailure("index_complete", false, "[degraded] index_complete=false; fix the persisted per-file failure before rerunning index"));

        if (Includes("workspace"))
        {
            if (status.WorkspaceCheck?.Checked != true)
            {
                failures.Add(new StatusCheckFailure("workspace_unavailable", true, "[stale] workspace_check unavailable"));
            }
            else if (!status.WorkspaceCheck.MatchesWorkspace)
            {
                var check = status.WorkspaceCheck;
                failures.Add(new StatusCheckFailure(
                    "workspace_stale",
                    true,
                    $"[stale] workspace_check reason={check.Reason} changed={check.ChangedFileCount} missing={check.MissingFileCount} unindexed={check.UnindexedFileCount}"));
            }
        }

        if (Includes("graph") && !status.GraphTableAvailable)
            failures.Add(new StatusCheckFailure("graph_table_available", false, "[degraded] graph_table_available=false"));
        if (Includes("graph") && !status.ReferenceGraphComplete)
            failures.Add(new StatusCheckFailure("reference_graph_complete", false, $"[degraded] reference_graph_complete=false reasons={string.Join(',', status.ReferenceGraphIncompleteReasons ?? [])}"));
        if (Includes("issues") && !status.IssuesTableAvailable)
            failures.Add(new StatusCheckFailure("issues_table_available", false, "[degraded] issues_table_available=false"));
        if (Includes("issues") && status.IssuesTableAvailable && !status.FileIssuesDataCurrent)
            failures.Add(new StatusCheckFailure("file_issues_data_current", false, "[degraded] file_issues_data_current=false"));
        if (Includes("workspace") && status.MigrationInProgress)
            failures.Add(new StatusCheckFailure("migration_in_progress", false, "[degraded] migration_in_progress=true"));
        if (Includes("sql") && !status.SqlGraphContractReady)
            failures.Add(new StatusCheckFailure("sql_graph_contract_ready", false, $"[degraded] sql_graph_contract_ready=false reason={status.SqlGraphContractDegradedReason ?? "unknown"}"));
        if (Includes("hotspot") && !status.HotspotFamilyReady)
            failures.Add(new StatusCheckFailure("hotspot_family_ready", false, $"[degraded] hotspot_family_ready=false reason={status.HotspotFamilyDegradedReason ?? "unknown"}"));
        if (Includes("csharp") && !status.CSharpSymbolNameReady)
            failures.Add(new StatusCheckFailure("csharp_symbol_name_ready", false, "[degraded] csharp_symbol_name_ready=false"));
        if (Includes("csharp") && !status.CSharpMetadataTargetReady)
            failures.Add(new StatusCheckFailure("csharp_metadata_target_ready", false, $"[degraded] csharp_metadata_target_ready=false reason={status.CSharpMetadataTargetDegradedReason ?? "unknown"}"));
        if (Includes("fold") && !status.FoldReady)
            failures.Add(new StatusCheckFailure("fold_ready", false, $"[degraded] fold_ready=false reason={status.FoldReadyReason ?? "unknown"}"));
        if (Includes("newer") && status.IndexNewerThanReader)
            failures.Add(new StatusCheckFailure("index_newer_than_reader", false, $"[degraded] index_newer_than_reader=true reason={status.IndexNewerThanReaderReason ?? "unknown"}"));

        return failures;
    }

    private static List<StatusRepairCommand>? BuildStatusRepairCommands(
        StatusResult status,
        IReadOnlyList<StatusCheckFailure> failures,
        QueryCommandOptions options)
    {
        if (failures.Count == 0)
            return null;

        var candidates = new List<StatusRepairCommand>();
        foreach (var failure in failures)
        {
            var command = failure.Name switch
            {
                "workspace_stale" or "workspace_unavailable" => BuildIndexRepairCommand(
                    status,
                    options,
                    failure.Name,
                    rebuild: false,
                    safetyClass: "workspace_refresh",
                    "Re-runs indexing for the current workspace snapshot."),
                "index_complete" => BuildIndexRepairCommand(
                    status,
                    options,
                    failure.Name,
                    rebuild: false,
                    safetyClass: "source_error_recovery",
                    "Fix the reported file/extractor error first; successful rows remain persisted and a rebuild is not required."),
                "reference_graph_complete" => BuildIndexRepairCommand(
                    status,
                    options,
                    failure.Name,
                    rebuild: false,
                    safetyClass: "reference_graph_refresh",
                    GetReferenceGraphRepairSafetyNote(status)),
                "graph_table_available" or "issues_table_available" or "file_issues_data_current"
                    or "sql_graph_contract_ready" or "csharp_symbol_name_ready" or "csharp_metadata_target_ready"
                    => BuildIndexRepairCommand(
                        status,
                        options,
                        failure.Name,
                        rebuild: false,
                        safetyClass: "metadata_refresh",
                        "Rewrites stale or missing index metadata before query results are trusted."),
                "hotspot_family_ready" or "index_newer_than_reader" => BuildIndexRepairCommand(
                    status,
                    options,
                    failure.Name,
                    rebuild: true,
                    safetyClass: "full_rebuild",
                    "Performs a full rebuild because partial updates cannot prove every indexed row was restamped."),
                "fold_ready" => BuildBackfillFoldRepairCommand(options, failure.Name),
                "migration_in_progress" => BuildStatusCheckRepairCommand(options, failure.Name),
                _ => null,
            };
            if (command != null)
                candidates.Add(command);
        }

        var commands = DeduplicateStatusRepairCommands(candidates);
        return commands.Count == 0 ? null : commands;
    }

    private static string GetReferenceGraphRepairSafetyNote(StatusResult status)
        => GetReferenceGraphDegradationRootCause(status) switch
        {
            DegradationReasonCodes.DynamicReferenceGraphContractStale =>
                "Refresh indexing to rewrite stale dynamic-language graph rows and extractor-version stamps.",
            DegradationReasonCodes.ReferenceExtractionCapStateUnavailable =>
                "Refresh indexing to populate current per-file issue state before trusting reference-graph completeness.",
            DegradationReasonCodes.SymbolsOnlyGraphOmitted =>
                "Rerun indexing without --symbols-only to generate reference-graph rows.",
            DegradationReasonCodes.GraphTableMissing =>
                "Run normal indexing to create and stamp the reference-graph generation.",
            DegradationReasonCodes.IndexIncomplete =>
                "Inspect index_incomplete_reasons and address the reported omitted input or extraction work.",
            _ =>
                "Reduce or exclude the cap-hitting generated/pathological source before rerunning indexing.",
        };

    private static StatusRepairCommand BuildIndexRepairCommand(
        StatusResult status,
        QueryCommandOptions options,
        string reason,
        bool rebuild,
        string safetyClass,
        string safetyNote)
    {
        var args = new List<string>
        {
            "index",
            string.IsNullOrWhiteSpace(status.ProjectRoot) ? "." : status.ProjectRoot!,
        };
        if (options.DbPathExplicit)
        {
            args.Add("--db");
            args.Add(ResolveWritableDbPathOrPlaceholder(options.DbPath));
        }
        if (rebuild)
            args.Add("--rebuild");
        var symlinkPolicy = NormalizeIndexedSymlinkPolicy(status.IndexedFollowSymlinksPolicy);
        if (symlinkPolicy != null)
        {
            args.Add("--follow-symlinks");
            args.Add(symlinkPolicy);
        }

        return new StatusRepairCommand
        {
            Name = "cdidx",
            Action = "index",
            Args = args,
            Reason = reason,
            Reasons = [reason],
            MutationClass = "index_write",
            SafetyClass = safetyClass,
            SafetyNotes =
            [
                safetyNote,
                "Avoid running concurrently with another cdidx index writer for the same database.",
            ],
        };
    }

    private static string? NormalizeIndexedSymlinkPolicy(string? rawPolicy)
    {
        if (string.IsNullOrWhiteSpace(rawPolicy))
            return null;

        return rawPolicy.Trim().ToLowerInvariant() switch
        {
            "internal" => "internal",
            "all" => "all",
            _ => null,
        };
    }

    private static StatusRepairCommand BuildBackfillFoldRepairCommand(QueryCommandOptions options, string reason)
    {
        var args = new List<string> { "backfill-fold" };
        if (options.DbPathExplicit)
        {
            args.Add("--db");
            args.Add(ResolveWritableDbPathOrPlaceholder(options.DbPath));
        }

        return new StatusRepairCommand
        {
            Name = "cdidx",
            Action = "backfill_fold",
            Args = args,
            Reason = reason,
            Reasons = [reason],
            MutationClass = "database_write",
            SafetyClass = "fold_backfill",
            SafetyNotes =
            [
                "Restamps folded-name columns in place without reparsing source files.",
                "Use a full index rebuild instead if the database must be regenerated from source.",
            ],
        };
    }

    private static StatusRepairCommand BuildStatusCheckRepairCommand(QueryCommandOptions options, string reason)
    {
        var args = new List<string> { "status", "--check", "--json" };
        if (options.DbPathExplicit)
        {
            args.Add("--db");
            args.Add(options.DbPath);
        }

        return new StatusRepairCommand
        {
            Name = "cdidx",
            Action = "status_check",
            Args = args,
            Reason = reason,
            Reasons = [reason],
            MutationClass = "read_only",
            SafetyClass = "wait_for_writer",
            SafetyNotes =
            [
                "Wait for the active index or migration writer to finish before rerunning status.",
                "Do not start a second writer unless the existing writer is known to be gone.",
            ],
        };
    }

    internal static List<StatusRepairCommand> DeduplicateStatusRepairCommands(
        IEnumerable<StatusRepairCommand> candidates)
    {
        var commands = new List<StatusRepairCommand>();
        foreach (var candidate in candidates)
        {
            if (candidate.Reasons.Count == 0 && !string.IsNullOrWhiteSpace(candidate.Reason))
                candidate.Reasons.Add(candidate.Reason);

            var existing = commands.FirstOrDefault(command =>
                string.Equals(command.Name, candidate.Name, StringComparison.Ordinal)
                && string.Equals(command.Action, candidate.Action, StringComparison.Ordinal)
                && string.Equals(command.MutationClass, candidate.MutationClass, StringComparison.Ordinal)
                && string.Equals(command.SafetyClass, candidate.SafetyClass, StringComparison.Ordinal)
                && command.Args.SequenceEqual(candidate.Args, StringComparer.Ordinal)
                && command.SafetyNotes.SequenceEqual(candidate.SafetyNotes, StringComparer.Ordinal));
            if (existing == null)
            {
                commands.Add(candidate);
                continue;
            }

            foreach (var reason in candidate.Reasons)
            {
                if (!existing.Reasons.Contains(reason, StringComparer.Ordinal))
                    existing.Reasons.Add(reason);
            }
        }

        return commands;
    }

    private static void WriteStatusRepairCommands(IReadOnlyList<StatusRepairCommand>? commands)
    {
        if (commands == null)
            return;

        foreach (var command in commands)
        {
            CommandErrorWriter.WriteStderr(
                $"[repair] {RenderStatusRepairCommand(command)} "
                + $"(reasons={string.Join(',', command.Reasons)}; action={command.Action}; "
                + $"mutation={command.MutationClass}; safety={command.SafetyClass})");
        }
    }

    private static void WriteStatusCheckDiagnostics(IReadOnlyList<StatusCheckFailure> failures)
    {
        foreach (var failure in failures)
            CommandErrorWriter.WriteStderr(failure.Diagnostic);
    }

    private static void WriteWorkspaceCheckSampleDiagnostics(IndexFreshnessCheckResult check)
    {
        WriteWorkspaceCheckSampleDiagnostic(
            "changed_files",
            check.ChangedFileCount,
            check.ChangedFiles,
            check.ChangedFilesTruncated,
            check.ChangedFilesPathLimit,
            check.ChangedFilesOmittedCount);
        WriteWorkspaceCheckSampleDiagnostic(
            "missing_files",
            check.MissingFileCount,
            check.MissingFiles,
            check.MissingFilesTruncated,
            check.MissingFilesPathLimit,
            check.MissingFilesOmittedCount);
        WriteWorkspaceCheckSampleDiagnostic(
            "outside_sparse_cone_files",
            check.OutsideSparseConeFileCount,
            check.OutsideSparseConeFiles,
            check.OutsideSparseConeFilesTruncated,
            check.OutsideSparseConeFilesPathLimit,
            check.OutsideSparseConeFilesOmittedCount);
        WriteWorkspaceCheckSampleDiagnostic(
            "unindexed_files",
            check.UnindexedFileCount,
            check.UnindexedFiles,
            check.UnindexedFilesTruncated,
            check.UnindexedFilesPathLimit,
            check.UnindexedFilesOmittedCount);
        WriteWorkspaceCheckSampleDiagnostic(
            "unverifiable_files",
            check.UnverifiableFileCount,
            check.UnverifiableFiles,
            check.UnverifiableFilesTruncated,
            check.UnverifiableFilesPathLimit,
            check.UnverifiableFilesOmittedCount);
        WriteWorkspaceCheckSampleDiagnostic(
            "scan_errors",
            check.ScanErrorCount,
            check.ScanErrors,
            check.ScanErrorsTruncated,
            check.ScanErrorsPathLimit,
            check.ScanErrorsOmittedCount);
    }

    private static void WriteWorkspaceCheckSampleDiagnostic(
        string field,
        int totalCount,
        IReadOnlyList<string> samples,
        bool truncated,
        int pathLimit,
        int omittedCount)
    {
        if (totalCount <= 0)
            return;

        CommandErrorWriter.WriteStderr(
            $"[stale] workspace_check.{field} coverage={(truncated ? "sample" : "complete")} "
            + $"returned={samples.Count} total={totalCount} omitted={omittedCount} path_limit={pathLimit} "
            + $"paths=[{string.Join(", ", samples.Select(EscapeStatusRepairControlCharacters))}]");
    }

    private static int GetStatusCheckExitCode(IReadOnlyList<StatusCheckFailure> failures)
    {
        var stale = failures.Any(f => f.IsStale);
        var degraded = failures.Any(f => !f.IsStale);
        return (stale, degraded) switch
        {
            (false, false) => CommandExitCodes.Success,
            (true, false) => 1,
            (false, true) => 2,
            _ => 3,
        };
    }

    private static bool IsFoldOnlyReadinessDegraded(StatusResult status)
        => !status.FoldReady
           && status.GraphTableAvailable
           && status.IssuesTableAvailable
           && status.SqlGraphContractReady
           && status.HotspotFamilyReady
           && status.CSharpSymbolNameReady
           && status.CSharpMetadataTargetReady;

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => DegradationReasonCodes.BuildFoldNotReadyExplanation(foldReadyReason);

    private static string BuildFoldNotReadyWarning(string? foldReadyReason, string backfillCommand, string rebuildCommand)
        => $"{BuildFoldNotReadyExplanation(foldReadyReason)} Run `{backfillCommand}` to restamp folded-name columns in place, or `{rebuildCommand}` for a full rebuild.";

    private static string BuildStatusFreshnessLabel(StatusResult status)
    {
        if (status.WorkspaceCheck != null)
            return status.WorkspaceCheck.Checked
                ? (status.WorkspaceCheck.MatchesWorkspace ? "fresh" : "stale")
                : "unknown";

        if (!status.IndexedAt.HasValue || !status.LatestModified.HasValue)
            return "unknown";

        if (status.GitIsDirty == true)
            return "stale";

        return status.IndexedAt.Value >= status.LatestModified.Value ? "fresh" : "stale";
    }

    private static void WriteWorkspaceCheck(IndexFreshnessCheckResult check)
    {
        if (!check.Checked)
        {
            Console.WriteLine($"Check   : unavailable ({check.Reason})");
        }
        else if (check.MatchesWorkspace)
        {
            Console.WriteLine($"Check   : matches workspace ({check.MatchedFileCount:N0} files)");
        }
        else
        {
            Console.WriteLine($"Check   : stale ({check.Reason})");
        }

        if (check.ChangedFileCount > 0)
            Console.WriteLine($"  Changed indexed files : {check.ChangedFileCount:N0}{FormatSamples(check.ChangedFiles)}");
        if (check.MissingFileCount > 0)
            Console.WriteLine($"  Missing indexed files : {check.MissingFileCount:N0}{FormatSamples(check.MissingFiles)}");
        if (check.OutsideSparseConeFileCount > 0)
            Console.WriteLine($"  Outside sparse cone : {check.OutsideSparseConeFileCount:N0}{FormatSamples(check.OutsideSparseConeFiles)}");
        if (check.UnindexedFileCount > 0)
            Console.WriteLine($"  Unindexed workspace files : {check.UnindexedFileCount:N0}{FormatSamples(check.UnindexedFiles)}");
        if (check.UnverifiableFileCount > 0)
            Console.WriteLine($"  Unverifiable DB rows : {check.UnverifiableFileCount:N0}{FormatSamples(check.UnverifiableFiles)}");
        if (check.ScanErrorCount > 0)
            Console.WriteLine($"  Scan errors : {check.ScanErrorCount:N0}{FormatSamples(check.ScanErrors)}");
    }

    private static void WriteStatusAge(StatusResult status, TimeSpan staleAfter)
    {
        if (!status.IndexedAt.HasValue)
            return;

        var age = GetUtcNow() - status.IndexedAt.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        Console.WriteLine($"Age     : index is {FormatDuration(age)} old (threshold: {FormatDuration(staleAfter)})");
    }

    private readonly record struct LimitedStatusKindCounts(
        Dictionary<string, long> Counts,
        int TotalCount,
        int OmittedCount,
        bool NamesTruncated);

    internal static void ApplyStatusSymbolKindLimits(StatusResult status, Dictionary<string, long> symbolKinds)
    {
        var limitedSymbolKinds = LimitStatusKindCounts(symbolKinds);
        status.SymbolKinds = limitedSymbolKinds.Counts;
        if (limitedSymbolKinds.OmittedCount > 0 || limitedSymbolKinds.NamesTruncated)
        {
            status.SymbolKindLimit = MaxStatusSymbolKindEntries;
            status.SymbolKindNameLimit = MaxStatusSymbolKindNameLength;
            status.SymbolKindTotalCount = limitedSymbolKinds.TotalCount;
            status.SymbolKindOmittedCount = limitedSymbolKinds.OmittedCount;
            status.SymbolKindNamesTruncated = limitedSymbolKinds.NamesTruncated;
        }

        if (status.SymbolsByLanguage is not { Count: > 0 })
            return;

        Dictionary<string, int>? totalCounts = null;
        Dictionary<string, int>? omittedCounts = null;
        List<string>? truncatedLanguages = null;
        foreach (var (language, kinds) in status.SymbolsByLanguage.ToArray())
        {
            var limited = LimitStatusKindCounts(kinds);
            status.SymbolsByLanguage[language] = limited.Counts;
            if (limited.OmittedCount == 0 && !limited.NamesTruncated)
                continue;

            totalCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
            totalCounts[language] = limited.TotalCount;
            if (limited.OmittedCount > 0)
            {
                omittedCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                omittedCounts[language] = limited.OmittedCount;
            }

            if (limited.NamesTruncated)
            {
                truncatedLanguages ??= [];
                truncatedLanguages.Add(language);
            }
        }

        status.SymbolsByLanguageKindTotalCounts = totalCounts;
        status.SymbolsByLanguageKindOmittedCounts = omittedCounts;
        status.SymbolsByLanguageKindNamesTruncated = truncatedLanguages;
    }

    internal static string BuildStatusSummary(StatusResult status)
    {
        var topLangs = status.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key);
        var freshness = BuildStatusFreshnessLabel(status);
        var dirty = status.GitIsDirty == true ? ", dirty" : "";
        var degraded = IsStatusDegraded(status) ? ", DEGRADED" : "";
        var incomplete = status.IndexComplete ? "" : ", INCOMPLETE";
        return $"{status.Files} files, {status.Symbols} symbols, {status.References} refs across {status.Languages.Count} languages ({string.Join(", ", topLangs)}); index {freshness}{dirty}{incomplete}{degraded}";
    }

    private static LimitedStatusKindCounts LimitStatusKindCounts(IReadOnlyDictionary<string, long> counts)
    {
        var limited = new Dictionary<string, long>(StringComparer.Ordinal);
        var consumed = 0;
        var namesTruncated = false;
        foreach (var (kind, count) in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                     .Take(MaxStatusSymbolKindEntries))
        {
            consumed++;
            var displayKind = LimitStatusSymbolKindName(kind, ref namesTruncated);
            if (limited.TryGetValue(displayKind, out var existing))
                limited[displayKind] = existing + count;
            else
                limited[displayKind] = count;
        }

        return new LimitedStatusKindCounts(
            limited,
            counts.Count,
            Math.Max(0, counts.Count - consumed),
            namesTruncated);
    }

    private static string LimitStatusSymbolKindName(string kind, ref bool namesTruncated)
    {
        if (kind.Length <= MaxStatusSymbolKindNameLength)
            return kind;

        namesTruncated = true;
        return kind[..(MaxStatusSymbolKindNameLength - 3)] + "...";
    }

    private static JsonObject BuildEffectiveConfigJson(QueryCommandOptions options, string[] cmdArgs, string? appVersion)
    {
        var redactPaths = options.RedactPaths ?? true;

        JsonObject Entry<T>(T? value, string source)
        {
            var entry = new JsonObject
            {
                ["value"] = JsonSerializer.SerializeToNode(value),
                ["source"] = source,
            };
            AddEffectiveConfigSourceSummary(entry, source);
            return entry;
        }

        string? PathValue(string? value)
            => value == null
                ? null
                : redactPaths
                    ? DiagnosticSanitizer.ForSupportSafePath(value)
                    : DiagnosticSanitizer.ForPathWithSecretsRedacted(value);

        var staleAfterEnvValue = CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable);

        var payload = new JsonObject
        {
            ["api_version"] = "1",
            ["redaction"] = JsonSerializer.SerializeToNode(new DoctorRedactionJsonResult(redactPaths, true)),
            ["effective_config"] = new JsonObject
            {
                ["db_path"] = Entry(PathValue(options.DbPath), ResolveDbPathConfigSource(options)),
                ["data_dir"] = Entry(PathValue(options.DataDir), options.DataDirSource ?? "flag"),
                ["limit"] = Entry(options.Limit, ResolveNumericConfigSource(cmdArgs, "--limit", "--top", DefaultLimitEnvironmentVariable)),
                ["snippet_lines"] = Entry(options.SnippetLines, ResolveNumericConfigSource(cmdArgs, "--snippet-lines", null, DefaultSnippetLinesEnvironmentVariable)),
                ["max_line_width"] = Entry(options.MaxLineWidth, ResolveNumericConfigSource(cmdArgs, "--max-line-width", null, DefaultMaxLineWidthEnvironmentVariable)),
                ["json"] = Entry(options.Json, HasOption(cmdArgs, "--json") ? "flag" : "default"),
                ["stale_after"] = Entry(options.StaleAfter?.ToString() ?? staleAfterEnvValue, options.StaleAfter.HasValue ? "flag" : ResolveEnvSource(StaleAfterEnvironmentVariable)),
                ["global_tool_log_dir"] = Entry(PathValue(GlobalToolLog.ResolveLogDirectoryForStatus()), ResolveEnvSource("CDIDX_GLOBAL_TOOL_LOG_DIR")),
                ["version"] = Entry(appVersion ?? ConsoleUi.LoadVersion(), "build"),
            },
        };
        return payload;
    }

    private static void AddEffectiveConfigSourceSummary(JsonObject entry, string source)
    {
        var sourceKind = source;
        string? sourceDetail = null;
        if (source.StartsWith("config:", StringComparison.Ordinal))
        {
            sourceKind = "config_file";
            sourceDetail = Path.GetFileName(source["config:".Length..]);
        }
        else if (source.StartsWith("env:", StringComparison.Ordinal))
        {
            sourceKind = "environment";
            sourceDetail = source["env:".Length..];
        }

        entry["source_kind"] = sourceKind;
        if (string.IsNullOrWhiteSpace(sourceDetail))
        {
            if (sourceKind == "config_file")
                entry["source"] = sourceKind;
            return;
        }

        var bounded = CdidxConfigFile.FormatConfigSourceDetail(sourceDetail);
        if (sourceKind == "config_file")
            entry["source"] = $"config:{bounded.Text}";
        entry["source_detail"] = bounded.Text;
        if (bounded.Truncated)
        {
            entry["source_detail_length"] = bounded.OriginalLength;
            entry["source_detail_truncated"] = true;
        }
    }

    private static string ResolveDbPathConfigSource(QueryCommandOptions options)
    {
        if (options.DbPathExplicit)
            return "flag";
        return options.DataDirSource switch
        {
            DbPathResolver.DataDirSourceFlag => "flag",
            DbPathResolver.DataDirSourceEnv => $"env:{DbPathResolver.DataDirEnvironmentVariable}",
            DbPathResolver.DataDirSourceXdg => "env:XDG_DATA_HOME",
            DbPathResolver.DataDirSourceWorkspace => "workspace",
            _ => "default",
        };
    }

    private static string ResolveNumericConfigSource(string[] args, string primaryFlag, string? aliasFlag, string envName)
    {
        if (HasOption(args, primaryFlag) || (aliasFlag != null && HasOption(args, aliasFlag)))
            return "flag";
        if (CdidxEnvironment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = CdidxConfigSourceResolver.GetSource(envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }

    private static string ResolveEnvSource(string envName)
    {
        if (CdidxEnvironment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = CdidxConfigSourceResolver.GetSource(envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }
}
