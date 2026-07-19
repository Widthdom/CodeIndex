using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private JsonNode ExecuteLanguages(JsonNode? id, JsonNode? args)
    {
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

        (Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool References, bool Graph, List<string> CapabilityGaps, List<LanguageUnsupportedGuidance> UnsupportedGuidance)> Languages, int SymbolLanguageCount, int ReferenceLanguageCount) BuildCatalog(string? workspaceRoot)
        {
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceRoot);
            var langExtensions = FileIndexer.GetLanguageExtensions(workspaceRoot);
            var symbolLangs = SymbolExtractor.GetSupportedLanguages(workspaceRoot);
            var referenceLangs = ReferenceExtractor.GetSupportedLanguages(workspaceRoot);
            var languages = new Dictionary<string, (List<string> Extensions, List<string> Aliases, bool Symbols, bool References, bool Graph, List<string> CapabilityGaps, List<LanguageUnsupportedGuidance> UnsupportedGuidance)>(StringComparer.Ordinal);
            foreach (var (ext, lang) in langExtensions)
            {
                if (!languages.TryGetValue(lang, out var info))
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
                    languages[lang] = info;
                }
                info.Extensions.Add(ext);
            }

            foreach (var lang in FileIndexer.GetContentDetectedLanguageBuckets())
            {
                if (languages.ContainsKey(lang))
                    continue;

                var hasSymbols = symbolLangs.Contains(lang);
                var hasReferences = referenceLangs.Contains(lang);
                languages[lang] = (
                    new List<string>(),
                    QueryCommandRunner.GetLanguageAliases(lang).ToList(),
                    hasSymbols,
                    hasReferences,
                    hasReferences,
                    LanguageCapabilitySupport.BuildGaps(hasSymbols, hasReferences, hasReferences),
                    LanguageCapabilitySupport.BuildUnsupportedGuidance(lang, hasSymbols, hasReferences, hasReferences));
            }

            return (languages, symbolLangs.Count, referenceLangs.Count);
        }

        JsonNode BuildResponse(HashSet<string>? indexedLanguages, string? workspaceRoot)
        {
            var catalog = BuildCatalog(workspaceRoot);
            var sorted = catalog.Languages
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
                ["detection_policy"] = new JsonObject
                {
                    ["filename_case_policy"] = "filesystem",
                    ["filename_case_source"] = "path_case_sensitive",
                    ["extension_case_policy"] = "case_insensitive",
                },
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

            var summary = $"{sorted.Count} languages supported. {catalog.SymbolLanguageCount} with symbol extraction, {catalog.ReferenceLanguageCount} with reference extraction, {catalog.ReferenceLanguageCount} with call-graph queries.";
            return CreateToolResult(id, summary, payload);
        }

        var configuredDatabaseAvailable = _dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_dbPath, ":memory:", StringComparison.Ordinal)
            || File.Exists(LongPath.EnsureWindowsPrefix(_dbPath));
        if (!indexedOnly && !configuredDatabaseAvailable)
            return BuildResponse(null, workspaceRoot: null);

        return WithDbReader(id, args, reader =>
        {
            var status = reader.GetStatus();
            var indexedLanguages = indexedOnly
                ? new HashSet<string>(status.Languages.Keys, StringComparer.Ordinal)
                : null;
            return BuildResponse(indexedLanguages, reader.GetIndexedProjectRoot());
        });
    }


}
