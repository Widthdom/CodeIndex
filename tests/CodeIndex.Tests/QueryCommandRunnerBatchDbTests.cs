using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
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
            using (var filesOutputDocument = ParseJsonOutput(filesRecord.GetProperty("stdout").GetString()!))
            {
                var filesOutput = filesOutputDocument.RootElement;
                Assert.Equal(1, filesOutput.GetProperty("count").GetInt32());
                Assert.Equal(1, filesOutput.GetProperty("files").GetInt32());
            }

            var statusRecord = statusRecordDocument.RootElement;
            Assert.Equal("batch_result", statusRecord.GetProperty("record").GetString());
            Assert.Equal("ok", statusRecord.GetProperty("status").GetString());
            Assert.Equal("status", statusRecord.GetProperty("command").GetString());
            using (var statusOutputDocument = ParseJsonOutput(statusRecord.GetProperty("stdout").GetString()!))
            {
                var statusOutput = statusOutputDocument.RootElement;
                Assert.Equal(1, statusOutput.GetProperty("files").GetInt32());
                Assert.Equal(Path.GetFullPath(childRoot), statusOutput.GetProperty("project_root").GetString());
            }

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
}
