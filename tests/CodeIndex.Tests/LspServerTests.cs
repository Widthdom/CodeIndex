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
    public void TryReadMessage_RejectsFrameOverMaxLength()
    {
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {LspServer.MaxLspFrameBytes + 1}\r\n\r\n");
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
