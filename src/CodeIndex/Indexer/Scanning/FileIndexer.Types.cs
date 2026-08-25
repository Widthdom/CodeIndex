namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    public enum SymlinkPolicy
    {
        None,
        Internal,
        All,
    }

    internal enum FileProbeStatus
    {
        Supported,
        Unsupported,
        ProbeFailed,
        Missing,
    }

    internal enum LanguageDetectionConfidence
    {
        High,
        Medium,
        Low,
    }

    internal const string HeaderLexicalMarkerDetectionSource = "header_lexical_marker";
    internal const string HeaderSampledLexicalMarkerDetectionSource = "header_sampled_lexical_marker";
    internal const string HeaderLexicalFallbackDetectionSource = "header_lexical_fallback";
    internal const string HeaderSampledLexicalFallbackDetectionSource = "header_sampled_lexical_fallback";
    internal const string HeaderExtensionFallbackDetectionSource = "header_extension_fallback";
    internal const string LanguageMapOverrideDetectionSource = "language_map_override";
    internal const string ExactFilenameDetectionSource = "exact_filename";
    internal const string FilenamePrefixPatternDetectionSource = "filename_prefix_pattern";
    internal const string ShebangDetectionSource = "shebang";
    internal const string ZshCompdefDetectionSource = "zsh_compdef";
    internal const string AmbiguousContentDetectionSource = "content";
    internal const string AmbiguousProjectDetectionSource = "project";
    internal const string AmbiguousFallbackDetectionSource = "ambiguous";

    internal readonly record struct LanguageDetectionResult(
        FileProbeStatus Status,
        string? Language,
        string? DetectionSource = null,
        LanguageDetectionConfidence? Confidence = null);

    internal static string GetLanguageDetectionConfidenceCode(LanguageDetectionConfidence confidence) => confidence switch
    {
        LanguageDetectionConfidence.High => "high",
        LanguageDetectionConfidence.Medium => "medium",
        LanguageDetectionConfidence.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, null),
    };

    public enum ScanIssueSeverity
    {
        Warning,
        Error,
    }

    public readonly record struct ScanError(string Path, string Message, ScanIssueSeverity Severity = ScanIssueSeverity.Error)
    {
        public bool IsFatal => Severity == ScanIssueSeverity.Error;
    }

    internal readonly record struct FileIdentity(ulong DeviceId, ulong Inode);

    internal readonly record struct FileHandleSnapshot(
        long Length,
        DateTime ModifiedUtc,
        FileIdentity? Identity);

    internal readonly record struct IndexingFileTarget(
        string FilePath,
        string RelativePath,
        string DisplayRelativePath,
        string IndexPath,
        string? ReusableLanguage,
        bool GeneratedExtractionSuppressed)
    {
        // Keep the full-scan and MCP processing vocabulary compact while the field name
        // documents that non-reusable detections (.h and extensionless script/content
        // detection) must still be repeated after the file is securely opened.
        // full-scan / MCP 側の語彙は簡潔に保ちつつ、再利用不可の検出（.h と extensionless
        // script/content）はsecure open後に再実行する必要があることをfield名で明示する。
        internal string? Language => ReusableLanguage;
    }

    internal sealed class IndexingFileTargetCollection : IReadOnlyList<string>
    {
        private readonly List<IndexingFileTarget> _targets;

        internal IndexingFileTargetCollection(int capacity)
        {
            _targets = new List<IndexingFileTarget>(capacity);
        }

        internal IReadOnlyList<string> FilePaths => this;

        public int Count => _targets.Count;

        // Preserve array-style indexed-loop call sites while Count remains the
        // IReadOnlyCollection<string> contract exposed by the path view.
        // path view の IReadOnlyCollection<string> 契約は Count のまま保ちつつ、
        // array 由来の indexed-loop call site には Length を提供する。
        internal int Length => Count;

        public IndexingFileTarget this[int index] => _targets[index];

        string IReadOnlyList<string>.this[int index] => _targets[index].FilePath;

        internal void Add(IndexingFileTarget target) => _targets.Add(target);

        public List<IndexingFileTarget>.Enumerator GetEnumerator() => _targets.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => ((IEnumerable<string>)this).GetEnumerator();

        IEnumerator<string> IEnumerable<string>.GetEnumerator()
        {
            foreach (var target in _targets)
                yield return target.FilePath;
        }
    }

    public readonly record struct ScanFilesResult(
        IReadOnlyList<string> Files,
        IReadOnlyDictionary<string, string> FileLanguages,
        IReadOnlyList<ScanError> Errors,
        IReadOnlyList<string> NonIndexablePaths,
        IReadOnlyList<string> UnknownExtensionFiles,
        IReadOnlyList<string> ProbeFailedFilePaths,
        IReadOnlyList<string> ListedDirectories,
        IReadOnlyList<string> FullyScannedDirectories,
        IReadOnlySet<string> CheckpointedDirectories,
        IReadOnlyList<string> AncestorIgnoreDirectories,
        IReadOnlyList<string> AttributePrunedDirectories,
        IReadOnlyList<string> NestedRepositories,
        IReadOnlyList<string> DanglingSymlinks)
    {
        public IReadOnlyDictionary<string, int> LanguageCounts { get; init; } = EmptyLanguageCounts;

        internal IReadOnlyDictionary<string, ProjectMarkerFingerprintResult> ProjectMarkerFingerprints { get; init; } =
            new Dictionary<string, ProjectMarkerFingerprintResult>(StringComparer.Ordinal);

        public bool HadErrors
        {
            get
            {
                foreach (var error in Errors)
                {
                    if (error.IsFatal)
                        return true;
                }

                return false;
            }
        }
    }

    internal readonly record struct ScanFilesWithDirectoryListingSnapshotsResult(
        ScanFilesResult ScanResult,
        ScanInputSnapshot InputSnapshot,
        IndexingFileTargetCollection? IndexingTargets = null);

    internal readonly record struct ScanFilesWithIndexingTargetsResult(
        ScanFilesResult ScanResult,
        IndexingFileTargetCollection IndexingTargets);

    internal sealed record ScanInputSnapshot(
        IReadOnlyList<DirectoryListingSnapshot> DirectoryListings,
        IReadOnlyList<ConfigurationInputSnapshot> ConfigurationInputs,
        bool IsComplete,
        string? IncompletePath,
        string? IncompleteReason,
        long ConfigurationGeneration);

    internal readonly record struct DirectoryListingSnapshot(
        string Path,
        DateTime ModifiedUtc);

    internal enum ConfigurationInputKind
    {
        MissingFile,
        MissingDirectory,
        File,
        Directory,
        MarkerFile,
        MarkerDirectory,
        RejectedOversizeFile,
    }

    internal sealed record ConfigurationInputSnapshot(
        string Path,
        ConfigurationInputKind Kind,
        long Length,
        DateTime ModifiedUtc,
        FileIdentity? Identity,
        byte[]? ContentHash);

    internal enum PathFilterKind
    {
        None,
        IgnoredByRules,
        ExcludedByDefaultDirectory,
        ExcludedByDefaultFile,
        OutsideProjectRoot,
        SymlinkDisallowed,
        IgnoreRulesUnavailable,
    }

    internal readonly record struct PathFilterResult(
        PathFilterKind FilterKind,
        IReadOnlyList<ScanError> Errors)
    {
        public bool ShouldSkip => FilterKind != PathFilterKind.None;
        public bool ShouldDeleteExisting => FilterKind is
            PathFilterKind.IgnoredByRules or
            PathFilterKind.ExcludedByDefaultDirectory or
            PathFilterKind.ExcludedByDefaultFile or
            PathFilterKind.OutsideProjectRoot or
            PathFilterKind.SymlinkDisallowed;
    }

    internal readonly record struct ProjectMarkerFingerprintBudget(
        int MaxDirectories,
        int MaxMarkerFiles);

    private sealed class ProjectMarkerFingerprintTraversalState(
        string language,
        IReadOnlyList<string> patterns,
        int maxDirectories,
        int maxMarkerFiles)
    {
        public string Language { get; } = language;
        public IReadOnlyList<string> Patterns { get; } = patterns;
        public int MaxDirectories { get; } = maxDirectories;
        public int MaxMarkerFiles { get; } = maxMarkerFiles;
        public List<string> ProjectMarkers { get; } = [];
        public List<ScanError> Errors { get; } = [];
        public int DirectoriesVisited { get; set; }
        public int PendingDirectories { get; set; }
        public int MarkerFilesCollected { get; set; }
        public bool TraversalStopped { get; set; }
        public bool Truncated { get; set; }
        public string TruncationReason { get; set; } = "unknown";
    }

    private readonly record struct ProjectMarkerFingerprintDirectory(
        string Path,
        string RelativePath,
        IgnoreRuleSet IgnoreRules,
        bool IsProjectRoot,
        int LanguageMask);

    private sealed class ProjectMarkerScopeCollectionState
    {
        public Dictionary<string, ProjectMarkerDirectoryCounts> Directories { get; } =
            new(StringComparer.Ordinal);
        public bool IsComplete { get; set; } = true;
    }

    private sealed class ProjectMarkerScopeNode(bool ignoreCase)
    {
        private readonly StringComparison _childNameComparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        private Dictionary<int, List<ProjectMarkerScopeChild>>? _children;

        public ProjectMarkerDirectoryCounts Counts { get; set; }

        public bool TryGetChild(ReadOnlySpan<char> name, out ProjectMarkerScopeNode child)
        {
            var hashCode = string.GetHashCode(name, _childNameComparison);
            if (_children != null && _children.TryGetValue(hashCode, out var candidates))
            {
                foreach (var candidate in candidates)
                {
                    if (name.Equals(candidate.Name.AsSpan(), _childNameComparison))
                    {
                        child = candidate.Node;
                        return true;
                    }
                }
            }

            child = null!;
            return false;
        }

        public void AddChild(string name, ProjectMarkerScopeNode child)
        {
            var hashCode = string.GetHashCode(name.AsSpan(), _childNameComparison);
            _children ??= [];
            if (!_children.TryGetValue(hashCode, out var candidates))
            {
                candidates = [];
                _children.Add(hashCode, candidates);
            }

            candidates.Add(new ProjectMarkerScopeChild(name, child));
        }
    }

    private readonly record struct ProjectMarkerScopeChild(
        string Name,
        ProjectMarkerScopeNode Node);

    private sealed record ProjectMarkerScopeSnapshot(ProjectMarkerScopeNode Root);

    private readonly record struct ProjectMarkerDirectoryCounts(
        int CSharp,
        int VisualBasic,
        int FSharp,
        int MsbuildPrimary,
        int MsbuildAll);

    internal readonly record struct ProjectMarkerFingerprintResult(string? Fingerprint, bool IsComplete)
    {
        public IReadOnlyList<ScanError> Warnings { get; init; } = [];
    }
}
