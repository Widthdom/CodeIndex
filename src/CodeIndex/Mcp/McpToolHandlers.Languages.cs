using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteLanguages(JsonNode? id, JsonNode? args)
    {
        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var referenceLangs = ReferenceExtractor.GetSupportedLanguages();
        var indexedOnly = args?["indexedOnly"]?.GetValue<bool>() ?? false;
        var capabilities = ReadStringOrArrayList(args, "capability")
            .Select(value => value.Trim().ToLowerInvariant())
            .ToList();
        var extensionFilter = args?["extension"]?.GetValue<string>()?.Trim();
        var normalizedExtension = string.IsNullOrWhiteSpace(extensionFilter)
            ? null
            : extensionFilter.StartsWith(".", StringComparison.Ordinal) ? extensionFilter : "." + extensionFilter;
        var aliasFilter = QueryCommandRunner.NormalizeLangFilterValue(args?["alias"]?.GetValue<string>());

        if (args?["capability"] is JsonArray capabilityArray && capabilities.Count != capabilityArray.Count)
            return CreateToolErrorResponse(id, "capability entries must be non-empty strings.");

        foreach (var capability in capabilities)
        {
            if (!IsKnownLanguageCapability(capability))
                return CreateToolErrorResponse(id, $"Invalid language capability '{capability}'. Use one of: symbols, graph, references.");
        }

        // Build consolidated language info / 統合言語情報を構築
        var allLangs = new Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool References, bool Graph, List<string> CapabilityGaps, List<LanguageUnsupportedGuidance> UnsupportedGuidance)>(StringComparer.Ordinal);
        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                var hasSymbols = symbolLangs.Contains(lang);
                var hasReferences = referenceLangs.Contains(lang);
                info = (
                    new List<string>(),
                    QueryCommandRunner.GetLanguageAliases(lang).ToList(),
                    hasSymbols,
                    hasReferences,
                    hasReferences,
                    LanguageCapabilitySupport.BuildGaps(hasSymbols, hasReferences, hasReferences),
                    LanguageCapabilitySupport.BuildUnsupportedGuidance(lang, hasSymbols, hasReferences, hasReferences));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        JsonNode BuildResponse(HashSet<string>? indexedLanguages)
        {
            var sorted = allLangs
                .Where(kv => !indexedOnly || indexedLanguages?.Contains(kv.Key) == true)
                .Where(kv => capabilities.All(capability => LanguageMatchesCapability(kv.Value.Symbols, kv.Value.References, kv.Value.Graph, capability)))
                .Where(kv => normalizedExtension is null || kv.Value.Extensions.Contains(normalizedExtension, StringComparer.OrdinalIgnoreCase))
                .Where(kv => aliasFilter is null
                    || string.Equals(kv.Key, aliasFilter, StringComparison.OrdinalIgnoreCase)
                    || kv.Value.Aliases.Contains(aliasFilter, StringComparer.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            var languagesArray = new JsonArray();
            foreach (var (lang, info) in sorted)
            {
                var extArray = new JsonArray();
                foreach (var ext in info.Extensions.OrderBy(e => e, StringComparer.Ordinal))
                    extArray.Add(ext);

                var guidanceArray = new JsonArray();
                foreach (var guidance in info.UnsupportedGuidance)
                {
                    guidanceArray.Add(new JsonObject
                    {
                        ["capability"] = guidance.Capability,
                        ["message"] = guidance.Message,
                        ["recommended_commands"] = new JsonArray(guidance.RecommendedCommands.Select(command => JsonValue.Create(command)).ToArray()),
                    });
                }

                languagesArray.Add(new JsonObject
                {
                    ["lang"] = lang,
                    ["extensions"] = extArray,
                    ["aliases"] = new JsonArray(info.Aliases.OrderBy(alias => alias, StringComparer.Ordinal).Select(alias => JsonValue.Create(alias)).ToArray()),
                    ["symbol_extraction"] = info.Symbols,
                    ["reference_extraction"] = info.References,
                    ["graph_queries"] = info.Graph,
                    ["capability_gaps"] = new JsonArray(info.CapabilityGaps.Select(gap => JsonValue.Create(gap)).ToArray()),
                    ["unsupported_guidance"] = guidanceArray,
                });
            }

            var payload = new JsonObject
            {
                ["languages"] = languagesArray,
                ["filters"] = new JsonObject
                {
                    ["indexedOnly"] = indexedOnly,
                    ["capability"] = new JsonArray(capabilities.Select(capability => JsonValue.Create(capability)).ToArray()),
                    ["extension"] = normalizedExtension,
                    ["alias"] = aliasFilter,
                },
            };
            if (normalizedExtension is not null)
            {
                payload["extension_lookup"] = new JsonObject
                {
                    ["extension"] = normalizedExtension,
                    ["matched"] = sorted.Count,
                    ["languages"] = new JsonArray(sorted.Select(kv => JsonValue.Create(kv.Key)).ToArray()),
                };
            }
            if (aliasFilter is not null)
            {
                payload["alias_lookup"] = new JsonObject
                {
                    ["alias"] = aliasFilter,
                    ["matched"] = sorted.Count,
                    ["languages"] = new JsonArray(sorted.Select(kv => JsonValue.Create(kv.Key)).ToArray()),
                };
            }

            var summary = $"{sorted.Count} languages supported. {symbolLangs.Count} with symbol extraction, {referenceLangs.Count} with reference extraction, {referenceLangs.Count} with call-graph queries.";
            return CreateToolResult(id, summary, payload);
        }

        if (!indexedOnly)
            return BuildResponse(null);

        return WithDbReader(id, args, reader => BuildResponse(new HashSet<string>(reader.GetStatus().Languages.Keys, StringComparer.Ordinal)));
    }


}
