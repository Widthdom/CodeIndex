using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private delegate IndexedFileStatReuseResult? McpIndexStatMatchResolver(
        in FileIndexer.IndexingFileTarget target);

    private sealed class McpIndexFileLoopContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required McpPathBoundary.IndexRootAuthorization AuthorizedRoot { get; init; }
        internal required FileIndexer.IndexingFileTargetCollection Targets { get; init; }
        internal required string ProjectPath { get; init; }
        internal required int TotalFileCount { get; init; }
        internal JsonNode? ProgressToken { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required int MaxSymbolsPerFile { get; init; }
        internal required int MaxReferencesPerFile { get; init; }
        internal required FileIndexer.SymlinkPolicy SymlinkPolicy { get; init; }
        internal required bool Rebuild { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool UseFullRunBatchMarker { get; init; }
        internal required bool ReuseFinalCSharpStatAtActualSkip { get; init; }
        internal required bool SymbolKindFilterMatchesPrior { get; init; }
        internal required bool CSharpIndexedProjectRootCompatible { get; init; }
        internal required bool CSharpSymbolNameContractMatchesCurrent { get; init; }
        internal required bool SqlGraphContractMatchesCurrent { get; init; }
        internal required bool HdlGraphContractMatchesCurrent { get; init; }
        internal required IReadOnlyDictionary<string, bool> HotspotFamilyTrustMatchesCurrent { get; init; }
        internal ReusableIndexedFileStatsSnapshot? ReusableIndexedFileStats { get; init; }
        internal IndexedFileStatReuseResult?[]? StatMatchedFiles { get; init; }
        internal bool[]? StatPreflightCompleted { get; init; }
        internal required SymbolKindFilter SymbolKindFilter { get; init; }
        internal required IndexCommandRunner.LazyDisposable<PostExtractionHookRunner> PostExtractionHooks { get; init; }
        internal required McpIndexCSharpWorkspaceState CSharpWorkspace { get; init; }
        internal required McpIndexStatMatchResolver GetStatMatchedFile { get; init; }
        internal required Action<string, long> RememberReadableFileSize { get; init; }
        internal required Action<long, IReadOnlyList<FileIssue>> InsertIssuesForIndexedFile { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action MarkSymbolKindFilterMetaIncompleteOnce { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh { get; init; }
        internal required List<IndexFileFailure> Failures { get; init; }
    }

    private sealed class McpIndexFileLoopSession
    {
        internal int Processed { get; set; }
        internal int Skipped { get; set; }
        internal bool FileBatchMarked { get; set; }
        internal bool FtsMutated { get; set; }
        internal bool MutualRecursionRefreshNeeded { get; set; }
        internal bool CSharpMetadataTargetsNeedRefresh { get; set; }
        internal int SymbolsDroppedByKindFilter { get; set; }
        internal HashSet<string>? ReusedHotspotFamilyLanguages { get; set; }
        internal required HashSet<string> IndexedSymbolExtractorLanguages { get; init; }
        internal long FreshCountFiles { get; set; }
        internal long FreshCountChunks { get; set; }
        internal long FreshCountSymbols { get; set; }
        internal long FreshCountReferences { get; set; }
    }

    private async Task RunMcpIndexFileLoopAsync(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session)
    {
        for (var targetIndex = 0; targetIndex < context.Targets.Length; targetIndex++)
        {
            session.FileBatchMarked = false;
            try
            {
                if (await ProcessMcpIndexFileAsync(context, session, targetIndex)
                        .ConfigureAwait(false))
                    continue;
            }
            catch (McpIndexAuthorizationException)
            {
                if (session.FileBatchMarked || context.UseFullRunBatchMarker)
                    context.Writer.ClearBatchInProgress();
                throw;
            }
            catch (FileIndexer.BinaryFileSkippedException ex)
            {
                var target = context.Targets[targetIndex];
                HandleMcpSkippedFile(context, session, in target, ex);
            }
            catch (FileIndexer.FileTooLargeSkippedException ex)
            {
                var target = context.Targets[targetIndex];
                HandleMcpSkippedFile(context, session, in target, ex);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                if (await HandleMissingMcpIndexFileAsync(context, session, targetIndex)
                        .ConfigureAwait(false))
                    continue;
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                if (session.FileBatchMarked || context.UseFullRunBatchMarker)
                    context.Writer.ClearBatchInProgress();
                throw;
            }
            catch (Exception ex)
            {
                if (session.FileBatchMarked)
                    context.Writer.ClearBatchInProgress();
                context.Failures.Add(BuildIndexFileFailure(
                    context.ProjectPath,
                    context.Targets[targetIndex].FilePath,
                    ex,
                    "index_file"));
            }

            session.Processed++;
            await EmitProgressNotificationAsync(
                    context.ProgressToken,
                    session.Processed,
                    context.TotalFileCount)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> CompleteMcpIndexFileEarlyAsync(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        bool emitProgress)
    {
        session.Skipped++;
        session.Processed++;
        if (emitProgress)
        {
            await EmitProgressNotificationAsync(
                    context.ProgressToken,
                    session.Processed,
                    context.TotalFileCount)
                .ConfigureAwait(false);
        }
        return true;
    }

    private static void CountFreshMcpIndexRows(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        int chunkCount = 0,
        int symbolCount = 0,
        int referenceCount = 0)
    {
        if (!context.StartedWithNoIndexedFiles)
            return;

        session.FreshCountFiles++;
        session.FreshCountChunks += chunkCount;
        session.FreshCountSymbols += symbolCount;
        session.FreshCountReferences += referenceCount;
    }

    private static void RememberReusedHotspotFamilyLanguage(
        McpIndexFileLoopSession session,
        string? language)
    {
        if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(language) || language == null)
            return;
        session.ReusedHotspotFamilyLanguages ??= new HashSet<string>(StringComparer.Ordinal);
        session.ReusedHotspotFamilyLanguages.Add(language);
    }

    private static void TryRememberMcpIndexFileSize(
        McpIndexFileLoopContext context,
        string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length >= 0)
                context.RememberReadableFileSize(path, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
        }
    }
}
