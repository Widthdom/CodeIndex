using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private bool TryAcceptScannedFile(
        string file,
        DirectoryScanState scanState,
        IgnoreRuleSet activeIgnoreRules,
        HashSet<string>? seenFilePaths,
        FileAttributes? knownAttributes = null)
    {
        if (!IsFilePathSyntaxIndexable(file))
        {
            var issuePath = FormatPathForScanIssue(file);
            scanState.Errors.Add(new ScanError(
                issuePath,
                "Skipped file because its path contains NUL or control characters.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(issuePath);
            return false;
        }

        if (seenFilePaths is not null && !seenFilePaths.Add(Path.GetFullPath(file)))
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(
                relativePath,
                "Skipped duplicate file path that differs only by case on a case-insensitive directory.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativePath);
            return false;
        }

        var fileName = Path.GetFileName(file.AsSpan());

        // Skip excluded file names / 除外ファイル名をスキップ
        if (IsDefaultExcludedFileName(fileName))
            return false;

        if (activeIgnoreRules.IsIgnored(file, isDirectory: false))
            return false;

        var knownIndexability = knownAttributes.HasValue
            ? GetFileIndexabilityForFoundAttributes(file, knownAttributes.Value, _symlinkPolicy, _projectRoot)
            : (FileProbeStatus?)null;
        return TryAcceptSupportedScannedFile(file, scanState, knownIndexability);
    }

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
            scanState.NonIndexablePaths.Add(relativePath);
            return false;
        }

        if (indexability == FileProbeStatus.ProbeFailed)
        {
            var relativePath = ToRelativePath(file);
            scanState.Errors.Add(new ScanError(relativePath, "Could not probe file for indexability/language."));
            scanState.ProbeFailedFilePaths.Add(relativePath);
            return false;
        }

        if (indexability != FileProbeStatus.Supported)
        {
            scanState.NonIndexablePaths.Add(ToRelativePath(file));
            return false;
        }

        var relativeFile = ToRelativePath(file);
        // Include files with a known extension/filename or an extensionless recognized shebang
        // 既知の拡張子・既知ファイル名、または拡張子なしで shebang を認識できるファイルを含める
        var language = TryDetectLanguageForIndexing(file, knownIndexability: indexability);
        if (language.Status == FileProbeStatus.Missing)
        {
            scanState.Errors.Add(new ScanError(
                relativeFile,
                "Skipped file because it was deleted during scanning.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativeFile);
            return false;
        }

        if (language.Status == FileProbeStatus.ProbeFailed)
        {
            scanState.Errors.Add(new ScanError(relativeFile, "Could not probe file for indexability/language."));
            scanState.ProbeFailedFilePaths.Add(relativeFile);
            return false;
        }

        if (language.Status != FileProbeStatus.Supported)
        {
            scanState.NonIndexablePaths.Add(relativeFile);
            if (HasUnknownExtension(file) && !IsInternalIndexArtifactPath(relativeFile))
                scanState.UnknownExtensionFiles.Add(relativeFile);
            return false;
        }

        if (TryGetFileIdentity(file, out var identity) && !scanState.VisitedFileIdentities.Add(identity))
        {
            scanState.Errors.Add(new ScanError(
                relativeFile,
                "Skipped hardlinked file because the same file content was already indexed from another path.",
                ScanIssueSeverity.Warning));
            scanState.NonIndexablePaths.Add(relativeFile);
            return false;
        }

        if (language.Language is { Length: > 0 } acceptedLanguage)
            scanState.FileLanguages[file] = acceptedLanguage;
        return true;
    }
}
