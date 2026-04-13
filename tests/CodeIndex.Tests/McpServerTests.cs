using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for McpServer JSON-RPC message handling.
/// McpServerのJSON-RPCメッセージ処理のテスト。
/// </summary>
public class McpServerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _db;
    private readonly McpServer _server;

    public McpServerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_test_{Guid.NewGuid():N}.db");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();

        // Seed test data / テストデータを投入
        var writer = new DbWriter(_db.Connection);
        // Stamp graph + issues ready so reads trust the seeded references like a completed index run.
        // seed したデータを完了 index と同等に扱うため readiness を stamp しておく。
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
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

    // --- Protocol tests / プロトコルテスト ---

    [Fact]
    public void Initialize_ReturnsProtocolVersion()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"test"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, response["id"]!.GetValue<int>());
        Assert.Equal("2025-03-26", response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("cdidx", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal(ConsoleUi.LoadVersion(), response["result"]!["serverInfo"]!["version"]!.GetValue<string>());
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
        // Verify language list comes from ReferenceExtractor / 言語リストがReferenceExtractorから来ることを検証
        foreach (var lang in ReferenceExtractor.GetSupportedLanguages())
        {
            Assert.Contains(lang, instructions);
        }
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
    public void ToolsList_Returns19Tools()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(22, tools.Count);

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("search", names);
        Assert.Contains("impact_analysis", names);
        Assert.Contains("definition", names);
        Assert.Contains("references", names);
        Assert.Contains("callers", names);
        Assert.Contains("callees", names);
        Assert.Contains("symbols", names);
        Assert.Contains("files", names);
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
        var queryToolNames = new[] { "search", "definition", "references", "callers", "callees", "symbols", "files", "excerpt", "map", "analyze_symbol", "status", "outline" };

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
        Assert.NotNull(structured["indexed_at"]);
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
    public void ToolsCall_References_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graphLanguage"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graphSupportReason"]!.GetValue<string>());
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
    public void ToolsCall_Callees_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Run","lang":"markdown"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("markdown", response["result"]!["structuredContent"]!["graphLanguage"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graphSupported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graphSupportReason"]!.GetValue<string>());
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
        Assert.NotNull(structured["indexed_at"]);
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

        // Verify a detection-only language / 検出のみの言語を検証
        var markdown = languages.First(l => l!["lang"]!.GetValue<string>() == "markdown")!;
        Assert.False(markdown["symbol_extraction"]!.GetValue<bool>());
        Assert.False(markdown["graph_queries"]!.GetValue<bool>());
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
        var server = new McpServer("/nonexistent/path/test.db", "0.1.1");
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

    public void Dispose()
    {
        _db.Dispose();
        DeleteDbPath();
    }

    private void DeleteDbPath()
    {
        if (!File.Exists(_dbPath))
            return;

        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (UnauthorizedAccessException)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Symbols_ExactZero_AttachesSnakeCaseHint()
    {
        // Codex #88 review regression: MCP server uses CamelCase PropertyNamingPolicy, but the
        // #88 contract requires `exact_zero_hint.{relaxed_count,sample_names,suggestion}` with
        // snake_case keys. Without the explicit JsonObject builder, serializing the DTO through
        // _jsonOptions would emit a mixed payload (snake_case outer key, camelCase inner keys).
        // Verify the MCP response uses snake_case end-to-end on the exact-zero path for all 5
        // leaf commands (symbols seed path is enough to exercise the shared builder).
        // Codex 指摘: MCP は CamelCase 既定なので、明示的 JsonObject builder が snake_case
        // キーを保つことを固定する回帰テスト。
        var request = JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Ap","exact":true}}}
            """)!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var hint = structured["exact_zero_hint"];
        Assert.NotNull(hint);

        // Snake_case outer field keys (contract). CamelCase equivalents must NOT be present.
        // 外キー + 内キーが snake_case であること。camelCase 版が存在してはいけない。
        Assert.NotNull(hint!["relaxed_count"]);
        Assert.NotNull(hint["sample_names"]);
        Assert.NotNull(hint["suggestion"]);
        Assert.Null(hint["relaxedCount"]);
        Assert.Null(hint["sampleNames"]);

        // The relaxed substring "Ap" must catch the seeded `App` symbol.
        // substring "Ap" は seed の App シンボルを拾う。
        Assert.Equal(1, hint["relaxed_count"]!.GetValue<int>());
        Assert.Contains("App", hint["sample_names"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Symbols_ExactZero_NoHintWhenRelaxedAlsoZero()
    {
        // Genuine miss: `--exact` + a name no substring would match either. No hint should be
        // emitted (the "No symbols found." message is enough).
        // 本物の miss（substring も 0 件）ではヒントを出さない。
        var request = JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"zzz_nonexistent_xyz","exact":true}}}
            """)!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Null(structured["exact_zero_hint"]);
    }
}
