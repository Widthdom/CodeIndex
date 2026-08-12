using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private enum McpSkippedFileKind
    {
        Binary,
        Oversized,
    }

    private void HandleMcpSkippedFile(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        in CSharpStaticInterfacePrepass.FileTarget target,
        FileIndexer.BinaryFileSkippedException exception)
    {
        PersistMcpSkippedFile(
            context,
            session,
            in target,
            McpSkippedFileKind.Binary,
            exception);
    }

    private void HandleMcpSkippedFile(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        in CSharpStaticInterfacePrepass.FileTarget target,
        FileIndexer.FileTooLargeSkippedException exception)
    {
        PersistMcpSkippedFile(
            context,
            session,
            in target,
            McpSkippedFileKind.Oversized,
            exception);
    }

    private void PersistMcpSkippedFile(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        in CSharpStaticInterfacePrepass.FileTarget target,
        McpSkippedFileKind kind,
        Exception exception)
    {
        try
        {
            var record = context.Indexer.BuildSkippedFileRecord(
                target.FilePath,
                target.RelativePath,
                target.Language);
            context.RememberReadableFileSize(target.FilePath, record.Size);
            if (!context.LoadedCSharpWorkspaceSnapshotMatches(in target, record))
            {
                session.Skipped++;
                return;
            }
            if (record.Lang == "csharp")
                session.CSharpMetadataTargetsNeedRefresh = true;
            using var txn = context.Writer.BeginTransaction(
                context.CancellationToken,
                GetMcpSkippedFileTransactionOperation(kind));
            if (record.Lang == "typescript")
                context.RequireTypeScriptAugmentationRefresh();
            var referenceIdentityChanged = false;
            var fileId = context.StartedWithNoIndexedFiles
                ? context.Writer.InsertNewFile(record)
                : context.Writer.UpsertFile(record, out referenceIdentityChanged);
            if (referenceIdentityChanged)
                session.MutualRecursionRefreshNeeded = true;
            context.Writer.InsertChunks([], context.CancellationToken);
            context.Writer.InsertSymbols([], context.CancellationToken);
            context.Writer.InsertReferencesInAtomicFileScope([], context.CancellationToken);
            context.InsertIssuesForIndexedFile(
                fileId,
                BuildMcpSkippedFileIssues(kind, exception));
            context.WriteProjectRootOnce();
            txn.Commit();
            if (!string.IsNullOrWhiteSpace(record.Lang))
                session.IndexedSymbolExtractorLanguages.Add(record.Lang);
            CountFreshMcpIndexRows(context, session);
            session.FtsMutated = true;
        }
        catch (Exception cleanupException) when (
            cleanupException is not McpIndexAuthorizationException)
        {
            context.Failures.Add(BuildIndexFileFailure(
                context.ProjectPath,
                target.FilePath,
                cleanupException,
                GetMcpSkippedFileFailureStage(kind)));
        }
    }

    private static IReadOnlyList<FileIssue> BuildMcpSkippedFileIssues(
        McpSkippedFileKind kind,
        Exception exception)
    {
        return kind switch
        {
            McpSkippedFileKind.Binary =>
                [IndexCommandRunner.BuildNullByteIssue(
                    (FileIndexer.BinaryFileSkippedException)exception)],
            McpSkippedFileKind.Oversized =>
            [
                new FileIssue
                {
                    Path = ((FileIndexer.FileTooLargeSkippedException)exception).RelativePath,
                    Kind = "file_too_large",
                    Line = 0,
                    Message = CommandErrorWriter.FormatSanitizedExceptionMessage(exception),
                }
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string GetMcpSkippedFileTransactionOperation(McpSkippedFileKind kind)
        => kind switch
        {
            McpSkippedFileKind.Binary => "mcp index skipped binary",
            McpSkippedFileKind.Oversized => "mcp index skipped oversized file",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string GetMcpSkippedFileFailureStage(McpSkippedFileKind kind)
        => kind switch
        {
            McpSkippedFileKind.Binary => "record_skipped_binary",
            McpSkippedFileKind.Oversized => "record_skipped_oversized_file",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private async Task<bool> HandleMissingMcpIndexFileAsync(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        int targetIndex)
    {
        var target = context.Targets[targetIndex];
        if (session.FileBatchMarked)
            context.Writer.ClearBatchInProgress();

        if (target.Language == "csharp" && context.HasCSharpWorkspaceSnapshots())
        {
            context.DeferCSharpLoadedSnapshotDrift(target.DisplayRelativePath);
            session.Skipped++;
            session.Processed++;
            await EmitProgressNotificationAsync(
                    context.ProgressToken,
                    session.Processed,
                    context.TotalFileCount)
                .ConfigureAwait(false);
            return true;
        }

        try
        {
            var relativePath = FileIndexer.NormalizePathSeparators(
                FileIndexer.GetRelativePathFromDirectory(
                    context.ProjectPath,
                    target.FilePath));
            if (context.Writer.HasFileAtPath(relativePath))
            {
                using var txn = context.Writer.BeginTransaction(
                    context.CancellationToken,
                    "mcp index delete missing file");
                context.Writer.DeleteFileByPath(relativePath);
                session.MutualRecursionRefreshNeeded = true;
                session.CSharpMetadataTargetsNeedRefresh = true;
                context.RequireTypeScriptAugmentationRefresh();
                context.WriteProjectRootOnce();
                txn.Commit();
                session.FtsMutated = true;
            }
        }
        catch (Exception cleanupException)
        {
            context.Failures.Add(BuildIndexFileFailure(
                context.ProjectPath,
                target.FilePath,
                cleanupException,
                "delete_missing_file"));
        }
        return false;
    }
}
