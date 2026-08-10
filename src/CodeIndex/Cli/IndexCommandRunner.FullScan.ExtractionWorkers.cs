using System.Collections.Concurrent;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class FullScanExtractionWorkerContext
    {
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required FullScanFileTarget[] FileTargets { get; init; }
        internal IReadOnlyList<int>? ExtractionFileIndexes { get; init; }
        internal required int ExtractionWorkItemCount { get; init; }
        internal required int ExtractionWorkerCount { get; init; }
        internal required bool ParallelizeExtraction { get; init; }
        internal required int[] ExtractionTailSchedule { get; init; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace { get; init; }
        internal CSharpPrepassSymbolArtifactCache? CSharpPrepassSymbolArtifacts { get; init; }
        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>? CSharpWorkspaceFileSnapshots { get; init; }
        internal required PostExtractionHookRunner PostExtractionHooks { get; init; }
        internal required ActiveExtractionPhase?[] ActiveExtractionPhases { get; init; }
        internal required BlockingCollection<FullScanFileWorkItem> ExtractionResults { get; init; }
        internal required CancellationToken ExtractionCancellationToken { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
    }

    private static Task[] StartFullScanExtractionWorkers(
        FullScanExtractionWorkerContext context)
    {
        var indexer = context.Indexer;
        var options = context.Options;
        var projectRoot = context.ProjectRoot;
        var fileTargets = context.FileTargets;
        var extractionFileIndexes = context.ExtractionFileIndexes;
        var extractionWorkItemCount = context.ExtractionWorkItemCount;
        var extractionWorkerCount = context.ExtractionWorkerCount;
        var parallelizeExtraction = context.ParallelizeExtraction;
        var extractionTailSchedule = context.ExtractionTailSchedule;
        var csharpWorkspace = context.CSharpWorkspace;
        var csharpPrepassSymbolArtifacts = context.CSharpPrepassSymbolArtifacts;
        var csharpWorkspaceFileSnapshots = context.CSharpWorkspaceFileSnapshots;
        var postExtractionHooks = context.PostExtractionHooks;
        var activeExtractionPhases = context.ActiveExtractionPhases;
        var extractionResults = context.ExtractionResults;
        var extractionCancellationToken = context.ExtractionCancellationToken;
        var cancellationToken = context.CancellationToken;
        var nextExtractionIndex = -1;
        var workers = Enumerable.Range(0, extractionWorkerCount)
            .Select(workerIndex => Task.Factory.StartNew(() =>
            {
                using var workerSymbolExtractionWorker = new LazyDisposable<SymbolExtractionWorkerClient>(
                    () => new SymbolExtractionWorkerClient(options.MaxFileSizeBytes));
                while (true)
                {
                    extractionCancellationToken.ThrowIfCancellationRequested();
                    var extractionIndex = Interlocked.Increment(ref nextExtractionIndex);
                    if (extractionIndex >= extractionWorkItemCount)
                        break;

                    var tailStart = extractionWorkItemCount - extractionTailSchedule.Length;
                    var workOrdinal = extractionIndex >= tailStart
                        ? extractionTailSchedule[extractionIndex - tailStart]
                        : extractionIndex;
                    var fileIndex = ResolveFullScanExtractionFileIndex(
                        extractionFileIndexes,
                        workOrdinal);
                    var target = fileTargets[fileIndex];
                    var filePath = target.FilePath;
                    var relativeFilePath = target.RelativePath;
                    var displayRelativePath = target.DisplayRelativePath;
                    try
                    {
                        Volatile.Write(ref activeExtractionPhases[workerIndex], new(displayRelativePath, "reading"));
                        FullScanFileContentLoadForTesting?.Invoke(displayRelativePath);
                        var loaded = indexer.BuildLoadedRecordWithRawBytes(
                            filePath,
                            relativeFilePath,
                            target.Language,
                            extractionCancellationToken);
                        var record = loaded.Record;
                        var workspaceFileSnapshots = csharpWorkspaceFileSnapshots;
                        if (target.Language == "csharp"
                            && workspaceFileSnapshots != null
                            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                target.FilePath,
                                target.IndexPath,
                                target.DisplayRelativePath,
                                record.Size,
                                record.Modified,
                                workspaceFileSnapshots,
                                out var changedPath,
                                extractionCancellationToken))
                        {
                            extractionResults.Add(
                                FullScanFileWorkItem.Failure(
                                    fileIndex,
                                    filePath,
                                    displayRelativePath,
                                    "csharp_workspace_validation",
                                    new CSharpWorkspaceSnapshotDriftException(
                                        FormatCSharpWorkspaceSnapshotPath(projectRoot, changedPath))),
                                extractionCancellationToken);
                            continue;
                        }
                        var content = loaded.Content;
                        var rawBytes = loaded.RawBytes;
                        var warning = loaded.Warning;
                        var hasOversizeLine = loaded.HasOversizeLine;
                        IReadOnlyList<ChunkRecord>? chunks = null;
                        IReadOnlyList<SymbolRecord>? symbols = null;
                        IReadOnlyList<ReferenceRecord>? references = null;
                        IReadOnlyList<FileIssue>? issues = null;
                        var generatedSuppressionIssue = target.GeneratedExtractionSuppressed
                            ? indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path)
                            : null;
                        if (parallelizeExtraction)
                        {
                            Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "chunking"));
                            chunks = ChunkSplitter.SplitNormalized(0, content, loaded.Facts);
                            if (generatedSuppressionIssue != null)
                            {
                                symbols = [];
                                references = [];
                                Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "validating"));
                                issues = AppendIssueIfMissing(
                                    FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.Facts),
                                    generatedSuppressionIssue);
                                extractionResults.Add(
                                    FullScanFileWorkItem.Precomputed(
                                        fileIndex,
                                        filePath,
                                        displayRelativePath,
                                        record,
                                        warning,
                                        chunks,
                                        symbols,
                                        references,
                                        issues,
                                        generatedSuppressionIssue,
                                        generatedSuppressionChecked: true),
                                    extractionCancellationToken);
                                continue;
                            }
                            Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "symbols"));
                            FullScanFilePhaseForTesting?.Invoke(record.Path, "symbols");
                            FileIssue? symbolRegexTimeoutIssue;
                            if (string.Equals(record.Lang, "csharp", StringComparison.Ordinal)
                                && record.Checksum is { } checksum
                                && csharpPrepassSymbolArtifacts?.TryTake(
                                    record.Path,
                                    checksum,
                                    out var symbolArtifact) == true)
                            {
                                symbols = symbolArtifact.Symbols;
                                symbolRegexTimeoutIssue = null;
                            }
                            else
                            {
                                var symbolExtraction = ExtractSymbolsWithStallTimeout(
                                    0,
                                    record.Lang,
                                    content,
                                    filePath,
                                    projectRoot,
                                    record.Path,
                                    Volatile.Read(ref activeExtractionPhases[workerIndex])!.Format(),
                                    true,
                                    hasOversizeLine,
                                    loaded.ConflictMarkerLine,
                                    workerSymbolExtractionWorker.Value,
                                    extractionCancellationToken);
                                symbols = symbolExtraction.Symbols;
                                symbolRegexTimeoutIssue =
                                    symbolExtraction.RegexTimeoutIssue;
                            }
                            if (string.Equals(record.Lang, "csharp", StringComparison.Ordinal))
                            {
                                var sourceFileContext = new FileContext(
                                    projectRoot,
                                    record.Path,
                                    filePath,
                                    record.Lang);
                                postExtractionHooks.ObserveCSharpStaticInterfaceSourceSymbols(
                                    sourceFileContext,
                                    symbols);
                            }
                            if (symbols.Count > options.MaxSymbolsPerFile)
                            {
                                var issue = BuildSymbolCountExceededIssue(record.Path, symbols.Count, options.MaxSymbolsPerFile);
                                IReadOnlyList<FileIssue> capIssues = symbolRegexTimeoutIssue == null
                                    ? [issue]
                                    : AppendIssue([symbolRegexTimeoutIssue], issue);
                                extractionResults.Add(
                                    FullScanFileWorkItem.Precomputed(fileIndex, filePath, displayRelativePath, record, issue.Message, [], [], [], capIssues),
                                    extractionCancellationToken);
                                continue;
                            }
                            SymbolExtractor.ApplyFamilyScope(
                                symbols,
                                indexer.GetFamilyScopeKey(filePath, record.Lang),
                                record.Lang);
                            FileIssue? referenceRegexTimeoutIssue = null;
                            ReferenceExtractionResult? referenceExtraction = null;
                            if (options.SymbolsOnly)
                            {
                                references = [];
                            }
                            else
                            {
                                Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "references"));
                                FullScanFilePhaseForTesting?.Invoke(record.Path, "references");
                                using var regexTimeouts = BoundedRegex.CaptureTimeouts(record.Lang, "reference_extraction");
                                referenceExtraction = ReferenceExtractor.ExtractDetailedNormalized(
                                    0,
                                    record.Lang,
                                    content,
                                    hasOversizeLine,
                                    symbols,
                                    record.Path,
                                    record.Lang == "csharp" ? csharpWorkspace.Symbols : null,
                                    extractionCancellationToken,
                                    maxReferenceCount: options.MaxReferencesPerFile + 1,
                                    conflictMarkerLine: loaded.ConflictMarkerLine,
                                    workspaceRoot: projectRoot,
                                    csharpStaticInterfaceMemberLookups: csharpWorkspace.StaticInterfaceMemberLookups,
                                    csharpQualifiedPatternLookups: csharpWorkspace.QualifiedPatternLookups);
                                references = referenceExtraction.References;
                                referenceRegexTimeoutIssue = BuildRegexTimeoutIssue(record.Path, regexTimeouts);
                            }
                            Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "validating"));
                            issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.Facts);
                            if (symbolRegexTimeoutIssue != null)
                                issues = AppendIssue(issues, symbolRegexTimeoutIssue);
                            if (referenceRegexTimeoutIssue != null)
                                issues = AppendIssue(issues, referenceRegexTimeoutIssue);
                            if (referenceExtraction != null)
                                issues = AppendReferenceExtractionDiagnosticIssues(issues, record.Path, referenceExtraction.Diagnostics);
                            if (references.Count > options.MaxReferencesPerFile)
                            {
                                var issue = BuildReferenceCountExceededIssue(record.Path, references.Count, options.MaxReferencesPerFile);
                                references = [];
                                issues = AppendIssue(issues, issue);
                            }
                        }
                        else
                        {
                            Volatile.Write(ref activeExtractionPhases[workerIndex], new(record.Path, "validating"));
                            issues = FileIndexer.ValidateContent(record.Path, rawBytes, content, record.Lang, loaded.Inspection, loaded.Facts);
                        }
                        extractionResults.Add(
                            parallelizeExtraction
                                ? FullScanFileWorkItem.Precomputed(
                                    fileIndex,
                                    filePath,
                                    displayRelativePath,
                                    record,
                                    warning,
                                    chunks!,
                                    symbols!,
                                    references!,
                                    issues!,
                                    generatedSuppressionIssue,
                                    generatedSuppressionChecked: true,
                                    content: postExtractionHooks.HasHooks ? content : null,
                                    contentFacts: postExtractionHooks.HasHooks
                                        ? loaded.Facts
                                        : null)
                                : FullScanFileWorkItem.Success(
                                    fileIndex,
                                    filePath,
                                    displayRelativePath,
                                    record,
                                    content,
                                    loaded.Facts,
                                    warning,
                                    chunks,
                                    symbols,
                                    references,
                                    issues,
                                    generatedSuppressionIssue,
                                    generatedSuppressionChecked: true),
                            extractionCancellationToken);
                    }
                    catch (OperationCanceledException) when (extractionCancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (FileIndexer.BinaryFileSkippedException ex)
                    {
                        var record = indexer.BuildSkippedFileRecord(filePath, relativeFilePath, target.Language);
                        var workspaceFileSnapshots = csharpWorkspaceFileSnapshots;
                        if (target.Language == "csharp"
                            && workspaceFileSnapshots != null
                            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                target.FilePath,
                                target.IndexPath,
                                target.DisplayRelativePath,
                                record.Size,
                                record.Modified,
                                workspaceFileSnapshots,
                                out var changedPath,
                                extractionCancellationToken))
                        {
                            extractionResults.Add(
                                FullScanFileWorkItem.Failure(
                                    fileIndex,
                                    filePath,
                                    displayRelativePath,
                                    "csharp_workspace_validation",
                                    new CSharpWorkspaceSnapshotDriftException(
                                        FormatCSharpWorkspaceSnapshotPath(projectRoot, changedPath))),
                                extractionCancellationToken);
                            continue;
                        }
                        var issue = BuildNullByteIssue(ex);
                        var sanitizedMessage = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
                        extractionResults.Add(
                            FullScanFileWorkItem.Precomputed(fileIndex, filePath, displayRelativePath, record, sanitizedMessage, [], [], [], [issue]),
                            extractionCancellationToken);
                    }
                    catch (FileIndexer.FileTooLargeSkippedException ex)
                    {
                        var sanitizedMessage = CommandErrorWriter.FormatSanitizedExceptionMessage(ex);
                        var record = indexer.BuildSkippedFileRecord(filePath, relativeFilePath, target.Language);
                        var workspaceFileSnapshots = csharpWorkspaceFileSnapshots;
                        if (target.Language == "csharp"
                            && workspaceFileSnapshots != null
                            && !CSharpStaticInterfacePrepass.TryValidateLoadedFileStatSnapshot(
                                target.FilePath,
                                target.IndexPath,
                                target.DisplayRelativePath,
                                record.Size,
                                record.Modified,
                                workspaceFileSnapshots,
                                out var changedPath,
                                extractionCancellationToken))
                        {
                            extractionResults.Add(
                                FullScanFileWorkItem.Failure(
                                    fileIndex,
                                    filePath,
                                    displayRelativePath,
                                    "csharp_workspace_validation",
                                    new CSharpWorkspaceSnapshotDriftException(
                                        FormatCSharpWorkspaceSnapshotPath(projectRoot, changedPath))),
                                extractionCancellationToken);
                            continue;
                        }
                        var issue = new FileIssue
                        {
                            Path = ex.RelativePath,
                            Kind = "file_too_large",
                            Line = 0,
                            Message = sanitizedMessage,
                        };
                        extractionResults.Add(
                            FullScanFileWorkItem.Precomputed(fileIndex, filePath, displayRelativePath, record, sanitizedMessage, [], [], [], [issue]),
                            extractionCancellationToken);
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        var item = target.Language == "csharp" && csharpWorkspaceFileSnapshots != null
                            ? FullScanFileWorkItem.Failure(
                                fileIndex,
                                filePath,
                                displayRelativePath,
                                "csharp_workspace_validation",
                                new CSharpWorkspaceSnapshotDriftException(target.DisplayRelativePath))
                            : FullScanFileWorkItem.Skipped(
                                fileIndex,
                                filePath,
                                displayRelativePath,
                                $"{displayRelativePath}: skipped because it was deleted during indexing.");
                        extractionResults.Add(item, extractionCancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var failedPhase = Volatile.Read(ref activeExtractionPhases[workerIndex])?.Phase ?? "unknown";
                        extractionResults.Add(FullScanFileWorkItem.Failure(fileIndex, filePath, displayRelativePath, failedPhase, ex), extractionCancellationToken);
                    }
                    finally
                    {
                        Volatile.Write(ref activeExtractionPhases[workerIndex], null);
                    }
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        return workers;
    }
}
