using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Models;

internal sealed record LanguageCapabilityCatalogSnapshot(
    IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> Languages,
    IReadOnlyList<LanguageMapOverrides.Diagnostic> Diagnostics);

internal sealed record LanguageCatalogSupportInfo(
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ExactFilenames,
    IReadOnlyList<string> FilenamePrefixPatterns,
    IReadOnlyList<string> LegacyPatterns,
    IReadOnlyList<FileIndexer.LanguagePattern> PatternProvenance,
    IReadOnlyList<string> Aliases,
    bool Detection,
    bool Symbols,
    bool References,
    bool Outline,
    bool Graph,
    IReadOnlyList<string> CapabilityGaps,
    IReadOnlyList<LanguageUnsupportedGuidance> UnsupportedGuidance);

internal sealed record LanguageCapabilityCountScope(
    string Scope,
    bool Available,
    int? CatalogMembership,
    int? Detection,
    int? SymbolExtraction,
    int? ReferenceExtraction,
    int? Outline,
    int? GraphQueries)
{
    public JsonObject ToJson()
        => new()
        {
            ["catalog_membership"] = BuildCount("catalog_membership", CatalogMembership),
            ["detection"] = BuildCount("detection", Detection),
            ["symbol_extraction"] = BuildCount("symbol_extraction", SymbolExtraction),
            ["reference_extraction"] = BuildCount("reference_extraction", ReferenceExtraction),
            ["outline"] = BuildCount("outline", Outline),
            ["graph_queries"] = BuildCount("graph_queries", GraphQueries),
        };

    private JsonObject BuildCount(string capability, int? count)
        => new()
        {
            ["scope"] = Scope,
            ["capability"] = capability,
            ["count"] = count.HasValue ? JsonValue.Create(count.Value) : null,
            ["available"] = Available,
        };
}

internal sealed record LanguageCapabilityCountSnapshot(
    LanguageCapabilityCountScope Catalog,
    LanguageCapabilityCountScope MatchedCatalog,
    LanguageCapabilityCountScope IndexedWorkspace)
{
    public JsonObject ToJson()
        => new()
        {
            ["catalog"] = Catalog.ToJson(),
            ["matched_catalog"] = MatchedCatalog.ToJson(),
            ["indexed_workspace"] = IndexedWorkspace.ToJson(),
        };

    public string FormatSummary()
    {
        var catalogCount = Catalog.CatalogMembership.GetValueOrDefault();
        var matchedCount = MatchedCatalog.CatalogMembership.GetValueOrDefault();
        var indexedSummary = IndexedWorkspace.Available
            ? $"{IndexedWorkspace.CatalogMembership.GetValueOrDefault()} indexed workspace languages"
            : "indexed workspace language count unavailable";
        return $"{catalogCount} catalog languages; "
               + $"{Catalog.SymbolExtraction.GetValueOrDefault()} with symbol extraction; "
               + $"{Catalog.ReferenceExtraction.GetValueOrDefault()} with reference extraction; "
               + $"{Catalog.Outline.GetValueOrDefault()} with outline support; "
               + $"{Catalog.GraphQueries.GetValueOrDefault()} with graph queries; {indexedSummary}; "
               + $"{matchedCount} matching catalog languages";
    }
}

internal static class LanguageCapabilityCatalog
{
    internal static IReadOnlyList<string> SupportedCapabilities { get; } = Array.AsReadOnly(
    [
        "all",
        "none",
        "graph",
        "references",
        "symbols",
        "missing-any",
        "missing-graph",
        "missing-references",
        "missing-symbols",
        "search-only",
    ]);

    public static LanguageCapabilityCatalogSnapshot Build(
        string? workspaceRoot,
        Func<string, IReadOnlyList<string>> getAliases)
    {
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceRoot);
        var languagePatterns = FileIndexer.GetLanguagePatterns(workspaceRoot, out var diagnostics);
        var symbolLanguages = SymbolExtractor.GetSupportedLanguages(workspaceRoot);
        var referenceLanguages = ReferenceExtractor.GetSupportedLanguages(workspaceRoot);
        var languages = new Dictionary<string, MutableLanguageCatalogSupportInfo>(StringComparer.Ordinal);

        MutableLanguageCatalogSupportInfo CreateSupportInfo(string language)
        {
            var hasSymbols = symbolLanguages.Contains(language);
            var hasReferences = referenceLanguages.Contains(language);
            return new MutableLanguageCatalogSupportInfo(
                getAliases(language),
                Detection: true,
                Symbols: hasSymbols,
                References: hasReferences,
                Outline: hasSymbols,
                Graph: hasReferences,
                LanguageCapabilitySupport.BuildGaps(hasSymbols, hasReferences, hasReferences),
                LanguageCapabilitySupport.BuildUnsupportedGuidance(language, hasSymbols, hasReferences, hasReferences));
        }

        foreach (var pattern in languagePatterns)
        {
            var language = pattern.Language;
            if (!languages.TryGetValue(language, out var info))
            {
                info = CreateSupportInfo(language);
                languages[language] = info;
            }

            switch (pattern.Kind)
            {
                case FileIndexer.LanguagePatternKind.Extension:
                    info.Extensions.Add(pattern.Pattern);
                    break;
                case FileIndexer.LanguagePatternKind.ExactFilename:
                    info.ExactFilenames.Add(pattern.Pattern);
                    break;
                case FileIndexer.LanguagePatternKind.FilenamePrefixPattern:
                    info.FilenamePrefixPatterns.Add(pattern.Pattern);
                    break;
            }
            if (!info.LegacyPatterns.Contains(pattern.Pattern, StringComparer.Ordinal))
                info.LegacyPatterns.Add(pattern.Pattern);
            info.PatternProvenance.Add(pattern);
        }

        foreach (var language in FileIndexer.GetContentDetectedLanguageBuckets())
        {
            if (!languages.ContainsKey(language))
                languages[language] = CreateSupportInfo(language);
        }

        return new LanguageCapabilityCatalogSnapshot(
            languages
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new KeyValuePair<string, LanguageCatalogSupportInfo>(
                    pair.Key,
                    pair.Value.Freeze()))
                .ToList(),
            diagnostics);
    }

    public static LanguageCapabilityCountSnapshot Count(
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> catalog,
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> matchedCatalog,
        IReadOnlyDictionary<string, long>? indexedLanguageCounts)
    {
        var indexedCatalog = indexedLanguageCounts is null
            ? null
            : catalog
                .Where(pair => indexedLanguageCounts.TryGetValue(pair.Key, out var count) && count > 0)
                .ToList();
        return new LanguageCapabilityCountSnapshot(
            CountScope("catalog", catalog),
            CountScope("matched_catalog", matchedCatalog),
            indexedCatalog is null
                ? UnavailableScope("indexed_workspace")
                : CountScope("indexed_workspace", indexedCatalog));
    }

    internal static bool IsKnownCapability(string capability)
        => SupportedCapabilities.Contains(capability, StringComparer.Ordinal);

    internal static bool MatchesCapability(LanguageCatalogSupportInfo language, string capability)
        => capability switch
        {
            "all" => language.Symbols && language.References && language.Graph,
            "none" => !language.Symbols && !language.References && !language.Graph,
            "symbols" => language.Symbols,
            "references" => language.References,
            "graph" => language.Graph,
            "missing-any" => language.CapabilityGaps.Count > 0,
            "missing-symbols" => !language.Symbols,
            "missing-references" => !language.References,
            "missing-graph" => !language.Graph,
            "search-only" => !language.Symbols && !language.References && !language.Graph,
            _ => false,
        };

    internal static bool MatchesLanguage(string language, string lookup)
        => string.Equals(DbReader.NormalizeQueryLanguage(lookup), language, StringComparison.Ordinal);

    internal static bool MatchesExtension(LanguageCatalogSupportInfo language, string lookup)
    {
        var normalized = NormalizeLookupKey(lookup);
        return language.Extensions.Any(extension =>
            string.Equals(NormalizeLookupKey(extension), normalized, StringComparison.Ordinal));
    }

    internal static bool MatchesAlias(LanguageCatalogSupportInfo language, string lookup)
    {
        var normalized = NormalizeLookupKey(lookup);
        return language.Aliases.Any(alias =>
            string.Equals(NormalizeLookupKey(alias), normalized, StringComparison.Ordinal));
    }

    internal static JsonObject BuildExtensionLookup(
        string value,
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> matches,
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> catalog)
    {
        var normalizedExtension = NormalizeExtension(value);
        var result = new JsonObject
        {
            ["extension"] = value,
            ["normalized_extension"] = normalizedExtension,
            ["matched"] = matches.Count,
            ["languages"] = BuildStringArray(matches.Select(pair => pair.Key)),
            ["extension_case_policy"] = "case_insensitive",
            ["ambiguous"] = false,
        };

        if (!FileIndexer.TryGetAmbiguousLanguageDescriptor(normalizedExtension, out var descriptor))
            return result;

        result["ambiguous"] = true;
        result["bucket_language"] = descriptor.BucketLanguage;
        result["candidates"] = new JsonArray(descriptor.Candidates.Select(candidate =>
        {
            var support = catalog.FirstOrDefault(pair =>
                string.Equals(pair.Key, candidate.Language, StringComparison.Ordinal)).Value;
            return (JsonNode)new JsonObject
            {
                ["lang"] = candidate.Language,
                ["display_name"] = candidate.DisplayName,
                ["aliases"] = BuildStringArray(support?.Aliases ?? []),
                ["evidence"] = new JsonObject
                {
                    ["shebang_interpreters"] = BuildStringArray(
                        FileIndexer.GetShebangInterpretersForLanguage(candidate.Language)),
                    ["content_pattern"] = candidate.ContentPattern,
                    ["content_case_policy"] = "case_sensitive",
                    ["project_markers"] = BuildStringArray(candidate.ProjectMarkerPatterns),
                    ["project_marker_case_policy"] = "case_insensitive",
                },
            };
        }).ToArray());
        result["detection_rules"] = new JsonArray(
            BuildDetectionRule(
                FileIndexer.LanguageMapOverrideDetectionSource,
                FileIndexer.LanguageDetectionConfidence.High,
                precedence: 1,
                selection: "authoritative"),
            BuildDetectionRule(
                FileIndexer.ExactFilenameDetectionSource,
                confidence: null,
                precedence: 2,
                selection: "authoritative_when_exact_filename_matches",
                ("case_policy", JsonValue.Create("filesystem")),
                ("case_policy_source", JsonValue.Create("path_case_sensitive")),
                ("applicable_patterns", BuildFilenameRuleArray(
                    FileIndexer.GetExactFilenameLanguageRulesForExtension(normalizedExtension)))),
            BuildDetectionRule(
                FileIndexer.FilenamePrefixPatternDetectionSource,
                confidence: null,
                precedence: 3,
                selection: "authoritative_when_non_empty_suffix_matches",
                ("case_policy", JsonValue.Create("filesystem")),
                ("case_policy_source", JsonValue.Create("path_case_sensitive")),
                ("patterns", BuildFilenameRuleArray(
                    FileIndexer.GetFilenamePrefixLanguageRules()))),
            BuildDetectionRule(
                FileIndexer.ShebangDetectionSource,
                FileIndexer.LanguageDetectionConfidence.High,
                precedence: 4,
                selection: "recognized_interpreter_authoritative",
                ("candidate_restricted", JsonValue.Create(false)),
                ("probe_scope", JsonValue.Create("first_physical_line")),
                ("probe_byte_limit", JsonValue.Create(FileIndexer.ShebangProbeByteLimit)),
                ("line_termination_policy", JsonValue.Create("required_before_limit_unless_eof")),
                ("interpreter_case_policy", JsonValue.Create("case_insensitive")),
                ("interpreter_rules", new JsonArray(
                    FileIndexer.GetShebangInterpreterRules()
                        .Select(rule => (JsonNode)new JsonObject
                        {
                            ["match"] = rule.MatchKind,
                            ["pattern"] = rule.Pattern,
                            ["language"] = rule.Language,
                        })
                        .ToArray()))),
            BuildDetectionRule(
                FileIndexer.AmbiguousContentDetectionSource,
                FileIndexer.LanguageDetectionConfidence.High,
                precedence: 5,
                selection: "exactly_one_candidate_matches",
                ("probe_byte_limit", JsonValue.Create(FileIndexer.AmbiguousLanguageProbeByteLimit))),
            BuildDetectionRule(
                FileIndexer.AmbiguousProjectDetectionSource,
                FileIndexer.LanguageDetectionConfidence.Medium,
                precedence: 6,
                selection: "exactly_one_candidate_matches",
                ("ancestor_directory_limit", JsonValue.Create(FileIndexer.AmbiguousProjectMarkerAncestorLimit)),
                ("entry_limit_per_directory", JsonValue.Create(FileIndexer.AmbiguousProjectMarkerEntryLimit))),
            BuildDetectionRule(
                FileIndexer.AmbiguousFallbackDetectionSource,
                FileIndexer.LanguageDetectionConfidence.Low,
                precedence: 7,
                selection: "zero_or_multiple_candidates_match"));
        result["input_handling"] = new JsonObject
        {
            ["empty_text"] = "project_then_ambiguous",
            ["binary_or_invalid_text"] = "unsupported",
            ["missing_without_indexability_context"] = "ambiguous",
        };
        result["override_guidance"] = new JsonObject
        {
            ["config_file"] = LanguageMapOverrides.WorkspaceFileName,
            ["entries"] = new JsonArray(descriptor.Candidates.Select(candidate => (JsonNode)new JsonObject
            {
                ["extension"] = descriptor.Extension,
                ["language"] = candidate.Language,
            }).ToArray()),
        };
        return result;
    }

    internal static string NormalizeLookupKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character) || character is '-' or '_' or '.')
                continue;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static string NormalizeExtension(string value)
    {
        var trimmed = value.Trim();
        var withDot = trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : "." + trimmed;
        return withDot.ToLowerInvariant();
    }

    private static JsonArray BuildStringArray(IEnumerable<string> values)
        => new(values
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select(value => JsonValue.Create(value))
            .ToArray());

    private static JsonArray BuildFilenameRuleArray(
        IEnumerable<FileIndexer.FilenameLanguageRule> rules)
        => new(rules
            .Select(rule => (JsonNode)new JsonObject
            {
                ["pattern"] = rule.Pattern,
                ["language"] = rule.Language,
            })
            .ToArray());

    private static JsonObject BuildDetectionRule(
        string source,
        FileIndexer.LanguageDetectionConfidence? confidence,
        int precedence,
        string selection,
        params (string Name, JsonNode? Value)[] details)
    {
        var rule = new JsonObject
        {
            ["source"] = source,
            ["precedence"] = precedence,
            ["selection"] = selection,
        };
        if (confidence is { } reportedConfidence)
        {
            rule["confidence"] =
                FileIndexer.GetLanguageDetectionConfidenceCode(reportedConfidence);
        }
        foreach (var (name, value) in details)
            rule[name] = value;
        return rule;
    }

    private static LanguageCapabilityCountScope CountScope(
        string scope,
        IReadOnlyList<KeyValuePair<string, LanguageCatalogSupportInfo>> languages)
        => new(
            scope,
            Available: true,
            CatalogMembership: languages.Count,
            Detection: languages.Count(pair => pair.Value.Detection),
            SymbolExtraction: languages.Count(pair => pair.Value.Symbols),
            ReferenceExtraction: languages.Count(pair => pair.Value.References),
            Outline: languages.Count(pair => pair.Value.Outline),
            GraphQueries: languages.Count(pair => pair.Value.Graph));

    private static LanguageCapabilityCountScope UnavailableScope(string scope)
        => new(
            scope,
            Available: false,
            CatalogMembership: null,
            Detection: null,
            SymbolExtraction: null,
            ReferenceExtraction: null,
            Outline: null,
            GraphQueries: null);

    private sealed class MutableLanguageCatalogSupportInfo(
        IReadOnlyList<string> aliases,
        bool Detection,
        bool Symbols,
        bool References,
        bool Outline,
        bool Graph,
        IReadOnlyList<string> CapabilityGaps,
        IReadOnlyList<LanguageUnsupportedGuidance> UnsupportedGuidance)
    {
        public List<string> Extensions { get; } = [];
        public List<string> ExactFilenames { get; } = [];
        public List<string> FilenamePrefixPatterns { get; } = [];
        public List<string> LegacyPatterns { get; } = [];
        public List<FileIndexer.LanguagePattern> PatternProvenance { get; } = [];

        public LanguageCatalogSupportInfo Freeze()
            => new(
                Extensions.ToArray(),
                ExactFilenames.ToArray(),
                FilenamePrefixPatterns.ToArray(),
                LegacyPatterns.ToArray(),
                PatternProvenance.ToArray(),
                aliases.ToArray(),
                Detection,
                Symbols,
                References,
                Outline,
                Graph,
                CapabilityGaps.ToArray(),
                UnsupportedGuidance.ToArray());
    }
}
