using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    public static int RunLanguages(string[] cmdArgs, JsonSerializerOptions jsonOptions)
    {
        var options = ParseArgs(
            cmdArgs,
            jsonDefault: false,
            validateDefaultLimit: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);
        if (TryWriteUnsupportedOptionError("languages", cmdArgs, CliFlagSchema.GetAcceptedFlagNamesForCommand("languages")))
            return CommandExitCodes.UsageError;
        if (TryWriteParseError(options, "languages", jsonOptions))
            return CommandExitCodes.UsageError;
        if (TryWriteUnexpectedPositionals("languages", options))
            return CommandExitCodes.UsageError;
        if (TryWriteUnsupportedOutputFormat("languages", options, LanguageOutputFormats, "Use `--format json` for language rows or `--format count` for aggregate capability totals."))
            return CommandExitCodes.UsageError;
        var json = options.Json || options.CountOnly || options.OutputFormat == OutputFormatCount || options.SummaryOnly;

        var loadIndexedCounts = options.LanguagesIndexedOnly || ShouldLoadLanguageIndexedCounts(options);
        var configuredDatabaseAvailable = options.DbPathExplicit
            || options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath));
        if (configuredDatabaseAvailable || loadIndexedCounts)
        {
            return WithDb(options, jsonOptions, reader =>
            {
                var status = reader.GetStatus(includeDatabaseSizeAttribution: false);
                var catalog = LanguageCatalog.Build(reader.GetIndexedProjectRoot());
                var indexedLanguageCounts = loadIndexedCounts ? status.Languages : null;
                return WriteLanguages(
                    SelectLanguages(catalog.Languages, indexedLanguageCounts),
                    catalog.Languages.Count,
                    indexedLanguageCounts,
                    catalog.Diagnostics);
            });
        }

        var defaultCatalog = LanguageCatalog.Build(workspaceRoot: null);
        return WriteLanguages(
            SelectLanguages(defaultCatalog.Languages, indexedLanguageCounts: null),
            defaultCatalog.Languages.Count,
            indexedLanguageCounts: null,
            defaultCatalog.Diagnostics);

        IEnumerable<KeyValuePair<string, LanguageCatalogEntry>> SelectLanguages(
            IEnumerable<KeyValuePair<string, LanguageCatalogEntry>> languages,
            IReadOnlyDictionary<string, long>? indexedLanguageCounts)
        {
            var selected = languages;
            if (options.LanguagesIndexedOnly)
                selected = selected.Where(kv => indexedLanguageCounts?.ContainsKey(kv.Key) == true);
            if (HasLanguageLookup(options))
                selected = selected.Where(kv => LanguageMatchesLookup(kv.Key, kv.Value, options));
            return selected;
        }

        int WriteLanguages(
            IEnumerable<KeyValuePair<string, LanguageCatalogEntry>> languages,
            int totalLanguageCount,
            IReadOnlyDictionary<string, long>? indexedLanguageCounts,
            IReadOnlyList<LanguageMapOverrides.Diagnostic> languageMapDiagnostics)
        {
            var filtered = languages
                .Where(kv => options.LanguageCapabilities.All(capability => LanguageMatchesCapability(kv.Value, capability)))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();
            if (json && (options.SummaryOnly || options.CountOnly || options.OutputFormat == OutputFormatCount))
            {
                var payload = BuildLanguageSummaryPayload(
                    filtered,
                    totalLanguageCount,
                    indexedLanguageCounts,
                    options,
                    languageMapDiagnostics);
                payload["reference_extraction_limits"] = JsonSerializer.SerializeToNode(
                    ReferenceExtractor.GetSafetyLimits(),
                    CliJsonSerializerContextFactory.Create(jsonOptions).ReferenceExtractionSafetyLimits);
                AddActiveSqliteDiagnostics(payload);
                CommandOutputWriter.WriteJsonNode(payload, jsonOptions);
                return CommandExitCodes.Success;
            }

            var boundedLimit = JsonEnvelopeWrapper.GetBoundedResponseLimit("languages");
            if (!boundedLimit.HasValue
                && cmdArgs.Any(arg => string.Equals(arg, "--limit", StringComparison.Ordinal)
                                      || arg.StartsWith("--limit=", StringComparison.Ordinal)
                                      || string.Equals(arg, "--top", StringComparison.Ordinal)
                                      || arg.StartsWith("--top=", StringComparison.Ordinal)))
            {
                boundedLimit = options.Limit;
            }
            if (boundedLimit.HasValue)
            {
                filtered = filtered
                    .Skip(JsonEnvelopeWrapper.GetBoundedResponseOffset("languages"))
                    .Take(boundedLimit.Value)
                    .ToList();
            }

            if (json)
            {
                var entries = filtered.Select(kv => new LanguageEntryJsonResult(
                    kv.Key,
                    kv.Value.Extensions.OrderBy(e => e).ToList(),
                    kv.Value.ExactFilenames.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    kv.Value.FilenamePrefixPatterns.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    kv.Value.LegacyPatterns.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                    kv.Value.PatternProvenance
                        .OrderBy(value => GetLanguagePatternKindName(value.Kind), StringComparer.Ordinal)
                        .ThenBy(value => value.Pattern, StringComparer.Ordinal)
                        .Select(value => new LanguagePatternProvenanceJsonResult(
                            value.Pattern,
                            GetLanguagePatternKindName(value.Kind),
                            value.Source))
                        .ToList(),
                    kv.Value.Aliases.OrderBy(a => a).ToList(),
                    kv.Value.Symbols,
                    kv.Value.References,
                    kv.Value.Graph,
                    kv.Value.CapabilityGaps,
                    kv.Value.UnsupportedGuidance,
                    GetIndexedLanguageCount(indexedLanguageCounts, kv.Key))).ToList();
                Console.WriteLine(SerializeQueryJson(
                    new LanguagesJsonResult(
                        entries,
                        BuildLanguageDetectionPolicy(),
                        BuildLanguageMapDiagnostics(languageMapDiagnostics),
                        ReferenceExtractor.GetSafetyLimits()),
                    CliJsonSerializerContextFactory.Create(jsonOptions).LanguagesJsonResult,
                    jsonOptions));
            }
            else
            {
                // Fixed-width Patterns column for short lists; spill long lists onto a continuation
                // line so the Symbols / Graph columns are never swallowed by a wide extension string.
                // 拡張子が短い場合は固定幅テーブル、長い場合は継続行に退避させることで、
                // Symbols / Graph 列が拡張子文字列に埋もれないようにする。
                const int ExtensionColumnWidth = 36;
                const int AliasColumnWidth = 12;
                var showIndexedCounts = indexedLanguageCounts != null;
                if (showIndexedCounts)
                {
                    Console.WriteLine($"{"Language",-14} {"Patterns",-36} {"Aliases",-12} {"Indexed",-7} {"Symbols",-9} {"Refs",-5} {"Graph",-7}");
                    Console.WriteLine(new string('-', 93));
                }
                else
                {
                    Console.WriteLine($"{"Language",-14} {"Patterns",-36} {"Aliases",-12} {"Symbols",-9} {"Refs",-5} {"Graph",-7}");
                    Console.WriteLine(new string('-', 85));
                }
                foreach (var (lang, info) in filtered)
                {
                    var exts = string.Join(" ", info.LegacyPatterns.OrderBy(e => e));
                    var aliases = string.Join(" ", info.Aliases.OrderBy(a => a));
                    var aliasCell = string.IsNullOrWhiteSpace(aliases) ? "-" : aliases;
                    var indexedCount = GetIndexedLanguageCount(indexedLanguageCounts, lang);
                    var indexedCell = indexedCount?.ToString(CultureInfo.InvariantCulture) ?? "-";
                    var sym = info.Symbols ? "yes" : "-";
                    var refs = info.References ? "yes" : "-";
                    var graph = info.Graph ? "yes" : "-";
                    if (exts.Length <= ExtensionColumnWidth && aliases.Length <= AliasColumnWidth)
                    {
                        if (showIndexedCounts)
                            Console.WriteLine($"{lang,-14} {exts,-36} {aliasCell,-12} {indexedCell,-7} {sym,-9} {refs,-5} {graph,-7}");
                        else
                            Console.WriteLine($"{lang,-14} {exts,-36} {aliasCell,-12} {sym,-9} {refs,-5} {graph,-7}");
                    }
                    else
                    {
                        if (showIndexedCounts)
                            Console.WriteLine($"{lang,-14} {"",-36} {"",-12} {indexedCell,-7} {sym,-9} {refs,-5} {graph,-7}");
                        else
                            Console.WriteLine($"{lang,-14} {"",-36} {"",-12} {sym,-9} {refs,-5} {graph,-7}");
                        Console.WriteLine($"  Patterns: {exts}");
                        if (!string.IsNullOrWhiteSpace(aliases))
                            Console.WriteLine($"  Aliases: {aliases}");
                        if (info.CapabilityGaps.Count > 0)
                            Console.WriteLine($"  Gaps: {string.Join(", ", info.CapabilityGaps)}");
                    }
                }
                CommandErrorWriter.WriteStderr($"\n({filtered.Count} languages)");
            }

            return CommandExitCodes.Success;
        }
    }

    private static string GetLanguagePatternKindName(FileIndexer.LanguagePatternKind kind)
        => kind switch
        {
            FileIndexer.LanguagePatternKind.Extension => "extension",
            FileIndexer.LanguagePatternKind.ExactFilename => "exact_filename",
            FileIndexer.LanguagePatternKind.FilenamePrefixPattern => "filename_prefix_pattern",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    internal static readonly string[] LanguageDetectionPrecedence =
    [
        "language_map_override",
        "exact_filename",
        "filename_prefix_pattern",
        "ambiguous_extension_shebang",
        "ambiguous_extension_content_or_project",
        "built_in_extension",
        "plugin_extension",
        "unknown_extension_shebang",
    ];

    private static LanguageDetectionPolicyJsonResult BuildLanguageDetectionPolicy()
        => new(
            FilenameCasePolicy: "filesystem",
            FilenameCaseSource: "path_case_sensitive",
            ExtensionCasePolicy: "case_insensitive",
            Precedence: LanguageDetectionPrecedence.ToList());

    private static List<LanguageMapDiagnosticJsonResult> BuildLanguageMapDiagnostics(
        IReadOnlyList<LanguageMapOverrides.Diagnostic> diagnostics)
        => diagnostics.Select(diagnostic => new LanguageMapDiagnosticJsonResult(
            diagnostic.Code,
            diagnostic.Config,
            diagnostic.Reason,
            diagnostic.BlocksParentFallback)).ToList();

    private static bool HasLanguageLookup(QueryCommandOptions options)
        => options.LanguageLookups.Count > 0 || options.LanguageExtensionLookups.Count > 0 || options.LanguageAliasLookups.Count > 0;

    private static bool ShouldLoadLanguageIndexedCounts(QueryCommandOptions options)
    {
        var shouldLoadForDbBackedSummary = options.SummaryOnly || options.CountOnly || options.OutputFormat == OutputFormatCount;
        if (!HasLanguageLookup(options) && !shouldLoadForDbBackedSummary)
            return false;
        if (options.DbPathExplicit)
            return true;
        if (options.DbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(LongPath.EnsureWindowsPrefix(options.DbPath));
    }

    private static long? GetIndexedLanguageCount(IReadOnlyDictionary<string, long>? indexedLanguageCounts, string lang)
    {
        if (indexedLanguageCounts == null)
            return null;
        return indexedLanguageCounts.TryGetValue(lang, out var count) ? count : 0;
    }

    private static bool LanguageMatchesLookup(string lang, LanguageCatalogEntry language, QueryCommandOptions options)
        => options.LanguageLookups.Any(lookup => LanguageCatalog.MatchesLanguage(lang, lookup))
           || options.LanguageExtensionLookups.Any(lookup => LanguageCatalog.MatchesExtension(language, lookup))
           || options.LanguageAliasLookups.Any(lookup => LanguageCatalog.MatchesAlias(language, lookup));

    private static bool LanguageMatchesCapability(LanguageCatalogEntry language, string capability)
        => LanguageCatalog.MatchesCapability(language, capability);

    private static bool TryNormalizeLanguageCapability(string value, out string capability)
    {
        capability = value.Trim().ToLowerInvariant();
        return capability is
            LanguageCapabilityGraph or
            LanguageCapabilityReferences or
            LanguageCapabilitySymbols or
            LanguageCapabilityAll or
            LanguageCapabilityNone or
            LanguageCapabilityMissingAny or
            LanguageCapabilityMissingGraph or
            LanguageCapabilityMissingReferences or
            LanguageCapabilityMissingSymbols or
            LanguageCapabilitySearchOnly;
    }

    private static JsonObject BuildLanguageSummaryPayload(
        IReadOnlyList<KeyValuePair<string, LanguageCatalogEntry>> languages,
        int totalLanguageCount,
        IReadOnlyDictionary<string, long>? indexedLanguageCounts,
        QueryCommandOptions options,
        IReadOnlyList<LanguageMapOverrides.Diagnostic> languageMapDiagnostics)
    {
        var payload = new JsonObject
        {
            ["api_version"] = JsonOutputContract.ApiVersion,
            ["count"] = languages.Count,
            ["language_count"] = languages.Count,
            ["total_language_count"] = totalLanguageCount,
            ["capability_counts"] = BuildLanguageCapabilityCounts(languages),
            ["detection_policy"] = BuildLanguageDetectionPolicyNode(),
            ["language_map_diagnostics"] = BuildLanguageMapDiagnosticNode(languageMapDiagnostics),
        };

        if (options.OutputFormat == OutputFormatCount)
            payload["format"] = OutputFormatCount;
        if (options.SummaryOnly)
            payload["summary_only"] = true;
        if (options.LanguagesIndexedOnly)
            payload["indexed_only"] = true;
        if (options.LanguageCapabilities.Count > 0)
            payload["capability_filters"] = BuildStringArray(options.LanguageCapabilities);
        if (options.LanguageLookups.Count > 0)
            payload["language_filters"] = BuildStringArray(options.LanguageLookups);
        if (options.LanguageExtensionLookups.Count > 0)
            payload["extension_filters"] = BuildStringArray(options.LanguageExtensionLookups);
        if (options.LanguageAliasLookups.Count > 0)
            payload["alias_filters"] = BuildStringArray(options.LanguageAliasLookups);

        if (indexedLanguageCounts != null)
        {
            var indexedLanguages = languages
                .Where(kv => GetIndexedLanguageCount(indexedLanguageCounts, kv.Key).GetValueOrDefault() > 0)
                .ToList();
            long indexedFileCount = 0;
            foreach (var (lang, _) in indexedLanguages)
                indexedFileCount += GetIndexedLanguageCount(indexedLanguageCounts, lang).GetValueOrDefault();

            payload["indexed_language_count"] = indexedLanguages.Count;
            payload["indexed_file_count"] = indexedFileCount;
            payload["indexed_capability_counts"] = BuildLanguageCapabilityCounts(indexedLanguages);
        }

        return payload;
    }

    private static JsonObject BuildLanguageCapabilityCounts(IReadOnlyList<KeyValuePair<string, LanguageCatalogEntry>> languages)
    {
        static bool HasAll(LanguageCatalogEntry language)
            => language.Symbols && language.References && language.Graph;
        static bool HasNone(LanguageCatalogEntry language)
            => !language.Symbols && !language.References && !language.Graph;
        static bool IsSymbolOnly(LanguageCatalogEntry language)
            => language.Symbols && !language.References && !language.Graph;

        return new JsonObject
        {
            ["all"] = languages.Count(kv => HasAll(kv.Value)),
            ["none"] = languages.Count(kv => HasNone(kv.Value)),
            ["search_only"] = languages.Count(kv => HasNone(kv.Value)),
            ["symbols"] = languages.Count(kv => kv.Value.Symbols),
            ["references"] = languages.Count(kv => kv.Value.References),
            ["graph"] = languages.Count(kv => kv.Value.Graph),
            ["symbol_only"] = languages.Count(kv => IsSymbolOnly(kv.Value)),
            ["missing_any"] = languages.Count(kv => kv.Value.CapabilityGaps.Count > 0),
            ["missing_symbols"] = languages.Count(kv => !kv.Value.Symbols),
            ["missing_references"] = languages.Count(kv => !kv.Value.References),
            ["missing_graph"] = languages.Count(kv => !kv.Value.Graph),
        };
    }

    private static JsonArray BuildStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private static JsonObject BuildLanguageDetectionPolicyNode()
        => new()
        {
            ["filename_case_policy"] = "filesystem",
            ["filename_case_source"] = "path_case_sensitive",
            ["extension_case_policy"] = "case_insensitive",
            ["precedence"] = new JsonArray(LanguageDetectionPrecedence.Select(value => JsonValue.Create(value)).ToArray()),
        };

    private static JsonArray BuildLanguageMapDiagnosticNode(
        IReadOnlyList<LanguageMapOverrides.Diagnostic> diagnostics)
        => new(diagnostics.Select(diagnostic => (JsonNode)new JsonObject
        {
            ["code"] = diagnostic.Code,
            ["config"] = diagnostic.Config,
            ["reason"] = diagnostic.Reason,
            ["blocks_parent_fallback"] = diagnostic.BlocksParentFallback,
        }).ToArray());
}
