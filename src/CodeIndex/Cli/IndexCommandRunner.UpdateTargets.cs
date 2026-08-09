using System.Text.Json;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static int? TryResolveUpdateTargets(
        string projectRoot,
        IndexCommandOptions options,
        string[] spinnerFrames,
        JsonSerializerOptions jsonOptions,
        string? priorWorkspaceVerifiedHead,
        IReadOnlyList<string> priorWorkspaceVerificationPendingPaths,
        bool priorWorkspaceVerificationPendingPathsComplete,
        string? currentHeadCommit,
        CancellationToken cancellationToken,
        out HashSet<string> targetPaths,
        out HashSet<string> gitTargetPaths,
        out HashSet<string> explicitFileTargetPaths,
        out bool relevantIgnoreFileChanged,
        out bool workspaceHeadCoverageVerified)
    {
        targetPaths = new HashSet<string>(StringComparer.Ordinal);
        gitTargetPaths = new HashSet<string>(StringComparer.Ordinal);
        explicitFileTargetPaths = new HashSet<string>(StringComparer.Ordinal);
        relevantIgnoreFileChanged = false;
        workspaceHeadCoverageVerified = false;
        HashSet<string>? skipWorktreePaths = null;
        var sparseTargetSkipped = false;
        var mutableTargetPaths = targetPaths;
        var mutableGitTargetPaths = gitTargetPaths;

        bool IsMissingSparseSkippedTarget(string relativePath)
        {
            var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                return false;

            skipWorktreePaths ??= GitHelper.TryGetSkipWorktreePaths(projectRoot, cancellationToken);
            return IsSparseSkippedPath(skipWorktreePaths, relativePath);
        }

        void AddNormalizedGitTargets(
            string repoRoot,
            IReadOnlyList<string> changedFiles,
            out bool touchedRelevantIgnoreFile)
        {
            var normalized = NormalizeCommitFileTargets(
                projectRoot,
                repoRoot,
                changedFiles,
                out touchedRelevantIgnoreFile);
            foreach (var path in normalized)
            {
                if (IsMissingSparseSkippedTarget(path))
                {
                    sparseTargetSkipped = true;
                    continue;
                }
                mutableTargetPaths.Add(path);
                mutableGitTargetPaths.Add(path);
            }
        }

        int? AddPendingWorkspaceVerificationTargets()
        {
            if (!priorWorkspaceVerificationPendingPathsComplete)
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    "persisted workspace verification pending-path coverage is incomplete",
                    CommandExitCodes.UsageError,
                    "Run `cdidx index <projectPath>` for a verified full-workspace refresh before using a scoped Git refresh.",
                    CommandErrorCodes.UsageError);
            }

            foreach (var path in priorWorkspaceVerificationPendingPaths)
            {
                var normalizedPath = FileIndexer.NormalizeIndexPath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                    continue;
                var absolutePath = Path.GetFullPath(Path.Combine(
                    projectRoot,
                    normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
                var relativePath = FileIndexer.NormalizePathSeparators(
                    FileIndexer.GetRelativePathFromProjectRoot(projectRoot, absolutePath));
                if (relativePath == "." || IsOutsideProjectRoot(relativePath))
                {
                    return WriteCommandError(
                        options.Json,
                        jsonOptions,
                        "persisted workspace verification pending paths contain an invalid project-relative path",
                        CommandExitCodes.UsageError,
                        "Run `cdidx index <projectPath>` for a verified full-workspace refresh before using a scoped Git refresh.",
                        CommandErrorCodes.UsageError);
                }
                if (IsMissingSparseSkippedTarget(relativePath))
                {
                    sparseTargetSkipped = true;
                    continue;
                }
                mutableTargetPaths.Add(relativePath);
                mutableGitTargetPaths.Add(relativePath);
            }
            return null;
        }

        if (options.Commits.Count > 0)
        {
            CancellationTokenSource? spinnerCts = null;
            try
            {
                if (!options.Json)
                    spinnerCts = ConsoleUi.StartSpinner("Resolving changed files...", spinnerFrames);
                var repoRoot = GitHelper.TryGetRepositoryRoot(projectRoot, cancellationToken) ?? Path.GetFullPath(projectRoot);
                foreach (var commit in options.Commits)
                {
                    var changedFiles = GitHelper.GetChangedFilesFromCommit(projectRoot, commit, cancellationToken);
                    AddNormalizedGitTargets(repoRoot, changedFiles, out var commitTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= commitTouchedRelevantIgnoreFile;
                }

                var currentHeadCovered = !string.IsNullOrWhiteSpace(currentHeadCommit)
                    && options.Commits.Any(commit =>
                        GitRefCoversCurrentHead(projectRoot, commit, currentHeadCommit, cancellationToken));
                if (currentHeadCovered && !string.IsNullOrWhiteSpace(priorWorkspaceVerifiedHead))
                {
                    var pendingTargetError = AddPendingWorkspaceVerificationTargets();
                    if (pendingTargetError != null)
                        return pendingTargetError.Value;
                    var resolvedBaseline = GitHelper.TryResolveCommit(
                        projectRoot,
                        priorWorkspaceVerifiedHead,
                        cancellationToken);
                    if (resolvedBaseline == null)
                    {
                        return WriteCommandError(
                            options.Json,
                            jsonOptions,
                            $"persisted workspace verification baseline could not be resolved by git: {priorWorkspaceVerifiedHead}",
                            CommandExitCodes.UsageError,
                            "Fetch the missing history and rerun the scoped refresh, or run `cdidx index <projectPath>` for a verified full-workspace refresh.",
                            CommandErrorCodes.UsageError);
                    }

                    var baselineChanges = GitHelper.GetChangedFilesBetweenRefs(
                        projectRoot,
                        resolvedBaseline,
                        currentHeadCommit!,
                        cancellationToken);
                    AddNormalizedGitTargets(repoRoot, baselineChanges, out var baselineTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= baselineTouchedRelevantIgnoreFile;
                    // A skip-worktree path that is absent from the sparse checkout cannot be
                    // reconciled against its indexed row. Keep the prior whole-workspace
                    // verification stamp even though the visible-cone update may succeed.
                    // sparse checkout 外の不在 path は indexed row と照合できないため、
                    // visible cone の更新が成功しても workspace 全体の検証 stamp は進めない。
                    workspaceHeadCoverageVerified = !sparseTargetSkipped;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"failed to resolve changed files from git commits: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                    CommandExitCodes.UsageError,
                    "Check the commit refs and rerun `cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`.",
                    CommandErrorCodes.UsageError);
            }
            finally
            {
                ConsoleUi.StopSpinner(spinnerCts);
            }
            if (!options.Json && !options.Quiet)
            {
                CommandOutputWriter.WriteLine($"  Found {ConsoleUi.Counted(targetPaths.Count, "changed file")} from git");
                CommandOutputWriter.WriteLine("  Note    : After reset/rebase/amend/switch/merge, prefer `cdidx .` over `--commits` for a full sync / 履歴改変やcheckout変更後は `--commits` より `cdidx .` を推奨");
            }
        }

        if (options.ChangedBetweenSpecified)
        {
            CancellationTokenSource? spinnerCts = null;
            WriteIndexJsonLiveness(options, "resolving changed files between git refs...");
            var resolveHeartbeat = StartIndexJsonPhaseHeartbeat(options, "resolving changed files between git refs");
            try
            {
                if (!options.Json)
                    spinnerCts = ConsoleUi.StartSpinner("Resolving changed files between refs...", spinnerFrames);
                var repoRoot = GitHelper.TryGetRepositoryRoot(projectRoot, cancellationToken) ?? Path.GetFullPath(projectRoot);
                var changedFiles = GitHelper.GetChangedFilesBetweenRefs(projectRoot, options.ChangedBetweenRefs[0], options.ChangedBetweenRefs[1], cancellationToken);
                AddNormalizedGitTargets(repoRoot, changedFiles, out var rangeTouchedRelevantIgnoreFile);
                relevantIgnoreFileChanged |= rangeTouchedRelevantIgnoreFile;

                var newRefCoversCurrentHead = !string.IsNullOrWhiteSpace(currentHeadCommit)
                    && GitRefCoversCurrentHead(
                        projectRoot,
                        options.ChangedBetweenRefs[1],
                        currentHeadCommit,
                        cancellationToken);
                if (newRefCoversCurrentHead && !string.IsNullOrWhiteSpace(priorWorkspaceVerifiedHead))
                {
                    var pendingTargetError = AddPendingWorkspaceVerificationTargets();
                    if (pendingTargetError != null)
                        return pendingTargetError.Value;
                    var resolvedBaseline = GitHelper.TryResolveCommit(
                        projectRoot,
                        priorWorkspaceVerifiedHead,
                        cancellationToken);
                    if (resolvedBaseline == null)
                    {
                        return WriteCommandError(
                            options.Json,
                            jsonOptions,
                            $"persisted workspace verification baseline could not be resolved by git: {priorWorkspaceVerifiedHead}",
                            CommandExitCodes.UsageError,
                            "Fetch the missing history and rerun `--changed-between`, or run `cdidx index <projectPath>` for a verified full-workspace refresh.",
                            CommandErrorCodes.UsageError);
                    }

                    var resolvedOldRef = GitHelper.TryResolveCommit(
                        projectRoot,
                        options.ChangedBetweenRefs[0],
                        cancellationToken);
                    if (!string.Equals(resolvedBaseline, resolvedOldRef, StringComparison.OrdinalIgnoreCase))
                    {
                        var baselineChanges = GitHelper.GetChangedFilesBetweenRefs(
                            projectRoot,
                            resolvedBaseline,
                            currentHeadCommit!,
                            cancellationToken);
                        AddNormalizedGitTargets(repoRoot, baselineChanges, out var baselineTouchedRelevantIgnoreFile);
                        relevantIgnoreFileChanged |= baselineTouchedRelevantIgnoreFile;
                    }
                    workspaceHeadCoverageVerified = !sparseTargetSkipped;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"failed to resolve changed files between git refs: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                    CommandExitCodes.UsageError,
                    "Check the refs and rerun `cdidx index <projectPath> --changed-between <old-ref> <new-ref>`.",
                    CommandErrorCodes.UsageError);
            }
            finally
            {
                StopIndexJsonPhaseHeartbeat(resolveHeartbeat);
                ConsoleUi.StopSpinner(spinnerCts);
            }

            WriteIndexJsonLiveness(options, $"found {ConsoleUi.Counted(targetPaths.Count, "changed file")}; preparing update...");
            if (!options.Json)
                CommandOutputWriter.WriteLine($"  Found {targetPaths.Count} changed file(s) between git refs");
        }

        if (options.UpdateFiles.Count > 0)
        {
            relevantIgnoreFileChanged |= ContainsRelevantIgnoreFileUpdate(projectRoot, options.UpdateFiles);
            foreach (var relPath in NormalizeUpdateFileTargets(projectRoot, options.UpdateFiles, options.Json))
            {
                targetPaths.Add(relPath);
                explicitFileTargetPaths.Add(relPath);
            }
        }

        return null;
    }

    private static bool IsSparseSkippedPath(IReadOnlySet<string>? skipWorktreePaths, string relativePath)
        => skipWorktreePaths != null
           && skipWorktreePaths.Contains(FileIndexer.NormalizeIndexPath(relativePath));

    private static void WriteIndexJsonLiveness(IndexCommandOptions options, string message)
    {
        if (!options.Json || options.Quiet)
            return;

        CommandErrorWriter.WriteStderr($"cdidx: {message}");
    }

    private static (CancellationTokenSource Cts, Task Task)? StartIndexJsonPhaseHeartbeat(
        IndexCommandOptions options,
        string phase,
        Func<string?>? detailProvider = null)
    {
        return StartObservedJsonPhaseHeartbeat(
            options.Json && !options.Quiet,
            "cdidx-index",
            phase,
            ConsoleUi.TryWriteErrorLine,
            detailProvider);
    }

    internal static (CancellationTokenSource Cts, Task Task)? StartObservedJsonPhaseHeartbeat(
        bool enabled,
        string component,
        string phase,
        Action<string> messageWriter,
        Func<string?>? detailProvider = null,
        TimeSpan? interval = null,
        Action<string>? warningWriter = null)
    {
        if (!enabled)
            return null;

        ArgumentNullException.ThrowIfNull(messageWriter);

        var cts = new CancellationTokenSource();
        var heartbeatInterval = interval ?? TimeSpan.FromSeconds(5);
        var task = BackgroundTaskObserver.Run(
            token => RunObservedJsonPhaseHeartbeatLoop(phase, messageWriter, detailProvider, heartbeatInterval, token),
            component,
            $"{phase} heartbeat",
            cts.Token,
            warningWriter);
        return (cts, task);
    }

    private static async Task RunObservedJsonPhaseHeartbeatLoop(
        string phase,
        Action<string> messageWriter,
        Func<string?>? detailProvider,
        TimeSpan interval,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }

            if (token.IsCancellationRequested)
                break;

            var detail = detailProvider?.Invoke();
            var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
            messageWriter($"cdidx: still {phase}{suffix}...");
        }
    }

    private static void StopIndexJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
        => StopObservedJsonPhaseHeartbeat(heartbeat);

    internal static void StopObservedJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
    {
        if (heartbeat == null)
            return;

        var cts = heartbeat.Value.Cts;
        var task = heartbeat.Value.Task;
        cts.Cancel();
        if (task.IsCompleted)
        {
            cts.Dispose();
            return;
        }

        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                if (completedTask.IsFaulted)
                    _ = completedTask.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            cts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
