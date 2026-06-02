using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static int RunDryRun(
        IndexCommandOptions options,
        bool ignoreCase,
        string ignoreRuleRoot,
        JsonSerializerOptions jsonOptions,
        CliJsonSerializerContext jsonContext,
        CancellationToken cancellationToken)
    {
        var projectPath = options.ProjectPath!;
        var dryIndexer = new FileIndexer(projectPath, ignoreCase, ignoreRuleRoot, options.MaxFileSizeBytes, directoryIgnoreCaseProbe: null, symlinkPolicy: options.SymlinkPolicy);
        IReadOnlyList<string> dryCandidates;
        var errorList = new List<CliJsonMessage>();
        var dryScanErrorKeys = new HashSet<string>(StringComparer.Ordinal);

        void RecordDryRunScanErrors(IEnumerable<FileIndexer.ScanError> scanErrors)
        {
            foreach (var scanError in scanErrors)
            {
                var key = $"{scanError.Path}\n{scanError.Message}";
                if (!dryScanErrorKeys.Add(key))
                    continue;

                errorList.Add(new CliJsonMessage(scanError.Path, scanError.Message));
                if (!options.Json)
                    ConsoleUi.PrintWarning($"{scanError.Path}: {scanError.Message}");
            }
        }

        if (!TryResolveDryRunCandidates(
            options,
            dryIndexer,
            projectPath,
            jsonOptions,
            cancellationToken,
            RecordDryRunScanErrors,
            out dryCandidates,
            out var exitCode))
        {
            return exitCode;
        }

        var dryFiles = new List<string>();
        var langCounts = new Dictionary<string, int>();
        foreach (var f in dryCandidates)
        {
            var pathFilter = dryIndexer.EvaluatePathFilter(f);
            RecordDryRunScanErrors(pathFilter.Errors);
            if (pathFilter.ShouldSkip)
                continue;

            if (!TryProbeDryRunFile(dryIndexer, f, out var lang, out var message))
            {
                if (message != null)
                {
                    var displayPath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectPath, f));
                    errorList.Add(new CliJsonMessage(displayPath, message));
                    if (!options.Json && !options.Quiet)
                        ConsoleUi.PrintWarning($"{displayPath}: {message}");
                }
                continue;
            }

            dryFiles.Add(f);
            langCounts[lang] = langCounts.GetValueOrDefault(lang) + 1;
        }
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new IndexDryRunJsonResult
            {
                Status = "dry_run",
                FilesTotal = dryFiles.Count,
                Languages = langCounts,
                Errors = errorList.Count > 0 ? errorList : null,
            }, jsonContext.IndexDryRunJsonResult));
        }
        else
        {
            Console.WriteLine($"Dry run: {dryFiles.Count} files would be indexed");
            foreach (var (lang, count) in langCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {lang,-12} {count,6}");
        }
        return CommandExitCodes.Success;
    }

    private static bool TryResolveDryRunCandidates(
        IndexCommandOptions options,
        FileIndexer dryIndexer,
        string projectPath,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        Action<IEnumerable<FileIndexer.ScanError>> recordDryRunScanErrors,
        out IReadOnlyList<string> dryCandidates,
        out int exitCode)
    {
        dryCandidates = [];
        exitCode = CommandExitCodes.Success;

        if (options.UpdateFiles.Count > 0)
        {
            // --files: only the specified files / --files: 指定ファイルのみ
            var relevantIgnoreFileChanged = ContainsRelevantIgnoreFileUpdate(projectPath, options.UpdateFiles);
            var updatePaths = NormalizeUpdateFileTargets(projectPath, options.UpdateFiles, options.Json);
            if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(updatePaths))
            {
                FileIndexer.ScanFilesResult scanResult;
                try
                {
                    scanResult = dryIndexer.ScanFilesDetailed(cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    exitCode = WriteDryRunInterrupted(options, jsonOptions);
                    return false;
                }
                dryCandidates = scanResult.Files;
                recordDryRunScanErrors(scanResult.Errors);
            }
            else
            {
                dryCandidates = updatePaths
                    .Select(path => Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(p => File.Exists(LongPath.EnsureWindowsPrefix(p)))
                    .ToList();
            }
        }
        else if (options.Commits.Count > 0 || options.ChangedBetweenSpecified)
        {
            // Git update modes: files changed in commits or between refs.
            // Git更新モード: コミットまたはref間の変更ファイル。
            var changedFiles = new HashSet<string>(StringComparer.Ordinal);
            var relevantIgnoreFileChanged = false;
            var repoRoot = GitHelper.TryGetRepositoryRoot(projectPath) ?? Path.GetFullPath(projectPath);
            try
            {
                foreach (var commit in options.Commits)
                {
                    var changed = GitHelper.GetChangedFilesFromCommit(projectPath, commit);
                    var normalized = NormalizeCommitFileTargets(projectPath, repoRoot, changed, out var commitTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= commitTouchedRelevantIgnoreFile;
                    foreach (var path in normalized)
                        changedFiles.Add(path);
                }
            }
            catch (Exception ex)
            {
                exitCode = WriteCommandError(
                    options.Json,
                    jsonOptions,
                    $"failed to resolve changed files from git commits: {ex.Message}",
                    CommandExitCodes.UsageError,
                    "Check the commit refs and rerun `cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`.",
                    CommandErrorCodes.UsageError);
                return false;
            }
            if (options.ChangedBetweenRefs.Count == 2)
            {
                try
                {
                    var changed = GitHelper.GetChangedFilesBetweenRefs(projectPath, options.ChangedBetweenRefs[0], options.ChangedBetweenRefs[1]);
                    var normalized = NormalizeCommitFileTargets(projectPath, repoRoot, changed, out var rangeTouchedRelevantIgnoreFile);
                    relevantIgnoreFileChanged |= rangeTouchedRelevantIgnoreFile;
                    foreach (var path in normalized)
                        changedFiles.Add(path);
                }
                catch { /* ignore git errors in dry-run */ }
            }

            if (relevantIgnoreFileChanged || ContainsIgnoreFilePath(changedFiles))
            {
                FileIndexer.ScanFilesResult scanResult;
                try
                {
                    scanResult = dryIndexer.ScanFilesDetailed(cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    exitCode = WriteDryRunInterrupted(options, jsonOptions);
                    return false;
                }
                dryCandidates = scanResult.Files;
                recordDryRunScanErrors(scanResult.Errors);
            }
            else
            {
                dryCandidates = changedFiles
                    .Select(path => Path.Combine(projectPath, path.Replace('/', Path.DirectorySeparatorChar)))
                    .Where(p => File.Exists(LongPath.EnsureWindowsPrefix(p)))
                    .ToList();
            }
        }
        else
        {
            FileIndexer.ScanFilesResult scanResult;
            try
            {
                scanResult = dryIndexer.ScanFilesDetailed(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                exitCode = WriteDryRunInterrupted(options, jsonOptions);
                return false;
            }
            dryCandidates = scanResult.Files;
            recordDryRunScanErrors(scanResult.Errors);
        }

        return true;
    }

    private static int WriteDryRunInterrupted(IndexCommandOptions options, JsonSerializerOptions jsonOptions) => WriteCommandError(
        options.Json,
        jsonOptions,
        "Interrupted before dry-run scan completed.",
        CommandExitCodes.Interrupted,
        "Rerun `cdidx index --dry-run` when you are ready to inspect the candidate files again.",
        CommandErrorCodes.Interrupted);
}
