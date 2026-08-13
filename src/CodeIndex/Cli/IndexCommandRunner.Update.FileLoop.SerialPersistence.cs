using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class UpdateFileLoopSession
    {
        private UpdateFilePersistenceResult PersistSerialUpdateFile(
            string relativePath,
            string absolutePath,
            FileRecord record,
            LoadedFileRecord loaded,
            FileIssue? generatedSuppressionIssue,
            SymbolExtractionWorkerClient symbolExtractionWorker,
            bool projectRootWritten,
            ref bool fileBatchMarked)
        {
            var mutualRecursionRefreshNeeded = false;
            var symbolsDroppedByKindFilter = 0;

            writer.MarkBatchInProgress();
            fileBatchMarked = true;
            var recordRequiresTypeScriptAugmentationRefresh = record.Lang == "typescript";
            using var txn = writer.BeginTransaction(cancellationToken, "update file");
            if (recordRequiresTypeScriptAugmentationRefresh)
                RequireTypeScriptAugmentationRefresh();
            var stalePurged = PurgeStaleUpdateCleanupPaths(
                record.Path,
                record.Checksum,
                projectRootWritten);
            if (stalePurged > 0)
            {
                RequireTypeScriptAugmentationRefresh();
                if (!options.SymbolsOnly)
                    mutualRecursionRefreshNeeded = true;
            }
            WriteProjectRootOnce();
            var fileId = writer.UpsertFile(record, out var referenceIdentityChanged);
            if (!options.SymbolsOnly && referenceIdentityChanged)
                mutualRecursionRefreshNeeded = true;
            SetUpdatePhase(FormatIndexPhasePath(relativePath, "chunking"), "chunking");
            var chunks = ChunkSplitter.SplitNormalized(
                fileId,
                loaded.Content,
                loaded.Facts);
            if (generatedSuppressionIssue != null)
            {
                writer.InsertChunks(chunks, cancellationToken);
                writer.InsertSymbols([], cancellationToken);
                writer.InsertReferencesInAtomicFileScope(
                    [],
                    refreshMutualRecursionFlags: false,
                    cancellationToken);
                SetUpdatePhase(
                    FormatIndexPhasePath(relativePath, "validating"),
                    "validating");
                var generatedIssues = AppendIssueIfMissing(
                    FileIndexer.ValidateContent(
                        record.Path,
                        loaded.RawBytes,
                        loaded.Content,
                        record.Lang,
                        loaded.Inspection,
                        loaded.Facts),
                    generatedSuppressionIssue);
                writer.InsertIssues(fileId, generatedIssues);
                SetUpdatePhase(
                    FormatIndexPhasePath(relativePath, "committing"),
                    "committing");
                writer.ClearBatchInProgress();
                txn.Commit();
                fileBatchMarked = false;
                RecordDynamicGraphFileRefresh(record.Lang);
                return new UpdateFilePersistenceResult(
                    0,
                    mutualRecursionRefreshNeeded,
                    $"  [OK  ] {relativePath} ({chunks.Count} chunks, generated-code extraction skipped)");
            }

            SetUpdatePhase(FormatIndexPhasePath(relativePath, "symbols"), "symbols");
            var symbolExtraction = ExtractSymbolsWithStallTimeout(
                fileId,
                record.Lang,
                loaded.Content,
                absolutePath,
                projectRoot,
                record.Path,
                FormatIndexPhasePath(relativePath, "symbols"),
                true,
                loaded.HasOversizeLine,
                loaded.ConflictMarkerLine,
                symbolExtractionWorker,
                cancellationToken);
            var symbols = symbolExtraction.Symbols;
            var symbolRegexTimeoutIssue = symbolExtraction.RegexTimeoutIssue;
            var fileContext = new FileContext(
                projectRoot,
                record.Path,
                absolutePath,
                record.Lang);
            var sourceContractSeenBeforeObservation =
                postExtractionHooks.Value.SawCSharpStaticInterfaceSourceContract;
            postExtractionHooks.Value.ObserveCSharpStaticInterfaceSourceSymbols(
                fileContext,
                symbols);
            if (record.Lang == "csharp"
                && !csharpWorkspace.HasSourceStaticInterfaceContracts
                && !sourceContractSeenBeforeObservation
                && postExtractionHooks.Value.SawCSharpStaticInterfaceSourceContract)
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
                fileBatchMarked = false;
                RecordDynamicGraphFileRefresh(record.Lang);
                return new UpdateFilePersistenceResult(
                    0,
                    mutualRecursionRefreshNeeded,
                    $"  [SKIP] {relativePath} ({issue.Message})");
            }

            var familyScopeKey = indexer.GetFamilyScopeKey(absolutePath, record.Lang);
            SymbolExtractor.ApplyFamilyScope(symbols, familyScopeKey, record.Lang);
            postExtractionHooks.Value.OnSymbolsExtractedAfterSourceObservation(
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
                fileBatchMarked = false;
                RecordDynamicGraphFileRefresh(record.Lang);
                return new UpdateFilePersistenceResult(
                    symbolsDroppedByKindFilter,
                    mutualRecursionRefreshNeeded,
                    $"  [SKIP] {relativePath} ({issue.Message})");
            }

            writer.InsertChunks(chunks, cancellationToken);
            FileIndexer.ValidateSymbolLineRanges(record, symbols);
            writer.InsertSymbols(symbols, cancellationToken);
            SetUpdatePhase(
                FormatIndexPhasePath(relativePath, "references"),
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
                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                    cancellationToken,
                    maxReferenceCount: options.MaxReferencesPerFile + 1,
                    conflictMarkerLine: loaded.ConflictMarkerLine,
                    workspaceRoot: projectRoot,
                    csharpStaticInterfaceMemberLookups:
                        csharpWorkspace.StaticInterfaceMemberLookups,
                    csharpQualifiedPatternLookups:
                        csharpWorkspace.QualifiedPatternLookups);
                references = referenceExtraction.References;
                referenceRegexTimeoutIssue =
                    BuildRegexTimeoutIssue(record.Path, regexTimeouts);
            }
            postExtractionHooks.Value.OnReferencesExtracted(fileContext, references);
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
            SetUpdatePhase(
                FormatIndexPhasePath(relativePath, "validating"),
                "validating");
            IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(
                record.Path,
                loaded.RawBytes,
                loaded.Content,
                record.Lang,
                loaded.Inspection,
                loaded.Facts);
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
            SetUpdatePhase(
                FormatIndexPhasePath(relativePath, "committing"),
                "committing");
            writer.ClearBatchInProgress();
            txn.Commit();
            fileBatchMarked = false;
            RecordDynamicGraphFileRefresh(record.Lang);
            if (!options.SymbolsOnly && (symbols.Count > 0 || references.Count > 0))
                mutualRecursionRefreshNeeded = true;

            return new UpdateFilePersistenceResult(
                symbolsDroppedByKindFilter,
                mutualRecursionRefreshNeeded,
                $"  [OK  ] {relativePath} ({chunks.Count} chunks, {symbols.Count} symbols, {references.Count} refs)");
        }
    }
}
