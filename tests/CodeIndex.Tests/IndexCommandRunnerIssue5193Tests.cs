using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void RunOptimizeFts_DryRunPathDisclosureRequiresShowPaths_Issue5193()
    {
        var relativeDbPath = $"cdidx_optimize_preview_paths_{Guid.NewGuid():N}.db";
        var dbPath = Path.GetFullPath(relativeDbPath);
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                db.InitializeSchema();

            SqliteConnection.ClearAllPools();
            var bytesBefore = File.ReadAllBytes(dbPath);
            string? writesBefore;
            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
                writesBefore = db.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey);

            var cases = new[]
            {
                new { InputPath = dbPath, ShowPaths = false, ExpectedPath = "<redacted>" },
                new { InputPath = dbPath, ShowPaths = true, ExpectedPath = dbPath },
                new { InputPath = relativeDbPath, ShowPaths = false, ExpectedPath = relativeDbPath },
                new { InputPath = relativeDbPath, ShowPaths = true, ExpectedPath = dbPath },
            };

            foreach (var testCase in cases)
            {
                foreach (var useIndexAlias in new[] { false, true })
                {
                    foreach (var json in new[] { false, true })
                    {
                        var args = new List<string>
                        {
                            "--db",
                            testCase.InputPath,
                            "--dry-run",
                        };
                        if (json)
                            args.Add("--json");
                        if (testCase.ShowPaths)
                            args.Add("--show-paths");

                        var (exitCode, output) = RunOptimizePreviewAndCapture(
                            args.ToArray(),
                            useIndexAlias);

                        Assert.Equal(CommandExitCodes.Success, exitCode);
                        if (json)
                        {
                            using var document = JsonDocument.Parse(output);
                            Assert.Equal(
                                testCase.ExpectedPath,
                                document.RootElement.GetProperty("db_path").GetString());
                        }
                        else
                        {
                            Assert.Contains(testCase.ExpectedPath, output, StringComparison.Ordinal);
                        }

                        if (!testCase.ShowPaths)
                            Assert.DoesNotContain(dbPath, output, StringComparison.Ordinal);
                    }
                }
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal(bytesBefore, File.ReadAllBytes(dbPath));
            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            {
                Assert.Equal(
                    writesBefore,
                    db.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath + "-shm");
            DeleteFile(dbPath + "-wal");
            DeleteFile(dbPath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RunIndexOptimize_DryRunBlankDbInputReportsResolvedFallback_Issue5193(
        string blankDbPath)
    {
        var projectPath = CreateTempProject();
        var dbPath = Path.Combine(projectPath, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                db.InitializeSchema();

            SqliteConnection.ClearAllPools();
            var bytesBefore = File.ReadAllBytes(dbPath);
            foreach (var showPaths in new[] { false, true })
            {
                foreach (var json in new[] { false, true })
                {
                    var args = new List<string>
                    {
                        "--db",
                        blankDbPath,
                        "--dry-run",
                    };
                    if (json)
                        args.Add("--json");
                    if (showPaths)
                        args.Add("--show-paths");

                    var (exitCode, output) = RunOptimizePreviewAndCapture(
                        args.ToArray(),
                        useIndexAlias: true,
                        projectPath: projectPath);

                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    var expectedPath = showPaths ? dbPath : "<redacted>";
                    if (json)
                    {
                        using var document = JsonDocument.Parse(output);
                        Assert.Equal(
                            expectedPath,
                            document.RootElement.GetProperty("db_path").GetString());
                    }
                    else
                    {
                        Assert.Contains(expectedPath, output, StringComparison.Ordinal);
                    }

                    if (!showPaths)
                        Assert.DoesNotContain(dbPath, output, StringComparison.Ordinal);
                }
            }

            SqliteConnection.ClearAllPools();
            Assert.Equal(bytesBefore, File.ReadAllBytes(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectPath);
        }
    }

    private (int ExitCode, string Output) RunOptimizePreviewAndCapture(
        string[] args,
        bool useIndexAlias,
        string projectPath = ".")
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            try
            {
                using var stdout = new StringWriter();
                Console.SetOut(stdout);
                var exitCode = useIndexAlias
                    ? IndexCommandRunner.Run(
                        [projectPath, "--optimize", .. args],
                        _jsonOptions,
                        cancellationForTesting: null,
                        output: null)
                    : IndexCommandRunner.RunOptimizeFts(
                        args,
                        _jsonOptions,
                        forceLogicalObjectSizeFallbackForTesting: true);
                return (exitCode, stdout.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }
}
