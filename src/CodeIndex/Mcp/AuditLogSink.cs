using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
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
    internal const int MaxArgValuesSerializedBytes = 16 * 1024;
    internal const int MaxAuditArgumentCount = 64;
    internal const int MaxRequestIdChars = 256;
    internal const int MaxSerializedEventBytes = 64 * 1024;

    private static readonly Regex SecretValuePattern = new(
        "(?i)(github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[0-9A-Z]{16}|://[^/\\s:@]+:[^/\\s:@]+@|(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|authorization)=[^&\\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly bool _includeValues;
    private readonly Encoding _utf8NoBom = new UTF8Encoding(false);
    private long _bytesWritten;
    private bool _disposed;

    internal AuditLogSink(string path, long maxBytes, bool includeValues)
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
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

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
    }

    /// <summary>Path the sink writes to (absolute, post normalisation).</summary>
    internal string Path => _path;

    /// <summary>Whether full argument values are echoed into each record.</summary>
    internal bool IncludeValues => _includeValues;

    /// <summary>Size threshold (bytes) at which rotation triggers after a write.</summary>
    internal long MaxBytes => _maxBytes;

    internal void Record(AuditEvent evt)
    {
        if (_disposed)
            return;

        string line;
        try
        {
            line = SerializeEvent(evt, _includeValues);
        }
        catch
        {
            // Serialization failures must not crash the MCP loop.
            return;
        }

        var encoded = _utf8NoBom.GetBytes(line + "\n");
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                // Open + write + close per record so an external `tail -F` keeps following
                // rotations and so the file is closed during rename. Audit volume is bounded
                // by tool-call cadence (not loop hot paths), so the per-call open is cheap.
                // 1 レコードごとに open/write/close する。外部 `tail -F` の rotation 追従と
                // rename 中のロック回避のため。ツール呼び出し頻度はループのホットパスではない
                // ので open のコストは許容範囲。
                using (var stream = PrivateLogFile.OpenAppend(_path, FileShare.ReadWrite))
                {
                    stream.Write(encoded, 0, encoded.Length);
                    stream.Flush();
                }
                _bytesWritten += encoded.Length;

                if (_bytesWritten >= _maxBytes)
                {
                    RotateLocked();
                }
            }
            catch
            {
                // Best-effort: a failing audit write must not break the tool call.
                // ベストエフォート: 監査出力失敗で本体呼び出しを壊さない。
            }
        }
    }

    /// <summary>
    /// Rotate the current file to `<path>.1`, cascading older files up to `RotationKeep` slots.
    /// The newest record always lives in `<path>`; `<path>.(RotationKeep-1)` is the oldest retained slot.
    /// The previous oldest is deleted so a slow drain of the audit log cannot fill the disk.
    /// Caller must hold `_gate`.
    /// </summary>
    private void RotateLocked()
    {
        try
        {
            // Drop the current oldest slot so we never spill past `RotationKeep` files.
            // 最古スロットを先に削除して、合計ファイル数が RotationKeep を超えないようにする。
            SafeDelete(SlotPath(RotationKeep - 1));

            // Cascade surviving rotated slots upward (path.N → path.N+1).
            // 既存ローテーション済みスロットを 1 つずつ古い側へずらす。
            for (var slot = RotationKeep - 2; slot >= 1; slot--)
            {
                var current = SlotPath(slot);
                var next = SlotPath(slot + 1);
                var ioCurrent = LongPath.EnsureWindowsPrefix(current);
                var ioNext = LongPath.EnsureWindowsPrefix(next);
                if (!File.Exists(ioCurrent))
                    continue;
                if (File.Exists(ioNext))
                    SafeDelete(ioNext);
                File.Move(ioCurrent, ioNext);
            }

            var ioPath = LongPath.EnsureWindowsPrefix(_path);
            if (File.Exists(ioPath))
            {
                var first = SlotPath(1);
                var ioFirst = LongPath.EnsureWindowsPrefix(first);
                if (File.Exists(ioFirst))
                    SafeDelete(ioFirst);
                File.Move(ioPath, ioFirst);
            }
            _bytesWritten = 0;
        }
        catch
        {
            // Rotation failure (e.g. file locked by reader on Windows) degrades to "keep
            // writing to the existing file"; the file may exceed maxBytes but we never
            // crash the tool call.
            // rotation 失敗時は既存ファイルへの追記継続にフォールバックし、ツール呼び出しを
            // 壊さない。サイズが maxBytes を超えうるが、ベストエフォートを優先する。
        }
    }

    private string SlotPath(int slot) =>
        slot <= 0 ? _path : _path + "." + slot.ToString(CultureInfo.InvariantCulture);

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore: rotation is best-effort.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    internal static string SerializeEvent(AuditEvent evt, bool includeValues)
    {
        var serialized = SerializeEventCore(evt, includeValues);
        if (Encoding.UTF8.GetByteCount(serialized) <= MaxSerializedEventBytes)
            return serialized;

        if (includeValues && evt.ArgValues is not null)
        {
            var fallback = evt with
            {
                ArgValues = null,
                ArgValuesTruncated = true,
                ArgValueTruncationReasons = AppendTruncationReason(evt.ArgValueTruncationReasons, "event_size_limit"),
                ArgValuesSerializedBytes = null,
            };
            serialized = SerializeEventCore(fallback, includeValues: false);
            if (Encoding.UTF8.GetByteCount(serialized) <= MaxSerializedEventBytes)
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
        };
        serialized = SerializeEventCore(compact, includeValues && compact.ArgValues is not null);
        if (Encoding.UTF8.GetByteCount(serialized) <= MaxSerializedEventBytes)
            return serialized;

        return serialized;
    }

    private static string SerializeEventCore(AuditEvent evt, bool includeValues)
    {
        using var buffer = new MemoryStream();
        using (var jw = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            // Mirror MetricsSink: local-only JSONL stays human readable in tail/grep.
            // 出力は local 限定なので tail/grep で読める relaxed encoder を使う。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
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

            if (evt.ResultCount is { } rc)
                jw.WriteNumber("result_count", rc);

            jw.WriteNumber("elapsed_ms", Math.Round(evt.ElapsedMs, 3));
            jw.WriteNumber("error_code", evt.ErrorCode);
            if (evt.ErrorType is { } et)
                jw.WriteString("error", et);
            jw.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
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
            if (SecretValuePattern.IsMatch(text))
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
            if (!state.TryReserveSerializedBytes(Encoding.UTF8.GetByteCount(json)))
                return CreateTruncatedValue();
            return JsonNode.Parse(json) ?? CreateTruncatedValue();
        }
        catch
        {
            state.AddTruncationReason("scalar_serialization_failed");
            return CreateTruncatedValue();
        }
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
        bool ArgValuesRedacted = false,
        bool ArgValuesTruncated = false,
        IReadOnlyList<string>? ArgValueTruncationReasons = null,
        int? ArgValuesSerializedBytes = null,
        int? RequestIdLength = null,
        bool RequestIdTruncated = false,
        int? CallerNameLength = null,
        bool CallerNameTruncated = false,
        int? CallerVersionLength = null,
        bool CallerVersionTruncated = false);
}
