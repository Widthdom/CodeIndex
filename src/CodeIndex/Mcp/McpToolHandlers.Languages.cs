using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    internal const int MinLanguageCatalogMaxBytes = MinResourceListMaxBytes;
    internal const int DefaultLanguageCatalogMaxBytes = DefaultResourceListMaxBytes;
    internal const int MaxLanguageCatalogMaxBytes = MaxResourceListMaxBytes;

    private JsonNode ExecuteLanguages(JsonNode? id, JsonNode? args)
    {
        var adjustments = new ArgumentAdjustmentCollector();
        var indexedOnly = args?["indexedOnly"]?.GetValue<bool>() ?? false;
        var capabilities = ReadStringOrArrayList(args, "capability")
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var languageFilter = NormalizeOptionalLookup(args?["language"]?.GetValue<string>());
        var extensionFilter = NormalizeOptionalLookup(args?["extension"]?.GetValue<string>());
        var aliasFilter = NormalizeOptionalLookup(args?["alias"]?.GetValue<string>());
        var normalizedLanguage = languageFilter is null
            ? null
            : CodeIndex.Database.DbReader.NormalizeQueryLanguage(languageFilter);
        var normalizedExtension = extensionFilter is null
            ? null
            : extensionFilter.StartsWith(".", StringComparison.Ordinal) ? extensionFilter : "." + extensionFilter;
        var normalizedAlias = aliasFilter is null ? null : LanguageCapabilityCatalog.NormalizeLookupKey(aliasFilter);
        var limit = ReadLimit(args, QueryCommandRunner.DefaultQueryLimit, adjustments);

        foreach (var capability in capabilities)
        {
            if (!IsKnownLanguageCapability(capability))
            {
                return CreateToolErrorResponse(
                    id,
                    $"Invalid language capability '{capability}'. Use one of: {string.Join(", ", LanguageCapabilityCatalog.SupportedCapabilities)}.");
            }
        }

        var requestedMaxBytes = DefaultLanguageCatalogMaxBytes;
        if (args?["maxBytes"] is JsonNode maxBytesNode
            && (maxBytesNode is not JsonValue maxBytesValue
                || !maxBytesValue.TryGetValue<int>(out requestedMaxBytes)
                || requestedMaxBytes < MinLanguageCatalogMaxBytes
                || requestedMaxBytes > MaxLanguageCatalogMaxBytes))
        {
            return CreateLanguageCatalogMaxBytesError(id);
        }

        var effectiveMaxBytes = Math.Min(requestedMaxBytes, GetMaxResponseBytes());
        var activeTransportMaxResponseBytes = Volatile.Read(ref _activeTransportMaxResponseBytes);
        if (activeTransportMaxResponseBytes > 0)
            effectiveMaxBytes = Math.Min(effectiveMaxBytes, activeTransportMaxResponseBytes);
        if (_currentBatchResponseItemMaxBytes.Value is { } batchResponseItemMaxBytes)
            effectiveMaxBytes = Math.Min(effectiveMaxBytes, batchResponseItemMaxBytes);

        McpQueryCursor? cursor = null;
        if (args?["cursor"] is JsonNode cursorNode)
        {
            if (cursorNode is not JsonValue cursorValue
                || !cursorValue.TryGetValue<string>(out var cursorText)
                || !TryParseMcpQueryCursor(cursorText, out cursor))
            {
                return CreateMcpCursorError(
                    id,
                    "languages",
                    "cursor_malformed",
                    "cursor must be an opaque response:v2 next_cursor returned by languages.",
                    stale: false);
            }
        }

        JsonNode BuildResponse(
            IReadOnlyDictionary<string, long>? indexedLanguageCounts,
            string? workspaceRoot,
            string? stableAt)
        {
            var catalog = LanguageCapabilityCatalog.Build(
                workspaceRoot,
                QueryCommandRunner.GetLanguageAliases);
            var filtered = catalog.Languages
                .Where(pair => !indexedOnly
                               || indexedLanguageCounts?.TryGetValue(pair.Key, out var indexedCount) == true
                               && indexedCount > 0)
                .Where(pair => capabilities.All(capability =>
                    LanguageCapabilityCatalog.MatchesCapability(pair.Value, capability)))
                .Where(pair => normalizedLanguage is null
                    || LanguageCapabilityCatalog.MatchesLanguage(pair.Key, normalizedLanguage))
                .Where(pair => normalizedExtension is null
                    || LanguageCapabilityCatalog.MatchesExtension(pair.Value, normalizedExtension))
                .Where(pair => aliasFilter is null
                    || LanguageCapabilityCatalog.MatchesAlias(pair.Value, aliasFilter)
                    || LanguageCapabilityCatalog.MatchesLanguage(pair.Key, aliasFilter))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();

            var queryFingerprint = BuildMcpQueryFingerprint(
                "languages",
                limit,
                "catalog-v2",
                [
                    new("alias", normalizedAlias),
                    new("extension", normalizedExtension is null
                        ? null
                        : LanguageCapabilityCatalog.NormalizeLookupKey(normalizedExtension)),
                    new("indexed-only", indexedOnly ? "true" : "false"),
                    new("language", normalizedLanguage),
                    new("max-bytes", requestedMaxBytes.ToString(CultureInfo.InvariantCulture)),
                    new("sort", "language-ordinal-v1"),
                ],
                ("capability", capabilities, PreserveOrder: false));
            var generation = (
                BuildLanguageCatalogGenerationFingerprint(
                    catalog,
                    indexedLanguageCounts),
                stableAt);

            if (ValidateMcpQueryCursor(
                    id,
                    "languages",
                    cursor,
                    queryFingerprint,
                    generation.Item1,
                    filtered.Count) is JsonObject cursorError)
            {
                return cursorError;
            }

            var offset = cursor?.Offset ?? 0;
            var availableCount = Math.Min(limit, filtered.Count - offset);
            for (var returnedCount = availableCount; returnedCount >= 0; returnedCount--)
            {
                if (returnedCount == 0 && availableCount > 0)
                {
                    return CreateLanguageCatalogEffectiveMaxBytesError(
                        id,
                        requestedMaxBytes,
                        effectiveMaxBytes);
                }

                var page = filtered.Skip(offset).Take(returnedCount).ToList();
                var byteBudgetReached = returnedCount < availableCount;
                var payload = BuildLanguageCatalogPayload(
                    page,
                    filtered,
                    catalog,
                    indexedLanguageCounts,
                    indexedOnly,
                    capabilities,
                    normalizedLanguage,
                    normalizedExtension,
                    aliasFilter,
                    requestedMaxBytes,
                    effectiveMaxBytes,
                    byteBudgetReached);
                AddMcpPaginationEnvelope(
                    payload,
                    filtered.Count,
                    returnedCount,
                    offset,
                    limit,
                    queryFingerprint,
                    generation);
                payload["continuation_reason"] = byteBudgetReached
                    ? "byte_budget"
                    : offset + returnedCount < filtered.Count ? "item_limit" : "complete";
                adjustments.ApplyTo(payload);

                var summary = $"{returnedCount} of {filtered.Count} matching languages returned; "
                    + $"{catalog.Languages.Count} languages are in the catalog.";
                var response = CreateLanguageCatalogToolResult(id, summary, payload);
                if (TryMeasureJsonUtf8BytesWithinLimit(
                        response,
                        _jsonOptions,
                        effectiveMaxBytes,
                        out _))
                {
                    return response;
                }
            }

            return CreateLanguageCatalogEffectiveMaxBytesError(
                id,
                requestedMaxBytes,
                effectiveMaxBytes);
        }

        var configuredDatabaseAvailable = _dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_dbPath, ":memory:", StringComparison.Ordinal)
            || File.Exists(LongPath.EnsureWindowsPrefix(_dbPath));
        if (!indexedOnly && !configuredDatabaseAvailable)
            return BuildResponse(null, workspaceRoot: null, stableAt: null);

        return WithDbReader(id, args, reader =>
        {
            var status = reader.GetStatus(includeDatabaseSizeAttribution: false);
            return BuildResponse(
                status.Languages,
                reader.GetIndexedProjectRoot(),
                reader.GetPaginationGeneration().StableAt);
        });
    }

    private JsonObject BuildLanguageCatalogPayload(
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> page,
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> filtered,
        LanguageCapabilityCatalogSnapshot catalog,
        IReadOnlyDictionary<string, long>? indexedLanguageCounts,
        bool indexedOnly,
        IReadOnlyList<string> capabilities,
        string? languageFilter,
        string? extensionFilter,
        string? aliasFilter,
        int requestedMaxBytes,
        int effectiveMaxBytes,
        bool byteBudgetReached)
    {
        var scopedCounts = LanguageCapabilityCatalog.Count(
            catalog.Languages,
            filtered,
            indexedLanguageCounts);
        var payload = new JsonObject
        {
            ["languages"] = new JsonArray(page.Select(pair =>
                BuildLanguageCatalogEntry(pair.Key, pair.Value, indexedLanguageCounts)).ToArray()),
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
                ["language"] = languageFilter,
                ["extension"] = extensionFilter,
                ["alias"] = aliasFilter,
            },
            ["summary"] = new JsonObject
            {
                ["catalog_language_count"] = catalog.Languages.Count,
                ["filtered_language_count"] = filtered.Count,
                ["symbol_extraction_language_count"] = catalog.Languages.Count(pair => pair.Value.Symbols),
                ["reference_extraction_language_count"] = catalog.Languages.Count(pair => pair.Value.References),
                ["graph_query_language_count"] = catalog.Languages.Count(pair => pair.Value.Graph),
            },
            ["response_budget"] = new JsonObject
            {
                ["scope"] = "json_rpc_envelope",
                ["requested_max_bytes"] = requestedMaxBytes,
                ["effective_max_bytes"] = effectiveMaxBytes,
                ["byte_budget_reached"] = byteBudgetReached,
            },
        };

        if (languageFilter is not null)
        {
            payload["language_lookup"] = BuildLanguageLookup(
                "language",
                languageFilter,
                filtered);
        }
        if (extensionFilter is not null)
        {
            payload["extension_lookup"] = LanguageCapabilityCatalog.BuildExtensionLookup(
                extensionFilter,
                filtered,
                catalog.Languages);
        }
        if (aliasFilter is not null)
        {
            payload["alias_lookup"] = BuildLanguageLookup(
                "alias",
                aliasFilter,
                filtered);
        }

        return payload;
    }

    private static JsonObject BuildLanguageCatalogEntry(
        string language,
        LanguageCatalogSupportInfo entry,
        IReadOnlyDictionary<string, long>? indexedLanguageCounts)
    {
        var result = new JsonObject
        {
            ["lang"] = language,
            ["extensions"] = BuildLanguageStringArray(entry.Extensions),
            ["exact_filenames"] = BuildLanguageStringArray(entry.ExactFilenames),
            ["filename_prefix_patterns"] = BuildLanguageStringArray(entry.FilenamePrefixPatterns),
            ["legacy_patterns"] = BuildLanguageStringArray(entry.LegacyPatterns),
            ["pattern_provenance"] = new JsonArray(entry.PatternProvenance
                .OrderBy(pattern => pattern.Kind)
                .ThenBy(pattern => pattern.Pattern, StringComparer.Ordinal)
                .Select(pattern => (JsonNode)new JsonObject
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
                }).ToArray()),
            ["aliases"] = BuildLanguageStringArray(entry.Aliases),
            ["detection"] = entry.Detection,
            ["symbol_extraction"] = entry.Symbols,
            ["reference_extraction"] = entry.References,
            ["outline"] = entry.Outline,
            ["graph_queries"] = entry.Graph,
            ["capability_gaps"] = BuildLanguageStringArray(entry.CapabilityGaps),
            ["unsupported_guidance"] = new JsonArray(entry.UnsupportedGuidance.Select(guidance => (JsonNode)new JsonObject
            {
                ["capability"] = guidance.Capability,
                ["message"] = guidance.Message,
                ["recommended_commands"] = new JsonArray(guidance.RecommendedCommands
                    .Select(command => JsonValue.Create(command)).ToArray()),
            }).ToArray()),
        };
        if (indexedLanguageCounts is not null)
        {
            result["indexed_file_count"] = indexedLanguageCounts.TryGetValue(language, out var count)
                ? count
                : 0;
        }
        return result;
    }

    private static JsonObject BuildLanguageLookup(
        string propertyName,
        string value,
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> matches)
        => new()
        {
            [propertyName] = value,
            ["matched"] = matches.Count,
            ["languages"] = new JsonArray(matches.Select(pair => JsonValue.Create(pair.Key)).ToArray()),
        };

    private static JsonArray BuildLanguageStringArray(IEnumerable<string> values)
        => new(values
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => JsonValue.Create(value))
            .ToArray());

    private JsonObject CreateLanguageCatalogToolResult(
        JsonNode? id,
        string summary,
        JsonObject payload)
    {
        EnrichToolStructuredContent(payload);
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["mimeType"] = "application/json",
                    ["text"] = summary,
                },
            },
            ["structuredContent"] = payload,
        };
        return CreateSuccessResponse(hasId: true, id: id, result: result);
    }

    private static string BuildLanguageCatalogGenerationFingerprint(
        LanguageCapabilityCatalogSnapshot catalog,
        IReadOnlyDictionary<string, long>? indexedLanguageCounts)
    {
        var components = new List<string?>
        {
            "mcp-language-catalog-generation:v2",
        };
        foreach (var (language, entry) in catalog.Languages)
        {
            components.Add("language:" + language);
            components.AddRange(entry.Extensions.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => "extension:" + value));
            components.AddRange(entry.ExactFilenames.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => "exact-filename:" + value));
            components.AddRange(entry.FilenamePrefixPatterns.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => "filename-prefix:" + value));
            components.AddRange(entry.Aliases.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => "alias:" + value));
            components.AddRange(entry.CapabilityGaps.OrderBy(value => value, StringComparer.Ordinal)
                .Select(value => "capability-gap:" + value));
            components.Add("detection:" + entry.Detection);
            components.Add("symbols:" + entry.Symbols);
            components.Add("references:" + entry.References);
            components.Add("outline:" + entry.Outline);
            components.Add("graph:" + entry.Graph);
            foreach (var pattern in entry.PatternProvenance
                         .OrderBy(value => value.Kind)
                         .ThenBy(value => value.Pattern, StringComparer.Ordinal))
            {
                components.Add("pattern-kind:" + pattern.Kind);
                components.Add("pattern:" + pattern.Pattern);
                components.Add("pattern-source:" + pattern.Source);
            }
            foreach (var guidance in entry.UnsupportedGuidance
                         .OrderBy(value => value.Capability, StringComparer.Ordinal)
                         .ThenBy(value => value.Message, StringComparer.Ordinal))
            {
                components.Add("guidance-capability:" + guidance.Capability);
                components.Add("guidance-message:" + guidance.Message);
                components.AddRange(guidance.RecommendedCommands
                    .Select(value => "guidance-command:" + value));
            }
        }

        foreach (var diagnostic in catalog.Diagnostics
                     .OrderBy(value => value.Code, StringComparer.Ordinal)
                     .ThenBy(value => value.Config, StringComparer.Ordinal))
        {
            components.Add("diagnostic-code:" + diagnostic.Code);
            components.Add("diagnostic-config:" + diagnostic.Config);
            components.Add("diagnostic-reason:" + diagnostic.Reason);
            components.Add("diagnostic-blocks-parent:" + diagnostic.BlocksParentFallback);
        }

        if (indexedLanguageCounts is not null)
        {
            components.AddRange(indexedLanguageCounts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"indexed-language:{pair.Key}:{pair.Value.ToString(CultureInfo.InvariantCulture)}"));
        }

        return InspectGraphCursorCodec.BuildQueryFingerprint(components);
    }

    private static string? NormalizeOptionalLookup(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private JsonObject CreateLanguageCatalogMaxBytesError(JsonNode? id)
        => CreateToolErrorResponse(
            id,
            $"languages maxBytes must be between {MinLanguageCatalogMaxBytes} and {MaxLanguageCatalogMaxBytes}.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Choose a maxBytes value inside the advertised languages schema range.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["error_code"] = "invalid_max_bytes",
                ["min_max_bytes"] = MinLanguageCatalogMaxBytes,
                ["max_max_bytes"] = MaxLanguageCatalogMaxBytes,
                ["default_max_bytes"] = DefaultLanguageCatalogMaxBytes,
            });

    private JsonObject CreateLanguageCatalogEffectiveMaxBytesError(
        JsonNode? id,
        int requestedMaxBytes,
        int effectiveMaxBytes)
        => CreateToolErrorResponse(
            id,
            "languages maxBytes is too small for the response metadata and one catalog entry.",
            category: McpErrorEnvelope.CategoryInvalidArgument,
            suggestion: "Increase maxBytes and restart languages pagination without cursor.",
            retrySafe: true,
            extraData: new JsonObject
            {
                ["error_code"] = "max_bytes_too_small",
                ["requested_max_bytes"] = requestedMaxBytes,
                ["effective_max_bytes"] = effectiveMaxBytes,
                ["restart_required"] = true,
            });
}
