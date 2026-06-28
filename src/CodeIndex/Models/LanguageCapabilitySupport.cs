using System.Text;
using System.Text.Json.Serialization;

namespace CodeIndex.Models;

internal sealed record LanguageUnsupportedGuidance(
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("recommended_commands")] List<string> RecommendedCommands);

internal static class LanguageCapabilitySupport
{
    public static List<string> BuildGaps(bool symbols, bool references, bool graph)
    {
        var gaps = new List<string>();
        if (!symbols)
            gaps.Add("missing-symbols");
        if (!references)
            gaps.Add("missing-references");
        if (!graph)
            gaps.Add("missing-graph");
        return gaps;
    }

    public static List<LanguageUnsupportedGuidance> BuildUnsupportedGuidance(
        string language,
        bool symbols,
        bool references,
        bool graph)
    {
        var guidance = new List<LanguageUnsupportedGuidance>();
        var fallbackCommands = BuildFallbackCommands(symbols);
        var fallbackList = FormatCommandList(fallbackCommands);

        if (!symbols)
        {
            guidance.Add(new LanguageUnsupportedGuidance(
                "symbols",
                $"Symbol extraction is not advertised for '{language}'; use {fallbackList} instead.",
                fallbackCommands));
        }
        if (!references)
        {
            guidance.Add(new LanguageUnsupportedGuidance(
                "references",
                $"Reference extraction is not advertised for '{language}'; empty references are not authoritative. Use {fallbackList} instead.",
                fallbackCommands));
        }
        if (!graph)
        {
            guidance.Add(new LanguageUnsupportedGuidance(
                "graph",
                $"Graph queries are not advertised for '{language}'; empty callers, callees, or impact results are not authoritative. Use {fallbackList} instead.",
                fallbackCommands));
        }

        return guidance;
    }

    private static List<string> BuildFallbackCommands(bool symbols)
        => symbols
            ? ["search", "symbols", "definition", "outline", "excerpt", "files"]
            : ["search", "files", "excerpt"];

    private static string FormatCommandList(IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
            return string.Empty;
        if (commands.Count == 1)
            return commands[0];
        if (commands.Count == 2)
            return $"{commands[0]} or {commands[1]}";

        var builder = new StringBuilder();
        for (var i = 0; i < commands.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(i == commands.Count - 1 ? ", or " : ", ");
            }
            builder.Append(commands[i]);
        }
        return builder.ToString();
    }
}
