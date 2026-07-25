using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static partial class IndexWatchRunner
{
    private static List<FileSystemWatcher> CreateAncestorIgnoreWatchers(
        string projectRoot,
        string ignoreRuleRoot,
        bool ignoreCase,
        Action<string> enqueue)
    {
        var watchers = new List<FileSystemWatcher>();
        var fullProjectRoot = Path.GetFullPath(projectRoot);
        var fullIgnoreRuleRoot = Path.GetFullPath(ignoreRuleRoot);
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullProjectRoot, fullIgnoreRuleRoot, comparison))
            return watchers;

        var relativeProjectRoot = Path.GetRelativePath(fullIgnoreRuleRoot, fullProjectRoot);
        if (Path.IsPathRooted(relativeProjectRoot)
            || relativeProjectRoot == ".."
            || relativeProjectRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeProjectRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return watchers;
        }

        try
        {
            var directory = Directory.GetParent(fullProjectRoot);
            while (directory != null)
            {
                var ancestorWatcher = new FileSystemWatcher(directory.FullName)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watchers.Add(ancestorWatcher);
                ancestorWatcher.Filters.Add(".gitignore");
                ancestorWatcher.Filters.Add(".cdidxignore");
                ancestorWatcher.Created += (_, e) => enqueue(e.FullPath);
                ancestorWatcher.Changed += (_, e) => enqueue(e.FullPath);
                ancestorWatcher.Deleted += (_, e) => enqueue(e.FullPath);
                ancestorWatcher.Renamed += (_, e) =>
                {
                    enqueue(e.OldFullPath);
                    enqueue(e.FullPath);
                };
                ancestorWatcher.EnableRaisingEvents = true;

                if (string.Equals(directory.FullName, fullIgnoreRuleRoot, comparison))
                    break;

                directory = directory.Parent;
            }
        }
        catch
        {
            foreach (var watcher in watchers)
                watcher.Dispose();
            throw;
        }

        return watchers;
    }

    private static int RunPartialUpdate(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<string> changedPaths,
        string resolvedDbPath,
        CancellationToken cancellationToken,
        string phase = "incremental")
    {
        var baseArgs = BuildSubRunArgs(baseOptions, resolvedDbPath);
        var batches = BuildPartialUpdateBatches(baseArgs, changedPaths);
        if (batches == null)
            return RunFullRescan(baseOptions, jsonOptions, resolvedDbPath, cancellationToken);

        var exitCode = CommandExitCodes.Success;
        foreach (var batch in batches)
        {
            var stopwatch = Stopwatch.StartNew();
            var args = new List<string>(baseArgs.Count + 1 + batch.Count);
            args.AddRange(baseArgs);
            args.Add("--files");
            args.AddRange(batch);

            var subRunExitCode = InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "updated", batch.Count, phase, batch, cancellationToken);
            RecordSubRunExitCode(ref exitCode, subRunExitCode);
            if (cancellationToken.IsCancellationRequested)
                break;
        }

        return exitCode;
    }

    internal static List<List<string>>? BuildPartialUpdateBatches(IReadOnlyList<string> baseArgs, IReadOnlyList<string> changedPaths)
    {
        var baseArgumentChars = EstimateSubRunArgumentChars(baseArgs) + EstimateSubRunArgumentChars("--files");
        var batches = new List<List<string>>();
        var current = new List<string>();
        var currentArgumentChars = baseArgumentChars;

        foreach (var path in changedPaths)
        {
            var pathArgumentChars = EstimateSubRunArgumentChars(path);
            if (baseArgumentChars + pathArgumentChars > MaxSubRunArgumentChars)
                return null;

            if (current.Count > 0 && currentArgumentChars + pathArgumentChars > MaxSubRunArgumentChars)
            {
                batches.Add(current);
                current = new List<string>();
                currentArgumentChars = baseArgumentChars;
            }

            current.Add(path);
            currentArgumentChars += pathArgumentChars;
        }

        if (current.Count > 0)
            batches.Add(current);
        return batches;
    }

    private static int EstimateSubRunArgumentChars(IEnumerable<string> args)
    {
        var total = 0;
        foreach (var arg in args)
            total += EstimateSubRunArgumentChars(arg);
        return total;
    }

    private static int EstimateSubRunArgumentChars(string arg)
        => arg.Length + 1;

    private static int RunFullRescan(
        IndexCommandOptions baseOptions,
        JsonSerializerOptions jsonOptions,
        string resolvedDbPath,
        CancellationToken cancellationToken,
        string phase = "incremental")
    {
        var stopwatch = Stopwatch.StartNew();
        var args = BuildSubRunArgs(baseOptions, resolvedDbPath);
        // No --files: this is a default incremental full scan.
        // --files を付けない: 通常のインクリメンタル全件スキャン。
        return InvokeSubRunAndEmit(baseOptions, jsonOptions, args, stopwatch, "rescanned", batchSize: null, phase, batchPaths: null, cancellationToken);
    }

    private static void RecordSubRunExitCode(ref int watchExitCode, int subRunExitCode)
    {
        if (subRunExitCode != CommandExitCodes.Success)
            watchExitCode = subRunExitCode;
    }

    private static bool ShouldIgnoreWatchInternalPath(
        string projectRoot,
        string resolvedDbPath,
        string fullPath,
        bool ignoreCase,
        bool dbPathExplicit)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = Path.GetFullPath(fullPath);
        var normalizedProjectRoot = Path.GetFullPath(projectRoot);
        var normalizedDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(resolvedDbPath));

        var defaultDataDir = Path.Combine(normalizedProjectRoot, ".cdidx");
        var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
        if (!dbPathExplicit && !string.IsNullOrEmpty(dbDirectory)
            && !IsSamePath(defaultDataDir, dbDirectory, comparison)
            && IsSameOrUnderDirectory(dbDirectory, normalizedPath, comparison))
        {
            return true;
        }

        if (IsSamePath(normalizedPath, normalizedDbPath, comparison))
            return true;

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (IsSamePath(normalizedPath, normalizedDbPath + suffix, comparison))
                return true;
        }

        var lockPath = IndexLock.GetLockPath(normalizedDbPath);
        if (IsSamePath(normalizedPath, lockPath, comparison)
            || IsSamePath(normalizedPath, IndexLock.GetInfoPath(lockPath), comparison)
            || normalizedPath.StartsWith(lockPath + ".", comparison))
        {
            return true;
        }

        return false;
    }

    private static WatchPathDisposition ClassifyWatchPath(
        string projectRoot,
        string resolvedDbPath,
        string fullPath,
        bool ignoreCase,
        bool dbPathExplicit,
        FileIndexer fileIndexer)
    {
        var invalidation = FileIndexer.ClassifyIndexInputInvalidation(projectRoot, fullPath);
        if (invalidation != FileIndexer.IndexInputInvalidationKind.None)
            return WatchPathDisposition.Reconcile;

        if (ShouldIgnoreWatchInternalPath(projectRoot, resolvedDbPath, fullPath, ignoreCase, dbPathExplicit)
            || fileIndexer.ShouldSkipPath(fullPath))
        {
            return WatchPathDisposition.Ignore;
        }

        return WatchPathDisposition.Index;
    }

    private static bool IsSameOrUnderDirectory(string directory, string fullPath, StringComparison comparison)
    {
        var normalizedDirectory = Path.GetFullPath(directory);
        if (IsSamePath(normalizedDirectory, fullPath, comparison))
            return true;

        var directoryPrefix = Path.EndsInDirectorySeparator(normalizedDirectory)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryPrefix, comparison);
    }

    private static bool IsSamePath(string left, string right, StringComparison comparison)
        => string.Equals(
            TrimDirectorySeparators(left),
            TrimDirectorySeparators(right),
            comparison);

    private static string TrimDirectorySeparators(string value)
    {
        var root = Path.GetPathRoot(value);
        var trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) && !string.IsNullOrEmpty(root)
            ? root
            : trimmed;
    }

}
