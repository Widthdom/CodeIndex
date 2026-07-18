using System.Collections.ObjectModel;
using System.Runtime.Loader;
using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private sealed class PatternWorkspaceState(string? workspaceRoot)
    {
        private ExtractorWorkspaceSnapshot snapshot = ExtractorWorkspaceSnapshot.Empty;

        internal object Gate { get; } = new();
        internal string? WorkspaceRoot { get; } = workspaceRoot;
        internal Dictionary<string, ISymbolExtractor> PatternSymbolExtractors { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> PatternSources { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ISymbolExtractor> WorkspaceSymbolExtractors { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, IReferenceExtractor> WorkspaceReferenceExtractors { get; } = new(StringComparer.Ordinal);
        internal List<string> LoadedPluginPaths { get; } = [];
        internal List<AssemblyLoadContext> PluginLoadContexts { get; } = [];
        internal List<string> LoadedPaths { get; } = [];
        internal List<(string Path, PatternConfigFingerprint Fingerprint)> FailedFingerprints { get; } = [];
        internal List<PatternConfigStatus> Configs { get; } = [];
        internal List<ExtractorRegistryDiagnostic> Diagnostics { get; } = [];
        internal int ConfigCount { get; set; }
        internal int SkippedFileCount { get; set; }
        internal int DiagnosticTotalCount { get; set; }
        internal int RuleCount { get; set; }
        internal int PluginAssemblyCount { get; set; }
        internal bool Retired { get; private set; }
        internal long LastAccessSequence { get; set; }

        internal ExtractorWorkspaceSnapshot GetSnapshot()
            => Volatile.Read(ref snapshot);

        internal void PublishSnapshot()
        {
            if (Retired)
            {
                Volatile.Write(ref snapshot, ExtractorWorkspaceSnapshot.Empty);
                return;
            }

            var user = GetUserExtractorSnapshot();
            var symbolExtractors = new Dictionary<string, ISymbolExtractor>(StringComparer.Ordinal);
            var referenceExtractors = new Dictionary<string, IReferenceExtractor>(StringComparer.Ordinal);

            CopyPatternExtractors("workspace", symbolExtractors);
            foreach (var (language, extractor) in WorkspaceSymbolExtractors)
                symbolExtractors[language] = extractor;
            foreach (var (language, extractor) in WorkspaceReferenceExtractors)
                referenceExtractors[language] = extractor;
            CopyPatternExtractors("user", symbolExtractors);
            foreach (var (language, extractor) in user.SymbolExtractors)
                symbolExtractors[language] = extractor;
            foreach (var (language, extractor) in user.ReferenceExtractors)
                referenceExtractors[language] = extractor;

            var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SetPatternExtensions("workspace");
            SetLanguageExtensions(extensions, WorkspaceSymbolExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
            SetLanguageExtensions(extensions, WorkspaceReferenceExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
            SetPatternExtensions("user");
            SetLanguageExtensions(extensions, user.SymbolExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
            SetLanguageExtensions(extensions, user.ReferenceExtractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
            Volatile.Write(
                ref snapshot,
                new ExtractorWorkspaceSnapshot(
                    new ReadOnlyDictionary<string, ISymbolExtractor>(symbolExtractors),
                    new ReadOnlyDictionary<string, IReferenceExtractor>(referenceExtractors),
                    new ReadOnlyDictionary<string, string>(extensions),
                    Configs.ToArray(),
                    Diagnostics.ToArray(),
                    ConfigCount,
                    SkippedFileCount,
                    DiagnosticTotalCount,
                    RuleCount,
                    PluginAssemblyCount,
                    PluginLoadContexts.Count));

            void CopyPatternExtractors(string source, Dictionary<string, ISymbolExtractor> target)
            {
                foreach (var (language, extractor) in PatternSymbolExtractors)
                {
                    if (PatternSources.TryGetValue(language, out var registrationSource)
                        && string.Equals(registrationSource, source, StringComparison.Ordinal))
                    {
                        target[language] = extractor;
                    }
                }
            }

            void SetPatternExtensions(string source)
            {
                SetLanguageExtensions(
                    extensions,
                    PatternSymbolExtractors
                        .Where(entry => PatternSources.TryGetValue(entry.Key, out var registrationSource)
                                        && string.Equals(registrationSource, source, StringComparison.Ordinal))
                        .Select(entry => (entry.Value.Language, entry.Value.FileExtensions)));
            }
        }

        internal void Reset()
        {
            lock (Gate)
            {
                Retired = false;
                ClearState();
                PublishSnapshot();
            }
        }

        internal void Retire()
        {
            lock (Gate)
            {
                Retired = true;
                ClearState();
                Volatile.Write(ref snapshot, ExtractorWorkspaceSnapshot.Empty);
            }
        }

        private void ClearState()
        {
            PatternSymbolExtractors.Clear();
            PatternSources.Clear();
            WorkspaceSymbolExtractors.Clear();
            WorkspaceReferenceExtractors.Clear();
            LoadedPluginPaths.Clear();
            UnloadPluginAssemblyContexts(PluginLoadContexts);
            LoadedPaths.Clear();
            FailedFingerprints.Clear();
            Configs.Clear();
            Diagnostics.Clear();
            ConfigCount = 0;
            SkippedFileCount = 0;
            DiagnosticTotalCount = 0;
            RuleCount = 0;
            PluginAssemblyCount = 0;
        }
    }

    private sealed record ExtractorWorkspaceSnapshot(
        IReadOnlyDictionary<string, ISymbolExtractor> SymbolExtractors,
        IReadOnlyDictionary<string, IReferenceExtractor> ReferenceExtractors,
        IReadOnlyDictionary<string, string> LanguageExtensions,
        IReadOnlyList<PatternConfigStatus> Configs,
        IReadOnlyList<ExtractorRegistryDiagnostic> Diagnostics,
        int ConfigCount,
        int SkippedFileCount,
        int DiagnosticTotalCount,
        int RuleCount,
        int PluginAssemblyCount,
        int RetainedLoadContextCount)
    {
        internal static ExtractorWorkspaceSnapshot Empty { get; } = new(
            new ReadOnlyDictionary<string, ISymbolExtractor>(new Dictionary<string, ISymbolExtractor>(StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, IReferenceExtractor>(new Dictionary<string, IReferenceExtractor>(StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Array.Empty<PatternConfigStatus>(),
            Array.Empty<ExtractorRegistryDiagnostic>(),
            0,
            0,
            0,
            0,
            0,
            0);
    }

    private sealed record UserExtractorSnapshot(
        IReadOnlyDictionary<string, ISymbolExtractor> SymbolExtractors,
        IReadOnlyDictionary<string, IReferenceExtractor> ReferenceExtractors)
    {
        internal static UserExtractorSnapshot Empty { get; } = new(
            new ReadOnlyDictionary<string, ISymbolExtractor>(new Dictionary<string, ISymbolExtractor>(StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, IReferenceExtractor>(new Dictionary<string, IReferenceExtractor>(StringComparer.Ordinal)));
    }

    private static readonly PatternWorkspaceState DefaultPatternWorkspace = new(workspaceRoot: null);
    private static readonly List<PatternWorkspaceState> PatternWorkspaces = [];
    private static UserExtractorSnapshot userExtractorSnapshot = UserExtractorSnapshot.Empty;
    private static long workspaceAccessSequence;

    private static PatternWorkspaceState CreatePatternWorkspace(string workspaceRoot)
    {
        var state = new PatternWorkspaceState(workspaceRoot);
        lock (state.Gate)
            state.PublishSnapshot();
        return state;
    }

    private static void ReplacePatternWorkspace(PatternWorkspaceState state)
    {
        PatternWorkspaceState? replaced = null;
        PatternWorkspaceState? evicted = null;
        lock (Gate)
        {
            var index = PatternWorkspaces.FindIndex(existing =>
                PathCasing.PathsEqual(existing.WorkspaceRoot!, state.WorkspaceRoot!));
            if (index >= 0)
            {
                replaced = PatternWorkspaces[index];
                PatternWorkspaces[index] = state;
            }
            else
                PatternWorkspaces.Add(state);

            TouchPatternWorkspace(state);
            evicted = TrimPatternWorkspaces(state);
        }

        replaced?.Retire();
        if (evicted != null && !ReferenceEquals(evicted, replaced))
            evicted.Retire();
    }

    private static PatternWorkspaceState GetOrCreatePatternWorkspace(string workspaceRoot)
    {
        PatternWorkspaceState state;
        PatternWorkspaceState? evicted;
        lock (Gate)
        {
            var existing = PatternWorkspaces.FirstOrDefault(candidate =>
                PathCasing.PathsEqual(candidate.WorkspaceRoot!, workspaceRoot));
            if (existing != null)
            {
                TouchPatternWorkspace(existing);
                return existing;
            }

            state = CreatePatternWorkspace(workspaceRoot);
            PatternWorkspaces.Add(state);
            TouchPatternWorkspace(state);
            evicted = TrimPatternWorkspaces(state);
        }

        evicted?.Retire();
        return state;
    }

    private static ExtractorWorkspaceSnapshot GetPatternSnapshot(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return DefaultPatternWorkspace.GetSnapshot();

        var fullRoot = Path.GetFullPath(workspaceRoot);
        lock (Gate)
        {
            var state = PatternWorkspaces.FirstOrDefault(candidate =>
                PathCasing.PathsEqual(candidate.WorkspaceRoot!, fullRoot));
            if (state == null)
                return DefaultPatternWorkspace.GetSnapshot();

            TouchPatternWorkspace(state);
            return state.GetSnapshot();
        }
    }

    private static ExtractorWorkspaceSnapshot GetWorkspaceSnapshotForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DefaultPatternWorkspace.GetSnapshot();

        var fullPath = Path.GetFullPath(path);
        lock (Gate)
        {
            var state = PatternWorkspaces
                .Where(candidate => PathCasing.IsFullPathEqualOrParent(candidate.WorkspaceRoot!, fullPath))
                .OrderByDescending(candidate => candidate.WorkspaceRoot!.Length)
                .FirstOrDefault();
            if (state == null)
                return DefaultPatternWorkspace.GetSnapshot();

            TouchPatternWorkspace(state);
            return state.GetSnapshot();
        }
    }

    private static void TouchPatternWorkspace(PatternWorkspaceState state)
        => state.LastAccessSequence = ++workspaceAccessSequence;

    private static PatternWorkspaceState? TrimPatternWorkspaces(PatternWorkspaceState retainedState)
    {
        if (PatternWorkspaces.Count <= MaxRetainedWorkspaceSnapshots)
            return null;

        var evicted = PatternWorkspaces
            .Where(state => !ReferenceEquals(state, retainedState))
            .OrderBy(state => state.LastAccessSequence)
            .First();
        PatternWorkspaces.Remove(evicted);
        return evicted;
    }

    private static void ResetPatternWorkspaces()
    {
        DefaultPatternWorkspace.Reset();
        ReleaseWorkspaceSnapshots();
    }

    internal static void ReleaseWorkspaceSnapshots()
    {
        PatternWorkspaceState[] workspaces;
        lock (Gate)
        {
            workspaces = PatternWorkspaces.ToArray();
            PatternWorkspaces.Clear();
            workspaceAccessSequence = 0;
        }

        foreach (var workspace in workspaces)
            workspace.Retire();
    }

    private static UserExtractorSnapshot GetUserExtractorSnapshot()
        => Volatile.Read(ref userExtractorSnapshot);

    private static void PublishUserExtractorSnapshot()
    {
        Volatile.Write(
            ref userExtractorSnapshot,
            new UserExtractorSnapshot(
                new ReadOnlyDictionary<string, ISymbolExtractor>(new Dictionary<string, ISymbolExtractor>(SymbolExtractors, StringComparer.Ordinal)),
                new ReadOnlyDictionary<string, IReferenceExtractor>(new Dictionary<string, IReferenceExtractor>(ReferenceExtractors, StringComparer.Ordinal))));
    }

    private static void SetLanguageExtensions(
        Dictionary<string, string> target,
        IEnumerable<(string Language, IReadOnlyCollection<string> FileExtensions)> extractors)
    {
        foreach (var (language, fileExtensions) in extractors)
        {
            var normalizedLanguage = NormalizePluginLanguage(language);
            foreach (var extension in fileExtensions)
            {
                var normalizedExtension = NormalizePluginExtension(extension);
                if (normalizedExtension != null)
                    target[normalizedExtension] = normalizedLanguage;
            }
        }
    }

    private static bool PatternConfigPathIsLoaded(PatternWorkspaceState state, string fullPath)
        => state.LoadedPaths.Any(path => PathCasing.PathsEqual(path, fullPath));

    private static bool TryMarkPatternConfigPathLoaded(PatternWorkspaceState state, string fullPath)
    {
        if (PatternConfigPathIsLoaded(state, fullPath))
            return false;

        state.LoadedPaths.Add(fullPath);
        return true;
    }

    private static bool TryGetFailedPatternConfigFingerprint(
        PatternWorkspaceState state,
        string fullPath,
        out PatternConfigFingerprint fingerprint)
    {
        foreach (var entry in state.FailedFingerprints)
        {
            if (!PathCasing.PathsEqual(entry.Path, fullPath))
                continue;

            fingerprint = entry.Fingerprint;
            return true;
        }

        fingerprint = null!;
        return false;
    }

    private static void SetFailedPatternConfigFingerprint(
        PatternWorkspaceState state,
        string fullPath,
        PatternConfigFingerprint fingerprint)
    {
        RemoveFailedPatternConfigFingerprint(state, fullPath);
        state.FailedFingerprints.Add((fullPath, fingerprint));
    }

    private static void RemoveFailedPatternConfigFingerprint(PatternWorkspaceState state, string fullPath)
        => state.FailedFingerprints.RemoveAll(entry => PathCasing.PathsEqual(entry.Path, fullPath));
}
