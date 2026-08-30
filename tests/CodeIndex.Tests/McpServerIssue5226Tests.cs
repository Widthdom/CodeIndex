using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_ImpactCountOnlyPreservesSqlCallerReadinessWithoutLanguageFilter_Issue5226()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_impact_count_sql_language_issue5226");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/issue5226_target.py",
                "python",
                "def issue5226_cross_language():\n    return 0\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/issue5226_caller.sql",
                "sql",
                """
                CREATE PROCEDURE dbo.issue5226_Caller
                AS
                BEGIN
                    SELECT issue5226_cross_language();
                END;
                GO
                """);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":5226,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"issue5226_cross_language","countOnly":true,"limit":1,"maxHops":1}}}""")!;
            var structured = server.HandleMessage(request)!["result"]!["structuredContent"]!;

            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.False(structured["authoritative_count"]!.GetValue<bool>());
            Assert.Null(structured["total"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ImpactCountOnlyIgnoresLimitAndPreservesCompletenessSignals_Issue5226()
    {
        InsertIndexedFile(
            "src/issue5226-target.cs",
            "csharp",
            "public static class McpIssue5226Target { public static void Hit() { } }");
        for (int i = 0; i < 6; i++)
        {
            InsertIndexedFile(
                $"src/issue5226-caller-{i}.cs",
                "csharp",
                $"public sealed class McpIssue5226Caller{i} {{ public void Run() {{ McpIssue5226Target.Hit(); }} }}");
        }

        JsonNode Call(bool countOnly)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 5226,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "impact_analysis",
                    ["arguments"] = new JsonObject
                    {
                        ["query"] = "McpIssue5226Target.Hit",
                        ["lang"] = "csharp",
                        ["maxHops"] = 1,
                        ["limit"] = 1,
                        ["countOnly"] = countOnly,
                    },
                },
            };
            return _server.HandleMessage(request)!["result"]!["structuredContent"]!;
        }

        var count = Call(countOnly: true);
        Assert.True(count["count_only"]!.GetValue<bool>());
        Assert.Equal(6, count["count"]!.GetValue<int>());
        Assert.Equal(6, count["file_count"]!.GetValue<int>());
        Assert.Equal(6, count["total"]!.GetValue<int>());
        Assert.False(count["truncated"]!.GetValue<bool>());
        Assert.True(count["authoritative_count"]!.GetValue<bool>());
        Assert.Empty(count["results"]!.AsArray());
        Assert.Equal(5, count["top_files"]!.AsArray().Count);

        var rows = Call(countOnly: false);
        Assert.Equal(1, rows["count"]!.GetValue<int>());
        Assert.Single(rows["callers"]!.AsArray());
        Assert.True(rows["truncated"]!.GetValue<bool>());
        Assert.Equal("user_limit", rows["truncated_reason"]!.GetValue<string>());

        var capKind = ReferenceExtractor.ReferenceSafetyCapDiagnosticKinds[0];
        using (var command = _db.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO file_issues (file_id, kind, line, message)
                SELECT id, @kind, 1, 'reference extraction safety cap reached'
                FROM files
                WHERE path = 'src/issue5226-target.cs';
                """;
            command.Parameters.AddWithValue("@kind", capKind);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var incomplete = Call(countOnly: true);
        Assert.Equal(6, incomplete["count"]!.GetValue<int>());
        Assert.False(incomplete["reference_graph_complete"]!.GetValue<bool>());
        Assert.False(incomplete["authoritative_count"]!.GetValue<bool>());
        Assert.Null(incomplete["total"]);
    }
}
