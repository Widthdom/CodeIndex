using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class LspServerBudgetTests
{
    [Fact]
    public void TryWriteMessage_RejectsOversizedResponseFrame_Issue3817()
    {
        using var output = new MemoryStream();
        var payload = new string('x', LspServer.MaxLspResponseFrameBytes + 1);

        Assert.False(LspServer.TryWriteMessage(output, payload, out var bodyBytes));

        Assert.Equal(LspServer.MaxLspResponseFrameBytes + 1, bodyBytes);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void HandleMessage_LiveDocumentSync_EvictsOldestBufferWhenAggregateBudgetIsFull_Issue3817()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_live_sync_byte_bound");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var text = new string('x', LspServer.MaxPositionDocumentBytes);
            for (var i = 0; i < 5; i++)
            {
                var sourcePath = Path.Combine(projectRoot, $"large{i}.cs");
                Assert.Null(server.HandleMessage(CreateDidOpenRequest(sourcePath, text, version: i + 1)));
            }

            Assert.True(server.LiveDocumentBytesForTests <= LspServer.MaxLiveDocumentBytes);
            Assert.True(server.LiveDocumentEvictionCountForTests > 0);
            Assert.True(server.LiveDocumentEvictedBytesForTests > 0);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_LiveDocumentSync_UsesLatestTextWhenContentChangesAreOverLimit_Issue3817()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_live_sync_changes_bound");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var diskSource = "class App { void Needle() { } void Call() { Missing(); } }\n";
            var latestSource = "class App { void Needle() { } void Call() { Needle(); } }\n";
            File.WriteAllText(sourcePath, diskSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", diskSource);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            var contentChanges = Enumerable.Range(0, LspServer.MaxContentChangesPerNotification + 5)
                .Select(i => new { text = i == LspServer.MaxContentChangesPerNotification + 4 ? latestSource : diskSource })
                .ToArray();
            var changeRequest = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "textDocument/didChange",
                @params = new
                {
                    textDocument = new
                    {
                        uri = new Uri(sourcePath).AbsoluteUri,
                        version = 3817,
                    },
                    contentChanges,
                },
            });

            Assert.Null(server.HandleMessage(CreateDidOpenRequest(sourcePath, diskSource, version: 1)));
            Assert.Null(server.HandleMessage(changeRequest));
            var response = server.HandleMessage(CreateDefinitionRequest(
                sourcePath,
                3817,
                0,
                latestSource.LastIndexOf("Needle();", StringComparison.Ordinal)));

            Assert.NotNull(response);
            Assert.NotEmpty(response!["result"]!.AsArray());
            Assert.Equal(5, server.ContentChangeEntriesDroppedForTests);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Hover_UsesWorkspaceRelativePath_Issue3817()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_hover_relative_path");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class App { int Count() { return 1; } void Call() { Count(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.cs", "csharp", source);
            MarkGraphReady(dbPath);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);

            var hover = server.HandleMessage(CreatePositionRequest(
                "textDocument/hover",
                sourcePath,
                38170,
                0,
                source.LastIndexOf("Count();", StringComparison.Ordinal)));

            Assert.NotNull(hover);
            var value = hover!["result"]!["contents"]!["value"]!.GetValue<string>();
            Assert.Contains("app.cs:", value, StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot, value, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HandleMessage_Hover_RedactsAbsolutePathWithoutWorkspaceRoot_Issue3817()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_hover_redacted_path");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            var source = "class App { int Count() { return 1; } void Call() { Count(); } }\n";
            File.WriteAllText(sourcePath, source);
            TestProjectHelper.InsertIndexedFile(dbPath, sourcePath, "csharp", source);
            MarkGraphReady(dbPath);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions());

            var hover = server.HandleMessage(CreatePositionRequest(
                "textDocument/hover",
                sourcePath,
                38171,
                0,
                source.LastIndexOf("Count();", StringComparison.Ordinal)));

            Assert.NotNull(hover);
            var value = hover!["result"]!["contents"]!["value"]!.GetValue<string>();
            Assert.Contains("[outside workspace]:", value, StringComparison.Ordinal);
            Assert.DoesNotContain(sourcePath, value, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateDefinitionRequest(string sourcePath, int id, int line, int character) =>
        CreatePositionRequest("textDocument/definition", sourcePath, id, line, character);

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

    private static void MarkGraphReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
    }
}
