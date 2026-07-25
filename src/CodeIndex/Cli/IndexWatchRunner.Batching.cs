using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Thread-safe queue that coalesces FileSystemWatcher events into a single batch once the
/// stream has been quiet for the debounce interval. Extracted for unit testing without
/// touching the filesystem.
/// FileSystemWatcher イベントを debounce 期間の静穏まで蓄積し、まとめてバッチ化するスレッドセーフな
/// キュー。ファイルシステムに触れずユニットテストできるよう分離。
/// </summary>
internal sealed class FileChangeBatcher
{
    internal const int DefaultMaxPendingPaths = IndexWatchRunner.DefaultWatchPendingPathLimit;

    private readonly object _gate = new();
    private readonly HashSet<string> _pending;
    private long _lastEventTimestamp;
    private bool _hasLastEventTimestamp;
    private bool _overflowRequested;
    private string? _overflowReason;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPendingPaths;

    public FileChangeBatcher(
        TimeSpan debounce,
        TimeProvider? timeProvider = null,
        bool ignoreCase = true,
        int maxPendingPaths = DefaultMaxPendingPaths)
    {
        if (maxPendingPaths <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingPaths), "Maximum pending path count must be positive.");

        _debounce = debounce;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxPendingPaths = maxPendingPaths;
        // On case-sensitive filesystems (Linux ext4), `foo.py` and `Foo.py` are distinct files,
        // so coalescing them via OrdinalIgnoreCase would drop one rename leg and leave the
        // renamed-to file unindexed. The watch loop passes the filesystem's case sensitivity in.
        // 大小区別する FS (Linux ext4 など) では foo.py と Foo.py が別ファイルになるため、
        // OrdinalIgnoreCase で集約するとリネーム片方が落ち、リネーム先が索引されなくなる。
        _pending = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public void Add(string path)
    {
        lock (_gate)
        {
            if (_overflowRequested)
            {
                RecordEventTimestampLocked();
                return;
            }

            if (!_pending.Contains(path))
            {
                if (_pending.Count >= _maxPendingPaths)
                {
                    RequestFullRescanLocked(
                        $"pending path limit exceeded ({_maxPendingPaths.ToString("N0", CultureInfo.InvariantCulture)} paths)");
                    return;
                }

                _pending.Add(path);
            }

            RecordEventTimestampLocked();
        }
    }

    public void RequestFullRescan(string? reason = null)
    {
        lock (_gate)
        {
            RequestFullRescanLocked(reason);
        }
    }

    public bool TryDrain(out IReadOnlyList<string> batch, out bool fullRescan, out string? overflowReason)
        => TryDrainCore(requireDebounce: true, out batch, out fullRescan, out overflowReason);

    public bool TryDrainImmediately(out IReadOnlyList<string> batch, out bool fullRescan, out string? overflowReason)
        => TryDrainCore(requireDebounce: false, out batch, out fullRescan, out overflowReason);

    private bool TryDrainCore(
        bool requireDebounce,
        out IReadOnlyList<string> batch,
        out bool fullRescan,
        out string? overflowReason)
    {
        lock (_gate)
        {
            if (_pending.Count == 0 && !_overflowRequested)
            {
                batch = Array.Empty<string>();
                fullRescan = false;
                overflowReason = null;
                return false;
            }

            if (requireDebounce
                && _hasLastEventTimestamp
                && _timeProvider.GetElapsedTime(_lastEventTimestamp) < _debounce)
            {
                batch = Array.Empty<string>();
                fullRescan = false;
                overflowReason = null;
                return false;
            }

            var snapshot = new List<string>(_pending.Count);
            foreach (var path in _pending)
                snapshot.Add(path);
            batch = snapshot;
            fullRescan = _overflowRequested;
            overflowReason = _overflowReason;
            _pending.Clear();
            _overflowRequested = false;
            _overflowReason = null;
            return true;
        }
    }

    private void RequestFullRescanLocked(string? reason)
    {
        _pending.Clear();
        _overflowRequested = true;
        if (!string.IsNullOrEmpty(reason))
            _overflowReason = IndexWatchRunner.FormatWatchDiagnosticText(reason);
        RecordEventTimestampLocked();
    }

    private void RecordEventTimestampLocked()
    {
        _lastEventTimestamp = _timeProvider.GetTimestamp();
        _hasLastEventTimestamp = true;
    }
}
