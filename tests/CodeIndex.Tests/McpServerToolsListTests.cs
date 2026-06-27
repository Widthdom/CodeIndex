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
    public void ToolsList_IndexPathSchemaReflectsProjectPathContract_Issue3186()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
    public void ToolsList_EachToolPublishesSchemaAndExampleContract()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(24, tools.Count);
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool!["name"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            Assert.Equal("object", tool["inputSchema"]!["type"]!.GetValue<string>());

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
    public void ToolsList_Returns23Tools()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(24, tools.Count);

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
    public void ToolsList_MetaAdvertisesFirstTimeAiDiscoveryCatalog()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
    public void ToolsList_EveryDescriptionIncludesLanguageSupportClause()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        var searchProperties = tools.First(t => t!["name"]!.GetValue<string>() == "search")!["inputSchema"]!["properties"]!;
        Assert.Contains("identifiers", searchProperties["query"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("case-sensitive exact text identity", searchProperties["exactSubstring"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
        var response = _server.HandleMessage(request)!;

        var tools = response["result"]!["tools"]!.AsArray();
        foreach (var name in new[] { "callers", "callees" })
        {
            var tool = tools.First(t => t!["name"]!.GetValue<string>() == name)!;
            var kindDescription = tool["inputSchema"]!["properties"]!["kind"]!["description"]!.GetValue<string>();

            Assert.Contains("call-graph", kindDescription);
            Assert.Contains("call, instantiate, subscribe", kindDescription);
            Assert.Contains("rejected", kindDescription);
            Assert.Contains("references", kindDescription);
        }
    }

    [Fact]
    public void ToolsList_CallersCalleesAnalyzeSymbolDescriptions_PinSnakeCaseMixedKindFields()
    {
        // MCP structured JSON follows the same snake_case convention as CLI JSON.
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;
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
