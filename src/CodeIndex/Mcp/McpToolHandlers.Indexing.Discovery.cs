using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private sealed record McpIndexTargetSet(FileIndexer.IndexingFileTargetCollection All,
        List<CSharpStaticInterfacePrepass.FileTarget> CSharp);

    private sealed record McpIndexDiscoveryPlan(
        FilePurgePlan PurgePlan,
        IReadOnlySet<string>? RetainedPathsForReuse,
        bool HadCSharpStaticInterfaceContractsBeforePurge,
        McpIndexScanAuthority ScanAuthority);

    private sealed record McpIndexScanAuthority(
        string ProjectPath,
        IReadOnlyDictionary<string, string> FileLanguages,
        HashSet<string>? RetainedPaths,
        HashSet<string>? ListedDirectories,
        HashSet<string>? AuthoritativeSubtreeDirectories,
        HashSet<string>? ExplicitlyRemovedPaths,
        bool HadErrors)
    {
        internal bool IsExistingCSharpSymbolPathNowNonCSharp(string indexPath)
        {
            var normalizedIndexPath = FileIndexer.NormalizeIndexPath(indexPath);
            var currentPath = Path.Combine(
                ProjectPath,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(normalizedIndexPath));
            if (FileLanguages.TryGetValue(currentPath, out var currentLanguage))
                return currentLanguage != "csharp";

            if (RetainedPaths?.Contains(normalizedIndexPath) == true)
                return false;
            if (!HadErrors || ExplicitlyRemovedPaths?.Contains(normalizedIndexPath) == true)
                return true;

            var directory = GetIndexParentDirectory(normalizedIndexPath);
            if (ListedDirectories?.Contains(directory) == true)
                return true;
            while (true)
            {
                if (AuthoritativeSubtreeDirectories?.Contains(directory) == true)
                    return true;
                if (directory.Length == 0)
                    return false;
                directory = GetIndexParentDirectory(directory);
            }
        }
    }

    private static FilePurgePlan PlanInitialMcpIndexPurge(
        DbWriter writer,
        string projectPath,
        bool startedWithNoIndexedFiles,
        CancellationToken cancellationToken)
    {
        var plan = startedWithNoIndexedFiles
            ? FilePurgePlan.Empty
            : writer.PlanStaleFiles(projectPath, cancellationToken: cancellationToken);
        McpIndexStaleFilePurgePlannedForTesting?.Invoke(plan.Count);
        return plan;
    }

    private static McpIndexTargetSet BuildMcpIndexTargetSet(
        FileIndexer indexer,
        FileIndexer.ScanFilesResult scanResult,
        FileIndexer.IndexingFileTargetCollection indexingTargets)
    {
        var csharp = new List<CSharpStaticInterfacePrepass.FileTarget>(
            scanResult.LanguageCounts.GetValueOrDefault("csharp"));
        foreach (var target in indexingTargets)
        {
            if (target.ReusableLanguage != "csharp")
                continue;

            csharp.Add(new CSharpStaticInterfacePrepass.FileTarget(
                target.FilePath,
                target.RelativePath,
                target.DisplayRelativePath,
                target.IndexPath,
                target.ReusableLanguage,
                target.GeneratedExtractionSuppressed,
                indexer.ResolvesSymlinkTargets));
        }
        return new McpIndexTargetSet(indexingTargets, csharp);
    }

    private static McpIndexDiscoveryPlan BuildMcpIndexDiscoveryPlan(
        DbWriter writer,
        string projectPath,
        FileIndexer.ScanFilesResult scanResult,
        McpIndexTargetSet targets,
        FilePurgePlan initialPurgePlan,
        bool startedWithNoIndexedFiles,
        bool deferCSharpMutations,
        bool? csharpSourceEvidence,
        bool priorFilterRetainedCSharpContracts,
        CancellationToken cancellationToken)
    {
        var authority = BuildMcpIndexScanAuthority(
            projectPath,
            scanResult,
            targets,
            startedWithNoIndexedFiles);
        var purgePlan = RefineMcpIndexPurgePlan(
            writer,
            authority,
            initialPurgePlan,
            cancellationToken);
        if (deferCSharpMutations && purgePlan.Count > 0)
            purgePlan = FilePurgePlan.Empty;

        var hadCSharpContracts = !startedWithNoIndexedFiles
            && purgePlan.Count > 0
            && writer.HasCSharpFilesInFileIds(purgePlan.FileIds, cancellationToken)
            && (csharpSourceEvidence == true
                || writer.HasCSharpStaticInterfaceContractMembersInFileIds(
                    purgePlan.FileIds,
                    includeInterfaceDeclarationsAsConservativeEvidence:
                        csharpSourceEvidence == null
                        || !priorFilterRetainedCSharpContracts,
                    cancellationToken));
        var retainedPathsForReuse = SelectMcpIndexRetainedPathsForReuse(
            purgePlan,
            authority,
            targets.All.Count,
            startedWithNoIndexedFiles);
        return new McpIndexDiscoveryPlan(
            purgePlan,
            retainedPathsForReuse,
            hadCSharpContracts,
            authority);
    }

    private static McpIndexScanAuthority BuildMcpIndexScanAuthority(
        string projectPath,
        FileIndexer.ScanFilesResult scanResult,
        McpIndexTargetSet targets,
        bool startedWithNoIndexedFiles)
    {
        if (startedWithNoIndexedFiles)
            return new(projectPath, scanResult.FileLanguages, null, null, null, null, scanResult.HadErrors);

        var retainedPaths = new HashSet<string>(targets.All.Count, StringComparer.Ordinal);
        foreach (var target in targets.All)
            retainedPaths.Add(target.IndexPath);
        if (!scanResult.HadErrors)
            return new(projectPath, scanResult.FileLanguages, retainedPaths, null, null, null, false);

        retainedPaths.UnionWith(
            scanResult.ProbeFailedFilePaths.Select(FileIndexer.NormalizeIndexPath));
        var listedDirectories = scanResult.ListedDirectories
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);
        var authoritativeDirectories = scanResult.FullyScannedDirectories
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);
        authoritativeDirectories.UnionWith(
            scanResult.AttributePrunedDirectories.Select(FileIndexer.NormalizeIndexPath));
        authoritativeDirectories.UnionWith(
            scanResult.NestedRepositories.Select(FileIndexer.NormalizeIndexPath));
        var explicitlyRemovedPaths = scanResult.NonIndexablePaths
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);
        return new(
            projectPath,
            scanResult.FileLanguages,
            retainedPaths,
            listedDirectories,
            authoritativeDirectories,
            explicitlyRemovedPaths,
            true);
    }

    private static FilePurgePlan RefineMcpIndexPurgePlan(
        DbWriter writer,
        McpIndexScanAuthority authority,
        FilePurgePlan initialPurgePlan,
        CancellationToken cancellationToken)
    {
        if (authority.RetainedPaths == null)
            return initialPurgePlan;

        // Partial scans authorize only listed children and fully scanned/pruned subtrees.
        // Preserve pre-scan IDs for paths that reappear; prefer a containing scan plan so
        // its deleted-byte estimate is not double counted by Merge.
        var scanPurgePlan = !authority.HadErrors
            || authority.AuthoritativeSubtreeDirectories!.Contains(string.Empty)
                ? writer.PlanFilesOutsideRetainedSet(
                    authority.RetainedPaths,
                    cancellationToken)
                : writer.PlanFilesOutsideRetainedSetWithinListedDirectories(
                    authority.RetainedPaths,
                    authority.ListedDirectories!,
                    authority.AuthoritativeSubtreeDirectories,
                    authority.ExplicitlyRemovedPaths!,
                    cancellationToken);
        if (scanPurgePlan.Count == 0)
            return initialPurgePlan;
        return ContainsEveryMcpIndexPurgeId(scanPurgePlan, initialPurgePlan)
            ? scanPurgePlan
            : FilePurgePlan.Merge([initialPurgePlan, scanPurgePlan]);
    }

    private static bool ContainsEveryMcpIndexPurgeId(FilePurgePlan candidate, FilePurgePlan required)
    {
        if (required.Count > candidate.Count)
            return false;
        foreach (var fileId in required.FileIds)
        {
            if (!FilePurgePlan.ContainsSortedFileId(candidate.FileIds, fileId))
                return false;
        }
        return true;
    }

    private static IReadOnlySet<string>? SelectMcpIndexRetainedPathsForReuse(
        FilePurgePlan purgePlan,
        McpIndexScanAuthority authority,
        long targetCount,
        bool startedWithNoIndexedFiles)
    {
        if (startedWithNoIndexedFiles
            || purgePlan.RemainingFileCount - targetCount <= targetCount)
        {
            return null;
        }

        McpIndexRetainedPathFilterAllocatedForTesting?.Invoke((int)targetCount);
        return authority.RetainedPaths;
    }

    private static string GetIndexParentDirectory(string path) =>
        path.LastIndexOf('/') is var separatorIndex && separatorIndex >= 0 ? path[..separatorIndex] : string.Empty;
}
