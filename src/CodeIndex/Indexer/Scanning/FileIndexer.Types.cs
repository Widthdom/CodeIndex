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

    internal enum LanguageDetectionSource
    {
        HeaderLexicalMarker,
        HeaderSampledLexicalMarker,
        HeaderLexicalFallback,
        HeaderSampledLexicalFallback,
        HeaderExtensionFallback,
    }

    internal readonly record struct LanguageDetectionResult(
        FileProbeStatus Status,
        string? Language,
        LanguageDetectionSource? Source = null,
        LanguageDetectionConfidence? Confidence = null);

    internal static string GetLanguageDetectionSourceCode(LanguageDetectionSource source) => source switch
    {
        LanguageDetectionSource.HeaderLexicalMarker => "header_lexical_marker",
        LanguageDetectionSource.HeaderSampledLexicalMarker => "header_sampled_lexical_marker",
        LanguageDetectionSource.HeaderLexicalFallback => "header_lexical_fallback",
        LanguageDetectionSource.HeaderSampledLexicalFallback => "header_sampled_lexical_fallback",
        LanguageDetectionSource.HeaderExtensionFallback => "header_extension_fallback",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

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

    private sealed class ProjectMarkerFingerprintTraversalState
    {
        public int DirectoriesVisited { get; set; }
        public int MarkerFilesCollected { get; set; }
        public bool Truncated { get; set; }
        public string TruncationReason { get; set; } = "unknown";
    }

    private readonly record struct ProjectMarkerFingerprintDirectory(
        string Path,
        string RelativePath,
        IgnoreRuleSet IgnoreRules,
        bool IsProjectRoot);

    internal readonly record struct ProjectMarkerFingerprintResult(string? Fingerprint, bool IsComplete)
    {
        public IReadOnlyList<ScanError> Warnings { get; init; } = [];
    }
}
