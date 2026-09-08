using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const int AuditRecoveryByteLimit = 64 * 1024;
    internal const int AuditPlanPathLimit = 10_000;
    internal const int AuditPlanRowLimit = 100_000;
    internal const int AuditPlanPageLimit = 10;
    private const int AuditPlanPathByteLimit = 4096;
    private const string AuditPlanTokenPrefix = "audit-plan:v1:";
    private sealed record AuditRecoveryRequest(bool TopSummary = false, bool Plan = false,
        string? Cursor = null, string? Partition = null);

    private static bool TryExtractAuditRecovery(string[] args, out string[] remaining, out AuditRecoveryRequest request)
    {
        var clean = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        request = new();
        remaining = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { clean.AddRange(args[i..]); break; }
            var name = args[i].Split('=', 2)[0];
            if (name is not ("--summary-level" or "--partition-plan" or "--plan-cursor" or "--partition"))
            { clean.Add(args[i]); continue; }
            if (!seen.Add(name)) return false;
            if (name == "--partition-plan")
            {
                if (args[i] != name) return false;
                request = request with { Plan = true };
                continue;
            }
            string value;
            if (args[i].Contains('=')) value = args[i][(name.Length + 1)..];
            else if (++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal)) value = args[i];
            else return false;
            if (name == "--summary-level")
            {
                if (value is not ("top" or "detailed")) return false;
                request = request with { TopSummary = value == "top" };
            }
            else
            {
                if (value.Length > AuditContinuationTokenLimit || !value.StartsWith(AuditPlanTokenPrefix, StringComparison.Ordinal)) return false;
                request = name == "--plan-cursor" ? request with { Cursor = value } : request with { Partition = value };
            }
        }
        if (request.Cursor != null && !request.Plan || request.Partition != null && request.Plan) return false;
        if (request.TopSummary) clean.Insert(0, "--summary-only");
        if (request.Plan) clean.InsertRange(0, ["--format", "compact"]);
        remaining = clean.ToArray();
        return true;
    }

    private static int WriteAuditRecoveryError(string[] args, JsonSerializerOptions jsonOptions)
        => CommandErrorWriter.WriteJsonOrHuman(AuditContinuationRequestsJson(args)
                || args.TakeWhile(arg => arg != "--").Any(arg => arg is "--partition-plan" or "--summary-level=top")
                || args.Zip(args.Skip(1)).Any(pair => pair.First == "--summary-level" && pair.Second == "top"), jsonOptions,
            "Invalid, stale, or mismatched audit partition plan or summary options.", CommandExitCodes.UsageError,
            "Use audit --all --summary-level top, or --partition-plan [--plan-cursor <token>], or --partition <token>. Regenerate the plan after index, recipe, or scope changes.",
            GetUsageLineOrThrow("audit"), CommandErrorCodes.UsageError, command: "audit");

    private static int GetAuditRecoveryByteLimit(QueryCommandOptions options, AuditAllRunState state)
        => state.RecoveryRequest.TopSummary || state.RecoveryRequest.Plan
            ? Math.Min(options.MaxJsonBytes ?? AuditRecoveryByteLimit, AuditRecoveryByteLimit)
            : options.MaxJsonBytes ?? DefaultAuditAllJsonByteLimit;

    private static string GetAuditPlanBinding(DbReader reader, QueryCommandOptions options, AuditAllRunState state)
    {
        var parts = new List<string> { "audit-path-plan-v1", reader.GetPaginationGeneration().Identity, reader.GetIndexedProjectRoot() ?? "",
            string.Join('\0', BuildAuditPlanArgv(options, state)) };
        foreach (var recipe in state.SelectedRecipes)
        {
            parts.Add(recipe.Name);
            var scope = BuildSearchRecipeScope(recipe, options);
            AddExternalRecipeSourceExclusion(reader, scope);
            parts.Add(scope.Name);
            parts.Add(string.Join('\0', scope.PathPatterns));
            parts.Add(string.Join('\0', scope.ExcludePaths));
            parts.Add(scope.ExcludeTests.ToString());
            parts.Add(BuildSearchRecipeFreshnessContext(recipe, recipe.Queries, "current", null).ExpectedRecipeVersion ?? "");
        }
        return AuditBaselineStore.Hash(parts.ToArray());
    }

    private static string EncodeAuditPlanToken(string binding, string kind, int ordinal)
    {
        var value = ordinal.ToString(CultureInfo.InvariantCulture);
        var payload = new JsonObject { ["binding"] = binding, ["kind"] = kind, ["ordinal"] = ordinal,
            ["checksum"] = AuditBaselineStore.Hash(binding, kind, value) };
        return AuditPlanTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
    }

    private static bool TryDecodeAuditPlanToken(string token, string binding, string kind, out int ordinal)
    {
        ordinal = 0;
        try
        {
            if (token.Length > AuditContinuationTokenLimit || !token.StartsWith(AuditPlanTokenPrefix, StringComparison.Ordinal)) return false;
            using var document = JsonDocument.Parse(Convert.FromBase64String(token[AuditPlanTokenPrefix.Length..]), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
                || root.GetProperty("binding").GetString() != binding || root.GetProperty("kind").GetString() != kind) return false;
            ordinal = root.GetProperty("ordinal").GetInt32();
            return ordinal >= 0 && ordinal < AuditPlanPathLimit
                && root.GetProperty("checksum").GetString() == AuditBaselineStore.Hash(binding, kind, ordinal.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { return false; }
    }

    private static List<string> BuildAuditPlanArgv(QueryCommandOptions options, AuditAllRunState state)
    {
        var args = BuildAuditAllRecoveryArgv(state.SelectedRecipes[0].Name, options, includeDb: false, includeScope: false, safeOptionLiterals: true);
        // InvocationContext emits [cdidx, audit, recipe]. Pin the database even when
        // its original selection came from workspace/environment discovery.
        args[2] = "--all";
        AddReplayValueOption(args, "--db", Path.GetFullPath(options.DbPath));
        if (!options.SourceOnly)
        {
            if (options.AuditScopeExplicit) AddReplayValueOption(args, "--audit-scope", options.AuditScope);
            else if (state.SelectedRecipes.Select(recipe => recipe.DefaultScope).Distinct(StringComparer.Ordinal).Count() == 1)
                AddReplayValueOption(args, "--audit-scope", state.SelectedRecipes[0].DefaultScope);
        }
        return args;
    }

    private static JsonObject AuditReplayNode(List<string> args)
        => new() { ["argv"] = new JsonArray(args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
            ["command"] = string.Join(" ", args.Select(QuoteReplayShellArg)) };

    private static int? PrepareAuditPartition(DbReader reader, QueryCommandOptions options, JsonSerializerOptions jsonOptions,
        AuditAllRunState state, CancellationToken cancellationToken)
    {
        var request = state.RecoveryRequest;
        if (request.Plan && state.ContinuationInput != null)
            return WriteAuditRecoveryError(["--json"], jsonOptions);
        state.PlanGeneration = reader.GetPaginationGeneration().Identity;
        state.PlanBinding = GetAuditPlanBinding(reader, options, state);
        var ordinal = 0;
        var token = request.Plan ? request.Cursor : request.Partition;
        if (token != null && !TryDecodeAuditPlanToken(token, state.PlanBinding, request.Plan ? "page" : "unit", out ordinal))
            return WriteAuditRecoveryError(["--json"], jsonOptions);

        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var inventoryCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var visited = 0;
        string? unavailable = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var cancellationScope = reader.BeginCancellationScope(deadline.Token);
            reader.RunWithCancellationInterrupt(() =>
            {
                if (state.SelectedRecipes.Sum(recipe => recipe.Queries.Count) > AuditContinuationQueryLimit)
                { unavailable = "plan_query_budget"; return false; }
                foreach (var recipe in state.SelectedRecipes)
                {
                    var scope = BuildSearchRecipeScope(recipe, options);
                    AddExternalRecipeSourceExclusion(reader, scope);
                    foreach (var query in recipe.Queries)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var child = BuildSearchRecipeQueryScope(scope, query);
                        var requiredPaths = GetSearchRecipeRequiredPathPatterns(options, query);
                        var scopeKey = AuditBaselineStore.Hash(string.Join('\0', child.PathPatterns), string.Join('\0', child.ExcludePaths),
                            child.ExcludeTests.ToString(), string.Join('\0', requiredPaths ?? []));
                        if (!inventoryCache.TryGetValue(scopeKey, out var candidates))
                        {
                            var remaining = Math.Min(AuditPlanPathLimit, AuditPlanRowLimit - visited);
                            candidates = reader.GetAuditPartitionPaths(options.Lang, child.PathPatterns, child.ExcludePaths,
                                child.ExcludeTests, options.Since, requiredPaths, remaining + 1, AuditPlanPathByteLimit);
                            visited += candidates.Count;
                            if (candidates.Count > remaining) { unavailable = "plan_inventory_budget"; return false; }
                            inventoryCache.Add(scopeKey, candidates);
                        }
                        foreach (var path in candidates)
                        {
                            if (Encoding.UTF8.GetByteCount(path) > AuditPlanPathByteLimit || path.Contains('\0'))
                            { unavailable = "plan_path_budget"; return false; }
                            paths.Add(path);
                            if (paths.Count > AuditPlanPathLimit) { unavailable = "plan_path_count_budget"; return false; }
                        }
                    }
                }
                return true;
            });
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { unavailable = cancellationToken.IsCancellationRequested ? "cancelled" : "plan_time_budget"; }
        if (state.PlanBinding != GetAuditPlanBinding(reader, options, state)) unavailable = "index_changed_during_plan";
        if (unavailable != null)
        {
            var failure = new JsonObject { ["api_version"] = JsonOutputContract.ApiVersion, ["mode"] = "audit_partition_plan",
                ["available"] = false, ["reason"] = unavailable, ["coverage_authoritative"] = false,
                ["guidance"] = "Narrow the original --path/--lang scope and regenerate the plan. No partial path inventory is presented as complete." };
            Console.WriteLine(failure.ToJsonString(EnsureJsonNodeSerializerOptions(jsonOptions)));
            return unavailable == "cancelled" ? CommandExitCodes.CancelledBySignal
                : options.AllowPartial ? CommandExitCodes.Success : CommandExitCodes.PartialResult;
        }
        var ordered = paths.ToArray();
        if (ordinal >= ordered.Length && (ordered.Length != 0 || token != null))
            return WriteAuditRecoveryError(["--json"], jsonOptions);
        if (!request.Plan)
        {
            if (ordered.Length == 0) return WriteAuditRecoveryError(["--json"], jsonOptions);
            state.PartitionPath = ordered[ordinal];
            state.PartitionId = AuditBaselineStore.Hash(state.PlanBinding, state.PartitionPath);
            return null;
        }
        return WriteAuditPartitionPlan(options, jsonOptions, state, ordered, ordinal, visited);
    }

    private static int WriteAuditPartitionPlan(QueryCommandOptions options, JsonSerializerOptions jsonOptions,
        AuditAllRunState state, string[] paths, int offset, int visited)
    {
        var args = BuildAuditPlanArgv(options, state);
        var versions = new JsonArray();
        foreach (var recipe in state.SelectedRecipes)
            versions.Add(new JsonObject { ["recipe"] = recipe.Name,
                ["version"] = BuildSearchRecipeFreshnessContext(recipe, recipe.Queries, "current", null).ExpectedRecipeVersion,
                ["scope"] = BuildSearchRecipeScope(recipe, options).Name });
        var units = new JsonArray();
        for (var i = offset; i < Math.Min(paths.Length, offset + AuditPlanPageLimit); i++)
        {
            var token = EncodeAuditPlanToken(state.PlanBinding!, "unit", i);
            var replay = AuditReplayNode([.. args, "--partition", token]);
            replay["id"] = AuditBaselineStore.Hash(state.PlanBinding!, paths[i]);
            replay["path"] = paths[i];
            replay["state"] = "pending";
            units.Add(replay);
        }
        var root = new JsonObject { ["api_version"] = JsonOutputContract.ApiVersion, ["mode"] = "audit_partition_plan",
            ["available"] = true, ["generation"] = state.PlanGeneration, ["binding"] = state.PlanBinding,
            ["effective_scope_and_recipe_versions_fingerprint"] = state.PlanBinding,
            ["recipe_versions"] = versions,
            ["unit"] = "one_exact_indexed_path_all_selected_recipe_queries", ["disjoint_paths"] = true,
            ["eligible_path_count"] = paths.Length, ["inventory_complete"] = true,
            ["coverage_authoritative"] = false, ["covered_partition_count"] = 0, ["pending_partition_count"] = paths.Length,
            ["execution_state_scope"] = "new_plan_no_execution_receipts_imported",
            ["page_offset"] = offset, ["units"] = units,
            ["limits"] = new JsonObject { ["paths"] = AuditPlanPathLimit, ["inventory_rows"] = AuditPlanRowLimit,
                ["visited_rows"] = visited, ["queries"] = AuditContinuationQueryLimit, ["page_units"] = AuditPlanPageLimit,
                ["plan_time_ms"] = 10_000, ["output_bytes"] = GetAuditRecoveryByteLimit(options, state),
                ["candidate_rows_per_query"] = AuditAllCandidateRowsPerQuery },
            ["guidance"] = "Run each unit argv and collect its partition receipt by id. Page cursors enumerate plans, not completed execution. Retries may repeat observations. A capped single file stays pending/non-authoritative; inspect its source manually. Aggregate recipe/query observations, never cross-recipe unique findings. Baseline reviews are separate." };
        var serializer = EnsureJsonNodeSerializerOptions(jsonOptions);
        while (true)
        {
            var next = offset + units.Count;
            root["returned_partition_count"] = units.Count;
            root["remaining_partition_count"] = paths.Length - next;
            root["next"] = next < paths.Length
                ? AuditReplayNode([.. args, "--partition-plan", "--max-json-bytes", GetAuditRecoveryByteLimit(options, state).ToString(CultureInfo.InvariantCulture),
                    "--plan-cursor", EncodeAuditPlanToken(state.PlanBinding!, "page", next)]) : null;
            var json = root.ToJsonString(serializer);
            if (GetJsonDocumentByteCount(json) <= GetAuditRecoveryByteLimit(options, state)) { Console.WriteLine(json); return CommandExitCodes.Success; }
            if (units.Count <= 1) return WriteAuditRecoveryBudgetError(options, jsonOptions, GetJsonDocumentByteCount(json));
            units.RemoveAt(units.Count - 1);
        }
    }

    private static int WriteAuditRecoveryBudgetError(QueryCommandOptions options, JsonSerializerOptions jsonOptions, int minimum)
        => CommandErrorWriter.WriteResponseBudgetError(true, jsonOptions, "audit", "Audit recovery metadata exceeds the response budget.",
            "Increase --max-json-bytes within 65536 bytes or simplify the scope filters.", requestedBytes: options.RequestedMaxJsonBytes,
            effectiveBytes: Math.Min(options.MaxJsonBytes ?? AuditRecoveryByteLimit, AuditRecoveryByteLimit), minimumRequiredBytes: minimum,
            recommendedBytes: minimum <= AuditRecoveryByteLimit ? minimum : null, usage: GetUsageLineOrThrow("audit"),
            retryByIncreasingBudget: minimum <= AuditRecoveryByteLimit, maximumEffectiveBytes: AuditRecoveryByteLimit);

    private static void AddAuditRecoveryProjection(JsonObject payload, QueryCommandOptions options, AuditAllRunState state)
    {
        if (state.PartitionId != null)
        {
            var covered = AuditExecutionComplete(state) && !AuditHasObservationOmissions(state)
                && state.Errors.Count == 0 && state.OmittedErrorCount == 0 && state.IndexState == "current";
            payload["partition"] = new JsonObject { ["id"] = state.PartitionId, ["path"] = state.PartitionPath,
                ["binding"] = state.PlanBinding, ["generation"] = state.PlanGeneration,
                ["state"] = covered ? "covered" : "pending", ["covered_partition_count"] = covered ? 1 : 0,
                ["pending_partition_count"] = covered ? 0 : 1, ["execution_state_scope"] = "this_partition_only",
                ["coverage_authoritative"] = covered && payload["summary"]!["count_authoritative"]!.GetValue<bool>(),
                ["reason"] = state.Recipes.SelectMany(recipe => recipe.Queries).Any(query => query.Result?.CandidateWindowExhausted == true)
                    ? "single_file_candidate_window_exhausted" : null };
        }
        if (!state.RecoveryRequest.TopSummary) return;
        payload.Remove("recipes");
        payload.Remove("selected_recipe_names");
        payload.Remove("recipe_source_diagnostics");
        payload["summary_level"] = "top";
        payload["summary"]!["selected_query_count"] = state.SelectedRecipes.Sum(recipe => recipe.Queries.Count);
        payload["summary"]!["completed_query_count"] = state.Recipes.Sum(recipe => recipe.Queries.Count(query => query.Status is "completed" or "previously_accounted"));
        payload["summary"]!["failed_query_count"] = state.Recipes.Sum(recipe => recipe.Queries.Count(query => query.Status == "failed"));
        payload["limits"]!["effective_max_json_bytes"] = GetAuditRecoveryByteLimit(options, state);
        payload["recipe_source_diagnostic_count"] = state.RegistryDiagnostics.Count;
        payload["recovery"] = new JsonObject { ["partition_plan"] = AuditReplayNode([.. BuildAuditPlanArgv(options, state), "--partition-plan"]),
            ["guidance"] = "Resume continuation.next_command when available. For non-resumable candidate windows request the bounded partition plan. Plans restart observations and do not prove exhaustive coverage." };
    }
}
