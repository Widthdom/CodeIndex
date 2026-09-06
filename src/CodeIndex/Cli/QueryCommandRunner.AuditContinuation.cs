using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private const int AuditContinuationQueryLimit = 512;
    private const int AuditContinuationTokenLimit = 16384;
    private const string AuditContinuationPrefix = "audit:v1:";

    private static bool TryExtractAuditContinuation(string[] args, out string[] remaining, out string? token)
    {
        var clean = new List<string>();
        token = null;
        remaining = [];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") { clean.AddRange(args[i..]); break; }
            if (args[i] == "--continuation" || args[i].StartsWith("--continuation=", StringComparison.Ordinal))
            {
                if (token != null) return false;
                if (args[i] == "--continuation")
                {
                    if (++i >= args.Length) return false;
                    token = args[i];
                }
                else token = args[i]["--continuation=".Length..];
                if (token.Length > AuditContinuationTokenLimit || !token.StartsWith(AuditContinuationPrefix, StringComparison.Ordinal))
                    return false;
            }
            else clean.Add(args[i]);
        }
        remaining = clean.ToArray();
        return true;
    }

    private static int WriteAuditContinuationError(string[] args, JsonSerializerOptions jsonOptions, bool json = false)
        => CommandErrorWriter.WriteJsonOrHuman(json || AuditContinuationRequestsJson(args), jsonOptions,
            "Invalid, stale, or mismatched audit continuation.", CommandExitCodes.UsageError,
            "Restart audit --all without --continuation. Keep the index, recipe definitions, scope, selectors, and --limit unchanged when resuming.",
            GetUsageLineOrThrow("audit"), CommandErrorCodes.UsageError, command: "audit");

    private static bool AuditContinuationRequestsJson(string[] args)
    {
        if (ProgramRunner.ContainsJsonOutputFlag(args)) return true;
        string? format = null;
        var summary = false;
        for (var i = 0; i < args.Length && args[i] != "--"; i++)
        {
            if (args[i] == "--summary-only") summary = true;
            if (args[i] == "--compact") format = OutputFormatCompact;
            if (args[i].StartsWith("--format=", StringComparison.Ordinal)) format = args[i][9..];
            if (args[i] == "--format" && i + 1 < args.Length) format = args[++i];
        }
        return format is OutputFormatJson or OutputFormatCompact or OutputFormatCount || format == null && summary;
    }

    private static string BuildAuditContinuationBinding(DbReader reader, QueryCommandOptions options, AuditAllRunState state)
    {
        var parts = new List<string>
        {
            "audit-continuation-v1-fixed-candidates-10000",
            reader.GetPaginationGeneration().Identity,
            reader.GetIndexedProjectRoot() ?? "",
            options.CountOnly.ToString(), options.SummaryOnly.ToString(),
        };
        foreach (var recipe in state.SelectedRecipes)
        {
            parts.Add(BuildAuditAllRecoveryCommand(recipe.Name, options, includeDb: false));
            parts.Add(BuildSearchRecipeFreshnessContext(recipe, recipe.Queries, "current", null).ExpectedRecipeVersion ?? "");
        }
        return AuditBaselineStore.Hash(parts.ToArray());
    }

    private static bool InitializeAuditContinuation(DbReader reader, QueryCommandOptions options, AuditAllRunState state)
    {
        state.ContinuationBinding = BuildAuditContinuationBinding(reader, options, state);
        var count = state.SelectedRecipes.Sum(recipe => recipe.Queries.Count);
        state.InitialOffsets = new int[count];
        if (state.ContinuationInput == null) return true;
        if (count > AuditContinuationQueryLimit) return false;
        try
        {
            var encoded = state.ContinuationInput[AuditContinuationPrefix.Length..];
            var bytes = Convert.FromBase64String(encoded);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || root.GetProperty("binding").GetString() != state.ContinuationBinding)
                return false;
            var values = root.GetProperty("offsets");
            if (values.GetArrayLength() != count) return false;
            var offsets = values.EnumerateArray().Select(value => value.GetInt32()).ToArray();
            if (offsets.Any(value => value < -2 || value > AuditAllCandidateRowsPerQuery)) return false;
            if (root.GetProperty("checksum").GetString() != AuditBaselineStore.Hash(state.ContinuationBinding, values.GetRawText()))
                return false;
            state.InitialOffsets = offsets;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            // Untrusted token failures are deliberately bounded and never echo token contents.
            return false;
        }
    }

    private static int[]? GetAuditContinuationOffsets(AuditAllRunState state)
    {
        if (state.InitialOffsets == null) return null;
        var offsets = (int[])state.InitialOffsets.Clone();
        foreach (var query in state.Recipes.SelectMany(recipe => recipe.Queries))
        {
            if (query.Status != "completed" || query.Result == null) continue;
            var result = query.Result;
            offsets[query.Position] = !state.SuppressRows && (result.Truncated || query.ByteOmittedResultCount > 0 || query.DetailOmittedResultCount > 0)
                ? query.RowOffset + result.Results.Count
                : result.CandidateWindowExhausted ? -2 : -1;
        }
        return offsets;
    }

    private static bool AuditExecutionComplete(AuditAllRunState state)
        => !state.Cancelled && !state.TimeBudgetExceeded && !state.GenerationChanged
            && state.Recipes.Count == state.SelectedRecipes.Count
            && state.Recipes.All(recipe => recipe.OmittedQueryCount == 0
                && recipe.Queries.Count == recipe.Recipe.Queries.Count
                && recipe.Queries.All(query => query.Status is "completed" or "previously_accounted"));

    private static bool AuditHasObservationOmissions(AuditAllRunState state)
        => state.ByteOmittedResultCount > 0
            || state.DetailOmittedResultCount > 0
            || state.InitialOffsets?.Contains(-2) == true
            || state.Recipes.SelectMany(recipe => recipe.Queries).Any(query => !state.SuppressRows && query.Result?.Truncated == true
                || query.Result?.CandidateWindowExhausted == true);

    private static JsonObject BuildAuditContinuation(QueryCommandOptions options, AuditAllRunState state)
    {
        var offsets = GetAuditContinuationOffsets(state);
        var unavailableReason = state.GenerationChanged ? "index_changed_during_audit"
            : offsets == null ? "index_generation_unavailable"
            : offsets.Length > AuditContinuationQueryLimit && offsets.Any(value => value >= 0) ? "continuation_query_limit"
            : null;
        string? token = null;
        if (unavailableReason == null && offsets!.Any(value => value >= 0))
        {
            var values = new JsonArray();
            foreach (var offset in offsets!) values.Add(offset);
            var payload = new JsonObject
            {
                ["binding"] = state.ContinuationBinding,
                ["offsets"] = values,
                ["checksum"] = AuditBaselineStore.Hash(state.ContinuationBinding!, values.ToJsonString()),
            };
            token = AuditContinuationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        }
        var fallback = new JsonArray();
        var position = 0;
        var fallbackCount = 0;
        foreach (var recipe in state.SelectedRecipes)
        {
            foreach (var query in recipe.Queries)
            {
                var needsFallback = offsets != null && offsets[position] == -2
                    || unavailableReason != null && (unavailableReason != "continuation_query_limit" || offsets![position] >= 0);
                position++;
                if (!needsFallback) continue;
                fallbackCount++;
                if (fallback.Count >= AuditAllRecoveryCommandLimit) continue;
                fallback.Add(new JsonObject
                {
                    ["recipe"] = recipe.Name,
                    ["query_name"] = query.Name,
                    ["reason"] = unavailableReason ?? "child_coverage_not_authoritative",
                    ["command"] = BuildAuditAllRecoveryCommand(recipe.Name, options)
                        + " --include-query " + QuoteReplayShellArg(query.Name),
                    ["guidance"] = "This bounded fallback restarts the child query and may repeat observations. Narrow its path scope to inspect beyond the candidate window.",
                });
            }
        }
        string? command = null;
        if (token != null)
        {
            var recipe = state.SelectedRecipes[0];
            var prefix = "cdidx audit " + QuoteReplayShellArg(recipe.Name);
            var replay = BuildAuditAllRecoveryCommand(recipe.Name, options);
            var nextByteLimit = options.MaxJsonBytes ?? DefaultAuditAllJsonByteLimit;
            if (state.EmittedResultCount == 0 && state.ByteOmittedResultCount > 0)
                nextByteLimit = (int)Math.Min(MaxSearchJsonByteLimit, (long)nextByteLimit * 2);
            command = "cdidx audit --all" + replay[prefix.Length..]
                + " --total-limit " + state.EffectiveTotalLimit.ToString(CultureInfo.InvariantCulture)
                + " --max-json-bytes " + nextByteLimit.ToString(CultureInfo.InvariantCulture)
                + (options.CountOnly ? " --count" : "") + (options.SummaryOnly ? " --summary-only" : "")
                + " --continuation " + QuoteReplayShellArg(token);
        }
        return new JsonObject
        {
            ["next_token"] = token,
            ["next_command"] = command,
            ["available"] = token != null,
            ["unavailable_reason"] = unavailableReason,
            ["query_limit"] = AuditContinuationQueryLimit,
            ["token_byte_limit"] = AuditContinuationTokenLimit,
            ["fallback_count"] = fallbackCount,
            ["fallback_omitted_count"] = fallbackCount - fallback.Count,
            ["fallbacks"] = fallback,
            ["semantics"] = "recipe_query_observations_no_cross_recipe_deduplication",
        };
    }
}
