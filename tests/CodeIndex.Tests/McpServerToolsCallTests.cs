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
    [Theory]
    [InlineData("callers", "{\"query\":\"Run\",\"countOnly\":true}")]
    [InlineData("callees", "{\"query\":\"Run\"}")]
    [InlineData("deps", "{}")]
    [InlineData("impact_analysis", "{\"query\":\"Run\"}")]
    public void ToolsCall_ReferenceGraphCommandsExposeCapHitIncompleteness_Issue4620(
        string tool,
        string argumentsJson)
    {
        var capKind = ReferenceExtractor.ReferenceSafetyCapDiagnosticKinds[0];
        using (var command = _db.Connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO file_issues (file_id, kind, line, message)
                SELECT id, @kind, 1, 'reference extraction safety cap reached'
                FROM files
                WHERE path = 'src/app.cs';
                """;
            command.Parameters.AddWithValue("@kind", capKind);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 4620,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = JsonNode.Parse(argumentsJson),
            },
        };

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.False(structured["reference_graph_complete"]!.GetValue<bool>());
        Assert.True(structured["degraded"]!.GetValue<bool>());
        Assert.Equal(50_000, structured["reference_extraction_limits"]!["max_lookup_symbols"]!.GetValue<int>());
        var capHits = structured["reference_extraction_cap_hits"]!;
        Assert.True(capHits["state_available"]!.GetValue<bool>());
        Assert.Equal(1, capHits["hit_count"]!.GetValue<long>());
        Assert.Equal(1, capHits["affected_file_count"]!.GetValue<long>());
        Assert.Contains(capKind, capHits["reasons"]!.AsArray().Select(reason => reason!.GetValue<string>()));
        Assert.Contains(capKind, structured["reference_graph_incomplete_reasons"]!.AsArray().Select(reason => reason!.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_LanguagesPublishesReferenceExtractionLimits_Issue4620()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":4620,"method":"tools/call","params":{"name":"languages","arguments":{}}}""")!;

        var response = _server.HandleMessage(request)!;
        var limits = response["result"]!["structuredContent"]!["reference_extraction_limits"]!;

        Assert.Equal(50_000, limits["max_lookup_symbols"]!.GetValue<int>());
        Assert.Equal(20_000, limits["max_lookup_lines"]!.GetValue<int>());
        Assert.Equal(512, limits["max_names_per_line"]!.GetValue<int>());
        Assert.Equal(20_000, limits["max_container_candidates"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_IndexPersistsAndReturnsReferenceCapHitSummary_Issue4620()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_reference_cap_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_reference_cap_{Guid.NewGuid():N}.db");
        var previousLimits = ReferenceExtractor.SafetyLimitsForTesting;
        var previousContentLoadHook = McpServer.McpIndexFileContentLoadForTesting;
        try
        {
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllText(
                Path.Combine(fixtureDir, "app.py"),
                "def first():\n    pass\n\ndef second():\n    pass\n");
            var testLimits = new ReferenceExtractionSafetyLimits
            {
                MaxLookupSymbols = 1,
                MaxLookupLines = 100,
                MaxNamesPerLine = 100,
                MaxContainerCandidates = 100,
            };
            ReferenceExtractor.SafetyLimitsForTesting = testLimits;
            McpServer.McpIndexFileContentLoadForTesting = _ =>
                ReferenceExtractor.SafetyLimitsForTesting = testLimits;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var response = CallIndex(server, fixtureDir);
            var structured = response["result"]!["structuredContent"]!;

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.False(structured["reference_graph_complete"]!.GetValue<bool>());
            Assert.Equal(1, structured["reference_extraction_limits"]!["max_lookup_symbols"]!.GetValue<int>());
            Assert.True(structured["reference_extraction_cap_hits"]!["hit_count"]!.GetValue<long>() > 0);

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var reader = new DbReader(verifyDb.Connection);
            var lastRunCapHits = reader.GetStatus().LastIndexRun!.ReferenceExtractionCapHits!;
            Assert.True(lastRunCapHits.HitCount > 0);
            Assert.Equal("app.py", Assert.Single(lastRunCapHits.Files).File);
        }
        finally
        {
            McpServer.McpIndexFileContentLoadForTesting = previousContentLoadHook;
            ReferenceExtractor.SafetyLimitsForTesting = previousLimits;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Search_ImmutableUriReportsStaleSnapshotRisk_Issue4555()
    {
        using var server = new McpServer(
            DbConnectionFactory.ToReadOnlyUri(_dbPath) + "&cache=shared",
            ConsoleUi.LoadVersion(),
            dbPathExplicit: true);
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"__no_match_issue4555__","limit":1}}}""")!;

        var response = server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["wal_stale_snapshot_risk"]!.GetValue<bool>());
        Assert.Equal("explicit_immutable_read_only", structured["wal_stale_snapshot_reason"]!.GetValue<string>());

        var batchRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"batch_query","arguments":{"maxResponseBytes":900,"queries":[{"tool":"search","arguments":{"query":"App","limit":1}}]}}}""")!;
        var batchResponse = server.HandleMessage(batchRequest)!;
        var batchStructured = batchResponse["result"]!["structuredContent"]!;
        Assert.True(batchStructured["wal_stale_snapshot_risk"]!.GetValue<bool>());

        var errorRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search","arguments":{}}}""")!;
        var errorResponse = server.HandleMessage(errorRequest)!;
        var errorStructured = errorResponse["result"]!["structuredContent"]!;
        Assert.True(errorResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.True(errorStructured["wal_stale_snapshot_risk"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("search", "{\"query\":\"App\"}")]
    [InlineData("definition", "{\"query\":\"Run\"}")]
    [InlineData("unused_symbols", "{}")]
    public void ToolsCall_StructuredContentRootIncludesApiVersion_Issue4436(string tool, string argumentsJson)
    {
        var request = JsonNode.Parse(
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argumentsJson}}}}}")!;

        var response = _server.HandleMessage(request)!;

        Assert.Equal(JsonOutputContract.ApiVersion, response["result"]!["structuredContent"]!["api_version"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_SearchFormatCompactEmitsMatchLineAndNoTerminalCursor_Issues1642And4402()
    {
        InsertIndexedFile(
            "src/compact-line.cs",
            "csharp",
            "class CompactLine\n{\n    void Needle4402() { }\n}\n");
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Needle4402","format":"compact","limit":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var row = Assert.Single(structured["results"]!.AsArray());

        Assert.Equal("compact", structured["format"]!.GetValue<string>());
        Assert.Equal("src/compact-line.cs", row!["file"]!.GetValue<string>());
        Assert.Equal(3, row["line"]!.GetValue<int>());
        Assert.Null(row["snippet"]);
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["total"]!.GetValue<int>());
        Assert.False(structured["truncated"]!.GetValue<bool>());
        Assert.False(structured["more_available"]!.GetValue<bool>());
        Assert.Null(structured["next_cursor"]);
    }

    [Fact]
    public void ToolsCall_SearchFormatCountAliasesCountOnly_Issue1642()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","format":"count"}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["count_only"]!.GetValue<bool>());
        Assert.True(structured["count"]!.GetValue<int>() > 0);
        Assert.NotNull(structured["result_stable_at"]);
        Assert.Empty(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_SearchFilesAndMapExposeCliQueryOptions_Issue3542()
    {
        InsertIndexedFile("src/large.cs", "csharp", "public class Large { " + new string('x', 512) + " }\n");

        var searchRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","snippetFocus":"leftmost"}}}""")!;
        var searchResponse = _server.HandleMessage(searchRequest)!;
        var searchStructured = searchResponse["result"]!["structuredContent"]!;
        Assert.Equal("leftmost", searchStructured["snippetFocus"]!.GetValue<string>());

        var filesRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"files","arguments":{"orderBySize":true,"rawBytes":true,"limit":1}}}""")!;
        var filesResponse = _server.HandleMessage(filesRequest)!;
        var filesStructured = filesResponse["result"]!["structuredContent"]!;
        Assert.True(filesStructured["orderBySize"]!.GetValue<bool>());
        Assert.True(filesStructured["rawBytes"]!.GetValue<bool>());
        Assert.False(filesStructured["raw_bytes_payload_supported"]!.GetValue<bool>());
        Assert.Equal("src/large.cs", filesStructured["results"]![0]!["path"]!.GetValue<string>());

        var mapRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"map","arguments":{"minEntrypointConfidence":0.5,"sections":["hotspots"]}}}""")!;
        var mapResponse = _server.HandleMessage(mapRequest)!;
        var mapStructured = mapResponse["result"]!["structuredContent"]!;
        Assert.Equal(0.5, mapStructured["minEntrypointConfidence"]!.GetValue<double>());
    }

    [Fact]
    public void ToolsCall_SymbolGraphAndAnalyzeExposeCliQueryOptions_Issue3542()
    {
        InsertIndexedFile("src/visible.cs", "csharp", "public class Visible { public void RunVisible() { } private void Hidden() { } }\n");

        var symbolsRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"RunVisible","visibility":["public"],"excludeVisibility":"private","format":"compact"}}}""")!;
        var symbolsResponse = _server.HandleMessage(symbolsRequest)!;
        var symbolsStructured = symbolsResponse["result"]!["structuredContent"]!;
        Assert.Equal("compact", symbolsStructured["format"]!.GetValue<string>());
        Assert.Equal("public", Assert.Single(symbolsStructured["visibility"]!.AsArray())!.GetValue<string>());
        Assert.Equal("private", Assert.Single(symbolsStructured["excludeVisibility"]!.AsArray())!.GetValue<string>());

        var callersRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callers","arguments":{"query":"RunVisible","rawKinds":true,"format":"count"}}}""")!;
        var callersResponse = _server.HandleMessage(callersRequest)!;
        var callersStructured = callersResponse["result"]!["structuredContent"]!;
        Assert.True(callersStructured["rawKinds"]!.GetValue<bool>());
        Assert.True(callersStructured["count_only"]!.GetValue<bool>());

        var analyzeRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"RunVisible","format":"compact"}}}""")!;
        var analyzeResponse = _server.HandleMessage(analyzeRequest)!;
        var analyzeStructured = analyzeResponse["result"]!["structuredContent"]!;
        Assert.Equal("compact", analyzeStructured["format"]!.GetValue<string>());
        Assert.True(analyzeStructured["definition_count"]!.GetValue<int>() >= 1);
        Assert.NotNull(analyzeStructured["definitions"]);
        var compactCandidateDefinition = analyzeStructured["candidate_bundles"]![0]!["definition"]!;
        Assert.Null(compactCandidateDefinition["content"]);
        Assert.Null(compactCandidateDefinition["body_content"]);

        var analyzeCountRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"RunVisible","format":"count"}}}""")!;
        var analyzeCountResponse = _server.HandleMessage(analyzeCountRequest)!;
        var analyzeCountStructured = analyzeCountResponse["result"]!["structuredContent"]!;
        var countCandidateDefinition = analyzeCountStructured["candidate_bundles"]![0]!["definition"]!;
        Assert.True(analyzeCountStructured["count_only"]!.GetValue<bool>());
        Assert.Null(countCandidateDefinition["content"]);
        Assert.Null(countCandidateDefinition["body_content"]);
    }

    [Fact]
    public void ToolsCall_UnusedAndHotspotsExposeVisibilityAndBucketOptions_Issue3542()
    {
        var hotspotsRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"visibility":"public","excludeVisibility":["private"]}}}""")!;
        var hotspotsResponse = _server.HandleMessage(hotspotsRequest)!;
        var hotspotsStructured = hotspotsResponse["result"]!["structuredContent"]!;
        Assert.Equal("public", Assert.Single(hotspotsStructured["visibility"]!.AsArray())!.GetValue<string>());
        Assert.Equal("private", Assert.Single(hotspotsStructured["excludeVisibility"]!.AsArray())!.GetValue<string>());

        var unusedRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"visibility":"public","byBucket":true}}}""")!;
        var unusedResponse = _server.HandleMessage(unusedRequest)!;
        var unusedStructured = unusedResponse["result"]!["structuredContent"]!;
        Assert.True(unusedStructured["byBucket"]!.GetValue<bool>());
        Assert.NotNull(unusedStructured["symbols_by_bucket"]);
        Assert.Equal("public", Assert.Single(unusedStructured["visibility"]!.AsArray())!.GetValue<string>());
    }

    [Theory]
    [InlineData("search", "format")]
    [InlineData("symbol_hotspots", "groupBy")]
    public void ToolsCall_EnumLikeScalarTooLong_RejectsBeforeNormalization_Issue3116(string toolName, string argumentName)
    {
        var oversized = new string('A', McpBoundedText.MaxScalarArgumentChars + 1);
        var args = new JsonObject
        {
            [argumentName] = oversized,
        };
        if (toolName == "search")
            args["query"] = "Run";
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = args,
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        var structured = result["structuredContent"]!;
        Assert.Contains($"Argument '{argumentName}' on tool '{toolName}' is too long", text);
        Assert.DoesNotContain(oversized, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(toolName, structured["tool"]!.GetValue<string>());
        Assert.Equal(argumentName, structured["parameter"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxScalarArgumentChars, structured["max_length"]!.GetValue<int>());
        Assert.Equal(oversized.Length, structured["actual_length"]!.GetValue<int>());
        Assert.True(structured["value_truncated"]!.GetValue<bool>());
        Assert.Equal(oversized.Length, structured["value_length"]!.GetValue<int>());
        Assert.Equal(McpBoundedText.ForDisplay(oversized).Text, structured["value"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_SearchReturnsStableAtAndCursorContinuesAfterAnchor_Issue1462()
    {
        InsertIndexedFile("src/other.cs", "csharp", "public class Other { public void Run() { } }");
        var firstRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","limit":1}}}""")!;

        var firstResponse = _server.HandleMessage(firstRequest)!;
        var first = firstResponse["result"]!["structuredContent"]!;
        var cursor = first["next_cursor"]!.GetValue<string>();
        var stableAt = first["result_stable_at"]!.GetValue<DateTime>();

        Assert.Single(first["results"]!.AsArray());
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var secondRequest = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["limit"] = 1,
                    ["cursor"] = cursor,
                },
            },
        };

        var secondResponse = _server.HandleMessage(secondRequest)!;
        var second = secondResponse["result"]!["structuredContent"]!;

        Assert.Single(second["results"]!.AsArray());
        Assert.Equal(stableAt, second["result_stable_at"]!.GetValue<DateTime>());
        Assert.NotEqual(
            first["results"]!.AsArray()[0]!["path"]!.GetValue<string>(),
            second["results"]!.AsArray()[0]!["path"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("NaN:1:0")]
    [InlineData("Infinity:1:0")]
    [InlineData("1:-1:0")]
    [InlineData("1:1:-1")]
    public void ToolsCall_Search_InvalidCursorDomain_ReturnsInvalidCursorError_Issue3193(string cursor)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["cursor"] = cursor,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("'cursor' must be a search pagination cursor", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("invalid_argument", result["structuredContent"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_TruncatedResponseIncludesNextOffsetAndPages()
    {
        InsertIndexedFile(
            "src/paged-callers.cs",
            "csharp",
            """
            class PagedCallers {
                void Alpha() { Target(); }
                void Beta() { Target(); }
                void Gamma() { Target(); }
                void Target() { }
            }
            """);

        var firstRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Target","lang":"csharp","exactName":true,"path":"src/paged-callers.cs","limit":2}}}""")!;
        var firstResponse = _server.HandleMessage(firstRequest)!;
        var first = firstResponse["result"]!["structuredContent"]!;

        Assert.Equal(2, first["count"]!.GetValue<int>());
        Assert.True(first["truncated"]!.GetValue<bool>());
        Assert.True(first["more_available"]!.GetValue<bool>());
        Assert.Equal(2, first["next_offset"]!.GetValue<int>());
        var firstNames = first["results"]!.AsArray()
            .Select(row => row!["callerName"]!.GetValue<string>())
            .ToArray();

        var secondRequest = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Target","lang":"csharp","exactName":true,"path":"src/paged-callers.cs","limit":2,"offset":2}}}""")!;
        var secondResponse = _server.HandleMessage(secondRequest)!;
        var second = secondResponse["result"]!["structuredContent"]!;

        Assert.Equal(2, second["offset"]!.GetValue<int>());
        Assert.False(second["truncated"]!.GetValue<bool>());
        Assert.False(second["more_available"]!.GetValue<bool>());
        Assert.Null(second["next_offset"]);
        var secondNames = second["results"]!.AsArray()
            .Select(row => row!["callerName"]!.GetValue<string>())
            .ToArray();

        var allNames = firstNames.Concat(secondNames).ToArray();
        Assert.Equal(allNames.Distinct().Count(), allNames.Length);
        Assert.Contains("Alpha", allNames);
        Assert.Contains("Beta", allNames);
        Assert.Contains("Gamma", allNames);
    }

    [Theory]
    [InlineData("references", "Target")]
    [InlineData("callees", "Source")]
    public void ToolsCall_GraphTools_TruncatedResponseIncludesEnvelope_Issue1415(string tool, string query)
    {
        InsertIndexedFile(
            "src/paged-graph.cs",
            "csharp",
            """
            class PagedGraph {
                void Source() { Alpha(); Beta(); Gamma(); }
                void Alpha() { Target(); }
                void Beta() { Target(); }
                void Gamma() { Target(); }
                void Target() { }
            }
            """);

        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":{"query":"Target","lang":"csharp","exactName":true,"path":"src/paged-graph.cs","limit":2}}}"""
                .Replace("TOOL", tool, StringComparison.Ordinal)
                .Replace("Target", query, StringComparison.Ordinal))!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(2, structured["count"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.True(structured["more_available"]!.GetValue<bool>());
        Assert.Equal(2, structured["next_offset"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Search_WithResultsIncludesNextStepSuggestion()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"public class App","limit":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var suggestion = structured["next_step_suggestion"]!;

        Assert.Equal("excerpt", suggestion["tool"]!.GetValue<string>());
        Assert.Equal("src/app.cs", suggestion["args"]!["path"]!.GetValue<string>());
        Assert.True(suggestion["args"]!["startLine"]!.GetValue<int>() >= 1);
        Assert.True(suggestion["args"]!["endLine"]!.GetValue<int>() >= suggestion["args"]!["startLine"]!.GetValue<int>());
        Assert.Contains("excerpt", suggestion["suggested_action"]!.GetValue<string>());
        Assert.Contains("definition", suggestion["suggested_action"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_WithResultsIncludesNextStepSuggestion()
    {
        InsertIndexedFile(
            "src/reference-hint.cs",
            "csharp",
            """
            class ReferenceHint {
                void Caller() { Target(); }
                void Target() { }
            }
            """);
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Target","lang":"csharp","exactName":true,"limit":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var suggestion = structured["next_step_suggestion"]!;

        Assert.Equal("excerpt", suggestion["tool"]!.GetValue<string>());
        Assert.Equal("src/reference-hint.cs", suggestion["args"]!["path"]!.GetValue<string>());
        Assert.Contains("callers", suggestion["suggested_action"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_WithResultIncludesNextStepSuggestion()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"src/app.cs","startLine":1,"endLine":1}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var suggestion = structured["next_step_suggestion"]!;

        Assert.Equal("outline", suggestion["tool"]!.GetValue<string>());
        Assert.Equal("src/app.cs", suggestion["args"]!["path"]!.GetValue<string>());
        Assert.Contains("outline", suggestion["suggested_action"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_EmptyResultIncludesRecoveryHint()
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"MissingSymbol","lang":"csharp","exactName":true}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var hint = structured["recovery_hint"]!;

        Assert.Equal("no_results", hint["reason"]!.GetValue<string>());
        Assert.Equal("symbols", hint["tool"]!.GetValue<string>());
        Assert.Equal("MissingSymbol", hint["args"]!["query"]!.GetValue<string>());
        Assert.Contains("relax exactName/path/lang/kind filters", hint["suggested_action"]!.GetValue<string>());
        Assert.Contains("search", hint["suggested_action"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ProjectFilterResolverFailure_ReturnsInvalidArgument_Issue3160()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","project":"DefinitelyMissingProject3160"}}}""")!;

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>(), response.ToJsonString());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Project filter could not be resolved: InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool 'search' failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DefinitelyMissingProject3160", text, StringComparison.Ordinal);
        var structured = result["structuredContent"]!;
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, structured["category"]!.GetValue<string>());
        Assert.Equal("project", structured["parameter"]!.GetValue<string>());
        Assert.Equal("InvalidOperationException", structured["diagnostic"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ReusesDbContextAcrossInvocations()
    {
        // #1494: every MCP tool call used to construct a fresh DbContext (and reopen the
        // SQLite connection, reapply pragmas, re-register every SQL function). The session
        // should now cache a single DbContext after the first tool call and reuse it.
        // #1494: 旧実装はツール呼び出しごとに DbContext を作り直していたため、SQLite 接続再開・
        // PRAGMA 再適用・SQL 関数再登録のコストを毎回払っていた。セッション内では一度だけ開いた
        // DbContext を再利用するようになっていることを検証する。
        Assert.Null(GetSharedDbContextField(_server));

        var first = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!);
        Assert.False(first!["result"]?["isError"]?.GetValue<bool>() ?? false);
        var afterFirst = GetSharedDbContextField(_server);
        Assert.NotNull(afterFirst);

        var second = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!);
        Assert.False(second!["result"]?["isError"]?.GetValue<bool>() ?? false);
        Assert.Same(afterFirst, GetSharedDbContextField(_server));
    }

    [Fact]
    public void ToolsCall_DbMissingThenCreated_ReopensSharedContext()
    {
        // The cached DbContext must drop itself when the file is missing so a follow-up call
        // — after the user runs `cdidx index` from another shell — can succeed instead of
        // failing against a stale handle.
        // DB ファイルが消えた場合はキャッシュをクリアし、外部で再作成された後の呼び出しで
        // 古いハンドルに失敗せず再オープンできることを確認する。
        var missingPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_reopen");
        using var server = new McpServer(missingPath, ConsoleUi.LoadVersion());
        try
        {
            var miss = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;
            Assert.True(miss["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.Null(GetSharedDbContextField(server));

            using (var seed = new DbContext(DbOpenIntent.WriteIndex, missingPath))
            {
                seed.InitializeSchema();
            }

            var hit = server.HandleMessage(JsonNode.Parse(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;
            Assert.False(hit["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.NotNull(GetSharedDbContextField(server));
        }
        finally
        {
            server.Dispose();
            TestProjectHelper.DeleteSqliteDatabaseFiles(missingPath);
        }
    }

    [Fact]
    public void ToolsCall_UnknownArgument_ReturnsInvalidParams()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"limt":1,"query":"abc"}}}""")!;

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var structured = result["structuredContent"]!;
        Assert.Equal("invalid_argument", structured["category"]!.GetValue<string>());
        Assert.Equal("limt", structured["unknown_argument"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_IndexAllowsAdvertisedMaxFileBytesArgument()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"/","maxFileBytes":1024}}}""")!;

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.DoesNotContain("Unknown argument 'maxFileBytes'", text, StringComparison.Ordinal);
        Assert.Contains("Path must be within the current working directory", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", """{"query":"App","limit":0}""", "limit", 1, 0)]
    [InlineData("definition", """{"query":"App","limit":-1}""", "limit", 1, -1)]
    [InlineData("references", """{"query":"App","offset":-1}""", "offset", 0, -1)]
    public void ToolsCall_InvalidLimitOrOffsetBounds_ReturnsInvalidParams_Issue3195(
        string toolName,
        string argumentsJson,
        string parameter,
        int minimum,
        int actual)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = JsonNode.Parse(argumentsJson),
            },
        };

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains($"Argument '{parameter}'", error["message"]!.GetValue<string>());
        var data = error["data"]!;
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, data["category"]!.GetValue<string>());
        Assert.Equal(parameter, data["parameter"]!.GetValue<string>());
        Assert.Equal(minimum, data["minimum"]!.GetValue<int>());
        Assert.Equal(actual, data["actual"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_DisabledTool_ReturnsMethodNotFoundError()
    {
        var deny = McpToolFilter.Parse(null, "index");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"/tmp/whatever"}}}""")!;
        var response = server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("Tool not enabled", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_ReturnsResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 search result", text);
        // Content summary includes file path for AI orientation / サマリにファイルパスを含む
        Assert.Contains("src/app.cs", text);

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/app.cs", structured["results"]![0]!["path"]!.GetValue<string>());
        Assert.NotNull(structured["results"]![0]!["snippet"]);
        Assert.NotNull(structured["results"]![0]!["matchLines"]);
        Assert.NotNull(structured["results"]![0]!["highlights"]);
        Assert.Null(structured["results"]![0]!["content"]);
    }

    [Fact]
    public void ToolsCall_Search_ListRecipesReturnsBuiltIns_Issue3545()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_SEARCH_RECIPE_PATHS");
        env.Set("CDIDX_SEARCH_RECIPE_PATHS", null);
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"listRecipes":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["count"]!.GetValue<int>() >= 1);
        var recipes = structured["recipes"]!.AsArray();
        var risky = recipes.Single(recipe => recipe!["name"]!.GetValue<string>() == "risky-code")!;
        Assert.Equal("source", risky["default_scope"]!.GetValue<string>());
        Assert.Contains(risky["default_path_patterns"]!.AsArray(), path => path!.GetValue<string>() == "src/**");
        Assert.Contains(risky["default_exclude_paths"]!.AsArray(), path => path!.GetValue<string>() == "tests/**");
        Assert.Contains(risky["queries"]!.AsArray(), query => query!["name"]!.GetValue<string>() == "unbounded-json-parse");
    }

    [Fact]
    public void ToolsCall_Search_RunRecipeReturnsGroupedResults_Issue3545()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_SEARCH_RECIPE_PATHS");
        env.Set("CDIDX_SEARCH_RECIPE_PATHS", null);
        InsertIndexedFile("src/json.cs", "csharp", "var doc = JsonDocument.Parse(payload);\n");
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"recipe":"risky-code","limit":5}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("risky-code", structured["recipe"]!["name"]!.GetValue<string>());
        Assert.True(structured["result_count"]!.GetValue<int>() >= 1);
        var jsonParseQuery = structured["queries"]!.AsArray()
            .Single(query => query!["name"]!.GetValue<string>() == "unbounded-json-parse")!;
        Assert.Equal(1, jsonParseQuery["count"]!.GetValue<int>());
        Assert.Equal("src/json.cs", jsonParseQuery["results"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_RunRecipeAppliesDefaultSourceScopeAndAuditScopeAll_Issue3714()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_SEARCH_RECIPE_PATHS");
        env.Set("CDIDX_SEARCH_RECIPE_PATHS", null);
        InsertIndexedFile("src/json.cs", "csharp", "var doc = JsonDocument.Parse(payload);\n");
        InsertIndexedFile("src/json-extra.cs", "csharp", "var doc = JsonDocument.Parse(otherPayload);\n");
        InsertIndexedFile("docs/json.md", "markdown", "Document JsonDocument.Parse usage.\n");
        InsertIndexedFile("tests/JsonTests.cs", "csharp", "var doc = JsonDocument.Parse(payload);\n");

        var sourceRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"recipe":"json-parse-apis","limit":1}}}""")!;
        var sourceResponse = _server.HandleMessage(sourceRequest)!;

        Assert.Null(sourceResponse["error"]);
        var sourceStructured = sourceResponse["result"]!["structuredContent"]!;
        var sourceQuery = sourceStructured["queries"]!.AsArray()
            .Single(query => query!["name"]!.GetValue<string>() == "json-document-parse")!;
        var sourcePaths = sourceQuery["results"]!.AsArray()
            .Select(result => result!["path"]!.GetValue<string>())
            .ToList();

        Assert.Equal("source", sourceStructured["audit_scope"]!.GetValue<string>());
        Assert.Equal("src/**", sourceStructured["path"]!.GetValue<string>());
        Assert.Contains(sourceStructured["excludePaths"]!.AsArray(), path => path!.GetValue<string>() == "tests/**");
        Assert.True(sourceStructured["excludeTests"]!.GetValue<bool>());
        var sourcePath = Assert.Single(sourcePaths);
        Assert.StartsWith("src/json", sourcePath, StringComparison.Ordinal);
        Assert.Equal(1, sourceQuery["count"]!.GetValue<int>());
        Assert.True(sourceQuery["truncated"]!.GetValue<bool>());
        Assert.Equal(sourcePath, sourceQuery["top_files"]![0]!["path"]!.GetValue<string>());

        var allRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search","arguments":{"recipe":"json-parse-apis","auditScope":"all","limit":10}}}""")!;
        var allResponse = _server.HandleMessage(allRequest)!;

        Assert.Null(allResponse["error"]);
        var allStructured = allResponse["result"]!["structuredContent"]!;
        var allQuery = allStructured["queries"]!.AsArray()
            .Single(query => query!["name"]!.GetValue<string>() == "json-document-parse")!;
        var allPaths = allQuery["results"]!.AsArray()
            .Select(result => result!["path"]!.GetValue<string>())
            .ToList();

        Assert.Equal("all", allStructured["audit_scope"]!.GetValue<string>());
        Assert.Null(allStructured["path"]);
        Assert.False(allStructured["excludeTests"]!.GetValue<bool>());
        Assert.Contains("src/json.cs", allPaths);
        Assert.DoesNotContain("docs/json.md", allPaths);
        Assert.Contains("tests/JsonTests.cs", allPaths);
    }

    [Fact]
    public void ToolsCall_Search_ListRecipesIncludesConfiguredSources_Issue3545()
    {
        var recipePath = Path.Combine(_projectRoot, "search-recipes.json");
        File.WriteAllText(recipePath, """
            {
              "recipes": [
                {
                  "name": "local-audit",
                  "description": "Local audit recipe",
                  "queries": [
                    {
                      "name": "todo-comments",
                      "query": "TODO",
                      "description": "Find local TODO markers",
                      "recommendedLabels": ["audit"],
                      "falsePositiveGuidance": "Ignore deliberate test fixtures.",
                      "exactSubstring": true
                    }
                  ]
                }
              ]
            }
            """);
        using var env = EnvironmentVariableScope.Capture("CDIDX_SEARCH_RECIPE_PATHS");
        env.Set("CDIDX_SEARCH_RECIPE_PATHS", recipePath);
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"listRecipes":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        var recipes = structured["recipes"]!.AsArray();
        var local = recipes.Single(recipe => recipe!["name"]!.GetValue<string>() == "local-audit")!;
        Assert.Equal("todo-comments", local["queries"]![0]!["name"]!.GetValue<string>());
        Assert.Null(structured["recipe_source_diagnostics"]);
    }

    [Fact]
    public void ToolsCall_Search_ListRecipesBoundsConfiguredSourceDiagnostics_Issue3545()
    {
        var recipePaths = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var recipePath = Path.Combine(_projectRoot, $"invalid-search-recipes-{i}.json");
            File.WriteAllText(recipePath, "[" + string.Join(",", Enumerable.Repeat("42", 40)) + "]");
            recipePaths.Add(recipePath);
        }

        using var env = EnvironmentVariableScope.Capture("CDIDX_SEARCH_RECIPE_PATHS");
        env.Set("CDIDX_SEARCH_RECIPE_PATHS", string.Join(Path.PathSeparator, recipePaths));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"listRecipes":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        var diagnostics = structured["recipe_source_diagnostics"]!.AsArray();
        Assert.True(diagnostics.Count <= 65);
        Assert.Contains(diagnostics, diagnostic => diagnostic!.GetValue<string>().Contains("truncated after 64 entries", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolsCall_SearchRecipe_AppliesResultOutputMetadata_Issue3558()
    {
        InsertIndexedFile(
            "src/todo.cs",
            "csharp",
            """
            // TODO: inspect generated SQL.
            public class TodoFixture { }
            """);
        var recipePath = Path.Combine(_projectRoot, "search-recipes-metadata.json");
        File.WriteAllText(recipePath, """
            {
              "recipes": [
                {
                  "name": "local-audit",
                  "description": "Local audit recipe",
                  "queries": [
                    {
                      "name": "todo-comments",
                      "query": "TODO",
                      "description": "Find local TODO markers",
                      "recommendedLabels": ["audit"],
                      "falsePositiveGuidance": "Ignore deliberate test fixtures.",
                      "exactSubstring": true
                    }
                  ]
                }
              ]
            }
            """);
        using var env = EnvironmentVariableScope.Capture("CDIDX_SEARCH_RECIPE_PATHS");
        env.Set("CDIDX_SEARCH_RECIPE_PATHS", recipePath);
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"recipe":"local-audit","snippetLines":3,"maxLineWidth":96}}}""")!;

        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        var query = structured["queries"]![0]!;
        var result = query["results"]![0]!;
        Assert.Equal(3, result["snippetLines"]!.GetValue<int>());
        Assert.Equal(96, result["maxLineWidth"]!.GetValue<int>());
        Assert.True(result["exact"]!.GetValue<bool>());
        Assert.False(result["rawFts"]!.GetValue<bool>());
        Assert.True(result["literalHighlightsAvailable"]!.GetValue<bool>());
        Assert.Null(result["literalHighlightWarning"]);
    }

    [Fact]
    public void ToolsCall_Search_AcceptsScalarExcludePaths_Issue3538()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","excludePaths":"src/app.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Definition_AcceptsLspCompatibleAlias_Issue3538()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","lspCompatible":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["error"]);
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["lspCompatible"]!.GetValue<bool>());
        Assert.Equal("file", structured["results"]![0]!["uri"]!.GetValue<string>().Split(':')[0]);
    }

    [Fact]
    public void ToolsCall_DeprecatedAliasTypeError_CarriesCompatibilityMetadata_Issue3538()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"App","exact":"yes"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var data = response["error"]!["data"]!;
        Assert.Equal("exactName", data["alias_of"]!.GetValue<string>());
        Assert.True(data["deprecated"]!.GetValue<bool>());
        Assert.Equal("boolean", data["expected"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_GuardFiltersReturnEvidence_Issue2852()
    {
        InsertIndexedFile(
            "src/guard-mcp.cs",
            "csharp",
            """
            using System.IO;

            public class GuardMcp
            {
                public void Atomic(string path, string tempPath)
                {
                    using var stream = new FileStream(path, FileMode.Create);
                    File.Move(tempPath, path, overwrite: true);
                }

                public void NonAtomic(string path)
                {
                    using var stream = new FileStream(path, FileMode.Create);
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"FileMode.Create","exactSubstring":true,"requireAfter":"File.Move","guardWindow":2}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        var result = structured["results"]![0]!;
        Assert.Equal("src/guard-mcp.cs", result["path"]!.GetValue<string>());
        var evidence = Assert.Single(result["guardEvidence"]!.AsArray());
        Assert.Equal("require", evidence!["role"]!.GetValue<string>());
        Assert.Equal("after", evidence["direction"]!.GetValue<string>());
        Assert.Equal("File.Move", evidence["query"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_GuardFiltersFailFastWhenCombinedArraysExceedLimit_Issue3073()
    {
        var requireBefore = new JsonArray();
        for (var i = 0; i < DbReader.MaxSearchGuardFilters; i++)
            requireBefore.Add($"Guard{i}");
        var requireAfter = new JsonArray();
        requireAfter.Add("Overflow");
        requireAfter.Add(42);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["requireBefore"] = requireBefore,
                    ["requireAfter"] = requireAfter,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"search accepts at most {DbReader.MaxSearchGuardFilters} guard filters; got {DbReader.MaxSearchGuardFilters + 1}.", text);
        Assert.DoesNotContain("entries must be strings", text);
        Assert.Equal("invalid_argument", result["structuredContent"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_GuardPaginationResumesWithinSplitChunk_Issue2852()
    {
        InsertIndexedFile(
            "src/guard-paged.cs",
            "csharp",
            """
            using System.IO;

            public class GuardPaged
            {
                public void First(string path)
                {
                    var one = File.ReadAllText(path);
                }

                public void Second(string path)
                {
                    var two = File.ReadAllText(path);
                }
            }
            """);

        var firstRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"File.ReadAllText","exactSubstring":true,"rejectBefore":"Length","guardWindow":1,"limit":1}}}""")!;
        var firstResponse = _server.HandleMessage(firstRequest)!;
        var firstStructured = firstResponse["result"]!["structuredContent"]!;
        var firstSnippet = firstStructured["results"]![0]!["snippet"]!.GetValue<string>();
        var cursor = firstStructured["next_cursor"]!.GetValue<string>();

        var secondRequest = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{\"query\":\"File.ReadAllText\",\"exactSubstring\":true,\"rejectBefore\":\"Length\",\"guardWindow\":1,\"limit\":1,\"cursor\":\"" + cursor + "\"}}}")!;
        var secondResponse = _server.HandleMessage(secondRequest)!;
        var secondStructured = secondResponse["result"]!["structuredContent"]!;
        var secondSnippet = secondStructured["results"]![0]!["snippet"]!.GetValue<string>();

        Assert.Contains("one", firstSnippet);
        Assert.Contains("two", secondSnippet);
    }

    [Fact]
    public void ToolsCall_Search_ExactSubstringReturnsLiteralHighlightMetadata()
    {
        InsertIndexedFile("src/sql.cs", "csharp", "var CommandText = $\"SELECT 1\";\nvar CommandText = other;\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"CommandText = $","exactSubstring":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var highlight = response["result"]!["structuredContent"]!["results"]![0]!["highlights"]![0]!;
        var literalOccurrence = highlight["literalTermOccurrences"]![0]!;
        Assert.Equal("CommandText = $", highlight["literalTerms"]![0]!.GetValue<string>());
        Assert.Equal("CommandText = $", literalOccurrence["term"]!.GetValue<string>());
        Assert.Equal(1, literalOccurrence["line"]!.GetValue<int>());
        Assert.Equal(5, literalOccurrence["column"]!.GetValue<int>());
        Assert.Equal("CommandText = $".Length, literalOccurrence["length"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Search_TokenBoundaryFiltersLongerIdentifiers_Issue4323()
    {
        InsertIndexedFile("src/client.cs", "csharp", "using System.Net.Http;\nvar client = new HttpClient();\n");
        InsertIndexedFile("src/handler.cs", "csharp", "using System.Net.Http;\nvar handler = new HttpClientHandler();\n");
        InsertIndexedFile("src/object-init.cs", "csharp", "using System;\nusing System.Net.Http;\nvar client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"new HttpClient","tokenBoundary":true,"limit":10}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();
        var paths = results.Select(result => result!["path"]!.GetValue<string>()).ToArray();
        Assert.True(structured["tokenBoundary"]!.GetValue<bool>());
        Assert.Equal(2, results.Count);
        Assert.Contains("src/client.cs", paths);
        Assert.Contains("src/object-init.cs", paths);
        Assert.DoesNotContain("src/handler.cs", paths);
        Assert.NotNull(results[0]!["highlights"]);
    }

    [Fact]
    public void ToolsCall_Search_PunctuationHeavyQueryAddsExactSubstringRecoveryHint()
    {
        InsertIndexedFile("src/sql.cs", "csharp", "var CommandText = $\"SELECT 1\";\nvar CommandText = other;\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"CommandText = $","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var recoveryHint = response["result"]!["structuredContent"]!["recovery_hint"]!;
        Assert.Equal("punctuation_heavy_query", recoveryHint["reason"]!.GetValue<string>());
        Assert.Equal(
            "This looks like a literal code phrase; rerun the search with exactSubstring=true for punctuation-sensitive matching. Use token-boundary search when longer identifiers should not match shorter code phrases.",
            recoveryHint["suggested_action"]!.GetValue<string>());
        Assert.Equal("search", recoveryHint["tool"]!.GetValue<string>());
        Assert.Equal("CommandText = $", recoveryHint["args"]!["query"]!.GetValue<string>());
        Assert.True(recoveryHint["args"]!["exactSubstring"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Search_ExcludesGeneratedFilesByDefault()
    {
        InsertIndexedFile("src/generated.g.cs", "csharp", "class Generated { void Needle() {} }\n", generated: true);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Needle"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Empty(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_Search_IncludeGeneratedReturnsGeneratedFiles()
    {
        InsertIndexedFile("src/generated.g.cs", "csharp", "class Generated { void Needle() {} }\n", generated: true);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Needle","includeGenerated":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/generated.g.cs", structured["results"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_NoResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"nonexistent_xyz_123"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("No results found", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        // Zero-result responses include freshness hint / 0件時に鮮度ヒントを含む
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0);
        Assert.True(structured["freshness_available"]!.GetValue<bool>());
        Assert.NotNull(structured["indexed_at"]);
    }

    [Fact]
    public void ToolsCall_Files_NoResults_OnEmptyIndex_EmitsNullIndexedAt()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_empty");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
                var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files","arguments":{"query":"nonexistent_xyz_123"}}}""")!;
                var response = server.HandleMessage(request)!;
                using var document = JsonDocument.Parse(response.ToJsonString());
                var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");

                Assert.Equal(0, structured.GetProperty("count").GetInt32());
                Assert.Equal(0, structured.GetProperty("results").GetArrayLength());
                Assert.Equal(0, structured.GetProperty("indexed_file_count").GetInt64());
                Assert.True(structured.GetProperty("freshness_available").GetBoolean());
                Assert.True(structured.TryGetProperty("indexed_at", out var indexedAt));
                Assert.Equal(JsonValueKind.Null, indexedAt.ValueKind);
            }
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Search_RawQuerySupportsFtsSyntax()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Ap*","rawQuery":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.True(response["result"]!["structuredContent"]!["rawQuery"]!.GetValue<bool>());
        var result = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal("src/app.cs", result["path"]!.GetValue<string>());
        Assert.True(result["rawFts"]!.GetValue<bool>());
        Assert.False(result["exact"]!.GetValue<bool>());
        Assert.False(result["literalHighlightsAvailable"]!.GetValue<bool>());
        Assert.Equal("literal_highlights_unavailable_raw_fts", result["literalHighlightWarning"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_ExactWithRawQueryReportsEffectiveLiteralHighlightMode_Issue3558()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","rawQuery":true,"exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.True(response["result"]!["structuredContent"]!["rawQuery"]!.GetValue<bool>());
        var result = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.True(result["exact"]!.GetValue<bool>());
        Assert.False(result["rawFts"]!.GetValue<bool>());
        Assert.True(result["literalHighlightsAvailable"]!.GetValue<bool>());
        Assert.Null(result["literalHighlightWarning"]);
        var highlight = result["highlights"]![0]!;
        Assert.Equal("App", highlight["literalTerms"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    [InlineData("analyze_symbol")]
    [InlineData("impact_analysis")]
    public void ToolsCall_BareVerbatimPrefix_IsRejected(string toolName)
    {
        var request = JsonNode.Parse($@"{{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{{""name"":""{toolName}"",""arguments"":{{""query"":""@""}}}}}}")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("bare verbatim prefixes like `@` are not valid queries", text);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_RejectsOversizedQuery_Issue3184()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "impact_analysis",
                ["arguments"] = new JsonObject
                {
                    ["query"] = new string('a', QueryLimits.MaxQueryLength + 1),
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Equal(QueryLimits.FormatQueryTooLongError(), result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, result["structuredContent"]!["category"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_SnippetLinesControlsExcerptLength()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","snippetLines":3}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(3, response["result"]!["structuredContent"]!["snippetLines"]!.GetValue<int>());
        var snippet = response["result"]!["structuredContent"]!["results"]![0]!["snippet"]!.GetValue<string>();
        Assert.True(snippet.Split('\n').Length <= 3);
    }

    [Fact]
    public void ToolsCall_Search_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("src/long.cs", "csharp", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"TARGET","exact":true,"maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Contains("TARGET", result["snippet"]!.GetValue<string>());
        Assert.DoesNotContain("...(+", result["snippet"]!.GetValue<string>());
        Assert.True(result["snippet"]!.GetValue<string>().Length > 512);
    }

    [Fact]
    public void ToolsCall_Search_MaxLineWidthAboveCeilingReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","maxLineWidth":4097}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("maxLineWidth must be less than or equal to 4096", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Search_ExcludeTests_ReturnsOnlySourceMatches()
    {
        InsertIndexedFile("tests/app_test.cs", "csharp", "public class AppTests { public void RunScenario() { var app = new App(); } }");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","excludeTests":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.True(response["result"]!["structuredContent"]!["excludeTests"]!.GetValue<bool>());
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["results"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Map_ReturnsRepoOverview()
    {
        InsertIndexedFile("src/Program.cs", "csharp", "public class Program\n{\n    public static void Main(string[] args)\n    {\n        var app = new App();\n    }\n}\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"map","arguments":{"limit":5,"excludeTests":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(5, response["result"]!["structuredContent"]!["limit"]!.GetValue<int>());
        Assert.NotNull(response["result"]!["structuredContent"]!["languages"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["modules"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["topFiles"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["indexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["projectRoot"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspaceIndexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspaceLatestModified"]);
        Assert.Contains("Main", response["result"]!["structuredContent"]!["entrypoints"]!.ToJsonString());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ReturnsBundledContext()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","includeBody":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("Run", response["result"]!["structuredContent"]!["query"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["structuredContent"]!["file"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["definitions"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["nearby_symbols"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["callers"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["callees"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspace_indexed_at"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["workspace_latest_modified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["project_root"]);
        Assert.True(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Heading","lang":"toml"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("toml", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("Use search, definition, excerpt, or files instead.", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_KeepsSubscribeReferencesVisibleInBundle()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var reference = response["result"]!["structuredContent"]!["references"]![0]!;
        var caller = response["result"]!["structuredContent"]!["callers"]![0]!;

        Assert.Equal("subscribe", reference["referenceKind"]!.GetValue<string>());
        Assert.Equal("Hook", reference["containerName"]!.GetValue<string>());
        Assert.Equal("Hook", caller["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", caller["calleeName"]!.GetValue<string>());
        Assert.Empty(response["result"]!["structuredContent"]!["callees"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_KeepsSubscribeCalleesVisibleForCallerSymbols()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Hook","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var callee = response["result"]!["structuredContent"]!["callees"]![0]!;

        Assert.Equal("Hook", callee["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", callee["calleeName"]!.GetValue<string>());
        Assert.Equal("subscribe", callee["referenceKind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_NonExactEnumMemberStaysGraphSupported()
    {
        InsertIndexedFile("src/colors.cs", "csharp",
            """
            namespace Demo;

            public enum Color
            {
                Red,
                Green
            }

            public class UsesColor
            {
                public Color Shade => Color.Red;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Red","lang":"csharp"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var definition = structured["definitions"]![0]!;

        Assert.Equal("Red", definition["name"]!.GetValue<string>());
        Assert.Equal("enum", definition["containerKind"]!.GetValue<string>());
        Assert.Equal("Color", definition["containerName"]!.GetValue<string>());
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("Shade", structured["references"]![0]!["containerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_NonExactCrossLanguageMixedHitPrefersGraphCapablePrimaryDefinition()
    {
        InsertIndexedFile("web/app.js", "javascript",
            """
            function Ready() {}

            function Helper() {}

            Ready();
            """);
        InsertIndexedFile("src/status.cs", "csharp",
            """
            namespace Demo;

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Ready"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var nearbyPaths = structured["nearby_symbols"]!
            .AsArray()
            .Select(symbol => symbol?["path"]?.GetValue<string>())
            .Where(path => path != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal("web/app.js", structured["file"]!["path"]!.GetValue<string>());
        Assert.Equal("javascript", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Contains("web/app.js", nearbyPaths);
        Assert.DoesNotContain("src/status.cs", nearbyPaths);
        Assert.Contains(structured["nearby_symbols"]!.AsArray(),
            symbol => symbol?["name"]?.GetValue<string>() == "Helper");
        Assert.All(structured["references"]!.AsArray(),
            reference => Assert.Equal("javascript", reference?["lang"]?.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Callers_DefaultQueryKeepsSubscribeRowsVisible()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var row = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("Hook", row["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", row["calleeName"]!.GetValue<string>());
        // #501: MCP wire format exposes referenceKind (preferred summary), referenceKinds (sorted distinct), and hasMixedReferenceKinds
        // #501: MCP のワイヤ形式は referenceKind（要約）、referenceKinds（ソート済み distinct）、hasMixedReferenceKinds を返す
        Assert.Equal("subscribe", row["referenceKind"]!.GetValue<string>());
        Assert.False(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "subscribe" }, kinds);
    }

    [Fact]
    public void ToolsCall_Callers_SurfacesMixedReferenceKindsForCallAndSubscribeContainer()
    {
        InsertIndexedFile("src/MixedOwner.cs", "csharp",
            """
            using System;

            public class MixedOwner
            {
                public event EventHandler? Changed;

                public void SetupAndFire()
                {
                    Changed += OnChanged;
                    Changed(this, EventArgs.Empty);
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var row = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("SetupAndFire", row["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", row["calleeName"]!.GetValue<string>());
        Assert.Equal(2, row["referenceCount"]!.GetValue<int>());
        Assert.True(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "call", "subscribe" }, kinds);
        Assert.Equal("subscribe", row["referenceKind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_SurfacesMixedReferenceKindsInBundledCallers()
    {
        InsertIndexedFile("src/MixedOwner.cs", "csharp",
            """
            using System;

            public class MixedOwner
            {
                public event EventHandler? Changed;

                public void SetupAndFire()
                {
                    Changed += OnChanged;
                    Changed(this, EventArgs.Empty);
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Changed","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var caller = response["result"]!["structuredContent"]!["callers"]![0]!;

        Assert.Equal("SetupAndFire", caller["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", caller["calleeName"]!.GetValue<string>());
        Assert.Equal("subscribe", caller["referenceKind"]!.GetValue<string>());
        Assert.True(caller["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = caller["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "call", "subscribe" }, kinds);
    }

    [Fact]
    public void ToolsCall_Callees_DefaultQueryKeepsSubscribeRowsVisible()
    {
        InsertIndexedFile("src/Publisher.cs", "csharp",
            """
            using System;

            public class Publisher
            {
                public event EventHandler? Changed;
            }
            """);
        InsertIndexedFile("src/Subscriber.cs", "csharp",
            """
            using System;

            public class Subscriber
            {
                public void Hook(Publisher publisher)
                {
                    publisher.Changed += OnChanged;
                }

                private void OnChanged(object? sender, EventArgs e) { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Hook","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var row = response["result"]!["structuredContent"]!["results"]![0]!;
        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("Hook", row["callerName"]!.GetValue<string>());
        Assert.Equal("Changed", row["calleeName"]!.GetValue<string>());
        Assert.Equal("subscribe", row["referenceKind"]!.GetValue<string>());
        // #501: callees rows stay split per kind so referenceKinds is a single-element array and hasMixedReferenceKinds is false
        // #501: callees 行は kind 単位で分かれるため referenceKinds は単要素、hasMixedReferenceKinds は false
        Assert.False(row["hasMixedReferenceKinds"]!.GetValue<bool>());
        var kinds = row["referenceKinds"]!.AsArray().Select(k => k!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "subscribe" }, kinds);
    }

    [Fact]
    public void ToolsCall_References_ReturnsIndexedReference()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("login", response["result"]!["structuredContent"]!["results"]![0]!["containerName"]!.GetValue<string>());
        Assert.Equal("call", response["result"]!["structuredContent"]!["results"]![0]!["referenceKind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = "def login(user, password): return Run(user) # " + new string('x', 700);
        InsertIndexedFile("src/session.py", "python", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var result = response["result"]!["structuredContent"]!["results"]![0]!;

        Assert.False(result["contextTruncated"]!.GetValue<bool>());
        Assert.Contains("Run(user)", result["context"]!.GetValue<string>());
        Assert.DoesNotContain("...(+", result["context"]!.GetValue<string>());
        Assert.True(result["context"]!.GetValue<string>().Length > 512);
    }

    [Fact]
    public void ToolsCall_References_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"toml"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("toml", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_ExactOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_ExactEnumMember_ReturnsIndexedReference()
    {
        InsertIndexedFile("src/colors.cs", "csharp",
            """
            namespace Demo;

            public enum Color
            {
                Red,
                Green
            }

            public class UsesColor
            {
                public Color Shade => Color.Red;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Red","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("Found 1 reference.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("Shade", structured["results"]![0]!["containerName"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
    }

    [Fact]
    public void ToolsCall_References_ExactMixedCallableAndEnumMemberKeepsGraphSupported()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public class Worker
            {
                public void Ready() { }

                public void Use()
                {
                    Ready();
                }
            }

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Ready","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.Equal("Use", structured["results"]![0]!["containerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_References_ExactCrossLanguageMixedHitDoesNotForceCSharpGraphLanguage()
    {
        InsertIndexedFile("web/app.js", "javascript",
            """
            export function Ready() {}

            Ready();
            """);
        InsertIndexedFile("src/status.cs", "csharp",
            """
            namespace Demo;

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Ready","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("javascript", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
    }

    [Fact]
    public void ToolsCall_Callers_ReturnsCallerSummary()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Run"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("login", response["result"]!["structuredContent"]!["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_ExactEnumMember_ReturnsIndexedCaller()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public enum Nested
            {
                A = 1,
                B = A
            }

            public class UsesEnum
            {
                public Nested Value => Nested.A;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"A","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("Value", structured["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Found 1 caller.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_ExactMixedCallableAndEnumMemberKeepsGraphSupported()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public class Worker
            {
                public void Ready() { }

                public void Use()
                {
                    Ready();
                }
            }

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Ready","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.Equal("Use", structured["results"]![0]!["callerName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callers_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Run","lang":"toml"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("toml", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_ReturnsCalleeSummary()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"login"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(1, response["result"]!["structuredContent"]!["count"]!.GetValue<int>());
        Assert.Equal("login", response["result"]!["structuredContent"]!["results"]![0]!["callerName"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_ExactEnumMember_UsesZeroSchema()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public enum Nested
            {
                A = 1,
                B = A
            }

            public class UsesEnum
            {
                public Nested Value => Nested.A;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"A","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("No callees found.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_ExactMixedCallableAndEnumMemberKeepsGraphSupported()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public class Worker
            {
                public void Ready()
                {
                    Next();
                }

                public void Next() { }
            }

            public enum Status
            {
                Ready
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Ready","lang":"csharp","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.Equal("csharp", structured["graph_language"]!.GetValue<string>());
        Assert.Equal("Next", structured["results"]![0]!["calleeName"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Callees_UnsupportedLanguage_ReturnsGraphSupportHint()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callees","arguments":{"query":"Run","lang":"toml"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal("toml", response["result"]!["structuredContent"]!["graph_language"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["graph_supported"]!.GetValue<bool>());
        Assert.Contains("not indexed", response["result"]!["structuredContent"]!["graph_support_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeAndReferencesShareStaleSqlGraphFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_analyze_symbol_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"fn_Target","lang":"sql","exact":true}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());

            var referencesRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"references","arguments":{"query":"fn_Target","lang":"sql"}}}""")!;
            var referencesResponse = server.HandleMessage(referencesRequest)!;
            var referencesStructured = referencesResponse["result"]!["structuredContent"]!;

            Assert.Equal(1, referencesStructured["count"]!.GetValue<int>());
            Assert.False(referencesStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", referencesStructured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_CallersAndAnalyzeShareMixedRepoCSharpFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_callers_mixed_sql_graph_contract");
        try
        {
            var dbPath = CreateMixedSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"N","exact":true}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);

            var analyzeRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"N","exact":true}}}""")!;
            var analyzeResponse = server.HandleMessage(analyzeRequest)!;
            var analyzeStructured = analyzeResponse["result"]!["structuredContent"]!;

            Assert.Null(analyzeStructured["sql_graph_contract_ready"]);
            Assert.Null(analyzeStructured["sql_graph_contract_degraded_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("callers", "attribute")]
    [InlineData("callers", "annotation")]
    [InlineData("callers", "type_reference")]
    [InlineData("callers", "type_tag")]
    [InlineData("callers", "import")]
    [InlineData("callees", "attribute")]
    [InlineData("callees", "annotation")]
    [InlineData("callees", "type_reference")]
    [InlineData("callees", "type_tag")]
    [InlineData("callees", "import")]
    public void ToolsCall_CallersOrCallees_NonCallGraphKindReturnsToolError(string tool, string kind)
    {
        // issue #293 + issue #444: the MCP `callers` / `callees` tools must reject non-call-graph
        // kinds. Metadata rows (`attribute` / `annotation`) are attributed to the enclosing
        // body-range symbol (so `callers Obsolete kind=attribute` reports the enclosing class
        // instead of the annotated method, and file-level targets drop entirely). `type_reference`
        // rows are compile-time type mentions (declaration types, generic constraints, `is`/`as`/
        // `instanceof`, XML-doc `cref`) and not runtime calls. `type_tag` rows describe JavaScript/
        // TypeScript discriminant narrowing, not calls. AI clients should be redirected to the
        // `references` tool for these enumerations. `import` rows are structural dependency edges,
        // not runtime calls, and follow the same rejection path.
        // issue #293 + issue #444 補足: MCP の `callers` / `callees` ツールは非 call-graph な kind を
        // 必ず弾く。metadata 行 (`attribute` / `annotation`) は body-range の外側シンボルに帰属する
        // ため、`callers Obsolete kind=attribute` は注釈対象のメソッドではなく外側クラスを返し、
        // file-level target は完全に脱落する。`type_reference` は宣言型・generic 制約・`is`/`as`/
        // `instanceof`・XML-doc `cref` といった compile-time な型言及であり実行時呼び出しではない。
        // `type_tag` 行も JavaScript / TypeScript の discriminant narrowing であり call ではない。
        // `import` 行も runtime call ではなく構造的な dependency edge なので同じ拒否経路に入る。
        // AI クライアントは列挙のために `references` ツールに誘導する。
        var requestJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\","
            + "\"params\":{\"name\":\"" + tool + "\","
            + "\"arguments\":{\"query\":\"SomeSymbol\",\"kind\":\"" + kind + "\"}}}";
        var request = JsonNode.Parse(requestJson)!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"'kind: {kind}' is not supported on '{tool}'", text);
        Assert.Contains("'references' tool", text);
        if (kind == "import")
            Assert.Contains("Import references are structural dependency edges, not runtime calls", text);
    }

    [Fact]
    public void ToolsCall_References_AcceptsTypeReferenceKind()
    {
        // issue #444: `references` with `kind: "type_reference"` is a legitimate query (the
        // compile-time type-position edges emitted by ReferenceExtractor for C#/Java base
        // lists, declaration types, generic constraints, `is`/`as`/`instanceof`, and XML-doc
        // `cref`). It must succeed and return the expected `reference_kind` in
        // structuredContent, unlike the rejected `callers`/`callees` tools.
        // issue #444: MCP `references` の `kind: "type_reference"` は compile-time な型位置エッジ
        // を列挙する正当なクエリ（C#/Java の継承リスト・宣言型・generic 制約・`is`/`as`/
        // `instanceof`・XML-doc `cref`）。拒否される `callers` / `callees` とは異なり、成功して
        // structuredContent に `reference_kind` を返さなければならない。
        InsertIndexedFile("src/Target.cs", "csharp",
            """
            public class TargetBase { }
            """);
        InsertIndexedFile("src/Consumer.cs", "csharp",
            """
            public class Consumer : TargetBase
            {
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"TargetBase","kind":"type_reference","lang":"csharp","exactName":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("type_reference", structured["kind"]!.GetValue<string>());
        Assert.True(structured["count"]!.GetValue<int>() >= 1);
        var results = structured["results"]!.AsArray();
        Assert.Contains(results, r => r!["referenceKind"]!.GetValue<string>() == "type_reference"
            && r["symbolName"]!.GetValue<string>() == "TargetBase");
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ClassSymbolReturnsHeuristicFileDependencyHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var fileImpacts = structured["file_impacts"]!.AsArray();

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.Equal("src/App.cs", fileImpacts[0]!["sourcePath"]!.GetValue<string>());
        Assert.Equal("src/FolderDiffService.cs", fileImpacts[0]!["targetPath"]!.GetValue<string>());
        Assert.True(structured["has_class_like_definitions"]!.GetValue<bool>());
        Assert.Contains("heuristic hints only", structured["note"]!.GetValue<string>());
        Assert.Contains("heuristic only", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ClassAndNamespaceWithSameNameStillReturnsHeuristicHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            namespace FooService;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.True(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal(2, structured["definition_count"]!.GetValue<int>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_HeuristicHintsUseVisibleCount()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Run(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["file_count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_count"]!.GetValue<int>());
        Assert.Equal(0, structured["confirmed_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_file_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_FoldEquivalentClassDefinitionsReportAmbiguity()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FullwidthFooService.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);
        MarkFoldReady();

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.Equal(2, structured["definition_count"]!.GetValue<int>());
        Assert.Equal("multiple_definition_files", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ClassCollisionWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/BarService.cs", "csharp",
            """
            public class BarService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(BarService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.False(structured["heuristic"]!.GetValue<bool>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_CommentOnlyTypeMentionDoesNotProduceHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/OtherService.cs", "csharp",
            """
            public class OtherService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(OtherService service)
                {
                    service.Run(); // TODO: maybe replace with FooService later
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_StringLiteralTypeMentionDoesNotProduceHints()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/Worker.cs", "csharp",
            """
            public class Worker
            {
                public void Execute() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Worker worker)
                {
                    var label = "FooService";
                    worker.Execute();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_NamespaceStaysZero()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace Acme;

            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            namespace Acme;

            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Acme"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal("non_callable_symbol_kind", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ImportOnlyQueryReportsNonCallableSymbolKind()
    {
        InsertIndexedFile("src/app.py", "python",
            """
            import requests
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"requests"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Equal("non_callable_symbol_kind", structured["zero_result_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_UnicodeTypeEvidenceStillReturnsHints()
    {
        InsertIndexedFile("src/ＦｏｏＳｅｒｖｉｃｅ.cs", "csharp",
            """
            public class ＦｏｏＳｅｒｖｉｃｅ
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(ＦｏｏＳｅｒｖｉｃｅ service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"ＦｏｏＳｅｒｖｉｃｅ"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_DuplicateDefinitionsInOneFileReportAmbiguity()
    {
        InsertIndexedFile("src/Services.cs", "csharp",
            """
            namespace A
            {
                public class FooService
                {
                    public void Run() { }
                }
            }

            namespace B
            {
                public class FooService
                {
                    public void Run() { }
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(2, structured["definition_count"]!.GetValue<int>());
        Assert.Equal(1, structured["definition_file_count"]!.GetValue<int>());
        Assert.True(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal("multiple_definitions", structured["zero_result_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_DepthZeroReturnsResolvedSymbolWithoutCallers()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxHops":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Empty(structured["callers"]!.AsArray());
        Assert.Equal("depth_requested_zero", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Equal("precondition", structured["suggestion_type"]!.GetValue<string>());
        var failureChain = Assert.IsType<JsonArray>(structured["impact_failure_chain"]);
        Assert.Equal("depth_requested_zero", Assert.Single(failureChain)!.GetValue<string>());
        Assert.Equal("Use `cdidx impact <symbol> --max-hops 1` or higher to traverse callers.", structured["suggestion"]!.GetValue<string>());
    }

    // #1534: requests above the server cap (50) must surface a warning and `max_hops_requested`
    // instead of silently clamping, so agents can react (raise the cap, accept partial depth, etc.).
    // #1534: サーバー上限 (50) を超える maxHops は黙ってクランプせず、warnings と
    // max_hops_requested で通知し、エージェントが対応できるようにする。
    [Fact]
    public void ToolsCall_ImpactAnalysis_MaxHopsAboveCapSurfacesWarningAndRequestedValue()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxHops":100}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(50, structured["max_hops"]!.GetValue<int>());
        Assert.Equal(100, structured["max_hops_requested"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth"]!.GetValue<int>());
        Assert.Equal(100, structured["max_depth_requested"]!.GetValue<int>());
        var warnings = structured["warnings"]!.AsArray();
        Assert.Single(warnings);
        var warning = warnings[0]!.GetValue<string>();
        Assert.Contains("maxHops was clamped from 100 to 50", warning);
        Assert.Contains("[0, 50]", warning);
        var summaryText = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("maxHops was clamped from 100 to 50", summaryText);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_MaxHopsWithinCapDoesNotEmitWarning()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxHops":50}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(50, structured["max_hops"]!.GetValue<int>());
        Assert.Equal(50, structured["max_hops_requested"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth"]!.GetValue<int>());
        Assert.Equal(50, structured["max_depth_requested"]!.GetValue<int>());
        Assert.Null(structured["warnings"]);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_DeprecatedMaxDepthSurfacesWarning()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","maxDepth":2}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(2, structured["max_hops"]!.GetValue<int>());
        Assert.Equal(2, structured["max_depth"]!.GetValue<int>());
        var warning = Assert.Single(structured["warnings"]!.AsArray())!.GetValue<string>();
        Assert.Contains("maxDepth is deprecated", warning);
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ReturnsSameCallerPerReferenceKind()
    {
        InsertIndexedFile("src/EventHub.cs", "csharp",
            """
            public class EventHub
            {
                public event System.Action? Changed;
                public void Changed() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(EventHub hub)
                {
                    hub.Changed += OnChanged;
                    hub.Changed();
                }

                private void OnChanged() { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Changed","limit":10}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var callers = structured["callers"]!.AsArray()
            .Where(caller => caller!["path"]!.GetValue<string>() == "src/App.cs"
                && caller!["callerName"]!.GetValue<string>() == "Boot")
            .ToList();

        Assert.Equal(2, callers.Count);
        Assert.Equal(new[] { "call", "subscribe" }, callers.Select(caller => caller!["referenceKind"]!.GetValue<string>()).Order().ToArray());
        Assert.All(callers, caller => Assert.Equal(1, caller!["referenceCount"]!.GetValue<int>()));
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ExcludeTestsIgnoresOutOfScopeDuplicateDefinitions()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("tests/FooServiceTests.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService","excludeTests":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal(1, structured["definition_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/FooService.cs", structured["definitions"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_IgnoresUnsupportedLanguageDuplicates()
    {
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/tools.txt", "text",
            """
            FooService() {
              :
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FooService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definitions"]!.GetValue<bool>());
        Assert.False(structured["has_multiple_definition_files"]!.GetValue<bool>());
        Assert.Equal(1, structured["definition_file_count"]!.GetValue<int>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal("src/FooService.cs", structured["definitions"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_ExactDefinitionResolutionSkipsUnsupportedMatchesBeforeLimit()
    {
        for (int i = 0; i < 60; i++)
        {
            InsertIndexedFile($"scripts/Foo{i:D2}.sh", "text",
                """
                Foo() {
                  :
                }
                """);
        }

        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(Foo service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Foo"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["definition_count"]!.GetValue<int>());
        Assert.Equal("src/Foo.cs", structured["definitions"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("src/App.cs", structured["file_impacts"]![0]!["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_SubstringTypeEvidenceDoesNotProduceHints()
    {
        InsertIndexedFile("src/Foo.cs", "csharp",
            """
            public class Foo
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/FooService.cs", "csharp",
            """
            public class FooService
            {
                public void Run() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Handle(FooService service)
                {
                    service.Run();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Foo"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_HeuristicHintsSetTruncatedWhenLimitReached()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App1.cs", "csharp",
            """
            public class App1
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);
        InsertIndexedFile("src/App2.cs", "csharp",
            """
            public class App2
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.True(structured["heuristic"]!.GetValue<bool>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["hint_count"]!.GetValue<int>());
        Assert.Single(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_HeuristicHintsKeepActualReferenceCount()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/App.cs", "csharp",
            """
            public class App
            {
                public void Boot(FolderDiffService service)
                {
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                    service.ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("file_dependency_hints", structured["impact_mode"]!.GetValue<string>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(4, structured["file_impacts"]![0]!["referenceCount"]!.GetValue<int>());
        Assert.Equal("ExecuteFolderDiffAsync,FolderDiffService", structured["file_impacts"]![0]!["symbols"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_UnresolvedExternalCallWithoutTypeEvidenceReturnsNoHints()
    {
        InsertIndexedFile("src/FolderDiffService.cs", "csharp",
            """
            public class FolderDiffService
            {
                public void ExecuteFolderDiffAsync() { }
            }
            """);
        InsertIndexedFile("src/ExternalConsumer.cs", "csharp",
            """
            public class ExternalConsumer
            {
                public void Boot()
                {
                    ExecuteFolderDiffAsync();
                }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"FolderDiffService"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal("none", structured["impact_mode"]!.GetValue<string>());
        Assert.False(structured["heuristic"]!.GetValue<bool>());
        Assert.Equal(0, structured["hint_count"]!.GetValue<int>());
        Assert.Equal("class_symbol_no_symbol_callers", structured["zero_result_reason"]!.GetValue<string>());
        Assert.Empty(structured["file_impacts"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_IncludesCombinedExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbol_refs_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Contains("idx_symbol_refs_container_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_NonExactOnReadOnlyLegacyDb_OmitsExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def login(user, password):\n    return Run(user)\n");
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run"}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_Symbols_ExactOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_ExactWithoutQuery_OnReadOnlyLegacyDb_OmitsExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n");
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"exact":true,"limit":1}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
        Assert.Null(structured["exact_index_available"]);
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_Definition_ExactOnReadOnlyLegacyDb_IncludesExactIndexSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ExactSignals_RespectMcpQueryScopeForStaleCSharpCanonicalNames()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n");
        var writer = new DbWriter(_db.Connection);
        writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, "0");

        var pythonRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","lang":"python","exact":true}}}""")!;
        var pythonResponse = _server.HandleMessage(pythonRequest)!;
        var pythonStructured = pythonResponse["result"]!["structuredContent"]!;

        Assert.True(pythonStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(pythonStructured["degraded_reason"]);
        Assert.True(pythonStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(pythonStructured["degraded_reason"]);

        var csharpRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","lang":"csharp","exact":true}}}""")!;
        var csharpResponse = _server.HandleMessage(csharpRequest)!;
        var csharpStructured = csharpResponse["result"]!["structuredContent"]!;

        Assert.False(csharpStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("csharp_symbol_name_ready=false", csharpStructured["degraded_reason"]!.GetValue<string>());
        Assert.False(csharpStructured["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("csharp_symbol_name_ready=false", csharpStructured["degraded_reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_WithMissingSymbolFallbackIndex_IncludesBundleSignal()
    {
        InsertIndexedFile("src/session.py", "python", "def Run(user):\n    return user\n\ndef login(user, password):\n    return Run(user)\n");
        ForceLegacyExactFallbackMode();
        DropSymbolExactFallbackIndex();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;

        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.False(response["result"]!["structuredContent"]!["exact_index_available"]!.GetValue<bool>());
        Assert.Contains("idx_symbols_name_nocase", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("Run", response["result"]!["structuredContent"]!["definitions"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_UnsupportedGraphLanguage_SkipsGraphDegradedSignal()
    {
        InsertIndexedFile("docs/guide.toml", "toml", "title = \"Heading\"\nrun = \"Run\"\n");
        ForceLegacyExactFallbackMode();
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Heading","lang":"toml","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.False(structured["graph_supported"]!.GetValue<bool>());
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactOnReadOnlyLegacyDb_PathOnlyUnsupportedSlice_SkipsGraphDegradedSignal()
    {
        InsertIndexedFile("docs/guide.toml", "toml", "title = \"Heading\"\nrun = \"Run\"\n");
        ForceLegacyExactFallbackMode();
        DropGraphExactFallbackIndexes();
        using var readOnlyServer = new McpServer(new Uri(_dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Run","path":"docs/","exact":true}}}""")!;
        var response = readOnlyServer.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
        Assert.True(structured["exact_index_available"]!.GetValue<bool>());
        Assert.Null(structured["degraded_reason"]);
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ExactZeroHintWhenWholeBundleIsEmpty()
    {
        InsertIndexedFile("src/handler.cs", "csharp",
            """
            public class Handler
            {
                public void HandleRequest() { }
                public void HandleRequestAsync() { HandleRequest(); }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"HandleRe","exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();

        Assert.NotNull(structured["exact_zero_hint"]);
        Assert.Equal(2, structured["exact_zero_hint"]!["relaxed_count"]!.GetValue<int>());
        Assert.Contains("HandleRequest", structured["exact_zero_hint"]!["sample_names"]!.ToJsonString());
        Assert.Contains("Substring would return 2", text);
    }

    [Fact]
    public void ToolsCall_Search_MissingQuery_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("query", text);
    }

    [Fact]
    public void ToolsCall_Search_ExactSubstringAliasMatchesBackwardCompatibleExact()
    {
        InsertIndexedFile("src/search.cs", "csharp", "void Run() { }\nvoid RunAsync() { Run(); }\n");

        var exactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run();","exact":true}}}""")!;
        var aliasRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run();","exactSubstring":true}}}""")!;

        var exactResponse = _server.HandleMessage(exactRequest)!;
        var aliasResponse = _server.HandleMessage(aliasRequest)!;

        Assert.Equal(
            exactResponse["result"]!["structuredContent"]!.ToJsonString(),
            aliasResponse["result"]!["structuredContent"]!.ToJsonString());
    }

    [Fact]
    public void ToolsCall_Search_RejectsExactNameAlias()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","exactName":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Unknown argument 'exactName'", text);
    }

    [Fact]
    public void ToolsCall_Search_IncludesTruncatedAndTotalEnvelope()
    {
        InsertIndexedFile("src/search-a.cs", "csharp", "public class SearchA { public void Target() { } }");
        InsertIndexedFile("src/search-b.cs", "csharp", "public class SearchB { public void Target() { } }");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Target","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.Null(structured["total"]);
        Assert.Single(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_References_IncludesTruncatedAndTotalEnvelope()
    {
        InsertIndexedFile(
            "src/reference-envelope.cs",
            "csharp",
            """
            public class CallerOne { public void Run(App app) { app.Run(); } }
            public class CallerTwo { public void Run(App app) { app.Run(); } }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"csharp","limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>());
        Assert.True(structured["total"]!.GetValue<int>() >= 2);
        Assert.Single(structured["results"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_References_CountOnly_OmitsRowsAndReturnsHistogram()
    {
        InsertIndexedFile(
            "src/count-only.cs",
            "csharp",
            """
            public class CountOnlyCaller { public void Run(App app) { app.Run(); app.Run(); } }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Run","lang":"csharp","countOnly":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["count_only"]!.GetValue<bool>());
        Assert.True(structured["count"]!.GetValue<int>() >= 2);
        Assert.Empty(structured["results"]!.AsArray());
        Assert.NotEmpty(structured["top_files"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_CallersAndCallees_CountOnly_OmitsRowsAndReturnsHistogram()
    {
        InsertIndexedFile("src/count-only-graph.py", "python", "def login(user):\n    return Run(user)\n");

        var callersRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"callers","arguments":{"query":"Run","lang":"python","countOnly":true}}}""")!;
        var callersResponse = _server.HandleMessage(callersRequest)!;
        var callersStructured = callersResponse["result"]!["structuredContent"]!;
        Assert.True(callersStructured["count_only"]!.GetValue<bool>());
        Assert.True(callersStructured["count"]!.GetValue<int>() >= 1);
        Assert.Empty(callersStructured["results"]!.AsArray());
        Assert.NotEmpty(callersStructured["top_files"]!.AsArray());

        var calleesRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callees","arguments":{"query":"login","lang":"python","countOnly":true}}}""")!;
        var calleesResponse = _server.HandleMessage(calleesRequest)!;
        var calleesStructured = calleesResponse["result"]!["structuredContent"]!;
        Assert.True(calleesStructured["count_only"]!.GetValue<bool>());
        Assert.True(calleesStructured["count"]!.GetValue<int>() >= 1);
        Assert.Empty(calleesStructured["results"]!.AsArray());
        Assert.NotEmpty(calleesStructured["top_files"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_ImpactAnalysis_CountOnly_OmitsCallerRows()
    {
        InsertIndexedFile(
            "src/impact-count-only.cs",
            "csharp",
            """
            public class ImpactCountOnlyCaller { public void Hit(App app) { app.Run(); } }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"Run","lang":"csharp","countOnly":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["count_only"]!.GetValue<bool>());
        Assert.Empty(structured["results"]!.AsArray());
        Assert.NotNull(structured["top_files"]);
    }

    [Fact]
    public void ToolsCall_ResponseOverByteLimit_ReturnsStructuredError()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_RESPONSE_MAX_BYTES", "256");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32603, response["error"]!["code"]!.GetValue<int>());
        Assert.Equal("response_too_large", response["error"]!["data"]!["reason"]!.GetValue<string>());
        Assert.Equal(256, response["error"]!["data"]!["limit_bytes"]!.GetValue<int>());
        Assert.True(response["error"]!["data"]!["actual_bytes"]!.GetValue<int>() > 256);
        Assert.False(response["error"]!["data"]!["actual_bytes_exact"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_InvalidResponseByteLimit_RedactsSecretLookingEnvValue_Issue3403()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        const string secret = "0123456789abcdef0123456789abcdef";
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", $"token={secret}");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!;

        var stderr = ConsoleCapture.CaptureError(() => _server.HandleMessage(request));

        Assert.Contains("CDIDX_MCP_RESPONSE_MAX_BYTES='token=<redacted>'", stderr);
        Assert.DoesNotContain(secret, stderr);
    }

    [Fact]
    public void ToolsCall_InvalidResponseByteLimit_RedactsPathAndUrlEnvValue_Issue3403()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_RESPONSE_MAX_BYTES");
        const string path = "/Users/example/private/project";
        const string url = "https://example.test/private/project/config.json";
        const string queryUrl = "https://example.test?query=user-content";
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", $"path={path} url={url} query={queryUrl}");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!;

        var stderr = ConsoleCapture.CaptureError(() => _server.HandleMessage(request));

        Assert.Contains("CDIDX_MCP_RESPONSE_MAX_BYTES='path=<redacted> url=https://example.test<redacted> query=https://example.test<redacted>'", stderr);
        Assert.DoesNotContain(path, stderr);
        Assert.DoesNotContain("/private/project/config.json", stderr);
        Assert.DoesNotContain("query=user-content", stderr);
    }

    [Fact]
    public void ToolsCall_Status_ReportsResponseByteLimitCaps()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_MCP_RESPONSE_MAX_BYTES",
            "CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_RESPONSE_MAX_BYTES", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        env.Set("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var mcp = response["result"]!["structuredContent"]!["mcp"]!;
        var limits = mcp["limits"]!;
        Assert.Equal(McpServer.MaxConfiguredResponseBytes, limits["max_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxConfiguredResponseBytes, limits["max_configured_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, limits["batch_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, limits["max_batch_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, limits["batch_query_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, limits["batch_query_max_response_bytes"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchQuerySize, limits["batch_query_max_queries"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxBatchRequestCount, limits["json_rpc_batch_max_requests"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToolsCall_StatusUpdateCheck_UsesRequestCancellationToken_Issue3658()
    {
        var previous = McpServer.StatusUpdateCheckForTesting;
        using var cts = new CancellationTokenSource();
        var observedToken = CancellationToken.None;
        var observedCallerCancellation = false;
        McpServer.StatusUpdateCheckForTesting = (version, token) =>
        {
            observedToken = token;
            cts.Cancel();
            observedCallerCancellation = token.IsCancellationRequested;
            return UpdateChecker.CreateDisabledResult(version);
        };
        try
        {
            using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
            var transport = new QueuedFrameTransport(
                """{"jsonrpc":"2.0","id":3658,"method":"tools/call","params":{"name":"status","arguments":{"updateCheck":true}}}""");

            await server.RunAsync(transport, cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(observedToken.CanBeCanceled);
            Assert.True(observedCallerCancellation);
        }
        finally
        {
            McpServer.StatusUpdateCheckForTesting = previous;
        }
    }

    [Fact]
    public void ToolsCall_References_ClampsTooLargeOffset_Issue3436()
    {
        InsertIndexedFile(
            "src/offset-clamp.cs",
            "csharp",
            """
            public class OffsetClampCaller { public void Hit(App app) { app.Run(); } }
            """);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "references",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["lang"] = "csharp",
                    ["offset"] = McpServer.MaxMcpPaginationOffset + 1,
                    ["limit"] = 1,
                },
            },
        };
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(McpServer.MaxMcpPaginationOffset, structured["offset"]!.GetValue<int>());
        Assert.True(structured["total"]!.GetValue<int>() > 0);
        var warning = Assert.Single(structured["warnings"]!.AsArray());
        Assert.Contains("offset was clamped", warning!.GetValue<string>(), StringComparison.Ordinal);
        var adjustment = Assert.Single(structured["argument_adjustments"]!.AsArray());
        Assert.Equal("offset", adjustment!["argument"]!.GetValue<string>());
        Assert.Equal("clamped", adjustment["action"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset + 1, adjustment["requested"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, adjustment["effective"]!.GetValue<int>());
        Assert.Equal(0, adjustment["minimum"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpPaginationOffset, adjustment["maximum"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Search_ReportsClampedLimitAndSnippetLines_Issue3436()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "Run",
                    ["limit"] = 999,
                    ["snippetLines"] = 999,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(SearchSnippetFormatter.MaxSnippetLines, structured["snippetLines"]!.GetValue<int>());
        var warnings = structured["warnings"]!.AsArray().Select(warning => warning!.GetValue<string>()).ToArray();
        Assert.Contains(warnings, warning => warning.Contains("limit was clamped", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("snippetLines was clamped", StringComparison.Ordinal));
        var adjustments = structured["argument_adjustments"]!.AsArray();
        var limit = adjustments.Single(adjustment => adjustment!["argument"]!.GetValue<string>() == "limit")!;
        Assert.Equal("clamped", limit["action"]!.GetValue<string>());
        Assert.Equal(999, limit["requested"]!.GetValue<int>());
        Assert.Equal(200, limit["effective"]!.GetValue<int>());
        var snippetLines = adjustments.Single(adjustment => adjustment!["argument"]!.GetValue<string>() == "snippetLines")!;
        Assert.Equal("clamped", snippetLines["action"]!.GetValue<string>());
        Assert.Equal(999, snippetLines["requested"]!.GetValue<int>());
        Assert.Equal(SearchSnippetFormatter.MaxSnippetLines, snippetLines["effective"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Map_ReportsClampedDepth_Issue3436()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "map",
                ["arguments"] = new JsonObject
                {
                    ["depth"] = McpServer.MaxMcpMapDepth + 1,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(McpServer.MaxMcpMapDepth, structured["depth"]!.GetValue<int>());
        var adjustment = Assert.Single(structured["argument_adjustments"]!.AsArray());
        Assert.Equal("depth", adjustment!["argument"]!.GetValue<string>());
        Assert.Equal("clamped", adjustment["action"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxMcpMapDepth + 1, adjustment["requested"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpMapDepth, adjustment["effective"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Map_DepthReaggregatesPathScopedModules_Issue4573()
    {
        InsertIndexedFile("src/issue4573/App.cs", "csharp", "class App4573 {}\n");
        InsertIndexedFile("src/issue4573/Worker.cs", "csharp", "class Worker4573 {}\n");
        InsertIndexedFile("tests/issue4573/AppTests.cs", "csharp", "class AppTests4573 {}\n");
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"map","arguments":{"path":"src/issue4573/**","sections":["tree"],"depth":1,"limit":10}}}""")!;

        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var module = Assert.Single(structured["modules"]!.AsArray());

        Assert.Equal(2, structured["fileCount"]!.GetValue<int>());
        Assert.Equal("src", module!["module"]!.GetValue<string>());
        Assert.Equal(2, module["files"]!.GetValue<int>());
        Assert.Equal(1, structured["depth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Map_HeadMetadataUsesMapSnapshot_Issue4573()
    {
        var initialHead = new string('a', 40);
        var nextHead = new string('b', 40);
        var initialTimestamp = DateTimeOffset.Parse("2026-07-17T01:02:03Z", CultureInfo.InvariantCulture);
        var nextTimestamp = initialTimestamp.AddMinutes(1);
        var writer = new DbWriter(_db.Connection);
        writer.SetMetaValues(
            (DbContext.IndexedHeadShaMetaKey, initialHead),
            (DbContext.IndexedHeadTimestampMetaKey, initialTimestamp.ToString("O", CultureInfo.InvariantCulture)));
        RepoMapBuilder.HeadMetadataCapturedForTesting.Value = () => writer.SetMetaValues(
            (DbContext.IndexedHeadShaMetaKey, nextHead),
            (DbContext.IndexedHeadTimestampMetaKey, nextTimestamp.ToString("O", CultureInfo.InvariantCulture)));
        try
        {
            var request = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"map","arguments":{"sections":["summary"]}}}""")!;

            var response = _server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(initialHead, structured["indexed_head_sha"]!.GetValue<string>());
            Assert.Equal(initialTimestamp, structured["indexed_head_timestamp"]!.GetValue<DateTimeOffset>());
            Assert.Equal(initialHead, structured["head_freshness"]!["indexed_head"]!.GetValue<string>());
        }
        finally
        {
            RepoMapBuilder.HeadMetadataCapturedForTesting.Value = null;
        }
    }

    [Fact]
    public void ToolsCall_Map_ReportsIgnoredNegativeDepth_Issue3436()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "map",
                ["arguments"] = new JsonObject
                {
                    ["depth"] = -1,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Null(structured["depth"]);
        var adjustment = Assert.Single(structured["argument_adjustments"]!.AsArray());
        Assert.Equal("depth", adjustment!["argument"]!.GetValue<string>());
        Assert.Equal("ignored", adjustment["action"]!.GetValue<string>());
        Assert.Equal(-1, adjustment["requested"]!.GetValue<int>());
        Assert.Null(adjustment["effective"]);
    }

    [Fact]
    public void ToolsCall_Status_ReportsPaginationOffsetCap()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var limits = response["result"]!["structuredContent"]!["mcp"]!["limits"]!;
        Assert.Equal(McpServer.MaxMcpPaginationOffset, limits["max_pagination_offset"]!.GetValue<int>());
    }

    [ExternalProcessFact]
    public void ToolsCall_StatusCompact_ReportsAcceptedExtensionTrustOverrides_3735()
    {
        string? windowsGitDirectory = null;
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                PostExtractionHookRunner.HooksDirectoryEnvironmentVariable,
                ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable,
                GitHelper.GitExecutableEnvironmentVariable);
            using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
                "cdidx_mcp_status_extensions_3735");
            try
            {
                var hooksDir = Path.Combine(extensionProject.Root, "hooks");
                windowsGitDirectory = OperatingSystem.IsWindows()
                    ? TestProjectHelper.CreateTrustedWindowsGitDirectory("cdidx_mcp_status_git_3735")
                    : null;
                var gitPath = Path.Combine(
                    windowsGitDirectory ?? _projectRoot,
                    OperatingSystem.IsWindows() ? "git.exe" : "git");
                Directory.CreateDirectory(hooksDir);
                File.WriteAllText(
                    gitPath,
                    OperatingSystem.IsWindows()
                        ? "not a portable executable"
                        : "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo 'git version 2.0.0'; exit 0; fi\nexit 1\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        gitPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "on");
                env.Set(GitHelper.GitExecutableEnvironmentVariable, gitPath);

                var response = _server.HandleMessage(JsonNode.Parse(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{"format":"compact"}}}""")!)!;

                var trustOverrides = response["result"]!["structuredContent"]!["trust_overrides"]!.AsArray();
                Assert.Equal(OperatingSystem.IsWindows() ? 2 : 3, trustOverrides.Count);
                Assert.Contains(
                    trustOverrides,
                    item => item?["kind"]?.GetValue<string>() == "workspace_plugin_directory"
                            && item["environment_variable"]!.GetValue<string>() == ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable
                            && item["value"]!.GetValue<string>() == "on");
                var hookOverride = Assert.Single(
                    trustOverrides,
                    item => item?["kind"]?.GetValue<string>() == "hook_directory_override");
                Assert.Equal(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hookOverride!["environment_variable"]!.GetValue<string>());
                Assert.EndsWith("hooks", hookOverride["path"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.DoesNotContain(_projectRoot, hookOverride["path"]!.GetValue<string>(), StringComparison.Ordinal);

                var gitExecutable = response["result"]!["structuredContent"]!["git_executable"]!;
                Assert.Equal("environment_override", gitExecutable["source"]!.GetValue<string>());
                if (OperatingSystem.IsWindows())
                {
                    Assert.False(gitExecutable["accepted"]!.GetValue<bool>());
                    Assert.Equal("invalid_executable_format", gitExecutable["reason"]!.GetValue<string>());
                    Assert.DoesNotContain(trustOverrides, item => item?["kind"]?.GetValue<string>() == "git_executable");
                }
                else
                {
                    Assert.True(gitExecutable["accepted"]!.GetValue<bool>());
                    Assert.Equal("accepted", gitExecutable["reason"]!.GetValue<string>());
                    Assert.Equal("current_user", gitExecutable["owner"]!.GetValue<string>());
                    Assert.True(gitExecutable["owner_trusted"]!.GetValue<bool>());
                    Assert.True(gitExecutable["ancestor_directories_trusted"]!.GetValue<bool>());
                    var gitOverride = Assert.Single(
                        trustOverrides,
                        item => item?["kind"]?.GetValue<string>() == "git_executable");
                    Assert.Equal(GitHelper.GitExecutableEnvironmentVariable, gitOverride!["environment_variable"]!.GetValue<string>());
                }
            }
            finally
            {
                if (windowsGitDirectory != null)
                    TestProjectHelper.DeleteDirectory(windowsGitDirectory);
            }
        }
    }

    [Fact]
    public void ToolsCall_Status_ReportsKeepAliveIntervalBounds()
    {
        var response = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var limits = response["result"]!["structuredContent"]!["mcp"]!["limits"]!;
        Assert.Equal(McpServer.MinKeepAliveIntervalSeconds, limits["keep_alive_min_interval_s"]!.GetValue<double>());
        Assert.Equal(McpServer.MaxKeepAliveIntervalSeconds, limits["keep_alive_max_interval_s"]!.GetValue<double>());
    }

    [Fact]
    public void ToolsCall_Status_ReportsEffectiveRateLimitCaps()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_MCP_RATE_LIMIT_RPS",
            "CDIDX_MCP_RATE_LIMIT_BURST");
        env.Set("CDIDX_MCP_RATE_LIMIT_RPS", "1000000");
        env.Set("CDIDX_MCP_RATE_LIMIT_BURST", "1000000");

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var mcp = response["result"]!["structuredContent"]!["mcp"]!;
        var limits = mcp["limits"]!;
        Assert.Equal(RateLimiterOptions.MaxRefillTokensPerSecond, limits["rate_limit_max_rps"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.MaxBurstCapacity, limits["rate_limit_max_burst"]!.GetValue<double>());

        var rateLimit = mcp["rate_limit"]!;
        Assert.True(rateLimit["enabled"]!.GetValue<bool>());
        Assert.Equal(RateLimiterOptions.MaxRefillTokensPerSecond, rateLimit["rps"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.MaxBurstCapacity, rateLimit["burst"]!.GetValue<double>());
        // A canonical direct call consumes the fixed caller-wide pre-validation bucket and
        // its secondary per-tool bucket (#4547).
        // canonical な direct call は固定 caller-wide pre-validation bucket と secondary
        // per-tool bucket の両方を消費する（#4547）。
        Assert.Equal(2, rateLimit["bucket_count"]!.GetValue<int>());
        Assert.True(rateLimit["bucket_idle_ttl_seconds"]!.GetValue<double>() > 0);
        Assert.True(rateLimit["next_prune_in_ms"]!.GetValue<long>() >= 0);
        Assert.True(rateLimit["last_prune_age_ms"]!.GetValue<long>() >= 0);
        Assert.Equal(0, rateLimit["last_pruned_bucket_count"]!.GetValue<int>());

        var requestTimeouts = mcp["request_timeouts"]!;
        Assert.Equal(0L, requestTimeouts["isolated_action_draining_count"]!.GetValue<long>());
        Assert.Equal(0L, requestTimeouts["isolated_action_drained_count"]!.GetValue<long>());
        Assert.True(requestTimeouts["timeout_ms"]!.GetValue<long>() > 0);
    }

    [Fact]
    public void ToolsCall_Status_RateLimitEnvironmentInventoryStaysAligned_Issue4177()
    {
        var rateLimitInventory = EnvironmentVariableInventory.Items
            .Where(item => item.Name is RateLimiterOptions.RpsEnvVar or RateLimiterOptions.BurstEnvVar or RateLimiterOptions.BucketIdleSecondsEnvVar)
            .ToDictionary(item => item.Name, StringComparer.Ordinal);

        foreach (var name in new[] { RateLimiterOptions.RpsEnvVar, RateLimiterOptions.BurstEnvVar, RateLimiterOptions.BucketIdleSecondsEnvVar })
        {
            var item = rateLimitInventory[name];
            Assert.Equal(EnvironmentVariableInventory.DomainConfig, item.Domain);
            Assert.Equal("mcp", item.Category);
            Assert.Equal(EnvironmentVariableInventory.SensitivityPublic, item.Sensitivity);
            Assert.Equal("performance", item.Policy);
            Assert.Equal("yes", item.ConfigFileSupported);
        }

        using var env = EnvironmentVariableScope.Capture(
            RateLimiterOptions.RpsEnvVar,
            RateLimiterOptions.BurstEnvVar,
            RateLimiterOptions.BucketIdleSecondsEnvVar);
        env.Set(RateLimiterOptions.RpsEnvVar, "2.5");
        env.Set(RateLimiterOptions.BurstEnvVar, "4");
        env.Set(RateLimiterOptions.BucketIdleSecondsEnvVar, "30");

        using var server = new McpServer(_dbPath, "1.0", dbPathExplicit: true);
        var response = server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        var mcp = response["result"]!["structuredContent"]!["mcp"]!;
        var limits = mcp["limits"]!;
        Assert.Equal(RateLimiterOptions.MaxRefillTokensPerSecond, limits["rate_limit_max_rps"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.MaxBurstCapacity, limits["rate_limit_max_burst"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.DefaultMaxBucketCount, limits["rate_limit_max_buckets"]!.GetValue<int>());

        var rateLimit = mcp["rate_limit"]!;
        Assert.True(rateLimit["enabled"]!.GetValue<bool>());
        Assert.Equal(2.5, rateLimit["rps"]!.GetValue<double>());
        Assert.Equal(4.0, rateLimit["burst"]!.GetValue<double>());
        Assert.Equal(30.0, rateLimit["bucket_idle_ttl_seconds"]!.GetValue<double>());
        Assert.Equal(RateLimiterOptions.DefaultMaxBucketCount, rateLimit["bucket_limit"]!.GetValue<int>());
        Assert.Equal(0, rateLimit["bucket_limit_rejection_count"]!.GetValue<int>());
        Assert.True(rateLimit["next_prune_in_ms"]!.GetValue<long>() >= 0);
    }

    [Fact]
    public void ToolsCall_Search_RejectsFalseExactNameAlias()
    {
        InsertIndexedFile("src/search_false_alias.cs", "csharp", "void Run() { }\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Run","exactName":false}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Unknown argument 'exactName'", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_ReturnsResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 symbol", text);
        Assert.Equal("App", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("class", response["result"]!["structuredContent"]!["results"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("public class App { public void Run() { } }", response["result"]!["structuredContent"]!["results"]![0]!["signature"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_ExactNameAliasMatchesBackwardCompatibleExact()
    {
        InsertIndexedFile("src/exact.cs", "csharp", "public class ExactApp { public void Run() { } public void RunAsync() { } }");

        var exactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exact":true}}}""")!;
        var aliasRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactName":true}}}""")!;

        var exactResponse = _server.HandleMessage(exactRequest)!;
        var aliasResponse = _server.HandleMessage(aliasRequest)!;

        Assert.Equal(
            exactResponse["result"]!["structuredContent"]!.ToJsonString(),
            aliasResponse["result"]!["structuredContent"]!.ToJsonString());
    }

    [Fact]
    public void ToolsCall_Symbols_RejectsExactSubstringAlias()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactSubstring":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Unknown argument 'exactSubstring'", text);
    }

    [Fact]
    public void ToolsCall_Symbols_RejectsFalseExactSubstringAlias()
    {
        InsertIndexedFile("src/symbol_false_alias.cs", "csharp", "public class ExactApp { public void Run() { } }\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"Run","exactSubstring":false}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("Unknown argument 'exactSubstring'", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("search", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("search", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("definition", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("definition", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("references", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("references", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("callers", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("callers", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("callees", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("callees", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("symbols", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("symbols", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    [InlineData("analyze_symbol", """{"query":"Run","exact":true,"exactName":true}""")]
    [InlineData("analyze_symbol", """{"query":"Run","exact":true,"exactSubstring":true}""")]
    public void ToolsCall_ExactAliases_RejectsCombinedFlags(string toolName, string argumentsJson)
    {
        var request = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName + "\",\"arguments\":" + argumentsJson + "}}")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(
            text.Contains("Pass only one of 'exact', 'exactSubstring', 'tokenBoundary', 'exactName'.", StringComparison.Ordinal)
            || text.Contains("Pass only one of 'exact', 'exactSubstring', 'exactName'.", StringComparison.Ordinal)
            || text.Contains("Unknown argument", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"names":[]}""", "no usable entries")]
    [InlineData("""{"names":[""]}""", "no usable entries")]
    [InlineData("""{"names":["   "]}""", "no usable entries")]
    public void ToolsCall_Symbols_RejectsMalformedOrEmptyNames(string argsJson, string expectedMessageFragment)
    {
        // Malformed or empty `names` must fail closed — falling through to an unfiltered full-symbol
        // dump would mislead downstream automation about candidate resolution.
        // 不正・空の `names` は必ずエラーで弾くこと。全件検索に化けると下流の判断を狂わせる。
        var request = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"symbols\",\"arguments\":" + argsJson + "}}")!;
        var response = _server.HandleMessage(request)!;
        Assert.True(response["result"]!["isError"]!.GetValue<bool>(), $"expected isError for arguments {argsJson}");
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(expectedMessageFragment, text);
    }

    [Fact]
    public void ToolsCall_Symbols_RejectsScalarNamesAsInvalidParams_Issue3538()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"names":""}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Null(response["result"]);
        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains("Invalid type for argument 'names'", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var data = error["data"]!;
        Assert.Equal("names", data["parameter"]!.GetValue<string>());
        Assert.Equal("array", data["expected"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Symbols_FilterByKind()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"kind":"function"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        Assert.Equal("Run", results[0]!["name"]!.GetValue<string>());
        Assert.Equal("function", results[0]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Definition_ReturnsDefinitionContent()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"Run","includeBody":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 definition", text);
        Assert.Equal("Run", response["result"]!["structuredContent"]!["results"]![0]!["name"]!.GetValue<string>());
        Assert.Contains("public void Run()", response["result"]!["structuredContent"]!["results"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_DefinitionAndGraphToolsShareStaleContractFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_definition_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            DowngradeSqlGraphContractRows(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var definitionRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"fn_Target","lang":"sql","exact":true}}}""")!;
            var definitionResponse = server.HandleMessage(definitionRequest)!;
            var definitionStructured = definitionResponse["result"]!["structuredContent"]!;

            Assert.Equal(1, definitionStructured["count"]!.GetValue<int>());
            Assert.Null(definitionStructured["sql_graph_contract_ready"]);
            Assert.Null(definitionStructured["degraded"]);
            Assert.Null(definitionStructured["sql_graph_contract_degraded_reason"]);

            var callersRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"callers","arguments":{"query":"dbo.fn_Target","lang":"sql","exact":true}}}""")!;
            var callersResponse = server.HandleMessage(callersRequest)!;
            var callersStructured = callersResponse["result"]!["structuredContent"]!;

            Assert.False(callersStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.NotNull(callersStructured["sql_graph_contract_degraded_reason"]);

            var impactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"impact_analysis","arguments":{"query":"fn_Target","lang":"sql"}}}""")!;
            var impactResponse = server.HandleMessage(impactRequest)!;
            var impactStructured = impactResponse["result"]!["structuredContent"]!;

            Assert.Equal(1, impactStructured["count"]!.GetValue<int>());
            Assert.False(impactStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", impactStructured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_DepsAndHotspotsShareDegradedZeroResultFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_deps_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());

            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var hotspotsStructured = hotspotsResponse["result"]!["structuredContent"]!;

            Assert.Equal(0, hotspotsStructured["count"]!.GetValue<int>());
            Assert.False(hotspotsStructured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", hotspotsStructured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_DepsCyclesUsesGraphBudgetBeyondDisplayLimit_Issue3185()
    {
        var writer = new DbWriter(_db.Connection);
        var highTargetId = InsertDependencyFile(writer, "src/HighTarget.cs");
        var highCallerId = InsertDependencyFile(writer, "src/HighCaller.cs");
        var cycleAId = InsertDependencyFile(writer, "src/CycleA.cs");
        var cycleBId = InsertDependencyFile(writer, "src/CycleB.cs");
        var cycleCId = InsertDependencyFile(writer, "src/CycleC.cs");
        var cycleDId = InsertDependencyFile(writer, "src/CycleD.cs");
        InsertDependencySymbols(writer, highTargetId, ["HighTarget"]);
        InsertDependencyReferences(writer, highCallerId, Enumerable.Repeat("HighTarget", 5).ToArray());
        InsertDependencySymbols(writer, cycleAId, ["CycleA"]);
        InsertDependencyReferences(writer, cycleAId, ["CycleB"]);
        InsertDependencySymbols(writer, cycleBId, ["CycleB"]);
        InsertDependencyReferences(writer, cycleBId, ["CycleA"]);
        InsertDependencySymbols(writer, cycleCId, ["CycleC"]);
        InsertDependencyReferences(writer, cycleCId, ["CycleD"]);
        InsertDependencySymbols(writer, cycleDId, ["CycleD"]);
        InsertDependencyReferences(writer, cycleDId, ["CycleC"]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"cycles":true,"limit":1,"lang":"csharp"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var cycle = Assert.Single(structured["cycles"]!.AsArray());
        var nodes = cycle!["nodes"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        var nextStepFlags = structured["next_step_flags"]!.AsArray()
            .Select(flag => flag!.GetValue<string>())
            .ToArray();

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(2, nodes.Length);
        Assert.All(nodes, node => Assert.StartsWith("src/Cycle", node));
        Assert.Equal("partial_display_limit", structured["cycle_result_scope"]!.GetValue<string>());
        Assert.Contains("limit=2", nextStepFlags);
        Assert.Contains("path=<narrower-glob>", nextStepFlags);
        Assert.DoesNotContain(nextStepFlags, flag => flag.StartsWith("--", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolsCall_Deps_JsonGraph_ReturnsGraphPayload()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_deps_json_graph");
        try
        {
            var dbPath = CreateSqlGraphContractFixtureDb(projectRoot);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"format":"json-graph","lang":"sql"}}}""")!;
            var response = server.HandleMessage(request)!;
            var graph = response["result"]!["structuredContent"]!["graph"]!;

            Assert.NotEmpty(graph["nodes"]!.AsArray());
            Assert.NotEmpty(graph["edges"]!.AsArray());
            Assert.NotNull(graph["edges"]![0]!["reference_count"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Deps_IncludeGeneratedControlsGeneratedEdges_Issue3544()
    {
        var writer = new DbWriter(_db.Connection);
        var targetId = InsertDependencyFile(writer, "src/DependencyTarget.cs");
        var generatedSourceId = InsertDependencyFile(writer, "src/GeneratedSource.g.cs", generated: true);
        InsertDependencySymbols(writer, targetId, ["DependencyTarget"]);
        InsertDependencyReferences(writer, generatedSourceId, ["DependencyTarget"]);

        var defaultRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"deps","arguments":{"path":"src/GeneratedSource.g.cs","lang":"csharp"}}}""")!;
        var defaultResponse = _server.HandleMessage(defaultRequest)!;
        var defaultStructured = defaultResponse["result"]!["structuredContent"]!;

        Assert.Equal(0, defaultStructured["count"]!.GetValue<int>());
        Assert.False(defaultStructured["includeGenerated"]!.GetValue<bool>());
        Assert.True(defaultStructured["generated_code_filter_supported"]!.GetValue<bool>());
        Assert.Equal("source_and_target_files", defaultStructured["generated_code_scope"]!.GetValue<string>());

        var includeRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"deps","arguments":{"path":"src/GeneratedSource.g.cs","lang":"csharp","includeGenerated":true}}}""")!;
        var includeResponse = _server.HandleMessage(includeRequest)!;
        var includeStructured = includeResponse["result"]!["structuredContent"]!;
        var edge = Assert.Single(includeStructured["edges"]!.AsArray());

        Assert.Equal(1, includeStructured["count"]!.GetValue<int>());
        Assert.True(includeStructured["includeGenerated"]!.GetValue<bool>());
        Assert.Equal("src/GeneratedSource.g.cs", edge!["sourcePath"]!.GetValue<string>());
        Assert.Equal("src/DependencyTarget.cs", edge["targetPath"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedAndHotspotsShareCleanZeroResultFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_unused_zero_sql_graph_contract");
        try
        {
            var dbPath = CreateSqlGraphContractZeroResultFixtureDb(projectRoot);
            DowngradeSqlGraphContractVersion(dbPath);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"kind":"interface"}}}""")!;
            var response = server.HandleMessage(request)!;
            var structured = response["result"]!["structuredContent"]!;

            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.Null(structured["sql_graph_contract_ready"]);
            Assert.Null(structured["sql_graph_contract_degraded_reason"]);
            Assert.Null(structured["degraded"]);

            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"kind":"class"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var hotspotsStructured = hotspotsResponse["result"]!["structuredContent"]!;

            Assert.Equal(0, hotspotsStructured["count"]!.GetValue<int>());
            Assert.Null(hotspotsStructured["sql_graph_contract_ready"]);
            Assert.Null(hotspotsStructured["sql_graph_contract_degraded_reason"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("definition", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("symbols", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("references", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("callers", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("callees", """{"query":"nonexistent_xyz_123"}""")]
    [InlineData("files", """{"query":"nonexistent_xyz_123","lang":"nonexistent"}""")]
    public void ToolsCall_ZeroResults_IncludesFreshnessHint(string toolName, string argsJson)
    {
        var request = JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argsJson}}}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0, $"{toolName} should include indexed_file_count");
        Assert.True(structured["freshness_available"]!.GetValue<bool>());
        Assert.NotNull(structured["indexed_at"]);
    }

    [Fact]
    public void ToolsCall_Files_NoResults_OnLegacyReadOnlyDb_EmitsFreshnessDegradedSignal()
    {
        var dbPath = CreateLegacyDbWithoutIndexedAt();
        try
        {
            using var readOnlyServer = new McpServer(new Uri(dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files","arguments":{"query":"nonexistent_xyz_123"}}}""")!;
            var response = readOnlyServer.HandleMessage(request)!;
            using var document = JsonDocument.Parse(response.ToJsonString());
            var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");

            Assert.Equal(0, structured.GetProperty("count").GetInt32());
            Assert.Equal(0, structured.GetProperty("results").GetArrayLength());
            Assert.Equal(1, structured.GetProperty("indexed_file_count").GetInt64());
            Assert.False(structured.GetProperty("freshness_available").GetBoolean());
            Assert.Contains("files.indexed_at column missing", structured.GetProperty("freshness_degraded_reason").GetString());
            Assert.Equal(JsonValueKind.Null, structured.GetProperty("indexed_at").ValueKind);
        }
        finally
        {
            DeleteFileRobust(dbPath);
        }
    }

    [Theory]
    [InlineData("search", """{"query":"nonexistent_xyz_123"}""", "results")]
    [InlineData("files", """{"query":"nonexistent_xyz_123"}""", "results")]
    public void ToolsCall_ZeroResults_EmptyIndexIncludesNullFreshnessTimestamp(string toolName, string argsJson, string resultsKey)
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_empty");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

                var request = JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argsJson}}}}}""")!;
                var response = server.HandleMessage(request)!;

                var structured = response["result"]!["structuredContent"]!;
                Assert.Equal(0, structured["count"]!.GetValue<int>());
                Assert.Equal(0, structured["indexed_file_count"]!.GetValue<long>());
                Assert.True(structured.AsObject().ContainsKey("indexed_at"));
                Assert.Null(structured["indexed_at"]);
                Assert.Empty(structured[resultsKey]!.AsArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Theory]
    [InlineData("definition", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("symbols", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("references", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("callers", """{"query":"Ru","exact":true}""", "Run")]
    [InlineData("callees", """{"query":"Runn","exact":true}""", "Runner")]
    public void ToolsCall_ExactZeroResults_IncludeExactZeroHint(string toolName, string argsJson, string expectedSampleName)
    {
        InsertIndexedFile(
            "src/extra.cs",
            "csharp",
            """
            public class Extra
            {
                public void Runner()
                {
                    Run();
                }

                public void Run() { }
            }
            """);

        var request = JsonNode.Parse($$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argsJson}}}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.NotNull(structured["exact_zero_hint"]);
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0);
        Assert.True(structured["exact_zero_hint"]!["relaxed_count"]!.GetValue<int>() > 0);
        Assert.Contains(expectedSampleName, structured["exact_zero_hint"]!["sample_names"]!.AsArray().Select(node => node!?.GetValue<string>()));
        Assert.Equal("drop --exact or use the exact indexed name", structured["exact_zero_hint"]!["suggestion"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Definition_ExactZeroHint_RespectsRequestedLimitForRelaxedCount()
    {
        InsertIndexedFile(
            "src/extra_limit.cs",
            "csharp",
            """
            public class ExtraLimit
            {
                public void HandleRequest1() { }
                public void HandleRequest2() { }
                public void HandleRequest3() { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"Handle","exact":true,"limit":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["exact_zero_hint"]!["relaxed_count"]!.GetValue<int>());
        Assert.Single(structured["exact_zero_hint"]!["sample_names"]!.AsArray());
    }

    [Fact]
    public void ToolsCall_Symbols_MultiNameExactZeroHint_OmitsRelaxedCountButReturnsSamples()
    {
        InsertIndexedFile(
            "src/extra_multi.cs",
            "csharp",
            """
            public class ExtraMulti
            {
                public void AlphaWorker() { }
                public void BetaWorker() { }
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"names":["Alpha","Beta"],"exact":true,"limit":999}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.NotNull(structured["exact_zero_hint"]);
        Assert.Null(structured["exact_zero_hint"]!["relaxed_count"]);
        Assert.Contains("AlphaWorker", structured["exact_zero_hint"]!["sample_names"]!.AsArray().Select(node => node!?.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Files_ReturnsResults()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"files","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Found 1 file", text);
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["results"]![0]!["path"]!.GetValue<string>());
        Assert.Equal("csharp", response["result"]!["structuredContent"]!["results"]![0]!["lang"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["structuredContent"]!["results"]![0]!["modified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["results"]![0]!["indexedAt"]);
    }

    [Fact]
    public void ToolsCall_Excerpt_ReturnsExcerpt()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"src/app.cs","startLine":1,"endLine":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Excerpt returned", text);
        Assert.Equal("src/app.cs", response["result"]!["structuredContent"]!["path"]!.GetValue<string>());
        Assert.Contains("public class App", response["result"]!["structuredContent"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_ClampsLongSingleLineContent()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        var focusColumn = longLine.IndexOf("TARGET", StringComparison.Ordinal) + 1;
        InsertIndexedFile("dist/data.txt", "text", longLine);

        var request = JsonNode.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"excerpt\",\"arguments\":{{\"path\":\"dist/data.txt\",\"startLine\":1,\"endLine\":1,\"maxLineWidth\":96,\"focusColumn\":{focusColumn},\"focusLength\":6}}}}}}")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["contentTruncated"]!.GetValue<bool>());
        Assert.DoesNotContain(longLine, structured["content"]!.GetValue<string>());
        Assert.Contains("TARGET", structured["content"]!.GetValue<string>());
        Assert.True(structured["content"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
        var recovery = structured["contentRecovery"]!;
        Assert.Equal(1, recovery["startLine"]!.GetValue<int>());
        Assert.Equal(1, recovery["endLine"]!.GetValue<int>());
        Assert.Equal(
            ["cdidx", "excerpt", "dist/data.txt", "--db", Path.GetFullPath(_dbPath), "--start", "1", "--end", "1", "--max-line-width", "0", "--json"],
            recovery["argv"]!.AsArray().Select(argument => argument!.GetValue<string>()).ToArray());
        Assert.Equal(OperatingSystem.IsWindows() ? "powershell" : "posix-sh", recovery["commandShell"]!.GetValue<string>());
        Assert.True(recovery["commandDisplayOnly"]!.GetValue<bool>());
        var recoveryCommand = recovery["command"]!.GetValue<string>();
        Assert.Contains("cdidx excerpt dist/data.txt", recoveryCommand);
        Assert.Contains("--db", recoveryCommand);
        Assert.Contains(_dbPath, recoveryCommand);
        Assert.Contains("--start 1 --end 1 --max-line-width 0 --json", recoveryCommand);
    }

    [Fact]
    public void ToolsCall_Excerpt_ClampsLongSingleLineContentWithoutFocus()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/data-no-focus.txt", "text", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-no-focus.txt","startLine":1,"endLine":1,"maxLineWidth":96}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.True(structured["contentTruncated"]!.GetValue<bool>());
        Assert.DoesNotContain(longLine, structured["content"]!.GetValue<string>());
        Assert.True(structured["content"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusLineWithoutFocusColumnReturnsError()
    {
        InsertIndexedFile("dist/data-focus-error.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-error.txt","startLine":1,"endLine":1,"maxLineWidth":96,"focusLine":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusLine and focusLength require focusColumn", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusColumnZeroReturnsError()
    {
        InsertIndexedFile("dist/data-focus-zero.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-zero.txt","startLine":1,"endLine":1,"maxLineWidth":96,"focusColumn":0}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusColumn must be greater than or equal to 1", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusLengthZeroReturnsError()
    {
        InsertIndexedFile("dist/data-focus-length-zero.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-length-zero.txt","startLine":1,"endLine":1,"focusColumn":1,"focusLength":0}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusLength must be greater than or equal to 1", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = new string('a', 320) + "TARGET" + new string('b', 320);
        InsertIndexedFile("dist/data-max-width-zero.txt", "text", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-max-width-zero.txt","startLine":1,"endLine":1,"maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.False(structured["contentTruncated"]!.GetValue<bool>());
        Assert.Equal(longLine, structured["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_NegativeBeforeReturnsError()
    {
        InsertIndexedFile("dist/data-before-negative.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-before-negative.txt","startLine":1,"before":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("before must be in [0, 1000]", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_NegativeAfterReturnsError()
    {
        InsertIndexedFile("dist/data-after-negative.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-after-negative.txt","startLine":1,"after":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("after must be in [0, 1000]", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_BeforeAboveCapClampsContext()
    {
        InsertIndexedFile("dist/data-before-overflow.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-before-overflow.txt","startLine":1,"before":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1000, structured["before"]!.GetValue<int>());
        Assert.True(structured["contextTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Excerpt_AfterAboveCapClampsContext()
    {
        InsertIndexedFile("dist/data-after-overflow.txt", "text", "line one\nline two");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-after-overflow.txt","startLine":1,"after":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1000, structured["after"]!.GetValue<int>());
        Assert.True(structured["contextTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Excerpt_HugeEndLineDoesNotOverflow()
    {
        InsertIndexedFile("dist/data-endline-overflow.txt", "text", "line one\nline two\nline three");

        // endLine close to int.MaxValue + bounded `after` would overflow int addition and
        // wrap to a negative number before Math.Min clamped, masking the real file size.
        // Validate the handler returns a sane excerpt instead (#1528).
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-endline-overflow.txt","startLine":1,"endLine":2147483647,"after":1000}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("dist/data-endline-overflow.txt", structured["path"]!.GetValue<string>());
        Assert.Equal(1, structured["startLine"]!.GetValue<int>());
        Assert.Equal(3, structured["endLine"]!.GetValue<int>());
        Assert.Contains("line three", structured["content"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusLineOutsideReturnedRangeReturnsError()
    {
        InsertIndexedFile("dist/data-focus-range.txt", "text", "line one\nline two\nline three");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-range.txt","startLine":2,"endLine":2,"focusLine":999,"focusColumn":1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusLine (999) must be within the returned excerpt range (2-2)", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Excerpt_FocusColumnOutsideFocusedLineReturnsError()
    {
        InsertIndexedFile("dist/data-focus-column-range.txt", "text", new string('a', 320) + "TARGET" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"excerpt","arguments":{"path":"dist/data-focus-column-range.txt","startLine":1,"endLine":1,"focusColumn":9999,"maxLineWidth":40}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("focusColumn (9999) must be within the focused line length (646)", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_ReturnsLiteralMatchesWithContext()
    {
        InsertIndexedFile("src/Auth.cs", "csharp",
            """
            class Auth
            {
                void Guard() {}
                void Next() {}
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"guard","path":"src/Auth.cs","before":1,"after":1}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["fileCount"]!.GetValue<int>());
        Assert.Equal("src/Auth.cs", result["path"]!.GetValue<string>());
        Assert.Equal(3, result["line"]!.GetValue<int>());
        Assert.Equal(10, result["column"]!.GetValue<int>());
        Assert.Equal(2, result["startLine"]!.GetValue<int>());
        Assert.Equal(4, result["endLine"]!.GetValue<int>());
        Assert.Contains("void Guard()", result["snippet"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_ClampsLongSingleLineSnippet()
    {
        InsertIndexedFile("dist/search.txt", "text", new string('a', 320) + "target" + new string('b', 320));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search.txt","maxLineWidth":96,"exact":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.True(result["snippetTruncated"]!.GetValue<bool>());
        Assert.Contains("target", result["snippet"]!.GetValue<string>());
        Assert.True(result["snippet"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_FindInFile_MaxLineWidthZeroDisablesTruncation()
    {
        var longLine = new string('a', 320) + "target" + new string('b', 320);
        InsertIndexedFile("dist/search-max-width-zero.txt", "text", longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-max-width-zero.txt","maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.False(result["snippetTruncated"]!.GetValue<bool>());
        Assert.Equal(longLine, result["snippet"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_NegativeBeforeReturnsError()
    {
        InsertIndexedFile("dist/search-before-negative.txt", "text", "target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-before-negative.txt","before":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("before must be greater than or equal to 0", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_NegativeAfterReturnsError()
    {
        InsertIndexedFile("dist/search-after-negative.txt", "text", "target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-after-negative.txt","after":-1}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("after must be greater than or equal to 0", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_FindInFile_BeforeAfterAboveCapClampContext()
    {
        InsertIndexedFile("dist/search-context-overflow.txt", "text", "target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-context-overflow.txt","before":2147483647,"after":2147483647}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(1000, structured["before"]!.GetValue<int>());
        Assert.Equal(1000, structured["after"]!.GetValue<int>());
        Assert.True(structured["contextTruncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_FindInFile_SnippetLinesControlsMatchContext()
    {
        InsertIndexedFile("dist/search-snippet-lines.txt", "text", "line one\nline two\ntarget\nline four\nline five");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-snippet-lines.txt","snippetLines":5}}}""")!;
        var response = _server.HandleMessage(request)!;
        Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Equal(2, structured["before"]!.GetValue<int>());
        Assert.Equal(2, structured["after"]!.GetValue<int>());
        Assert.Equal(5, structured["snippetLines"]!.GetValue<int>());
        Assert.Equal(1, result["startLine"]!.GetValue<int>());
        Assert.Equal(5, result["endLine"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_FindInFile_FocusLineAndColumnRestrictMatch()
    {
        InsertIndexedFile("dist/search-focus.txt", "text", "target here\nno match\nother target");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"target","path":"dist/search-focus.txt","focusLine":3,"focusColumn":8}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(3, result["line"]!.GetValue<int>());
        Assert.Equal(7, result["column"]!.GetValue<int>());
        Assert.Equal(3, structured["focusLine"]!.GetValue<int>());
        Assert.Equal(8, structured["focusColumn"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_FindInFile_RegexMatchesAnchors()
    {
        InsertIndexedFile("dist/search-regex.txt", "text", "alpha\ntarget()\nnot target()");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"^target","path":"dist/search-regex.txt","regex":true}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var result = structured["results"]![0]!;

        Assert.True(structured["regex"]!.GetValue<bool>());
        Assert.Equal(1, structured["count"]!.GetValue<int>());
        Assert.Equal(2, result["line"]!.GetValue<int>());
        Assert.Equal(1, result["column"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ClampsBundledReferenceContext()
    {
        InsertIndexedFile("src/target.js", "javascript",
            """
            function target() {
              return true;
            }
            """);
        var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "dist/bundle.js",
            Lang = "javascript",
            Size = longLine.Length,
            Lines = 1,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks([
            new ChunkRecord { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = longLine }
        ]);
        writer.InsertReferences([
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "target",
                ReferenceKind = "call",
                Line = 1,
                Column = longLine.IndexOf("target", StringComparison.Ordinal) + 1,
                Context = longLine,
            }
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"target","lang":"javascript","maxLineWidth":96}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var firstReference = structured["references"]![0]!;

        Assert.True(firstReference["contextTruncated"]!.GetValue<bool>());
        Assert.Contains("target()", firstReference["context"]!.GetValue<string>());
        Assert.True(firstReference["context"]!.GetValue<string>().Length <= 96);
        Assert.Equal(96, structured["maxLineWidth"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_AnalyzeSymbol_ZeroMaxLineWidthDisablesTruncation()
    {
        var longLine = "const x = 0; " + new string('a', 320) + " target(); " + new string('b', 320);
        InsertIndexedFile("src/analyze-target.js", "javascript",
            "function target() { return true; }\n" + longLine);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"target","lang":"javascript","maxLineWidth":0}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var firstReference = structured["references"]![0]!;

        Assert.False(firstReference["contextTruncated"]!.GetValue<bool>());
        Assert.Contains("target()", firstReference["context"]!.GetValue<string>());
        Assert.DoesNotContain("...(+", firstReference["context"]!.GetValue<string>());
        Assert.True(firstReference["context"]!.GetValue<string>().Length > 512);
    }

    [Fact]
    public void ToolsCall_FindInFile_NoResultsIncludesFreshnessHints()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"missing","path":"src/app.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;

        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Equal(0, structured["fileCount"]!.GetValue<int>());
        Assert.True(structured["indexed_file_count"]!.GetValue<long>() > 0);
        Assert.True(structured["freshness_available"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_FindInFile_CountsEverySameLineOccurrence()
    {
        InsertIndexedFile("src/Sample.cs", "csharp", "alpha alpha alpha\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"alpha","path":"src/Sample.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();

        Assert.Equal(3, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["fileCount"]!.GetValue<int>());
        Assert.Equal([1, 7, 13], results.Select(node => node!["column"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void ToolsCall_FindInFile_CountsOverlappingOccurrences()
    {
        InsertIndexedFile("src/Sample.cs", "csharp", "// banana\n");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"find_in_file","arguments":{"query":"ana","path":"src/Sample.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;
        var structured = response["result"]!["structuredContent"]!;
        var results = structured["results"]!.AsArray();

        Assert.Equal(2, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["fileCount"]!.GetValue<int>());
        Assert.Equal([5, 7], results.Select(node => node!["column"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void ToolsCall_Status_ReturnsCounts()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Database stats returned", text);
        Assert.Equal(1, response["result"]!["structuredContent"]!["files"]!.GetValue<long>());
        Assert.Equal(1, response["result"]!["structuredContent"]!["chunks"]!.GetValue<long>());
        Assert.Equal(2, response["result"]!["structuredContent"]!["symbols"]!.GetValue<long>());
        Assert.Equal(0, response["result"]!["structuredContent"]!["references"]!.GetValue<long>());
        Assert.Contains("1 files, 2 symbols, 0 refs", response["result"]!["structuredContent"]!["summary"]!.GetValue<string>());
        Assert.Equal(1, response["result"]!["structuredContent"]!["symbolKinds"]!["function"]!.GetValue<long>());
        Assert.NotNull(response["result"]!["structuredContent"]!["indexedAt"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["latestModified"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["project_root"]);
        Assert.NotNull(response["result"]!["structuredContent"]!["hotspot_family_ready"]);
        Assert.Null(response["result"]!["structuredContent"]!["hotspotFamilyReady"]);
        Assert.False(response["result"]!["structuredContent"]!["foldReady"]!.GetValue<bool>());
        Assert.Equal(DegradationReasonCodes.MissingFoldBackfill, response["result"]!["structuredContent"]!["fold_ready_reason"]!.GetValue<string>());
        Assert.Contains("--exact falls back", response["result"]!["structuredContent"]!["degraded_reason"]!.GetValue<string>());
        Assert.Equal("cdidx backfill-fold", response["result"]!["structuredContent"]!["recommended_action"]!.GetValue<string>());
        Assert.Equal("cdidx index . --rebuild", response["result"]!["structuredContent"]!["alternative_action"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Status_CheckCompactExposesReadinessDiagnostics_Issue3541()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{"check":true,"scopes":["issues"],"staleAfterSeconds":60,"format":"compact","explain":"readiness","config":true,"logPath":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal("compact", structured["format"]!.GetValue<string>());
        Assert.Contains("1 files, 2 symbols, 0 refs", structured["summary"]!.GetValue<string>());
        Assert.Equal(1, structured["symbol_kinds"]!["function"]!.GetValue<long>());
        Assert.Equal(60, structured["stale_after_seconds"]!.GetValue<long>());
        Assert.NotNull(structured["workspace_check"]);
        Assert.Empty(structured["failed_checks"]!.AsArray());
        Assert.True(structured["readiness"]!["issues_table_available"]!.GetValue<bool>());
        Assert.True(structured["explain"]!["readiness"]!["issues_table_available"]!.GetValue<bool>());
        Assert.Empty(structured["explain"]!["failed_check_details"]!.AsArray());
        Assert.Equal(_dbPath, structured["effective_config"]!["db_path"]!.GetValue<string>());
        Assert.Equal(60, structured["effective_config"]!["stale_after_seconds"]!.GetValue<int>());
        Assert.False(structured["effective_config"]!["update_check_requested"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(structured["log_path"]!.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Validate_FiltersSeverityAndSupportsCompactCount_Issue3541()
    {
        InsertValidationIssues(
            new FileIssue
            {
                Path = "src/app.cs",
                Kind = "replacement_char",
                Line = 3,
                Message = "warning replacement",
                Origin = FileIssue.OriginDecodeReplacement,
                Severity = FileIssue.SeverityWarning,
            },
            new FileIssue
            {
                Path = "src/app.cs",
                Kind = "replacement_char",
                Line = 4,
                Message = "info literal",
                Origin = FileIssue.OriginSourceLiteral,
                Severity = FileIssue.SeverityInfo,
            });

        var compactRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"validate","arguments":{"severity":"warning","limit":1,"format":"compact"}}}""")!;
        var compactResponse = _server.HandleMessage(compactRequest)!;

        var compact = compactResponse["result"]!["structuredContent"]!;
        Assert.Equal("compact", compact["format"]!.GetValue<string>());
        Assert.Equal(1, compact["count"]!.GetValue<int>());
        var issue = Assert.Single(compact["issues"]!.AsArray())!;
        Assert.Equal(FileIssue.SeverityWarning, issue["severity"]!.GetValue<string>());
        Assert.Equal("replacement_char", issue["kind"]!.GetValue<string>());
        Assert.Equal("src/app.cs", Assert.Single(compact["top_files"]!.AsArray())!["path"]!.GetValue<string>());

        var countRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"validate","arguments":{"severity":"info","countOnly":true}}}""")!;
        var countResponse = _server.HandleMessage(countRequest)!;

        var count = countResponse["result"]!["structuredContent"]!;
        Assert.Equal("count", count["format"]!.GetValue<string>());
        Assert.Equal(1, count["count"]!.GetValue<int>());
        Assert.Null(count["issues"]);
    }

    [Fact]
    public void ToolsCall_Status_ReportsDegradedHotspotFamilyTrust()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_status_hotspots_family");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.Contains("hotspot_family_support_not_indexed=csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
            Assert.Null(structured["hotspotFamilyDegradedReason"]);
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Status_ReportsDegradedSqlGraphContractTrust()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_status_sql_graph");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/target.sql",
                "sql",
                """
                CREATE FUNCTION dbo.fn_Target()
                RETURNS INT
                AS
                BEGIN
                    RETURN 1;
                END;
                GO
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/caller.sql",
                "sql",
                """
                CREATE PROCEDURE dbo.usp_Caller
                AS
                BEGIN
                    SELECT dbo.fn_Target();
                END;
                GO
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkSqlGraphContractReady();

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbol_references
                    SET symbol_name = 'fn_Target',
                        symbol_name_folded = 'fn_target',
                        column_number = 1
                    WHERE symbol_name = 'dbo.fn_Target';
                    DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["sql_graph_contract_ready"]!.GetValue<bool>());
            Assert.Contains("sql_graph_contract_ready=false", structured["sql_graph_contract_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Status_ReadOnlyUriForExplicitDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_uri");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_status");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var sourcePath = Path.Combine(projectRoot, "src", "app.cs");
            File.WriteAllText(sourcePath, "class App { void Run() {} }\n");

            using var readOnlyServer = new McpServer(new Uri(dbPath).AbsoluteUri + "?immutable=1", ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = readOnlyServer.HandleMessage(request)!;

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, response["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
            Assert.True(response["result"]!["structuredContent"]!["gitIsDirty"]!.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Status_CustomDbUnderCdidx_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_custom_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, response["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
            Assert.True(response["result"]!["structuredContent"]!["gitIsDirty"]!.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ToolsCall_Status_ExplicitProjectLocalDb_LeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_project_local_explicit");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Null(response["result"]!["structuredContent"]!["project_root"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitHead"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitIsDirty"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Status_ExplicitProjectLocalReadOnlyUri_LeavesWorkspaceMetadataNullWhenMetadataIsMissing()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_project_local_uri");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using var server = new McpServer(dbUri, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Null(response["result"]!["structuredContent"]!["project_root"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitHead"]);
            Assert.Null(response["result"]!["structuredContent"]!["gitIsDirty"]);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void ToolsCall_Status_ExplicitExternalCodeIndexDb_UsesPersistedProjectRootMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_codeindex_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_status_codeindex_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");
            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();

            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App { void Run() {} }\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.Equal(projectRoot, response["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, response["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
            Assert.True(response["result"]!["structuredContent"]!["gitIsDirty"]!.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void ToolsCall_Ping_ReturnsVersionAndTimestamp()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2032, 4, 5, 6, 7, 8, TimeSpan.Zero));
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, null, null, null, null, McpServer.DefaultMaxConcurrency, clock);
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
        var response = server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("cdidx v", text);
        Assert.Contains("is ready", text);
        Assert.NotNull(response["result"]!["structuredContent"]!["version"]);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.ToString("O"), response["result"]!["structuredContent"]!["timestamp"]!.GetValue<string>());
        Assert.NotNull(response["result"]!["structuredContent"]!["db_exists"]);
    }

    [Fact]
    public void ToolsCall_BatchQuery_IncludesPing()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Single(results);
        Assert.Equal("ping", results[0]!["tool"]!.GetValue<string>());
        Assert.NotNull(results[0]!["result"]!["version"]);
    }

    [Fact]
    public void ToolsCall_BatchQuery_EchoesSlotIdAndSummary_Issue3539()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"slotId":"ping-slot","tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var slot = Assert.Single(response["result"]!["structuredContent"]!["results"]!.AsArray())!;
        Assert.Equal("ping-slot", slot["slot_id"]!.GetValue<string>());
        Assert.Equal("ping", slot["tool"]!.GetValue<string>());
        Assert.True(slot["ok"]!.GetValue<bool>());
        Assert.Contains("cdidx v", slot["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(slot["result"]!["version"]);
    }

    [Fact]
    public void ToolsCall_BatchQuery_EstimateOnlyDoesNotExecuteSlots_Issue3539()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"estimateOnly":true,"queries":[{"id":"slot-a","tool":"ping"},{"slotId":"slot-b","tool":"search","arguments":{"query":"Run","limit":1}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["estimate_only"]!.GetValue<bool>());
        Assert.Equal(2, structured["total_count"]!.GetValue<int>());
        Assert.Equal(0, structured["metadata"]!["executed"]!.GetValue<int>());
        Assert.Empty(structured["results"]!.AsArray());
        Assert.True(structured["metadata"]!["estimated_response_bytes"]!.GetValue<int>() > 0);
        var estimates = structured["slot_estimates"]!.AsArray();
        Assert.Equal(2, estimates.Count);
        Assert.Equal("slot-a", estimates[0]!["slot_id"]!.GetValue<string>());
        Assert.Equal("ping", estimates[0]!["tool"]!.GetValue<string>());
        Assert.Equal("slot-b", estimates[1]!["slot_id"]!.GetValue<string>());
        Assert.Equal("search", estimates[1]!["tool"]!.GetValue<string>());
        Assert.Contains("query=\"Run\"", estimates[1]!["args_summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsCall_BatchQuery_BlocksIndexInBatch()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"index","arguments":{"path":"."}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Contains("not allowed", results[0]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_BlocksBackfillFoldInBatch()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"backfill_fold","arguments":{}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
        Assert.Contains("not allowed", results[0]!["error"]!.GetValue<string>());
    }

    // Regression pins for issue #1537: batch_query must surface envelope metadata
    // (total_elapsed_ms / success_count / failure_count) and per-slot elapsed_ms so
    // callers can detect partial failure and slow inner queries without re-issuing.
    // #1537 回帰テスト: batch_query は envelope メタデータ（total_elapsed_ms /
    // success_count / failure_count）とスロット毎の elapsed_ms を返し、部分失敗や
    // 遅いクエリを再実行せず検出できるようにする。
    [Fact]
    public void ToolsCall_BatchQuery_ReturnsEnvelopeMetadata_Issue1537()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"},{"tool":"status"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var metadata = structured["metadata"]!;
        Assert.Equal(2, metadata["success_count"]!.GetValue<int>());
        Assert.Equal(0, metadata["failure_count"]!.GetValue<int>());
        Assert.True(metadata["total_elapsed_ms"]!.GetValue<long>() >= 0);

        var results = structured["results"]!.AsArray();
        Assert.Equal(2, results.Count);
        foreach (var slot in results)
        {
            Assert.True(slot!["elapsed_ms"]!.GetValue<long>() >= 0);
            Assert.NotNull(slot["args_summary"]);
        }

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("all succeeded", text);
    }

    [Fact]
    public void ToolsCall_BatchQuery_CountsFailuresInEnvelope_Issue1537()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"},{"tool":"index","arguments":{"path":"."}},{"tool":"bogus_tool"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var metadata = structured["metadata"]!;
        Assert.Equal(1, metadata["success_count"]!.GetValue<int>());
        Assert.Equal(2, metadata["failure_count"]!.GetValue<int>());
        Assert.Equal(3, metadata["submitted"]!.GetValue<int>());
        Assert.Equal(3, metadata["executed"]!.GetValue<int>());
        Assert.Equal(2, metadata["errors"]!.GetValue<int>());
        Assert.Equal(3, structured["total_count"]!.GetValue<int>());
        Assert.Equal(1, structured["success_count"]!.GetValue<int>());
        Assert.Equal(2, structured["failure_count"]!.GetValue<int>());
        Assert.True(structured["partial_failure"]!.GetValue<bool>());
        Assert.Equal("isolated", structured["failure_scope"]!.GetValue<string>());

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.NotNull(results[0]!["elapsed_ms"]);
        Assert.True(results[0]!["ok"]!.GetValue<bool>());
        Assert.NotNull(results[1]!["elapsed_ms"]);
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.NotNull(results[2]!["elapsed_ms"]);
        Assert.False(results[2]!["ok"]!.GetValue<bool>());
        Assert.Equal(1, results[1]!["request_index"]!.GetValue<int>());
        Assert.Equal(2, results[2]!["request_index"]!.GetValue<int>());
        Assert.Contains("not allowed", results[1]!["error"]!.GetValue<string>());
        Assert.Contains("Unknown tool", results[2]!["error"]!.GetValue<string>());

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 3 of 3 queries", text);
        Assert.Contains("1 succeeded, 2 failed", text);
    }

    [Fact]
    public void ToolsCall_BatchQuery_UnknownToolName_TruncatesDisplay_Issue3118()
    {
        var toolName = new string('b', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = toolName,
                            ["arguments"] = new JsonObject
                            {
                                ["x"] = 1,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.DoesNotContain(toolName, response.ToJsonString(), StringComparison.Ordinal);
        var result = response["result"]!;
        var structured = result["structuredContent"]!;
        Assert.True(structured["partial_failure"]!.GetValue<bool>());
        var slot = Assert.Single(structured["results"]!.AsArray())!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal(display.Text, slot["tool"]!.GetValue<string>());
        Assert.Equal(toolName.Length, slot["tool_length"]!.GetValue<int>());
        Assert.True(slot["tool_truncated"]!.GetValue<bool>());
        Assert.Equal($"Unknown tool: {display.Text}", slot["error"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_UnknownArgumentName_CarriesTruncationMetadata_Issue3117()
    {
        var argumentName = new string('u', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var display = McpBoundedText.ForDisplay(argumentName);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = "search",
                            ["arguments"] = new JsonObject
                            {
                                ["query"] = "App",
                                [argumentName] = 1,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.DoesNotContain(argumentName, response.ToJsonString(), StringComparison.Ordinal);
        var slot = Assert.Single(response["result"]!["structuredContent"]!["results"]!.AsArray())!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal("search", slot["tool"]!.GetValue<string>());
        Assert.Equal(display.Text, slot["unknown_argument"]!.GetValue<string>());
        Assert.Equal(argumentName.Length, slot["unknown_argument_length"]!.GetValue<int>());
        Assert.True(slot["unknown_argument_truncated"]!.GetValue<bool>());
        Assert.Contains(display.Text, slot["error"]!.GetValue<string>());
        Assert.Contains(display.Text, slot["args_summary"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_EnumLikeScalarTooLong_CarriesTruncationMetadata_Issue3116()
    {
        var oversized = new string('A', McpBoundedText.MaxScalarArgumentChars + 25);
        var display = McpBoundedText.ForDisplay(oversized);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = "search",
                            ["arguments"] = new JsonObject
                            {
                                ["query"] = "App",
                                ["format"] = oversized,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.DoesNotContain(oversized, response.ToJsonString(), StringComparison.Ordinal);
        var slot = Assert.Single(response["result"]!["structuredContent"]!["results"]!.AsArray())!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal("search", slot["tool"]!.GetValue<string>());
        Assert.Equal("format", slot["parameter"]!.GetValue<string>());
        Assert.Equal(display.Text, slot["value"]!.GetValue<string>());
        Assert.Equal(McpBoundedText.MaxScalarArgumentChars, slot["max_length"]!.GetValue<int>());
        Assert.Equal(oversized.Length, slot["actual_length"]!.GetValue<int>());
        Assert.Equal(oversized.Length, slot["value_length"]!.GetValue<int>());
        Assert.True(slot["value_truncated"]!.GetValue<bool>());
        Assert.Contains(display.Text, slot["error"]!.GetValue<string>());
        Assert.Contains(display.Text, slot["args_summary"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_SanitizesSlotExceptionMessage_Issue2849()
    {
        const string secret = "SECRET_BATCH_SLOT_2849";
        var corruptDbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_corrupt");
        File.WriteAllText(corruptDbPath, $"not a sqlite database {secret}");
        var previous = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            using var server = new McpServer(corruptDbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"status"}]}}}""")!;

            var response = server.HandleMessage(request)!;

            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(1, structured["failure_count"]!.GetValue<int>());
            var slot = structured["results"]!.AsArray().Single()!;
            var error = slot["error"]!.GetValue<string>();
            Assert.Equal("Tool 'status' failed. See cdidx server stderr for details.", error);
            Assert.DoesNotContain(secret, error);
            Assert.DoesNotContain("file is not a database", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(McpErrorEnvelope.CategoryIndexCorrupted, slot["category"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previous);
            TestProjectHelper.DeleteSqliteDatabaseFiles(corruptDbPath);
        }
    }

    [Fact]
    public void ToolsCall_FindInFileInvalidRegex_DoesNotEchoRegexExceptionMessage_Issue3370()
    {
        const string secret = "SECRET_REGEX_3370";
        var request = JsonNode.Parse(
            "{\"jsonrpc\":\"2.0\",\"id\":3370,\"method\":\"tools/call\",\"params\":{\"name\":\"find_in_file\",\"arguments\":{\"path\":\"src/app.cs\",\"query\":\"(?<"
            + secret
            + "\",\"regex\":true}}}")!;

        var response = _server.HandleMessage(request)!;

        var error = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Equal("invalid regular expression. Check regex syntax and retry.", error);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsCall_BatchQuery_RejectsTypeMismatchedInnerArguments_Issue1615()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"search","arguments":{"query":"App","limit":"twenty"}},{"tool":"search","arguments":{"query":"App","format":false}},{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(3, structured["total_count"]!.GetValue<int>());
        Assert.Equal(1, structured["success_count"]!.GetValue<int>());
        Assert.Equal(2, structured["failure_count"]!.GetValue<int>());
        Assert.True(structured["partial_failure"]!.GetValue<bool>());
        Assert.Equal("isolated", structured["failure_scope"]!.GetValue<string>());

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.False(results[0]!["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid type for argument 'limit'", results[0]!["error"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, results[0]!["category"]!.GetValue<string>());
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.Contains("Invalid type for argument 'format'", results[1]!["error"]!.GetValue<string>());
        Assert.True(results[2]!["ok"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("\"twenty\"", "string")]
    [InlineData("{}", "object")]
    [InlineData("[]", "array")]
    public void ToolsCall_NumericArgumentsRejectWrongJsonShapes_Issue3791(string limitJson, string expectedActual)
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3791,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","limit":"""
            + limitJson
            + """}}}""")!;

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains("Invalid type for argument 'limit'", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var data = error["data"]!;
        Assert.Equal("limit", data["parameter"]!.GetValue<string>());
        Assert.Equal("integer", data["expected"]!.GetValue<string>());
        Assert.Equal(expectedActual, data["actual"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"not an array\"", "string")]
    [InlineData("{}", "object")]
    public void ToolsCall_BatchQueryRejectsNonArrayQueries_Issue3791(string queriesJson, string expectedActual)
    {
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3791,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":"""
            + queriesJson
            + """}}}""")!;

        var response = _server.HandleMessage(request)!;

        var error = response["error"]!;
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
        Assert.Contains("Invalid type for argument 'queries'", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var data = error["data"]!;
        Assert.Equal("queries", data["parameter"]!.GetValue<string>());
        Assert.Equal("array", data["expected"]!.GetValue<string>());
        Assert.Equal(expectedActual, data["actual"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ReportsMalformedSlotsAndActualExecutionCounts_Issue1838_1992_1994()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[123,{"tool":"search","arguments":{"query":"App","path":["",null,42,"src"]}},{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var metadata = structured["metadata"]!;
        Assert.Equal(3, metadata["submitted"]!.GetValue<int>());
        Assert.Equal(3, metadata["executed"]!.GetValue<int>());
        Assert.Equal(2, metadata["errors"]!.GetValue<int>());

        var results = structured["results"]!.AsArray();
        Assert.Equal(3, results.Count);
        Assert.Equal(0, results[0]!["request_index"]!.GetValue<int>());
        Assert.False(results[0]!["ok"]!.GetValue<bool>());
        Assert.Contains("must be an object", results[0]!["error"]!.GetValue<string>());
        Assert.Equal(1, results[1]!["request_index"]!.GetValue<int>());
        Assert.False(results[1]!["ok"]!.GetValue<bool>());
        Assert.Contains("path contains 3 invalid entries", results[1]!["error"]!.GetValue<string>());
        Assert.Equal(2, results[2]!["request_index"]!.GetValue<int>());
        Assert.True(results[2]!["ok"]!.GetValue<bool>());

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Executed 3 of 3 queries", text);
        Assert.Contains("1 succeeded, 2 failed", text);
    }

    [Fact]
    public void ToolsCall_RejectsOversizedPathArrays_Issue2028()
    {
        var paths = new JsonArray();
        for (var i = 0; i < McpServer.MaxMcpArrayFilterCount + 1; i++)
            paths.Add($"src/{i}.cs");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "App",
                    ["path"] = paths,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("path must contain at most", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, structured["category"]!.GetValue<string>());
        Assert.Equal(1, structured["invalid_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_PathListArgumentsUseCliBounds_Issue3182()
    {
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterCount, McpServer.MaxMcpArrayFilterCount);
        Assert.Equal(QueryCommandRunner.MaxQueryPathFilterLength, McpServer.MaxMcpArrayFilterStringLength);

        var tooManyPaths = new JsonArray();
        for (var i = 0; i < QueryCommandRunner.MaxQueryPathFilterCount + 1; i++)
            tooManyPaths.Add($"src/{i}.cs");
        AssertListError(
            "search",
            new JsonObject
            {
                ["query"] = "App",
                ["path"] = tooManyPaths,
            },
            "path must contain at most",
            expectedInvalidCount: 1);

        AssertListError(
            "search",
            new JsonObject
            {
                ["query"] = "App",
                ["excludePaths"] = new JsonArray { new string('a', QueryCommandRunner.MaxQueryPathFilterLength + 1) },
            },
            $"Entries must be non-empty strings no longer than {QueryCommandRunner.MaxQueryPathFilterLength} characters.",
            expectedInvalidCount: 1);

        AssertListError(
            "map",
            new JsonObject
            {
                ["sections"] = new JsonArray { 42 },
            },
            "sections contains 1 invalid entry",
            expectedInvalidCount: 1);

        void AssertListError(string toolName, JsonObject arguments, string expectedText, int expectedInvalidCount)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = arguments,
                },
            };

            var response = _server.HandleMessage(request)!;

            var result = response["result"]!;
            Assert.True(result["isError"]!.GetValue<bool>(), response.ToJsonString());
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains(expectedText, text);
            var structured = result["structuredContent"]!;
            Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, structured["category"]!.GetValue<string>());
            Assert.Equal(expectedInvalidCount, structured["invalid_count"]!.GetValue<int>());
        }
    }

    [Fact]
    public void ToolsCall_StringListArgumentsExposeSharedBounds_Issue3752()
    {
        var names = new JsonArray();
        for (var i = 0; i < McpServer.MaxMcpArrayFilterCount + 1; i++)
            names.Add($"App{i}");
        var tooManyResponse = _server.HandleMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "symbols",
                ["arguments"] = new JsonObject
                {
                    ["names"] = names,
                },
            },
        })!;

        var tooManyStructured = tooManyResponse["result"]!["structuredContent"]!;
        Assert.True(tooManyResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(McpServer.MaxMcpArrayFilterCount, tooManyStructured["max_count"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpArrayFilterCount + 1, tooManyStructured["actual_count"]!.GetValue<int>());

        var longExclude = new string('x', McpServer.MaxMcpArrayFilterStringLength + 1);
        var tooLongResponse = _server.HandleMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject
                {
                    ["query"] = "App",
                    ["excludePaths"] = longExclude,
                },
            },
        })!;

        var tooLongStructured = tooLongResponse["result"]!["structuredContent"]!;
        Assert.True(tooLongResponse["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(McpServer.MaxMcpArrayFilterStringLength, tooLongStructured["max_length"]!.GetValue<int>());
        Assert.Equal(longExclude.Length, tooLongStructured["actual_length"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_StringListArgumentErrorsCarryBounds_Issue3752()
    {
        var paths = new JsonArray();
        for (var i = 0; i < McpServer.MaxMcpArrayFilterCount + 1; i++)
            paths.Add($"src/{i}.cs");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "batch_query",
                ["arguments"] = new JsonObject
                {
                    ["queries"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["tool"] = "search",
                            ["arguments"] = new JsonObject
                            {
                                ["query"] = "App",
                                ["path"] = paths,
                            },
                        },
                    },
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        var slot = response["result"]!["structuredContent"]!["results"]!.AsArray().Single()!;
        Assert.False(slot["ok"]!.GetValue<bool>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, slot["category"]!.GetValue<string>());
        Assert.Equal(McpServer.MaxMcpArrayFilterCount, slot["max_count"]!.GetValue<int>());
        Assert.Equal(McpServer.MaxMcpArrayFilterCount + 1, slot["actual_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_TruncatesAggregateResponse_Issue1416()
    {
        var previous = Environment.GetEnvironmentVariable("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        Environment.SetEnvironmentVariable("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", "950");
        try
        {
            InsertIndexedFile("src/large.cs", "csharp", "// " + new string('x', 5000));
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"slotId":"ping-slot","tool":"ping"},{"slotId":"excerpt-slot","tool":"excerpt","arguments":{"path":"src/large.cs","startLine":1,"endLine":1,"maxLineWidth":0}}]}}}""")!;
            var response = _server.HandleMessage(request)!;

            var structured = response["result"]!["structuredContent"]!;
            Assert.True(structured["truncated"]?.GetValue<bool>() ?? false, response.ToJsonString());
            var actualResponseBytes = Encoding.UTF8.GetByteCount(response.ToJsonString());
            Assert.True(actualResponseBytes <= 950, $"Actual response was {actualResponseBytes} bytes.");
            Assert.True(structured["metadata"]!["estimated_response_bytes"]!.GetValue<int>() <= 950);
            Assert.Equal(950, structured["metadata"]!["response_byte_limit"]!.GetValue<int>());
            Assert.Equal(2, structured["metadata"]!["submitted"]!.GetValue<int>());
            var executed = structured["metadata"]!["executed"]!.GetValue<int>();
            Assert.InRange(executed, 1, 2);
            Assert.Equal(0, structured["metadata"]!["errors"]!.GetValue<int>());
            Assert.Equal("cascading", structured["failure_scope"]!.GetValue<string>());
            Assert.NotNull(structured["cascade_started_at_index"]);
            Assert.True(structured["partial_failure"]!.GetValue<bool>());

            var truncatedQueries = structured["truncated_queries"]!.AsArray();
            Assert.NotEmpty(truncatedQueries);
            Assert.All(truncatedQueries, q => Assert.NotNull(q!["args_summary"]));
            Assert.All(truncatedQueries, q => Assert.NotNull(q!["slot_id"]));
            Assert.Contains(truncatedQueries, q =>
                q!["reason"]?.GetValue<string>() is "response_byte_limit_exceeded" or "response_byte_limit_already_exceeded" or "final_response_byte_limit_exceeded");
            var splitHint = structured["split_hint"]!;
            Assert.Equal("response_byte_limit_exceeded", splitHint["reason"]!.GetValue<string>());
            var firstTruncatedRequestIndex = truncatedQueries
                .Select(q => q!["request_index"]!.GetValue<int>())
                .Min();
            Assert.Equal(firstTruncatedRequestIndex, splitHint["next_request_index"]!.GetValue<int>());
            Assert.StartsWith("batch_query:v1:", splitHint["resume_cursor"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(splitHint["suggested_query_count"]!.GetValue<int>() >= 1);

            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("Response truncated", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", previous);
        }
    }

    [Fact]
    public void ToolsCall_BatchQuery_ClampsTooLargeResponseLimitEnvironment()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"ping"}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var metadata = response["result"]!["structuredContent"]!["metadata"]!;
        Assert.Equal(McpServer.MaxBatchQueryResponseByteLimit, metadata["response_byte_limit"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_UsesPerCallResponseBudget_Issue3539()
    {
        InsertIndexedFile("src/large-per-call.cs", "csharp", "// " + new string('x', 5000));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"maxResponseBytes":1200,"queries":[{"slotId":"first","tool":"ping"},{"slotId":"second","tool":"excerpt","arguments":{"path":"src/large-per-call.cs","startLine":1,"endLine":1,"maxLineWidth":0}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1200, structured["metadata"]!["response_byte_limit"]!.GetValue<int>());
        Assert.True(structured["truncated"]!.GetValue<bool>(), response.ToJsonString());
        Assert.NotNull(structured["split_hint"]);
        Assert.True(Encoding.UTF8.GetByteCount(response.ToJsonString()) <= 1200);
    }

    [Fact]
    public void ToolsCall_BatchQuery_CompactsTruncatedMetadataToHonorTightResponseBudget()
    {
        InsertIndexedFile("src/large-per-call-tight.cs", "csharp", "// " + new string('x', 5000));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"maxResponseBytes":900,"queries":[{"slotId":"first","tool":"ping"},{"slotId":"second","tool":"excerpt","arguments":{"path":"src/large-per-call-tight.cs","startLine":1,"endLine":1,"maxLineWidth":0}}]}}}""")!;

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["truncated"]!.GetValue<bool>(), response.ToJsonString());
        Assert.Equal(900, structured["metadata"]!["response_byte_limit"]!.GetValue<int>());
        Assert.True(Encoding.UTF8.GetByteCount(response.ToJsonString()) <= 900, response.ToJsonString());
        Assert.Equal("Response truncated at 900 bytes.", response["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var truncatedQuery = Assert.Single(structured["truncated_queries"]!.AsArray());
        Assert.NotNull(truncatedQuery!["slot_id"]);
        Assert.NotNull(truncatedQuery["args_summary"]);
        Assert.Null(truncatedQuery["tool"]);
        Assert.Equal("response_byte_limit_exceeded", structured["split_hint"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ClampedPerCallBudgetCountsAdjustmentsAgainstBudget_Issue3539()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES");
        env.Set("CDIDX_MCP_BATCH_RESPONSE_MAX_BYTES", "1400");
        InsertIndexedFile("src/large-per-call-clamped.cs", "csharp", "// " + new string('x', 5000));
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"maxResponseBytes":2000,"queries":[{"slotId":"first","tool":"ping"},{"slotId":"second","tool":"excerpt","arguments":{"path":"src/large-per-call-clamped.cs","startLine":1,"endLine":1,"maxLineWidth":0}}]}}}""")!;

        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(1400, structured["metadata"]!["response_byte_limit"]!.GetValue<int>());
        Assert.True(Encoding.UTF8.GetByteCount(response.ToJsonString()) <= 1400, response.ToJsonString());
        var adjustment = Assert.Single(structured["argument_adjustments"]!.AsArray());
        Assert.Equal("maxResponseBytes", adjustment!["argument"]!.GetValue<string>());
        Assert.Equal("clamped", adjustment["action"]!.GetValue<string>());
        Assert.Equal(2000, adjustment["requested"]!.GetValue<int>());
        Assert.Equal(1400, adjustment["effective"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_BatchQuery_ArgsSummaryReflectsRequestedArguments_Issue1537()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"symbols","arguments":{"query":"App","lang":"csharp"}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var slot = response["result"]!["structuredContent"]!["results"]!.AsArray()[0]!;
        var summary = slot["args_summary"]!.GetValue<string>();
        Assert.Contains("query=", summary);
        Assert.Contains("App", summary);
        Assert.Contains("lang=", summary);
    }

    [Fact]
    public void ToolsCall_BatchQuery_ArgsSummaryBoundsHugeScalarNumbers_Issue3816()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"batch_query","arguments":{"queries":[{"tool":"status","arguments":{"huge":1e9999,"flag":true,"missing":null}}]}}}""")!;
        var response = _server.HandleMessage(request)!;

        var slot = response["result"]!["structuredContent"]!["results"]!.AsArray()[0]!;
        var summary = slot["args_summary"]!.GetValue<string>();
        Assert.Contains("huge=<number>", summary, StringComparison.Ordinal);
        Assert.Contains("flag=true", summary, StringComparison.Ordinal);
        Assert.Contains("missing=null", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("999999", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolsCall_Languages_ReturnsCapabilities()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"languages","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("languages supported", text);
        var structured = response["result"]!["structuredContent"]!;
        var precedence = structured["detection_policy"]!["precedence"]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToList();
        Assert.Equal("language_map_override", precedence[0]);
        Assert.True(precedence.IndexOf("exact_filename") < precedence.IndexOf("built_in_extension"));
        Assert.NotNull(structured["language_map_diagnostics"]);

        var languages = structured["languages"]!.AsArray();
        Assert.True(languages.Count > 20); // We support 30+ languages

        // Verify a known language has the right capabilities / 既知の言語の機能を検証
        var csharp = languages.First(l => l!["lang"]!.GetValue<string>() == "csharp")!;
        Assert.True(csharp["symbol_extraction"]!.GetValue<bool>());
        Assert.True(csharp["reference_extraction"]!.GetValue<bool>());
        Assert.True(csharp["graph_queries"]!.GetValue<bool>());
        Assert.Contains(".cs", csharp["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));

        var javascript = languages.First(l => l!["lang"]!.GetValue<string>() == "javascript")!;
        Assert.Contains(".cjs", javascript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains(".mjs", javascript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));

        var typescript = languages.First(l => l!["lang"]!.GetValue<string>() == "typescript")!;
        Assert.Contains(".cts", typescript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains(".mts", typescript["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));

        var assembly = languages.First(l => l!["lang"]!.GetValue<string>() == "assembly")!;
        Assert.True(assembly["symbol_extraction"]!.GetValue<bool>());
        Assert.True(assembly["reference_extraction"]!.GetValue<bool>());
        Assert.True(assembly["graph_queries"]!.GetValue<bool>());
        Assert.Contains(".asm", assembly["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains(".S", assembly["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains("assembler", assembly["aliases"]!.AsArray().Select(e => e!.GetValue<string>()));

        // Verify a markup language / markup 言語を検証
        var markdown = languages.First(l => l!["lang"]!.GetValue<string>() == "markdown")!;
        Assert.True(markdown["symbol_extraction"]!.GetValue<bool>());
        Assert.True(markdown["reference_extraction"]!.GetValue<bool>());
        Assert.True(markdown["graph_queries"]!.GetValue<bool>());
        Assert.DoesNotContain("missing-references", markdown["capability_gaps"]!.AsArray().Select(e => e!.GetValue<string>()));

        var yaml = languages.First(l => l!["lang"]!.GetValue<string>() == "yaml")!;
        Assert.True(yaml["symbol_extraction"]!.GetValue<bool>());
        Assert.True(yaml["reference_extraction"]!.GetValue<bool>());
        Assert.True(yaml["graph_queries"]!.GetValue<bool>());
        Assert.Contains("yml", yaml["aliases"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.DoesNotContain("missing-symbols", yaml["capability_gaps"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.DoesNotContain("missing-references", yaml["capability_gaps"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Empty(yaml["unsupported_guidance"]!.AsArray());
        Assert.Empty(csharp["unsupported_guidance"]!.AsArray());

        // Pin #215: HTML must report symbol_extraction=true and list all four
        // extensions so AI tools discover HTML support via the MCP languages tool.
        // #215 を pin: HTML は symbol_extraction=true で、.html / .htm / .xhtml / .shtml
        // の 4 拡張子を MCP languages ツールから返すこと。
        var html = languages.First(l => l!["lang"]!.GetValue<string>() == "html")!;
        Assert.True(html["symbol_extraction"]!.GetValue<bool>());
        Assert.True(html["reference_extraction"]!.GetValue<bool>());
        Assert.True(html["graph_queries"]!.GetValue<bool>());
        var htmlExtensions = html["extensions"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Contains(".html", htmlExtensions);
        Assert.Contains(".htm", htmlExtensions);
        Assert.Contains(".xhtml", htmlExtensions);
        Assert.Contains(".shtml", htmlExtensions);

        var dependencyManifest = languages.First(l => l!["lang"]!.GetValue<string>() == "dependency_manifest")!;
        Assert.True(dependencyManifest["symbol_extraction"]!.GetValue<bool>());
        Assert.True(dependencyManifest["reference_extraction"]!.GetValue<bool>());
        Assert.True(dependencyManifest["graph_queries"]!.GetValue<bool>());
        Assert.DoesNotContain("missing-symbols", dependencyManifest["capability_gaps"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains("Directory.Packages.props", dependencyManifest["exact_filenames"]!.AsArray().Select(e => e!.GetValue<string>()));

        var dependencyLock = languages.First(l => l!["lang"]!.GetValue<string>() == "dependency_lock")!;
        Assert.True(dependencyLock["symbol_extraction"]!.GetValue<bool>());
        Assert.True(dependencyLock["reference_extraction"]!.GetValue<bool>());
        Assert.True(dependencyLock["graph_queries"]!.GetValue<bool>());
        Assert.DoesNotContain("missing-symbols", dependencyLock["capability_gaps"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Contains("packages.lock.json", dependencyLock["exact_filenames"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public void ToolsCall_Languages_SeparatesFilenamePatternKindsAndKeepsLegacyPatterns_Issue4617()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"languages","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        var languages = response["result"]!["structuredContent"]!["languages"]!.AsArray();
        var dockerfile = languages.Single(entry => entry!["lang"]!.GetValue<string>() == "dockerfile")!;
        var extensions = dockerfile["extensions"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
        var exactFilenames = dockerfile["exact_filenames"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
        var prefixPatterns = dockerfile["filename_prefix_patterns"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
        var legacyPatterns = dockerfile["legacy_patterns"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();

        Assert.DoesNotContain("Dockerfile", extensions);
        Assert.Contains("Dockerfile", exactFilenames);
        Assert.Contains("Dockerfile.<suffix>", prefixPatterns);
        Assert.Contains("Dockerfile", legacyPatterns);
        Assert.Contains("Dockerfile.<suffix>", legacyPatterns);
        Assert.Contains(
            dockerfile["pattern_provenance"]!.AsArray(),
            item => item!["pattern"]!.GetValue<string>() == "Dockerfile"
                && item["kind"]!.GetValue<string>() == "exact_filename"
                && item["source"]!.GetValue<string>() == "built_in");
    }

    [Fact]
    public void ToolsCall_Languages_DefaultCatalogUsesIndexedWorkspaceSnapshot_Issue4602()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(
                    _projectRoot,
                    new McpWorkspaceCatalogSymbolExtractor());
                Assert.Contains("mcpcatalog", SymbolExtractor.GetSupportedLanguages(_projectRoot));
                Assert.Contains("mcpcatalog", FileIndexer.GetLanguageExtensions(_projectRoot).Values);

                var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"languages","arguments":{}}}""")!;
                var response = _server.HandleMessage(request)!;

                var language = Assert.Single(
                    response["result"]!["structuredContent"]!["languages"]!.AsArray(),
                    entry => entry?["lang"]?.GetValue<string>() == "mcpcatalog")!;
                Assert.True(language["symbol_extraction"]!.GetValue<bool>());
                Assert.Contains(".mcpcatalog", language["extensions"]!.AsArray().Select(extension => extension!.GetValue<string>()));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    private sealed class McpWorkspaceCatalogSymbolExtractor : ISymbolExtractor
    {
        public string Language => "mcpcatalog";
        public IReadOnlyCollection<string> FileExtensions => [".mcpcatalog"];

        public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
            => [];
    }

    [Fact]
    public void ToolsCall_Languages_FiltersByCliCompatibleMetadata_Issue3540()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"languages","arguments":{"capability":["graph","references"],"extension":"cs","alias":"cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        var languages = structured["languages"]!.AsArray();
        var language = Assert.Single(languages)!;
        Assert.Equal("csharp", language["lang"]!.GetValue<string>());
        Assert.True(language["graph_queries"]!.GetValue<bool>());
        Assert.Contains(".cs", language["extensions"]!.AsArray().Select(e => e!.GetValue<string>()));
        Assert.Equal(".cs", structured["filters"]!["extension"]!.GetValue<string>());
        Assert.Equal(2, structured["filters"]!["capability"]!.AsArray().Count);
        Assert.Equal(1, structured["extension_lookup"]!["matched"]!.GetValue<int>());
        Assert.Equal("csharp", Assert.Single(structured["extension_lookup"]!["languages"]!.AsArray())!.GetValue<string>());
        Assert.Equal(1, structured["alias_lookup"]!["matched"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Languages_IndexedOnlyUsesDatabaseLanguages_Issue3540()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"languages","arguments":{"indexedOnly":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["filters"]!["indexedOnly"]!.GetValue<bool>());
        var language = Assert.Single(structured["languages"]!.AsArray())!;
        Assert.Equal("csharp", language["lang"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Outline_ReturnsSymbols()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"outline","arguments":{"path":"src/app.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("symbol", text.ToLowerInvariant());
        Assert.NotNull(result["structuredContent"]);
        var structured = result["structuredContent"]!;
        Assert.Equal("src/app.cs", structured["path"]!.GetValue<string>());
        var symbols = structured["symbols"]!.AsArray();
        var run = symbols.Single(symbol => symbol!["name"]!.GetValue<string>() == "Run")!;
        Assert.Equal("Run()", run["displayName"]!.GetValue<string>());
        Assert.Equal("App.Run", run["path"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Outline_NotFound_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"outline","arguments":{"path":"nonexistent.cs"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var structured = response["result"]!["structuredContent"]!;
        Assert.NotNull(structured["error"]);
        Assert.NotNull(structured["indexed_file_count"]);
    }

    [Fact]
    public void ToolsCall_Index_MissingPath_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("db")]
    [InlineData("optimize")]
    public void ToolsCall_Index_RejectsUnsupportedArguments_Issue2848(string argumentName)
    {
        var arguments = new JsonObject
        {
            ["path"] = ".",
            [argumentName] = argumentName switch
            {
                "db" => JsonValue.Create("alternate.db"),
                "optimize" => JsonValue.Create(true),
                _ => throw new ArgumentOutOfRangeException(nameof(argumentName), argumentName, null),
            },
        };
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "index",
                ["arguments"] = arguments,
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains($"Unknown argument '{argumentName}' for tool 'index'.", text);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(argumentName, structured["unknown_argument"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_Index_DryRunReportsAdvancedControlsAndUnsupportedModes_Issue3543()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_dryrun_advanced_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.py"), "class App:\n    def run(self):\n        return 1\n");
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir,
                        ["dryRun"] = true,
                        ["maxSymbolsPerFile"] = 12,
                        ["followSymlinks"] = "internal",
                        ["includeSymbolKind"] = new JsonArray(JsonValue.Create("function")),
                        ["excludeSymbolKind"] = "class",
                        ["memoryTrace"] = true,
                        ["parallelism"] = 2,
                        ["commits"] = new JsonArray(JsonValue.Create("HEAD")),
                        ["watch"] = true,
                        ["debounce"] = 25,
                    },
                },
            };

            var response = _server.HandleMessage(request)!;

            var structured = response["result"]!["structuredContent"]!;
            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.True(structured["dry_run"]!.GetValue<bool>());
            Assert.False(structured["summary"]!["would_mutate_database"]!.GetValue<bool>());
            Assert.Equal("internal", structured["index_options"]!["followSymlinks"]!.GetValue<string>());
            Assert.Equal(1, structured["index_options"]!["effective_parallelism"]!.GetValue<int>());
            Assert.NotNull(structured["memory_trace"]);
            var unsupportedNames = structured["unsupported_modes"]!.AsArray()
                .Select(mode => mode!["name"]!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("parallelism", unsupportedNames);
            Assert.Contains("commits", unsupportedNames);
            Assert.Contains("watch", unsupportedNames);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_DryRunPreservesCheckedRootIdentityInResponseAndAudit_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_identity_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_identity");
        var auditPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_identity_{Guid.NewGuid():N}.jsonl");
        Directory.CreateDirectory(fixtureDir);
        string? checkedRootIdentity = null;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }\n");
            using (var sink = new AuditLogSink(auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false))
            using (var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, sink))
            {
                var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

                Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
                checkedRootIdentity = response["result"]!["structuredContent"]!["checked_root_identity"]!.GetValue<string>();
                Assert.StartsWith("fsid:v1:", checkedRootIdentity, StringComparison.Ordinal);
            }

            var auditRecord = Assert.Single(File.ReadAllLines(auditPath));
            using var auditJson = JsonDocument.Parse(auditRecord);
            Assert.Equal(
                checkedRootIdentity,
                auditJson.RootElement.GetProperty("checked_root_identity").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            if (File.Exists(auditPath))
                File.Delete(auditPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_PostAuthorizationExceptionPreservesCheckedRootIdentityInAudit_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_audit_error_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_audit_error");
        var auditPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_audit_error_{Guid.NewGuid():N}.jsonl");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }\n");
            using (var sink = new AuditLogSink(auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false))
            using (var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: false, sink))
            {
                McpServer.McpIndexAuthorizationCompletedForTesting = () =>
                    throw new InvalidOperationException("test-only post-authorization failure");

                var response = CallIndex(server, fixtureDir);

                Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
                Assert.Equal(
                    McpErrorEnvelope.CategoryInternalError,
                    response["result"]!["structuredContent"]!["category"]!.GetValue<string>());
            }

            var auditRecord = Assert.Single(File.ReadAllLines(auditPath));
            using var auditJson = JsonDocument.Parse(auditRecord);
            Assert.StartsWith(
                "fsid:v1:",
                auditJson.RootElement.GetProperty("checked_root_identity").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            McpServer.McpIndexAuthorizationCompletedForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            if (File.Exists(auditPath))
                File.Delete(auditPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsAuthorizedRootSymlinkSwap_Issue4606()
    {
        if (OperatingSystem.IsWindows())
            return;

        var allowedTarget = Path.Combine(Path.GetFullPath("."), $"mcp_index_swap_allowed_{Guid.NewGuid():N}");
        var linkPath = Path.Combine(Path.GetFullPath("."), $"mcp_index_swap_link_{Guid.NewGuid():N}");
        var outsideTarget = TestProjectHelper.CreateTempProject("cdidx_mcp_index_swap_outside");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_swap");
        Directory.CreateDirectory(allowedTarget);
        try
        {
            File.WriteAllText(Path.Combine(allowedTarget, "allowed.cs"), "public class Allowed { }\n");
            File.WriteAllText(Path.Combine(outsideTarget, "outside.cs"), "public class Outside { }\n");
            Directory.CreateSymbolicLink(linkPath, allowedTarget);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            McpServer.McpIndexAuthorizationCompletedForTesting = () =>
            {
                Directory.Delete(linkPath);
                Directory.CreateSymbolicLink(linkPath, outsideTarget);
            };

            var response = CallIndex(server, linkPath, arguments => arguments["dryRun"] = true);

            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(McpErrorEnvelope.CategoryPermissionDenied, structured["category"]!.GetValue<string>());
            Assert.Equal("requested_root_changed", structured["authorization_failure_reason"]!.GetValue<string>());
            Assert.StartsWith(
                "fsid:v1:",
                structured["checked_root_identity"]!.GetValue<string>(),
                StringComparison.Ordinal);
        }
        finally
        {
            McpServer.McpIndexAuthorizationCompletedForTesting = null;
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);
            TestProjectHelper.DeleteDirectory(allowedTarget);
            TestProjectHelper.DeleteDirectory(outsideTarget);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsAuthorizedRootIdentityReplacement_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_identity_swap_{Guid.NewGuid():N}");
        var originalDir = fixtureDir + "_original";
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_identity_swap");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "allowed.cs"), "public class Allowed { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            McpServer.McpIndexAuthorizationCompletedForTesting = () =>
            {
                Directory.Move(fixtureDir, originalDir);
                Directory.CreateDirectory(fixtureDir);
                File.WriteAllText(Path.Combine(fixtureDir, "replacement.cs"), "public class Replacement { }\n");
            };

            var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(McpErrorEnvelope.CategoryPermissionDenied, structured["category"]!.GetValue<string>());
            Assert.Equal("root_identity_changed", structured["authorization_failure_reason"]!.GetValue<string>());
        }
        finally
        {
            McpServer.McpIndexAuthorizationCompletedForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteDirectory(originalDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsFileReplacementAtAuthorizedOpenBoundary_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_file_swap_{Guid.NewGuid():N}");
        var victimPath = Path.Combine(fixtureDir, "victim.cs");
        var originalPath = Path.Combine(fixtureDir, "victim.original");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_file_swap");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(victimPath, "public class Allowed { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var swapped = 0;
            McpServer.McpIndexEntryOpenBoundaryForTesting = path =>
            {
                if (!PathCasing.PathsEqual(path, victimPath)
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                File.Move(victimPath, originalPath);
                File.WriteAllText(victimPath, "public class Replacement { }\n");
            };

            var response = CallIndex(server, fixtureDir);

            Assert.True(swapped == 1, response.ToJsonString());
            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            var structured = response["result"]!["structuredContent"]!;
            Assert.True(
                string.Equals(
                    McpErrorEnvelope.CategoryPermissionDenied,
                    structured["category"]!.GetValue<string>(),
                    StringComparison.Ordinal),
                response.ToJsonString());
            Assert.Equal("entry_identity_changed", structured["authorization_failure_reason"]!.GetValue<string>());
            Assert.StartsWith(
                "fsid:v1:",
                structured["checked_root_identity"]!.GetValue<string>(),
                StringComparison.Ordinal);
        }
        finally
        {
            McpServer.McpIndexEntryOpenBoundaryForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsAmbiguousLanguageFileReplacementAtAuthorizedOpenBoundary_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_ambiguous_file_swap_{Guid.NewGuid():N}");
        var victimPath = Path.Combine(fixtureDir, "victim.m");
        var originalPath = Path.Combine(fixtureDir, "victim.original");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_ambiguous_file_swap");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(victimPath, "@interface Allowed\n@end\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var swapped = 0;
            McpServer.McpIndexEntryOpenBoundaryForTesting = path =>
            {
                if (!PathCasing.PathsEqual(path, victimPath)
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                File.Move(victimPath, originalPath);
                File.WriteAllText(victimPath, "function replacement\nend\n");
            };

            var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

            Assert.Equal(1, swapped);
            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(McpErrorEnvelope.CategoryPermissionDenied, structured["category"]!.GetValue<string>());
            Assert.Equal("entry_identity_changed", structured["authorization_failure_reason"]!.GetValue<string>());
        }
        finally
        {
            McpServer.McpIndexEntryOpenBoundaryForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsDirectoryReplacementAtAuthorizedOpenBoundary_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_directory_swap_{Guid.NewGuid():N}");
        var directoryPath = Path.Combine(fixtureDir, "src");
        var originalPath = Path.Combine(fixtureDir, "src.original");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_directory_swap");
        Directory.CreateDirectory(directoryPath);
        try
        {
            File.WriteAllText(Path.Combine(directoryPath, "allowed.cs"), "public class Allowed { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var swapped = 0;
            McpServer.McpIndexEntryOpenBoundaryForTesting = path =>
            {
                if (!PathCasing.PathsEqual(path, directoryPath)
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                Directory.Move(directoryPath, originalPath);
                Directory.CreateDirectory(directoryPath);
                File.WriteAllText(Path.Combine(directoryPath, "replacement.cs"), "public class Replacement { }\n");
            };

            var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

            Assert.True(swapped == 1, response.ToJsonString());
            Assert.True(response["result"]?["isError"]?.GetValue<bool>() == true, response.ToJsonString());
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(McpErrorEnvelope.CategoryPermissionDenied, structured["category"]!.GetValue<string>());
            Assert.Equal("directory_identity_changed", structured["authorization_failure_reason"]!.GetValue<string>());
        }
        finally
        {
            McpServer.McpIndexEntryOpenBoundaryForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void IndexRootAuthorization_EnumeratesRetainedDirectoryHandleAcrossDoubleSwap_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_directory_double_swap_{Guid.NewGuid():N}");
        var directoryPath = Path.Combine(fixtureDir, "content4606");
        var heldOriginalPath = Path.Combine(fixtureDir, "content4606.original");
        var replacementPath = Path.Combine(fixtureDir, "content4606.replacement");
        Directory.CreateDirectory(directoryPath);
        Directory.CreateDirectory(replacementPath);
        try
        {
            File.WriteAllText(Path.Combine(directoryPath, "allowed.cs"), "public class Allowed { }\n");
            File.WriteAllText(Path.Combine(replacementPath, "outside.cs"), "public class Outside { }\n");
            var swapStarted = 0;
            var swapCompleted = 0;
            void BeforeEnumeration(string path)
            {
                if (!PathCasing.PathsEqual(path, directoryPath)
                    || Interlocked.Exchange(ref swapStarted, 1) != 0)
                {
                    return;
                }

                Directory.Move(directoryPath, heldOriginalPath);
                Directory.Move(replacementPath, directoryPath);
            }
            void AfterEnumeration(string path)
            {
                if (!PathCasing.PathsEqual(path, directoryPath)
                    || Interlocked.Exchange(ref swapCompleted, 1) != 0)
                {
                    return;
                }

                Directory.Move(directoryPath, replacementPath);
                Directory.Move(heldOriginalPath, directoryPath);
            }

            Assert.True(
                McpPathBoundary.TryCaptureIndexRoot(
                    fixtureDir,
                    _ => true,
                    entryOpenBoundary: null,
                    directoryEnumerationBoundary: BeforeEnumeration,
                    directoryEnumerationCompleted: AfterEnumeration,
                    out var authorization,
                    out var error),
                error);
            using (authorization)
            {
                var entries = authorization!.EnumerateAuthorizedFileSystemEntries(directoryPath).ToArray();
                Assert.Equal("allowed.cs", Path.GetFileName(Assert.Single(entries)));
            }

            Assert.Equal(1, swapStarted);
            Assert.Equal(1, swapCompleted);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_DoesNotReuseLanguageMapOutsideAuthorizedProjectScope_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_langmap_scope_{Guid.NewGuid():N}");
        var projectPath = Path.Combine(fixtureDir, "client-root");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_langmap_scope");
        Directory.CreateDirectory(projectPath);
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            File.WriteAllText(
                Path.Combine(fixtureDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: csharp\n");
            File.WriteAllText(Path.Combine(projectPath, "outside.custom"), "public class Outside { }\n");
            Assert.Equal(
                "csharp",
                LanguageMapOverrides.LoadEffectiveMapFromDirectory(projectPath)[".custom"]);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var response = CallIndex(server, projectPath, arguments => arguments["dryRun"] = true);

            Assert.Null(response["result"]!["isError"]);
            var summary = response["result"]!["structuredContent"]!["summary"]!;
            Assert.Equal(0, summary["files_scanned"]!.GetValue<int>());
            Assert.Equal(1, summary["unknown_extension_file_count"]!.GetValue<int>());
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsLanguageMapReplacementAtAuthorizedOpenBoundary_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_langmap_swap_{Guid.NewGuid():N}");
        var configPath = Path.Combine(fixtureDir, LanguageMapOverrides.WorkspaceFileName);
        var originalPath = configPath + ".original";
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_langmap_swap");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(configPath, "entries:\n- extension: custom\n  language: csharp\n");
            File.WriteAllText(Path.Combine(fixtureDir, "app.custom"), "public class Allowed { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var swapped = 0;
            McpServer.McpIndexEntryOpenBoundaryForTesting = path =>
            {
                if (!PathCasing.PathsEqual(path, configPath)
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                File.Move(configPath, originalPath);
                File.WriteAllText(configPath, "entries:\n- extension: custom\n  language: text\n");
            };

            var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

            Assert.Equal(1, swapped);
            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            Assert.Equal(
                "entry_identity_changed",
                response["result"]!["structuredContent"]!["authorization_failure_reason"]!.GetValue<string>());
        }
        finally
        {
            McpServer.McpIndexEntryOpenBoundaryForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsPatternConfigReplacementAtAuthorizedOpenBoundary_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_pattern_swap_{Guid.NewGuid():N}");
        var patternDirectory = Path.Combine(fixtureDir, ".cdidx", "patterns");
        var configPath = Path.Combine(patternDirectory, "custom.yaml");
        var originalPath = configPath + ".original";
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_pattern_swap");
        Directory.CreateDirectory(patternDirectory);
        try
        {
            File.WriteAllText(
                configPath,
                "language: custom\n- extension: .custom\n- kind: function\n  regex: '(?<name>Allowed)'\n");
            File.WriteAllText(Path.Combine(fixtureDir, "app.custom"), "Allowed\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var swapped = 0;
            McpServer.McpIndexEntryOpenBoundaryForTesting = path =>
            {
                if (!PathCasing.PathsEqual(path, configPath)
                    || Interlocked.Exchange(ref swapped, 1) != 0)
                {
                    return;
                }

                File.Move(configPath, originalPath);
                File.WriteAllText(configPath, "language: replacement\n");
            };

            var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

            Assert.Equal(1, swapped);
            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            Assert.Equal(
                "entry_identity_changed",
                response["result"]!["structuredContent"]!["authorization_failure_reason"]!.GetValue<string>());
        }
        finally
        {
            McpServer.McpIndexEntryOpenBoundaryForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_LoadsProjectPatternConfigFromAuthorizedSnapshot_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_pattern_authorized_{Guid.NewGuid():N}");
        var patternDirectory = Path.Combine(fixtureDir, ".cdidx", "patterns");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_pattern_authorized");
        Directory.CreateDirectory(patternDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(patternDirectory, "custom.yaml"),
                "language: custom\n- extension: .custom\n- kind: function\n  regex: '(?<name>Allowed)'\n");
            File.WriteAllText(Path.Combine(fixtureDir, "app.custom"), "Allowed\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var response = CallIndex(server, fixtureDir, arguments => arguments["dryRun"] = true);

            Assert.Null(response["result"]!["isError"]);
            var summary = response["result"]!["structuredContent"]!["summary"]!;
            Assert.Equal(1, summary["files_scanned"]!.GetValue<int>());
            Assert.Equal(0, summary["unknown_extension_file_count"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void IndexRootAuthorization_AcceptsWindowsLongPathSidecars_Issue4606()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_long_sidecar_{Guid.NewGuid():N}");
        var deepDirectory = fixtureDir;
        while (deepDirectory.Length < 270)
            deepDirectory = Path.Combine(deepDirectory, "deep-segment");
        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(deepDirectory));
        try
        {
            foreach (var fileName in new[] { ".gitignore", ".gitmodules" })
            {
                var path = Path.Combine(deepDirectory, fileName);
                File.WriteAllText(LongPath.EnsureWindowsPrefix(path), "# bounded\n");
            }

            Assert.True(
                McpPathBoundary.TryCaptureIndexRoot(
                    fixtureDir,
                    _ => true,
                    entryOpenBoundary: null,
                    directoryEnumerationBoundary: null,
                    directoryEnumerationCompleted: null,
                    out var authorization,
                    out var error),
                error);
            using (authorization)
            {
                foreach (var fileName in new[] { ".gitignore", ".gitmodules" })
                {
                    using var stream = authorization!.OpenAuthorizedRead(
                        LongPath.EnsureWindowsPrefix(Path.Combine(deepDirectory, fileName)));
                    Assert.True(stream.Length > 0);
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_RejectsScopedUnsupportedModesWithoutDryRun_Issue3543()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_scope_reject_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.py"), "def run():\n    return 1\n");
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir,
                        ["files"] = new JsonArray(JsonValue.Create("app.py")),
                    },
                },
            };

            var response = _server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["index_started"]!.GetValue<bool>());
            Assert.Equal("files", Assert.Single(structured["unsupported_modes"]!.AsArray())!["name"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_AppliesMaxSymbolsPerFile_Issue3543()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_max_symbols_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_max_symbols");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.py"), "class App:\n    def one(self):\n        return 1\n    def two(self):\n        return 2\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir,
                        ["maxSymbolsPerFile"] = 1,
                    },
                },
            };

            var response = server.HandleMessage(request)!;

            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(0, structured["summary"]!["symbols"]!.GetValue<long>());
            Assert.True(structured["summary"]!["errors"]!.GetValue<int>() == 0);
            Assert.Equal(1, structured["index_options"]!["maxSymbolsPerFile"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RebuildRestampsExtractorVersionForZeroSymbolLanguage()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_zero_symbol_version_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_zero_symbol_version");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "empty.py"), "# intentionally no declarations\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("python"), "0");
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var response = CallIndex(server, fixtureDir, args => args["rebuild"] = true);

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
            Assert.Equal(0, response["result"]!["structuredContent"]!["summary"]!["symbols"]!.GetValue<long>());
            using var verify = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            verify.TryMigrateForRead();
            Assert.Equal(
                SymbolExtractor.GetContractVersion("python").ToString(CultureInfo.InvariantCulture),
                verify.GetMetaString(DbContext.GetSymbolExtractorVersionMetaKey("python")));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_ReprocessesUnchangedFilesWhenMaxSymbolsPerFileChanges_Issue3543()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_max_symbols_reuse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_max_symbols_reuse");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.py"), "class App:\n    def one(self):\n        return 1\n    def two(self):\n        return 2\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstResponse = CallIndex(server, fixtureDir);

            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.True(ReadSymbolKindCounts(dbPath).Values.Sum() > 1);

            var secondResponse = CallIndex(server, fixtureDir, args => args["maxSymbolsPerFile"] = 1);

            Assert.False(secondResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            var structured = secondResponse["result"]!["structuredContent"]!;
            Assert.Equal(0, structured["summary"]!["symbols"]!.GetValue<long>());
            Assert.Empty(ReadSymbolKindCounts(dbPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_ReprocessesUnchangedFilesWhenSymbolKindFilterChanges_Issue3543()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_symbol_filter_reuse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_symbol_filter_reuse");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.py"), "class App:\n    def run(self):\n        return 1\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstResponse = CallIndex(server, fixtureDir);

            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            var firstCounts = ReadSymbolKindCounts(dbPath);
            Assert.True(firstCounts.GetValueOrDefault("function") > 0);

            var secondResponse = CallIndex(server, fixtureDir, args => args["excludeSymbolKind"] = "function");

            Assert.False(secondResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            var secondCounts = ReadSymbolKindCounts(dbPath);
            Assert.True(secondCounts.GetValueOrDefault("class") > 0);
            Assert.False(secondCounts.ContainsKey("function"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_WithoutCSharpSkipsCSharpPrepass()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_no_csharp_prepass_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_no_csharp_prepass");
        var ranCSharpPrepass = false;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.ts"), "interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(fixtureDir, "tool.py"), "def run():\n    return 1\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            McpServer.McpIndexCSharpPrepassForTesting = () => ranCSharpPrepass = true;

            var response = CallIndex(server, fixtureDir);

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.False(ranCSharpPrepass);
        }
        finally
        {
            McpServer.McpIndexCSharpPrepassForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_FreshWithoutTypeScriptSkipsTypeScriptAugmentationRebuild()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_fresh_no_ts_augmentation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_fresh_no_ts_augmentation");
        var rebuiltTypeScriptAugmentation = false;
        var foldBackfillVerifications = 0;
        var languagePresenceChecks = 0;
        var indexedLanguageReads = 0;
        var statReuseLookups = 0;
        var reusableLookups = 0;
        var countReads = 0;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(fixtureDir, "tool.py"), "def run():\n    return 1\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            McpServer.McpIndexTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;
            DbWriter.FoldBackfillVerificationForTesting = () => foldBackfillVerifications++;
            DbWriter.LanguagePresenceCheckForTesting = _ => languagePresenceChecks++;
            DbWriter.IndexedLanguagesReadForTesting = () => indexedLanguageReads++;
            DbWriter.ReusableUnchangedFileLookupForTesting = _ => reusableLookups++;
            DbWriter.CountsReadForTesting = () => countReads++;
            IndexedFileStatReuse.LookupForTesting = _ => statReuseLookups++;

            var response = CallIndex(server, fixtureDir);

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
            Assert.False(rebuiltTypeScriptAugmentation);
            Assert.Equal(1, foldBackfillVerifications);
            Assert.Equal(0, languagePresenceChecks);
            Assert.Equal(0, indexedLanguageReads);
            Assert.Equal(0, statReuseLookups);
            Assert.Equal(0, reusableLookups);
            Assert.Equal(0, countReads);
            Assert.Equal(2, response["result"]!["structuredContent"]!["summary"]!["files"]!.GetValue<long>());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            Assert.Equal(
                DbContext.TypeScriptAugmentationVersion.ToString(CultureInfo.InvariantCulture),
                db.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
        }
        finally
        {
            McpServer.McpIndexTypeScriptAugmentationRebuildForTesting = null;
            DbWriter.FoldBackfillVerificationForTesting = null;
            DbWriter.LanguagePresenceCheckForTesting = null;
            DbWriter.IndexedLanguagesReadForTesting = null;
            DbWriter.ReusableUnchangedFileLookupForTesting = null;
            DbWriter.CountsReadForTesting = null;
            IndexedFileStatReuse.LookupForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_NoOpSkipsUnchangedFinalizers()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_noop_ts_augmentation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_noop_ts_augmentation");
        var resolvedCSharpMetadataTargets = false;
        var rebuiltTypeScriptAugmentation = false;
        var optimizedFts = false;
        var discoveredPostExtractionHooks = false;
        var loadedPaths = new List<string>();
        var statSnapshotReads = 0;
        var foldBackfillVerifications = 0;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(fixtureDir, "app.ts"), "interface AppApi { run(): void; }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstResponse = CallIndex(server, fixtureDir);

            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false);

            McpServer.McpIndexFileContentLoadForTesting = path => loadedPaths.Add(path);
            McpServer.McpIndexPostExtractionHookDiscoveryForTesting = () => discoveredPostExtractionHooks = true;
            McpServer.McpIndexFtsOptimizeForTesting = () => optimizedFts = true;
            McpServer.McpIndexCSharpMetadataResolveForTesting = () => resolvedCSharpMetadataTargets = true;
            McpServer.McpIndexTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;
            DbWriter.ReusableStatSnapshotReadForTesting = () => statSnapshotReads++;
            DbWriter.FoldBackfillVerificationForTesting = () => foldBackfillVerifications++;

            var secondResponse = CallIndex(server, fixtureDir);

            Assert.False(secondResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.Empty(loadedPaths);
            Assert.False(discoveredPostExtractionHooks);
            Assert.False(optimizedFts);
            Assert.False(resolvedCSharpMetadataTargets);
            Assert.False(rebuiltTypeScriptAugmentation);
            Assert.Equal(1, statSnapshotReads);
            Assert.Equal(1, foldBackfillVerifications);
        }
        finally
        {
            McpServer.McpIndexFileContentLoadForTesting = null;
            McpServer.McpIndexPostExtractionHookDiscoveryForTesting = null;
            McpServer.McpIndexFtsOptimizeForTesting = null;
            McpServer.McpIndexCSharpMetadataResolveForTesting = null;
            McpServer.McpIndexTypeScriptAugmentationRebuildForTesting = null;
            DbWriter.ReusableStatSnapshotReadForTesting = null;
            DbWriter.FoldBackfillVerificationForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_InvalidLegacyModifiedReindexesFile()
    {
        var fixtureDir = Path.Combine(
            Path.GetFullPath("."),
            $"cdidx_mcp_invalid_legacy_modified_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_invalid_legacy_modified");
        var loadedPaths = new System.Collections.Concurrent.ConcurrentBag<string>();
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var firstResponse = CallIndex(server, fixtureDir);
            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false, firstResponse.ToJsonString());

            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE files SET modified = 'not-a-timestamp' WHERE path = 'app.cs'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            McpServer.McpIndexFileContentLoadForTesting = path => loadedPaths.Add(path);
            var secondResponse = CallIndex(server, fixtureDir);

            Assert.False(secondResponse["result"]?["isError"]?.GetValue<bool>() ?? false, secondResponse.ToJsonString());
            Assert.Contains("app.cs", loadedPaths);
        }
        finally
        {
            McpServer.McpIndexFileContentLoadForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public async Task ToolsCall_Index_StatSnapshotObservesRequestCancellationToken()
    {
        var fixtureDir = Path.Combine(
            Path.GetFullPath("."),
            $"cdidx_mcp_stat_snapshot_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_stat_snapshot_cancel");
        using var cancellation = new CancellationTokenSource();
        var hookInvoked = false;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var firstResponse = CallIndex(server, fixtureDir);
            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false, firstResponse.ToJsonString());

            DbWriter.ReusableStatSnapshotReadForTesting = () =>
            {
                hookInvoked = true;
                cancellation.Cancel();
            };
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = fixtureDir },
                },
            };
            var transport = new QueuedFrameTransport(request.ToJsonString());

            await server.RunAsync(transport, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(hookInvoked);
        }
        finally
        {
            DbWriter.ReusableStatSnapshotReadForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public async Task ToolsCall_Index_LaterPlannerCancellationDoesNotRerunMaintenanceOnDispose_Issue4591()
    {
        var fixtureDir = Path.Combine(
            Path.GetFullPath("."),
            $"cdidx_mcp_planner_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_planner_cancel");
        using var cancellation = new CancellationTokenSource();
        var plannerCallCount = 0;
        try
        {
            var sourcePath = Path.Combine(fixtureDir, "app.py");
            File.WriteAllText(sourcePath, "print('first')\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var firstResponse = CallIndex(server, fixtureDir);
            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false, firstResponse.ToJsonString());

            File.WriteAllText(sourcePath, "print('second')\n");
            DbContext.PlannerStatisticsCommandCreatedForTesting = _ =>
            {
                plannerCallCount++;
                cancellation.Cancel();
            };
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject { ["path"] = fixtureDir },
                },
            };
            var transport = new QueuedFrameTransport(request.ToJsonString());

            await server.RunAsync(transport, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
            server.Dispose();

            Assert.Equal(1, plannerCallCount);
        }
        finally
        {
            DbContext.PlannerStatisticsCommandCreatedForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public async Task ToolsCall_Index_CancelledDuringCSharpContractPreflight_DoesNotPurgeStaleFiles()
    {
        var fixtureDir = Path.Combine(
            Path.GetFullPath("."),
            $"cdidx_mcp_contract_preflight_cancel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_contract_preflight_cancel");
        var previousPreflightHook = DbWriter.CSharpContractPreflightForTesting;
        using var cancellation = new CancellationTokenSource();
        var hookInvoked = false;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "keep.cs"), "public class Keep { }\n");
            var stalePath = Path.Combine(fixtureDir, "stale.cs");
            File.WriteAllText(stalePath, "public class Stale { }\n");

            using (var server = new McpServer(dbPath, ConsoleUi.LoadVersion()))
            {
                var firstResponse = CallIndex(server, fixtureDir);
                Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false, firstResponse.ToJsonString());
                File.Delete(stalePath);

                DbWriter.CSharpContractPreflightForTesting = () =>
                {
                    hookInvoked = true;
                    cancellation.Cancel();
                    previousPreflightHook?.Invoke();
                };
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "index",
                        ["arguments"] = new JsonObject { ["path"] = fixtureDir },
                    },
                };
                var transport = new QueuedFrameTransport(request.ToJsonString());

                await server.RunAsync(transport, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.True(hookInvoked);
            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE path = 'stale.cs'";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.CSharpContractPreflightForTesting = previousPreflightHook;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_ReprocessesAfterPartialSymbolKindFilterChange_Issue3543()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_symbol_filter_partial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_symbol_filter_partial");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.py"), "class App:\n    def run(self):\n        return 1\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstResponse = CallIndex(server, fixtureDir);

            Assert.False(firstResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.True(ReadSymbolKindCounts(dbPath).GetValueOrDefault("function") > 0);

            var throwOnce = true;
            McpServer.McpIndexFileCommittedForTesting = _ =>
            {
                if (!throwOnce)
                    return;
                throwOnce = false;
                throw new InvalidOperationException("forced partial MCP index failure");
            };

            var partialResponse = CallIndex(server, fixtureDir, args => args["excludeSymbolKind"] = "function");

            var partialStructured = partialResponse["result"]!["structuredContent"]!;
            Assert.True(partialStructured["summary"]!["errors"]!.GetValue<int>() > 0);
            Assert.False(ReadSymbolKindCounts(dbPath).ContainsKey("function"));

            McpServer.McpIndexFileCommittedForTesting = null;
            var finalResponse = CallIndex(server, fixtureDir);

            Assert.False(finalResponse["result"]?["isError"]?.GetValue<bool>() ?? false);
            Assert.True(ReadSymbolKindCounts(dbPath).GetValueOrDefault("function") > 0);
        }
        finally
        {
            McpServer.McpIndexFileCommittedForTesting = null;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_UnknownToolName_TruncatesDisplay_Issue3118()
    {
        var toolName = new string('t', McpBoundedText.MaxToolNameChars + 25);
        var display = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = new JsonObject
                {
                    ["x"] = 1,
                },
            },
        };

        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        Assert.DoesNotContain(toolName, response.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal($"Unknown tool: {display.Text}", response["error"]!["message"]!.GetValue<string>());
        var data = response["error"]!["data"]!;
        Assert.Equal(display.Text, data["tool"]!.GetValue<string>());
        Assert.Equal(toolName.Length, data["tool_length"]!.GetValue<int>());
        Assert.True(data["tool_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_UnknownToolName_TruncatesMetricsLabels_Issue3118()
    {
        var toolName = new string('m', McpBoundedText.MaxToolNameChars + 25);
        var language = new string('l', McpBoundedText.MaxDiagnosticDisplayChars + 25);
        var toolDisplay = McpBoundedText.ForDisplay(toolName, McpBoundedText.MaxToolNameChars);
        var languageDisplay = McpBoundedText.ForDisplay(language);
        var metricsPath = TestProjectHelper.CreateTempFilePath("cdidx_mcp_metrics", ".jsonl");
        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["arguments"] = new JsonObject
                    {
                        ["lang"] = language,
                    },
                },
            };

            var response = _server.HandleMessage(request)!;

            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
            Assert.True(session.WaitForIdle(TimeSpan.FromSeconds(5)));
            var line = Assert.Single(File.ReadAllLines(metricsPath));
            Assert.DoesNotContain(toolName, line, StringComparison.Ordinal);
            Assert.DoesNotContain(language, line, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            Assert.Equal(toolDisplay.Text, root.GetProperty("tool").GetString());
            Assert.Equal(languageDisplay.Text, root.GetProperty("language").GetString());
            Assert.Equal(1, root.GetProperty("exit_code").GetInt32());
            Assert.Equal("unknown_tool", root.GetProperty("error").GetString());
            var requestId = root.GetProperty("request_id").GetString()!;
            Assert.StartsWith(McpRequestIdTelemetry.TokenPrefix, requestId, StringComparison.Ordinal);
            Assert.Equal(McpRequestIdTelemetry.TokenLength, requestId.Length);
            Assert.Equal("number", root.GetProperty("request_id_type").GetString());
            Assert.Equal(1, root.GetProperty("request_id_length").GetInt32());
        }
        finally
        {
            DeleteFileRobust(metricsPath);
        }
    }

    [Fact]
    public async Task ToolsCall_ResponseCompletesWhileMetricsWriterIsBlocked_Issue4552()
    {
        var metricsPath = TestProjectHelper.CreateTempFilePath("cdidx_mcp_metrics_blocked", ".jsonl");
        using var writerEntered = new ManualResetEventSlim(false);
        using var releaseWriter = new ManualResetEventSlim(false);
        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            session.BeforeBatchWriteForTests = () =>
            {
                writerEntered.Set();
                releaseWriter.Wait(TimeSpan.FromSeconds(5));
            };
            var request = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"not-a-tool","arguments":{}}}""")!;

            var responseTask = Task.Run(() => _server.HandleMessage(request));

            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));
            var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
            Assert.Equal(1, session.SnapshotDiagnostics().QueuedEventCount);
            Assert.Equal(0, session.SnapshotDiagnostics().WrittenEventCount);
            releaseWriter.Set();
            Assert.True(session.WaitForIdle(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseWriter.Set();
            DeleteFileRobust(metricsPath);
        }
    }

    [Fact]
    public void ToolsCall_HighCardinalityCredentialRequestIds_KeepMetricsTokensFixedAndOpaque_Issue4551()
    {
        var metricsPath = TestProjectHelper.CreateTempFilePath("cdidx_mcp_metrics_request_ids", ".jsonl");
        var ids = Enumerable.Range(0, 32)
            .Select(index => $"Bearer metrics-secret-{index:D2}-" + new string((char)('a' + (index % 26)), 64))
            .ToArray();
        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            foreach (var id in ids)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "ping",
                        ["arguments"] = new JsonObject(),
                    },
                };

                var response = _server.HandleMessage(request)!;
                Assert.Equal(id, response["id"]!.GetValue<string>());
            }

            Assert.True(session.WaitForIdle(TimeSpan.FromSeconds(5)), "metrics writer did not become idle");
            var lines = File.ReadAllLines(metricsPath);
            Assert.Equal(ids.Length, lines.Length);
            var rawMetrics = string.Join('\n', lines);
            Assert.All(ids, id => Assert.DoesNotContain(id, rawMetrics, StringComparison.Ordinal));
            var records = lines
                .Select(ParseTelemetryRecord)
                .ToArray();
            Assert.All(records, record =>
            {
                var token = record.GetProperty("request_id").GetString()!;
                Assert.StartsWith(McpRequestIdTelemetry.TokenPrefix, token, StringComparison.Ordinal);
                Assert.Equal(McpRequestIdTelemetry.TokenLength, token.Length);
                Assert.Equal("string", record.GetProperty("request_id_type").GetString());
                Assert.Equal(ids[0].Length, record.GetProperty("request_id_length").GetInt32());
            });
            Assert.Equal(ids.Length, records
                .Select(record => record.GetProperty("request_id").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count());
        }
        finally
        {
            DeleteFileRobust(metricsPath);
        }
    }

    [Fact]
    public void ProcessFrame_BatchToolCallsUseItemRequestIdsAcrossStderrAndMetrics_Issue4551()
    {
        var metricsPath = TestProjectHelper.CreateTempFilePath("cdidx_mcp_batch_request_ids", ".jsonl");
        var auditPath = TestProjectHelper.CreateTempFilePath("cdidx_mcp_batch_request_ids_audit", ".jsonl");
        var ids = new[]
        {
            "Bearer batch-secret-alpha-4551",
            "sk-proj-batch-secret-beta-4551",
        };
        var expected = ids
            .Select(id => McpRequestIdTelemetry.Create(JsonValue.Create(id)))
            .ToArray();
        var batch = new JsonArray();
        foreach (var id in ids)
        {
            batch.Add(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "ping",
                    ["arguments"] = new JsonObject(),
                },
            });
        }

        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            using var auditSink = new AuditLogSink(
                auditPath,
                AuditLogSink.DefaultMaxBytes,
                includeValues: false);
            using var server = new McpServer(
                _dbPath,
                ConsoleUi.LoadVersion(),
                dbPathExplicit: false,
                authenticator: null,
                auditLog: auditSink);
            using var error = new StringWriter();
            string? response;
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
                    response = server.ProcessFrame(batch.ToJsonString());
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }

            Assert.NotNull(response);
            using (var responseDocument = JsonDocument.Parse(response))
            {
                Assert.Equal(
                    ids,
                    responseDocument.RootElement.EnumerateArray()
                        .Select(item => item.GetProperty("id").GetString())
                        .ToArray());
            }

            Assert.True(session.WaitForIdle(TimeSpan.FromSeconds(5)), "metrics writer did not become idle");
            var metricsText = File.ReadAllText(metricsPath);
            Assert.True(auditSink.WaitForIdle(TimeSpan.FromSeconds(5)), "audit log writer did not become idle");
            var auditText = File.ReadAllText(auditPath);
            var stderrText = error.ToString();
            Assert.All(ids, id =>
            {
                Assert.DoesNotContain(id, metricsText, StringComparison.Ordinal);
                Assert.DoesNotContain(id, auditText, StringComparison.Ordinal);
                Assert.DoesNotContain(id, stderrText, StringComparison.Ordinal);
            });

            var metricsRecords = File.ReadAllLines(metricsPath)
                .Select(ParseTelemetryRecord)
                .ToDictionary(
                    record => record.GetProperty("request_id").GetString()!,
                    StringComparer.Ordinal);
            var auditRecords = File.ReadAllLines(auditPath)
                .Select(ParseTelemetryRecord)
                .ToDictionary(
                    record => record.GetProperty("request_id").GetString()!,
                    StringComparer.Ordinal);
            var stderrRecords = stderrText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal))
                .Select(line => ParseTelemetryRecord(line[line.IndexOf('{')..]))
                .ToDictionary(
                    record => record.GetProperty("request_id").GetString()!,
                    StringComparer.Ordinal);

            Assert.Equal(ids.Length, metricsRecords.Count);
            Assert.Equal(ids.Length, auditRecords.Count);
            Assert.Equal(ids.Length, stderrRecords.Count);
            foreach (var requestId in expected)
            {
                var metric = metricsRecords[requestId.Token];
                var audit = auditRecords[requestId.Token];
                var stderr = stderrRecords[requestId.Token];
                Assert.Equal(requestId.Type, metric.GetProperty("request_id_type").GetString());
                Assert.Equal(requestId.Length, metric.GetProperty("request_id_length").GetInt32());
                Assert.Equal(requestId.Type, audit.GetProperty("request_id_type").GetString());
                Assert.Equal(requestId.Length, audit.GetProperty("request_id_length").GetInt32());
                Assert.Equal(requestId.Type, stderr.GetProperty("request_id_type").GetString());
                Assert.Equal(requestId.Length, stderr.GetProperty("request_id_length").GetInt32());
            }
        }
        finally
        {
            DeleteFileRobust(metricsPath);
            DeleteFileRobust(auditPath);
        }
    }

    [Fact]
    public void ProcessFrame_BatchExplicitNullIdIsDistinctFromAbsentAndInvalidTelemetry_Issue4551()
    {
        var metricsPath = TestProjectHelper.CreateTempFilePath("cdidx_mcp_batch_null_request_id", ".jsonl");
        var batch = JsonNode.Parse(
            """[{"jsonrpc":"2.0","id":null,"method":"tools/call","params":{"name":"ping","arguments":{}}},{"jsonrpc":"2.0","method":"tools/call","params":{"name":"ping","arguments":{}}},{"jsonrpc":"2.0","id":true,"method":"tools/call","params":{"name":"ping","arguments":{}}}]""")!;
        var expected = McpRequestIdTelemetry.Create(id: (JsonNode?)null);

        try
        {
            using var session = MetricsSink.TryStartForTesting(metricsPath, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            using var error = new StringWriter();
            string? response;
            lock (TestConsoleLock.Gate)
            {
                var previousError = Console.Error;
                try
                {
                    Console.SetError(error);
                    response = _server.ProcessFrame(batch.ToJsonString());
                }
                finally
                {
                    Console.SetError(previousError);
                }
            }

            Assert.NotNull(response);
            using (var responseDocument = JsonDocument.Parse(response))
                Assert.Equal(2, responseDocument.RootElement.GetArrayLength());

            Assert.True(session.WaitForIdle(TimeSpan.FromSeconds(5)), "metrics writer did not become idle");
            var metric = Assert.Single(File.ReadAllLines(metricsPath).Select(ParseTelemetryRecord));
            Assert.Equal(expected.Token, metric.GetProperty("request_id").GetString());
            Assert.Equal("null", metric.GetProperty("request_id_type").GetString());
            Assert.Equal(0, metric.GetProperty("request_id_length").GetInt32());

            var invocation = Assert.Single(error.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("\"event\":\"mcp.tool.invocation\"", StringComparison.Ordinal)));
            Assert.Contains(expected.Token, invocation, StringComparison.Ordinal);
            Assert.Contains("\"request_id_type\":\"null\"", invocation, StringComparison.Ordinal);
            Assert.Contains("\"request_id_length\":0", invocation, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFileRobust(metricsPath);
        }
    }

    private static JsonElement ParseTelemetryRecord(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ToolsCall_Index_WhenDbLockHeld_ReturnsBusyErrorAndAuditsCheckedRootIdentity_Issue4606()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_lock_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_lock");
        var auditPath = Path.Combine(Path.GetTempPath(), $"cdidx_mcp_index_lock_{Guid.NewGuid():N}.jsonl");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var infoPath = lockPath + ".info";
        File.WriteAllText(
            infoPath,
            $$"""{"pid":{{Environment.ProcessId}},"since":"2026-01-02T03:04:05.0000000+00:00"}""");
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using (var sink = new AuditLogSink(auditPath, AuditLogSink.DefaultMaxBytes, includeValues: false))
            using (var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true, sink))
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject
                    {
                        ["name"] = "index",
                        ["arguments"] = new JsonObject
                        {
                            ["path"] = fixtureDir
                        }
                    }
                };

                var response = server.HandleMessage(request)!;

                Assert.True(response["result"]!["isError"]!.GetValue<bool>());
                var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
                Assert.Contains("index already running on this DB", text);
                Assert.Contains($"pid {Environment.ProcessId}", text);
                Assert.Contains("2026-01-02T03:04:05", text);
            }

            var auditRecord = Assert.Single(File.ReadAllLines(auditPath));
            using var auditJson = JsonDocument.Parse(auditRecord);
            Assert.StartsWith(
                "fsid:v1:",
                auditJson.RootElement.GetProperty("checked_root_identity").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            heldLock.Dispose();
            TestProjectHelper.DeleteFile(infoPath);
            TestProjectHelper.DeleteFile(lockPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            if (File.Exists(auditPath))
                File.Delete(auditPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_WhenDbLockInfoTooLarge_ReturnsBusyWithoutHolderDetails()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_large_lock_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_large_lock");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var infoPath = lockPath + ".info";
        File.WriteAllText(infoPath, $$"""{"pid":{{Environment.ProcessId}},"since":"2026-01-02T03:04:05.0000000+00:00","padding":"{{new string('x', 5 * 1024)}}"}""");
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("index already running on this DB", text);
            Assert.Contains("holder metadata unavailable", text);
            Assert.DoesNotContain($"pid {Environment.ProcessId}", text);
        }
        finally
        {
            heldLock.Dispose();
            TestProjectHelper.DeleteFile(infoPath);
            TestProjectHelper.DeleteFile(lockPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_WhenDbLockInfoTooDeep_ReturnsBusyWithoutHolderDetails_Issue3043()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_deep_lock_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_deep_lock");
        var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var infoPath = lockPath + ".info";
        var info = new StringBuilder($$"""{"pid":{{Environment.ProcessId}},"since":"2026-01-02T03:04:05.0000000+00:00","extra":""");
        AppendNestedObject(info, McpIndexRunLock.MaxInfoJsonDepth + 1);
        info.Append('}');
        File.WriteAllText(infoPath, info.ToString());
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("index already running on this DB", text);
            Assert.Contains("holder metadata unavailable", text);
            Assert.DoesNotContain($"pid {Environment.ProcessId}", text);
        }
        finally
        {
            heldLock.Dispose();
            TestProjectHelper.DeleteFile(infoPath);
            TestProjectHelper.DeleteFile(lockPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_NonexistentDir_ReturnsError()
    {
        // Use a path within CWD that doesn't exist / CWD内の存在しないパスを使用
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"./nonexistent_subdir_xyz_test"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_Index_PopulatesValidateIssuesLikeCliIndex()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var filePath = Path.Combine(fixtureDir, "bom_sample.cs");
        try
        {
            File.WriteAllBytes(filePath, [0xEF, 0xBB, 0xBF, (byte)'c', (byte)'l', (byte)'a', (byte)'s', (byte)'s', (byte)' ', (byte)'A', (byte)' ', (byte)'{', (byte)'}', (byte)'\n']);

            var indexRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var indexResponse = _server.HandleMessage(indexRequest)!;
            Assert.False(indexResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            var validateRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"validate","arguments":{}}}""")!;
            var validateResponse = _server.HandleMessage(validateRequest)!;
            var issues = validateResponse["result"]!["structuredContent"]!["issues"]!.AsArray();

            Assert.Contains(issues, issue => issue!["kind"]!.GetValue<string>() == "bom");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_FatalScanErrorDoesNotRestampReadiness_Issue2874()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_scan_error_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_scan_error");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };

            var firstResponse = server.HandleMessage(request)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var readyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var userVersion = readyDb.GetUserVersion();
                Assert.NotEqual(0, userVersion & DbContext.GraphReadyFlag);
                Assert.NotEqual(0, userVersion & DbContext.IssuesReadyFlag);
            }

            File.WriteAllText(Path.Combine(fixtureDir, ".gitignore"), new string('a', 256 * 1024 + 1));
            request["id"] = 2;

            var secondResponse = server.HandleMessage(request)!;
            var structured = secondResponse["result"]!["structuredContent"]!;

            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Equal(1, structured["summary"]!["errors"]!.GetValue<int>());
            Assert.Equal(1, structured["summary"]!["failed_count"]!.GetValue<int>());
            var failure = Assert.Single(structured["failures"]!.AsArray());
            Assert.Equal(".gitignore", failure!["path"]!.GetValue<string>());
            Assert.Equal("scan", failure["stage"]!.GetValue<string>());
            Assert.Equal(nameof(FileIndexer.ScanError), failure["exception_type"]!.GetValue<string>());
            Assert.False(failure["message_truncated"]!.GetValue<bool>());
            Assert.DoesNotContain(fixtureDir, failure["message"]!.GetValue<string>());
            Assert.DoesNotContain("\n", failure["message"]!.GetValue<string>());
            var diagnostics = structured["diagnostics"]!;
            Assert.Equal(1, diagnostics["total_count"]!.GetValue<int>());
            Assert.False(diagnostics["truncated"]!.GetValue<bool>());
            Assert.Equal(1, diagnostics["categories"]!["recoverable_index_error"]!.GetValue<int>());
            var diagnostic = Assert.Single(diagnostics["items"]!.AsArray());
            Assert.Equal("recoverable_index_error", diagnostic!["code"]!.GetValue<string>());
            Assert.Equal("recoverable_index_error", diagnostic["category"]!.GetValue<string>());
            Assert.Equal(".gitignore", diagnostic["path"]!.GetValue<string>());
            Assert.Equal("scan", diagnostic["stage"]!.GetValue<string>());
            Assert.Equal(nameof(FileIndexer.ScanError), diagnostic["exception_type"]!.GetValue<string>());
            Assert.DoesNotContain(fixtureDir, diagnostic["message"]!.GetValue<string>());

            using var failedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var failedUserVersion = failedDb.GetUserVersion();
            Assert.Equal(0, failedUserVersion & DbContext.GraphReadyFlag);
            Assert.Equal(0, failedUserVersion & DbContext.IssuesReadyFlag);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_FailedFirstMutation_DoesNotRewriteIndexedProjectRootMetadata()
    {
        var projectRootA = TestProjectHelper.CreateTempProject("cdidx_mcp_index_root_a");
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_root_b_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_root");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRootA);
            var sourcePathA = Path.Combine(projectRootA, "app.cs");
            File.WriteAllText(sourcePathA, "public class AppA { public void Run() { } }\n");
            TestProjectHelper.RunGit(projectRootA, "add", "app.cs");
            TestProjectHelper.RunGit(projectRootA, "commit", "-m", "init-a");
            var headA = TestProjectHelper.RunGit(projectRootA, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            });
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER fail_update
                    BEFORE UPDATE ON files
                    BEGIN
                        SELECT RAISE(FAIL, 'boom');
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            Directory.CreateDirectory(fixtureDir);
            TestProjectHelper.InitializeGitRepo(fixtureDir);
            var sourcePathB = Path.Combine(fixtureDir, "app.cs");
            File.WriteAllText(sourcePathB, "public class AppB { public void Run() { } public void Extra() { } }\n");
            TestProjectHelper.RunGit(fixtureDir, "add", "app.cs");
            TestProjectHelper.RunGit(fixtureDir, "commit", "-m", "init-b");
            File.SetLastWriteTimeUtc(sourcePathB, DateTime.UtcNow.AddSeconds(2));

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var indexRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var indexResponse = server.HandleMessage(indexRequest)!;
            Assert.Equal(1, indexResponse["result"]!["structuredContent"]!["summary"]!["errors"]!.GetValue<int>());

            var statusRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var statusResponse = server.HandleMessage(statusRequest)!;

            Assert.Equal(projectRootA, statusResponse["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(headA, statusResponse["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRootA);
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_SuccessfulNoOpBackfillsMissingIndexedProjectRootMetadata()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_noop_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_noop");
        try
        {
            Directory.CreateDirectory(fixtureDir);
            TestProjectHelper.InitializeGitRepo(fixtureDir);
            var sourcePath = Path.Combine(fixtureDir, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            TestProjectHelper.RunGit(fixtureDir, "add", "app.cs");
            TestProjectHelper.RunGit(fixtureDir, "commit", "-m", "init");
            var expectedHead = TestProjectHelper.RunGit(fixtureDir, "rev-parse", "HEAD").Trim();

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var indexRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(indexRequest)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
                cmd.ExecuteNonQuery();
            }

            var secondResponse = server.HandleMessage(indexRequest)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Equal(1, secondResponse["result"]!["structuredContent"]!["summary"]!["skipped"]!.GetValue<int>());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(fixtureDir), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var statusRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status","arguments":{}}}""")!;
            var statusResponse = server.HandleMessage(statusRequest)!;

            Assert.Equal(Path.GetFullPath(fixtureDir), statusResponse["result"]!["structuredContent"]!["project_root"]!.GetValue<string>());
            Assert.Equal(expectedHead, statusResponse["result"]!["structuredContent"]!["gitHead"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_NullByteFilePersistsNullByteIssue_Issue3835()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_null_byte_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_null_byte");
        try
        {
            Directory.CreateDirectory(fixtureDir);
            var prefix = Encoding.UTF8.GetBytes("public class Polluted { public void Run() { } }\n");
            var bytes = new byte[prefix.Length + 1];
            Array.Copy(prefix, bytes, prefix.Length);
            bytes[^1] = 0;
            File.WriteAllBytes(Path.Combine(fixtureDir, "binary.cs"), bytes);

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("null_byte"));
            Assert.Equal("binary.cs", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("byte offset", issue.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_MaxReferencesPerFilePersistsReferenceCountExceededIssue_Issue3719()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_reference_cap_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_reference_cap");
        try
        {
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllText(Path.Combine(fixtureDir, "DenseReferences.cs"), BuildDenseReferenceCSharpSource(8));

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var response = CallIndex(server, fixtureDir, args => args["maxReferencesPerFile"] = 2);

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("reference_count_exceeded"));
            Assert.Equal("DenseReferences.cs", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("maxReferencesPerFile", issue.Message);

            using var command = db.Connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM chunks),
                    (SELECT COUNT(*) FROM symbols),
                    (SELECT COUNT(*) FROM symbol_references)
                """;
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.True(row.GetInt64(0) > 0);
            Assert.True(row.GetInt64(1) > 0);
            Assert.Equal(0, row.GetInt64(2));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Index_RefreshesMutualRecursionOnceAfterBulkReferenceInsert()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_mutual_recursion_{Guid.NewGuid():N}");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_mutual_recursion");
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var previousFtsOptimizeHook = McpServer.McpIndexFtsOptimizeForTesting;
        var refreshCount = 0;
        var ftsOptimizeCount = 0;
        try
        {
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllText(
                Path.Combine(fixtureDir, "MutualRecursionA.cs"),
                """
                public static class MutualRecursionA
                {
                    public static void CrossCycleA() { CrossCycleB(); }
                }
                """);
            File.WriteAllText(
                Path.Combine(fixtureDir, "MutualRecursionB.cs"),
                """
                public static class MutualRecursionB
                {
                    public static void CrossCycleB() { CrossCycleA(); }
                }
                """);

            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var response = CallIndex(server, fixtureDir);

            Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
            Assert.Equal(1, refreshCount);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.TryMigrateForRead();
                var writer = new DbWriter(db.Connection);
                Assert.True(writer.ReferenceIdentityContractMatchesCurrent());
                using var command = db.Connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1";
                Assert.Equal(2L, (long)command.ExecuteScalar()!);

                writer.ClearReferenceIdentityContractReady();
                command.CommandText = "DELETE FROM symbol_reference_candidates";
                command.ExecuteNonQuery();
            }

            refreshCount = 0;
            var repairResponse = CallIndex(server, fixtureDir);
            Assert.False(repairResponse["result"]?["isError"]?.GetValue<bool>() ?? false, repairResponse.ToJsonString());
            Assert.Equal(1, refreshCount);

            using var verification = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var verificationWriter = new DbWriter(verification.Connection);
            Assert.True(verificationWriter.ReferenceIdentityContractMatchesCurrent());
            using var candidateCommand = verification.Connection.CreateCommand();
            candidateCommand.CommandText = "SELECT COUNT(*) FROM symbol_reference_candidates";
            Assert.True((long)candidateCommand.ExecuteScalar()! > 0);

            refreshCount = 0;
            McpServer.McpIndexFtsOptimizeForTesting = () =>
            {
                ftsOptimizeCount++;
                previousFtsOptimizeHook?.Invoke();
            };
            var graphNeutralPath = Path.Combine(fixtureDir, "graph-neutral.py");
            File.WriteAllText(graphNeutralPath, "# text-only source\n");
            var neutralInsertResponse = CallIndex(server, fixtureDir);
            Assert.False(neutralInsertResponse["result"]?["isError"]?.GetValue<bool>() ?? false, neutralInsertResponse.ToJsonString());
            Assert.Equal(0, refreshCount);
            Assert.Equal(0, ftsOptimizeCount);
            Assert.Equal(1, verificationWriter.GetFtsIncrementalWritesSinceOptimize());

            verificationWriter.SetMeta(
                DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey,
                (DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold - 1).ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(graphNeutralPath, "# changed text-only source\n");
            File.SetLastWriteTimeUtc(graphNeutralPath, DateTime.UtcNow.AddSeconds(2));
            var neutralUpdateResponse = CallIndex(server, fixtureDir);
            Assert.False(neutralUpdateResponse["result"]?["isError"]?.GetValue<bool>() ?? false, neutralUpdateResponse.ToJsonString());
            Assert.Equal(0, refreshCount);
            Assert.Equal(1, ftsOptimizeCount);
            Assert.Equal(0, verificationWriter.GetFtsIncrementalWritesSinceOptimize());
        }
        finally
        {
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            McpServer.McpIndexFtsOptimizeForTesting = previousFtsOptimizeHook;
            TestProjectHelper.DeleteDirectory(fixtureDir);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_StampsFoldReady()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.False(structured["dry_run"]!.GetValue<bool>());
        Assert.False(structured["was_already_complete"]!.GetValue<bool>());
        Assert.False(structured["fold_ready_before"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_after"]!.GetValue<bool>());
        Assert.True(structured["verified"]!.GetValue<bool>());
        Assert.Equal(27, structured["user_version_before"]!.GetValue<int>());
        Assert.Equal(31, structured["user_version_after"]!.GetValue<int>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());
        Assert.Equal(2, structured["progress"]!["rows_done"]!.GetValue<int>());
        Assert.Equal(2, structured["progress"]!["rows_total"]!.GetValue<int>());
        Assert.Equal(1.0, structured["progress"]!["fraction"]!.GetValue<double>());

        using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        verifyDb.TryMigrateForRead();
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
    }

    [Fact]
    public void ToolsCall_BackfillFold_ExceptionUsesSanitizedToolError_Issue3201()
    {
        var previousDebug = Environment.GetEnvironmentVariable(McpServer.DebugEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, null);
            DbWriter.FoldBackfillRowUpdatedForTesting = () =>
                throw new InvalidOperationException("SECRET_BACKFILL_LITERAL from /private/path");

            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
            var response = _server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>(), response.ToJsonString());
            var text = response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Equal("Tool 'backfill_fold' failed. See cdidx server stderr for details.", text);
            Assert.DoesNotContain("SECRET_BACKFILL_LITERAL", response.ToJsonString());
            Assert.DoesNotContain("/private/path", response.ToJsonString());

            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal("internal_error", structured["category"]!.GetValue<string>());
            Assert.Equal("backfill_fold", structured["tool"]!.GetValue<string>());
            Assert.Equal(nameof(InvalidOperationException), structured["exception_type"]!.GetValue<string>());
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            Environment.SetEnvironmentVariable(McpServer.DebugEnvironmentVariable, previousDebug);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_DryRunDoesNotWrite()
    {
        var writer = new DbWriter(_db.Connection);
        writer.BackfillFoldedColumns(rewriteAll: true);
        writer.MarkFoldReady();
        writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{"dry_run":true}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["dry_run"]!.GetValue<bool>());
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(2, structured["progress"]!["rows_total"]!.GetValue<int>());
        Assert.False(structured["verified"]!.GetValue<bool>());
        Assert.False(structured["fold_ready_before"]!.GetValue<bool>());
        Assert.False(structured["fold_ready_after"]!.GetValue<bool>());
        Assert.False(structured["fold_ready"]!.GetValue<bool>());

        Assert.Equal("DEADBEEFDEADBEEF", _db.GetMetaString("fold_key_fingerprint"));
        Assert.Equal(DbContext.CurrentSchemaVersion, _db.GetUserVersion());
    }

    [Fact]
    public void ToolsCall_BackfillFold_SecondRunSignalsAlreadyComplete()
    {
        var first = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        Assert.False(_server.HandleMessage(first)!["result"]!["isError"]?.GetValue<bool>() ?? false);

        var second = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(second)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(0, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.Equal(0, structured["progress"]!["rows_total"]!.GetValue<int>());
        Assert.False(structured["rewrite_all"]!.GetValue<bool>());
        Assert.True(structured["was_already_complete"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_before"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_after"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_BackfillFold_ForceRewritesAlreadyCompleteRows()
    {
        var first = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        Assert.False(_server.HandleMessage(first)!["result"]!["isError"]?.GetValue<bool>() ?? false);

        var forced = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"backfill_fold","arguments":{"force":true}}}""")!;
        var response = _server.HandleMessage(forced)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.True(structured["force"]!.GetValue<bool>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.False(structured["was_already_complete"]!.GetValue<bool>());
        Assert.True(structured["fold_ready_after"]!.GetValue<bool>());
    }

    [Fact]
    public void ToolsCall_BackfillFold_RewritesAllWhenOnlyFingerprintDrifted()
    {
        var writer = new DbWriter(_db.Connection);
        writer.BackfillFoldedColumns(rewriteAll: true);
        writer.MarkFoldReady();
        writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["rewrite_all"]!.GetValue<bool>());
        Assert.True(structured["verified"]!.GetValue<bool>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());

        using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        verifyDb.TryMigrateForRead();
        Assert.Equal(NameFold.Fingerprint(), verifyDb.GetMetaString("fold_key_fingerprint"));
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
    }

    [Fact]
    public void ToolsCall_Index_DoesNotRestampFoldReadyWhenFoldKeyVersionMismatches()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_version_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_version");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Straße() { } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            File.WriteAllText(Path.Combine(fixtureDir, "new.cs"), "public class NewFile { }");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.False(secondResponse["result"]!["structuredContent"]!["fold_ready"]!.GetValue<bool>());
            Assert.Equal("stale_fold_key_version", secondResponse["result"]!["structuredContent"]!["fold_ready_reason"]!.GetValue<string>());
            var text = secondResponse["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("older fold-key version", text);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.NotEqual(NameFold.Version.ToString(), storedVersion);
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_ClearsHotspotFamilyTrustOnPartialFailure()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_hotspot_family_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_hotspot_family");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Run() { } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var seededDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            WriteOversizedAsciiFile(Path.Combine(fixtureDir, "app.cs"));

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Equal(1, secondResponse["result"]!["structuredContent"]!["summary"]!["errors"]!.GetValue<int>());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_Rebuild_SucceedsOnFreshDb()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_rebuild_fresh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_rebuild_fresh");
        var statSnapshotReads = 0;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            DbWriter.ReusableStatSnapshotReadForTesting = () => statSnapshotReads++;

            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir,
                        ["rebuild"] = true,
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(response["result"]!["structuredContent"]!["summary"]!["files"]!.GetValue<long>() >= 1L);
            Assert.Equal(0, statSnapshotReads);
        }
        finally
        {
            DbWriter.ReusableStatSnapshotReadForTesting = null;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_ResolvesTypeScriptPathAliasesFromProjectRoot()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_ts_alias_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_ts_alias");
        try
        {
            Directory.CreateDirectory(Path.Combine(fixtureDir, "src", "components"));
            Directory.CreateDirectory(Path.Combine(fixtureDir, "src", "pages"));
            File.WriteAllText(Path.Combine(fixtureDir, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(fixtureDir, "src", "components", "Button.tsx"), "export const Button = () => null;\n");
            File.WriteAllText(Path.Combine(fixtureDir, "src", "pages", "Page.tsx"), "import { Button } from \"@/components/Button\";\n");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir,
                        ["rebuild"] = true,
                    }
                }
            };

            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbols
                    WHERE kind = 'import'
                      AND name = 'src/components/Button.tsx'
                    """;
                Assert.Equal(1L, (long)command.ExecuteScalar()!);
            }

            Directory.CreateDirectory(Path.Combine(fixtureDir, "app", "components"));
            File.WriteAllText(Path.Combine(fixtureDir, "app", "components", "Button.tsx"), "export const UpdatedButton = () => null;\n");
            File.WriteAllText(Path.Combine(fixtureDir, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["app/*"]
                    }
                  }
                }
                """);

            response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT name, COUNT(*)
                    FROM symbols
                    WHERE kind = 'import'
                      AND name IN ('src/components/Button.tsx', 'app/components/Button.tsx')
                    GROUP BY name
                    """;
                using var reader = command.ExecuteReader();
                var counts = new Dictionary<string, long>(StringComparer.Ordinal);
                while (reader.Read())
                    counts[reader.GetString(0)] = reader.GetInt64(1);

                Assert.Equal(1L, counts["app/components/Button.tsx"]);
                Assert.False(counts.ContainsKey("src/components/Button.tsx"));
            }
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_RestampsHotspotFamilyReadyWhenMarkerFingerprintChanges()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_marker_fingerprint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var srcDir = Path.Combine(fixtureDir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_marker_fingerprint");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part1.cs"),
                """
                public partial class Api
                {
                    public void Run() { }
                }
                """);
            File.WriteAllText(Path.Combine(srcDir, "Api.Part2.cs"),
                """
                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(srcDir, "Caller.cs"),
                """
                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                        api.Run(1);
                    }
                }
                """);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var seededDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            File.WriteAllText(Path.Combine(fixtureDir, "Extra.csproj"), "<Project />");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_KeepsCsharpHotspotFamilyTrustWhenOnlyVbMarkersChange()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_marker_isolation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var srcDir = Path.Combine(fixtureDir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_marker_isolation");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(srcDir, "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            File.WriteAllText(Path.Combine(fixtureDir, "Unrelated.vbproj"), "<Project />");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var structured = hotspotsResponse["result"]!["structuredContent"]!;
            Assert.True(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            if (structured["degraded"] is JsonNode degradedNode)
                Assert.False(degradedNode.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_Index_RestampsHotspotFamilyTrustWhenOnlyMetadataWasCleared()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_marker_metadata_only_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var srcDir = Path.Combine(fixtureDir, "src");
        Directory.CreateDirectory(srcDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_marker_metadata_only");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(srcDir, "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(srcDir, "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(secondResponse["result"]!["structuredContent"]!["summary"]!["skipped"]!.GetValue<int>() > 0);

            using (var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(
                    DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
                Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
            }

            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var structured = hotspotsResponse["result"]!["structuredContent"]!;
            Assert.True(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.Equal(2, structured["count"]!.GetValue<int>());
            if (structured["degraded"] is JsonNode degradedNode)
                Assert.False(degradedNode.GetValue<bool>());
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ReportsDegradedHotspotFamilyTrust()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_hotspots_family_signal");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.Contains("hotspot_family_support_not_indexed=csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
            Assert.Contains("degraded", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ReportsLegacyNullFamilyKeysAsDegraded()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_hotspots_family_legacy");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols
                    SET family_key = NULL,
                        container_qualified_name = NULL
                    WHERE file_id IN (
                        SELECT id FROM files WHERE lang = 'csharp'
                    );
                    """;
                cmd.ExecuteNonQuery();
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.Contains("csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_ReportsMissingMarkerFingerprintAsDegraded()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_hotspots_family_missing_fingerprint");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part1.cs", "csharp", "public partial class Api { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Api.Part2.cs", "csharp", "public partial class Api { public void Run(int value) { } }");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.False(structured["hotspot_family_ready"]!.GetValue<bool>());
            Assert.Null(structured["hotspotFamilyReady"]);
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.Contains("hotspot_family_disabled_at_index_time=csharp", structured["hotspot_family_degraded_reason"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_GroupByFileReportsGroupingMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_hotspots_group_file");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp",
                """
                public class One
                {
                    private void A() { A(); A(); }
                    private void B() { B(); }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Two.cs", "csharp",
                """
                public class Two
                {
                    private void C() { C(); }
                }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function","groupBy":"file","limit":1}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            var hotspot = structured["hotspots"]!.AsArray().Single()!;
            var query = structured["query_context"]!;
            Assert.Equal("file", structured["grouped_by"]!.GetValue<string>());
            Assert.Equal("file", structured["grouping_unit"]!.GetValue<string>());
            Assert.Equal("returned_files", structured["count_kind"]!.GetValue<string>());
            Assert.Equal("files", structured["limit_applies_to"]!.GetValue<string>());
            Assert.Equal(new[] { "reference_count" }, structured["score_fields"]!.AsArray().Select(field => field!.GetValue<string>()).ToArray());
            Assert.Equal(new[] { "reference_count", "path" }, structured["ranking_fields"]!.AsArray().Select(field => field!.GetValue<string>()).ToArray());
            Assert.Equal("file", query["group_by"]!.GetValue<string>());
            Assert.Equal("file", query["grouping_unit"]!.GetValue<string>());
            Assert.Equal("returned_files", query["count_kind"]!.GetValue<string>());
            Assert.Equal("files", query["limit_applies_to"]!.GetValue<string>());
            Assert.Equal(1, structured["count"]!.GetValue<int>());
            Assert.Equal("src/One.cs", hotspot["path"]!.GetValue<string>());
            Assert.Equal(3, hotspot["reference_count"]!.GetValue<int>());
            Assert.Equal(2, hotspot["symbol_count"]!.GetValue<int>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_GroupByStatementWithoutSqlLangIsRejected_Issue4116()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"groupBy":"statement"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("groupBy statement is only supported with lang=sql", result["content"]![0]!["text"]!.GetValue<string>());
        var structured = result["structuredContent"]!;
        Assert.Equal("groupBy", structured["parameter"]!.GetValue<string>());
        Assert.Equal("statement", structured["value"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, structured["category"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_SymbolHotspots_GroupByNameKindIsRejected()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"groupBy":"name_kind"}}}""")!;
        var response = _server.HandleMessage(request)!;

        var result = response["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("Unsupported symbol_hotspots groupBy 'name_kind'", result["content"]![0]!["text"]!.GetValue<string>());
        var structured = result["structuredContent"]!;
        Assert.Equal("groupBy", structured["parameter"]!.GetValue<string>());
        Assert.Equal("name_kind", structured["value"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, structured["category"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_ProjectScopeFiltersHotspotsAndUnusedSymbols_Issue1707()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "AppA"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "AppB"));
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppA", "src\AppA\AppA.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppB", "src\AppB\AppB.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "AppA", "AppA.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "AppB", "AppB.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/AppA/ServiceA.cs", "csharp",
                """
                public class ServiceA
                {
                    public void UsedA() { UsedA(); }
                    public void UnusedA() { }
                }

                public class CallerA
                {
                    public void Call(ServiceA service) { service.UsedA(); }
                }
                """);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/AppB/ServiceB.cs", "csharp",
                """
                public class ServiceB
                {
                    public void UsedB() { UsedB(); UsedB(); }
                    public void UnusedB() { }
                }

                public class CallerB
                {
                    public void Call(ServiceB service)
                    {
                        service.UsedB();
                        service.UsedB();
                    }
                }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
                writer.MarkHotspotFamilyReady("csharp", "fixture-fingerprint");
            }

            Environment.CurrentDirectory = projectRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var hotspotsRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_hotspots","arguments":{"lang":"csharp","kind":"function","project":"AppA"}}}""")!;
            var hotspotsResponse = server.HandleMessage(hotspotsRequest)!;
            var hotspotNames = hotspotsResponse["result"]!["structuredContent"]!["hotspots"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            var unusedRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","project":"AppA"}}}""")!;
            var unusedResponse = server.HandleMessage(unusedRequest)!;
            var unusedNames = unusedResponse["result"]!["structuredContent"]!["symbols"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.False(hotspotsResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Contains("UsedA", hotspotNames);
            Assert.DoesNotContain("UsedB", hotspotNames);
            Assert.False(unusedResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.Contains("UnusedA", unusedNames);
            Assert.DoesNotContain("UnusedB", unusedNames);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ProjectScopeUsesIndexedProjectRootWhenCurrentDirectoryDiffers_Issue3183()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope_indexed_root");
        var otherRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope_other_cwd");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "AppA"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "AppB"));
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppA", "src\AppA\AppA.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AppB", "src\AppB\AppB.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "AppA", "AppA.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "AppB", "AppB.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/AppA/ServiceA.cs", "csharp", "public class ServiceA { }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/AppB/ServiceB.cs", "csharp", "public class ServiceB { }\n");

            Environment.CurrentDirectory = otherRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"Service","project":"AppA","exactSubstring":true}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var results = response["result"]!["structuredContent"]!["results"]!.AsArray();
            var result = Assert.Single(results);
            Assert.Equal("src/AppA/ServiceA.cs", result!["path"]!.GetValue<string>());
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(otherRoot);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ProjectScopeFallbackReportsEffectiveRoot_Issue3461()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope_fallback_root");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_project_scope_fallback");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App/ServiceA.cs", "csharp", "public class ServiceA { }\n");

            Environment.CurrentDirectory = projectRoot;
            var expectedProjectRoot = Path.GetFullPath(Environment.CurrentDirectory);
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"ServiceA","project":"App","exactSubstring":true}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            var result = Assert.Single(structured["results"]!.AsArray());
            Assert.Equal("src/App/ServiceA.cs", result!["path"]!.GetValue<string>());
            Assert.Equal(expectedProjectRoot, structured["project_filter_root"]!.GetValue<string>());
            Assert.Equal(QueryCommandRunner.ProjectFilterRootFallbackReasonCurrentDirectory, structured["project_filter_root_fallback_reason"]!.GetValue<string>());
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ProjectScopeErrorSanitizesCaughtExceptionMessage_Issue3660()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope_sanitized_exception");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_project_scope_sanitized_exception");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        var secretProject = "secret-project-token-ghp_1234567890abcdef-private";
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            Environment.CurrentDirectory = projectRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var requestJson = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"ServiceA","project":"__PROJECT__","exactSubstring":true}}}
            """.Replace("__PROJECT__", secretProject, StringComparison.Ordinal);
            var response = server.HandleMessage(JsonNode.Parse(requestJson)!)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var contentText = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var structured = response["result"]!["structuredContent"]!;
            var structuredMessage = structured["message"]!.GetValue<string>();
            var diagnostic = structured["diagnostic"]!.GetValue<string>();
            Assert.Equal("InvalidOperationException", diagnostic);
            Assert.Contains("Project filter could not be resolved: InvalidOperationException", contentText);
            Assert.DoesNotContain(secretProject, contentText);
            Assert.DoesNotContain(secretProject, structuredMessage);
            Assert.DoesNotContain(secretProject, diagnostic);
            Assert.DoesNotContain(projectRoot, contentText);
            Assert.DoesNotContain(projectRoot, structuredMessage);
            Assert.DoesNotContain(projectRoot, diagnostic);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_ProjectScopeErrorDoesNotLeakRootDiagnosticToNextResult_Issue3461()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_project_scope_error_root");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_project_scope_error");
        var originalCurrentDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            Environment.CurrentDirectory = projectRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion(), dbPathExplicit: true);
            var invalidSearch = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"ServiceA","project":"App","since":"not-a-timestamp"}}}""")!;
            var invalidSearchResponse = server.HandleMessage(invalidSearch)!;
            var ping = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ping","arguments":{}}}""")!;
            var pingResponse = server.HandleMessage(ping)!;

            Assert.True(invalidSearchResponse["result"]!["isError"]!.GetValue<bool>());
            Assert.False(pingResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.False(pingResponse["result"]!["structuredContent"]!.AsObject().ContainsKey("project_filter_root"));
            Assert.False(pingResponse["result"]!["structuredContent"]!.AsObject().ContainsKey("project_filter_root_fallback_reason"));
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Index_Rebuild_IgnoresUnreadableDirectoriesWhenCollectingMarkerFingerprints()
    {
        if (OperatingSystem.IsWindows())
            return;

        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_unreadable_marker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_unreadable_marker");
        var unreadableDir = Path.Combine(fixtureDir, "secret");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(fixtureDir, "app.cs"), "public class App { public void Run() { } }");
            Directory.CreateDirectory(unreadableDir);
            File.WriteAllText(Path.Combine(unreadableDir, "Hidden.csproj"), "<Project />");
            originalMode = File.GetUnixFileMode(unreadableDir);
            File.SetUnixFileMode(unreadableDir, UnixFileMode.None);

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir,
                        ["rebuild"] = true,
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(response["result"]!["structuredContent"]!["summary"]!["files"]!.GetValue<long>() >= 1L);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(unreadableDir))
                File.SetUnixFileMode(unreadableDir, originalMode.Value);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_BlankFile_ReturnsError()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_backfill_blank");
        File.WriteAllText(dbPath, string.Empty);

        try
        {
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("not an existing CodeIndex DB", text);
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_BackfillFold_NonexistentFileUri_ReturnsError()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_backfill_missing");
        var dbUri = new Uri(dbPath).AbsoluteUri;
        using var server = new McpServer(dbUri, ConsoleUi.LoadVersion());
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Database not found", text);
    }

    [Fact]
    public void ToolsCall_BackfillFold_LegacyDbWithoutCodeIndexMeta_Succeeds()
    {
        using (var dropMeta = _db.Connection.CreateCommand())
        {
            dropMeta.CommandText = "DROP TABLE codeindex_meta;";
            dropMeta.ExecuteNonQuery();
        }

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"backfill_fold","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(2, structured["symbols"]!.GetValue<int>());
        Assert.Equal(0, structured["symbol_references"]!.GetValue<int>());
        Assert.True(structured["fold_ready"]!.GetValue<bool>());

        using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        verifyDb.TryMigrateForRead();
        Assert.Equal(NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString("fold_key_version"));
        Assert.Equal(NameFold.Fingerprint(), verifyDb.GetMetaString("fold_key_fingerprint"));
        var reader = new DbReader(verifyDb.Connection);
        Assert.True(reader._foldReady);
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_IncludesConfidenceBuckets()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/config/unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "ExportedApi",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "PathResolver",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "public class PathResolver",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AdoptionService",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public class AdoptionService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "TokenService",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public class TokenService",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "AppSettings",
                Line = 9,
                StartLine = 9,
                EndLine = 11,
                Signature = "public class AppSettings",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "ApplyConfiguration",
                Line = 12,
                StartLine = 12,
                EndLine = 12,
                Signature = "public void ApplyConfiguration()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "UseIOptions",
                Line = 13,
                StartLine = 13,
                EndLine = 13,
                Signature = "public void UseIOptions()",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ConnectionString",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string ConnectionString { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AppSettings",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*unused_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        var symbols = structured["symbols"]!.AsArray();
        Assert.Equal(9, structured["count"]!.GetValue<int>());
        Assert.Equal(1, structured["returned_bucket_counts"]!["likely_unused_private"]!.GetValue<int>());
        Assert.Equal(1, structured["returned_bucket_counts"]!["maybe_unused_nonpublic"]!.GetValue<int>());
        Assert.Equal(3, structured["returned_bucket_counts"]!["public_or_exported_no_refs"]!.GetValue<int>());
        Assert.Equal(4, structured["returned_bucket_counts"]!["reflection_or_config_suspect"]!.GetValue<int>());
        Assert.Equal(1, structured["summary"]!["by_bucket"]!["likely_unused_private"]!.GetValue<int>());
        Assert.Equal(8, structured["summary"]!["by_confidence"]!["low"]!.GetValue<int>());
        Assert.Equal("low", structured["bucket_taxonomy"]!["reflection_or_config_suspect"]!["confidence"]!.GetValue<string>());
        Assert.Contains("reflection", structured["bucket_taxonomy"]!["reflection_or_config_suspect"]!["description"]!.GetValue<string>());
        Assert.Equal("Hidden", symbols[0]!["name"]!.GetValue<string>());
        Assert.Equal("likely_unused_private", symbols[0]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("medium", symbols[0]!["unusedConfidence"]!.GetValue<string>());
        Assert.Equal("PathResolver", symbols[2]!["name"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols[2]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("ConnectionString", symbols[3]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[3]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("ApplyConfiguration", symbols[7]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[7]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("UseIOptions", symbols[8]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[8]!["unusedBucket"]!.GetValue<string>());
        Assert.Contains("returned buckets", response["result"]!["content"]![0]!["text"]!.GetValue<string>());

        var filteredRequest = JsonNode.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*unused_fixture.cs*","bucket":"likely_unused_private","minConfidence":"medium"}}}""")!;
        var filteredResponse = _server.HandleMessage(filteredRequest)!;
        var filteredStructured = filteredResponse["result"]!["structuredContent"]!;
        var filteredSymbols = filteredStructured["symbols"]!.AsArray();

        Assert.False(filteredResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
        Assert.Equal(1, filteredStructured["count"]!.GetValue<int>());
        Assert.Equal("Hidden", filteredSymbols[0]!["name"]!.GetValue<string>());
        Assert.Equal("likely_unused_private", filteredSymbols[0]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_IncludesUnusedCSharpEnumMembersWithoutDegradedMetadata()
    {
        InsertIndexedFile("src/cases.cs", "csharp",
            """
            namespace Demo;

            public enum Color
            {
                Red,
                Blue
            }

            public enum TrulyUnused
            {
                Green
            }

            public class UsesColor
            {
                public Color Shade => Color.Red;
            }
            """);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        var names = structured["symbols"]!
            .AsArray()
            .Select(symbol => symbol?["name"]?.GetValue<string>())
            .Where(name => name != null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(structured["graph_supported"]!.GetValue<bool>());
        Assert.Null(structured["graphDegraded"]);
        Assert.Null(structured["unsupportedSymbolKind"]);
        Assert.DoesNotContain("Color", names);
        Assert.Contains("TrulyUnused", names);
        Assert.DoesNotContain("Red", names);
        Assert.Contains("Blue", names);
        Assert.Contains("Green", names);
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_EnumDeclarationsReturnNormalSummary()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_unused_enum_gap_summary");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/cases.cs", "csharp",
                """
                namespace Demo;

                public enum Color
                {
                    Red,
                    Blue
                }

                public enum TrulyUnused
                {
                    Green
                }

                public class UsesColor
                {
                    public Color Shade => Color.Red;
                }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkGraphReady();
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            var names = structured["symbols"]!
                .AsArray()
                .Select(symbol => symbol?["name"]?.GetValue<string>())
                .Where(name => name != null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(structured["count"]!.GetValue<int>() >= 2);
            Assert.Null(structured["graphDegraded"]);
            Assert.Null(structured["unsupportedSymbolKind"]);
            Assert.DoesNotContain("Color", names);
            Assert.Contains("TrulyUnused", names);
            Assert.Contains("Green", names);
            Assert.Contains(
                "Found",
                response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesReflectionAttributedPropertyAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*reflection_unused_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray();
        Assert.Equal("UserDto", symbols[0]!["name"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols[0]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("FullName", symbols[1]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[1]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesCommentSeparatedReflectionAttributeAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_comment_fixture.cs",
            Lang = "csharp",
            Size = 220,
            Lines = 8,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    // Bound from JSON payload.
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 7,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*reflection_comment_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray();
        Assert.Equal("FullName", symbols[1]!["name"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols[1]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_MissingChunksDegradesReflectionClassificationWithoutCrashing()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_missing_chunks_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 8,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 6,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE chunks;";
            cmd.ExecuteNonQuery();
        }

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*reflection_missing_chunks_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("public_or_exported_no_refs", symbols["FullName"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_KeepsPlainCliOptionsPropertiesInPublicBucket()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/cli_options_fixture.cs",
            Lang = "csharp",
            Size = 180,
            Lines = 6,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "CliOptions",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public sealed class CliOptions",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ShowHelp",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public bool ShowHelp { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "ProjectPath",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "public string? ProjectPath { get; init; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "CliOptions",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*cli_options_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("public_or_exported_no_refs", symbols["ShowHelp"]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols["ProjectPath"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesQualifiedAndSuffixedAttributesAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_qualified_fixture.cs",
            Lang = "csharp",
            Size = 360,
            Lines = 12,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 12,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [global::System.Text.Json.Serialization.JsonPropertyName("full_name")]
                    public string QualifiedName { get; set; } = string.Empty;
                    [JsonPropertyNameAttribute("display_name")]
                    public string SuffixedName { get; set; } = string.Empty;
                    [System.Text.Json.Serialization.JsonIgnoreAttribute]
                    public string IgnoredName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 10,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "QualifiedName",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public string QualifiedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "SuffixedName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string SuffixedName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "IgnoredName",
                Line = 10,
                StartLine = 10,
                EndLine = 10,
                Signature = "public string IgnoredName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*reflection_qualified_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!
            .AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("reflection_or_config_suspect", symbols["QualifiedName"]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("reflection_or_config_suspect", symbols["SuffixedName"]!["unusedBucket"]!.GetValue<string>());
        Assert.Equal("public_or_exported_no_refs", symbols["IgnoredName"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_ClassifiesBlockCommentSeparatedReflectionAttributeAsSuspect()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_block_comment_fixture.cs",
            Lang = "csharp",
            Size = 280,
            Lines = 10,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    /* bound from payload
                       via serializer */
                    public string FullName { get; set; } = string.Empty;
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*reflection_block_comment_fixture.cs*"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!
            .AsArray()
            .ToDictionary(symbol => symbol!["name"]!.GetValue<string>(), StringComparer.Ordinal);
        Assert.Equal("reflection_or_config_suspect", symbols["FullName"]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_UnsupportedLanguageReturnsZero()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "script.txt",
            Lang = "text",
            Size = 64,
            Lines = 4,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "helper",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
                Signature = "helper() {",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"text"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.False(structured["graph_supported"]!.GetValue<bool>());
        Assert.Equal(0, structured["count"]!.GetValue<int>());
        Assert.Empty(structured["symbols"]!.AsArray());
        Assert.Contains("unavailable", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_LargePublicLimit_RespectsMcpClamp()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/large_public_unused_fixture.cs",
            Lang = "csharp",
            Size = 16000,
            Lines = 2600,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "public class PublicNoise0000 { }",
            }
        ]);

        var symbols = new List<SymbolRecord>();
        for (var i = 0; i < 2500; i++)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = $"PublicNoise{i:D4}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
                Signature = $"public class PublicNoise{i:D4} {{ }}",
                Visibility = "public",
            });
        }
        writer.InsertSymbols(symbols);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*large_public_unused_fixture.cs*","limit":3000}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var structured = response["result"]!["structuredContent"]!;
        Assert.Equal(200, structured["count"]!.GetValue<int>());
        Assert.Equal(200, structured["symbols"]!.AsArray().Count);
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_MissingGraphTable_MarksResponseDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_unused_missing_graph");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.cs",
                    Lang = "csharp",
                    Size = 42,
                    Lines = 3,
                    Modified = new DateTime(2024, 1, 1),
                    Checksum = Guid.NewGuid().ToString("N"),
                });
                writer.InsertChunks([new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 3,
                    Content = "public class App\n{\n    public void Run() { }\n}",
                }]);
                writer.InsertSymbols([new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = "App",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 4,
                    Signature = "public class App",
                    Visibility = "public",
                }]);
            }

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp"}}}""")!;
            var response = server.HandleMessage(request)!;

            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
            var structured = response["result"]!["structuredContent"]!;
            Assert.Equal(0, structured["count"]!.GetValue<int>());
            Assert.True(structured["degraded"]!.GetValue<bool>());
            Assert.False(structured["graph_table_available"]!.GetValue<bool>());
            Assert.Contains("missing", structured["note"]!.GetValue<string>());
            Assert.Contains("degraded", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ToolsCall_Index_RestampsFoldReadyWhenFoldKeyVersionMismatchesButAllRowsAreRewritten()
    {
        var fixtureDir = Path.Combine(Path.GetFullPath("."), $"mcp_index_version_rewrite_fixture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureDir);
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_index_version_rewrite");
        try
        {
            File.WriteAllText(Path.Combine(fixtureDir, "intl.py"), "def Straße():\n    pass\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            var firstIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var firstResponse = server.HandleMessage(firstIndex)!;
            Assert.False(firstResponse["result"]!["isError"]?.GetValue<bool>() ?? false);

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    UPDATE files SET modified = '2000-01-01T00:00:00.0000000Z' WHERE path = 'intl.py';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var rewrittenPath = Path.Combine(fixtureDir, "intl.py");
            File.WriteAllText(rewrittenPath, "def Straße():\n    return 1\n");

            var secondIndex = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 2,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = fixtureDir
                    }
                }
            };
            var secondResponse = server.HandleMessage(secondIndex)!;
            Assert.False(secondResponse["result"]!["isError"]?.GetValue<bool>() ?? false);
            Assert.True(secondResponse["result"]!["structuredContent"]!["fold_ready"]!.GetValue<bool>());
            Assert.Null(secondResponse["result"]!["structuredContent"]!["fold_ready_reason"]);

            using var verify = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var userVerCmd = verify.Connection.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.Connection.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.Equal(NameFold.Version.ToString(), storedVersion);

            var reader = new DbReader(verify.Connection, verify.IsReadOnly);
            Assert.Single(reader.SearchSymbols(new[] { "STRASSE" }, limit: 10, exact: true));
        }
        finally
        {
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            TestProjectHelper.DeleteDirectory(fixtureDir);
        }
    }

    [Fact]
    public void ToolsCall_UnusedSymbols_DiversifiesReflectionSuspectBeforeLimit()
    {
        var writer = new DbWriter(_db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/reflection_diversified_unused_fixture.cs",
            Lang = "csharp",
            Size = 200,
            Lines = 12,
            Modified = new DateTime(2024, 1, 1),
            Checksum = Guid.NewGuid().ToString("N"),
        });
        writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 10,
                Content = """
                using System.Text.Json.Serialization;

                public class UserDto
                {
                    [JsonPropertyName("full_name")]
                    public string FullName { get; set; } = string.Empty;
                    public void Run() { Hidden(); }
                    private void Hidden() { }
                    internal void InternalOnly() { }
                }
                """,
            }
        ]);
        writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = "UserDto",
                Line = 3,
                StartLine = 3,
                EndLine = 8,
                Signature = "public class UserDto",
                Visibility = "public",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = "FullName",
                Line = 5,
                StartLine = 5,
                EndLine = 5,
                Signature = "public string FullName { get; set; } = string.Empty;",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Run",
                Line = 6,
                StartLine = 6,
                EndLine = 6,
                Signature = "public void Run() { Hidden(); }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Hidden",
                Line = 7,
                StartLine = 7,
                EndLine = 7,
                Signature = "private void Hidden() { }",
                Visibility = "private",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "InternalOnly",
                Line = 8,
                StartLine = 8,
                EndLine = 8,
                Signature = "internal void InternalOnly() { }",
                Visibility = "internal",
                ContainerKind = "class",
                ContainerName = "UserDto",
            },
        ]);

        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"unused_symbols","arguments":{"lang":"csharp","path":"src/*reflection_diversified_unused_fixture.cs*","limit":4}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        var symbols = response["result"]!["structuredContent"]!["symbols"]!.AsArray();
        Assert.Equal(["InternalOnly", "UserDto", "FullName", "Run"], symbols.Select(symbol => symbol!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal("reflection_or_config_suspect", symbols[2]!["unusedBucket"]!.GetValue<string>());
    }

    [Fact]
    public void ToolsCall_UnknownTool_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nonexistent","arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_MissingToolName_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Index_PathTraversal_ReturnsError()
    {
        var request = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"index","arguments":{"path":"/etc"}}}""")!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("current working directory", text);
    }

    [Fact]
    public void ToolsCall_Index_SymlinkEscapingCurrentDirectory_ReturnsError()
    {
        if (OperatingSystem.IsWindows())
            return;

        var originalCurrentDirectory = Environment.CurrentDirectory;
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_symlink_root");
        var outsideRoot = TestProjectHelper.CreateTempProject("cdidx_mcp_symlink_outside");
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_mcp_symlink");
        var linkPath = Path.Combine(projectRoot, "outside-link");
        try
        {
            File.WriteAllText(Path.Combine(outsideRoot, "secret.cs"), "public class Secret { }\n");
            Directory.CreateSymbolicLink(linkPath, outsideRoot);

            Environment.CurrentDirectory = projectRoot;
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "index",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = linkPath
                    }
                }
            };
            var response = server.HandleMessage(request)!;

            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Contains("current working directory", text);
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(outsideRoot);
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
        }
    }

    [Fact]
    public void ToolsCall_Search_QueryTooLong_ReturnsError()
    {
        var longQuery = new string('a', 1001);
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"search\",\"arguments\":{\"query\":\"" + longQuery + "\"}}}";
        var request = JsonNode.Parse(json)!;
        var response = _server.HandleMessage(request)!;

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("too long", text);
    }

    // Regression pins for issue #199: MCP tool handlers must normalize mixed-case lang/kind.
    // #199 回帰テスト: MCP ハンドラも --lang / --kind を大文字小文字なく扱うことを固定する。
    [Fact]
    public void ToolsCall_Symbols_AcceptsLangCsharpCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","lang":"CSharp"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("csharp", structuredUpper["lang"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","lang":"csharp"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Symbols_AcceptsKindClassCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","kind":"CLASS"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("class", structuredUpper["kind"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","kind":"class"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());

        // Prove the kind filter is actually applied, not silently dropped: the seeded "App" symbol
        // is a class, so querying it with kind=FUNCTION must return 0 regardless of casing.
        // kind フィルタが実際に適用されていることを確認: seed した App は class なので、
        // kind=FUNCTION での検索は大文字小文字に関わらず 0 件になるべき。
        var requestWrongKind = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbols","arguments":{"query":"App","kind":"FUNCTION"}}}""")!;
        var responseWrongKind = _server.HandleMessage(requestWrongKind)!;
        var structuredWrongKind = responseWrongKind["result"]!["structuredContent"]!;
        Assert.Equal("function", structuredWrongKind["kind"]!.GetValue<string>());
        Assert.Equal(0, structuredWrongKind["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Definition_AcceptsLangCsharpCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","lang":"CSharp"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("csharp", structuredUpper["lang"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","lang":"csharp"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_Definition_AcceptsKindClassCaseInsensitively_Issue199()
    {
        var requestUpper = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","kind":"CLASS"}}}""")!;
        var responseUpper = _server.HandleMessage(requestUpper)!;

        var structuredUpper = responseUpper["result"]!["structuredContent"]!;
        Assert.Equal("class", structuredUpper["kind"]!.GetValue<string>());
        Assert.True(structuredUpper["count"]!.GetValue<int>() >= 1);

        var requestLower = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","kind":"class"}}}""")!;
        var responseLower = _server.HandleMessage(requestLower)!;
        var structuredLower = responseLower["result"]!["structuredContent"]!;

        Assert.Equal(structuredLower["count"]!.GetValue<int>(), structuredUpper["count"]!.GetValue<int>());

        // Prove the kind filter is actually applied, not silently echoed.
        // The shared fixture only seeds `App` as a class, so querying with kind:"FUNCTION"
        // must return 0 if the normalized kind is threaded through to GetDefinitions().
        // kind フィルタが捨てられずに実際に適用されていることを確認する。
        var requestWrongKind = JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"definition","arguments":{"query":"App","kind":"FUNCTION"}}}""")!;
        var responseWrongKind = _server.HandleMessage(requestWrongKind)!;
        var structuredWrongKind = responseWrongKind["result"]!["structuredContent"]!;
        Assert.Equal("function", structuredWrongKind["kind"]!.GetValue<string>());
        Assert.Equal(0, structuredWrongKind["count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_RateLimitDisabled_NoThrottle()
    {
        // Default (no env vars) must behave like the pre-#1560 server so existing stdio
        // single-user sessions are unaffected. The limiter still records nothing on every
        // call regardless of how many succeed.
        // 既定（環境変数なし）では #1560 以前と同じ挙動で、stdio 単一ユーザーは影響を受けない。
        for (var i = 0; i < 5; i++)
        {
            var request = JsonNode.Parse($"{{\"jsonrpc\":\"2.0\",\"id\":{i},\"method\":\"tools/call\",\"params\":{{\"name\":\"languages\"}}}}")!;
            var response = _server.HandleMessage(request)!;
            Assert.Null(response["error"]);
        }
    }

    [Fact]
    public void ToolsCall_PreValidationFailuresConsumeRateLimitQuota_Issue4547()
    {
        JsonNode Send(int id, Func<JsonObject> createParams) => _server.HandleMessage(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = createParams(),
        })!;

        void AssertSecondCallIsRateLimited(
            Func<JsonObject> createParams,
            Action<JsonNode> assertFirstResponse,
            int expectedBucketCount = 1)
        {
            InstallRateLimiter(_server, new RateLimiterOptions
            {
                RefillTokensPerSecond = 1.0,
                BurstCapacity = 1.0,
            });

            assertFirstResponse(Send(1, createParams));
            var throttled = Send(2, createParams);

            Assert.Equal(McpErrorEnvelope.CodeRateLimited, throttled["error"]!["code"]!.GetValue<int>());
            Assert.Equal(McpErrorEnvelope.CategoryRateLimited, throttled["error"]!["data"]!["category"]!.GetValue<string>());
            Assert.Equal(expectedBucketCount, _server.RateLimiter.BucketCount);
        }

        // Missing/non-string names, empty/oversized/unknown names, and known-tool argument
        // failures must all consume quota before their detailed validation response (#4547).
        // missing/non-string、empty/oversized/unknown 名、既知 tool の argument failure は
        // すべて詳細検証レスポンスより先に quota を消費する（#4547）。
        AssertSecondCallIsRateLimited(
            () => new JsonObject { ["arguments"] = new JsonObject() },
            first => Assert.Equal(McpErrorEnvelope.CategoryMissingParameter, first["error"]!["data"]!["category"]!.GetValue<string>()));
        AssertSecondCallIsRateLimited(
            () => new JsonObject { ["name"] = 42, ["arguments"] = new JsonObject() },
            first => Assert.Equal(McpErrorEnvelope.CategoryMissingParameter, first["error"]!["data"]!["category"]!.GetValue<string>()));
        AssertSecondCallIsRateLimited(
            () => new JsonObject { ["name"] = string.Empty, ["arguments"] = new JsonObject() },
            first => Assert.Equal(McpErrorEnvelope.CategoryToolUnknown, first["error"]!["data"]!["category"]!.GetValue<string>()));
        AssertSecondCallIsRateLimited(
            () => new JsonObject { ["name"] = "SEARCH", ["arguments"] = new JsonObject() },
            first => Assert.Equal(McpErrorEnvelope.CategoryToolUnknown, first["error"]!["data"]!["category"]!.GetValue<string>()));
        AssertSecondCallIsRateLimited(
            () => new JsonObject { ["name"] = new string('x', McpBoundedText.MaxToolNameChars + 1), ["arguments"] = new JsonObject() },
            first => Assert.Equal(McpErrorEnvelope.CategoryToolUnknown, first["error"]!["data"]!["category"]!.GetValue<string>()));
        AssertSecondCallIsRateLimited(
            () => new JsonObject
            {
                ["name"] = "search",
                ["arguments"] = new JsonObject { ["query"] = "abc", ["limt"] = 1 },
            },
            first => Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument, first["result"]!["structuredContent"]!["category"]!.GetValue<string>()),
            expectedBucketCount: 2);
    }

    [Fact]
    public void ToolsCall_CoarsePreValidationQuotaSpansCanonicalNames_Issue4547()
    {
        InstallRateLimiter(_server, new RateLimiterOptions
        {
            RefillTokensPerSecond = 1.0,
            BurstCapacity = 1.0,
        });

        var malformedSearch = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"abc","limt":1}}}""")!)!;
        var malformedDefinition = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"definition","arguments":{"symbol":"Thing","limt":1}}}""")!)!;

        Assert.Equal(McpErrorEnvelope.CategoryInvalidArgument,
            malformedSearch["result"]!["structuredContent"]!["category"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CodeRateLimited, malformedDefinition["error"]!["code"]!.GetValue<int>());
        Assert.Equal(McpErrorEnvelope.CategoryRateLimited,
            malformedDefinition["error"]!["data"]!["category"]!.GetValue<string>());
        // Only the caller-wide bucket and the admitted canonical search bucket exist;
        // the rejected definition call must not allocate another per-tool key (#4547).
        // caller-wide bucket と許可済み canonical search bucket だけが存在し、拒否された
        // definition call は追加の per-tool key を確保してはならない（#4547）。
        Assert.Equal(2, _server.RateLimiter.BucketCount);
    }

    [Fact]
    public void ToolsCall_UniqueUnknownNamesStayBoundedAndLegitimateToolRecoversAtAdvertisedExpiry_Issue4547()
    {
        var clock = InstallRateLimiter(_server, new RateLimiterOptions
        {
            RefillTokensPerSecond = 1.0,
            BurstCapacity = 16.0,
            MaxBucketCount = 2,
            BucketIdleTtl = TimeSpan.FromSeconds(10),
        });

        for (var i = 0; i < 16; i++)
        {
            var unknown = _server.HandleMessage(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = i,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = $"unknown-{i}",
                    ["arguments"] = new JsonObject(),
                },
            })!;
            Assert.Equal(McpErrorEnvelope.CategoryToolUnknown, unknown["error"]!["data"]!["category"]!.GetValue<string>());
        }
        Assert.Equal(1, _server.RateLimiter.BucketCount);

        var exhaustedInvalidPartition = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":16,"method":"tools/call","params":{"name":"another-unknown","arguments":{}}}""")!)!;
        Assert.Equal(McpErrorEnvelope.CodeRateLimited, exhaustedInvalidPartition["error"]!["code"]!.GetValue<int>());
        Assert.Equal(1, _server.RateLimiter.BucketCount);

        clock.Now = clock.Now.AddSeconds(1);
        var languages = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":17,"method":"tools/call","params":{"name":"languages"}}""")!)!;
        Assert.Null(languages["error"]);
        Assert.Equal(2, _server.RateLimiter.BucketCount);

        clock.Now = clock.Now.AddSeconds(8);
        var saturated = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":18,"method":"tools/call","params":{"name":"status"}}""")!)!;
        Assert.Equal(McpErrorEnvelope.CodeRateLimited, saturated["error"]!["code"]!.GetValue<int>());
        var retryAfterMs = saturated["error"]!["data"]!["retry_after_ms"]!.GetValue<long>();
        Assert.Equal(2000, retryAfterMs);

        clock.Now = clock.Now.AddMilliseconds(retryAfterMs);
        var recovered = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":19,"method":"tools/call","params":{"name":"status"}}""")!)!;
        Assert.Null(recovered["error"]);
        Assert.Equal(2, _server.RateLimiter.BucketCount);
    }

    [Fact]
    public void ToolsCall_HierarchyRetryIncludesChargedCoarseRefillAfterCapDenial_Issue4547()
    {
        var clock = InstallRateLimiter(_server, new RateLimiterOptions
        {
            RefillTokensPerSecond = 0.1,
            BurstCapacity = 1.0,
            MaxBucketCount = 2,
            BucketIdleTtl = TimeSpan.FromSeconds(11),
        });
        _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"client-a","version":"1.0"}}}""")!);
        const string caller = "client-a/1.0";
        Assert.True(_server.RateLimiter.TryAcquire(RateLimiter.ToolsCallPreValidationBucketName, caller).Allowed);
        Assert.True(_server.RateLimiter.TryAcquire("unrelated", "other-client").Allowed);
        clock.Now = clock.Now.AddSeconds(10);

        var denied = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;

        Assert.Equal(McpErrorEnvelope.CodeRateLimited, denied["error"]!["code"]!.GetValue<int>());
        var retryAfterMs = denied["error"]!["data"]!["retry_after_ms"]!.GetValue<long>();
        Assert.Equal(10_000, retryAfterMs);

        clock.Now = clock.Now.AddMilliseconds(retryAfterMs);
        var recovered = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;
        Assert.Null(recovered["error"]);
        Assert.Equal(2, _server.RateLimiter.BucketCount);
    }

    [Fact]
    public void ToolsCall_DisabledKnownToolConsumesQuotaBeforeEnablementCheck_Issue4547()
    {
        var deny = McpToolFilter.Parse(null, "status");
        using var server = new McpServer(_dbPath, ConsoleUi.LoadVersion(), false, deny);
        InstallRateLimiter(server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });
        var request = JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!;

        var disabled = server.HandleMessage(request)!;
        var throttled = server.HandleMessage(McpJsonNode.Clone(request)!)!;

        Assert.Equal(-32601, disabled["error"]!["code"]!.GetValue<int>());
        Assert.Equal(McpErrorEnvelope.CategoryToolDisabled, disabled["error"]!["data"]!["category"]!.GetValue<string>());
        Assert.Equal(McpErrorEnvelope.CodeRateLimited, throttled["error"]!["code"]!.GetValue<int>());
        Assert.Equal(2, server.RateLimiter.BucketCount);
    }

    [Fact]
    public void ToolsCall_RateLimited_ReturnsStructuredNegative32000()
    {
        // Bucket of capacity 1, refilling at 1/sec. First call succeeds, second is denied
        // with -32000 carrying tool / caller / retry_after_ms (#1560 contract).
        // 容量 1・補充 1/sec のバケット。1 回目は成功、2 回目は -32000 で tool/caller/retry_after_ms
        // を含む構造化レスポンスになる（#1560 の契約）。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });

        var initialize = JsonNode.Parse("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"client-a","version":"1.2.3"}}}""")!;
        _server.HandleMessage(initialize);

        var first = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!;
        Assert.Null(first["error"]);
        Assert.False(first["result"]!["isError"]?.GetValue<bool>() ?? false);

        var second = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"status"}}""")!)!;

        Assert.Null(second["result"]);
        var error = second["error"]!;
        Assert.Equal(-32000, error["code"]!.GetValue<int>());
        Assert.Contains("Rate limit exceeded", error["message"]!.GetValue<string>());
        var data = error["data"]!;
        Assert.Equal("rate_limited", data["error_category"]!.GetValue<string>());
        Assert.Equal("status", data["tool"]!.GetValue<string>());
        Assert.Equal("client-a/1.2.3", data["caller"]!.GetValue<string>());
        Assert.True(data["retry_after_ms"]!.GetValue<long>() >= 1);
        Assert.Equal(2, second["id"]!.GetValue<int>());
    }

    [Fact]
    public void CreateRateLimitedErrorResponse_BoundsToolAndCallerMetadata_Issue4177()
    {
        var tool = new string('t', McpBoundedText.MaxToolNameChars + 8);
        var caller = new string('c', McpBoundedText.MaxClientIdentityChars + 8);
        var toolDisplay = McpBoundedText.ForDisplay(tool, McpBoundedText.MaxToolNameChars);
        var callerDisplay = McpBoundedText.ForDisplay(caller, McpBoundedText.MaxClientIdentityChars);

        var response = McpServer.CreateRateLimitedErrorResponse(JsonValue.Create(7), tool, caller, retryAfterMs: 250);
        var serialized = response.ToJsonString();

        Assert.DoesNotContain(tool, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(caller, serialized, StringComparison.Ordinal);
        var error = response["error"]!;
        Assert.Equal(McpErrorEnvelope.CodeRateLimited, error["code"]!.GetValue<int>());
        Assert.Contains(toolDisplay.Text, error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var data = error["data"]!;
        Assert.Equal(McpErrorEnvelope.CategoryRateLimited, data["category"]!.GetValue<string>());
        Assert.True(data["retry_safe"]!.GetValue<bool>());
        Assert.Equal("rate_limited", data["error_category"]!.GetValue<string>());
        Assert.Equal(toolDisplay.Text, data["tool"]!.GetValue<string>());
        Assert.Equal(tool.Length, data["tool_length"]!.GetValue<int>());
        Assert.True(data["tool_truncated"]!.GetValue<bool>());
        Assert.Equal(callerDisplay.Text, data["caller"]!.GetValue<string>());
        Assert.Equal(caller.Length, data["caller_length"]!.GetValue<int>());
        Assert.True(data["caller_truncated"]!.GetValue<bool>());
        Assert.Equal(250, data["retry_after_ms"]!.GetValue<long>());
    }

    [Fact]
    public void ToolsCall_RateLimit_LayersCallerWideAndKnownToolBuckets_Issue4547()
    {
        // Known tools retain secondary per-tool buckets while all calls also consume the
        // shared caller-wide quota (#1560 / #4547).
        // 既知 tool は secondary per-tool bucket を維持しつつ、全 call が共有 caller-wide
        // quota も消費する（#1560 / #4547）。
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 2.0 });

        Assert.Null(_server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"status"}}""")!)!["error"]);
        var languages = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"languages"}}""")!)!;
        Assert.Null(languages["error"]);
        Assert.Equal(3, _server.RateLimiter.BucketCount);

        var throttled = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"status"}}""")!)!;
        Assert.Equal(McpErrorEnvelope.CodeRateLimited, throttled["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_RateLimitPrecedesProjectFilterResolution_Issue3160()
    {
        InstallRateLimiter(_server, new RateLimiterOptions { RefillTokensPerSecond = 1.0, BurstCapacity = 1.0 });

        var initialize = JsonNode.Parse("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"clientInfo":{"name":"client-a","version":"1.2.3"}}}""")!;
        _server.HandleMessage(initialize);

        var first = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"query":"App"}}}""")!)!;
        Assert.Null(first["error"]);

        var second = _server.HandleMessage(JsonNode.Parse(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search","arguments":{"query":"App","project":"DefinitelyMissingProject3160"}}}""")!)!;

        var error = second["error"]!;
        Assert.Equal(-32000, error["code"]!.GetValue<int>());
        Assert.Contains("Rate limit exceeded", error["message"]!.GetValue<string>());
        Assert.DoesNotContain("Project filter could not be resolved", second.ToJsonString(), StringComparison.Ordinal);
    }
}
