using System.Globalization;
using System.Text;
using System.Text.Json;
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
        var json = options.Json;

        var langExtensions = FileIndexer.GetLanguageExtensions();
        var symbolLangs = SymbolExtractor.GetSupportedLanguages();
        var graphLangs = ReferenceExtractor.GetSupportedLanguages();

        // Build a consolidated view: language -> capability flags and gaps.
        // 統合ビュー: 言語 -> capability flag と gap。
        var allLangs = new Dictionary<string, LanguageSupportInfo>(StringComparer.Ordinal);

        foreach (var (ext, lang) in langExtensions)
        {
            if (!allLangs.TryGetValue(lang, out var info))
            {
                var hasSymbols = symbolLangs.Contains(lang);
                var hasReferences = graphLangs.Contains(lang);
                info = new LanguageSupportInfo(
                    [],
                    GetLanguageAliases(lang).ToList(),
                    hasSymbols,
                    hasReferences,
                    hasReferences,
                    LanguageCapabilitySupport.BuildGaps(hasSymbols, hasReferences, hasReferences),
                    LanguageCapabilitySupport.BuildUnsupportedGuidance(lang, hasSymbols, hasReferences, hasReferences));
                allLangs[lang] = info;
            }
            info.Extensions.Add(ext);
        }

        // Sort by language name / 言語名でソート
        var sorted = allLangs.OrderBy(kv => kv.Key).ToList();

        if (options.LanguagesIndexedOnly || ShouldLoadLanguageIndexedCounts(options))
        {
            return WithDb(options, jsonOptions, reader =>
            {
                var indexedLanguageCounts = reader.GetStatus().Languages;
                return WriteLanguages(SelectLanguages(sorted, indexedLanguageCounts), indexedLanguageCounts);
            });
        }

        return WriteLanguages(SelectLanguages(sorted, indexedLanguageCounts: null), indexedLanguageCounts: null);

        IEnumerable<KeyValuePair<string, LanguageSupportInfo>> SelectLanguages(
            IEnumerable<KeyValuePair<string, LanguageSupportInfo>> languages,
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
            IEnumerable<KeyValuePair<string, LanguageSupportInfo>> languages,
            IReadOnlyDictionary<string, long>? indexedLanguageCounts)
        {
            var filtered = languages
                .Where(kv => options.LanguageCapabilities.All(capability => LanguageMatchesCapability(kv.Value, capability)))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            if (json)
            {
                var entries = filtered.Select(kv => new LanguageEntryJsonResult(
                    kv.Key,
                    kv.Value.Extensions.OrderBy(e => e).ToList(),
                    kv.Value.Aliases.OrderBy(a => a).ToList(),
                    kv.Value.Symbols,
                    kv.Value.References,
                    kv.Value.Graph,
                    kv.Value.CapabilityGaps,
                    kv.Value.UnsupportedGuidance,
                    GetIndexedLanguageCount(indexedLanguageCounts, kv.Key))).ToList();
                Console.WriteLine(JsonSerializer.Serialize(new LanguagesJsonResult(entries), CliJsonSerializerContextFactory.Create(jsonOptions).LanguagesJsonResult));
            }
            else
            {
                // Fixed-width Extensions column for short lists; spill long lists onto a continuation
                // line so the Symbols / Graph columns are never swallowed by a wide extension string.
                // 拡張子が短い場合は固定幅テーブル、長い場合は継続行に退避させることで、
                // Symbols / Graph 列が拡張子文字列に埋もれないようにする。
                const int ExtensionColumnWidth = 36;
                const int AliasColumnWidth = 12;
                var showIndexedCounts = indexedLanguageCounts != null;
                if (showIndexedCounts)
                {
                    Console.WriteLine($"{"Language",-14} {"Extensions",-36} {"Aliases",-12} {"Indexed",-7} {"Symbols",-9} {"Refs",-5} {"Graph",-7}");
                    Console.WriteLine(new string('-', 93));
                }
                else
                {
                    Console.WriteLine($"{"Language",-14} {"Extensions",-36} {"Aliases",-12} {"Symbols",-9} {"Refs",-5} {"Graph",-7}");
                    Console.WriteLine(new string('-', 85));
                }
                foreach (var (lang, info) in filtered)
                {
                    var exts = string.Join(" ", info.Extensions.OrderBy(e => e));
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
                        Console.WriteLine($"  Extensions: {exts}");
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

    private sealed record LanguageSupportInfo(
        List<string> Extensions,
        List<string> Aliases,
        bool Symbols,
        bool References,
        bool Graph,
        List<string> CapabilityGaps,
        List<LanguageUnsupportedGuidance> UnsupportedGuidance);

    private static bool HasLanguageLookup(QueryCommandOptions options)
        => options.LanguageLookups.Count > 0 || options.LanguageExtensionLookups.Count > 0 || options.LanguageAliasLookups.Count > 0;

    private static bool ShouldLoadLanguageIndexedCounts(QueryCommandOptions options)
    {
        if (!HasLanguageLookup(options))
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

    private static bool LanguageMatchesLookup(string lang, LanguageSupportInfo language, QueryCommandOptions options)
        => options.LanguageLookups.Any(lookup => string.Equals(DbReader.NormalizeQueryLanguage(lookup), lang, StringComparison.Ordinal))
           || options.LanguageExtensionLookups.Any(lookup => LanguageMatchesExtensionLookup(language, lookup))
           || options.LanguageAliasLookups.Any(lookup => LanguageMatchesAliasLookup(language, lookup));

    private static bool LanguageMatchesExtensionLookup(LanguageSupportInfo language, string lookup)
    {
        var normalized = NormalizeLanguageLookupKey(lookup);
        return language.Extensions.Any(ext => string.Equals(NormalizeLanguageLookupKey(ext), normalized, StringComparison.Ordinal));
    }

    private static bool LanguageMatchesAliasLookup(LanguageSupportInfo language, string lookup)
    {
        var normalized = NormalizeLanguageLookupKey(lookup);
        return language.Aliases.Any(alias => string.Equals(NormalizeLanguageLookupKey(alias), normalized, StringComparison.Ordinal));
    }

    private static string NormalizeLanguageLookupKey(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '.')
                continue;
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }

    private static bool LanguageMatchesCapability(LanguageSupportInfo language, string capability)
        => capability switch
        {
            LanguageCapabilitySymbols => language.Symbols,
            LanguageCapabilityReferences => language.References,
            LanguageCapabilityGraph => language.Graph,
            LanguageCapabilityMissingSymbols => !language.Symbols,
            LanguageCapabilityMissingReferences => !language.References,
            LanguageCapabilityMissingGraph => !language.Graph,
            LanguageCapabilitySearchOnly => !language.Symbols && !language.References && !language.Graph,
            _ => false,
        };

    private static bool TryNormalizeLanguageCapability(string value, out string capability)
    {
        capability = value.Trim().ToLowerInvariant();
        return capability is
            LanguageCapabilityGraph or
            LanguageCapabilityReferences or
            LanguageCapabilitySymbols or
            LanguageCapabilityMissingGraph or
            LanguageCapabilityMissingReferences or
            LanguageCapabilityMissingSymbols or
            LanguageCapabilitySearchOnly;
    }
}