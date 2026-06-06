using System.Text.Json;

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

        Console.Error.WriteLine($"cdidx: {message}");
    }

    private static (CancellationTokenSource Cts, Task Task)? StartIndexJsonPhaseHeartbeat(
        IndexCommandOptions options,
        string phase,
        Func<string?>? detailProvider = null)
    {
        if (!options.Json || options.Quiet)
            return null;

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var task = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                var detail = detailProvider?.Invoke();
                var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
                Console.Error.WriteLine($"cdidx: still {phase}{suffix}...");
            }
        }, token);
        return (cts, task);
    }

    private static void StopIndexJsonPhaseHeartbeat((CancellationTokenSource Cts, Task Task)? heartbeat)
    {
        if (heartbeat == null)
            return;

        heartbeat.Value.Cts.Cancel();
        try
        {
            heartbeat.Value.Task.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException or TaskCanceledException))
        {
        }
        heartbeat.Value.Cts.Dispose();
    }
}
