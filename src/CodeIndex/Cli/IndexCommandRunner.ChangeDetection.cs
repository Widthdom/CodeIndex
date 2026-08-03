using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static Dictionary<string, FileIndexer.ProjectMarkerFingerprintResult> GetHotspotFamilyMarkerFingerprints(
        FileIndexer indexer,
        CancellationToken cancellationToken = default) =>
        indexer.GetProjectMarkerFingerprintResults(cancellationToken);

    private static int AddProjectMarkerFingerprintWarnings(
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints,
        List<CliJsonMessage> warningList,
        IndexCommandOptions options)
    {
        var added = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fingerprint in currentFingerprints.Values)
        {
            foreach (var warning in fingerprint.Warnings)
            {
                if (!IsProjectMarkerFingerprintWarning(warning))
                    continue;

                var path = string.IsNullOrWhiteSpace(warning.Path)
                    ? "<project_marker_fingerprint>"
                    : warning.Path;
                var key = $"{path}\0{warning.Message}";
                if (!seen.Add(key))
                    continue;

                warningList.Add(new CliJsonMessage(path, warning.Message));
                added++;
                if (!options.Json && !options.Quiet)
                    ConsoleUi.PrintWarning($"{path}: {warning.Message}");
            }
        }

        return added;
    }

    private static bool IsProjectMarkerFingerprintWarning(FileIndexer.ScanError warning) =>
        warning.Message.StartsWith("Project marker discovery skipped", StringComparison.Ordinal)
        || warning.Message.StartsWith("Project marker discovery truncated", StringComparison.Ordinal)
        || warning.Message.StartsWith("Skipped .gitmodules", StringComparison.Ordinal);

    private static void RestampHotspotFamilyTrustForUpdate(
        DbWriter writer,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            var currentVersion = DbContext.GetHotspotFamilyVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            if (priorVersions.TryGetValue(lang, out var priorVersion)
                && priorFingerprints.TryGetValue(lang, out var priorFingerprint)
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint)
            {
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
            }
        }
    }

    private static void RestampHotspotFamilyTrustForFullScan(
        DbWriter writer,
        IReadOnlySet<string>? reusedLanguages,
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            var currentVersion = DbContext.GetHotspotFamilyVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!currentFingerprints.TryGetValue(lang, out var currentFingerprint))
                continue;

            if (!currentFingerprint.IsComplete)
            {
                writer.MarkHotspotFamilyMarkerFingerprintIncomplete(lang, currentFingerprint.Fingerprint);
                continue;
            }

            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            if (reusedLanguages?.Contains(lang) != true || (priorVersion == currentVersion && priorFingerprint == currentFingerprint.Fingerprint))
                writer.MarkHotspotFamilyReady(lang, currentFingerprint.Fingerprint);
        }
    }

    private static Dictionary<string, bool> GetHotspotFamilyTrustMatchesCurrent(
        IReadOnlyDictionary<string, string?> priorVersions,
        IReadOnlyDictionary<string, string?> priorFingerprints,
        IReadOnlyDictionary<string, FileIndexer.ProjectMarkerFingerprintResult> currentFingerprints)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            var currentVersion = DbContext.GetHotspotFamilyVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
            currentFingerprints.TryGetValue(lang, out var currentFingerprint);
            priorVersions.TryGetValue(lang, out var priorVersion);
            priorFingerprints.TryGetValue(lang, out var priorFingerprint);
            values[lang] = currentFingerprint.IsComplete
                && priorVersion == currentVersion
                && priorFingerprint == currentFingerprint.Fingerprint;
        }

        return values;
    }

    private static bool AllowReuseWithCurrentHotspotFamilyTrust(
        string? lang,
        IReadOnlyDictionary<string, bool> hotspotFamilyTrustMatchesCurrent)
    {
        if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(lang))
            return true;

        return lang != null
            && hotspotFamilyTrustMatchesCurrent.TryGetValue(lang, out var matchesCurrent)
            && matchesCurrent;
    }

    internal static bool IsOutsideProjectRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return true;

        var normalized = OperatingSystem.IsWindows()
            ? relativePath.Replace('\\', '/')
            : relativePath;
        return normalized == ".." || normalized.StartsWith("../", StringComparison.Ordinal);
    }

    private static bool ContainsIgnoreFilePath(IEnumerable<string> paths)
        => paths.Any(FileIndexer.IsIgnoreFilePath);

    private static bool ContainsExtractorConfigurationPath(string projectRoot, IEnumerable<string> paths)
        => paths.Any(path =>
            FileIndexer.ClassifyIndexInputInvalidation(projectRoot, path)
                == FileIndexer.IndexInputInvalidationKind.ExtractorConfiguration);

    private static bool ContainsJavaScriptTypeScriptConfigPath(IEnumerable<string> paths)
        => paths.Any(IsJavaScriptTypeScriptConfigPath);

    private static bool IsJavaScriptTypeScriptLanguage(string? language)
        => string.Equals(language, "javascript", StringComparison.Ordinal)
            || string.Equals(language, "typescript", StringComparison.Ordinal);

    private static bool IsJavaScriptTypeScriptConfigPath(string path)
    {
        var fileName = Path.GetFileName(path.AsSpan());
        return fileName.Equals("jsconfig.json".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("tsconfig.json".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || (fileName.StartsWith("jsconfig.".AsSpan(), StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".json".AsSpan(), StringComparison.OrdinalIgnoreCase))
            || (fileName.StartsWith("tsconfig.".AsSpan(), StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".json".AsSpan(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsRelevantIgnoreFileUpdate(string projectRoot, IEnumerable<string> updateFiles)
    {
        foreach (var file in updateFiles)
        {
            var absolutePath = Path.IsPathRooted(file)
                ? Path.GetFullPath(file)
                : Path.GetFullPath(Path.Combine(projectRoot, file));
            if (FileIndexer.IsIgnoreFilePath(absolutePath) && IsRelevantIgnoreFileForProjectRoot(projectRoot, absolutePath))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> NormalizeCommitFileTargets(
        string projectRoot,
        string repoRoot,
        IEnumerable<string> changedFiles,
        out bool relevantIgnoreFileChanged)
    {
        relevantIgnoreFileChanged = false;
        var normalized = new List<string>();
        foreach (var changedFile in changedFiles)
        {
            var absolutePath = Path.GetFullPath(Path.Combine(repoRoot, changedFile.Replace('/', Path.DirectorySeparatorChar)));
            if (FileIndexer.IsIgnoreFilePath(absolutePath) && IsRelevantIgnoreFileForProjectRoot(projectRoot, absolutePath))
                relevantIgnoreFileChanged = true;

            var relativePath = FileIndexer.NormalizePathSeparators(
                FileIndexer.GetRelativePathFromProjectRoot(projectRoot, absolutePath));
            if (IsOutsideProjectRoot(relativePath))
                continue;

            normalized.Add(relativePath);
        }

        return normalized;
    }

    private static bool IsRelevantIgnoreFileForProjectRoot(string projectRoot, string ignoreFileAbsolutePath)
    {
        var ignoreDirectory = Path.GetDirectoryName(ignoreFileAbsolutePath);
        if (string.IsNullOrEmpty(ignoreDirectory))
            return false;

        return IsPathEqualOrParent(ignoreDirectory, projectRoot)
            || IsPathEqualOrParent(projectRoot, ignoreDirectory);
    }

    private static string DescribePathFilter(FileIndexer.PathFilterKind filterKind)
        => filterKind switch
        {
            FileIndexer.PathFilterKind.IgnoredByRules => "ignored by .gitignore/.cdidxignore",
            FileIndexer.PathFilterKind.ExcludedByDefaultDirectory => "excluded by default directory rules",
            FileIndexer.PathFilterKind.ExcludedByDefaultFile => "excluded by default file rules",
            FileIndexer.PathFilterKind.OutsideProjectRoot => "outside the project root",
            FileIndexer.PathFilterKind.IgnoreRulesUnavailable => "ignore rules unavailable",
            _ => "filtered",
        };

    private static IReadOnlyList<string> NormalizeUpdateFileTargets(string projectRoot, IEnumerable<string> updateFiles, bool json)
    {
        var normalized = new List<string>();
        foreach (var file in updateFiles)
        {
            var absPath = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(projectRoot, file));
            var relPath = FileIndexer.NormalizePathSeparators(
                FileIndexer.GetRelativePathFromProjectRoot(projectRoot, absPath));
            if (IsOutsideProjectRoot(relPath))
            {
                if (!json)
                    CommandErrorWriter.WriteStderr($"  [WARN] Skipping file outside project root: {file}. Use a path under the indexed project root or run `cdidx index` from the correct workspace.");
                continue;
            }

            normalized.Add(relPath);
        }

        return normalized;
    }

    private static bool IsPathEqualOrParent(string candidateParent, string candidateChild)
    {
        var normalizedParent = Path.GetFullPath(candidateParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedChild = Path.GetFullPath(candidateChild)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return PathCasing.IsPathEqualOrParent(normalizedParent, normalizedChild);
    }
}
