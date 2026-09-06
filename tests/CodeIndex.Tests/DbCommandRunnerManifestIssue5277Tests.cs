using System.Runtime.InteropServices;
using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class DbCommandRunnerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_RestoreManifest_RejectsGrowthAfterLengthProbe_Issue5277(bool dryRun)
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_manifest_growth_5277");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            Assert.Equal(CommandExitCodes.Success,
                RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]).ExitCode);
            var before = File.ReadAllBytes(dbPath);
            DbCommandRunner.AvailableFreeSpaceForTesting = _ => long.MaxValue;
            var invoked = false;
            DbCommandRunner.CheckpointManifestAfterLengthProbeForTesting = path =>
            {
                invoked = true;
                File.AppendAllText(path, "\npadding=" + new string('x', DbCommandRunner.CheckpointManifestByteLimit));
            };

            string[] args = dryRun
                ? ["restore", "saved", "--dry-run", "--db", dbPath, "--json"]
                : ["restore", "saved", "--db", dbPath, "--json"];
            var (exitCode, json) = RunAndCaptureJson(args);

            Assert.True(invoked);
            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            if (dryRun)
            {
                Assert.False(json.GetProperty("manifest_valid").GetBoolean());
                Assert.Contains(json.GetProperty("diagnostics").EnumerateArray(),
                    diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_manifest_too_large");
            }
            else
                Assert.Equal(CommandErrorCodes.DbError, json.GetProperty("error_code").GetString());
            Assert.Equal(before, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
            DbCommandRunner.CheckpointManifestAfterLengthProbeForTesting = null;
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreManifest_PreservesEncodingsAndExactByteBoundaries_Issue5277()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_manifest_encodings_5277");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            Assert.Equal(CommandExitCodes.Success,
                RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]).ExitCode);
            var path = Path.Combine(dbPath + ".checkpoints", "saved", "manifest.txt");
            DbCommandRunner.AvailableFreeSpaceForTesting = _ => long.MaxValue;
            const string manifest = "name=saved\r\ncreated_at_utc=2026-01-01T00:00:00Z\r\ndb_file=codeindex.db\r\npadding=日本語";
            Encoding[] encodings = [new UTF8Encoding(false), new UTF8Encoding(true),
                Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32, new UTF32Encoding(true, true)];
            foreach (var encoding in encodings)
            {
                var initialBytes = encoding.GetPreamble().Concat(encoding.GetBytes(manifest)).ToArray();
                foreach (var size in new[] { initialBytes.Length, DbCommandRunner.CheckpointManifestByteLimit - 4,
                             DbCommandRunner.CheckpointManifestByteLimit, DbCommandRunner.CheckpointManifestByteLimit + 4 })
                {
                    var bytes = initialBytes.Concat(encoding.GetBytes(new string('x',
                        (size - initialBytes.Length) / encoding.GetByteCount("x")))).ToArray();
                    Assert.Equal(size, bytes.Length);
                    File.WriteAllBytes(path, bytes);
                    var (exitCode, json) = RunAndCaptureJson(["restore", "saved", "--dry-run", "--db", dbPath, "--json"]);
                    var accepted = size <= DbCommandRunner.CheckpointManifestByteLimit;
                    Assert.Equal(accepted ? CommandExitCodes.Success : CommandExitCodes.DatabaseError, exitCode);
                    Assert.Equal(accepted, json.GetProperty("manifest_valid").GetBoolean());
                    if (!accepted)
                        Assert.Contains(json.GetProperty("diagnostics").EnumerateArray(),
                            diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_manifest_too_large");
                }
            }
        }
        finally
        {
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Theory]
    [InlineData("oversize-before-open")]
    [InlineData("replacement-after-open")]
    [InlineData("directory-before-open")]
    [InlineData("symlink-before-open")]
    [InlineData("fifo-before-open")]
    [InlineData("unreadable-before-open")]
    [InlineData("missing")]
    public void Run_RestoreManifest_ValidatesOpenedFile_Issue5277(string scenario)
    {
        if (OperatingSystem.IsWindows() && scenario is "symlink-before-open" or "fifo-before-open" or "unreadable-before-open")
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_manifest_replace_5277");
        var dbPath = Path.Combine(root, "codeindex.db");
        string? unreadablePath = null;
        try
        {
            InitializeEmptyDb(dbPath);
            Assert.Equal(CommandExitCodes.Success,
                RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]).ExitCode);
            var manifestPath = Path.Combine(dbPath + ".checkpoints", "saved", "manifest.txt");
            var before = File.ReadAllBytes(dbPath);
            DbCommandRunner.AvailableFreeSpaceForTesting = _ => long.MaxValue;
            var invoked = false;
            void Replace(string path)
            {
                invoked = true;
                File.Move(path, path + ".original");
                switch (scenario)
                {
                    case "directory-before-open":
                        Directory.CreateDirectory(path);
                        break;
                    case "symlink-before-open":
                        File.CreateSymbolicLink(path, path + ".original");
                        break;
                    case "fifo-before-open":
                        Assert.Equal(0, MakeManifestFifo(path, 384));
                        break;
                    case "unreadable-before-open":
                        File.Copy(path + ".original", path);
                        unreadablePath = path;
#pragma warning disable CA1416
                        File.SetUnixFileMode(path, UnixFileMode.None);
#pragma warning restore CA1416
                        break;
                    default:
                        File.WriteAllText(path, new string('x', DbCommandRunner.CheckpointManifestByteLimit + 1));
                        break;
                }
            }

            if (scenario == "missing")
                File.Delete(manifestPath);
            else if (scenario == "replacement-after-open")
                DbCommandRunner.CheckpointManifestAfterLengthProbeForTesting = Replace;
            else
                DbCommandRunner.CheckpointManifestBeforeOpenForTesting = Replace;

            var (exitCode, json) = RunAndCaptureJson(["restore", "saved", "--dry-run", "--db", dbPath, "--json"]);
            Assert.Equal(scenario != "missing", invoked);
            var accepted = scenario == "replacement-after-open";
            Assert.Equal(accepted ? CommandExitCodes.Success : CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal(accepted, json.GetProperty("manifest_valid").GetBoolean());
            if (!accepted)
            {
                var expectedCode = scenario switch
                {
                    "missing" => "checkpoint_manifest_missing",
                    "oversize-before-open" => "checkpoint_manifest_too_large",
                    _ => "checkpoint_manifest_invalid",
                };
                Assert.Contains(json.GetProperty("diagnostics").EnumerateArray(),
                    diagnostic => diagnostic.GetProperty("code").GetString() == expectedCode);
            }
            Assert.Equal(before, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
#pragma warning disable CA1416
            if (unreadablePath is not null)
                File.SetUnixFileMode(unreadablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            DbCommandRunner.CheckpointManifestBeforeOpenForTesting = null;
            DbCommandRunner.CheckpointManifestAfterLengthProbeForTesting = null;
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreManifest_CancellationAfterOpenDoesNotMutateDatabase_Issue5277()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_manifest_cancel_5277");
        var dbPath = Path.Combine(root, "codeindex.db");
        using var cancellation = new CancellationTokenSource();
        try
        {
            InitializeEmptyDb(dbPath);
            Assert.Equal(CommandExitCodes.Success,
                RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]).ExitCode);
            var before = File.ReadAllBytes(dbPath);
            DbCommandRunner.CheckpointManifestAfterLengthProbeForTesting = _ => cancellation.Cancel();

            var (exitCode, json) = RunAndCaptureJson(["restore", "saved", "--db", dbPath, "--json"], cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(CommandErrorCodes.Interrupted, json.GetProperty("error_code").GetString());
            Assert.Equal(before, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
        }
        finally
        {
            DbCommandRunner.CheckpointManifestAfterLengthProbeForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void ManifestReader_BoundsConsumptionAndPropagatesCancellation_Issue5277()
    {
        const int budget = 32;
        foreach (var length in new[] { budget - 1, budget, budget + 1, budget * 4 })
        {
            using var stream = new MemoryStream(Enumerable.Repeat((byte)'x', length).ToArray());
            var text = DataDirectorySecurity.ReadTextWithinLimit(stream, budget);
            Assert.Equal(length <= budget ? new string('x', length) : null, text);
            Assert.Equal(Math.Min(length, budget + 1), stream.Position);
            Assert.True(stream.CanRead);
        }

        using var cancellation = new CancellationTokenSource();
        using var cancelStream = new CancelManifestReadStream(cancellation);
        Assert.Throws<OperationCanceledException>(() =>
            DataDirectorySecurity.ReadTextWithinLimit(cancelStream, budget, cancellation.Token));
        Assert.Equal(1, cancelStream.ReadCount);
        Assert.Throws<OperationCanceledException>(() =>
            DataDirectorySecurity.ReadTextWithinLimit(cancelStream, budget, cancellation.Token));
        Assert.Equal(1, cancelStream.ReadCount);
    }

    private sealed class CancelManifestReadStream(CancellationTokenSource cancellation) : MemoryStream(new byte[64])
    {
        internal int ReadCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            cancellation.Cancel();
            return base.Read(buffer, offset, count);
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeManifestFifo(string path, uint mode);
}
