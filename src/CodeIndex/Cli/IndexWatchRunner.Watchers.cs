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
    internal interface IWatchBackend : IDisposable
    {
        string Name { get; }

        Task StartAsync(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken);
    }

    internal static Func<string, string, bool, IWatchBackend>? WatchBackendFactoryForTesting { get; set; }
    private static readonly TimeSpan BackendStartupStabilizationDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PollingWatchInterval = TimeSpan.FromSeconds(2);

    internal static IReadOnlyCollection<string> CapturePollingSnapshotPathsForTesting(
        string projectRoot,
        string ignoreRuleRoot,
        string resolvedDbPath,
        bool ignoreCase,
        bool dbPathExplicit,
        FileIndexer.SymlinkPolicy symlinkPolicy = FileIndexer.SymlinkPolicy.None,
        CancellationToken cancellationToken = default)
    {
        using var backend = new PollingWatchBackend(
            projectRoot,
            ignoreRuleRoot,
            resolvedDbPath,
            ignoreCase,
            dbPathExplicit,
            symlinkPolicy);
        return backend.CaptureSnapshotPaths(cancellationToken);
    }

    internal static IReadOnlyCollection<string> CapturePollingUpdatePathsForTesting(
        string projectRoot,
        string ignoreRuleRoot,
        string resolvedDbPath,
        bool ignoreCase,
        bool dbPathExplicit,
        FileIndexer.SymlinkPolicy symlinkPolicy,
        Action update,
        CancellationToken cancellationToken = default)
    {
        using var backend = new PollingWatchBackend(
            projectRoot,
            ignoreRuleRoot,
            resolvedDbPath,
            ignoreCase,
            dbPathExplicit,
            symlinkPolicy);
        return backend.CaptureUpdatePaths(update, cancellationToken);
    }

    internal static IReadOnlyCollection<string> CaptureAncestorIgnorePollingPathsForTesting(
        string projectRoot,
        string ignoreRuleRoot,
        bool ignoreCase)
        => EnumerateAncestorIgnorePaths(projectRoot, ignoreRuleRoot, ignoreCase).ToArray();

    internal static bool PollingInternalTargetPathsMatchForTesting(
        string candidatePath,
        string targetPath)
        => PollingWatchBackend.MatchesInternalTarget(candidatePath, targetPath);

    internal static bool PollingTargetPathEqualOrParentForTesting(
        string parentPath,
        string candidatePath,
        Func<string, string, bool> pathsEqualByDirectoryNamespace)
        => PollingWatchBackend.IsTargetPathEqualOrParent(
            parentPath,
            candidatePath,
            pathsEqualByDirectoryNamespace);

    private static IWatchBackend CreateWatchBackend(
        string projectRoot,
        string ignoreRuleRoot,
        string resolvedDbPath,
        bool ignoreCase,
        bool dbPathExplicit,
        FileIndexer.SymlinkPolicy symlinkPolicy,
        int attempt)
    {
        var backendOverride = WatchBackendFactoryForTesting?.Invoke(projectRoot, ignoreRuleRoot, ignoreCase);
        if (backendOverride != null)
            return backendOverride;

        if (ShouldUseFullPollingWatchBackendForTesting(OperatingSystem.IsMacOS(), attempt))
        {
            return new PollingWatchBackend(
                projectRoot,
                ignoreRuleRoot,
                resolvedDbPath,
                ignoreCase,
                dbPathExplicit,
                symlinkPolicy);
        }

        return new FileSystemWatchBackend(
            projectRoot,
            ignoreRuleRoot,
            ignoreCase,
            pollAncestorIgnorePaths: ShouldPollAncestorIgnorePaths(
                projectRoot,
                ignoreRuleRoot,
                ignoreCase,
                attempt));
    }

    private static bool ShouldPollAncestorIgnorePaths(
        string projectRoot,
        string ignoreRuleRoot,
        bool ignoreCase,
        int attempt)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var hasAncestorIgnorePaths = !IsSamePath(
            Path.GetFullPath(projectRoot),
            Path.GetFullPath(ignoreRuleRoot),
            comparison);
        return ShouldPollAncestorIgnorePathsForTesting(
            OperatingSystem.IsMacOS(),
            Environment.Version.Major,
            attempt,
            hasAncestorIgnorePaths);
    }

    internal static bool ShouldUseFullPollingWatchBackendForTesting(
        bool isMacOs,
        int attempt)
        => isMacOs && attempt > 0;

    internal static bool ShouldPollAncestorIgnorePathsForTesting(
        bool isMacOs,
        int runtimeMajorVersion,
        int attempt,
        bool hasAncestorIgnorePaths)
        => isMacOs
            && runtimeMajorVersion <= 8
            && attempt == 0
            && hasAncestorIgnorePaths;

    private static string ResolveWatchBackendName()
        => OperatingSystem.IsMacOS()
            ? "fsevents"
            : OperatingSystem.IsLinux()
                ? "inotify"
                : OperatingSystem.IsWindows()
                    ? "read_directory_changes_w"
                    : "filesystem_watcher";

    private static List<FileSystemWatcher> CreateAncestorIgnoreWatchers(
        string projectRoot,
        string ignoreRuleRoot,
        bool ignoreCase,
        Action<string> enqueue,
        Action<Exception?> reportError)
    {
        var watchers = new List<FileSystemWatcher>();
        var fullProjectRoot = TrimDirectorySeparators(Path.GetFullPath(projectRoot));
        var fullIgnoreRuleRoot = TrimDirectorySeparators(Path.GetFullPath(ignoreRuleRoot));
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (IsSamePath(fullProjectRoot, fullIgnoreRuleRoot, comparison))
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
                ancestorWatcher.Error += (_, e) => reportError(e.GetException());
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

    private static IEnumerable<string> EnumerateAncestorIgnorePaths(
        string projectRoot,
        string ignoreRuleRoot,
        bool ignoreCase)
    {
        var fullProjectRoot = TrimDirectorySeparators(Path.GetFullPath(projectRoot));
        var fullIgnoreRuleRoot = TrimDirectorySeparators(Path.GetFullPath(ignoreRuleRoot));
        var comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (IsSamePath(fullProjectRoot, fullIgnoreRuleRoot, comparison))
            yield break;

        var relativeProjectRoot = Path.GetRelativePath(fullIgnoreRuleRoot, fullProjectRoot);
        if (Path.IsPathRooted(relativeProjectRoot)
            || relativeProjectRoot == ".."
            || relativeProjectRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeProjectRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            yield break;
        }

        var directory = Directory.GetParent(fullProjectRoot);
        while (directory != null)
        {
            yield return Path.Combine(directory.FullName, ".gitignore");
            yield return Path.Combine(directory.FullName, ".cdidxignore");
            if (IsSamePath(directory.FullName, fullIgnoreRuleRoot, comparison))
                yield break;
            directory = directory.Parent;
        }
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

            var subRunExitCode = InvokeSubRunAndEmitCore(
                baseOptions,
                jsonOptions,
                args,
                stopwatch,
                "updated",
                batch.Count,
                phase,
                batch,
                cancellationToken,
                suppressUsageErrorOutput: true);
            if (subRunExitCode == CommandExitCodes.UsageError)
            {
                var rescanExitCode = RunFullRescan(
                    baseOptions,
                    jsonOptions,
                    resolvedDbPath,
                    cancellationToken,
                    phase);
                RecordSubRunExitCode(ref exitCode, rescanExitCode);
                return exitCode;
            }

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
        if (IsSamePath(defaultDataDir, normalizedPath, comparison))
            return Directory.Exists(LongPath.EnsureWindowsPrefix(normalizedPath));

        var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
        if (!dbPathExplicit && !string.IsNullOrEmpty(dbDirectory)
            && !IsSamePath(defaultDataDir, dbDirectory, comparison)
            && IsSameOrUnderDirectory(dbDirectory, normalizedPath, comparison))
        {
            return true;
        }

        foreach (var targetPath in GetWatchInternalTargetPaths(normalizedDbPath))
        {
            if (IsSamePath(normalizedPath, targetPath, comparison)
                || AtomicFileWriter.IsTempPathForTarget(targetPath, normalizedPath, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GetWatchInternalTargetPaths(string normalizedDbPath)
    {
        var lockPath = IndexLock.GetLockPath(normalizedDbPath);
        return
        [
            .. GetSqliteInternalTargetPaths(normalizedDbPath),
            lockPath,
            IndexLock.GetInfoPath(lockPath),
        ];
    }

    private static string[] GetSqliteInternalTargetPaths(string normalizedDbPath)
        =>
        [
            normalizedDbPath,
            normalizedDbPath + "-wal",
            normalizedDbPath + "-shm",
            normalizedDbPath + "-journal",
        ];

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

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = Path.GetFullPath(fullPath);
        var defaultDataDir = Path.Combine(Path.GetFullPath(projectRoot), ".cdidx");
        if (IsSamePath(defaultDataDir, normalizedPath, comparison))
        {
            return Directory.Exists(LongPath.EnsureWindowsPrefix(normalizedPath))
                ? WatchPathDisposition.Ignore
                : WatchPathDisposition.Reconcile;
        }

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

    private sealed class FileSystemWatchBackend : IWatchBackend
    {
        private readonly string _projectRoot;
        private readonly string _ignoreRuleRoot;
        private readonly bool _ignoreCase;
        private readonly bool _pollAncestorIgnorePaths;
        private FileSystemWatcher? _watcher;
        private List<FileSystemWatcher>? _ancestorIgnoreWatchers;
        private AncestorIgnorePollingWatcher? _ancestorIgnorePollingWatcher;

        internal FileSystemWatchBackend(
            string projectRoot,
            string ignoreRuleRoot,
            bool ignoreCase,
            bool pollAncestorIgnorePaths)
        {
            _projectRoot = projectRoot;
            _ignoreRuleRoot = ignoreRuleRoot;
            _ignoreCase = ignoreCase;
            _pollAncestorIgnorePaths = pollAncestorIgnorePaths;
        }

        public string Name => ResolveWatchBackendName();

        public Task StartAsync(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_watcher != null, this);
            cancellationToken.ThrowIfCancellationRequested();

            var watcher = new FileSystemWatcher(_projectRoot)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = InternalBufferSize,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
            };
            _watcher = watcher;

            watcher.Created += (_, e) => enqueue(e.FullPath);
            watcher.Changed += (_, e) => enqueue(e.FullPath);
            watcher.Deleted += (_, e) => enqueue(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                enqueue(e.OldFullPath);
                enqueue(e.FullPath);
            };
            watcher.Error += (_, e) => reportError(e.GetException());

            watcher.EnableRaisingEvents = true;
            if (_pollAncestorIgnorePaths)
            {
                _ancestorIgnorePollingWatcher = new AncestorIgnorePollingWatcher(
                    _projectRoot,
                    _ignoreRuleRoot,
                    _ignoreCase);
                _ancestorIgnorePollingWatcher.Start(enqueue, reportError, cancellationToken);
            }
            else
            {
                _ancestorIgnoreWatchers = CreateAncestorIgnoreWatchers(
                    _projectRoot,
                    _ignoreRuleRoot,
                    _ignoreCase,
                    enqueue,
                    reportError);
            }

            // FileSystemWatcher can report a fatal EventStream startup error asynchronously
            // just after EnableRaisingEvents succeeds. Keep startup provisional long enough for
            // that callback to select the fallback before the baseline begins.
            // EnableRaisingEvents 成功直後に fatal EventStream error が非同期通知される場合が
            // あるため、この安定化区間までは startup を provisional として扱う。
            Task.Delay(BackendStartupStabilizationDelay, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _ancestorIgnorePollingWatcher?.Dispose();
            _ancestorIgnorePollingWatcher = null;

            if (_ancestorIgnoreWatchers != null)
            {
                foreach (var ancestorWatcher in _ancestorIgnoreWatchers)
                {
                    try { ancestorWatcher.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                    ancestorWatcher.Dispose();
                }
                _ancestorIgnoreWatchers = null;
            }

            if (_watcher != null)
            {
                try { _watcher.EnableRaisingEvents = false; } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }

    private sealed class AncestorIgnorePollingWatcher : IDisposable
    {
        private readonly record struct FileStamp(
            bool Exists,
            long Length,
            long LastWriteUtcTicks,
            long CreationUtcTicks);

        private readonly IReadOnlyList<string> _paths;
        private readonly StringComparer _pathComparer;
        private CancellationTokenSource? _loopCancellation;
        private Task? _loopTask;
        private Dictionary<string, FileStamp>? _snapshot;
        private Action<string>? _enqueue;
        private Action<Exception?>? _reportError;
        private bool _started;

        internal AncestorIgnorePollingWatcher(
            string projectRoot,
            string ignoreRuleRoot,
            bool ignoreCase)
        {
            _paths = EnumerateAncestorIgnorePaths(projectRoot, ignoreRuleRoot, ignoreCase).ToArray();
            _pathComparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        }

        internal void Start(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_started, this);
            cancellationToken.ThrowIfCancellationRequested();

            _snapshot = CaptureSnapshot(cancellationToken);
            _enqueue = enqueue;
            _reportError = reportError;
            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = Task.Run(
                () => RunLoopAsync(_loopCancellation.Token),
                CancellationToken.None);
            _started = true;
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PollingWatchInterval, cancellationToken).ConfigureAwait(false);
                    PollOnce(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (CodeIndex.FileSystemTraversalPolicy.IsExpectedTraversalException(ex))
            {
                _reportError?.Invoke(ex);
            }
        }

        private void PollOnce(CancellationToken cancellationToken)
        {
            Dictionary<string, FileStamp> next;
            try
            {
                next = CaptureSnapshot(cancellationToken);
            }
            catch (Exception ex) when (CodeIndex.FileSystemTraversalPolicy.IsExpectedTraversalException(ex))
            {
                _reportError?.Invoke(ex);
                return;
            }

            var previous = _snapshot ?? new Dictionary<string, FileStamp>(_pathComparer);
            foreach (var (path, stamp) in next)
            {
                if (!previous.TryGetValue(path, out var priorStamp) || priorStamp != stamp)
                    _enqueue?.Invoke(path);
            }

            _snapshot = next;
        }

        private Dictionary<string, FileStamp> CaptureSnapshot(CancellationToken cancellationToken)
        {
            var snapshot = new Dictionary<string, FileStamp>(_pathComparer);
            foreach (var path in _paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot[path] = CaptureFileStamp(path);
            }

            return snapshot;
        }

        private static FileStamp CaptureFileStamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileStamp(
                        Exists: true,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        info.CreationTimeUtc.Ticks)
                    : default;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return default;
            }
        }

        public void Dispose()
        {
            var cancellation = _loopCancellation;
            _loopCancellation = null;
            if (cancellation != null)
            {
                cancellation.Cancel();
                try
                {
                    _loopTask?.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                cancellation.Dispose();
            }

            _loopTask = null;
            _snapshot = null;
            _enqueue = null;
            _reportError = null;
        }
    }

    private sealed class PollingWatchBackend : IWatchBackend
    {
        private readonly record struct FileStamp(
            long Length,
            long LastWriteUtcTicks,
            long CreationUtcTicks,
            string? LinkTarget);

        private readonly string _projectRoot;
        private readonly string _ignoreRuleRoot;
        private readonly string _resolvedDbPath;
        private readonly bool _ignoreCase;
        private readonly bool _dbPathExplicit;
        private readonly StringComparer _pathComparer;
        private readonly FileIndexer _fileIndexer;
        private CancellationTokenSource? _loopCancellation;
        private Task? _loopTask;
        private Dictionary<string, FileStamp>? _snapshot;
        private Action<string>? _enqueue;
        private Action<Exception?>? _reportError;
        private bool _started;

        internal PollingWatchBackend(
            string projectRoot,
            string ignoreRuleRoot,
            string resolvedDbPath,
            bool ignoreCase,
            bool dbPathExplicit,
            FileIndexer.SymlinkPolicy symlinkPolicy)
        {
            _projectRoot = Path.GetFullPath(projectRoot);
            _ignoreRuleRoot = Path.GetFullPath(ignoreRuleRoot);
            _resolvedDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(resolvedDbPath));
            _ignoreCase = ignoreCase;
            _dbPathExplicit = dbPathExplicit;
            _pathComparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _fileIndexer = new FileIndexer(
                _projectRoot,
                ignoreCase,
                _ignoreRuleRoot,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: symlinkPolicy,
                internalIndexDatabasePath: _resolvedDbPath);
        }

        public string Name => "polling";

        public Task StartAsync(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_started, this);
            cancellationToken.ThrowIfCancellationRequested();

            _snapshot = CaptureSnapshot(cancellationToken);
            _enqueue = enqueue;
            _reportError = reportError;
            _loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loopTask = Task.Run(
                () => RunLoopAsync(_loopCancellation.Token),
                CancellationToken.None);
            _started = true;
            return Task.CompletedTask;
        }

        private async Task RunLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PollingWatchInterval, cancellationToken).ConfigureAwait(false);
                    PollOnce(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (CodeIndex.FileSystemTraversalPolicy.IsExpectedTraversalException(ex))
            {
                _reportError?.Invoke(ex);
            }
        }

        private void PollOnce(CancellationToken cancellationToken)
        {
            Dictionary<string, FileStamp> next;
            try
            {
                next = CaptureSnapshot(cancellationToken);
            }
            catch (Exception ex) when (CodeIndex.FileSystemTraversalPolicy.IsExpectedTraversalException(ex))
            {
                _reportError?.Invoke(ex);
                return;
            }

            var previous = _snapshot ?? new Dictionary<string, FileStamp>(_pathComparer);
            foreach (var (path, stamp) in next)
            {
                if (!previous.TryGetValue(path, out var priorStamp) || priorStamp != stamp)
                    _enqueue?.Invoke(path);
            }

            foreach (var path in previous.Keys)
            {
                if (!next.ContainsKey(path))
                    _enqueue?.Invoke(path);
            }

            _snapshot = next;
        }

        internal IReadOnlyCollection<string> CaptureSnapshotPaths(CancellationToken cancellationToken)
            => CaptureSnapshot(cancellationToken).Keys.ToArray();

        internal IReadOnlyCollection<string> CaptureUpdatePaths(
            Action update,
            CancellationToken cancellationToken)
        {
            var updatedPaths = new HashSet<string>(_pathComparer);
            _snapshot = CaptureSnapshot(cancellationToken);
            _enqueue = path => updatedPaths.Add(path);
            update();
            PollOnce(cancellationToken);
            return updatedPaths.ToArray();
        }

        private Dictionary<string, FileStamp> CaptureSnapshot(CancellationToken cancellationToken)
        {
            var snapshot = new Dictionary<string, FileStamp>(_pathComparer);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(_projectRoot);

            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pendingDirectories.Pop();
                foreach (var file in CodeIndex.FileSystemTraversalPolicy.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ShouldTrackFile(file))
                        AddFileStamp(snapshot, file);
                }

                foreach (var childDirectory in CodeIndex.FileSystemTraversalPolicy.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0
                            && !_fileIndexer.ShouldSkipPath(childDirectory, isDirectory: true))
                        {
                            pendingDirectories.Push(childDirectory);
                        }
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                    }
                }
            }

            foreach (var ignorePath in EnumerateAncestorIgnorePaths(
                         _projectRoot,
                         _ignoreRuleRoot,
                         _ignoreCase))
            {
                if (!ResolvesToWatchInternalPath(ignorePath))
                    AddFileStamp(snapshot, ignorePath);
            }

            return snapshot;
        }

        private bool ShouldTrackFile(string path)
        {
            if (ResolvesToWatchInternalPath(path))
                return false;

            if (FileIndexer.ClassifyIndexInputInvalidation(_projectRoot, path)
                != FileIndexer.IndexInputInvalidationKind.None)
            {
                return true;
            }

            return !ShouldIgnoreWatchInternalPath(
                    _projectRoot,
                    _resolvedDbPath,
                    path,
                    _ignoreCase,
                    _dbPathExplicit)
                && !_fileIndexer.ShouldSkipPath(path);
        }

        private bool ResolvesToWatchInternalPath(string path)
        {
            if (!TryResolveReparsePointPaths(path, out var immediatePath, out var finalPath))
                return false;

            return IsResolvedWatchInternalPath(immediatePath)
                || IsResolvedWatchInternalPath(finalPath);
        }

        private bool IsResolvedWatchInternalPath(string resolvedPath)
        {
            var normalizedProjectRoot = Path.GetFullPath(_projectRoot);
            var normalizedDbPath = Path.GetFullPath(_resolvedDbPath);
            var defaultDataDir = Path.Combine(normalizedProjectRoot, ".cdidx");
            if (PathCasing.PathsEqualByDirectoryNamespace(defaultDataDir, resolvedPath))
                return Directory.Exists(LongPath.EnsureWindowsPrefix(resolvedPath));

            var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
            if (!_dbPathExplicit
                && !string.IsNullOrEmpty(dbDirectory)
                && !PathCasing.PathsEqualByDirectoryNamespace(defaultDataDir, dbDirectory)
                && IsTargetPathEqualOrParent(dbDirectory, resolvedPath))
            {
                return true;
            }

            if (MatchesDatabaseOwnedInternalPath(normalizedDbPath, resolvedPath))
                return true;

            var lockPath = IndexLock.GetLockPath(normalizedDbPath);
            foreach (var targetPath in new[] { lockPath, IndexLock.GetInfoPath(lockPath) })
            {
                if (MatchesInternalTarget(resolvedPath, targetPath))
                    return true;
            }

            var canonicalDbPath = FileIndexer.NormalizePathForIdentityComparison(normalizedDbPath);
            return MatchesDatabaseOwnedInternalPath(canonicalDbPath, resolvedPath);
        }

        private static bool MatchesDatabaseOwnedInternalPath(string dbPath, string candidatePath)
        {
            foreach (var targetPath in GetSqliteInternalTargetPaths(dbPath))
            {
                if (MatchesInternalTarget(candidatePath, targetPath))
                    return true;
            }

            if (IsTargetPathEqualOrParent(dbPath + ".checkpoints", candidatePath))
                return true;

            var dbDirectory = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrEmpty(dbDirectory)
                || !TryGetTargetRelativePath(
                    dbDirectory,
                    candidatePath,
                    PathCasing.PathsEqualByDirectoryNamespace,
                    out var candidateRelativePath)
                || candidateRelativePath.Length == 0)
            {
                return false;
            }

            var dbFileName = Path.GetFileName(dbPath);
            var separatorIndex = candidateRelativePath.IndexOf(Path.DirectorySeparatorChar);
            if (separatorIndex < 0 && Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
                separatorIndex = candidateRelativePath.IndexOf(Path.AltDirectorySeparatorChar);
            var candidateFileName = separatorIndex >= 0
                ? candidateRelativePath[..separatorIndex]
                : candidateRelativePath;
            var restoreTempPrefix = dbFileName + ".restore-tmp-";
            var restoreBackupPrefix = dbFileName + ".restore-backup-";
            if (candidateFileName.StartsWith(restoreTempPrefix, StringComparison.Ordinal)
                || candidateFileName.StartsWith(restoreBackupPrefix, StringComparison.Ordinal))
            {
                return true;
            }

            if (!candidateFileName.StartsWith(restoreTempPrefix, StringComparison.OrdinalIgnoreCase)
                && !candidateFileName.StartsWith(restoreBackupPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return PathCasing.IsIgnoreCase(dbDirectory);
        }

        internal static bool MatchesInternalTarget(
            string resolvedPath,
            string targetPath)
        {
            if (FileIndexer.TryGetFileIdentity(resolvedPath, out var resolvedIdentity)
                && FileIndexer.TryGetFileIdentity(targetPath, out var targetIdentity)
                && resolvedIdentity == targetIdentity)
            {
                return true;
            }

            if (MatchesInternalTargetPath(resolvedPath, targetPath))
                return true;

            var canonicalResolvedPath = FileIndexer.NormalizePathForIdentityComparison(resolvedPath);
            var canonicalTargetPath = FileIndexer.NormalizePathForIdentityComparison(targetPath);
            return MatchesInternalTargetPath(canonicalResolvedPath, canonicalTargetPath);
        }

        private static bool MatchesInternalTargetPath(
            string resolvedPath,
            string targetPath)
        {
            if (PathCasing.PathsEqualByDirectoryNamespace(resolvedPath, targetPath))
                return true;

            var targetDirectory = Path.GetDirectoryName(targetPath);
            var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrEmpty(targetDirectory)
                || string.IsNullOrEmpty(resolvedDirectory)
                || !PathCasing.PathsEqualByDirectoryNamespace(targetDirectory, resolvedDirectory))
            {
                return false;
            }

            var candidateInTargetDirectory = Path.Combine(targetDirectory, Path.GetFileName(resolvedPath));
            if (AtomicFileWriter.IsTempPathForTarget(
                    targetPath,
                    candidateInTargetDirectory,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (!AtomicFileWriter.IsTempPathForTarget(
                    targetPath,
                    candidateInTargetDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return PathCasing.IsIgnoreCase(targetDirectory);
        }

        private static bool IsTargetPathEqualOrParent(string parentPath, string candidatePath)
            => IsTargetPathEqualOrParent(
                parentPath,
                candidatePath,
                PathCasing.PathsEqualByDirectoryNamespace);

        internal static bool IsTargetPathEqualOrParent(
            string parentPath,
            string candidatePath,
            Func<string, string, bool> pathsEqualByDirectoryNamespace)
            => TryGetTargetRelativePath(
                parentPath,
                candidatePath,
                pathsEqualByDirectoryNamespace,
                out _);

        private static bool TryGetTargetRelativePath(
            string parentPath,
            string candidatePath,
            Func<string, string, bool> pathsEqualByDirectoryNamespace,
            out string relativePath)
        {
            var normalizedParent = PathCasing.NormalizeBoundaryPath(parentPath);
            var normalizedCandidate = PathCasing.NormalizeBoundaryPath(candidatePath);
            if (TryGetTargetRelativePathCore(
                normalizedParent,
                normalizedCandidate,
                pathsEqualByDirectoryNamespace,
                out relativePath))
            {
                return true;
            }

            normalizedParent = PathCasing.NormalizeBoundaryPath(
                FileIndexer.NormalizePathForIdentityComparison(normalizedParent));
            normalizedCandidate = PathCasing.NormalizeBoundaryPath(
                FileIndexer.NormalizePathForIdentityComparison(normalizedCandidate));
            return TryGetTargetRelativePathCore(
                normalizedParent,
                normalizedCandidate,
                pathsEqualByDirectoryNamespace,
                out relativePath);
        }

        private static bool TryGetTargetRelativePathCore(
            string normalizedParent,
            string normalizedCandidate,
            Func<string, string, bool> pathsEqualByDirectoryNamespace,
            out string relativePath)
        {
            relativePath = string.Empty;
            if (string.Equals(normalizedParent, normalizedCandidate, StringComparison.Ordinal))
                return true;

            var parentPrefix = Path.EndsInDirectorySeparator(normalizedParent)
                ? normalizedParent
                : normalizedParent + Path.DirectorySeparatorChar;
            var alternateParentPrefix = Path.EndsInDirectorySeparator(normalizedParent)
                ? normalizedParent
                : normalizedParent + Path.AltDirectorySeparatorChar;
            var ordinalPrefix = normalizedCandidate.StartsWith(parentPrefix, StringComparison.Ordinal)
                || normalizedCandidate.StartsWith(alternateParentPrefix, StringComparison.Ordinal);
            if (!ordinalPrefix)
            {
                var ignoreCaseEqual = string.Equals(
                    normalizedParent,
                    normalizedCandidate,
                    StringComparison.OrdinalIgnoreCase);
                var ignoreCasePrefix = normalizedCandidate.StartsWith(
                        parentPrefix,
                        StringComparison.OrdinalIgnoreCase)
                    || normalizedCandidate.StartsWith(
                        alternateParentPrefix,
                        StringComparison.OrdinalIgnoreCase);
                if (!ignoreCaseEqual && !ignoreCasePrefix)
                    return false;

                var candidateAncestor = normalizedCandidate[..normalizedParent.Length];
                if (!pathsEqualByDirectoryNamespace(normalizedParent, candidateAncestor))
                    return false;
                if (ignoreCaseEqual)
                    return true;
            }

            var suffixStart = Path.EndsInDirectorySeparator(normalizedParent)
                ? normalizedParent.Length
                : normalizedParent.Length + 1;
            relativePath = normalizedCandidate[suffixStart..];
            return true;
        }

        private static bool TryResolveReparsePointPaths(
            string path,
            out string immediatePath,
            out string finalPath)
        {
            immediatePath = string.Empty;
            finalPath = string.Empty;
            try
            {
                var attributes = File.GetAttributes(path);
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);
                if ((attributes & FileAttributes.ReparsePoint) == 0
                    && info.LinkTarget is null)
                {
                    return false;
                }

                var immediateTarget = info.ResolveLinkTarget(returnFinalTarget: false);
                var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
                if (immediateTarget?.Exists != true || finalTarget?.Exists != true)
                    return false;

                immediatePath = Path.GetFullPath(immediateTarget.FullName);
                finalPath = FileIndexer.NormalizePathForIdentityComparison(path);
                return true;
            }
            catch (Exception ex) when (CodeIndex.FileSystemTraversalPolicy.IsExpectedTraversalException(ex))
            {
                return false;
            }
        }

        private static void AddFileStamp(Dictionary<string, FileStamp> snapshot, string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    var linkTarget = info.LinkTarget;
                    var contentInfo = linkTarget is null
                        ? info
                        : info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
                    if (contentInfo?.Exists != true)
                        return;

                    snapshot[path] = new FileStamp(
                        contentInfo.Length,
                        contentInfo.LastWriteTimeUtc.Ticks,
                        contentInfo.CreationTimeUtc.Ticks,
                        linkTarget);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
            }
        }

        public void Dispose()
        {
            var cancellation = _loopCancellation;
            _loopCancellation = null;
            if (cancellation != null)
            {
                cancellation.Cancel();
                try
                {
                    _loopTask?.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                cancellation.Dispose();
            }

            _loopTask = null;
            _snapshot = null;
            _enqueue = null;
            _reportError = null;
        }
    }
}
