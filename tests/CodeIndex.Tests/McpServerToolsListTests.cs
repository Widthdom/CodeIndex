using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Mcp;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void ToolsList_OutlinePublishesPaginationProjectionAndByteControls_Issue4897()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":4897,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var outline = response["result"]!["tools"]!.AsArray()
            .Single(tool => tool!["name"]!.GetValue<string>() == "outline")!;
        var properties = outline["inputSchema"]!["properties"]!;

        Assert.NotNull(properties["fields"]);
        Assert.Contains(
            properties["sort"]!["enum"]!.AsArray(),
            value => value!.GetValue<string>() == "source");
        Assert.Equal(100, properties["limit"]!["default"]!.GetValue<int>());
        Assert.Equal(1, properties["limit"]!["minimum"]!.GetValue<int>());
        Assert.Equal(200, properties["limit"]!["maximum"]!.GetValue<int>());
        Assert.Contains("page:v1", properties["cursor"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, properties["maxBytes"]!["minimum"]!.GetValue<int>());
        Assert.Equal(
            McpServer.MaxClientResponseJsonBytes,
            properties["maxBytes"]!["maximum"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_IndexPathSchemaReflectsProjectPathContract_Issue3186()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var indexTool = tools.First(t => t!["name"]!.GetValue<string>() == "index")!;
        var pathSchema = indexTool["inputSchema"]!["properties"]!["path"]!;

        Assert.Equal("string", pathSchema["type"]!.GetValue<string>());
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, pathSchema["maxLength"]!.GetValue<int>());
        Assert.DoesNotContain("(?!/)", pathSchema["pattern"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("absolute or relative", pathSchema["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsList_QualifiedCommonCallCompletenessOption_IsScopedToGraphTools_Issue4867()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;
        var tools = response["result"]!["tools"]!.AsArray();

        foreach (var toolName in new[] { "references", "callers", "callees" })
        {
            var tool = tools.First(candidate => candidate!["name"]!.GetValue<string>() == toolName)!;
            var option = tool["inputSchema"]!["properties"]!["includeQualifiedCommonCalls"]!;
            Assert.Equal("boolean", option["type"]!.GetValue<string>());
            Assert.False(option["default"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void ToolsList_MemberReadCompatibilityOption_IsScopedToTraversalTools_Issue4894()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;
        var tools = response["result"]!["tools"]!.AsArray();

        foreach (var toolName in new[] { "callers", "callees", "impact_analysis" })
        {
            var tool = tools.First(candidate => candidate!["name"]!.GetValue<string>() == toolName)!;
            var option = tool["inputSchema"]!["properties"]!["includeMemberReads"]!;
            Assert.Equal("boolean", option["type"]!.GetValue<string>());
            Assert.False(option["default"]!.GetValue<bool>());
        }

        var references = tools.First(candidate => candidate!["name"]!.GetValue<string>() == "references")!;
        Assert.Null(references["inputSchema"]!["properties"]!["includeMemberReads"]);
    }

    [Fact]
    public void ToolsList_EachToolPublishesSchemaAndExampleContract()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(McpToolFilter.KnownToolNames.Count, tools.Count);
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
            Assert.Equal(
                "https://json-schema.org/draft/2020-12/schema",
                tool["outputSchema"]!["$schema"]!.GetValue<string>());
            Assert.Equal("object", tool["outputSchema"]!["type"]!.GetValue<string>());
            Assert.Equal(2, tool["outputSchema"]!["oneOf"]!.AsArray().Count);
            Assert.NotNull(tool["outputSchema"]!["$defs"]!["success"]);
            Assert.NotNull(tool["outputSchema"]!["$defs"]!["error"]);

            var examples = tool["examples"]!.AsArray();
            Assert.NotEmpty(examples);
            foreach (var example in examples)
            {
                Assert.Equal("tools/call", example!["request"]!["method"]!.GetValue<string>());
                Assert.Equal(tool["name"]!.GetValue<string>(), example["request"]!["params"]!["name"]!.GetValue<string>());
                Assert.NotNull(example["request"]!["params"]!["arguments"]);
                Assert.False(string.IsNullOrWhiteSpace(example["response_excerpt"]!.GetValue<string>()));
            }
        }
    }

    [Fact]
    public void ToolsList_DefaultCatalogIsAgentSafeAndPointsToFullDefinitions_Issues4724_5059()
    {
        var fullRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var compactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!;

        var fullResponse = _server.HandleMessage(fullRequest)!;
        var compactResponse = _server.HandleMessage(compactRequest)!;
        var compactResult = compactResponse["result"]!;
        var compactTools = compactResult["tools"]!.AsArray();
        var fullTools = fullResponse["result"]!["tools"]!.AsArray();

        Assert.Equal(McpToolFilter.KnownToolNames.Count, compactTools.Count);
        Assert.Equal(
            fullTools.Select(tool => tool!["name"]!.GetValue<string>()),
            compactTools.Select(tool => tool!["name"]!.GetValue<string>()));
        Assert.True(
            Encoding.UTF8.GetByteCount(compactResponse.ToJsonString())
            <= McpServer.DefaultToolsListResponseByteBudget);
        Assert.True(
            Encoding.UTF8.GetByteCount(compactResponse.ToJsonString())
            < Encoding.UTF8.GetByteCount(fullResponse.ToJsonString()) / 3);
        foreach (var tool in compactTools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());
            Assert.True(tool["inputSchema"]!.AsObject().Count > 1);
            Assert.Null(tool["outputSchema"]);
            Assert.Null(tool["examples"]);
            Assert.NotNull(tool["annotations"]);
            Assert.NotNull(tool["x-stability"]);
        }

        var compactSearch = compactTools.Single(tool => tool!["name"]!.GetValue<string>() == "search")!;
        Assert.Null(compactSearch["inputSchema"]!["properties"]!["query"]!["description"]);
        Assert.Equal(1, compactSearch["inputSchema"]!["properties"]!["query"]!["minLength"]!.GetValue<int>());
        Assert.Contains(
            compactSearch["inputSchema"]!["anyOf"]!.AsArray(),
            mode => mode!["required"]!.AsArray().Any(required => required!.GetValue<string>() == "query"));
        var compactSuggestion = compactTools.Single(tool => tool!["name"]!.GetValue<string>() == "suggest_improvement")!;
        Assert.Contains("Never include source code", compactSuggestion["description"]!.GetValue<string>(), StringComparison.Ordinal);
        var compactUnused = compactTools.Single(tool => tool!["name"]!.GetValue<string>() == "unused_symbols")!;
        Assert.Contains("meaningful only for languages with reference extraction", compactUnused["description"]!.GetValue<string>(), StringComparison.Ordinal);
        var compactValidate = compactTools.Single(tool => tool!["name"]!.GetValue<string>() == "validate")!;
        Assert.Contains("authoritative only while `file_issues_data_current` is true", compactValidate["description"]!.GetValue<string>(), StringComparison.Ordinal);
        var compactImpact = compactTools.Single(tool => tool!["name"]!.GetValue<string>() == "impact_analysis")!;
        Assert.Contains("File-level fallback may be heuristic", compactImpact["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(
            compactTools,
            tool => Assert.True(tool!["description"]!.GetValue<string>().Length <= 240));

        var meta = compactResult["_meta"]!;
        Assert.Equal("compact", meta["format"]!.GetValue<string>());
        Assert.False(meta["definitions_complete"]!.GetValue<bool>());
        Assert.True(meta["discovery_contract"]!["input_schemas_are_authoritative"]!.GetValue<bool>());
        Assert.False(meta["discovery_contract"]!["output_schemas_are_included"]!.GetValue<bool>());
        Assert.False(meta["discovery_contract"]!["examples_are_included"]!.GetValue<bool>());
        Assert.True(meta["discovery_contract"]!["full_definitions_available_on_demand"]!.GetValue<bool>());
        Assert.Equal("tools/list", meta["full_definition_request"]!["method"]!.GetValue<string>());
        Assert.Equal("full", meta["full_definition_request"]!["params"]!["format"]!.GetValue<string>());

        var sections = meta["size_telemetry"]!["sections"]!;
        Assert.True(sections["input_schemas"]!["utf8_bytes"]!.GetValue<int>() > 0);
        Assert.Equal(0, sections["output_schemas"]!["utf8_bytes"]!.GetValue<int>());
        Assert.Equal(0, sections["examples"]!["utf8_bytes"]!.GetValue<int>());
        Assert.False(meta["size_telemetry"]!["contains_tool_arguments"]!.GetValue<bool>());

        var fullSections = fullResponse["result"]!["_meta"]!["size_telemetry"]!["sections"]!;
        var measuredFullDescriptionBytes = fullTools.Sum(tool =>
            Encoding.UTF8.GetByteCount(tool!["description"]!.ToJsonString()));
        Assert.Equal(measuredFullDescriptionBytes, fullSections["descriptions"]!["utf8_bytes"]!.GetValue<int>());
        Assert.Contains(
            fullTools,
            tool => tool!["description"]!.GetValue<string>().Any(character => character > 127));
        Assert.True(fullSections["output_schemas"]!["utf8_bytes"]!.GetValue<int>() > 0);
        Assert.True(fullSections["examples"]!["utf8_bytes"]!.GetValue<int>() > 0);
        Assert.True(fullSections["capability_metadata"]!["utf8_bytes"]!.GetValue<int>() > 0);
    }

    [Fact]
    public void ToolsList_NamesRetrievesSelectedFullDefinitions_Issue4724()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full","names":"status"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tool = Assert.Single(response["result"]!["tools"]!.AsArray())!;
        Assert.Equal("status", tool["name"]!.GetValue<string>());
        Assert.NotEmpty(tool["examples"]!.AsArray());
        Assert.NotNull(tool["inputSchema"]!["properties"]!["fields"]);
        Assert.True(tool["inputSchema"]!["additionalProperties"] is not null);
        var meta = response["result"]!["_meta"]!;
        Assert.Equal(1, meta["response_controls"]!["tools_total"]!.GetValue<int>());
        Assert.Equal(
            McpToolFilter.KnownToolNames.Count,
            meta["response_controls"]!["enabled_tools_total"]!.GetValue<int>());
        Assert.True(meta["response_controls"]!["names_filtered"]!.GetValue<bool>());
        Assert.Equal("name_filtered", meta["catalog_scope"]!.GetValue<string>());
        Assert.False(meta["discovery_contract"]!["tools_list_is_authoritative"]!.GetValue<bool>());
        Assert.Equal("enabled_tools", meta["discovery_contract"]!["catalog_metadata_scope"]!.GetValue<string>());
        Assert.Contains("search", meta["capability_groups"]!["discovery"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void ToolsList_ContinuationCursorPreservesCompactNameFilter_Issue4724()
    {
        var firstRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"compact","names":["search","status"],"limit":1}}""")!;
        var firstResponse = _server.HandleMessage(firstRequest)!;
        var cursor = firstResponse["result"]!["nextCursor"]!.GetValue<string>();
        var secondRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list",
            ["params"] = new JsonObject { ["cursor"] = cursor },
        };

        var secondResponse = _server.HandleMessage(secondRequest)!;
        var tool = Assert.Single(secondResponse["result"]!["tools"]!.AsArray())!;
        Assert.Equal("status", tool["name"]!.GetValue<string>());
        Assert.Null(tool["examples"]);
        Assert.True(tool["inputSchema"]!.AsObject().Count > 1);
        Assert.Equal("compact", secondResponse["result"]!["_meta"]!["format"]!.GetValue<string>());
        Assert.True(secondResponse["result"]!["_meta"]!["response_controls"]!["names_filtered"]!.GetValue<bool>());

        var conflictingRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 3,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["cursor"] = cursor,
                ["format"] = "full",
            },
        };
        var conflictingResponse = _server.HandleMessage(conflictingRequest)!;
        Assert.Equal(-32602, conflictingResponse["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_ExplicitCompactMatchesDefaultResponse_Issues4724_5059()
    {
        var defaultRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var explicitRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"format":"compact"}}""")!;

        var defaultResponse = _server.HandleMessage(defaultRequest)!;
        var explicitResponse = _server.HandleMessage(explicitRequest)!;

        Assert.Equal(
            defaultResponse["result"]!["tools"]!.ToJsonString(),
            explicitResponse["result"]!["tools"]!.ToJsonString());
        var defaultMeta = defaultResponse["result"]!["_meta"]!.DeepClone().AsObject();
        var explicitMeta = explicitResponse["result"]!["_meta"]!.DeepClone().AsObject();
        defaultMeta.Remove("correlation_id");
        defaultMeta.Remove("request_id");
        explicitMeta.Remove("correlation_id");
        explicitMeta.Remove("request_id");
        Assert.Equal(defaultMeta.ToJsonString(), explicitMeta.ToJsonString());
    }

    [Fact]
    public void ToolsList_InvalidCatalogControlsReturnInvalidParams_Issue4724()
    {
        var formatRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"summary"}}""")!;
        var namesRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{"names":[]}}""")!;

        var formatResponse = _server.HandleMessage(formatRequest)!;
        var namesResponse = _server.HandleMessage(namesRequest)!;

        Assert.Equal(-32602, formatResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", formatResponse["error"]!["data"]!["category"]!.GetValue<string>());
        Assert.Equal(-32602, namesResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxToolsListNameFilters, namesResponse["error"]!["data"]!["max_tool_name_filters"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_ReturnsAllKnownTools()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(McpToolFilter.KnownToolNames.Count, tools.Count);

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("search", names);
        Assert.Contains("impact_analysis", names);
        Assert.Contains("definition", names);
        Assert.Contains("references", names);
        Assert.Contains("callers", names);
        Assert.Contains("callees", names);
        Assert.Contains("symbols", names);
        Assert.Contains("files", names);
        Assert.Contains("find_in_file", names);
        Assert.Contains("excerpt", names);
        Assert.Contains("read_resource", names);
        Assert.Contains("map", names);
        Assert.Contains("analyze_symbol", names);
        Assert.Contains("status", names);
        Assert.Contains("outline", names);
        Assert.Contains("batch_query", names);
        Assert.Contains("validate", names);
        Assert.Contains("ping", names);
        Assert.Contains("deps", names);
        Assert.Contains("languages", names);
        Assert.Contains("index", names);
        Assert.Contains("backfill_fold", names);
        Assert.Contains("suggest_improvement", names);
    }

    [Fact]
    public void ToolsList_SuggestionCategorySchemaMatchesValidCategories_Issue4423()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tool = response["result"]!["tools"]!.AsArray()
            .First(item => item!["name"]!.GetValue<string>() == "suggest_improvement")!;
        var categories = tool["inputSchema"]!["properties"]!["category"]!["enum"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();

        Assert.Equal(SuggestionRecord.ValidCategories, categories);
    }

    [Fact]
    public void ToolsList_MetaAdvertisesFirstTimeAiDiscoveryCatalog()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var meta = response["result"]!["_meta"]!;
        Assert.Equal("cdidx.mcp.tools.v1", meta["catalog_version"]!.GetValue<string>());

        var guide = meta["first_time_ai_guide"]!.AsArray()
            .Select(entry => entry!.GetValue<string>())
            .ToArray();
        Assert.Contains(guide, entry => entry.Contains("status", StringComparison.Ordinal));
        Assert.Contains(guide, entry => entry.Contains("batch_query", StringComparison.Ordinal));

        var groups = meta["capability_groups"]!;
        var discovery = groups["discovery"]!.AsArray().Select(entry => entry!.GetValue<string>()).ToArray();
        var navigation = groups["symbol_navigation"]!.AsArray().Select(entry => entry!.GetValue<string>()).ToArray();
        var maintenance = groups["index_maintenance"]!.AsArray().Select(entry => entry!.GetValue<string>()).ToArray();
        Assert.Contains("search", discovery);
        Assert.Contains("definition", navigation);
        Assert.Contains("index", maintenance);

        var workflows = meta["recommended_workflows"]!.AsArray();
        Assert.Contains(workflows, workflow =>
            workflow!["name"]!.GetValue<string>() == "first_pass_orientation"
            && workflow["tools"]!.AsArray().Any(tool => tool!.GetValue<string>() == "status"));

        var contract = meta["discovery_contract"]!;
        Assert.True(contract["tools_list_is_authoritative"]!.GetValue<bool>());
        Assert.True(contract["disabled_tools_are_omitted"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsList_LimitAndCursorPageDiscoveryCatalog_Issue4304()
    {
        var firstRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["limit"] = 3,
            },
        };
        var firstResponse = _server.HandleMessage(firstRequest)!;

        var firstResult = firstResponse["result"]!;
        var firstTools = firstResult["tools"]!.AsArray();
        Assert.Equal(3, firstTools.Count);
        Assert.StartsWith("v1.", firstResult["nextCursor"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(new[] { "search", "definition", "references" }, firstTools.Select(tool => tool!["name"]!.GetValue<string>()).ToArray());

        var controls = firstResult["_meta"]!["response_controls"]!;
        Assert.Equal(McpToolFilter.KnownToolNames.Count, controls["tools_total"]!.GetValue<int>());
        Assert.Equal(3, controls["tools_returned"]!.GetValue<int>());
        Assert.Equal(0, controls["tools_offset"]!.GetValue<int>());
        Assert.Equal(3, controls["tools_page_size"]!.GetValue<int>());
        Assert.Equal(McpServer.DefaultToolsListPageSize, controls["default_tools_list_page_size"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxToolsListPageSize, controls["max_tools_list_page_size"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, controls["max_pagination_offset"]!.GetValue<int>());

        var contract = firstResult["_meta"]!["discovery_contract"]!;
        Assert.True(contract["pagination_supported"]!.GetValue<bool>());
        Assert.Equal("params.cursor", contract["cursor_param"]!.GetValue<string>());
        Assert.Equal("params.limit", contract["limit_param"]!.GetValue<string>());

        var secondRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["limit"] = 3,
                ["cursor"] = firstResult["nextCursor"]!.GetValue<string>(),
            },
        };
        var secondResponse = _server.HandleMessage(secondRequest)!;
        var secondTools = secondResponse["result"]!["tools"]!.AsArray();

        Assert.Equal(3, secondTools.Count);
        Assert.Equal(new[] { "callers", "callees", "symbols" }, secondTools.Select(tool => tool!["name"]!.GetValue<string>()).ToArray());
        Assert.StartsWith("v1.", secondResponse["result"]!["nextCursor"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(3, secondResponse["result"]!["_meta"]!["response_controls"]!["tools_offset"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_LegacyNumericCursorContinuesFullCatalog_Issue5059()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":5059,"method":"tools/list","params":{"cursor":"3","limit":3}}""")!;

        var response = _server.HandleMessage(request)!;
        var result = response["result"]!;
        var tools = result["tools"]!.AsArray();

        Assert.Equal(new[] { "callers", "callees", "symbols" }, tools.Select(tool => tool!["name"]!.GetValue<string>()).ToArray());
        Assert.All(tools, tool =>
        {
            Assert.NotNull(tool!["outputSchema"]);
            Assert.NotNull(tool["examples"]);
        });
        Assert.Equal("full", result["_meta"]!["size_telemetry"]!["format"]!.GetValue<string>());
        Assert.Equal("6", result["nextCursor"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_InvalidPaginationParamsReturnInvalidParams_Issue4304()
    {
        var invalidParams = JsonNode.Parse("""{"jsonrpc":"2.0","id":0,"method":"tools/list","params":[]}""")!;
        var paramsResponse = _server.HandleMessage(invalidParams)!;

        Assert.Equal(-32602, paramsResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", paramsResponse["error"]!["data"]!["category"]!.GetValue<string>());

        var invalidCursor = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["cursor"] = "not-an-offset",
            },
        };
        var cursorResponse = _server.HandleMessage(invalidCursor)!;

        Assert.Equal(-32602, cursorResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", cursorResponse["error"]!["data"]!["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, cursorResponse["error"]!["data"]!["max_pagination_offset"]!.GetValue<int>());

        var invalidLimit = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/list",
            ["params"] = new JsonObject
            {
                ["limit"] = McpServer.MaxToolsListPageSize + 1,
            },
        };
        var limitResponse = _server.HandleMessage(invalidLimit)!;

        Assert.Equal(-32602, limitResponse["error"]!["code"]!.GetValue<int>());
        Assert.Equal("invalid_argument", limitResponse["error"]!["data"]!["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxToolsListPageSize, limitResponse["error"]!["data"]!["max_tools_list_page_size"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_EveryDescriptionIncludesLanguageSupportClause()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            var description = tool["description"]!.GetValue<string>();
            Assert.Contains("Language support:", description, StringComparison.Ordinal);

            if (name is "references" or "callers" or "callees")
            {
                var expected = "Supports graph/reference extraction for: " +
                    string.Join(", ", ReferenceExtractor.GetSupportedLanguages().OrderBy(lang => lang, StringComparer.Ordinal));
                Assert.Contains(expected, description, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ToolsList_SearchAdvertisesQueryOrRecipeModes_Issue3545()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var modes = searchTool["inputSchema"]!["anyOf"]!.AsArray()
            .Select(mode => mode!["required"]!.AsArray().Single()!.GetValue<string>())
            .ToArray();
        Assert.Contains("query", modes);
        Assert.Contains("recipe", modes);
        Assert.Contains("listRecipes", modes);
    }

    [Fact]
    public void ToolsList_EveryInputSchemaRejectsAdditionalPropertiesAndPublishesStability()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            Assert.False(tool!["inputSchema"]!["additionalProperties"]!.GetValue<bool>());
            Assert.Contains(tool["x-stability"]!.GetValue<string>(), new[] { "stable", "experimental", "deprecated" });
        }

        var impact = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        Assert.Equal("experimental", impact["x-stability"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_SearchIncludesPathFilterParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var properties = searchTool["inputSchema"]!["properties"]!;

        Assert.NotNull(properties["path"]);
        Assert.NotNull(properties["excludePaths"]);
        Assert.NotNull(properties["excludeTests"]);
    }

    [Fact]
    public void ToolsList_SearchDescriptionStaysCompact()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var description = searchTool["description"]!.GetValue<string>();

        Assert.True(description.Length < 1000);
        Assert.Contains("USER_GUIDE.md#search", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsList_CommonSchemasAdvertiseClientSideConstraints()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var searchProperties = searchTool["inputSchema"]!["properties"]!;
        Assert.Equal(1, searchProperties["query"]!["minLength"]!.GetValue<int>());
        Assert.Equal(1024, searchProperties["query"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(1, searchProperties["limit"]!["minimum"]!.GetValue<int>());
        Assert.Equal(200, searchProperties["limit"]!["maximum"]!.GetValue<int>());

        var pathStringSchema = searchProperties["path"]!["oneOf"]!.AsArray()[0]!;
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, pathStringSchema["maxLength"]!.GetValue<int>());
        Assert.NotNull(pathStringSchema["pattern"]);
        var pathArraySchema = searchProperties["path"]!["oneOf"]!.AsArray()[1]!;
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterCount, pathArraySchema["maxItems"]!.GetValue<int>());
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, pathArraySchema["items"]!["maxLength"]!.GetValue<int>());
        var excludePathsSchema = searchProperties["excludePaths"]!;
        var excludePathsStringSchema = excludePathsSchema["oneOf"]!.AsArray()[0]!;
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, excludePathsStringSchema["maxLength"]!.GetValue<int>());
        var excludePathsArraySchema = excludePathsSchema["oneOf"]!.AsArray()[1]!;
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterCount, excludePathsArraySchema["maxItems"]!.GetValue<int>());
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, excludePathsArraySchema["items"]!["maxLength"]!.GetValue<int>());

        var referencesTool = tools.First(t => t!["name"]!.GetValue<string>() == "references")!;
        var kindEnum = referencesTool["inputSchema"]!["properties"]!["kind"]!["enum"]!.AsArray()
            .Select(v => v!.GetValue<string>())
            .ToArray();
        Assert.Contains("call", kindEnum);
        Assert.Contains("type_reference", kindEnum);

        var mapTool = tools.First(t => t!["name"]!.GetValue<string>() == "map")!;
        var sectionsSchema = mapTool["inputSchema"]!["properties"]!["sections"]!;
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterCount, sectionsSchema["maxItems"]!.GetValue<int>());
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, sectionsSchema["items"]!["maxLength"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_NavigationDescriptionsIncludeConcreteExamples()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var expectedExamples = new Dictionary<string, string[]>
        {
            ["search"] = ["USER_GUIDE.md#search", "prefix", "exactSubstring"],
            ["definition"] = ["Examples:", "例:", "definition {\"query\":\"McpServer\"}", "\"includeBody\":true", "\"exactName\":true"],
            ["references"] = ["Examples:", "例:", "references {\"query\":\"Run\"}", "\"kind\":\"type_reference\""],
            ["callers"] = ["Examples:", "例:", "callers {\"query\":\"HandleRequest\"}", "\"rankBy\":\"weighted\""],
            ["callees"] = ["Examples:", "例:", "callees {\"query\":\"Run\"}", "\"kind\":\"instantiate\"", "\"limit\":10"],
            ["symbols"] = ["Examples:", "例:", "symbols {\"query\":\"Service\"}", "\"kind\":\"function\"", "\"exactName\":true"],
        };

        foreach (var (name, fragments) in expectedExamples)
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var description = tool["description"]!.GetValue<string>();
            foreach (var fragment in fragments)
                Assert.Contains(fragment, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolsList_NavigationDescriptionsExplainWhenAndNextStep()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "search", "definition", "references", "callers", "callees", "symbols", "files", "excerpt", "find_in_file", "map", "outline" })
        {
            var description = tools.First(t => t!["name"]!.GetValue<string>() == name)!["description"]!.GetValue<string>();
            Assert.Contains("Use this", description, StringComparison.Ordinal);
            Assert.Contains("Prefer", description, StringComparison.Ordinal);
        }

        var searchDescription = tools.First(t => t!["name"]!.GetValue<string>() == "search")!["description"]!.GetValue<string>();
        Assert.Contains("before shell grep", searchDescription, StringComparison.Ordinal);
        Assert.Contains("common next step", searchDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsList_CommonSchemaDescriptionsGuideDisambiguation()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchProperties = tools.First(t => t!["name"]!.GetValue<string>() == "search")!["inputSchema"]!["properties"]!;
        Assert.Contains("identifiers", searchProperties["query"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("case-sensitive exact text identity", searchProperties["exactSubstring"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("identifier/token boundaries", searchProperties["tokenBoundary"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("partial tokens", searchProperties["prefix"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("narrow by module", searchProperties["path"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("production-code investigation", searchProperties["excludeTests"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("generated code", searchProperties["includeGenerated"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("large result sets", searchProperties["format"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);

        var definitionProperties = tools.First(t => t!["name"]!.GetValue<string>() == "definition")!["inputSchema"]!["properties"]!;
        Assert.Contains("symbol name must match exactly", definitionProperties["exactName"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Alias of `exactName`", definitionProperties["exact"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsList_ExactAliasParametersAreExposed()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchTool = tools.First(t => t!["name"]!.GetValue<string>() == "search")!;
        var symbolsTool = tools.First(t => t!["name"]!.GetValue<string>() == "symbols")!;

        Assert.NotNull(searchTool["inputSchema"]!["properties"]!["exactSubstring"]);
        Assert.NotNull(searchTool["inputSchema"]!["properties"]!["exact"]);
        Assert.NotNull(symbolsTool["inputSchema"]!["properties"]!["exactName"]);
        Assert.NotNull(symbolsTool["inputSchema"]!["properties"]!["exact"]);
    }

    [Fact]
    public void ToolsList_CallersCalleesKindDescription_ExcludesMetadataKinds()
    {
        // Keep the `kind` schema description honest: the callers/callees handlers reject
        // metadata kinds (`attribute`, `annotation`) as a usage error, so the schema must
        // not advertise them as valid filter values.
        // callers/callees の handler は metadata kinds (`attribute` / `annotation`) を拒否するため、
        // schema の `kind` description も有効値として列挙しないこと。
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "callers", "callees" })
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var kindDescription = tool["inputSchema"]!["properties"]!["kind"]!["description"]!.GetValue<string>();

            Assert.Contains("canonical", kindDescription);
            Assert.Contains("call, instantiate, subscribe", kindDescription);
            Assert.Contains("rejected", kindDescription);
            Assert.Contains("references", kindDescription);
        }
    }

    [Fact]
    public void ToolsList_CallersCalleesAnalyzeSymbolDescriptions_PinSnakeCaseMixedKindFields()
    {
        // MCP structured JSON follows the same snake_case convention as CLI JSON.
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "callers", "callees", "analyze_symbol" })
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var description = tool["description"]!.GetValue<string>();

            Assert.Contains("reference_kind", description);
            Assert.Contains("reference_kinds", description);
            Assert.Contains("has_mixed_reference_kinds", description);
            Assert.DoesNotContain("referenceKind", description);
            Assert.DoesNotContain("hasMixedReferenceKinds", description);
        }
    }

    [Fact]
    public void ToolsList_ImpactAnalysisDescribesHeuristicFallback()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var impactTool = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        var description = impactTool["description"]!.GetValue<string>();
        var limitDescription = impactTool["inputSchema"]!["properties"]!["limit"]!["description"]!.GetValue<string>();

        Assert.Contains("heuristic file-level dependency hints", description);
        Assert.Contains("impact_mode", description);
        Assert.Contains("file_impacts", description);
        Assert.Contains("heuristic file-level dependency hints", limitDescription);
        Assert.Contains("truncated", limitDescription);
    }

    [Fact]
    public void ToolsList_DepsExposesGeneratedCodeFilter_Issue3544()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var depsTool = tools.First(t => t!["name"]!.GetValue<string>() == "deps")!;
        var includeGenerated = depsTool["inputSchema"]!["properties"]!["includeGenerated"]!;

        Assert.Equal("boolean", includeGenerated["type"]!.GetValue<string>());
        Assert.False(includeGenerated["default"]!.GetValue<bool>());
        Assert.Contains("source or target", includeGenerated["description"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_IndexHasRequiredPathParam()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var indexTool = tools.First(t => t!["name"]!.GetValue<string>() == "index")!;
        var required = indexTool["inputSchema"]!["required"]!.AsArray();
        Assert.Contains("path", required.Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void ToolsList_QueryToolsHaveReadOnlyAnnotations()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var queryToolNames = new[] { "search", "definition", "references", "callers", "callees", "symbols", "files", "find_in_file", "excerpt", "map", "analyze_symbol", "status", "outline" };

        foreach (var name in queryToolNames)
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var annotations = tool["annotations"];
            Assert.NotNull(annotations);
            Assert.True(annotations!["readOnlyHint"]!.GetValue<bool>(), $"{name} should have readOnlyHint=true");
            Assert.False(annotations["destructiveHint"]!.GetValue<bool>(), $"{name} should have destructiveHint=false");
            Assert.True(annotations["idempotentHint"]!.GetValue<bool>(), $"{name} should have idempotentHint=true");
            Assert.False(annotations["openWorldHint"]!.GetValue<bool>(), $"{name} should have openWorldHint=false");
        }
    }

    [Fact]
    public void ToolsList_IndexToolHasWriteAnnotations()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var indexTool = tools.First(t => t!["name"]!.GetValue<string>() == "index")!;
        var annotations = indexTool["annotations"];
        Assert.NotNull(annotations);
        Assert.False(annotations!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(annotations["destructiveHint"]!.GetValue<bool>());
        Assert.False(annotations["idempotentHint"]!.GetValue<bool>());
        Assert.False(annotations["openWorldHint"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsList_FilteredByAllowList_HidesDisabledTools()
    {
        var allow = McpToolFilter.Parse("search, references", null);
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, allow);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = server.HandleMessage(request)!;
        var tools = response["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "references", "search" }, names);
    }

    [Fact]
    public void ToolsList_KnownToolNamesMatchAdvertisedTools_Issue3829()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var advertised = response["result"]!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var known = McpToolFilter.KnownToolNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(known, advertised);
    }

    [Fact]
    public void ToolsList_FilteredByDenyList_HidesDeniedTools()
    {
        var deny = McpToolFilter.Parse(null, "index,backfill_fold,suggest_improvement");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = server.HandleMessage(request)!;
        var names = response["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

        Assert.DoesNotContain("index", names);
        Assert.DoesNotContain("backfill_fold", names);
        Assert.DoesNotContain("suggest_improvement", names);
        Assert.Contains("search", names);
        Assert.Contains("references", names);
    }

    [Fact]
    public void ToolsList_FilteredMetaDoesNotAdvertiseDeniedTools()
    {
        var deny = McpToolFilter.Parse(null, "index,backfill_fold,suggest_improvement");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = server.HandleMessage(request)!;
        var groups = response["result"]!["_meta"]!["capability_groups"]!;

        var maintenance = groups["index_maintenance"]!.AsArray()
            .Select(tool => tool!.GetValue<string>())
            .ToArray();
        var feedback = groups["feedback"]!.AsArray()
            .Select(tool => tool!.GetValue<string>())
            .ToArray();

        Assert.DoesNotContain("index", maintenance);
        Assert.DoesNotContain("backfill_fold", maintenance);
        Assert.DoesNotContain("suggest_improvement", feedback);
        Assert.Contains("search", groups["discovery"]!.AsArray().Select(tool => tool!.GetValue<string>()));
    }

    [Fact]
    public void ToolsList_ImpactAnalysisMaxHopsSchemaDocumentsCap()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"format":"full"}}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var impactTool = tools.First(t => t!["name"]!.GetValue<string>() == "impact_analysis")!;
        var maxHopsSchema = impactTool["inputSchema"]!["properties"]!["maxHops"]!;

        Assert.Equal(50, maxHopsSchema["maximum"]!.GetValue<int>());
        Assert.Equal(0, maxHopsSchema["minimum"]!.GetValue<int>());
        var description = maxHopsSchema["description"]!.GetValue<string>();
        Assert.Contains("Server-side cap", description);
        Assert.Contains("warnings", description);
        Assert.Contains("max_hops_requested", description);
        var maxDepthSchema = impactTool["inputSchema"]!["properties"]!["maxDepth"]!;
        Assert.Contains("Deprecated alias", maxDepthSchema["description"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_BatchQuerySchemaAdvertisesLimitsAndControls_Issue3539()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var batchQuery = tools.First(tool => tool!["name"]!.GetValue<string>() == "batch_query")!;
        var properties = batchQuery["inputSchema"]!["properties"]!;
        var queries = properties["queries"]!;

        Assert.Equal(1, queries["minItems"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQuerySize, queries["maxItems"]!.GetValue<int>());
        var itemProperties = queries["items"]!["properties"]!;
        Assert.Equal("string", itemProperties["id"]!["type"]!.GetValue<string>());
        Assert.Equal("string", itemProperties["slotId"]!["type"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, properties["maxResponseBytes"]!["maximum"]!.GetValue<int>());
        Assert.False(properties["estimateOnly"]!["default"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsList_ReferencesOffsetSchemaAdvertisesCap()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var references = tools.First(tool => tool!["name"]!.GetValue<string>() == "references")!;
        var offset = references["inputSchema"]!["properties"]!["offset"]!;
        Assert.Equal(0, offset["minimum"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, offset["maximum"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsList_MapDepthSchemaAdvertisesCap_Issue3436()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var map = tools.First(tool => tool!["name"]!.GetValue<string>() == "map")!;
        var depth = map["inputSchema"]!["properties"]!["depth"]!;

        Assert.Equal(0, depth["minimum"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpMapDepth, depth["maximum"]!.GetValue<int>());
    }
}
