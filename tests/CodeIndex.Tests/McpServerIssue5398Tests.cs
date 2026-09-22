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

        InsertIndexedFile("format5398.py", "python",
            "value = f'{value:\\N{LATIN SMALL LETTER A}}'\nLATIN()\ndef f(): return\"LATIN\"\nLATIN()\n" +
            "f'{LATIN!r }'\nf'{LATIN= # LATIN\n}'\nf'{value:\\\nLATIN}'\nLATIN()\n");
        foreach (var tool in new[] { "search", "find", "find_in_file" })
        {
            var args = new JsonObject { ["path"] = "format5398.py", ["query"] = "LATIN", ["origin"] = "code",
                ["countOnly"] = true, [tool == "search" ? "exact" : "regex"] = true };
            var result = Payload5349(Call5349(tool, args));
            Assert.True(result["origin_classification_complete"]!.GetValue<bool>());
            Assert.True(result["authoritative_count"]!.GetValue<bool>());
            if (tool != "search") Assert.Equal(5, result["count"]!.GetValue<int>());
            var (exit, json, _) = QueryCommandTestSupport.CaptureConsole(() => CodeIndex.Cli.ProgramRunner.Run(
                [tool == "search" ? "search" : "find", "LATIN", "--path", "format5398.py", "--db", _dbPath,
                    "--count", "--json", "--origin", "code", tool == "search" ? "--exact" : "--regex"],
                QueryCommandTestSupport.JsonOptions, "test"));
            Assert.Equal(0, exit);
            Assert.Equal(JsonNode.Parse(json)!["count"]!.GetValue<int>(), result["count"]!.GetValue<int>());
        }

        foreach (var (suffix, source, reason) in new[]
        {
            ("bracket", "value = f'{Needle5398(]}'\n", "unbalanced_interpolation"),
            ("space", "f'{Needle5398! r}'\nNeedle5398()\n", "unsupported_interpolation_conversion"),
            ("comment", "f'{Needle5398! # comment\n r}'\nNeedle5398()\n", "unsupported_interpolation_conversion"),
            ("newline", "f'{Needle5398!\n r}'\nNeedle5398()\n", "unsupported_interpolation_conversion"),
            ("continuation", "f'{Needle5398!\\\n r}'\nNeedle5398()\n", "unsupported_interpolation_conversion"),
        })
        {
            var badPath = $"bad5398-{suffix}.py";
            InsertIndexedFile(badPath, "python", source);
            foreach (var tool in new[] { "search", "find", "find_in_file" })
            foreach (var countOnly in new[] { false, true })
            {
                var args = new JsonObject { ["path"] = badPath, ["query"] = "Needle5398",
                    ["origin"] = "code", ["countOnly"] = countOnly, [tool == "search" ? "exact" : "regex"] = true };
                var result = Payload5349(Call5349(tool, args));
                Assert.False(result["origin_classification_complete"]!.GetValue<bool>());
                Assert.True(result["partial_result"]!.GetValue<bool>());
                if (countOnly) Assert.False(result["authoritative_count"]!.GetValue<bool>());
                Assert.Contains(reason, result.ToJsonString(), StringComparison.Ordinal);
            }
            foreach (var command in new[] { "search", "find" })
            {
                var (exit, json, _) = QueryCommandTestSupport.CaptureConsole(() => CodeIndex.Cli.ProgramRunner.Run(
                    [command, "Needle5398", "--path", badPath, "--db", _dbPath, "--count", "--json",
                        "--origin", "code", command == "search" ? "--exact" : "--regex"],
                    QueryCommandTestSupport.JsonOptions, "test"));
                Assert.Equal(11, exit);
                var result = JsonNode.Parse(json)!;
                Assert.False(result["authoritative_count"]!.GetValue<bool>());
                Assert.False(result["origin_classification_complete"]!.GetValue<bool>());
                Assert.Equal(0, result["count"]!.GetValue<int>());
                Assert.Contains(reason, json, StringComparison.Ordinal);
            }
        }
    }
}
