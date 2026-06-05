using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

/// <summary>
/// Integration tests for the MCP audit log wiring (#1562). Exercises McpServer with a real
/// AuditLogSink and asserts the on-disk JSONL records reflect what the wire response says.
/// MCP audit ログ配線の統合テスト (#1562)。実際の AuditLogSink を組み込んだ McpServer を駆動し、
/// ディスク上の JSONL レコードがワイヤーレスポンスと一致することを確認する。
/// </summary>
public class McpAuditLogTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _auditPath;

    public McpAuditLogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_audit_{Guid.NewGuid():N}.db");
        _auditPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_audit_{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        foreach (var p in new[] { _auditPath, _auditPath + ".1", _auditPath + ".2", _auditPath + ".3" })
        {
            if (File.Exists(p))
                File.Delete(p);
        }
    }

    private McpServer CreateServer(AuditLogSink sink) =>
        new(_dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, sink);

    private McpServer CreateServerWithFilter(AuditLogSink sink, McpToolFilter filter) =>
        new(_dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, serializeResponse: null, authenticator: null, toolFilter: filter, auditLog: sink);

    [Fact]
    public void ToolsCall_Ping_EmitsAuditRecordWithCallerFromInitialize()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);

        var init = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"claude-code","version":"9.9.9"}}}""")!;
        _ = server.HandleMessage(init);

        var ping = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
        var response = server.HandleMessage(ping)!;

        Assert.False(response["result"]!.AsObject().ContainsKey("isError")
            && response["result"]!["isError"]!.GetValue<bool>(),
            "ping must not be a tool error");

        var record = ReadOnlyRecord();
        Assert.Equal("ping", record.GetProperty("tool").GetString());
        Assert.Equal("claude-code", record.GetProperty("caller").GetString());
        Assert.Equal("9.9.9", record.GetProperty("caller_version").GetString());
        Assert.Equal(0, record.GetProperty("error_code").GetInt32());
        Assert.False(record.TryGetProperty("error", out _));
        Assert.True(record.GetProperty("elapsed_ms").GetDouble() >= 0.0);
        // ping arguments are empty so arg_keys is an empty array (not omitted).
        // ping の引数は空なので arg_keys は空配列（省略ではなく）。
        Assert.Equal(0, record.GetProperty("arg_keys").GetArrayLength());
    }

    [Fact]
    public void ToolsCall_Ping_TruncatesClientInfoCallerFields_Issue3120()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);
        var name = new string('n', McpBoundedText.MaxClientInfoChars + 25);
        var version = new string('v', McpBoundedText.MaxClientInfoChars + 25);
        var nameDisplay = McpBoundedText.ForDisplay(name, McpBoundedText.MaxClientInfoChars);
        var versionDisplay = McpBoundedText.ForDisplay(version, McpBoundedText.MaxClientInfoChars);
        var init = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = name,
                    ["version"] = version,
                },
            },
        };
        _ = server.HandleMessage(init);

        var ping = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
        _ = server.HandleMessage(ping);

        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain(name, rawLog, StringComparison.Ordinal);
        Assert.DoesNotContain(version, rawLog, StringComparison.Ordinal);
        var record = ReadOnlyRecord();
        Assert.Equal(nameDisplay.Text, record.GetProperty("caller").GetString());
        Assert.Equal(name.Length, record.GetProperty("caller_length").GetInt32());
        Assert.True(record.GetProperty("caller_truncated").GetBoolean());
        Assert.Equal(versionDisplay.Text, record.GetProperty("caller_version").GetString());
        Assert.Equal(version.Length, record.GetProperty("caller_version_length").GetInt32());
        Assert.True(record.GetProperty("caller_version_truncated").GetBoolean());
    }

    [Fact]
    public void ToolsCall_MissingToolName_StillEmitsAuditRecord()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);

        var malformed = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{}}}""")!;
        var response = server.HandleMessage(malformed)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());

        var record = ReadOnlyRecord();
        Assert.Equal("(missing)", record.GetProperty("tool").GetString());
        Assert.Equal(-32602, record.GetProperty("error_code").GetInt32());
        Assert.Equal("missing_tool_name", record.GetProperty("error").GetString());
    }

    [Fact]
    public void ToolsCall_OversizedRequestId_IsRejectedBeforeAuditRecord_Issue3104()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);
        var oversizedId = new string('r', McpServer.MaxRequestIdCharacterCount + 1);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = oversizedId,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "ping",
                ["arguments"] = new JsonObject(),
            },
        };

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_request", response["error"]!["data"]!["category"]!.GetValue<string>());
        AssertJsonNullId(response);
        if (File.Exists(_auditPath))
        {
            var rawLog = File.ReadAllText(_auditPath);
            Assert.DoesNotContain(oversizedId, rawLog, StringComparison.Ordinal);
            Assert.True(string.IsNullOrWhiteSpace(rawLog), "oversized request ids must be rejected before tool audit emission");
        }
    }

    [Fact]
    public void ToolsCall_DisabledTool_EmitsAuditRecordWithToolDisabledError()
    {
        // Regression for #1562 codex review: operator-denied tools must show up in the
        // audit log so the policy can be verified after the fact. Previously the
        // `-32601 Tool not enabled` early return bypassed TryEmitAudit, leaving disabled
        // attempts invisible even though missing/unknown tools were still captured.
        // #1562 codex レビュー回帰: オペレータが拒否したツール呼び出しも audit に
        // 残し、deny ポリシーの効果を後追いできるようにする。
        var deny = McpToolFilter.Parse(null, "index");
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServerWithFilter(sink, deny);

        var call = JsonNode.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"index","arguments":{"path":"/tmp/x"}}}""")!;
        var response = server.HandleMessage(call)!;

        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());

        var record = ReadOnlyRecord();
        Assert.Equal("index", record.GetProperty("tool").GetString());
        Assert.Equal(-32601, record.GetProperty("error_code").GetInt32());
        Assert.Equal("tool_disabled", record.GetProperty("error").GetString());
    }

    [Fact]
    public void Initialize_WithoutClientInfo_ClearsStaleCallerFields()
    {
        // Regression for #1562 codex review: a reconnect that omits clientInfo must not
        // inherit the previous client's name/version on subsequent audit records, since
        // that mis-attributes activity to the wrong caller.
        // #1562 codex レビュー回帰: clientInfo を省略した再 initialize は、以後の
        // audit レコードを直前のクライアント名で記録してはならない。
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);

        var init1 = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"claude-code","version":"1.0.0"}}}""")!;
        _ = server.HandleMessage(init1);

        var init2 = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!;
        _ = server.HandleMessage(init2);

        var ping = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
        _ = server.HandleMessage(ping);

        var record = ReadOnlyRecord();
        Assert.False(record.TryGetProperty("caller", out _),
            "caller must be cleared when subsequent initialize omits clientInfo");
        Assert.False(record.TryGetProperty("caller_version", out _),
            "caller_version must be cleared when subsequent initialize omits clientInfo");
    }

    [Fact]
    public void ToolsCall_UnknownTool_EmitsAuditRecordWithInvalidParamsCode()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);

        var unknown = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"does_not_exist","arguments":{"x":1}}}""")!;
        var response = server.HandleMessage(unknown)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());

        var record = ReadOnlyRecord();
        Assert.Equal("does_not_exist", record.GetProperty("tool").GetString());
        Assert.Equal(-32602, record.GetProperty("error_code").GetInt32());
        Assert.Equal("jsonrpc_error", record.GetProperty("error").GetString());
        Assert.Equal(1, record.GetProperty("arg_keys").GetArrayLength());
        Assert.Equal("x", record.GetProperty("arg_keys")[0].GetString());
    }

    [Fact]
    public void ToolsCall_UnknownTool_TruncatesAuditToolName_Issue3118()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);
        var toolName = new string('u', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = new JsonObject
                {
                    ["x"] = 1,
                },
            },
        };

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain(toolName, rawLog, StringComparison.Ordinal);
        var record = ReadOnlyRecord();
        Assert.Equal(display.Text, record.GetProperty("tool").GetString());
        Assert.Equal(toolName.Length, record.GetProperty("tool_length").GetInt32());
        Assert.True(record.GetProperty("tool_truncated").GetBoolean());
        Assert.Equal(-32602, record.GetProperty("error_code").GetInt32());
    }

    [Fact]
    public void ToolsCall_IncludeValues_TruncatesArgumentKeysInAuditValues_Issue3117()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);
        var argumentName = new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = new JsonObject
                {
                    ["path"] = ".",
                    [argumentName] = "value",
                },
            },
        };

        var response = server.HandleMessage(request)!;

        Assert.False(response.AsObject().ContainsKey("error"));
        Assert.NotNull(response["result"]);
        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain(argumentName, rawLog, StringComparison.Ordinal);
        var record = ReadOnlyRecord();
        Assert.Equal(display.Text, record.GetProperty("arg_keys")[1].GetString());
        Assert.Equal(argumentName.Length, record.GetProperty("arg_key_lengths").GetProperty(display.Text).GetInt32());
        Assert.True(record.GetProperty("arg_keys_truncated").GetBoolean());
        Assert.True(record.GetProperty("arg_values").TryGetProperty(display.Text, out _));
    }

    [Fact]
    public void ToolsCall_IncludeValues_EchoesArgValuesIntoRecord()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);

        var unknown = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"does_not_exist","arguments":{"query":"hello","limit":7}}}""")!;
        _ = server.HandleMessage(unknown);

        var record = ReadOnlyRecord();
        var values = record.GetProperty("arg_values");
        Assert.Equal("hello", values.GetProperty("query").GetString());
        Assert.Equal(7, values.GetProperty("limit").GetInt32());

        var lengths = record.GetProperty("arg_lengths");
        Assert.Equal(5, lengths.GetProperty("query").GetInt32());
        Assert.Equal(0, lengths.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void ToolsCall_IncludeValues_RedactsSecretLikeArgumentValues_Issue3067()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);

        var token = "ghp_abcdefghijklmnopqrstuvwxyz123456";
        var unknown = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "does_not_exist",
                ["arguments"] = new JsonObject
                {
                    ["apiToken"] = token,
                    ["query"] = "https://user:pass@example.test/repo",
                },
            },
        };
        _ = server.HandleMessage(unknown);

        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain(token, rawLog, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass", rawLog, StringComparison.Ordinal);

        var record = ReadOnlyRecord();
        var values = record.GetProperty("arg_values");
        Assert.Equal(AuditLogSink.RedactedValue, values.GetProperty("apiToken").GetString());
        Assert.Equal(AuditLogSink.RedactedValue, values.GetProperty("query").GetString());
        Assert.True(record.GetProperty("arg_values_redacted").GetBoolean());
    }

    [Fact]
    public void ToolsCall_IncludeValues_BudgetsArgumentPayload_Issue3106()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);
        var longQuery = new string('q', AuditLogSink.MaxArgValueStringChars + 25);
        var items = new JsonArray();
        for (var i = 0; i < AuditLogSink.MaxArgValueArrayItems + 3; i++)
            items.Add(i);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "does_not_exist",
                ["arguments"] = new JsonObject
                {
                    ["query"] = longQuery,
                    ["items"] = items,
                },
            },
        };

        _ = server.HandleMessage(request);

        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain(longQuery, rawLog, StringComparison.Ordinal);
        var record = ReadOnlyRecord();
        Assert.True(record.GetProperty("arg_values_truncated").GetBoolean());
        var reasons = record.GetProperty("arg_values_truncation_reasons").EnumerateArray()
            .Select(reason => reason.GetString())
            .ToArray();
        Assert.Contains("string_length_limit", reasons);
        Assert.Contains("array_item_count_limit", reasons);
        var values = record.GetProperty("arg_values");
        Assert.EndsWith("...", values.GetProperty("query").GetString(), StringComparison.Ordinal);
        Assert.Equal(AuditLogSink.MaxArgValueArrayItems, values.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void ToolsCall_IncludeValues_ChargesTopLevelArgumentKeysToValueBudget_Issue3106()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);

        var arguments = new JsonObject();
        string? lastDisplayKey = null;
        for (var i = 0; i < 64; i++)
        {
            var key = "arg" + i.ToString("D2", CultureInfo.InvariantCulture)
                + "_" + new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 25);
            lastDisplayKey = McpBoundedText.ForDisplay(key).Text;
            arguments[key] = new string('v', 200);
        }
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "does_not_exist",
                ["arguments"] = arguments,
            },
        };

        _ = server.HandleMessage(request);

        var record = ReadOnlyRecord();
        Assert.True(record.GetProperty("arg_values_truncated").GetBoolean());
        Assert.True(record.GetProperty("arg_values_serialized_bytes").GetInt32() <= AuditLogSink.MaxArgValuesSerializedBytes);
        var reasons = record.GetProperty("arg_values_truncation_reasons").EnumerateArray()
            .Select(reason => reason.GetString())
            .ToArray();
        Assert.Contains("serialized_bytes_limit", reasons);
        Assert.False(record.GetProperty("arg_values").TryGetProperty(lastDisplayKey!, out _));
    }

    [Fact]
    public void ToolsCall_CapsAuditArgumentKeyCount_Issue3237()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);
        var arguments = new JsonObject();
        for (var i = 0; i < AuditLogSink.MaxAuditArgumentCount + 3; i++)
            arguments[$"arg{i.ToString(CultureInfo.InvariantCulture)}"] = i;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "does_not_exist",
                ["arguments"] = arguments,
            },
        };

        _ = server.HandleMessage(request);

        var record = ReadOnlyRecord();
        Assert.Equal(AuditLogSink.MaxAuditArgumentCount, record.GetProperty("arg_keys").GetArrayLength());
        Assert.True(record.GetProperty("arg_keys_truncated").GetBoolean());
        Assert.Contains(record.GetProperty("arg_key_truncation_reasons").EnumerateArray(),
            reason => reason.GetString() == "arg_key_count_limit");
        Assert.False(record.GetProperty("arg_values").TryGetProperty(
            $"arg{AuditLogSink.MaxAuditArgumentCount.ToString(CultureInfo.InvariantCulture)}", out _));
    }

    [Fact]
    public void ToolsCall_MaxLengthRequestId_PreservesAuditRequestId_Issue3237()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);
        var id = new string('r', McpServer.MaxRequestIdCharacterCount);
        var serializedId = JsonSerializer.Serialize(id);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "ping",
                ["arguments"] = new JsonObject(),
            },
        };

        var response = server.HandleMessage(request)!;

        Assert.False(response.AsObject().ContainsKey("error"));
        Assert.NotNull(response["result"]);
        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain("request_id_truncated", rawLog, StringComparison.Ordinal);
        var record = ReadOnlyRecord();
        Assert.Equal(serializedId, record.GetProperty("request_id").GetString());
        Assert.False(record.TryGetProperty("request_id_length", out _));
        Assert.False(record.TryGetProperty("request_id_truncated", out _));
    }

    [Fact]
    public void ToolsCall_ValuesOmitted_ByDefault()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false);
        using var server = CreateServer(sink);

        var unknown = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"does_not_exist","arguments":{"query":"secret"}}}""")!;
        _ = server.HandleMessage(unknown);

        var record = ReadOnlyRecord();
        Assert.False(record.TryGetProperty("arg_values", out _),
            "arg_values must stay absent when include-values is off");
        Assert.Equal(6, record.GetProperty("arg_lengths").GetProperty("query").GetInt32());
    }

    [Fact]
    public void ToolsCall_IncludeValues_RedactsLongSecretLikeTopLevelArgumentKey_Issue3067()
    {
        using var sink = new AuditLogSink(_auditPath, AuditLogSink.DefaultMaxBytes, includeValues: true);
        using var server = CreateServer(sink);

        var secret = "top-secret-long-key-value";
        var secretKey = new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 10) + "Password";
        var display = McpBoundedText.ForDisplay(secretKey);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "does_not_exist",
                ["arguments"] = new JsonObject
                {
                    [secretKey] = secret,
                },
            },
        };

        _ = server.HandleMessage(request);

        var rawLog = File.ReadAllText(_auditPath);
        Assert.DoesNotContain(secret, rawLog, StringComparison.Ordinal);
        var record = ReadOnlyRecord();
        Assert.Equal(display.Text, record.GetProperty("arg_keys")[0].GetString());
        Assert.Equal(AuditLogSink.RedactedValue, record.GetProperty("arg_values").GetProperty(display.Text).GetString());
        Assert.True(record.GetProperty("arg_values_redacted").GetBoolean());
    }

    [Fact]
    public void ExtractErrorCode_NoError_ReturnsZero()
    {
        var ok = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"result":{"content":[]}}""")!;
        var (code, type) = McpServer.ExtractErrorCode(ok);
        Assert.Equal(0, code);
        Assert.Null(type);
    }

    [Fact]
    public void ExtractErrorCode_JsonRpcError_ReturnsCodeAndType()
    {
        var err = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"x"}}""")!;
        var (code, type) = McpServer.ExtractErrorCode(err);
        Assert.Equal(-32601, code);
        Assert.Equal("jsonrpc_error", type);
    }

    [Fact]
    public void ExtractErrorCode_ToolError_ReturnsPositiveOne()
    {
        var err = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"result":{"isError":true,"content":[]}}""")!;
        var (code, type) = McpServer.ExtractErrorCode(err);
        Assert.Equal(1, code);
        Assert.Equal("tool_error", type);
    }

    [Fact]
    public void ExtractResultCount_PrefersExplicitCountOverArrayLength()
    {
        var response = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"result":{"structuredContent":{"count":42,"results":[1,2,3]}}}""")!;
        Assert.Equal(42, McpServer.ExtractResultCount(response));
    }

    [Fact]
    public void ExtractResultCount_FallsBackToResultsArrayLength()
    {
        var response = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"result":{"structuredContent":{"results":[1,2,3,4]}}}""")!;
        Assert.Equal(4, McpServer.ExtractResultCount(response));
    }

    [Fact]
    public void ExtractResultCount_ReturnsNullForToolError()
    {
        var response = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"result":{"isError":true,"structuredContent":{"count":5}}}""")!;
        Assert.Null(McpServer.ExtractResultCount(response));
    }

    [Fact]
    public void SanitizeArgs_NullArgs_ReturnsEmptyTriple()
    {
        var (keys, lengths, keyLengths, echo) = McpServer.SanitizeArgs(null, includeValues: true);
        Assert.Empty(keys);
        Assert.Empty(lengths);
        Assert.Empty(keyLengths);
        Assert.Null(echo);
    }

    [Fact]
    public void SanitizeArgs_IncludeValuesFalse_ReturnsKeysAndLengthsOnly()
    {
        var args = JsonNode.Parse("""{"query":"hello","items":[1,2,3]}""");
        var (keys, lengths, keyLengths, echo) = McpServer.SanitizeArgs(args, includeValues: false);
        Assert.Equal(new[] { "query", "items" }, keys);
        Assert.Equal(5, lengths.Single(kv => kv.Key == "query").Value);
        Assert.Equal(3, lengths.Single(kv => kv.Key == "items").Value);
        Assert.Empty(keyLengths);
        Assert.Null(echo);
    }

    [Fact]
    public void SanitizeArgs_IncludeValuesTrue_DeepClonesArgs()
    {
        var args = JsonNode.Parse("""{"query":"hello"}""")!.AsObject();
        var (_, _, _, echo) = McpServer.SanitizeArgs(args, includeValues: true);
        Assert.NotNull(echo);
        // Mutating the source must not change the cloned echo.
        // 元データを書き換えてもクローンに影響しないこと。
        args["query"] = "MUTATED";
        Assert.Equal("hello", echo!["query"]!.GetValue<string>());
    }

    private JsonElement ReadOnlyRecord()
    {
        var lines = File.ReadAllLines(_auditPath);
        Assert.Single(lines);
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    private static void AssertJsonNullId(JsonNode response)
    {
        Assert.True(response.AsObject().ContainsKey("id"), "JSON-RPC error responses must include id:null");
        Assert.Null(response["id"]);
    }
}
