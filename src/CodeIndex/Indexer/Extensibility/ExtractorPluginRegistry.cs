using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeIndex.Indexer.Extensibility;

public static class ExtractorPluginRegistry
{
    public const int CurrentApiVersion = 1;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ISymbolExtractor> SymbolExtractors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReferenceExtractor> ReferenceExtractors = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoadedPatternConfigPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ExtractorRegistryDiagnostic> Diagnostics = [];
    private const int DiagnosticLimit = 20;
    private static int pluginAssemblyCount;
    private static int patternConfigCount;
    private static int skippedFileCount;
    private static int diagnosticTotalCount;
    private static bool pluginsLoaded;

    public static IReadOnlyCollection<string> SymbolLanguages
    {
        get
        {
            EnsurePluginsLoaded();
            lock (Gate)
                return SymbolExtractors.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public static IReadOnlyCollection<string> ReferenceLanguages
    {
        get
        {
            EnsurePluginsLoaded();
            lock (Gate)
                return ReferenceExtractors.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public static IReadOnlyDictionary<string, string> LanguageExtensions
    {
        get
        {
            EnsurePluginsLoaded();
            lock (Gate)
            {
                var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddLanguageExtensions(extensions, SymbolExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
                AddLanguageExtensions(extensions, ReferenceExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
                return extensions;
            }
        }
    }

    public static bool TryGetSymbolExtractor(string language, out ISymbolExtractor extractor)
    {
        EnsurePluginsLoaded();
        lock (Gate)
            return SymbolExtractors.TryGetValue(language, out extractor!);
    }

    public static bool TryGetReferenceExtractor(string language, out IReferenceExtractor extractor)
    {
        EnsurePluginsLoaded();
        lock (Gate)
            return ReferenceExtractors.TryGetValue(language, out extractor!);
    }

    internal static ExtractorRegistryStatus GetStatusSnapshot()
    {
        EnsurePluginsLoaded();
        lock (Gate)
        {
            return new ExtractorRegistryStatus
            {
                PluginAssemblyCount = pluginAssemblyCount,
                PatternConfigCount = patternConfigCount,
                SymbolExtractorCount = SymbolExtractors.Count,
                ReferenceExtractorCount = ReferenceExtractors.Count,
                SkippedFileCount = skippedFileCount,
                DiagnosticCount = diagnosticTotalCount,
                DiagnosticLimit = DiagnosticLimit,
                DiagnosticsTruncated = diagnosticTotalCount > Diagnostics.Count,
                Diagnostics = Diagnostics.Count == 0 ? null : Diagnostics.ToList(),
            };
        }
    }

    public static void Register(ISymbolExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var language = NormalizePluginLanguage(extractor.Language);
        lock (Gate)
            SymbolExtractors[language] = extractor;
    }

    public static void Register(IReferenceExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var language = NormalizePluginLanguage(extractor.Language);
        lock (Gate)
            ReferenceExtractors[language] = extractor;
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            LoadedPatternConfigPaths.Clear();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            patternConfigCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            pluginsLoaded = true;
        }
    }

    internal static void ReloadForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            LoadedPatternConfigPaths.Clear();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            patternConfigCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            pluginsLoaded = false;
        }
    }

    internal static void LoadPatternConfigsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        foreach (var patternPath in EnumeratePatternConfigPaths(Path.GetFullPath(projectRoot)))
            TryLoadPatternConfig(patternPath);
    }

    internal static void LoadPatternConfigsForPath(string? path)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory) ?? string.Empty;

        while (!string.IsNullOrEmpty(directory))
        {
            foreach (var patternPath in EnumeratePatternConfigPaths(directory, includeUserDirectory: false))
                TryLoadPatternConfig(patternPath);
            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
        }
    }

    private static void EnsurePluginsLoaded()
    {
        if (Volatile.Read(ref pluginsLoaded))
            return;

        lock (Gate)
        {
            if (pluginsLoaded)
                return;

            foreach (var pluginPath in EnumeratePluginAssemblyPaths())
                TryLoadPlugin(pluginPath);
            foreach (var patternPath in EnumeratePatternConfigPaths(Environment.CurrentDirectory))
                TryLoadPatternConfig(patternPath);

            pluginsLoaded = true;
        }
    }

    private static IEnumerable<string> EnumeratePluginAssemblyPaths()
    {
        foreach (var directory in EnumeratePluginDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var pluginPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                yield return pluginPath;
        }
    }

    private static IEnumerable<string> EnumeratePluginDirectories()
    {
        yield return Path.Combine(Environment.CurrentDirectory, ".cdidx", "plugins");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".cdidx", "plugins");
    }

    private static IEnumerable<string> EnumeratePatternConfigPaths(string workspaceRoot, bool includeUserDirectory = true)
    {
        foreach (var directory in EnumeratePatternDirectories(workspaceRoot, includeUserDirectory))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var path in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly))
                yield return path;
            foreach (var path in Directory.EnumerateFiles(directory, "*.yml", SearchOption.TopDirectoryOnly))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumeratePatternDirectories(string workspaceRoot, bool includeUserDirectory)
    {
        yield return Path.Combine(workspaceRoot, ".cdidx", "patterns");

        if (!includeUserDirectory)
            yield break;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            yield return Path.Combine(home, ".config", "cdidx", "patterns");
    }

    private static void TryLoadPatternConfig(string path)
    {
        var fullPath = path;
        try
        {
            fullPath = Path.GetFullPath(path);
            lock (Gate)
            {
                if (!LoadedPatternConfigPaths.Add(fullPath))
                    return;
            }

            var language = string.Empty;
            var extensions = new List<string>();
            var patterns = new List<ConfiguredSymbolExtractor.PatternRule>();
            string? pendingKind = null;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                if (TryReadScalar(line, "language", out var value))
                {
                    language = NormalizePluginLanguage(value);
                }
                else if (TryReadScalar(line.TrimStart('-').Trim(), "extension", out value))
                {
                    extensions.Add(NormalizePluginExtension(value) ?? value);
                }
                else if (TryReadScalar(line.TrimStart('-').Trim(), "kind", out value))
                {
                    pendingKind = value.Trim();
                }
                else if (TryReadScalar(line.TrimStart('-').Trim(), "regex", out value) && pendingKind != null)
                {
                    patterns.Add(new ConfiguredSymbolExtractor.PatternRule(
                        pendingKind,
                        new Regex(value, RegexOptions.Compiled | RegexOptions.CultureInvariant)));
                    pendingKind = null;
                }
            }

            if (language.Length > 0 && patterns.Count > 0)
            {
                Register(new ConfiguredSymbolExtractor(language, extensions, patterns));
                lock (Gate)
                    patternConfigCount++;
            }
            else
            {
                RecordDiagnostic(
                    "pattern",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    "Pattern config skipped: missing language or regex patterns.",
                    countsAsSkippedFile: true);
            }
        }
        catch (Exception ex)
        {
            RecordDiagnostic(
                "pattern",
                fullPath,
                typeName: null,
                severity: "error",
                $"Failed to load pattern config: {ex.Message}",
                countsAsSkippedFile: true);
        }
    }

    private static bool TryReadScalar(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        value = line[prefix.Length..].Trim().Trim('"', '\'').Replace("\\\\", "\\", StringComparison.Ordinal);
        return value.Length > 0;
    }

    private static void TryLoadPlugin(string pluginPath)
    {
        var fullPath = pluginPath;
        try
        {
            fullPath = Path.GetFullPath(pluginPath);
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
            var attribute = assembly.GetCustomAttribute<CdidxPluginAttribute>();
            if (attribute == null)
            {
                RecordDiagnostic(
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    "Plugin assembly skipped: missing CdidxPluginAttribute.",
                    countsAsSkippedFile: true);
                return;
            }

            if (attribute.MinApiVersion > CurrentApiVersion
                || attribute.MaxApiVersion < CurrentApiVersion)
            {
                RecordDiagnostic(
                    "plugin",
                    fullPath,
                    typeName: null,
                    severity: "skipped",
                    $"Plugin assembly skipped: API range {attribute.MinApiVersion}-{attribute.MaxApiVersion} does not include {CurrentApiVersion}.",
                    countsAsSkippedFile: true);
                return;
            }

            lock (Gate)
                pluginAssemblyCount++;

            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsAbstract: false, IsInterface: false } && type.GetConstructor(Type.EmptyTypes) != null)
                    TryRegisterPluginType(type, fullPath);
            }
        }
        catch (Exception ex)
        {
            RecordDiagnostic(
                "plugin",
                fullPath,
                typeName: null,
                severity: "error",
                $"Failed to load plugin assembly: {ex.Message}",
                countsAsSkippedFile: true);
        }
    }

    private static void TryRegisterPluginType(Type type, string pluginPath)
    {
        try
        {
            if (typeof(ISymbolExtractor).IsAssignableFrom(type)
                && Activator.CreateInstance(type) is ISymbolExtractor symbolExtractor)
            {
                Register(symbolExtractor);
            }

            if (typeof(IReferenceExtractor).IsAssignableFrom(type)
                && Activator.CreateInstance(type) is IReferenceExtractor referenceExtractor)
            {
                Register(referenceExtractor);
            }
        }
        catch (Exception ex)
        {
            RecordDiagnostic(
                "plugin_type",
                pluginPath,
                type.FullName,
                severity: "error",
                $"Failed to instantiate plugin type: {ex.Message}",
                countsAsSkippedFile: false);
        }
    }

    private static void RecordDiagnostic(
        string kind,
        string path,
        string? typeName,
        string severity,
        string message,
        bool countsAsSkippedFile)
    {
        lock (Gate)
        {
            diagnosticTotalCount++;
            if (countsAsSkippedFile)
                skippedFileCount++;
            if (Diagnostics.Count < DiagnosticLimit)
                Diagnostics.Add(new ExtractorRegistryDiagnostic(kind, path, typeName, severity, message));
        }
    }

    private static void AddLanguageExtensions(
        Dictionary<string, string> target,
        IEnumerable<(string Language, IReadOnlyCollection<string> FileExtensions)> plugins)
    {
        foreach (var (language, fileExtensions) in plugins)
        {
            var normalizedLanguage = NormalizePluginLanguage(language);
            foreach (var extension in fileExtensions)
            {
                var normalizedExtension = NormalizePluginExtension(extension);
                if (normalizedExtension != null)
                    target.TryAdd(normalizedExtension, normalizedLanguage);
            }
        }
    }

    private static string NormalizePluginLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Plugin language must be non-empty.", nameof(language));

        return language.Trim().ToLowerInvariant();
    }

    private static string? NormalizePluginExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        extension = extension.Trim().ToLowerInvariant();
        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }
}

public sealed class ExtractorRegistryStatus
{
    [JsonPropertyName("plugin_assembly_count")]
    public int PluginAssemblyCount { get; init; }
    [JsonPropertyName("pattern_config_count")]
    public int PatternConfigCount { get; init; }
    [JsonPropertyName("symbol_extractor_count")]
    public int SymbolExtractorCount { get; init; }
    [JsonPropertyName("reference_extractor_count")]
    public int ReferenceExtractorCount { get; init; }
    [JsonPropertyName("skipped_file_count")]
    public int SkippedFileCount { get; init; }
    [JsonPropertyName("diagnostic_count")]
    public int DiagnosticCount { get; init; }
    [JsonPropertyName("diagnostic_limit")]
    public int DiagnosticLimit { get; init; }
    [JsonPropertyName("diagnostics_truncated")]
    public bool DiagnosticsTruncated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtractorRegistryDiagnostic>? Diagnostics { get; init; }
}

public sealed record ExtractorRegistryDiagnostic(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("type_name")] string? TypeName,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message);
