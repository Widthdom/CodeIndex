using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

namespace CodeIndex.Mcp;

/// <summary>
/// Opt-in per-MCP-server JSONL audit log for tool invocations (#1562). Emits one
/// structured record per `tools/call` so compliance reviewers can answer "who called
/// which tool with what shape of arguments and when did it fail?" without re-running
/// the index. Argument *keys* and *lengths* are always recorded; full argument values
/// are gated behind an opt-in flag because cdidx queries can contain literal source
/// snippets or secret-shaped strings. Best-effort: any IO failure is swallowed so
/// audit emission cannot break the underlying tool call.
/// オプトインの MCP ツール呼び出し監査ログ (#1562)。`tools/call` ごとに 1 レコードを書き出し、
/// コンプライアンス監査で「誰が・どんな引数形で・いつ呼び出して失敗したか」を後追いできるよう
/// にする。引数の *キー* と *長さ* は常に記録するが、引数の *値* は明示フラグでのみ含める
/// （cdidx の検索クエリにはソース片や secret 風の文字列が含まれうるため）。IO 失敗時は
/// ベストエフォートで握り潰し、ツール本体を壊さない。
/// </summary>
internal sealed class AuditLogSink : IDisposable
{
    internal const long DefaultMaxBytes = 50L * 1024 * 1024; // 50 MiB
    internal const long MinMaxBytes = 4 * 1024;              // 4 KiB
    internal const long MaxMaxBytes = 1024L * 1024 * 1024;   // 1 GiB
    internal const int RotationKeep = 3;                     // path, path.1, path.2
    internal const string RedactedValue = "[REDACTED]";
    internal const string TruncatedValue = "[TRUNCATED]";
    internal const int MaxArgValueDepth = 8;
    internal const int MaxArgValueProperties = 64;
    internal const int MaxArgValueArrayItems = 64;
    internal const int MaxArgValueTotalNodes = 512;
    internal const int MaxArgValueStringChars = 512;
    internal const int MaxSecretValueScanChars = MaxArgValueStringChars + 256;
    internal const int MaxArgValuesSerializedBytes = 16 * 1024;
    internal const int MaxAuditArgumentCount = 64;
    internal const int MaxAuditArgumentKeyChars = McpBoundedText.MaxDiagnosticDisplayChars;
    internal const int MaxRequestIdChars = 256;
    internal const int MaxSerializedEventBytes = 64 * 1024;
    internal const int DefaultQueueCapacity = 1024;
    internal const int MaxConfiguredQueueCapacity = 16 * 1024;
    private static readonly TimeSpan DisposeWriterTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex SecretValuePattern = new(
        "(?i)(github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16}|\\bBearer\\s+[A-Za-z0-9._~+/=\\-]{16,}(?=$|[^A-Za-z0-9._~+/=\\-])|://[^/\\s:@]+:[^/\\s:@]+@|(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|authorization)=[^&\\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeoutPolicy.RedactionRegexTimeout);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly bool _includeValues;
    private readonly int _queueCapacity;
    private readonly Channel<byte[]> _recordQueue;
    private readonly Task _writerTask;
    private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
    private readonly ManualResetEventSlim _idleEvent = new(initialState: true);
    private long _bytesWritten;
    private long _pendingRecordCount;
    private long _droppedRecordCount;
    private long _queueFullDropCount;
    private long _serializationFailureCount;
    private long _writeFailureCount;
    private long _rotationFailureCount;
    private long _rotationCleanupFailureCount;
    private string? _lastDropReason;
    private string? _lastRotationFailure;
    private int _disposed;

    internal Action? BeforeWriteForTests { get; set; }

    internal sealed record AuditLogDiagnostics(
        string Path,
        bool IncludeValues,
        long MaxBytes,
        long BytesWritten,
        bool Disposed,
        int QueueCapacity,
        long QueueDepth,
        long DroppedRecordCount,
        long QueueFullDropCount,
        long SerializationFailureCount,
        long WriteFailureCount,
        long RotationFailureCount,
        long RotationCleanupFailureCount,
        bool RotationDegraded,
        string? LastDropReason,
        string? LastRotationFailure);

    internal AuditLogSink(string path, long maxBytes, bool includeValues, int? queueCapacity = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Audit log path must be non-empty.", nameof(path));
        if (maxBytes < MinMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), $"maxBytes must be >= {MinMaxBytes} bytes.");
        if (maxBytes > MaxMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), $"maxBytes must be <= {MaxMaxBytes} bytes.");

        _path = System.IO.Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _includeValues = includeValues;
        _queueCapacity = ResolveQueueCapacity(queueCapacity);
        DataDirectorySecurity.CreateSensitiveParentDirectoryForFile(_path);

        // Probe-open at construction so misconfigured paths (existing directory, read-only
        // file, denied parent) fail loudly before ProgramRunner claims auditing is on.
        // Without this the first Record() call silently swallows the IO failure and the
        // operator gets a "running with audit" message but an empty log (#1562 review).
        // 構築時に append open を試行する。既存ディレクトリや書き込み不可ファイルなど
        // 設定不備を、ProgramRunner が「audit 有効で起動」と表示する前に検出する。
        // 後で Record() が握り潰すと操作者には audit 有効に見えるがログは空、となる。
        using (var probe = PrivateLogFile.OpenAppend(_path, FileShare.ReadWrite))
        {
            _bytesWritten = probe.Length;
        }
        PrivateLogFile.TrySetPrivatePermissions(_path);
        _recordQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Producers use TryWrite, so a full queue becomes a best-effort drop with
            // queue_full diagnostics instead of blocking the MCP request path.
            // producer 側は TryWrite を使うため、満杯時は MCP request path を塞がず
            // queue_full 診断付きの best-effort drop になる。
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        _writerTask = BackgroundTaskObserver.Run(DrainQueueAsync, "cdidx-mcp-audit", "audit log writer");
    }

    /// <summary>Path the sink writes to (absolute, post normalisation).</summary>
    internal string Path => _path;

    /// <summary>Whether full argument values are echoed into each record.</summary>
    internal bool IncludeValues => _includeValues;

    /// <summary>Size threshold (bytes) at which rotation triggers after a write.</summary>
    internal long MaxBytes => _maxBytes;

    internal AuditLogDiagnostics SnapshotDiagnostics()
    {
        var bytesWritten = Volatile.Read(ref _bytesWritten);
        var droppedRecordCount = Interlocked.Read(ref _droppedRecordCount);
        var serializationFailureCount = Interlocked.Read(ref _serializationFailureCount);
        var writeFailureCount = Interlocked.Read(ref _writeFailureCount);
        var rotationFailureCount = Interlocked.Read(ref _rotationFailureCount);
        var rotationCleanupFailureCount = Interlocked.Read(ref _rotationCleanupFailureCount);
        var rotationDegraded = rotationFailureCount > 0
            || rotationCleanupFailureCount > 0
            || bytesWritten >= _maxBytes;
        return new AuditLogDiagnostics(
            _path,
            _includeValues,
            _maxBytes,
            bytesWritten,
            Volatile.Read(ref _disposed) != 0,
            _queueCapacity,
            Math.Max(0, Interlocked.Read(ref _pendingRecordCount)),
            droppedRecordCount,
            Interlocked.Read(ref _queueFullDropCount),
            serializationFailureCount,
            writeFailureCount,
            rotationFailureCount,
            rotationCleanupFailureCount,
            rotationDegraded,
            Volatile.Read(ref _lastDropReason),
            Volatile.Read(ref _lastRotationFailure));
    }

    internal void Record(AuditEvent evt)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        string line;
        try
        {
            line = SerializeEvent(evt, _includeValues);
        }
        catch (Exception ex)
        {
            // Serialization failures must not crash the MCP loop.
            RecordDropped("serialization_failure", ex);
            return;
        }

        var encoded = _utf8NoBom.GetBytes(line + "\n");
        if (Interlocked.Increment(ref _pendingRecordCount) == 1)
            _idleEvent.Reset();
        if (_recordQueue.Writer.TryWrite(encoded))
            return;

        MarkRecordCompleted();
        if (Volatile.Read(ref _disposed) == 0)
            RecordDropped("queue_full", new AuditLogQueueFullException());
    }

    private async Task DrainQueueAsync()
    {
        await foreach (var encoded in _recordQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                WriteEncodedRecord(encoded);
            }
            finally
            {
                MarkRecordCompleted();
            }
        }
    }

    private void MarkRecordCompleted()
    {
        if (Interlocked.Decrement(ref _pendingRecordCount) <= 0)
            _idleEvent.Set();
    }

    private void WriteEncodedRecord(byte[] encoded)
    {
        try
        {
            BeforeWriteForTests?.Invoke();
            // Open + write + close per record so an external `tail -F` keeps following
            // rotations and so the file is closed during rename. The queue keeps this
            // IO off the caller's hot path while preserving serialized file writes.
            // 1 レコードごとに open/write/close する。queue により呼び出し側の hot path から
            // IO を外しつつ、ファイル書き込みの直列性は維持する。
            using (var stream = PrivateLogFile.OpenAppend(_path, FileShare.ReadWrite))
            {
                stream.Write(encoded, 0, encoded.Length);
                stream.Flush();
            }
            var bytesWritten = Volatile.Read(ref _bytesWritten) + encoded.Length;
            Volatile.Write(ref _bytesWritten, bytesWritten);

            if (bytesWritten >= _maxBytes)
            {
                RotateLocked();
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a failing audit write must not break the tool call.
            // ベストエフォート: 監査出力失敗で本体呼び出しを壊さない。
            RecordDropped("write_failure", ex);
        }
    }

    /// <summary>
    /// Rotate the current file to `<path>.1`, cascading older files up to `RotationKeep` slots.
    /// `<path>.(RotationKeep-1)` is the oldest retained slot, and the previous oldest is
    /// deleted so a slow drain of the audit log cannot fill the disk.
    /// Caller must be the single audit queue writer.
    /// </summary>
    private void RotateLocked()
    {
        if (PrivateLogFile.TryRotateSlots(
            _path,
            RotationKeep,
            onFailure: ex => RecordRotationFailure("rotation_failure", ex),
            onCleanupFailure: ex => RecordRotationFailure("rotation_cleanup_failure", ex)))
            Volatile.Write(ref _bytesWritten, 0);
    }

    private void RecordDropped(string reason, Exception exception)
    {
        Interlocked.Increment(ref _droppedRecordCount);
        switch (reason)
        {
            case "serialization_failure":
                Interlocked.Increment(ref _serializationFailureCount);
                break;
            case "queue_full":
                Interlocked.Increment(ref _queueFullDropCount);
                break;
            case "write_failure":
                Interlocked.Increment(ref _writeFailureCount);
                break;
        }
        Volatile.Write(ref _lastDropReason, FormatFailureReason(reason, exception));
    }

    internal void RecordRotationFailure(string reason, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (reason)
        {
            case "rotation_failure":
                Interlocked.Increment(ref _rotationFailureCount);
                break;
            case "rotation_cleanup_failure":
                Interlocked.Increment(ref _rotationCleanupFailureCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Expected rotation_failure or rotation_cleanup_failure.");
        }
        Volatile.Write(ref _lastRotationFailure, FormatFailureReason(reason, exception));
    }

    private static string FormatFailureReason(string reason, Exception exception)
        => $"{reason}:{DiagnosticRedactor.ClassifyException(exception)}:{exception.GetType().Name}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _recordQueue.Writer.TryComplete();
        try
        {
            _writerTask.Wait(DisposeWriterTimeout);
        }
        catch
        {
            // Disposal is best-effort; audit logging must not block process shutdown.
            // dispose は best-effort。監査ログでプロセス終了を止めない。
        }
        if (_writerTask.IsCompleted)
            _idleEvent.Dispose();
    }

    internal bool WaitForIdle(TimeSpan timeout)
        => WaitForIdle(timeout, CancellationToken.None);

    internal bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Interlocked.Read(ref _pendingRecordCount) <= 0 || _idleEvent.Wait(timeout, cancellationToken);
    }

    private static int ResolveQueueCapacity(int? queueCapacity)
    {
        var capacity = queueCapacity ?? DefaultQueueCapacity;
        if (capacity <= 0 || capacity > MaxConfiguredQueueCapacity)
            throw new ArgumentOutOfRangeException(
                nameof(queueCapacity),
                capacity,
                $"Audit log queue capacity must be between 1 and {MaxConfiguredQueueCapacity.ToString(CultureInfo.InvariantCulture)}.");
        return capacity;
    }

    private sealed class AuditLogQueueFullException : Exception
    {
    }

    internal static string SerializeEvent(AuditEvent evt, bool includeValues)
    {
        evt = BoundEventScalarFields(evt);
        if (TrySerializeEventCore(evt, includeValues, out var serialized))
            return serialized;

        if (includeValues && evt.ArgValues is not null)
        {
            var fallback = evt with
            {
                ArgValues = null,
                ArgValuesTruncated = true,
                ArgValueTruncationReasons = AppendTruncationReason(evt.ArgValueTruncationReasons, "event_size_limit"),
                ArgValuesSerializedBytes = null,
                EventTruncated = true,
                EventTruncationReasons = AppendTruncationReason(evt.EventTruncationReasons, "event_size_limit"),
            };
            if (TrySerializeEventCore(fallback, includeValues: false, out serialized))
                return serialized;

            evt = fallback;
        }

        var compact = evt with
        {
            ArgKeys = Array.Empty<string>(),
            ArgLengths = Array.Empty<KeyValuePair<string, int>>(),
            ArgKeyLengths = null,
            ArgKeysTruncated = true,
            ArgKeyTruncationReasons = AppendTruncationReason(evt.ArgKeyTruncationReasons, "event_size_limit"),
            ArgKeysOmittedCount = Math.Max(evt.ArgKeysOmittedCount, evt.ArgKeys.Count),
            EventTruncated = true,
            EventTruncationReasons = AppendTruncationReason(evt.EventTruncationReasons, "event_size_limit"),
        };
        if (TrySerializeEventCore(compact, includeValues && compact.ArgValues is not null, out serialized))
            return serialized;

        var minimal = compact with
        {
            CallerName = null,
            CallerVersion = null,
            RequestId = null,
            ResultCount = null,
            ErrorType = null,
            ArgKeys = Array.Empty<string>(),
            ArgLengths = Array.Empty<KeyValuePair<string, int>>(),
            ArgValues = null,
            ArgKeyLengths = null,
            ArgValuesSerializedBytes = null,
            ArgValuesTruncated = compact.ArgValues is not null || compact.ArgValuesTruncated,
            ArgValueTruncationReasons = AppendTruncationReason(compact.ArgValueTruncationReasons, "event_size_limit"),
            EventTruncated = true,
            EventTruncationReasons = AppendTruncationReason(compact.EventTruncationReasons, "event_size_limit"),
        };
        if (TrySerializeEventCore(minimal, includeValues: false, out serialized))
            return serialized;

        return "{\"event_truncated\":true,\"event_truncation_reasons\":[\"event_size_limit\"]}";
    }

    private static bool TrySerializeEventCore(AuditEvent evt, bool includeValues, out string serialized)
    {
        serialized = string.Empty;
        using var buffer = new BoundedJsonUtf8Stream(
            MaxSerializedEventBytes,
            captureSerialized: true,
            bytes => new AuditEventByteLimitExceededException(bytes));
        try
        {
            using (var jw = new Utf8JsonWriter(buffer, LocalJsonlJsonWriterOptions.Create()))
            {
                WriteEventCore(jw, evt, includeValues);
            }
            serialized = buffer.GetCapturedString() ?? string.Empty;
            return true;
        }
        catch (AuditEventByteLimitExceededException)
        {
            return false;
        }
    }

    private static void WriteEventCore(Utf8JsonWriter jw, AuditEvent evt, bool includeValues)
    {
        jw.WriteStartObject();
        jw.WriteString("timestamp", evt.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        jw.WriteString("tool", evt.Tool);
        if (evt.ToolLength is { } toolLength)
            jw.WriteNumber("tool_length", toolLength);
        if (evt.ToolTruncated)
            jw.WriteBoolean("tool_truncated", true);
        if (evt.CallerName is { } caller)
            jw.WriteString("caller", caller);
        if (evt.CallerNameLength is { } callerLength)
            jw.WriteNumber("caller_length", callerLength);
        if (evt.CallerNameTruncated)
            jw.WriteBoolean("caller_truncated", true);
        if (evt.CallerVersion is { } callerVersion)
            jw.WriteString("caller_version", callerVersion);
        if (evt.CallerVersionLength is { } callerVersionLength)
            jw.WriteNumber("caller_version_length", callerVersionLength);
        if (evt.CallerVersionTruncated)
            jw.WriteBoolean("caller_version_truncated", true);
        if (evt.RequestId is { } reqId)
            jw.WriteString("request_id", reqId);
        if (evt.RequestIdLength is { } requestIdLength)
            jw.WriteNumber("request_id_length", requestIdLength);
        if (evt.RequestIdTruncated)
            jw.WriteBoolean("request_id_truncated", true);

        jw.WritePropertyName("arg_keys");
        jw.WriteStartArray();
        foreach (var key in evt.ArgKeys)
            jw.WriteStringValue(key);
        jw.WriteEndArray();

        jw.WritePropertyName("arg_lengths");
        jw.WriteStartObject();
        foreach (var kv in evt.ArgLengths)
            jw.WriteNumber(kv.Key, kv.Value);
        jw.WriteEndObject();

        var argKeysTruncated = evt.ArgKeysTruncated;
        if (evt.ArgKeyLengths is { Count: > 0 } argKeyLengths)
        {
            jw.WritePropertyName("arg_key_lengths");
            jw.WriteStartObject();
            foreach (var kv in argKeyLengths)
                jw.WriteNumber(kv.Key, kv.Value);
            jw.WriteEndObject();
            argKeysTruncated = true;
        }
        if (argKeysTruncated)
            jw.WriteBoolean("arg_keys_truncated", true);
        if (evt.ArgKeyTruncationReasons is { Count: > 0 } argKeyReasons)
        {
            jw.WritePropertyName("arg_key_truncation_reasons");
            jw.WriteStartArray();
            foreach (var reason in argKeyReasons)
                jw.WriteStringValue(reason);
            jw.WriteEndArray();
        }
        if (evt.ArgKeysOmittedCount > 0)
            jw.WriteNumber("arg_keys_omitted_count", evt.ArgKeysOmittedCount);
        if (evt.ArgKeyNamesTruncatedCount > 0)
            jw.WriteNumber("arg_key_names_truncated_count", evt.ArgKeyNamesTruncatedCount);

        if (includeValues && evt.ArgValues is { } values)
        {
            jw.WritePropertyName("arg_values");
            values.WriteTo(jw);
        }
        if (evt.ArgValuesRedacted)
            jw.WriteBoolean("arg_values_redacted", true);
        if (evt.ArgValuesTruncated)
        {
            jw.WriteBoolean("arg_values_truncated", true);
            jw.WriteNumber("arg_values_max_bytes", MaxArgValuesSerializedBytes);
            if (evt.ArgValuesSerializedBytes is { } argValuesSerializedBytes)
                jw.WriteNumber("arg_values_serialized_bytes", argValuesSerializedBytes);
            if (evt.ArgValueTruncationReasons is { Count: > 0 } reasons)
            {
                jw.WritePropertyName("arg_values_truncation_reasons");
                jw.WriteStartArray();
                foreach (var reason in reasons)
                    jw.WriteStringValue(reason);
                jw.WriteEndArray();
            }
        }

        if (evt.EventTruncated)
        {
            jw.WriteBoolean("event_truncated", true);
            jw.WriteNumber("event_max_bytes", MaxSerializedEventBytes);
            if (evt.EventTruncationReasons is { Count: > 0 } eventReasons)
            {
                jw.WritePropertyName("event_truncation_reasons");
                jw.WriteStartArray();
                foreach (var reason in eventReasons)
                    jw.WriteStringValue(reason);
                jw.WriteEndArray();
            }
        }

        if (evt.ResultCount is { } rc)
            jw.WriteNumber("result_count", rc);

        jw.WriteNumber("elapsed_ms", Math.Round(evt.ElapsedMs, 3));
        jw.WriteNumber("error_code", evt.ErrorCode);
        if (evt.ErrorType is { } et)
            jw.WriteString("error", et);
        jw.WriteEndObject();
    }

    private static IReadOnlyList<string> AppendTruncationReason(IReadOnlyList<string>? reasons, string reason)
    {
        var result = new List<string>();
        if (reasons is not null)
        {
            foreach (var existing in reasons)
            {
                if (StringComparer.Ordinal.Equals(existing, reason))
                    return reasons;
                result.Add(existing);
            }
        }
        result.Add(reason);
        return result;
    }

    private static AuditEvent BoundEventScalarFields(AuditEvent evt)
    {
        var tool = BoundAuditText(evt.Tool, McpBoundedText.MaxToolNameChars, evt.ToolLength, evt.ToolTruncated);
        var caller = BoundAuditText(evt.CallerName, McpBoundedText.MaxClientInfoChars, evt.CallerNameLength, evt.CallerNameTruncated);
        var callerVersion = BoundAuditText(evt.CallerVersion, McpBoundedText.MaxClientInfoChars, evt.CallerVersionLength, evt.CallerVersionTruncated);
        var requestId = BoundAuditText(evt.RequestId, MaxRequestIdChars, evt.RequestIdLength, evt.RequestIdTruncated);

        return evt with
        {
            Tool = tool.Text!,
            ToolLength = tool.Length,
            ToolTruncated = tool.Truncated,
            CallerName = caller.Text,
            CallerNameLength = caller.Length,
            CallerNameTruncated = caller.Truncated,
            CallerVersion = callerVersion.Text,
            CallerVersionLength = callerVersion.Length,
            CallerVersionTruncated = callerVersion.Truncated,
            RequestId = requestId.Text,
            RequestIdLength = requestId.Length,
            RequestIdTruncated = requestId.Truncated,
            ErrorType = evt.ErrorType is null
                ? null
                : McpBoundedText.ForDisplay(evt.ErrorType, McpBoundedText.MaxDiagnosticDisplayChars).Text,
        };
    }

    private static (string? Text, int? Length, bool Truncated) BoundAuditText(string? value, int maxChars, int? length, bool truncated)
    {
        if (value is null)
            return (null, length, truncated);

        var display = McpBoundedText.ForDisplay(value, maxChars);
        if (!display.Truncated)
            return (value, length, truncated);

        return (display.Text, length ?? display.OriginalLength, true);
    }

    private sealed class AuditEventByteLimitExceededException(int bytesWritten) : Exception
    {
        public int BytesWritten { get; } = bytesWritten;
    }

    internal static JsonNode? SanitizeArgValue(string key, JsonNode? value, out bool redacted)
    {
        var state = new ArgValueSanitizationState();
        var sanitized = SanitizeArgValue(key, value, state);
        redacted = state.Redacted;
        return sanitized;
    }

    internal static JsonNode? SanitizeArgValue(string key, JsonNode? value, ArgValueSanitizationState state)
        => SanitizeArgValueCore(key, value, state, depth: 0);

    private static JsonNode? SanitizeArgValueCore(string key, JsonNode? value, ArgValueSanitizationState state, int depth)
    {
        if (!state.TryReserveNode())
            return CreateTruncatedValue();

        if (IsSecretLikeKey(key))
        {
            state.MarkRedacted();
            state.TryReserveSerializedBytes(EstimateStringJsonBytes(RedactedValue));
            return JsonValue.Create(RedactedValue);
        }

        return value switch
        {
            null => ReserveNull(state),
            JsonObject obj => SanitizeObject(obj, state, depth),
            JsonArray arr => SanitizeArray(arr, state, depth),
            JsonValue jsonValue => SanitizeScalar(jsonValue, state),
            _ => null,
        };
    }

    private static JsonNode? ReserveNull(ArgValueSanitizationState state)
    {
        state.TryReserveSerializedBytes("null".Length);
        return null;
    }

    private static JsonNode SanitizeObject(JsonObject obj, ArgValueSanitizationState state, int depth)
    {
        if (depth >= MaxArgValueDepth)
        {
            state.AddTruncationReason("depth_limit");
            return CreateTruncatedValue();
        }

        if (!state.TryReserveSerializedBytes(2))
            return CreateTruncatedValue();

        var clone = new JsonObject();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var propertyCount = 0;
        foreach (var (key, value) in obj)
        {
            if (propertyCount >= MaxArgValueProperties)
            {
                state.AddTruncationReason("object_property_count_limit");
                break;
            }

            var keyDisplay = McpBoundedText.ForDisplay(key);
            if (keyDisplay.Truncated)
                state.AddTruncationReason("object_property_key_length_limit");
            var displayKey = MakeUniqueObjectDisplayKey(key, keyDisplay, usedKeys);

            if (!state.TryReservePropertyName(displayKey))
                break;

            clone[displayKey] = SanitizeArgValueCore(key, value, state, depth + 1);
            propertyCount++;
        }
        return clone;
    }

    private static JsonNode SanitizeArray(JsonArray arr, ArgValueSanitizationState state, int depth)
    {
        if (depth >= MaxArgValueDepth)
        {
            state.AddTruncationReason("depth_limit");
            return CreateTruncatedValue();
        }

        if (!state.TryReserveSerializedBytes(2))
            return CreateTruncatedValue();

        var clone = new JsonArray();
        var itemCount = 0;
        foreach (var value in arr)
        {
            if (itemCount >= MaxArgValueArrayItems)
            {
                state.AddTruncationReason("array_item_count_limit");
                break;
            }

            clone.Add(SanitizeArgValueCore(string.Empty, value, state, depth + 1));
            itemCount++;
        }
        return clone;
    }

    private static JsonNode SanitizeScalar(JsonValue value, ArgValueSanitizationState state)
    {
        if (value.TryGetValue<string>(out var text))
        {
            if (ContainsSecretValue(text))
            {
                state.MarkRedacted();
                state.TryReserveSerializedBytes(EstimateStringJsonBytes(RedactedValue));
                return JsonValue.Create(RedactedValue);
            }

            var display = McpBoundedText.ForDisplay(text, MaxArgValueStringChars);
            if (display.Truncated)
                state.AddTruncationReason("string_length_limit");
            if (!state.TryReserveSerializedBytes(EstimateStringJsonBytes(display.Text)))
                return CreateTruncatedValue();
            return JsonValue.Create(display.Text);
        }

        try
        {
            var json = value.ToJsonString();
            var jsonByteCount = Encoding.UTF8.GetByteCount(json);
            if (!state.TryReserveSerializedBytes(jsonByteCount))
                return CreateTruncatedValue();
            return BoundedJson.ParseNode(json, jsonByteCount, MaxArgValueDepth + 1) ?? CreateTruncatedValue();
        }
        catch
        {
            state.AddTruncationReason("scalar_serialization_failed");
            return CreateTruncatedValue();
        }
    }

    private static bool ContainsSecretValue(string text)
    {
        var scanTruncated = text.Length > MaxSecretValueScanChars;
        var scanText = scanTruncated ? text[..MaxSecretValueScanChars] : text;
        return RegexTimeoutPolicy.IsRedactionMatchOrFallback(
            () => SecretValuePattern.IsMatch(scanText)
                  || (scanTruncated && ContainsDisplayedUriUserInfoPrefix(scanText)));
    }

    private static bool ContainsDisplayedUriUserInfoPrefix(string scanText)
    {
        var visibleLength = Math.Min(scanText.Length, MaxArgValueStringChars);
        var searchStart = 0;
        while (searchStart < visibleLength)
        {
            var separator = scanText.IndexOf(
                "://",
                searchStart,
                visibleLength - searchStart,
                StringComparison.Ordinal);
            if (separator < 0)
                return false;

            var authorityStart = separator + "://".Length;
            for (var index = authorityStart; index < visibleLength; index++)
            {
                var character = scanText[index];
                if (IsUriAuthorityTerminator(character) || IsUriBoundaryDelimiter(character) || character == '@')
                    break;
                if (character == ':' && index > authorityStart)
                {
                    if (HasDisplayedUriUserInfoPasswordPrefix(scanText, authorityStart, index, visibleLength))
                        return true;
                    break;
                }
            }

            searchStart = authorityStart;
        }

        return false;
    }

    private static bool HasDisplayedUriUserInfoPasswordPrefix(
        string scanText,
        int authorityStart,
        int userInfoSeparator,
        int visibleLength)
    {
        var passwordStart = userInfoSeparator + 1;
        if (passwordStart >= visibleLength)
            return false;

        var sawDisplayedPassword = false;
        for (var index = passwordStart; index < scanText.Length; index++)
        {
            var character = scanText[index];
            if (character == '@')
                return sawDisplayedPassword;
            if (IsUriAuthorityTerminator(character))
                return false;
            if (IsUriBoundaryDelimiter(character))
            {
                if (LooksLikeDelimitedHostPort(scanText, authorityStart, userInfoSeparator, passwordStart, index))
                    return false;
                return sawDisplayedPassword || index == passwordStart;
            }
            if (index < visibleLength)
                sawDisplayedPassword = true;
        }

        return sawDisplayedPassword;
    }

    private static bool IsUriAuthorityTerminator(char character) =>
        char.IsWhiteSpace(character) || character is '/' or '?' or '#';

    private static bool IsUriBoundaryDelimiter(char character) =>
        character is '"' or '\'' or '<' or '>' or ')' or ']' or '}' or ',' or ';';

    private static bool LooksLikeDelimitedHostPort(
        string scanText,
        int authorityStart,
        int portSeparator,
        int portStart,
        int delimiterIndex)
    {
        if (!LooksLikeLocalOrQualifiedHost(scanText.AsSpan(authorityStart, portSeparator - authorityStart)))
            return false;
        if (delimiterIndex <= portStart)
            return false;
        for (var index = portStart; index < delimiterIndex; index++)
        {
            if (!char.IsAsciiDigit(scanText[index]))
                return false;
        }

        return true;
    }

    private static bool LooksLikeLocalOrQualifiedHost(ReadOnlySpan<char> host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Length >= 3 && host[0] == '[' && host[^1] == ']')
            return true;
        return host.Contains('.');
    }

    private static JsonValue CreateTruncatedValue() => JsonValue.Create(TruncatedValue);

    private static int EstimateStringJsonBytes(string value)
        => Encoding.UTF8.GetByteCount(value) + 2;

    private static int EstimatePropertyNameJsonBytes(string key)
        => EstimateStringJsonBytes(key) + 1;

    private static string MakeUniqueObjectDisplayKey(string rawKey, BoundedMcpText display, ISet<string> usedKeys)
    {
        if (usedKeys.Add(display.Text))
            return display.Text;

        var disambiguator = 2;
        while (true)
        {
            var suffix = "#" + disambiguator.ToString(CultureInfo.InvariantCulture);
            var candidate = ComposeObjectDisplayKeyWithSuffix(rawKey, suffix);
            if (usedKeys.Add(candidate))
                return candidate;
            disambiguator++;
        }
    }

    private static string ComposeObjectDisplayKeyWithSuffix(string rawKey, string suffix)
    {
        const int maxDisplayTextChars = McpBoundedText.MaxDiagnosticDisplayChars + 3;
        var maxPrefixChars = Math.Max(0, maxDisplayTextChars - suffix.Length - 3);
        return McpBoundedText.ForDisplay(rawKey, maxPrefixChars).Text + suffix;
    }

    private static bool IsSecretLikeKey(string key)
    {
        var normalized = NormalizeKey(key);
        return normalized.Contains("pwd", StringComparison.Ordinal)
            || normalized.Contains("auth", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("accesskey", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("sessioncookie", StringComparison.Ordinal);
    }

    private static string NormalizeKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    internal sealed class ArgValueSanitizationState
    {
        private readonly List<string> _truncationReasons = new();
        private int _nodeCount;
        private int _serializedBytes;

        internal bool Redacted { get; private set; }
        internal bool Truncated => _truncationReasons.Count > 0;
        internal IReadOnlyList<string> TruncationReasons => _truncationReasons;
        internal int SerializedBytes => _serializedBytes;

        internal void MarkRedacted() => Redacted = true;

        internal bool TryReserveNode()
        {
            if (_nodeCount >= MaxArgValueTotalNodes)
            {
                AddTruncationReason("node_count_limit");
                return false;
            }

            _nodeCount++;
            return true;
        }

        internal bool TryReserveSerializedBytes(int byteCount)
        {
            var next = _serializedBytes + Math.Max(0, byteCount);
            if (next > MaxArgValuesSerializedBytes)
            {
                AddTruncationReason("serialized_bytes_limit");
                return false;
            }

            _serializedBytes = next;
            return true;
        }

        internal bool TryReservePropertyName(string key)
            => TryReserveSerializedBytes(EstimatePropertyNameJsonBytes(key));

        internal void AddTruncationReason(string reason)
        {
            foreach (var existing in _truncationReasons)
            {
                if (StringComparer.Ordinal.Equals(existing, reason))
                    return;
            }

            _truncationReasons.Add(reason);
        }
    }

    /// <summary>
    /// Compute the per-key length sketch used by audit records. Strings → char count;
    /// arrays → element count; objects → key count; scalars (number / bool / null) → 0.
    /// 文字列は文字数、配列は要素数、オブジェクトはキー数、それ以外は 0 を返す。
    /// </summary>
    internal static int MeasureArgLength(JsonNode? node) => node switch
    {
        null => 0,
        JsonArray arr => arr.Count,
        JsonObject obj => obj.Count,
        JsonValue value when value.TryGetValue<string>(out var s) => s.Length,
        _ => 0,
    };

    /// <summary>
    /// Snapshot of an MCP tool invocation written to the audit JSONL.
    /// </summary>
    internal sealed record AuditEvent(
        DateTimeOffset Timestamp,
        string Tool,
        string? CallerName,
        string? CallerVersion,
        string? RequestId,
        IReadOnlyList<string> ArgKeys,
        IReadOnlyList<KeyValuePair<string, int>> ArgLengths,
        JsonNode? ArgValues,
        int? ResultCount,
        double ElapsedMs,
        int ErrorCode,
        string? ErrorType,
        int? ToolLength = null,
        bool ToolTruncated = false,
        IReadOnlyList<KeyValuePair<string, int>>? ArgKeyLengths = null,
        bool ArgKeysTruncated = false,
        IReadOnlyList<string>? ArgKeyTruncationReasons = null,
        int ArgKeysOmittedCount = 0,
        int ArgKeyNamesTruncatedCount = 0,
        bool ArgValuesRedacted = false,
        bool ArgValuesTruncated = false,
        IReadOnlyList<string>? ArgValueTruncationReasons = null,
        int? ArgValuesSerializedBytes = null,
        bool EventTruncated = false,
        IReadOnlyList<string>? EventTruncationReasons = null,
        int? RequestIdLength = null,
        bool RequestIdTruncated = false,
        int? CallerNameLength = null,
        bool CallerNameTruncated = false,
        int? CallerVersionLength = null,
        bool CallerVersionTruncated = false);
}
