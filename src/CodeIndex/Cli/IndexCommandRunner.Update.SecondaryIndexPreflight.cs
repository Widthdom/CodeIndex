using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    internal static bool ShouldCountPathFilteredUpdateTargetAsMutating(
        FileIndexer.PathFilterResult pathFilter)
        => pathFilter.ShouldSkip && pathFilter.ShouldDeleteExisting;

    private static bool ShouldUseUpdateSecondaryIndexStagingAfterStatPreflight(
        DbWriter writer,
        FileIndexer indexer,
        IndexCommandOptions options,
        string projectRoot,
        IReadOnlyCollection<string> targetPaths,
        int indexedFileCount,
        CSharpStaticInterfaceWorkspaceSymbols csharpWorkspace,
        bool symbolKindFilterMatchesPrior,
        bool csharpSymbolNameContractMatchesCurrent,
        bool sqlGraphContractMatchesCurrent,
        bool hdlGraphContractMatchesCurrent,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = new UpdateFileTarget[targetPaths.Count];
            var includedPaths = new HashSet<string>(
                targetPaths.Count,
                StringComparer.Ordinal);
            var targetIndex = 0;
            foreach (var targetPath in targetPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = UpdateFileTarget.Create(projectRoot, targetPath);
                targets[targetIndex++] = target;
                includedPaths.Add(target.IndexPath);
            }

            var reusableFiles = writer.LoadReusableIndexedFileStats(
                options.MaxSymbolsPerFile,
                options.MaxReferencesPerFile,
                cancellationToken,
                initialCapacity: includedPaths.Count,
                includedPaths: includedPaths,
                maxFileSizeBytes:
                    options.MaxFileSizeBytes
                    ?? FileIndexer.DefaultMaxFileSizeBytes);
            var visitedFileIdentities =
                new HashSet<FileIndexer.FileIdentity>();
            var estimatedMutatingTargetCount = 0;
            bool RecordEstimatedMutatingTarget()
            {
                estimatedMutatingTargetCount++;
                return ShouldUseUpdateReferenceSecondaryIndexBulkLoad(
                    estimatedMutatingTargetCount,
                    indexedFileCount);
            }

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathFilter = indexer.EvaluatePathFilter(target.FilePath);
                if (pathFilter.Errors.Any())
                    return true;
                if (pathFilter.ShouldSkip)
                {
                    if (ShouldCountPathFilteredUpdateTargetAsMutating(pathFilter)
                        && RecordEstimatedMutatingTarget())
                    {
                        return true;
                    }
                    continue;
                }

                var indexability =
                    indexer.GetFileIndexabilityForIndexing(target.FilePath);
                var detection = indexer.TryDetectLanguageForIndexing(
                    target.FilePath,
                    knownIndexability: indexability);
                if (indexability == FileIndexer.FileProbeStatus.ProbeFailed
                    || detection.Status == FileIndexer.FileProbeStatus.ProbeFailed)
                {
                    return true;
                }
                if (indexability != FileIndexer.FileProbeStatus.Supported
                    || detection.Status
                        != FileIndexer.FileProbeStatus.Supported)
                {
                    if (RecordEstimatedMutatingTarget())
                        return true;
                    continue;
                }

                if (FileIndexer.TryGetFileIdentity(
                        target.FilePath,
                        out var identity,
                        out var linkCount)
                    && linkCount > 1
                    && !visitedFileIdentities.Add(identity))
                {
                    if (RecordEstimatedMutatingTarget())
                        return true;
                    continue;
                }

                var language = GetStatReusableLanguage(
                    target.FilePath,
                    detection);
                var generatedExtractionSuppressed =
                    indexer.IsGeneratedCodeExtractionSuppressed(
                        target.IndexPath);
                var allowReuse = symbolKindFilterMatchesPrior
                    && (language != "csharp"
                        || csharpSymbolNameContractMatchesCurrent)
                    && (language != "csharp"
                        || !csharpWorkspace.HasStaticInterfaceContracts)
                    && (language != "sql"
                        || sqlGraphContractMatchesCurrent)
                    && (language is not (
                        "verilog" or "systemverilog" or "vhdl")
                        || hdlGraphContractMatchesCurrent);
                if (!allowReuse
                    || IndexedFileStatReuse.TryGetReusableUnchangedFile(
                        reusableFiles,
                        target.FilePath,
                        target.IndexPath,
                        language,
                        generatedExtractionSuppressed) == null)
                {
                    if (RecordEstimatedMutatingTarget())
                        return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // This is only a bulk-staging optimization. Any preflight uncertainty keeps
            // staging enabled and lets the authoritative per-file loop report the error.
            return true;
        }
    }
}
