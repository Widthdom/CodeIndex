using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunBatch_ImplicitDefaultDbPreservesAbsolutePathResolution_Issue5083()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_implicit_db");
        try
        {
            var sourceDirectory = Path.Combine(projectRoot, "src");
            var sourcePath = Path.Combine(sourceDirectory, "Sample.cs");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(sourcePath, "class Sample { void Method() { } }\n");
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Sample.cs",
                "csharp",
                "class Sample { void Method() { } }\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM codeindex_meta WHERE key = $key;";
                command.Parameters.AddWithValue("$key", DbContext.IndexedProjectRootMetaKey);
                command.ExecuteNonQuery();
            }

            lock (TestConsoleLock.Gate)
            {
                var previousCurrentDirectory = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = projectRoot;
                    var canonicalSourcePath = Path.Combine(
                        Path.GetFullPath(Environment.CurrentDirectory),
                        "src",
                        "Sample.cs");
                    var input = JsonSerializer.Serialize(
                        new[] { "excerpt", canonicalSourcePath, "--start", "1", "--json" }) + "\n";
                    var result = CaptureConsoleWithInput(
                        input,
                        () => QueryCommandRunner.RunBatch(["--json-summary"], _jsonOptions));
                    var lines = ParseJsonLines(result.Stdout);

                    Assert.True(
                        result.Result == CommandExitCodes.Success,
                        $"stdout: {result.Stdout}{Environment.NewLine}stderr: {result.Stderr}");
                    Assert.Equal(string.Empty, result.Stderr);
                    Assert.Equal("ok", lines[0].RootElement.GetProperty("status").GetString());
                    Assert.Equal(
                        "src/Sample.cs",
                        lines[0].RootElement.GetProperty("result").GetProperty("path").GetString());
                }
                finally
                {
                    Environment.CurrentDirectory = previousCurrentDirectory;
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_ParentDbPathStartingWithDashesRemainsOneOptionToken_Issue5083()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_batch_dash_db");
        try
        {
            var dbPath = Path.Combine(projectRoot, "--batch.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(projectRoot));
            }
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Dash.cs",
                "csharp",
                "class Dash { }\n");

            lock (TestConsoleLock.Gate)
            {
                var previousCurrentDirectory = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = projectRoot;
                    var input = JsonSerializer.Serialize(new[] { "files", "--count", "--json" }) + "\n";
                    var result = CaptureConsoleWithInput(
                        input,
                        () => QueryCommandRunner.RunBatch(
                            ["--db=--batch.db", "--json-summary"],
                            _jsonOptions));
                    var lines = ParseJsonLines(result.Stdout);

                    Assert.Equal(CommandExitCodes.Success, result.Result);
                    Assert.Equal(string.Empty, result.Stderr);
                    Assert.Equal("ok", lines[0].RootElement.GetProperty("status").GetString());
                    Assert.Equal(
                        1,
                        lines[0].RootElement.GetProperty("result").GetProperty("count").GetInt32());
                }
                finally
                {
                    Environment.CurrentDirectory = previousCurrentDirectory;
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBatch_ChildDbAfterPassThroughAndDanglingValuePreserveParsing_Issue5083()
    {
        var parentRoot = TestProjectHelper.CreateTempProject("cdidx_batch_parent_parser_db");
        var childRoot = TestProjectHelper.CreateTempProject("cdidx_batch_child_parser_db");
        try
        {
            var parentDbPath = TestProjectHelper.CreateProjectDb(parentRoot);
            var childDbPath = TestProjectHelper.CreateProjectDb(childRoot);
            TestProjectHelper.InsertIndexedFile(parentDbPath, "src/ParentA.cs", "csharp", "class ParentA { }\n");
            TestProjectHelper.InsertIndexedFile(parentDbPath, "src/ParentB.cs", "csharp", "class ParentB { }\n");
            TestProjectHelper.InsertIndexedFile(childDbPath, "src/Child.cs", "csharp", "class Child { }\n");

            var input = string.Join(
                "\n",
                JsonSerializer.Serialize(
                    new[] { "files", "--count", "--json", "--", "Child", "--db", childDbPath }),
                JsonSerializer.Serialize(new[] { "search", "--query" }),
                string.Empty);
            var result = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(
                    ["--db", parentDbPath, "--json-summary"],
                    _jsonOptions));
            var lines = ParseJsonLines(result.Stdout);

            Assert.Equal(CommandExitCodes.UsageError, result.Result);
            Assert.Equal(string.Empty, result.Stderr);
            Assert.Equal(3, lines.Count);
            Assert.Equal("ok", lines[0].RootElement.GetProperty("status").GetString());
            Assert.Equal(
                1,
                lines[0].RootElement.GetProperty("result").GetProperty("count").GetInt32());
            Assert.Equal("error", lines[1].RootElement.GetProperty("status").GetString());
            Assert.Equal(CommandExitCodes.UsageError, lines[1].RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal(1, lines[2].RootElement.GetProperty("command_failures").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(parentRoot);
            TestProjectHelper.DeleteDirectory(childRoot);
        }
    }

    [Fact]
    public void RunBatch_ParentDbKeepsStatusProvenanceConsistentAcrossFormatsAndOverrides_Issue5083()
    {
        var parentRoot = TestProjectHelper.CreateTempProject("cdidx_batch_parent_status_db");
        var childRoot = TestProjectHelper.CreateTempProject("cdidx_batch_child_status_db");
        try
        {
            var (parentDbPath, parentHead) = CreateBatchStatusDatabaseIssue5083(
                parentRoot,
                "parent",
                fileCount: 2);
            var (childDbPath, childHead) = CreateBatchStatusDatabaseIssue5083(
                childRoot,
                "child",
                fileCount: 1);
            Assert.NotEqual(parentHead, childHead);

            var textInput = JsonSerializer.Serialize(new[] { "status" }) + "\n";
            var textResult = CaptureConsoleWithInput(
                textInput,
                () => QueryCommandRunner.RunBatch(["--db", parentDbPath], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, textResult.Result);
            Assert.Equal(string.Empty, textResult.Stderr);
            Assert.Contains(parentHead, textResult.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(childHead, textResult.Stdout, StringComparison.Ordinal);

            var jsonInput = JsonSerializer.Serialize(new[] { "status", "--json" }) + "\n";
            var jsonResult = CaptureConsoleWithInput(
                jsonInput,
                () => QueryCommandRunner.RunBatch(["--db", parentDbPath], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, jsonResult.Result);
            Assert.Equal(string.Empty, jsonResult.Stderr);
            using (var jsonDocument = JsonDocument.Parse(jsonResult.Stdout))
            {
                AssertBatchStatusProvenanceIssue5083(
                    jsonDocument.RootElement,
                    parentRoot,
                    parentHead,
                    expectedFiles: 2);
            }

            var compactInput = JsonSerializer.Serialize(new[] { "status", "--json", "--compact" }) + "\n";
            var compactResult = CaptureConsoleWithInput(
                compactInput,
                () => QueryCommandRunner.RunBatch(["--db", parentDbPath], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, compactResult.Result);
            Assert.Equal(string.Empty, compactResult.Stderr);
            using (var compactDocument = JsonDocument.Parse(compactResult.Stdout))
            {
                AssertBatchStatusEnvelopeIssue5083(
                    compactDocument.RootElement,
                    parentDbPath,
                    parentRoot,
                    parentHead,
                    expectedFiles: 2);
            }

            var summaryInput = string.Join(
                "\n",
                JsonSerializer.Serialize(new[] { "status", "--json", "--compact" }),
                JsonSerializer.Serialize(new[] { "status", "--db", childDbPath, "--json", "--compact" }),
                JsonSerializer.Serialize(new[] { "status", "--json", "--compact" }),
                string.Empty);
            var summaryResult = CaptureConsoleWithInput(
                summaryInput,
                () => QueryCommandRunner.RunBatch(
                    ["--db", parentDbPath, "--json-summary", "--parallel", "2"],
                    _jsonOptions));
            var summaryLines = ParseJsonLines(summaryResult.Stdout);

            Assert.Equal(CommandExitCodes.Success, summaryResult.Result);
            Assert.Equal(string.Empty, summaryResult.Stderr);
            Assert.Equal(4, summaryLines.Count);
            using var firstParentRecord = summaryLines[0];
            using var childOverrideRecord = summaryLines[1];
            using var secondParentRecord = summaryLines[2];
            using var summaryRecord = summaryLines[3];
            AssertBatchStatusEnvelopeIssue5083(
                firstParentRecord.RootElement.GetProperty("result"),
                parentDbPath,
                parentRoot,
                parentHead,
                expectedFiles: 2);
            AssertBatchStatusEnvelopeIssue5083(
                childOverrideRecord.RootElement.GetProperty("result"),
                childDbPath,
                childRoot,
                childHead,
                expectedFiles: 1);
            AssertBatchStatusEnvelopeIssue5083(
                secondParentRecord.RootElement.GetProperty("result"),
                parentDbPath,
                parentRoot,
                parentHead,
                expectedFiles: 2);
            Assert.Equal("batch_summary", summaryRecord.RootElement.GetProperty("record").GetString());
            Assert.Equal(3, summaryRecord.RootElement.GetProperty("commands_processed").GetInt32());
            Assert.Equal(0, summaryRecord.RootElement.GetProperty("command_failures").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(parentRoot);
            TestProjectHelper.DeleteDirectory(childRoot);
        }
    }

    [Fact]
    public void RunBatch_ChildExplicitDbUsesChildDatabaseAndProjectRoot_Issue4354()
    {
        var parentRoot = TestProjectHelper.CreateTempProject("cdidx_batch_parent_db");
        var childRoot = TestProjectHelper.CreateTempProject("cdidx_batch_child_db");
        try
        {
            var parentDbPath = TestProjectHelper.CreateProjectDb(parentRoot);
            var childDbPath = TestProjectHelper.CreateProjectDb(childRoot);
            TestProjectHelper.InsertIndexedFile(parentDbPath, "src/parent-a.cs", "csharp", "class ParentA {}\n");
            TestProjectHelper.InsertIndexedFile(parentDbPath, "src/parent-b.cs", "csharp", "class ParentB {}\n");
            TestProjectHelper.InsertIndexedFile(childDbPath, "src/child.cs", "csharp", "class Child {}\n");

            var input = string.Join(
                "\n",
                JsonSerializer.Serialize(new[] { "files", "--db", childDbPath, "--count", "--json" }),
                JsonSerializer.Serialize(new[] { "status", "--db", childDbPath, "--json" }),
                string.Empty);
            var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                input,
                () => QueryCommandRunner.RunBatch(["--db", parentDbPath, "--json-summary"], _jsonOptions));
            var lines = ParseJsonLines(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(3, lines.Count);
            using var filesRecordDocument = lines[0];
            using var statusRecordDocument = lines[1];
            using var summaryDocument = lines[2];

            var filesRecord = filesRecordDocument.RootElement;
            Assert.Equal("batch_result", filesRecord.GetProperty("record").GetString());
            Assert.Equal("ok", filesRecord.GetProperty("status").GetString());
            Assert.Equal("files", filesRecord.GetProperty("command").GetString());
            Assert.False(filesRecord.TryGetProperty("stdout", out _));
            var filesOutput = filesRecord.GetProperty("result");
            Assert.Equal(1, filesOutput.GetProperty("count").GetInt32());
            Assert.Equal(1, filesOutput.GetProperty("files").GetInt32());

            var statusRecord = statusRecordDocument.RootElement;
            Assert.Equal("batch_result", statusRecord.GetProperty("record").GetString());
            Assert.Equal("ok", statusRecord.GetProperty("status").GetString());
            Assert.Equal("status", statusRecord.GetProperty("command").GetString());
            Assert.False(statusRecord.TryGetProperty("stdout", out _));
            var statusOutput = statusRecord.GetProperty("result");
            Assert.Equal(1, statusOutput.GetProperty("files").GetInt32());
            Assert.Equal(Path.GetFullPath(childRoot), statusOutput.GetProperty("project_root").GetString());

            var summary = summaryDocument.RootElement;
            Assert.Equal("batch_summary", summary.GetProperty("record").GetString());
            Assert.Equal(2, summary.GetProperty("commands_processed").GetInt32());
            Assert.Equal(0, summary.GetProperty("command_failures").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(parentRoot);
            TestProjectHelper.DeleteDirectory(childRoot);
        }
    }

    [Fact]
    public void RunBatch_ChildExplicitDbUsesChildLanguageRegistry_Issue4842()
    {
        var parentRoot = TestProjectHelper.CreateTempProject("cdidx_batch_lang_parent");
        var childRoot = TestProjectHelper.CreateTempProject("cdidx_batch_lang_child");
        try
        {
            var parentDbPath = TestProjectHelper.CreateProjectDb(parentRoot);
            var childDbPath = TestProjectHelper.CreateProjectDb(childRoot);
            var patternDirectory = Path.Combine(childRoot, ".cdidx", "patterns");
            Directory.CreateDirectory(patternDirectory);
            File.WriteAllText(
                Path.Combine(patternDirectory, "child.yaml"),
                "language: \"child-dsl\"\nextensions:\n  - extension: \".child\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");
            TestProjectHelper.InsertIndexedFile(childDbPath, "src/example.child", "child-dsl", "needle\n");
            var input = JsonSerializer.Serialize(
                new[] { "search", "needle", "--db", childDbPath, "--lang", "child-dsl", "--json=array" }) + "\n";

            lock (TestConsoleLock.Gate)
            {
                try
                {
                    ExtractorPluginRegistry.ResetForTests();

                    var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
                        input,
                        () => QueryCommandRunner.RunBatch(["--db", parentDbPath, "--json-summary"], _jsonOptions));
                    var lines = ParseJsonLines(stdout);

                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    Assert.Equal("ok", lines[0].RootElement.GetProperty("status").GetString());
                    Assert.Equal(
                        "src/example.child",
                        lines[0].RootElement.GetProperty("result")[0].GetProperty("path").GetString());
                }
                finally
                {
                    ExtractorPluginRegistry.ResetForTests();
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(parentRoot);
            TestProjectHelper.DeleteDirectory(childRoot);
        }
    }

    private static (string DbPath, string Head) CreateBatchStatusDatabaseIssue5083(
        string projectRoot,
        string prefix,
        int fileCount)
    {
        TestProjectHelper.InitializeGitRepo(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        for (var i = 0; i < fileCount; i++)
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "src", $"{prefix}-{i}.cs"),
                $"class {prefix}_{i} {{ }}\n");
        }
        TestProjectHelper.RunGit(projectRoot, "add", "src");
        TestProjectHelper.RunGit(projectRoot, "commit", "-m", $"{prefix} initial");
        var head = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
        var branch = TestProjectHelper.RunGit(projectRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        for (var i = 0; i < fileCount; i++)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/{prefix}-{i}.cs",
                "csharp",
                $"class {prefix}_{i} {{ }}\n");
        }
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.IndexedHeadCommitMetaKey, head);
            writer.SetMeta(DbContext.WorkspaceVerifiedHeadShaMetaKey, head);
            writer.SetMeta(DbContext.IndexedHeadShaMetaKey, head);
            writer.SetMeta(DbContext.IndexedHeadBranchMetaKey, branch);
            writer.SetMeta(DbContext.IndexedHeadTimestampMetaKey, DateTime.UtcNow.ToString("O"));
        }

        return (dbPath, head);
    }

    private static void AssertBatchStatusEnvelopeIssue5083(
        JsonElement envelope,
        string expectedDbPath,
        string expectedProjectRoot,
        string expectedHead,
        int expectedFiles)
    {
        var metadata = envelope.GetProperty("metadata");
        Assert.Equal(expectedDbPath, metadata.GetProperty("db_path").GetString());
        Assert.Equal(expectedHead, metadata.GetProperty("indexed_at_head_sha").GetString());
        var results = envelope.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        var status = results[0];
        Assert.Equal(expectedFiles, status.GetProperty("files").GetInt32());
        Assert.Equal(expectedHead, status.GetProperty("git_head").GetString());
        if (status.TryGetProperty("project_root", out var projectRoot))
            Assert.Equal(Path.GetFullPath(expectedProjectRoot), projectRoot.GetString());
        AssertBatchHeadFreshnessIssue5083(status.GetProperty("head_freshness"), expectedHead);
    }

    private static void AssertBatchStatusProvenanceIssue5083(
        JsonElement status,
        string expectedProjectRoot,
        string expectedHead,
        int expectedFiles)
    {
        Assert.Equal(expectedFiles, status.GetProperty("files").GetInt32());
        Assert.Equal(Path.GetFullPath(expectedProjectRoot), status.GetProperty("project_root").GetString());
        Assert.Equal(expectedHead, status.GetProperty("git_head").GetString());
        Assert.Equal(expectedHead, status.GetProperty("indexed_head_commit").GetString());
        Assert.Equal(expectedHead, status.GetProperty("workspace_verified_head_sha").GetString());
        Assert.Equal(expectedHead, status.GetProperty("indexed_head_sha").GetString());
        Assert.False(status.GetProperty("worktree_head_changed").GetBoolean());
        AssertBatchHeadFreshnessIssue5083(status.GetProperty("head_freshness"), expectedHead);
    }

    private static void AssertBatchHeadFreshnessIssue5083(
        JsonElement freshness,
        string expectedHead)
    {
        Assert.Equal(expectedHead, freshness.GetProperty("runtime_head").GetString());
        Assert.Equal(expectedHead, freshness.GetProperty("indexed_head").GetString());
        Assert.Equal(expectedHead, freshness.GetProperty("legacy_full_scan_head").GetString());
        Assert.Equal(expectedHead, freshness.GetProperty("workspace_verified_head").GetString());
        Assert.Equal(expectedHead, freshness.GetProperty("latest_index_head").GetString());
        Assert.False(freshness.GetProperty("worktree_head_changed").GetBoolean());
        Assert.NotEqual("head_changed", freshness.GetProperty("state").GetString());
    }
}
