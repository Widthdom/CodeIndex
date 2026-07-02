using System.Security.Cryptography;
using System.Text;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    public string GetFamilyScopeKey(string absolutePath, string? lang)
    {
        var fullPath = GetProjectMarkerScopeFullPath(absolutePath);
        var projectMarkerPatterns = GetProjectMarkerPatterns(lang);
        if (projectMarkerPatterns != null)
        {
            var primaryProjectMarkerPatterns = GetPrimaryProjectMarkerPatterns(lang) ?? projectMarkerPatterns;
            var primaryPatternsCoverAllMarkers = ProjectMarkerPatternListsEqual(primaryProjectMarkerPatterns, projectMarkerPatterns);
            var currentDir = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(currentDir))
            {
                var markerCount = CountProjectMarkerFiles(currentDir, primaryProjectMarkerPatterns);
                if (markerCount == 1)
                    return NormalizeScopeKey(ToRelativePath(currentDir));
                if (markerCount > 1)
                    return DeriveAmbiguousProjectScopeKey(fullPath, currentDir);
                if (!primaryPatternsCoverAllMarkers && CountProjectMarkerFiles(currentDir, projectMarkerPatterns) > 0)
                    return NormalizeScopeKey(ToRelativePath(currentDir));

                if (PathsEqual(currentDir, _projectRoot))
                    break;

                currentDir = Path.GetDirectoryName(currentDir);
            }
        }

        var relativePath = ToRelativePath(fullPath);
        return DeriveFallbackFamilyScopeKey(relativePath);
    }

    public static IReadOnlyList<string> GetHotspotFamilyMarkerLanguages() => HotspotFamilyMarkerLanguages;

    public static bool SupportsHotspotFamilyMarkerLanguage(string? lang) =>
        GetProjectMarkerPatterns(lang) != null;

    public string? GetProjectMarkerFingerprint(string? lang, CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, cancellationToken).Fingerprint;

    internal ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResult(
        string? lang,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, MaxProjectMarkerFingerprintDirectories, MaxProjectMarkerFingerprintFiles, cancellationToken);

    internal string? GetProjectMarkerFingerprintForTesting(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, maxDirectories, maxMarkerFiles, cancellationToken).Fingerprint;

    internal ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResultForTesting(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResult(lang, maxDirectories, maxMarkerFiles, cancellationToken);

    private ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResult(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectMarkerPatterns = GetProjectMarkerPatterns(lang);
        if (projectMarkerPatterns == null)
            return new ProjectMarkerFingerprintResult(null, IsComplete: true);

        var projectMarkers = new List<string>();
        var traversalState = new ProjectMarkerFingerprintTraversalState();
        var errors = new List<ScanError>();
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(errors, ref fullyScanned);
        if (preloadResult.IgnoreRulesAvailable)
        {
            CollectProjectMarkerFiles(
                _projectRoot,
                preloadResult.Rules,
                projectMarkerPatterns,
                projectMarkers,
                Math.Max(1, maxDirectories),
                Math.Max(1, maxMarkerFiles),
                traversalState,
                errors,
                cancellationToken);
        }
        else
        {
            traversalState.Truncated = true;
        }

        if (traversalState.Truncated)
        {
            projectMarkers.Add(
                $"__cdidx_project_marker_fingerprint_truncated__:reason={traversalState.TruncationReason};directories={traversalState.DirectoriesVisited};markers={traversalState.MarkerFilesCollected}");
        }

        projectMarkers.Sort(StringComparer.Ordinal);

        var payload = string.Join('\n', projectMarkers);
        return new ProjectMarkerFingerprintResult(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            !traversalState.Truncated)
        {
            Warnings = GetNonFatalScanErrors(errors),
        };
    }

    private static ScanError[] GetNonFatalScanErrors(List<ScanError> errors)
    {
        var warningCount = 0;
        foreach (var error in errors)
        {
            if (!error.IsFatal)
                warningCount++;
        }

        if (warningCount == 0)
            return [];
        if (warningCount == errors.Count)
            return errors.ToArray();

        var warnings = new ScanError[warningCount];
        var index = 0;
        foreach (var error in errors)
        {
            if (!error.IsFatal)
                warnings[index++] = error;
        }

        return warnings;
    }

    public static string DeriveFallbackFamilyScopeKey(string relativePath)
    {
        var normalized = NormalizeScopeKey(relativePath);
        if (normalized == ".")
            return ".";

        var firstSeparator = normalized.IndexOf('/');
        if (firstSeparator < 0)
            return ".";

        return normalized[..firstSeparator];
    }

    private static string NormalizeScopeKey(string relativePath)
    {
        var start = 0;
        var end = relativePath.Length;
        while (start < end && IsScopeKeySeparator(relativePath[start]))
            start++;
        while (end > start && IsScopeKeySeparator(relativePath[end - 1]))
            end--;

        if (start == end)
            return ".";

        var span = relativePath.AsSpan(start, end - start);
        if (span.Length == 1 && span[0] == '.')
            return ".";

        if (span.IndexOf('\\') >= 0)
            return span.ToString().Replace('\\', '/');

        return start == 0 && end == relativePath.Length
            ? relativePath
            : relativePath[start..end];
    }

    private static bool IsScopeKeySeparator(char value) => value is '/' or '\\';

    private string DeriveAmbiguousProjectScopeKey(string absolutePath, string anchorDir)
    {
        var anchorScope = NormalizeScopeKey(ToRelativePath(anchorDir));
        var relativeFromAnchor = NormalizeScopeKey(GetRelativePathFromDirectory(anchorDir, absolutePath));
        if (relativeFromAnchor == ".")
            return anchorScope;

        var firstSeparator = relativeFromAnchor.IndexOf('/');
        if (firstSeparator < 0)
            return JoinScope(anchorScope, $"__file__/{relativeFromAnchor}");

        return JoinScope(anchorScope, relativeFromAnchor[..firstSeparator]);
    }

    private static string JoinScope(string left, string right)
    {
        if (left == ".")
            return right;

        return $"{left}/{right}";
    }

    private static string GetProjectMarkerScopeFullPath(string path) =>
        Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(path);

    private static bool ProjectMarkerPatternListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private int CountProjectMarkerFiles(string dir, IReadOnlyList<string> patterns)
    {
        var count = 0;
        var prefixedDir = LongPath.EnsureWindowsPrefix(dir);
        foreach (var pattern in patterns)
        {
            foreach (var markerFile in CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(prefixedDir, pattern))
            {
                if (!IsProjectMarkerVisible(LongPath.RemoveWindowsPrefix(markerFile), activeIgnoreRules: null))
                    continue;

                count++;
                if (count > 1)
                    return count;
            }
        }

        return count;
    }

    private bool IsProjectMarkerVisible(string markerFile, IgnoreRuleSet? activeIgnoreRules)
    {
        if (HasSkippedAttributes(markerFile))
            return false;

        return activeIgnoreRules is null
            ? !EvaluatePathFilter(markerFile).ShouldSkip
            : !activeIgnoreRules.IsIgnored(markerFile, isDirectory: false);
    }

    private void CollectProjectMarkerFiles(
        string dir,
        IgnoreRuleSet inheritedIgnoreRules,
        IReadOnlyList<string> patterns,
        List<string> projectMarkers,
        int maxDirectories,
        int maxMarkerFiles,
        ProjectMarkerFingerprintTraversalState traversalState,
        List<ScanError> errors,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<ProjectMarkerFingerprintDirectory>();
        pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(
            dir,
            ToRelativePath(dir),
            inheritedIgnoreRules,
            IsProjectRoot: true));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();
            if (GetDirectoryFilterKind(current.Path, current.RelativePath, current.IgnoreRules, current.IsProjectRoot) != PathFilterKind.None)
                continue;

            if (traversalState.DirectoriesVisited >= maxDirectories)
            {
                TruncateProjectMarkerTraversal(
                    traversalState,
                    errors,
                    current.Path,
                    $"directory budget {maxDirectories:N0} exhausted after visiting {traversalState.DirectoriesVisited:N0} directories");
                return;
            }

            var currentDirectory = current.Path;
            traversalState.DirectoriesVisited++;
            try
            {
                var fullyScanned = true;
                var loadResult = LoadIgnoreRulesForDirectory(currentDirectory, current.IgnoreRules, errors, ref fullyScanned);
                if (!loadResult.IgnoreRulesAvailable)
                {
                    TruncateProjectMarkerTraversal(
                        traversalState,
                        errors,
                        currentDirectory,
                        "ignore-rule loading failed");
                    return;
                }

                var activeIgnoreRules = loadResult.Rules;
                var prefixedCurrentDirectory = LongPath.EnsureWindowsPrefix(currentDirectory);
                foreach (var pattern in patterns)
                {
                    foreach (var enumeratedMarkerFile in CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(prefixedCurrentDirectory, pattern))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var markerFile = LongPath.RemoveWindowsPrefix(enumeratedMarkerFile);
                        if (!IsProjectMarkerVisible(markerFile, activeIgnoreRules))
                            continue;

                        if (traversalState.MarkerFilesCollected >= maxMarkerFiles)
                        {
                            TruncateProjectMarkerTraversal(
                                traversalState,
                                errors,
                                currentDirectory,
                                $"marker file budget {maxMarkerFiles:N0} exhausted after collecting {traversalState.MarkerFilesCollected:N0} marker files");
                            return;
                        }

                        projectMarkers.Add(NormalizeScopeKey(ToRelativePath(markerFile)));
                        traversalState.MarkerFilesCollected++;
                    }
                }

                var passthrough = IsSubmoduleAncestorPassthrough(current.RelativePath);
                foreach (var enumeratedSubDir in EnumerateProjectMarkerDirectories(currentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var subDir = LongPath.RemoveWindowsPrefix(enumeratedSubDir);
                    if (HasSkippedAttributes(subDir))
                        continue;
                    var subRelativePath = ToRelativePath(subDir);
                    if (IsNestedGitRepository(subDir) && !IsSubmoduleOrAncestor(subRelativePath))
                        continue;
                    if (passthrough && !IsSubmoduleOrAncestor(subRelativePath))
                        continue;
                    if (GetDirectoryFilterKind(subDir, subRelativePath, activeIgnoreRules) != PathFilterKind.None)
                        continue;

                    if (traversalState.DirectoriesVisited + pendingDirectories.Count >= maxDirectories)
                    {
                        TruncateProjectMarkerTraversal(
                            traversalState,
                            errors,
                            currentDirectory,
                            $"directory budget {maxDirectories:N0} would be exceeded while queuing subdirectories after visiting {traversalState.DirectoriesVisited:N0} directories");
                        return;
                    }

                    pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(
                        subDir,
                        subRelativePath,
                        activeIgnoreRules,
                        IsProjectRoot: false));
                }
            }
            catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
            {
                var exceptionType = FileSystemTraversalFailure.ExceptionTypeName(ex);
                AddProjectMarkerTraversalWarning(errors, currentDirectory, exceptionType);
                MarkProjectMarkerTraversalTruncated(
                    traversalState,
                    $"traversal failed with {exceptionType}");
            }
        }
    }

    private void TruncateProjectMarkerTraversal(
        ProjectMarkerFingerprintTraversalState traversalState,
        List<ScanError> errors,
        string directory,
        string reason)
    {
        MarkProjectMarkerTraversalTruncated(traversalState, reason);
        AddProjectMarkerBudgetWarning(errors, directory, reason);
    }

    private static void MarkProjectMarkerTraversalTruncated(
        ProjectMarkerFingerprintTraversalState traversalState,
        string reason)
    {
        if (!traversalState.Truncated)
            traversalState.TruncationReason = reason;
        traversalState.Truncated = true;
    }

    private static IEnumerable<string> EnumerateProjectMarkerDirectories(string dir)
        => EnumerateProjectMarkerDirectoriesForTesting is { } enumerate
            ? enumerate(dir)
            : CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(LongPath.EnsureWindowsPrefix(dir));

    private void AddProjectMarkerTraversalWarning(List<ScanError> errors, string directory, string exceptionType)
    {
        if (errors.Count(static error => error.Message.StartsWith("Project marker discovery skipped", StringComparison.Ordinal))
            >= MaxProjectMarkerTraversalWarnings)
        {
            return;
        }

        var relativePath = NormalizeIgnorePath(ToRelativePath(directory));
        if (string.IsNullOrEmpty(relativePath))
            relativePath = ".";

        errors.Add(new ScanError(
            relativePath,
            $"Project marker discovery skipped this subtree because it could not be traversed ({exceptionType}).",
            ScanIssueSeverity.Warning));
    }

    private void AddProjectMarkerBudgetWarning(List<ScanError> errors, string directory, string reason)
    {
        if (errors.Count(static error => error.Message.StartsWith("Project marker discovery truncated", StringComparison.Ordinal))
            >= MaxProjectMarkerTraversalWarnings)
        {
            return;
        }

        var relativePath = NormalizeIgnorePath(ToRelativePath(directory));
        if (string.IsNullOrEmpty(relativePath))
            relativePath = ".";

        errors.Add(new ScanError(
            relativePath,
            $"Project marker discovery truncated because {reason}.",
            ScanIssueSeverity.Warning));
    }

    private static readonly string[] CSharpProjectMarkerPatterns = ["*.csproj"];
    private static readonly string[] VisualBasicProjectMarkerPatterns = ["*.vbproj"];
    private static readonly string[] FSharpProjectMarkerPatterns = ["*.fsproj"];
    private static readonly string[] MsbuildProjectMarkerPatterns = ["*.csproj", "*.fsproj", "*.vbproj", "*.props", "*.targets"];
    private static readonly string[] MsbuildPrimaryProjectMarkerPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    private static IReadOnlyList<string>? GetProjectMarkerPatterns(string? lang) => lang switch
    {
        "csharp" => CSharpProjectMarkerPatterns,
        "vb" => VisualBasicProjectMarkerPatterns,
        "fsharp" => FSharpProjectMarkerPatterns,
        "msbuild" => MsbuildProjectMarkerPatterns,
        _ => null,
    };

    private static IReadOnlyList<string>? GetPrimaryProjectMarkerPatterns(string? lang) => lang switch
    {
        "csharp" => CSharpProjectMarkerPatterns,
        "vb" => VisualBasicProjectMarkerPatterns,
        "fsharp" => FSharpProjectMarkerPatterns,
        "msbuild" => MsbuildPrimaryProjectMarkerPatterns,
        _ => null,
    };
}
