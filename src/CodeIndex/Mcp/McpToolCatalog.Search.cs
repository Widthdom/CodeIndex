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
                + string.Join(", ", CliFlagSchema.GetCanonicalValuesForCommand("search", "--origin")) + ".");
            properties["excludeOrigin"] = StringOrArraySchema("Exclude lexical match origins; exclusions win over inclusion. Accepts comma-separated strings or arrays.");
            properties["resultKind"] = StringOrArraySchema("Include result kinds, using CLI search classification. Find supports origin names and identifier; declaration/call_site require search.");
            properties["excludeComments"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            properties["excludeStrings"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
            properties["excludeFixtures"] = new JsonObject { ["type"] = "boolean", ["default"] = false };
        }

        scopedFind["description"] = scopedFind["description"]!.GetValue<string>()
            + " Semantic filters require regex=true. / 意味フィルターには regex=true が必要。";
        var scopedProperties = scopedFind["inputSchema"]!["properties"]!.AsObject();
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
            "Bounded repository-wide literal or regex find over indexed files. Pass all=true or an explicit path. Resume next_cursor after row, file, line, or byte caps; inspect scan_complete, partial_result, authority, and recovery_guidance. Semantic filters require regex=true and reuse CLI classification. / 索引済みファイルを対象とする上限付きのリポジトリ横断検索。all=true または path を指定する。行数・ファイル数・走査行数・応答サイズの上限に達したら next_cursor で続行し、走査完了・部分結果・確定性・復旧案内を確認する。意味フィルターは regex=true が必要で CLI と同じ分類を使う。",
            schema, ReadOnlyAnnotations()));
    }
}
