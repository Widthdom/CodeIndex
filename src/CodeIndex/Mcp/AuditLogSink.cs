using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    internal const int RotationKeep = 3;                     // path, path.1, path.2

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
        using (var probe = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            _bytesWritten = probe.Length;
        }
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
                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
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
            if (evt.CallerName is { } caller)
                jw.WriteString("caller", caller);
            if (evt.CallerVersion is { } callerVersion)
                jw.WriteString("caller_version", callerVersion);
            if (evt.RequestId is { } reqId)
                jw.WriteString("request_id", reqId);

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

            if (includeValues && evt.ArgValues is { } values)
            {
                jw.WritePropertyName("arg_values");
                values.WriteTo(jw);
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
        string? ErrorType);
}
