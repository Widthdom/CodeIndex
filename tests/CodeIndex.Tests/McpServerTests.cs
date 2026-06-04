using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for McpServer JSON-RPC message handling.
/// McpServerのJSON-RPCメッセージ処理のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class McpServerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _projectRoot;
    private readonly DbContext _db;
    private readonly McpServer _server;

    public McpServerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_test_{Guid.NewGuid():N}.db");
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_workspace");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();

        // Seed test data / テストデータを投入
        var writer = new DbWriter(_db.Connection);
        writer.SetMeta(DbContext.IndexedProjectRootMetaKey, _projectRoot);
        // Stamp graph + issues ready so reads trust the seeded references like a completed index run.
        // seed したデータを完了 index と同等に扱うため readiness を stamp しておく。
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
        writer.MarkCSharpSymbolNameContractReady();
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/app.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
            Checksum = "abc123",
        });
        writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 10,
            Content = "public class App { public void Run() { } }",
        }]);
        writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "class",
            Name = "App",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = "public class App { public void Run() { } }",
        },
        new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "Run",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = "public void Run() { }",
            ContainerKind = "class",
            ContainerName = "App",
            ContainerQualifiedName = "App",
        }]);

        _server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
    }

    [Fact]
    public void ProcessFrame_UsesTraceParentFromMetaAsActivityParent()
    {
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{parentTraceId}-{parentSpanId}-01";
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CodeIndex.CodeIndexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["traceparent"] = traceParent,
                },
            },
        };

        var response = _server.ProcessFrame(request.ToJsonString());

        Assert.NotNull(response);
        var activity = Assert.Single(stopped.Where(activity => activity.OperationName == "mcp.request"));
        Assert.Equal(parentTraceId, activity.TraceId);
        Assert.Equal(parentSpanId, activity.ParentSpanId);
        Assert.Equal("tools/list", activity.GetTagItem("rpc.method"));
    }

    [Fact]
    public void ProcessFrame_IgnoresNonStringTraceParent()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["_meta"] = new JsonObject
                {
                    ["traceparent"] = 42,
                },
            },
        };

        var response = _server.ProcessFrame(request.ToJsonString());

        Assert.NotNull(response);
        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.TryGetProperty("result", out _));
    }

    private void InsertIndexedFile(string path, string lang, string content, bool generated = false)
    {
        var normalized = content.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = normalized.Length,
            Lines = lines.Length,
            Modified = ManualTimeProvider.FixtureUtcNow.UtcDateTime,
            Checksum = Guid.NewGuid().ToString("N"),
            Generated = generated,
        });
        writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = lines.Length,
            Content = normalized,
        }]);

        var symbols = SymbolExtractor.Extract(fileId, lang, normalized);
        writer.InsertSymbols(symbols);
        writer.InsertReferences(ReferenceExtractor.Extract(fileId, lang, normalized, symbols));
    }

    private void MarkFoldReady()
    {
        var writer = new DbWriter(_db.Connection);
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
    }

    [Fact]
    public void ToolsCall_SearchFormatCompactEmitsFileLineOnly_Issue1642()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","format":"compact"}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var row = Assert.Single(structured["results"]!.AsArray());

        Assert.Equal("compact", structured["format"]!.GetValue<string>());
        Assert.Equal("src/app.cs", row!["file"]!.GetValue<string>());
        Assert.Equal(1, row["line"]!.GetValue<int>());
        Assert.Null(row["snippet"]);
    }

    [Fact]
    public void ToolsCall_SearchFormatCountAliasesCountOnly_Issue1642()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","format":"count"}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["count_only"]!.GetValue<bool>());
        Assert.True(structured["count"]!.GetValue<int>() > 0);
        Assert.NotNull(structured["result_stable_at"]);
        Assert.Empty(structured["results"]!.AsArray());
    }

    [Theory]
    [InlineData("search", "format")]
    [InlineData("symbol_hotspots", "groupBy")]
    public void ToolsCall_EnumLikeScalarTooLong_RejectsBeforeNormalization_Issue3116(string toolName, string argumentName)
    {
        var oversized = new string('A', McpBoundedText.MaxScalarArgumentChars + 1);
        var args = new JsonObject
        {
            [argumentName] = oversized,
        };
        if (toolName == "search")
            args["query"] = "Run";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = args,
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var structured = result["structuredContent"]!;
        Assert.Contains($"Argument '{argumentName}' on tool '{toolName}' is too long", text);
        Assert.DoesNotContain(oversized, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(toolName, structured["tool"]!.GetValue<string>());
        Assert.Equal(argumentName, structured["parameter"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxScalarArgumentChars, structured["max_length"]!.GetValue<int>());
        Assert.Equal(oversized.Length, structured["actual_length"]!.GetValue<int>());
        Assert.True(structured["value_truncated"]!.GetValue<bool>());
        Assert.Equal(oversized.Length, structured["value_length"]!.GetValue<int>());
        Assert.Equal(McpBoundedText.ForDisplay(oversized).Text, structured["value"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_SearchReturnsStableAtAndCursorContinuesAfterAnchor_Issue1462()
    {
        InsertIndexedFile("src/other.cs", "csharp", "public class Other { public void Run() { } }");
        var firstRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","limit":1}}}""")!;

        var firstResponse = _server.HandleMessage(firstRequest)!;
        var first = firstResponse["result"]!["structuredContent"]!;
        var cursor = first["next_cursor"]!.GetValue<string>();
        var stableAt = first["result_stable_at"]!.GetValue<DateTime>();

        Assert.Single(first["results"]!.AsArray());
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var secondRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["limit"] = 1,
                    ["cursor"] = cursor,
                },
            },
        };

        var secondResponse = _server.HandleMessage(secondRequest)!;
        var second = secondResponse["result"]!["structuredContent"]!;

        Assert.Single(second["results"]!.AsArray());
        Assert.Equal(stableAt, second["result_stable_at"]!.GetValue<DateTime>());
        Assert.NotEqual(
            first["results"]!.AsArray()[0]!["path"]!.GetValue<string>(),
            second["results"]!.AsArray()[0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_TruncatedResponseIncludesNextOffsetAndPages()
    {
        InsertIndexedFile(
            "src/paged-callers.cs",
            "csharp",
            """
            class PagedCallers {
                void Alpha() { Target(); }
                void Beta() { Target(); }
                void Gamma() { Target(); }
                void Target() { }
            }
            """);

        var firstRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Target","lang":"csharp","exactName":true,"path":"src/paged-callers.cs","limit":2}}}""")!;
        var firstResponse = _server.HandleMessage(firstRequest)!;
        var first = firstResponse["result"]!["structuredContent"]!;

        Assert.Equal(2, first["count"]!.GetValue<int>());
        Assert.True(first["truncated"]!.GetValue<bool>());
        Assert.True(first["more_available"]!.GetValue<bool>());
        Assert.Equal(2, first["next_offset"]!.GetValue<int>());
        var firstNames = first["results"]!.AsArray()
            .Select(row => row!["callerName"]!.GetValue<string>())
            .ToArray();

        var secondRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Target","lang":"csharp","exactName":true,"path":"src/paged-callers.cs","limit":2,"offset":2}}}""")!;
        var secondResponse = _server.HandleMessage(secondRequest)!;
        var second = secondResponse["result"]!["structuredContent"]!;

        Assert.Equal(2, second["offset"]!.GetValue<int>());
        Assert.False(second["truncated"]!.GetValue<bool>());
        Assert.False(second["more_available"]!.GetValue<bool>());
        Assert.Null(second["next_offset"]);
        var secondNames = second["results"]!.AsArray()
            .Select(row => row!["callerName"]!.GetValue<string>())
            .ToArray();

        var allNames = firstNames.Concat(secondNames).ToArray();
        Assert.Equal(allNames.Distinct().Count(), allNames.Length);
        Assert.Contains("Alpha", allNames);
        Assert.Contains("Beta", allNames);
        Assert.Contains("Gamma", allNames);
    }

    [Theory]
    [InlineData("references", "Target")]
    [InlineData("callees", "Source")]
    public void ToolsCall_GraphTools_TruncatedResponseIncludesEnvelope_Issue1415(string tool, string query)
    {
        InsertIndexedFile(
            "src/paged-graph.cs",
            "csharp",
            """
            class PagedGraph {
                void Source() { Alpha(); Beta(); Gamma(); }
                void Alpha() { Target(); }
                void Beta() { Target(); }
                void Gamma() { Target(); }
                void Target() { }
            }
            """);

        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":{"query":"Target","lang":"csharp","exactName":true,"path":"src/paged-graph.cs","limit":2}}}"""
                .Replace("TOOL", tool, StringComparison.Ordinal)
                .Replace("Target", query, StringComparison.Ordinal))!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(2, structured["count"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.True(structured["more_available"]!.GetValue<bool>());
        Assert.Equal(2, structured["next_offset"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Search_WithResultsIncludesNextStepSuggestion()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"public class App","limit":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var suggestion = structured["next_step_suggestion"]!;

        Assert.Equal("excerpt", suggestion["tool"]!.GetValue<string>());
        Assert.Equal("src/app.cs", suggestion["args"]!["path"]!.GetValue<string>());
        Assert.True(suggestion["args"]!["startLine"]!.GetValue<int>() >= 1);
        Assert.True(suggestion["args"]!["endLine"]!.GetValue<int>() >= suggestion["args"]!["startLine"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_References_WithResultsIncludesNextStepSuggestion()
    {
        InsertIndexedFile(
            "src/reference-hint.cs",
            "csharp",
            """
            class ReferenceHint {
                void Caller() { Target(); }
                void Target() { }
            }
            """);
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Target","lang":"csharp","exactName":true,"limit":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var suggestion = structured["next_step_suggestion"]!;

        Assert.Equal("excerpt", suggestion["tool"]!.GetValue<string>());
        Assert.Equal("src/reference-hint.cs", suggestion["args"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_WithResultIncludesNextStepSuggestion()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"src/app.cs","startLine":1,"endLine":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var suggestion = structured["next_step_suggestion"]!;

        Assert.Equal("outline", suggestion["tool"]!.GetValue<string>());
        Assert.Equal("src/app.cs", suggestion["args"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_EmptyResultIncludesRecoveryHint()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"MissingSymbol","lang":"csharp","exactName":true}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var hint = structured["recovery_hint"]!;

        Assert.Equal("no_results", hint["reason"]!.GetValue<string>());
        Assert.Equal("symbols", hint["tool"]!.GetValue<string>());
        Assert.Equal("MissingSymbol", hint["args"]!["query"]!.GetValue<string>());
    }

    // --- Protocol tests / プロトコルテスト ---

    [Fact]
    public async Task StdioTransport_WriteFrameAsync_UsesLfTerminatorEvenWhenHostNewLineDiffers()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var transport = new StdioMcpTransport(input, output, bufferSize: 1024);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", CancellationToken.None);

        var bytes = output.ToArray();
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public async Task ProcessLineAsync_UsesLfTerminatorEvenWhenWriterNewLineIsCrLf()
    {
        using var writer = new StringWriter { NewLine = "\r\n" };

        await _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", writer);

        var response = writer.ToString();
        Assert.EndsWith("\n", response, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", response);
    }

    [Fact]
    public async Task ProcessLineAsync_PingReturnsStructuredHealth()
    {
        using var writer = new StringWriter();

        await _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", writer);

        using var document = JsonDocument.Parse(writer.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("uptime_s").GetInt64() >= 0);
        Assert.True(result.GetProperty("db_open").GetBoolean());
        Assert.True(result.GetProperty("transport_ready").GetBoolean());
        Assert.True(DateTimeOffset.TryParse(result.GetProperty("last_request_at").GetString(), out _));
        Assert.True(DateTimeOffset.TryParse(result.GetProperty("last_db_check_at").GetString(), out _));
    }

    [Fact]
    public async Task ProcessLineAsync_ToolCallEmitsInvocationTelemetry()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":123,"method":"tools/call","params":{"name":"ping","arguments":{}}}""", writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal));
        Assert.Contains("[rid=123 cid=", line);
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal("mcp.tool.invocation", root.GetProperty("event").GetString());
        Assert.Equal("ping", root.GetProperty("tool").GetString());
        Assert.Equal("123", root.GetProperty("request_id").GetString());
        Assert.Equal("success", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("correlation_id", out var correlationId));
        Assert.False(string.IsNullOrWhiteSpace(correlationId.GetString()));
    }

    [Fact]
    public async Task ProcessLineAsync_UnknownToolName_TruncatesTelemetry_Issue3118()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var toolName = new string('l', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
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

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync(request.ToJsonString(), writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        Assert.DoesNotContain(toolName, writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(toolName, error.ToString(), StringComparison.Ordinal);
        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal));
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal("mcp.tool.invocation", root.GetProperty("event").GetString());
        Assert.Equal(display.Text, root.GetProperty("tool").GetString());
        Assert.Equal(toolName.Length, root.GetProperty("tool_length").GetInt32());
        Assert.True(root.GetProperty("tool_truncated").GetBoolean());
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(-32602, root.GetProperty("error_code").GetInt32());
    }

    [Fact]
    public async Task ProcessLineAsync_UnknownArgumentName_TruncatesTelemetryKeyMetadata_Issue3117()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var argumentName = new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 123,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = new JsonObject
                {
                    ["path"] = ".",
                    [argumentName] = true,
                },
            },
        };

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync(request.ToJsonString(), writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        Assert.DoesNotContain(argumentName, writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(argumentName, error.ToString(), StringComparison.Ordinal);
        var line = error.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal));
        var jsonStart = line.IndexOf('{');
        using var document = JsonDocument.Parse(line[jsonStart..]);
        var root = document.RootElement;
        Assert.Equal("mcp.tool.invocation", root.GetProperty("event").GetString());
        Assert.Contains(root.GetProperty("arg_keys").EnumerateArray(), key => key.GetString() == display.Text);
        Assert.Equal(argumentName.Length, root.GetProperty("arg_key_lengths").GetProperty(display.Text).GetInt32());
        Assert.True(root.GetProperty("arg_keys_truncated").GetBoolean());
    }

    [Fact]
    public async Task ProcessLineAsync_FallbackErrorIncludesCorrelationData()
    {
        using var writer = new StringWriter();
        using var error = new StringWriter();

        await Task.Run(() =>
        {
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
#pragma warning disable xUnit1031
                    _server.ProcessLineAsync("""{"jsonrpc":"2.0","id":321,"method":"tools/call","params":{"name":42,"arguments":{}}}""", writer).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }
        });

        var response = JsonNode.Parse(writer.ToString())!;
        var data = response["error"]!["data"]!;
        Assert.Equal("321", data["request_id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(data["correlation_id"]!.GetValue<string>()));
        Assert.Contains("[cdidx-mcp] [rid=321 cid=", error.ToString());
    }

    [Fact]
    public void Initialize_ReturnsProtocolVersion()
    {
        // Issue #1554: negotiation echoes back the client's requested protocolVersion when
        // it is in the server's supported set, instead of hardcoding the server's preferred
        // version. The legacy `2024-11-05` client is still supported, so the response should
        // mirror what the client asked for.
        // Issue #1554: 交渉ロジックはサーバー対応集合にあるクライアント要求バージョンを
        // そのまま返すようにした。レガシー `2024-11-05` クライアントは引き続きサポートする
        // ため、レスポンスは要求された値と一致する。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"test"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Equal("2024-11-05", response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("cdidx", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(ConsoleUi.LoadVersion(), response["result"]!["serverInfo"]!["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_InitializeEmitsInitializedNotificationAfterResponseOnlyOnce()
    {
        var transport = new QueueMcpTransport(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"test-client","version":"1.0"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"test-client","version":"1.0"}}}""");

        await _server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(3, transport.WrittenFrames.Count);
        Assert.Equal(1, JsonNode.Parse(transport.WrittenFrames[0])!["id"]!.GetValue<int>());
        Assert.Equal("notifications/initialized", JsonNode.Parse(transport.WrittenFrames[1])!["method"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse(transport.WrittenFrames[2])!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_StartupLogSanitizesDbPathByDefault_Issue1469()
    {
        using var error = new StringWriter();
        var previousDebug = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            await Task.Run(() =>
            {
                lock (TestConsoleLock.Gate)
                {
                    var previousError = Console.Error;
                    try
                    {
                        Console.SetError(error);
#pragma warning disable xUnit1031
                        _server.RunAsync(new QueueMcpTransport(), CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                    }
                    finally
                    {
                        Console.SetError(previousError);
                    }
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previousDebug);
        }

        var log = error.ToString();
        Assert.Contains("db: " + Path.GetFileName(_dbPath), log);
        Assert.DoesNotContain(_dbPath, log);
    }

    [Fact]
    public void Initialize_AdvertisesResourcesAndPrompts()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        var capabilities = response["result"]!["capabilities"]!;
        Assert.False(capabilities["tools"]!["listChanged"]!.GetValue<bool>());
        Assert.False(capabilities["resources"]!["subscribe"]!.GetValue<bool>());
        Assert.False(capabilities["resources"]!["listChanged"]!.GetValue<bool>());
        Assert.False(capabilities["prompts"]!["listChanged"]!.GetValue<bool>());
        Assert.NotNull(capabilities["logging"]);
        Assert.True(capabilities["roots"]!["listChanged"]!.GetValue<bool>());
        Assert.NotNull(capabilities["sampling"]);
    }

    [Fact]
    public void Initialize_CapturesClientCapabilitiesAndRootsForSessionStatus()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"codex","version":"5.0"},"capabilities":{"experimental":{"progress":true}},"rootUri":"file:///workspace","roots":[{"uri":"file:///workspace/src"}]}}""")!;
        _server.HandleMessage(request);

        Assert.True(_server.ClientCapabilitiesForTests!["experimental"]!["progress"]!.GetValue<bool>());
        Assert.Equal(["file:///workspace", "file:///workspace/src"], _server.ClientRootsForTests);

        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;
        var session = response["result"]!["structuredContent"]!["mcp_session"]!;

        Assert.True(session["client_capabilities"]!["experimental"]!["progress"]!.GetValue<bool>());
        Assert.Equal("codex", session["client_info"]!["name"]!.GetValue<string>());
        Assert.Equal("5.0", session["client_info"]!["version"]!.GetValue<string>());
        Assert.Equal("file:///workspace", session["roots"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("info", session["log_level"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_ClientInfo_TruncatesSessionStatusAndCallerIdentity_Issue3120()
    {
        var name = new string('n', McpBoundedText.MaxClientInfoChars + 25);
        var version = new string('v', McpBoundedText.MaxClientInfoChars + 25);
        var nameDisplay = McpBoundedText.ForDisplay(name, McpBoundedText.MaxClientInfoChars);
        var versionDisplay = McpBoundedText.ForDisplay(version, McpBoundedText.MaxClientInfoChars);
        var request = new JsonObject
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

        _server.HandleMessage(request);

        Assert.Equal($"{nameDisplay.Text}/{versionDisplay.Text}", _server.CurrentCaller);
        var status = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(status)!;

        Assert.DoesNotContain(name, response.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(version, response.ToJsonString(), StringComparison.Ordinal);
        var clientInfo = response["result"]!["structuredContent"]!["mcp_session"]!["client_info"]!;
        Assert.Equal(nameDisplay.Text, clientInfo["name"]!.GetValue<string>());
        Assert.Equal(name.Length, clientInfo["name_length"]!.GetValue<int>());
        Assert.True(clientInfo["name_truncated"]!.GetValue<bool>());
        Assert.Equal(versionDisplay.Text, clientInfo["version"]!.GetValue<string>());
        Assert.Equal(version.Length, clientInfo["version_length"]!.GetValue<int>());
        Assert.True(clientInfo["version_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void LoggingSetLevel_UpdatesSessionLogLevel()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"emergency"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Equal("emergency", _server.McpLogLevelForTests);
    }

    [Fact]
    public void LoggingSetLevel_InvalidLevel_ReturnsInvalidParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"trace"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", response["error"]!["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownNotification_ReturnsNoResponseAndLogsWarning()
    {
        using var writer = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            var previous = Console.Error;
            try
            {
                Console.SetError(writer);
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initalized"}""")!;

                var response = _server.HandleMessage(request);

                Assert.Null(response);
            }
            finally
            {
                Console.SetError(previous);
            }
        }

        Assert.Contains("Ignoring unknown notification", writer.ToString());
        Assert.Contains("notifications/initalized", writer.ToString());
    }

    [Fact]
    public void ResourcesList_ReturnsIndexedFilesAsResources()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        var resource = response["result"]!["resources"]!.AsArray()
            .Single(r => r!["name"]!.GetValue<string>() == "src/app.cs")!;
        Assert.Equal("cdidx://file/src/app.cs", resource["uri"]!.GetValue<string>());
        Assert.Equal("text/x-csharp", resource["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_ReturnsIndexedFileContent()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"cdidx://file/src/app.cs"}}""")!;
        var response = _server.HandleMessage(request)!;

        var content = response["result"]!["contents"]!.AsArray().Single()!;
        Assert.Equal("cdidx://file/src/app.cs", content["uri"]!.GetValue<string>());
        Assert.Equal("text/x-csharp", content["mimeType"]!.GetValue<string>());
        Assert.Contains("public class App", content["text"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_NonStringUri_ReturnsInvalidParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":42}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("missing_parameter", response["error"]!["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void ResourcesRead_UriTooLong_RejectsBeforeParse_Issue3122()
    {
        var uri = "cdidx://file/" + new string('x', McpBoundedText.MaxResourceUriChars + 25);
        var display = McpBoundedText.ForDisplay(uri, McpBoundedText.MaxResourceUriChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "resources/read",
            ["params"] = new JsonObject
            {
                ["uri"] = uri,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(uri, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Resource uri is too long: {display.Text}", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["uri"]!.GetValue<string>());
        Assert.Equal(uri.Length, data["uri_length"]!.GetValue<int>());
        Assert.True(data["uri_truncated"]!.GetValue<bool>());
        Assert.Equal(McpBoundedText.MaxResourceUriChars, data["max_length"]!.GetValue<int>());
        Assert.Equal(uri.Length, data["actual_length"]!.GetValue<int>());
    }

    [Fact]
    public void ResourcesRead_NotFound_ReturnsBoundedUriData_Issue3122()
    {
        const string uri = "cdidx://file/src/missing.cs";
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"cdidx://file/src/missing.cs"}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal($"Resource not found: {uri}", response["error"]!["message"]!.GetValue<string>());
        Assert.Equal(uri, response["error"]!["data"]!["uri"]!.GetValue<string>());
        Assert.Equal("invalid_argument", response["error"]!["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void PromptsListAndGet_ReturnPromptMessages()
    {
        var list = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"prompts/list","params":{}}""")!;
        var listResponse = _server.HandleMessage(list)!;

        var names = listResponse["result"]!["prompts"]!.AsArray()
            .Select(p => p!["name"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("summarize_file", names);
        Assert.Contains("find_unused", names);
        Assert.Contains("impact_of_changing", names);

        var get = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"prompts/get","params":{"name":"impact_of_changing","arguments":{"symbol":"Run"}}}""")!;
        var getResponse = _server.HandleMessage(get)!;
        var message = getResponse["result"]!["messages"]!.AsArray().Single()!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        Assert.Contains("impact_analysis", message["content"]!["text"]!.GetValue<string>());
        Assert.Contains("Run", message["content"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void PromptsGet_PromptNameTooLong_TruncatesDiagnostics_Issue3121()
    {
        var name = new string('p', McpBoundedText.MaxPromptNameChars + 25);
        var display = McpBoundedText.ForDisplay(name, McpBoundedText.MaxPromptNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "prompts/get",
            ["params"] = new JsonObject
            {
                ["name"] = name,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(name, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Prompt name is too long: '{display.Text}'", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal("name", data["parameter"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxPromptNameChars, data["max_length"]!.GetValue<int>());
        Assert.Equal(name.Length, data["actual_length"]!.GetValue<int>());
        Assert.Equal(display.Text, data["value"]!.GetValue<string>());
        Assert.Equal(name.Length, data["value_length"]!.GetValue<int>());
        Assert.True(data["value_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void PromptsGet_ArgumentValueTooLong_RejectsBeforePromptInterpolation_Issue3121()
    {
        var path = new string('x', McpBoundedText.MaxPromptArgumentChars + 25);
        var display = McpBoundedText.ForDisplay(path, McpBoundedText.MaxPromptArgumentChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "prompts/get",
            ["params"] = new JsonObject
            {
                ["name"] = "summarize_file",
                ["arguments"] = new JsonObject
                {
                    ["path"] = path,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(path, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Prompt argument 'path' is too long: '{display.Text}'", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal("path", data["parameter"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxPromptArgumentChars, data["max_length"]!.GetValue<int>());
        Assert.Equal(path.Length, data["actual_length"]!.GetValue<int>());
        Assert.Equal(display.Text, data["value"]!.GetValue<string>());
        Assert.Equal(path.Length, data["value_length"]!.GetValue<int>());
        Assert.True(data["value_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Initialize_RequestedCurrentProtocolVersion_EchoesBack()
    {
        // Issue #1554: when the client pins the current preferred version, the server
        // must echo it (the client and server agree, no fallback needed).
        // Issue #1554: クライアントが現行の優先バージョンを指定した場合、サーバーは
        // そのまま返す（合意済みなので fallback 不要）。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_NoProtocolVersion_UsesPreferred()
    {
        // Issue #1554: empty params still works — the negotiation falls back to the server's
        // preferred (newest) version so existing clients that never sent the field keep
        // working unchanged.
        // Issue #1554: params が空でも動作する — 既定の優先バージョン（最新）に fallback
        // することで、protocolVersion を送らない既存クライアントの互換を保つ。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_UnsupportedProtocolVersion_ReturnsInvalidParamsError()
    {
        // Issue #1554: an unsupported requested version must NOT silently downgrade to the
        // server's preferred version — that hid mismatches and made future spec bumps
        // observably break clients. Instead the handshake returns -32602 with structured
        // data so clients can branch on the failure and report a precise diagnostic.
        // Issue #1554: 未対応の要求バージョンを黙ってダウングレードしてはならない
        // （ミスマッチを覆い隠してしまうため）。-32602 と構造化データを返し、クライアントが
        // 失敗判定して正確な診断を出せるようにする。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains("2099-01-01", error["message"]!.GetValue<string>());
        Assert.Contains("2025-03-26", error["message"]!.GetValue<string>());

        var data = error["data"]!;
        Assert.Equal("2099-01-01", data["requestedVersion"]!.GetValue<string>());
        var supported = data["supportedVersions"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .ToArray();
        Assert.Equal(McpServer.SupportedProtocolVersions, supported);

        // #1581: the version-negotiation error path must also carry the canonical envelope
        // (`category` / `suggestion` / `retry_safe`) on top of the #1554 version fields so
        // clients can branch on category instead of parsing the message string.
        // #1581: バージョン交渉エラーも canonical envelope を必ず併載し、クライアントは
        // category で分岐できる。
        Assert.Equal("invalid_argument", data["category"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(data["suggestion"]!.GetValue<string>()));
        Assert.False(data["retry_safe"]!.GetValue<bool>());
    }

    [Fact]
    public void Initialize_MalformedProtocolVersion_FallsBackToPreferred()
    {
        // Non-string `protocolVersion` (e.g. number, null, object) is treated the same as
        // "field absent": fall back to the preferred version. Erroring would break tolerant
        // clients that send `null` when no preference exists, while a silent fallback only
        // kicks in for genuinely malformed inputs and not for the strict-mismatch path.
        // 非文字列の `protocolVersion`（数値・null・オブジェクト）は「未指定」と同じ扱いで
        // 既定の優先バージョンに fallback する。null を許容するクライアントとの互換を残しつつ、
        // 厳格な不一致ケース（文字列だが対応外）には引き続き -32602 を返せる。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":42}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void SupportedProtocolVersions_IsNewestFirstAndIncludesPreferred()
    {
        // The preferred version must be the newest entry; ordering matters for future
        // additions because clients may rely on the listed order as a "newest-first" hint.
        // 既定の優先バージョンは先頭でなければならない。クライアントが「先頭が最新」と
        // して扱う可能性があるため、順序の保証は明示的に必要。
        Assert.NotEmpty(McpServer.SupportedProtocolVersions);
        Assert.Equal("2025-03-26", McpServer.SupportedProtocolVersions[0]);
        Assert.Contains("2024-11-05", McpServer.SupportedProtocolVersions);
    }

    [Fact]
    public void CancelRequest_ForActiveRequest_ReturnsRequestCancelledError()
    {
        // Issue #1418: MCP `$/cancelRequest` must target the matching JSON-RPC id and
        // cancel the per-request token before the tool does DB work.
        // Issue #1418: MCP `$/cancelRequest` は対応する JSON-RPC id の per-request token を
        // cancel し、ツールが DB 作業に入る前に中断できる必要がある。
        _server.RequestRegisteredForTests = id =>
        {
            var cancel = JsonNode.Parse("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1418}}""")!;
            Assert.Null(_server.HandleMessage(cancel));
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, error["code"]!.GetValue<int>());
        Assert.Equal("request_cancelled", error["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void NotificationsCancelled_ForActiveRequestId_ReturnsRequestCancelledError()
    {
        _server.RequestRegisteredForTests = id =>
        {
            var cancel = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1418}}""")!;
            Assert.Null(_server.HandleMessage(cancel));
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, error["code"]!.GetValue<int>());
        Assert.Equal("request_cancelled", error["data"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void CancelRequest_UnknownOrMalformedId_IsNotificationOnly()
    {
        var unknown = JsonNode.Parse("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"missing"}}""")!;
        var missing = JsonNode.Parse("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{}}""")!;

        Assert.Null(_server.HandleMessage(unknown));
        Assert.Null(_server.HandleMessage(missing));
    }

    [Fact]
    public void ToolCall_ErrorIncludesCorrelationData()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"search","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("42", structured["request_id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(structured["correlation_id"]!.GetValue<string>()));
    }

    [Fact]
    public void ToolCall_ResponseIncludesCorrelationMeta()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var meta = response["result"]!["_meta"]!;
        Assert.Equal("\"abc\"", meta["request_id"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(meta["correlation_id"]!.GetValue<string>()));
    }

    [Fact]
    public void BatchQuery_SlotsIncludeChildCorrelationIds()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping","arguments":{}},{"tool":"languages","arguments":{}}]}}}""")!;

        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        var first = results[0]!["correlation_id"]!.GetValue<string>();
        var second = results[1]!["correlation_id"]!.GetValue<string>();
        Assert.EndsWith(".1", first, StringComparison.Ordinal);
        Assert.EndsWith(".2", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("analyze_symbol")]
    [InlineData("impact_analysis")]
    public void ToolCall_RequiredQuery_DistinguishesMissingFromWhitespace(string toolName)
    {
        var missing = CallToolAndReadErrorMessage(toolName, new JsonObject());
        var blank = CallToolAndReadErrorMessage(toolName, new JsonObject { ["query"] = "   " });

        Assert.Equal("Missing required parameter: query", missing);
        Assert.Equal("Parameter \"query\" cannot be empty or whitespace-only", blank);
    }

    [Theory]
    [InlineData("outline")]
    [InlineData("excerpt")]
    [InlineData("index")]
    public void ToolCall_RequiredPath_DistinguishesMissingFromWhitespace(string toolName)
    {
        var missing = CallToolAndReadErrorMessage(toolName, new JsonObject());
        var blank = CallToolAndReadErrorMessage(toolName, new JsonObject { ["path"] = "   " });

        Assert.Equal("Missing required parameter: path", missing);
        Assert.Equal("Parameter \"path\" cannot be empty or whitespace-only", blank);
    }

    [Fact]
    public void ToolCall_FindInFilePath_DistinguishesMissingFromWhitespace()
    {
        var missing = CallToolAndReadErrorMessage("find_in_file", new JsonObject { ["query"] = "Run" });
        var blank = CallToolAndReadErrorMessage("find_in_file", new JsonObject
        {
            ["query"] = "Run",
            ["path"] = "   "
        });

        Assert.Equal("Missing required parameter: path", missing);
        Assert.Equal("Parameter \"path\" cannot be empty or whitespace-only", blank);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("description")]
    public void ToolCall_SuggestImprovementRequiredStrings_DistinguishMissingFromWhitespace(string propertyName)
    {
        var baseArguments = new JsonObject
        {
            ["category"] = "unexpected_error",
            ["description"] = "The tool should report this behavior more clearly."
        };
        baseArguments.Remove(propertyName);
        var missing = CallToolAndReadErrorMessage("suggest_improvement", baseArguments);

        var blankArguments = new JsonObject
        {
            ["category"] = "unexpected_error",
            ["description"] = "The tool should report this behavior more clearly.",
            [propertyName] = "   "
        };
        var blank = CallToolAndReadErrorMessage("suggest_improvement", blankArguments);

        Assert.Equal($"Missing required parameter: {propertyName}", missing);
        Assert.Equal($"Parameter \"{propertyName}\" cannot be empty or whitespace-only", blank);
    }

    [Fact]
    public void BuildUnsupportedProtocolMessage_MentionsRequestedAndSupported()
    {
        var msg = McpServer.BuildUnsupportedProtocolMessage("2099-01-01");
        Assert.Contains("2099-01-01", msg);
        foreach (var supported in McpServer.SupportedProtocolVersions)
            Assert.Contains(supported, msg);
    }

    [Fact]
    public void BuildUnsupportedProtocolLog_IsActionable()
    {
        var log = McpServer.BuildUnsupportedProtocolLog("2099-01-01");
        Assert.Contains("Rejecting initialize", log);
        Assert.Contains("2099-01-01", log);
        Assert.Contains("Upgrade the server or pin a supported version", log);
    }

    [Fact]
    public void NegotiateProtocolVersion_TruncatesUnsupportedRequestedVersion_Issue3119()
    {
        var requested = new string('v', McpBoundedText.MaxProtocolVersionChars + 25);
        var display = McpBoundedText.ForDisplay(requested, McpBoundedText.MaxProtocolVersionChars);
        var initializeParams = new JsonObject
        {
            ["protocolVersion"] = requested,
        };

        var negotiated = McpServer.NegotiateProtocolVersion(initializeParams, out var requestedVersion);

        Assert.Null(negotiated);
        Assert.NotNull(requestedVersion);
        Assert.Equal(display.Text, requestedVersion.Value.Text);
        Assert.Equal(requested.Length, requestedVersion.Value.OriginalLength);
        Assert.True(requestedVersion.Value.Truncated);
    }

    [Fact]
    public void Initialize_UnsupportedProtocolVersion_TruncatesDiagnostics_Issue3119()
    {
        var requested = new string('p', McpBoundedText.MaxProtocolVersionChars + 25);
        var display = McpBoundedText.ForDisplay(requested, McpBoundedText.MaxProtocolVersionChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = requested,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(requested, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(display.Text, response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["requestedVersion"]!.GetValue<string>());
        Assert.Equal(requested.Length, data["requestedVersion_length"]!.GetValue<int>());
        Assert.True(data["requestedVersion_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Initialize_NullId_PreservesNullResponseId()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":null,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        using var document = JsonDocument.Parse(response.ToJsonString());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("2025-03-26", root.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void Initialize_NoId_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void Initialize_BooleanId_ReturnsInvalidRequestWithNullId()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":true,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        using var document = JsonDocument.Parse(response.ToJsonString());
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
        Assert.Equal(-32600, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("id must be string, number, or null", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Initialize_ReturnsToolsCapability()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
        Assert.False(response["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsList_EachToolPublishesSchemaAndExampleContract()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(24, tools.Count);
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());

            var examples = tool["examples"]!.AsArray();
            Assert.NotEmpty(examples);
            foreach (var example in examples)
            {
                Assert.Equal("tools/call", example!["request"]!["method"]!.GetValue<string>());
                Assert.Equal(tool["name"]!.GetValue<string>(), example["request"]!["params"]!["name"]!.GetValue<string>());
                Assert.NotNull(example["request"]!["params"]!["arguments"]);
                Assert.False(string.IsNullOrWhiteSpace(example["response_excerpt"]!.GetValue<string>()));
            }
        }
    }

    [Theory]
    [InlineData("ping", """{}""")]
    [InlineData("status", """{}""")]
    [InlineData("search", """{"query":"Run","limit":5}""")]
    public void ToolCall_ResponseShape_HasStableMcpResultEnvelope(string toolName, string argumentsJson)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = JsonNode.Parse(argumentsJson),
            },
        };
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Null(response["error"]);

        var result = response["result"]!;
        Assert.NotNull(result["content"]);
        Assert.Equal("text", result["content"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.NotNull(result["structuredContent"]);
        Assert.NotNull(result["_meta"]!["request_id"]);
        Assert.NotNull(result["_meta"]!["correlation_id"]);
    }

    [Fact]
    public void ToolCall_TypeMismatch_ReturnsInvalidParams_Issue1417()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","limit":"not-an-int"}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", error["data"]!["category"]!.GetValue<string>());
        Assert.Equal("limit", error["data"]!["parameter"]!.GetValue<string>());
        Assert.Equal("integer", error["data"]!["expected"]!.GetValue<string>());
        Assert.Equal("string", error["data"]!["actual"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_ReturnsInstructions()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!;
        var response = _server.HandleMessage(request)!;

        var instructions = response["result"]!["instructions"]?.GetValue<string>();
        Assert.NotNull(instructions);
        Assert.Contains("map", instructions!);
        Assert.Contains("analyze_symbol", instructions);
        Assert.Contains("search", instructions);
        // Verify index-first bootstrap guidance / インデックス未作成時の案内を検証
        Assert.Contains("index", instructions);
        Assert.Contains("backfill_fold", instructions);
        Assert.Contains("impact_mode", instructions);
        Assert.Contains("file_impacts", instructions);
        Assert.Contains("heuristic file-level dependency hints", instructions);
        // Verify language list comes from ReferenceExtractor / 言語リストがReferenceExtractorから来ることを検証
        foreach (var lang in ReferenceExtractor.GetSupportedLanguages())
        {
            Assert.Contains(lang, instructions);
        }
    }

    // --- Authentication tests (#1559) / 認証テスト (#1559) ---

    [Fact]
    public void DefaultAuthenticator_AllowsRequestsWithoutToken()
    {
        // #1559: the historical stdio default must keep working without an auth token so
        // existing clients (Claude Code, Cursor, Windsurf) don't break when the upgrade
        // ships. The permissive default is wired by the parameterless ctor.
        // #1559: stdio 既定の従来動作はトークン無しで通る必要がある。permissive 既定は
        // 引数なしコンストラクタで wire される。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"ping"}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void TokenAuthenticator_MatchingToken_DispatchesNormally()
    {
        // #1559: when the server is configured with a token, the matching token in
        // params.auth.token authenticates the request and dispatch proceeds.
        // #1559: トークン設定済みサーバーに対し、params.auth.token に一致する値を
        // 添えれば認証成功し dispatch に進む。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"auth":{"token":"s3cret"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void TokenAuthenticator_MissingToken_ReturnsUnauthorized()
    {
        // #1559: a configured server must reject requests with no token. The wire response
        // carries only -32001 + "Unauthorized" (no detail) per the #1530 sanitization rule.
        // #1559: トークン設定済みサーバーはトークン未提示のリクエストを拒否する。ワイヤ応答は
        // #1530 サニタイズ方針に従い -32001 と "Unauthorized" のみ。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32001, error["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_WrongToken_ReturnsUnauthorized()
    {
        // #1559: an incorrect token must produce the same wire response shape as a missing
        // token so callers cannot mount a token-presence oracle on the response body.
        // #1559: 不一致トークンも未提示と同じワイヤ応答にすることで、応答本文を見て
        // トークン有無を判定するオラクル攻撃を防ぐ。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"auth":{"token":"wrong"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32001, error["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_ToolsCallWithToken_Dispatches()
    {
        // #1559: the auth check runs uniformly across initialize/tools/list/tools/call/ping
        // so a tool dispatch with a matching token still reaches the handler and returns the
        // tool result instead of an Unauthorized error.
        // #1559: 認証チェックは initialize/tools/list/tools/call/ping に統一されているため、
        // 一致トークン付きのツール呼び出しはハンドラまで届き Unauthorized ではなくツール結果を返す。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","auth":{"token":"s3cret"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.NotNull(response["result"]);
        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.Null(response["error"]);
    }

    [Fact]
    public void TokenAuthenticator_NotificationsBypassAuthCheck()
    {
        // Notifications (no id) produce no response so the auth check would have nothing to
        // signal on; the existing notification short-circuit must stay BEFORE the auth gate
        // so a token-protected server still tolerates `notifications/initialized` without
        // synthesising an error response.
        // 通知 (id 無し) は応答が無いので認証チェックがエラーを返す手段を持たない。通知の
        // ショートサーキットを認証ゲートより前に置き続け、token 保護サーバーでも
        // `notifications/initialized` を黙って受け入れられるようにする。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!;

        var response = server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void TokenAuthenticator_MalformedAuthShape_TreatedAsMissing()
    {
        // Defensive: an `auth` object whose `token` field is not a string (number, array,
        // object) must not crash the server. The token-authenticator catches the cast and
        // treats it as a missing token so the wire stays uniform.
        // 防御: `auth.token` が文字列でない（数値・配列・オブジェクト）入力でサーバーが
        // クラッシュしてはならない。token authenticator は cast 失敗を未提示扱いにし、
        // ワイヤ応答を統一する。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"auth":{"token":42}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32001, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void BuildAuthFailureLog_IsActionable()
    {
        // The stderr log keeps the actionable detail (method, reason, recovery hint) so an
        // operator can diagnose without combing through the wire transcript. The wire stays
        // sanitized per #1530.
        // stderr ログには診断用詳細 (method/reason/復旧ヒント) を残す。ワイヤ応答は #1530
        // 方針でサニタイズしたまま保つ。
        var log = McpServer.BuildAuthFailureLog("tools/call", "missing auth token");

        Assert.Contains("Auth failed", log);
        Assert.Contains("tools/call", log);
        Assert.Contains("missing auth token", log);
        Assert.Contains("CDIDX_MCP_AUTH_TOKEN", log);
        Assert.Contains("params.auth.token", log);
    }

    [Fact]
    public void TokenAuthenticator_EmptyTokenInCtor_Rejected()
    {
        // An empty configured token would make every empty-string presentation succeed, so
        // the constructor must refuse it. RunMcp's factory already pre-filters on
        // whitespace, but the constructor is the public contract.
        // 空文字を期待トークンに設定すると空文字提示が全て通ってしまうため、コンストラクタで
        // 拒否する。RunMcp の factory は空白フィルタを掛けるが、コンストラクタが公開契約。
        Assert.Throws<ArgumentException>(() => new TokenMcpAuthenticator(string.Empty));
    }

    [Fact]
    public void McpAuthenticatorFactory_NoEnv_ReturnsLocalStdio()
    {
        // FromEnvironment() must default to permissive stdio when the env var is unset or
        // whitespace, so unconfigured installs preserve the historical behaviour.
        // 環境変数が未設定 or 空白の場合は permissive stdio に fallback し、未設定インストールの
        // 従来動作を維持する。
        var previous = Environment.GetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, null);
            Assert.IsType<LocalStdioAuthenticator>(McpAuthenticatorFactory.FromEnvironment());

            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, "   ");
            Assert.IsType<LocalStdioAuthenticator>(McpAuthenticatorFactory.FromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, previous);
        }
    }

    [Fact]
    public void TokenAuthenticator_NonStringMethod_ReturnsUnauthorized()
    {
        // #1559: a non-string `method` (e.g. `42`) must hit the auth gate and produce
        // -32001 "Unauthorized", not a -32603 "Internal error" leaked from a throwing
        // GetValue<string>() call on the way to dispatch. Otherwise the token-protected
        // server would tell unauthenticated callers that their malformed request reached
        // dispatch internals.
        // #1559: 非文字列 method（例: 42）は認証ゲートに到達して -32001 を返すべきで、
        // GetValue<string>() の例外から -32603 を漏らしてはならない。漏らすと token 保護下で
        // 未認証呼び出しに「dispatch 内部まで届いた」事実を伝えてしまう。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":42}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        Assert.Equal(-32001, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Unauthorized", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void TokenAuthenticator_MissingMethodWithToken_ReturnsMethodError()
    {
        // After auth passes, a request that omits `method` entirely still gets the
        // structured -32600 "missing method" error. This documents that the auth gate runs
        // first but does not swallow downstream method-shape validation when the caller is
        // authenticated.
        // 認証が通った後で `method` が欠落しているリクエストには従来通り -32600
        // "missing method" を返す。認証ゲートは先行するが、認証済み呼び出しに対しては
        // 既存の method 形式検証を残す。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"params":{"auth":{"token":"s3cret"}}}""")!;

        var response = server.HandleMessage(request)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("missing method", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void BuildAuthFailureLog_SanitizesControlCharsInMethod()
    {
        // The stderr log interpolates the caller-controlled `method`; if we don't strip
        // control characters, an attacker can send `"method":"evil\n[forged]"` and split
        // the diagnostic across two lines (log forging). Sanitization replaces \n/\r/etc.
        // with `?` and clamps method length so a single auth failure never spans lines.
        // stderr ログには caller 由来の `method` が埋め込まれる。制御文字を除去しないと
        // `"method":"evil\n[forged]"` で 1 件のログを 2 行に分割されてしまう（ログ偽造）。
        // 制御文字を `?` に置換し、長さも切り詰める。
        var log = McpServer.BuildAuthFailureLog("evil\n[forged]\rfoo\t", "missing auth token");

        Assert.DoesNotContain('\n', log);
        Assert.DoesNotContain('\r', log);
        Assert.DoesNotContain('\t', log);
        Assert.Contains("evil?[forged]?foo?", log);
        Assert.Contains("missing auth token", log);
    }

    [Fact]
    public void BuildAuthFailureLog_ClampsLongMethod()
    {
        // The log clamps method to a fixed cap to keep a single auth-failure line readable
        // and to bound the cost of stderr writes when a hostile client sends a giant method.
        // method を一定長に切り詰めることでログ行を読みやすく保ち、巨大 method による
        // stderr 書き込みコストも抑える。
        var huge = new string('A', 5000);

        var log = McpServer.BuildAuthFailureLog(huge, "missing auth token");

        Assert.DoesNotContain(new string('A', 5000), log);
        Assert.Contains("…", log);
    }

    [Fact]
    public void BuildAuthFailureLog_NullMethod_LabeledNone()
    {
        // After the safe method-extraction change, `method` may be null when the request
        // omits it or sets it to a non-string. The log must still be readable rather than
        // showing a literal "null".
        // 安全な method 抽出により method が null になり得る（欠落 or 非文字列）。ログは
        // 読みやすい表記にしておき、リテラル "null" を出さない。
        var log = McpServer.BuildAuthFailureLog(null, "missing auth token");

        Assert.Contains("Auth failed for method (none)", log);
    }

    [Fact]
    public void TokenAuthenticator_WrongLengthToken_UniformWireResponse()
    {
        // The hash-based compare normalizes presented tokens to a fixed length before
        // FixedTimeEquals, so a wrong-length guess and a wrong equal-length guess produce
        // byte-identical wire responses. Verifies the two error bodies are exactly equal
        // (no length echoed back, no detail leaked).
        // ハッシュ比較により提示トークンは固定長に正規化されてから FixedTimeEquals に渡る
        // ので、長さ違いの推測と同長の不一致は同一のワイヤ応答になる。両エラーボディが
        // バイト単位で完全一致することを確認する。
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("s3cret"));
        var shortReq = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"x"}}}""")!;
        var sameLenReq = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"WRONG!"}}}""")!;

        var shortResp = server.HandleMessage(shortReq)!;
        var sameLenResp = server.HandleMessage(sameLenReq)!;

        ((JsonObject)shortResp["error"]!["data"]!).Remove("correlation_id");
        ((JsonObject)sameLenResp["error"]!["data"]!).Remove("correlation_id");
        ((JsonObject)shortResp["error"]!["data"]!).Remove("request_id");
        ((JsonObject)sameLenResp["error"]!["data"]!).Remove("request_id");
        Assert.Equal(shortResp.ToJsonString(), sameLenResp.ToJsonString());
        Assert.Equal(-32001, shortResp["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void McpAuthenticatorFactory_TokenSet_ReturnsTokenAuthenticator()
    {
        // When CDIDX_MCP_AUTH_TOKEN holds a non-whitespace value, the factory must produce a
        // TokenMcpAuthenticator that enforces a matching token on the wire.
        // CDIDX_MCP_AUTH_TOKEN に空白以外の値があれば factory は TokenMcpAuthenticator を
        // 返し、ワイヤ上で一致トークンを強制する。
        var previous = Environment.GetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, "s3cret");
            var authenticator = McpAuthenticatorFactory.FromEnvironment();
            Assert.IsType<TokenMcpAuthenticator>(authenticator);

            var matching = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"s3cret"}}}""")!;
            Assert.True(authenticator.Authenticate(matching).IsAuthenticated);

            var bad = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"ping","params":{"auth":{"token":"nope"}}}""")!;
            Assert.False(authenticator.Authenticate(bad).IsAuthenticated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpAuthenticatorFactory.AuthTokenEnvVar, previous);
        }
    }

    [Fact]
    public void BuildOversizedMessageLog_IsActionable()
    {
        var message = McpServer.BuildOversizedMessageLog(1_234_567, 1_500_000);

        Assert.Contains("Message too large", message);
        Assert.Contains("chars", message);
        Assert.Contains("bytes", message);
        Assert.Contains("Split the request into smaller JSON-RPC messages", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildJsonParseErrorLog_IsActionable()
    {
        var message = McpServer.BuildJsonParseErrorLog("Expected ':'");

        Assert.Contains("JSON parse error", message);
        Assert.Contains("Send one UTF-8 JSON-RPC object per line", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildInvalidUtf8ErrorLog_IsActionable()
    {
        var message = McpServer.BuildInvalidUtf8ErrorLog("invalid byte sequence");

        Assert.Contains("invalid UTF-8", message);
        Assert.Contains("Send one UTF-8 JSON-RPC object per line", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildResponseSerializationErrorLog_IdentifiesResponseSerializationStage()
    {
        var message = McpServer.BuildResponseSerializationErrorLog("serializer failed");

        Assert.Contains("Error serializing response", message);
        Assert.Contains("serializer failed", message);
        Assert.Contains("minimal JSON-RPC error response", message);
    }

    [Fact]
    public void BuildResponseWriteErrorLog_IdentifiesResponseWriteStage()
    {
        var message = McpServer.BuildResponseWriteErrorLog("pipe closed");

        Assert.Contains("Error writing response", message);
        Assert.Contains("pipe closed", message);
        Assert.Contains("client connection", message);
    }

    [Fact]
    public void BuildToolErrorLog_IsActionable()
    {
        var message = McpServer.BuildToolErrorLog("search", "bad db");

        Assert.Contains("Tool error (search): bad db", message);
        Assert.Contains("Fix the tool arguments", message);
        Assert.Contains("refresh the index if needed", message);
        Assert.Contains("retry", message);
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_OmitsExceptionMessage()
    {
        // Issue #1530: the JSON-RPC tool result must not echo `ex.Message`,
        // because SQLite errors and other content-bearing exceptions can quote
        // bound parameter values or matched text. Only the tool name and
        // exception type should reach the wire; full detail stays in stderr.
        var ex = new InvalidOperationException("near 'SECRET_LITERAL': syntax error");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            message = McpServer.BuildSanitizedToolErrorMessage("search", ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Equal("Tool 'search' failed. See cdidx server stderr for details.", message);
        Assert.Contains("server stderr", message);
        Assert.DoesNotContain("SECRET_LITERAL", message);
        Assert.DoesNotContain("syntax error", message);
        Assert.DoesNotContain(nameof(InvalidOperationException), message);
    }

    [Fact]
    public void BuildSanitizedLoopErrorMessage_OmitsExceptionMessage()
    {
        // Same protection as BuildSanitizedToolErrorMessage but for the
        // outer JSON-RPC loop catch-all (#1530).
        var ex = new InvalidOperationException("PRAGMA failed: secret table 'leaky_table' missing");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            message = McpServer.BuildSanitizedLoopErrorMessage(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Equal("Internal MCP error. See cdidx server stderr for details.", message);
        Assert.Contains("server stderr", message);
        Assert.DoesNotContain("leaky_table", message);
        Assert.DoesNotContain("PRAGMA failed", message);
        Assert.DoesNotContain(nameof(InvalidOperationException), message);
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_UnsafeDebugIncludesExceptionType()
    {
        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            var ex = new InvalidOperationException("near 'SECRET_LITERAL': syntax error");

            var message = McpServer.BuildSanitizedToolErrorMessage("search", ex);

            Assert.Contains("Error executing search", message);
            Assert.Contains(nameof(InvalidOperationException), message);
            Assert.DoesNotContain("SECRET_LITERAL", message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_CodeIndexException_EchoesStructuredFields()
    {
        // Issue #1580: CodeIndexException carries author-controlled Code / Category /
        // Path / Hint values, so the MCP catch-all must surface them so clients can
        // branch on Code without parsing free-form messages. The free-form `Message`
        // text built by CodeIndexException itself (which already includes the path
        // suffix) still must not be echoed verbatim, to keep #1530 closed for the
        // database message body.
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection.",
            path: "/var/cdidx/state.db",
            hint: "Close other cdidx invocations.");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            message = McpServer.BuildSanitizedToolErrorMessage("search", ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Contains("Error executing search", message);
        Assert.Contains(nameof(CodeIndexException), message);
        Assert.Contains("[E002_DB_LOCKED/database]", message);
        Assert.Contains("path='/var/cdidx/state.db'", message);
        Assert.Contains("hint='Close other cdidx invocations.'", message);
        Assert.Contains("server stderr", message);
    }

    [Fact]
    public void ProcessFrame_ResponseSerializationFailure_ReturnsMinimalErrorWithRequestId()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            _ => throw new JsonException("serializer failed"));

        var response = server.ProcessFrame("""{"jsonrpc":"2.0","id":42,"method":"tools/list"}""");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(42, root.GetProperty("id").GetInt32());
        Assert.Equal(-32603, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("serializing MCP response", root.GetProperty("error").GetProperty("message").GetString());
        var data = root.GetProperty("error").GetProperty("data");
        Assert.Equal("42", data.GetProperty("request_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("correlation_id").GetString()));
    }

    [Fact]
    public void ProcessFrame_ResponseSerializationFailureForNonObjectRequest_ReturnsErrorWithNullId()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            _ => throw new JsonException("serializer failed"));

        var response = server.ProcessFrame("""["not","an","object"]""");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.Equal(-32603, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public void ProcessFrame_ResponseSerializationFailureForInvalidRequestId_ReturnsErrorWithNullId()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            _ => throw new JsonException("serializer failed"));

        var response = server.ProcessFrame("""{"jsonrpc":"2.0","id":false,"method":"tools/list"}""");

        using var doc = JsonDocument.Parse(response!);
        var root = doc.RootElement;
        Assert.Equal(-32603, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task ProcessLineAsync_ResponseWriteFailure_DoesNotThrow()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());

        await server.ProcessLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            new ThrowingTextWriter());
    }

    [Fact]
    public async Task ProcessLineAsync_ParseError_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync("not json", new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("\"code\":-32700", writer.ToString());
        Assert.Contains("JSON parse error", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_OversizedFrame_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(new string('x', 1_000_001), new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("Message too large", writer.ToString());
        Assert.Contains("Message too large", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_UnsupportedProtocol_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""",
                new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("Unsupported MCP protocolVersion", writer.ToString());
        Assert.Contains("Rejecting initialize", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_UnsupportedProtocol_TruncatesErrorLog_Issue3119()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var requested = new string('q', McpBoundedText.MaxProtocolVersionChars + 25);
        var display = McpBoundedText.ForDisplay(requested, McpBoundedText.MaxProtocolVersionChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = requested,
            },
        };
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(
                request.ToJsonString(),
                new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.DoesNotContain(requested, writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(requested, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(display.Text, writer.ToString());
        Assert.Contains(display.Text, error.ToString());
        Assert.Contains("Rejecting initialize", error.ToString());
    }

    [Fact]
    public async Task ProcessLineAsync_AuthFailure_WritesResponseBeforeErrorLog()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false,
            new TokenMcpAuthenticator("secret"));
        using var writer = new StringWriter();
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.ProcessLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                new AssertingTextWriter(writer, () => Assert.Equal(string.Empty, error.ToString())));
        }
        finally
        {
            Console.SetError(previousError);
        }

        Assert.Contains("Unauthorized", writer.ToString());
        Assert.Contains("Auth failed", error.ToString());
    }

    [Fact]
    public async Task RunAsync_ParseErrorWriteFailure_LogsWriteFailureAndParseError()
    {
        var transport = new ShutdownProbeTransport("stdio", _ => throw new IOException("pipe closed"), "not json");
        using var server = new McpServer(_dbPath, "test");
        using var error = new StringWriter();
        var previousError = Console.Error;
        Console.SetError(error);
        try
        {
            await server.RunAsync(transport, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previousError);
        }

        var log = error.ToString();
        Assert.Contains("Error writing response", log);
        Assert.Contains("pipe closed", log);
        Assert.Contains("JSON parse error", log);
    }

    [Fact]
    public void BuildSanitizedLoopErrorMessage_CodeIndexException_EchoesStructuredFields()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbLocked,
            category: CodeIndexExceptionCategory.Database,
            message: "Failed to open SQLite connection.",
            path: "/var/cdidx/state.db",
            hint: "Close other cdidx invocations.");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            message = McpServer.BuildSanitizedLoopErrorMessage(ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Contains("Internal error", message);
        Assert.Contains(nameof(CodeIndexException), message);
        Assert.Contains("[E002_DB_LOCKED/database]", message);
        Assert.Contains("path='/var/cdidx/state.db'", message);
        Assert.Contains("hint='Close other cdidx invocations.'", message);
        Assert.Contains("server stderr", message);
    }

    [Fact]
    public void BuildSanitizedToolErrorMessage_CodeIndexException_NoPathNoHint_OmitsFragments()
    {
        var ex = new CodeIndexException(
            code: CommandErrorCodes.DbError,
            category: CodeIndexExceptionCategory.Database,
            message: "Generic failure.");

        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        string message;
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, "unsafe");
            message = McpServer.BuildSanitizedToolErrorMessage("status", ex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
        }

        Assert.Contains("[E008_DB_ERROR/database]", message);
        Assert.DoesNotContain("path=", message);
        Assert.DoesNotContain("hint=", message);
    }

    [Fact]
    public void BuildSanitizedIndexFileFailureMessage_OmitsRawExceptionMessage_Issue3202()
    {
        var message = McpServer.BuildSanitizedIndexFileFailureMessageForTesting(
            "index_file",
            nameof(InvalidOperationException),
            out var truncated);

        Assert.Equal("File indexing failed during index_file (InvalidOperationException). See cdidx server stderr for details.", message);
        Assert.False(truncated);
        Assert.DoesNotContain("SECRET_LITERAL", message);
        Assert.DoesNotContain("/private/path", message);
    }

    [Fact]
    public void SanitizeMcpIndexFailureMessage_CapsAndCollapsesText_Issue3202()
    {
        var raw = "first line\nsecond\tline " + new string('x', McpServer.MaxMcpIndexFailureMessageLength + 100);

        var message = McpServer.SanitizeMcpIndexFailureMessageForTesting(raw, out var truncated);

        Assert.True(truncated);
        Assert.True(message.Length <= McpServer.MaxMcpIndexFailureMessageLength);
        Assert.EndsWith("...(truncated)", message);
        Assert.DoesNotContain("\n", message);
        Assert.DoesNotContain("\t", message);
    }

    [Fact]
    public void ToolsCall_ReusesDbContextAcrossInvocations()
    {
        // #1494: every MCP tool call used to construct a fresh DbContext (and reopen the
        // SQLite connection, reapply pragmas, re-register every SQL function). The session
        // should now cache a single DbContext after the first tool call and reuse it.
        // #1494: 旧実装はツール呼び出しごとに DbContext を作り直していたため、SQLite 接続再開・
        // PRAGMA 再適用・SQL 関数再登録のコストを毎回払っていた。セッション内では一度だけ開いた
        // DbContext を再利用するようになっていることを検証する。
        Assert.Null(GetSharedDbContextField(_server));

        var first = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!);
        Assert.False(first!["result"]?["isError"]?.GetValue<bool>() ?? false);
        var afterFirst = GetSharedDbContextField(_server);
        Assert.NotNull(afterFirst);

        var second = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!);
        Assert.False(second!["result"]?["isError"]?.GetValue<bool>() ?? false);
        Assert.Same(afterFirst, GetSharedDbContextField(_server));
    }

    [Fact]
    public void ToolsCall_DbMissingThenCreated_ReopensSharedContext()
    {
        // The cached DbContext must drop itself when the file is missing so a follow-up call
        // — after the user runs `cdidx index` from another shell — can succeed instead of
        // failing against a stale handle.
        // DB ファイルが消えた場合はキャッシュをクリアし、外部で再作成された後の呼び出しで
        // 古いハンドルに失敗せず再オープンできることを確認する。
        var missingPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_reopen_{Guid.NewGuid():N}.db");
        using var server = new McpServer(missingPath, ConsoleUi.LoadVersion());
        try
        {
            var miss = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;
            Assert.True(miss["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.Null(GetSharedDbContextField(server));

            using (var seed = new DbContext(missingPath))
            {
                seed.InitializeSchema();
            }

            var hit = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;
            Assert.False(hit["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.NotNull(GetSharedDbContextField(server));
        }
        finally
        {
            server.Dispose();
            DeleteFileRobust(missingPath);
        }
    }

    [Fact]
    public void Dispose_ReleasesSharedDbContext()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_dispose_{Guid.NewGuid():N}.db");
        try
        {
            using (var seed = new DbContext(dbPath))
            {
                seed.InitializeSchema();
            }

            var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            _ = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!);
            Assert.NotNull(GetSharedDbContextField(server));

            server.Dispose();

            Assert.Null(GetSharedDbContextField(server));
            Assert.Throws<ObjectDisposedException>(() => server.GetOrOpenSharedDb());
        }
        finally
        {
            DeleteFileRobust(dbPath);
        }
    }

    private static DbContext? GetSharedDbContextField(McpServer server)
    {
        var field = typeof(McpServer).GetField("_sharedDb",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (DbContext?)field!.GetValue(server);
    }

    [Fact]
    public async Task ProcessLineAsync_WhenResponseSerializationFails_ReturnsJsonRpcError()
    {
        using var server = new McpServer(
            _dbPath,
            ConsoleUi.LoadVersion(),
            false,
            _ => throw new InvalidOperationException("serialize boom"));
        using var stdout = new StringWriter();

        await server.ProcessLineAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""", stdout);

        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        var error = root.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        // Issue #1530: raw ex.Message must not leak into the JSON-RPC response.
        // Only the exception type name and a stderr breadcrumb should surface.
        var message = error.GetProperty("message").GetString();
        Assert.Contains("InvalidOperationException", message);
        Assert.Contains("cdidx server stderr", message);
        Assert.DoesNotContain("serialize boom", message);
    }

    [Fact]
    public void Notification_Initialized_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void Notification_Cancelled_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/cancelled"}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    [Fact]
    public void UnknownNotification_ReturnsNullAndLogsToStderr()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            try
            {
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/bogus","params":{"x":1}}""")!;
                var response = _server.HandleMessage(request);

                Assert.Null(response);
                Assert.Contains(
                    McpServer.BuildUnknownNotificationLog("notifications/bogus"),
                    errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    // --- Shutdown / concurrency tests (#1567) / shutdown と並列上限のテスト ---

    [Fact]
    public void Notification_Shutdown_ReturnsNullAndLogsToStderr()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            try
            {
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/shutdown"}""")!;
                var response = _server.HandleMessage(request);

                Assert.Null(response);
                Assert.Contains("notifications/shutdown", errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void Notification_Exit_ReturnsNullAndLogsToStderr()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            try
            {
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/exit"}""")!;
                var response = _server.HandleMessage(request);

                Assert.Null(response);
                Assert.Contains("notifications/exit", errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public async Task RunAsync_ShutdownNotification_DrainsAndExits()
    {
        // The loop must exit cleanly when the wire-level `notifications/shutdown` arrives
        // even if the transport has not been closed externally. WriteFrameAsync is still
        // called once with `null` because shutdown is a notification (#1567).
        // 外部からトランスポートが閉じられなくても `notifications/shutdown` でループが正常終了
        // することを確認する (#1567)。通知なので応答は null。
        var transport = new ShutdownProbeTransport(
            """{"jsonrpc":"2.0","method":"notifications/shutdown"}""");
        using var server = new McpServer(_dbPath, "test");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
        Assert.Null(transport.LastWritten);
    }

    [Fact]
    public async Task RunAsync_ShutdownNotification_PreemptsRemainingFrames()
    {
        // A `tools/list` request queued behind shutdown must not be served — shutdown
        // wins so the server can stop without taking on more work (#1567).
        // shutdown の後ろに積まれたフレームは処理しない (#1567)。
        var transport = new ShutdownProbeTransport(
            """{"jsonrpc":"2.0","method":"notifications/shutdown"}""",
            """{"jsonrpc":"2.0","id":99,"method":"tools/list"}""");
        using var server = new McpServer(_dbPath, "test");

        await server.RunAsync(transport, CancellationToken.None);

        // Exactly one write: the null for the shutdown notification. The queued tools/list
        // is never read because the loop breaks after observing `_running == false`.
        // shutdown 通知の null 応答 1 件のみで、後続の tools/list は read されない。
        Assert.Equal(1, transport.WriteCount);
        Assert.Null(transport.LastWritten);
    }

    [Fact]
    public async Task RunAsync_InvalidUtf8DecodeFailure_ReturnsParseError()
    {
        var transport = new InvalidUtf8ReadTransport("stdio");
        using var server = new McpServer(_dbPath, "test");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
        using var response = JsonDocument.Parse(transport.LastWritten!);
        var root = response.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Contains("invalid UTF-8", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task RunAsync_InvalidUtf8DecodeFailure_WaitsForPriorStdioResponse()
    {
        var transport = new InvalidUtf8ReadTransport(
            "stdio",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        using var firstResponseStarted = new ManualResetEventSlim(false);
        using var server = new McpServer(_dbPath, "test", false, response =>
        {
            firstResponseStarted.Set();
            Thread.Sleep(200);
            return response.ToJsonString();
        });

        await server.RunAsync(transport, CancellationToken.None);

        Assert.True(firstResponseStarted.IsSet);
        Assert.Equal(2, transport.WrittenFrames.Count);
        using var first = JsonDocument.Parse(transport.WrittenFrames[0]!);
        using var second = JsonDocument.Parse(transport.WrittenFrames[1]!);
        Assert.Equal(1, first.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32700, second.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task StdioTransport_Utf16BomInput_ThrowsDecodeFailure()
    {
        var utf16Json = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}""" + "\n"))
            .ToArray();
        await using var input = new MemoryStream(utf16Json);
        await using var output = new MemoryStream();
        await using var transport = new StdioMcpTransport(input, output, bufferSize: 1024);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => transport.ReadFrameAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StdioTransport_WriteFrameAsync_FlushesBeforeReturning()
    {
        await using var input = new MemoryStream();
        await using var output = new FlushCountingStream();
        await using var transport = new StdioMcpTransport(input, output, bufferSize: 1024);

        await transport.WriteFrameAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""", CancellationToken.None);

        Assert.True(output.FlushCount > 0);
    }

    [Fact]
    public async Task RunAsync_StdioCancellationNotification_CancelsActiveRequest()
    {
        using var cancelWritten = new ManualResetEventSlim(false);
        var transport = new ShutdownProbeTransport(
            name: "stdio",
            onWrite: frame =>
            {
                if (frame is null)
                    cancelWritten.Set();
            },
            """{"jsonrpc":"2.0","id":1418,"method":"tools/call","params":{"name":"status","arguments":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1418}}""");
        using var server = new McpServer(_dbPath, "test");
        server.RequestRegisteredForTests = _ => Assert.True(cancelWritten.Wait(TimeSpan.FromSeconds(5)));

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Contains(transport.WrittenFrames, frame => frame?.Contains("\"request_cancelled\"", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RunAsync_IndexWithProgressToken_EmitsProgressNotificationBeforeResult()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "two.cs"), "public class Two { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1684,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject { ["progressToken"] = "issue-1684" },
                },
            };
            var transport = new ShutdownProbeTransport("stdio", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            var progressFrameIndex = transport.WrittenFrames.FindIndex(frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            var resultFrameIndex = transport.WrittenFrames.FindIndex(frame =>
                frame?.Contains("\"id\":1684", StringComparison.Ordinal) == true);
            Assert.True(progressFrameIndex >= 0);
            Assert.True(resultFrameIndex >= 0);
            Assert.True(progressFrameIndex < resultFrameIndex);

            var progress = JsonNode.Parse(transport.WrittenFrames[progressFrameIndex]!)!;
            Assert.Equal("issue-1684", progress["params"]!["progressToken"]!.GetValue<string>());
            Assert.Equal(2, progress["params"]!["total"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_NonStreamingIndexWithProgressToken_ReturnsFinalResultWithoutProgress()
    {
        var projectRoot = Path.Combine(Directory.GetCurrentDirectory(), $".tmp_mcp_progress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "one.cs"), "public class One { public void Run() { } }");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var server = new McpServer(dbPath, "test", dbPathExplicit: true);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1685,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = projectRoot },
                    ["_meta"] = new JsonObject { ["progressToken"] = "non-streaming" },
                },
            };
            var transport = new ShutdownProbeTransport("http-like", (Action<string?>?)null, request.ToJsonString());

            await server.RunAsync(transport, CancellationToken.None);

            Assert.DoesNotContain(transport.WrittenFrames, frame =>
                frame?.Contains("\"method\":\"notifications/progress\"", StringComparison.Ordinal) == true);
            Assert.Contains(transport.WrittenFrames, frame =>
                frame?.Contains("\"id\":1685", StringComparison.Ordinal) == true
                && frame.Contains("\"structuredContent\"", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void MaxConcurrency_DefaultExposesIssueBound()
    {
        Assert.Equal(McpServer.DefaultMaxConcurrency, _server.MaxConcurrency);
        Assert.Equal(8, _server.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_ExplicitOverride_TakesEffect()
    {
        using var server = new McpServer(
            _dbPath,
            "test",
            dbPathExplicit: false,
            serializeResponse: null,
            authenticator: null,
            toolFilter: null,
            maxConcurrency: 3);
        Assert.Equal(3, server.MaxConcurrency);
    }

    [Fact]
    public void MaxConcurrency_NonPositive_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new McpServer(_dbPath, "test", false, null, null, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new McpServer(_dbPath, "test", false, null, null, null, -1));
    }

    /// <summary>
    /// In-memory <see cref="IMcpTransport"/> that replays a scripted sequence of frames
    /// and records the responses the server writes back. Used to drive `RunAsync` from
    /// tests without standing up a real stdio / HTTP transport (#1567).
    /// テスト用のインメモリ MCP トランスポート (#1567)。固定フレーム列を再生し、サーバーの
    /// 応答を記録する。
    /// </summary>
    private sealed class ShutdownProbeTransport : IMcpTransport
    {
        private readonly Queue<string?> _frames;
        private readonly string _name;
        private readonly Action<string?>? _onWrite;

        public ShutdownProbeTransport(params string?[] frames)
            : this("shutdown-probe", null, frames)
        {
        }

        public ShutdownProbeTransport(string name, Action<string?>? onWrite, params string?[] frames)
        {
            _name = name;
            _onWrite = onWrite;
            _frames = new Queue<string?>(frames);
            // Append EOS so the loop terminates if shutdown never fires for some reason.
            // shutdown が来なかった場合のフェイルセーフとして末尾に EOS を積む。
            _frames.Enqueue(null);
        }

        public string Name => _name;
        public string Endpoint => "in-memory";
        public int WriteCount { get; private set; }
        public string? LastWritten { get; private set; }
        public List<string?> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCount++;
            LastWritten = frame;
            WrittenFrames.Add(frame);
            _onWrite?.Invoke(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InvalidUtf8ReadTransport : IMcpTransport
    {
        private readonly Queue<string> _frames;

        public InvalidUtf8ReadTransport(string name, params string[] frames)
        {
            Name = name;
            _frames = new Queue<string>(frames);
        }

        public string Name { get; }
        public string Endpoint => "invalid-utf8";
        public int WriteCount { get; private set; }
        public string? LastWritten { get; private set; }
        public List<string?> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (_frames.Count > 0)
                return Task.FromResult<string?>(_frames.Dequeue());
            throw new DecoderFallbackException("Unable to translate bytes [ED][A0][80] at index 0 from specified code page to Unicode.");
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCount++;
            LastWritten = frame;
            WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingWriteTransport : IMcpTransport
    {
        private readonly Queue<string?> _frames;

        public ThrowingWriteTransport(string name, params string?[] frames)
        {
            Name = name;
            _frames = new Queue<string?>(frames);
            _frames.Enqueue(null);
        }

        public string Name { get; }
        public string Endpoint => "throwing-write";
        public int WriteCount { get; private set; }

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCount++;
            throw new IOException("pipe closed");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_SequentialTransportWriteFailure_DoesNotThrow()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new ThrowingWriteTransport(
            "throwing-sequential",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task RunAsync_StdioTransportWriteFailure_DoesNotThrow()
    {
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
        var transport = new ThrowingWriteTransport(
            "stdio",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task HttpTransport_WriteOutOfBandFrameAsync_WithoutEventStream_IsBestEffort()
    {
        var port = AllocateLoopbackPort();
        await using var transport = new HttpMcpTransport(
            $"http://127.0.0.1:{port}/",
            "127.0.0.1",
            port,
            bearerToken: null);

        await transport.WriteOutOfBandFrameAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);

        Assert.False(transport.HasEventStreams);
    }

    [Fact]
    public void Ping_ReturnsEmptyResult()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":99,"method":"ping"}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(99, response["id"]!.GetValue<int>());
        Assert.NotNull(response["result"]);
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"unknown/method"}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Method not found", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void MissingMethod_ReturnsInvalidRequest()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void MissingMethodAndId_ReturnsNull()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0"}""")!;
        var response = _server.HandleMessage(request);

        Assert.Null(response);
    }

    // --- tools/list tests / ツール一覧テスト ---

    [Fact]
    public void ToolsList_Returns23Tools()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(24, tools.Count);

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("search", names);
        Assert.Contains("impact_analysis", names);
        Assert.Contains("definition", names);
        Assert.Contains("references", names);
        Assert.Contains("callers", names);
        Assert.Contains("callees", names);
        Assert.Contains("symbols", names);
        Assert.Contains("files", names);
        Assert.Contains("find_in_file", names);
        Assert.Contains("excerpt", names);
        Assert.Contains("map", names);
        Assert.Contains("analyze_symbol", names);
        Assert.Contains("status", names);
        Assert.Contains("outline", names);
        Assert.Contains("batch_query", names);
        Assert.Contains("validate", names);
        Assert.Contains("ping", names);
        Assert.Contains("deps", names);
        Assert.Contains("languages", names);
        Assert.Contains("index", names);
        Assert.Contains("backfill_fold", names);
        Assert.Contains("suggest_improvement", names);
    }

    [Fact]
    public void ToolsList_EveryDescriptionIncludesLanguageSupportClause()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            var description = tool["description"]!.GetValue<string>();
            Assert.Contains("Language support:", description, StringComparison.Ordinal);

            if (name is "references" or "callers" or "callees")
            {
                var expected = "Supports graph/reference extraction for: " +
                    string.Join(", ", ReferenceExtractor.GetSupportedLanguages().OrderBy(lang => lang, StringComparer.Ordinal));
                Assert.Contains(expected, description, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ToolsList_SearchHasRequiredQueryParam()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var required = searchTool["inputSchema"]!["required"]!.AsArray();
        Assert.Contains("query", required.Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void ToolsList_EveryInputSchemaRejectsAdditionalPropertiesAndPublishesStability()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            Assert.False(tool!["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
            Assert.Contains(tool["x-stability"]!.GetValue<string>(), new[] { "stable", "experimental", "deprecated" });
        }

        var impact = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        Assert.Equal("experimental", impact["x-stability"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnknownArgument_ReturnsInvalidParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"limt":1,"query":"abc"}}}""")!;

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var structured = result["structuredContent"]!;
        Assert.Equal("invalid_argument", structured["category"]!.GetValue<string>());
        Assert.Equal("limt", structured["unknown_argument"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_IndexAllowsAdvertisedMaxFileBytesArgument()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"/","maxFileBytes":1024}}}""")!;

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.DoesNotContain("Unknown argument 'maxFileBytes'", text, StringComparison.Ordinal);
        Assert.Contains("Path must be within the current working directory", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsList_SearchIncludesPathFilterParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var properties = searchTool["inputSchema"]!["properties"]!;

        Assert.NotNull(properties["path"]);
        Assert.NotNull(properties["excludePaths"]);
        Assert.NotNull(properties["excludeTests"]);
    }

    [Fact]
    public void ToolsList_SearchDescriptionStaysCompact()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var description = searchTool["description"]!.GetValue<string>();

        Assert.True(description.Length < 1000);
        Assert.Contains("USER_GUIDE.md#search", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsList_CommonSchemasAdvertiseClientSideConstraints()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var searchProperties = searchTool["inputSchema"]!["properties"]!;
        Assert.Equal(1, searchProperties["query"]!["minLength"]!.GetValue<int>());
        Assert.Equal(1024, searchProperties["query"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(1, searchProperties["limit"]!["minimum"]!.GetValue<int>());
        Assert.Equal(200, searchProperties["limit"]!["maximum"]!.GetValue<int>());

        var pathStringSchema = searchProperties["path"]!["oneOf"]!.AsArray()[0]!;
        Assert.Equal(4096, pathStringSchema["maxLength"]!.GetValue<int>());
        Assert.NotNull(pathStringSchema["pattern"]);

        var referencesTool = tools.First(t => t!["name"]!.GetValue<string>() == "references")!;
        var kindEnum = referencesTool["inputSchema"]!["properties"]!["kind"]!["enum"]!.AsArray()
            .Select(v => v!.GetValue<string>())
            .ToArray();
        Assert.Contains("call", kindEnum);
        Assert.Contains("type_reference", kindEnum);
    }

    [Fact]
    public void ToolCall_WithStructuredContent_DeclaresJsonMimeType()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;

        var content = response["result"]!["content"]!.AsArray()[0]!;
        Assert.Equal("text", content["type"]!.GetValue<string>());
        Assert.Equal("application/json", content["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_NavigationDescriptionsIncludeConcreteExamples()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var expectedExamples = new Dictionary<string, string[]>
        {
            ["search"] = ["USER_GUIDE.md#search", "prefix", "exactSubstring"],
            ["definition"] = ["Examples:", "例:", "definition {\"query\":\"McpServer\"}", "\"includeBody\":true", "\"exactName\":true"],
            ["references"] = ["Examples:", "例:", "references {\"query\":\"Run\"}", "\"kind\":\"type_reference\""],
            ["callers"] = ["Examples:", "例:", "callers {\"query\":\"HandleRequest\"}", "\"rankBy\":\"weighted\""],
            ["callees"] = ["Examples:", "例:", "callees {\"query\":\"Run\"}", "\"kind\":\"instantiate\"", "\"limit\":10"],
            ["symbols"] = ["Examples:", "例:", "symbols {\"query\":\"Service\"}", "\"kind\":\"function\"", "\"exactName\":true"],
        };

        foreach (var (name, fragments) in expectedExamples)
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var description = tool["description"]!.GetValue<string>();
            foreach (var fragment in fragments)
                Assert.Contains(fragment, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolsList_ExactAliasParametersAreExposed()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var symbolsTool = tools.First(t => t!["name"]!.GetValue<string>() == "symbols")!;

        Assert.NotNull(searchTool["inputSchema"]!["properties"]!["exactSubstring"]);
        Assert.NotNull(searchTool["inputSchema"]!["properties"]!["exact"]);
        Assert.NotNull(symbolsTool["inputSchema"]!["properties"]!["exactName"]);
        Assert.NotNull(symbolsTool["inputSchema"]!["properties"]!["exact"]);
    }

    [Fact]
    public void ToolsList_CallersCalleesKindDescription_ExcludesMetadataKinds()
    {
        // Keep the `kind` schema description honest: the callers/callees handlers reject
        // metadata kinds (`attribute`, `annotation`) as a usage error, so the schema must
        // not advertise them as valid filter values.
        // callers/callees の handler は metadata kinds (`attribute` / `annotation`) を拒否するため、
        // schema の `kind` description も有効値として列挙しないこと。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "callers", "callees" })
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var kindDescription = tool["inputSchema"]!["properties"]!["kind"]!["description"]!.GetValue<string>();

            Assert.Contains("call-graph", kindDescription);
            Assert.Contains("call, instantiate, subscribe", kindDescription);
            Assert.Contains("rejected", kindDescription);
            Assert.Contains("references", kindDescription);
        }
    }

    [Fact]
    public void ToolsList_CallersCalleesAnalyzeSymbolDescriptions_PinSnakeCaseMixedKindFields()
    {
        // MCP structured JSON follows the same snake_case convention as CLI JSON.
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "callers", "callees", "analyze_symbol" })
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var description = tool["description"]!.GetValue<string>();

            Assert.Contains("reference_kind", description);
            Assert.Contains("reference_kinds", description);
            Assert.Contains("has_mixed_reference_kinds", description);
            Assert.DoesNotContain("referenceKind", description);
            Assert.DoesNotContain("hasMixedReferenceKinds", description);
        }
    }

    [Fact]
    public void ToolsList_ImpactAnalysisDescribesHeuristicFallback()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var impactTool = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        var description = impactTool["description"]!.GetValue<string>();
        var limitDescription = impactTool["inputSchema"]!["properties"]!["limit"]!["description"]!.GetValue<string>();

        Assert.Contains("heuristic file-level dependency hints", description);
        Assert.Contains("impact_mode", description);
        Assert.Contains("file_impacts", description);
        Assert.Contains("heuristic file-level dependency hints", limitDescription);
        Assert.Contains("truncated", limitDescription);
    }

    [Fact]
    public void ToolsList_IndexHasRequiredPathParam()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var indexTool = tools.First(t => t!["name"]!.GetValue<string>() == "index")!;
        var required = indexTool["inputSchema"]!["required"]!.AsArray();
        Assert.Contains("path", required.Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void ToolsList_QueryToolsHaveReadOnlyAnnotations()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var queryToolNames = new[] { "search", "definition", "references", "callers", "callees", "symbols", "files", "find_in_file", "excerpt", "map", "analyze_symbol", "status", "outline" };

        foreach (var name in queryToolNames)
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var annotations = tool["annotations"];
            Assert.NotNull(annotations);
            Assert.True(annotations!["readOnlyHint"]!.GetValue<bool>(), $"{name} should have readOnlyHint=true");
            Assert.False(annotations["destructiveHint"]!.GetValue<bool>(), $"{name} should have destructiveHint=false");
            Assert.True(annotations["idempotentHint"]!.GetValue<bool>(), $"{name} should have idempotentHint=true");
            Assert.False(annotations["openWorldHint"]!.GetValue<bool>(), $"{name} should have openWorldHint=false");
        }
    }

    [Fact]
    public void ToolsList_IndexToolHasWriteAnnotations()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var indexTool = tools.First(t => t!["name"]!.GetValue<string>() == "index")!;
        var annotations = indexTool["annotations"];
        Assert.NotNull(annotations);
        Assert.False(annotations!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(annotations["destructiveHint"]!.GetValue<bool>());
        Assert.False(annotations["idempotentHint"]!.GetValue<bool>());
        Assert.False(annotations["openWorldHint"]!.GetValue<bool>());
    }

    // --- tool enablement filter tests (#1561) ---

    [Fact]
    public void McpToolFilter_AllowAll_EnablesEveryKnownTool()
    {
        var filter = McpToolFilter.AllowAll();
        foreach (var name in McpToolFilter.KnownToolNames)
            Assert.True(filter.IsEnabled(name), $"{name} should be enabled in AllowAll");
    }

    [Fact]
    public void McpToolFilter_Parse_AllowListPinsVisibleSet()
    {
        var filter = McpToolFilter.Parse("search, references", null);

        Assert.True(filter.IsEnabled("search"));
        Assert.True(filter.IsEnabled("references"));
        Assert.False(filter.IsEnabled("index"));
        Assert.False(filter.IsEnabled("backfill_fold"));
        Assert.False(filter.IsEnabled("suggest_improvement"));
    }

    [Fact]
    public void McpToolFilter_Parse_DenyListRemovesIndividualTools()
    {
        var filter = McpToolFilter.Parse(null, "index, backfill_fold");

        Assert.True(filter.IsEnabled("search"));
        Assert.False(filter.IsEnabled("index"));
        Assert.False(filter.IsEnabled("backfill_fold"));
    }

    [Fact]
    public void McpToolFilter_Parse_OverlongAllowListFailsClosed_Issue2905()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var filter = McpToolFilter.Parse(new string('s', McpToolFilter.MaxToolFilterCsvLength + 1), null);

                foreach (var name in McpToolFilter.KnownToolNames)
                    Assert.False(filter.IsEnabled(name), $"{name} should be disabled when an invalid allowlist is supplied");
                Assert.Contains(McpToolFilter.AllowEnvVarName, stderr.ToString());
                Assert.Contains("was rejected", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_TooManyDenyEntriesAreRejected_Issue2905()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var tooMany = string.Join(',', Enumerable.Repeat("index", McpToolFilter.MaxToolFilterCsvEntries + 1));
                var filter = McpToolFilter.Parse(null, tooMany);

                Assert.True(filter.IsEnabled("index"));
                Assert.Contains(McpToolFilter.DenyEnvVarName, stderr.ToString());
                Assert.Contains("accepts at most", stderr.ToString());
                Assert.Contains("was rejected", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    [Fact]
    public void McpToolFilter_Parse_AllowWinsOverDeny()
    {
        var filter = McpToolFilter.Parse("search,index", "index");

        Assert.True(filter.IsEnabled("search"));
        Assert.True(filter.IsEnabled("index"));
        Assert.False(filter.IsEnabled("references"));
    }

    [Fact]
    public void McpToolFilter_Parse_UnknownNamesInDenyListDoNotAffectKnownTools()
    {
        // A typo in CDIDX_MCP_TOOLS_DENY simply does not match anything; the known set stays
        // enabled. Allowlist semantics deliberately differ: a non-empty allowlist is treated
        // as a strict pin, so an allowlist of only-unknown names exposes nothing — that empty
        // surface is visible at the next tools/list call.
        var denyFilter = McpToolFilter.Parse(null, "bogus_tool");
        foreach (var name in McpToolFilter.KnownToolNames)
            Assert.True(denyFilter.IsEnabled(name), $"{name} should remain enabled when denylist names only unknown tools");

        var allowFilter = McpToolFilter.Parse("bogus_tool", null);
        foreach (var name in McpToolFilter.KnownToolNames)
            Assert.False(allowFilter.IsEnabled(name), $"{name} should be disabled when allowlist only names unknown tools");
    }

    [Fact]
    public void McpToolFilter_IsKnownTool_DistinguishesKnownFromUnknown()
    {
        Assert.True(McpToolFilter.IsKnownTool("search"));
        Assert.True(McpToolFilter.IsKnownTool("SEARCH"));
        Assert.False(McpToolFilter.IsKnownTool("bogus_tool"));
        Assert.False(McpToolFilter.IsKnownTool(null));
        Assert.False(McpToolFilter.IsKnownTool(string.Empty));
    }

    [Fact]
    public void ToolsList_FilteredByAllowList_HidesDisabledTools()
    {
        var allow = McpToolFilter.Parse("search, references", null);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, allow);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = server.HandleMessage(request)!;
        var tools = response["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "references", "search" }, names);
    }

    [Fact]
    public void ToolsList_FilteredByDenyList_HidesDeniedTools()
    {
        var deny = McpToolFilter.Parse(null, "index,backfill_fold,suggest_improvement");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = server.HandleMessage(request)!;
        var names = response["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.DoesNotContain("index", names);
        Assert.DoesNotContain("backfill_fold", names);
        Assert.DoesNotContain("suggest_improvement", names);
        Assert.Contains("search", names);
        Assert.Contains("references", names);
    }

    [Fact]
    public void ToolsCall_DisabledTool_ReturnsMethodNotFoundError()
    {
        var deny = McpToolFilter.Parse(null, "index");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"/tmp/whatever"}}}""")!;
        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Tool not enabled", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_DisabledInnerTool_ReturnsSlotError()
    {
        // batch_query stays enabled, but the slot for a denied inner tool must surface a
        // per-slot error instead of executing it. Otherwise CDIDX_MCP_TOOLS_DENY could be
        // bypassed by smuggling the disabled name into a batch slot.
        var deny = McpToolFilter.Parse(null, "symbols");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"symbols","arguments":{"query":"App"}}]}}}""")!;
        var response = server.HandleMessage(request)!;
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();

        Assert.Single(results);
        var slot = results[0]!.AsObject();
        var slotError = slot["error"]!.GetValue<string>();
        Assert.Contains("Tool not enabled", slotError);
        // Carry the JSON-RPC error code on the slot so AI clients can branch on a code
        // instead of substring-matching prose (#1561).
        // AI クライアントが prose を部分一致せず code で分岐できるよう、slot にコードを乗せる (#1561)。
        Assert.Equal(-32601, slot["code"]!.GetValue<int>());
    }

    [Fact]
    public void Initialize_InstructionsOmitsDisabledTools()
    {
        // BuildInstructions feeds tool-selection guidance to AI clients via `initialize`.
        // Once an operator disables a tool through the gate, the instructions must stop
        // advertising it; otherwise the client follows the guidance and hits a `-32601`
        // every time (#1561).
        // BuildInstructions は initialize 経由で AI クライアントに tool 選択ガイダンスを渡す。
        // gate で無効化された tool を案内し続けると、クライアントが従って毎回 -32601 を踏むので、
        // 無効化されたツールについての文章は出力しない (#1561)。
        var allow = McpToolFilter.Parse("search,definition", null);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, allow);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!;
        var response = server.HandleMessage(request)!;
        var instructions = response["result"]!["instructions"]!.GetValue<string>();

        Assert.Contains("'search'", instructions);
        Assert.Contains("'definition'", instructions);
        // Disabled tools must not be mentioned by name. Use single-quote-anchored names so
        // that prose like "graph-supported languages" does not false-positive on "graph".
        // 無効化された tool 名はガイダンスに含めない。"graph-supported languages" のような
        // 一般語で誤検出しないよう、'name' 形式で照合する。
        Assert.DoesNotContain("'index'", instructions);
        Assert.DoesNotContain("'map'", instructions);
        Assert.DoesNotContain("'status'", instructions);
        Assert.DoesNotContain("'batch_query'", instructions);
        Assert.DoesNotContain("'backfill_fold'", instructions);
        Assert.DoesNotContain("'suggest_improvement'", instructions);
        Assert.DoesNotContain("'analyze_symbol'", instructions);
        Assert.DoesNotContain("'outline'", instructions);
        Assert.DoesNotContain("'find_in_file'", instructions);
        Assert.DoesNotContain("'excerpt'", instructions);
        Assert.DoesNotContain("'languages'", instructions);
        Assert.DoesNotContain("'files'", instructions);
        Assert.DoesNotContain("'deps'", instructions);
        Assert.DoesNotContain("'unused_symbols'", instructions);
        Assert.DoesNotContain("'symbol_hotspots'", instructions);
        Assert.DoesNotContain("'impact_analysis'", instructions);
        // The exactName-guidance sentence used to enumerate "symbols/definition/references/
        // callers/callees/analyze_symbol" verbatim. With only 'search' and 'definition'
        // enabled, none of those disabled names should leak into the guidance.
        // exactName 案内に旧実装はツール名を直書きしていたため、無効化されたツール名が漏れて
        // いないかを bare 名前 (single-quote 無し) でも確認する。
        Assert.DoesNotContain("symbols/", instructions);
        Assert.DoesNotContain("references/", instructions);
    }

    [Fact]
    public void ToolsCall_BatchQuery_DisabledWriteTool_PrefersGateCodeOverWriteGuard()
    {
        // When a write tool is excluded by the gate AND smuggled into a batch slot, both
        // guards could match. The gate runs first so the slot carries the structured
        // `code: -32601` shape — "this tool is not on offer for this deployment" — instead
        // of the generic write-in-batch prose. Otherwise scoped clients see different
        // error shapes depending on whether a tool happened to be a write tool (#1561).
        // 書き込みツールが gate でも除外され、かつ batch slot に紛れ込んだケース。両 guard が
        // 該当するが、gate を先に走らせて構造化 `code: -32601` を出すことで、scoped クライアントが
        // 「このデプロイでは無効」という意図を一貫した shape で受け取れる (#1561)。
        var allow = McpToolFilter.Parse("batch_query,search", null);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, allow);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"index","arguments":{"path":"/tmp/x"}}]}}}""")!;
        var response = server.HandleMessage(request)!;
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();

        Assert.Single(results);
        var slot = results[0]!.AsObject();
        Assert.Equal(-32601, slot["code"]!.GetValue<int>());
        Assert.Contains("Tool not enabled", slot["error"]!.GetValue<string>());
    }

    // --- tools/call tests / ツール呼び出しテスト ---

    [Fact]
    public void ToolsCall_Search_ReturnsResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 search result", text);
        // Content summary includes file path for AI orientation / サマリにファイルパスを含む
        Assert.Contains("src/app.cs", text);

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/app.cs", structured["results"]![0]!["path"]!.GetValue<string>());
        Assert.NotNull(structured["results"]![0]!["snippet"]);
        Assert.NotNull(structured["results"]![0]!["matchLines"]);
        Assert.NotNull(structured["results"]![0]!["highlights"]);
        Assert.Null(structured["results"]![0]!["content"]);
    }

    [Fact]
    public void ToolsCall_Search_GuardFiltersReturnEvidence_Issue2852()
    {
        InsertIndexedFile(
            "src/guard-mcp.cs",
            "csharp",
            """
            using System.IO;

            public class GuardMcp
            {
                public void Atomic(string path, string tempPath)
                {
                    using var stream = new FileStream(path, FileMode.Create);
                    File.Move(tempPath, path, overwrite: true);
                }

                public void NonAtomic(string path)
                {
                    using var stream = new FileStream(path, FileMode.Create);
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"FileMode.Create","exactSubstring":true,"requireAfter":"File.Move","guardWindow":2}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        var result = structured["results"]![0]!;
        Assert.Equal("src/guard-mcp.cs", result["path"]!.GetValue<string>());
        var evidence = Assert.Single(result["guardEvidence"]!.AsArray());
        Assert.Equal("require", evidence!["role"]!.GetValue<string>());
        Assert.Equal("after", evidence["direction"]!.GetValue<string>());
        Assert.Equal("File.Move", evidence["query"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_GuardPaginationResumesWithinSplitChunk_Issue2852()
    {
        InsertIndexedFile(
            "src/guard-paged.cs",
            "csharp",
            """
            using System.IO;

            public class GuardPaged
            {
                public void First(string path)
                {
                    var one = File.ReadAllText(path);
                }

                public void Second(string path)
                {
                    var two = File.ReadAllText(path);
                }
            }
            """);

        var firstRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"File.ReadAllText","exactSubstring":true,"rejectBefore":"Length","guardWindow":1,"limit":1}}}""")!;
        var firstResponse = _server.HandleMessage(firstRequest)!;
        var firstStructured = firstResponse["result"]!["structuredContent"]!;
        var firstSnippet = firstStructured["results"]![0]!["snippet"]!.GetValue<string>();
        var cursor = firstStructured["next_cursor"]!.GetValue<string>();

        var secondRequest = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{\"query\":\"File.ReadAllText\",\"exactSubstring\":true,\"rejectBefore\":\"Length\",\"guardWindow\":1,\"limit\":1,\"cursor\":\"" + cursor + "\"}}}")!;
        var secondResponse = _server.HandleMessage(secondRequest)!;
        var secondStructured = secondResponse["result"]!["structuredContent"]!;
        var secondSnippet = secondStructured["results"]![0]!["snippet"]!.GetValue<string>();

        Assert.Contains("one", firstSnippet);
        Assert.Contains("two", secondSnippet);
    }

    [Fact]
    public void ToolsCall_Search_ExactSubstringReturnsLiteralHighlightMetadata()
    {
        InsertIndexedFile("src/sql.cs", "csharp", "var CommandText = $\"SELECT 1\";\nvar CommandText = other;\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"CommandText = $","exactSubstring":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var highlight = response["result"]!["structuredContent"]!["results"]![0]!["highlights"]![0]!;
        var literalOccurrence = highlight["literalTermOccurrences"]![0]!;
        Assert.Equal("CommandText = $", highlight["literalTerms"]![0]!.GetValue<string>());
        Assert.Equal("CommandText = $", literalOccurrence["term"]!.GetValue<string>());
        Assert.Equal(1, literalOccurrence["line"]!.GetValue<int>());
        Assert.Equal(5, literalOccurrence["column"]!.GetValue<int>());
        Assert.Equal("CommandText = $".Length, literalOccurrence["length"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Search_PunctuationHeavyQueryAddsExactSubstringRecoveryHint()
    {
        InsertIndexedFile("src/sql.cs", "csharp", "var CommandText = $\"SELECT 1\";\nvar CommandText = other;\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"CommandText = $","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var recoveryHint = response["result"]!["structuredContent"]!["recovery_hint"]!;
        Assert.Equal("punctuation_heavy_query", recoveryHint["reason"]!.GetValue<string>());
        Assert.Equal("search", recoveryHint["tool"]!.GetValue<string>());
        Assert.Equal("CommandText = $", recoveryHint["args"]!["query"]!.GetValue<string>());
        Assert.True(recoveryHint["args"]!["exactSubstring"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Search_ExcludesGeneratedFilesByDefault()
    {
        InsertIndexedFile("src/generated.g.cs", "csharp", "class Generated { void Needle() {} }\n", generated: true);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Needle"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Empty(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_Search_IncludeGeneratedReturnsGeneratedFiles()
    {
        InsertIndexedFile("src/generated.g.cs", "csharp", "class Generated { void Needle() {} }\n", generated: true);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Needle","includeGenerated":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/generated.g.cs", structured["results"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_NoResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"nonexistent_xyz_123"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("No results found", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        // Zero-result responses include freshness hint / 0件時に鮮度ヒントを含む
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0);
        Assert.True(structured["freshness_available"]!.GetValue<bool>());
        Assert.NotNull(structured["indexed_at"]);
    }

    [Fact]
    public void ToolsCall_Files_NoResults_OnEmptyIndex_EmitsNullIndexedAt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_empty_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files","arguments":{"query":"nonexistent_xyz_123"}}}""")!;
                var response = server.HandleMessage(request)!;
                using var document = JsonDocument.Parse(response.ToJsonString());
                var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");

                Assert.Equal(0, structured.GetProperty("count").GetInt32());
                Assert.Equal(0, structured.GetProperty("results").GetArrayLength());
                Assert.Equal(0, structured.GetProperty("indexed_file_count").GetInt64());
                Assert.True(structured.GetProperty("freshness_available").GetBoolean());
                Assert.True(structured.TryGetProperty("indexed_at", out var indexedAt));
                Assert.Equal(JsonValueKind.Null, indexedAt.ValueKind);
            }
        }
        finally
        {
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Search_RawQuerySupportsFtsSyntax()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Ap*","rawQuery":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.True(response["result"]!["structuredContent"]!["rawQuery"]!.GetValue<bool>());
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["results"]![0]!["path"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("analyze_symbol")]
    [InlineData("impact_analysis")]
    public void ToolsCall_BareVerbatimPrefix_IsRejected(string toolName)
    {
        var request = JsonNode.Parse($@"{{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{{""name"":""{toolName}"",""arguments"":{{""query"":""@""}}}}}}")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("bare verbatim prefixes like `@` are not valid queries", text);
    }

    [Fact]
    public void ToolsCall_Search_SnippetLinesControlsExcerptLength()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","snippetLines":3}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(3, response["result"]!["structuredContent"]!["snippetLines"]!.GetValue<int>());
        var snippet = response["result"]!["structuredContent"]!["results"]![0]!["snippet"]!.GetValue<string>();
        Assert.True(snippet.Split('\n').Length <= 3);
    }

    [Fact]
    public void ToolsCall_Search_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("src/long.cs", "csharp", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"TARGET","exact":true,"maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Contains("TARGET", result["snippet"]!.GetValue<string>());
        Assert.DoesNotContain("...(+", result["snippet"]!.GetValue<string>());
        Assert.True(result["snippet"]!.GetValue<string>().Length > 512);
    }

    [Fact]
    public void ToolsCall_Search_MaxLineWidthAboveCeilingReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","maxLineWidth":4097}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("maxLineWidth must be less than or equal to 4096", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_ExcludeTests_ReturnsOnlySourceMatches()
    {
        InsertIndexedFile("tests/app_test.cs", "csharp", "public class AppTests { public void RunScenario() { var app = new App(); } }");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","excludeTests":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.True(response["result"]!["structuredContent"]!["excludeTests"]!.GetValue<bool>());
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["results"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Map_ReturnsRepoOverview()
    {
        InsertIndexedFile("src/Program.cs", "csharp", "public class Program\n{\n    public static void Main(string[] args)\n    {\n        var app = new App();\n    }\n}\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"map","arguments":{"limit":5,"excludeTests":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(5, response["result"]!["structuredContent"]!["limit"]!.GetValue<int>());
        Assert.NotNull(response["result"]!["structuredContent"]!["languages"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["modules"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["topFiles"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["indexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["projectRoot"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspaceIndexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspaceLatestModified"]);
        Assert.Contains("Main", response["result"]!["structuredContent"]!["entrypoints"]!.ToJsonString());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ReturnsBundledContext()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","includeBody":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("Run", response["result"]!["structuredContent"]!["query"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["structuredContent"]!["file"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["definitions"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["nearby_symbols"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["callers"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["callees"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspace_indexed_at"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspace_latest_modified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["project_root"]);
        Assert.True(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Heading","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("Use search, definition, excerpt, or files instead.", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_KeepsSubscribeReferencesVisibleInBundle()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var reference = response["result"]!["structuredContent"]!["references"]![0]!;
        var caller = response["result"]!["structuredContent"]!["callers"]![0]!;

        Assert.Equal("subscribe", reference["referenceKind"]!.GetValue<string>());
        Assert.Equal("Hook", reference["containerName"]!.GetValue<string>());
        Assert.Equal("Hook", caller["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", caller["calleeName"]!.GetValue<string>());
        Assert.Empty(response["result"]!["structuredContent"]!["callees"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_KeepsSubscribeCalleesVisibleForCallerSymbols()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Hook","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var callee = response["result"]!["structuredContent"]!["callees"]![0]!;

        Assert.Equal("Hook", callee["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", callee["calleeName"]!.GetValue<string>());
        Assert.Equal("event", callee["referenceKind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_NonExactEnumMemberStaysGraphSupported()
    {
        InsertIndexedFile("src/colors.cs", "csharp",
            """
            namespace Demo;

            public enum Color
            {
                Red,
                Green
            }

            public class UsesColor
            {
                public Color Shade => Color.Red;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Red","lang":"csharp"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var definition = structured["definitions"]![0]!;

        Assert.Equal("Red", definition["name"]!.GetValue<string>());
        Assert.Equal("enum", definition["containerKind"]!.GetValue<string>());
        Assert.Equal("Color", definition["containerName"]!.GetValue<string>());
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("Shade", structured["references"]![0]!["containerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_NonExactCrossLanguageMixedHitPrefersGraphCapablePrimaryDefinition()
    {
        InsertIndexedFile("web/app.js", "javascript",
            """
            function Ready() {}

            function Helper() {}

            Ready();
            """);
        InsertIndexedFile("src/status.cs", "csharp",
            """
            namespace Demo;

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Ready"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var nearbyPaths = structured["nearby_symbols"]!
            .AsArray()
            .Select(symbol => symbol?["path"]?.GetValue<string>())
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal("web/app.js", structured["file"]!["path"]!.GetValue<string>());
        Assert.Equal("javascript", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Contains("web/app.js", nearbyPaths);
        Assert.DoesNotContain("src/status.cs", nearbyPaths);
        Assert.Contains(structured["nearby_symbols"]!.AsArray(),
            symbol => symbol?["name"]?.GetValue<string>() == "Helper");
        Assert.All(structured["references"]!.AsArray(),
            reference => Assert.Equal("javascript", reference?["lang"]?.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Callers_DefaultQueryKeepsSubscribeRowsVisible()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var row = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("Hook", row["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", row["calleeName"]!.GetValue<string>());
        // #501: MCP wire format exposes referenceKind (preferred summary), referenceKinds (sorted distinct), and hasMixedReferenceKinds
        // #501: MCP のワイヤ形式は referenceKind（要約）、referenceKinds（ソート済み distinct）、hasMixedReferenceKinds を返す
        Assert.Equal("event", row["referenceKind"]!.GetValue<string>());
        Assert.False(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "event" }, kinds);
    }

    [Fact]
    public void ToolsCall_Callers_SurfacesMixedReferenceKindsForCallAndSubscribeContainer()
    {
        InsertIndexedFile("src/MixedOwner.cs", "csharp",
            """
            using System;

            public class MixedOwner
            {
                public event EventHandler? Changed;

                public void SetupAndFire()
                {
                    Changed += OnChanged;
                    Changed(this, EventArgs.Empty);
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var row = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("SetupAndFire", row["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", row["calleeName"]!.GetValue<string>());
        Assert.Equal(2, row["referenceCount"]!.GetValue<int>());
        Assert.True(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "event", "invoke" }, kinds);
        Assert.Equal("event", row["referenceKind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_SurfacesMixedReferenceKindsInBundledCallers()
    {
        InsertIndexedFile("src/MixedOwner.cs", "csharp",
            """
            using System;

            public class MixedOwner
            {
                public event EventHandler? Changed;

                public void SetupAndFire()
                {
                    Changed += OnChanged;
                    Changed(this, EventArgs.Empty);
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var caller = response["result"]!["structuredContent"]!["callers"]![0]!;

        Assert.Equal("SetupAndFire", caller["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", caller["calleeName"]!.GetValue<string>());
        Assert.Equal("event", caller["referenceKind"]!.GetValue<string>());
        Assert.True(caller["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = caller["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "event", "invoke" }, kinds);
    }

    [Fact]
    public void ToolsCall_Callees_DefaultQueryKeepsSubscribeRowsVisible()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Hook","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var row = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("Hook", row["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", row["calleeName"]!.GetValue<string>());
        Assert.Equal("event", row["referenceKind"]!.GetValue<string>());
        // #501: callees rows stay split per kind so referenceKinds is a single-element array and hasMixedReferenceKinds is false
        // #501: callees 行は kind 単位で分かれるため referenceKinds は単要素、hasMixedReferenceKinds は false
        Assert.False(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "event" }, kinds);
    }

    [Fact]
    public void ToolsCall_References_ReturnsIndexedReference()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("login", response["result"]!["structuredContent"]!["results"]![0]!["containerName"]!.GetValue<string>());
        Assert.Equal("call", response["result"]!["structuredContent"]!["results"]![0]!["referenceKind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = "def login(user, password): return Run(user) # " + new string('x', 700);
        InsertIndexedFile("src/session.py", "python", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var result = response["result"]!["structuredContent"]!["results"]![0]!;

        Assert.False(result["contextTruncated"]!.GetValue<bool>());
        Assert.Contains("Run(user)", result["context"]!.GetValue<string>());
        Assert.DoesNotContain("...(+", result["context"]!.GetValue<string>());
        Assert.True(result["context"]!.GetValue<string>().Length > 512);
    }

    [Fact]
    public void ToolsCall_References_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_ExactOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_ExactEnumMember_ReturnsIndexedReference()
    {
        InsertIndexedFile("src/colors.cs", "csharp",
            """
            namespace Demo;

            public enum Color
            {
                Red,
                Green
            }

            public class UsesColor
            {
                public Color Shade => Color.Red;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Red","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("Found 1 reference.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("Shade", structured["results"]![0]!["containerName"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
    }

    [Fact]
    public void ToolsCall_References_ExactMixedCallableAndEnumMemberKeepsGraphSupported()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public class Worker
            {
                public void Ready() { }

                public void Use()
                {
                    Ready();
                }
            }

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Ready","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.Equal("Use", structured["results"]![0]!["containerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_ExactCrossLanguageMixedHitDoesNotForceCSharpGraphLanguage()
    {
        InsertIndexedFile("web/app.js", "javascript",
            """
            export function Ready() {}

            Ready();
            """);
        InsertIndexedFile("src/status.cs", "csharp",
            """
            namespace Demo;

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Ready","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("javascript", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
    }

    [Fact]
    public void ToolsCall_Callers_ReturnsCallerSummary()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Run"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("login", response["result"]!["structuredContent"]!["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_ExactEnumMember_ReturnsIndexedCaller()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public enum Nested
            {
                A = 1,
                B = A
            }

            public class UsesEnum
            {
                public Nested Value => Nested.A;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"A","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("Value", structured["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Found 1 caller.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_ExactMixedCallableAndEnumMemberKeepsGraphSupported()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public class Worker
            {
                public void Ready() { }

                public void Use()
                {
                    Ready();
                }
            }

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Ready","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.Equal("Use", structured["results"]![0]!["callerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_ReturnsCalleeSummary()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"login"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("login", response["result"]!["structuredContent"]!["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_ExactEnumMember_UsesZeroSchema()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public enum Nested
            {
                A = 1,
                B = A
            }

            public class UsesEnum
            {
                public Nested Value => Nested.A;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"A","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("No callees found.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_ExactMixedCallableAndEnumMemberKeepsGraphSupported()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public class Worker
            {
                public void Ready()
                {
                    Next();
                }

                public void Next() { }
            }

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Ready","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.Equal("Next", structured["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_analyze_symbol_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"fn_Target","lang":"sql","exact":true}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_References_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_references_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"fn_Target","lang":"sql"}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Callers_MixedRepoStaleSqlGraphContractDoesNotDegradePureCSharpQuery()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_callers_mixed_sql_graph_contract");
        try
        {
            var dbPath = CreateMixedSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"N","exact":true}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("callers", "attribute")]
    [InlineData("callers", "annotation")]
    [InlineData("callers", "type_reference")]
    [InlineData("callers", "import")]
    [InlineData("callees", "attribute")]
    [InlineData("callees", "annotation")]
    [InlineData("callees", "type_reference")]
    [InlineData("callees", "import")]
    public void ToolsCall_CallersOrCallees_NonCallGraphKindReturnsToolError(string tool, string kind)
    {
        // issue #293 + issue #444: the MCP `callers` / `callees` tools must reject non-call-graph
        // kinds. Metadata rows (`attribute` / `annotation`) are attributed to the enclosing
        // body-range symbol (so `callers Obsolete kind=attribute` reports the enclosing class
        // instead of the annotated method, and file-level targets drop entirely). `type_reference`
        // rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/
        // `instanceof`, XML-doc `cref`) and not runtime calls. AI clients should be redirected to
        // the `references` tool for these enumerations. `import` rows are structural dependency
        // edges, not runtime calls, and follow the same rejection path.
        // issue #293 + issue #444 補足: MCP の `callers` / `callees` ツールは非 call-graph な kind を
        // 必ず弾く。metadata 行 (`attribute` / `annotation`) は body-range の外側シンボルに帰属する
        // ため、`callers Obsolete kind=attribute` は注釈対象のメソッドではなく外側クラスを返し、
        // file-level target は完全に脱落する。`type_reference` は宣言型・generic 制約・`is`/`as`/
        // `instanceof`・XML-doc `cref` といった compile-time な型言及であり実行時呼び出しではない。
        // `import` 行も runtime call ではなく構造的な dependency edge なので同じ拒否経路に入る。
        // AI クライアントは列挙のために `references` ツールに誘導する。
        var requestJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\","
            + "\"params\":{\"name\":\"" + tool + "\","
            + "\"arguments\":{\"query\":\"SomeSymbol\",\"kind\":\"" + kind + "\"}}}";
        var request = JsonNode.Parse(requestJson)!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"'kind: {kind}' is not supported on '{tool}'", text);
        Assert.Contains("'references' tool", text);
        if (kind == "import")
            Assert.Contains("Import references are structural dependency edges, not runtime calls", text);
    }

    [Fact]
    public void ToolsCall_References_AcceptsTypeReferenceKind()
    {
        // issue #444: `references` with `kind: "type_reference"` is a legitimate query (the
        // compile-time type-position edges emitted by ReferenceExtractor for C#/Java base
        // lists, declaration types, generic constraints, `is`/`as`/`instanceof`, and XML-doc
        // `cref`). It must succeed and return the expected `reference_kind` in
        // structuredContent, unlike the rejected `callers`/`callees` tools.
        // issue #444: MCP `references` の `kind: "type_reference"` は compile-time な型位置エッジ
        // を列挙する正当なクエリ（C#/Java の継承リスト・宣言型・generic 制約・`is`/`as`/
        // `instanceof`・XML-doc `cref`）。拒否される `callers` / `callees` とは異なり、成功して
        // structuredContent に `reference_kind` を返さなければならない。
        InsertIndexedFile("src/Target.cs", "csharp",
            """
            public class TargetBase { }
            """);
        InsertIndexedFile("src/Consumer.cs", "csharp",
            """
            public class Consumer : TargetBase
            {
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"TargetBase","kind":"type_reference","lang":"csharp","exactName":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("type_reference", structured["kind"]!.GetValue<string>());
        Assert.True(structured["count"]!.GetValue<int>() >= 1);
        var results = structured["results"]!.AsArray();
        Assert.Contains(results, r => r!["referenceKind"]!.GetValue<string>() == "type_reference"
            && r["symbolName"]!.GetValue<string>() == "TargetBase");
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ClassSymbolReturnsHeuristicFileDependencyHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var fileImpacts = structured["file_impacts"]!.AsArray();

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.Equal("src/App.cs", fileImpacts[0]!["sourcePath"]!.GetValue<string>());
        Assert.Equal("src/FolderDiffService.cs", fileImpacts[0]!["targetPath"]!.GetValue<string>());
        Assert.True(structured["has_class_like_definitions"]!.GetValue<bool>());
        Assert.Contains("heuristic hints only", structured["note"]!.GetValue<string>());
        Assert.Contains("heuristic only", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ClassAndNamespaceWithSameNameStillReturnsHeuristicHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            namespace FooService;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.True(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal(2, structured["definition_count"]!.GetValue<int>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_HeuristicHintsUseVisibleCount()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["file_count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_file_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_FoldEquivalentClassDefinitionsReportAmbiguity()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FullwidthFooService.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);
        MarkFoldReady();

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.Equal(2, structured["definition_count"]!.GetValue<int>());
        Assert.Equal("multiple_definition_files", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ClassCollisionWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/BarService.cs", "csharp",
            """
            public class BarService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(BarService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.False(structured["heuristic"]!.GetValue<bool>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_CommentOnlyTypeMentionDoesNotProduceHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/OtherService.cs", "csharp",
            """
            public class OtherService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(OtherService service)
                {
                    service.Run(); // TODO: maybe replace with FooService later
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_StringLiteralTypeMentionDoesNotProduceHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/Worker.cs", "csharp",
            """
            public class Worker
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Worker worker)
                {
                    var label = "FooService";
                    worker.Execute();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_NamespaceStaysZero()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace Acme;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            namespace Acme;

            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Acme"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal("non_callable_symbol_kind", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ImportOnlyQueryReportsNonCallableSymbolKind()
    {
        InsertIndexedFile("src/app.py", "python",
            """
            import requests
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"requests"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Equal("non_callable_symbol_kind", structured["zero_result_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_UnicodeTypeEvidenceStillReturnsHints()
    {
        InsertIndexedFile("src/ＦｏｏＳｅｒｖｉｃｅ.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(ＦｏｏＳｅｒｖｉｃｅ service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"ＦｏｏＳｅｒｖｉｃｅ"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_DuplicateDefinitionsInOneFileReportAmbiguity()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace A
            {
                public class FooService
                {
                    public void Run() { }
                }
            }

            namespace B
            {
                public class FooService
                {
                    public void Run() { }
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(2, structured["definition_count"]!.GetValue<int>());
        Assert.Equal(1, structured["definition_file_count"]!.GetValue<int>());
        Assert.True(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal("multiple_definitions", structured["zero_result_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_DepthZeroReturnsResolvedSymbolWithoutCallers()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxHops":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Empty(structured["callers"]!.AsArray());
        Assert.Equal("depth_requested_zero", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Equal("precondition", structured["suggestion_type"]!.GetValue<string>());
        var failureChain = Assert.IsType<JsonArray>(structured["impact_failure_chain"]);
        Assert.Equal("depth_requested_zero", Assert.Single(failureChain)!.GetValue<string>());
        Assert.Equal("Use `cdidx impact <symbol> --max-hops 1` or higher to traverse callers.", structured["suggestion"]!.GetValue<string>());
    }

    // #1534: requests above the server cap (50) must surface a warning and `max_hops_requested`
    // instead of silently clamping, so agents can react (raise the cap, accept partial depth, etc.).
    // #1534: サーバー上限 (50) を超える maxHops は黙ってクランプせず、warnings と
    // max_hops_requested で通知し、エージェントが対応できるようにする。
    [Fact]
    public void ToolsCall_ImpactAnalysis_MaxHopsAboveCapSurfacesWarningAndRequestedValue()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxHops":100}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(50, structured["max_hops"]!.GetValue<int>());
        Assert.Equal(100, structured["max_hops_requested"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth"]!.GetValue<int>());
        Assert.Equal(100, structured["max_depth_requested"]!.GetValue<int>());
        var warnings = structured["warnings"]!.AsArray();
        Assert.Single(warnings);
        var warning = warnings[0]!.GetValue<string>();
        Assert.Contains("maxHops was clamped from 100 to 50", warning);
        Assert.Contains("[0, 50]", warning);
        var summaryText = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("maxHops was clamped from 100 to 50", summaryText);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_MaxHopsWithinCapDoesNotEmitWarning()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxHops":50}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(50, structured["max_hops"]!.GetValue<int>());
        Assert.Equal(50, structured["max_hops_requested"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth_requested"]!.GetValue<int>());
        Assert.Null(structured["warnings"]);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_DeprecatedMaxDepthSurfacesWarning()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxDepth":2}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(2, structured["max_hops"]!.GetValue<int>());
        Assert.Equal(2, structured["max_depth"]!.GetValue<int>());
        var warning = Assert.Single(structured["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains("maxDepth is deprecated", warning);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ReturnsSameCallerPerReferenceKind()
    {
        InsertIndexedFile("src/EventHub.cs", "csharp",
            """
            public class EventHub
            {
                public event System.Action? Changed;
                public void Changed() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(EventHub hub)
                {
                    hub.Changed += OnChanged;
                    hub.Changed();
                }

                private void OnChanged() { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Changed","limit":10}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var callers = structured["callers"]!.AsArray()
            .Where(caller => caller!["path"]!.GetValue<string>() == "src/App.cs"
                && caller!["callerName"]!.GetValue<string>() == "Boot")
            .ToList();

        Assert.Equal(2, callers.Count);
        Assert.Equal(new[] { "call", "subscribe" }, callers.Select(caller => caller!["referenceKind"]!.GetValue<string>()).Order().ToArray());
        Assert.All(callers, caller => Assert.Equal(1, caller!["referenceCount"]!.GetValue<int>()));
    }

    [Fact]
    public void ToolsList_ImpactAnalysisMaxHopsSchemaDocumentsCap()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var impactTool = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        var maxHopsSchema = impactTool["inputSchema"]!["properties"]!["maxHops"]!;

        Assert.Equal(50, maxHopsSchema["maximum"]!.GetValue<int>());
        Assert.Equal(0, maxHopsSchema["minimum"]!.GetValue<int>());
        var description = maxHopsSchema["description"]!.GetValue<string>();
        Assert.Contains("Server-side cap", description);
        Assert.Contains("warnings", description);
        Assert.Contains("max_hops_requested", description);
        var maxDepthSchema = impactTool["inputSchema"]!["properties"]!["maxDepth"]!;
        Assert.Contains("Deprecated alias", maxDepthSchema["description"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ExcludeTestsIgnoresOutOfScopeDuplicateDefinitions()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("tests/FooServiceTests.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService","excludeTests":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal(1, structured["definition_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/FooService.cs", structured["definitions"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_IgnoresUnsupportedLanguageDuplicates()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/tools.txt", "text",
            """
            FooService() {
              :
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal(1, structured["definition_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/FooService.cs", structured["definitions"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ExactDefinitionResolutionSkipsUnsupportedMatchesBeforeLimit()
    {
        for (int i = 0; i < 60; i++)
        {
            InsertIndexedFile($"scripts/Foo{i:D2}.sh", "text",
                """
                Foo() {
                  :
                }
                """);
        }

        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Foo service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Foo"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Equal("src/Foo.cs", structured["definitions"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_SubstringTypeEvidenceDoesNotProduceHints()
    {
        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Handle(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Foo"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_HeuristicHintsSetTruncatedWhenLimitReached()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App1.cs", "csharp",
            """
            public class App1
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);
        InsertIndexedFile("src/App2.cs", "csharp",
            """
            public class App2
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.Single(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_HeuristicHintsKeepActualReferenceCount()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(4, structured["file_impacts"]![0]!["referenceCount"]!.GetValue<int>());
        Assert.Equal("ExecuteFolderDiffAsync,FolderDiffService", structured["file_impacts"]![0]!["symbols"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_UnresolvedExternalCallWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/ExternalConsumer.cs", "csharp",
            """
            public class ExternalConsumer
            {
                public void Boot()
                {
                    ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.False(structured["heuristic"]!.GetValue<bool>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_IncludesCombinedExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Contains("idx_symbol_refs_container_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_NonExactOnReadOnlyLegacyDb_OmitsExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run"}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_Symbols_ExactOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_ExactWithoutQuery_OnReadOnlyLegacyDb_OmitsExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n");
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"exact":true,"limit":1}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_Definition_ExactOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ExactSignals_RespectMcpQueryScopeForStaleCSharpCanonicalNames()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n");
        var writer = new DbWriter(_db.Connection);
        writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, "0");

        var pythonRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","lang":"python","exact":true}}}""")!;
        var pythonResponse = _server.HandleMessage(pythonRequest)!;
        var pythonStructured = pythonResponse["result"]!["structuredContent"]!;

        Assert.True(pythonStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(pythonStructured["degraded_reason"]);
        Assert.True(pythonStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(pythonStructured["degraded_reason"]);

        var csharpRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","lang":"csharp","exact":true}}}""")!;
        var csharpResponse = _server.HandleMessage(csharpRequest)!;
        var csharpStructured = csharpResponse["result"]!["structuredContent"]!;

        Assert.False(csharpStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("csharp_symbol_name_ready=false", csharpStructured["degraded_reason"]!.GetValue<string>());
        Assert.False(csharpStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("csharp_symbol_name_ready=false", csharpStructured["degraded_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_WithMissingSymbolFallbackIndex_IncludesBundleSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
        ForceLegacyExactFallbackMode();
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["definitions"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_UnsupportedGraphLanguage_SkipsGraphDegradedSignal()
    {
        InsertIndexedFile("docs/guide.md", "markdown", "# Heading\n\nSee also `Run`.\n");
        ForceLegacyExactFallbackMode();
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Heading","lang":"markdown","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.False(structured["graph_supported"]!.GetValue<bool>());
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_PathOnlyUnsupportedSlice_SkipsGraphDegradedSignal()
    {
        InsertIndexedFile("docs/guide.md", "markdown", "# Heading\n\nSee also `Run`.\n");
        ForceLegacyExactFallbackMode();
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","path":"docs/","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactZeroHintWhenWholeBundleIsEmpty()
    {
        InsertIndexedFile("src/handler.cs", "csharp",
            """
            public class Handler
            {
                public void HandleRequest() { }
                public void HandleRequestAsync() { HandleRequest(); }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"HandleRe","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();

        Assert.NotNull(structured["exact_zero_hint"]);
        Assert.Equal(2, structured["exact_zero_hint"]!["relaxed_count"]!.GetValue<int>());
        Assert.Contains("HandleRequest", structured["exact_zero_hint"]!["sample_names"]!.ToJsonString());
        Assert.Contains("Substring would return 2", text);
    }

    [Fact]
    public void ToolsCall_Search_MissingQuery_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("query", text);
    }

    [Fact]
    public void ToolsCall_Search_ExactSubstringAliasMatchesBackwardCompatibleExact()
    {
        InsertIndexedFile("src/search.cs", "csharp", "void Run() { }\nvoid RunAsync() { Run(); }\n");

        var exactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run();","exact":true}}}""")!;
        var aliasRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run();","exactSubstring":true}}}""")!;

        var exactResponse = _server.HandleMessage(exactRequest)!;
        var aliasResponse = _server.HandleMessage(aliasRequest)!;

        Assert.Equal(
            exactResponse["result"]!["structuredContent"]!.ToJsonString(),
            aliasResponse["result"]!["structuredContent"]!.ToJsonString());
    }

    [Fact]
    public void ToolsCall_Search_RejectsExactNameAlias()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","exactName":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Unknown argument 'exactName'", text);
    }

    [Fact]
    public void ToolsCall_Search_IncludesTruncatedAndTotalEnvelope()
    {
        InsertIndexedFile("src/search-a.cs", "csharp", "public class SearchA { public void Target() { } }");
        InsertIndexedFile("src/search-b.cs", "csharp", "public class SearchB { public void Target() { } }");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Target","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.Null(structured["total"]);
        Assert.Single(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_References_IncludesTruncatedAndTotalEnvelope()
    {
        InsertIndexedFile(
            "src/reference-envelope.cs",
            "csharp",
            """
            public class CallerOne { public void Run(App app) { app.Run(); } }
            public class CallerTwo { public void Run(App app) { app.Run(); } }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"csharp","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.True(structured["total"]!.GetValue<int>() >= 2);
        Assert.Single(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_References_CountOnly_OmitsRowsAndReturnsHistogram()
    {
        InsertIndexedFile(
            "src/count-only.cs",
            "csharp",
            """
            public class CountOnlyCaller { public void Run(App app) { app.Run(); app.Run(); } }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"csharp","countOnly":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["count_only"]!.GetValue<bool>());
        Assert.True(structured["count"]!.GetValue<int>() >= 2);
        Assert.Empty(structured["results"]!.AsArray());
        Assert.NotEmpty(structured["top_files"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_CountOnly_OmitsCallerRows()
    {
        InsertIndexedFile(
            "src/impact-count-only.cs",
            "csharp",
            """
            public class ImpactCountOnlyCaller { public void Hit(App app) { app.Run(); } }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","lang":"csharp","countOnly":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["count_only"]!.GetValue<bool>());
        Assert.Empty(structured["results"]!.AsArray());
        Assert.NotNull(structured["top_files"]);
    }

    [Fact]
    public void ToolsCall_ResponseOverByteLimit_ReturnsStructuredError()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_RESPONSE_MAX_BYTES", "256");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32603, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("response_too_large", response["error"]!["data"]!["reason"]!.GetValue<string>());
        Assert.Equal(256, response["error"]!["data"]!["limit_bytes"]!.GetValue<int>());
        Assert.True(response["error"]!["data"]!["actual_bytes"]!.GetValue<int>() > 256);
        Assert.False(response["error"]!["data"]!["actual_bytes_exact"]!.GetValue<bool>());
    }

    [Fact]
    public void ResponseLimitSerializer_StopsBeforeFullStringMaterialization_Issue2860()
    {
        var payload = new JsonObject
        {
            ["value"] = new string('x', 10_000),
        };

        var withinLimit = _server.TrySerializeJsonNodeWithinByteLimitForTests(payload, 256, out var serialized, out var bytesWritten);

        Assert.False(withinLimit);
        Assert.Null(serialized);
        Assert.True(bytesWritten > 256);
        Assert.True(bytesWritten < 10_000);
    }

    [Fact]
    public void ResponseLimitSerializer_ReturnsCapturedJsonWhenWithinLimit_Issue2860()
    {
        var payload = new JsonObject
        {
            ["value"] = "ok",
        };

        var withinLimit = _server.TrySerializeJsonNodeWithinByteLimitForTests(payload, 256, out var serialized, out var bytesWritten);

        Assert.True(withinLimit);
        Assert.NotNull(serialized);
        Assert.Equal(Encoding.UTF8.GetByteCount(serialized), bytesWritten);
        using var parsed = JsonDocument.Parse(serialized);
        Assert.Equal("ok", parsed.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void ToolsCall_Status_ReportsResponseByteLimitCaps()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_MCP_RESPONSE_MAX_BYTES",
            "CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        env.Set("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var mcp = response["result"]!["structuredContent"]!["mcp"]!;
        var limits = mcp["limits"]!;
        Assert.Equal(McpServer.MaxConfiguredResponseBytes, limits["max_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxConfiguredResponseBytes, limits["max_configured_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, limits["batch_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, limits["max_batch_response_bytes"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_ReferencesOffsetSchemaAdvertisesCap()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var references = tools.First(tool => tool!["name"]!.GetValue<string>() == "references")!;
        var offset = references["inputSchema"]!["properties"]!["offset"]!;
        Assert.Equal(0, offset["minimum"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, offset["maximum"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_References_ClampsTooLargeOffset()
    {
        InsertIndexedFile(
            "src/offset-clamp.cs",
            "csharp",
            """
            public class OffsetClampCaller { public void Hit(App app) { app.Run(); } }
            """);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "references",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["lang"] = "csharp",
                    ["offset"] = McpServer.MaxMcpPaginationOffset + 1,
                    ["limit"] = 1,
                },
            },
        };
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(McpServer.MaxMcpPaginationOffset, structured["offset"]!.GetValue<int>());
        Assert.True(structured["total"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void ToolsCall_Status_ReportsPaginationOffsetCap()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var limits = response["result"]!["structuredContent"]!["mcp"]!["limits"]!;
        Assert.Equal(McpServer.MaxMcpPaginationOffset, limits["max_pagination_offset"]!.GetValue<int>());
    }

    [Fact]
    public void Constructor_InvalidKeepAliveEnvironment_DoesNotThrow()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S");
        env.Set("CDIDX_MCP_KEEP_ALIVE_INTERVAL_S", "Infinity");

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"ping"}""")!)!;

        Assert.Equal("ok", response["result"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Status_ReportsKeepAliveIntervalBounds()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var limits = response["result"]!["structuredContent"]!["mcp"]!["limits"]!;
        Assert.Equal(McpServer.MinKeepAliveIntervalSeconds, limits["keep_alive_min_interval_s"]!.GetValue<double>());
        Assert.Equal(McpServer.MaxKeepAliveIntervalSeconds, limits["keep_alive_max_interval_s"]!.GetValue<double>());
    }

    [Fact]
    public void ToolsCall_Status_ReportsEffectiveRateLimitCaps()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_MCP_RATE_LIMIT_RPS",
            "CDIDX_MCP_RATE_LIMIT_BURST");
        env.Set("CDIDX_MCP_RATE_LIMIT_RPS", "1000000");
        env.Set("CDIDX_MCP_RATE_LIMIT_BURST", "1000000");

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var mcp = response["result"]!["structuredContent"]!["mcp"]!;
        var limits = mcp["limits"]!;
        Assert.Equal(RateLimiterOptions.MaxRefillTokensPerSecond, limits["rate_limit_max_rps"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.MaxBurstCapacity, limits["rate_limit_max_burst"]!.GetValue<double>());

        var rateLimit = mcp["rate_limit"]!;
        Assert.True(rateLimit["enabled"]!.GetValue<bool>());
        Assert.Equal(RateLimiterOptions.MaxRefillTokensPerSecond, rateLimit["rps"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.MaxBurstCapacity, rateLimit["burst"]!.GetValue<double>());
    }

    [Fact]
    public async Task ProcessFrameAsync_BatchResponseOverByteLimit_ReturnsStructuredError()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_RESPONSE_MAX_BYTES", "128");

        var frame = "["
            + string.Join(",", Enumerable.Range(1, 10).Select(id => $$"""{"jsonrpc":"2.0","id":{{id}},"method":"ping"}"""))
            + "]";
        var responseText = await _server.ProcessFrameAsync(frame);
        using var response = JsonDocument.Parse(responseText!);
        var root = response.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("response_too_large", data.GetProperty("reason").GetString());
        Assert.Equal(128, data.GetProperty("limit_bytes").GetInt32());
        Assert.True(data.GetProperty("actual_bytes").GetInt32() > 128);
    }

    [Fact]
    public void ToolsCall_Search_RejectsFalseExactNameAlias()
    {
        InsertIndexedFile("src/search_false_alias.cs", "csharp", "void Run() { }\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","exactName":false}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Unknown argument 'exactName'", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_ReturnsResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 symbol", text);
        Assert.Equal("App", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("class", response["result"]!["structuredContent"]!["results"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("public class App { public void Run() { } }", response["result"]!["structuredContent"]!["results"]![0]!["signature"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_ExactNameAliasMatchesBackwardCompatibleExact()
    {
        InsertIndexedFile("src/exact.cs", "csharp", "public class ExactApp { public void Run() { } public void RunAsync() { } }");

        var exactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exact":true}}}""")!;
        var aliasRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactName":true}}}""")!;

        var exactResponse = _server.HandleMessage(exactRequest)!;
        var aliasResponse = _server.HandleMessage(aliasRequest)!;

        Assert.Equal(
            exactResponse["result"]!["structuredContent"]!.ToJsonString(),
            aliasResponse["result"]!["structuredContent"]!.ToJsonString());
    }

    [Fact]
    public void ToolsCall_Symbols_RejectsExactSubstringAlias()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactSubstring":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Unknown argument 'exactSubstring'", text);
    }

    [Fact]
    public void ToolsCall_Symbols_RejectsFalseExactSubstringAlias()
    {
        InsertIndexedFile("src/symbol_false_alias.cs", "csharp", "public class ExactApp { public void Run() { } }\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactSubstring":false}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Unknown argument 'exactSubstring'", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("search", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("search", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("definition", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("definition", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("references", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("references", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("callers", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("callers", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("callees", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("callees", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("symbols", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("symbols", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("analyze_symbol", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("analyze_symbol", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    public void ToolsCall_ExactAliases_RejectsCombinedFlags(string toolName, string argumentsJson)
    {
        var request = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName + "\",\"arguments\":" + argumentsJson + "}}")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(
            text.Contains("Pass only one of 'exact', 'exactSubstring', 'exactName'.", StringComparison.Ordinal)
            || text.Contains("Unknown argument", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"names":""}""", "must be an array")]
    [InlineData("""{"names":[]}""", "no usable entries")]
    [InlineData("""{"names":[""]}""", "no usable entries")]
    [InlineData("""{"names":["   "]}""", "no usable entries")]
    public void ToolsCall_Symbols_RejectsMalformedOrEmptyNames(string argsJson, string expectedMessageFragment)
    {
        // Malformed or empty `names` must fail closed — falling through to an unfiltered full-symbol
        // dump would mislead downstream automation about candidate resolution.
        // 不正・空の `names` は必ずエラーで弾くこと。全件検索に化けると下流の判断を狂わせる。
        var request = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"symbols\",\"arguments\":" + argsJson + "}}")!;
        var response = _server.HandleMessage(request)!;
        Assert.True(response["result"]!["isError"]!.GetValue<bool>(), $"expected isError for arguments {argsJson}");
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(expectedMessageFragment, text);
    }

    [Fact]
    public void ToolsCall_Symbols_FilterByKind()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"kind":"function"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        Assert.Equal("Run", results[0]!["name"]!.GetValue<string>());
        Assert.Equal("function", results[0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Definition_ReturnsDefinitionContent()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"Run","includeBody":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 definition", text);
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("public void Run()", response["result"]!["structuredContent"]!["results"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Definition_DoesNotReportSqlGraphContractDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_definition_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var definitionRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"fn_Target","lang":"sql","exact":true}}}""")!;
            var definitionResponse = server.HandleMessage(definitionRequest)!;
            var definitionStructured = definitionResponse["result"]!["structuredContent"]!;

            Assert.Equal(1, definitionStructured["count"]!.GetValue<int>());
            Assert.Null(definitionStructured["sql_graph_contract_ready"]);
            Assert.Null(definitionStructured["sql_graph_contract_ready"]);
            Assert.Null(definitionStructured["degraded"]);
            Assert.Null(definitionStructured["sql_graph_contract_degraded_reason"]);
            Assert.Null(definitionStructured["sql_graph_contract_degraded_reason"]);

            var callersRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callers","arguments":{"query":"dbo.fn_Target","lang":"sql","exact":true}}}""")!;
            var callersResponse = server.HandleMessage(callersRequest)!;
            var callersStructured = callersResponse["result"]!["structuredContent"]!;

            Assert.False(callersStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(callersStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.NotNull(callersStructured["sql_graph_contract_degraded_reason"]);
            Assert.NotNull(callersStructured["sql_graph_contract_degraded_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_StaleSqlGraphContractIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_impact_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"fn_Target","lang":"sql"}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_MixedRepoStaleSqlGraphContractDoesNotDegradePureCSharpBundle()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_analyze_symbol_mixed_sql_graph_contract");
        try
        {
            var dbPath = CreateMixedSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"N","exact":true}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Deps_ZeroResultSqlScopeStillIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_deps_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Hotspots_ZeroResultSqlScopeStillIncludesDegradedState()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_hotspots_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ZeroResultStaysCleanWhenSqlSymbolsCannotMatchKind()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_unused_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"kind":"interface"}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["degraded"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ZeroResultStaysCleanWhenSqlSymbolsCannotMatchKind()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_hotspots_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"kind":"class"}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("definition", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("symbols", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("references", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("callers", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("callees", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("files", """{"query":"nonexistent_xyz_123","lang":"nonexistent"}""")]
    public void ToolsCall_ZeroResults_IncludesFreshnessHint(string toolName, string argsJson)
    {
        var request = JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argsJson}}}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0, $"{toolName} should include indexed_file_count");
        Assert.True(structured["freshness_available"]!.GetValue<bool>());
        Assert.NotNull(structured["indexed_at"]);
    }

    [Fact]
    public void ToolsCall_Files_NoResults_OnLegacyReadOnlyDb_EmitsFreshnessDegradedSignal()
    {
        var dbPath = CreateLegacyDbWithoutIndexedAt();
        try
        {
            using var readOnlyServer = new McpServer(new Uri(dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files","arguments":{"query":"nonexistent_xyz_123"}}}""")!;
            var response = readOnlyServer.HandleMessage(request)!;
            using var document = JsonDocument.Parse(response.ToJsonString());
            var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");

            Assert.Equal(0, structured.GetProperty("count").GetInt32());
            Assert.Equal(0, structured.GetProperty("results").GetArrayLength());
            Assert.Equal(1, structured.GetProperty("indexed_file_count").GetInt64());
            Assert.False(structured.GetProperty("freshness_available").GetBoolean());
            Assert.Contains("files.indexed_at column missing", structured.GetProperty("freshness_degraded_reason").GetString());
            Assert.Equal(JsonValueKind.Null, structured.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }

    [Theory]
    [InlineData("search", """{"query":"nonexistent_xyz_123"}""", "results")]
    [InlineData("files", """{"query":"nonexistent_xyz_123"}""", "results")]
    public void ToolsCall_ZeroResults_EmptyIndexIncludesNullFreshnessTimestamp(string toolName, string argsJson, string resultsKey)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_empty_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

                var request = JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argsJson}}}}}""")!;
                var response = server.HandleMessage(request)!;

                var structured = response["result"]!["structuredContent"]!;
                Assert.Equal(0, structured["count"]!.GetValue<int>());
                Assert.Equal(0, structured["indexed_file_count"]!.GetValue<long>());
                Assert.True(structured.AsObject().ContainsKey("indexed_at"));
                Assert.Null(structured["indexed_at"]);
                Assert.Empty(structured[resultsKey]!.AsArray());
            }
        }
        finally
        {
            DeleteFileRobust(dbPath);
        }
    }

    [Theory]
    [InlineData("definition", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("symbols", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("references", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("callers", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("callees", """{"query":"Runn","exact":true}""", "Runner")]
    public void ToolsCall_ExactZeroResults_IncludeExactZeroHint(string toolName, string argsJson, string expectedSampleName)
    {
        InsertIndexedFile(
            "src/extra.cs",
            "csharp",
            """
            public class Extra
            {
                public void Runner()
                {
                    Run();
                }

                public void Run() { }
            }
            """);

        var request = JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argsJson}}}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.NotNull(structured["exact_zero_hint"]);
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0);
        Assert.True(structured["exact_zero_hint"]!["relaxed_count"]!.GetValue<int>() > 0);
        Assert.Contains(expectedSampleName, structured["exact_zero_hint"]!["sample_names"]!.AsArray().Select(node => node!?.GetValue<string>()));
        Assert.Equal("drop --exact or use the exact indexed name", structured["exact_zero_hint"]!["suggestion"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Definition_ExactZeroHint_RespectsRequestedLimitForRelaxedCount()
    {
        InsertIndexedFile(
            "src/extra_limit.cs",
            "csharp",
            """
            public class ExtraLimit
            {
                public void HandleRequest1() { }
                public void HandleRequest2() { }
                public void HandleRequest3() { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"Handle","exact":true,"limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["exact_zero_hint"]!["relaxed_count"]!.GetValue<int>());
        Assert.Single(structured["exact_zero_hint"]!["sample_names"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_Symbols_MultiNameExactZeroHint_OmitsRelaxedCountButReturnsSamples()
    {
        InsertIndexedFile(
            "src/extra_multi.cs",
            "csharp",
            """
            public class ExtraMulti
            {
                public void AlphaWorker() { }
                public void BetaWorker() { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"names":["Alpha","Beta"],"exact":true,"limit":999}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.NotNull(structured["exact_zero_hint"]);
        Assert.Null(structured["exact_zero_hint"]!["relaxed_count"]);
        Assert.Contains("AlphaWorker", structured["exact_zero_hint"]!["sample_names"]!.AsArray().Select(node => node!?.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Files_ReturnsResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 file", text);
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["results"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("csharp", response["result"]!["structuredContent"]!["results"]![0]!["lang"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["structuredContent"]!["results"]![0]!["modified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["results"]![0]!["indexedAt"]);
    }

    [Fact]
    public void ToolsCall_Excerpt_ReturnsExcerpt()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"src/app.cs","startLine":1,"endLine":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Excerpt returned", text);
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["path"]!.GetValue<string>());
        Assert.Contains("public class App", response["result"]!["structuredContent"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_ClampsLongSingleLineContent()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        var focusColumn = longLine.IndexOf("TARGET", StringComparison.Ordinal) + 1;
        InsertIndexedFile("dist/data.txt", "text", longLine);

        var request = JsonNode.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"excerpt\",\"arguments\":{{\"path\":\"dist/data.txt\",\"startLine\":1,\"endLine\":1,\"maxLineWidth\":96,\"focusColumn\":{focusColumn},\"focusLength\":6}}}}}}")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["contentTruncated"]!.GetValue<bool>());
        Assert.DoesNotContain(longLine, structured["content"]!.GetValue<string>());
        Assert.Contains("TARGET", structured["content"]!.GetValue<string>());
        Assert.True(structured["content"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Excerpt_ClampsLongSingleLineContentWithoutFocus()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/data-no-focus.txt", "text", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-no-focus.txt","startLine":1,"endLine":1,"maxLineWidth":96}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["contentTruncated"]!.GetValue<bool>());
        Assert.DoesNotContain(longLine, structured["content"]!.GetValue<string>());
        Assert.True(structured["content"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusLineWithoutFocusColumnReturnsError()
    {
        InsertIndexedFile("dist/data-focus-error.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-error.txt","startLine":1,"endLine":1,"maxLineWidth":96,"focusLine":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusLine and focusLength require focusColumn", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusColumnZeroReturnsError()
    {
        InsertIndexedFile("dist/data-focus-zero.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-zero.txt","startLine":1,"endLine":1,"maxLineWidth":96,"focusColumn":0}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusColumn must be greater than or equal to 1", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusLengthZeroReturnsError()
    {
        InsertIndexedFile("dist/data-focus-length-zero.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-length-zero.txt","startLine":1,"endLine":1,"focusColumn":1,"focusLength":0}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusLength must be greater than or equal to 1", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/data-max-width-zero.txt", "text", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-max-width-zero.txt","startLine":1,"endLine":1,"maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.False(structured["contentTruncated"]!.GetValue<bool>());
        Assert.Equal(longLine, structured["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_NegativeBeforeReturnsError()
    {
        InsertIndexedFile("dist/data-before-negative.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-before-negative.txt","startLine":1,"before":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("before must be in [0, 1000]", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_NegativeAfterReturnsError()
    {
        InsertIndexedFile("dist/data-after-negative.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-after-negative.txt","startLine":1,"after":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("after must be in [0, 1000]", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_BeforeAboveCapClampsContext()
    {
        InsertIndexedFile("dist/data-before-overflow.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-before-overflow.txt","startLine":1,"before":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1000, structured["before"]!.GetValue<int>());
        Assert.True(structured["contextTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Excerpt_AfterAboveCapClampsContext()
    {
        InsertIndexedFile("dist/data-after-overflow.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-after-overflow.txt","startLine":1,"after":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1000, structured["after"]!.GetValue<int>());
        Assert.True(structured["contextTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Excerpt_HugeEndLineDoesNotOverflow()
    {
        InsertIndexedFile("dist/data-endline-overflow.txt", "text", "line one\nline two\nline three");

        // endLine close to int.MaxValue + bounded `after` would overflow int addition and
        // wrap to a negative number before Math.Min clamped, masking the real file size.
        // Validate the handler returns a sane excerpt instead (#1528).
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-endline-overflow.txt","startLine":1,"endLine":2147483647,"after":1000}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("dist/data-endline-overflow.txt", structured["path"]!.GetValue<string>());
        Assert.Equal(1, structured["startLine"]!.GetValue<int>());
        Assert.Equal(3, structured["endLine"]!.GetValue<int>());
        Assert.Contains("line three", structured["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusLineOutsideReturnedRangeReturnsError()
    {
        InsertIndexedFile("dist/data-focus-range.txt", "text", "line one\nline two\nline three");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-range.txt","startLine":2,"endLine":2,"focusLine":999,"focusColumn":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusLine (999) must be within the returned excerpt range (2-2)", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusColumnOutsideFocusedLineReturnsError()
    {
        InsertIndexedFile("dist/data-focus-column-range.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-column-range.txt","startLine":1,"endLine":1,"focusColumn":9999,"maxLineWidth":40}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusColumn (9999) must be within the focused line length (646)", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_ReturnsLiteralMatchesWithContext()
    {
        InsertIndexedFile("src/Auth.cs", "csharp",
            """
            class Auth
            {
                void Guard() {}
                void Next() {}
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"guard","path":"src/Auth.cs","before":1,"after":1}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["fileCount"]!.GetValue<int>());
        Assert.Equal("src/Auth.cs", result["path"]!.GetValue<string>());
        Assert.Equal(3, result["line"]!.GetValue<int>());
        Assert.Equal(10, result["column"]!.GetValue<int>());
        Assert.Equal(2, result["startLine"]!.GetValue<int>());
        Assert.Equal(4, result["endLine"]!.GetValue<int>());
        Assert.Contains("void Guard()", result["snippet"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_ClampsLongSingleLineSnippet()
    {
        InsertIndexedFile("dist/search.txt", "text", new string('a', 320) + "target" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search.txt","maxLineWidth":96,"exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.True(result["snippetTruncated"]!.GetValue<bool>());
        Assert.Contains("target", result["snippet"]!.GetValue<string>());
        Assert.True(result["snippet"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_FindInFile_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = new string('a', 320) + "target" + new string('b', 320);
        InsertIndexedFile("dist/search-max-width-zero.txt", "text", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-max-width-zero.txt","maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.False(result["snippetTruncated"]!.GetValue<bool>());
        Assert.Equal(longLine, result["snippet"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_NegativeBeforeReturnsError()
    {
        InsertIndexedFile("dist/search-before-negative.txt", "text", "target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-before-negative.txt","before":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("before must be greater than or equal to 0", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_NegativeAfterReturnsError()
    {
        InsertIndexedFile("dist/search-after-negative.txt", "text", "target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-after-negative.txt","after":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("after must be greater than or equal to 0", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_BeforeAfterAboveCapClampContext()
    {
        InsertIndexedFile("dist/search-context-overflow.txt", "text", "target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-context-overflow.txt","before":2147483647,"after":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1000, structured["before"]!.GetValue<int>());
        Assert.Equal(1000, structured["after"]!.GetValue<int>());
        Assert.True(structured["contextTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_FindInFile_SnippetLinesControlsMatchContext()
    {
        InsertIndexedFile("dist/search-snippet-lines.txt", "text", "line one\nline two\ntarget\nline four\nline five");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-snippet-lines.txt","snippetLines":5}}}""")!;
        var response = _server.HandleMessage(request)!;
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Equal(2, structured["before"]!.GetValue<int>());
        Assert.Equal(2, structured["after"]!.GetValue<int>());
        Assert.Equal(5, structured["snippetLines"]!.GetValue<int>());
        Assert.Equal(1, result["startLine"]!.GetValue<int>());
        Assert.Equal(5, result["endLine"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_FindInFile_FocusLineAndColumnRestrictMatch()
    {
        InsertIndexedFile("dist/search-focus.txt", "text", "target here\nno match\nother target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-focus.txt","focusLine":3,"focusColumn":8}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(3, result["line"]!.GetValue<int>());
        Assert.Equal(7, result["column"]!.GetValue<int>());
        Assert.Equal(3, structured["focusLine"]!.GetValue<int>());
        Assert.Equal(8, structured["focusColumn"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_FindInFile_RegexMatchesAnchors()
    {
        InsertIndexedFile("dist/search-regex.txt", "text", "alpha\ntarget()\nnot target()");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"^target","path":"dist/search-regex.txt","regex":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.True(structured["regex"]!.GetValue<bool>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(2, result["line"]!.GetValue<int>());
        Assert.Equal(1, result["column"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ClampsBundledReferenceContext()
    {
        InsertIndexedFile("src/target.js", "javascript",
            """
            function target() {
              return true;
            }
            """);
        var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "dist/bundle.js",
            Lang = "javascript",
            Size = longLine.Length,
            Lines = 1,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks([
            new ChunkRecord { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = longLine }
        ]);
        writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "target",
                ReferenceKind = "call",
                Line = 1,
                Column = longLine.IndexOf("target", StringComparison.Ordinal) + 1,
                Context = longLine,
            }
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"target","lang":"javascript","maxLineWidth":96}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var firstReference = structured["references"]![0]!;

        Assert.True(firstReference["contextTruncated"]!.GetValue<bool>());
        Assert.Contains("target()", firstReference["context"]!.GetValue<string>());
        Assert.True(firstReference["context"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ZeroMaxLineWidthDisablesTruncation()
    {
        var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
        InsertIndexedFile("src/analyze-target.js", "javascript",
            "function target() { return true; }\n" + longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"target","lang":"javascript","maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var firstReference = structured["references"]![0]!;

        Assert.False(firstReference["contextTruncated"]!.GetValue<bool>());
        Assert.Contains("target()", firstReference["context"]!.GetValue<string>());
        Assert.DoesNotContain("...(+", firstReference["context"]!.GetValue<string>());
        Assert.True(firstReference["context"]!.GetValue<string>().Length > 512);
    }

    [Fact]
    public void ToolsCall_FindInFile_NoResultsIncludesFreshnessHints()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"missing","path":"src/app.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["fileCount"]!.GetValue<int>());
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0);
        Assert.True(structured["freshness_available"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_FindInFile_CountsEverySameLineOccurrence()
    {
        InsertIndexedFile("src/Sample.cs", "csharp", "alpha alpha alpha\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"alpha","path":"src/Sample.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();

        Assert.Equal(3, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["fileCount"]!.GetValue<int>());
        Assert.Equal([1, 7, 13], results.Select(node => node!["column"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void ToolsCall_FindInFile_CountsOverlappingOccurrences()
    {
        InsertIndexedFile("src/Sample.cs", "csharp", "// banana\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"ana","path":"src/Sample.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();

        Assert.Equal(2, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["fileCount"]!.GetValue<int>());
        Assert.Equal([5, 7], results.Select(node => node!["column"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void ToolsCall_Status_ReturnsCounts()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Database stats returned", text);
        Assert.Equal(1, response["result"]!["structuredContent"]!["files"]!.GetValue<long>());
        Assert.Equal(1, response["result"]!["structuredContent"]!["chunks"]!.GetValue<long>());
        Assert.Equal(2, response["result"]!["structuredContent"]!["symbols"]!.GetValue<long>());
        Assert.Equal(0, response["result"]!["structuredContent"]!["references"]!.GetValue<long>());
        Assert.NotNull(response["result"]!["structuredContent"]!["indexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["latestModified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["project_root"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["hotspot_family_ready"]);
        Assert.Null(response["result"]!["structuredContent"]!["hotspotFamilyReady"]);
        Assert.False(response["result"]!["structuredContent"]!["foldReady"]!.GetValue<bool>());
        Assert.Equal(DegradationReasonCodes.MissingFoldBackfill, response["result"]!["structuredContent"]!["fold_ready_reason"]!.GetValue<string>());
        Assert.Contains("--exact falls back", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("cdidx backfill-fold", response["result"]!["structuredContent"]!["recommended_action"]!.GetValue<string>());
        Assert.Equal("cdidx index . --rebuild", response["result"]!["structuredContent"]!["alternative_action"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Status_ReportsDegradedHotspotFamilyTrust()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_status_hotspots_family_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.Contains("hotspot_family_support_not_indexed=csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
            Assert.Null(structured["hotspotFamilyDegradedReason"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Status_ReportsDegradedSqlGraphContractTrust()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_status_sql_graph_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/target.sql",
                "sql",
                """
                CREATE FUNCTION dbo.fn_Target()
                RETURNS INT
                AS
                BEGIN
                    RETURN 1;
                END;
                GO
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/caller.sql",
                "sql",
                """
                CREATE PROCEDURE dbo.usp_Caller
                AS
                BEGIN
                    SELECT dbo.fn_Target();
                END;
                GO
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkSqlGraphContractReady();

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbol_references
                    SET symbol_name = 'fn_Target',
                        symbol_name_folded = 'fn_target',
                        column_number = 1
                    WHERE symbol_name = 'dbo.fn_Target';
                    DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Status_ReadOnlyUriForExplicitDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_uri");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_status_{Guid.NewGuid():N}.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            using var readOnlyServer = new McpServer(new Uri(dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = readOnlyServer.HandleMessage(request)!;

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, response["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
            Assert.True(response["result"]!["structuredContent"]!["gitIsDirty"]!.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Status_CustomDbUnderCdidx_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_custom_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, response["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
            Assert.True(response["result"]!["structuredContent"]!["gitIsDirty"]!.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ToolsCall_Status_ExplicitProjectLocalDb_LeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_project_local_explicit");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Null(response["result"]!["structuredContent"]!["project_root"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitHead"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitIsDirty"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Status_ExplicitProjectLocalReadOnlyUri_LeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_project_local_uri");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using var server = new McpServer(dbUri, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Null(response["result"]!["structuredContent"]!["project_root"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitHead"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitIsDirty"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void ToolsCall_Status_ExplicitExternalCodeIndexDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_codeindex_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_codeindex_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, response["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
            Assert.True(response["result"]!["structuredContent"]!["gitIsDirty"]!.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ToolsCall_Ping_ReturnsVersionAndTimestamp()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2032, 4, 5, 6, 7, 8, TimeSpan.Zero));
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, null, null, null, null, McpServer.DefaultMaxConcurrency, clock);
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
        var response = server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("cdidx v", text);
        Assert.Contains("is ready", text);
        Assert.NotNull(response["result"]!["structuredContent"]!["version"]);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.ToString("O"), response["result"]!["structuredContent"]!["timestamp"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["structuredContent"]!["db_exists"]);
    }

    [Fact]
    public void ToolsCall_BatchQuery_ExecutesMultipleQueries()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"status"},{"tool":"files","arguments":{}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 2 of 2 queries", text);
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0]!["request_index"]!.GetValue<int>());
        Assert.True(results[0]!["ok"]!.GetValue<bool>());
        Assert.Equal("status", results[0]!["tool"]!.GetValue<string>());
        Assert.Equal(1, results[1]!["request_index"]!.GetValue<int>());
        Assert.True(results[1]!["ok"]!.GetValue<bool>());
        Assert.Equal("files", results[1]!["tool"]!.GetValue<string>());
        var metadata = response["result"]!["structuredContent"]!["metadata"]!;
        Assert.Equal(2, metadata["submitted"]!.GetValue<int>());
        Assert.Equal(2, metadata["executed"]!.GetValue<int>());
        Assert.Equal(0, metadata["errors"]!.GetValue<int>());
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["total_count"]!.GetValue<int>());
        Assert.Equal(2, structured["success_count"]!.GetValue<int>());
        Assert.Equal(0, structured["failure_count"]!.GetValue<int>());
        Assert.False(structured["partial_failure"]!.GetValue<bool>());
        Assert.Equal("none", structured["failure_scope"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_IncludesPing()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        Assert.Equal("ping", results[0]!["tool"]!.GetValue<string>());
        Assert.NotNull(results[0]!["result"]!["version"]);
    }

    [Fact]
    public void ToolsCall_BatchQuery_BlocksIndexInBatch()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"index","arguments":{"path":"."}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Contains("not allowed", results[0]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_BlocksBackfillFoldInBatch()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"backfill_fold","arguments":{}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Contains("not allowed", results[0]!["error"]!.GetValue<string>());
    }

    // Regression pins for issue #1537: batch_query must surface envelope metadata
    // (total_elapsed_ms / success_count / failure_count) and per-slot elapsed_ms so
    // callers can detect partial failure and slow inner queries without re-issuing.
    // #1537 回帰テスト: batch_query は envelope メタデータ（total_elapsed_ms /
    // success_count / failure_count）とスロット毎の elapsed_ms を返し、部分失敗や
    // 遅いクエリを再実行せず検出できるようにする。
    [Fact]
    public void ToolsCall_BatchQuery_ReturnsEnvelopeMetadata_Issue1537()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"},{"tool":"status"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var metadata = structured["metadata"]!;
        Assert.Equal(2, metadata["success_count"]!.GetValue<int>());
        Assert.Equal(0, metadata["failure_count"]!.GetValue<int>());
        Assert.True(metadata["total_elapsed_ms"]!.GetValue<long>() >= 0);

        var results = structured["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        foreach (var slot in results)
        {
            Assert.True(slot!["elapsed_ms"]!.GetValue<long>() >= 0);
            Assert.NotNull(slot["args_summary"]);
        }

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("all succeeded", text);
    }

    [Fact]
    public void ToolsCall_BatchQuery_CountsFailuresInEnvelope_Issue1537()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"},{"tool":"index","arguments":{"path":"."}},{"tool":"bogus_tool"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var metadata = structured["metadata"]!;
        Assert.Equal(1, metadata["success_count"]!.GetValue<int>());
        Assert.Equal(2, metadata["failure_count"]!.GetValue<int>());
        Assert.Equal(3, metadata["submitted"]!.GetValue<int>());
        Assert.Equal(3, metadata["executed"]!.GetValue<int>());
        Assert.Equal(2, metadata["errors"]!.GetValue<int>());
        Assert.Equal(3, structured["total_count"]!.GetValue<int>());
        Assert.Equal(1, structured["success_count"]!.GetValue<int>());
        Assert.Equal(2, structured["failure_count"]!.GetValue<int>());
        Assert.True(structured["partial_failure"]!.GetValue<bool>());
        Assert.Equal("isolated", structured["failure_scope"]!.GetValue<string>());

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.NotNull(results[0]!["elapsed_ms"]);
        Assert.True(results[0]!["ok"]!.GetValue<bool>());
        Assert.NotNull(results[1]!["elapsed_ms"]);
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.NotNull(results[2]!["elapsed_ms"]);
        Assert.False(results[2]!["ok"]!.GetValue<bool>());
        Assert.Equal(1, results[1]!["request_index"]!.GetValue<int>());
        Assert.Equal(2, results[2]!["request_index"]!.GetValue<int>());
        Assert.Contains("not allowed", results[1]!["error"]!.GetValue<string>());
        Assert.Contains("Unknown tool", results[2]!["error"]!.GetValue<string>());

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 3 of 3 queries", text);
        Assert.Contains("1 succeeded, 2 failed", text);
    }

    [Fact]
    public void ToolsCall_BatchQuery_UnknownToolName_TruncatesDisplay_Issue3118()
    {
        var toolName = new string('b', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = toolName,
                            ["arguments"] = new JsonObject
                            {
                                ["x"] = 1,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.DoesNotContain(toolName, response.ToJsonString(), StringComparison.Ordinal);
        var result = response["result"]!;
        var structured = result["structuredContent"]!;
        Assert.True(structured["partial_failure"]!.GetValue<bool>());
        var slot = Assert.Single(structured["results"]!.AsArray())!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal(display.Text, slot["tool"]!.GetValue<string>());
        Assert.Equal(toolName.Length, slot["tool_length"]!.GetValue<int>());
        Assert.True(slot["tool_truncated"]!.GetValue<bool>());
        Assert.Equal($"Unknown tool: {display.Text}", slot["error"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_UnknownArgumentName_CarriesTruncationMetadata_Issue3117()
    {
        var argumentName = new string('u', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = "search",
                            ["arguments"] = new JsonObject
                            {
                                ["query"] = "App",
                                [argumentName] = 1,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.DoesNotContain(argumentName, response.ToJsonString(), StringComparison.Ordinal);
        var slot = Assert.Single(response["result"]!["structuredContent"]!["results"]!.AsArray())!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal("search", slot["tool"]!.GetValue<string>());
        Assert.Equal(display.Text, slot["unknown_argument"]!.GetValue<string>());
        Assert.Equal(argumentName.Length, slot["unknown_argument_length"]!.GetValue<int>());
        Assert.True(slot["unknown_argument_truncated"]!.GetValue<bool>());
        Assert.Contains(display.Text, slot["error"]!.GetValue<string>());
        Assert.Contains(display.Text, slot["args_summary"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_EnumLikeScalarTooLong_CarriesTruncationMetadata_Issue3116()
    {
        var oversized = new string('A', McpBoundedText.MaxScalarArgumentChars + 25);
        var display = McpBoundedText.ForDisplay(oversized);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = "search",
                            ["arguments"] = new JsonObject
                            {
                                ["query"] = "App",
                                ["format"] = oversized,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.DoesNotContain(oversized, response.ToJsonString(), StringComparison.Ordinal);
        var slot = Assert.Single(response["result"]!["structuredContent"]!["results"]!.AsArray())!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal("search", slot["tool"]!.GetValue<string>());
        Assert.Equal("format", slot["parameter"]!.GetValue<string>());
        Assert.Equal(display.Text, slot["value"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxScalarArgumentChars, slot["max_length"]!.GetValue<int>());
        Assert.Equal(oversized.Length, slot["actual_length"]!.GetValue<int>());
        Assert.Equal(oversized.Length, slot["value_length"]!.GetValue<int>());
        Assert.True(slot["value_truncated"]!.GetValue<bool>());
        Assert.Contains(display.Text, slot["error"]!.GetValue<string>());
        Assert.Contains(display.Text, slot["args_summary"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_SanitizesSlotExceptionMessage_Issue2849()
    {
        const string secret = "SECRET_BATCH_SLOT_2849";
        var corruptDbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_corrupt_{Guid.NewGuid():N}.db");
        File.WriteAllText(corruptDbPath, $"not a sqlite database {secret}");
        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            using var server = new McpServer(corruptDbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"status"}]}}}""")!;

            var response = server.HandleMessage(request)!;

            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(1, structured["failure_count"]!.GetValue<int>());
            var slot = structured["results"]!.AsArray().Single()!;
            var error = slot["error"]!.GetValue<string>();
            Assert.Equal("Tool 'status' failed. See cdidx server stderr for details.", error);
            Assert.DoesNotContain(secret, error);
            Assert.DoesNotContain("file is not a database", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(McpErrorEnvelope.CategoryIndexCorrupted, slot["category"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
            DeleteFileRobust(corruptDbPath);
        }
    }

    [Fact]
    public void ToolsCall_BatchQuery_RejectsTypeMismatchedInnerArguments_Issue1615()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"search","arguments":{"query":"App","limit":"twenty"}},{"tool":"search","arguments":{"query":"App","format":false}},{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(3, structured["total_count"]!.GetValue<int>());
        Assert.Equal(1, structured["success_count"]!.GetValue<int>());
        Assert.Equal(2, structured["failure_count"]!.GetValue<int>());
        Assert.True(structured["partial_failure"]!.GetValue<bool>());
        Assert.Equal("isolated", structured["failure_scope"]!.GetValue<string>());

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.False(results[0]!["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid type for argument 'limit'", results[0]!["error"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, results[0]!["category"]!.GetValue<string>());
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid type for argument 'format'", results[1]!["error"]!.GetValue<string>());
        Assert.True(results[2]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ReportsMalformedSlotsAndActualExecutionCounts_Issue1838_1992_1994()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[123,{"tool":"search","arguments":{"query":"App","path":["",null,42,"src"]}},{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var metadata = structured["metadata"]!;
        Assert.Equal(3, metadata["submitted"]!.GetValue<int>());
        Assert.Equal(3, metadata["executed"]!.GetValue<int>());
        Assert.Equal(2, metadata["errors"]!.GetValue<int>());

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0]!["request_index"]!.GetValue<int>());
        Assert.False(results[0]!["ok"]!.GetValue<bool>());
        Assert.Contains("must be an object", results[0]!["error"]!.GetValue<string>());
        Assert.Equal(1, results[1]!["request_index"]!.GetValue<int>());
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.Contains("path contains 3 invalid entries", results[1]!["error"]!.GetValue<string>());
        Assert.Equal(2, results[2]!["request_index"]!.GetValue<int>());
        Assert.True(results[2]!["ok"]!.GetValue<bool>());

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 3 of 3 queries", text);
        Assert.Contains("1 succeeded, 2 failed", text);
    }

    [Fact]
    public void ToolsCall_RejectsOversizedPathArrays_Issue2028()
    {
        var paths = new JsonArray();
        for (var i = 0; i < McpServer.MaxMcpArrayFilterCount + 1; i++)
            paths.Add($"src/{i}.cs");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "App",
                    ["path"] = paths,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("path must contain at most", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, structured["category"]!.GetValue<string>());
        Assert.Equal(1, structured["invalid_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_TruncatesAggregateResponse_Issue1416()
    {
        var previous = Environment.GetEnvironmentVariable("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", "950");
        try
        {
            InsertIndexedFile("src/large.cs", "csharp", "// " + new string('x', 5000));
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"},{"tool":"excerpt","arguments":{"path":"src/large.cs","startLine":1,"endLine":1,"maxLineWidth":0}}]}}}""")!;
            var response = _server.HandleMessage(request)!;

            var structured = response["result"]!["structuredContent"]!;
            Assert.True(structured["truncated"]?.GetValue<bool>() ?? false, response.ToJsonString());
            var actualResponseBytes = Encoding.UTF8.GetByteCount(response.ToJsonString());
            Assert.True(actualResponseBytes <= 950, $"Actual response was {actualResponseBytes} bytes.");
            Assert.True(structured["metadata"]!["estimated_response_bytes"]!.GetValue<int>() <= 950);
            Assert.Equal(950, structured["metadata"]!["response_byte_limit"]!.GetValue<int>());
            Assert.Equal(2, structured["metadata"]!["submitted"]!.GetValue<int>());
            Assert.Equal(2, structured["metadata"]!["executed"]!.GetValue<int>());
            Assert.Equal(0, structured["metadata"]!["errors"]!.GetValue<int>());
            Assert.Equal("cascading", structured["failure_scope"]!.GetValue<string>());
            Assert.NotNull(structured["cascade_started_at_index"]);
            Assert.True(structured["partial_failure"]!.GetValue<bool>());

            var truncatedQueries = structured["truncated_queries"]!.AsArray();
            Assert.NotEmpty(truncatedQueries);
            Assert.All(truncatedQueries, q => Assert.NotNull(q!["args_summary"]));
            Assert.Contains(truncatedQueries, q =>
                q!["tool"]?.GetValue<string>() == "ping" &&
                q["reason"]?.GetValue<string>() == "final_response_byte_limit_exceeded");

            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("Response truncated", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", previous);
        }
    }

    [Fact]
    public void ToolsCall_BatchQuery_ClampsTooLargeResponseLimitEnvironment()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var metadata = response["result"]!["structuredContent"]!["metadata"]!;
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, metadata["response_byte_limit"]!.GetValue<int>());
    }

    [Fact]
    public void ApplyExcerptOutputBudget_TruncatesAtLineBoundary_Issue1605()
    {
        var payload = new JsonObject
        {
            ["content"] = "short\n" + new string('x', 200),
            ["contentTruncated"] = false,
        };

        McpServer.ApplyExcerptOutputBudget(payload, 20);

        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.Equal("output_size_cap", payload["truncation_reason"]!.GetValue<string>());
        Assert.Equal("short", payload["content"]!.GetValue<string>());
        Assert.True(payload["contentTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ArgsSummaryReflectsRequestedArguments_Issue1537()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"symbols","arguments":{"query":"App","lang":"csharp"}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var slot = response["result"]!["structuredContent"]!["results"]!.AsArray()[0]!;
        var summary = slot["args_summary"]!.GetValue<string>();
        Assert.Contains("query=", summary);
        Assert.Contains("App", summary);
        Assert.Contains("lang=", summary);
    }

    [Fact]
    public void ToolsCall_Languages_ReturnsCapabilities()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"languages","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("languages supported", text);
        var languages = response["result"]!["structuredContent"]!["languages"]!.AsArray();
        Assert.True(languages.Count > 20); // We support 30+ languages

        // Verify a known language has the right capabilities / 既知の言語の機能を検証
        var csharp = languages.First(l => l!["lang"]!.GetValue<string>() == "csharp")!;
        Assert.True(csharp["symbol_extraction"]!.GetValue<bool>());
        Assert.True(csharp["graph_queries"]!.GetValue<bool>());
        Assert.Contains(".cs", csharp["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));

        var javascript = languages.First(l => l!["lang"]!.GetValue<string>() == "javascript")!;
        Assert.Contains(".cjs", javascript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains(".mjs", javascript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));

        var typescript = languages.First(l => l!["lang"]!.GetValue<string>() == "typescript")!;
        Assert.Contains(".cts", typescript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains(".mts", typescript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));

        var assembly = languages.First(l => l!["lang"]!.GetValue<string>() == "assembly")!;
        Assert.True(assembly["symbol_extraction"]!.GetValue<bool>());
        Assert.True(assembly["graph_queries"]!.GetValue<bool>());
        Assert.Contains(".asm", assembly["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains(".S", assembly["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains("assembler", assembly["aliases"]!.AsArray().Select(e => e!.GetValue<string>()));

        // Verify a detection-only language / 検出のみの言語を検証
        var markdown = languages.First(l => l!["lang"]!.GetValue<string>() == "markdown")!;
        Assert.True(markdown["symbol_extraction"]!.GetValue<bool>());
        Assert.False(markdown["graph_queries"]!.GetValue<bool>());

        var yaml = languages.First(l => l!["lang"]!.GetValue<string>() == "yaml")!;
        Assert.Contains("yml", yaml["aliases"]!.AsArray().Select(e => e!.GetValue<string>()));

        // Pin #215: HTML must report symbol_extraction=true and list all four
        // extensions so AI tools discover HTML support via the MCP languages tool.
        // #215 を pin: HTML は symbol_extraction=true で、.html / .htm / .xhtml / .shtml
        // の 4 拡張子を MCP languages ツールから返すこと。
        var html = languages.First(l => l!["lang"]!.GetValue<string>() == "html")!;
        Assert.True(html["symbol_extraction"]!.GetValue<bool>());
        var htmlExtensions = html["extensions"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains(".html", htmlExtensions);
        Assert.Contains(".htm", htmlExtensions);
        Assert.Contains(".xhtml", htmlExtensions);
        Assert.Contains(".shtml", htmlExtensions);
    }

    [Fact]
    public void ToolsCall_Outline_ReturnsSymbols()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"outline","arguments":{"path":"src/app.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("symbol", text.ToLowerInvariant());
        Assert.NotNull(result["structuredContent"]);
        var structured = result["structuredContent"]!;
        Assert.Equal("src/app.cs", structured["path"]!.GetValue<string>());
        var symbols = structured["symbols"]!.AsArray();
        var run = symbols.Single(symbol => symbol!["name"]!.GetValue<string>() == "Run")!;
        Assert.Equal("Run()", run["displayName"]!.GetValue<string>());
        Assert.Equal("App.Run", run["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Outline_NotFound_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"outline","arguments":{"path":"nonexistent.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.NotNull(structured["error"]);
        Assert.NotNull(structured["indexed_file_count"]);
    }

    [Fact]
    public void ToolsCall_Index_MissingPath_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("db")]
    [InlineData("parallelism")]
    [InlineData("files")]
    [InlineData("commits")]
    [InlineData("changedBetween")]
    [InlineData("dryRun")]
    [InlineData("optimize")]
    public void ToolsCall_Index_RejectsUnsupportedArguments_Issue2848(string argumentName)
    {
        var arguments = new JsonObject
        {
            ["path"] = ".",
            [argumentName] = argumentName switch
            {
                "db" => JsonValue.Create("alternate.db"),
                "parallelism" => JsonValue.Create(2),
                "files" => new JsonArray(JsonValue.Create("src/app.cs")),
                "commits" => new JsonArray(JsonValue.Create("HEAD")),
                "changedBetween" => new JsonArray(JsonValue.Create("HEAD~1"), JsonValue.Create("HEAD")),
                "dryRun" => JsonValue.Create(true),
                "optimize" => JsonValue.Create(true),
                _ => throw new ArgumentOutOfRangeException(nameof(argumentName), argumentName, null),
            },
        };
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = arguments,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"Unknown argument '{argumentName}' for tool 'index'.", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(argumentName, structured["unknown_argument"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnknownArgumentName_TruncatesDisplay_Issue3117()
    {
        var argumentName = new string('x', McpBoundedText.MaxDiagnosticDisplayChars + 25);
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
                    [argumentName] = true,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.DoesNotContain(argumentName, response.ToJsonString(), StringComparison.Ordinal);
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"Unknown argument '{display.Text}' for tool 'index'.", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(display.Text, structured["unknown_argument"]!.GetValue<string>());
        Assert.Equal(argumentName.Length, structured["unknown_argument_length"]!.GetValue<int>());
        Assert.True(structured["unknown_argument_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_UnknownToolName_TruncatesDisplay_Issue3118()
    {
        var toolName = new string('t', McpBoundedText.MaxToolNameChars + 25);
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

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(toolName, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Unknown tool: {display.Text}", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["tool"]!.GetValue<string>());
        Assert.Equal(toolName.Length, data["tool_length"]!.GetValue<int>());
        Assert.True(data["tool_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_UnknownToolName_TruncatesMetricsLabels_Issue3118()
    {
        var toolName = new string('m', McpBoundedText.MaxToolNameChars + 25);
        var language = new string('l', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var toolDisplay = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var languageDisplay = McpBoundedText.ForDisplay(language);
        var metricsPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_metrics_{Guid.NewGuid():N}.jsonl");
        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
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
                        ["lang"] = language,
                    },
                },
            };

            var response = _server.HandleMessage(request)!;

            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
            var line = Assert.Single(File.ReadAllLines(metricsPath));
            Assert.DoesNotContain(toolName, line, StringComparison.Ordinal);
            Assert.DoesNotContain(language, line, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal(toolDisplay.Text, root.GetProperty("tool").GetString());
            Assert.Equal(languageDisplay.Text, root.GetProperty("language").GetString());
            Assert.Equal(1, root.GetProperty("exit_code").GetInt32());
            Assert.Equal("unknown_tool", root.GetProperty("error").GetString());
        }
        finally
        {
            DeleteFileRobust(metricsPath);
        }
    }

    [Fact]
    public void SanitizeArgs_TruncatesArgumentKeysForAuditAndTelemetry_Issue3117()
    {
        var argumentName = new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 1);
        var display = McpBoundedText.ForDisplay(argumentName);
        var args = new JsonObject
        {
            [argumentName] = "value",
        };

        var (keys, lengths, keyLengths, valuesEcho) = McpServer.SanitizeArgs(args, includeValues: false);

        Assert.Equal([display.Text], keys);
        var length = Assert.Single(lengths);
        Assert.Equal(display.Text, length.Key);
        Assert.Equal("value".Length, length.Value);
        var keyLength = Assert.Single(keyLengths);
        Assert.Equal(display.Text, keyLength.Key);
        Assert.Equal(argumentName.Length, keyLength.Value);
        Assert.Null(valuesEcho);
    }

    [Fact]
    public void SanitizeArgs_TruncatesArgumentKeysInValuesEcho_Issue3117()
    {
        var argumentName = new string('k', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName);
        var args = new JsonObject
        {
            [argumentName] = "value",
        };

        var (_, _, keyLengths, valuesEcho) = McpServer.SanitizeArgs(args, includeValues: true);

        Assert.NotNull(valuesEcho);
        var json = valuesEcho!.ToJsonString();
        Assert.DoesNotContain(argumentName, json, StringComparison.Ordinal);
        Assert.Equal("value", valuesEcho[display.Text]!.GetValue<string>());
        var keyLength = Assert.Single(keyLengths);
        Assert.Equal(display.Text, keyLength.Key);
        Assert.Equal(argumentName.Length, keyLength.Value);
    }

    [Fact]
    public void SanitizeArgs_DisambiguatesCollidingTruncatedKeys_Issue3117()
    {
        var sharedPrefix = new string('c', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var firstArgumentName = sharedPrefix + "a";
        var secondArgumentName = sharedPrefix + "b";
        var args = new JsonObject
        {
            [firstArgumentName] = "one",
            [secondArgumentName] = "two",
        };

        var (keys, lengths, keyLengths, valuesEcho) = McpServer.SanitizeArgs(args, includeValues: true);

        Assert.Equal(2, keys.Count);
        Assert.Equal(2, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, lengths.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, keyLengths.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(valuesEcho);
        var json = valuesEcho!.ToJsonString();
        Assert.DoesNotContain(firstArgumentName, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secondArgumentName, json, StringComparison.Ordinal);
        Assert.Equal("one", valuesEcho[keys[0]]!.GetValue<string>());
        Assert.Equal("two", valuesEcho[keys[1]]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Index_WhenDbLockHeld_ReturnsBusyError()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_lock_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_lock_{Guid.NewGuid():N}.db");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var infoPath = lockPath + ".info";
        File.WriteAllText(
            infoPath,
            $$"""{"pid":{{Environment.ProcessId}},"since":"2026-01-02T03:04:05.0000000+00:00"}""");
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
        try
        {
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
                        ["path"] = fixtureDir
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("index already running on this DB", text);
            Assert.Contains($"pid {Environment.ProcessId}", text);
            Assert.Contains("2026-01-02T03:04:05", text);
        }
        finally
        {
            heldLock.Dispose();
            File.Delete(infoPath);
            File.Delete(lockPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void McpIndexRunLock_TryAcquire_OnPosix_WritesPrivateInfoFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_private_lock_{Guid.NewGuid():N}.db");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        var infoPath = lockPath + ".info";
        try
        {
            Assert.True(McpIndexRunLock.TryAcquire(dbPath, out var runLock, out var error), error);
            Assert.NotNull(runLock);
            using (runLock!)
            {
                Assert.True(File.Exists(infoPath));
                Assert.Equal(
                    DataDirectorySecurity.PrivateFileMode,
                    File.GetUnixFileMode(infoPath) & DataDirectorySecurity.PermissionBits);
            }
        }
        finally
        {
            if (File.Exists(infoPath))
                File.Delete(infoPath);
            if (File.Exists(lockPath))
                File.Delete(lockPath);
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_WhenDbLockInfoTooLarge_ReturnsBusyWithoutHolderDetails()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_large_lock_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_large_lock_{Guid.NewGuid():N}.db");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var infoPath = lockPath + ".info";
        File.WriteAllText(infoPath, $$"""{"pid":{{Environment.ProcessId}},"since":"2026-01-02T03:04:05.0000000+00:00","padding":"{{new string('x', 5 * 1024)}}"}""");
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
        try
        {
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
                        ["path"] = fixtureDir
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("index already running on this DB", text);
            Assert.Contains("holder metadata unavailable", text);
            Assert.DoesNotContain($"pid {Environment.ProcessId}", text);
        }
        finally
        {
            heldLock.Dispose();
            File.Delete(infoPath);
            File.Delete(lockPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_WhenDbLockInfoTooDeep_ReturnsBusyWithoutHolderDetails_Issue3043()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_deep_lock_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_deep_lock_{Guid.NewGuid():N}.db");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var infoPath = lockPath + ".info";
        var info = new StringBuilder($$"""{"pid":{{Environment.ProcessId}},"since":"2026-01-02T03:04:05.0000000+00:00","extra":""");
        AppendNestedObject(info, McpIndexRunLock.MaxInfoJsonDepth + 1);
        info.Append('}');
        File.WriteAllText(infoPath, info.ToString());
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
        try
        {
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
                        ["path"] = fixtureDir
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("index already running on this DB", text);
            Assert.Contains("holder metadata unavailable", text);
            Assert.DoesNotContain($"pid {Environment.ProcessId}", text);
        }
        finally
        {
            heldLock.Dispose();
            File.Delete(infoPath);
            File.Delete(lockPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_NonexistentDir_ReturnsError()
    {
        // Use a path within CWD that doesn't exist / CWD内の存在しないパスを使用
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"./nonexistent_subdir_xyz_test"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Index_PopulatesValidateIssuesLikeCliIndex()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var filePath = Path.Combine(fixtureDir, "bom_sample.cs");
        try
        {
            File.WriteAllBytes(filePath, [0xEF, 0xBB, 0xBF, (byte)'c', (byte)'l', (byte)'a', (byte)'s', (byte)'s', (byte)' ', (byte)'A', (byte)' ', (byte)'{', (byte)'}', (byte)'\n']);

            var indexRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var indexResponse = _server.HandleMessage(indexRequest)!;
            Assert.False(indexResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            var validateRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"validate","arguments":{}}}""")!;
            var validateResponse = _server.HandleMessage(validateRequest)!;
            var issues = validateResponse["result"]!["structuredContent"]!["issues"]!.AsArray();

            Assert.Contains(issues, issue => issue!["kind"]!.GetValue<string>() == "bom");
        }
        finally
        {
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_FatalScanErrorDoesNotRestampReadiness_Issue2874()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_scan_error_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_scan_error_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
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
                        ["path"] = fixtureDir
                    }
                }
            };

            var firstResponse = server.HandleMessage(request)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var readyDb = new DbContext(dbPath))
            {
                var userVersion = readyDb.GetUserVersion();
                Assert.NotEqual(0, userVersion & DbContext.GraphReadyFlag);
                Assert.NotEqual(0, userVersion & DbContext.IssuesReadyFlag);
            }

            File.WriteAllText(Path.Combine(fixtureDir, ".gitignore"), new string('a', 256 * 1024 + 1));
            request["id"] = 2;

            var secondResponse = server.HandleMessage(request)!;
            var structured = secondResponse["result"]!["structuredContent"]!;

            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Equal(1, structured["summary"]!["errors"]!.GetValue<int>());
            Assert.Equal(1, structured["summary"]!["failed_count"]!.GetValue<int>());
            var failure = Assert.Single(structured["failures"]!.AsArray());
            Assert.Equal(".gitignore", failure!["path"]!.GetValue<string>());
            Assert.Equal("scan", failure["stage"]!.GetValue<string>());
            Assert.Equal(nameof(FileIndexer.ScanError), failure["exception_type"]!.GetValue<string>());
            Assert.False(failure["message_truncated"]!.GetValue<bool>());
            Assert.DoesNotContain(fixtureDir, failure["message"]!.GetValue<string>());
            Assert.DoesNotContain("\n", failure["message"]!.GetValue<string>());

            using var failedDb = new DbContext(dbPath);
            var failedUserVersion = failedDb.GetUserVersion();
            Assert.Equal(0, failedUserVersion & DbContext.GraphReadyFlag);
            Assert.Equal(0, failedUserVersion & DbContext.IssuesReadyFlag);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_FailedFirstMutation_DoesNotRewriteIndexedProjectRootMetadata()
    {
        var projectRootA = TestProjectHelper.CreateTempProject("cdidx_mcp_index_root_a");
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_root_b_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_root_{Guid.NewGuid():N}.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRootA);
            var sourcePathA = Path.Combine(projectRootA, "app.cs");
            File.WriteAllText(sourcePathA, "public class AppA { public void Run() { } }\n");
            TestProjectHelper.RunGit(projectRootA, "add", "app.cs");
            TestProjectHelper.RunGit(projectRootA, "commit", "-m", "init-a");
            var headA = TestProjectHelper.RunGit(projectRootA, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER fail_update
                    BEFORE UPDATE ON files
                    BEGIN
                        SELECT RAISE(FAIL, 'boom');
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            Directory.CreateDirectory(fixtureDir);
            TestProjectHelper.InitializeGitRepo(fixtureDir);
            var sourcePathB = Path.Combine(fixtureDir, "app.cs");
            File.WriteAllText(sourcePathB, "public class AppB { public void Run() { } public void Extra() { } }\n");
            TestProjectHelper.RunGit(fixtureDir, "add", "app.cs");
            TestProjectHelper.RunGit(fixtureDir, "commit", "-m", "init-b");
            File.SetLastWriteTimeUtc(sourcePathB, DateTime.UtcNow.AddSeconds(2));

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var indexRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var indexResponse = server.HandleMessage(indexRequest)!;
            Assert.Equal(1, indexResponse["result"]!["structuredContent"]!["summary"]!["errors"]!.GetValue<int>());

            var statusRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var statusResponse = server.HandleMessage(statusRequest)!;

            Assert.Equal(projectRootA, statusResponse["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(headA, statusResponse["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRootA);
            if (Directory.Exists(fixtureDir))
                TestProjectHelper.DeleteDirectory(fixtureDir);
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_SuccessfulNoOpBackfillsMissingIndexedProjectRootMetadata()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_noop_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_noop_{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(fixtureDir);
            TestProjectHelper.InitializeGitRepo(fixtureDir);
            var sourcePath = Path.Combine(fixtureDir, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            TestProjectHelper.RunGit(fixtureDir, "add", "app.cs");
            TestProjectHelper.RunGit(fixtureDir, "commit", "-m", "init");
            var expectedHead = TestProjectHelper.RunGit(fixtureDir, "rev-parse", "HEAD").Trim();

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var indexRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(indexRequest)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var db = new DbContext(dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }

            var secondResponse = server.HandleMessage(indexRequest)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Equal(1, secondResponse["result"]!["structuredContent"]!["summary"]!["skipped"]!.GetValue<int>());

            using (var db = new DbContext(dbPath))
            {
                Assert.Equal(Path.GetFullPath(fixtureDir), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var statusRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var statusResponse = server.HandleMessage(statusRequest)!;

            Assert.Equal(Path.GetFullPath(fixtureDir), statusResponse["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, statusResponse["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(fixtureDir))
                TestProjectHelper.DeleteDirectory(fixtureDir);
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_StampsFoldReady()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.False(structured["dry_run"]!.GetValue<bool>());
        Assert.False(structured["was_already_complete"]!.GetValue<bool>());
        Assert.False(structured["fold_ready_before"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_after"]!.GetValue<bool>());
        Assert.True(structured["verified"]!.GetValue<bool>());
        Assert.Equal(3, structured["user_version_before"]!.GetValue<int>());
        Assert.Equal(7, structured["user_version_after"]!.GetValue<int>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());
        Assert.Equal(2, structured["progress"]!["rows_done"]!.GetValue<int>());
        Assert.Equal(2, structured["progress"]!["rows_total"]!.GetValue<int>());
        Assert.Equal(1.0, structured["progress"]!["fraction"]!.GetValue<double>());

        using var verifyDb = new DbContext(_dbPath);
        verifyDb.TryMigrateForRead();
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
    }

    [Fact]
    public void ToolsCall_BackfillFold_ExceptionUsesSanitizedToolError_Issue3201()
    {
        var previousDebug = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            DbWriter.FoldBackfillRowUpdatedForTesting = () =>
                throw new InvalidOperationException("SECRET_BACKFILL_LITERAL from /private/path");

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
            var response = _server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            var text = response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Equal("Tool 'backfill_fold' failed. See cdidx server stderr for details.", text);
            Assert.DoesNotContain("SECRET_BACKFILL_LITERAL", response.ToJsonString());
            Assert.DoesNotContain("/private/path", response.ToJsonString());

            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal("internal_error", structured["category"]!.GetValue<string>());
            Assert.Equal("backfill_fold", structured["tool"]!.GetValue<string>());
            Assert.Equal(nameof(InvalidOperationException), structured["exception_type"]!.GetValue<string>());
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previousDebug);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_DryRunDoesNotWrite()
    {
        var writer = new DbWriter(_db.Connection);
        writer.BackfillFoldedColumns(rewriteAll: true);
        writer.MarkFoldReady();
        writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{"dry_run":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["dry_run"]!.GetValue<bool>());
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(2, structured["progress"]!["rows_total"]!.GetValue<int>());
        Assert.False(structured["verified"]!.GetValue<bool>());
        Assert.False(structured["fold_ready_before"]!.GetValue<bool>());
        Assert.False(structured["fold_ready_after"]!.GetValue<bool>());
        Assert.False(structured["fold_ready"]!.GetValue<bool>());

        Assert.Equal("DEADBEEFDEADBEEF", _db.GetMetaString("fold_key_fingerprint"));
        Assert.Equal(7, _db.GetUserVersion());
    }

    [Fact]
    public void ToolsCall_BackfillFold_SecondRunSignalsAlreadyComplete()
    {
        var first = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        Assert.False(_server.HandleMessage(first)!["result"]!["isError"]?.GetValue<bool>() ?? false);

        var second = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(second)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.Equal(0, structured["progress"]!["rows_total"]!.GetValue<int>());
        Assert.False(structured["rewrite_all"]!.GetValue<bool>());
        Assert.True(structured["was_already_complete"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_before"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_after"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_BackfillFold_ForceRewritesAlreadyCompleteRows()
    {
        var first = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        Assert.False(_server.HandleMessage(first)!["result"]!["isError"]?.GetValue<bool>() ?? false);

        var forced = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"backfill_fold","arguments":{"force":true}}}""")!;
        var response = _server.HandleMessage(forced)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["force"]!.GetValue<bool>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.False(structured["was_already_complete"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_after"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_BackfillFold_RewritesAllWhenOnlyFingerprintDrifted()
    {
        var writer = new DbWriter(_db.Connection);
        writer.BackfillFoldedColumns(rewriteAll: true);
        writer.MarkFoldReady();
        writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.True(structured["verified"]!.GetValue<bool>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());

        using var verifyDb = new DbContext(_dbPath);
        verifyDb.TryMigrateForRead();
        Assert.Equal(NameFold.Fingerprint(), verifyDb.GetMetaString("fold_key_fingerprint"));
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
    }

    [Fact]
    public void ToolsCall_Index_DoesNotRestampFoldReadyWhenFoldKeyVersionMismatches()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_version_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_version_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Straße() { } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            File.WriteAllText(Path.Combine(fixtureDir, "new.cs"), "public class NewFile { }");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.False(secondResponse["result"]!["structuredContent"]!["fold_ready"]!.GetValue<bool>());
            Assert.Equal("stale_fold_key_version", secondResponse["result"]!["structuredContent"]!["fold_ready_reason"]!.GetValue<string>());
            var text = secondResponse["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("older fold-key version", text);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.NotEqual(NameFold.Version.ToString(), storedVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_ClearsHotspotFamilyTrustOnPartialFailure()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_hotspot_family_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_hotspot_family_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Run() { } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var seededDb = new DbContext(dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            WriteOversizedAsciiFile(Path.Combine(fixtureDir, "app.cs"));

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Equal(1, secondResponse["result"]!["structuredContent"]!["summary"]!["errors"]!.GetValue<int>());

            using var verifyDb = new DbContext(dbPath);
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_Rebuild_SucceedsOnFreshDb()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_rebuild_fresh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_rebuild_fresh_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

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
                        ["path"] = fixtureDir,
                        ["rebuild"] = true,
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(response["result"]!["structuredContent"]!["summary"]!["files"]!.GetValue<long>() >= 1L);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_ResolvesTypeScriptPathAliasesFromProjectRoot()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_ts_alias_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_ts_alias_{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(Path.Combine(fixtureDir, "src", "components"));
            Directory.CreateDirectory(Path.Combine(fixtureDir, "src", "pages"));
            File.WriteAllText(Path.Combine(fixtureDir, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(fixtureDir, "src", "components", "Button.tsx"), "export const Button = () => null;\n");
            File.WriteAllText(Path.Combine(fixtureDir, "src", "pages", "Page.tsx"), "import { Button } from \"@/components/Button\";\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
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
                        ["path"] = fixtureDir,
                        ["rebuild"] = true,
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbols
                    WHERE kind = 'import'
                      AND name = 'src/components/Button.tsx'
                    """;
                Assert.Equal(1L, (long)command.ExecuteScalar()!);
            }

            Directory.CreateDirectory(Path.Combine(fixtureDir, "app", "components"));
            File.WriteAllText(Path.Combine(fixtureDir, "app", "components", "Button.tsx"), "export const UpdatedButton = () => null;\n");
            File.WriteAllText(Path.Combine(fixtureDir, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["app/*"]
                    }
                  }
                }
                """);

            response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT name, COUNT(*)
                    FROM symbols
                    WHERE kind = 'import'
                      AND name IN ('src/components/Button.tsx', 'app/components/Button.tsx')
                    GROUP BY name
                    """;
                using var reader = command.ExecuteReader();
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                while (reader.Read())
                    counts[reader.GetString(0)] = reader.GetInt64(1);

                Assert.Equal(1L, counts["app/components/Button.tsx"]);
                Assert.False(counts.ContainsKey("src/components/Button.tsx"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_RestampsHotspotFamilyReadyWhenMarkerFingerprintChanges()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_marker_fingerprint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var srcDir = Path.Combine(fixtureDir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_marker_fingerprint_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part1.cs"),
                """
                public partial class Api
                {
                    public void Run() { }
                }
                """);
            File.WriteAllText(Path.Combine(srcDir, "Api.Part2.cs"),
                """
                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(srcDir, "Caller.cs"),
                """
                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                        api.Run(1);
                    }
                }
                """);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var seededDb = new DbContext(dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            File.WriteAllText(Path.Combine(fixtureDir, "Extra.csproj"), "<Project />");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal(
                DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_KeepsCsharpHotspotFamilyTrustWhenOnlyVbMarkersChange()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_marker_isolation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var srcDir = Path.Combine(fixtureDir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_marker_isolation_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(srcDir, "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            File.WriteAllText(Path.Combine(fixtureDir, "Unrelated.vbproj"), "<Project />");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var structured = hotspotsResponse["result"]!["structuredContent"]!;
            Assert.True(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            if (structured["degraded"] is JsonNode degradedNode)
                Assert.False(degradedNode.GetValue<bool>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_Index_RestampsHotspotFamilyTrustWhenOnlyMetadataWasCleared()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_marker_metadata_only_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var srcDir = Path.Combine(fixtureDir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_marker_metadata_only_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(srcDir, "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(secondResponse["result"]!["structuredContent"]!["summary"]!["skipped"]!.GetValue<int>() > 0);

            using (var verifyDb = new DbContext(dbPath))
            {
                Assert.Equal(
                    DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
                Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
            }

            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var structured = hotspotsResponse["result"]!["structuredContent"]!;
            Assert.True(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.Equal(2, structured["count"]!.GetValue<int>());
            if (structured["degraded"] is JsonNode degradedNode)
                Assert.False(degradedNode.GetValue<bool>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ReportsDegradedHotspotFamilyTrust()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_hotspots_family_signal_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.Contains("hotspot_family_support_not_indexed=csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
            Assert.Contains("degraded", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ReportsLegacyNullFamilyKeysAsDegraded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_hotspots_family_legacy_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols
                    SET family_key = NULL,
                        container_qualified_name = NULL
                    WHERE file_id IN (
                        SELECT id FROM files WHERE lang = 'csharp'
                    );
                    """;
                cmd.ExecuteNonQuery();
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.Contains("csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ReportsMissingMarkerFingerprintAsDegraded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_hotspots_family_missing_fingerprint_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.Contains("hotspot_family_disabled_at_index_time=csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_GroupByFileReportsGroupingMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_hotspots_group_file");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp",
                """
                public class One
                {
                    private void A() { A(); A(); }
                    private void B() { B(); }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Two.cs", "csharp",
                """
                public class Two
                {
                    private void C() { C(); }
                }
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function","groupBy":"file","limit":1}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            var hotspot = structured["hotspots"]!.AsArray().Single()!;
            Assert.Equal("file", structured["grouped_by"]!.GetValue<string>());
            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.Equal("src/One.cs", hotspot["path"]!.GetValue<string>());
            Assert.Equal(3, hotspot["reference_count"]!.GetValue<int>());
            Assert.Equal(2, hotspot["symbol_count"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ProjectScopeFiltersHotspotsAndUnusedSymbols_Issue1707()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "AppA"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "AppB"));
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppA", "src\AppA\AppA.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppB", "src\AppB\AppB.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "AppA", "AppA.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "AppB", "AppB.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/AppA/ServiceA.cs", "csharp",
                """
                public class ServiceA
                {
                    public void UsedA() { UsedA(); }
                    public void UnusedA() { }
                }

                public class CallerA
                {
                    public void Call(ServiceA service) { service.UsedA(); }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/AppB/ServiceB.cs", "csharp",
                """
                public class ServiceB
                {
                    public void UsedB() { UsedB(); UsedB(); }
                    public void UnusedB() { }
                }

                public class CallerB
                {
                    public void Call(ServiceB service)
                    {
                        service.UsedB();
                        service.UsedB();
                    }
                }
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
            }

            Environment.CurrentDirectory = projectRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function","project":"AppA"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var hotspotNames = hotspotsResponse["result"]!["structuredContent"]!["hotspots"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            var unusedRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","project":"AppA"}}}""")!;
            var unusedResponse = server.HandleMessage(unusedRequest)!;
            var unusedNames = unusedResponse["result"]!["structuredContent"]!["symbols"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.False(hotspotsResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Contains("UsedA", hotspotNames);
            Assert.DoesNotContain("UsedB", hotspotNames);
            Assert.False(unusedResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Contains("UnusedA", unusedNames);
            Assert.DoesNotContain("UnusedB", unusedNames);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Index_Rebuild_IgnoresUnreadableDirectoriesWhenCollectingMarkerFingerprints()
    {
        if (OperatingSystem.IsWindows())
            return;

        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_unreadable_marker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_unreadable_marker_{Guid.NewGuid():N}.db");
        var unreadableDir = Path.Combine(fixtureDir, "secret");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Run() { } }");
            Directory.CreateDirectory(unreadableDir);
            File.WriteAllText(Path.Combine(unreadableDir, "Hidden.csproj"), "<Project />");
            originalMode = File.GetUnixFileMode(unreadableDir);
            File.SetUnixFileMode(unreadableDir, UnixFileMode.None);

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
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
                        ["path"] = fixtureDir,
                        ["rebuild"] = true,
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(response["result"]!["structuredContent"]!["summary"]!["files"]!.GetValue<long>() >= 1L);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(unreadableDir))
                File.SetUnixFileMode(unreadableDir, originalMode.Value);
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_BlankFile_ReturnsError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_backfill_blank_{Guid.NewGuid():N}.db");
        File.WriteAllText(dbPath, string.Empty);

        try
        {
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("not an existing CodeIndex DB", text);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_NonexistentFileUri_ReturnsError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_backfill_missing_{Guid.NewGuid():N}.db");
        var dbUri = new Uri(dbPath).AbsoluteUri;
        using var server = new McpServer(dbUri, ConsoleUi.LoadVersion());
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Database not found", text);
    }

    [Fact]
    public void ToolsCall_BackfillFold_LegacyDbWithoutCodeIndexMeta_Succeeds()
    {
        using (var dropMeta = _db.Connection.CreateCommand())
        {
            dropMeta.CommandText = "DROP TABLE codeindex_meta;";
            dropMeta.ExecuteNonQuery();
        }

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());

        using var verifyDb = new DbContext(_dbPath);
        verifyDb.TryMigrateForRead();
        Assert.Equal(NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString("fold_key_version"));
        Assert.Equal(NameFold.Fingerprint(), verifyDb.GetMetaString("fold_key_fingerprint"));
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_IncludesConfidenceBuckets()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/config/unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "PathResolver",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "public class PathResolver",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AdoptionService",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public class AdoptionService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "TokenService",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public class TokenService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AppSettings",
                Line = 9,
                StartLine = 9,
                EndLine = 11,
                Signature = "public class AppSettings",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ApplyConfiguration",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public void ApplyConfiguration()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "UseIOptions",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public void UseIOptions()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ConnectionString",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string ConnectionString { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"unused_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        var symbols = structured["symbols"]!.AsArray();
        Assert.Equal(9, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["returned_bucket_counts"]!["likely_unused_private"]!.GetValue<int>());
        Assert.Equal(1, structured["returned_bucket_counts"]!["maybe_unused_nonpublic"]!.GetValue<int>());
        Assert.Equal(6, structured["returned_bucket_counts"]!["public_or_exported_no_refs"]!.GetValue<int>());
        Assert.Equal(1, structured["returned_bucket_counts"]!["reflection_or_config_suspect"]!.GetValue<int>());
        Assert.Equal(1, structured["summary"]!["by_bucket"]!["likely_unused_private"]!.GetValue<int>());
        Assert.Equal(8, structured["summary"]!["by_confidence"]!["low"]!.GetValue<int>());
        Assert.Equal("low", structured["bucket_taxonomy"]!["reflection_or_config_suspect"]!["confidence"]!.GetValue<string>());
        Assert.Contains("reflection", structured["bucket_taxonomy"]!["reflection_or_config_suspect"]!["description"]!.GetValue<string>());
        Assert.Equal("Hidden", symbols[0]!["name"]!.GetValue<string>());
        Assert.Equal("likely_unused_private", symbols[0]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("medium", symbols[0]!["unusedConfidence"]!.GetValue<string>());
        Assert.Equal("PathResolver", symbols[2]!["name"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols[2]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("ConnectionString", symbols[3]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[3]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("ApplyConfiguration", symbols[7]!["name"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols[7]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("UseIOptions", symbols[8]!["name"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols[8]!["unusedBucket"]!.GetValue<string>());
        Assert.Contains("returned buckets", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_IncludesUnusedCSharpEnumMembersWithoutDegradedMetadata()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public enum Color
            {
                Red,
                Blue
            }

            public enum TrulyUnused
            {
                Green
            }

            public class UsesColor
            {
                public Color Shade => Color.Red;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        var names = structured["symbols"]!
            .AsArray()
            .Select(symbol => symbol?["name"]?.GetValue<string>())
            .Where(name => name != null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.DoesNotContain("Color", names);
        Assert.Contains("TrulyUnused", names);
        Assert.DoesNotContain("Red", names);
        Assert.Contains("Blue", names);
        Assert.Contains("Green", names);
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_EnumDeclarationsReturnNormalSummary()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_unused_enum_gap_summary");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red,
                    Blue
                }

                public enum TrulyUnused
                {
                    Green
                }

                public class UsesColor
                {
                    public Color Shade => Color.Red;
                }
                """);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            var names = structured["symbols"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(structured["count"]!.GetValue<int>() >= 2);
            Assert.Null(structured["graphDegraded"]);
            Assert.Null(structured["unsupportedSymbolKind"]);
            Assert.DoesNotContain("Color", names);
            Assert.Contains("TrulyUnused", names);
            Assert.Contains("Green", names);
            Assert.Contains(
                "Found",
                response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesReflectionAttributedPropertyAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"reflection_unused_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray();
        Assert.Equal("UserDto", symbols[0]!["name"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols[0]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("FullName", symbols[1]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[1]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesCommentSeparatedReflectionAttributeAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_fixture.cs",
            Lang = "csharp",
            Size = 220,
            Lines = 8,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    // Bound from JSON payload.
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"reflection_comment_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray();
        Assert.Equal("FullName", symbols[1]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[1]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_MissingChunksDegradesReflectionClassificationWithoutCrashing()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_missing_chunks_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE chunks;";
            cmd.ExecuteNonQuery();
        }

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"reflection_missing_chunks_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("public_or_exported_no_refs", symbols["FullName"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_KeepsPlainCliOptionsPropertiesInPublicBucket()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/cli_options_fixture.cs",
            Lang = "csharp",
            Size = 180,
            Lines = 6,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "CliOptions",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public sealed class CliOptions",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ShowHelp",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public bool ShowHelp { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ProjectPath",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "public string? ProjectPath { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"cli_options_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("public_or_exported_no_refs", symbols["ShowHelp"]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols["ProjectPath"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesQualifiedAndSuffixedAttributesAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_qualified_fixture.cs",
            Lang = "csharp",
            Size = 360,
            Lines = 12,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [global::System.Text.Json.Serialization.JsonPropertyName("full_name")]
                    public string QualifiedName { get; set; } = string.Empty;
                    [JsonPropertyNameAttribute("display_name")]
                    public string SuffixedName { get; set; } = string.Empty;
                    [System.Text.Json.Serialization.JsonIgnoreAttribute]
                    public string IgnoredName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 10,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "QualifiedName",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string QualifiedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "SuffixedName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string SuffixedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredName",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string IgnoredName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"reflection_qualified_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!
            .AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("reflection_or_config_suspect", symbols["QualifiedName"]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols["SuffixedName"]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols["IgnoredName"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesBlockCommentSeparatedReflectionAttributeAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 280,
            Lines = 10,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    /* bound from payload
                       via serializer */
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"reflection_block_comment_fixture.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!
            .AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("reflection_or_config_suspect", symbols["FullName"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_UnsupportedLanguageReturnsZero()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "script.txt",
            Lang = "text",
            Size = 64,
            Lines = 4,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "helper",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                Signature = "helper() {",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"text"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.False(structured["graph_supported"]!.GetValue<bool>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Empty(structured["symbols"]!.AsArray());
        Assert.Contains("unavailable", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_LargePublicLimit_RespectsMcpClamp()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/large_public_unused_fixture.cs",
            Lang = "csharp",
            Size = 16000,
            Lines = 2600,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "public class PublicNoise0000 { }",
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 2500; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = $"public class PublicNoise{i:D4} {{ }}",
                Visibility = "public",
            });
        }
        writer.InsertSymbols(symbols);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"large_public_unused_fixture.cs","limit":3000}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(200, structured["count"]!.GetValue<int>());
        Assert.Equal(200, structured["symbols"]!.AsArray().Count);
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_MissingGraphTable_MarksResponseDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_unused_missing_graph");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.cs",
                    Lang = "csharp",
                    Size = 42,
                    Lines = 3,
                    Modified = new DateTime(2024, 1, 1),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 3,
                    Content = "public class App\n{\n    public void Run() { }\n}",
                }]);
                writer.InsertSymbols([new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = "App",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 4,
                    Signature = "public class App",
                    Visibility = "public",
                }]);
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.False(structured["graph_table_available"]!.GetValue<bool>());
            Assert.Contains("missing", structured["note"]!.GetValue<string>());
            Assert.Contains("degraded", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]

    public void ToolsCall_Index_RestampsFoldReadyWhenFoldKeyVersionMismatchesButAllRowsAreRewritten()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_version_rewrite_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_version_rewrite_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "intl.py"), "def Straße():\n    pass\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    UPDATE files SET modified = '2000-01-01T00:00:00.0000000Z' WHERE path = 'intl.py';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var rewrittenPath = Path.Combine(fixtureDir, "intl.py");
            File.WriteAllText(rewrittenPath, "def Straße():\n    return 1\n");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(secondResponse["result"]!["structuredContent"]!["fold_ready"]!.GetValue<bool>());
            Assert.Null(secondResponse["result"]!["structuredContent"]!["fold_ready_reason"]);

            using var verify = new DbContext(dbPath);
            using var userVerCmd = verify.Connection.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.Connection.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.Equal(NameFold.Version.ToString(), storedVersion);

            var reader = new DbReader(verify.Connection, verify.IsReadOnly);
            Assert.Single(reader.SearchSymbols(new[] { "STRASSE" }, limit: 10, exact: true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(fixtureDir))
                Directory.Delete(fixtureDir, recursive: true);
        }
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_DiversifiesReflectionSuspectBeforeLimit()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_diversified_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 12,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                    public void Run() { Hidden(); }
                    private void Hidden() { }
                    internal void InternalOnly() { }
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public void Run() { Hidden(); }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"reflection_diversified_unused_fixture.cs","limit":4}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray();
        Assert.Equal(["InternalOnly", "UserDto", "FullName", "Run"], symbols.Select(symbol => symbol!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal("reflection_or_config_suspect", symbols[2]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnknownTool_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nonexistent","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_MissingToolName_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    // --- Security tests / セキュリティテスト ---

    [Fact]
    public void ToolsCall_Index_PathTraversal_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"/etc"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("current working directory", text);
    }

    [Fact]
    public void ToolsCall_Index_SymlinkEscapingCurrentDirectory_ReturnsError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var originalCurrentDirectory = Environment.CurrentDirectory;
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_symlink_root");
        var outsideRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_symlink_outside");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_symlink_{Guid.NewGuid():N}.db");
        var linkPath = Path.Combine(projectRoot, "outside-link");
        try
        {
            File.WriteAllText(Path.Combine(outsideRoot, "secret.cs"), "public class Secret { }\n");
            Directory.CreateSymbolicLink(linkPath, outsideRoot);

            Environment.CurrentDirectory = projectRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
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
                        ["path"] = linkPath
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("current working directory", text);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(outsideRoot);
            DeleteFileRobust(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Search_QueryTooLong_ReturnsError()
    {
        var longQuery = new string('a', 1001);
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{\"query\":\"" + longQuery + "\"}}}";
        var request = JsonNode.Parse(json)!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("too long", text);
    }

    // --- Database not found tests / DB未検出テスト ---

    [Fact]
    public void ToolsCall_Search_DbNotFound_ReturnsError()
    {
        using var server = new McpServer("/nonexistent/path/test.db", "0.1.1");
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"test"}}}""")!;
        var response = server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("not found", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    // --- suggest_improvement tests / suggest_improvement テスト ---

    [Fact]
    public void SuggestImprovement_ValidInput_ReturnsSuccess()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_GITHUB_TOKEN");
        env.Set("CDIDX_GITHUB_TOKEN", null);
        // Use unique description to avoid dedup collision with other test runs
        // 他テスト実行との重複排除衝突を避けるため一意な description を使用
        var uniqueDesc = $"Arrow functions are not detected as symbols {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "symbol_extraction", ["language"] = "typescript", ["description"] = uniqueDesc }
            }
        };
        var request = (JsonNode)json;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("draft", structured["lifecycle_status"]!.GetValue<string>());
        Assert.NotNull(structured["hash"]);
        Assert.True(structured["stored_locally"]!.GetValue<bool>());
        Assert.False(structured["submitted_to_github"]!.GetValue<bool>());
        Assert.Equal("token_not_configured", structured["github_submission_reason"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(_dbPath)!), structured["cdidx_dir"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_CrashReport_ReturnsSuccess()
    {
        var uniqueDesc = $"NullReferenceException when searching with empty query {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "crash_report", ["description"] = uniqueDesc }
            }
        };
        var request = (JsonNode)json;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_RecordsClientAttributionFromInitialize()
    {
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"codex","version":"5.0"}}}""")!);
        var uniqueDesc = $"Attribution metadata regression {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                    ["toolInvocationContext"] = "Investigating suggestion triage",
                    ["evidencePaths"] = new JsonArray { "src/CodeIndex/Mcp/McpToolHandlers.cs" }
                }
            }
        };

        _server.HandleMessage((JsonNode)json);

        var cdidxDir = Path.GetDirectoryName(_dbPath)!;
        var dbName = Path.GetFileNameWithoutExtension(_dbPath);
        var stored = new SuggestionStore(cdidxDir, dbName).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Equal("codex/5.0", stored.CreatedByAgent);
        Assert.Equal(_server.CurrentSessionId, stored.SessionId);
        Assert.Equal(ConsoleUi.LoadVersion(), stored.ClientVersion);
        Assert.Equal("codex", stored.McpClientName);
        Assert.Equal("5.0", stored.McpClientVersion);
        Assert.Equal("Investigating suggestion triage", stored.ToolInvocationContext);
        Assert.Equal(["src/CodeIndex/Mcp/McpToolHandlers.cs"], stored.EvidencePaths);
    }

    [Fact]
    public void SuggestImprovement_RejectsNonRelativeEvidencePath()
    {
        var uniqueDesc = $"Evidence path validation regression {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                    ["evidencePaths"] = new JsonArray { "/Users/example/project/src/File.cs" }
                }
            }
        };

        var response = _server.HandleMessage((JsonNode)json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var message = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("repository-relative", message);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingAvailable_StoresSampledMetadata()
    {
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = """{"title":"Improve TypeScript arrow symbol extraction","tags":["symbol_extraction","typescript","ranking"]}"""
                }
            };
        };
        var uniqueDesc = $"TypeScript arrow symbols need clearer extraction {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "symbol_extraction",
                    ["language"] = "typescript",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("Improve TypeScript arrow symbol extraction", structured["sampled_title"]!.GetValue<string>());
        Assert.Contains(structured["sampled_tags"]!.AsArray(), tag => tag!.GetValue<string>() == "typescript");
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Equal("Improve TypeScript arrow symbol extraction", stored.SampledTitle);
        Assert.Contains("symbol_extraction", stored.SampledTags!);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingResponseIsTooLarge_IgnoresSampledMetadata()
    {
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $$"""{"title":"{{new string('A', 9000)}}","tags":["security"]}"""
                }
            };
        };
        var uniqueDesc = $"Oversized sampling response regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Null(structured["sampled_title"]);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Null(stored.SampledTitle);
        Assert.Null(stored.SampledTags);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingResponseJsonIsTooDeep_IgnoresSampledMetadata()
    {
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            Assert.Equal("sampling/createMessage", method);
            var deepTail = new string('[', 40) + "null" + new string(']', 40);
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $$"""{"title":"Deep sampling metadata","tags":["security"],"nested":{{deepTail}}}"""
                }
            };
        };
        var uniqueDesc = $"Deep sampling response regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Null(structured["sampled_title"]);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Null(stored.SampledTitle);
        Assert.Null(stored.SampledTags);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingAvailable_BoundsPromptAndSummarizesInvocationContext()
    {
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        string? capturedPrompt = null;
        _server.ClientRequestHandlerForTests = (method, parameters) =>
        {
            Assert.Equal("sampling/createMessage", method);
            capturedPrompt = parameters?["messages"]?[0]?["content"]?["text"]?.GetValue<string>();
            return new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = """{"title":"Bound sampling prompt","tags":["security"]}"""
                }
            };
        };
        var uniqueDesc = new string('\u3042', 2000);
        var context = new string('\u3044', 1000);
        const string secretValue = "secret-token-1234567890";
        var toolInvocationContext = $"search request included token {secretValue} and detailed invocation payload";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                    ["context"] = context,
                    ["toolInvocationContext"] = toolInvocationContext,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("recorded", structured["status"]!.GetValue<string>());
        Assert.Equal("Bound sampling prompt", structured["sampled_title"]!.GetValue<string>());
        Assert.NotNull(capturedPrompt);
        Assert.True(Encoding.UTF8.GetByteCount(capturedPrompt) <= 4096);
        Assert.Contains("tool_invocation_context: provided;", capturedPrompt);
        Assert.Contains("raw content withheld", capturedPrompt);
        Assert.DoesNotContain(secretValue, capturedPrompt);
        Assert.Contains("[truncated]", capturedPrompt);
        var stored = new SuggestionStore(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath)).LoadAll()
            .Single(s => s.Description == uniqueDesc);
        Assert.Equal(toolInvocationContext, stored.ToolInvocationContext);
    }

    [Fact]
    public void SuggestImprovement_WhenSamplingDisabled_DoesNotCallClientSampling()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_SAMPLING");
        env.Set("CDIDX_MCP_SAMPLING", "0");
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"sampling":{}}}}""")!);
        var called = false;
        _server.ClientRequestHandlerForTests = (_, _) =>
        {
            called = true;
            return null;
        };
        var uniqueDesc = $"Sampling opt-out regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                }
            }
        };

        var response = _server.HandleMessage(request)!;

        Assert.False(called);
        Assert.Equal("recorded", response["result"]!["structuredContent"]!["status"]!.GetValue<string>());
        Assert.Null(response["result"]!["structuredContent"]!["sampled_title"]);
    }

    [Fact]
    public void Index_WhenClientRootsExcludePath_ReturnsError()
    {
        var requestedMethods = new List<string>();
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethods.Add(method);
            return new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = "file:///tmp/cdidx-not-this-workspace" })
            };
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Contains("roots/list", requestedMethods);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Index_WhenClientRootsAreEmpty_ReturnsError()
    {
        var requestedMethods = new List<string>();
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethods.Add(method);
            return new JsonObject { ["roots"] = new JsonArray() };
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Contains("roots/list", requestedMethods);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Index_WhenClientRootsHaveNoFileRoots_ReturnsError()
    {
        var requestedMethods = new List<string>();
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"capabilities":{"roots":{}}}}""")!);
        _server.ClientRequestHandlerForTests = (method, _) =>
        {
            requestedMethods.Add(method);
            return new JsonObject
            {
                ["roots"] = new JsonArray(new JsonObject { ["uri"] = "https://example.com/workspace" })
            };
        };
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"."}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Contains("roots/list", requestedMethods);
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("MCP client root", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_DuplicateSubmission_ReturnsDuplicate()
    {
        var uniqueDesc = $"Add support for Zig language {Guid.NewGuid():N}";
        JsonNode MakeRequest(int id) => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "language_support", ["description"] = uniqueDesc }
            }
        };

        _server.HandleMessage(MakeRequest(1));
        var response2 = _server.HandleMessage(MakeRequest(2))!;

        var structured = response2["result"]!["structuredContent"]!;
        Assert.Equal("duplicate", structured["status"]!.GetValue<string>());
        Assert.Equal("draft", structured["lifecycle_status"]!.GetValue<string>());
        Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(_dbPath)!), structured["cdidx_dir"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_UnwritableCdidxDir_ReturnsActionableError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dir = TestProjectHelper.CreateTempProject("cdidx_mcp_readonly");
        var originalMode = File.GetUnixFileMode(dir);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            using var server = new McpServer(Path.Combine(dir, "codeindex.db"), ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"other","description":"Permission probe regression"}}}""")!;

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var message = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("Cannot write to .cdidx directory", message);
            Assert.Contains(Path.GetFullPath(dir), message);
            Assert.Contains("check directory ownership, permissions, and read-only mounts", message);
        }
        finally
        {
            try { File.SetUnixFileMode(dir, originalMode); } catch { }
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void SuggestImprovement_WriteProbePreservesExistingProbeFile()
    {
        var cdidxDir = Path.GetDirectoryName(_dbPath)!;
        var existingProbe = Path.Combine(cdidxDir, ".write_probe");
        File.WriteAllText(existingProbe, "keep me");
        var uniqueDesc = $"Probe preservation regression {Guid.NewGuid():N}";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject
                {
                    ["category"] = "other",
                    ["description"] = uniqueDesc,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.Equal("keep me", File.ReadAllText(existingProbe));
    }

    [Fact]
    public void SuggestImprovement_InvalidCategory_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"invalid_category","description":"Some description"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Invalid category", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    // Regression pin for issue #1582: typo'd category should surface a "Did you mean: ..." hint and
    // expose machine-readable similar values via `result.data.similar_values` for MCP clients.
    // #1582 回帰テスト: タイポしたカテゴリは "Did you mean: ..." ヒントを返し、MCP クライアント向けに
    // `result.data.similar_values` で類似候補を構造化して提供する。
    [Fact]
    public void SuggestImprovement_InvalidCategoryTypo_ReturnsDidYouMeanWithSimilarValues_Issue1582()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"symbol_extractoin","description":"Some description"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Invalid category", text);
        Assert.Contains("Did you mean: symbol_extraction", text);

        var data = result["data"]!.AsObject();
        var similar = data["similar_values"]!.AsArray();
        Assert.Contains(similar, n => n!.GetValue<string>() == "symbol_extraction");
    }

    [Fact]
    public void SuggestImprovement_MissingDescription_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"other"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("description", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_SourceCodeInDescription_ReturnsError()
    {
        // Build the JSON with actual newlines in description so SourceCodeDetector sees code lines
        // SourceCodeDetector がコード行を認識するよう、description に実際の改行を含む JSON を構築
        var desc = "public void Foo()\n{\n    var x = 1;\n    var y = 2;\n    var z = x + y;\n    Console.WriteLine(z);\n}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "other", ["description"] = desc }
            }
        };
        var response = _server.HandleMessage(json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("source code", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_SourceCodeInContext_ReturnsError()
    {
        var ctx = "function foo() {\n    let x = 1;\n    let y = 2;\n    return x + y;\n}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "suggest_improvement",
                ["arguments"] = new JsonObject { ["category"] = "other", ["description"] = "Something is wrong", ["context"] = ctx }
            }
        };
        var response = _server.HandleMessage(json)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("source code", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void SuggestImprovement_BlockedInBatchQuery()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"suggest_improvement","arguments":{"category":"other","description":"test"}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        Assert.Contains("not allowed in batch_query", results[0]!["error"]!.GetValue<string>());
    }

    // Regression pins for issue #199: MCP tool handlers must normalize mixed-case lang/kind.
    // #199 回帰テスト: MCP ハンドラも --lang / --kind を大文字小文字なく扱うことを固定する。
    [Fact]
    public void ToolsCall_Symbols_AcceptsLangCsharpCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","lang":"CSharp"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("csharp", structuredUpper["lang"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","lang":"csharp"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Symbols_AcceptsKindClassCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","kind":"CLASS"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("class", structuredUpper["kind"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","kind":"class"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());

        // Prove the kind filter is actually applied, not silently dropped: the seeded "App" symbol
        // is a class, so querying it with kind=FUNCTION must return 0 regardless of casing.
        // kind フィルタが実際に適用されていることを確認: seed した App は class なので、
        // kind=FUNCTION での検索は大文字小文字に関わらず 0 件になるべき。
        var requestWrongKind = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","kind":"FUNCTION"}}}""")!;
        var responseWrongKind = _server.HandleMessage(requestWrongKind)!;
        var structuredWrongKind = responseWrongKind["result"]!["structuredContent"]!;
        Assert.Equal("function", structuredWrongKind["kind"]!.GetValue<string>());
        Assert.Equal(0, structuredWrongKind["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Definition_AcceptsLangCsharpCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","lang":"CSharp"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("csharp", structuredUpper["lang"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","lang":"csharp"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Definition_AcceptsKindClassCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","kind":"CLASS"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("class", structuredUpper["kind"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","kind":"class"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());

        // Prove the kind filter is actually applied, not silently echoed.
        // The shared fixture only seeds `App` as a class, so querying with kind:"FUNCTION"
        // must return 0 if the normalized kind is threaded through to GetDefinitions().
        // kind フィルタが捨てられずに実際に適用されていることを確認する。
        var requestWrongKind = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","kind":"FUNCTION"}}}""")!;
        var responseWrongKind = _server.HandleMessage(requestWrongKind)!;
        var structuredWrongKind = responseWrongKind["result"]!["structuredContent"]!;
        Assert.Equal("function", structuredWrongKind["kind"]!.GetValue<string>());
        Assert.Equal(0, structuredWrongKind["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ProcessFrameAsync_RequestTimeout_ReturnsStructuredTimeoutError()
    {
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(20),
        };
        server.RequestDelayForTests = _ =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(200));
            return Task.CompletedTask;
        };

        var responseText = await server.ProcessFrameAsync(
            """{"jsonrpc":"2.0","id":123,"method":"tools/call","params":{"name":"status"}}""");

        var response = JsonNode.Parse(responseText!)!;
        var error = response["error"]!;
        Assert.Equal(-32603, error["code"]!.GetValue<int>());
        Assert.Equal("Request timed out", error["message"]!.GetValue<string>());
        Assert.Equal("timeout", error["data"]!["reason"]!.GetValue<string>());
        Assert.True(error["data"]!["elapsed_ms"]!.GetValue<long>() >= 1);
        Assert.Equal("internal_error", error["data"]!["category"]!.GetValue<string>());
        Assert.True(error["data"]!["retry_safe"]!.GetValue<bool>());
        Assert.Equal(123, response["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task ProcessFrameAsync_BatchRequestTimeout_ReturnsStructuredTimeoutError()
    {
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false)
        {
            RequestTimeout = TimeSpan.FromMilliseconds(20),
        };
        server.RequestDelayForTests = _ =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(200));
            return Task.CompletedTask;
        };

        var responseText = await server.ProcessFrameAsync(
            """[{"jsonrpc":"2.0","id":123,"method":"tools/call","params":{"name":"status"}}]""");

        var response = JsonNode.Parse(responseText!)!.AsArray().Single()!;
        var error = response["error"]!;
        Assert.Equal(-32603, error["code"]!.GetValue<int>());
        Assert.Equal("Request timed out", error["message"]!.GetValue<string>());
        Assert.Equal("timeout", error["data"]!["reason"]!.GetValue<string>());
        Assert.Equal(123, response["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_StdioEofDrainsInFlightRequestBeforeReturning()
    {
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false);
        server.RequestRegisteredForTests = _ => { };
        var transport = new QueuedFrameTransport(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""");

        await server.RunAsync(transport, CancellationToken.None);

        Assert.Single(transport.WrittenFrames);
        var response = JsonNode.Parse(transport.WrittenFrames[0]!)!;
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Null(response["error"]);
    }

    private static string CreateLegacyDbWithoutIndexedAt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_legacy_{Guid.NewGuid():N}.db");
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL UNIQUE,
                    lang TEXT,
                    size INTEGER,
                    lines INTEGER,
                    modified DATETIME
                );
                CREATE TABLE symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id INTEGER NOT NULL,
                    name TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO files (path, lang, size, lines, modified)
                VALUES ('src/legacy.cs', 'csharp', 42, 3, '2026-01-01T00:00:00Z');
                """;
            insert.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        return dbPath;
    }

    private static string CreateSqlGraphContractFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/target.sql",
            "sql",
            """
            CREATE FUNCTION dbo.fn_Target()
            RETURNS INT
            AS
            BEGIN
                RETURN 1;
            END;
            GO
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/caller.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.usp_Caller
            AS
            BEGIN
                SELECT dbo.fn_Target();
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateMixedSqlGraphContractFixtureDb(string projectRoot)
    {
        var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/mixed.cs",
            "csharp",
            """
            public class MixedCalls
            {
                public void N() { }

                public void M()
                {
                    N();
                }
            }
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static string CreateSqlGraphContractZeroResultFixtureDb(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/a.cs",
            "csharp",
            """
            public class C
            {
                public void M() { }
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/b.sql",
            "sql",
            """
            CREATE PROCEDURE dbo.Target
            AS
            BEGIN
                SELECT 1;
            END;
            GO
            """);

        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkSqlGraphContractReady();
        return dbPath;
    }

    private static void DowngradeSqlGraphContractRows(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE symbol_references
            SET symbol_name = 'fn_Target',
                symbol_name_folded = 'fn_target',
                column_number = 1
            WHERE symbol_name = 'dbo.fn_Target';
            DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
            """;
        cmd.ExecuteNonQuery();
    }

    private static void DowngradeSqlGraphContractVersion(string dbPath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _server.Dispose();
        _db.Dispose();
        DeleteDbPath();
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }

    private void DeleteDbPath()
    {
        DeleteFileRobust(_dbPath);
    }

    private static void DeleteFileRobust(string path)
    {
        if (!File.Exists(path))
            return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            SqliteConnection.ClearAllPools();

            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void DropGraphExactFallbackIndexes()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbol_refs_name_nocase;
            DROP INDEX IF EXISTS idx_symbol_refs_container_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void DropSymbolExactFallbackIndex()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_symbols_name_nocase;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private void ForceLegacyExactFallbackMode()
    {
        using var db = new DbContext(_dbPath);
        db.ClearReadyFlags();
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
    }

    // --- Rate limiter (issue #1560) / レート制限器（#1560） ---

    private sealed class FixedClock
    {
        public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
    }

    private static FixedClock InstallRateLimiter(McpServer server, RateLimiterOptions options)
    {
        var clock = new FixedClock();
        server.OverrideRateLimiterForTests(new RateLimiter(options, clock.Read));
        return clock;
    }

    [Fact]
    public void ToolsCall_RateLimitDisabled_NoThrottle()
    {
        // Default (no env vars) must behave like the pre-#1560 server so existing stdio
        // single-user sessions are unaffected. The limiter still records nothing on every
        // call regardless of how many succeed.
        // 既定（環境変数なし）では #1560 以前と同じ挙動で、stdio 単一ユーザーは影響を受けない。
        for (var i = 0; i < 5; i++)
        {
            var request = JsonNode.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"tools/call\",\"params\":{{\"name\":\"status\"}}}}")!;
            var response = _server.HandleMessage(request)!;
            Assert.Null(response["error"]);
        }
    }

    [Fact]
    public void ToolsCall_RateLimited_ReturnsStructuredNegative32000()
    {
        // Bucket of capacity 1, refilling at 1/sec. First call succeeds, second is denied
        // with -32000 carrying tool / caller / retry_after_ms (#1560 contract).
        // 容量 1・補充 1/sec のバケット。1 回目は成功、2 回目は -32000 で tool/caller/retry_after_ms
        // を含む構造化レスポンスになる（#1560 の契約）。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });

        var initialize = JsonNode.Parse("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"client-a","version":"1.2.3"}}}""")!;
        _server.HandleMessage(initialize);

        var first = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;
        Assert.Null(first["error"]);
        Assert.False(first["result"]!["isError"]?.GetValue<bool>() ?? false);

        var second = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;

        Assert.Null(second["result"]);
        var error = second["error"]!;
        Assert.Equal(-32000, error["code"]!.GetValue<int>());
        Assert.Contains("Rate limit exceeded", error["message"]!.GetValue<string>());
        var data = error["data"]!;
        Assert.Equal("rate_limited", data["error_category"]!.GetValue<string>());
        Assert.Equal("status", data["tool"]!.GetValue<string>());
        Assert.Equal("client-a/1.2.3", data["caller"]!.GetValue<string>());
        Assert.True(data["retry_after_ms"]!.GetValue<long>() >= 1);
        Assert.Equal(2, second["id"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_RateLimit_KeysByTool()
    {
        // Different tools have independent buckets, so once `status` is throttled the
        // sibling tool `languages` still goes through (#1560).
        // 別ツールは独立バケットを持つため、`status` がスロットルされても `languages` は通る（#1560）。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });

        Assert.Null(_server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!["error"]);
        Assert.NotNull(_server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!["error"]);

        var languages = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"languages"}}""")!)!;
        Assert.Null(languages["error"]);
    }

    [Fact]
    public void Initialize_CapturesClientInfoAsCallerIdentity()
    {
        // The caller identity is read from `clientInfo.name` on `initialize` so the
        // limiter can attribute / throttle per client. Missing `clientInfo` falls back to
        // `"unknown"` so anonymous clients still get a coherent bucket (#1560).
        // `clientInfo.name` を取り込むことで、クライアント単位の計量・スロットルが効く。
        // `clientInfo` が無い場合は `"unknown"` に fallback する（#1560）。
        Assert.Equal("unknown", _server.CurrentCaller);

        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"my-client","version":"1.2.3"}}}""")!);
        Assert.Equal("my-client/1.2.3", _server.CurrentCaller);
    }

    [Fact]
    public void Initialize_UpgradesFromUnknownToNamedCaller()
    {
        // The first initialize() with a named clientInfo upgrades the caller out of the
        // anonymous `"unknown"` bucket, so subsequent calls are throttled per client
        // rather than under a shared anonymous bucket (#1560).
        // 最初の名前付き initialize で `"unknown"` から昇格し、以降は client 単位で計量される。
        Assert.Equal("unknown", _server.CurrentCaller);

        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"named-client"}}}""")!);
        Assert.Equal("named-client", _server.CurrentCaller);
    }

    [Fact]
    public void Initialize_NamedCallerIsSticky_RejectsReIdentifySwap()
    {
        // Once a named caller has been captured, re-initialize() under a *different* name
        // is ignored so a networked MCP session cannot reset its rate-limit bucket simply
        // by re-initializing under a fresh identity. The retained name continues to key
        // all subsequent (tool, caller) buckets (#1560 DoS vector).
        // 名前付き caller の取得後は、別名での再 initialize() を無視し、レート制限バケットを
        // リセットする経路を塞ぐ（#1560 DoS）。
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"first-client"}}}""")!);
        Assert.Equal("first-client", _server.CurrentCaller);

        // Re-init under a different name is ignored / 別名は無視
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"clientInfo":{"name":"second-client"}}}""")!);
        Assert.Equal("first-client", _server.CurrentCaller);

        // Re-init with empty clientInfo (resolves to "unknown") also cannot downgrade /
        // 空の clientInfo（"unknown" に解決）でも降格しない。
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"initialize","params":{}}""")!);
        Assert.Equal("first-client", _server.CurrentCaller);
    }

    [Fact]
    public void BuildCallerSwapRejectionLog_IsActionable()
    {
        var log = McpServer.BuildCallerSwapRejectionLog("first-client", "second-client");
        Assert.Contains("Ignoring re-initialize", log);
        Assert.Contains("first-client", log);
        Assert.Contains("second-client", log);
    }

    [Fact]
    public void BuildCallerSwapRejectionLog_TruncatesClientInfoIdentities_Issue3120()
    {
        var current = new string('c', McpBoundedText.MaxClientIdentityChars + 25);
        var attempted = new string('a', McpBoundedText.MaxClientIdentityChars + 25);
        var currentDisplay = McpBoundedText.ForDisplay(current, McpBoundedText.MaxClientIdentityChars);
        var attemptedDisplay = McpBoundedText.ForDisplay(attempted, McpBoundedText.MaxClientIdentityChars);

        var log = McpServer.BuildCallerSwapRejectionLog(current, attempted);

        Assert.DoesNotContain(current, log, StringComparison.Ordinal);
        Assert.DoesNotContain(attempted, log, StringComparison.Ordinal);
        Assert.Contains(currentDisplay.Text, log);
        Assert.Contains(attemptedDisplay.Text, log);
    }

    [Fact]
    public void BatchQuery_RejectsNestedBatchQuerySlots()
    {
        // batch_query slots that themselves request `batch_query` are rejected before
        // rate-limit token consumption so the per-(tool, caller) bucket cannot be drained
        // by recursive expansion, and the error message names the constraint explicitly
        // instead of bubbling up the generic "Unknown tool" error (#1560 nesting vector).
        // 内側で batch_query を呼ぶスロットは、トークン消費の前に明示的に拒否し、再帰展開で
        // バケットを枯渇させる経路を塞ぐ。エラーメッセージもネスト禁止を明示する（#1560）。
        var request = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[
            {"tool":"batch_query","args":{"queries":[]}}
        ]}}}
        """)!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        var nested = results[0]!;
        Assert.Equal("batch_query cannot be nested inside batch_query.", nested["error"]!.GetValue<string>());
        Assert.Null(nested["error_category"]);
    }

    [Fact]
    public void BatchQuery_PerSlotRateLimited_MarksOnlyOverQuotaSlots()
    {
        // batch_query mitigation: each inner slot also consumes a token from the
        // (inner-tool, caller) bucket, so a misbehaving client cannot bypass the limiter
        // by stuffing 10 inner `search` calls into a single allowed batch (#1560 evidence).
        // Outer `batch_query` and inner `status` have independent buckets keyed by tool;
        // burst=2 lets the outer call through and the first two inner `status` slots, while
        // the third inner slot is throttled and surfaces error_category=rate_limited +
        // retry_after_ms in the per-slot result.
        // batch_query の対策: 内側スロットも (inner-tool, caller) からトークンを消費するため、
        // 内側 search を 10 個詰めて制限を迂回できない。バケットはツール毎に独立しており、
        // burst=2 なら外側 batch_query と 1〜2 個目の内側 status が通り、3 個目はスロット単位で
        // error_category=rate_limited と retry_after_ms を返す。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 0.1, BurstCapacity = 2.0 });

        var request = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[
            {"tool":"status"},
            {"tool":"status"},
            {"tool":"status"}
        ]}}}
        """)!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);

        Assert.Null(results[0]!["error"]);
        Assert.Null(results[1]!["error"]);

        var throttled = results[2]!;
        Assert.NotNull(throttled["error"]);
        Assert.Equal("rate_limited", throttled["error_category"]!.GetValue<string>());
        Assert.True(throttled["retry_after_ms"]!.GetValue<long>() >= 1);

        var metadata = structured["metadata"]!;
        Assert.Equal(2, metadata["success_count"]!.GetValue<int>());
        Assert.Equal(1, metadata["failure_count"]!.GetValue<int>());
    }

    [Fact]
    public void BuildRateLimitedLog_IsActionable()
    {
        var log = McpServer.BuildRateLimitedLog("search", "client-a", 250);
        Assert.Contains("Rate limit exceeded", log);
        Assert.Contains("search", log);
        Assert.Contains("client-a", log);
        Assert.Contains("250", log);
        Assert.Contains("CDIDX_MCP_RATE_LIMIT_RPS", log);
    }

    // --- Structured error envelope (#1581) / 構造化エラー envelope（#1581） ---

    private static void AssertEnvelope(JsonNode? data, string expectedCategory, bool expectedRetrySafe)
    {
        Assert.NotNull(data);
        Assert.Equal(expectedCategory, data!["category"]!.GetValue<string>());
        Assert.Equal(expectedRetrySafe, data["retry_safe"]!.GetValue<bool>());
        var suggestion = data["suggestion"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(suggestion));
    }

    [Fact]
    public void ErrorResponse_InvalidRequest_NotAnObject_CarriesEnvelope()
    {
        // #1581: every JSON-RPC error response must carry `data.{category, suggestion, retry_safe}`.
        // Sending a non-object JSON value (here, an array) trips the `-32600` branch. Clients
        // should be able to branch on `data.category == "invalid_request"` instead of the
        // free-text `message`.
        // #1581: すべての JSON-RPC エラー応答は `data.{category, suggestion, retry_safe}` を
        // 含む。`-32600` 経路（非オブジェクト）でも canonical envelope を返す。
        var response = _server.HandleMessage(JsonNode.Parse("[]")!)!;
        var error = response["error"]!;
        Assert.Equal(-32600, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "invalid_request", expectedRetrySafe: false);
    }

    [Fact]
    public void ErrorResponse_MethodNotFound_CarriesEnvelope()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"no/such/method"}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32601, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "method_not_found", expectedRetrySafe: false);
    }

    [Fact]
    public void ErrorResponse_MissingToolName_CarriesEnvelope()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "missing_parameter", expectedRetrySafe: false);
    }

    [Fact]
    public void ErrorResponse_UnknownTool_CarriesEnvelopeAndToolName()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"does_not_exist"}}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "tool_unknown", expectedRetrySafe: false);
        Assert.Equal("does_not_exist", error["data"]!["tool"]!.GetValue<string>());
    }

    [Fact]
    public void ErrorResponse_ToolDisabled_CarriesEnvelope()
    {
        // Tool-disabled (#1561) keeps the -32601 wire code but adds the #1581 envelope so
        // clients can distinguish operator-disabled tools from typos (tool_unknown).
        // tool_disabled（#1561）はワイヤコード -32601 を維持しつつ envelope を併載し、
        // typo（tool_unknown）と区別できるようにする。
        Environment.SetEnvironmentVariable("CDIDX_MCP_TOOLS_DENY", "status");
        try
        {
            using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
            var response = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;
            var error = response["error"]!;
            Assert.Equal(-32601, error["code"]!.GetValue<int>());
            AssertEnvelope(error["data"], "tool_disabled", expectedRetrySafe: false);
            Assert.Equal("status", error["data"]!["tool"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_MCP_TOOLS_DENY", null);
        }
    }

    [Fact]
    public void ErrorResponse_Unauthorized_CarriesEnvelope()
    {
        // Auth failures use server code -32001 with `permission_denied`; the wire message
        // stays generic so a token-protected server does not leak internals to unauth callers.
        // 認証失敗はサーバーコード -32001 と permission_denied で返し、生メッセージは汎用に保つ。
        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: false,
            new TokenMcpAuthenticator("secret-token"));
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")!)!;
        var error = response["error"]!;
        Assert.Equal(-32001, error["code"]!.GetValue<int>());
        AssertEnvelope(error["data"], "permission_denied", expectedRetrySafe: false);
    }

    [Fact]
    public void RateLimited_Error_AlsoCarriesCanonicalEnvelope()
    {
        // The pre-existing #1560 fields (`error_category`, `tool`, `caller`, `retry_after_ms`)
        // stay intact for backward compatibility; #1581 adds `category`, `suggestion`, and
        // `retry_safe` (true — back off and retry) under the same `error.data` object.
        // #1560 既存フィールドは維持しつつ、#1581 の canonical envelope を併載する。
        // rate_limited は retry_safe=true（バックオフして再試行）。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });
        var initialize = JsonNode.Parse("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"c","version":"1"}}}""")!;
        _server.HandleMessage(initialize);
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!);
        var throttled = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var data = throttled["error"]!["data"]!;
        // Legacy fields preserved / 既存フィールド維持
        Assert.Equal("rate_limited", data["error_category"]!.GetValue<string>());
        Assert.Equal("status", data["tool"]!.GetValue<string>());
        // Canonical envelope added / canonical envelope を併載
        AssertEnvelope(data, "rate_limited", expectedRetrySafe: true);
    }

    [Fact]
    public void RateLimited_ErrorAndLog_TruncatesCallerIdentity_Issue3120()
    {
        var caller = new string('r', McpBoundedText.MaxClientIdentityChars + 25);
        var display = McpBoundedText.ForDisplay(caller, McpBoundedText.MaxClientIdentityChars);

        var response = McpServer.CreateRateLimitedErrorResponse(null, "status", caller, retryAfterMs: 123);
        var log = McpServer.BuildRateLimitedLog("status", caller, retryAfterMs: 123);

        Assert.DoesNotContain(caller, response.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(caller, log, StringComparison.Ordinal);
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["caller"]!.GetValue<string>());
        Assert.Equal(caller.Length, data["caller_length"]!.GetValue<int>());
        Assert.True(data["caller_truncated"]!.GetValue<bool>());
        Assert.Contains(display.Text, log);
    }

    [Fact]
    public void ToolResult_DatabaseMissing_CarriesEnvelopeOnStructuredContent()
    {
        // Tool-result errors (MCP isError shape) mirror the JSON-RPC envelope by exposing
        // `category` / `suggestion` / `retry_safe` under `result.structuredContent`. The
        // `index_missing` category is retry_safe so clients can rebuild and retry.
        // ツール結果エラー（MCP isError 形式）も `result.structuredContent` に envelope を載せる。
        // index_missing は retry_safe=true（rebuild 後に再試行可能）。
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_test_missing_{Guid.NewGuid():N}.db");
        using var server = new McpServer(missingDb, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        Assert.Null(response["error"]);
        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        AssertEnvelope(result["structuredContent"], "index_missing", expectedRetrySafe: true);
    }

    [Fact]
    public void ClassifyException_MapsCancelledToRequestCancelled()
    {
        var c = McpErrorEnvelope.ClassifyException(new OperationCanceledException());
        Assert.Equal("request_cancelled", c.Category);
        Assert.True(c.RetrySafe);
        Assert.Equal(McpErrorEnvelope.CodeRequestCancelled, c.JsonRpcCode);
    }

    [Fact]
    public void ClassifyException_MapsSqliteSchemaErrorsToIndexStale()
    {
        // SqliteException whose message names a missing table/column maps to `index_stale`
        // so clients know `cdidx index --rebuild` is the path to recovery (retry_safe=true).
        // テーブル / カラム不在を訴える SqliteException は index_stale にマッピングし、
        // rebuild で復旧可能（retry_safe=true）であることをクライアントに伝える。
        SqliteException sqlite;
        try
        {
            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM no_such_table_xyz";
            cmd.ExecuteReader();
            throw new InvalidOperationException("expected SqliteException");
        }
        catch (SqliteException ex)
        {
            sqlite = ex;
        }

        var c = McpErrorEnvelope.ClassifyException(sqlite);
        Assert.Equal("index_stale", c.Category);
        Assert.True(c.RetrySafe);
        Assert.Equal(McpErrorEnvelope.CodeIndexStale, c.JsonRpcCode);
    }

    [Fact]
    public void ClassifyException_DefaultsToInternalError()
    {
        var c = McpErrorEnvelope.ClassifyException(new InvalidOperationException("boom"));
        Assert.Equal("internal_error", c.Category);
        Assert.False(c.RetrySafe);
        Assert.Equal(-32603, c.JsonRpcCode);
    }

    [Fact]
    public void ErrorResponse_ProcessFrame_ParseError_CarriesEnvelope()
    {
        // ProcessFrame returns a serialized response string; parse it back to inspect the
        // envelope. Parse-error responses have `id: null` per JSON-RPC spec.
        // ProcessFrame は文字列を返すので、パースして envelope を検査する。Parse error は
        // 仕様により `id: null`。
        var raw = _server.ProcessFrame("not a json frame");
        Assert.NotNull(raw);
        var response = JsonNode.Parse(raw!)!;
        var error = response["error"]!;
        Assert.Equal(-32700, error["code"]!.GetValue<int>());
        AssertJsonNullId(response);
        AssertEnvelope(error["data"], "parse_error", expectedRetrySafe: false);
    }

    [Fact]
    public void ProcessFrame_OversizedUtf8Bytes_ReturnsParseErrorWithNullId()
    {
        var multibyte = new string('\u3042', (McpServer.MaxLineByteLength / 3) + 1);
        var raw = _server.ProcessFrame("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"q\":\"" + multibyte + "\"}}");

        var response = JsonNode.Parse(raw!)!;
        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("message_too_large", response["error"]!["data"]!["category"]!.GetValue<string>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void ProcessFrame_TooDeepJson_ReturnsParseErrorWithNullId()
    {
        var raw = _server.ProcessFrame(new string('[', McpServer.MaxJsonDepth + 2) + "0" + new string(']', McpServer.MaxJsonDepth + 2));

        var response = JsonNode.Parse(raw!)!;
        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("parse_error", response["error"]!["data"]!["category"]!.GetValue<string>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void IsServerResponseFrame_TooDeepResponse_ReturnsFalse_Issue3012()
    {
        Assert.True(InvokeIsServerResponseFrame("""{"jsonrpc":"2.0","id":1,"result":{}}"""));

        var frame = BuildNestedJsonRpcResponse(McpServer.MaxJsonDepth + 1);

        Assert.False(InvokeIsServerResponseFrame(frame));
    }

    [Fact]
    public void HandleMessage_BatchMixedRequests_ReturnsResponseArray()
    {
        var batch = JsonNode.Parse("""[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"nope"}]""")!;

        var response = _server.HandleMessage(batch)!.AsArray();

        Assert.Equal(2, response.Count);
        Assert.Equal(1, response[0]!["id"]!.GetValue<int>());
        Assert.NotNull(response[0]!["result"]);
        Assert.Equal(2, response[1]!["id"]!.GetValue<int>());
        Assert.Equal(-32601, response[1]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void HandleMessage_AllNotificationBatch_ReturnsNull()
    {
        var batch = JsonNode.Parse("""[{"jsonrpc":"2.0","method":"notifications/initialized"}]""")!;

        Assert.Null(_server.HandleMessage(batch));
    }

    [Fact]
    public void HandleMessage_EmptyBatch_ReturnsInvalidRequest()
    {
        var response = _server.HandleMessage(JsonNode.Parse("[]")!)!;

        Assert.Equal(-32600, response["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response);
    }

    [Fact]
    public void HandleMessage_NestedBatchItem_ReturnsInvalidRequestInBatchResponse()
    {
        var response = _server.HandleMessage(JsonNode.Parse("""[[{"jsonrpc":"2.0","id":1,"method":"ping"}]]""")!)!.AsArray();

        Assert.Single(response);
        Assert.Equal(-32600, response[0]!["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response[0]!);
    }

    [Fact]
    public void HandleMessage_ScalarBatchItem_ReturnsInvalidRequestWithNullId()
    {
        var response = _server.HandleMessage(JsonNode.Parse("""[1]""")!)!.AsArray();

        Assert.Single(response);
        Assert.Equal(-32600, response[0]!["error"]!["code"]!.GetValue<int>());
        AssertJsonNullId(response[0]!);
    }

    [Fact]
    public void BuildData_ExtraDataCannotShadowCanonicalKeys()
    {
        // Defense-in-depth: if a category-specific call passes `extraData` with the same key
        // names, the canonical contract still wins so clients always see a coherent envelope.
        // canonical キー（category / suggestion / retry_safe）は extraData で上書きできない。
        var extra = new JsonObject
        {
            ["category"] = "spoofed",
            ["suggestion"] = "spoofed",
            ["retry_safe"] = true,
            ["tool"] = "search",
        };
        var data = McpErrorEnvelope.BuildData("invalid_argument", "real suggestion", retrySafe: false, extra);
        Assert.Equal("invalid_argument", data["category"]!.GetValue<string>());
        Assert.Equal("real suggestion", data["suggestion"]!.GetValue<string>());
        Assert.False(data["retry_safe"]!.GetValue<bool>());
        Assert.Equal("search", data["tool"]!.GetValue<string>());
    }

    private static void AssertJsonNullId(JsonNode node)
    {
        var obj = Assert.IsType<JsonObject>(node);
        Assert.True(obj.ContainsKey("id"));
        Assert.Null(obj["id"]);
    }

    private static bool InvokeIsServerResponseFrame(string frame)
    {
        var method = typeof(McpServer).GetMethod("IsServerResponseFrame", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [frame])!;
    }

    private static string BuildNestedJsonRpcResponse(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"jsonrpc":"2.0","id":1,"result":""");
        AppendNestedObject(builder, nestedObjectCount);
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendNestedObject(StringBuilder builder, int nestedObjectCount)
    {
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append("""{"next":""");

        builder.Append('0');

        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append('}');
    }

    private static void WriteOversizedAsciiFile(string path)
    {
        const int targetBytes = 10 * 1024 * 1024 + 1;
        var chunk = new byte[8192];
        Array.Fill(chunk, (byte)'a');

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        int written = 0;
        while (written < targetBytes)
        {
            var toWrite = Math.Min(chunk.Length, targetBytes - written);
            stream.Write(chunk, 0, toWrite);
            written += toWrite;
        }
    }

    // Issue #1573: the MCP loop used to block on stdin until EOF with no signal-driven exit
    // path, so SIGINT/SIGTERM left the process hung. The fix wires CancellationToken through to
    // the transport's ReadFrameAsync; this test pins that contract by tripping the token while
    // ReadFrameAsync is blocked and asserting the loop drains cleanly and disposes the transport.
    // #1573: 旧実装は stdin EOF まで固まり、SIGINT/SIGTERM で吊り下がっていた。修正で
    // CancellationToken が ReadFrameAsync まで届くようになったため、ブロック中にトークンを
    // トリップしたときループが正常終了し transport が dispose されることを固定するテスト。
    [Fact]
    public async Task RunAsync_CancellationDrainsLoopAndDisposesTransport()
    {
        var transport = new CancellableFakeTransport();
        using var cts = new CancellationTokenSource();

        var runTask = _server.RunAsync(transport, cts.Token);

        await transport.WaitForReadAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        // The loop must observe cancellation and return on its own — without the fix this awaits
        // forever because ReadLineAsync ignored the (then-non-existent) token.
        // 修正前は ReadLineAsync が token を見ていないため永遠にブロックした。完了することを確認。
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(runTask.IsCompletedSuccessfully, "RunAsync should exit cleanly when its CancellationToken is cancelled.");
        Assert.True(transport.ReadCalls >= 1);
    }

    // Cancellation must also unblock readers that were already pending before the signal arrived
    // and stop the loop without producing a spurious WriteFrameAsync call (which would otherwise
    // be observable as a half-completed request on the wire).
    // 信号到着前から待機中の reader も解除し、ループが余分な WriteFrameAsync を出さずに終了することを確認。
    [Fact]
    public async Task RunAsync_CancelledBeforeAnyResponseDoesNotWriteFrame()
    {
        var transport = new CancellableFakeTransport();
        using var cts = new CancellationTokenSource();

        var runTask = _server.RunAsync(transport, cts.Token);
        await transport.WaitForReadAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, transport.WriteCalls);
    }

    private sealed class QueuedFrameTransport : IMcpTransport
    {
        private readonly Queue<string?> _frames;

        public QueuedFrameTransport(params string[] frames)
        {
            _frames = new Queue<string?>(frames.Cast<string?>().Append(null));
        }

        public string Name => "stdio";
        public string Endpoint => "memory://queued";
        public List<string?> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
            => Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// In-memory IMcpTransport whose ReadFrameAsync blocks until the supplied CancellationToken
    /// trips. Records read/write counts so tests can assert the loop actually entered the read
    /// before cancellation arrived (and never wrote a response after).
    /// CancellationToken がトリップするまで ReadFrameAsync をブロックするインメモリ実装。
    /// ループが read に入ってからキャンセルが来たことと、その後 write が発生していないことを検証する。
    /// </summary>
    private sealed class CancellableFakeTransport : IMcpTransport
    {
        private readonly TaskCompletionSource _readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "fake";
        public string Endpoint => "memory://test";
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            ReadCalls++;
            _readEntered.TrySetResult();
            // Honour the token: a never-completing Task<string?> + token-driven cancellation
            // mirrors how stdio's ReadLineAsync(CancellationToken) behaves on SIGINT/SIGTERM.
            // token に従って待機。stdio の ReadLineAsync(CancellationToken) と同じ動作を再現する。
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return Task.CompletedTask;
        }

        public Task WaitForReadAsync(TimeSpan timeout) => _readEntered.Task.WaitAsync(timeout);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class QueueMcpTransport : IMcpTransport, IOutOfBandMcpTransport
    {
        private readonly Queue<string> _frames;

        public QueueMcpTransport(params string[] frames)
        {
            _frames = new Queue<string>(frames);
        }

        public string Name => "memory";
        public string Endpoint => "memory://test";
        public List<string> WrittenFrames { get; } = [];

        public Task<string?> ReadFrameAsync(CancellationToken cancellationToken)
            => Task.FromResult(_frames.Count == 0 ? null : _frames.Dequeue());

        public Task WriteFrameAsync(string? frame, CancellationToken cancellationToken)
        {
            if (frame is not null)
                WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public Task WriteOutOfBandFrameAsync(string frame, CancellationToken cancellationToken)
        {
            WrittenFrames.Add(frame);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // The shutdown helper is the heart of the #1573 fix: cancelling the CTS through Console.CancelKeyPress
    // (and PosixSignal.SIGTERM on Unix) must trip the loop. This test exercises the cross-platform
    // Ctrl+C path by raising the .NET CancelKeyPress event directly via reflection — the test cannot
    // send real signals to the test process without crashing the xUnit runner.
    // #1573 の中核。Console.CancelKeyPress 経由 (Unix では PosixSignal.SIGTERM 経由) でループを
    // 止められることが要件。実信号は xUnit runner ごと落とすため、リフレクションで CancelKeyPress
    // を直接発火させてクロスプラットフォーム経路を検証する。
    [Fact]
    public void RegisterShutdownHandlers_ConsoleCancelKeyPress_CancelsToken()
    {
        using var cts = new CancellationTokenSource();
        using var registration = McpServer.RegisterShutdownHandlers(cts);

        Assert.False(cts.IsCancellationRequested);
        RaiseConsoleCancelKeyPress();
        Assert.True(cts.IsCancellationRequested);
    }

    // After the registration is disposed, a subsequent Ctrl+C must not touch a stale CTS — the
    // typical RunMcpHttp shape disposes the CTS right after the registration, so a late signal
    // would otherwise hit ObjectDisposedException and crash the host.
    // registration を dispose した後の Ctrl+C は使用済み CTS に触れてはならない。RunMcpHttp は
    // registration の直後に CTS を dispose するため、late signal で ObjectDisposedException で
    // host が落ちないことを担保する。
    [Fact]
    public void RegisterShutdownHandlers_AfterDispose_DoesNotInvokeHandler()
    {
        using var cts = new CancellationTokenSource();
        var registration = McpServer.RegisterShutdownHandlers(cts);
        registration.Dispose();

        RaiseConsoleCancelKeyPress();

        Assert.False(cts.IsCancellationRequested);
    }

    private string CallToolAndReadErrorMessage(string toolName, JsonObject arguments)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            }
        };
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        return response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void RaiseConsoleCancelKeyPress()
    {
        // Console.CancelKeyPress is exposed as a public event but its backing delegate field is
        // private. Reflection is the only test-time path; .NET intentionally does not let user
        // code synthesise ConsoleCancelEventArgs (its constructor is internal). We construct the
        // args via the same internal ctor the runtime uses for real Ctrl+C events. The backing
        // field is null when no handlers are attached — that itself proves the handler was
        // removed, so callers must not assume a non-null delegate after dispose.
        // Console.CancelKeyPress は public event だが backing field は private で、
        // ConsoleCancelEventArgs の ctor も internal。reflection が唯一のテスト経路。
        // ハンドラ未登録の状態ではフィールドは null になり、それ自体が解除済みの証拠なので、
        // 呼び出し側は dispose 後に non-null を仮定してはならない。
        var field = typeof(Console).GetField("s_cancelCallbacks", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var del = (ConsoleCancelEventHandler?)field!.GetValue(null);
        if (del == null)
            return;
        var argsType = typeof(ConsoleCancelEventArgs);
        var argsCtor = argsType.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, binder: null, types: new[] { typeof(ConsoleSpecialKey) }, modifiers: null);
        Assert.NotNull(argsCtor);
        var args = (ConsoleCancelEventArgs)argsCtor!.Invoke(new object[] { ConsoleSpecialKey.ControlC });
        del!.Invoke(null!, args);
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(char value) =>
            throw new IOException("pipe closed");

        public override Task WriteAsync(string? value) =>
            throw new IOException("pipe closed");

        public override Task WriteLineAsync(string? value) =>
            throw new IOException("pipe closed");
    }

    private sealed class AssertingTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly Action _beforeWrite;

        public AssertingTextWriter(TextWriter inner, Action beforeWrite)
        {
            _inner = inner;
            _beforeWrite = beforeWrite;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override async Task WriteAsync(string? value)
        {
            _beforeWrite();
            await _inner.WriteAsync(value).ConfigureAwait(false);
        }

        public override async Task WriteAsync(char value)
        {
            _beforeWrite();
            await _inner.WriteAsync(value).ConfigureAwait(false);
        }

        public override Task FlushAsync() => _inner.FlushAsync();
    }

    private sealed class FlushCountingStream : MemoryStream
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
