using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal sealed class SearchCountOriginCoverage(QueryCommandOptions options)
    {
        private readonly bool _enabled = HasSearchOriginFilters(options)
            || options.GroupBy == "origin" || options.CountBy == "origin" || options.UniqueBy == "origin";
        private readonly SortedSet<string> _reasons = new(StringComparer.Ordinal);
        private int? _retryPasses;
        public bool Complete { get; private set; } = true;

        public void Observe(CompactSearchResult candidate)
        {
            if (!_enabled)
                return;
            if (candidate.MatchFacets.Count == 0)
                MarkUnknown("origin_evidence_unavailable", null);
            foreach (var facet in candidate.MatchFacets)
            {
                if (facet.Origin == SearchMatchClassifier.Unknown)
                    MarkUnknown(facet.OriginUnavailable?.Reason ?? "origin_evidence_unavailable",
                        facet.OriginUnavailable?.RetryOriginPasses);
            }
        }

        private void MarkUnknown(string reason, int? retryPasses)
        {
            Complete = false;
            // Reasons come from the classifier's fixed vocabulary, never source text.
            if (_reasons.Count < 16)
                _reasons.Add(reason);
            if (retryPasses.HasValue)
                _retryPasses = Math.Max(_retryPasses ?? 0, retryPasses.Value);
        }

        public void Merge(SearchCountOriginCoverage other)
        {
            if (!other.Complete)
                foreach (var reason in other._reasons)
                    MarkUnknown(reason, other._retryPasses);
        }

        private string RecoveryGuidance => _retryPasses is { } passes
            ? $"Rerun with --origin-passes {passes} for another bounded C#/Python lexical pass. Inspect unknown matches without origin/result-kind exclusions; missing or malformed context remains unknown. Larger output limits do not repair classification."
            : "Inspect unknown matches without origin/result-kind exclusions and review the affected source manually. Missing/malformed context or the maximum lexical budget cannot be resolved by more passes or larger output limits. Absence is not authoritative.";

        public void AddJsonFields(JsonObject payload)
        {
            if (!_enabled)
                return;
            AddClassificationDiagnostics(payload);
            // Classification cannot restore authority to a potentially stale snapshot.
            // Include diagnostics here for named children as well as their parent.
            AddActiveSqliteDiagnostics(payload);
            var degraded = !Complete || JsonBool(payload, "degraded") == true
                || JsonBool(payload, "wal_stale_snapshot_risk") == true;
            payload["degraded"] = degraded;
            payload["authoritative_count"] = !degraded && JsonBool(payload, "authoritative_count") != false;
            if (Complete)
                return;
            payload["partial_result"] = true;
        }

        public void AddClassificationDiagnostics(JsonObject payload)
        {
            if (!_enabled)
                return;
            payload["origin_classification_complete"] = Complete;
            payload["origin_passes"] = options.OriginPasses;
            if (Complete)
                return;
            payload["classification_incomplete_reason"] = "origin_classification_unavailable";
            payload["classification_incomplete_reasons"] = new JsonArray(_reasons.Select(reason => JsonValue.Create(reason)).ToArray());
            if (_retryPasses.HasValue)
                payload["retry_origin_passes"] = _retryPasses.Value;
            payload["classification_recovery_guidance"] = RecoveryGuidance;
        }

        public string EnrichJson(string json, JsonSerializerOptions jsonOptions)
        {
            if (!_enabled)
                return json;
            var payload = JsonNode.Parse(json)!.AsObject();
            AddJsonFields(payload);
            return payload.ToJsonString(jsonOptions);
        }

        public int ExitCode(int outputExitCode = CommandExitCodes.Success)
            => outputExitCode == CommandExitCodes.Success && !Complete && !options.AllowPartial
                ? CommandExitCodes.PartialResult : outputExitCode;

        public void WriteHumanWarning()
        {
            if (!Complete)
                CommandErrorWriter.WriteStderr("WARN: origin classification is incomplete; counts and absence are not authoritative. " + RecoveryGuidance);
        }
    }
}
