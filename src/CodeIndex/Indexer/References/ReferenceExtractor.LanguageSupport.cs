using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    public static IReadOnlyCollection<string> GetSupportedLanguages()
        => GetSupportedLanguages(workspaceRoot: null);

    internal static IReadOnlyCollection<string> GetSupportedLanguages(string? workspaceRoot)
    {
        var pluginLanguages = ExtractorPluginRegistry.GetReferenceLanguages(workspaceRoot);
        var capacity = RegisteredLanguages.Count + AdditionalReferenceLanguages.Length + pluginLanguages.Count;
        var languages = new List<string>(capacity);
        var seen = new HashSet<string>(capacity, StringComparer.Ordinal);

        AddSupportedLanguages(RegisteredLanguages, languages, seen);
        AddSupportedLanguages(AdditionalReferenceLanguages, languages, seen);
        AddSupportedLanguages(pluginLanguages, languages, seen);
        return languages.ToArray();
    }

    private static void AddSupportedLanguages(
        IEnumerable<string> candidates,
        List<string> languages,
        HashSet<string> seen)
    {
        foreach (var language in candidates)
        {
            if (seen.Add(language))
                languages.Add(language);
        }
    }

    /// <summary>
    /// Registered language keys for reference extraction.
    /// 参照抽出に登録されている言語キー。
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredLanguages => BuiltInLanguages;

    private static string? NormalizeLanguage(string? lang)
    {
        if (lang is null)
            return null;

        var trimmed = lang.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return null;

        if (trimmed.Equals("vue", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("svelte", StringComparison.OrdinalIgnoreCase))
        {
            return "typescript";
        }

        if (trimmed.Equals("razor", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("blazor", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cshtml", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        return NormalizeLanguageKey(lang, trimmed);
    }

    private static string? NormalizePluginLanguage(string? lang)
    {
        if (lang is null)
            return null;

        var trimmed = lang.AsSpan().Trim();
        return trimmed.IsEmpty ? null : NormalizeLanguageKey(lang, trimmed);
    }

    private static string NormalizeLanguageKey(string original, ReadOnlySpan<char> trimmed)
    {
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (char.ToLowerInvariant(trimmed[i]) != trimmed[i])
                return trimmed.ToString().ToLowerInvariant();
        }

        return trimmed.Length == original.Length && trimmed.SequenceEqual(original.AsSpan())
            ? original
            : trimmed.ToString();
    }

    public static bool SupportsLanguage(string? lang)
        => SupportsLanguage(lang, GetSupportedLanguages(workspaceRoot: null));

    internal static bool SupportsLanguage(
        string? lang,
        IReadOnlyCollection<string> supportedLanguages)
    {
        var normalized = NormalizeLanguage(lang);
        if (normalized != null && supportedLanguages.Contains(normalized, StringComparer.Ordinal))
            return true;

        return NormalizePluginLanguage(lang) is string pluginLanguage
            && supportedLanguages.Contains(pluginLanguage, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the registered reference extractor for a supported language.
    /// 対応言語の登録済み参照抽出器を返す。
    /// </summary>
    public static bool TryGetExtractor(string? lang, out IReferenceExtractor extractor)
        => TryGetExtractor(lang, out extractor, out _);

    private static bool TryGetExtractor(string? lang, out IReferenceExtractor extractor, out string? normalized)
    {
        normalized = NormalizeLanguage(lang);
        if (normalized != null && Extractors.TryGetValue(normalized, out extractor!))
            return true;

        extractor = null!;
        return false;
    }

    public static bool? SupportsSymbolGraph(string? lang, string? kind, string? containerKind)
    {
        if (lang == null)
            return null;

        return SupportsLanguage(lang);
    }

    internal static bool? SupportsSymbolGraph(
        string? lang,
        string? kind,
        string? containerKind,
        IReadOnlyCollection<string> supportedLanguages)
    {
        if (lang == null)
            return null;

        return SupportsLanguage(lang, supportedLanguages);
    }

    public static string? GetUnsupportedSymbolKind(string? lang, string? kind, string? containerKind)
    {
        return null;
    }

    /// <summary>
    /// Build a human-readable reason explaining graph-support status for the given language.
    /// Returns null when neither language nor support status is known.
    /// 指定言語の graph 対応状況を人間向けに説明する文字列を返す。言語も対応状況も不明なら null。
    /// </summary>
    public static string? BuildGraphSupportReason(string? lang, bool? graphSupported, string? kind = null, string? containerKind = null)
    {
        if (lang == null || graphSupported == null)
            return null;

        if (graphSupported.Value)
            return $"Call-graph extraction is indexed for '{lang}'.";

        return $"Call-graph extraction is not indexed for '{lang}'. Use search, definition, excerpt, or files instead.";
    }

    public static string? BuildGraphSupportReasonWithUnsupportedEnumMemberGap(string? lang, bool? graphSupported, bool hasUnsupportedEnumMember, bool hasSupportedGraphDefinition)
    {
        var baseReason = BuildGraphSupportReason(lang, graphSupported);
        if (!hasUnsupportedEnumMember)
            return baseReason;

        var enumGapReason = hasSupportedGraphDefinition
            ? "Exact results also include C# enum members whose access edges are not indexed yet."
            : BuildGraphSupportReason("csharp", true, "enum", "enum");

        if (!hasSupportedGraphDefinition)
            return enumGapReason;

        if (string.IsNullOrWhiteSpace(baseReason))
            return enumGapReason;
        if (string.IsNullOrWhiteSpace(enumGapReason) || string.Equals(baseReason, enumGapReason, StringComparison.Ordinal))
            return baseReason;

        return $"{baseReason} {enumGapReason}";
    }

    private static string NormalizeKotlinBacktickIdentifier(string name)
    {
        if (name.Length >= 2 && name[0] == '`' && name[^1] == '`')
            return name[1..^1];
        return name;
    }

    /// <summary>
    /// Extract indexed references for supported languages.
    /// 対応言語向けにインデックス化する参照を抽出する。
    /// </summary>
}
