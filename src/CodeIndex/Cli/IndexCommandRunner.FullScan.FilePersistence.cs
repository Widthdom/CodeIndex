using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanFilePersistenceContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required FullScanFileWorkItem Item { get; init; }
        internal required FileRecord Record { get; init; }
        internal FileIssue? GeneratedSuppressionIssue { get; init; }
        internal required bool StartedWithNoIndexedFiles { get; init; }
        internal required bool DeferCSharpMutationsForIncompleteScan { get; init; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace { get; init; }
        internal required PostExtractionHookRunner PostExtractionHooks { get; init; }
        internal required SymbolExtractionWorkerClient SymbolExtractionWorker { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action<long, IReadOnlyList<FileIssue>> InsertIssuesForIndexedFile { get; init; }
        internal required Action WriteProjectRootOnce { get; init; }
        internal required Action<string, string> SetPhase { get; init; }
    }

    private sealed record FullScanFilePersistenceResult(
        int ExtractedChunks,
        int ExtractedSymbols,
        int ExtractedReferences,
        int PersistedChunks,
        int PersistedSymbols,
        int PersistedReferences,
        int SymbolsDroppedByKindFilter,
        bool MutualRecursionRefreshNeeded,
        bool CSharpMetadataTargetsNeedRefresh,
        bool StampSymbolExtractorLanguage,
        string VerboseMessage);

    private static FullScanFilePersistenceResult PersistFullScanFile(
        FullScanFilePersistenceContext context)
    {
        var writer = context.Writer;
        var item = context.Item;
        var record = context.Record;
        var options = context.Options;
        var cancellationToken = context.CancellationToken;
        var mutualRecursionRefreshNeeded = false;
        var csharpMetadataTargetsNeedRefresh = false;
        var symbolsDroppedByKindFilter = 0;

        using var txn = writer.BeginTransaction(cancellationToken, "full scan file");
        if (!context.StartedWithNoIndexedFiles)
        {
            var stalePurged = context.DeferCSharpMutationsForIncompleteScan
                ? 0
                : writer.PurgeStaleFilesSharingChecksum(
                    context.ProjectRoot,
                    record.Path,
                    record.Checksum);
            if (stalePurged > 0)
            {
                csharpMetadataTargetsNeedRefresh = true;
                if (!options.SymbolsOnly)
                    mutualRecursionRefreshNeeded = true;
            }
        }
        var referenceIdentityChanged = false;
        var fileId = context.StartedWithNoIndexedFiles
            ? writer.InsertNewFile(record)
            : writer.UpsertFile(record, out referenceIdentityChanged);
        if (!options.SymbolsOnly && referenceIdentityChanged)
            mutualRecursionRefreshNeeded = true;

        context.SetPhase(FormatIndexPhasePath(record.Path, "chunking"), "chunking");
        var chunks = item.Chunks == null
            ? ChunkSplitter.SplitNormalized(
                fileId,
                item.Content!,
                item.HasOversizeLine ?? ChunkSplitter.HasOversizeLine(item.Content!),
                record.Lines)
            : ReassignChunkFileIds(item.Chunks, fileId);
        if (context.GeneratedSuppressionIssue != null)
        {
            writer.InsertChunks(chunks, cancellationToken);
            writer.InsertSymbols([], cancellationToken);
            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
            var generatedIssues = AppendIssueIfMissing(
                RequireWorkItemIssues(item),
                context.GeneratedSuppressionIssue);
            context.InsertIssuesForIndexedFile(fileId, generatedIssues);
            context.SetPhase(
                FormatIndexPhasePath(record.Path, "committing"),
                "committing");
            context.WriteProjectRootOnce();
            txn.Commit();
            return new FullScanFilePersistenceResult(
                chunks.Count,
                item.Symbols?.Count ?? 0,
                item.References?.Count ?? 0,
                chunks.Count,
                0,
                0,
                0,
                mutualRecursionRefreshNeeded,
                csharpMetadataTargetsNeedRefresh,
                StampSymbolExtractorLanguage: true,
                $"  [OK  ] {record.Path} ({chunks.Count} chunks, generated-code extraction skipped)");
        }

        context.SetPhase(FormatIndexPhasePath(record.Path, "symbols"), "symbols");
        FullScanFilePhaseForTesting?.Invoke(record.Path, "symbols");
        SymbolExtractionResult? symbolExtraction = null;
        var symbols = item.Symbols == null
            ? (symbolExtraction = ExtractSymbolsWithStallTimeout(
                fileId,
                record.Lang,
                item.Content!,
                item.FilePath,
                context.ProjectRoot,
                record.Path,
                FormatIndexPhasePath(record.Path, "symbols"),
                true,
                item.HasOversizeLine,
                item.ConflictMarkerLine,
                context.SymbolExtractionWorker,
                cancellationToken)).Symbols
            : ReassignSymbolFileIds(item.Symbols, fileId);
        var extractedSymbolCount = symbols.Count;
        var symbolRegexTimeoutIssue = symbolExtraction?.RegexTimeoutIssue;
        var fileContext = new FileContext(
            context.ProjectRoot,
            record.Path,
            item.FilePath,
            record.Lang);
        context.PostExtractionHooks.ObserveCSharpStaticInterfaceSourceSymbols(
            fileContext,
            symbols);
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
            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
            context.InsertIssuesForIndexedFile(fileId, capIssues);
            txn.Commit();
            return new FullScanFilePersistenceResult(
                chunks.Count,
                extractedSymbolCount,
                item.References?.Count ?? 0,
                0,
                0,
                0,
                0,
                mutualRecursionRefreshNeeded,
                csharpMetadataTargetsNeedRefresh,
                StampSymbolExtractorLanguage: false,
                $"  [SKIP] {record.Path} ({issue.Message})");
        }

        var familyScopeKey = context.Indexer.GetFamilyScopeKey(item.FilePath, record.Lang);
        if (item.Symbols == null)
        {
            SymbolExtractor.ApplyFamilyScope(
                symbols,
                familyScopeKey);
        }
        var mutableSymbols = symbols as IList<SymbolRecord> ?? symbols.ToList();
        context.PostExtractionHooks.OnSymbolsExtractedAfterSourceObservation(
            fileContext,
            mutableSymbols,
            item.Content,
            familyScopeKey);
        symbolsDroppedByKindFilter = options.SymbolKindFilter.Apply(mutableSymbols);
        symbols = (IReadOnlyList<SymbolRecord>)mutableSymbols;
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
            writer.InsertReferencesInAtomicFileScope([], cancellationToken);
            writer.InsertIssues(fileId, capIssues);
            txn.Commit();
            return new FullScanFilePersistenceResult(
                chunks.Count,
                extractedSymbolCount,
                item.References?.Count ?? 0,
                0,
                0,
                0,
                symbolsDroppedByKindFilter,
                mutualRecursionRefreshNeeded,
                csharpMetadataTargetsNeedRefresh,
                StampSymbolExtractorLanguage: false,
                $"  [SKIP] {record.Path} ({issue.Message})");
        }

        writer.InsertChunks(chunks, cancellationToken);
        FileIndexer.ValidateSymbolLineRanges(record, symbols);
        writer.InsertSymbols(symbols, cancellationToken);
        if (symbolRegexTimeoutIssue != null)
        {
            var baseIssues = RequireWorkItemIssues(item);
            item = item with { Issues = AppendIssue(baseIssues, symbolRegexTimeoutIssue) };
        }
        context.SetPhase(FormatIndexPhasePath(record.Path, "references"), "references");
        FullScanFilePhaseForTesting?.Invoke(record.Path, "references");
        IReadOnlyList<ReferenceRecord> references;
        var extractedReferenceCount = item.References?.Count ?? 0;
        if (options.SymbolsOnly)
        {
            references = [];
        }
        else
        {
            FileIssue? regexTimeoutIssue = null;
            ReferenceExtractionResult? referenceExtraction = null;
            if (item.References == null)
            {
                using var regexTimeouts = BoundedRegex.CaptureTimeouts(
                    record.Lang,
                    "reference_extraction");
                referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                    fileId,
                    record.Lang,
                    item.Content!,
                    item.HasOversizeLine
                        ?? ChunkSplitter.HasOversizeLine(item.Content!),
                    symbols,
                    record.Path,
                    record.Lang == "csharp"
                        ? context.CSharpWorkspace.Symbols
                        : null,
                    cancellationToken,
                    maxReferenceCount: options.MaxReferencesPerFile + 1,
                    conflictMarkerLine: item.ConflictMarkerLine,
                    workspaceRoot: context.ProjectRoot,
                    csharpStaticInterfaceMemberLookups:
                        context.CSharpWorkspace.StaticInterfaceMemberLookups,
                    csharpQualifiedPatternLookups:
                        context.CSharpWorkspace.QualifiedPatternLookups);
                references = referenceExtraction.References;
                regexTimeoutIssue = BuildRegexTimeoutIssue(record.Path, regexTimeouts);
            }
            else
            {
                references = ReassignReferenceFileIds(item.References, fileId);
            }
            extractedReferenceCount = references.Count;
            context.PostExtractionHooks.OnReferencesExtracted(
                fileContext,
                AsMutableList(references));
            if (regexTimeoutIssue != null)
            {
                var baseIssues = RequireWorkItemIssues(item);
                item = item with { Issues = AppendIssue(baseIssues, regexTimeoutIssue) };
            }
            if (referenceExtraction != null)
            {
                var baseIssues = RequireWorkItemIssues(item);
                item = item with
                {
                    Issues = AppendReferenceExtractionDiagnosticIssues(
                        baseIssues,
                        record.Path,
                        referenceExtraction.Diagnostics),
                };
            }
            if (references.Count > options.MaxReferencesPerFile)
            {
                var issue = BuildReferenceCountExceededIssue(
                    record.Path,
                    references.Count,
                    options.MaxReferencesPerFile);
                references = [];
                var baseIssues = RequireWorkItemIssues(item);
                item = item with { Issues = AppendIssue(baseIssues, issue) };
            }
        }

        if (context.StartedWithNoIndexedFiles)
        {
            writer.InsertReferencesForNewFilesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                cancellationToken);
        }
        else
        {
            writer.InsertReferencesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                cancellationToken);
        }
        if (!options.SymbolsOnly && (symbols.Count > 0 || references.Count > 0))
            mutualRecursionRefreshNeeded = true;
        context.SetPhase(FormatIndexPhasePath(record.Path, "validating"), "validating");
        var issues = RequireWorkItemIssues(item);
        context.InsertIssuesForIndexedFile(fileId, issues);
        context.SetPhase(FormatIndexPhasePath(record.Path, "committing"), "committing");
        context.WriteProjectRootOnce();
        txn.Commit();

        return new FullScanFilePersistenceResult(
            chunks.Count,
            extractedSymbolCount,
            extractedReferenceCount,
            chunks.Count,
            symbols.Count,
            references.Count,
            symbolsDroppedByKindFilter,
            mutualRecursionRefreshNeeded,
            csharpMetadataTargetsNeedRefresh,
            StampSymbolExtractorLanguage: true,
            $"  [OK  ] {record.Path} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
    }
}
