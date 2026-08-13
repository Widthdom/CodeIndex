using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{

    private sealed record UpdateFilePersistenceResult(
        int SymbolsDroppedByKindFilter,
        bool MutualRecursionRefreshNeeded,
        string VerboseMessage);

    private sealed class SkippedUpdateFilePersistenceContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string AbsolutePath { get; init; }
        internal required string RelativePath { get; init; }
        internal string? KnownLanguage { get; init; }
        internal required bool ProjectRootWritten { get; init; }
        internal required string TransactionName { get; init; }
        internal required string WorkspaceChangedMessage { get; init; }
        internal required FileIssue Issue { get; init; }
        internal required int TargetIndex { get; init; }
        internal required ReadableFileByteTracker ReadableFileBytes { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Func<FileRecord, bool> ValidateSkippedRecord { get; init; }
        internal required Func<string, string?, bool, int> PurgeStaleUpdateCleanupPaths { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action<string?> RecordDynamicGraphFileRefresh { get; init; }
    }

    private sealed record SkippedUpdateFilePersistenceResult(
        bool MutualRecursionRefreshNeeded);


    private static SkippedUpdateFilePersistenceResult PersistSkippedUpdateFile(
        SkippedUpdateFilePersistenceContext context)
    {
        var writer = context.Writer;
        var cancellationToken = context.CancellationToken;
        var mutualRecursionRefreshNeeded = false;

        writer.MarkBatchInProgress();
        var batchMarkerOwned = true;
        try
        {
            using var txn = writer.BeginTransaction(
                cancellationToken,
                context.TransactionName);
            var skippedRecord = context.Indexer.BuildSkippedFileRecord(
                context.AbsolutePath,
                context.RelativePath,
                context.KnownLanguage);
            UpdateSkippedFileRecordBuiltForTesting?.Invoke(
                context.RelativePath,
                context.KnownLanguage);
            if (!context.ValidateSkippedRecord(skippedRecord))
            {
                throw new CSharpWorkspaceChangedException(
                    context.WorkspaceChangedMessage);
            }
            context.ReadableFileBytes.Remember(
                context.TargetIndex,
                skippedRecord.Size);
            var stalePurged = context.PurgeStaleUpdateCleanupPaths(
                skippedRecord.Path,
                skippedRecord.Checksum,
                context.ProjectRootWritten);
            if (skippedRecord.Lang == "typescript" || stalePurged > 0)
                context.RequireTypeScriptAugmentationRefresh();
            if (!context.Options.SymbolsOnly && stalePurged > 0)
                mutualRecursionRefreshNeeded = true;
            context.WriteProjectRootOnce();
            var fileId = writer.UpsertFile(
                skippedRecord,
                out var referenceIdentityChanged);
            if (!context.Options.SymbolsOnly && referenceIdentityChanged)
                mutualRecursionRefreshNeeded = true;
            writer.InsertChunks([], cancellationToken);
            writer.InsertSymbols([], cancellationToken);
            writer.InsertReferencesInAtomicFileScope(
                [],
                refreshMutualRecursionFlags: false,
                cancellationToken);
            writer.InsertIssues(fileId, [context.Issue]);
            writer.ClearBatchInProgress();
            txn.Commit();
            context.RecordDynamicGraphFileRefresh(skippedRecord.Lang);
            batchMarkerOwned = false;
        }
        finally
        {
            if (batchMarkerOwned)
                writer.ClearBatchInProgress();
        }

        return new SkippedUpdateFilePersistenceResult(
            mutualRecursionRefreshNeeded);
    }
}
