using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

/// <summary>
/// Opt-in JSONL metrics emitter. Resolves the destination from an explicit `--metrics` CLI
/// path or the `CDIDX_METRICS` environment variable and writes one record per CLI command
/// (and per MCP tool call) so maintainers can detect latency regressions or throughput
/// drops without reproducing them by hand. Best-effort: runtime failures never break the
/// command; they emit one bounded warning and remain observable while later batches retry
/// with bounded backoff (#1549 / #4552).
/// オプトインのJSONLメトリクス出力。`--metrics` または `CDIDX_METRICS` から出力先を解決し、
/// CLIコマンドおよびMCPツール呼び出しごとに1レコードを書き出す。実行時 IO 失敗は本体コマンドを
/// 壊さず、bounded warning を一度だけ出して可観測性を保ち、後続 batch を bounded backoff で
/// 再試行する (#1549 / #4552)。
/// </summary>
internal static class MetricsSink
{
    private static readonly AsyncLocal<Session?> CurrentSession = new();
    internal const string EnvVarName = "CDIDX_METRICS";
    internal const long DefaultMaxBytes = 50L * 1024 * 1024;
    internal const int RotationKeep = 3;
    internal const int MaxStringFieldChars = 1024;
    internal const int MaxSerializedEventBytes = 8 * 1024;
    internal const int DefaultQueueCapacity = 1024;
    internal const int MaxQueueCapacity = 16 * 1024;
    internal const int MaxBatchEventCount = 64;
    private const int MaxFailureDiagnosticChars = 192;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisposeWriterTimeout = TimeSpan.FromSeconds(5);

    internal static IDisposable? TryStart(string? explicitPath) =>
        TryStart(explicitPath, DefaultMaxBytes, CommandErrorWriter.WriteWarning);

    internal static Session? TryStartForTesting(
        string? explicitPath,
        long maxBytes,
        int? queueCapacity = null,
        Action<string>? warningSink = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        TimeSpan? disposeWriterTimeout = null) =>
        TryStart(explicitPath, maxBytes, warningSink, queueCapacity, retryDelay, disposeWriterTimeout) as Session;

    private static IDisposable? TryStart(
        string? explicitPath,
        long maxBytes,
        Action<string>? warningSink,
        int? queueCapacity = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        TimeSpan? disposeWriterTimeout = null)
    {
        var path = ResolvePath(explicitPath);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            DataDirectorySecurity.CreateSensitiveParentDirectoryForFile(fullPath);

            long bytesWritten;
            using (var probe = PrivateLogFile.OpenAppend(fullPath, FileShare.ReadWrite))
            {
                bytesWritten = probe.Length;
            }
            PrivateLogFile.TrySetPrivatePermissions(fullPath);

            var session = new Session(fullPath, maxBytes, bytesWritten, warningSink, queueCapacity, retryDelay, disposeWriterTimeout);
            CurrentSession.Value = session;
            return session;
        }
        catch (Exception ex)
        {
            // Best-effort: a metrics sink that cannot open its file must not block the command.
            // メトリクス出力先が開けなくても本体コマンドはブロックしない。
            warningSink?.Invoke(
                "metrics output disabled; failed to open the configured metrics path "
                + $"({CommandErrorWriter.FormatSanitizedException(ex)}).");
            CurrentSession.Value = null;
            return null;
        }
    }

    internal static bool IsActive => CurrentSession.Value is not null;

    internal static MetricsDiagnostics? SnapshotDiagnosticsForTesting()
        => CurrentSession.Value?.SnapshotDiagnostics();

    internal static MetricsDiagnostics? SnapshotDiagnostics()
        => CurrentSession.Value?.SnapshotDiagnostics();

    internal static void Record(MetricsEvent evt)
    {
        var session = CurrentSession.Value;
        session?.Write(evt);
    }

    internal static string? ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var envValue = CdidxEnvironment.GetEnvironmentVariable(EnvVarName);
        return string.IsNullOrWhiteSpace(envValue) ? null : envValue;
    }

    private static string FormatFailure(string reason, Exception exception)
    {
        var formatted = $"{reason}:{DiagnosticRedactor.ClassifyException(exception)}:{exception.GetType().Name}";
        return formatted.Length <= MaxFailureDiagnosticChars
            ? formatted
            : formatted[..MaxFailureDiagnosticChars];
    }

    internal sealed class Session : IDisposable
    {
        private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
        private readonly long _maxBytes;
        private readonly int _queueCapacity;
        private readonly Channel<QueuedEvent> _eventQueue;
        private readonly ConcurrentDictionary<long, QueuedEvent> _pendingEvents = new();
        private readonly Task _writerTask;
        private readonly ManualResetEventSlim _idleEvent = new(initialState: true);
        private readonly CancellationTokenSource _shutdownSignal = new();
        private readonly Action<string>? _warningSink;
        private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
        private readonly TimeSpan _disposeWriterTimeout;
        private long _bytesWritten;
        private long _pendingEventCount;
        private long _queueDepth;
        private long _queuedEventCount;
        private long _writtenEventCount;
        private long _droppedEventCount;
        private long _queueFullDropCount;
        private long _serializationFailureCount;
        private long _writeFailureCount;
        private long _rotationFailureCount;
        private long _batchFlushCount;
        private long _recoveryCount;
        private long _nextRetryAtUtcTicks;
        private long _lastRecoveryAtUtcTicks;
        private long _nextEventId;
        private int _consecutiveFailureCount;
        private int _degraded;
        private int _warningEmitted;
        private string? _lastFailure;
        private int _disposed;

        public Session(
            string path,
            long maxBytes,
            long bytesWritten,
            Action<string>? warningSink,
            int? queueCapacity = null,
            Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
            TimeSpan? disposeWriterTimeout = null)
        {
            Path = path;
            _maxBytes = maxBytes;
            _bytesWritten = bytesWritten;
            _warningSink = warningSink;
            _retryDelay = retryDelay ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
            _disposeWriterTimeout = disposeWriterTimeout ?? DisposeWriterTimeout;
            if (_disposeWriterTimeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(disposeWriterTimeout));
            _queueCapacity = ResolveQueueCapacity(queueCapacity);
            _eventQueue = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(_queueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            _writerTask = BackgroundTaskObserver.Run(DrainQueueAsync, "cdidx-metrics", "metrics writer");
        }

        public string Path { get; }

        internal Action? BeforeBatchWriteForTests { get; set; }

        internal MetricsDiagnostics SnapshotDiagnostics()
        {
            var nextRetryAtTicks = Interlocked.Read(ref _nextRetryAtUtcTicks);
            var lastRecoveryAtTicks = Interlocked.Read(ref _lastRecoveryAtUtcTicks);
            return new MetricsDiagnostics(
                Path,
                _maxBytes,
                Volatile.Read(ref _bytesWritten),
                Volatile.Read(ref _disposed) != 0,
                Volatile.Read(ref _degraded) != 0,
                _queueCapacity,
                Math.Max(0, Interlocked.Read(ref _queueDepth)),
                Interlocked.Read(ref _queuedEventCount),
                Interlocked.Read(ref _writtenEventCount),
                Interlocked.Read(ref _droppedEventCount),
                Interlocked.Read(ref _queueFullDropCount),
                Interlocked.Read(ref _serializationFailureCount),
                Interlocked.Read(ref _writeFailureCount),
                Interlocked.Read(ref _rotationFailureCount),
                Interlocked.Read(ref _batchFlushCount),
                Volatile.Read(ref _consecutiveFailureCount),
                Interlocked.Read(ref _recoveryCount),
                ToTimestamp(nextRetryAtTicks),
                ToTimestamp(lastRecoveryAtTicks),
                Volatile.Read(ref _lastFailure));
        }

        public void Write(MetricsEvent evt)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            byte[] encoded;
            try
            {
                encoded = _utf8NoBom.GetBytes(SerializeEvent(evt) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _serializationFailureCount);
                RecordDrop(1, "serialization_failure", ex, warn: true);
                return;
            }

            var queuedEvent = new QueuedEvent(Interlocked.Increment(ref _nextEventId), encoded);
            _pendingEvents[queuedEvent.Id] = queuedEvent;
            if (Interlocked.Increment(ref _pendingEventCount) == 1)
                _idleEvent.Reset();
            if (_eventQueue.Writer.TryWrite(queuedEvent))
            {
                Interlocked.Increment(ref _queueDepth);
                Interlocked.Increment(ref _queuedEventCount);
                return;
            }

            MarkEventCompleted(queuedEvent);
            if (Volatile.Read(ref _disposed) == 0)
            {
                Interlocked.Increment(ref _queueFullDropCount);
                RecordDrop(1, "queue_full", new MetricsQueueFullException(), warn: true);
            }
            else
            {
                Interlocked.Increment(ref _droppedEventCount);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (ReferenceEquals(CurrentSession.Value, this))
                CurrentSession.Value = null;
            _eventQueue.Writer.TryComplete();
            _shutdownSignal.Cancel();
            try
            {
                if (!_writerTask.Wait(_disposeWriterTimeout))
                    AccountShutdownTimeoutDrops();
            }
            catch
            {
                AccountShutdownTimeoutDrops();
                // Metrics are best-effort and must not hold process shutdown indefinitely.
                // メトリクスは best-effort であり、プロセス終了を無期限に止めない。
            }
        }

        internal bool WaitForIdle(TimeSpan timeout)
            => WaitForIdle(timeout, CancellationToken.None);

        internal bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Read(ref _pendingEventCount) <= 0 || _idleEvent.Wait(timeout, cancellationToken);
        }

        private async Task DrainQueueAsync()
        {
            var batch = new List<QueuedEvent>(MaxBatchEventCount);
            while (await _eventQueue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxBatchEventCount && _eventQueue.Reader.TryRead(out var queuedEvent))
                {
                    Interlocked.Decrement(ref _queueDepth);
                    batch.Add(queuedEvent);
                }
                if (batch.Count == 0)
                    continue;

                await WaitForRetryAsync().ConfigureAwait(false);
                try
                {
                    WriteBatch(batch);
                }
                catch (Exception ex)
                {
                    // Keep the single reader alive even if a test hook or an unexpected
                    // stream implementation throws outside the normal write guards.
                    // test hook や予期しない stream 実装が通常の guard 外で失敗しても
                    // single reader を生存させる。
                    Interlocked.Increment(ref _writeFailureCount);
                    RecordRuntimeFailure("write_failure", ex, batch, 0);
                }
                finally
                {
                    for (var i = 0; i < batch.Count; i++)
                        MarkEventCompleted(batch[i]);
                }
            }
        }

        private async Task WaitForRetryAsync()
        {
            var scheduledTicks = Interlocked.Read(ref _nextRetryAtUtcTicks);
            if (scheduledTicks <= 0)
                return;

            var delay = new TimeSpan(Math.Max(0, scheduledTicks - DateTimeOffset.UtcNow.UtcTicks));
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await _retryDelay(delay, _shutdownSignal.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdownSignal.IsCancellationRequested)
                {
                    // Shutdown skips retry sleep so queued events get a bounded drain chance.
                    // 終了時は retry sleep を飛ばし、queued event の bounded drain を試みる。
                }
            }
            Interlocked.CompareExchange(ref _nextRetryAtUtcTicks, 0, scheduledTicks);
        }

        private void WriteBatch(IReadOnlyList<QueuedEvent> sourceBatch)
        {
            BeforeBatchWriteForTests?.Invoke();
            var batch = sourceBatch.Where(queuedEvent => queuedEvent.IsPending).ToList();
            if (batch.Count == 0)
                return;

            var wroteAny = false;
            var index = 0;
            while (index < batch.Count)
            {
                var currentBytes = Volatile.Read(ref _bytesWritten);
                if (currentBytes > 0 && currentBytes + batch[index].Encoded.Length > _maxBytes)
                {
                    if (!TryRotate(batch, index))
                        return;
                    currentBytes = 0;
                }

                var start = index;
                long segmentBytes = 0;
                while (index < batch.Count)
                {
                    var nextLength = batch[index].Encoded.Length;
                    if (segmentBytes > 0 && currentBytes + segmentBytes + nextLength > _maxBytes)
                        break;
                    segmentBytes += nextLength;
                    index++;
                    if (currentBytes + segmentBytes >= _maxBytes)
                        break;
                }

                try
                {
                    using (var stream = PrivateLogFile.OpenAppend(Path, FileShare.ReadWrite))
                    {
                        for (var i = start; i < index; i++)
                            stream.Write(batch[i].Encoded, 0, batch[i].Encoded.Length);
                        stream.Flush();
                    }
                    Volatile.Write(ref _bytesWritten, currentBytes + segmentBytes);
                    for (var i = start; i < index; i++)
                    {
                        if (!batch[i].TryMarkWritten())
                            continue;
                        Interlocked.Increment(ref _writtenEventCount);
                        wroteAny = true;
                    }
                    Interlocked.Increment(ref _batchFlushCount);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _writeFailureCount);
                    RecordRuntimeFailure("write_failure", ex, batch, start);
                    return;
                }

                if (Volatile.Read(ref _bytesWritten) >= _maxBytes && !TryRotate(batch, index))
                    return;
            }

            if (wroteAny)
                RecordRecoveryIfNeeded();
        }

        private bool TryRotate(IReadOnlyList<QueuedEvent> batch, int dropStart)
        {
            var capturedFailure = default(Exception);
            if (PrivateLogFile.TryRotateSlots(
                Path,
                RotationKeep,
                onFailure: ex => capturedFailure = ex))
            {
                Volatile.Write(ref _bytesWritten, 0);
                return true;
            }

            Interlocked.Increment(ref _rotationFailureCount);
            RecordRuntimeFailure(
                "rotation_failure",
                capturedFailure ?? new IOException("metrics rotation failed"),
                batch,
                dropStart);
            return false;
        }

        private void RecordRuntimeFailure(
            string reason,
            Exception exception,
            IReadOnlyList<QueuedEvent> batch,
            int dropStart)
        {
            var droppedEventCount = 0;
            for (var i = dropStart; i < batch.Count; i++)
            {
                if (batch[i].TryMarkDropped())
                    droppedEventCount++;
            }
            if (droppedEventCount != 0)
                Interlocked.Add(ref _droppedEventCount, droppedEventCount);
            var consecutiveFailures = Interlocked.Increment(ref _consecutiveFailureCount);
            Volatile.Write(ref _degraded, 1);
            Volatile.Write(ref _lastFailure, FormatFailure(reason, exception));
            var exponent = Math.Min(20, Math.Max(0, consecutiveFailures - 1));
            var retryMilliseconds = Math.Min(
                MaxRetryDelay.TotalMilliseconds,
                InitialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent));
            var nextRetryAt = DateTimeOffset.UtcNow.AddMilliseconds(retryMilliseconds);
            Interlocked.Exchange(ref _nextRetryAtUtcTicks, nextRetryAt.UtcTicks);
            WarnOnce();
        }

        private void RecordDrop(int count, string reason, Exception exception, bool warn)
        {
            Interlocked.Add(ref _droppedEventCount, count);
            Volatile.Write(ref _degraded, 1);
            Volatile.Write(ref _lastFailure, FormatFailure(reason, exception));
            if (warn)
                WarnOnce();
        }

        private void RecordRecoveryIfNeeded()
        {
            Interlocked.Exchange(ref _consecutiveFailureCount, 0);
            Interlocked.Exchange(ref _nextRetryAtUtcTicks, 0);
            if (Interlocked.Exchange(ref _degraded, 0) == 0)
                return;

            Interlocked.Increment(ref _recoveryCount);
            Interlocked.Exchange(ref _lastRecoveryAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        private void WarnOnce()
        {
            if (_warningSink is null || Interlocked.Exchange(ref _warningEmitted, 1) != 0)
                return;

            try
            {
                _warningSink("metrics output degraded; events were dropped or delayed; the background sink will keep retrying.");
            }
            catch
            {
                // Diagnostic output must never break the command or the writer loop.
            }
        }

        private void AccountShutdownTimeoutDrops()
        {
            var abandoned = 0;
            foreach (var queuedEvent in _pendingEvents.Values)
            {
                if (queuedEvent.TryMarkDropped())
                    abandoned++;
            }
            if (abandoned == 0)
                return;

            Interlocked.Add(ref _droppedEventCount, abandoned);
            Volatile.Write(ref _degraded, 1);
            Volatile.Write(ref _lastFailure, "shutdown_timeout:timeout:TimeoutException");
            WarnOnce();
        }

        private void MarkEventCompleted(QueuedEvent queuedEvent)
        {
            _pendingEvents.TryRemove(queuedEvent.Id, out _);
            if (Interlocked.Decrement(ref _pendingEventCount) <= 0)
                _idleEvent.Set();
        }

        private static int ResolveQueueCapacity(int? queueCapacity)
        {
            var capacity = queueCapacity ?? DefaultQueueCapacity;
            if (capacity <= 0 || capacity > MaxQueueCapacity)
                throw new ArgumentOutOfRangeException(
                    nameof(queueCapacity),
                    capacity,
                    $"Metrics queue capacity must be between 1 and {MaxQueueCapacity.ToString(CultureInfo.InvariantCulture)}.");
            return capacity;
        }

        private static DateTimeOffset? ToTimestamp(long utcTicks)
            => utcTicks <= 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);

        private sealed class MetricsQueueFullException : Exception
        {
        }

        private sealed class QueuedEvent(long id, byte[] encoded)
        {
            private int _outcome;

            internal long Id { get; } = id;
            internal byte[] Encoded { get; } = encoded;
            internal bool IsPending => Volatile.Read(ref _outcome) == 0;
            internal bool TryMarkWritten() => Interlocked.CompareExchange(ref _outcome, 1, 0) == 0;
            internal bool TryMarkDropped() => Interlocked.CompareExchange(ref _outcome, 2, 0) == 0;
        }
    }

    internal static string SerializeEvent(MetricsEvent evt)
    {
        var encoded = SerializeEventBytes(evt, MaxStringFieldChars);
        if (encoded.Length <= MaxSerializedEventBytes)
            return Encoding.UTF8.GetString(encoded);

        // JSON escaping can expand already-clamped strings, especially control chars.
        // Re-clamp all string fields until the serialized JSON object fits the event cap.
        var low = 0;
        var high = MaxStringFieldChars - 1;
        var best = SerializeEventBytes(evt, 0);
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var candidate = SerializeEventBytes(evt, mid);
            if (candidate.Length <= MaxSerializedEventBytes)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return Encoding.UTF8.GetString(best);
    }

    private static byte[] SerializeEventBytes(MetricsEvent evt, int maxStringChars)
    {
        using var buffer = new MemoryStream();
        using (var jw = new Utf8JsonWriter(buffer, LocalJsonlJsonWriterOptions.Create()))
        {
            jw.WriteStartObject();
            jw.WriteString("timestamp", evt.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            WriteBoundedString(jw, "tool", evt.Tool, maxStringChars);
            WriteBoundedString(jw, "source", evt.Source, maxStringChars);
            jw.WriteNumber("elapsed_ms", Math.Round(evt.ElapsedMs, 3));
            jw.WriteNumber("exit_code", evt.ExitCode);
            if (evt.Language is { } lang)
                WriteBoundedString(jw, "language", lang, maxStringChars);
            if (evt.BytesRead is { } br)
                jw.WriteNumber("bytes_read", br);
            if (evt.BytesWritten is { } bw)
                jw.WriteNumber("bytes_written", bw);
            if (evt.WalCheckpointMs is { } wal)
                jw.WriteNumber("wal_checkpoint_ms", Math.Round(wal, 3));
            if (evt.FilesIndexed is { } fi)
                jw.WriteNumber("files_indexed", fi);
            if (evt.Error is { } err)
                WriteBoundedString(jw, "error", err, maxStringChars);
            jw.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteBoundedString(Utf8JsonWriter jw, string name, string value, int maxChars)
    {
        var bounded = BoundString(value, maxChars);
        jw.WriteString(name, bounded.Text);
        if (!bounded.Truncated)
            return;

        jw.WriteNumber(name + "_length", bounded.OriginalLength);
        jw.WriteBoolean(name + "_truncated", true);
    }

    private static BoundedMetricsString BoundString(string value, int maxChars)
    {
        var safeMax = Math.Max(0, maxChars);
        if (value.Length <= safeMax)
            return new BoundedMetricsString(value, value.Length, Truncated: false);

        var end = safeMax;
        if (end > 0 && end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end]))
            end--;

        return new BoundedMetricsString(value.Substring(0, end), value.Length, Truncated: true);
    }
}

internal readonly record struct BoundedMetricsString(string Text, int OriginalLength, bool Truncated);

internal sealed record MetricsDiagnostics(
    string Path,
    long MaxBytes,
    long BytesWritten,
    bool Disposed,
    bool Degraded,
    int QueueCapacity,
    long QueueDepth,
    long QueuedEventCount,
    long WrittenEventCount,
    long DroppedEventCount,
    long QueueFullDropCount,
    long SerializationFailureCount,
    long WriteFailureCount,
    long RotationFailureCount,
    long BatchFlushCount,
    int ConsecutiveFailureCount,
    long RecoveryCount,
    DateTimeOffset? NextRetryAt,
    DateTimeOffset? LastRecoveryAt,
    string? LastFailure);

/// <summary>
/// Structured metrics record emitted to the JSONL sink. Optional fields are omitted from
/// the payload when null so consumers can grow new fields without breaking older parsers.
/// JSONLシンクに出力する構造化メトリクスレコード。null フィールドは出力しないので、
/// 新しいフィールドを追加しても古いパーサを壊さない。
/// </summary>
internal sealed record MetricsEvent(
    DateTimeOffset Timestamp,
    string Tool,
    string Source,
    double ElapsedMs,
    int ExitCode,
    string? Language = null,
    long? BytesRead = null,
    long? BytesWritten = null,
    double? WalCheckpointMs = null,
    int? FilesIndexed = null,
    string? Error = null);
