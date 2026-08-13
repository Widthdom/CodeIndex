using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed partial class FullScanPreWriteSession(
        FullScanPreWriteRequest request,
        FullScanPreWriteState state)
    {
        internal FullScanPreWriteRequest Request { get; } = request;
        internal FullScanPreWriteState State { get; } = state;
    }

    private readonly record struct FullScanPreWriteRequest(
        FullScanPreWriteCore Core,
        FullScanPreWriteBaseline Baseline,
        FullScanPreWriteContracts Contracts,
        FullScanPreWriteRuntime Runtime,
        FullScanPreWriteReusePolicy Reuse);

    private readonly record struct FullScanPreWriteCore(
        DbWriter Writer,
        FileIndexer Indexer,
        IndexCommandOptions Options,
        string ProjectRoot);

    private readonly record struct FullScanPreWriteBaseline(
        bool PriorIndexComplete,
        int PriorReadiness,
        bool PriorSymbolsOnlyGraphOmitted,
        bool? PriorCSharpStaticInterfaceSourceEvidence,
        bool StartedWithNoIndexedFiles,
        bool ScanHadErrors,
        bool ProjectRootWritten);

    private readonly record struct FullScanPreWriteContracts(
        bool SymbolKindFilterMatchesPrior,
        bool CSharpSymbolNameContractMatchesCurrent,
        bool CSharpIndexedProjectRootCompatible,
        bool CSharpHotspotTrustMatchesCurrent,
        bool RequiresConservativeCSharpSourceRefresh,
        bool ForceExtractorRefresh);

    private readonly record struct FullScanPreWriteRuntime(
        FullScanFileTarget[] FileTargets,
        IReadOnlyList<CSharpStaticInterfacePrepass.FileTarget> CSharpPrepassTargets,
        IReadOnlyDictionary<string, string> FileLanguages,
        int CSharpPrepassCapacity,
        int ExtractionParallelism,
        int FilesCount,
        string ActualMode,
        CancellationToken CancellationToken);

    private readonly record struct FullScanPreWriteReusePolicy(
        bool CanSkipTargetsBeforeContentLoad,
        bool SqlGraphContractMatchesCurrent,
        bool HdlGraphContractMatchesCurrent,
        IReadOnlyDictionary<string, bool> HotspotFamilyTrustMatchesCurrent,
        bool JavaScriptTypeScriptRefreshRequired);

    private sealed class FullScanPreWriteState
    {
        internal required FullScanPreWriteMutableScanState Scan { get; init; }
        internal required FullScanPreWriteCSharpState CSharp { get; init; }
        internal required FullScanPreWriteSelectionState Selection { get; init; }
        internal required FullScanPreWriteDiagnosticsState Diagnostics { get; init; }
    }

    private sealed class FullScanPreWriteMutableScanState
    {
        internal required FilePurgePlan StaleFilePurgePlan { get; set; }
        internal bool DeferCSharpMutationsForIncompleteScan { get; set; }
        internal int Purged { get; set; }
        internal bool FtsMutated { get; set; }
        internal bool HadCSharpStaticInterfaceContractsBeforePurge { get; set; }
    }

    private sealed class FullScanPreWriteCSharpState
    {
        internal ReusableIndexedFileStatsSnapshot? ReusableIndexedFileStats { get; set; }
        internal Dictionary<string, IndexedFileStatReuseResult?>? CSharpPrepassStatReuse { get; set; }
        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            WorkspaceFileSnapshots
        { get; set; }
        internal CSharpStaticInterfaceWorkspaceSymbols Workspace { get; set; } =
            null!;
        internal CSharpPrepassSymbolArtifactCache? PrepassSymbolArtifacts { get; set; }
        internal bool ForceFullRefreshFromInvalidatedNoOp { get; set; }
        internal bool PreservePriorPositiveSourceNoOp { get; set; }
        internal FullScanPreWriteCSharpEvidence Evidence { get; } = new();
    }

    private sealed class FullScanPreWriteCSharpEvidence
    {
        internal bool ForStamp { get; set; }
        internal bool Complete { get; set; }
    }

    private sealed class FullScanPreWriteSelectionState
    {
        internal List<int>? ExtractionFileIndexes { get; set; }
        internal int ExtractionWorkItemCount { get; set; }
        internal int Processed { get; set; }
        internal int Skipped { get; set; }
        internal required ReadableFileByteTracker ReadableFileBytes { get; init; }
        internal HashSet<string>? ReusedHotspotFamilyLanguages { get; set; }
        internal HashSet<string>? SkippedSymbolExtractorLanguages { get; set; }
        internal bool UseFtsBulkLoad { get; set; }
    }

    private sealed class FullScanPreWriteDiagnosticsState
    {
        internal required List<CliJsonMessage> ErrorList { get; init; }
        internal required List<StatusIndexFileError> FileErrorList { get; init; }
        internal required List<CliJsonMessage> WarningList { get; init; }
        internal required HashSet<string> ReportedCSharpWorkspaceFailures { get; init; }
        internal int Errors { get; set; }
    }
}
