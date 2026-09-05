using System.Globalization;
using System.Text;
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
    [InlineData("cli")]
    [InlineData("environment")]
    [InlineData("default")]
    public void Index_SizePolicySurvivesOrdinaryChecksAndScopedUpdates_Issue5258(string source)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_size_policy");
        var root = project.Root;
        var dbPath = Path.Combine(root, ".cdidx", "codeindex.db");
        var originalEnvironment = Environment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        var originalDirectory = Environment.CurrentDirectory;
        const long largerLimit = 8 * 1024 * 1024;
        var expectedLimit = source == "default" ? FileIndexer.DefaultMaxFileSizeBytes : largerLimit;
        try
        {
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable,
                source == "environment" ? largerLimit.ToString(CultureInfo.InvariantCulture) : source == "cli" ? "1024" : null);
            Environment.CurrentDirectory = root;
            Directory.CreateDirectory(Path.Combine(root, "a"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"),
                """{"members":["a","b"],"index_strategy":"single"}""");
            File.WriteAllText(Path.Combine(root, "a", "small.py"), "print('small')\n");
            var largePath = Path.Combine(root, "b", "large.py");
            var content = new StringBuilder();
            var minimumBytes = source == "default" ? 256 : FileIndexer.DefaultMaxFileSizeBytes;
            while (content.Length <= minimumBytes)
                content.Append("# ").Append('x', 120).Append('\n');
            File.WriteAllText(largePath, content.ToString(), new UTF8Encoding(false));

            var args = new List<string> { root, "--db", dbPath, "--json" };
            if (source == "cli")
                args.AddRange(["--max-file-bytes", largerLimit.ToString(CultureInfo.InvariantCulture)]);
            var (initialExit, initial) = RunAndCaptureJson(args.ToArray());
            Assert.Equal(CommandExitCodes.Success, initialExit);
            Assert.True(initial.GetProperty("index_complete").GetBoolean());
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, null);
            AssertHealthy();
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, "1024");
            AssertHealthy();
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, null);

            File.AppendAllText(Path.Combine(root, "a", "small.py"), "print('updated')\n");
            var (scopedExit, scoped) = RunAndCaptureJson(
                [root, "--db", dbPath, "--files", "a/small.py", "--max-file-bytes", "1024", "--json"]);
            Assert.Equal(CommandExitCodes.Success, scopedExit);
            Assert.True(scoped.GetProperty("index_complete").GetBoolean());
            AssertHealthy();
            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
                Assert.Equal(expectedLimit.ToString(CultureInfo.InvariantCulture), db.GetMetaString(IndexedFileSizePolicy.MetaKey));

            File.AppendAllText(largePath, "# changed\n");
            var (refreshExit, refresh) = RunAndCaptureJson([root, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExit);
            Assert.True(refresh.GetProperty("index_complete").GetBoolean());
            AssertHealthy();

            // Legacy and invalid stamps use bounded evidence already present in files.
            foreach (var legacyStamp in new string?[] { null, "2147483648", "-1", "garbage" })
            {
                using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                    new DbWriter(db).SetMeta(IndexedFileSizePolicy.MetaKey, legacyStamp);
                AssertHealthy();
            }

            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            using (var reader = new DbReader(db))
            {
                foreach (var failure in new Exception[] { new IOException("private path"), new UnauthorizedAccessException("secret"), new InvalidOperationException("probe") })
                {
                    var check = IndexFreshnessChecker.Check(reader, root, internalIndexDatabasePath: dbPath,
                        beforeFileLoadForTesting: path => { if (path == "b/large.py") throw failure; });
                    Assert.False(check.Checked);
                    Assert.Equal(0, check.MissingFileCount);
                    Assert.Equal(1, check.UnverifiableFileCount);
                    Assert.Contains("b/large.py", check.UnverifiableFiles);
                    Assert.Equal(1, check.ScanErrorCount);
                }
            }
            File.Delete(largePath);
            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            using (var reader = new DbReader(db))
            {
                var check = IndexFreshnessChecker.Check(reader, root, internalIndexDatabasePath: dbPath);
                Assert.True(check.Checked);
                Assert.Equal(1, check.MissingFileCount);
                Assert.Contains("b/large.py", check.MissingFiles);
                Assert.Equal(0, check.UnverifiableFileCount);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, originalEnvironment);
            Environment.CurrentDirectory = originalDirectory;
        }

        void AssertHealthy()
        {
            var (statusExit, status) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExit);
            Assert.True(status.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
            var (workspaceExit, workspace) = RunProgramAndCaptureJson(["workspace", "status", "--check", "--json"], root);
            Assert.Equal(CommandExitCodes.Success, workspaceExit);
            Assert.Equal(2, workspace.GetProperty("member_health_summary").GetProperty("healthy_member_count").GetInt32());
        }
    }

    [Fact]
    public void Index_McpPersistsAndReusesBoundedSizePolicy_Issue5258()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_mcp_size_policy");
        var root = project.Root;
        var dbPath = Path.Combine(root, ".cdidx", "codeindex.db");
        var previousDirectory = Environment.CurrentDirectory;
        var previousEnvironment = Environment.GetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable);
        try
        {
            Environment.CurrentDirectory = root;
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, null);
            var path = Path.Combine(root, "sample.py");
            File.WriteAllText(path, "print('sample')\r\n");
            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var first = CallIndex(new JsonObject { ["path"] = root, ["maxFileBytes"] = 1024L });
            Assert.Equal(1024, first["max_file_bytes"]!.GetValue<long>());
            var dry = CallIndex(new JsonObject { ["path"] = root, ["dryRun"] = true });
            Assert.Equal(1024, dry["max_file_bytes"]!.GetValue<long>());
            var next = CallIndex(new JsonObject { ["path"] = root });
            Assert.Equal(1024, next["max_file_bytes"]!.GetValue<long>());
            var (statusExit, _) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExit);

            // The persisted budget remains a real read bound after a file grows.
            File.WriteAllText(path, new string('x', 1025));
            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var reader = new DbReader(db);
            var failed = IndexFreshnessChecker.Check(reader, root, internalIndexDatabasePath: dbPath);
            Assert.False(failed.Checked);
            Assert.Equal(1, failed.ScanErrorCount);
            Assert.Equal(1, failed.UnverifiableFileCount);
            Assert.Equal(0, failed.MissingFileCount);

            // Invalid environment input must match the existing warning's 4 MiB fallback,
            // rather than silently keeping the saved 1 KiB admission limit.
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, "invalid");
            var (recoveredExit, recovered) = RunAndCaptureJson([root, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, recoveredExit);
            Assert.True(recovered.GetProperty("index_complete").GetBoolean());
            using (var recoveredDb = new DbContext(DbOpenIntent.QueryOnly, dbPath))
                Assert.Equal(FileIndexer.DefaultMaxFileSizeBytes.ToString(CultureInfo.InvariantCulture),
                    recoveredDb.GetMetaString(IndexedFileSizePolicy.MetaKey));

            JsonNode CallIndex(JsonObject arguments)
            {
                var request = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = "index", ["arguments"] = arguments },
                };
                var response = server.HandleMessage(JsonNode.Parse(request.ToJsonString())!)!;
                Assert.True(response["result"] is not null, response.ToJsonString());
                Assert.False(response["result"]?["isError"]?.GetValue<bool>() ?? false, response.ToJsonString());
                return response["result"]!["structuredContent"]!;
            }
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable(FileIndexer.MaxFileSizeEnvironmentVariable, previousEnvironment);
        }
    }
}
