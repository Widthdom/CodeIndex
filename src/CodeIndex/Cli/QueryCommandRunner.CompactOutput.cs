using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static int GetCompactSectionLimit(QueryCommandOptions options)
        => options.LimitExplicit ? options.Limit : DefaultCompactSectionLimit;

    private static int GetCompactSourceLimit(int compactLimit)
    {
        var sourceLimit = compactLimit + 1;
        return NumericFlagUpperBounds.TryGetValue("--limit", out var maxLimit)
            ? Math.Min(sourceLimit, maxLimit)
            : sourceLimit;
    }

    private static JsonObject ApplySymbolAnalysisCompactCaps(SymbolAnalysisResult analysis, int sectionLimit)
    {
        var sections = new JsonObject();
        TruncateCompactSection(analysis.Definitions, sectionLimit, sections, "definitions");
        TruncateCompactSection(analysis.NearbySymbols, sectionLimit, sections, "nearby_symbols");
        TruncateCompactSection(analysis.References, sectionLimit, sections, "references");
        TruncateCompactSection(analysis.Callers, sectionLimit, sections, "callers");
        TruncateCompactSection(analysis.Callees, sectionLimit, sections, "callees");
        return BuildCompactTruncationMetadata(sectionLimit, sections);
    }

    private static JsonObject BuildCompactTruncationMetadata(int sectionLimit, JsonObject sections)
        => new()
        {
            ["section_limit"] = sectionLimit,
            ["sections"] = sections,
        };

    private static void AddCompactJsonFields(JsonObject payload, int compactLimit, JsonObject truncation)
    {
        payload["compact"] = true;
        payload["compact_limit"] = compactLimit;
        payload["truncation"] = truncation;
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
