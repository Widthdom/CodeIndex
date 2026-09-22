using System.Text.Json.Nodes;
using CodeIndex.Cli;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private static void AddSemanticSearchTools(JsonArray tools)
    {
        var search = tools.OfType<JsonObject>().Single(tool => tool["name"]!.GetValue<string>() == "search");
        var scopedFind = tools.OfType<JsonObject>().Single(tool => tool["name"]!.GetValue<string>() == "find_in_file");
        foreach (var tool in new[] { search, scopedFind })
        {
            var properties = tool["inputSchema"]!["properties"]!.AsObject();
            properties["origin"] = StringOrArraySchema(
                "Include lexical match origins (OR within the list). Accepts comma-separated strings or arrays: "
                + string.Join(", ", CliFlagSchema.GetCanonicalValuesForCommand("search", "--origin"))
                + ". Bounded indexed lexical context supports C# and Python (3.12/3.13 strings and f-strings); shell uses line-local classification. Unknown context keeps filtered results non-authoritative.");
            properties["excludeOrigin"] = StringOrArraySchema("Exclude lexical match origins; exclusions win over inclusion. Accepts comma-separated strings or arrays.");
            properties["resultKind"] = StringOrArraySchema("Include result kinds, using CLI search classification. Find supports origin names and identifier; declaration/call_site require search.");
            properties["excludeComments"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            properties["excludeStrings"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            properties["excludeFixtures"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
        }

        scopedFind["description"] = scopedFind["description"]!.GetValue<string>()
            + " Regex is line-local unless multiline=true (example: A\\nB). Semantic filters require regex=true and multiline=false. / 通常の正規表現は行単位。複数行の例 A\\nB には multiline=true を指定。意味フィルターは regex=true、multiline=false が必要。";
        var scopedProperties = scopedFind["inputSchema"]!["properties"]!.AsObject();
        scopedProperties["multiline"] = new JsonObject
        {
            ["type"] = "boolean",
            ["default"] = false,
            ["description"] = "Pass bounded multiline source to regex=true; default regex is line-local. Example: A\\nB. No semantic/focus/context filters. Non-overlapping matches expose exclusive match_end_line/match_end_column; LF-normalized source, UTF-16 columns. See docs/find-multiline.md.",
        };
        scopedProperties["windowLines"] = new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["maximum"] = 64,
            ["default"] = 8,
            ["description"] = "Maximum physical lines per multiline owner window; requires multiline=true and binds cursors.",
        };
        scopedProperties["windowBytes"] = new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["maximum"] = 262144,
            ["default"] = 65536,
            ["description"] = "Maximum LF-normalized UTF-8 window bytes; overflow is partial, never authoritative absence. Requires multiline=true; binds cursors.",
        };
        scopedProperties["cursor"] = new JsonObject
        {
            ["type"] = "string",
            ["maxLength"] = MaxMcpQueryCursorCharacters,
            ["description"] = "Resume next_cursor with the same query, filters, and count mode. Limit and maxBytes may change. Restart after indexing.",
        };
        scopedProperties["countOnly"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
        scopedProperties["maxBytes"] = new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["maximum"] = MaxConfiguredResponseBytes,
            ["default"] = DefaultFindMaxBytes,
            ["description"] = "Maximum UTF-8 bytes in structuredContent (default 65536). Whole rows are paged without advancing past omitted matches. Server response limits also apply.",
        };
        var schema = scopedFind["inputSchema"]!.DeepClone().AsObject();
        schema["required"] = new JsonArray { "query" };
        schema["properties"]!["all"] = new JsonObject
        {
            ["type"] = "boolean",
            ["default"] = false,
            ["description"] = "Explicitly scan all indexed files with file/line safety caps. Specify either all=true or path, never both.",
        };
        schema["properties"]!["path"]!["description"] = "Explicit file/path scope instead of all=true; accepts a string or array.";
        schema["properties"]!["lineScanLimit"] = new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = 1,
            ["maximum"] = QueryCommandRunner.MaxFindLineScanLimit,
            ["default"] = QueryCommandRunner.FindAllLineScanLimit,
            ["description"] = "Maximum indexed lines per all=true scan page; may change when resuming a cursor. Requires all=true.",
        };
        tools.Add(CreateToolDefinition("find",
            "Bounded repository-wide literal or regex find over indexed files. Regex is line-local unless multiline=true (example: A\\nB); semantic filters cannot be combined with multiline. Pass all=true or an explicit path. Resume next_cursor after row, file, line, or byte caps; inspect scan_complete, partial_result, authority, and recovery_guidance. Semantic filters require regex=true and reuse CLI classification. / 索引済みファイルを対象とする上限付きのリポジトリ横断検索。通常の正規表現は行単位。複数行には multiline=true を指定し、意味フィルターとは併用しない。all=true または path を指定する。行数・ファイル数・走査行数・応答サイズの上限に達したら next_cursor で続行し、走査完了・部分結果・確定性・復旧案内を確認する。意味フィルターは regex=true が必要で CLI と同じ分類を使う。",
            schema, ReadOnlyAnnotations()));
    }
}
