using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateFilePersistenceContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required string RelativePath { get; init; }
        internal required string AbsolutePath { get; init; }
        internal required FileRecord Record { get; init; }
        internal required LoadedFileRecord Loaded { get; init; }
        internal FileIssue? GeneratedSuppressionIssue { get; init; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace { get; init; }
        internal required PostExtractionHookRunner PostExtractionHooks { get; init; }
        internal required SymbolExtractionWorkerClient SymbolExtractionWorker { get; init; }
        internal required bool ProjectRootWritten { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action RequireTypeScriptAugmentationRefresh { get; init; }
        internal required Func<string, string?, bool, int> PurgeStaleUpdateCleanupPaths { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action<string?> RecordDynamicGraphFileRefresh { get; init; }
        internal required Action<bool> SetBatchMarkerOwned { get; init; }
        internal required Action<string, string> SetPhase { get; init; }
    }

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

    private static UpdateFilePersistenceResult PersistUpdateFile(
        UpdateFilePersistenceContext context)
    {
        var writer = context.Writer;
        var options = context.Options;
        var record = context.Record;
        var loaded = context.Loaded;
        var cancellationToken = context.CancellationToken;
        var mutualRecursionRefreshNeeded = false;
        var symbolsDroppedByKindFilter = 0;

        writer.MarkBatchInProgress();
        context.SetBatchMarkerOwned(true);
        var recordRequiresTypeScriptAugmentationRefresh = record.Lang == "typescript";
        using var txn = writer.BeginTransaction(cancellationToken, "update file");
        if (recordRequiresTypeScriptAugmentationRefresh)
            context.RequireTypeScriptAugmentationRefresh();
        var stalePurged = context.PurgeStaleUpdateCleanupPaths(
            record.Path,
            record.Checksum,
            context.ProjectRootWritten);
        if (stalePurged > 0)
        {
            context.RequireTypeScriptAugmentationRefresh();
            if (!options.SymbolsOnly)
                mutualRecursionRefreshNeeded = true;
        }
        context.WriteProjectRootOnce();
        var fileId = writer.UpsertFile(record, out var referenceIdentityChanged);
        if (!options.SymbolsOnly && referenceIdentityChanged)
            mutualRecursionRefreshNeeded = true;
        context.SetPhase(FormatIndexPhasePath(context.RelativePath, "chunking"), "chunking");
        var chunks = ChunkSplitter.SplitNormalized(
            fileId,
            loaded.Content,
            loaded.HasOversizeLine,
            record.Lines);
        if (context.GeneratedSuppressionIssue != null)
        {
            writer.InsertChunks(chunks, cancellationToken);
            writer.InsertSymbols([], cancellationToken);
            writer.InsertReferencesInAtomicFileScope(
                [],
                refreshMutualRecursionFlags: false,
                cancellationToken);
            context.SetPhase(
                FormatIndexPhasePath(context.RelativePath, "validating"),
                "validating");
            var generatedIssues = AppendIssueIfMissing(
                FileIndexer.ValidateContent(
                    record.Path,
                    loaded.RawBytes,
                    loaded.Content,
                    record.Lang,
                    loaded.Inspection,
                    loaded.HasOversizeLine,
                    loaded.ConflictMarkerLine),
                context.GeneratedSuppressionIssue);
            writer.InsertIssues(fileId, generatedIssues);
            context.SetPhase(
                FormatIndexPhasePath(context.RelativePath, "committing"),
                "committing");
            writer.ClearBatchInProgress();
            txn.Commit();
            context.SetBatchMarkerOwned(false);
            context.RecordDynamicGraphFileRefresh(record.Lang);
            return new UpdateFilePersistenceResult(
                0,
                mutualRecursionRefreshNeeded,
                $"  [OK  ] {context.RelativePath} ({chunks.Count} chunks, generated-code extraction skipped)");
        }

        context.SetPhase(FormatIndexPhasePath(context.RelativePath, "symbols"), "symbols");
        var symbolExtraction = ExtractSymbolsWithStallTimeout(
            fileId,
            record.Lang,
            loaded.Content,
            context.AbsolutePath,
            context.ProjectRoot,
            record.Path,
            FormatIndexPhasePath(context.RelativePath, "symbols"),
            true,
            loaded.HasOversizeLine,
            loaded.ConflictMarkerLine,
            context.SymbolExtractionWorker,
            cancellationToken);
        var symbols = symbolExtraction.Symbols;
        var symbolRegexTimeoutIssue = symbolExtraction.RegexTimeoutIssue;
        var fileContext = new FileContext(
            context.ProjectRoot,
            record.Path,
            context.AbsolutePath,
            record.Lang);
        var sourceContractSeenBeforeObservation =
            context.PostExtractionHooks.SawCSharpStaticInterfaceSourceContract;
        context.PostExtractionHooks.ObserveCSharpStaticInterfaceSourceSymbols(
            fileContext,
            symbols);
        if (record.Lang == "csharp"
            && !context.CSharpWorkspace.HasSourceStaticInterfaceContracts
            && !sourceContractSeenBeforeObservation
            && context.PostExtractionHooks.SawCSharpStaticInterfaceSourceContract)
        {
            writer.SetCSharpStaticInterfaceSourceEvidence(null);
            throw new CSharpWorkspaceChangedException(
                "A C# static-interface contract appeared after workspace preflight.");
        }
        if (symbols.Count > options.MaxSymbolsPerFile)
        {
            var issue = BuildSymbolCountExceededIssue(
                record.Path,
                symbols.Count,
                options.MaxSymbolsPerFile);
            IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                ? [issue]
                : AppendIssue([symbolRegexTimeoutIssue], issue);
            writer.InsertSymbols([], cancellationToken);
            writer.InsertReferencesInAtomicFileScope(
                [],
                refreshMutualRecursionFlags: false,
                cancellationToken);
            writer.InsertIssues(fileId, capIssues);
            writer.ClearBatchInProgress();
            txn.Commit();
            context.SetBatchMarkerOwned(false);
            context.RecordDynamicGraphFileRefresh(record.Lang);
            return new UpdateFilePersistenceResult(
                0,
                mutualRecursionRefreshNeeded,
                $"  [SKIP] {context.RelativePath} ({issue.Message})");
        }

        var familyScopeKey = context.Indexer.GetFamilyScopeKey(context.AbsolutePath, record.Lang);
        SymbolExtractor.ApplyFamilyScope(symbols, familyScopeKey, record.Lang);
        context.PostExtractionHooks.OnSymbolsExtractedAfterSourceObservation(
            fileContext,
            symbols,
            loaded.Content,
            familyScopeKey);
        symbolsDroppedByKindFilter = options.SymbolKindFilter.Apply(symbols);
        if (symbols.Count > options.MaxSymbolsPerFile)
        {
            var issue = BuildSymbolCountExceededIssue(
                record.Path,
                symbols.Count,
                options.MaxSymbolsPerFile);
            IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                ? [issue]
                : AppendIssue([symbolRegexTimeoutIssue], issue);
            writer.InsertSymbols([], cancellationToken);
            writer.InsertReferencesInAtomicFileScope(
                [],
                refreshMutualRecursionFlags: false,
                cancellationToken);
            writer.InsertIssues(fileId, capIssues);
            writer.ClearBatchInProgress();
            txn.Commit();
            context.SetBatchMarkerOwned(false);
            context.RecordDynamicGraphFileRefresh(record.Lang);
            return new UpdateFilePersistenceResult(
                symbolsDroppedByKindFilter,
                mutualRecursionRefreshNeeded,
                $"  [SKIP] {context.RelativePath} ({issue.Message})");
        }

        writer.InsertChunks(chunks, cancellationToken);
        FileIndexer.ValidateSymbolLineRanges(record, symbols);
        writer.InsertSymbols(symbols, cancellationToken);
        context.SetPhase(
            FormatIndexPhasePath(context.RelativePath, "references"),
            "references");
        List<ReferenceRecord> references;
        FileIssue? referenceRegexTimeoutIssue;
        ReferenceExtractionResult referenceExtraction;
        using (var regexTimeouts = BoundedRegex.CaptureTimeouts(
                   record.Lang,
                   "reference_extraction"))
        {
            referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                fileId,
                record.Lang,
                loaded.Content,
                loaded.HasOversizeLine,
                symbols,
                record.Path,
                record.Lang == "csharp" ? context.CSharpWorkspace.Symbols : null,
                cancellationToken,
                maxReferenceCount: options.MaxReferencesPerFile + 1,
                conflictMarkerLine: loaded.ConflictMarkerLine,
                workspaceRoot: context.ProjectRoot,
                csharpStaticInterfaceMemberLookups:
                    context.CSharpWorkspace.StaticInterfaceMemberLookups,
                csharpQualifiedPatternLookups:
                    context.CSharpWorkspace.QualifiedPatternLookups);
            references = referenceExtraction.References;
            referenceRegexTimeoutIssue =
                BuildRegexTimeoutIssue(record.Path, regexTimeouts);
        }
        context.PostExtractionHooks.OnReferencesExtracted(fileContext, references);
        FileIssue? referenceCapIssue = null;
        if (references.Count > options.MaxReferencesPerFile)
        {
            referenceCapIssue = BuildReferenceCountExceededIssue(
                record.Path,
                references.Count,
                options.MaxReferencesPerFile);
            references = [];
        }
        writer.InsertReferencesInAtomicFileScope(
            references,
            refreshMutualRecursionFlags: false,
            cancellationToken);
        context.SetPhase(
            FormatIndexPhasePath(context.RelativePath, "validating"),
            "validating");
        IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(
            record.Path,
            loaded.RawBytes,
            loaded.Content,
            record.Lang,
            loaded.Inspection,
            loaded.HasOversizeLine,
            loaded.ConflictMarkerLine);
        if (symbolRegexTimeoutIssue != null)
            issues = AppendIssue(issues, symbolRegexTimeoutIssue);
        if (referenceRegexTimeoutIssue != null)
            issues = AppendIssue(issues, referenceRegexTimeoutIssue);
        issues = AppendReferenceExtractionDiagnosticIssues(
            issues,
            record.Path,
            referenceExtraction.Diagnostics);
        if (referenceCapIssue != null)
            issues = AppendIssue(issues, referenceCapIssue);
        writer.InsertIssues(fileId, issues);
        context.SetPhase(
            FormatIndexPhasePath(context.RelativePath, "committing"),
            "committing");
        writer.ClearBatchInProgress();
        txn.Commit();
        context.SetBatchMarkerOwned(false);
        context.RecordDynamicGraphFileRefresh(record.Lang);
        if (!options.SymbolsOnly && (symbols.Count > 0 || references.Count > 0))
            mutualRecursionRefreshNeeded = true;

        return new UpdateFilePersistenceResult(
            symbolsDroppedByKindFilter,
            mutualRecursionRefreshNeeded,
            $"  [OK  ] {context.RelativePath} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
    }

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
            UpdateSkippedFileRecordBuiltForTesting?.Invoke(context.RelativePath);
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
