using System.Runtime.Loader;
using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    public const int CurrentApiVersion = 1;
    internal const string TrustWorkspacePluginsEnvironmentVariable = "CDIDX_TRUST_WORKSPACE_PLUGINS";
    internal const int MaxPatternConfigBytes = 64 * 1024;
    internal const int MaxPatternRulesPerConfig = 128;
    internal const int MaxPatternRulesTotal = 128;
    internal const int MaxPatternLanguageLength = 64;
    internal const int MaxPatternExtensionLength = 64;
    internal const int MaxPatternKindLength = 64;
    internal const int MaxPatternRegexLength = 4096;
    internal const int MaxPatternConfigCandidatesPerDirectory = 128;
    internal const int MaxPluginAssemblyCandidatesPerDirectory = 128;
    internal const int MaxPluginAssemblyCandidatesTotal = 256;
    internal const long MaxPluginAssemblyBytes = 64 * 1024 * 1024;
    internal const int MaxExtensionAssemblyTypes = 4096;
    internal static readonly TimeSpan PatternRegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ISymbolExtractor> SymbolExtractors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReferenceExtractor> ReferenceExtractors = new(StringComparer.Ordinal);
    private static readonly List<string> LoadedPluginAssemblyPaths = [];
    private static readonly HashSet<string> LoadedPatternConfigPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<AssemblyLoadContext> LoadedPluginAssemblyContexts = [];
    private static readonly IReadOnlyList<string> PatternConfigSearchPatterns = ["*.yaml", "*.yml"];
    private static readonly List<ExtractorRegistryDiagnostic> Diagnostics = [];
    private const int DiagnosticLimit = 20;
    private static int pluginAssemblyCount;
    private static int patternConfigCount;
    private static int skippedFileCount;
    private static int diagnosticTotalCount;
    private static int loadedPatternRuleCount;
    private static bool pluginsLoaded;
    internal static int? TypeInspectionLimitForTesting { get; set; }

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
                RetainedLoadContextCount = LoadedPluginAssemblyContexts.Count,
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
            LoadedPluginAssemblyPaths.Clear();
            LoadedPatternConfigPaths.Clear();
            UnloadPluginAssemblyContexts();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            patternConfigCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            loadedPatternRuleCount = 0;
            pluginsLoaded = true;
            TypeInspectionLimitForTesting = null;
        }
    }

    internal static void ReloadForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            LoadedPluginAssemblyPaths.Clear();
            LoadedPatternConfigPaths.Clear();
            UnloadPluginAssemblyContexts();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            patternConfigCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            loadedPatternRuleCount = 0;
            pluginsLoaded = false;
            TypeInspectionLimitForTesting = null;
        }
    }

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests()
        => EnumeratePluginAssemblyPaths().ToArray();

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests(string? projectRoot)
        => EnumeratePluginAssemblyPaths(EnumeratePluginDirectories(projectRoot)).ToArray();

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests(IReadOnlyList<string> directories)
        => EnumeratePluginAssemblyPaths(directories).ToArray();

    internal static IReadOnlyList<string> EnumeratePatternConfigPathsFromDirectoryForTests(string directory)
        => EnumeratePatternConfigPathsFromDirectory(directory, workspaceRoot: null).ToArray();

    internal static IReadOnlyList<AssemblyLoadContext> PluginAssemblyLoadContextsForTests()
    {
        lock (Gate)
            return LoadedPluginAssemblyContexts.ToList();
    }

    internal static void LoadPluginAssembliesForTests(IReadOnlyList<string> directories)
        => LoadPluginAssemblies(directories);

    internal static void LoadPluginForTests(string pluginPath)
        => TryLoadPlugin(pluginPath);

    internal static bool TryMarkPluginAssemblyPathLoadedForTests(string pluginPath)
    {
        lock (Gate)
            return TryMarkPluginAssemblyPathLoaded(Path.GetFullPath(pluginPath));
    }

    internal static void LoadPluginsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot) || !WorkspacePluginsTrusted())
            return;

        LoadPluginAssemblies(EnumerateWorkspacePluginDirectories(Path.GetFullPath(projectRoot)));
    }

    internal static void LoadPatternConfigsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        LoadPluginsForProjectRoot(projectRoot);
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

        foreach (var patternPath in EnumerateUserPatternConfigPaths())
            TryLoadPatternConfig(patternPath);
    }

    private static void EnsurePluginsLoaded()
    {
        if (Volatile.Read(ref pluginsLoaded))
            return;

        lock (Gate)
        {
            if (pluginsLoaded)
                return;

            LoadPluginAssemblies(EnumeratePluginDirectories(projectRoot: null));
            pluginsLoaded = true;
        }
    }

    private static bool TryMarkPluginAssemblyPathLoaded(string fullPath)
    {
        if (LoadedPluginAssemblyPaths.Any(path => string.Equals(path, fullPath, PathCasing.ComparisonFor(fullPath))))
            return false;

        LoadedPluginAssemblyPaths.Add(fullPath);
        return true;
    }

    private static int ResolveTypeInspectionLimit()
        => TypeInspectionLimitForTesting is > 0 ? TypeInspectionLimitForTesting.Value : MaxExtensionAssemblyTypes;

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
