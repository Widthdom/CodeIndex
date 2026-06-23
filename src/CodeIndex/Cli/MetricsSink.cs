using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

/// <summary>
/// Opt-in JSONL metrics emitter. Resolves the destination from an explicit `--metrics` CLI
/// path or the `CDIDX_METRICS` environment variable and writes one record per CLI command
/// (and per MCP tool call) so maintainers can detect latency regressions or throughput
/// drops without reproducing them by hand. Best-effort: any IO failure is swallowed so
/// metrics emission cannot break the underlying command (#1549).
/// オプトインのJSONLメトリクス出力。`--metrics` または `CDIDX_METRICS` から出力先を解決し、
/// CLIコマンドおよびMCPツール呼び出しごとに1レコードを書き出す。IO失敗時はベストエフォートで
/// 黙って無視し、メトリクス出力がコマンド本体を壊さないようにする (#1549)。
/// </summary>
internal static class MetricsSink
{
    private static readonly AsyncLocal<Session?> CurrentSession = new();
    internal const string EnvVarName = "CDIDX_METRICS";
    internal const long DefaultMaxBytes = 50L * 1024 * 1024;
    internal const int RotationKeep = 3;
    internal const int MaxStringFieldChars = 1024;
    internal const int MaxSerializedEventBytes = 8 * 1024;
    internal const int MaxConsecutiveFailures = 3;
    private const int MaxFailureDiagnosticChars = 192;

    internal static IDisposable? TryStart(string? explicitPath) =>
        TryStart(explicitPath, DefaultMaxBytes, CommandErrorWriter.WriteWarning);

    internal static IDisposable? TryStartForTesting(string? explicitPath, long maxBytes) =>
        TryStart(explicitPath, maxBytes, warningSink: null);

    private static IDisposable? TryStart(string? explicitPath, long maxBytes, Action<string>? warningSink)
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

            var session = new Session(fullPath, maxBytes, bytesWritten);
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
        var formatted = $"{reason}:{exception.GetType().Name}:{CommandErrorWriter.FormatSanitizedException(exception)}";
        return formatted.Length <= MaxFailureDiagnosticChars
            ? formatted
            : formatted[..MaxFailureDiagnosticChars];
    }

    internal sealed class Session : IDisposable
    {
        private readonly object _gate = new();
        private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
        private readonly long _maxBytes;
        private long _bytesWritten;
        private long _droppedEventCount;
        private long _writeFailureCount;
        private long _rotationFailureCount;
        private int _consecutiveFailureCount;
        private bool _disabledForFailures;
        private string? _lastFailure;
        private bool _disposed;

        public Session(string path, long maxBytes, long bytesWritten)
        {
            Path = path;
            _maxBytes = maxBytes;
            _bytesWritten = bytesWritten;
        }

        public string Path { get; }

        internal MetricsDiagnostics SnapshotDiagnostics()
        {
            lock (_gate)
            {
                return new MetricsDiagnostics(
                    Path,
                    _bytesWritten,
                    _disposed,
                    _disabledForFailures,
                    _consecutiveFailureCount,
                    _droppedEventCount,
                    _writeFailureCount,
                    _rotationFailureCount,
                    _lastFailure);
            }
        }

        public void Write(MetricsEvent evt)
        {
            lock (_gate)
            {
                if (_disposed || _disabledForFailures)
                    return;

                try
                {
                    var encoded = _utf8NoBom.GetBytes(SerializeEvent(evt) + Environment.NewLine);
                    if (_bytesWritten > 0 && _bytesWritten + encoded.Length > _maxBytes && !RotateLocked("rotation_failure"))
                        return;

                    using (var stream = PrivateLogFile.OpenAppend(Path, FileShare.ReadWrite))
                    {
                        stream.Write(encoded, 0, encoded.Length);
                        stream.Flush();
                    }
                    _bytesWritten += encoded.Length;
                    _consecutiveFailureCount = 0;

                    if (_bytesWritten >= _maxBytes)
                        RotateLocked("rotation_failure");
                }
                catch (Exception ex)
                {
                    // Best-effort only / ベストエフォートのみ
                    RecordFailureLocked("write_failure", ex);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                CurrentSession.Value = null;
            }
        }

        private bool RotateLocked(string reason)
        {
            var capturedFailure = default(Exception);
            if (PrivateLogFile.TryRotateSlots(
                Path,
                RotationKeep,
                onFailure: ex => capturedFailure = ex))
            {
                _bytesWritten = 0;
                _consecutiveFailureCount = 0;
                return true;
            }

            RecordFailureLocked(reason, capturedFailure ?? new IOException("metrics rotation failed"));
            return false;
        }

        private void RecordFailureLocked(string reason, Exception exception)
        {
            _droppedEventCount++;
            _consecutiveFailureCount++;
            switch (reason)
            {
                case "write_failure":
                    _writeFailureCount++;
                    break;
                case "rotation_failure":
                    _rotationFailureCount++;
                    break;
            }

            _lastFailure = FormatFailure(reason, exception);
            if (_consecutiveFailureCount >= MaxConsecutiveFailures)
                _disabledForFailures = true;
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
    long BytesWritten,
    bool Disposed,
    bool DisabledForFailures,
    int ConsecutiveFailureCount,
    long DroppedEventCount,
    long WriteFailureCount,
    long RotationFailureCount,
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
