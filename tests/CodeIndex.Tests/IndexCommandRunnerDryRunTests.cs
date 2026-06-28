using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_DryRun_ReadOnlyUriDbPath_ReturnsDryRunSummary()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", readOnlyUri, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("languages").GetProperty("csharp").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithChangedBetweenMissingRef_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(projectRoot);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "HEAD", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("--changed-between requires exactly two refs", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithChangedBetweenInvalidRef_ReturnsUsageError_3046()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "HEAD", "missing-ref", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("failed to resolve changed files between git refs", json.GetProperty("message").GetString());
            Assert.Contains("cdidx index <projectPath> --changed-between <old-ref> <new-ref>", json.GetProperty("hint").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRunWithInvalidCommitRange_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "tracked.cs"), "class Sample {}\n");
            RunGit(projectRoot, "add", "tracked.cs");
            RunGit(projectRoot, "commit", "-m", "initial");
            File.WriteAllText(Path.Combine(projectRoot, "other.cs"), "class Other {}\n");
            RunGit(projectRoot, "add", "other.cs");
            RunGit(projectRoot, "commit", "-m", "other");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--commits", "HEAD~1..HEAD", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("ranges and tag refs are not accepted", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_IgnoresUnixFifoWithoutHanging()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            CreateUnixFifo(Path.Combine(projectRoot, "tool"));
            CreateUnixFifo(Path.Combine(projectRoot, "tool.sh"));
            CreateUnixFifo(Path.Combine(projectRoot, "Dockerfile"));

            var result = RunCliInSubprocessWithTimeout([projectRoot, "--dry-run", "--json"], projectRoot, TimeSpan.FromSeconds(10));

            Assert.False(result.TimedOut, "cdidx index --dry-run hung on a FIFO entry.");
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);

            using var document = JsonDocument.Parse(result.StdOut);
            Assert.Equal("dry_run", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("files_total").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_JsonCapsFileSamples()
    {
        var projectRoot = CreateTempProject();
        var fileCount = IndexCommandRunner.DryRunFileSampleLimit + 3;
        try
        {
            foreach (var i in Enumerable.Range(0, fileCount))
                File.WriteAllText(Path.Combine(projectRoot, $"sample{i:D3}.cs"), $"public class Sample{i} {{ }}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(fileCount, json.GetProperty("files_total").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("languages").GetProperty("csharp").GetInt32());
            Assert.Equal(IndexCommandRunner.DryRunFileSampleLimit, json.GetProperty("file_sample_limit").GetInt32());
            Assert.Equal(IndexCommandRunner.DefaultDryRunPathLimit, json.GetProperty("candidate_path_limit").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("candidate_paths_processed").GetInt32());
            Assert.False(json.GetProperty("candidate_paths_truncated").GetBoolean());
            Assert.False(json.GetProperty("totals_lower_bound").GetBoolean());
            Assert.True(json.GetProperty("file_samples_truncated").GetBoolean());
            Assert.Equal(IndexCommandRunner.DryRunFileSampleLimit, json.GetProperty("file_samples").GetArrayLength());
            Assert.Equal(0, json.GetProperty("errors_total").GetInt32());
            Assert.False(json.GetProperty("errors_truncated").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_PathLimitTruncatesCandidateProcessing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "sample001.cs"), "public class Sample001 { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "sample002.cs"), "public class Sample002 { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "sample003.cs"), "public class Sample003 { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--dry-run-path-limit", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(2, json.GetProperty("files_total").GetInt32());
            Assert.Equal(2, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(2, json.GetProperty("candidate_path_limit").GetInt32());
            Assert.Equal(2, json.GetProperty("candidate_paths_processed").GetInt32());
            Assert.True(json.GetProperty("candidate_paths_truncated").GetBoolean());
            Assert.True(json.GetProperty("totals_lower_bound").GetBoolean());
            Assert.True(json.GetProperty("file_samples_truncated").GetBoolean());
            Assert.Equal(0, json.GetProperty("errors_total").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_JsonCapsErrorSamples()
    {
        var projectRoot = CreateTempProject();
        var fileCount = IndexCommandRunner.DryRunErrorSampleLimit + 3;
        try
        {
            foreach (var i in Enumerable.Range(0, fileCount))
                File.WriteAllText(Path.Combine(projectRoot, $"large{i:D3}.cs"), $"public class Large{i} {{ }}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--max-file-bytes", "1", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("errors_total").GetInt32());
            Assert.Equal(IndexCommandRunner.DryRunErrorSampleLimit, json.GetProperty("error_limit").GetInt32());
            Assert.True(json.GetProperty("errors_truncated").GetBoolean());
            Assert.Equal(IndexCommandRunner.DryRunErrorSampleLimit, json.GetProperty("errors").GetArrayLength());
            Assert.Equal(IndexCommandRunner.DryRunFileSampleLimit, json.GetProperty("file_sample_limit").GetInt32());
            Assert.False(json.GetProperty("file_samples_truncated").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsProjectedUpdatesDeletesAndUnknowns()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "changed.cs"), "public class Changed { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "deleted.cs"), "public class Deleted { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(2, CountRows(dbPath, "files"));

            File.AppendAllText(Path.Combine(projectRoot, "changed.cs"), "public class ChangedAgain { }\n");
            File.Delete(Path.Combine(projectRoot, "deleted.cs"));
            File.WriteAllText(Path.Combine(projectRoot, "notes.unknownext"), "plain text\n");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "changed.cs",
                "deleted.cs",
                "notes.unknownext",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("estimates").GetBoolean());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(1, json.GetProperty("unknown_extension_total").GetInt32());
            Assert.Equal(0, json.GetProperty("unsupported_total").GetInt32());
            var mutations = json.GetProperty("estimated_table_mutations");
            Assert.True(mutations.GetProperty("files").GetInt64() >= 2);
            Assert.True(mutations.GetProperty("chunks").GetInt64() > 0);
            Assert.True(mutations.GetProperty("symbols").GetInt64() > 0);
            Assert.True(mutations.TryGetProperty("file_issues", out _));
            Assert.Equal(2, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_NormalizesUnicodeDbPathForEstimates()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var nfdFileName = "cafe\u0301.py";
            File.WriteAllText(Path.Combine(projectRoot, nfdFileName), "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", nfdFileName, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", nfdFileName, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("chunks").GetInt64() > 0);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsChecksumRenamePurgeWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "old.py");
            var newPath = Path.Combine(projectRoot, "new.py");
            File.WriteAllText(oldPath, "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "old.py", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.Move(oldPath, newPath);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "new.py", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 2);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsSupportedExtensionRenamePurgeWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.md");
            File.WriteAllText(oldPath, "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.py", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.Move(oldPath, newPath);
            File.AppendAllText(newPath, "# Updated during rename\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "foo.md", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 2);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsUnsupportedExtensionRenamePurgeWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.bin");
            File.WriteAllText(oldPath, "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.py", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.Move(oldPath, newPath);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "foo.bin", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 1);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScan_ReportsProjectedPurgesWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "kept.cs"), "public class Kept { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "removed.cs"), "public class Removed { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            File.Delete(Path.Combine(projectRoot, "removed.cs"));
            File.WriteAllText(Path.Combine(projectRoot, "notes.unknownext"), "plain text\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(1, json.GetProperty("unknown_extension_total").GetInt32());
            Assert.True(json.TryGetProperty("unsupported_total", out _));
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 2);
            Assert.Equal(2, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScan_ReportsUnreadableDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal("secret", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not scan directory due to permissions.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var (humanExitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--dry-run"]);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("secret", stderr);
            Assert.Contains("Could not scan directory due to permissions.", stderr);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScan_DoesNotProjectUnreadableSubtreePurge()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "stale.cs"), "public class Stale { }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(2, CountRows(dbPath, "files"));

            File.Delete(Path.Combine(projectRoot, "stale.cs"));
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(2, CountRows(dbPath, "files"));
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_IgnoresUnixFifoKnownFilename()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            CreateUnixFifo(Path.Combine(projectRoot, "Dockerfile"));

            var result = RunCliInSubprocessWithTimeout([projectRoot, "--files", "Dockerfile", "--dry-run", "--json"], projectRoot, TimeSpan.FromSeconds(10));

            Assert.False(result.TimedOut, "cdidx index --dry-run --files hung on a FIFO entry.");
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);

            using var document = JsonDocument.Parse(result.StdOut);
            Assert.Equal("dry_run", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("files_total").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_IgnoresAbsolutePathOutsideProjectRoot()
    {
        var projectRoot = CreateTempProject();
        var outsidePath = Path.Combine(Path.GetTempPath(), $"cdidx_dryrun_outside_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(outsidePath, "public class Outside { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", outsidePath, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
        }
        finally
        {
            DeleteFile(outsidePath);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_IgnoresTraversalOutsideProjectRoot()
    {
        var parentDir = Path.Combine(Path.GetTempPath(), $"cdidx_dryrun_parent_{Guid.NewGuid():N}");
        var projectRoot = Path.Combine(parentDir, "project");
        var outsidePath = Path.Combine(parentDir, "outside.cs");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(outsidePath, "public class Outside { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "../outside.cs", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
        }
        finally
        {
            DeleteDirectory(parentDir);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_DoesNotCountUnreadableKnownExtensionFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            File.WriteAllText(sourcePath, "public class A { }\n");
            SetUnixPermissions(sourcePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal("a.cs", json.GetProperty("errors")[0].GetProperty("file").GetString());
        }
        finally
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            if (File.Exists(sourcePath))
                SetUnixPermissions(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_DoesNotAcquireLock()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_dryrun_{Guid.NewGuid():N}.db");
        var lockPath = dbPath + ".lock";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            // Hold the lockfile while running --dry-run to prove dry-run never tries to acquire.
            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--dry-run", "--json"]);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("dry_run", json.GetProperty("status").GetString());
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
            DeleteFile(lockPath);
        }
    }
}
