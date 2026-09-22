using System.Text.Json.Nodes;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_PythonOriginsShareSearchFindAndUnknownAuthority_Issue5398()
    {
        const string path = "tests/test_origins5398.py";
        InsertIndexedFile(path, "python", "# Needle5398\nx = '''\nNeedle5398\n'''\nNeedle5398()\nx = f'Needle5398 {Needle5398(\"Needle5398\")}'\n");
        foreach (var tool in new[] { "search", "find", "find_in_file" })
        foreach (var (filters, cliFilters) in new (string, string[])[]
        {
            ("{\"origin\":\"code\"}", ["--origin", "code"]),
            ("{\"origin\":\"string_literal\"}", ["--origin", "string_literal"]),
            ("{\"excludeOrigin\":[\"comment\",\"string_literal\"]}", ["--exclude-origin", "comment,string_literal"]),
            ("{\"excludeFixtures\":true}", ["--exclude-fixtures"]),
            ("{\"excludeStrings\":true,\"excludeComments\":true}", ["--exclude-strings", "--exclude-comments"]),
        })
        {
            var args = JsonNode.Parse(filters)!.AsObject();
            args["path"] = path;
            args["query"] = "Needle5398";
            args[tool == "search" ? "exact" : "regex"] = true;
            var rows = Payload5349(Call5349(tool, args));
            Assert.True(rows["origin_classification_complete"]!.GetValue<bool>());
            args["countOnly"] = true;
            var count = Payload5349(Call5349(tool, args));
            Assert.True(count["authoritative_count"]!.GetValue<bool>());
            var command = tool == "search" ? "search" : "find";
            var (exit, json, _) = QueryCommandTestSupport.CaptureConsole(() => CodeIndex.Cli.ProgramRunner.Run(
                [command, "Needle5398", "--path", path, "--db", _dbPath, "--count", "--json",
                    tool == "search" ? "--exact" : "--regex", .. cliFilters], QueryCommandTestSupport.JsonOptions, "test"));
            Assert.Equal(0, exit);
            Assert.Equal(JsonNode.Parse(json)!["count"]!.GetValue<int>(), count["count"]!.GetValue<int>());
        }

        InsertIndexedFile("bad5398.py", "python", "value = f'{Needle5398(]}'\n");
        foreach (var tool in new[] { "search", "find", "find_in_file" })
        foreach (var countOnly in new[] { false, true })
        {
            var args = new JsonObject { ["path"] = "bad5398.py", ["query"] = "Needle5398",
                ["origin"] = "code", ["countOnly"] = countOnly, [tool == "search" ? "exact" : "regex"] = true };
            var result = Payload5349(Call5349(tool, args));
            Assert.False(result["origin_classification_complete"]!.GetValue<bool>());
            Assert.True(result["partial_result"]!.GetValue<bool>());
            if (countOnly) Assert.False(result["authoritative_count"]!.GetValue<bool>());
            Assert.Contains("unbalanced_interpolation", result.ToJsonString(), StringComparison.Ordinal);
        }
    }
}
