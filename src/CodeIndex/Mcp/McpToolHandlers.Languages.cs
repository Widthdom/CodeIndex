using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
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

        JsonNode BuildResponse(
            IReadOnlyDictionary<string, long>? indexedLanguageCounts,
            string? workspaceRoot)
        {
            var catalog = LanguageCapabilityCatalog.Build(
                workspaceRoot,
                QueryCommandRunner.GetLanguageAliases);
            var sorted = catalog.Languages
                .Where(kv => !indexedOnly
                             || (indexedLanguageCounts?.TryGetValue(kv.Key, out var indexedCount) == true
                                 && indexedCount > 0))
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

                var exactFilenameArray = new JsonArray();
                foreach (var filename in info.ExactFilenames.OrderBy(value => value, StringComparer.Ordinal))
                    exactFilenameArray.Add(filename);

                var filenamePrefixPatternArray = new JsonArray();
                foreach (var pattern in info.FilenamePrefixPatterns.OrderBy(value => value, StringComparer.Ordinal))
                    filenamePrefixPatternArray.Add(pattern);

                var legacyPatternArray = new JsonArray();
                foreach (var pattern in info.LegacyPatterns.OrderBy(value => value, StringComparer.Ordinal))
                    legacyPatternArray.Add(pattern);

                var provenanceArray = new JsonArray();
                foreach (var pattern in info.PatternProvenance
                    .OrderBy(value => value.Kind)
                    .ThenBy(value => value.Pattern, StringComparer.Ordinal))
                {
                    provenanceArray.Add(new JsonObject
                    {
                        ["pattern"] = pattern.Pattern,
                        ["kind"] = pattern.Kind switch
                        {
                            FileIndexer.LanguagePatternKind.Extension => "extension",
                            FileIndexer.LanguagePatternKind.ExactFilename => "exact_filename",
                            FileIndexer.LanguagePatternKind.FilenamePrefixPattern => "filename_prefix_pattern",
                            _ => throw new ArgumentOutOfRangeException(nameof(pattern.Kind), pattern.Kind, null),
                        },
                        ["source"] = pattern.Source,
                    });
                }

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

                var languageRow = new JsonObject
                {
                    ["lang"] = lang,
                    ["extensions"] = extArray,
                    ["exact_filenames"] = exactFilenameArray,
                    ["filename_prefix_patterns"] = filenamePrefixPatternArray,
                    ["legacy_patterns"] = legacyPatternArray,
                    ["pattern_provenance"] = provenanceArray,
                    ["aliases"] = new JsonArray(info.Aliases.OrderBy(alias => alias, StringComparer.Ordinal).Select(alias => JsonValue.Create(alias)).ToArray()),
                    ["detection"] = info.Detection,
                    ["symbol_extraction"] = info.Symbols,
                    ["reference_extraction"] = info.References,
                    ["outline"] = info.Outline,
                    ["graph_queries"] = info.Graph,
                    ["capability_gaps"] = new JsonArray(info.CapabilityGaps.Select(gap => JsonValue.Create(gap)).ToArray()),
                    ["unsupported_guidance"] = guidanceArray,
                };
                if (indexedLanguageCounts is not null)
                {
                    languageRow["indexed_file_count"] = indexedLanguageCounts.TryGetValue(lang, out var indexedFileCount)
                        ? indexedFileCount
                        : 0;
                }
                languagesArray.Add(languageRow);
            }

            var scopedCounts = LanguageCapabilityCatalog.Count(
                catalog.Languages,
                sorted,
                indexedLanguageCounts);
            var payload = new JsonObject
            {
                ["api_version"] = JsonOutputContract.ApiVersion,
                ["languages"] = languagesArray,
                ["language_capability_counts"] = scopedCounts.ToJson(),
                ["detection_policy"] = new JsonObject
                {
                    ["filename_case_policy"] = "filesystem",
                    ["filename_case_source"] = "path_case_sensitive",
                    ["extension_case_policy"] = "case_insensitive",
                    ["precedence"] = new JsonArray(QueryCommandRunner.LanguageDetectionPrecedence
                        .Select(value => JsonValue.Create(value)).ToArray()),
                },
                ["language_map_diagnostics"] = new JsonArray(catalog.Diagnostics.Select(diagnostic => (JsonNode)new JsonObject
                {
                    ["code"] = diagnostic.Code,
                    ["config"] = diagnostic.Config,
                    ["reason"] = diagnostic.Reason,
                    ["blocks_parent_fallback"] = diagnostic.BlocksParentFallback,
                }).ToArray()),
                ["reference_extraction_limits"] = JsonSerializer.SerializeToNode(
                    ReferenceExtractor.GetSafetyLimits(),
                    _jsonOptions),
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

            return CreateToolResult(
                id,
                $"{sorted.Count} languages supported in matched catalog. {scopedCounts.FormatSummary()}.",
                payload);
        }

        var configuredDatabaseAvailable = _dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_dbPath, ":memory:", StringComparison.Ordinal)
            || File.Exists(LongPath.EnsureWindowsPrefix(_dbPath));
        if (!indexedOnly && !configuredDatabaseAvailable)
            return BuildResponse(null, workspaceRoot: null);

        return WithDbReader(id, args, reader =>
        {
            var status = reader.GetStatus(includeDatabaseSizeAttribution: false);
            return BuildResponse(status.Languages, reader.GetIndexedProjectRoot());
        });
    }
}
