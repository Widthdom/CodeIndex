using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private static void PersistExtractedMcpIndexFile(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        in CSharpStaticInterfacePrepass.FileTarget target,
        FileRecord record,
        in LoadedFileRecord loaded,
        long fileId,
        IReadOnlyList<ChunkRecord> chunks,
        DbWriter.TransactionScope txn)
    {
        var (symbols, symbolRegexTimeoutIssue) = ExtractMcpIndexSymbols(
            context,
            in target,
            record,
            in loaded,
            fileId);
        var familyScopeKey = context.Indexer.GetFamilyScopeKey(target.FilePath, record.Lang);
        SymbolExtractor.ApplyFamilyScope(symbols, familyScopeKey, record.Lang);
        var fileContext = new FileContext(
            context.ProjectPath,
            record.Path,
            target.FilePath,
            record.Lang);
        context.PostExtractionHooks.Value.ObserveCSharpStaticInterfaceSourceSymbols(
            fileContext,
            symbols);
        context.PostExtractionHooks.Value.OnSymbolsExtractedAfterSourceObservation(
            fileContext,
            symbols,
            loaded.Content,
            familyScopeKey);
        session.SymbolsDroppedByKindFilter += context.SymbolKindFilter.Apply(symbols);

        var committed = PersistMcpIndexSymbolsAndReferences(
            context,
            session,
            record,
            in loaded,
            fileId,
            chunks,
            symbols,
            symbolRegexTimeoutIssue,
            fileContext);
        CompleteMcpIndexFileTransaction(
            context,
            session,
            record,
            txn,
            committed.Chunks,
            committed.Symbols,
            committed.References);
    }

    private static (List<SymbolRecord> Symbols, FileIssue? TimeoutIssue)
        ExtractMcpIndexSymbols(
            McpIndexFileLoopContext context,
            in CSharpStaticInterfacePrepass.FileTarget target,
            FileRecord record,
            in LoadedFileRecord loaded,
            long fileId)
    {
        if (record.Lang == "csharp"
            && record.Checksum is { } checksum
            && context.CSharpWorkspace.PrepassArtifacts?.TryTake(
                record.Path,
                checksum,
                out var artifact) == true)
        {
            foreach (var symbol in artifact.Symbols)
                symbol.FileId = fileId;
            return (artifact.Symbols, null);
        }

        using var regexTimeouts = BoundedRegex.CaptureTimeouts(
            record.Lang,
            "symbol_extraction");
        var symbols = SymbolExtractor.ExtractNormalized(
            fileId,
            record.Lang,
            loaded.Content,
            loaded.HasOversizeLine,
            target.FilePath,
            context.ProjectPath,
            context.CancellationToken,
            loaded.ConflictMarkerLine,
            patternConfigsAlreadyLoaded: true);
        return (
            symbols,
            IndexCommandRunner.BuildRegexTimeoutIssue(record.Path, regexTimeouts));
    }

    private static McpIndexCommittedRows PersistMcpIndexSymbolsAndReferences(
        McpIndexFileLoopContext context,
        McpIndexFileLoopSession session,
        FileRecord record,
        in LoadedFileRecord loaded,
        long fileId,
        IReadOnlyList<ChunkRecord> chunks,
        List<SymbolRecord> symbols,
        FileIssue? symbolRegexTimeoutIssue,
        FileContext fileContext)
    {
        if (symbols.Count > context.MaxSymbolsPerFile)
        {
            PersistMcpSymbolCap(
                context,
                record,
                fileId,
                symbols.Count,
                symbolRegexTimeoutIssue);
            return default;
        }

        context.Writer.InsertChunks(chunks, context.CancellationToken);
        context.Writer.InsertSymbols(symbols, context.CancellationToken);
        var extracted = ExtractMcpIndexReferences(
            context,
            record,
            in loaded,
            fileId,
            symbols,
            fileContext);
        var references = extracted.References;
        FileIssue? referenceCapIssue = null;
        if (references.Count > context.MaxReferencesPerFile)
        {
            referenceCapIssue = BuildMcpReferenceCountExceededIssue(
                record.Path,
                references.Count,
                context.MaxReferencesPerFile);
            references = [];
        }
        PersistMcpIndexReferences(context, references);
        if (symbols.Count > 0 || references.Count > 0)
            session.MutualRecursionRefreshNeeded = true;
        PersistMcpIndexValidationIssues(
            context,
            record,
            in loaded,
            fileId,
            symbolRegexTimeoutIssue,
            extracted.TimeoutIssue,
            extracted.Extraction.Diagnostics,
            referenceCapIssue);
        return new McpIndexCommittedRows(chunks.Count, symbols.Count, references.Count);
    }

    private static (List<ReferenceRecord> References,
        ReferenceExtractionResult Extraction,
        FileIssue? TimeoutIssue) ExtractMcpIndexReferences(
        McpIndexFileLoopContext context,
        FileRecord record,
        in LoadedFileRecord loaded,
        long fileId,
        IReadOnlyList<SymbolRecord> symbols,
        FileContext fileContext)
    {
        ReferenceExtractionResult extraction;
        FileIssue? timeoutIssue;
        using (var regexTimeouts = BoundedRegex.CaptureTimeouts(
                   record.Lang,
                   "reference_extraction"))
        {
            var workspace = context.CSharpWorkspace.Workspace;
            extraction = ReferenceExtractor.ExtractDetailedNormalized(
                fileId,
                record.Lang,
                loaded.Content,
                loaded.HasOversizeLine,
                symbols,
                record.Path,
                record.Lang == "csharp" ? workspace.Symbols : null,
                context.CancellationToken,
                maxReferenceCount: context.MaxReferencesPerFile + 1,
                conflictMarkerLine: loaded.ConflictMarkerLine,
                workspaceRoot: context.ProjectPath,
                csharpStaticInterfaceMemberLookups:
                    workspace.StaticInterfaceMemberLookups,
                csharpQualifiedPatternLookups: workspace.QualifiedPatternLookups);
            timeoutIssue = IndexCommandRunner.BuildRegexTimeoutIssue(
                record.Path,
                regexTimeouts);
        }
        context.PostExtractionHooks.Value.OnReferencesExtracted(
            fileContext,
            extraction.References);
        return (extraction.References, extraction, timeoutIssue);
    }

    private static void PersistMcpIndexReferences(
        McpIndexFileLoopContext context,
        IReadOnlyList<ReferenceRecord> references)
    {
        if (context.StartedWithNoIndexedFiles)
        {
            context.Writer.InsertReferencesForNewFilesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                context.CancellationToken);
            return;
        }
        context.Writer.InsertReferencesInAtomicFileScope(
            references,
            refreshMutualRecursionFlags: false,
            context.CancellationToken);
    }

    private static void PersistMcpIndexValidationIssues(
        McpIndexFileLoopContext context,
        FileRecord record,
        in LoadedFileRecord loaded,
        long fileId,
        FileIssue? symbolTimeoutIssue,
        FileIssue? referenceTimeoutIssue,
        IReadOnlyList<ReferenceExtractionDiagnostic> diagnostics,
        FileIssue? referenceCapIssue)
    {
        IReadOnlyList<FileIssue> issues = FileIndexer.ValidateContent(
            record.Path,
            loaded.RawBytes,
            loaded.Content,
            record.Lang,
            loaded.Inspection,
            loaded.Facts);
        if (symbolTimeoutIssue != null)
            issues = IndexCommandRunner.AppendIssue(issues, symbolTimeoutIssue);
        if (referenceTimeoutIssue != null)
            issues = IndexCommandRunner.AppendIssue(issues, referenceTimeoutIssue);
        issues = IndexCommandRunner.AppendReferenceExtractionDiagnosticIssues(
            issues,
            record.Path,
            diagnostics);
        if (referenceCapIssue != null)
            issues = IndexCommandRunner.AppendIssue(issues, referenceCapIssue);
        context.InsertIssuesForIndexedFile(fileId, issues);
    }
}
