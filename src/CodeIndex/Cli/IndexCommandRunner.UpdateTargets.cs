using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static int? TryResolveUpdateTargets(
        string projectRoot,
        IndexCommandOptions options,
        string[] spinnerFrames,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        out HashSet<string> targetPaths,
        out bool relevantIgnoreFileChanged)
    {
        targetPaths = new HashSet<string>(StringComparer.Ordinal);
        relevantIgnoreFileChanged = false;

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
                    var normalized = NormalizeCommitFileTargets(projectRoot, repoRoot, changedFiles, out var commitTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= commitTouchedRelevantIgnoreFile;
                    foreach (var f in normalized)
                        targetPaths.Add(f);
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
                    $"failed to resolve changed files from git commits: {ex.Message}",
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
                Console.WriteLine($"  Found {ConsoleUi.Counted(targetPaths.Count, "changed file")} from git");
                Console.WriteLine("  Note    : After reset/rebase/amend/switch/merge, prefer `cdidx .` over `--commits` for a full sync / 履歴改変やcheckout変更後は `--commits` より `cdidx .` を推奨");
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
                var normalized = NormalizeCommitFileTargets(projectRoot, repoRoot, changedFiles, out var rangeTouchedRelevantIgnoreFile);
                relevantIgnoreFileChanged |= rangeTouchedRelevantIgnoreFile;
                foreach (var f in normalized)
                    targetPaths.Add(f);
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
                    $"failed to resolve changed files between git refs: {ex.Message}",
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
                Console.WriteLine($"  Found {targetPaths.Count} changed file(s) between git refs");
        }

        if (options.UpdateFiles.Count > 0)
        {
            relevantIgnoreFileChanged |= ContainsRelevantIgnoreFileUpdate(projectRoot, options.UpdateFiles);
            foreach (var relPath in NormalizeUpdateFileTargets(projectRoot, options.UpdateFiles, options.Json))
                targetPaths.Add(relPath);
        }

        return null;
    }

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
