using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Cli;

// Baselines are local evidence records, never executable recipe definitions.
internal static class AuditBaselineStore
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal const int MaxEntries = 10_000;
    internal const int ResultLimit = 200;
    internal const string Recovery = "Refresh the index and repeat the same recipes and filters with sufficient --limit and --total-limit. Review unknown or changed evidence manually; never treat unknown as resolved.";

    internal static string Hash(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", parts)))).ToLowerInvariant();

    internal static string NormalizePath(string path)
    {
        // A POSIX literal backslash is ambiguous with a Windows separator: fail closed.
        if (path.Length is 0 or > 1024 || path.Any(char.IsControl)
            || path.StartsWith('/') || path.Contains(':') || path.Contains('\\'))
            throw new InvalidDataException("Baseline requires unambiguous repository-relative slash paths.");
        if (path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Baseline path is not canonical.");
        return path;
    }

    internal static JsonObject Read(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || !ExecutableExtensionBoundary.IsRegularFilePath(path))
            throw new InvalidDataException("Baseline input must be a regular file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException("Baseline exceeds the 8 MiB input limit.");
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = stream.Read(block, 0, Math.Min(block.Length, MaxBytes + 1 - (int)buffer.Length))) > 0)
        {
            buffer.Write(block, 0, read);
            if (buffer.Length > MaxBytes)
                throw new InvalidDataException("Baseline exceeds the 8 MiB input limit.");
        }
        var root = JsonNode.Parse(buffer.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
            ?? throw new InvalidDataException("Expected a baseline JSON object.");
        Validate(root);
        return root;
    }

    internal static void Validate(JsonObject root)
    {
        if (Text(root, "format") != "cdidx-audit-baseline" || Number(root, "schema_version") != 1
            || root["entries"] is not JsonArray entries || entries.Count > MaxEntries)
            throw new InvalidDataException("Unsupported or invalid baseline schema; export a new baseline.");
        if (root["coverage_reasons"] != null)
        {
            if (root["coverage_reasons"] is not JsonArray coverage || coverage.Count > 32
                || coverage.Any(reason => reason is not JsonValue value || !value.TryGetValue<string>(out var text)
                    || text.Length is 0 or > 64 || text.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')))
                throw new InvalidDataException("Invalid baseline coverage reasons.");
        }
        foreach (var node in entries)
        {
            if (node is not JsonObject entry)
                throw new InvalidDataException("Invalid baseline entry.");
            NormalizePath(Text(entry, "path"));
            foreach (var key in new[] { "recipe", "query", "id", "match", "context" })
            {
                var value = Text(entry, key);
                if (value.Length is 0 or > 256 || value.Any(char.IsControl))
                    throw new InvalidDataException("Invalid baseline identity.");
            }
            if (Number(entry, "line") < 1
                || Text(entry, "id") != Hash(Text(entry, "recipe"), Text(entry, "query"), Text(entry, "path"), Text(entry, "match")))
                throw new InvalidDataException("Invalid baseline identity or location.");
            if (entry["review"] is JsonObject review)
            {
                CheckAnnotation(Text(review, "actor"));
                CheckAnnotation(Text(review, "reason"));
                if (Text(review, "recorded_at").Length > 64 || !DateTimeOffset.TryParse(Text(review, "recorded_at"),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
                    throw new InvalidDataException("Invalid review timestamp.");
                if (Text(review, "context") != Text(entry, "context") || Text(review, "state") != "reviewed_safe")
                    throw new InvalidDataException("Review evidence does not match its entry.");
            }
            else if (entry["review"] != null)
                throw new InvalidDataException("Invalid review annotation.");
        }
    }

    internal static void Write(string path, JsonObject baseline, bool overwrite)
    {
        Validate(baseline);
        var json = baseline.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidDataException("Baseline exceeds the 8 MiB output limit; narrow the audit scope.");
        AtomicFileWriter.WriteText(path, json, new UTF8Encoding(false), AtomicFileWriter.WriteProfile.Sensitive, overwrite);
    }

    internal static void Review(JsonObject baseline, string id, string actor, string reason)
    {
        CheckAnnotation(actor);
        CheckAnnotation(reason);
        var matches = baseline["entries"]!.AsArray().OfType<JsonObject>().Where(entry => Text(entry, "id") == id).ToArray();
        if (matches.Length != 1 || !Flag(matches[0], "identity_complete") || !CoverageComplete(baseline))
            throw new InvalidDataException("Review requires one unambiguous entry in a complete baseline.");
        matches[0]["review"] = new JsonObject
        {
            ["state"] = "reviewed_safe",
            ["actor"] = actor,
            ["reason"] = reason,
            ["context"] = Text(matches[0], "context"),
            ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    internal static JsonObject Compare(JsonObject baseline, JsonObject current)
    {
        Validate(baseline);
        Validate(current);
        var reasons = new JsonArray();
        foreach (var key in new[] { "identity_version", "recipe_schema_version", "scope_fingerprint", "recipe_fingerprint", "workspace_fingerprint" })
            if (Text(baseline, key).Length == 0 || Text(baseline, key) != Text(current, key))
                reasons.Add(key + "_incomparable");
        if (!CoverageComplete(baseline)) reasons.Add("baseline_coverage_incomplete");
        if (!CoverageComplete(current)) reasons.Add("current_coverage_incomplete");
        if (Text(baseline, "index_generation").Length == 0 || Text(current, "index_generation").Length == 0)
            reasons.Add("index_provenance_unavailable");
        var compatible = reasons.Count == 0;
        var oldEntries = baseline["entries"]!.AsArray().OfType<JsonObject>().ToArray();
        var newEntries = current["entries"]!.AsArray().OfType<JsonObject>().ToArray();
        var oldGroups = oldEntries.GroupBy(entry => Text(entry, "id")).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var newGroups = newEntries.GroupBy(entry => Text(entry, "id")).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var moved = oldEntries.Concat(newEntries).GroupBy(entry => Hash(Text(entry, "recipe"), Text(entry, "query"), Text(entry, "match")))
            .Where(group => group.Select(entry => Text(entry, "path")).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var totals = new JsonObject { ["new"] = 0, ["unchanged"] = 0, ["resolved"] = 0, ["unknown"] = 0 };
        var results = new JsonArray();
        foreach (var id in oldGroups.Keys.Union(newGroups.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            oldGroups.TryGetValue(id, out var oldGroup);
            newGroups.TryGetValue(id, out var newGroup);
            var entry = (newGroup ?? oldGroup)![0];
            var ambiguous = oldGroup?.Length > 1 || newGroup?.Length > 1
                || moved.Contains(Hash(Text(entry, "recipe"), Text(entry, "query"), Text(entry, "match")));
            var evidenceComplete = (oldGroup ?? []).Concat(newGroup ?? []).All(item => Flag(item, "identity_complete"));
            var contextChanged = oldGroup != null && newGroup != null && Text(oldGroup[0], "context") != Text(newGroup[0], "context");
            var classification = !compatible || ambiguous || !evidenceComplete || contextChanged ? "unknown"
                : oldGroup == null ? "new" : newGroup == null ? "resolved" : "unchanged";
            totals[classification] = Number(totals, classification) + 1;
            if (results.Count >= ResultLimit) continue;
            results.Add(new JsonObject
            {
                ["id"] = id,
                ["recipe"] = Text(entry, "recipe"),
                ["query"] = Text(entry, "query"),
                ["path"] = Text(entry, "path"),
                ["line"] = Number(entry, "line"),
                ["classification"] = classification,
                ["reason"] = !compatible ? "coverage_or_contract_incomparable" : ambiguous ? "ambiguous_identity_or_rename"
                    : !evidenceComplete ? "identity_evidence_incomplete" : contextChanged ? "evidence_changed_requires_review" : "compatible_evidence",
                ["baseline_observation_count"] = oldGroup?.Length ?? 0,
                ["current_observation_count"] = newGroup?.Length ?? 0,
                ["review"] = oldGroup?.Length == 1 ? oldGroup[0]["review"]?.DeepClone() : null,
                ["review_applies"] = classification == "unchanged" && oldGroup![0]["review"] != null,
            });
        }
        var total = totals.Sum(pair => pair.Value!.GetValue<int>());
        return new JsonObject
        {
            ["api_version"] = "1",
            ["mode"] = "audit_baseline_compare",
            ["comparable"] = compatible,
            ["reasons"] = reasons,
            ["baseline_coverage_reasons"] = baseline["coverage_reasons"]?.DeepClone(),
            ["current_coverage_reasons"] = current["coverage_reasons"]?.DeepClone(),
            ["totals"] = totals,
            ["count_semantics"] = "distinct_identity_groups",
            ["total"] = total,
            ["returned"] = results.Count,
            ["omitted_count"] = total - results.Count,
            ["truncated"] = total > results.Count,
            ["limit"] = ResultLimit,
            ["baseline_observation_count"] = oldEntries.Length,
            ["current_observation_count"] = newEntries.Length,
            ["results"] = results,
            ["recovery_guidance"] = Recovery,
        };
    }

    internal static string Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    internal static int Number(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : -1;
    internal static bool Flag(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static bool CoverageComplete(JsonObject node) => Flag(node, "complete") && Flag(node, "count_authoritative")
        && node["coverage_reasons"] is JsonArray reasons && reasons.Count == 0;

    private static void CheckAnnotation(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw new InvalidDataException("Review actor and reason must be nonblank, control-free text of at most 512 characters.");
    }
}
