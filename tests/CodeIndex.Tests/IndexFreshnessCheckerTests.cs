using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class IndexFreshnessCheckerTests
{
    [Fact]
    public void FormatScanFailureSample_ClassifiesIoMessage_Issue3471()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var rawPath = "/Users/example/private/project/secret.cs";
        var exception = new IOException($"Could not read {rawPath} token={secret}");

        var sample = IndexFreshnessChecker.FormatScanFailureSample("src/App.cs", exception);

        Assert.Equal("src/App.cs: io-error", sample);
        Assert.DoesNotContain(rawPath, sample);
        Assert.DoesNotContain(secret, sample);
    }

    [Fact]
    public void FormatScanFailureSample_ClassifiesAccessDenied_Issue3471()
    {
        var exception = new UnauthorizedAccessException("/Users/example/private/project/secret.cs");

        var sample = IndexFreshnessChecker.FormatScanFailureSample("src/Secret.cs", exception);

        Assert.Equal("src/Secret.cs: access-denied", sample);
    }

    [Fact]
    public void FormatScanFailureSample_ClassifiesProbeFailure_Issue3471()
    {
        var exception = new InvalidOperationException("probe failed for /Users/example/private/project/secret.cs");

        var sample = IndexFreshnessChecker.FormatScanFailureSample("src/Broken.cs", exception);

        Assert.Equal("src/Broken.cs: probe-failed", sample);
    }

    [Fact]
    public void Check_StampedPathCaseSensitivityAvoidsFilesystemWriteProbe_Issue3828()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_case_stamp");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousGitProbe = GitHelper.FileSystemIgnoreCaseProbeForTesting;
        var previousIndexerProbe = FileIndexer.FileSystemIgnoreCaseProbeForTesting;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using var db = new DbContext(dbPath);
            db.InitializeSchema();
            var reader = new DbReader(db);
            GitHelper.FileSystemIgnoreCaseProbeForTesting = _ => throw new IOException("git case probe should not run");
            FileIndexer.FileSystemIgnoreCaseProbeForTesting = _ => throw new IOException("indexer case probe should not run");

            var result = IndexFreshnessChecker.Check(reader, projectRoot, pathCaseSensitive: true);

            Assert.True(result.Checked);
        }
        finally
        {
            GitHelper.FileSystemIgnoreCaseProbeForTesting = previousGitProbe;
            FileIndexer.FileSystemIgnoreCaseProbeForTesting = previousIndexerProbe;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
