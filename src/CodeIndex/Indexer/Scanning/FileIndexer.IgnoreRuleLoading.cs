using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private IgnoreRuleLoadResult LoadIgnoreRulesForDirectory(
        string dir,
        IgnoreRuleSet inheritedIgnoreRules,
        List<ScanError>? errors,
        ref bool fullyScanned)
    {
        List<IgnoreRule>? rules = null;
        var ignoreRulesAvailable = true;

        foreach (var ignoreFileName in IgnoreFileNames)
        {
            var ignorePath = Path.Combine(dir, ignoreFileName);
            if (!TryAppendIgnoreRulesFromFile(
                    dir,
                    ignorePath,
                    ignoreFileName,
                    ref rules,
                    errors,
                    ref fullyScanned))
            {
                fullyScanned = false;
                ignoreRulesAvailable = false;
            }
        }

        return ignoreRulesAvailable
            ? new IgnoreRuleLoadResult(IgnoreRuleSet.CreateChild(inheritedIgnoreRules, AsLoadedIgnoreRules(rules)), IgnoreRulesAvailable: true)
            : new IgnoreRuleLoadResult(inheritedIgnoreRules, IgnoreRulesAvailable: false);
    }

    private IgnoreRuleLoadResult LoadWorkspaceConfigIgnoreRules(
        IgnoreRuleSet inheritedIgnoreRules,
        List<ScanError>? errors,
        ref bool fullyScanned)
    {
        var configIgnorePath = Path.Combine(_projectRoot, ".codeindex", ".cdidxignore");
        return LoadIgnoreRulesFile(
            sourceDirectory: _projectRoot,
            ignorePath: configIgnorePath,
            ignoreFileName: ".codeindex/.cdidxignore",
            inheritedIgnoreRules,
            errors,
            ref fullyScanned);
    }

    private IgnoreRuleLoadResult LoadAncestorIgnoreRules(List<ScanError>? errors, ref bool fullyScanned)
    {
        var activeIgnoreRules = IgnoreRuleSet.Empty;
        foreach (var dir in _ancestorIgnoreDirectories)
        {
            if (!CanReadDirectory(dir, out var reason))
            {
                errors?.Add(new ScanError(ToRelativePath(dir), $"Could not read ancestor ignore directory: {reason}."));
                fullyScanned = false;
                return new IgnoreRuleLoadResult(activeIgnoreRules, IgnoreRulesAvailable: false);
            }

            var loadResult = LoadIgnoreRulesForDirectory(dir, activeIgnoreRules, errors, ref fullyScanned);
            activeIgnoreRules = loadResult.Rules;
            if (!loadResult.IgnoreRulesAvailable)
                return new IgnoreRuleLoadResult(activeIgnoreRules, IgnoreRulesAvailable: false);
        }

        return LoadWorkspaceConfigIgnoreRules(activeIgnoreRules, errors, ref fullyScanned);
    }

    private IgnoreRuleLoadResult LoadIgnoreRulesFile(
        string sourceDirectory,
        string ignorePath,
        string ignoreFileName,
        IgnoreRuleSet inheritedIgnoreRules,
        List<ScanError>? errors,
        ref bool fullyScanned)
    {
        List<IgnoreRule>? rules = null;
        if (!TryAppendIgnoreRulesFromFile(
                sourceDirectory,
                ignorePath,
                ignoreFileName,
                ref rules,
                errors,
                ref fullyScanned))
        {
            fullyScanned = false;
            return new IgnoreRuleLoadResult(inheritedIgnoreRules, IgnoreRulesAvailable: false);
        }

        return new IgnoreRuleLoadResult(IgnoreRuleSet.CreateChild(inheritedIgnoreRules, AsLoadedIgnoreRules(rules)), IgnoreRulesAvailable: true);
    }

    private static IReadOnlyList<IgnoreRule> AsLoadedIgnoreRules(List<IgnoreRule>? rules)
        => rules is { Count: > 0 } ? rules : Array.Empty<IgnoreRule>();

    private bool TryAppendIgnoreRulesFromFile(
        string sourceDirectory,
        string ignorePath,
        string ignoreFileName,
        ref List<IgnoreRule>? rules,
        List<ScanError>? errors,
        ref bool fullyScanned)
    {
        var prefixedIgnorePath = LongPath.EnsureWindowsPrefix(ignorePath);

        try
        {
            if (!TryReadBoundedUtf8SidecarLines(
                    prefixedIgnorePath,
                    MaxIgnoreFileBytes,
                    MaxIgnoreFileLines,
                    out var lines,
                    out var skippedReason,
                    out var readFailure))
            {
                if (readFailure.ExceptionType is nameof(FileNotFoundException) or nameof(DirectoryNotFoundException))
                    return true;

                if (readFailure.ExceptionType == nameof(UnauthorizedAccessException))
                {
                    if (!File.Exists(prefixedIgnorePath))
                        throw new UnauthorizedAccessException(readFailure.Reason);

                    errors?.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName} due to permissions.", ScanIssueSeverity.Warning));
                    return true;
                }

                errors?.Add(new ScanError(
                    ToRelativePath(ignorePath),
                    $"Could not safely read {ignoreFileName} because {skippedReason}."));
                fullyScanned = false;
                return false;
            }

            var lineNumber = 0;
            var rulesInFile = 0;
            foreach (var line in lines)
            {
                lineNumber++;
                if (IgnoreRule.TryParse(sourceDirectory, line, _ignoreCase, out var rule, out var errorMessage) && rule != null)
                {
                    if (rulesInFile >= MaxIgnoreRulesPerFile)
                    {
                        errors?.Add(new ScanError(
                            $"{ToRelativePath(ignorePath)}:{lineNumber}",
                            $"Stopped scanning because {ignoreFileName} exceeds {MaxIgnoreRulesPerFile} rules."));
                        fullyScanned = false;
                        return false;
                    }

                    (rules ??= []).Add(rule);
                    rulesInFile++;
                }
                else if (errorMessage != null)
                {
                    errors?.Add(new ScanError($"{ToRelativePath(ignorePath)}:{lineNumber}", errorMessage, ScanIssueSeverity.Warning));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (!File.Exists(prefixedIgnorePath))
                throw;

            errors?.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName} due to permissions.", ScanIssueSeverity.Warning));
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            errors?.Add(new ScanError(ToRelativePath(ignorePath), $"Could not read {ignoreFileName}."));
            fullyScanned = false;
            return false;
        }

        return true;
    }

    private string NormalizeIgnoreRuleRoot(string? ignoreRuleRoot)
    {
        if (string.IsNullOrWhiteSpace(ignoreRuleRoot))
            return _projectRoot;

        var candidate = Path.GetFullPath(ignoreRuleRoot);
        return IsPathEqualOrParent(candidate, _projectRoot)
            ? candidate
            : _projectRoot;
    }

    private static IReadOnlyList<string> BuildAncestorIgnoreDirectories(string ignoreRuleRoot, string projectRoot)
    {
        if (PathsEqual(ignoreRuleRoot, projectRoot))
            return [];

        if (!IsPathEqualOrParent(ignoreRuleRoot, projectRoot))
            return [];

        var directories = new List<string>();
        var root = Path.GetFullPath(ignoreRuleRoot);
        var current = Directory.GetParent(Path.GetFullPath(projectRoot));
        while (current != null)
        {
            directories.Add(current.FullName);
            if (PathsEqual(current.FullName, root))
            {
                directories.Reverse();
                return directories;
            }

            current = current.Parent;
        }

        return [];
    }

    private static bool CanReadDirectory(string dir, out string reason)
    {
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(dir)))
        {
            reason = "directory-missing";
            return false;
        }

        try
        {
            _ = CodeIndex.FileSystemTraversalPolicy.HasAnyFileSystemEntry(LongPath.EnsureWindowsPrefix(dir));
            reason = string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "access-denied";
            return false;
        }
        catch (IOException)
        {
            reason = "io-error";
            return false;
        }
    }
}
