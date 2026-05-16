using System.Text.Json.Nodes;
using System.Text.Json;
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
            Modified = new DateTime(2024, 1, 1),
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
        }]);

        _server = new McpServer(_dbPath, ConsoleUi.LoadVersion());
    }

    private void InsertIndexedFile(string path, string lang, string content)
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
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
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

    // --- Protocol tests / プロトコルテスト ---

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

    [Fact]
    public void BuildOversizedMessageLog_IsActionable()
    {
        var message = McpServer.BuildOversizedMessageLog(1_234_567);

        Assert.Contains("Message too large", message);
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

        var message = McpServer.BuildSanitizedToolErrorMessage("search", ex);

        Assert.Contains("Error executing search", message);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.Contains("server stderr", message);
        Assert.DoesNotContain("SECRET_LITERAL", message);
        Assert.DoesNotContain("syntax error", message);
    }

    [Fact]
    public void BuildSanitizedLoopErrorMessage_OmitsExceptionMessage()
    {
        // Same protection as BuildSanitizedToolErrorMessage but for the
        // outer JSON-RPC loop catch-all (#1530).
        var ex = new InvalidOperationException("PRAGMA failed: secret table 'leaky_table' missing");

        var message = McpServer.BuildSanitizedLoopErrorMessage(ex);

        Assert.Contains("Internal error", message);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.Contains("server stderr", message);
        Assert.DoesNotContain("leaky_table", message);
        Assert.DoesNotContain("PRAGMA failed", message);
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
    public void ToolsList_CallersCalleesAnalyzeSymbolDescriptions_PinCamelCaseMixedKindFields()
    {
        // #501 round 2: MCP tool descriptions must advertise the response fields in MCP camelCase
        // (`referenceKind`, `referenceKinds`, `hasMixedReferenceKinds`) because MCP serializes with
        // `JsonNamingPolicy.CamelCase`. This test pins those field names so a future edit that
        // accidentally switches back to CLI snake_case is caught before it reaches MCP consumers.
        // #501 round 2: MCP は `JsonNamingPolicy.CamelCase` でシリアライズするため、ツール説明も
        // camelCase（`referenceKind` / `referenceKinds` / `hasMixedReferenceKinds`）で書く必要がある。
        // 将来の編集で CLI snake_case に戻してしまう silent regression をこのテストで止める。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "callers", "callees", "analyze_symbol" })
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var description = tool["description"]!.GetValue<string>();

            Assert.Contains("referenceKind", description);
            Assert.Contains("referenceKinds", description);
            Assert.Contains("hasMixedReferenceKinds", description);
            Assert.DoesNotContain("reference_kinds", description);
            Assert.DoesNotContain("has_mixed_reference_kinds", description);
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
        Assert.NotNull(response["result"]!["structuredContent"]!["nearbySymbols"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["callers"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["callees"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspaceIndexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspaceLatestModified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["projectRoot"]);
        Assert.True(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Heading","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graphLanguage"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
        Assert.Contains("Use search, definition, excerpt, or files instead.", response["result"]!["structuredContent"]!["graphSupportReason"]!.GetValue<string>());
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
        Assert.Equal("subscribe", callee["referenceKind"]!.GetValue<string>());
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
        Assert.Equal("csharp", structured["graphLanguage"]!.GetValue<string>());
        Assert.True(structured["graphSupported"]!.GetValue<bool>());
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
        var nearbyPaths = structured["nearbySymbols"]!
            .AsArray()
            .Select(symbol => symbol?["path"]?.GetValue<string>())
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal("web/app.js", structured["file"]!["path"]!.GetValue<string>());
        Assert.Equal("javascript", structured["graphLanguage"]!.GetValue<string>());
        Assert.True(structured["graphSupported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Contains("web/app.js", nearbyPaths);
        Assert.DoesNotContain("src/status.cs", nearbyPaths);
        Assert.Contains(structured["nearbySymbols"]!.AsArray(),
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
        Assert.Equal("subscribe", row["referenceKind"]!.GetValue<string>());
        Assert.False(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "subscribe" }, kinds);
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
        Assert.Equal(new[] { "call", "subscribe" }, kinds);
        // #501: preferred summary (`instantiate` > `subscribe` > `MIN(call)`) is `subscribe` for the mixed call+subscribe set
        // #501: 混在時の preferred 要約（`instantiate` > `subscribe` > `MIN(call)`）は `subscribe`
        Assert.Equal("subscribe", row["referenceKind"]!.GetValue<string>());
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
        Assert.Equal("subscribe", caller["referenceKind"]!.GetValue<string>());
        Assert.True(caller["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = caller["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "call", "subscribe" }, kinds);
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
        Assert.Equal("subscribe", row["referenceKind"]!.GetValue<string>());
        // #501: callees rows stay split per kind so referenceKinds is a single-element array and hasMixedReferenceKinds is false
        // #501: callees 行は kind 単位で分かれるため referenceKinds は単要素、hasMixedReferenceKinds は false
        Assert.False(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "subscribe" }, kinds);
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

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graphLanguage"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graphSupportReason"]!.GetValue<string>());
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
        Assert.False(response["result"]!["structuredContent"]!["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degradedReason"]!.GetValue<string>());
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

        Assert.Equal("Found 1 references.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("Shade", structured["results"]![0]!["containerName"]!.GetValue<string>());
        Assert.True(structured["graphSupported"]!.GetValue<bool>());
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

        Assert.True(structured["graphSupported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graphLanguage"]!.GetValue<string>());
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
        Assert.Equal("javascript", structured["graphLanguage"]!.GetValue<string>());
        Assert.True(structured["graphSupported"]!.GetValue<bool>());
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
        Assert.Equal("csharp", structured["graphLanguage"]!.GetValue<string>());
        Assert.True(structured["graphSupported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("Value", structured["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Found 1 callers.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
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

        Assert.True(structured["graphSupported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graphLanguage"]!.GetValue<string>());
        Assert.Equal("Use", structured["results"]![0]!["callerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graphLanguage"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graphSupportReason"]!.GetValue<string>());
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
        Assert.Equal("csharp", structured["graphLanguage"]!.GetValue<string>());
        Assert.True(structured["graphSupported"]!.GetValue<bool>());
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

        Assert.True(structured["graphSupported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graphLanguage"]!.GetValue<string>());
        Assert.Equal("Next", structured["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graphLanguage"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graphSupportReason"]!.GetValue<string>());
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
            Assert.False(structured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sqlGraphContractDegradedReason"]!.GetValue<string>());
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
            Assert.False(structured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sqlGraphContractDegradedReason"]!.GetValue<string>());
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
            Assert.Null(structured["sqlGraphContractReady"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sqlGraphContractDegradedReason"]);
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
    [InlineData("callees", "attribute")]
    [InlineData("callees", "annotation")]
    [InlineData("callees", "type_reference")]
    public void ToolsCall_CallersOrCallees_NonCallGraphKindReturnsToolError(string tool, string kind)
    {
        // issue #293 + issue #444: the MCP `callers` / `callees` tools must reject non-call-graph
        // kinds. Metadata rows (`attribute` / `annotation`) are attributed to the enclosing
        // body-range symbol (so `callers Obsolete kind=attribute` reports the enclosing class
        // instead of the annotated method, and file-level targets drop entirely). `type_reference`
        // rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/
        // `instanceof`, XML-doc `cref`) and not runtime calls. AI clients should be redirected to
        // the `references` tool for these enumerations.
        // issue #293 + issue #444 補足: MCP の `callers` / `callees` ツールは非 call-graph な kind を
        // 必ず弾く。metadata 行 (`attribute` / `annotation`) は body-range の外側シンボルに帰属する
        // ため、`callers Obsolete kind=attribute` は注釈対象のメソッドではなく外側クラスを返し、
        // file-level target は完全に脱落する。`type_reference` は宣言型・generic 制約・`is`/`as`/
        // `instanceof`・XML-doc `cref` といった compile-time な型言及であり実行時呼び出しではない。
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxDepth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Empty(structured["callers"]!.AsArray());
        Assert.Equal("depth_zero", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Equal("Use `cdidx impact <symbol> --depth 1` or higher to traverse callers.", structured["suggestion"]!.GetValue<string>());
    }

    // #1534: requests above the server cap (50) must surface a warning and `max_depth_requested`
    // instead of silently clamping, so agents can react (raise the cap, accept partial depth, etc.).
    // #1534: サーバー上限 (50) を超える maxDepth は黙ってクランプせず、warnings と
    // max_depth_requested で通知し、エージェントが対応できるようにする。
    [Fact]
    public void ToolsCall_ImpactAnalysis_MaxDepthAboveCapSurfacesWarningAndRequestedValue()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxDepth":100}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(50, structured["max_depth"]!.GetValue<int>());
        Assert.Equal(100, structured["max_depth_requested"]!.GetValue<int>());
        var warnings = structured["warnings"]!.AsArray();
        Assert.Single(warnings);
        var warning = warnings[0]!.GetValue<string>();
        Assert.Contains("maxDepth was clamped from 100 to 50", warning);
        Assert.Contains("[0, 50]", warning);
        var summaryText = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("maxDepth was clamped from 100 to 50", summaryText);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_MaxDepthWithinCapDoesNotEmitWarning()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxDepth":50}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(50, structured["max_depth"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth_requested"]!.GetValue<int>());
        Assert.Null(structured["warnings"]);
    }

    [Fact]
    public void ToolsList_ImpactAnalysisMaxDepthSchemaDocumentsCap()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var impactTool = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        var maxDepthSchema = impactTool["inputSchema"]!["properties"]!["maxDepth"]!;

        Assert.Equal(50, maxDepthSchema["maximum"]!.GetValue<int>());
        Assert.Equal(0, maxDepthSchema["minimum"]!.GetValue<int>());
        var description = maxDepthSchema["description"]!.GetValue<string>();
        Assert.Contains("Server-side cap", description);
        Assert.Contains("warnings", description);
        Assert.Contains("max_depth_requested", description);
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

        Assert.False(response["result"]!["structuredContent"]!["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degradedReason"]!.GetValue<string>());
        Assert.Contains("idx_symbol_refs_container_nocase", response["result"]!["structuredContent"]!["degradedReason"]!.GetValue<string>());
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
        Assert.Null(structured["exactIndexAvailable"]);
        Assert.Null(structured["degradedReason"]);
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
        Assert.False(response["result"]!["structuredContent"]!["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degradedReason"]!.GetValue<string>());
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
        Assert.Null(structured["exactIndexAvailable"]);
        Assert.Null(structured["degradedReason"]);
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
        Assert.False(response["result"]!["structuredContent"]!["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degradedReason"]!.GetValue<string>());
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
        Assert.True(pythonStructured["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Null(pythonStructured["degradedReason"]);

        var csharpRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","lang":"csharp","exact":true}}}""")!;
        var csharpResponse = _server.HandleMessage(csharpRequest)!;
        var csharpStructured = csharpResponse["result"]!["structuredContent"]!;

        Assert.False(csharpStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("csharp_symbol_name_ready=false", csharpStructured["degraded_reason"]!.GetValue<string>());
        Assert.False(csharpStructured["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Contains("csharp_symbol_name_ready=false", csharpStructured["degradedReason"]!.GetValue<string>());
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
        Assert.False(response["result"]!["structuredContent"]!["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degradedReason"]!.GetValue<string>());
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

        Assert.False(structured["graphSupported"]!.GetValue<bool>());
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
        Assert.True(structured["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Null(structured["degradedReason"]);
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
        Assert.True(structured["exactIndexAvailable"]!.GetValue<bool>());
        Assert.Null(structured["degradedReason"]);
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
        Assert.Contains("exactSubstring", text);
    }

    [Fact]
    public void ToolsCall_Search_AllowsFalseExactNameAlias()
    {
        InsertIndexedFile("src/search_false_alias.cs", "csharp", "void Run() { }\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","exactName":false}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.NotNull(response["result"]!["structuredContent"]!["results"]);
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
        Assert.Contains("exactName", text);
    }

    [Fact]
    public void ToolsCall_Symbols_AllowsFalseExactSubstringAlias()
    {
        InsertIndexedFile("src/symbol_false_alias.cs", "csharp", "public class ExactApp { public void Run() { } }\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactSubstring":false}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.NotNull(response["result"]!["structuredContent"]!["results"]);
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
            Assert.Null(definitionStructured["sqlGraphContractReady"]);
            Assert.Null(definitionStructured["degraded"]);
            Assert.Null(definitionStructured["sql_graph_contract_degraded_reason"]);
            Assert.Null(definitionStructured["sqlGraphContractDegradedReason"]);

            var callersRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callers","arguments":{"query":"dbo.fn_Target","lang":"sql","exact":true}}}""")!;
            var callersResponse = server.HandleMessage(callersRequest)!;
            var callersStructured = callersResponse["result"]!["structuredContent"]!;

            Assert.False(callersStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(callersStructured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.NotNull(callersStructured["sql_graph_contract_degraded_reason"]);
            Assert.NotNull(callersStructured["sqlGraphContractDegradedReason"]);
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
            Assert.False(structured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sqlGraphContractDegradedReason"]!.GetValue<string>());
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
            Assert.Null(structured["sqlGraphContractReady"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sqlGraphContractDegradedReason"]);
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
            Assert.False(structured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sqlGraphContractDegradedReason"]!.GetValue<string>());
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
            Assert.False(structured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sqlGraphContractDegradedReason"]!.GetValue<string>());
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
            Assert.Null(structured["sqlGraphContractReady"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sqlGraphContractDegradedReason"]);
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
            Assert.Null(structured["sqlGraphContractReady"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["sqlGraphContractDegradedReason"]);
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
    public void ToolsCall_Excerpt_BeforeAboveCapReturnsError()
    {
        InsertIndexedFile("dist/data-before-overflow.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-before-overflow.txt","startLine":1,"before":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("before must be in [0, 1000]", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_AfterAboveCapReturnsError()
    {
        InsertIndexedFile("dist/data-after-overflow.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-after-overflow.txt","startLine":1,"after":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("after must be in [0, 1000]", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
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
        Assert.NotNull(response["result"]!["structuredContent"]!["projectRoot"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["hotspot_family_ready"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["hotspotFamilyReady"]);
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
            Assert.False(structured["hotspotFamilyReady"]!.GetValue<bool>());
            Assert.Contains("csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
            Assert.Contains("csharp", structured["hotspotFamilyDegradedReason"]!.GetValue<string>());
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
            Assert.False(structured["sqlGraphContractReady"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sqlGraphContractDegradedReason"]!.GetValue<string>());
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

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["projectRoot"]!.GetValue<string>());
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

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["projectRoot"]!.GetValue<string>());
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

            Assert.Null(response["result"]!["structuredContent"]!["projectRoot"]);
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

            Assert.Null(response["result"]!["structuredContent"]!["projectRoot"]);
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

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["projectRoot"]!.GetValue<string>());
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("cdidx v", text);
        Assert.Contains("is ready", text);
        Assert.NotNull(response["result"]!["structuredContent"]!["version"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["timestamp"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["db_exists"]);
    }

    [Fact]
    public void ToolsCall_BatchQuery_ExecutesMultipleQueries()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"status"},{"tool":"files","arguments":{}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 2 queries", text);
        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("status", results[0]!["tool"]!.GetValue<string>());
        Assert.Equal("files", results[1]!["tool"]!.GetValue<string>());
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

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.NotNull(results[0]!["elapsed_ms"]);
        Assert.NotNull(results[1]!["elapsed_ms"]);
        Assert.NotNull(results[2]!["elapsed_ms"]);
        Assert.Contains("not allowed", results[1]!["error"]!.GetValue<string>());
        Assert.Contains("Unknown tool", results[2]!["error"]!.GetValue<string>());

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("1 succeeded, 2 failed", text);
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

            Assert.Equal(projectRootA, statusResponse["result"]!["structuredContent"]!["projectRoot"]!.GetValue<string>());
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

            Assert.Equal(Path.GetFullPath(fixtureDir), statusResponse["result"]!["structuredContent"]!["projectRoot"]!.GetValue<string>());
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

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.True(structured["verified"]!.GetValue<bool>());
        Assert.Equal(3, structured["user_version_before"]!.GetValue<int>());
        Assert.Equal(7, structured["user_version_after"]!.GetValue<int>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());

        using var verifyDb = new DbContext(_dbPath);
        verifyDb.TryMigrateForRead();
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
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
            Assert.True(structured["hotspotFamilyReady"]!.GetValue<bool>());
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
            Assert.True(structured["hotspotFamilyReady"]!.GetValue<bool>());
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
            Assert.False(structured["hotspotFamilyReady"]!.GetValue<bool>());
            Assert.Contains("csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
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
            Assert.False(structured["hotspotFamilyReady"]!.GetValue<bool>());
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
        Assert.Contains("returned bucket(s)", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
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
        Assert.Null(structured["graph_degraded"]);
        Assert.Null(structured["unsupported_symbol_kind"]);
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
            Assert.Null(structured["graph_degraded"]);
            Assert.Null(structured["unsupported_symbol_kind"]);
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
        Assert.Equal(["Hidden", "InternalOnly", "UserDto", "FullName"], symbols.Select(symbol => symbol!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal("reflection_or_config_suspect", symbols[3]!["unusedBucket"]!.GetValue<string>());
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
        // Use unique description to avoid dedup collision with other test runs
        // 他テスト実行との重複排除衝突を避けるため一意な description を使用
        var uniqueDesc = $"Arrow functions are not detected as symbols {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1,
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
        Assert.NotNull(structured["hash"]);
        Assert.True(structured["stored_locally"]!.GetValue<bool>());
    }

    [Fact]
    public void SuggestImprovement_CrashReport_ReturnsSuccess()
    {
        var uniqueDesc = $"NullReferenceException when searching with empty query {Guid.NewGuid():N}";
        var json = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1,
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
    public void SuggestImprovement_DuplicateSubmission_ReturnsDuplicate()
    {
        var uniqueDesc = $"Add support for Zig language {Guid.NewGuid():N}";
        JsonNode MakeRequest(int id) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id,
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
    }

    [Fact]
    public void SuggestImprovement_InvalidCategory_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"suggest_improvement","arguments":{"category":"invalid_category","description":"Some description"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Invalid category", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
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
            ["jsonrpc"] = "2.0", ["id"] = 1,
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
            ["jsonrpc"] = "2.0", ["id"] = 1,
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
}
