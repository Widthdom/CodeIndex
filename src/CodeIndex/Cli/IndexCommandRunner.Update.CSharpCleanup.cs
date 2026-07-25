using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static FilePurgePlan PlanUpdateCSharpCleanup(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        HashSet<string> targetPaths,
        IReadOnlyCollection<string> gitTargetPaths,
        IReadOnlyCollection<string> explicitFileTargetPaths,
        IndexCommandOptions options,
        bool projectRootWritten,
        bool? priorCSharpStaticInterfaceSourceEvidence,
        Action throwIfUpdateCancelled,
        CancellationToken cancellationToken)
    {
        var scopedCleanupPlans = new List<FilePurgePlan>();
        var csharpPreWorkspaceCleanupTargets = new List<(
            string RetainedRelativePath,
            string? Checksum,
            bool IncludeDirectoryAndStem)>();

        if (priorCSharpStaticInterfaceSourceEvidence != false)
        {
            PlanGitUpdateCSharpCleanup(
                writer,
                projectRoot,
                targetPaths,
                gitTargetPaths,
                options.ChangedBetweenSpecified,
                scopedCleanupPlans,
                csharpPreWorkspaceCleanupTargets,
                cancellationToken);
        }

        if (options.ChangedBetweenSpecified
            && priorCSharpStaticInterfaceSourceEvidence != false)
        {
            throwIfUpdateCancelled();
            var skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(
                projectRoot,
                cancellationToken);
            var preservedMissingPaths = skipWorktreePaths == null
                ? null
                : new HashSet<string>(skipWorktreePaths, StringComparer.Ordinal);
            scopedCleanupPlans.Add(
                writer.PlanStaleCSharpFiles(
                    projectRoot,
                    preservedMissingPaths,
                    cancellationToken));
        }

        if (priorCSharpStaticInterfaceSourceEvidence != false)
        {
            PlanExplicitUpdateCSharpCleanup(
                writer,
                indexer,
                projectRoot,
                explicitFileTargetPaths,
                options.MaxFileSizeBytes,
                projectRootWritten,
                csharpPreWorkspaceCleanupTargets,
                cancellationToken);
            if (csharpPreWorkspaceCleanupTargets.Count > 0)
            {
                scopedCleanupPlans.Add(
                    writer.PlanStaleCSharpFilesSharingCleanupKeys(
                        projectRoot,
                        csharpPreWorkspaceCleanupTargets,
                        cancellationToken));
            }
        }

        return FilePurgePlan.Merge(scopedCleanupPlans);
    }

    private static void PlanGitUpdateCSharpCleanup(
        DbWriter writer,
        string projectRoot,
        HashSet<string> targetPaths,
        IReadOnlyCollection<string> gitTargetPaths,
        bool changedBetweenSpecified,
        List<FilePurgePlan> scopedCleanupPlans,
        List<(string RetainedRelativePath, string? Checksum, bool IncludeDirectoryAndStem)>
            csharpPreWorkspaceCleanupTargets,
        CancellationToken cancellationToken)
    {
        if (gitTargetPaths.Count == 0)
            return;

        // Git name-status supplies both sides of a rename. Resolve its missing side by the
        // indexed path directly instead of hashing every live file in a wide commit/range.
        // A live Git target is retained as a checksum-free alias key. Whole-path case folding
        // is only a candidate prefilter; old/new filesystem identities must match before an
        // exact persisted source row can be planned for deletion. Explicit --files may name
        // only the retained side, so those targets keep checksum/stem discovery below.
        // Git name-status は rename の両側を返すため missing 側を path で直接解決し、巨大
        // commit/range の live file を checksum のために二重読込しない。case-only rename
        // 用の alias key は保持し、片側しか指定できない --files だけ checksum を読む。
        var missingGitTargetIndexPaths = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? liveGitTargetIndexPaths = null;
        foreach (var gitTargetPath in gitTargetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gitTarget = UpdateFileTarget.Create(projectRoot, gitTargetPath);
            if (!File.Exists(LongPath.EnsureWindowsPrefix(gitTarget.FilePath)))
            {
                missingGitTargetIndexPaths.Add(gitTarget.IndexPath);
                continue;
            }

            (liveGitTargetIndexPaths ??= new HashSet<string>(StringComparer.Ordinal))
                .Add(gitTarget.IndexPath);
        }

        if (!changedBetweenSpecified)
        {
            scopedCleanupPlans.Add(
                writer.PlanCSharpFilesInPaths(
                    missingGitTargetIndexPaths,
                    cancellationToken));
        }

        if (liveGitTargetIndexPaths is not { Count: > 0 })
            return;

        // File.Exists(old-cased-path) can resolve the retained file on an
        // insensitive filesystem. Only a live Git path without an exact persisted
        // row is the retained alias key; the old side already present in the DB
        // must remain eligible for cleanup.
        // case-insensitive FS では旧 casing も File.Exists=true になるため、DB に
        // exact row がない live Git path だけを retained alias key とする。
        var persistedLiveGitPaths = writer.ResolveCSharpFilePaths(
            liveGitTargetIndexPaths,
            cancellationToken);
        var persistedLiveGitPathsByAlias = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var persistedLiveGitPath in persistedLiveGitPaths)
        {
            if (!persistedLiveGitPathsByAlias.TryGetValue(
                    persistedLiveGitPath,
                    out var aliasSources))
            {
                aliasSources = [];
                persistedLiveGitPathsByAlias.Add(persistedLiveGitPath, aliasSources);
            }

            aliasSources.Add(persistedLiveGitPath);
        }

        // Resolve source identities lazily per case-fold bucket. Ordinary modified
        // paths with exact persisted rows therefore pay no filesystem identity I/O,
        // while repeated pathological fold variants still remain O(delta).
        // source identity は実際に参照する fold bucket ごとに遅延解決し、通常の
        // exact-row更新ではidentity I/Oを避けつつ病的variantもO(delta)に保つ。
        var persistedAliasSourcesByIdentity = new Dictionary<
            string,
            Dictionary<FileIndexer.FileIdentity, List<string>>>(
            StringComparer.OrdinalIgnoreCase);

        var persistedCaseAliasSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var liveGitTargetIndexPath in liveGitTargetIndexPaths)
        {
            if (persistedLiveGitPaths.Contains(liveGitTargetIndexPath))
                continue;

            // Case folding only narrows the candidates. On mixed-policy directory
            // trees and case-sensitive filesystems two live spellings may be distinct
            // files, so directly plan only exact persisted rows that resolve to the
            // retained target's filesystem identity.
            // case folding は候補絞り込みに限定し、mixed-policy directory tree
            // でも retained target と filesystem identity が一致する exact row
            // だけを直接 plan する。
            var retainedTarget = UpdateFileTarget.Create(
                projectRoot,
                liveGitTargetIndexPath);
            var hasRetainedIdentity = FileIndexer.TryGetFileIdentity(
                LongPath.EnsureWindowsPrefix(retainedTarget.FilePath),
                out var retainedIdentity);
            var provenAliasSource = false;
            if (hasRetainedIdentity
                && persistedLiveGitPathsByAlias.TryGetValue(
                    liveGitTargetIndexPath,
                    out var candidateAliasSources))
            {
                if (!persistedAliasSourcesByIdentity.TryGetValue(
                        liveGitTargetIndexPath,
                        out var aliasIdentities))
                {
                    aliasIdentities = [];
                    foreach (var candidateAliasSource in candidateAliasSources)
                    {
                        var sourceTarget = UpdateFileTarget.Create(
                            projectRoot,
                            candidateAliasSource);
                        if (!FileIndexer.TryGetFileIdentity(
                                LongPath.EnsureWindowsPrefix(sourceTarget.FilePath),
                                out var sourceIdentity))
                        {
                            continue;
                        }

                        if (!aliasIdentities.TryGetValue(
                                sourceIdentity,
                                out var identitySources))
                        {
                            identitySources = [];
                            aliasIdentities.Add(sourceIdentity, identitySources);
                        }
                        identitySources.Add(candidateAliasSource);
                    }
                    persistedAliasSourcesByIdentity.Add(
                        liveGitTargetIndexPath,
                        aliasIdentities);
                }

                if (aliasIdentities.TryGetValue(retainedIdentity, out var aliasSources))
                {
                    foreach (var aliasSource in aliasSources)
                    {
                        persistedCaseAliasSources.Add(aliasSource);
                        provenAliasSource = true;
                    }
                }
            }

            if (!provenAliasSource)
            {
                csharpPreWorkspaceCleanupTargets.Add((
                    liveGitTargetIndexPath,
                    Checksum: null,
                    IncludeDirectoryAndStem: false));
            }
        }

        if (persistedCaseAliasSources.Count > 0)
        {
            scopedCleanupPlans.Add(
                writer.PlanCSharpFilesInPaths(
                    persistedCaseAliasSources,
                    cancellationToken));
        }

        // Git reports both casings for a case-only rename, while File.Exists sees
        // both paths as the same retained file on an insensitive filesystem. Drop
        // only the exact persisted source spelling from the live update set; the
        // retained spelling remains authoritative and the immutable alias cleanup
        // plan above removes the old row without hashing the file.
        // case-only rename では旧/新 casing の両方が存在扱いになるため、永続化済み
        // source spelling だけを live update set から除き、alias cleanup に委ねる。
        foreach (var persistedCaseAliasSource in persistedCaseAliasSources)
            targetPaths.Remove(persistedCaseAliasSource);
    }

    private static void PlanExplicitUpdateCSharpCleanup(
        DbWriter writer,
        FileIndexer indexer,
        string projectRoot,
        IReadOnlyCollection<string> explicitFileTargetPaths,
        long? maxFileSizeBytes,
        bool projectRootWritten,
        List<(string RetainedRelativePath, string? Checksum, bool IncludeDirectoryAndStem)>
            csharpPreWorkspaceCleanupTargets,
        CancellationToken cancellationToken)
    {
        // A one-sided rename can name only the retained file. Reuse the persisted checksum
        // for caller-selected paths whose filesystem stat is unchanged, and hash only new
        // or stat-changed paths. Turn matching checksum/stem cleanup into an immutable plan
        // before building the C# workspace, including when the retained path already has an
        // indexed row.
        // one-sided rename では retained 側しか指定されないため、caller-selected delta
        // だけ実 checksum を読み、workspace 構築前に cleanup ID を確定する。
        foreach (var targetPath in explicitFileTargetPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cleanupTarget = UpdateFileTarget.Create(projectRoot, targetPath);
            var ioPath = LongPath.EnsureWindowsPrefix(cleanupTarget.FilePath);
            if (!File.Exists(ioPath))
                continue;

            string? checksum = null;
            var includeDirectoryAndStem = false;
            try
            {
                var fileInfo = new FileInfo(ioPath);
                fileInfo.Refresh();
                if (!fileInfo.Exists)
                    continue;

                if (writer.TryGetFileChecksumByStat(
                        cleanupTarget.IndexPath,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc,
                        out checksum,
                        cancellationToken))
                {
                    includeDirectoryAndStem = projectRootWritten;
                }
                else
                {
                    var pathFilter = indexer.EvaluatePathFilter(cleanupTarget.FilePath);
                    if (pathFilter.ShouldSkip || pathFilter.Errors.Any(error => error.IsFatal))
                        continue;

                    var indexability =
                        indexer.GetFileIndexabilityForIndexing(cleanupTarget.FilePath);
                    var detection = indexer.TryDetectLanguageForIndexing(
                        cleanupTarget.FilePath,
                        knownIndexability: indexability);
                    if (indexability == FileIndexer.FileProbeStatus.Supported
                        && detection.Status == FileIndexer.FileProbeStatus.Supported)
                    {
                        UpdateCleanupChecksumReadForTesting?.Invoke(cleanupTarget.IndexPath);
                        if (FileIndexer.TryComputeChecksum(
                                cleanupTarget.FilePath,
                                maxFileSizeBytes ?? FileIndexer.DefaultMaxFileSizeBytes,
                                out var computedChecksum,
                                cancellationToken))
                        {
                            checksum = computedChecksum;
                        }
                        includeDirectoryAndStem = projectRootWritten;
                    }
                    else if (indexability != FileIndexer.FileProbeStatus.ProbeFailed
                             && detection.Status != FileIndexer.FileProbeStatus.ProbeFailed
                             && !writer.HasFileAtPath(cleanupTarget.IndexPath))
                    {
                        // The normal update loop permits only the same-stem cleanup for a
                        // newly unsupported retained target.
                        includeDirectoryAndStem = projectRootWritten;
                    }
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                // The normal update loop reports the read/probe failure. Do not plan a
                // cleanup that the actual target pass would not have reached.
                continue;
            }

            csharpPreWorkspaceCleanupTargets.Add((
                cleanupTarget.IndexPath,
                checksum,
                includeDirectoryAndStem));
        }
    }
}
