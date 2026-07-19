using System.Collections.ObjectModel;
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
    internal const int MaxRetainedWorkspaceSnapshots = 32;
    internal const string PluginLoadContextLifecycle = "isolated_worker_process_no_parent_load_context";
    internal static readonly TimeSpan PatternRegexTimeout = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan PatternTimeoutCooldown = TimeSpan.FromMinutes(1);
    internal static readonly IReadOnlyList<string> RegistrationPrecedence =
        ["built_in", "user_plugin", "user_pattern", "workspace_plugin", "workspace_pattern"];

    private static readonly object Gate = new();
    private static readonly object DefaultPluginDiscoveryGate = new();
    private static readonly Dictionary<string, ISymbolExtractor> SymbolExtractors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, IReferenceExtractor> ReferenceExtractors = new(StringComparer.Ordinal);
    private static readonly List<string> LoadedPluginAssemblyPaths = [];
    private static readonly List<ExtractorPluginWorkerClient> LoadedPluginWorkers = [];
    private static readonly List<ExecutableExtensionStagingHandle> LoadedPluginStagingHandles = [];
    private static readonly List<LoadedPluginState> LoadedPluginStates = [];
    private static readonly List<PluginLoadAttempt> PluginLoadAttempts = [];
    private static readonly IReadOnlyList<string> PatternConfigSearchPatterns = ["*.yaml", "*.yml"];
    private static readonly IReadOnlyDictionary<string, string> EmptyLanguageExtensions =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private static readonly List<ExtractorRegistryDiagnostic> Diagnostics = [];
    private const int DiagnosticLimit = 20;
    private static int pluginAssemblyCount;
    private static int skippedFileCount;
    private static int diagnosticTotalCount;
    private static bool pluginsLoaded;
    internal static int? TypeInspectionLimitForTesting { get; set; }
    internal static TimeSpan? WorkerOperationBudgetForTesting { get; set; }
    internal static string? UserPluginDirectoryForTesting { get; set; }
    private static bool suppressDefaultPluginDiscoveryForTesting;
    private static readonly AsyncLocal<bool> AuthorizedConfigurationScope = new();

    internal static IDisposable BeginAuthorizedConfigurationScope()
    {
        var previous = AuthorizedConfigurationScope.Value;
        AuthorizedConfigurationScope.Value = true;
        return new AuthorizedConfigurationScopeLease(previous);
    }

    private sealed class AuthorizedConfigurationScopeLease(bool previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            AuthorizedConfigurationScope.Value = previous;
            _disposed = true;
        }
    }

    public static IReadOnlyCollection<string> SymbolLanguages
        => GetSymbolLanguages(workspaceRoot: null);

    internal static IReadOnlyCollection<string> GetSymbolLanguages(string? workspaceRoot)
    {
        EnsurePluginsLoaded();
        return GetPatternSnapshot(workspaceRoot).SymbolExtractors.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyCollection<string> ReferenceLanguages
        => GetReferenceLanguages(workspaceRoot: null);

    internal static IReadOnlyCollection<string> GetReferenceLanguages(string? workspaceRoot)
    {
        EnsurePluginsLoaded();
        return GetPatternSnapshot(workspaceRoot).ReferenceExtractors.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyDictionary<string, string> LanguageExtensions
        => GetLanguageExtensions(workspaceRoot: null);

    internal static IReadOnlyDictionary<string, string> GetLanguageExtensions(string? workspaceRoot)
    {
        EnsurePluginsLoaded();
        var patternSnapshot = GetPatternSnapshot(workspaceRoot);
        return patternSnapshot.LanguageExtensions.Count == 0
            ? EmptyLanguageExtensions
            : patternSnapshot.LanguageExtensions;
    }

    internal static bool TryGetLanguageForExtension(string extension, out string language)
        => TryGetLanguageForExtension(extension, workspaceRoot: null, out language);

    internal static bool TryGetLanguageForExtension(string extension, string? workspaceRoot, out string language)
    {
        language = "";
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        EnsurePluginsLoaded();
        var patternSnapshot = GetPatternSnapshot(workspaceRoot);
        if (patternSnapshot.LanguageExtensions.TryGetValue(extension, out language!))
            return true;
        return false;
    }

    public static bool TryGetSymbolExtractor(string language, out ISymbolExtractor extractor)
        => TryGetSymbolExtractor(language, workspaceRoot: null, out extractor);

    internal static bool TryGetSymbolExtractor(
        string language,
        string? workspaceRoot,
        out ISymbolExtractor extractor)
    {
        EnsurePluginsLoaded();
        var patternSnapshot = GetPatternSnapshot(workspaceRoot);
        return patternSnapshot.SymbolExtractors.TryGetValue(language, out extractor!);
    }

    public static bool TryGetReferenceExtractor(string language, out IReferenceExtractor extractor)
        => TryGetReferenceExtractor(language, workspaceRoot: null, path: null, out extractor);

    internal static bool TryGetReferenceExtractor(
        string language,
        string? path,
        out IReferenceExtractor extractor)
        => TryGetReferenceExtractor(language, workspaceRoot: null, path, out extractor);

    internal static bool TryGetReferenceExtractor(
        string language,
        string? workspaceRoot,
        string? path,
        out IReferenceExtractor extractor)
    {
        EnsurePluginsLoaded();
        var snapshot = string.IsNullOrWhiteSpace(workspaceRoot)
            ? GetWorkspaceSnapshotForPath(path)
            : GetPatternSnapshot(workspaceRoot);
        return snapshot.ReferenceExtractors.TryGetValue(language, out extractor!);
    }

    internal static ExtractorRegistryStatus GetStatusSnapshot()
        => GetStatusSnapshot(workspaceRoot: null);

    internal static ExtractorRegistryStatus GetStatusSnapshot(string? workspaceRoot)
    {
        EnsurePluginsLoaded();
        var patternSnapshot = GetPatternSnapshot(workspaceRoot);
        lock (Gate)
        {
            var diagnostics = Diagnostics
                .Concat(patternSnapshot.Diagnostics)
                .Take(DiagnosticLimit)
                .ToList();
            return new ExtractorRegistryStatus
            {
                PluginAssemblyCount = pluginAssemblyCount + patternSnapshot.PluginAssemblyCount,
                PatternConfigCount = patternSnapshot.ConfigCount,
                SymbolExtractorCount = patternSnapshot.SymbolExtractors.Count,
                ReferenceExtractorCount = patternSnapshot.ReferenceExtractors.Count,
                RetainedLoadContextCount = 0,
                LoadContextLifecycle = PluginLoadContextLifecycle,
                SkippedFileCount = skippedFileCount + patternSnapshot.SkippedFileCount,
                DiagnosticCount = diagnosticTotalCount + patternSnapshot.DiagnosticTotalCount,
                DiagnosticLimit = DiagnosticLimit,
                DiagnosticsTruncated = diagnosticTotalCount + patternSnapshot.DiagnosticTotalCount > diagnostics.Count,
                Diagnostics = diagnostics.Count == 0 ? null : diagnostics,
                PatternConfigs = patternSnapshot.Configs.Count == 0 ? null : patternSnapshot.Configs.ToList(),
                SnapshotScope = string.IsNullOrWhiteSpace(workspaceRoot) ? "user" : "workspace",
                RegistrationPrecedence = RegistrationPrecedence,
            };
        }
    }

    public static void Register(ISymbolExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var language = NormalizePluginLanguage(extractor.Language);
        lock (Gate)
        {
            SymbolExtractors[language] = extractor;
            PublishUserExtractorSnapshot();
        }
        lock (DefaultPatternWorkspace.Gate)
            DefaultPatternWorkspace.PublishSnapshot();
    }

    public static void Register(IReferenceExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var language = NormalizePluginLanguage(extractor.Language);
        lock (Gate)
        {
            ReferenceExtractors[language] = extractor;
            PublishUserExtractorSnapshot();
        }
        lock (DefaultPatternWorkspace.Gate)
            DefaultPatternWorkspace.PublishSnapshot();
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            PublishUserExtractorSnapshot();
            LoadedPluginAssemblyPaths.Clear();
            DisposePluginWorkers();
            DisposePluginStagingHandles();
            LoadedPluginStates.Clear();
            PluginLoadAttempts.Clear();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            pluginsLoaded = true;
            TypeInspectionLimitForTesting = null;
            WorkerOperationBudgetForTesting = null;
            ExtractorPluginWorkerClient.ProcessStartedForTesting = null;
            UserPluginDirectoryForTesting = null;
            suppressDefaultPluginDiscoveryForTesting = true;
            UserPatternDirectoryOverrideForTests = null;
            WorkspacePluginLoadedBeforeCommitForTesting = null;
        }
        ResetPatternWorkspaces();
    }

    internal static void ReloadForTests()
    {
        lock (Gate)
        {
            SymbolExtractors.Clear();
            ReferenceExtractors.Clear();
            PublishUserExtractorSnapshot();
            LoadedPluginAssemblyPaths.Clear();
            DisposePluginWorkers();
            DisposePluginStagingHandles();
            LoadedPluginStates.Clear();
            PluginLoadAttempts.Clear();
            Diagnostics.Clear();
            pluginAssemblyCount = 0;
            skippedFileCount = 0;
            diagnosticTotalCount = 0;
            pluginsLoaded = false;
            TypeInspectionLimitForTesting = null;
            WorkerOperationBudgetForTesting = null;
            ExtractorPluginWorkerClient.ProcessStartedForTesting = null;
            UserPluginDirectoryForTesting = null;
            suppressDefaultPluginDiscoveryForTesting = false;
            UserPatternDirectoryOverrideForTests = null;
            WorkspacePluginLoadedBeforeCommitForTesting = null;
        }
        ResetPatternWorkspaces();
    }

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests()
        => EnumeratePluginAssemblyPaths().ToArray();

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests(string? projectRoot)
        => EnumeratePluginAssemblyPaths(EnumeratePluginDirectories(projectRoot)).ToArray();

    internal static IReadOnlyList<string> EnumeratePluginAssemblyPathsForTests(IReadOnlyList<string> directories)
        => EnumeratePluginAssemblyPaths(directories).ToArray();

    internal static IReadOnlyList<string> EnumeratePatternConfigPathsFromDirectoryForTests(string directory)
        => EnumeratePatternConfigPathsFromDirectory(DefaultPatternWorkspace, directory, workspaceRoot: null).ToArray();

    internal static int PluginWorkerCountForTests()
    {
        lock (Gate)
            return LoadedPluginWorkers.Count;
    }

    internal static IReadOnlyList<string> PluginStagedAssemblyPathsForTests()
    {
        lock (Gate)
            return LoadedPluginStagingHandles.Select(handle => handle.StagedPath).ToList();
    }

    internal static int WorkspaceSnapshotCountForTests()
    {
        lock (Gate)
            return PatternWorkspaces.Count;
    }

    internal static long WorkspaceGenerationForTests()
    {
        lock (Gate)
            return workspaceGeneration;
    }

    internal static long WorkspaceReloadSequenceForTests()
    {
        lock (Gate)
            return workspaceReloadSequence;
    }

    internal static int WorkspacePluginWorkerCountForTests(string workspaceRoot)
    {
        var fullRoot = Path.GetFullPath(workspaceRoot);
        PatternWorkspaceState? state;
        lock (Gate)
        {
            state = PatternWorkspaces.FirstOrDefault(candidate =>
                PathCasing.PathsEqual(candidate.WorkspaceRoot!, fullRoot));
        }

        if (state == null)
            return 0;

        lock (state.Gate)
            return state.PluginStates.Count;
    }

    internal static void LoadPluginAssembliesForTests(IReadOnlyList<string> directories)
        => LoadPluginAssemblies(directories);

    internal static void LoadPluginForTests(string pluginPath)
        => TryLoadPlugin(pluginPath);

    internal static void LoadPatternConfigForTests(string patternPath)
        => TryLoadPatternConfig(DefaultPatternWorkspace, patternPath, "user");

    internal static bool TryMarkPatternConfigPathLoadedForTests(string patternPath)
    {
        lock (DefaultPatternWorkspace.Gate)
        {
            var added = TryMarkPatternConfigPathLoaded(DefaultPatternWorkspace, Path.GetFullPath(patternPath));
            DefaultPatternWorkspace.PublishSnapshot();
            return added;
        }
    }

    internal static bool TryMarkPluginAssemblyPathLoadedForTests(string pluginPath)
    {
        lock (Gate)
            return TryMarkPluginAssemblyPathLoaded(Path.GetFullPath(pluginPath));
    }

    internal static void LoadPluginsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        var fullRoot = Path.GetFullPath(projectRoot);
        LoadWorkspacePlugins(GetOrCreatePatternWorkspace(fullRoot), fullRoot);
    }

    internal static void LoadPatternConfigsForProjectRoot(string? projectRoot)
    {
        RefreshDefaultPlugins();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        var fullRoot = Path.GetFullPath(projectRoot);
        var state = GetOrCreatePatternWorkspace(fullRoot);
        LoadWorkspacePlugins(state, fullRoot);
        LoadPatternConfigsForProjectRoot(state, fullRoot);
    }

    internal static void ReloadPatternConfigsForProjectRoot(string? projectRoot)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(projectRoot))
            return;

        var fullRoot = Path.GetFullPath(projectRoot);
        var state = StagePatternWorkspace(fullRoot);
        var committed = false;
        try
        {
            LoadWorkspacePlugins(state, fullRoot);
            LoadPatternConfigsForProjectRoot(state, fullRoot);
            committed = TryReplacePatternWorkspace(state);
        }
        finally
        {
            if (!committed)
                AbandonPatternWorkspace(state);
        }
    }

    internal static void ReloadAuthorizedPatternConfigsForProjectRoot(
        string projectRoot,
        Func<string, IEnumerable<string>> enumerateFileSystemEntries,
        Func<string, FileStream> openFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(enumerateFileSystemEntries);
        ArgumentNullException.ThrowIfNull(openFile);

        var fullRoot = Path.GetFullPath(projectRoot);
        var state = StagePatternWorkspace(fullRoot, includeUserConfiguration: false);
        var committed = false;
        try
        {
            LoadAuthorizedPatternConfigsFromDirectory(
                state,
                fullRoot,
                enumerateFileSystemEntries,
                openFile);

            committed = TryReplacePatternWorkspace(state);
        }
        finally
        {
            if (!committed)
                AbandonPatternWorkspace(state);
        }
    }

    internal static void LoadAuthorizedPatternConfigsForDirectory(
        string projectRoot,
        string directory,
        Func<string, IEnumerable<string>> enumerateFileSystemEntries,
        Func<string, FileStream> openFile)
    {
        var fullRoot = Path.GetFullPath(projectRoot);
        var fullDirectory = Path.GetFullPath(directory);
        if (!PathCasing.IsFullPathEqualOrParent(fullRoot, fullDirectory))
            return;

        PatternWorkspaceState? state;
        lock (Gate)
        {
            state = PatternWorkspaces.FirstOrDefault(candidate =>
                !candidate.IncludeUserConfiguration
                && PathCasing.PathsEqual(candidate.WorkspaceRoot!, fullRoot));
        }

        if (state == null)
            return;

        LoadAuthorizedPatternConfigsFromDirectory(
            state,
            fullDirectory,
            enumerateFileSystemEntries,
            openFile);
    }

    private static void LoadAuthorizedPatternConfigsFromDirectory(
        PatternWorkspaceState state,
        string directory,
        Func<string, IEnumerable<string>> enumerateFileSystemEntries,
        Func<string, FileStream> openFile)
    {
        var patternDirectory = Path.Combine(directory, ".cdidx", "patterns");
        if (!Directory.Exists(patternDirectory))
            return;

        var candidateCount = 0;
        foreach (var path in enumerateFileSystemEntries(patternDirectory))
        {
            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (candidateCount >= MaxPatternConfigCandidatesPerDirectory)
            {
                ReportPatternDirectorySkipped(
                    state,
                    patternDirectory,
                    $"too many pattern config candidates (maximum {MaxPatternConfigCandidatesPerDirectory} per directory)");
                break;
            }

            candidateCount++;
            TryLoadPatternConfig(state, path, "workspace", openFile);
        }
    }

    private static void LoadWorkspacePlugins(PatternWorkspaceState state, string fullRoot)
    {
        if (!WorkspacePluginsTrusted())
            return;

        LoadPluginAssemblies(EnumerateWorkspacePluginDirectories(fullRoot), state);
    }

    internal static void RegisterForWorkspaceForTests(string workspaceRoot, ISymbolExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var state = GetOrCreatePatternWorkspace(Path.GetFullPath(workspaceRoot));
        lock (state.Gate)
        {
            if (state.Retired)
                return;

            state.WorkspaceSymbolExtractors[NormalizePluginLanguage(extractor.Language)] = extractor;
            state.PublishSnapshot();
        }
    }

    internal static void RegisterForWorkspaceForTests(string workspaceRoot, IReferenceExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        var state = GetOrCreatePatternWorkspace(Path.GetFullPath(workspaceRoot));
        lock (state.Gate)
        {
            if (state.Retired)
                return;

            state.WorkspaceReferenceExtractors[NormalizePluginLanguage(extractor.Language)] = extractor;
            state.PublishSnapshot();
        }
    }

    private static void LoadPatternConfigsForProjectRoot(PatternWorkspaceState state, string fullRoot)
    {
        foreach (var patternPath in EnumerateUserPatternConfigPaths(state))
            TryLoadPatternConfig(state, patternPath, "user");

        foreach (var patternPath in EnumeratePatternConfigPaths(state, fullRoot, includeUserDirectory: false))
            TryLoadPatternConfig(state, patternPath, "workspace");
    }

    internal static void LoadPatternConfigsForPath(
        string? path,
        string? workspaceRoot,
        bool includeWorkspaceRoot = true)
    {
        EnsurePluginsLoaded();
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(workspaceRoot))
            return;

        var fullRoot = Path.GetFullPath(workspaceRoot);
        var directory = Path.GetFullPath(path);
        if (!Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory) ?? string.Empty;

        if (string.IsNullOrEmpty(directory) || !PathCasing.IsFullPathEqualOrParent(fullRoot, directory))
            return;

        var state = GetOrCreatePatternWorkspace(fullRoot);
        foreach (var patternPath in EnumerateUserPatternConfigPaths(state))
            TryLoadPatternConfig(state, patternPath, "user");

        while (PathCasing.IsFullPathEqualOrParent(fullRoot, directory))
        {
            if (!includeWorkspaceRoot && PathCasing.PathsEqual(directory, fullRoot))
                break;

            foreach (var patternPath in EnumeratePatternConfigPaths(state, directory, includeUserDirectory: false))
                TryLoadPatternConfig(state, patternPath, "workspace");

            if (PathCasing.PathsEqual(directory, fullRoot))
                break;
            directory = Directory.GetParent(directory)?.FullName ?? string.Empty;
            if (string.IsNullOrEmpty(directory))
                break;
        }

    }

    private static void EnsurePluginsLoaded()
    {
        if (AuthorizedConfigurationScope.Value)
            return;
        if (Volatile.Read(ref suppressDefaultPluginDiscoveryForTesting))
            return;
        if (Volatile.Read(ref pluginsLoaded))
            return;

        lock (DefaultPluginDiscoveryGate)
        {
            if (Volatile.Read(ref pluginsLoaded))
                return;

            LoadPluginAssemblies(EnumeratePluginDirectories(projectRoot: null));
            Volatile.Write(ref pluginsLoaded, true);
        }
    }

    private static void RefreshDefaultPlugins()
    {
        if (AuthorizedConfigurationScope.Value)
            return;
        if (Volatile.Read(ref suppressDefaultPluginDiscoveryForTesting))
            return;

        lock (DefaultPluginDiscoveryGate)
        {
            LoadPluginAssemblies(EnumeratePluginDirectories(projectRoot: null));
            Volatile.Write(ref pluginsLoaded, true);
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

    private sealed record PluginLoadAttempt(string Path, string Fingerprint, bool Succeeded);

    private sealed record PluginRegistration(
        string? SymbolLanguage,
        string? ReferenceLanguage,
        IsolatedPluginExtractorProxy Proxy,
        bool SupportsSymbols,
        bool SupportsReferences);

    private sealed record LoadedPluginState(
        string Path,
        string Fingerprint,
        ExtractorPluginWorkerClient Worker,
        ExecutableExtensionStagingHandle Staging,
        IReadOnlyList<PluginRegistration> Registrations);

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

    private static bool TryGetLanguageForExtension(
        string extension,
        IEnumerable<ISymbolExtractor> plugins,
        out string language)
    {
        foreach (var plugin in plugins)
        {
            if (TryMatchPluginExtension(extension, plugin.Language, plugin.FileExtensions, out language))
                return true;
        }

        language = "";
        return false;
    }

    private static bool TryGetLanguageForExtension(
        string extension,
        IEnumerable<IReferenceExtractor> plugins,
        out string language)
    {
        foreach (var plugin in plugins)
        {
            if (TryMatchPluginExtension(extension, plugin.Language, plugin.FileExtensions, out language))
                return true;
        }

        language = "";
        return false;
    }

    private static bool TryMatchPluginExtension(
        string extension,
        string pluginLanguage,
        IReadOnlyCollection<string> pluginExtensions,
        out string language)
    {
        foreach (var pluginExtension in pluginExtensions)
        {
            var normalizedExtension = NormalizePluginExtension(pluginExtension);
            if (normalizedExtension != null
                && string.Equals(normalizedExtension, extension, StringComparison.OrdinalIgnoreCase))
            {
                language = NormalizePluginLanguage(pluginLanguage);
                return true;
            }
        }

        language = "";
        return false;
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
