using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_DryRun_WithChangedBetween_ReportsRenameAndDelete_Issue4335()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "old.py"), "print('old')\n");
            File.WriteAllText(Path.Combine(projectRoot, "deleted.py"), "print('deleted')\n");
            RunGit(projectRoot, "add", "old.py", "deleted.py");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(Path.Combine(projectRoot, "old.py"), Path.Combine(projectRoot, "new.py"));
            File.Delete(Path.Combine(projectRoot, "deleted.py"));
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "rename and delete");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--changed-between",
                "HEAD~1",
                "HEAD",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(2, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Contains(
                json.GetProperty("file_samples").EnumerateArray(),
                sample => sample.GetString() == "new.py");
            Assert.Equal(2, CountRows(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithChangedBetween_SkipsSkipWorktreeMissingPath_Issue4335()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var sparsePath = Path.Combine(projectRoot, "src", "sparse.cs");
            File.WriteAllText(sparsePath, "class Sparse { }\n");
            RunGit(projectRoot, "add", "src/sparse.cs");
            RunGit(projectRoot, "commit", "-m", "initial");
            var verifiedHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.WriteAllText(sparsePath, "class Sparse { void Changed() { } }\n");
            RunGit(projectRoot, "add", "src/sparse.cs");
            RunGit(projectRoot, "commit", "-m", "change sparse file");
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(verifiedHead, currentHead);
            RunGit(projectRoot, "update-index", "--skip-worktree", "src/sparse.cs");
            File.Delete(sparsePath);

            var (dryRunExitCode, dryRunJson) = RunAndCaptureJson([
                projectRoot,
                "--changed-between",
                "HEAD~1",
                "HEAD",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, dryRunExitCode);
            Assert.Equal("dry_run", dryRunJson.GetProperty("status").GetString());
            Assert.Equal(0, dryRunJson.GetProperty("files_total").GetInt32());
            Assert.Equal(0, dryRunJson.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(0, dryRunJson.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(0, dryRunJson.GetProperty("candidate_paths_processed").GetInt32());
            Assert.Equal(1, CountRows(dbPath, "files"));

            var (updateExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--changed-between",
                "HEAD~1",
                "HEAD",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, CountRows(dbPath, "files"));
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(verifiedHead, db.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
            Assert.Equal(currentHead, db.GetMetaString(DbContext.IndexedHeadShaMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }
}
