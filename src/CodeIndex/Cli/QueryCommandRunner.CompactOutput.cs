using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int GetCompactSectionLimit(QueryCommandOptions options)
        => options.LimitExplicit ? options.Limit : DefaultCompactSectionLimit;

    internal static int GetCompactSourceLimit(int compactLimit)
    {
        var sourceLimit = compactLimit + 1;
        return NumericFlagUpperBounds.TryGetValue("--limit", out var maxLimit)
            ? Math.Min(sourceLimit, maxLimit)
            : sourceLimit;
    }

    internal static JsonObject ApplySymbolAnalysisCompactCaps(SymbolAnalysisResult analysis, int sectionLimit)
    {
        var sections = new JsonObject();
        TruncateCompactSection(analysis.Definitions, sectionLimit, sections, "definitions");
        TruncateCompactSection(analysis.NearbySymbols, sectionLimit, sections, "nearby_symbols");
        TruncateCompactSection(analysis.References, sectionLimit, sections, "references");
        TruncateCompactSection(analysis.Callers, sectionLimit, sections, "callers");
        TruncateCompactSection(analysis.Callees, sectionLimit, sections, "callees");
        var bundles = analysis.CandidateBundles ?? [];
        TruncateCompactSection(bundles, sectionLimit, sections, "candidate_bundles");
        var candidates = sections["candidate_bundles"]!.AsObject();
        var countAuthoritative = !analysis.CandidateCountIsLowerBound;
        var omittedCount = analysis.CandidateCount - bundles.Count;
        candidates["source_count_authoritative"] = countAuthoritative;
        candidates["truncated"] = omittedCount > 0 || !countAuthoritative;
        candidates[countAuthoritative ? "omitted_count" : "omitted_count_lower_bound"] = omittedCount;
        if (analysis.CandidateBundles != null)
        {
            for (var i = 0; i < analysis.CandidateBundles.Count; i++)
            {
                var bundle = analysis.CandidateBundles[i];
                TruncateCompactSection(bundle.NearbySymbols, sectionLimit, sections, $"candidate_bundles[{i}].nearby_symbols");
                TruncateCompactSection(bundle.References, sectionLimit, sections, $"candidate_bundles[{i}].references");
                TruncateCompactSection(bundle.Callers, sectionLimit, sections, $"candidate_bundles[{i}].callers");
                TruncateCompactSection(bundle.Callees, sectionLimit, sections, $"candidate_bundles[{i}].callees");
            }
        }
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static JsonObject BuildCompactTruncationMetadata(int sectionLimit, JsonObject sections)
        => new()
        {
            ["section_limit"] = sectionLimit,
            ["sections"] = sections,
        };

    internal static void AddCompactJsonFields(JsonObject payload, int compactLimit, JsonObject truncation)
    {
        payload["compact"] = true;
        payload["compact_limit"] = compactLimit;
        payload["truncation"] = truncation;
        if (truncation["sections"]?["candidate_bundles"] is JsonObject candidates)
        {
            payload["candidate_count"] = candidates["source_count"]!.DeepClone();
            payload["candidate_count_authoritative"] = candidates["source_count_authoritative"]!.DeepClone();
            if (!candidates["source_count_authoritative"]!.GetValue<bool>())
                payload["candidate_count_lower_bound"] = candidates["source_count"]!.DeepClone();
        }
    }

    private static void TruncateCompactSection<T>(List<T> items, int sectionLimit, JsonObject sections, string sectionName)
    {
        var sourceCount = items.Count;
        if (sourceCount > sectionLimit)
            items.RemoveRange(sectionLimit, sourceCount - sectionLimit);

        sections[sectionName] = new JsonObject
        {
            ["returned"] = items.Count,
            ["source_count"] = sourceCount,
            ["truncated"] = sourceCount > sectionLimit,
        };
    }
}
