using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private readonly record struct McpIndexCommittedRows(
        int Chunks,
        int Symbols,
        int References);

    private async Task<bool> ProcessMcpIndexFileAsync(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        int targetIndex)
    {
        var target = context.Targets[targetIndex];
        context.CancellationToken.ThrowIfCancellationRequested();
        context.AuthorizedRoot.EnsureAuthorizedEntry(target.FilePath);
        if (context.CSharpWorkspace.DeferMutations
            && target.Language == "csharp")
        {
            TryRememberMcpIndexFileSize(context, target.FilePath);
            return await CompleteMcpIndexFileEarlyAsync(context, session, emitProgress: true)
                .ConfigureAwait(false);
        }

        var statMatchedFile = ResolveMcpStatMatch(context, targetIndex, in target);
        if (context.CSharpWorkspace.PreservePriorPositiveSourceNoOp
            && target.Language == "csharp"
            && statMatchedFile == null)
        {
            context.CSharpWorkspace.DeferForStatRevalidation(in target, context.Writer);
            return await CompleteMcpIndexFileEarlyAsync(context, session, emitProgress: true)
                .ConfigureAwait(false);
        }
        if (statMatchedFile != null)
        {
            context.RememberReadableFileSize(target.FilePath, statMatchedFile.Value.Size);
            RememberReusedHotspotFamilyLanguage(session, target.Language);
            return await CompleteMcpIndexFileEarlyAsync(context, session, emitProgress: true)
                .ConfigureAwait(false);
        }

        McpIndexFileContentLoadForTesting?.Invoke(target.IndexPath);
        var loaded = context.Indexer.BuildLoadedRecordWithRawBytes(
            target.FilePath,
            target.RelativePath,
            target.Language,
            context.CancellationToken);
        var record = loaded.Record;
        if (!context.CSharpWorkspace.LoadedSnapshotMatches(
                in target,
                record,
                context.Writer))
        {
            context.RememberReadableFileSize(target.FilePath, record.Size);
            return await CompleteMcpIndexFileEarlyAsync(context, session, emitProgress: true)
                .ConfigureAwait(false);
        }
        context.RememberReadableFileSize(target.FilePath, record.Size);

        var generatedSuppressionIssue = target.GeneratedExtractionSuppressed == true
            ? context.Indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
            : null;
        if (CanReuseLoadedMcpIndexFile(context, record, generatedSuppressionIssue != null))
        {
            RememberReusedHotspotFamilyLanguage(session, record.Lang);
            return await CompleteMcpIndexFileEarlyAsync(context, session, emitProgress: false)
                .ConfigureAwait(false);
        }

        if (!context.UseFullRunBatchMarker)
        {
            context.Writer.MarkBatchInProgress();
            session.FileBatchMarked = true;
        }
        context.MarkSymbolKindFilterMetaIncompleteOnce();
        if (record.Lang == "csharp")
            session.CSharpMetadataTargetsNeedRefresh = true;
        using var txn = context.Writer.BeginTransaction(
            context.CancellationToken,
            "mcp index file");
        if (record.Lang == "typescript")
            context.RequireTypeScriptAugmentationRefresh();
        var referenceIdentityChanged = false;
        var fileId = context.StartedWithNoIndexedFiles
            ? context.Writer.InsertNewFile(record)
            : context.Writer.UpsertFile(record, out referenceIdentityChanged);
        if (referenceIdentityChanged)
            session.MutualRecursionRefreshNeeded = true;
        var chunks = ChunkSplitter.SplitNormalized(fileId, loaded.Content, loaded.Facts);

        if (generatedSuppressionIssue != null)
        {
            PersistGeneratedMcpIndexFile(
                context,
                session,
                record,
                in loaded,
                fileId,
                chunks,
                generatedSuppressionIssue,
                txn);
            session.Processed++;
            await EmitProgressNotificationAsync(
                    context.ProgressToken,
                    session.Processed,
                    context.TotalFileCount)
                .ConfigureAwait(false);
            McpIndexFileCommittedForTesting?.Invoke(record.Path);
            return true;
        }

        PersistExtractedMcpIndexFile(
            context,
            session,
            in target,
            record,
            in loaded,
            fileId,
            chunks,
            txn);
        McpIndexFileCommittedForTesting?.Invoke(record.Path);
        return false;
    }

    private static IndexedFileStatReuseResult? ResolveMcpStatMatch(
        McpIndexFileLoopContext context,
        int targetIndex,
        in CSharpStaticInterfacePrepass.FileTarget target)
    {
        if (context.StatPreflightCompleted == null)
            return null;
        if (!context.StatPreflightCompleted[targetIndex])
            return context.GetStatMatchedFile(in target);

        var cached = context.StatMatchedFiles![targetIndex];
        if (cached == null)
            return null;
        return context.ReuseFinalCSharpStatAtActualSkip && target.Language == "csharp"
            ? cached
            : IndexedFileStatReuse.TryGetReusableUnchangedFile(
                context.ReusableIndexedFileStats!,
                target.FilePath,
                target.IndexPath,
                target.Language,
                target.GeneratedExtractionSuppressed == true);
    }

    private static bool CanReuseLoadedMcpIndexFile(
        McpIndexFileLoopContext context,
        FileRecord record,
        bool generatedExtractionSuppressed)
    {
        return context.Writer.GetReusableUnchangedFileId(
            record.Path,
            record.Modified,
            record.Checksum,
            size: record.Size,
            lines: record.Lines,
            language: record.Lang,
            generated: record.Generated,
            maxSymbolsPerFile: context.MaxSymbolsPerFile,
            maxReferencesPerFile: context.MaxReferencesPerFile,
            generatedExtractionSuppressed: generatedExtractionSuppressed,
            allowReuse: !context.Rebuild
                && !context.StartedWithNoIndexedFiles
                && context.SymbolKindFilterMatchesPrior
                && (record.Lang != "csharp" || context.CSharpIndexedProjectRootCompatible)
                && (record.Lang != "csharp" || context.CSharpSymbolNameContractMatchesCurrent)
                && (record.Lang != "csharp" || !context.CSharpWorkspace.Workspace.HasStaticInterfaceContracts)
                && (record.Lang != "sql" || context.SqlGraphContractMatchesCurrent)
                && (record.Lang is not ("verilog" or "systemverilog" or "vhdl")
                    || context.HdlGraphContractMatchesCurrent)
                && AllowReuseWithCurrentHotspotFamilyTrust(
                    record.Lang,
                    context.HotspotFamilyTrustMatchesCurrent)) != null;
    }

    private static void PersistMcpSymbolCap(
        McpIndexFileLoopContext context,
        FileRecord record,
        long fileId,
        int symbolCount,
        FileIssue? symbolRegexTimeoutIssue)
    {
        var issue = BuildMcpSymbolCountExceededIssue(
            record.Path,
            symbolCount,
            context.MaxSymbolsPerFile);
        IReadOnlyList<FileIssue> issues = symbolRegexTimeoutIssue == null
            ? [issue]
            : IndexCommandRunner.AppendIssue([symbolRegexTimeoutIssue], issue);
        context.Writer.InsertSymbols([], context.CancellationToken);
        context.Writer.InsertReferencesInAtomicFileScope([], context.CancellationToken);
        context.InsertIssuesForIndexedFile(fileId, issues);
    }

    private static void PersistGeneratedMcpIndexFile(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        FileRecord record,
        in LoadedFileRecord loaded,
        long fileId,
        IReadOnlyList<ChunkRecord> chunks,
        FileIssue generatedSuppressionIssue,
        DbWriter.TransactionScope txn)
    {
        context.Writer.InsertChunks(chunks, context.CancellationToken);
        context.Writer.InsertSymbols([], context.CancellationToken);
        context.Writer.InsertReferencesInAtomicFileScope([], context.CancellationToken);
        var issues = IndexCommandRunner.AppendIssueIfMissing(
            FileIndexer.ValidateContent(
                record.Path,
                loaded.RawBytes,
                loaded.Content,
                record.Lang,
                loaded.Inspection,
                loaded.Facts),
            generatedSuppressionIssue);
        context.InsertIssuesForIndexedFile(fileId, issues);
        CompleteMcpIndexFileTransaction(context, session, record, txn, chunks.Count, 0, 0);
    }

    private static void CompleteMcpIndexFileTransaction(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        FileRecord record,
        DbWriter.TransactionScope txn,
        int chunks,
        int symbols,
        int references)
    {
        context.WriteProjectRootOnce();
        if (!context.UseFullRunBatchMarker)
            context.Writer.ClearBatchInProgress();
        txn.Commit();
        if (!string.IsNullOrWhiteSpace(record.Lang))
            session.IndexedSymbolExtractorLanguages.Add(record.Lang);
        CountFreshMcpIndexRows(context, session, chunks, symbols, references);
        session.FtsMutated = true;
    }

}
