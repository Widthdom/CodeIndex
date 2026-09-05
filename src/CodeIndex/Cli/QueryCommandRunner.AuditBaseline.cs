using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const string AuditBaselineUsage = "cdidx audit baseline-export|baseline-compare <baseline.json> [--recipe <name>] [--db <path>] [--lang <lang>] [--path <glob>] [--exclude-path <glob>] [--exclude-tests] [--audit-scope <source|all>] [--since <datetime>] [--limit <n>] [--total-limit <n>] [--overwrite] [--json]; cdidx audit baseline-review <baseline.json> <id> --actor <actor> --reason <reason> --overwrite [--json]";

    internal static int RunAuditBaseline(string[] args, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken = default,
        SearchAuditRecipeRegistry? registryForTesting = null)
    {
        var json = args.Contains("--json", StringComparer.Ordinal);
        try
        {
            if (args.Length < 2 || args[0] is not ("export" or "compare" or "review") || args[1].StartsWith('-'))
                throw new InvalidDataException("Specify baseline export, compare, or review and a file path.");
            var verb = args[0];
            var path = args[1];
            var overwrite = false;
            string? recipeName = null, actor = null, reason = null, id = null;
            var forwarded = new List<string> { "--all", "--limit", "1000", "--total-limit", "10000", "--snippet-lines", "20" };
            if (json) forwarded.Add("--json");
            for (var i = 2; i < args.Length; i++)
            {
                var flag = args[i];
                if (flag == "--json") continue;
                if (flag == "--overwrite") { overwrite = true; continue; }
                if (verb == "review" && i == 2 && !flag.StartsWith('-')) { id = flag; continue; }
                if (flag == "--exclude-tests" && verb != "review") { forwarded.Add(flag); continue; }
                if (flag is not ("--recipe" or "--actor" or "--reason" or "--db" or "--lang" or "--path"
                    or "--exclude-path" or "--audit-scope" or "--since" or "--limit" or "--total-limit")
                    || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported baseline option or missing option value.");
                var value = args[++i];
                if (verb == "review" && flag is not ("--actor" or "--reason")
                    || verb != "review" && flag is "--actor" or "--reason")
                    throw new InvalidDataException("Option is not supported for this baseline operation.");
                if (flag == "--recipe") recipeName = value;
                else if (flag == "--actor") actor = value;
                else if (flag == "--reason") reason = value;
                else forwarded.AddRange([flag, value]);
            }
            if (verb == "review")
            {
                if (!overwrite || id == null || actor == null || reason == null)
                    throw new InvalidDataException("Review requires an entry ID, --actor, --reason, and explicit --overwrite.");
                var baseline = AuditBaselineStore.Read(path);
                AuditBaselineStore.Review(baseline, id, actor, reason);
                cancellationToken.ThrowIfCancellationRequested();
                AuditBaselineStore.Write(path, baseline, overwrite: true);
                return WriteBaselineResult(new JsonObject { ["api_version"] = "1", ["mode"] = "audit_baseline_review", ["status"] = "saved", ["id"] = id }, json);
            }
            if (overwrite && verb != "export")
                throw new InvalidDataException("--overwrite is only valid for export and review.");
            var previous = verb == "compare" ? AuditBaselineStore.Read(path) : null;
            if (verb == "export" && File.Exists(path) && !overwrite)
                throw new InvalidDataException("Baseline already exists; use --overwrite to replace it explicitly.");
            var registry = registryForTesting ?? SearchAuditRecipes.Load();
            if (recipeName != null)
            {
                var selected = registry.Recipes.Where(recipe => recipe.Name == recipeName).ToArray();
                if (selected.Length != 1) throw new InvalidDataException("Unknown or ambiguous recipe; run cdidx recipes.");
                registry = new SearchAuditRecipeRegistry(selected, registry.Diagnostics);
            }
            return RunAuditAll(forwarded.ToArray(), jsonOptions, cancellationToken, registry, consume: (reader, options, state) =>
            {
                var snapshot = BuildAuditBaseline(reader, options, state);
                JsonObject output;
                if (previous != null)
                {
                    if (!state.Cancelled)
                        VerifyBaselinePriorPathCoverage(previous, snapshot, reader, options, cancellationToken);
                    output = AuditBaselineStore.Compare(previous, snapshot);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AuditBaselineStore.Write(path, snapshot, overwrite);
                    output = new JsonObject
                    {
                        ["api_version"] = "1",
                        ["mode"] = "audit_baseline_export",
                        ["status"] = "saved",
                        ["complete"] = snapshot["complete"]!.DeepClone(),
                        ["coverage_reasons"] = snapshot["coverage_reasons"]!.DeepClone(),
                        ["entry_count"] = snapshot["entries"]!.AsArray().Count,
                        ["recovery_guidance"] = AuditBaselineStore.Recovery,
                    };
                }
                WriteBaselineResult(output, json);
                return state.Cancelled ? CommandExitCodes.CancelledBySignal
                    : !AuditBaselineStore.Flag(snapshot, "complete") || previous != null
                        && (!AuditBaselineStore.Flag(output, "comparable") || AuditBaselineStore.Number(output["totals"]!.AsObject(), "unknown") > 0)
                        ? CommandExitCodes.PartialResult : CommandExitCodes.Success;
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return CommandErrorWriter.WriteJsonOrHuman(json, jsonOptions,
                "Baseline operation failed: " + CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
                CommandExitCodes.UsageError, "Check the bounded baseline schema, input options, and destination permissions. " + AuditBaselineStore.Recovery,
                AuditBaselineUsage, CommandErrorCodes.UsageError, command: "audit");
        }
    }

    private static void VerifyBaselinePriorPathCoverage(JsonObject previous, JsonObject current, DbReader? reader,
        QueryCommandOptions options, CancellationToken cancellationToken)
    {
        var root = reader?.GetIndexedProjectRoot();
        if (reader == null || string.IsNullOrWhiteSpace(root)) return;
        FileIndexer? indexer = null;
        HashSet<string>? sparsePaths = null;
        var policyLoaded = false;
        string? repositoryRoot = null;
        var requiresGitEvidence = !string.IsNullOrWhiteSpace(reader.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
        foreach (var path in previous["entries"]!.AsArray().OfType<JsonObject>()
            .Select(entry => AuditBaselineStore.Text(entry, "path")).Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetFileByPath(path) != null) continue;
            if (!policyLoaded)
            {
                repositoryRoot = GitHelper.TryGetRepositoryRoot(root, cancellationToken);
                sparsePaths = GitHelper.TryGetSkipWorktreePaths(root, cancellationToken);
                indexer = new FileIndexer(root, GitHelper.ResolveIgnoreCase(root, cancellationToken), repositoryRoot ?? Path.GetFullPath(root),
                    maxFileSizeBytes: IndexedFileSizePolicy.Resolve(reader, freshness: true), directoryIgnoreCaseProbe: null,
                    symlinkPolicy: IndexFreshnessChecker.ReadIndexedSymlinkPolicy(reader), internalIndexDatabasePath: options.DbPath);
                policyLoaded = true;
            }
            var coveredDeletion = false;
            try
            {
                var absolutePath = Path.Combine(root, path);
                if (!indexer!.EvaluatePathFilter(absolutePath).ShouldSkip
                    && (repositoryRoot == null && !requiresGitEvidence
                        || sparsePaths != null && !IndexFreshnessChecker.IsSkipWorktreePath(sparsePaths, path)))
                {
                    try { _ = File.GetAttributes(absolutePath); }
                    catch (FileNotFoundException) { coveredDeletion = true; }
                    catch (DirectoryNotFoundException) { coveredDeletion = true; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // Failed coverage probes must not turn an unindexed path into a resolved finding.
                coveredDeletion = false;
            }
            if (coveredDeletion) continue;
            current["complete"] = false;
            current["count_authoritative"] = false;
            current["coverage_reasons"]!.AsArray().Add("prior_path_coverage_unverified");
            return;
        }
    }

    private static int WriteBaselineResult(JsonObject result, bool json)
    {
        if (json) Console.WriteLine(result.ToJsonString());
        else if (result["totals"] is JsonObject totals)
        {
            Console.WriteLine($"Audit baseline: new={totals["new"]}, unchanged={totals["unchanged"]}, resolved={totals["resolved"]}, unknown={totals["unknown"]}; returned={result["returned"]}, omitted={result["omitted_count"]}.");
            foreach (var row in result["results"]!.AsArray().OfType<JsonObject>())
                Console.WriteLine($"{row["classification"]}: {row["path"]}:{row["line"]} {row["recipe"]}/{row["query"]} id={row["id"]} reason={row["reason"]} reviewed_safe={row["review_applies"]}");
            Console.WriteLine($"Comparison reasons: {result["reasons"]}; baseline coverage: {result["baseline_coverage_reasons"]}; current coverage: {result["current_coverage_reasons"]}.");
            Console.WriteLine(AuditBaselineStore.Recovery);
        }
        else if (result["id"] != null) Console.WriteLine($"Reviewed safe: {result["id"]}. Annotation saved.");
        else
        {
            Console.WriteLine($"{result["mode"]}: saved. Entries={result["entry_count"]}; complete={result["complete"]}; coverage reasons={result["coverage_reasons"]}.");
            Console.WriteLine(AuditBaselineStore.Recovery);
        }
        return CommandExitCodes.Success;
    }

    private static JsonObject BuildAuditBaseline(DbReader? reader, QueryCommandOptions options, AuditAllRunState state)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var indexedRoot = reader?.GetIndexedProjectRoot();
        if (string.IsNullOrWhiteSpace(indexedRoot)) reasons.Add("workspace_identity_unavailable");
        if (reader != null && state.BaselineStartGeneration != reader.GetPaginationGeneration().Identity)
            reasons.Add("index_changed_during_audit");
        if (reader != null && ResolveSearchQueryIndexFreshness(reader, options, out _) != "current")
            reasons.Add("index_not_current_after_audit");
        if (state.IndexState != "current") reasons.Add("index_not_current");
        if (reader?.GetPersistedIndexCompletion().IndexComplete != true) reasons.Add("index_incomplete");
        if (state.Cancelled) reasons.Add("cancelled");
        if (state.TimeBudgetExceeded) reasons.Add("time_budget");
        if (state.ResultLimitReached || state.ByteBudgetReached) reasons.Add("run_limit");
        if (state.RegistryDiagnostics.Count > 0) reasons.Add("recipe_registry_diagnostics");
        var entries = new JsonArray();
        var definitions = new List<string>();
        var scopes = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var omittedEntries = 0;
        foreach (var recipe in state.Recipes)
        {
            if (recipe.Status != "completed" || recipe.Queries.Count != recipe.Recipe.Queries.Count) reasons.Add("recipe_incomplete");
            // Reuse the canonical replay builder so every effective search filter participates.
            var replay = BuildAuditAllRecoveryCommand(recipe.Recipe.Name, options, includeDb: false);
            scopes.Add(new JsonObject
            {
                ["replay"] = replay,
                ["scope"] = recipe.Scope == null ? null : JsonSerializer.SerializeToNode(recipe.Scope,
                    CliJsonSerializerContextFactory.Create(options.InvocationJsonOptions!).SearchRecipeScopeJsonResult),
            });
            foreach (var query in recipe.Queries)
            {
                definitions.Add(recipe.Recipe.Name + "/" + query.Query.Name + ":" + query.Freshness?.DefinitionVersion);
                if (query.Freshness?.FreshnessState != "clean" || string.IsNullOrEmpty(query.Freshness?.DefinitionVersion)) reasons.Add("query_not_current");
                if (query.Result == null || query.Status != "completed" || query.Result.Truncated
                    || !query.Result.SourceTotalAuthoritative || query.Result.MinimumOmittedResultCount > 0 || query.ByteOmittedResultCount > 0)
                    reasons.Add("query_coverage_incomplete");
                foreach (var row in query.Result?.Results ?? [])
                {
                    string path;
                    try { path = AuditBaselineStore.NormalizePath(row.Path); }
                    catch (InvalidDataException) { reasons.Add("path_identity_ambiguous"); continue; }
                    var identityComplete = row.TruncatedLineCount == 0 && row.DroppedMatchLineCount == 0 && row.Highlights.Count > 0;
                    if (!identityComplete) reasons.Add("identity_evidence_incomplete");
                    foreach (var highlight in row.Highlights)
                    {
                        var match = AuditBaselineStore.Hash(highlight.Text.TrimEnd('\r'), row.EnclosingContainerName ?? "", row.EnclosingSymbolName ?? "");
                        var id = AuditBaselineStore.Hash(recipe.Recipe.Name, query.Query.Name, path, match);
                        if (!seen.Add(id + ":" + highlight.Line)) continue;
                        if (entries.Count >= AuditBaselineStore.MaxEntries) { reasons.Add("entry_limit"); omittedEntries++; continue; }
                        // No line coordinates enter the evidence hash. Store hashes, never source snippets.
                        var contextLines = row.Snippet.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                        var context = AuditBaselineStore.Hash(string.Join('\n', contextLines
                            .SkipWhile(string.IsNullOrWhiteSpace).Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse()), match);
                        entries.Add(new JsonObject
                        {
                            ["id"] = id,
                            ["recipe"] = recipe.Recipe.Name,
                            ["query"] = query.Query.Name,
                            ["path"] = path,
                            ["line"] = highlight.Line,
                            ["match"] = match,
                            ["context"] = context,
                            ["identity_complete"] = identityComplete && !highlight.Truncated,
                        });
                    }
                }
            }
        }
        var coverage = new JsonArray();
        foreach (var reason in reasons.Order(StringComparer.Ordinal)) coverage.Add(reason);
        return new JsonObject
        {
            ["format"] = "cdidx-audit-baseline",
            ["schema_version"] = 1,
            ["identity_version"] = "1",
            ["recipe_schema_version"] = "1",
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["scope_fingerprint"] = AuditBaselineStore.Hash(scopes.ToJsonString()),
            ["effective_filters"] = scopes,
            ["recipe_fingerprint"] = AuditBaselineStore.Hash(definitions.ToArray()),
            ["workspace_fingerprint"] = string.IsNullOrWhiteSpace(indexedRoot) ? "" : AuditBaselineStore.Hash(Path.GetFullPath(indexedRoot)),
            ["index_scope_fingerprint"] = reader == null ? "" : AuditBaselineStore.Hash(
                IndexedFileSizePolicy.Resolve(reader, freshness: true).ToString(System.Globalization.CultureInfo.InvariantCulture),
                IndexFreshnessChecker.ReadIndexedSymlinkPolicy(reader).ToString()),
            ["index_generation"] = reader == null ? "" : AuditBaselineStore.Hash(reader.GetPaginationGeneration().Identity),
            ["complete"] = reasons.Count == 0,
            ["count_authoritative"] = reasons.Count == 0,
            ["coverage_reasons"] = coverage,
            ["entry_limit"] = AuditBaselineStore.MaxEntries,
            ["omitted_entry_count"] = omittedEntries,
            ["omitted_entry_count_authoritative"] = reasons.Count == 0 || reasons.SetEquals(["entry_limit"]),
            ["entries_truncated"] = omittedEntries > 0,
            ["entries"] = entries,
        };
    }
}
