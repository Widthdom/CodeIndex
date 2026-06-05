using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public class AuditLogSinkTests
{
    [Fact]
    public void SerializeEvent_WritesRequiredFieldsAndOmitsOptionals()
    {
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero),
            Tool: "search",
            CallerName: null,
            CallerVersion: null,
            RequestId: null,
            ArgKeys: new[] { "query", "limit" },
            ArgLengths: new[]
            {
                new KeyValuePair<string, int>("query", 12),
                new KeyValuePair<string, int>("limit", 0),
            },
            ArgValues: null,
            ResultCount: 4,
            ElapsedMs: 12.345,
            ErrorCode: 0,
            ErrorType: null);

        var json = AuditLogSink.SerializeEvent(evt, includeValues: false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("search", root.GetProperty("tool").GetString());
        Assert.False(root.TryGetProperty("caller", out _));
        Assert.False(root.TryGetProperty("caller_version", out _));
        Assert.False(root.TryGetProperty("request_id", out _));
        Assert.False(root.TryGetProperty("arg_values", out _));
        Assert.False(root.TryGetProperty("error", out _));

        Assert.Equal(2, root.GetProperty("arg_keys").GetArrayLength());
        Assert.Equal("query", root.GetProperty("arg_keys")[0].GetString());
        Assert.Equal(12, root.GetProperty("arg_lengths").GetProperty("query").GetInt32());
        Assert.Equal(0, root.GetProperty("arg_lengths").GetProperty("limit").GetInt32());
        Assert.Equal(4, root.GetProperty("result_count").GetInt32());
        Assert.Equal(12.345, root.GetProperty("elapsed_ms").GetDouble());
        Assert.Equal(0, root.GetProperty("error_code").GetInt32());
    }

    [Fact]
    public void SerializeEvent_IncludesCallerAndErrorWhenSet()
    {
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: new DateTimeOffset(2026, 5, 16, 0, 0, 0, TimeSpan.Zero),
            Tool: "definition",
            CallerName: "claude-code",
            CallerVersion: "1.2.3",
            RequestId: "42",
            ArgKeys: new[] { "query" },
            ArgLengths: new[] { new KeyValuePair<string, int>("query", 5) },
            ArgValues: null,
            ResultCount: null,
            ElapsedMs: 1.0,
            ErrorCode: -32602,
            ErrorType: "jsonrpc_error");

        var json = AuditLogSink.SerializeEvent(evt, includeValues: false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("claude-code", root.GetProperty("caller").GetString());
        Assert.Equal("1.2.3", root.GetProperty("caller_version").GetString());
        Assert.Equal("42", root.GetProperty("request_id").GetString());
        Assert.Equal(-32602, root.GetProperty("error_code").GetInt32());
        Assert.Equal("jsonrpc_error", root.GetProperty("error").GetString());
        Assert.False(root.TryGetProperty("result_count", out _));
    }

    [Fact]
    public void SerializeEvent_OmitsArgValues_WhenIncludeValuesDisabled()
    {
        var args = JsonNode.Parse("""{"query":"secret","limit":3}""");
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Tool: "search",
            CallerName: null,
            CallerVersion: null,
            RequestId: null,
            ArgKeys: new[] { "query", "limit" },
            ArgLengths: new[]
            {
                new KeyValuePair<string, int>("query", 6),
                new KeyValuePair<string, int>("limit", 0),
            },
            ArgValues: args,
            ResultCount: 0,
            ElapsedMs: 1.0,
            ErrorCode: 0,
            ErrorType: null);

        var json = AuditLogSink.SerializeEvent(evt, includeValues: false);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("arg_values", out _));
    }

    [Fact]
    public void SerializeEvent_IncludesArgValues_WhenIncludeValuesEnabled()
    {
        var args = JsonNode.Parse("""{"query":"public class","limit":3}""");
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Tool: "search",
            CallerName: null,
            CallerVersion: null,
            RequestId: null,
            ArgKeys: new[] { "query", "limit" },
            ArgLengths: new[]
            {
                new KeyValuePair<string, int>("query", 12),
                new KeyValuePair<string, int>("limit", 0),
            },
            ArgValues: args,
            ResultCount: 0,
            ElapsedMs: 1.0,
            ErrorCode: 0,
            ErrorType: null);

        var json = AuditLogSink.SerializeEvent(evt, includeValues: true);
        using var doc = JsonDocument.Parse(json);
        var values = doc.RootElement.GetProperty("arg_values");
        Assert.Equal("public class", values.GetProperty("query").GetString());
        Assert.Equal(3, values.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void SerializeEvent_MarksRedactedArgValues_WhenSet_Issue3067()
    {
        var args = JsonNode.Parse("""{"token":"[REDACTED]"}""");
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Tool: "search",
            CallerName: null,
            CallerVersion: null,
            RequestId: null,
            ArgKeys: new[] { "token" },
            ArgLengths: new[] { new KeyValuePair<string, int>("token", 12) },
            ArgValues: args,
            ResultCount: 0,
            ElapsedMs: 1.0,
            ErrorCode: 0,
            ErrorType: null,
            ArgValuesRedacted: true);

        var json = AuditLogSink.SerializeEvent(evt, includeValues: true);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("arg_values_redacted").GetBoolean());
    }

    [Fact]
    public void SanitizeArgValue_RedactsSecretKeysAndTokenPatterns_Issue3067()
    {
        var args = JsonNode.Parse("""
            {
              "access_token": "plain-secret",
              "query": "token=ghp_abcdefghijklmnopqrstuvwxyz123456",
              "nested": {
                "password": "hunter2",
                "db_pwd": "hunter2",
                "auth_header": "Bearer abcdefghijklmnop"
              }
            }
            """);

        var sanitized = AuditLogSink.SanitizeArgValue("arguments", args, out var redacted)!.AsObject();

        Assert.True(redacted);
        Assert.Equal(AuditLogSink.RedactedValue, sanitized["access_token"]!.GetValue<string>());
        Assert.Equal(AuditLogSink.RedactedValue, sanitized["query"]!.GetValue<string>());
        Assert.Equal(AuditLogSink.RedactedValue, sanitized["nested"]!["password"]!.GetValue<string>());
        Assert.Equal(AuditLogSink.RedactedValue, sanitized["nested"]!["db_pwd"]!.GetValue<string>());
        Assert.Equal(AuditLogSink.RedactedValue, sanitized["nested"]!["auth_header"]!.GetValue<string>());
    }

    [Fact]
    public void SanitizeArgValue_BudgetsPayloadBeforeClone_Issue3106()
    {
        var items = new JsonArray();
        for (var i = 0; i < AuditLogSink.MaxArgValueArrayItems + 2; i++)
            items.Add(i);
        var args = new JsonObject
        {
            ["query"] = new string('x', AuditLogSink.MaxArgValueStringChars + 10),
            ["items"] = items,
        };
        var state = new AuditLogSink.ArgValueSanitizationState();

        var sanitized = AuditLogSink.SanitizeArgValue("arguments", args, state)!.AsObject();

        Assert.True(state.Truncated);
        Assert.Contains("string_length_limit", state.TruncationReasons);
        Assert.Contains("array_item_count_limit", state.TruncationReasons);
        Assert.EndsWith("...", sanitized["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(AuditLogSink.MaxArgValueArrayItems, sanitized["items"]!.AsArray().Count);
    }

    [Fact]
    public void SanitizeArgValue_TruncatesNestedObjectKeys_Issue3237()
    {
        var rawKey = new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(rawKey);
        var args = new JsonObject
        {
            [rawKey] = "value",
        };
        var state = new AuditLogSink.ArgValueSanitizationState();

        var sanitized = AuditLogSink.SanitizeArgValue("arguments", args, state)!.AsObject();

        Assert.True(state.Truncated);
        Assert.Contains("object_property_key_length_limit", state.TruncationReasons);
        Assert.False(sanitized.ContainsKey(rawKey));
        Assert.True(sanitized.ContainsKey(display.Text));
    }

    [Fact]
    public void SerializeEvent_DropsArgValues_WhenRecordExceedsEventBudget_Issue3237()
    {
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Tool: "search",
            CallerName: null,
            CallerVersion: null,
            RequestId: null,
            ArgKeys: new[] { "query" },
            ArgLengths: new[] { new KeyValuePair<string, int>("query", AuditLogSink.MaxSerializedEventBytes) },
            ArgValues: new JsonObject
            {
                ["query"] = new string('x', AuditLogSink.MaxSerializedEventBytes + 100),
            },
            ResultCount: 0,
            ElapsedMs: 1.0,
            ErrorCode: 0,
            ErrorType: null);

        var json = AuditLogSink.SerializeEvent(evt, includeValues: true);

        Assert.True(Encoding.UTF8.GetByteCount(json) <= AuditLogSink.MaxSerializedEventBytes);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("arg_values", out _));
        Assert.True(doc.RootElement.GetProperty("arg_values_truncated").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("arg_values_truncation_reasons").EnumerateArray(),
            reason => reason.GetString() == "event_size_limit");
    }

    [Fact]
    public void SerializeEvent_DropsArgKeyMetadata_WhenRecordExceedsEventBudget_Issue3237()
    {
        var keys = new List<string>();
        var lengths = new List<KeyValuePair<string, int>>();
        var keyLengths = new List<KeyValuePair<string, int>>();
        for (var i = 0; i < AuditLogSink.MaxAuditArgumentCount; i++)
        {
            var key = "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "_" + new string('\u6f22', 512);
            keys.Add(key);
            lengths.Add(new KeyValuePair<string, int>(key, i));
            keyLengths.Add(new KeyValuePair<string, int>(key, key.Length));
        }
        var evt = new AuditLogSink.AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            Tool: "search",
            CallerName: null,
            CallerVersion: null,
            RequestId: null,
            ArgKeys: keys,
            ArgLengths: lengths,
            ArgValues: null,
            ResultCount: 0,
            ElapsedMs: 1.0,
            ErrorCode: 0,
            ErrorType: null,
            ArgKeyLengths: keyLengths);

        var json = AuditLogSink.SerializeEvent(evt, includeValues: false);

        Assert.True(Encoding.UTF8.GetByteCount(json) <= AuditLogSink.MaxSerializedEventBytes);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Empty(root.GetProperty("arg_keys").EnumerateArray());
        Assert.Empty(root.GetProperty("arg_lengths").EnumerateObject());
        Assert.False(root.TryGetProperty("arg_key_lengths", out _));
        Assert.True(root.GetProperty("arg_keys_truncated").GetBoolean());
        Assert.Contains(root.GetProperty("arg_key_truncation_reasons").EnumerateArray(),
            reason => reason.GetString() == "event_size_limit");
    }

    [Fact]
    public void MeasureArgLength_ReportsTypeSpecificCounts()
    {
        Assert.Equal(0, AuditLogSink.MeasureArgLength(null));
        Assert.Equal(5, AuditLogSink.MeasureArgLength(JsonValue.Create("hello")));
        Assert.Equal(0, AuditLogSink.MeasureArgLength(JsonValue.Create(42)));
        Assert.Equal(0, AuditLogSink.MeasureArgLength(JsonValue.Create(true)));
        Assert.Equal(3, AuditLogSink.MeasureArgLength(JsonNode.Parse("[1, 2, 3]")));
        Assert.Equal(2, AuditLogSink.MeasureArgLength(JsonNode.Parse("""{"a":1,"b":2}""")));
    }

    [Fact]
    public void Record_AppendsJsonlLineToConfiguredPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var sink = new AuditLogSink(path, AuditLogSink.DefaultMaxBytes, includeValues: false);
            sink.Record(new AuditLogSink.AuditEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "ping",
                CallerName: null,
                CallerVersion: null,
                RequestId: "1",
                ArgKeys: Array.Empty<string>(),
                ArgLengths: Array.Empty<KeyValuePair<string, int>>(),
                ArgValues: null,
                ResultCount: 0,
                ElapsedMs: 0.5,
                ErrorCode: 0,
                ErrorType: null));

            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("ping", doc.RootElement.GetProperty("tool").GetString());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_OnUnixCreatesPrivateLogFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_private_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var sink = new AuditLogSink(path, AuditLogSink.DefaultMaxBytes, includeValues: false);

            Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Record_OnUnixCreatesPrivateLogFileAfterRotation()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_private_rotation_{Guid.NewGuid():N}.jsonl");
        var rotation1 = path + ".1";
        try
        {
            using var sink = new AuditLogSink(path, AuditLogSink.MinMaxBytes, includeValues: true);
            var payloadKey = new string('k', 32);
            var bigEvent = new AuditLogSink.AuditEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "search",
                CallerName: "test",
                CallerVersion: "1.0.0",
                RequestId: "1",
                ArgKeys: new[] { payloadKey },
                ArgLengths: new[] { new KeyValuePair<string, int>(payloadKey, 5000) },
                ArgValues: JsonNode.Parse($"{{\"{payloadKey}\":\"{new string('x', 5000)}\"}}"),
                ResultCount: 0,
                ElapsedMs: 1.0,
                ErrorCode: 0,
                ErrorType: null);
            var smallEvent = new AuditLogSink.AuditEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "ping",
                CallerName: null,
                CallerVersion: null,
                RequestId: "2",
                ArgKeys: Array.Empty<string>(),
                ArgLengths: Array.Empty<KeyValuePair<string, int>>(),
                ArgValues: null,
                ResultCount: 0,
                ElapsedMs: 0.5,
                ErrorCode: 0,
                ErrorType: null);

            sink.Record(bigEvent);
            sink.Record(smallEvent);

            Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(rotation1));
            Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(path));
        }
        finally
        {
            foreach (var p in new[] { path, rotation1, path + ".2" })
            {
                if (File.Exists(p))
                    File.Delete(p);
            }
        }
    }

    [Fact]
    public void Record_RotatesOnceMaxBytesExceeded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_rot_{Guid.NewGuid():N}.jsonl");
        var rotation1 = path + ".1";
        var rotation2 = path + ".2";
        try
        {
            // include-values=true ensures every record carries a large arg_values payload so
            // each Write exceeds MinMaxBytes (4 KiB) and triggers rotation. Rotation runs
            // *after* the write, so `path` is briefly absent until the next Record creates
            // it again (FileMode.Append re-opens). We assert on the rotated slots only.
            // include-values=true により arg_values が含まれ、各 Write が MinMaxBytes を超えて
            // rotation が確実に発火する。Rotation は write の後に走るので、`path` は次の
            // Record で再生成されるまで一時的に消える。検証はローテ済みスロットに限定する。
            using var sink = new AuditLogSink(path, AuditLogSink.MinMaxBytes, includeValues: true);
            var payloadKey = new string('k', 32);
            var bigEvent = new AuditLogSink.AuditEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "search",
                CallerName: "test",
                CallerVersion: "1.0.0",
                RequestId: "1",
                ArgKeys: new[] { payloadKey },
                ArgLengths: new[] { new KeyValuePair<string, int>(payloadKey, 5000) },
                ArgValues: JsonNode.Parse($"{{\"{payloadKey}\":\"{new string('x', 5000)}\"}}"),
                ResultCount: 0,
                ElapsedMs: 1.0,
                ErrorCode: 0,
                ErrorType: null);

            for (var i = 0; i < 3; i++)
                sink.Record(bigEvent);

            Assert.True(File.Exists(rotation1), "first rotation slot should exist after rotation");
            Assert.True(File.Exists(rotation2), "second rotation slot should exist after three big writes");
        }
        finally
        {
            foreach (var p in new[] { path, rotation1, rotation2, path + ".3" })
            {
                if (File.Exists(p))
                    File.Delete(p);
            }
        }
    }

    [Fact]
    public void Record_KeepsAtMostThreeFiles_DropsOldestOnRotationOverflow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_keep_{Guid.NewGuid():N}.jsonl");
        var rotation1 = path + ".1";
        var rotation2 = path + ".2";
        var rotation3 = path + ".3";
        try
        {
            using var sink = new AuditLogSink(path, AuditLogSink.MinMaxBytes, includeValues: true);
            var payloadKey = new string('k', 32);
            var bigEvent = new AuditLogSink.AuditEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "search",
                CallerName: "test",
                CallerVersion: "1.0.0",
                RequestId: "1",
                ArgKeys: new[] { payloadKey },
                ArgLengths: new[] { new KeyValuePair<string, int>(payloadKey, 5000) },
                ArgValues: JsonNode.Parse($"{{\"{payloadKey}\":\"{new string('x', 5000)}\"}}"),
                ResultCount: 0,
                ElapsedMs: 1.0,
                ErrorCode: 0,
                ErrorType: null);

            // Five large writes ⇒ five rotations. With RotationKeep=3 the oldest slot
            // (path.2 after each rotation) must be dropped, so path.3 is never created.
            // RotationKeep=3 なので path.3 は決して残らない（最古スロットが drop される）。
            for (var i = 0; i < 5; i++)
                sink.Record(bigEvent);

            Assert.True(File.Exists(rotation1));
            Assert.True(File.Exists(rotation2));
            Assert.False(File.Exists(rotation3),
                "RotationKeep=3 must drop the oldest slot rather than spilling to path.3");
        }
        finally
        {
            foreach (var p in new[] { path, rotation1, rotation2, rotation3 })
            {
                if (File.Exists(p))
                    File.Delete(p);
            }
        }
    }

    [Fact]
    public void Record_BestEffort_WhenWriteTargetIsUnwritable_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_unwritable_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var sink = new AuditLogSink(path, AuditLogSink.DefaultMaxBytes, includeValues: false);
            // Hijack the target path with a directory so FileStream(Append) cannot succeed.
            // This simulates "the audit destination became unwritable between construction
            // and the next emit"; Record must swallow the failure rather than crash the
            // surrounding MCP tool call.
            // ターゲットパスをディレクトリに差し替え、Append 用 FileStream を失敗させる。
            // 構築後に書き込み不能になったケースを擬似し、Record が握り潰すことを確認する。
            if (File.Exists(path))
                File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                var ex = Record.Exception(() => sink.Record(new AuditLogSink.AuditEvent(
                    Timestamp: DateTimeOffset.UtcNow,
                    Tool: "ping",
                    CallerName: null,
                    CallerVersion: null,
                    RequestId: "1",
                    ArgKeys: Array.Empty<string>(),
                    ArgLengths: Array.Empty<KeyValuePair<string, int>>(),
                    ArgValues: null,
                    ResultCount: 0,
                    ElapsedMs: 0.5,
                    ErrorCode: 0,
                    ErrorType: null)));
                Assert.Null(ex);
            }
            finally
            {
                Directory.Delete(path, recursive: true);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_RejectsMaxBytesBelowMin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_{Guid.NewGuid():N}.jsonl");
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var _ = new AuditLogSink(path, AuditLogSink.MinMaxBytes - 1, includeValues: false);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Constructor_FailsFast_WhenTargetPathIsADirectory()
    {
        // Regression for #1562 codex review: pre-fix, the constructor only stat'd the path
        // and ProgramRunner reported the server as started with auditing enabled even when
        // the destination could never accept writes. The first Record then silently dropped
        // events. The probe-open inside the constructor must surface the failure up front.
        // #1562 codex レビュー回帰テスト: 構築時に append open を試行し、書き込み不可な
        // パスは即座に失敗させる（旧実装は最初の Record まで失敗を握り潰していた）。
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_audit_ctor_dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            // .NET surfaces this as UnauthorizedAccessException on macOS/Linux and as
            // IOException on Windows; both inherit from SystemException. The point is
            // that *some* exception escapes the constructor instead of being deferred.
            // macOS/Linux では UnauthorizedAccessException、Windows では IOException が
            // 飛ぶ。重要なのは構築時に例外が出ること（Record まで遅延しないこと）。
            var ex = Record.Exception(() =>
            {
                using var _ = new AuditLogSink(path, AuditLogSink.DefaultMaxBytes, includeValues: false);
            });
            Assert.NotNull(ex);
            Assert.True(ex is IOException || ex is UnauthorizedAccessException,
                $"expected IOException or UnauthorizedAccessException but got {ex!.GetType().FullName}");
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}
