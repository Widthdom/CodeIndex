using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class LspServerRequestIdTests
{
    [Fact]
    public void HandleMessage_OversizedRequestId_ReturnsInvalidRequestWithoutEcho_Issue3113()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_lsp_large_id");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using var db = new DbContext(dbPath);
            using var server = new LspServer(
                new DbReader(db),
                "1.2.3",
                ProgramRunner.CreateDefaultJsonOptions(),
                projectRoot);
            var oversizedId = new string('\u00e9', LspServer.MaxLspRequestIdRawBytes / 2);
            var request = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{oversizedId}\",\"method\":\"initialize\",\"params\":{{}}}}";

            var response = server.HandleMessage(request);

            Assert.NotNull(response);
            Assert.Equal(-32600, response!["error"]!["code"]!.GetValue<int>());
            Assert.Contains(
                "Request id must be",
                response["error"]!["message"]!.GetValue<string>(),
                StringComparison.Ordinal);
            Assert.Null(response["id"]);
            Assert.DoesNotContain(oversizedId, response.ToJsonString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
