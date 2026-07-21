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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectMarkerPatterns = GetProjectMarkerPatterns(lang);
        if (projectMarkerPatterns == null)
            return new ProjectMarkerFingerprintResult(null, IsComplete: true);

        var language = lang!;
        var budgets = new Dictionary<string, ProjectMarkerFingerprintBudget>(1, StringComparer.Ordinal)
        {
            [language] = new ProjectMarkerFingerprintBudget(
                ProjectMarkerFingerprintDirectoryBudgetForTesting ?? MaxProjectMarkerFingerprintDirectories,
                MaxProjectMarkerFingerprintFiles),
        };
        return GetProjectMarkerFingerprintResults(budgets, cancellationToken)[language];
    }

    internal Dictionary<string, ProjectMarkerFingerprintResult> GetProjectMarkerFingerprintResults(
        CancellationToken cancellationToken = default)
    {
        var budgets = new Dictionary<string, ProjectMarkerFingerprintBudget>(
            HotspotFamilyMarkerLanguages.Length,
            StringComparer.Ordinal);
        foreach (var language in HotspotFamilyMarkerLanguages)
        {
            budgets.Add(
                language,
                new ProjectMarkerFingerprintBudget(
                    ProjectMarkerFingerprintDirectoryBudgetForTesting ?? MaxProjectMarkerFingerprintDirectories,
                    MaxProjectMarkerFingerprintFiles));
        }

        return GetProjectMarkerFingerprintResults(budgets, cancellationToken);
    }

    internal string? GetProjectMarkerFingerprintForTesting(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResultForTesting(lang, maxDirectories, maxMarkerFiles, cancellationToken).Fingerprint;

    internal ProjectMarkerFingerprintResult GetProjectMarkerFingerprintResultForTesting(
        string? lang,
        int maxDirectories,
        int maxMarkerFiles,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetProjectMarkerPatterns(lang) == null)
            return new ProjectMarkerFingerprintResult(null, IsComplete: true);

        var language = lang!;
        var budgets = new Dictionary<string, ProjectMarkerFingerprintBudget>(1, StringComparer.Ordinal)
        {
            [language] = new ProjectMarkerFingerprintBudget(maxDirectories, maxMarkerFiles),
        };
        return GetProjectMarkerFingerprintResults(budgets, cancellationToken)[language];
    }

    internal Dictionary<string, ProjectMarkerFingerprintResult> GetProjectMarkerFingerprintResultsForTesting(
        IReadOnlyDictionary<string, ProjectMarkerFingerprintBudget> budgets,
        CancellationToken cancellationToken = default) =>
        GetProjectMarkerFingerprintResults(budgets, cancellationToken);

    private Dictionary<string, ProjectMarkerFingerprintResult> GetProjectMarkerFingerprintResults(
        IReadOnlyDictionary<string, ProjectMarkerFingerprintBudget> budgets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var traversalStates = new List<ProjectMarkerFingerprintTraversalState>(budgets.Count);
        foreach (var language in HotspotFamilyMarkerLanguages)
        {
            if (!budgets.TryGetValue(language, out var budget))
                continue;

            traversalStates.Add(new ProjectMarkerFingerprintTraversalState(
                language,
                GetProjectMarkerPatterns(language)!,
                Math.Max(1, budget.MaxDirectories),
                Math.Max(1, budget.MaxMarkerFiles)));
        }

        var results = new Dictionary<string, ProjectMarkerFingerprintResult>(
            traversalStates.Count,
            StringComparer.Ordinal);
        if (traversalStates.Count == 0)
            return results;

        var preloadErrors = new List<ScanError>();
        var fullyScanned = true;
        var preloadResult = LoadAncestorIgnoreRules(preloadErrors, ref fullyScanned);
        CopyProjectMarkerErrors(preloadErrors, traversalStates, (1 << traversalStates.Count) - 1);
        if (preloadResult.IgnoreRulesAvailable)
        {
            CollectProjectMarkerFiles(
                _projectRoot,
                preloadResult.Rules,
                traversalStates,
                cancellationToken);
        }
        else
        {
            foreach (var traversalState in traversalStates)
            {
                traversalState.Truncated = true;
                traversalState.TraversalStopped = true;
            }
        }

        foreach (var traversalState in traversalStates)
        {
            if (traversalState.Truncated)
            {
                traversalState.ProjectMarkers.Add(
                    $"__cdidx_project_marker_fingerprint_truncated__:reason={traversalState.TruncationReason};directories={traversalState.DirectoriesVisited};markers={traversalState.MarkerFilesCollected}");
            }

            traversalState.ProjectMarkers.Sort(StringComparer.Ordinal);

            results.Add(
                traversalState.Language,
                new ProjectMarkerFingerprintResult(
                    ComputeProjectMarkerFingerprint(traversalState.ProjectMarkers),
                    !traversalState.Truncated)
                {
                    Warnings = GetNonFatalScanErrors(traversalState.Errors),
                });
        }

        return results;
    }

    private static string ComputeProjectMarkerFingerprint(IReadOnlyList<string> projectMarkers)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> separator = stackalloc byte[1];
        separator[0] = (byte)'\n';
        for (var i = 0; i < projectMarkers.Count; i++)
        {
            if (i > 0)
                hasher.AppendData(separator);

            AppendUtf8StringToHash(hasher, projectMarkers[i]);
        }

        var hash = hasher.GetHashAndReset();
        if (hash.Length != SHA256.HashSizeInBytes)
            throw new InvalidOperationException("SHA256 produced an unexpected hash length");
        return HexEncoding.ToLowerHexString(hash);
    }

    private static void AppendUtf8StringToHash(IncrementalHash hasher, string value)
    {
        Span<byte> buffer = stackalloc byte[4096];
        const int MaxCharsPerChunk = 1024;
        for (var offset = 0; offset < value.Length;)
        {
            var charCount = Math.Min(MaxCharsPerChunk, value.Length - offset);
            if (offset + charCount < value.Length
                && charCount > 0
                && char.IsHighSurrogate(value[offset + charCount - 1])
                && char.IsLowSurrogate(value[offset + charCount]))
            {
                charCount--;
            }

            if (charCount == 0)
                charCount = 1;

            var written = Encoding.UTF8.GetBytes(value.AsSpan(offset, charCount), buffer);
            if (written > 0)
                hasher.AppendData(buffer[..written]);
            offset += charCount;
        }
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
        _pathAccessValidator?.Invoke(dir);
        var count = 0;
        var prefixedDir = LongPath.EnsureWindowsPrefix(dir);
        foreach (var pattern in patterns)
        {
            foreach (var markerFile in CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(prefixedDir, pattern))
            {
                var unprefixedMarkerFile = LongPath.RemoveWindowsPrefix(markerFile);
                _pathAccessValidator?.Invoke(unprefixedMarkerFile);
                if (!IsProjectMarkerVisible(unprefixedMarkerFile, activeIgnoreRules: null))
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
            ? !ShouldSkipPath(markerFile)
            : !activeIgnoreRules.IsIgnored(markerFile, isDirectory: false);
    }

    private void CollectProjectMarkerFiles(
        string dir,
        IgnoreRuleSet inheritedIgnoreRules,
        IReadOnlyList<ProjectMarkerFingerprintTraversalState> traversalStates,
        CancellationToken cancellationToken)
    {
        var pendingDirectories = new Stack<ProjectMarkerFingerprintDirectory>();
        var directoryErrors = new List<ScanError>();
        var allLanguagesMask = (1 << traversalStates.Count) - 1;
        foreach (var traversalState in traversalStates)
            traversalState.PendingDirectories = 1;
        pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(
            dir,
            ToRelativePath(dir),
            inheritedIgnoreRules,
            IsProjectRoot: true,
            allLanguagesMask));
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingDirectories.Pop();
            DecrementProjectMarkerPendingDirectories(traversalStates, current.LanguageMask);
            var activeLanguageMask = GetActiveProjectMarkerLanguageMask(traversalStates, current.LanguageMask);
            if (activeLanguageMask == 0)
                continue;

            try
            {
                _pathAccessValidator?.Invoke(current.Path);
            }
            catch (IOException ex)
            {
                var exceptionType = FileSystemTraversalFailure.ExceptionTypeName(ex);
                MarkProjectMarkerTraversalFailure(
                    traversalStates,
                    activeLanguageMask,
                    current.Path,
                    exceptionType);
                continue;
            }
            if (GetDirectoryFilterKind(current.Path, current.RelativePath, current.IgnoreRules, current.IsProjectRoot) != PathFilterKind.None)
                continue;

            for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
            {
                var languageBit = 1 << stateIndex;
                if ((activeLanguageMask & languageBit) == 0)
                    continue;

                var traversalState = traversalStates[stateIndex];
                if (traversalState.DirectoriesVisited >= traversalState.MaxDirectories)
                {
                    TruncateProjectMarkerTraversal(
                        traversalState,
                        traversalState.Errors,
                        current.Path,
                        $"directory budget {traversalState.MaxDirectories:N0} exhausted after visiting {traversalState.DirectoriesVisited:N0} directories");
                    traversalState.TraversalStopped = true;
                    activeLanguageMask &= ~languageBit;
                    continue;
                }

                traversalState.DirectoriesVisited++;
            }

            if (activeLanguageMask == 0)
                continue;

            var currentDirectory = current.Path;
            IgnoreRuleLoadResult loadResult;
            directoryErrors.Clear();
            try
            {
                var fullyScanned = true;
                loadResult = LoadIgnoreRulesForDirectory(currentDirectory, current.IgnoreRules, directoryErrors, ref fullyScanned);
            }
            catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
            {
                CopyProjectMarkerErrors(directoryErrors, traversalStates, activeLanguageMask);
                var exceptionType = FileSystemTraversalFailure.ExceptionTypeName(ex);
                MarkProjectMarkerTraversalFailure(
                    traversalStates,
                    activeLanguageMask,
                    currentDirectory,
                    exceptionType);
                continue;
            }

            CopyProjectMarkerErrors(directoryErrors, traversalStates, activeLanguageMask);
            if (!loadResult.IgnoreRulesAvailable)
            {
                for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
                {
                    var languageBit = 1 << stateIndex;
                    if ((activeLanguageMask & languageBit) == 0)
                        continue;

                    var traversalState = traversalStates[stateIndex];
                    TruncateProjectMarkerTraversal(
                        traversalState,
                        traversalState.Errors,
                        currentDirectory,
                        "ignore-rule loading failed");
                    traversalState.TraversalStopped = true;
                }

                continue;
            }

            var activeIgnoreRules = loadResult.Rules;
            var descendLanguageMask = activeLanguageMask;
            var prefixedCurrentDirectory = LongPath.EnsureWindowsPrefix(currentDirectory);
            foreach (var pattern in MsbuildProjectMarkerPatterns)
            {
                var interestedLanguageMask = GetProjectMarkerPatternLanguageMask(
                    traversalStates,
                    descendLanguageMask,
                    pattern);
                if (interestedLanguageMask == 0)
                    continue;

                try
                {
                    foreach (var enumeratedMarkerFile in CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(prefixedCurrentDirectory, pattern))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var markerFile = LongPath.RemoveWindowsPrefix(enumeratedMarkerFile);
                        _pathAccessValidator?.Invoke(markerFile);
                        if (!IsProjectMarkerVisible(markerFile, activeIgnoreRules))
                            continue;

                        var normalizedMarkerPath = NormalizeScopeKey(ToRelativePath(markerFile));
                        for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
                        {
                            var languageBit = 1 << stateIndex;
                            if ((interestedLanguageMask & languageBit) == 0)
                                continue;

                            var traversalState = traversalStates[stateIndex];
                            if (traversalState.TraversalStopped)
                            {
                                interestedLanguageMask &= ~languageBit;
                                descendLanguageMask &= ~languageBit;
                                continue;
                            }

                            if (traversalState.MarkerFilesCollected >= traversalState.MaxMarkerFiles)
                            {
                                TruncateProjectMarkerTraversal(
                                    traversalState,
                                    traversalState.Errors,
                                    currentDirectory,
                                    $"marker file budget {traversalState.MaxMarkerFiles:N0} exhausted after collecting {traversalState.MarkerFilesCollected:N0} marker files");
                                traversalState.TraversalStopped = true;
                                interestedLanguageMask &= ~languageBit;
                                descendLanguageMask &= ~languageBit;
                                continue;
                            }

                            traversalState.ProjectMarkers.Add(normalizedMarkerPath);
                            traversalState.MarkerFilesCollected++;
                        }

                        if (interestedLanguageMask == 0)
                            break;
                    }
                }
                catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
                {
                    interestedLanguageMask = GetActiveProjectMarkerLanguageMask(
                        traversalStates,
                        interestedLanguageMask);
                    var exceptionType = FileSystemTraversalFailure.ExceptionTypeName(ex);
                    MarkProjectMarkerTraversalFailure(
                        traversalStates,
                        interestedLanguageMask,
                        currentDirectory,
                        exceptionType);
                    descendLanguageMask &= ~interestedLanguageMask;
                }
            }

            if (descendLanguageMask == 0)
                continue;

            try
            {
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

                    var childLanguageMask = 0;
                    for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
                    {
                        var languageBit = 1 << stateIndex;
                        if ((descendLanguageMask & languageBit) == 0)
                            continue;

                        var traversalState = traversalStates[stateIndex];
                        if (traversalState.TraversalStopped)
                        {
                            descendLanguageMask &= ~languageBit;
                            continue;
                        }

                        if (traversalState.DirectoriesVisited + traversalState.PendingDirectories >= traversalState.MaxDirectories)
                        {
                            TruncateProjectMarkerTraversal(
                                traversalState,
                                traversalState.Errors,
                                currentDirectory,
                                $"directory budget {traversalState.MaxDirectories:N0} would be exceeded while queuing subdirectories after visiting {traversalState.DirectoriesVisited:N0} directories");
                            traversalState.TraversalStopped = true;
                            descendLanguageMask &= ~languageBit;
                            continue;
                        }

                        traversalState.PendingDirectories++;
                        childLanguageMask |= languageBit;
                    }

                    if (childLanguageMask == 0)
                        continue;

                    pendingDirectories.Push(new ProjectMarkerFingerprintDirectory(
                        subDir,
                        subRelativePath,
                        activeIgnoreRules,
                        IsProjectRoot: false,
                        childLanguageMask));
                }
            }
            catch (Exception ex) when (FileSystemTraversalFailure.IsExpected(ex))
            {
                var exceptionType = FileSystemTraversalFailure.ExceptionTypeName(ex);
                MarkProjectMarkerTraversalFailure(
                    traversalStates,
                    GetActiveProjectMarkerLanguageMask(traversalStates, descendLanguageMask),
                    currentDirectory,
                    exceptionType);
            }
        }
    }

    private static int GetProjectMarkerPatternLanguageMask(
        IReadOnlyList<ProjectMarkerFingerprintTraversalState> traversalStates,
        int languageMask,
        string pattern)
    {
        var patternLanguageMask = 0;
        for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
        {
            var languageBit = 1 << stateIndex;
            if ((languageMask & languageBit) == 0)
                continue;

            var patterns = traversalStates[stateIndex].Patterns;
            for (var patternIndex = 0; patternIndex < patterns.Count; patternIndex++)
            {
                if (!string.Equals(patterns[patternIndex], pattern, StringComparison.Ordinal))
                    continue;

                patternLanguageMask |= languageBit;
                break;
            }
        }

        return patternLanguageMask;
    }

    private static int GetActiveProjectMarkerLanguageMask(
        IReadOnlyList<ProjectMarkerFingerprintTraversalState> traversalStates,
        int languageMask)
    {
        var activeLanguageMask = 0;
        for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
        {
            var languageBit = 1 << stateIndex;
            if ((languageMask & languageBit) != 0 && !traversalStates[stateIndex].TraversalStopped)
                activeLanguageMask |= languageBit;
        }

        return activeLanguageMask;
    }

    private static void DecrementProjectMarkerPendingDirectories(
        IReadOnlyList<ProjectMarkerFingerprintTraversalState> traversalStates,
        int languageMask)
    {
        for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
        {
            if ((languageMask & (1 << stateIndex)) != 0)
                traversalStates[stateIndex].PendingDirectories--;
        }
    }

    private static void CopyProjectMarkerErrors(
        IReadOnlyList<ScanError> errors,
        IReadOnlyList<ProjectMarkerFingerprintTraversalState> traversalStates,
        int languageMask)
    {
        if (errors.Count == 0)
            return;

        for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
        {
            if ((languageMask & (1 << stateIndex)) != 0)
                traversalStates[stateIndex].Errors.AddRange(errors);
        }
    }

    private void MarkProjectMarkerTraversalFailure(
        IReadOnlyList<ProjectMarkerFingerprintTraversalState> traversalStates,
        int languageMask,
        string directory,
        string exceptionType)
    {
        for (var stateIndex = 0; stateIndex < traversalStates.Count; stateIndex++)
        {
            if ((languageMask & (1 << stateIndex)) == 0)
                continue;

            var traversalState = traversalStates[stateIndex];
            AddProjectMarkerTraversalWarning(traversalState.Errors, directory, exceptionType);
            MarkProjectMarkerTraversalTruncated(
                traversalState,
                $"traversal failed with {exceptionType}");
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
        if (HasReachedProjectMarkerWarningLimit(errors, "Project marker discovery skipped"))
            return;

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
        if (HasReachedProjectMarkerWarningLimit(errors, "Project marker discovery truncated"))
            return;

        var relativePath = NormalizeIgnorePath(ToRelativePath(directory));
        if (string.IsNullOrEmpty(relativePath))
            relativePath = ".";

        errors.Add(new ScanError(
            relativePath,
            $"Project marker discovery truncated because {reason}.",
            ScanIssueSeverity.Warning));
    }

    private static bool HasReachedProjectMarkerWarningLimit(List<ScanError> errors, string messagePrefix)
    {
        var matchingWarningCount = 0;
        foreach (var error in errors)
        {
            if (!error.Message.StartsWith(messagePrefix, StringComparison.Ordinal))
                continue;

            matchingWarningCount++;
            if (matchingWarningCount >= MaxProjectMarkerTraversalWarnings)
                return true;
        }

        return false;
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
