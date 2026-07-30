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
    internal const string ShebangDetectionSource = "shebang";
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
        ScanInputSnapshot InputSnapshot);

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
            PathFilterKind.OutsideProjectRoot;
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

    internal readonly record struct ProjectMarkerFingerprintResult(string? Fingerprint, bool IsComplete)
    {
        public IReadOnlyList<ScanError> Warnings { get; init; } = [];
    }
}
