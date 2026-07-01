using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
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
                CommandErrorWriter.WriteStderr("Error: status --config cannot be combined with --check, --log-path, or --explain.");
                return CommandExitCodes.UsageError;
            }

            Console.WriteLine(BuildEffectiveConfigJson(options, cmdArgs, appVersion).ToJsonString(jsonOptions));
            return CommandExitCodes.Success;
        }
        if (options.StatusLogPath)
        {
            if (options.CheckWorkspace)
            {
                CommandErrorWriter.WriteStderr("Error: status --log-path cannot be combined with --check.");
                return CommandExitCodes.UsageError;
            }

            var logPath = GlobalToolLog.ResolveLogDirectoryForStatus();
            if (options.Json)
                Console.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["log_path"] = logPath }, jsonOptions));
            else
                Console.WriteLine(logPath);
            return CommandExitCodes.Success;
        }
        if (options.StatusExplainField != null)
        {
            if (options.Json)
                return WriteStatusReadinessExplanationJson(options.StatusExplainField, jsonOptions);
            return WriteStatusReadinessExplanation(options.StatusExplainField);
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

            var status = reader.GetStatus();
            WorkspaceMetadataEnricher.Enrich(status, options.DbPath, options.DbPathExplicit, cancellationToken);
            status.DataDir = options.DataDir;
            status.DataDirSource = options.DataDirSource;
            status.DataDirMode = DataDirectorySecurity.GetUnixModeString(GetDataDirectoryPath(options.DbPath));
            status.DbFileMode = DbContext.GetUnixFileModeString(options.DbPath);
            var macProfile = MacProfileDetector.DetectCurrentWithDiagnostics();
            status.MacProfile = macProfile.Profile;
            if (macProfile.Diagnostics.Count > 0)
                status.MacProfileDiagnostics = macProfile.Diagnostics.ToList();
            if (options.CheckWorkspace)
            {
                status.WorkspaceCheck = IndexFreshnessChecker.Check(reader, status.ProjectRoot, cancellationToken);
                status.IndexMatchesWorkspace = status.WorkspaceCheck.Checked
                    ? status.WorkspaceCheck.MatchesWorkspace
                    : null;
                status.StaleAfterSeconds = (long)Math.Round(staleAfter.Value.TotalSeconds, MidpointRounding.AwayFromZero);
                if (status.IndexedAt.HasValue)
                    status.IndexAgeSeconds = Math.Max(0, (long)Math.Round((GetUtcNow() - status.IndexedAt.Value).TotalSeconds, MidpointRounding.AwayFromZero));
            }
            // Attach runtime metadata / ランタイムメタデータを付加
            ApplyStatusSymbolKindLimits(status, reader.GetSymbolKindCounts());
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(status.ProjectRoot);
            status.GraphSupportedLanguages = ReferenceExtractor.GetSupportedLanguages().OrderBy(l => l).ToList();
            status.Extractors = ExtractorPluginRegistry.GetStatusSnapshot();
            var postExtractionHookSnapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();
            var postExtractionHooks = postExtractionHookSnapshot.Hooks;
            if (postExtractionHookSnapshot.Diagnostics.Count > 0)
                status.HookDiagnostics = postExtractionHookSnapshot.Diagnostics.ToList();
            var trustOverrides = ExtractorPluginRegistry.GetAcceptedTrustOverrides(status.ProjectRoot)
                .Concat(postExtractionHookSnapshot.TrustOverrides)
                .ToList();
            if (trustOverrides.Count > 0)
                status.TrustOverrides = trustOverrides;
            if (postExtractionHooks.Count > 0)
            {
                status.Hooks = postExtractionHooks
                    .Select(hook => new PostExtractionHookStatus
                    {
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
            var topLangs = status.Languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key);
            var freshness = BuildStatusFreshnessLabel(status);
            var dirty = status.GitIsDirty == true ? ", dirty" : "";
            ApplyStatusDegradationGuidance(status, options);

            var degraded = IsStatusDegraded(status)
                ? ", DEGRADED"
                : "";
            status.Summary = $"{status.Files} files, {status.Symbols} symbols, {status.References} refs across {status.Languages.Count} languages ({string.Join(", ", topLangs)}); index {freshness}{dirty}{degraded}";

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
                    WriteStatusCheckDiagnostics(checkFailures);
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
                // #1509 surface: SHA / branch / timestamp / drift come from the per-success
                // stamp (indexed_head_sha / _branch / _timestamp) and reflect last-touched HEAD
                // regardless of update mode. #1508/#1512's IndexedHeadCommit (full-scan only)
                // is rendered separately below when it disagrees with the runtime GitHead.
                if (status.IndexedHeadSha != null)
                {
                    var branchSuffix = string.IsNullOrWhiteSpace(status.IndexedHeadBranch)
                        ? string.Empty
                        : $" (branch {status.IndexedHeadBranch})";
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", $"{status.IndexedHeadSha}{branchSuffix}"));
                }
                else if (status.IndexedHeadCommit != null && !string.Equals(status.IndexedHeadCommit, status.GitHead, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("Idx HEAD", status.IndexedHeadCommit));
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
                // #1546: surface the persisted filesystem case-sensitivity so operators can
                // diagnose phantom path collapses on case-sensitive APFS / WSL / ReFS volumes.
                // #1546: case-sensitivity を診断用に明示する。
                if (status.PathCaseSensitive != null)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("FS Case", status.PathCaseSensitive == true ? "case-sensitive" : "case-insensitive"));
                WriteStatusReadinessSummary(status, options);
                if (status.WorktreeHeadChanged == true)
                    Console.WriteLine(ConsoleUi.FormatSummaryLine("WARN", $"worktree HEAD changed since the index was built ({ShortSha(status.IndexedHeadCommit)} -> {ShortSha(status.GitHead)}). Run `{BuildReindexRepairCommand(status.ProjectRoot, options.DbPath, options.DbPathExplicit)}` to refresh the index for the current branch."));
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
                var totalLangs = FileIndexer.GetLanguageExtensions().Values.Distinct().Count();
                var symbolLangs = SymbolExtractor.GetSupportedLanguages().Count;
                Console.WriteLine(ConsoleUi.FormatSummaryLine("Support", $"{totalLangs} detected, {symbolLangs} with symbols, {status.GraphSupportedLanguages?.Count ?? 0} with graph"));
            }

            if (!options.CheckWorkspace)
                return CommandExitCodes.Success;
            return GetStatusCheckExitCode(checkFailures);
        }, cancellationToken: cancellationToken);
    }

    private readonly record struct LimitedStatusKindCounts(
        Dictionary<string, long> Counts,
        int TotalCount,
        int OmittedCount,
        bool NamesTruncated);

    private static void ApplyStatusSymbolKindLimits(StatusResult status, Dictionary<string, long> symbolKinds)
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

        var staleAfterEnvValue = CdidxEnvironment.GetEnvironmentVariable(StaleAfterEnvironmentVariable);

        var payload = new JsonObject
        {
            ["api_version"] = "1",
            ["effective_config"] = new JsonObject
            {
                ["db_path"] = Entry(options.DbPath, ResolveDbPathConfigSource(options)),
                ["data_dir"] = Entry(options.DataDir, options.DataDirSource ?? "flag"),
                ["limit"] = Entry(options.Limit, ResolveNumericConfigSource(cmdArgs, "--limit", "--top", DefaultLimitEnvironmentVariable)),
                ["snippet_lines"] = Entry(options.SnippetLines, ResolveNumericConfigSource(cmdArgs, "--snippet-lines", null, DefaultSnippetLinesEnvironmentVariable)),
                ["max_line_width"] = Entry(options.MaxLineWidth, ResolveNumericConfigSource(cmdArgs, "--max-line-width", null, DefaultMaxLineWidthEnvironmentVariable)),
                ["json"] = Entry(options.Json, HasOption(cmdArgs, "--json") ? "flag" : "default"),
                ["stale_after"] = Entry(options.StaleAfter?.ToString() ?? staleAfterEnvValue, options.StaleAfter.HasValue ? "flag" : ResolveEnvSource(StaleAfterEnvironmentVariable)),
                ["global_tool_log_dir"] = Entry(GlobalToolLog.ResolveLogDirectoryForStatus(), ResolveEnvSource("CDIDX_GLOBAL_TOOL_LOG_DIR")),
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
        var configSource = CdidxEnvironment.GetConfigSource(envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }

    private static string ResolveEnvSource(string envName)
    {
        if (CdidxEnvironment.GetEnvironmentVariable(envName) is null)
            return "default";
        var configSource = CdidxEnvironment.GetConfigSource(envName);
        if (!string.IsNullOrWhiteSpace(configSource))
            return $"config:{configSource}";
        return $"env:{envName}";
    }
}
