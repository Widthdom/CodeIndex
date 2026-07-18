using System.Collections.ObjectModel;
using CodeIndex.Cli;

namespace CodeIndex.Indexer.Extensibility;

public static partial class ExtractorPluginRegistry
{
    private sealed class PatternWorkspaceState(string? workspaceRoot)
    {
        private PatternWorkspaceSnapshot snapshot = PatternWorkspaceSnapshot.Empty;

        internal object Gate { get; } = new();
        internal string? WorkspaceRoot { get; } = workspaceRoot;
        internal Dictionary<string, ISymbolExtractor> SymbolExtractors { get; } = new(StringComparer.Ordinal);
        internal List<string> LoadedPaths { get; } = [];
        internal List<(string Path, PatternConfigFingerprint Fingerprint)> FailedFingerprints { get; } = [];
        internal List<PatternConfigStatus> Configs { get; } = [];
        internal List<ExtractorRegistryDiagnostic> Diagnostics { get; } = [];
        internal int ConfigCount { get; set; }
        internal int SkippedFileCount { get; set; }
        internal int DiagnosticTotalCount { get; set; }
        internal int RuleCount { get; set; }

        internal PatternWorkspaceSnapshot GetSnapshot()
            => Volatile.Read(ref snapshot);

        internal void PublishSnapshot()
        {
            var extractors = new Dictionary<string, ISymbolExtractor>(SymbolExtractors, StringComparer.Ordinal);
            var extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddLanguageExtensions(
                extensions,
                extractors.Values.Select(extractor => (extractor.Language, extractor.FileExtensions)));
            Volatile.Write(
                ref snapshot,
                new PatternWorkspaceSnapshot(
                    new ReadOnlyDictionary<string, ISymbolExtractor>(extractors),
                    new ReadOnlyDictionary<string, string>(extensions),
                    Configs.ToArray(),
                    Diagnostics.ToArray(),
                    ConfigCount,
                    SkippedFileCount,
                    DiagnosticTotalCount,
                    RuleCount));
        }

        internal void Reset()
        {
            lock (Gate)
            {
                SymbolExtractors.Clear();
                LoadedPaths.Clear();
                FailedFingerprints.Clear();
                Configs.Clear();
                Diagnostics.Clear();
                ConfigCount = 0;
                SkippedFileCount = 0;
                DiagnosticTotalCount = 0;
                RuleCount = 0;
                PublishSnapshot();
            }
        }
    }

    private sealed record PatternWorkspaceSnapshot(
        IReadOnlyDictionary<string, ISymbolExtractor> SymbolExtractors,
        IReadOnlyDictionary<string, string> LanguageExtensions,
        IReadOnlyList<PatternConfigStatus> Configs,
        IReadOnlyList<ExtractorRegistryDiagnostic> Diagnostics,
        int ConfigCount,
        int SkippedFileCount,
        int DiagnosticTotalCount,
        int RuleCount)
    {
        internal static PatternWorkspaceSnapshot Empty { get; } = new(
            new ReadOnlyDictionary<string, ISymbolExtractor>(new Dictionary<string, ISymbolExtractor>(StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            Array.Empty<PatternConfigStatus>(),
            Array.Empty<ExtractorRegistryDiagnostic>(),
            0,
            0,
            0,
            0);
    }

    private static readonly PatternWorkspaceState DefaultPatternWorkspace = new(workspaceRoot: null);
    private static readonly List<PatternWorkspaceState> PatternWorkspaces = [];

    private static PatternWorkspaceState CreatePatternWorkspace(string workspaceRoot)
        => new(workspaceRoot);

    private static void ReplacePatternWorkspace(PatternWorkspaceState state)
    {
        lock (Gate)
        {
            var index = PatternWorkspaces.FindIndex(existing =>
                PathCasing.PathsEqual(existing.WorkspaceRoot!, state.WorkspaceRoot!));
            if (index >= 0)
                PatternWorkspaces[index] = state;
            else
                PatternWorkspaces.Add(state);
        }
    }

    private static PatternWorkspaceState GetOrCreatePatternWorkspace(string workspaceRoot)
    {
        lock (Gate)
        {
            var existing = PatternWorkspaces.FirstOrDefault(state =>
                PathCasing.PathsEqual(state.WorkspaceRoot!, workspaceRoot));
            if (existing != null)
                return existing;

            var created = CreatePatternWorkspace(workspaceRoot);
            PatternWorkspaces.Add(created);
            return created;
        }
    }

    private static PatternWorkspaceSnapshot GetPatternSnapshot(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return DefaultPatternWorkspace.GetSnapshot();

        var fullRoot = Path.GetFullPath(workspaceRoot);
        lock (Gate)
        {
            return PatternWorkspaces.FirstOrDefault(state =>
                       PathCasing.PathsEqual(state.WorkspaceRoot!, fullRoot))?.GetSnapshot()
                   ?? PatternWorkspaceSnapshot.Empty;
        }
    }

    private static void ResetPatternWorkspaces()
    {
        DefaultPatternWorkspace.Reset();
        lock (Gate)
            PatternWorkspaces.Clear();
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
