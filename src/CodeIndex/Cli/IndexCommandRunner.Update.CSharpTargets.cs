using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static List<CSharpStaticInterfacePrepass.FileTarget> BuildUpdateCSharpPrepassTargets(
        FileIndexer indexer,
        string projectRoot,
        IReadOnlyCollection<string> targetPaths,
        IReadOnlyDictionary<string, string>? scannedLanguages,
        out HashSet<string>? existingCSharpPathsNowUnsupportedOrNonCSharp)
    {
        var targets = new List<CSharpStaticInterfacePrepass.FileTarget>(targetPaths.Count);
        HashSet<string>? transitionedPaths = null;

        void RememberExistingCSharpTransition(string indexPath)
            => (transitionedPaths ??= new HashSet<string>(StringComparer.Ordinal)).Add(indexPath);

        foreach (var targetPath in targetPaths)
        {
            var updateTarget = UpdateFileTarget.Create(projectRoot, targetPath);
            var absPath = updateTarget.FilePath;
            if (!File.Exists(LongPath.EnsureWindowsPrefix(absPath)))
            {
                RememberExistingCSharpTransition(updateTarget.IndexPath);
                continue;
            }

            string? language;
            if (scannedLanguages != null)
            {
                // A clean expanded scan is the authoritative membership snapshot. A
                // caller-selected path that is absent from it was filtered, ignored, or
                // otherwise non-indexable and must not be reintroduced by extension-only
                // detection. The normal update loop still retains the target so it can
                // remove any persisted row.
                // clean expanded scan に存在しない caller target は filtered / ignored /
                // non-indexable であり、拡張子判定だけで workspace に戻してはならない。
                // 実 update target には残し、既存 row の削除処理を行う。
                if (!scannedLanguages.TryGetValue(absPath, out var scannedLanguage)
                    || scannedLanguage != "csharp")
                {
                    RememberExistingCSharpTransition(updateTarget.IndexPath);
                    continue;
                }

                language = scannedLanguage;
            }
            else
            {
                var detection = FileIndexer.TryDetectLanguage(absPath);
                if (detection.Status != FileIndexer.FileProbeStatus.Supported
                    || detection.Language != "csharp")
                {
                    RememberExistingCSharpTransition(updateTarget.IndexPath);
                    continue;
                }

                language = detection.Language;
            }

            var target = new CSharpStaticInterfacePrepass.FileTarget(
                updateTarget.FilePath,
                updateTarget.RelativePath,
                updateTarget.DisplayRelativePath,
                updateTarget.IndexPath,
                language,
                ResolveSymlinkTargets: indexer.ResolvesSymlinkTargets);
            targets.Add(target with
            {
                GeneratedExtractionSuppressed =
                    indexer.HasGeneratedCodeExtractionSuppressionPatterns
                    && indexer.IsGeneratedCodeExtractionSuppressed(target.IndexPath)
            });
        }

        existingCSharpPathsNowUnsupportedOrNonCSharp = transitionedPaths;
        return targets;
    }

    private static bool TryValidateCurrentCSharpTargetSet(
        string projectRoot,
        IEnumerable<string> currentTargetPaths,
        IReadOnlyDictionary<string, string>? scannedLanguages,
        IReadOnlyDictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot> snapshots,
        out string? failedPath,
        CancellationToken cancellationToken)
    {
        var currentCSharpTargets = new List<CSharpStaticInterfacePrepass.FileTarget>(
            snapshots.Count);
        foreach (var targetPath in currentTargetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = UpdateFileTarget.Create(projectRoot, targetPath);
            var ioPath = LongPath.EnsureWindowsPrefix(target.FilePath);
            var expectedCSharp = snapshots.ContainsKey(target.IndexPath);
            if (!File.Exists(ioPath))
            {
                if (expectedCSharp)
                {
                    failedPath = target.RelativePath;
                    return false;
                }

                continue;
            }

            var isCSharp = scannedLanguages != null
                ? scannedLanguages.TryGetValue(target.FilePath, out var scannedLanguage)
                  && scannedLanguage == "csharp"
                : FileIndexer.TryDetectLanguage(target.FilePath) is
                { Status: FileIndexer.FileProbeStatus.Supported, Language: "csharp" };
            if (!isCSharp)
            {
                if (expectedCSharp)
                {
                    failedPath = target.RelativePath;
                    return false;
                }

                continue;
            }

            if (!expectedCSharp)
            {
                failedPath = target.RelativePath;
                return false;
            }

            currentCSharpTargets.Add(new CSharpStaticInterfacePrepass.FileTarget(
                target.FilePath,
                target.RelativePath,
                target.RelativePath,
                target.IndexPath,
                "csharp"));
        }

        return CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
            currentCSharpTargets,
            snapshots,
            out failedPath,
            cancellationToken);
    }

    private static void DeferCSharpTargetsAfterIncompleteWorkspace(
        DbWriter writer,
        string projectRoot,
        HashSet<string> targetPaths,
        CancellationToken cancellationToken)
    {
        var deferredTargetPaths = new List<string>();
        var persistedLanguageCandidates = new List<(string TargetPath, string IndexPath)>();
        var persistedLanguageCandidatePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var targetPath in targetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = UpdateFileTarget.Create(projectRoot, targetPath);
            var detection = FileIndexer.TryDetectLanguage(target.FilePath);
            if (detection.Status == FileIndexer.FileProbeStatus.Supported
                && detection.Language == "csharp")
            {
                deferredTargetPaths.Add(targetPath);
                continue;
            }

            persistedLanguageCandidates.Add((targetPath, target.IndexPath));
            persistedLanguageCandidatePaths.Add(target.IndexPath);
        }

        var persistedCSharpPaths = writer.ResolveCSharpFilePaths(
            persistedLanguageCandidatePaths,
            cancellationToken);
        foreach (var candidate in persistedLanguageCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (persistedCSharpPaths.Contains(candidate.IndexPath))
                deferredTargetPaths.Add(candidate.TargetPath);
        }

        // targetPaths itself is enumerated only once above. Remove by the bounded result
        // list so a large incomplete workspace does not rescan it after the batched DB read.
        // targetPaths 自体は上で一度だけ走査し、batch 結果の path だけを直接除外する。
        foreach (var deferredTargetPath in deferredTargetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targetPaths.Remove(deferredTargetPath);
        }
    }
}
