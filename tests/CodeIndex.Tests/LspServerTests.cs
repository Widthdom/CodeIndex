using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Lsp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class LspServerTests
{
    [Fact]
    public void ExtractTokenAtUtf16Position_ReturnsIdentifierUnderCursor()
    {
        Assert.Equal("Needle", LspServer.ExtractTokenAtUtf16Position("var value = Needle.Call();", 14));
        Assert.Equal("Needle", LspServer.ExtractTokenAtUtf16Position("var value = Needle.Call();", 18));
    }

    [Fact]
    public void TryReadAllPositionLinesFromFile_PreservesUtf8AndRejectsGrowthAfterLengthCheck_Issue4750()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_growing_position_file");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            var unicodePath = Path.Combine(projectRoot, "unicode.cs");
            var unicodeText = new string('x', BoundedFile.SmallReadBufferSize - 1) + "日本語\r\n次の行\n";
            File.WriteAllText(unicodePath, unicodeText, utf8);

            Assert.True(LspServer.TryReadAllPositionLinesFromFile(
                unicodePath,
                out var unicodeLines,
                out var unicodeFailureReason));
            Assert.Null(unicodeFailureReason);
            Assert.Equal(
                new string?[] { new string('x', BoundedFile.SmallReadBufferSize - 1) + "日本語", "次の行", string.Empty },
                unicodeLines);

            const string prefix = "日本語\n";
            var growingPath = Path.Combine(projectRoot, "growing.cs");
            var initialText = prefix + new string(
                'x',
                LspServer.MaxPositionDocumentBytes - 1 - utf8.GetByteCount(prefix));
            File.WriteAllText(growingPath, initialText, utf8);
            Assert.Equal(LspServer.MaxPositionDocumentBytes - 1, new FileInfo(growingPath).Length);
            LspServer.PositionFileLengthCheckedForTesting = checkedPath =>
            {
                if (checkedPath == growingPath)
                    File.AppendAllText(checkedPath, "界", utf8);
            };

            Assert.False(LspServer.TryReadAllPositionLinesFromFile(
                growingPath,
                out var growingLines,
                out var growingFailureReason));
            Assert.Empty(growingLines);
            Assert.Equal("position_file_too_large", growingFailureReason);
        }
        finally
        {
            LspServer.PositionFileLengthCheckedForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
    public void TryReadMessage_ReadsDirectAllocatedPayloadAbovePoolThreshold_Issue3799()
    {
        var payload = new string('x', LspServer.MaxPooledPayloadBufferBytes + 1);
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}");
        using var stream = new MemoryStream(bytes);

        Assert.True(LspServer.TryReadMessage(stream, out var actual));

        Assert.Equal(payload.Length, actual.Length);
        Assert.Equal(payload, actual);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(LspServer.MaxPooledPayloadBufferBytes, true)]
    [InlineData(LspServer.MaxPooledPayloadBufferBytes + 1, false)]
    public void ShouldRentPayloadBuffer_DefinesPoolBoundary_Issue3799(int byteCount, bool expected)
    {
        Assert.Equal(expected, LspServer.ShouldRentPayloadBuffer(byteCount));
    }

    [Fact]
    public void ClearSensitivePayloadBufferForTests_ClearsOnlyUsedBytes_Issue3799()
    {
        var buffer = new byte[] { 1, 2, 3, 4, 5 };

        LspServer.ClearSensitivePayloadBufferForTests(buffer, usedBytes: 3);

        Assert.Equal(new byte[] { 0, 0, 0, 4, 5 }, buffer);
    }

    [Fact]
    public void ClearSensitivePayloadBufferForTests_ClampsUsedBytesToBufferLength_Issue3989()
    {
        var buffer = new byte[] { 1, 2, 3 };

        LspServer.ClearSensitivePayloadBufferForTests(buffer, usedBytes: 99);

        Assert.Equal(new byte[] { 0, 0, 0 }, buffer);
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

        Assert.False(LspServer.TryReadMessage(stream, out var actual, out var diagnostic));
        Assert.Equal(string.Empty, actual);
        Assert.NotNull(diagnostic);
        Assert.Equal(LspServer.ReadDiagnosticContentLengthTooLarge, diagnostic.Value.Code);
        Assert.Equal(LspServer.MaxLspFrameBytes + 1, diagnostic.Value.ContentLength);
        Assert.Equal(LspServer.MaxLspFrameBytes, diagnostic.Value.MaxContentLength);
    }

    [Theory]
    [InlineData("2", "2")]
    [InlineData("2", "3")]
    public void TryReadMessage_RejectsDuplicateContentLength_Issue3229(string firstLength, string secondLength)
    {
        var bytes = Encoding.UTF8.GetBytes($"Content-Length: {firstLength}\r\nContent-Length: {secondLength}\r\n\r\n{{}}");
        using var stream = new MemoryStream(bytes);

        Assert.False(LspServer.TryReadMessage(stream, out var actual, out var diagnostic));
        Assert.Equal(string.Empty, actual);
        Assert.NotNull(diagnostic);
        Assert.Equal(LspServer.ReadDiagnosticDuplicateContentLength, diagnostic.Value.Code);
    }

    [Fact]
    public void TryReadMessage_RejectsNegativeContentLength_Issue3757()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("Content-Length: -1\r\n\r\n"));

        Assert.False(LspServer.TryReadMessage(stream, out var actual, out var diagnostic));

        Assert.Equal(string.Empty, actual);
        Assert.NotNull(diagnostic);
        Assert.Equal(LspServer.ReadDiagnosticNegativeContentLength, diagnostic.Value.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+2")]
    public void TryReadMessage_RejectsMalformedContentLength_Issue3757(string value)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"Content-Length: {value}\r\n\r\n"));

        Assert.False(LspServer.TryReadMessage(stream, out var actual, out var diagnostic));

        Assert.Equal(string.Empty, actual);
        Assert.NotNull(diagnostic);
        Assert.Equal(LspServer.ReadDiagnosticMalformedContentLength, diagnostic.Value.Code);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    public async Task TryReadMessageAsync_CancellationDuringPendingRead_ThrowsOperationCanceled_Issue3769()
    {
        using var stream = new PendingReadStream();
        using var cts = new CancellationTokenSource();

        var readTask = LspServer.TryReadMessageAsync(stream, cts.Token).AsTask();
        await stream.WaitForReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readTask);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringPendingRead_ThrowsOperationCanceled_Issue3769()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_pending_cancel");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            using var input = new PendingReadStream();
            using var output = new MemoryStream();
            using var cts = new CancellationTokenSource();

            var runTask = server.RunAsync(input, output, cts.Token);
            await input.WaitForReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");

            Assert.NotNull(response);
            var capabilities = response!["result"]!["capabilities"]!;
            Assert.True(capabilities["definitionProvider"]!.GetValue<bool>());
            Assert.True(capabilities["declarationProvider"]!.GetValue<bool>());
            Assert.Null(capabilities["typeDefinitionProvider"]);
            Assert.Null(capabilities["implementationProvider"]);
            Assert.True(capabilities["documentSymbolProvider"]!["workDoneProgress"]!.GetValue<bool>());
            Assert.True(capabilities["workspaceSymbolProvider"]!["workDoneProgress"]!.GetValue<bool>());
            Assert.True(capabilities["hoverProvider"]!.GetValue<bool>());
            Assert.True(capabilities["documentHighlightProvider"]!.GetValue<bool>());
            Assert.Equal(1, capabilities["textDocumentSync"]!["change"]!.GetValue<int>());
            Assert.True(capabilities["textDocumentSync"]!["openClose"]!.GetValue<bool>());
            Assert.False(capabilities["completionProvider"]!["resolveProvider"]!.GetValue<bool>());
            Assert.Null(capabilities["codeLensProvider"]);
            Assert.False(capabilities["inlayHintProvider"]!["resolveProvider"]!.GetValue<bool>());
            Assert.Null(capabilities["renameProvider"]);
            Assert.Null(capabilities["foldingRangeProvider"]);
            Assert.Null(capabilities["selectionRangeProvider"]);
            Assert.Null(capabilities["signatureHelpProvider"]);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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

    [Theory]
    [InlineData("textDocument/typeDefinition")]
    [InlineData("textDocument/implementation")]
    [InlineData("textDocument/codeLens")]
    [InlineData("textDocument/prepareRename")]
    [InlineData("textDocument/rename")]
    [InlineData("textDocument/foldingRange")]
    [InlineData("textDocument/selectionRange")]
    [InlineData("textDocument/signatureHelp")]
    public void HandleMessage_UnsupportedOptionalMethods_ReturnMethodNotFound_Issues4360And4420And4465(string method)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_optional_methods");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "class App { void Run() { } }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", File.ReadAllText(sourcePath));
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreatePositionRequest(method, sourcePath, 4360, 0, 6);

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32601, response!["error"]!["code"]!.GetValue<int>());
            Assert.Equal("Method not found: " + method, response["error"]!["message"]!.GetValue<string>());
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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

            var inlayHints = server.HandleMessage(CreateTextDocumentRequest("textDocument/inlayHint", sourcePath, 35367));
            Assert.NotNull(inlayHints);
            Assert.True(inlayHints!["error"] is null, inlayHints["error"]?.ToJsonString());
            Assert.Empty(inlayHints!["result"]!.AsArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_SemanticTokens_ClassifiesCSharpKeywordsModifiersAndDeclarations_Issue4444()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_semantic_tokens");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = string.Join('\n',
            [
                "using Text = System.Text;",
                "namespace Sample.Tools { internal sealed class App",
                "{",
                "    private const int Count = 1;",
                "    public void Run() { }",
                "    private string Raw = \"\"\"",
                "public class RawContent",
                "\"\"\";",
                "    private string Verbatim = @\"start",
                "private sealed VerbatimContent\";",
                "}",
                "}",
            ]);
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage(CreateTextDocumentRequest("textDocument/semanticTokens/full", sourcePath, 4444));

            Assert.NotNull(response);
            var tokens = DecodeSemanticTokens(response!["result"]!["data"]!.AsArray(), source);
            AssertSemanticToken(tokens, 0, "using", 15, 0);
            AssertSemanticToken(tokens, 0, "System", 0, 0);
            AssertSemanticToken(tokens, 0, "Text", 0, 0);
            AssertSemanticToken(tokens, 1, "namespace", 15, 0);
            AssertSemanticToken(tokens, 1, "internal", 16, 0);
            AssertSemanticToken(tokens, 1, "sealed", 16, 0);
            AssertSemanticToken(tokens, 1, "class", 15, 0);
            AssertSemanticToken(tokens, 1, "App", 2, 1);
            AssertSemanticToken(tokens, 3, "private", 16, 0);
            AssertSemanticToken(tokens, 3, "const", 16, 0);
            AssertSemanticToken(tokens, 3, "Count", 23, 1);
            AssertSemanticToken(tokens, 4, "Run", 13, 1);
            Assert.DoesNotContain(tokens, token => token.Line is 6 or 9);
            Assert.DoesNotContain(tokens.SelectMany((left, index) => tokens.Skip(index + 1).Select(right => (left, right))), pair =>
                pair.left.Line == pair.right.Line &&
                pair.left.Character < pair.right.Character + pair.right.Text.Length &&
                pair.right.Character < pair.left.Character + pair.left.Text.Length);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_InlayHint_HonorsRangeAndSuppressesExplicitTypes_Issue4418()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_inlay_hint_range");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var sourceBuilder = new StringBuilder();
            var symbols = new List<SymbolRecord>();
            for (var index = 0; index < 1003; index++)
            {
                var name = $"value{index:D4}";
                sourceBuilder.Append("    ").Append(name).Append(" = ").Append(index).AppendLine(";");
                symbols.Add(new SymbolRecord
                {
                    Kind = "field",
                    Name = name,
                    Line = index + 1,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    ReturnType = "int",
                });
            }
            var source = sourceBuilder.ToString();
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using (var fixtureDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var fileIdCommand = fixtureDb.Connection.CreateCommand();
                fileIdCommand.CommandText = "SELECT id FROM files WHERE path = 'app.cs'";
                var fileId = Convert.ToInt64(fileIdCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                var writer = new DbWriter(fixtureDb.Connection);
                foreach (var symbol in symbols)
                    symbol.FileId = fileId;
                writer.InsertSymbols(symbols);
            }
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage(CreateInlayHintRequest(sourcePath, 4418, 1001, 0, 1002, 0));

            Assert.NotNull(response);
            Assert.True(response!["error"] is null, response["error"]?.ToJsonString());
            var hint = Assert.Single(response!["result"]!.AsArray());
            Assert.Equal(1001, hint!["position"]!["line"]!.GetValue<int>());
            Assert.Equal(13, hint["position"]!["character"]!.GetValue<int>());
            Assert.Equal(": int", hint["label"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Completion_ReturnsEmptyListWhenNoIndexedSymbolMatches_Issue4360()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_completion_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = """
                public class App
                {
                    public int Count() { return 1; }
                    public void Call() { MissingPrefix(); }
                }
                """;
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var missingCharacter = CharacterOf(source, 3, "MissingPrefix();") + 3;

            var completion = server.HandleMessage(CreatePositionRequest("textDocument/completion", sourcePath, 43601, 3, missingCharacter));

            Assert.NotNull(completion);
            Assert.False(completion!["result"]!["isIncomplete"]!.GetValue<bool>());
            Assert.Empty(completion["result"]!["items"]!.AsArray());
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var response = server.HandleMessage(BuildNestedLspRequest(LspServer.MaxJsonDepth + 1));

            Assert.NotNull(response);
            Assert.Equal(-32700, response!["error"]!["code"]!.GetValue<int>());
            Assert.Null(response["id"]);
            var message = response["error"]!["message"]!.GetValue<string>();
            Assert.Contains("payload_bytes=", message, StringComparison.Ordinal);
            Assert.Contains($"max_json_depth={LspServer.MaxJsonDepth}", message, StringComparison.Ordinal);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    public void HandleMessage_OwnedQuerySnapshotRefreshesAfterExternalWalCommit_Issue4557()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_query_snapshot_refresh");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var queryDb = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var server = new LspServer(queryDb, dbPath, "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4557,
                method = "workspace/symbol",
                @params = new { query = "AddedAfterLspStart" },
            });

            var before = server.HandleMessage(request);
            Assert.NotNull(before);
            Assert.Empty(before!["result"]!.AsArray());

            // Keep a source connection alive through the comparison so SQLite cannot
            // perform last-connection WAL cleanup independently of the query request.
            using var writerDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(writerDb.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "src/AddedAfterLspStart.cs",
                Lang = "csharp",
                Size = 1,
                Lines = 1,
                Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Checksum = "issue4557-lsp-refresh",
            });
            writer.InsertSymbols([
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = "AddedAfterLspStart",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                },
            ]);

            var expectedArtifacts = CaptureDatabaseArtifactsForLsp(dbPath);
            var after = server.HandleMessage(request);

            Assert.NotNull(after);
            var symbol = Assert.Single(after!["result"]!.AsArray());
            Assert.Equal("AddedAfterLspStart", symbol!["name"]!.GetValue<string>());
            Assert.Equal(expectedArtifacts, CaptureDatabaseArtifactsForLsp(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static Dictionary<string, (long Length, DateTime LastWriteTimeUtc, string Sha256)> CaptureDatabaseArtifactsForLsp(string dbPath)
    {
        var result = new Dictionary<string, (long, DateTime, string)>(StringComparer.Ordinal);
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (!File.Exists(path))
                continue;
            var info = new FileInfo(path);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            result[Path.GetFileName(path)] = (
                info.Length,
                info.LastWriteTimeUtc,
                Convert.ToHexString(SHA256.HashData(stream)));
        }

        return result;
    }

    [Fact]
    public void HandleMessage_WorkspaceSymbol_RejectsOversizedQuery_Issue3128()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_symbol_long_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    public void Run_DocumentSymbol_StreamsBoundedPartialResultsAndWorkDoneProgress_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_progress");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "progress.cs");
            var source = new StringBuilder("class ProgressSymbols\n{\n");
            for (var i = 0; i < LspServer.MaxSymbolProgressChunkItems; i++)
                source.Append("    void Method").Append(i.ToString("D3", CultureInfo.InvariantCulture)).Append("() { }\n");
            source.Append("}\n");
            File.WriteAllText(sourcePath, source.ToString());
            TestProjectHelper.InsertIndexedFile(dbPath, "progress.cs", "csharp", source.ToString());

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4721,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                    partialResultToken = "document-partial-4721",
                    workDoneToken = 4721,
                },
            });
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(request)));
            using var output = new MemoryStream();

            var exitCode = server.Run(input, output);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var messages = ReadLspMessages(output);
            var partialMessages = messages
                .Where(entry => HasProgressToken(entry.Message, "document-partial-4721"))
                .ToArray();
            Assert.True(partialMessages.Length > 1);
            Assert.All(partialMessages, entry =>
            {
                Assert.InRange(entry.Message["params"]!["value"]!.AsArray().Count, 1, LspServer.MaxSymbolProgressChunkItems);
                Assert.InRange(entry.BodyBytes, 1, LspServer.MaxSymbolProgressChunkBytes);
            });

            var names = partialMessages
                .SelectMany(entry => entry.Message["params"]!["value"]!.AsArray())
                .Select(symbol => symbol!["name"]!.GetValue<string>())
                .ToArray();
            var expectedNames = new[] { "ProgressSymbols" }
                .Concat(Enumerable.Range(0, LspServer.MaxSymbolProgressChunkItems)
                    .Select(i => $"Method{i:D3}"))
                .ToArray();
            Assert.Equal(expectedNames, names);

            var workDoneValues = messages
                .Where(entry => HasProgressToken(entry.Message, 4721))
                .Select(entry => entry.Message["params"]!["value"]!.AsObject())
                .ToArray();
            Assert.Equal("begin", workDoneValues[0]["kind"]!.GetValue<string>());
            Assert.Contains(workDoneValues, value => value["kind"]!.GetValue<string>() == "report");
            Assert.Equal("end", workDoneValues[^1]["kind"]!.GetValue<string>());
            Assert.Contains("Returned 101 symbols", workDoneValues[^1]["message"]!.GetValue<string>(), StringComparison.Ordinal);

            var response = Assert.Single(messages, entry => entry.Message["id"]?.GetValue<int>() == 4721);
            Assert.True(response.Message.ContainsKey("result"));
            Assert.Null(response.Message["result"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_DocumentSymbol_WritesWorkDoneBeginBeforeSymbolWorkCompletes_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_live_progress");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot)
            {
                BeforeSymbolRequestForTesting = cancellationToken =>
                {
                    entered.Set();
                    release.Wait(cancellationToken);
                },
            };
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 47213,
                method = "workspace/symbol",
                @params = new
                {
                    query = "",
                    workDoneToken = "live-progress-4721",
                },
            });
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(request)));
            using var output = new SignalingMemoryStream();

            var runTask = server.RunAsync(input, output);

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await output.WaitForWriteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(runTask.IsCompleted);
            release.Set();
            Assert.Equal(CommandExitCodes.Success, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));

            var messages = ReadLspMessages(output);
            var progress = messages
                .Where(entry => HasProgressToken(entry.Message, "live-progress-4721"))
                .Select(entry => entry.Message["params"]!["value"]!["kind"]!.GetValue<string>())
                .ToArray();
            Assert.Equal("begin", progress[0]);
            Assert.Equal("end", progress[^1]);
        }
        finally
        {
            release.Set();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WorkspaceSymbol_SurfacesPartialResultTruncation_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_workspace_symbol_progress");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < 3; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"needle{i}.cs",
                    "csharp",
                    $"class Needle{i} {{ }}\n");
            }

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 47210,
                method = "workspace/symbol",
                @params = new
                {
                    query = "Needle",
                    limit = 2,
                    partialResultToken = "workspace-partial-4721",
                },
            });
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(request)));
            using var output = new MemoryStream();

            Assert.Equal(CommandExitCodes.Success, server.Run(input, output));

            var messages = ReadLspMessages(output);
            var partial = Assert.Single(messages, entry => HasProgressToken(entry.Message, "workspace-partial-4721"));
            Assert.Equal(
                ["Needle0", "Needle1"],
                partial.Message["params"]!["value"]!.AsArray()
                    .Select(symbol => symbol!["name"]!.GetValue<string>())
                    .ToArray());
            var warning = Assert.Single(
                messages,
                entry => entry.Message["method"]?.GetValue<string>() == "window/logMessage");
            Assert.Contains(
                "truncated",
                warning.Message["params"]!["message"]!.GetValue<string>(),
                StringComparison.OrdinalIgnoreCase);
            var response = Assert.Single(messages, entry => entry.Message["id"]?.GetValue<int>() == 47210);
            Assert.True(response.Message.ContainsKey("result"));
            Assert.Null(response.Message["result"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DocumentSymbol_CancelRequestEndsProgressAndReturnsCancellationError_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_cancel");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "cancel.cs");
            const string source = "class CancelSymbols { void Method() { } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "cancel.cs", "csharp", source);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "request-4721",
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                    partialResultToken = "partial-cancel-4721",
                    workDoneToken = "work-cancel-4721",
                },
            });
            var cancel = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "$/cancelRequest",
                @params = new { id = "request-4721" },
            });
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(Frame(request) + Frame(cancel)));
            using var output = new MemoryStream();

            Assert.Equal(CommandExitCodes.Success, server.Run(input, output));

            var messages = ReadLspMessages(output);
            var workDoneValues = messages
                .Where(entry => HasProgressToken(entry.Message, "work-cancel-4721"))
                .Select(entry => entry.Message["params"]!["value"]!.AsObject())
                .ToArray();
            Assert.Equal(["begin", "end"], workDoneValues.Select(value => value["kind"]!.GetValue<string>()).ToArray());
            Assert.Contains("Cancelled", workDoneValues[^1]["message"]!.GetValue<string>(), StringComparison.Ordinal);
            var response = Assert.Single(
                messages,
                entry => entry.Message["id"]?.GetValue<string>() == "request-4721");
            Assert.Equal(-32800, response.Message["error"]!["code"]!.GetValue<int>());
            Assert.Equal("Request cancelled", response.Message["error"]!["message"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_CancelRequestBypassesFullInboundQueue_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_cancel_full_queue");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot)
            {
                BeforeSymbolRequestForTesting = cancellationToken =>
                {
                    Assert.True(
                        cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)),
                        "The active request was not cancelled after output backpressure was released.");
                    cancellationToken.ThrowIfCancellationRequested();
                },
            };
            var frames = new StringBuilder();
            frames.Append(Frame(
                """
                {"jsonrpc":"2.0","id":"active-4721","method":"workspace/symbol","params":{"query":""}}
                """));
            for (var i = 0; i < 40; i++)
            {
                frames.Append(Frame(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 5000 + i,
                    method = "initialize",
                    @params = new { },
                })));
            }
            frames.Append(Frame(
                """
                {"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"active-4721"}}
                """));
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames.ToString()));
            using var output = new MemoryStream();

            Assert.Equal(CommandExitCodes.Success, server.Run(input, output));

            var messages = ReadLspMessages(output);
            var cancelled = Assert.Single(
                messages,
                entry => entry.Message["id"] is JsonValue id
                    && id.TryGetValue<string>(out var value)
                    && value == "active-4721");
            Assert.Equal(-32800, cancelled.Message["error"]!["code"]!.GetValue<int>());
            Assert.Contains(
                messages,
                entry => entry.Message["error"]?["code"]?.GetValue<int>() == -32000);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_QueuePressurePreservesDocumentSyncNotifications_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_notification_full_queue");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "live.cs");
            const string initialSource = "class InitialLiveDocument { }\n";
            const string latestSource = "class LatestLiveDocument { void Method() { } }\n";
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot)
            {
                BeforeSymbolRequestForTesting = cancellationToken =>
                {
                    entered.Set();
                    release.Wait(cancellationToken);
                },
            };
            var frames = new StringBuilder();
            frames.Append(Frame(
                """
                {"jsonrpc":"2.0","id":"active-notification-4721","method":"workspace/symbol","params":{"query":""}}
                """));
            for (var i = 0; i < 40; i++)
            {
                frames.Append(Frame(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 6000 + i,
                    method = "initialize",
                    @params = new { },
                })));
            }
            frames.Append(Frame(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new
                {
                    textDocument = new
                    {
                        uri = new Uri(sourcePath).AbsoluteUri,
                        text = initialSource,
                    },
                },
            })));
            frames.Append(Frame(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "textDocument/didChange",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                    contentChanges = new[] { new { text = latestSource } },
                },
            })));
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(frames.ToString()));
            using var output = new SignalingMemoryStream();

            var runTask = server.RunAsync(input, output);

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await output.WaitForWriteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            Assert.Equal(CommandExitCodes.Success, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(Encoding.UTF8.GetByteCount(latestSource), server.LiveDocumentBytesForTests);
            var messages = ReadLspMessages(output);
            for (var id = 6000; id < 6040; id++)
            {
                Assert.Single(
                    messages,
                    entry => entry.Message["id"] is JsonValue responseId
                        && responseId.TryGetValue<int>(out var value)
                        && value == id);
            }
        }
        finally
        {
            release.Set();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_ServerBusyBackpressureRetainsEveryRejectedResponse_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_busy_response_backpressure");
        using var requestEntered = new ManualResetEventSlim();
        using var requestRelease = new ManualResetEventSlim();
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot)
            {
                BeforeSymbolRequestForTesting = cancellationToken =>
                {
                    requestEntered.Set();
                    requestRelease.Wait(cancellationToken);
                },
            };
            var frames = new StringBuilder();
            frames.Append(Frame(
                """
                {"jsonrpc":"2.0","id":"active-backpressure-4721","method":"workspace/symbol","params":{"query":""}}
                """));
            for (var i = 0; i < 50; i++)
            {
                frames.Append(Frame(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 7000 + i,
                    method = "initialize",
                    @params = new { },
                })));
            }
            const string cancel =
                """
                {"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"active-backpressure-4721"}}
                """;
            using var input = new StagedReadStream(
                Encoding.UTF8.GetBytes(frames.ToString()),
                Encoding.UTF8.GetBytes(Frame(cancel)));
            using var output = new FirstWriteGateMemoryStream();

            var runTask = server.RunAsync(input, output);

            Assert.True(requestEntered.Wait(TimeSpan.FromSeconds(5)));
            await output.WaitForBlockedWriteAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(runTask.IsCompleted);
            input.ReleaseSuffix();
            output.ReleaseWrites();
            Assert.Equal(CommandExitCodes.Success, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));

            var messages = ReadLspMessages(output);
            var activeResponse = Assert.Single(
                messages,
                entry => entry.Message["id"] is JsonValue id
                    && id.TryGetValue<string>(out var value)
                    && value == "active-backpressure-4721");
            Assert.Equal(-32800, activeResponse.Message["error"]!["code"]!.GetValue<int>());
            for (var id = 7000; id < 7050; id++)
            {
                Assert.Single(
                    messages,
                    entry => entry.Message["id"] is JsonValue responseId
                        && responseId.TryGetValue<int>(out var value)
                        && value == id);
            }
        }
        finally
        {
            requestRelease.Set();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_CancelledPartialResultsReportAlreadyEmittedCount_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_cancel_emitted_count");
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "cancel-count.cs");
            var source = new StringBuilder("class CancelCountSymbols\n{\n");
            for (var i = 0; i < LspServer.MaxSymbolProgressChunkItems * 2; i++)
                source.Append("    void Method").Append(i.ToString("D3", CultureInfo.InvariantCulture)).Append("() { }\n");
            source.Append("}\n");
            File.WriteAllText(sourcePath, source.ToString());
            TestProjectHelper.InsertIndexedFile(dbPath, "cancel-count.cs", "csharp", source.ToString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot)
            {
                BeforeSymbolRequestForTesting = cancellationToken =>
                {
                    cancellationToken.Register(() => cancellationObserved.TrySetResult());
                },
            };
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "cancel-count-request-4721",
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                    partialResultToken = "cancel-count-partial-4721",
                    workDoneToken = "cancel-count-work-4721",
                },
            });
            const string cancel =
                """
                {"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"cancel-count-request-4721"}}
                """;
            using var input = new StagedReadStream(
                Encoding.UTF8.GetBytes(Frame(request)),
                Encoding.UTF8.GetBytes(Frame(cancel)));
            using var output = new MarkerWriteGateMemoryStream("cancel-count-partial-4721");

            var runTask = server.RunAsync(input, output);

            await output.WaitForMarkerAsync().WaitAsync(TimeSpan.FromSeconds(5));
            input.ReleaseSuffix();
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            output.ReleaseMarker();
            Assert.Equal(CommandExitCodes.Success, await runTask.WaitAsync(TimeSpan.FromSeconds(5)));

            var messages = ReadLspMessages(output);
            var emittedCount = messages
                .Where(entry => HasProgressToken(entry.Message, "cancel-count-partial-4721"))
                .Sum(entry => entry.Message["params"]!["value"]!.AsArray().Count);
            Assert.True(emittedCount > 0);
            var workDoneEnd = messages
                .Where(entry => HasProgressToken(entry.Message, "cancel-count-work-4721"))
                .Select(entry => entry.Message["params"]!["value"]!.AsObject())
                .Last(value => value["kind"]!.GetValue<string>() == "end");
            Assert.Equal(
                $"Cancelled after {emittedCount} symbols.",
                workDoneEnd["message"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task RunAsync_ResponseWriteFailureCancelsPendingRead_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_output_failure");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            const string request = """{"jsonrpc":"2.0","id":47214,"method":"initialize","params":{}}""";
            using var input = new PrefixThenPendingReadStream(Encoding.UTF8.GetBytes(Frame(request)));
            using var output = new ThrowingWriteStream();

            var exception = await Assert.ThrowsAsync<IOException>(
                async () => await server.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal("Injected write failure.", exception.Message);
            await input.WaitForPendingReadAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_SymbolProgressTokensRejectUnboundedOrStructuredValues_Issue4721()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_symbol_progress_token");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var requests = new[]
            {
                """
                {"jsonrpc":"2.0","id":47211,"method":"workspace/symbol","params":{"query":"","partialResultToken":{}}}
                """,
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = 47212,
                    method = "workspace/symbol",
                    @params = new
                    {
                        query = "",
                        workDoneToken = new string('t', LspServer.MaxRequestIdStringChars + 1),
                    },
                }),
            };

            foreach (var request in requests)
            {
                var response = server.HandleMessage(request);
                Assert.NotNull(response);
                Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
                Assert.Equal("Invalid params", response["error"]!["message"]!.GetValue<string>());
            }
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            Assert.Equal(
                $"Parse error (payload_bytes=1, max_json_depth={LspServer.MaxJsonDepth})",
                parseError.RootElement.GetProperty("error").GetProperty("message").GetString());

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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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

    [Theory]
    [InlineData("record Z(int A) {} class B { record Z(int X) {} }\n")]
    [InlineData("class B { record Z(int X) {} } record Z(int A) {}\n")]
    public void HandleMessage_DocumentSymbol_DisambiguatesSameLineRecordContainersBySourceOrder_Issue4736(string source)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_qualified_record_members");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = TestProjectHelper.WriteTextFile(projectRoot, "App.java", source);
            TestProjectHelper.InsertIndexedFile(dbPath, "App.java", "java", source);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 47361,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var roots = response!["result"]!.AsArray();
            var topLevelZ = Assert.Single(roots.Where(symbol => symbol?["name"]?.GetValue<string>() == "Z"));
            Assert.Equal(
                ["A"],
                topLevelZ!["children"]!.AsArray().Select(child => child!["name"]!.GetValue<string>()));
            var b = Assert.Single(roots.Where(symbol => symbol?["name"]?.GetValue<string>() == "B"));
            var nestedZ = Assert.Single(b!["children"]!.AsArray());
            Assert.Equal("Z", nestedZ!["name"]!.GetValue<string>());
            Assert.Equal(
                ["X"],
                nestedZ["children"]!.AsArray().Select(child => child!["name"]!.GetValue<string>()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_NestsMixedRecordMembersDeterministically_Issue4736()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_record_members");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            const string source = "class Outer { readonly record struct Token(int Line, int Length) { int Body => Length; } }\n";
            var sourcePath = TestProjectHelper.WriteTextFile(projectRoot, "app.cs", source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 4736,
                method = "textDocument/documentSymbol",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                },
            });

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            var roots = response!["result"]!.AsArray();
            var outer = Assert.Single(roots);
            Assert.Equal("Outer", outer!["name"]!.GetValue<string>());
            var token = Assert.Single(outer["children"]!.AsArray());
            Assert.Equal("Token", token!["name"]!.GetValue<string>());
            Assert.Equal(
                ["Body", "Length", "Line"],
                token["children"]!.AsArray().Select(child => child!["name"]!.GetValue<string>()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_PreservesLiveHierarchyAcrossFullChanges_Issue4851()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_live_hierarchy");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var initialSource = string.Join(
                "\r\n",
                "namespace Ω",
                "{",
                "    public sealed class Outer",
                "    {",
                "        public readonly record struct Token(int Line, int 長さ)",
                "        {",
                "            public int Body => 長さ;",
                "            public sealed class Inner { }",
                "        }",
                "    }",
                "}",
                string.Empty);
            var shiftedLfSource = string.Join(
                "\n",
                "// full-change line shift",
                string.Empty,
                "namespace Ω",
                "{",
                "    public sealed class Outer",
                "    {",
                "        public readonly record struct Token(int Line, int 長さ)",
                "        {",
                "            public int Body => 長さ;",
                "            public sealed class Inner { }",
                "        }",
                "    }",
                "}",
                string.Empty);
            var newerCrLfSource = string.Join(
                "\r\n",
                "// newer full change",
                "namespace Ω",
                "{",
                "    public sealed class Outer",
                "    {",
                "        public readonly record struct Token(int Line, int 長さ)",
                "        {",
                "            public int Body => 長さ;",
                "            public string 追加 => \"値\";",
                "            public sealed class Inner { }",
                "        }",
                "    }",
                "}",
                string.Empty);
            var sourcePath = TestProjectHelper.WriteTextFile(projectRoot, "app.cs", initialSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", initialSource);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var indexedResponse = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 48509));
            Assert.NotNull(indexedResponse);
            var indexedRoots = indexedResponse!["result"]!.AsArray();
            var indexedStructure = GetDocumentSymbolStructure(indexedRoots);
            var indexedRecursiveCount = FlattenDocumentSymbols(indexedRoots).Count();

            Assert.Null(server.HandleMessage(CreateDidOpenRequest(sourcePath, initialSource, version: 1)));
            var initialResponse = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 48510));
            Assert.NotNull(initialResponse);
            var initialRoots = initialResponse!["result"]!.AsArray();
            var initialStructure = GetDocumentSymbolStructure(initialRoots);
            var initialRecursiveCount = FlattenDocumentSymbols(initialRoots).Count();
            Assert.Equal(indexedRoots.Count, initialRoots.Count);
            Assert.Equal(indexedRecursiveCount, initialRecursiveCount);
            Assert.Equal(indexedStructure, initialStructure);
            var initialToken = Assert.Single(
                FlattenDocumentSymbols(initialRoots)
                    .Where(symbol => symbol?["name"]?.GetValue<string>() == "Token"));
            var initialTokenLine = initialToken!["selectionRange"]!["start"]!["line"]!.GetValue<int>();
            var initialTokenChildren = initialToken["children"]!.AsArray();
            Assert.Contains(initialTokenChildren, child => child?["name"]?.GetValue<string>() == "Line");
            Assert.Contains(initialTokenChildren, child => child?["name"]?.GetValue<string>() == "長さ");

            Assert.Null(server.HandleMessage(CreateDidChangeRequest(sourcePath, shiftedLfSource, version: 2)));
            var shiftedResponse = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 48511));
            Assert.NotNull(shiftedResponse);
            var shiftedRoots = shiftedResponse!["result"]!.AsArray();
            Assert.Equal(initialRoots.Count, shiftedRoots.Count);
            Assert.Equal(initialRecursiveCount, FlattenDocumentSymbols(shiftedRoots).Count());
            Assert.Equal(initialStructure, GetDocumentSymbolStructure(shiftedRoots));
            var shiftedToken = Assert.Single(
                FlattenDocumentSymbols(shiftedRoots)
                    .Where(symbol => symbol?["name"]?.GetValue<string>() == "Token"));
            Assert.Equal(
                initialTokenLine + 2,
                shiftedToken!["selectionRange"]!["start"]!["line"]!.GetValue<int>());

            Assert.Null(server.HandleMessage(CreateDidChangeRequest(sourcePath, newerCrLfSource, version: 3)));
            var newerResponse = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 48512));
            Assert.NotNull(newerResponse);
            var newerRoots = newerResponse!["result"]!.AsArray();
            var newerStructure = GetDocumentSymbolStructure(newerRoots);
            Assert.Equal(initialRoots.Count, newerRoots.Count);
            Assert.Equal(initialRecursiveCount + 1, FlattenDocumentSymbols(newerRoots).Count());
            Assert.Contains(
                FlattenDocumentSymbols(newerRoots),
                symbol => symbol?["name"]?.GetValue<string>() == "追加");

            Assert.Null(server.HandleMessage(CreateDidChangeRequest(sourcePath, "class Stale { }\n", version: 2)));
            var staleResponse = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 48513));
            Assert.NotNull(staleResponse);
            Assert.Equal(newerStructure, GetDocumentSymbolStructure(staleResponse!["result"]!.AsArray()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_DocumentSymbol_UsesIndexedLanguageForLiveContentSensitiveExtension_Issue4851()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_live_indexed_language");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            const string indexedSource = "class IndexedType {\npublic:\n    void indexed();\n};\n";
            const string liveSource = "class LiveType {\npublic:\n    void live();\n};\n";
            var sourcePath = TestProjectHelper.WriteTextFile(projectRoot, "sample.h", indexedSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "sample.h", "cpp", indexedSource);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            Assert.Null(server.HandleMessage(CreateDidOpenRequest(sourcePath, indexedSource, version: 1)));
            Assert.Null(server.HandleMessage(CreateDidChangeRequest(sourcePath, liveSource, version: 2)));

            var response = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 48514));

            Assert.NotNull(response);
            var liveType = Assert.Single(response!["result"]!.AsArray());
            Assert.Equal("LiveType", liveType!["name"]!.GetValue<string>());
            var liveMethod = Assert.Single(liveType["children"]!.AsArray());
            Assert.Equal("live", liveMethod!["name"]!.GetValue<string>());
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
        const int responseBudget = 4 * 1024;
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_document_symbol_budget");
        LspServer.DocumentSymbolResponseBytesForTesting = responseBudget;
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "large.cs");
            var parameters = string.Join(", ", Enumerable.Range(0, 90).Select(i => $"int argument{i:D2}"));
            var source = new StringBuilder("class LargeSymbols\n{\n");
            for (var i = 0; i < 12; i++)
                source.Append("    void Method").Append(i.ToString("D4", CultureInfo.InvariantCulture)).Append('(').Append(parameters).Append(") { }\n");
            source.Append("}\n");

            File.WriteAllText(sourcePath, source.ToString());
            TestProjectHelper.InsertIndexedFile(dbPath, "large.cs", "csharp", source.ToString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            Assert.True(Encoding.UTF8.GetByteCount(symbols.ToJsonString(jsonOptions)) <= responseBudget);
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
            LspServer.DocumentSymbolResponseBytesForTesting = null;
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
            for (var i = 0; i < LspServer.MaxDocumentSymbolMaterialization + 1; i++)
                source.Append("    void Method").Append(i.ToString("D4", CultureInfo.InvariantCulture)).Append("() { }\n");
            source.Append("}\n");

            File.WriteAllText(sourcePath, source.ToString());
            TestProjectHelper.InsertIndexedFile(dbPath, "materialization.cs", "csharp", source.ToString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            var range = locations[0]!["range"]!;
            Assert.Equal(17, range["start"]!["character"]!.GetValue<int>());
            Assert.Equal(23, range["end"]!["character"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_PositionLookup_DisambiguatesClassFromSameNamedConstructor_Issue4443()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_class_constructor");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = """
                public class Widget
                {
                    public Widget() { }
                    private Widget? field;
                }
                """;
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            MarkGraphReady(dbPath);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var classCharacter = CharacterOf(source, 0, "Widget");

            var definition = server.HandleMessage(CreateDefinitionRequest(sourcePath, 44431, 0, classCharacter));
            var hover = server.HandleMessage(CreatePositionRequest("textDocument/hover", sourcePath, 44432, 0, classCharacter));
            var references = server.HandleMessage(CreateReferencesRequest(sourcePath, 44433, 0, classCharacter, includeDeclaration: true));

            Assert.NotNull(definition);
            var definitionLocation = Assert.Single(definition!["result"]!.AsArray());
            Assert.Equal(0, definitionLocation!["range"]!["start"]!["line"]!.GetValue<int>());

            Assert.NotNull(hover);
            var hoverText = hover!["result"]!["contents"]!["value"]!.GetValue<string>();
            Assert.Contains("class Widget", hoverText, StringComparison.Ordinal);
            Assert.DoesNotContain("Widget()", hoverText, StringComparison.Ordinal);

            Assert.NotNull(references);
            var referenceLocations = references!["result"]!.AsArray();
            Assert.NotEmpty(referenceLocations);
            Assert.DoesNotContain(referenceLocations, location => location!["range"]!["start"]!["line"]!.GetValue<int>() == 2);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Declaration_ReturnsDefinitionLocation_Issues3537And4420()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_alias");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreatePositionRequest(
                "textDocument/declaration",
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    public void HandleMessage_References_MatchesCliCandidateIdentityForOverload_Issue4622()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_reference_identity");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourceSemantics = InsertSourceSemanticsFixtureIssue4622(projectRoot, dbPath);
            var definitionPath = Path.Combine(projectRoot, "resolver.cs");
            var cappedDefinitionPath = Path.Combine(projectRoot, "capped-resolver.cs");
            var oneArgCallerPath = Path.Combine(projectRoot, "one.cs");
            var twoArgCallerPath = Path.Combine(projectRoot, "two.cs");
            var definitionSource = """
                class Resolver
                {
                    internal static int Choose(int value) => value;
                    internal static int Choose(int left, int right) => left + right;
                    internal static int Call() => Choose(1, 2);
                }
                """;
            var cappedDefinitionSourceBuilder = new StringBuilder();
            cappedDefinitionSourceBuilder.AppendLine("class CappedResolver");
            cappedDefinitionSourceBuilder.AppendLine("{");
            for (var arity = 1; arity <= 6; arity++)
            {
                var parameters = string.Join(", ", Enumerable.Range(1, arity).Select(index => $"int value{index}"));
                cappedDefinitionSourceBuilder.AppendLine($"    internal static int Select({parameters}) => value1;");
            }

            const string sixArguments = "1, 2, 3, 4, 5, 6";
            const int cappedCallCount = 55;
            for (var call = 0; call < cappedCallCount; call++)
                cappedDefinitionSourceBuilder.AppendLine($"    internal static int Call{call}() => Select({sixArguments});");
            cappedDefinitionSourceBuilder.AppendLine("}");
            var cappedDefinitionSource = cappedDefinitionSourceBuilder.ToString();
            var oneArgCallerSource = "class One { int Call() => Resolver.Choose(1); }\n";
            var twoArgCallerSource = "class Two { int Call() => Resolver.Choose(1, 2); }\n";
            File.WriteAllText(definitionPath, definitionSource);
            File.WriteAllText(cappedDefinitionPath, cappedDefinitionSource);
            File.WriteAllText(oneArgCallerPath, oneArgCallerSource);
            File.WriteAllText(twoArgCallerPath, twoArgCallerSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "resolver.cs", "csharp", definitionSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "capped-resolver.cs", "csharp", cappedDefinitionSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "one.cs", "csharp", oneArgCallerSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "two.cs", "csharp", twoArgCallerSource);
            MarkGraphReady(dbPath);

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var reader = new DbReader(db);
            var cliAnalysis = reader.AnalyzeSymbol("Choose", 50, exact: true);
            var cliCandidate = Assert.Single(cliAnalysis.CandidateBundles!
                .Where(candidate => candidate.Definition.Line == 4));
            Assert.True(cliCandidate.IdentityScoped);
            Assert.NotEmpty(cliCandidate.References);
            var twoArgumentReference = Assert.Single(cliCandidate.References.Where(reference => reference.Path == "two.cs"));
            var sameFileReference = Assert.Single(cliCandidate.References.Where(reference => reference.Path == "resolver.cs"));
            var expectedReferences = cliCandidate.References
                .Select(reference => (reference.Path, Line: reference.Line - 1, Character: Math.Max(reference.Column - 1, 0)))
                .OrderBy(reference => reference.Path, StringComparer.Ordinal)
                .ThenBy(reference => reference.Line)
                .ThenBy(reference => reference.Character)
                .ToArray();
            using var server = new LspServer(reader, "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var definitionCharacter = CharacterOf(definitionSource, 3, "Choose");
            var requestCharacter = CharacterOf(definitionSource, 4, "Choose");
            Assert.Equal(CharacterOf(twoArgCallerSource, 0, "Choose") + 1, twoArgumentReference.Column);
            Assert.Equal(requestCharacter + 1, sameFileReference.Column);

            var withoutDeclaration = server.HandleMessage(CreateReferencesRequest(
                definitionPath,
                46223,
                4,
                requestCharacter,
                includeDeclaration: false));
            var withDeclaration = server.HandleMessage(CreateReferencesRequest(
                definitionPath,
                46224,
                4,
                requestCharacter,
                includeDeclaration: true));
            var lastCappedCallLine = 8 + cappedCallCount - 1;
            var cappedDefinition = server.HandleMessage(CreateDefinitionRequest(
                cappedDefinitionPath,
                46225,
                lastCappedCallLine,
                CharacterOf(cappedDefinitionSource, lastCappedCallLine, "Select")));

            Assert.NotNull(withoutDeclaration);
            Assert.NotNull(withDeclaration);
            Assert.NotNull(cappedDefinition);
            var actualReferences = withoutDeclaration!["result"]!.AsArray()
                .Select(location =>
                {
                    var absolutePath = new Uri(location!["uri"]!.GetValue<string>()).LocalPath;
                    var relativePath = Path.GetRelativePath(projectRoot, absolutePath).Replace('\\', '/');
                    return (
                        Path: relativePath,
                        Line: location["range"]!["start"]!["line"]!.GetValue<int>(),
                        Character: location["range"]!["start"]!["character"]!.GetValue<int>());
                })
                .OrderBy(reference => reference.Path, StringComparer.Ordinal)
                .ThenBy(reference => reference.Line)
                .ThenBy(reference => reference.Character)
                .ToArray();
            Assert.Equal(expectedReferences, actualReferences);
            var withLocations = withDeclaration!["result"]!.AsArray();
            Assert.Equal(expectedReferences.Length + 1, withLocations.Count);
            var declaration = Assert.Single(withLocations.Where(location =>
                new Uri(location!["uri"]!.GetValue<string>()).LocalPath == definitionPath &&
                location["range"]!["start"]!["line"]!.GetValue<int>() == 3));
            Assert.Equal(definitionCharacter, declaration!["range"]!["start"]!["character"]!.GetValue<int>());
            Assert.Equal(definitionCharacter + "Choose".Length, declaration["range"]!["end"]!["character"]!.GetValue<int>());
            const int sixArgumentDefinitionLine = 7;
            var definition = Assert.Single(cappedDefinition!["result"]!.AsArray());
            Assert.Equal(sixArgumentDefinitionLine, definition!["range"]!["start"]!["line"]!.GetValue<int>());
            var expectedCharacter = CharacterOf(cappedDefinitionSource, sixArgumentDefinitionLine, "Select");
            Assert.Equal(expectedCharacter, definition["range"]!["start"]!["character"]!.GetValue<int>());
            Assert.Equal(expectedCharacter + "Select".Length, definition["range"]!["end"]!["character"]!.GetValue<int>());
            AssertSourceSemanticsIssue4622(server, sourceSemantics.SourcePath, sourceSemantics.SourceLines);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var request = CreateDefinitionRequest(
                unindexedPath,
                3428,
                0,
                unindexedSource.IndexOf("Needle();", StringComparison.Ordinal));
            var activities = new ConcurrentQueue<Activity>();
            using var parentActivity = new Activity("lsp-test-request").Start();
            var expectedTraceId = parentActivity.TraceId;
            using var listener = CaptureCodeIndexActivities(activities);

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
            var requestActivity = Assert.Single(activities.Where(activity =>
                activity.OperationName == "lsp.request" && activity.TraceId == expectedTraceId));
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
        lock (PathCasingTestLock.Gate)
        {
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
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    }

    [Fact]
    public void HandleMessage_Definition_RejectsCaseVariantWhenWorkspaceCaseSensitive()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_case_sensitive");
        lock (PathCasingTestLock.Gate)
        {
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
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
    }

    [Fact]
    public void HandleMessage_Definition_ResolvesIndexedDocumentBeyondBasenameCandidateCap()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_definition_many_basenames");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < LspServer.MaxDocumentPathFallbackCandidates; i++)
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions());
            var request = CreateDefinitionRequest(
                sourcePath,
                3426,
                0,
                source.IndexOf("Needle();", StringComparison.Ordinal));
            var activities = new ConcurrentQueue<Activity>();
            using var parentActivity = new Activity("lsp-test-request").Start();
            var expectedTraceId = parentActivity.TraceId;
            using var listener = CaptureCodeIndexActivities(activities);

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Empty(response!["result"]!.AsArray());
            var requestActivity = Assert.Single(activities.Where(activity =>
                activity.OperationName == "lsp.request" && activity.TraceId == expectedTraceId));
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
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
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

    private static string CreateInlayHintRequest(
        string sourcePath,
        int id,
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "textDocument/inlayHint",
            @params = new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                range = new
                {
                    start = new { line = startLine, character = startCharacter },
                    end = new { line = endLine, character = endCharacter },
                },
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

    private static List<(JsonObject Message, int BodyBytes)> ReadLspMessages(MemoryStream output)
    {
        output.Position = 0;
        var messages = new List<(JsonObject, int)>();
        while (LspServer.TryReadMessage(output, out var payload))
        {
            var message = JsonNode.Parse(payload)?.AsObject()
                ?? throw new InvalidDataException("Expected an object-shaped LSP message.");
            messages.Add((message, Encoding.UTF8.GetByteCount(payload)));
        }

        return messages;
    }

    private static bool HasProgressToken(JsonObject message, string expected) =>
        message["method"]?.GetValue<string>() == "$/progress"
        && message["params"]?["token"] is JsonValue token
        && token.TryGetValue<string>(out var actual)
        && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool HasProgressToken(JsonObject message, long expected) =>
        message["method"]?.GetValue<string>() == "$/progress"
        && message["params"]?["token"] is JsonValue token
        && token.TryGetValue<long>(out var actual)
        && actual == expected;

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

    private static List<(int Line, int Character, string Text, int Type, int Modifiers)> DecodeSemanticTokens(
        JsonArray data,
        string source)
    {
        var values = data.Select(node => node!.GetValue<int>()).ToArray();
        var sourceLines = source.Split('\n');
        var tokens = new List<(int, int, string, int, int)>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < values.Length; index += 5)
        {
            line += values[index];
            character = values[index] == 0 ? character + values[index + 1] : values[index + 1];
            tokens.Add((line, character, sourceLines[line].Substring(character, values[index + 2]), values[index + 3], values[index + 4]));
        }
        return tokens;
    }

    private static void AssertSemanticToken(
        List<(int Line, int Character, string Text, int Type, int Modifiers)> tokens,
        int line,
        string text,
        int type,
        int modifiers)
    {
        Assert.Contains(tokens, token =>
            token.Line == line &&
            token.Text == text &&
            token.Type == type &&
            token.Modifiers == modifiers);
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

    private static IReadOnlyList<string> GetDocumentSymbolStructure(JsonArray symbols)
    {
        var structure = new List<string>();
        AddDocumentSymbolStructure(symbols, string.Empty, structure);
        return structure;
    }

    private static void AddDocumentSymbolStructure(
        JsonArray symbols,
        string parentIdentity,
        List<string> structure)
    {
        foreach (var symbol in symbols)
        {
            var name = symbol!["name"]!.GetValue<string>();
            var kind = symbol["kind"]!.GetValue<int>();
            var identity = $"{parentIdentity}/{name}:{kind}";
            structure.Add(identity);
            if (symbol["children"] is JsonArray children)
                AddDocumentSymbolStructure(children, identity, structure);
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

    private static (string SourcePath, string[] SourceLines) InsertSourceSemanticsFixtureIssue4622(
        string projectRoot,
        string dbPath)
    {
        var sourcePath = Path.Combine(projectRoot, "app.cs");
        var source = """
            internal static bool TryReadUtf8File() => true;
            public Widget Widget { get; }
            int explicitField = 1;
            var inferredLocal = 1;
            int explicitLocal = 1;
            """;
        var sourceLines = source.Split('\n');
        File.WriteAllText(sourcePath, source);
        using var fixtureDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(fixtureDb.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "app.cs",
            Lang = "csharp",
            Size = source.Length,
            Lines = 5,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "issue4622-source-semantics",
        });
        writer.InsertChunks([
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 5,
                Content = source,
            },
        ]);
        writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "TryReadUtf8File", Line = 1, StartLine = 1, StartColumn = 20, EndLine = 1, ReturnType = "bool" },
            new SymbolRecord { FileId = fileId, Kind = "property", Name = "Widget", Line = 2, StartLine = 2, StartColumn = sourceLines[1].IndexOf("Widget", StringComparison.Ordinal), EndLine = 2, Signature = "public Widget Widget { get; }", ReturnType = "Widget" },
            new SymbolRecord { FileId = fileId, Kind = "field", Name = "explicitField", Line = 3, StartLine = 3, StartColumn = 0, EndLine = 3, ReturnType = "int" },
            new SymbolRecord { FileId = fileId, Kind = "variable", Name = "inferredLocal", Line = 4, StartLine = 4, StartColumn = 0, EndLine = 4, ReturnType = "int" },
            new SymbolRecord { FileId = fileId, Kind = "variable", Name = "explicitLocal", Line = 5, StartLine = 5, StartColumn = 0, EndLine = 5, ReturnType = "int" },
        ]);
        return (sourcePath, sourceLines);
    }

    private static void AssertSourceSemanticsIssue4622(
        LspServer server,
        string sourcePath,
        IReadOnlyList<string> sourceLines)
    {
        var documentSymbols = server.HandleMessage(CreateTextDocumentRequest("textDocument/documentSymbol", sourcePath, 46220));
        var workspaceSymbols = server.HandleMessage(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 46221,
            method = "workspace/symbol",
            @params = new { query = "Widget" },
        }));
        var inlayHints = server.HandleMessage(CreateInlayHintRequest(sourcePath, 46222, 0, 0, 4, int.MaxValue));

        Assert.NotNull(documentSymbols);
        Assert.NotNull(workspaceSymbols);
        Assert.NotNull(inlayHints);
        var expectedStart = sourceLines[0].IndexOf("TryReadUtf8File", StringComparison.Ordinal);
        var documentSymbol = Assert.Single(FlattenDocumentSymbols(documentSymbols!["result"]!.AsArray())
            .Where(symbol => symbol?["name"]?.GetValue<string>() == "TryReadUtf8File"));
        Assert.Equal(expectedStart, documentSymbol!["selectionRange"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(expectedStart + "TryReadUtf8File".Length, documentSymbol["selectionRange"]!["end"]!["character"]!.GetValue<int>());
        Assert.Equal(0, documentSymbol["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.True(documentSymbol["range"]!["end"]!["character"]!.GetValue<int>() >=
            documentSymbol["selectionRange"]!["end"]!["character"]!.GetValue<int>());
        var widgetStart = sourceLines[1].LastIndexOf("Widget", StringComparison.Ordinal);
        var widgetDocumentSymbol = Assert.Single(FlattenDocumentSymbols(documentSymbols["result"]!.AsArray())
            .Where(symbol => symbol?["name"]?.GetValue<string>() == "Widget"));
        Assert.Equal(widgetStart, widgetDocumentSymbol!["selectionRange"]!["start"]!["character"]!.GetValue<int>());
        Assert.True(widgetDocumentSymbol["range"]!["end"]!["character"]!.GetValue<int>() >=
            widgetDocumentSymbol["selectionRange"]!["end"]!["character"]!.GetValue<int>());
        var workspaceSymbol = Assert.Single(workspaceSymbols!["result"]!.AsArray());
        Assert.Equal(widgetStart, workspaceSymbol!["location"]!["range"]!["start"]!["character"]!.GetValue<int>());
        Assert.Equal(widgetStart + "Widget".Length, workspaceSymbol["location"]!["range"]!["end"]!["character"]!.GetValue<int>());
        var hint = Assert.Single(inlayHints!["result"]!.AsArray());
        Assert.Equal(3, hint!["position"]!["line"]!.GetValue<int>());
        Assert.Equal(4 + "inferredLocal".Length, hint["position"]!["character"]!.GetValue<int>());
        Assert.Equal(": int", hint["label"]!.GetValue<string>());
    }

    private static ActivityListener CaptureCodeIndexActivities(ConcurrentQueue<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CodeIndexTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void MarkGraphReady(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
    }

    private sealed class SignalingMemoryStream : MemoryStream
    {
        private readonly TaskCompletionSource writeObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitForWriteAsync() => writeObserved.Task;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            writeObserved.TrySetResult();
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            writeObserved.TrySetResult();
        }
    }

    private sealed class FirstWriteGateMemoryStream : MemoryStream
    {
        private readonly TaskCompletionSource blockedWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource writeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitForBlockedWriteAsync() => blockedWrite.Task;

        internal void ReleaseWrites() => writeRelease.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            blockedWrite.TrySetResult();
            await writeRelease.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            blockedWrite.TrySetResult();
            await writeRelease.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class MarkerWriteGateMemoryStream(string marker) : MemoryStream
    {
        private readonly TaskCompletionSource markerObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource markerRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitForMarkerAsync() => markerObserved.Task;

        internal void ReleaseMarker() => markerRelease.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            await WaitForMarkerReleaseAsync(buffer, cancellationToken);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            await WaitForMarkerReleaseAsync(
                buffer.AsMemory(offset, count),
                cancellationToken);
        }

        private async Task WaitForMarkerReleaseAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (!Encoding.UTF8.GetString(buffer.Span).Contains(marker, StringComparison.Ordinal))
                return;

            markerObserved.TrySetResult();
            await markerRelease.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class StagedReadStream(byte[] prefix, byte[] suffix) : Stream
    {
        private readonly TaskCompletionSource suffixRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int prefixOffset;
        private int suffixOffset;

        internal void ReleaseSuffix() => suffixRelease.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (prefixOffset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - prefixOffset);
                prefix.AsMemory(prefixOffset, count).CopyTo(buffer);
                prefixOffset += count;
                return count;
            }

            await suffixRelease.Task.WaitAsync(cancellationToken);
            if (suffixOffset >= suffix.Length)
                return 0;

            var suffixCount = Math.Min(buffer.Length, suffix.Length - suffixOffset);
            suffix.AsMemory(suffixOffset, suffixCount).CopyTo(buffer);
            suffixOffset += suffixCount;
            return suffixCount;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class PrefixThenPendingReadStream(byte[] prefix) : Stream
    {
        private readonly TaskCompletionSource pendingRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int offset;

        internal Task WaitForPendingReadAsync() => pendingRead.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
            => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - offset);
                prefix.AsMemory(offset, count).CopyTo(buffer);
                offset += count;
                return ValueTask.FromResult(count);
            }

            pendingRead.TrySetResult();
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override long Seek(long seekOffset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int bufferOffset, int count)
            => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new IOException("Injected write failure.");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Injected write failure."));

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Task.FromException(new IOException("Injected write failure."));
    }

    private sealed class PendingReadStream : Stream
    {
        private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WaitForReadAsync() => readStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
