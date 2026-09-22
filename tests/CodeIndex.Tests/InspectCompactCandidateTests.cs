using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class InspectCompactCandidateTests
{
    [Fact]
    public void CompactCandidates_PreserveBoundsIdentityCountsAndContinuation_Issue5397()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_compact_candidates_5397");
        try
        {
            TestProjectHelper.WriteTextFile(projectRoot, "src/A.cs", """
                namespace Fixture;
                public partial class Partial
                {
                    public void Ping() { }
                    public void Ping(int value) { }
                    public void Unique() { }
                    public void Pair() { }
                    public void Pair(int value) { }
                    public void CallerOne() => Ping();
                    public void CallerTwo() => Ping();
                    public void CallerThree() => Ping();
                }
                """);
            TestProjectHelper.WriteTextFile(projectRoot, "src/B.cs", """
                namespace Fixture;
                public partial class Partial { }
                """);
            TestProjectHelper.WriteTextFile(projectRoot, "src/C.cs", """
                namespace Other;
                public class Partial
                {
                    public void Ping() { }
                }
                """);
            for (var i = 0; i < 6; i++)
                TestProjectHelper.WriteTextFile(projectRoot, $"src/Cap{i}.cs",
                    $"namespace Scope{i};\npublic class Cap {{ }}\n");
            var (indexExit, _, indexError) = CaptureConsole(() =>
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], JsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExit);
            Assert.Empty(indexError);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());

            // Share the indexed fixture across zero, N, N+1, partial/overload and
            // internal-definition-cap boundaries, including the MCP projection.
            foreach (var (query, total) in new[] { ("Missing", 0), ("Unique", 1), ("Pair", 2), ("Ping", 3), ("Partial", 3), ("Cap", 6) })
            {
                var baseline = Inspect(query, 20, compact: false);
                Assert.False(baseline.ContainsKey("candidate_count_authoritative"));
                var baselineSelectors = Selectors(baseline);
                Assert.Equal(Math.Min(total, 5), baselineSelectors.Length);
                foreach (var limit in new[] { 1, 2, 3, 5, 20 })
                {
                    var expectedReturned = Math.Min(Math.Min(total, 5), limit);
                    var observed = Math.Min(total, Math.Min(limit + 1, 5));
                    var authoritative = total < Math.Min(limit + 1, 5);
                    var cli = Inspect(query, limit);
                    var mcp = Analyze(query, limit);
                    foreach (var (payload, isMcp) in new[] { (cli, false), (mcp, true) })
                    {
                        Assert.Equal(limit, payload["compact_limit"]!.GetValue<int>());
                        Assert.Equal(observed, payload["candidate_count"]!.GetValue<int>());
                        Assert.Equal(authoritative, payload["candidate_count_authoritative"]!.GetValue<bool>());
                        Assert.Equal(authoritative ? null : observed, payload["candidate_count_lower_bound"]?.GetValue<int>());
                        Assert.Equal(baselineSelectors.Take(expectedReturned), Selectors(payload));
                        Assert.Equal(expectedReturned, payload["definitions"]!.AsArray().Count);
                        var collection = payload["truncation"]!["sections"]!["candidate_bundles"]!;
                        Assert.Equal(expectedReturned, collection["returned"]!.GetValue<int>());
                        Assert.Equal(observed, collection["source_count"]!.GetValue<int>());
                        Assert.Equal(authoritative, collection["source_count_authoritative"]!.GetValue<bool>());
                        Assert.Equal(!authoritative || observed > expectedReturned, collection["truncated"]!.GetValue<bool>());
                        Assert.Equal(observed - expectedReturned,
                            collection[authoritative ? "omitted_count" : "omitted_count_lower_bound"]!.GetValue<int>());
                        Assert.Null(collection[authoritative ? "omitted_count_lower_bound" : "omitted_count"]);
                        var bundles = payload["candidate_bundles"]?.AsArray() ?? [];
                        for (var i = 0; i < bundles.Count; i++)
                        {
                            var selector = bundles[i]!["selector"]!;
                            var definition = payload["definitions"]![i]!;
                            Assert.Equal(selector["path"]!.GetValue<string>(), definition[isMcp ? "file" : "path"]!.GetValue<string>());
                            Assert.Equal(selector["line"]!.GetValue<int>(), definition["line"]!.GetValue<int>());
                            if (!isMcp)
                                Assert.Equal(selector["symbol_id"]!.GetValue<long>(), definition["symbol_id"]!.GetValue<long>());
                            foreach (var (child, countField) in new[] { ("nearby_symbols", "nearby_symbol_count"), ("references", "reference_count"), ("callers", "caller_count"), ("callees", "callee_count") })
                            {
                                Assert.InRange(isMcp ? bundles[i]![countField]!.GetValue<int>() : bundles[i]![child]!.AsArray().Count, 0, limit);
                                Assert.InRange(payload[child]!.AsArray().Count, 0, limit);
                            }
                        }
                        Assert.DoesNotContain(payload["truncation"]!["sections"]!.AsObject(),
                            entry => entry.Key.StartsWith($"candidate_bundles[{expectedReturned}]", StringComparison.Ordinal));
                    }
                }
            }

            foreach (var isMcp in new[] { false, true })
            {
                var first = isMcp ? Analyze("Ping", 1) : Inspect("Ping", 1);
                var bundle = first["candidate_bundles"]![0]!;
                var selector = bundle["selector"]!["selector"]!.GetValue<string>();
                var firstReference = first["references"]![0]!["line"]!.GetValue<int>();
                var section = bundle["graph_sections"]!["references"]!;
                var cursor = section["next_cursor"]!.GetValue<string>();
                Assert.Equal(1, section["returned"]!.GetValue<int>());
                Assert.True(section["total"]!.GetValue<int>() > 1);
                var next = isMcp ? Analyze("Ping", 1, cursor) : Inspect("Ping", 1, extra: ["--cursor", cursor]);
                var nextBundle = next["candidate_bundles"]![0]!;
                Assert.Equal(selector, nextBundle["selector"]!["selector"]!.GetValue<string>());
                Assert.Equal(1, nextBundle["graph_sections"]!["references"]!["offset"]!.GetValue<int>());
                Assert.NotEqual(firstReference, next["references"]![0]!["line"]!.GetValue<int>());

                var selected = Inspect(null, 1, extra: ["--selector", selector]);
                Assert.True(selected["candidate_count_authoritative"]!.GetValue<bool>());
                Assert.Equal(selector, Assert.Single(Selectors(selected)));
            }

            var projected = Inspect("Partial", 1, extra: ["--fields", "definitions,candidates"]);
            Assert.False(projected["candidate_count_authoritative"]!.GetValue<bool>());
            Assert.Equal(1, projected["truncation"]!["sections"]!["candidate_bundles"]!["returned"]!.GetValue<int>());
            var grouped = Inspect("Partial", 1, extra: ["--group-partials"]);
            Assert.Single(Selectors(grouped));
            Assert.True(grouped["definitions"]![0]!["definition_sites"]!.GetValue<int>() >= 2);

            // The byte guard returns the complete bounded collection or a typed error.
            var boundedArgs = new[] { "Ping", "--db", dbPath, "--compact", "--exact-name", "--limit", "1", "--max-json-bytes", "65536" };
            var (boundedExit, boundedOutput, boundedError) = CaptureConsole(() => QueryCommandRunner.RunInspect(boundedArgs, JsonOptions));
            Assert.Equal(CommandExitCodes.Success, boundedExit);
            Assert.Empty(boundedError);
            Assert.InRange(Encoding.UTF8.GetByteCount(boundedOutput), 1, 65536);
            Assert.Single(Selectors(JsonNode.Parse(boundedOutput)!.AsObject()));
            boundedArgs[^1] = "1024";
            var (smallExit, smallOutput, smallError) = CaptureConsole(() => QueryCommandRunner.RunInspect(boundedArgs, JsonOptions));
            Assert.NotEqual(CommandExitCodes.Success, smallExit);
            Assert.Empty(smallError);
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", JsonNode.Parse(smallOutput)!["error_code"]!.GetValue<string>());

            JsonObject Inspect(string? query, int limit, bool compact = true, string[]? extra = null)
            {
                var args = new List<string> { "--db", dbPath, "--json", "--exact-name", "--limit", limit.ToString(CultureInfo.InvariantCulture) };
                if (query != null) args.Insert(0, query);
                if (compact) args.Add("--compact");
                if (extra != null) args.AddRange(extra);
                var (exit, output, error) = CaptureConsole(() => QueryCommandRunner.RunInspect(args.ToArray(), JsonOptions));
                Assert.Equal(CommandExitCodes.Success, exit);
                Assert.Empty(error);
                return JsonNode.Parse(output)!.AsObject();
            }

            JsonObject Analyze(string query, int limit, string? cursor = null)
            {
                var args = new JsonObject { ["query"] = query, ["limit"] = limit, ["exactName"] = true, ["format"] = "compact" };
                if (cursor != null) args["cursor"] = cursor;
                JsonNode? response = null;
                CaptureConsole(() =>
                {
                    response = server.HandleMessage(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = 5397, ["method"] = "tools/call",
                        ["params"] = new JsonObject { ["name"] = "analyze_symbol", ["arguments"] = args },
                    });
                    return 0;
                });
                Assert.NotNull(response);
                Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
                return response["result"]!["structuredContent"]!.AsObject();
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string[] Selectors(JsonObject payload) =>
        payload["candidate_bundles"]?.AsArray()
            .Select(bundle => bundle!["selector"]!["selector"]!.GetValue<string>()).ToArray() ?? [];
}
