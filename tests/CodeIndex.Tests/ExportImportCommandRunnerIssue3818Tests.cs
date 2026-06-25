using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ExportImportCommandRunnerIssue3818Tests
{
    [Fact]
    public void RunImport_JsonCancellationReturnsInterrupted_Issue3818()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunImport(["archive.cdidx.zip", "--db", "codeindex.db", "--json"], jsonOptions, cts.Token));

        Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
        Assert.Equal(string.Empty, stderr);
        AssertImportError(stdout, "open_archive", CommandErrorCodes.Interrupted);
    }

    [Fact]
    public void ReplaceImportedDatabase_PostMoveCancellationRollsBackDestination_Issue3818()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_cancel_rollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(tempPath, "imported db");
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = _ =>
                throw new OperationCanceledException();

            Assert.Throws<OperationCanceledException>(() =>
                ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath));

            Assert.Equal("existing db", File.ReadAllText(dbPath));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = null;
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunImport_JsonReplacementFailureIncludesResidualDiagnostics_Issue3818()
    {
        var sourceRoot = TestProjectHelper.CreateTempProject("import_replacement_diag_source");
        var targetRoot = TestProjectHelper.CreateTempProject("import_replacement_diag_target");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(sourceRoot);
            var archivePath = ExportArchive(sourceRoot, sourceDbPath);
            var targetDbPath = TestProjectHelper.CreateProjectDb(targetRoot);
            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = _ =>
                throw new IOException("simulated post-move failure");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", targetDbPath, "--json"], jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.Equal("replace_db", root.GetProperty("phase").GetString());
            Assert.Equal("import_replacement_failed", root.GetProperty("error_code").GetString());
            Assert.Contains(
                root.GetProperty("diagnostics").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "import_replace_destination_state");
            Assert.True(DbContext.TryValidateExistingCodeIndexDb(targetDbPath, out _, out _));
        }
        finally
        {
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = null;
            TestProjectHelper.DeleteDirectory(sourceRoot);
            TestProjectHelper.DeleteDirectory(targetRoot);
        }
    }

    [Fact]
    public void CopyToWithLimit_CancellationBeforeReadThrowsWithoutWriting_Issue3818()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 4, cts.Token));
        Assert.Equal(0, target.Length);
    }

    private static string ExportArchive(string projectRoot, string sourceDbPath)
    {
        var archivePath = Path.Combine(projectRoot, "codeindex.cdidx.zip");
        var (exitCode, _, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunExport([archivePath, "--db", sourceDbPath], new JsonSerializerOptions(), "test"));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return archivePath;
    }

    private static void AssertImportError(string stdout, string expectedPhase, string expectedCode)
    {
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(expectedPhase, root.GetProperty("phase").GetString());
        Assert.Equal(expectedCode, root.GetProperty("error_code").GetString());
    }
}
