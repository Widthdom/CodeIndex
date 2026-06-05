using System.Globalization;
using System.Text;
using System.Text.Json;
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
            Assert.True(response!["result"]!["capabilities"]!["definitionProvider"]!.GetValue<bool>());
            Assert.True(response["result"]!["capabilities"]!["documentSymbolProvider"]!.GetValue<bool>());
            Assert.Equal("cdidx", response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
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
                method = "textDocument/hover",
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
            Assert.Equal("Method not found: textDocument/hover", response["error"]!["message"]!.GetValue<string>());
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
            Assert.Contains(symbols, symbol => symbol?["name"]?.GetValue<string>() == "App");
            Assert.Contains(symbols, symbol => symbol?["name"]?.GetValue<string>() == "Needle");
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

    [Fact]
    public void HandleMessage_DocumentSymbol_TruncatesDetailsAndCapsResponse_Issue3130()
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
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
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
            Assert.True(Encoding.UTF8.GetByteCount(symbols.ToJsonString()) <= LspServer.MaxDocumentSymbolResponseBytes);
            Assert.Contains(symbols, symbol =>
            {
                var detail = symbol?["detail"]?.GetValue<string>();
                return detail is { Length: <= LspServer.MaxDocumentSymbolDetailChars }
                    && detail.EndsWith("...", StringComparison.Ordinal);
            });
            Assert.All(symbols, symbol =>
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

    private static string CreateDefinitionRequest(string sourcePath, int id, int line, int character) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "textDocument/definition",
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                position = new { line, character },
            },
        });

    private static string Frame(string payload) =>
        $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";

    private static string CreateReferencesRequest(string sourcePath, int id, int line, int character) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "textDocument/references",
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                position = new { line, character },
            },
        });

    private static int CharacterOf(string source, int line, string value)
    {
        var lines = source.Split('\n');
        return lines[line].IndexOf(value, StringComparison.Ordinal);
    }

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

    private static void MarkGraphReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
    }
}
