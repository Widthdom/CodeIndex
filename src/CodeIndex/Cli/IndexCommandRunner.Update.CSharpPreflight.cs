using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private sealed class UpdateCSharpPreflightContext
    {
        internal required DbWriter Writer { get; init; }
        internal required FileIndexer Indexer { get; init; }
        internal required IndexCommandOptions Options { get; init; }
        internal required string ProjectRoot { get; init; }
        internal required HashSet<string> TargetPaths { get; init; }
        internal required bool PriorFilterRetainedCSharpContractMembers { get; init; }
        internal required bool? PriorCSharpStaticInterfaceSourceEvidence { get; init; }
        internal required FilePurgePlan ScopedCleanupPlan { get; init; }
        internal required bool ScopedCleanupHadCSharp { get; init; }
        internal required bool ScopedCleanupHadContract { get; init; }
        internal required bool HadIndexedCSharpFilesBeforeUpdate { get; init; }
        internal required int Updated { get; init; }
        internal required int Removed { get; init; }
        internal required CancellationToken CancellationToken { get; init; }
        internal required Action ThrowIfUpdateCancelled { get; init; }
        internal required Action<IEnumerable<FileIndexer.ScanError>>
            RecordScanErrors
        { get; init; }
        internal required Action<string, string> RecordCSharpWorkspaceDrift
        {
            get;
            init;
        }
    }

    private sealed class UpdateCSharpPreflightState
    {
        internal IReadOnlyDictionary<string, string>? ScannedUpdateLanguages
        {
            get;
            set;
        }

        internal required List<CSharpStaticInterfacePrepass.FileTarget>
            CSharpPrepassTargets
        { get; set; }
        internal HashSet<string>?
            ExistingCSharpPathsNowUnsupportedOrNonCSharp
        { get; set; }
        internal required CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace
        {
            get;
            set;
        }

        internal Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceSnapshots
        { get; set; }
        internal FileIndexer.ScanInputSnapshot? CSharpWorkspaceInputSnapshot
        {
            get;
            set;
        }

        internal bool DeferCSharpMutationsForIncompleteWorkspace
        {
            get;
            set;
        }

        internal bool? CSharpSourceEvidenceForStamp { get; set; }
        internal bool CSharpSourceEvidenceCompleteForStamp { get; set; }
        internal bool PreserveConservativePersistedContractEvidence
        {
            get;
            set;
        }

        internal bool CSharpTargetAffected { get; set; }
    }

    private sealed record UpdateCSharpPreflightResult(
        IReadOnlyDictionary<string, string>? ScannedUpdateLanguages,
        List<CSharpStaticInterfacePrepass.FileTarget> CSharpPrepassTargets,
        CSharpStaticInterfaceWorkspaceSymbols CSharpWorkspace,
        Dictionary<string, CSharpStaticInterfacePrepass.FileStatSnapshot>?
            CSharpWorkspaceSnapshots,
        FileIndexer.ScanInputSnapshot? CSharpWorkspaceInputSnapshot,
        bool DeferCSharpMutationsForIncompleteWorkspace,
        bool? CSharpSourceEvidenceForStamp,
        bool CSharpSourceEvidenceCompleteForStamp,
        bool CSharpTargetAffected);

    private static UpdateCSharpPreflightResult PrepareUpdateCSharpWorkspace(
        UpdateCSharpPreflightContext context)
    {
        context.ThrowIfUpdateCancelled();
        WriteIndexJsonLiveness(
            context.Options,
            "checking C# workspace contracts...");
        var heartbeat = StartIndexJsonPhaseHeartbeat(
            context.Options,
            "checking C# workspace contracts");
        var targets = BuildUpdateCSharpPrepassTargets(
            context.Indexer,
            context.ProjectRoot,
            context.TargetPaths,
            scannedLanguages: null,
            out var transitionedPaths);
        var state = new UpdateCSharpPreflightState
        {
            CSharpPrepassTargets = targets,
            ExistingCSharpPathsNowUnsupportedOrNonCSharp =
                transitionedPaths,
            CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols([], false),
        };
        try
        {
            BuildInitialUpdateCSharpWorkspace(context, state);
        }
        catch (OperationCanceledException) when (
            context.CancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(
                context.Updated + context.Removed,
                context.TargetPaths.Count);
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(heartbeat);
        }

        if (state.CSharpWorkspace.HasStaticInterfaceContracts
            || state.CSharpWorkspace.RequiresMemberReadReferenceRefresh)
            ExpandUpdateCSharpWorkspace(context, state);

        return new UpdateCSharpPreflightResult(
            state.ScannedUpdateLanguages,
            state.CSharpPrepassTargets,
            state.CSharpWorkspace,
            state.CSharpWorkspaceSnapshots,
            state.CSharpWorkspaceInputSnapshot,
            state.DeferCSharpMutationsForIncompleteWorkspace,
            state.CSharpSourceEvidenceForStamp,
            state.CSharpSourceEvidenceCompleteForStamp,
            state.CSharpTargetAffected);
    }

    private static void BuildInitialUpdateCSharpWorkspace(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state)
    {
        var writer = context.Writer;
        var cancellationToken = context.CancellationToken;
        var transitionedPaths =
            state.ExistingCSharpPathsNowUnsupportedOrNonCSharp;
        var transitionedPathWasCSharp = transitionedPaths is { Count: > 0 }
            && writer.HasCSharpFilesInPaths(
                transitionedPaths,
                cancellationToken);
        var transitionedPathHadContract = transitionedPaths is { Count: > 0 }
            && writer.HasCSharpStaticInterfaceContractSymbolsInPaths(
                transitionedPaths,
                includeInterfaceDeclarationsAsConservativeEvidence:
                    context.PriorCSharpStaticInterfaceSourceEvidence == null
                    || !context.PriorFilterRetainedCSharpContractMembers,
                cancellationToken);
        var transitionedPathHadMemberReadTarget =
            transitionedPaths is { Count: > 0 }
            && writer.HasCSharpMemberReadTargetSymbolsInPaths(
                transitionedPaths,
                cancellationToken);
        var scopedCleanupHadMemberReadTarget =
            context.ScopedCleanupPlan.FileIds.Count > 0
            && writer.HasCSharpMemberReadTargetSymbolsInFileIds(
                context.ScopedCleanupPlan.FileIds,
                cancellationToken);
        state.CSharpTargetAffected = state.CSharpPrepassTargets.Count > 0
            || transitionedPathWasCSharp
            || context.ScopedCleanupHadCSharp;
        var persistedContractEvidence = context.ScopedCleanupHadContract
            || transitionedPathHadContract
            || (state.CSharpTargetAffected
                && context.HadIndexedCSharpFilesBeforeUpdate
                && context.PriorCSharpStaticInterfaceSourceEvidence != false);
        state.PreserveConservativePersistedContractEvidence =
            persistedContractEvidence;

        if (state.CSharpPrepassTargets.Count == 0
            && !persistedContractEvidence
            && !transitionedPathHadMemberReadTarget
            && !scopedCleanupHadMemberReadTarget)
        {
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    transitionedPathHadContract);
        }
        else if (transitionedPathHadMemberReadTarget
                 || scopedCleanupHadMemberReadTarget)
        {
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    false,
                    RequiresMemberReadReferenceRefresh: true);
        }
        else if (persistedContractEvidence)
        {
            // Persisted contracts already require the complete C# update set. Defer
            // candidate reads and workspace materialization to that authoritative pass.
            // 永続化済みcontractがある場合は全C# update setが必要なため、candidate
            // readとworkspace materializationを後続のauthoritative passへ委譲する。
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols([], true);
        }
        else
        {
            BuildInitialUpdateCSharpWorkspaceSnapshot(context, state);
        }

        if (state.CSharpTargetAffected
            && context.PriorCSharpStaticInterfaceSourceEvidence == false)
        {
            state.CSharpSourceEvidenceForStamp =
                state.CSharpWorkspace.HasSourceStaticInterfaceContracts;
            state.CSharpSourceEvidenceCompleteForStamp =
                state.CSharpWorkspace.SourceContractEvidenceComplete;
        }

        if (!state.CSharpWorkspace.SourceContractEvidenceComplete)
        {
            state.CSharpWorkspace = state.CSharpWorkspace with
            {
                HasStaticInterfaceContracts = true,
            };
        }
    }

    private static void BuildInitialUpdateCSharpWorkspaceSnapshot(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state)
    {
        var capturedBefore =
            CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
                state.CSharpPrepassTargets,
                out var beforeSnapshots,
                out _,
                context.CancellationToken);
        if (!capturedBefore)
        {
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false);
            return;
        }

        UpdateCSharpPrepassForTesting?.Invoke();
        state.CSharpWorkspace =
            CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                context.Writer,
                context.Indexer,
                state.CSharpPrepassTargets,
                includeExistingSymbols: true,
                excludedExistingFileIds:
                    context.ScopedCleanupPlan.FileIds,
                isExistingSymbolPathExcluded: path =>
                    state
                        .ExistingCSharpPathsNowUnsupportedOrNonCSharp?
                        .Contains(path) == true,
                parallelism: context.Options.Parallelism,
                cancellationToken: context.CancellationToken);
        if (!CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                state.CSharpPrepassTargets,
                beforeSnapshots,
                out _,
                context.CancellationToken))
        {
            state.CSharpWorkspace = state.CSharpWorkspace with
            {
                HasStaticInterfaceContracts = true,
                SourceContractEvidenceComplete = false,
            };
            return;
        }

        state.CSharpWorkspaceSnapshots = beforeSnapshots;
    }

    private static void ExpandUpdateCSharpWorkspace(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state)
    {
        WriteIndexJsonLiveness(
            context.Options,
            "expanding C# update set for static interface contracts...");
        var heartbeat = StartIndexJsonPhaseHeartbeat(
            context.Options,
            "expanding C# update set for static interface contracts");
        try
        {
            UpdateCSharpExpansionScanStartingForTesting?.Invoke();
            var scanWithDirectorySnapshots =
                context.Indexer.ScanFilesDetailedWithDirectoryListingSnapshots(
                    cancellationToken: context.CancellationToken);
            var scanResult = scanWithDirectorySnapshots.ScanResult;
            state.CSharpWorkspaceInputSnapshot =
                scanWithDirectorySnapshots.InputSnapshot;
            var expandedScanHadFatalErrors =
                scanResult.Errors.Any(error => error.IsFatal);
            context.RecordScanErrors(scanResult.Errors);
            state.ScannedUpdateLanguages = scanResult.FileLanguages;
            if (expandedScanHadFatalErrors)
            {
                DeferExpandedUpdateCSharpWorkspace(context, state);
                return;
            }

            AddExpandedUpdateCSharpTargets(context, state, scanResult);
            BuildExpandedUpdateCSharpWorkspace(context, state);
        }
        catch (OperationCanceledException) when (
            context.CancellationToken.IsCancellationRequested)
        {
            throw new IndexInterruptedException(
                context.Updated + context.Removed,
                context.TargetPaths.Count);
        }
        finally
        {
            StopIndexJsonPhaseHeartbeat(heartbeat);
        }
    }

    private static void AddExpandedUpdateCSharpTargets(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state,
        FileIndexer.ScanFilesResult scanResult)
    {
        var expandedTargetIndexPaths =
            new HashSet<string>(StringComparer.Ordinal);
        foreach (var existingTargetPath in context.TargetPaths)
        {
            expandedTargetIndexPaths.Add(
                UpdateFileTarget.Create(
                    context.ProjectRoot,
                    existingTargetPath).IndexPath);
        }

        foreach (var filePath in scanResult.Files)
        {
            if (scanResult.FileLanguages.TryGetValue(
                    filePath,
                    out var language)
                && language == "csharp"
                && expandedTargetIndexPaths.Add(
                    UpdateFileTarget.Create(
                        context.ProjectRoot,
                        filePath).IndexPath))
            {
                context.TargetPaths.Add(filePath);
            }
        }

        state.CSharpPrepassTargets = BuildUpdateCSharpPrepassTargets(
            context.Indexer,
            context.ProjectRoot,
            context.TargetPaths,
            state.ScannedUpdateLanguages,
            out var transitionedPaths);
        state.ExistingCSharpPathsNowUnsupportedOrNonCSharp =
            transitionedPaths;
    }

    private static void BuildExpandedUpdateCSharpWorkspace(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state)
    {
        var cancellationToken = context.CancellationToken;
        var capturedBefore =
            CSharpStaticInterfacePrepass.TryCaptureFileStatSnapshots(
                state.CSharpPrepassTargets,
                out var beforeSnapshots,
                out var snapshotFailurePath,
                cancellationToken);
        if (state.CSharpPrepassTargets.Count == 0)
        {
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols([], false);
        }
        else if (!capturedBefore)
        {
            state.CSharpWorkspace =
                new CSharpStaticInterfaceWorkspaceSymbols(
                    [],
                    HasStaticInterfaceContracts: true,
                    SourceContractEvidenceComplete: false);
        }
        else
        {
            UpdateCSharpPrepassForTesting?.Invoke();
            state.CSharpWorkspace =
                CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    context.Writer,
                    context.Indexer,
                    state.CSharpPrepassTargets,
                    isExistingSymbolPathExcluded: path =>
                        state
                            .ExistingCSharpPathsNowUnsupportedOrNonCSharp?
                            .Contains(path) == true,
                    parallelism: context.Options.Parallelism,
                    excludedExistingFileIds:
                        context.ScopedCleanupPlan.FileIds,
                    cancellationToken: cancellationToken);
        }

        string? afterSnapshotFailurePath = null;
        var stableFilesAfterPrepass = capturedBefore
            && CSharpStaticInterfacePrepass.TryValidateFileStatSnapshots(
                state.CSharpPrepassTargets,
                beforeSnapshots,
                out afterSnapshotFailurePath,
                cancellationToken);
        var stableSnapshot = stableFilesAfterPrepass
            && state.CSharpWorkspace.SourceContractEvidenceComplete;
        if (!stableSnapshot)
        {
            state.DeferCSharpMutationsForIncompleteWorkspace = true;
            context.RecordCSharpWorkspaceDrift(
                state.CSharpWorkspace.IncompleteSourcePaths?.FirstOrDefault()
                    ?? snapshotFailurePath
                    ?? afterSnapshotFailurePath
                    ?? "<csharp_workspace>",
                "The C# workspace changed or became unreadable during contract preflight.");
            state.CSharpSourceEvidenceForStamp = null;
            state.CSharpSourceEvidenceCompleteForStamp = false;
            state.CSharpWorkspaceSnapshots = null;
            state.CSharpWorkspace = state.CSharpWorkspace with
            {
                HasStaticInterfaceContracts = true,
                SourceContractEvidenceComplete = false,
            };
            DeferCSharpTargetsAfterIncompleteWorkspace(
                context.Writer,
                context.ProjectRoot,
                context.TargetPaths,
                cancellationToken);
            return;
        }

        state.CSharpWorkspaceSnapshots = beforeSnapshots;
        state.CSharpSourceEvidenceForStamp =
            state.CSharpWorkspace.HasSourceStaticInterfaceContracts;
        state.CSharpSourceEvidenceCompleteForStamp = true;
        if (state.CSharpWorkspace.RequiresMemberReadReferenceRefresh)
        {
            // A target-set change requires every C# consumer to be re-extracted
            // against the new lookup, including otherwise reusable files.
            // target集合の変更時は、通常なら再利用可能なfileも含め、全C# consumerを
            // 新しいlookupで再抽出する。
            state.CSharpWorkspace = state.CSharpWorkspace with
            {
                HasStaticInterfaceContracts = true,
            };
        }

        // Persisted positive/legacy evidence remains conservative until every C# file
        // has been refreshed successfully. Even when the new source snapshot is
        // negative, disable C# stat reuse for this pass.
        // persisted positive/legacy evidence は全C# refresh成功まで保持し、
        // 新 snapshot がnegativeでも今回のC# stat reuseは無効化する。
        if (state.PreserveConservativePersistedContractEvidence)
        {
            state.CSharpWorkspace = state.CSharpWorkspace with
            {
                HasStaticInterfaceContracts = true,
            };
        }
    }

    private static void DeferExpandedUpdateCSharpWorkspace(
        UpdateCSharpPreflightContext context,
        UpdateCSharpPreflightState state)
    {
        // An incomplete enumeration cannot prove that a hook-hidden source contract
        // was absent from the omitted subtree. Preserve every C# row and reference
        // instead of rebuilding visible implementations against a partial lookup.
        // 不完全列挙では omitted subtree の hook-hidden contract 不在を証明できないため、
        // C# row/ref は全て保持し、non-C# target のみ進める。
        state.DeferCSharpMutationsForIncompleteWorkspace = true;
        state.CSharpSourceEvidenceForStamp = null;
        state.CSharpSourceEvidenceCompleteForStamp = false;
        state.CSharpWorkspaceSnapshots = null;
        state.CSharpWorkspace =
            new CSharpStaticInterfaceWorkspaceSymbols(
                [],
                HasStaticInterfaceContracts: true,
                SourceContractEvidenceComplete: false);
        DeferCSharpTargetsAfterIncompleteWorkspace(
            context.Writer,
            context.ProjectRoot,
            context.TargetPaths,
            context.CancellationToken);
    }
}
