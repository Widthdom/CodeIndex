using CodeIndex.Models;
using System.Runtime.InteropServices;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private bool TryAcceptScannedFile(
        string file,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        HashSet<string>? seenFilePaths,
        FileAttributes? knownAttributes = null,
        bool filePathCameFromDirectoryEnumeration = false)
    {
        if (!IsFilePathSyntaxIndexable(file))
        {
            var issuePath = FormatPathForScanIssue(file);
            scanState.Errors.Add(new ScanError(
                issuePath,
                "Skipped file because its path contains NUL or control characters.",
                ScanIssueSeverity.Warning));
            scanState.RecordNonIndexablePath(issuePath);
            return false;
        }

        if (IsInternalIndexArtifactPath(ToRelativePath(file)))
            return false;

        if (seenFilePaths is not null)
        {
            var seenFilePathKey = GetSeenFilePathKey(file, filePathCameFromDirectoryEnumeration);
            if (!seenFilePaths.Add(seenFilePathKey))
            {
                var relativePath = ToRelativePath(file);
                scanState.Errors.Add(new ScanError(
                    relativePath,
                    "Skipped duplicate file path that differs only by case on a case-insensitive directory.",
                    ScanIssueSeverity.Warning));
                scanState.RecordNonIndexablePath(relativePath);
                return false;
            }
        }

        var fileName = Path.GetFileName(file.AsSpan());

        // Skip excluded file names / 除外ファイル名をスキップ
        if (IsDefaultExcludedFileName(fileName) || IsBuiltInSuggestionStorePath(file))
            return false;

        if (activeIgnoreRules.IsIgnored(file, isDirectory: false))
            return false;

        var knownIndexability = knownAttributes.HasValue
            ? GetFileIndexabilityForFoundAttributes(file, knownAttributes.Value, _symlinkPolicy, _projectRoot)
            : (FileProbeStatus?)null;
        return TryAcceptSupportedScannedFile(file, scanState, knownIndexability);
    }

    private static string GetSeenFilePathKey(string file, bool filePathCameFromDirectoryEnumeration)
        => filePathCameFromDirectoryEnumeration ? file : Path.GetFullPath(file);

    private bool TryAcceptSupportedScannedFile(
        string file,
        DirectoryScanState scanState,
        FileProbeStatus? knownIndexability = null)
    {
        // Use the instance symlink policy here so full scans and update paths apply the same
        // file-link behavior.
        // full scan と update 経路で同じ file-link 挙動になるよう instance の symlink policy を使う。
        var indexability = knownIndexability ?? GetFileIndexabilityForIndexing(file);
        if (indexability == FileProbeStatus.Missing)
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(
                relativePath,
                "Skipped file because it was deleted during scanning.",
                ScanIssueSeverity.Warning));
            scanState.RecordNonIndexablePath(relativePath);
            return false;
        }

        if (indexability == FileProbeStatus.ProbeFailed)
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(relativePath, "Could not probe file for indexability/language."));
            scanState.RecordProbeFailedFilePath(relativePath);
            return false;
        }

        if (indexability != FileProbeStatus.Supported)
        {
            scanState.RecordNonIndexablePath(ToRelativePath(file));
            return false;
        }

        // Include files with a known extension/filename or an extensionless recognized shebang
        // 既知の拡張子・既知ファイル名、または拡張子なしで shebang を認識できるファイルを含める
        var language = TryDetectLanguageForIndexing(file, knownIndexability: indexability);
        if (language.Status == FileProbeStatus.Missing)
        {
            var relativeFile = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(
                relativeFile,
                "Skipped file because it was deleted during scanning.",
                ScanIssueSeverity.Warning));
            scanState.RecordNonIndexablePath(relativeFile);
            return false;
        }

        if (language.Status == FileProbeStatus.ProbeFailed)
        {
            var relativeFile = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(relativeFile, "Could not probe file for indexability/language."));
            scanState.RecordProbeFailedFilePath(relativeFile);
            return false;
        }

        if (language.Status != FileProbeStatus.Supported)
        {
            var relativeFile = ToRelativePath(file);
            scanState.RecordNonIndexablePath(relativeFile);
            if (HasUnknownExtension(file) && !IsInternalIndexArtifactPath(relativeFile))
                scanState.RecordUnknownExtensionFile(relativeFile);
            return false;
        }

        if (TryGetFileIdentity(file, out var identity, out var linkCount)
            && linkCount > 1
            && !scanState.RecordFileIdentity(identity))
        {
            var relativeFile = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(
                relativeFile,
                "Skipped hardlinked file because the same file content was already indexed from another path.",
                ScanIssueSeverity.Warning));
            scanState.RecordNonIndexablePath(relativeFile);
            return false;
        }

        if (language.Language is { Length: > 0 } acceptedLanguage)
        {
            scanState.FileLanguages[file] = acceptedLanguage;
            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(
                scanState.LanguageCounts,
                acceptedLanguage,
                out _);
            count++;
        }
        return true;
    }
}
