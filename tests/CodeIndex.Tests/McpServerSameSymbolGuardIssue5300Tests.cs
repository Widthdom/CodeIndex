using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_SameSymbolGuardRowsCountsEmptyAndStale_Issue5300()
    {
        const string source = "class Guarded\n{\n void M()\n {\n  Clear();\n  Return();\n }\n void N()\n {\n  Return();\n }\n}";
        TestProjectHelper.InsertFreshIndexedFile(_projectRoot, _dbPath, "src/guard.cs", "csharp", source);
        new DbWriter(_db.Connection).SetMeta(DbContext.SymbolKindFilterMetaKey, SymbolKindFilter.Empty.Signature);
        JsonNode Call(string query, bool count = false)
        {
            return _server.HandleMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 5300,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "search",
                    ["arguments"] = new JsonObject
                    {
                        ["query"] = query,
                        ["path"] = "src/guard.cs",
                        ["rejectBefore"] = "Clear",
                        ["guardScope"] = "same-symbol",
                        ["guardWindow"] = 20,
                        ["countOnly"] = count,
                    },
                },
            })!["result"]!;
        }
        var rows = Call("Return");
        Assert.False(rows["isError"]?.GetValue<bool>() ?? false);
        var payload = rows["structuredContent"]!;
        Assert.Equal("same-symbol", payload["guard_scope"]!.GetValue<string>());
        Assert.Equal(1, payload["guard_scope_contract_version"]!.GetValue<int>());
        Assert.Single(payload["results"]!.AsArray());
        Assert.Equal(1, Call("Return", count: true)["structuredContent"]!["count"]!.GetValue<int>());
        var empty = Call("Absent")["structuredContent"]!;
        Assert.Empty(empty["results"]!.AsArray());
        Assert.Equal("error", empty["guard_scope_unavailable_policy"]!.GetValue<string>());
        TestProjectHelper.AppendTextFile(_projectRoot, "src/guard.cs", "\n// changed");
        var failed = Call("Return");
        Assert.True(failed["isError"]!.GetValue<bool>());
        Assert.Contains("same_symbol_scope_unavailable", failed.ToJsonString());
    }
}
