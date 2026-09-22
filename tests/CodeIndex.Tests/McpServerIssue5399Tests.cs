using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsCall_MultilineFindSharesSpansCountsPaginationAndErrors_Issue5399()
    {
        InsertIndexedFile("window5399/a.txt", "text", "😀A\nB A\nB\nA\nB");
        foreach (var tool in new[] { "find", "find_in_file" })
        {
            var args = new JsonObject
            {
                ["query"] = "A\\nB",
                ["path"] = "window5399/",
                ["regex"] = true,
                ["multiline"] = true,
                ["windowLines"] = 2,
                ["limit"] = 1
            };
            var first = Payload5349(Call5349(tool, args));
            var row = Assert.Single(first["results"]!.AsArray())!;
            Assert.Equal(1, row["line"]!.GetValue<int>());
            Assert.Equal(3, row["column"]!.GetValue<int>());
            Assert.Equal(2, row["match_end_line"]!.GetValue<int>());
            Assert.Equal(2, row["match_end_column"]!.GetValue<int>());
            Assert.False(first["unbounded_absence_authoritative"]!.GetValue<bool>());
            Assert.False(first["scan_complete"]!.GetValue<bool>());
            var cursor = first["next_cursor"]!.GetValue<string>();
            args["cursor"] = cursor;
            args["limit"] = 10;
            var next = Payload5349(Call5349(tool, args));
            Assert.Equal(2, next["count"]!.GetValue<int>());
            Assert.False(next["authoritative_rows"]!.GetValue<bool>());
            Assert.True(next["scan_complete"]!.GetValue<bool>());
            Assert.Null(next["next_cursor"]);
            args["windowLines"] = 3;
            Assert.Contains("cursor", Call5349(tool, args).ToJsonString(), StringComparison.Ordinal);
            args.Remove("cursor");
            args["windowLines"] = 2;
            args["countOnly"] = true;
            var count = Payload5349(Call5349(tool, args));
            Assert.Equal(3, count["count"]!.GetValue<int>());
            var (exit, json, _) = CaptureConsole(() => ProgramRunner.Run(
                ["find", "A\\nB", "--regex", "--multiline", "--window-lines", "2", "--path", "window5399/",
                    "--db", _dbPath, "--json", "--count"], JsonOptions, "test"));
            Assert.Equal(0, exit);
            Assert.Equal(JsonNode.Parse(json)!["count"]!.GetValue<int>(), count["count"]!.GetValue<int>());
            args["windowBytes"] = 2;
            var capped = Payload5349(Call5349(tool, args));
            Assert.True(capped["partial_result"]!.GetValue<bool>());
            Assert.False(capped["authoritative_count"]!.GetValue<bool>());
            Assert.Equal("multiline_window_bytes", capped["scan_truncation_reason"]!.GetValue<string>());
            args.Remove("windowBytes");
            args["maxBytes"] = 100;
            Assert.Contains(CommandErrorCodes.ResponseBudgetTooSmall, Call5349(tool, args).ToJsonString(), StringComparison.Ordinal);
            args.Remove("maxBytes");
            foreach (var (name, value) in new (string, JsonNode?)[] { ("origin", JsonValue.Create("code")),
                ("before", JsonValue.Create(1)), ("focusLine", JsonValue.Create(1)),
                ("windowLines", JsonValue.Create(65)), ("windowBytes", JsonValue.Create(262145)),
                ("regex", JsonValue.Create(false)), ("multiline", JsonValue.Create("yes")) })
            {
                var invalid = args.DeepClone().AsObject();
                invalid[name] = value;
                var error = Call5349(tool, invalid);
                Assert.True(error["error"] is not null || error["result"]?["isError"]?.GetValue<bool>() == true, error.ToJsonString());
            }
        }
        InsertIndexedFile("budget5399/a.txt", "text", string.Join('\n',
            Enumerable.Repeat(new string('x', 500) + "A\nB", 20)));
        foreach (var tool in new[] { "find", "find_in_file" })
        {
            var args = new JsonObject
            {
                ["query"] = "A\\nB",
                ["path"] = "budget5399/",
                ["regex"] = true,
                ["multiline"] = true,
                ["limit"] = 20,
                ["maxBytes"] = 6000
            };
            var lines = new List<int>();
            string? cursor = null;
            var byteLimited = false;
            for (var page = 0; page < 30; page++)
            {
                if (cursor is null) args.Remove("cursor");
                else args["cursor"] = cursor;
                var result = Payload5349(Call5349(tool, args));
                lines.AddRange(result["results"]!.AsArray().Select(row => row!["line"]!.GetValue<int>()));
                byteLimited |= result["byte_limit_reached"]!.GetValue<bool>();
                cursor = result["next_cursor"]?.GetValue<string>();
                if (cursor is null) break;
            }
            Assert.Null(cursor);
            Assert.True(byteLimited);
            Assert.Equal(Enumerable.Range(0, 20).Select(index => index * 2 + 1), lines);
        }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            var writer = new DbWriter(connection);
            var fileId = writer.UpsertFile(new CodeIndex.Models.FileRecord
            { Path = "budget5399/z-gap.txt", Lang = "text", Lines = 2, Size = 2 });
            writer.InsertChunks([new CodeIndex.Models.ChunkRecord
                { FileId = fileId, ChunkIndex = 0, StartLine = 2, EndLine = 2, Content = "X" }]);
        }
        foreach (var tool in new[] { "find", "find_in_file" })
        {
            var args = new JsonObject
            {
                ["query"] = "A\\nB",
                ["path"] = "budget5399/",
                ["regex"] = true,
                ["multiline"] = true,
                ["limit"] = 200,
                ["maxBytes"] = 6000
            };
            var result = Payload5349(Call5349(tool, args));
            Assert.True(result["byte_limit_reached"]!.GetValue<bool>());
            Assert.True(result["byte_limit_omitted_count"]!.GetValue<int>() > 0);
            Assert.False(result["authoritative_rows"]!.GetValue<bool>());
            Assert.False(result["scan_complete"]!.GetValue<bool>());
            Assert.Null(result["next_cursor"]);
            Assert.Equal("multiline_source_gap", result["scan_truncation_reason"]!.GetValue<string>());
            Assert.Equal("multiline_source_gap", result["truncation_reason"]!.GetValue<string>());
            Assert.Contains("refresh", result["recovery_guidance"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            args["maxBytes"] = 100;
            Assert.Contains(CommandErrorCodes.ResponseBudgetTooSmall, Call5349(tool, args).ToJsonString(), StringComparison.Ordinal);
        }
        var tools = _server.HandleMessage(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 5399, ["method"] = "tools/list" })!["result"]!["tools"]!.AsArray();
        foreach (var tool in tools.Where(item => item!["name"]!.GetValue<string>() is "find" or "find_in_file"))
        {
            var properties = tool!["inputSchema"]!["properties"]!;
            Assert.Equal("boolean", properties["multiline"]!["type"]!.GetValue<string>());
            Assert.Equal(64, properties["windowLines"]!["maximum"]!.GetValue<int>());
            Assert.Equal(262144, properties["windowBytes"]!["maximum"]!.GetValue<int>());
            Assert.Contains("line-local", tool["description"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }
}
