using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class LspServerTests
{
    [Fact]
    public void ExtractTokenAtUtf16Position_ReturnsIdentifierUnderCursor()
    {
        Assert.Equal("Needle", LspServer.ExtractTokenAtUtf16Position("var value = Needle.Call();", 14));
        Assert.Equal("Needle", LspServer.ExtractTokenAtUtf16Position("var value = Needle.Call();", 18));
    }

    [Fact]
    public void TryReadMessage_ReadsContentLengthFramedPayload()
    {
        const string payload = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}";
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}");
        using var stream = new MemoryStream(bytes);

        Assert.True(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void TryReadMessage_AcceptsHeaderLineAtMaxLength()
    {
        const string payload = "{}";
        var maxLengthHeader = "X-" + new string('A', LspServer.MaxLspHeaderLineBytes - 2);
        var bytes = Encoding.UTF8.GetBytes($"{maxLengthHeader}\r\nContent-Length: {payload.Length}\r\n\r\n{payload}");
        using var stream = new MemoryStream(bytes);

        Assert.True(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void TryReadMessage_RejectsHeaderLineOverMaxLength()
    {
        var oversizedHeader = "X-" + new string('A', LspServer.MaxLspHeaderLineBytes - 1);
        var bytes = Encoding.UTF8.GetBytes($"{oversizedHeader}\r\nContent-Length: 2\r\n\r\n{{}}");
        using var stream = new MemoryStream(bytes);

        Assert.False(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void TryReadMessage_RejectsHeaderCountOverMax_Issue3230()
    {
        var headers = Enumerable.Range(0, LspServer.MaxLspHeaderCount)
            .Select(i => $"X-{i}: value");
        var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", headers) + "\r\nContent-Length: 2\r\n\r\n{}");
        using var stream = new MemoryStream(bytes);

        Assert.False(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void TryReadMessage_RejectsAggregateHeaderBytesOverMax_Issue3230()
    {
        var maxLineHeader = "X-" + new string('A', LspServer.MaxLspHeaderLineBytes - 2);
        var headers = Enumerable.Repeat(maxLineHeader, (LspServer.MaxLspHeaderBytes / LspServer.MaxLspHeaderLineBytes) + 1);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\r\n", headers) + "\r\nContent-Length: 2\r\n\r\n{}");
        using var stream = new MemoryStream(bytes);

        Assert.False(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void TryReadMessage_RejectsFrameOverMaxLength()
    {
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {LspServer.MaxLspFrameBytes + 1}\r\n\r\n");
        using var stream = new MemoryStream(bytes);

        Assert.False(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    [Theory]
    [InlineData("2", "2")]
    [InlineData("2", "3")]
    public void TryReadMessage_RejectsDuplicateContentLength_Issue3229(string firstLength, string secondLength)
    {
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {firstLength}\r\nContent-Length: {secondLength}\r\n\r\n{{}}");
        using var stream = new MemoryStream(bytes);

        Assert.False(LspServer.TryReadMessage(stream, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void TryReadMessage_CanceledBeforeRead_ThrowsOperationCanceled_Issue3427()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Content-Length: 2\r\n\r\n{}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LspServer.TryReadMessage(stream, out _, cts.Token));
    }

    [Fact]
    public void Run_CanceledBeforeRead_ThrowsOperationCanceled_Issue3427()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_canceled");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("Content-Length: 2\r\n\r\n{}"));
            using var output = new MemoryStream();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => server.Run(input, output, cts.Token));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Initialize_AdvertisesCoreCapabilities()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_initialize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

            Assert.NotNull(response);
            var capabilities = response!["result"]!["capabilities"]!;
            Assert.True(capabilities["definitionProvider"]!.GetValue<bool>());
            Assert.True(capabilities["declarationProvider"]!.GetValue<bool>());
            Assert.True(capabilities["typeDefinitionProvider"]!.GetValue<bool>());
            Assert.True(capabilities["implementationProvider"]!.GetValue<bool>());
            Assert.True(capabilities["documentSymbolProvider"]!.GetValue<bool>());
            Assert.True(capabilities["hoverProvider"]!.GetValue<bool>());
            Assert.True(capabilities["documentHighlightProvider"]!.GetValue<bool>());
            Assert.Equal(1, capabilities["textDocumentSync"]!["change"]!.GetValue<int>());
            Assert.True(capabilities["textDocumentSync"]!["openClose"]!.GetValue<bool>());
            Assert.False(capabilities["completionProvider"]!["resolveProvider"]!.GetValue<bool>());
            Assert.False(capabilities["codeLensProvider"]!["resolveProvider"]!.GetValue<bool>());
            Assert.False(capabilities["inlayHintProvider"]!["resolveProvider"]!.GetValue<bool>());
            Assert.True(capabilities["semanticTokensProvider"]!["full"]!.GetValue<bool>());
            Assert.Contains(capabilities["semanticTokensProvider"]!["legend"]!["tokenTypes"]!.AsArray(), node => node!.GetValue<string>() == "class");
            Assert.True(capabilities["workspace"]!["workspaceFolders"]!["supported"]!.GetValue<bool>());
            Assert.True(capabilities["workspace"]!["workspaceFolders"]!["changeNotifications"]!.GetValue<bool>());
            Assert.Equal("cdidx", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_LiveDocumentSync_UsesChangedBufferForPositionRequests_Issue3536()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_live_sync");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var diskSource = "class App { void Needle() { } void Call() { Missing(); } }\n";
            var liveSource = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, diskSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", diskSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            Assert.Null(server.HandleMessage(CreateDidOpenRequest(sourcePath, diskSource, version: 1)));
            Assert.Null(server.HandleMessage(CreateDidChangeRequest(sourcePath, liveSource, version: 2)));
            var liveResponse = server.HandleMessage(CreateDefinitionRequest(
                sourcePath,
                3536,
                0,
                liveSource.LastIndexOf("Needle();", StringComparison.Ordinal)));

            Assert.NotNull(liveResponse);
            Assert.NotEmpty(liveResponse!["result"]!.AsArray());

            Assert.Null(server.HandleMessage(CreateDidCloseRequest(sourcePath)));
            var closedResponse = server.HandleMessage(CreateDefinitionRequest(
                sourcePath,
                35361,
                0,
                liveSource.LastIndexOf("Needle();", StringComparison.Ordinal)));

            Assert.NotNull(closedResponse);
            Assert.Empty(closedResponse!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_LiveDocumentSync_EvictsOldestBufferWhenCacheIsFull_Issue3536()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_live_sync_bound");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            string? firstPath = null;
            string? firstLiveSource = null;
            string? lastPath = null;
            string? lastLiveSource = null;
            var sources = new List<(string Path, string DiskSource, string LiveSource)>();
            for (var i = 0; i <= LspServer.MaxLiveDocuments; i++)
            {
                var sourcePath = Path.Combine(projectRoot, $"file{i}.cs");
                var needle = $"Needle{i}";
                var missing = $"Missing{i}";
                var diskSource = $"class App{i} {{ void {needle}() {{ }} void Call() {{ {missing}(); }} }}\n";
                var liveSource = $"class App{i} {{ void {needle}() {{ }} void Call() {{ {needle}(); }} }}\n";
                File.WriteAllText(sourcePath, diskSource);
                TestProjectHelper.InsertIndexedFile(dbPath, $"file{i}.cs", "csharp", diskSource);
                sources.Add((sourcePath, diskSource, liveSource));
                if (i == 0)
                {
                    firstPath = sourcePath;
                    firstLiveSource = liveSource;
                }
                if (i == LspServer.MaxLiveDocuments)
                {
                    lastPath = sourcePath;
                    lastLiveSource = liveSource;
                }
            }

            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                Assert.Null(server.HandleMessage(CreateDidOpenRequest(source.Path, source.DiskSource, version: i + 1)));
                Assert.Null(server.HandleMessage(CreateDidChangeRequest(source.Path, source.LiveSource, version: i + 100)));
            }

            Assert.NotNull(firstPath);
            Assert.NotNull(firstLiveSource);
            var evictedResponse = server.HandleMessage(CreateDefinitionRequest(
                firstPath!,
                35368,
                0,
                firstLiveSource!.LastIndexOf("Needle0();", StringComparison.Ordinal)));

            Assert.NotNull(evictedResponse);
            Assert.Empty(evictedResponse!["result"]!.AsArray());

            Assert.NotNull(lastPath);
            Assert.NotNull(lastLiveSource);
            var retainedResponse = server.HandleMessage(CreateDefinitionRequest(
                lastPath!,
                35369,
                0,
                lastLiveSource!.LastIndexOf($"Needle{LspServer.MaxLiveDocuments}();", StringComparison.Ordinal)));

            Assert.NotNull(retainedResponse);
            Assert.NotEmpty(retainedResponse!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_RicherProviders_ReturnIndexBackedResponses_Issue3536()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_richer_providers");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = """
                public class App
                {
                    public int Count() { return 1; }
                    public void Call() { Count(); }
                }
                """;
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            MarkGraphReady(dbPath);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var countCallCharacter = CharacterOf(source, 3, "Count();");

            var hover = server.HandleMessage(CreatePositionRequest("textDocument/hover", sourcePath, 35362, 3, countCallCharacter));
            Assert.NotNull(hover);
            Assert.Contains("Count", hover!["result"]!["contents"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);

            var completion = server.HandleMessage(CreatePositionRequest("textDocument/completion", sourcePath, 35363, 3, countCallCharacter + 3));
            Assert.NotNull(completion);
            Assert.Contains(completion!["result"]!["items"]!.AsArray(), item => item!["label"]!.GetValue<string>() == "Count");

            var highlights = server.HandleMessage(CreatePositionRequest("textDocument/documentHighlight", sourcePath, 35364, 3, countCallCharacter));
            Assert.NotNull(highlights);
            Assert.NotEmpty(highlights!["result"]!.AsArray());

            var semanticTokens = server.HandleMessage(CreateTextDocumentRequest("textDocument/semanticTokens/full", sourcePath, 35365));
            Assert.NotNull(semanticTokens);
            Assert.NotEmpty(semanticTokens!["result"]!["data"]!.AsArray());

            var codeLens = server.HandleMessage(CreateTextDocumentRequest("textDocument/codeLens", sourcePath, 35366));
            Assert.NotNull(codeLens);
            Assert.NotEmpty(codeLens!["result"]!.AsArray());

            var inlayHints = server.HandleMessage(CreateTextDocumentRequest("textDocument/inlayHint", sourcePath, 35367));
            Assert.NotNull(inlayHints);
            Assert.NotEmpty(inlayHints!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_TooDeepJson_ReturnsParseError_Issue3021()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_depth");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage(BuildNestedLspRequest(LspServer.MaxJsonDepth + 1));

            Assert.NotNull(response);
            Assert.Equal(-32700, response!["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["id"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_UnknownMethod_TruncatesMethodName_Issue3127()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_unknown_method");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var method = new string('m', LspServer.MaxLspFrameBytes - 4096) + "UNBOUNDED_SENTINEL";
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var error = response!["error"]!;
            Assert.Equal(-32601, error["code"]!.GetValue<int>());
            var message = error["message"]!.GetValue<string>();
            Assert.StartsWith("Method not found: ", message, StringComparison.Ordinal);
            Assert.EndsWith("...", message, StringComparison.Ordinal);
            Assert.DoesNotContain("UNBOUNDED_SENTINEL", message, StringComparison.Ordinal);
            Assert.True(message.Length <= "Method not found: ".Length + LspServer.MaxUnknownMethodDiagnosticChars + "...".Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_OverMaxPayload_ReturnsParseError_Issue3657()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_oversized_payload");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + new string('m', LspServer.MaxLspFrameBytes) + "\"}";

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32700, response!["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["id"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_UnknownMethod_TruncatesMethodName_Issue3205()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_unknown_method_3205");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var method = "workspace/" + new string('m', LspServer.MaxUnknownMethodDiagnosticChars + 20) + "LEAK_SENTINEL";
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3205,
                method,
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var error = response!["error"]!;
            Assert.Equal(-32601, error["code"]!.GetValue<int>());
            var message = error["message"]!.GetValue<string>();
            Assert.StartsWith("Method not found: workspace/", message, StringComparison.Ordinal);
            Assert.EndsWith("...", message, StringComparison.Ordinal);
            Assert.DoesNotContain("LEAK_SENTINEL", message, StringComparison.Ordinal);
            Assert.True(message.Length <= "Method not found: ".Length + LspServer.MaxUnknownMethodDiagnosticChars + "...".Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_UnknownMethod_PreservesSlashDelimitedMethodName_Issue3127()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_unknown_method_slash");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "textDocument/unknownHover",
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
            Assert.Equal("Method not found: textDocument/unknownHover", response["error"]!["message"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_ObjectRequestId_ReturnsInvalidRequest_Issue3204()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_object_id");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage("""{"jsonrpc":"2.0","id":{"nested":1},"method":"initialize"}""");

            Assert.NotNull(response);
            Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["id"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_OversizedStringRequestId_ReturnsInvalidRequest_Issue3204()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_long_id");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var oversizedId = new string('i', LspServer.MaxRequestIdStringChars + 1);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = oversizedId,
                method = "initialize",
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["id"]);
            Assert.DoesNotContain(oversizedId, response.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_InvalidParams_ReturnsStableErrorMessage_Issue3200()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_invalid_params");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 20,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = string.Empty },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
            var message = response["error"]!["message"]!.GetValue<string>();
            Assert.Equal("Invalid params", message);
            Assert.DoesNotContain("textDocument.uri", message, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_InternalFailure_ReturnsStableErrorMessage_Issue3200()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_internal_error");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            db.Dispose();
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 21,
                method = "workspace/symbol",
                @params = new
                {
                    query = "Needle",
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32603, response!["error"]!["code"]!.GetValue<int>());
            var message = response["error"]!["message"]!.GetValue<string>();
            Assert.Equal("Internal error", message);
            Assert.DoesNotContain(nameof(ObjectDisposedException), message, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_WorkspaceSymbol_RejectsOversizedQuery_Issue3128()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_symbol_long_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var oversizedQuery = new string('q', QueryLimits.MaxQueryLength + 1);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3128,
                method = "workspace/symbol",
                @params = new
                {
                    query = oversizedQuery,
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var error = response!["error"]!;
            Assert.Equal(-32602, error["code"]!.GetValue<int>());
            Assert.Equal("Invalid params", error["message"]!.GetValue<string>());
            Assert.DoesNotContain(oversizedQuery, response.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_WorkspaceSymbol_HonorsClientLimit_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_symbol_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 5; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"file{i}.cs",
                    "csharp",
                    $"class Needle{i} {{ }}\n");
            }

            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3537,
                method = "workspace/symbol",
                @params = new
                {
                    query = "Needle",
                    limit = 2,
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(2, response!["result"]!.AsArray().Count);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_MalformedJsonFrame_WritesParseErrorAndContinues()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_malformed_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            const string initializeRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame("{") + Frame(initializeRequest)));
            using var output = new MemoryStream();

            var exitCode = server.Run(input, output);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            output.Position = 0;
            Assert.True(LspServer.TryReadMessage(output, out var parseErrorPayload));
            using var parseError = JsonDocument.Parse(parseErrorPayload);
            Assert.Equal(-32700, parseError.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal(JsonValueKind.Null, parseError.RootElement.GetProperty("id").ValueKind);

            Assert.True(LspServer.TryReadMessage(output, out var initializePayload));
            using var initialize = JsonDocument.Parse(initializePayload);
            Assert.True(initialize.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("definitionProvider").GetBoolean());
            Assert.False(LspServer.TryReadMessage(output, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ShutdownThenExit_StopsBeforeLaterFrames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_shutdown_exit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            const string shutdownRequest = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"shutdown\"}";
            const string exitNotification = "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}";
            const string initializeRequest = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"initialize\",\"params\":{}}";
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(
                Frame(shutdownRequest) + Frame(exitNotification) + Frame(initializeRequest)));
            using var output = new MemoryStream();

            var exitCode = server.Run(input, output);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            output.Position = 0;
            Assert.True(LspServer.TryReadMessage(output, out var shutdownPayload));
            using var shutdown = JsonDocument.Parse(shutdownPayload);
            Assert.Equal(2, shutdown.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(JsonValueKind.Null, shutdown.RootElement.GetProperty("result").ValueKind);
            Assert.False(LspServer.TryReadMessage(output, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ExitBeforeShutdown_ReturnsUsageError()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_exit_without_shutdown");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            const string exitNotification = "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}";
            const string initializeRequest = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"initialize\",\"params\":{}}";
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(exitNotification) + Frame(initializeRequest)));
            using var output = new MemoryStream();

            var exitCode = server.Run(input, output);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            output.Position = 0;
            Assert.False(LspServer.TryReadMessage(output, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_ReturnsIndexedSymbols()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "class App { void Needle() { } }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", File.ReadAllText(sourcePath));
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var symbols = response!["result"]!.AsArray();
            var app = Assert.Single(symbols.Where(symbol => symbol?["name"]?.GetValue<string>() == "App"));
            var children = app!["children"]!.AsArray();
            Assert.Contains(children, symbol => symbol?["name"]?.GetValue<string>() == "Needle");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_DoesNotNestSameRangeTopLevelSymbols_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_same_range");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class Alpha { } class Beta { }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 35374,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var symbols = response!["result"]!.AsArray();
            var alpha = Assert.Single(symbols.Where(symbol => symbol?["name"]?.GetValue<string>() == "Alpha"));
            var beta = Assert.Single(symbols.Where(symbol => symbol?["name"]?.GetValue<string>() == "Beta"));
            Assert.Null(alpha!["children"]);
            Assert.Null(beta!["children"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_NestsSameRangeChildAfterContainer_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_same_range_child");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class Z { void A() { } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 35375,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var symbols = response!["result"]!.AsArray();
            var z = Assert.Single(symbols.Where(symbol => symbol?["name"]?.GetValue<string>() == "Z"));
            var children = z!["children"]!.AsArray();
            Assert.Contains(children, symbol => symbol?["name"]?.GetValue<string>() == "A");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_NestsSameStartLongerContainerBeforeChild_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_same_start_container");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "namespace N { class C {\n}\n}\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 35376,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var symbols = response!["result"]!.AsArray();
            var n = Assert.Single(symbols.Where(symbol => symbol?["name"]?.GetValue<string>() == "N"));
            var children = n!["children"]!.AsArray();
            Assert.Contains(children, symbol => symbol?["name"]?.GetValue<string>() == "C");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_ResolvesDuplicateBasenamesByRelativePath()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_duplicate");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var srcPath = Path.Combine(projectRoot, "src", "app.cs");
            var testPath = Path.Combine(projectRoot, "tests", "app.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
            File.WriteAllText(srcPath, "class SrcApp { }\n");
            File.WriteAllText(testPath, "class TestApp { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", File.ReadAllText(srcPath));
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/app.cs", "csharp", File.ReadAllText(testPath));
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 22,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(testPath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var names = response!["result"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .ToArray();
            Assert.Contains("TestApp", names);
            Assert.DoesNotContain("SrcApp", names);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_DoesNotSuffixMatchProjectRootedUnindexedFile_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_unindexed_same_name");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var indexedPath = Path.Combine(projectRoot, "app.cs");
            var unindexedPath = Path.Combine(projectRoot, "dir", "app.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(unindexedPath)!);
            File.WriteAllText(indexedPath, "class IndexedApp { }\n");
            File.WriteAllText(unindexedPath, "class UnindexedApp { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", File.ReadAllText(indexedPath));
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 35377,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(unindexedPath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_RejectsOversizedTextDocumentUri_Issue3129()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_long_uri");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var oversizedUri = "file:///" + new string('a', LspServer.MaxTextDocumentUriChars);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3129,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = oversizedUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var error = response!["error"]!;
            Assert.Equal(-32602, error["code"]!.GetValue<int>());
            var message = error["message"]!.GetValue<string>();
            Assert.Equal("Invalid params", message);
            Assert.True(message.Length < 120);
            Assert.DoesNotContain(oversizedUri, message, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandleMessage_DocumentSymbol_TruncatesDetailsAndCapsResponse_Issue3130_Issue3743(bool writeIndented)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_budget");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "large.cs");
            var parameters = string.Join(", ", Enumerable.Range(0, 90).Select(i => $"int argument{i:D2}"));
            var source = new StringBuilder("class LargeSymbols\n{\n");
            for (var i = 0; i < LspServer.MaxDocumentSymbols; i++)
                source.Append("    void Method").Append(i.ToString("D4", CultureInfo.InvariantCulture)).Append('(').Append(parameters).Append(") { }\n");
            source.Append("}\n");

            File.WriteAllText(sourcePath, source.ToString());
            TestProjectHelper.InsertIndexedFile(dbPath, "large.cs", "csharp", source.ToString());
            using var db = new DbContext(dbPath);
            var jsonOptions = new JsonSerializerOptions(ProgramRunner.CreateDefaultJsonOptions())
            {
                WriteIndented = writeIndented,
            };
            using var server = new LspServer(new DbReader(db), "1.2.3", jsonOptions, projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3130,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var symbols = response!["result"]!.AsArray();
            Assert.NotEmpty(symbols);
            Assert.True(symbols.Count < LspServer.MaxDocumentSymbols);
            Assert.True(Encoding.UTF8.GetByteCount(symbols.ToJsonString(jsonOptions)) <= LspServer.MaxDocumentSymbolResponseBytes);
            var allSymbols = FlattenDocumentSymbols(symbols).ToArray();
            Assert.True(allSymbols.Length < LspServer.MaxDocumentSymbols);
            Assert.Contains(allSymbols, symbol =>
            {
                var detail = symbol?["detail"]?.GetValue<string>();
                return detail is { Length: <= LspServer.MaxDocumentSymbolDetailChars }
                    && detail.EndsWith("...", StringComparison.Ordinal);
            });
            Assert.All(allSymbols, symbol =>
            {
                var detail = symbol?["detail"]?.GetValue<string>();
                if (detail != null)
                    Assert.True(detail.Length <= LspServer.MaxDocumentSymbolDetailChars);
            });
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_CapsMaterializationBeforeSorting_Issue3758()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_materialization");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "materialization.cs");
            var source = new StringBuilder("class MaterializationBudget\n{\n");
            for (var i = 0; i < LspServer.MaxDocumentSymbolMaterialization + 25; i++)
                source.Append("    void Method").Append(i.ToString("D4", CultureInfo.InvariantCulture)).Append("() { }\n");
            source.Append("}\n");

            File.WriteAllText(sourcePath, source.ToString());
            TestProjectHelper.InsertIndexedFile(dbPath, "materialization.cs", "csharp", source.ToString());
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3758,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            using var activity = new Activity("lsp-document-symbol-test").Start();
            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var roots = response!["result"]!.AsArray();
            var root = Assert.Single(roots);
            Assert.Equal("MaterializationBudget", root!["name"]!.GetValue<string>());
            var children = root["children"]!.AsArray();
            Assert.True(children.Count < LspServer.MaxDocumentSymbolMaterialization);
            Assert.Equal("Method0000", children[0]!["name"]!.GetValue<string>());
            Assert.Equal("Method0001", children[1]!["name"]!.GetValue<string>());
            Assert.Equal("Method0002", children[2]!["name"]!.GetValue<string>());
            Assert.Equal(LspServer.MaxDocumentSymbolMaterialization, GetActivityTag(activity, "lsp.document_symbols.materialized_count"));
            Assert.Equal(true, GetActivityTag(activity, "lsp.document_symbols.materialization_truncated"));
            Assert.Equal(roots.Count, GetActivityTag(activity, "lsp.document_symbols.returned_root_count"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_RejectsNonStringTextDocumentUri_Issue3203()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_uri_type");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3203,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = 123 },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var error = response!["error"]!;
            Assert.Equal(-32602, error["code"]!.GetValue<int>());
            var message = error["message"]!.GetValue<string>();
            Assert.Equal("Invalid params", message);
            Assert.DoesNotContain("123", response.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("untitled:scratch.cs")]
    [InlineData("https://example.invalid/app.cs")]
    public void HandleMessage_DocumentSymbol_RejectsNonFileTextDocumentUri_Issue3206(string uri)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_uri_scheme");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3206,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var error = response!["error"]!;
            Assert.Equal(-32602, error["code"]!.GetValue<int>());
            Assert.Equal("Invalid params", error["message"]!.GetValue<string>());
            Assert.DoesNotContain(uri, response.ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ReturnsLocationForTokenAtPosition()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "textDocument/definition",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                    position = new { line = 0, character = source.IndexOf("Needle();", StringComparison.Ordinal) },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var locations = response!["result"]!.AsArray();
            Assert.NotEmpty(locations);
            Assert.Equal(new Uri(sourcePath).AbsoluteUri, locations[0]!["uri"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("textDocument/declaration")]
    [InlineData("textDocument/typeDefinition")]
    [InlineData("textDocument/implementation")]
    public void HandleMessage_DefinitionAliasMethods_ReturnLocations_Issue3537(string method)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreatePositionRequest(
                method,
                sourcePath,
                3537,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var locations = response!["result"]!.AsArray();
            Assert.NotEmpty(locations);
            Assert.Equal(new Uri(sourcePath).AbsoluteUri, locations[0]!["uri"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_UsesTrackedWorkspaceFolders_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_root_primary");
        var secondaryRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_root_secondary");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(secondaryRoot, "app.cs");
            var source = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, sourcePath, "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                sourcePath,
                35370,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var beforeInitialize = server.HandleMessage(request);
            Assert.NotNull(beforeInitialize);
            Assert.Empty(beforeInitialize!["result"]!.AsArray());

            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 35371,
                method = "initialize",
                @params = new
                {
                    workspaceFolders = new[]
                    {
                        new { uri = new Uri(secondaryRoot).AbsoluteUri, name = "secondary" },
                    },
                },
            });
            Assert.NotNull(server.HandleMessage(initialize));

            var afterInitialize = server.HandleMessage(request);
            Assert.NotNull(afterInitialize);
            var locations = afterInitialize!["result"]!.AsArray();
            var location = Assert.Single(locations);
            Assert.Equal(new Uri(sourcePath).AbsoluteUri, location!["uri"]!.GetValue<string>());

            var removeFolder = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "workspace/didChangeWorkspaceFolders",
                @params = new
                {
                    @event = new
                    {
                        added = Array.Empty<object>(),
                        removed = new[]
                        {
                            new { uri = new Uri(secondaryRoot).AbsoluteUri, name = "secondary" },
                        },
                    },
                },
            });
            Assert.Null(server.HandleMessage(removeFolder));

            var afterRemove = server.HandleMessage(request);
            Assert.NotNull(afterRemove);
            Assert.Empty(afterRemove!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(secondaryRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_DoesNotMapRelativeIndexPathToAddedWorkspaceFolder_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_relative_primary");
        var secondaryRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_relative_secondary");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var secondaryPath = Path.Combine(secondaryRoot, "app.cs");
            var primarySource = "class Primary { void Needle() { } }\n";
            var secondarySource = "class Secondary { void Call() { Needle(); } }\n";
            File.WriteAllText(secondaryPath, secondarySource);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", primarySource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            Assert.NotNull(server.HandleMessage(CreateInitializeRequestWithWorkspaceFolder(secondaryRoot, 35372)));

            var response = server.HandleMessage(CreateDefinitionRequest(
                secondaryPath,
                35373,
                0,
                secondarySource.IndexOf("Needle();", StringComparison.Ordinal)));

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(secondaryRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_KeepsRelativeResultUriAtProjectRoot_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_relative_result_primary");
        var secondaryRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_relative_result_secondary");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var targetPath = Path.Combine(projectRoot, "app.cs");
            var callerPath = Path.Combine(secondaryRoot, "caller.cs");
            var targetSource = "class App { void Needle() { } }\n";
            var callerSource = "class Caller { void Call() { Needle(); } }\n";
            File.WriteAllText(targetPath, targetSource);
            File.WriteAllText(callerPath, callerSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", targetSource);
            TestProjectHelper.InsertIndexedFile(dbPath, callerPath, "csharp", callerSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            Assert.NotNull(server.HandleMessage(CreateInitializeRequestWithWorkspaceFolder(secondaryRoot, 35376)));

            var response = server.HandleMessage(CreateDefinitionRequest(
                callerPath,
                35377,
                0,
                callerSource.IndexOf("Needle();", StringComparison.Ordinal)));

            Assert.NotNull(response);
            var locations = response!["result"]!.AsArray();
            Assert.Contains(locations, location => location?["uri"]?.GetValue<string>() == new Uri(targetPath).AbsoluteUri);
            Assert.DoesNotContain(locations, location => location?["uri"]?.GetValue<string>() == new Uri(Path.Combine(secondaryRoot, "app.cs")).AbsoluteUri);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(secondaryRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_PrefersCurrentIndexedDocumentForCommonToken()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_common_token");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var alphaPath = Path.Combine(projectRoot, "alpha.cs");
            var betaPath = Path.Combine(projectRoot, "beta.cs");
            var alphaSource = """
                class Alpha
                {
                    void Run() { }
                    void Call() { var alpha = new Alpha(); alpha.Run(); }
                }
                """;
            var betaSource = """
                class Beta
                {
                    void Run() { }
                    void Call() { var beta = new Beta(); beta.Run(); }
                }
                """;
            File.WriteAllText(alphaPath, alphaSource);
            File.WriteAllText(betaPath, betaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "alpha.cs", "csharp", alphaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "beta.cs", "csharp", betaSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(betaPath, 31, 3, CharacterOf(betaSource, 3, "Run();"));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var locations = response!["result"]!.AsArray();
            var location = Assert.Single(locations);
            Assert.Equal(new Uri(betaPath).AbsoluteUri, location!["uri"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ReturnsMultipleWorkspaceCandidates_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_multiple_candidates");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var alphaPath = Path.Combine(projectRoot, "alpha.cs");
            var betaPath = Path.Combine(projectRoot, "beta.cs");
            var callerPath = Path.Combine(projectRoot, "caller.cs");
            var alphaSource = "class Alpha { void Shared() { } }\n";
            var betaSource = "class Beta { void Shared() { } }\n";
            var callerSource = "class Caller { void Call() { Shared(); } }\n";
            File.WriteAllText(alphaPath, alphaSource);
            File.WriteAllText(betaPath, betaSource);
            File.WriteAllText(callerPath, callerSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "alpha.cs", "csharp", alphaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "beta.cs", "csharp", betaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "caller.cs", "csharp", callerSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(callerPath, 3537, 0, callerSource.IndexOf("Shared();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var uris = response!["result"]!
                .AsArray()
                .Select(location => location!["uri"]!.GetValue<string>())
                .ToArray();
            Assert.Contains(new Uri(alphaPath).AbsoluteUri, uris);
            Assert.Contains(new Uri(betaPath).AbsoluteUri, uris);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_References_PrefersCurrentIndexedDocumentForCommonToken()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_references_common_token");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var alphaPath = Path.Combine(projectRoot, "alpha.cs");
            var betaPath = Path.Combine(projectRoot, "beta.cs");
            var alphaSource = """
                class Worker { public Worker() { } }

                class Alpha
                {
                    void Call() { var worker = new Worker(); }
                }
                """;
            var betaSource = """
                class Worker { public Worker() { } }

                class Beta
                {
                    void Call() { var worker = new Worker(); }
                }
                """;
            File.WriteAllText(alphaPath, alphaSource);
            File.WriteAllText(betaPath, betaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "alpha.cs", "csharp", alphaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "beta.cs", "csharp", betaSource);
            MarkGraphReady(dbPath);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateReferencesRequest(betaPath, 32, 4, CharacterOf(betaSource, 4, "Worker();"));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var locations = response!["result"]!.AsArray();
            Assert.NotEmpty(locations);
            Assert.All(locations, location => Assert.Equal(new Uri(betaPath).AbsoluteUri, location!["uri"]!.GetValue<string>()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_References_HonorsIncludeDeclaration_Issue3537()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_references_include_declaration");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = """
                class App
                {
                    void Needle() { }
                    void Call() { Needle(); }
                }
                """;
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            MarkGraphReady(dbPath);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var character = CharacterOf(source, 3, "Needle();");
            var withoutDeclaration = CreateReferencesRequest(sourcePath, 3537, 3, character, includeDeclaration: false);
            var withDeclaration = CreateReferencesRequest(sourcePath, 3538, 3, character, includeDeclaration: true);

            var withoutResponse = server.HandleMessage(withoutDeclaration);
            var withResponse = server.HandleMessage(withDeclaration);

            Assert.NotNull(withoutResponse);
            Assert.NotNull(withResponse);
            var withoutLines = withoutResponse!["result"]!
                .AsArray()
                .Select(location => location!["range"]!["start"]!["line"]!.GetValue<int>())
                .ToArray();
            var withLines = withResponse!["result"]!
                .AsArray()
                .Select(location => location!["range"]!["start"]!["line"]!.GetValue<int>())
                .ToArray();
            Assert.DoesNotContain(2, withoutLines);
            Assert.Contains(2, withLines);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_References_PrefersCurrentIndexedDocumentWhenCommonTokenHasNoDefinitions()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_references_common_token_no_definition");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var alphaPath = Path.Combine(projectRoot, "alpha.cs");
            var betaPath = Path.Combine(projectRoot, "beta.cs");
            var alphaSource = """
                class Alpha
                {
                    void Call() { System.Console.WriteLine("alpha"); }
                }
                """;
            var betaSource = """
                class Beta
                {
                    void Call() { System.Console.WriteLine("beta"); }
                }
                """;
            File.WriteAllText(alphaPath, alphaSource);
            File.WriteAllText(betaPath, betaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "alpha.cs", "csharp", alphaSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "beta.cs", "csharp", betaSource);
            MarkGraphReady(dbPath);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateReferencesRequest(betaPath, 33, 2, CharacterOf(betaSource, 2, "WriteLine"));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var locations = response!["result"]!.AsArray();
            Assert.NotEmpty(locations);
            Assert.All(locations, location => Assert.Equal(new Uri(betaPath).AbsoluteUri, location!["uri"]!.GetValue<string>()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ReturnsEmptyForUnindexedDocument()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_unindexed");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var indexedPath = Path.Combine(projectRoot, "indexed.cs");
            var indexedSource = "class Indexed { void Needle() { } }\n";
            File.WriteAllText(indexedPath, indexedSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "indexed.cs", "csharp", indexedSource);
            var unindexedPath = Path.Combine(projectRoot, "unindexed.cs");
            var unindexedSource = "class Unindexed { void Call() { Needle(); } }\n";
            File.WriteAllText(unindexedPath, unindexedSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                unindexedPath,
                4,
                0,
                unindexedSource.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_UnindexedDocument_EmitsLookupFailureTrace_Issue3428()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_unindexed_trace");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var indexedPath = Path.Combine(projectRoot, "indexed.cs");
            var indexedSource = "class Indexed { void Needle() { } }\n";
            File.WriteAllText(indexedPath, indexedSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "indexed.cs", "csharp", indexedSource);
            var unindexedPath = Path.Combine(projectRoot, "unindexed.cs");
            var unindexedSource = "class Unindexed { void Call() { Needle(); } }\n";
            File.WriteAllText(unindexedPath, unindexedSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                unindexedPath,
                3428,
                0,
                unindexedSource.IndexOf("Needle();", StringComparison.Ordinal));
            var activities = new List<Activity>();
            using var listener = CaptureCodeIndexActivities(activities);

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
            var requestActivity = Assert.Single(activities.Where(activity => activity.OperationName == "lsp.request"));
            var failureEvent = Assert.Single(requestActivity.Events.Where(activityEvent => activityEvent.Name == "lsp.lookup_failed"));
            var tags = failureEvent.Tags.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString(), StringComparer.Ordinal);
            Assert.Equal("textDocument/definition", tags["lsp.method"]);
            Assert.Equal("file_not_indexed", tags["lsp.lookup.failure_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ReturnsEmptyForOutsideProjectDocument()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_project_root");
        var outsideRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_outside");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var indexedPath = Path.Combine(projectRoot, "app.cs");
            var indexedSource = "class Indexed { void Needle() { } }\n";
            File.WriteAllText(indexedPath, indexedSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", indexedSource);
            var outsidePath = Path.Combine(outsideRoot, "app.cs");
            var outsideSource = "class Outside { void Call() { Needle(); } }\n";
            File.WriteAllText(outsidePath, outsideSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                outsidePath,
                5,
                0,
                outsideSource.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(outsideRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ReturnsEmptyForOversizedIndexedDocument()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_oversized");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "huge.cs");
            var indexedSource = "class App { void Needle() { } }\n";
            TestProjectHelper.InsertIndexedFile(dbPath, "huge.cs", "csharp", indexedSource);
            var oversizedSource = "class App { void Call() { Needle(); } }\n" + new string('x', LspServer.MaxPositionDocumentBytes);
            File.WriteAllText(sourcePath, oversizedSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                sourcePath,
                6,
                0,
                oversizedSource.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ReturnsEmptyForLineOverPositionBudget_Issue3136()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_long_line");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "long_line.cs");
            var indexedSource = "class App { void Needle() { } void Call() { Needle(); } }\n";
            TestProjectHelper.InsertIndexedFile(dbPath, "long_line.cs", "csharp", indexedSource);
            var oversizedLine = new string('x', LspServer.MaxPositionLineChars + 1) + " Needle();\n";
            File.WriteAllText(sourcePath, oversizedLine);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                sourcePath,
                3136,
                0,
                oversizedLine.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_HonorsCaseInsensitiveWorkspaceCasing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_case_insensitive");
        try
        {
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase: true);
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "src", "Foo.cs");
            var requestPath = Path.Combine(projectRoot, "src", "foo.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var source = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Foo.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                requestPath,
                8,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.NotEmpty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_RejectsCaseVariantWhenWorkspaceCaseSensitive()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_case_sensitive");
        try
        {
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase: false);
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "src", "Foo.cs");
            var requestPath = Path.Combine(projectRoot, "src", "foo.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var source = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Foo.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                requestPath,
                9,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_ResolvesIndexedDocumentBeyondBasenameCandidateCap()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_many_basenames");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 1001; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/{i:D4}/index.cs",
                    "csharp",
                    $"class Filler{i} {{ }}\n");
            }

            var targetRelativePath = "src/zzzz/index.cs";
            var sourcePath = Path.Combine(projectRoot, "src", "zzzz", "index.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var source = "class Target { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, targetRelativePath, "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                sourcePath,
                7,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.NotEmpty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_BasenameFallbackHonorsCandidateCap_Issue3137()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_bounded_basename");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < LspServer.MaxDocumentPathFallbackCandidates; i++)
            {
                var fillerPath = Path.Combine(projectRoot, "src", i.ToString("D4", CultureInfo.InvariantCulture), "index.cs");
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    fillerPath,
                    "csharp",
                    $"class Filler{i} {{ void Needle() {{ }} }}\n");
            }

            var targetPath = Path.Combine(projectRoot, "src", "9999", "index.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var source = "class Target { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(targetPath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, targetPath, "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions());
            var request = CreateDefinitionRequest(
                targetPath,
                3137,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_RootlessRejectsRelativeIndexedPathWithoutWorkspace_Issue3426()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_rootless_relative");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var source = "class Target { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions());
            var request = CreateDefinitionRequest(
                sourcePath,
                3426,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));
            var activities = new List<Activity>();
            using var listener = CaptureCodeIndexActivities(activities);

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
            var requestActivity = Assert.Single(activities.Where(activity => activity.OperationName == "lsp.request"));
            var failureEvent = Assert.Single(requestActivity.Events.Where(activityEvent => activityEvent.Name == "lsp.lookup_failed"));
            var tags = failureEvent.Tags.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString(), StringComparer.Ordinal);
            Assert.Equal("file_not_indexed", tags["lsp.lookup.failure_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Definition_RootlessUsesWorkspaceFolderForRelativeIndexedPath_Issue3426()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_rootless_workspace");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var source = "class Target { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", source);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions());
            Assert.NotNull(server.HandleMessage(CreateInitializeRequestWithWorkspaceFolder(projectRoot, 34260)));
            var request = CreateDefinitionRequest(
                sourcePath,
                34261,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.NotEmpty(response!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateDefinitionRequest(string sourcePath, int id, int line, int character) =>
        CreatePositionRequest("textDocument/definition", sourcePath, id, line, character);

    private static string CreateInitializeRequestWithWorkspaceFolder(string workspaceRoot, int id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new
            {
                workspaceFolders = new[]
                {
                    new { uri = new Uri(workspaceRoot).AbsoluteUri, name = "workspace" },
                },
            },
        });

    private static string CreatePositionRequest(string method, string sourcePath, int id, int line, int character) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                position = new { line, character },
            },
        });

    private static string CreateTextDocumentRequest(string method, string sourcePath, int id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
            },
        });

    private static string CreateDidOpenRequest(string sourcePath, string text, int version) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new
                {
                    uri = new Uri(sourcePath).AbsoluteUri,
                    languageId = "csharp",
                    version,
                    text,
                },
            },
        });

    private static string CreateDidChangeRequest(string sourcePath, string text, int version) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new
                {
                    uri = new Uri(sourcePath).AbsoluteUri,
                    version,
                },
                contentChanges = new[]
                {
                    new { text },
                },
            },
        });

    private static string CreateDidCloseRequest(string sourcePath) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didClose",
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
            },
        });

    private static string Frame(string payload) =>
        $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";

    private static string CreateReferencesRequest(string sourcePath, int id, int line, int character, bool includeDeclaration = false) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "textDocument/references",
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                position = new { line, character },
                context = new { includeDeclaration },
            },
        });

    private static int CharacterOf(string source, int line, string value)
    {
        var lines = source.Split('\n');
        return lines[line].IndexOf(value, StringComparison.Ordinal);
    }

    private static IEnumerable<JsonNode?> FlattenDocumentSymbols(JsonArray symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return symbol;
            if (symbol?["children"] is JsonArray children)
            {
                foreach (var child in FlattenDocumentSymbols(children))
                    yield return child;
            }
        }
    }

    private static object? GetActivityTag(Activity activity, string key) =>
        activity.TagObjects.FirstOrDefault(tag => tag.Key == key).Value;

    private static string BuildNestedLspRequest(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":""");
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append("""{"next":""");

        builder.Append('0');

        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append('}');
        builder.Append('}');
        return builder.ToString();
    }

    private static ActivityListener CaptureCodeIndexActivities(List<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CodeIndexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void MarkGraphReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
    }
}
