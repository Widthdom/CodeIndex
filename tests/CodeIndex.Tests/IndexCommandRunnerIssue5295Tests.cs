using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Index_SizeOmissionsRequireExplicitRecovery_Issue5295(bool scoped)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_size_outcome");
        var root = project.Root;
        var dbPath = Path.Combine(root, ".cdidx", "codeindex.db");
        var previousEnvironment = Environment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        var previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, null);
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{"members":["."],"index_strategy":"single"}""");
            File.WriteAllText(Path.Combine(root, "small.py"), "def target(): pass\ndef caller(): target()\n");
            var largePath = Path.Combine(root, "large.py");
            foreach (var size in new[] { 127, 128 })
            {
                File.WriteAllText(largePath, "#" + new string('x', size - 1));
                var (exit, json) = RunAndCaptureJson([root, "--db", dbPath, "--max-file-bytes", "128", "--json"]);
                Assert.Equal(CommandExitCodes.Success, exit);
                Assert.True(json.GetProperty("index_complete").GetBoolean());
            }
            var references = CountRows(dbPath, "symbol_references");
            Assert.True(references > 0);
            File.WriteAllText(largePath, "#" + new string('x', 128));
            var args = new List<string> { root, "--db", dbPath };
            if (scoped)
                args.AddRange(["--files", "large.py"]);
            var (partialExit, partial) = RunAndCaptureJson([.. args, "--json"]);
            AssertPartial(partialExit, partial);
            Assert.Equal(references, CountRows(dbPath, "symbol_references"));
            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
                Assert.Equal("128", db.GetMetaString(IndexedFileSizePolicy.MetaKey));

            // No-op retries and unrelated scoped writes must retain the persisted omission.
            var (retryExit, retry) = RunAndCaptureJson([.. args, "--json"]);
            AssertPartial(retryExit, retry);
            var (otherExit, other) = RunAndCaptureJson([root, "--db", dbPath, "--files", "small.py", "--json"]);
            AssertPartial(otherExit, other);
            var (acceptedExit, accepted) = RunAndCaptureJson([.. args, "--allow-partial", "--json"]);
            Assert.Equal(CommandExitCodes.Success, acceptedExit);
            AssertPartial(CommandExitCodes.PartialResult, accepted);
            var (humanExit, _, stderr) = RunAndCaptureStreams([.. args]);
            Assert.Equal(CommandExitCodes.PartialResult, humanExit);
            Assert.Contains("actual_bytes=129; limit_bytes=128", stderr);
            Assert.Contains(".cdidxignore", stderr);
            var (statusExit, status) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.NotEqual(CommandExitCodes.Success, statusExit);
            Assert.False(status.GetProperty("index_complete").GetBoolean());
            Assert.Equal(1, status.GetProperty("size_omissions").GetProperty("affected_file_count").GetInt64());
            var repairs = status.GetProperty("repair_commands").EnumerateArray().ToArray();
            Assert.All(repairs.Where(r => r.GetProperty("action").GetString() == "index"), r =>
            {
                Assert.Contains("<reviewed-byte-limit>", r.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
                Assert.DoesNotContain("--rebuild", r.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
            });
            var (workspaceExit, workspace) = RunProgramAndCaptureJson(["workspace", "status", "--check", "--json"], root);
            Assert.NotEqual(CommandExitCodes.Success, workspaceExit);
            Assert.False(workspace.GetProperty("members")[0].GetProperty("index_health").GetProperty("index_complete").GetBoolean());

            // An explicit limit is the recovery decision, and an ordinary scan restores coverage.
            var (recoveredExit, recovered) = RunAndCaptureJson([.. args, "--max-file-bytes", "256", "--json"]);
            Assert.Equal(CommandExitCodes.Success, recoveredExit);
            Assert.True(recovered.GetProperty("index_complete").GetBoolean());
            Assert.True(recovered.GetProperty("reference_graph_complete").GetBoolean());
            var (healthyExit, _) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, healthyExit);
            var (limitedExit, _) = RunAndCaptureJson([.. args, "--max-file-bytes", "128", "--json"]);
            Assert.Equal(CommandExitCodes.PartialResult, limitedExit);
            File.WriteAllText(Path.Combine(root, ".cdidxignore"), "large.py\n");
            var (excludedExit, excluded) = RunAndCaptureJson([root, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, excludedExit);
            Assert.True(excluded.GetProperty("index_complete").GetBoolean());
            Assert.Equal(references, CountRows(dbPath, "symbol_references"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, previousEnvironment);
            Environment.CurrentDirectory = previousDirectory;
        }

        static void AssertPartial(int exit, JsonElement json)
        {
            Assert.Equal(CommandExitCodes.PartialResult, exit);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.IndexPartial, json.GetProperty("error_code").GetString());
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            var file = Assert.Single(json.GetProperty("size_omissions").GetProperty("files").EnumerateArray());
            Assert.Equal("large.py", file.GetProperty("path").GetString());
            Assert.Equal(129, file.GetProperty("actual_bytes").GetInt64());
            Assert.Equal(128, file.GetProperty("limit_bytes").GetInt64());
        }
    }

    [Fact]
    public void Index_SizeOmissionDiagnosticsAreBounded_Issue5295()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_size_diagnostics");
        var dbPath = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        for (var i = 0; i < 21; i++)
            File.WriteAllText(Path.Combine(project.Root, $"file{i:D2}.py"), "#" + new string('x', 128));
        var (exit, json) = RunAndCaptureJson([project.Root, "--db", dbPath, "--max-file-bytes", "128", "--json"]);
        Assert.Equal(CommandExitCodes.PartialResult, exit);
        var summary = json.GetProperty("size_omissions");
        Assert.Equal(21, summary.GetProperty("affected_file_count").GetInt64());
        Assert.Equal(20, summary.GetProperty("files").GetArrayLength());
        Assert.True(summary.GetProperty("files_truncated").GetBoolean());
        Assert.Equal(1, summary.GetProperty("omitted_file_count").GetInt64());
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE file_issues SET message = 'legacy size omission'; UPDATE files SET path = @path WHERE path = 'file00.py'";
            command.Parameters.AddWithValue("@path", "a\n" + new string('x', 600));
            command.ExecuteNonQuery();
        }
        using var reader = new DbReader(db);
        var diagnostic = reader.GetSizeOmissions()!.Files[0];
        Assert.Equal(512, diagnostic.Path.Length);
        Assert.True(diagnostic.PathTruncated);
        Assert.DoesNotContain('\n', diagnostic.Path);
        Assert.Equal(129, diagnostic.ActualBytes);
        Assert.Null(diagnostic.LimitBytes);
    }

    [Fact]
    public void Index_McpSizeOmissionResultAndStatusAgree_Issue5295()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_mcp_size_outcome");
        var root = project.Root;
        var dbPath = Path.Combine(root, ".cdidx", "codeindex.db");
        var previousDirectory = Environment.CurrentDirectory;
        var previousEnvironment = Environment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        try
        {
            Environment.CurrentDirectory = root;
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, null);
            File.WriteAllText(Path.Combine(root, "large.py"), "#" + new string('x', 128));
            File.WriteAllText(Path.Combine(root, "small.py"), "print('small')\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            foreach (var limit in new long?[] { 128, null, 256 })
            {
                var args = new JsonObject { ["path"] = root };
                if (limit.HasValue)
                    args["maxFileBytes"] = limit.Value;
                var response = Call("index", args);
                var structured = response["structuredContent"]!;
                var partial = limit != 256;
                Assert.Equal(partial, response["isError"]?.GetValue<bool>() ?? false);
                Assert.Equal(partial ? "partial" : "success", structured["status"]!.GetValue<string>());
                Assert.Equal(!partial, structured["index_complete"]!.GetValue<bool>());
                var status = Call("status", new JsonObject())["structuredContent"]!;
                Assert.Equal(!partial, status["index_complete"]!.GetValue<bool>());
                if (partial)
                {
                    Assert.Equal(128, structured["max_file_bytes"]!.GetValue<long>());
                    Assert.Equal(128, status["size_omissions"]!["files"]![0]!["limit_bytes"]!.GetValue<long>());
                }
            }

            JsonNode Call(string name, JsonObject args)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args },
                };
                var response = server.HandleMessage(JsonNode.Parse(request.ToJsonString())!)!;
                Assert.True(response["result"] != null, response.ToJsonString());
                return response["result"]!;
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, previousEnvironment);
        }
    }
}
