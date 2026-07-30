using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Cli;

internal sealed record LanguageCatalogEntry(
    List<string> Extensions,
    List<string> ExactFilenames,
    List<string> FilenamePrefixPatterns,
    List<string> LegacyPatterns,
    List<FileIndexer.LanguagePattern> PatternProvenance,
    List<string> Aliases,
    bool Symbols,
    bool References,
    bool Graph,
    List<string> CapabilityGaps,
    List<LanguageUnsupportedGuidance> UnsupportedGuidance);

internal sealed record LanguageCatalogSnapshot(
    IReadOnlyList<KeyValuePair<string, LanguageCatalogEntry>> Languages,
    int SymbolLanguageCount,
    int ReferenceLanguageCount,
    IReadOnlyList<LanguageMapOverrides.Diagnostic> Diagnostics);

internal static class LanguageCatalog
{
    internal static LanguageCatalogSnapshot Build(string? workspaceRoot)
    {
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceRoot);
        var languagePatterns = FileIndexer.GetLanguagePatterns(workspaceRoot, out var diagnostics);
        var symbolLanguages = SymbolExtractor.GetSupportedLanguages(workspaceRoot);
        var referenceLanguages = ReferenceExtractor.GetSupportedLanguages(workspaceRoot);
        var languages = new Dictionary<string, LanguageCatalogEntry>(StringComparer.Ordinal);

        foreach (var pattern in languagePatterns)
        {
            var language = pattern.Language;
            if (!languages.TryGetValue(language, out var entry))
            {
                entry = CreateEntry(language, symbolLanguages, referenceLanguages);
                languages[language] = entry;
            }

            switch (pattern.Kind)
            {
                case FileIndexer.LanguagePatternKind.Extension:
                    entry.Extensions.Add(pattern.Pattern);
                    break;
                case FileIndexer.LanguagePatternKind.ExactFilename:
                    entry.ExactFilenames.Add(pattern.Pattern);
                    break;
                case FileIndexer.LanguagePatternKind.FilenamePrefixPattern:
                    entry.FilenamePrefixPatterns.Add(pattern.Pattern);
                    break;
            }

            if (!entry.LegacyPatterns.Contains(pattern.Pattern, StringComparer.Ordinal))
                entry.LegacyPatterns.Add(pattern.Pattern);
            entry.PatternProvenance.Add(pattern);
        }

        foreach (var language in FileIndexer.GetContentDetectedLanguageBuckets())
        {
            if (!languages.ContainsKey(language))
                languages[language] = CreateEntry(language, symbolLanguages, referenceLanguages);
        }

        return new LanguageCatalogSnapshot(
            languages.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList(),
            symbolLanguages.Count,
            referenceLanguages.Count,
            diagnostics);
    }

    internal static bool MatchesLanguage(string language, string lookup)
        => string.Equals(
            DbReader.NormalizeQueryLanguage(lookup),
            language,
            StringComparison.Ordinal);

    internal static bool MatchesExtension(LanguageCatalogEntry language, string lookup)
    {
        var normalized = NormalizeLookupKey(lookup);
        return language.Extensions.Any(extension =>
            string.Equals(NormalizeLookupKey(extension), normalized, StringComparison.Ordinal));
    }

    internal static bool MatchesAlias(LanguageCatalogEntry language, string lookup)
    {
        var normalized = NormalizeLookupKey(lookup);
        return language.Aliases.Any(alias =>
            string.Equals(NormalizeLookupKey(alias), normalized, StringComparison.Ordinal));
    }

    internal static bool MatchesCapability(LanguageCatalogEntry language, string capability)
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

    private static LanguageCatalogEntry CreateEntry(
        string language,
        IReadOnlyCollection<string> symbolLanguages,
        IReadOnlyCollection<string> referenceLanguages)
    {
        var hasSymbols = symbolLanguages.Contains(language);
        var hasReferences = referenceLanguages.Contains(language);
        return new LanguageCatalogEntry(
            [],
            [],
            [],
            [],
            [],
            QueryCommandRunner.GetLanguageAliases(language).ToList(),
            hasSymbols,
            hasReferences,
            hasReferences,
            LanguageCapabilitySupport.BuildGaps(hasSymbols, hasReferences, hasReferences),
            LanguageCapabilitySupport.BuildUnsupportedGuidance(language, hasSymbols, hasReferences, hasReferences));
    }
}
