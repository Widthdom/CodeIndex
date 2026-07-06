using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Scans directories for source files and builds FileRecords.
/// ディレクトリを走査してソースファイルからFileRecordを構築する。
/// </summary>
public partial class FileIndexer
{
    internal const int MaxDanglingFileSystemEntryScanCandidates = 4096;
    internal static Func<string, bool>? FileSystemIgnoreCaseProbeForTesting { get; set; }
    internal static Func<string, FileSystemInfo?>? ResolveDirectoryLinkTargetForTesting { get; set; }

    private static readonly string[] HotspotFamilyMarkerLanguages = ["csharp", "vb", "fsharp", "msbuild"];
    private static readonly IReadOnlyDictionary<string, int> EmptyLanguageCounts =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private const int MaxDirectoryTraversalDepth = 128;
    private const int GitLfsPointerMaxBytes = 1024;
    private const int MaxGitmodulesBytes = 256 * 1024;
    private const int MaxGitmodulesLines = 4096;
    private const int MaxGitmodulesLineChars = 16 * 1024;
    internal const int MaxGitmodulesSubmodulePaths = 1024;
    private const int MaxProjectMarkerTraversalWarnings = 32;
    private static readonly string[] IgnoreFileNames = [".gitignore", ".cdidxignore"];
    private const int MaxIgnoreFileBytes = 256 * 1024;
    private const int MaxIgnoreFileLines = 8192;
    private const int MaxIgnoreRulesPerFile = 4096;
    private const int MaxProjectMarkerFingerprintDirectories = 8192;
    private const int MaxProjectMarkerFingerprintFiles = 4096;
    private const int MaxIgnorePatternLength = 512;
    public const string MaxFileSizeEnvironmentVariable = "CDIDX_MAX_FILE_BYTES";
    // Default maximum file size to index (4 MiB). Larger generated/vendor payloads
    // can still be opted in with --max-file-bytes, but the default path should not
    // allocate a single multi-megabyte byte[] for common source scans.
    // インデックス対象の既定最大ファイルサイズ (4 MiB)。生成物や vendor の大容量 payload は
    // --max-file-bytes で明示的に opt-in できるが、既定経路では一般的な source scan で
    // multi-MB の単一 byte[] を確保しない。
    public const long DefaultMaxFileSizeBytes = 4 * 1024 * 1024;
    private readonly string _projectRoot;
    private readonly string _projectRootRelativePrefix;
    private readonly string _ignoreRuleRoot;
    private readonly IReadOnlyList<string> _ancestorIgnoreDirectories;
    private readonly bool _ignoreCase;
    private readonly Func<string, bool?> _directoryIgnoreCaseProbe;
    private readonly Func<string, IEnumerable<string>>? _enumerateFilesForTesting;
    private readonly Func<string, IEnumerable<string>> _enumerateFileSystemEntries;
    private readonly Dictionary<string, bool> _directoryIgnoreCaseCache;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _languageMapOverrideCache;
    private readonly Dictionary<string, bool> _nestedGitRepositoryCache;
    private LanguageMapOverrideLookupCache? _lastLanguageMapOverrideLookup;
    private readonly long _maxFileSizeBytes;
    private readonly FileContentLoader _contentLoader;
    private readonly SymlinkPolicy _symlinkPolicy;
    private readonly int _maxDanglingFileSystemEntryScanCandidates;
    private readonly GeneratedCodePatternMatcher _generatedCodePatterns;
    // Submodule working-tree paths declared in <ignoreRuleRoot>/.gitmodules, relative to
    // _projectRoot and slash-normalized. Used to override SkipDirs so that submodules
    // hosted under SkipDirs-named directories (e.g. vendor/foo) remain visible to the
    // indexer. Empty when .gitmodules is missing or unreadable.
    // <ignoreRuleRoot>/.gitmodules で宣言された submodule のワークツリーパス（_projectRoot 相対、
    // スラッシュ正規化済み）。vendor/foo のように SkipDirs 名のディレクトリ配下にある submodule を
    // 可視化するため SkipDirs を上書きする。.gitmodules が無い・読めない場合は空。
    private readonly HashSet<string> _submodulePaths;
    // Ancestor path prefixes of every entry in _submodulePaths (exclusive of the submodule
    // itself). When such an ancestor matches SkipDirs we pass through it without indexing
    // its direct files, descending only into the submodule branch.
    // _submodulePaths 各要素の祖先パス（submodule 自身は含まない）。SkipDirs 名と一致した場合は
    // 通過モードとしてその直下ファイルを索引せず、submodule 方向のみ降りる。
    private readonly HashSet<string> _submoduleAncestorPaths;
    private readonly IReadOnlyList<ScanError> _submoduleLoadWarnings;

    internal static Func<string, IEnumerable<string>>? EnumerateProjectMarkerDirectoriesForTesting { get; set; }
    internal static Func<string, IReadOnlyList<string>>? ReadGitmodulesLinesForTesting { get; set; }

    private static readonly IReadOnlySet<string> EmptyCheckpointedDirectorySet = new HashSet<string>(StringComparer.Ordinal);

    private sealed class DirectoryScanState
    {
        public DirectoryScanState(
            List<string> results,
            Dictionary<string, string> fileLanguages,
            Dictionary<string, int> languageCounts,
            List<ScanError> errors,
            HashSet<string> listedDirectories,
            HashSet<string> fullyScannedDirectories,
            IReadOnlySet<string> checkpointedDirectories,
            HashSet<FileIdentity> visitedFileIdentities,
            HashSet<string> visitedDirectories)
        {
            Results = results;
            FileLanguages = fileLanguages;
            LanguageCounts = languageCounts;
            Errors = errors;
            ListedDirectories = listedDirectories;
            FullyScannedDirectories = fullyScannedDirectories;
            CheckpointedDirectories = checkpointedDirectories;
            VisitedFileIdentities = visitedFileIdentities;
            VisitedDirectories = visitedDirectories;
        }

        public List<string> Results { get; }
        public Dictionary<string, string> FileLanguages { get; }
        public Dictionary<string, int> LanguageCounts { get; }
        public List<ScanError> Errors { get; }
        public HashSet<string>? NonIndexablePaths { get; private set; }
        public HashSet<string>? UnknownExtensionFiles { get; private set; }
        public HashSet<string>? ProbeFailedFilePaths { get; private set; }
        public HashSet<string> ListedDirectories { get; }
        public HashSet<string> FullyScannedDirectories { get; }
        public IReadOnlySet<string> CheckpointedDirectories { get; }
        public HashSet<string>? AttributePrunedDirectories { get; private set; }
        public HashSet<string>? NestedRepositories { get; private set; }
        public HashSet<string>? DanglingSymlinks { get; private set; }
        public HashSet<FileIdentity> VisitedFileIdentities { get; }
        public HashSet<string> VisitedDirectories { get; }

        public void RecordNonIndexablePath(string path)
            => (NonIndexablePaths ??= CreatePathSet()).Add(path);

        public void RecordUnknownExtensionFile(string path)
            => (UnknownExtensionFiles ??= CreatePathSet()).Add(path);

        public void RecordProbeFailedFilePath(string path)
            => (ProbeFailedFilePaths ??= CreatePathSet()).Add(path);

        public void RecordAttributePrunedDirectory(string path)
            => (AttributePrunedDirectories ??= CreatePathSet()).Add(path);

        public void RecordNestedRepository(string path)
            => (NestedRepositories ??= CreatePathSet()).Add(path);

        public void RecordDanglingSymlink(string path)
            => (DanglingSymlinks ??= CreatePathSet()).Add(path);

        private static HashSet<string> CreatePathSet()
            => new(StringComparer.Ordinal);
    }

    private sealed record LanguageMapOverrideLookupCache(
        string StartDirectory,
        IReadOnlyDictionary<string, string> Overrides);

    public FileIndexer(string projectRoot)
        : this(projectRoot, ignoreCase: ProbeFileSystemIgnoreCase(projectRoot), ignoreRuleRoot: null)
    {
    }

    public FileIndexer(string projectRoot, bool ignoreCase)
        : this(projectRoot, ignoreCase, ignoreRuleRoot: null)
    {
    }

    public FileIndexer(
        string projectRoot,
        bool ignoreCase,
        string? ignoreRuleRoot,
        long? maxFileSizeBytes = null,
        IReadOnlyList<string>? generatedCodePatterns = null)
        : this(projectRoot, ignoreCase, ignoreRuleRoot, maxFileSizeBytes, directoryIgnoreCaseProbe: null, generatedCodePatterns: generatedCodePatterns)
    {
    }

    internal FileIndexer(
        string projectRoot,
        bool ignoreCase,
        string? ignoreRuleRoot,
        long? maxFileSizeBytes,
        Func<string, bool?>? directoryIgnoreCaseProbe,
        Func<string, IEnumerable<string>>? enumerateFiles = null,
        Func<string, IEnumerable<string>>? enumerateFileSystemEntries = null,
        SymlinkPolicy symlinkPolicy = SymlinkPolicy.None,
        int? maxDanglingFileSystemEntryScanCandidates = null,
        IReadOnlyList<string>? generatedCodePatterns = null)
    {
        _projectRoot = Path.GetFullPath(projectRoot);
        _projectRootRelativePrefix = CreateProjectRootRelativePrefix(_projectRoot);
        _ignoreRuleRoot = NormalizeIgnoreRuleRoot(ignoreRuleRoot);
        _ancestorIgnoreDirectories = BuildAncestorIgnoreDirectories(_ignoreRuleRoot, _projectRoot);
        _ignoreCase = ignoreCase;
        _directoryIgnoreCaseProbe = directoryIgnoreCaseProbe ?? ProbeExistingDirectoryIgnoreCase;
        _enumerateFilesForTesting = enumerateFiles;
        _enumerateFileSystemEntries = enumerateFileSystemEntries ?? (dir => CodeIndex.FileSystemTraversalPolicy.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)));
        _directoryIgnoreCaseCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        _languageMapOverrideCache = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        _nestedGitRepositoryCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        _maxFileSizeBytes = ResolveMaxFileSizeBytes(maxFileSizeBytes);
        _contentLoader = new FileContentLoader(_maxFileSizeBytes);
        _symlinkPolicy = symlinkPolicy;
        _maxDanglingFileSystemEntryScanCandidates = Math.Max(
            1,
            maxDanglingFileSystemEntryScanCandidates ?? MaxDanglingFileSystemEntryScanCandidates);
        _generatedCodePatterns = GeneratedCodePatternMatcher.FromPatterns(generatedCodePatterns, ignoreCase);
        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(_projectRoot);
        var pathComparer = _ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        (_submodulePaths, _submoduleAncestorPaths, _submoduleLoadWarnings) = LoadGitSubmodulePaths(_ignoreRuleRoot, _projectRoot, pathComparer);
    }

}
