using System.Text.Json.Nodes;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void GraphTools_AcceptInspectSelectorAndKeepCliIdentitySemantics_Issue5187()
    {
        QueryCommandRunnerIssue5187Tests.SeedGraphFixture(_dbPath);
        var reader = new DbReader(_db.Connection);
        var definitions = reader.GetDefinitions(
            "Issue5187Shared",
            limit: 10,
            kind: null,
            lang: "csharp",
            includeBody: false,
            pathPatterns: null,
            excludePathPatterns: null,
            excludeTests: false,
            exact: true);
        var alpha = Assert.Single(definitions, definition => definition.ContainerName == "Issue5187Alpha");
        var selector = reader.BuildSymbolCandidateSelector(alpha).Selector;

        var requests = new (string Tool, JsonObject Arguments, string Expected, string Forbidden)[]
        {
            ("references", new JsonObject { ["selector"] = selector }, "src/Alpha.cs", "tests/Beta.cs"),
            ("callers", new JsonObject { ["selector"] = selector }, "InvokeAlpha", "InvokeBeta"),
            ("callees", new JsonObject { ["selector"] = selector }, "Issue5187AlphaLeaf", "Issue5187BetaLeaf"),
            ("impact_analysis", new JsonObject { ["selector"] = selector, ["maxHops"] = 1 }, "InvokeAlpha", "InvokeBeta"),
        };

        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            var result = CallIssue4853Tool(_server, request.Tool, request.Arguments, id: 51870 + index);
            var json = result.ToJsonString();

            Assert.True(result["identity_scoped"]!.GetValue<bool>());
            Assert.Equal("selected_symbol_id", result["identity_scope_reason"]!.GetValue<string>());
            Assert.Equal(selector, result["selected_symbol"]!["selector"]!.GetValue<string>());
            Assert.Contains(request.Expected, json, StringComparison.Ordinal);
            Assert.DoesNotContain(request.Forbidden, json, StringComparison.Ordinal);
        }

        var ambiguous = CallIssue4853Tool(
            _server,
            "callees",
            new JsonObject
            {
                ["query"] = "Issue5187Shared",
                ["exact"] = true,
            },
            id: 51875);
        Assert.False(ambiguous["identity_scoped"]!.GetValue<bool>());
        Assert.Equal(2, ambiguous["candidate_count"]!.GetValue<int>());
        Assert.Equal(2, ambiguous["candidates"]!.AsArray().Count);
        Assert.Contains("not identity-scoped", ambiguous["identity_warning"]!.GetValue<string>(), StringComparison.Ordinal);

        var pathScoped = CallIssue4853Tool(
            _server,
            "references",
            new JsonObject
            {
                ["query"] = "Issue5187Shared",
                ["exact"] = true,
                ["path"] = new JsonArray("tests/**"),
                ["countOnly"] = true,
            },
            id: 518751);
        Assert.Equal(2, pathScoped["candidate_count"]!.GetValue<int>());
        var pathScopedCandidates = pathScoped["candidates"]!.AsArray()
            .Select(candidate => candidate!["qualified_name"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("Issue5187Fixture.Issue5187Alpha.Issue5187Shared", pathScopedCandidates);
        Assert.Contains("Issue5187Fixture.Issue5187Beta.Issue5187Shared", pathScopedCandidates);

        foreach (var tool in new[] { "references", "callees" })
        {
            var languageMismatch = CallIssue4853Tool(
                _server,
                tool,
                new JsonObject
                {
                    ["selector"] = selector,
                    ["lang"] = "typescript",
                    ["offset"] = 1,
                },
                id: tool == "references" ? 518752 : 518753);
            Assert.Equal(0, languageMismatch["count"]!.GetValue<int>());
            Assert.Equal(0, languageMismatch["total"]!.GetValue<int>());
        }

        var overloadSelectors = reader.GetDefinitions(
                "Issue5187Overloaded",
                limit: 10,
                kind: null,
                lang: "csharp",
                includeBody: false,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false,
                exact: true)
            .Select(reader.BuildSymbolCandidateSelector)
            .Select(candidate => candidate.Selector)
            .ToArray();
        Assert.Equal(2, overloadSelectors.Length);
        foreach (var overloadSelector in overloadSelectors)
        {
            foreach (var tool in new[] { "references", "callers", "impact_analysis" })
            {
                var arguments = new JsonObject
                {
                    ["selector"] = overloadSelector,
                    ["countOnly"] = true,
                };
                if (tool == "impact_analysis")
                    arguments["maxHops"] = 1;
                var overloadSelected = CallIssue4853Tool(
                    _server,
                    tool,
                    arguments,
                    id: 518754);
                Assert.Equal(0, overloadSelected["count"]!.GetValue<int>());
                Assert.True(overloadSelected["identity_scoped"]!.GetValue<bool>());
            }
        }

        foreach (var tool in new[] { "references", "callers" })
        {
            var overloadBare = CallIssue4853Tool(
                _server,
                tool,
                new JsonObject
                {
                    ["query"] = "Issue5187Overloaded",
                    ["exact"] = true,
                    ["countOnly"] = true,
                },
                id: 518755);
            Assert.False(overloadBare["identity_scoped"]!.GetValue<bool>());
            Assert.Equal(2, overloadBare["candidate_count"]!.GetValue<int>());
        }

        var filteredImpact = CallIssue4853Tool(
            _server,
            "impact_analysis",
            new JsonObject
            {
                ["selector"] = selector,
                ["lang"] = "typescript",
                ["maxHops"] = 0,
            },
            id: 518756);
        Assert.Equal(0, filteredImpact["definition_count"]!.GetValue<int>());
        Assert.Empty(filteredImpact["definitions"]!.AsArray());

        var selectedType = Assert.Single(reader.GetDefinitions(
            "Issue5187SelectedType",
            limit: 10,
            kind: null,
            lang: "csharp",
            includeBody: false,
            pathPatterns: null,
            excludePathPatterns: null,
            excludeTests: false,
            exact: true));
        var selectedTypeSelector = reader.BuildSymbolCandidateSelector(selectedType).Selector;
        var selectedTypeImpact = CallIssue4853Tool(
            _server,
            "impact_analysis",
            new JsonObject
            {
                ["selector"] = selectedTypeSelector,
                ["maxHops"] = 1,
            },
            id: 518757);
        Assert.Equal("file_dependency_hints", selectedTypeImpact["impact_mode"]!.GetValue<string>());
        Assert.Contains("tests/AlphaConsumer.cs", selectedTypeImpact.ToJsonString(), StringComparison.Ordinal);

        var malformed = CallIssue4853ToolError(
            _server,
            "callers",
            new JsonObject
            {
                ["selector"] = "id:-1@g:0000000000000000",
                ["path"] = new JsonArray("does-not-match/**"),
            },
            id: 51876);
        Assert.Equal("invalid_argument", malformed["category"]!.GetValue<string>());
        Assert.Equal("selector_malformed", malformed["error_code"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsList_DescribesGenerationBoundSelectorForMatchingGraphTools_Issue5187()
    {
        var response = _server.HandleMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 51877,
            ["method"] = "tools/list",
            ["params"] = new JsonObject { ["format"] = "full" },
        })!;
        var tools = response["result"]!["tools"]!.AsArray();

        foreach (var toolName in new[] { "references", "callers", "callees", "impact_analysis" })
        {
            var tool = tools.Single(candidate => candidate!["name"]!.GetValue<string>() == toolName)!;
            var inputSchema = tool["inputSchema"]!;
            var selector = inputSchema["properties"]!["selector"]!;
            var requiredModes = inputSchema["oneOf"]!.AsArray()
                .Select(mode => mode!["required"]!.AsArray().Single()!.GetValue<string>())
                .ToArray();

            Assert.Equal("string", selector["type"]!.GetValue<string>());
            Assert.Contains("generation-bound", selector["description"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(["query", "selector"], requiredModes);
            Assert.Contains("identity_scoped=false", tool["description"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }
}
