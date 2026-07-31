using System.Text.Json.Nodes;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class McpServerTests
{
    [Fact]
    public void Languages_ExactFiltersReportSeparateCatalogAndMatchCounts_Issue4896()
    {
        var language = CallIssue4896Languages(new JsonObject
        {
            ["language"] = "cs",
        }, id: 1);
        var languageEntry = Assert.Single(language["languages"]!.AsArray())!;
        Assert.Equal("csharp", languageEntry["lang"]!.GetValue<string>());
        Assert.Equal(1, language["summary"]!["filtered_language_count"]!.GetValue<int>());
        Assert.True(language["summary"]!["catalog_language_count"]!.GetValue<int>() > 1);
        Assert.True(language["summary"]!["symbol_extraction_language_count"]!.GetValue<int>() > 1);

        var duplicateCapability = CallIssue4896Languages(new JsonObject
        {
            ["language"] = "csharp",
            ["capability"] = new JsonArray("graph", "graph"),
        }, id: 2);
        Assert.Equal(
            "csharp",
            Assert.Single(duplicateCapability["languages"]!.AsArray())!["lang"]!.GetValue<string>());
        Assert.Single(duplicateCapability["filters"]!["capability"]!.AsArray());

        var alias = CallIssue4896Languages(new JsonObject
        {
            ["alias"] = "F#",
        }, id: 3);
        Assert.Equal("fsharp", Assert.Single(alias["languages"]!.AsArray())!["lang"]!.GetValue<string>());
        Assert.Equal(1, alias["alias_lookup"]!["matched"]!.GetValue<int>());

        var ambiguousExtension = CallIssue4896Languages(new JsonObject
        {
            ["extension"] = ".M",
        }, id: 4);
        Assert.Equal(
            "ambiguous_m",
            Assert.Single(ambiguousExtension["languages"]!.AsArray())!["lang"]!.GetValue<string>());
        var ambiguousLookup = ambiguousExtension["extension_lookup"]!;
        Assert.Equal(1, ambiguousLookup["matched"]!.GetValue<int>());
        Assert.Equal(".m", ambiguousLookup["normalized_extension"]!.GetValue<string>());
        Assert.True(ambiguousLookup["ambiguous"]!.GetValue<bool>());
        Assert.Equal(
            ["objc", "matlab"],
            ambiguousLookup["candidates"]!.AsArray()
                .Select(candidate => candidate!["lang"]!.GetValue<string>()));
        Assert.Contains(
            "octave",
            ambiguousLookup["candidates"]![1]!["aliases"]!.AsArray()
                .Select(aliasValue => aliasValue!.GetValue<string>()));
        var shebangRule = ambiguousLookup["detection_rules"]!.AsArray()
            .Single(rule => rule!["source"]!.GetValue<string>() == "shebang")!;
        Assert.Equal(4, shebangRule["precedence"]!.GetValue<int>());
        Assert.Equal(FileIndexer.ShebangProbeByteLimit, shebangRule["probe_byte_limit"]!.GetValue<int>());
        Assert.Equal(
            "required_before_limit_unless_eof",
            shebangRule["line_termination_policy"]!.GetValue<string>());
        Assert.Equal("case_insensitive", shebangRule["interpreter_case_policy"]!.GetValue<string>());
        var shebangRules = shebangRule["interpreter_rules"]!.AsArray();
        Assert.Contains(
            shebangRules,
            rule => rule!["pattern"]!.GetValue<string>() == "ruby"
                    && rule["language"]!.GetValue<string>() == "ruby");
        var filenamePrefixRule = ambiguousLookup["detection_rules"]!.AsArray()
            .Single(rule => rule!["source"]!.GetValue<string>() == "filename_prefix_pattern")!;
        Assert.Equal(3, filenamePrefixRule["precedence"]!.GetValue<int>());
        Assert.Contains(
            filenamePrefixRule["patterns"]!.AsArray(),
            rule => rule!["pattern"]!.GetValue<string>() == "Makefile.<suffix>"
                    && rule["language"]!.GetValue<string>() == "makefile");
        Assert.Equal(
            LanguageMapOverrides.WorkspaceFileName,
            ambiguousLookup["override_guidance"]!["config_file"]!.GetValue<string>());

        var emptyUnicodeLookup = CallIssue4896Languages(new JsonObject
        {
            ["language"] = "日本語",
        }, id: 5);
        Assert.Equal(0, emptyUnicodeLookup["total_count"]!.GetValue<int>());
        Assert.Equal(0, emptyUnicodeLookup["returned_count"]!.GetValue<int>());
        Assert.False(emptyUnicodeLookup["has_more"]!.GetValue<bool>());
        Assert.Null(emptyUnicodeLookup["next_cursor"]);
        Assert.Equal("complete", emptyUnicodeLookup["continuation_reason"]!.GetValue<string>());
    }

    [Fact]
    public void Languages_AcceptsEveryCliCapabilityFilter_Issue4896()
    {
        foreach (var capability in LanguageCapabilityCatalog.SupportedCapabilities)
        {
            var response = CallIssue4896Languages(new JsonObject
            {
                ["capability"] = capability,
            }, id: 6);

            Assert.Equal(
                [capability],
                response["filters"]!["capability"]!.AsArray()
                    .Select(value => value!.GetValue<string>()));
        }
    }

    [Fact]
    public void Languages_CursorEnumeratesCatalogAndRejectsMismatchAndStaleGeneration_Issue4896()
    {
        var names = new List<string>();
        string? cursor = null;
        var totalCount = -1;
        JsonObject? finalPage = null;
        for (var pageNumber = 0; pageNumber < 100; pageNumber++)
        {
            var arguments = new JsonObject
            {
                ["limit"] = 7,
            };
            if (cursor is not null)
                arguments["cursor"] = cursor;

            var page = CallIssue4896Languages(arguments, id: 10 + pageNumber);
            finalPage = page;
            totalCount = totalCount < 0 ? page["total_count"]!.GetValue<int>() : totalCount;
            Assert.Equal(totalCount, page["total_count"]!.GetValue<int>());
            Assert.Equal(
                page["languages"]!.AsArray().Count,
                page["returned_count"]!.GetValue<int>());
            names.AddRange(page["languages"]!.AsArray()
                .Select(entry => entry!["lang"]!.GetValue<string>()));

            cursor = page["next_cursor"]?.GetValue<string>();
            if (!page["has_more"]!.GetValue<bool>())
                break;
            Assert.False(string.IsNullOrWhiteSpace(cursor));
        }

        Assert.NotNull(finalPage);
        Assert.Equal(totalCount, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);
        Assert.False(finalPage!["has_more"]!.GetValue<bool>());
        Assert.Null(finalPage["next_cursor"]);
        Assert.True(finalPage["returned_count"]!.GetValue<int>() > 0);
        Assert.Equal("complete", finalPage["continuation_reason"]!.GetValue<string>());

        var first = CallIssue4896Languages(new JsonObject
        {
            ["limit"] = 3,
        }, id: 200);
        var firstCursor = first["next_cursor"]!.GetValue<string>();
        var malformed = CallIssue4853ToolError(
            _server,
            "languages",
            new JsonObject
            {
                ["limit"] = 3,
                ["cursor"] = "not-a-cursor",
            },
            id: 201);
        Assert.Equal("cursor_malformed", malformed["error_code"]!.GetValue<string>());

        var mismatch = CallIssue4853ToolError(
            _server,
            "languages",
            new JsonObject
            {
                ["language"] = "csharp",
                ["limit"] = 3,
                ["cursor"] = firstCursor,
            },
            id: 202);
        Assert.Equal("invalid_argument", mismatch["category"]!.GetValue<string>());
        Assert.Equal("cursor_query_mismatch", mismatch["error_code"]!.GetValue<string>());

        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var staleFirst = CallIssue4896Languages(new JsonObject
                {
                    ["limit"] = 1,
                }, id: 203);
                var staleCursor = staleFirst["next_cursor"]!.GetValue<string>();
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(
                    _projectRoot,
                    new McpWorkspaceCatalogSymbolExtractor());

                var stale = CallIssue4853ToolError(
                    _server,
                    "languages",
                    new JsonObject
                    {
                        ["limit"] = 1,
                        ["cursor"] = staleCursor,
                    },
                    id: 204);
                Assert.Equal("index_stale", stale["category"]!.GetValue<string>());
                Assert.Equal("cursor_stale", stale["error_code"]!.GetValue<string>());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void Languages_MaxBytesHonorsExactUtf8EnvelopeBoundary_Issue4896()
    {
        var exactBudget = McpServer.MaxLanguageCatalogMaxBytes;
        JsonObject? exactResponse = null;
        var exactBytes = -1;
        for (var iteration = 0; iteration < 6; iteration++)
        {
            exactResponse = CallIssue4896LanguagesResponse(new JsonObject
            {
                ["limit"] = 20,
                ["maxBytes"] = exactBudget,
            }, id: 300);
            Assert.True(_server.TrySerializeJsonNodeWithinByteLimitForTests(
                exactResponse,
                int.MaxValue,
                out _,
                out exactBytes));
            Assert.True(exactBytes <= exactBudget);
            if (exactBytes == exactBudget)
                break;
            exactBudget = exactBytes;
        }

        Assert.NotNull(exactResponse);
        Assert.Equal(exactBudget, exactBytes);
        var exactStructured = exactResponse!["result"]!["structuredContent"]!;
        var exactReturnedCount = exactStructured["returned_count"]!.GetValue<int>();
        Assert.True(exactReturnedCount > 1);
        Assert.False(exactStructured["response_budget"]!["byte_budget_reached"]!.GetValue<bool>());

        var oneByteLessResponse = CallIssue4896LanguagesResponse(new JsonObject
        {
            ["limit"] = 20,
            ["maxBytes"] = exactBudget - 1,
        }, id: 300);
        Assert.True(_server.TrySerializeJsonNodeWithinByteLimitForTests(
            oneByteLessResponse,
            exactBudget - 1,
            out _,
            out var oneByteLessBytes));
        Assert.True(oneByteLessBytes <= exactBudget - 1);
        Assert.True(
            oneByteLessResponse["result"]!["structuredContent"]!["returned_count"]!.GetValue<int>()
            < exactReturnedCount);
    }

    private JsonObject CallIssue4896Languages(JsonObject arguments, int id)
    {
        var response = CallIssue4896LanguagesResponse(arguments, id);
        Assert.Null(response["error"]);
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
        return response["result"]!["structuredContent"]!.AsObject();
    }

    private JsonObject CallIssue4896LanguagesResponse(JsonObject arguments, int id)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "languages",
                ["arguments"] = arguments,
            },
        };
        return _server.HandleMessage(request)!.AsObject();
    }
}
