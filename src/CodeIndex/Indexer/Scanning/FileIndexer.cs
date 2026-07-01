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
    private readonly string _ignoreRuleRoot;
    private readonly IReadOnlyList<string> _ancestorIgnoreDirectories;
    private readonly bool _ignoreCase;
    private readonly Func<string, bool?> _directoryIgnoreCaseProbe;
    private readonly Func<string, IEnumerable<string>>? _enumerateFilesForTesting;
    private readonly Func<string, IEnumerable<string>> _enumerateFileSystemEntries;
    private readonly Dictionary<string, bool> _directoryIgnoreCaseCache;
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

    private sealed record DirectoryScanState(
        List<string> Results,
        Dictionary<string, string> FileLanguages,
        List<ScanError> Errors,
        HashSet<string> NonIndexablePaths,
        HashSet<string> UnknownExtensionFiles,
        HashSet<string> ProbeFailedFilePaths,
        HashSet<string> ListedDirectories,
        HashSet<string> FullyScannedDirectories,
        HashSet<string> CheckpointedDirectories,
        HashSet<string> AttributePrunedDirectories,
        HashSet<string> NestedRepositories,
        HashSet<string> DanglingSymlinks,
        HashSet<FileIdentity> VisitedFileIdentities,
        HashSet<string> VisitedDirectories);

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
        _ignoreRuleRoot = NormalizeIgnoreRuleRoot(ignoreRuleRoot);
        _ancestorIgnoreDirectories = BuildAncestorIgnoreDirectories(_ignoreRuleRoot, _projectRoot);
        _ignoreCase = ignoreCase;
        _directoryIgnoreCaseProbe = directoryIgnoreCaseProbe ?? ProbeExistingDirectoryIgnoreCase;
        _enumerateFilesForTesting = enumerateFiles;
        _enumerateFileSystemEntries = enumerateFileSystemEntries ?? (dir => CodeIndex.FileSystemTraversalPolicy.EnumerateFileSystemEntries(LongPath.EnsureWindowsPrefix(dir)));
        _directoryIgnoreCaseCache = new Dictionary<string, bool>(StringComparer.Ordinal);
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

    internal static bool CanIndexFile(string filePath)
        => GetFileIndexability(filePath) == FileProbeStatus.Supported;

    internal static bool IsWindowsDevicePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var path = filePath.AsSpan();
        if (StartsWithWindowsDeviceNamespace(path))
        {
            return true;
        }

        for (var start = 0; start < path.Length;)
        {
            while (start < path.Length && IsWindowsPathSeparator(path[start]))
                start++;
            if (start >= path.Length)
                break;

            var end = start;
            while (end < path.Length && !IsWindowsPathSeparator(path[end]))
                end++;

            var name = path[start..end];
            var extensionIndex = name.IndexOf('.');
            if (extensionIndex >= 0)
                name = name[..extensionIndex];

            if (IsWindowsReservedDeviceName(name))
                return true;

            start = end + 1;
        }

        return false;
    }

    private static bool StartsWithWindowsDeviceNamespace(ReadOnlySpan<char> path)
    {
        if (path.Length >= 4
            && IsWindowsPathSeparator(path[0])
            && IsWindowsPathSeparator(path[1])
            && path[2] == '.'
            && IsWindowsPathSeparator(path[3]))
        {
            return true;
        }

        if (path.Length < 22
            || !IsWindowsPathSeparator(path[0])
            || !IsWindowsPathSeparator(path[1])
            || path[2] != '?'
            || !IsWindowsPathSeparator(path[3]))
        {
            return false;
        }

        var remaining = path[4..];
        if (!remaining.StartsWith("GLOBALROOT".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        remaining = remaining["GLOBALROOT".Length..];
        if (remaining.IsEmpty || !IsWindowsPathSeparator(remaining[0]))
            return false;

        remaining = remaining[1..];
        if (!remaining.StartsWith("Device".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        remaining = remaining["Device".Length..];
        return !remaining.IsEmpty && IsWindowsPathSeparator(remaining[0]);
    }

    private static bool IsWindowsPathSeparator(char value)
        => value is '\\' or '/';

    private static bool IsWindowsReservedDeviceName(ReadOnlySpan<char> name)
    {
        if (name.Equals("CON".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM".AsSpan(), StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT".AsSpan(), StringComparison.OrdinalIgnoreCase))
            && name[3] >= '1'
            && name[3] <= '9';
    }

    internal static bool HasSkippedAttributes(FileAttributes attributes, bool isWindows)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return true;

        return isWindows && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
    }

    private static bool HasSkippedAttributes(FileAttributes attributes)
        => HasSkippedAttributes(attributes, OperatingSystem.IsWindows());

    // Detect symbolic links / reparse points and Windows Hidden/System paths so the scanner can skip them.
    // Treats probe failures (e.g. dangling symlinks whose target is gone) as skipped attributes
    // so the scanner skips them instead of trying to read the missing target.
    // symlink / reparse point と Windows の Hidden/System 属性を検出し、スキャナでスキップできるようにする。
    // プローブ失敗（例: target が消えた dangling symlink）は missing target を読もうとせずスキップするため、
    // skip 対象属性扱いにする。
    private static bool HasSkippedAttributes(string path)
    {
        return FileSystemBoundary.TryGetAttributes(path, out var attributes) switch
        {
            FileSystemBoundaryProbeStatus.Found => HasSkippedAttributes(attributes),
            FileSystemBoundaryProbeStatus.Missing => true,
            _ => false,
        };
    }

    private static bool IsReparsePoint(string path)
    {
        return FileSystemBoundary.TryGetAttributes(path, out var attributes) == FileSystemBoundaryProbeStatus.Found
            && FileSystemBoundary.IsSymlinkOrReparsePoint(attributes);
    }

    private static FileProbeStatus ToFileProbeStatus(FileSystemBoundaryProbeStatus status)
        => status switch
        {
            FileSystemBoundaryProbeStatus.Missing => FileProbeStatus.Missing,
            FileSystemBoundaryProbeStatus.PermissionDenied or FileSystemBoundaryProbeStatus.IoError => OperatingSystem.IsWindows()
                ? FileProbeStatus.Supported
                : FileProbeStatus.ProbeFailed,
            _ => FileProbeStatus.ProbeFailed,
        };

    private bool ShouldSkipDirectoryLink(string subDir, List<ScanError> errors, HashSet<string> danglingSymlinks)
    {
        if (!IsReparsePoint(subDir))
            return HasSkippedAttributes(subDir);

        var relative = ToRelativePath(subDir);
        DirectoryInfo info = new(LongPath.EnsureWindowsPrefix(subDir));
        FileSystemInfo? target;
        try
        {
            target = ResolveDirectoryLinkTargetForTesting != null
                ? ResolveDirectoryLinkTargetForTesting(subDir)
                : info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            target = null;
        }
        catch (DirectoryNotFoundException)
        {
            target = null;
        }
        catch (IOException)
        {
            target = null;
        }
        catch (UnauthorizedAccessException)
        {
            errors.Add(new ScanError(
                relative,
                "Skipped symlinked directory because its target could not be resolved due to permissions.",
                ScanIssueSeverity.Warning));
            return true;
        }

        if (target?.FullName is not { Length: > 0 } targetPath || !Directory.Exists(LongPath.EnsureWindowsPrefix(targetPath)))
        {
            danglingSymlinks.Add(relative);
            errors.Add(new ScanError(relative, "Skipped dangling symlink because its target could not be resolved.", ScanIssueSeverity.Warning));
            return true;
        }

        if (_symlinkPolicy == SymlinkPolicy.All)
            return false;

        if (_symlinkPolicy == SymlinkPolicy.Internal && IsPathEqualOrParent(_projectRoot, targetPath))
            return false;

        errors.Add(new ScanError(
            relative,
            $"Skipped symlinked directory outside the active symlink policy: target {FormatSymlinkPolicyTargetForDiagnostic(targetPath)}",
            ScanIssueSeverity.Warning));
        return true;
    }

    private string FormatSymlinkPolicyTargetForDiagnostic(string targetPath)
    {
        if (!IsPathEqualOrParent(_projectRoot, targetPath))
            return "<outside project root>";

        var relative = NormalizePathSeparators(Path.GetRelativePath(_projectRoot, targetPath));
        return relative == "." ? "<project root>" : relative;
    }

    internal bool ShouldSkipDirectoryTraversal(string directory)
        => ShouldSkipDirectoryLink(
            directory,
            errors: new List<ScanError>(),
            danglingSymlinks: new HashSet<string>(StringComparer.Ordinal));

    private static string GetDirectoryTraversalIdentity(string directory)
    {
        if (!IsReparsePoint(directory))
            return directory;

        try
        {
            DirectoryInfo info = new(LongPath.EnsureWindowsPrefix(directory));
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target?.FullName is { Length: > 0 } targetPath)
                return targetPath;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return $"unresolved-reparse:{Path.GetFullPath(directory)}";
    }

    internal static FileProbeStatus GetFileIndexability(string filePath)
        => GetFileIndexability(filePath, SymlinkPolicy.None, projectRoot: null);

    internal FileProbeStatus GetFileIndexabilityForIndexing(string filePath)
        => GetFileIndexability(filePath, _symlinkPolicy, _projectRoot);

    internal static FileProbeStatus GetFileIndexability(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (OperatingSystem.IsWindows() && IsWindowsDevicePath(filePath))
            return FileProbeStatus.Unsupported;

        // File.GetAttributes uses lstat-like semantics on .NET (does not follow the symlink target),
        // which lets us apply the active symlink policy before the Unix stat() path follows the target.
        // Windows Hidden/System paths remain rejected to avoid indexing OS-owned caches during broad scans.
        // File.GetAttributes は .NET 上で lstat 相当（symlink target を辿らない）なので、
        // Unix の stat() が target を辿る前に symlink policy を適用できる。Windows では
        // broad scan で OS 管理 cache を索引しないよう Hidden/System も引き続き弾く。
        var probeStatus = FileSystemBoundary.TryGetAttributes(filePath, out var attributes);
        if (probeStatus != FileSystemBoundaryProbeStatus.Found)
            return ToFileProbeStatus(probeStatus);

        return GetFileIndexabilityForFoundAttributes(filePath, attributes, symlinkPolicy, projectRoot);
    }

    private static FileProbeStatus GetFileIndexabilityForFoundAttributes(
        string filePath,
        FileAttributes attributes,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes))
            return GetFileSymlinkIndexability(filePath, symlinkPolicy, projectRoot);

        if (HasSkippedAttributes(attributes))
            return FileProbeStatus.Unsupported;

        if (OperatingSystem.IsWindows())
            return FileProbeStatus.Supported;

        if (!UnixFileStatus.TryGetFileMode(filePath, out var mode))
            return FileProbeStatus.ProbeFailed;

        return (mode & UnixFileStatus.FileTypeMask) == UnixFileStatus.RegularFile
            ? FileProbeStatus.Supported
            : FileProbeStatus.Unsupported;
    }

    private static FileProbeStatus GetFileSymlinkIndexability(
        string filePath,
        SymlinkPolicy symlinkPolicy,
        string? projectRoot)
    {
        if (symlinkPolicy == SymlinkPolicy.None)
            return FileProbeStatus.Unsupported;

        FileSystemInfo? target;
        try
        {
            FileInfo info = new(LongPath.EnsureWindowsPrefix(filePath));
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (FileNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return FileProbeStatus.ProbeFailed;
        }
        catch (IOException)
        {
            return FileProbeStatus.ProbeFailed;
        }

        if (target?.FullName is not { Length: > 0 } targetPath)
            return FileProbeStatus.Unsupported;

        if (symlinkPolicy == SymlinkPolicy.Internal)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || !IsPathEqualOrParent(projectRoot, targetPath))
                return FileProbeStatus.Unsupported;
        }

        return GetFileIndexability(targetPath, SymlinkPolicy.None, projectRoot: null);
    }

}
