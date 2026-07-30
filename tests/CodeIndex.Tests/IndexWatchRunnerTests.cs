using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for <see cref="IndexWatchRunner"/> (`cdidx index --watch`).
/// `cdidx index --watch` のテスト。
/// </summary>
[Collection("Console sensitive")]
public class IndexWatchRunnerTests
{
    private static readonly TimeSpan TopLevelWatchTimeout = TimeSpan.FromSeconds(30);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void FileChangeBatcher_TryDrain_NoEvents_ReturnsFalse()
    {
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(100));
        Assert.False(batcher.TryDrain(out var batch, out var rescan, out var reason));
        Assert.Empty(batch);
        Assert.False(rescan);
        Assert.Null(reason);
    }

    [Fact]
    public void FileChangeBatcher_TryDrain_BeforeDebounceElapsed_ReturnsFalse()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), timeProvider);
        batcher.Add("/repo/a.py");
        // Less than the debounce window has elapsed.
        Assert.False(batcher.TryDrain(out var batch, out var rescan, out _));
        Assert.Empty(batch);
        Assert.False(rescan);
    }

    [Fact]
    public void FileChangeBatcher_TryDrain_AfterDebounceElapsed_ReturnsBatchOnce()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), timeProvider);

        batcher.Add("/repo/a.py");
        batcher.Add("/repo/b.py");
        // Coalesce duplicates regardless of casing on case-insensitive filesystems.
        batcher.Add("/repo/A.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out _));
        Assert.False(rescan);
        Assert.Equal(2, batch.Count);

        // Subsequent drain without new events returns false.
        Assert.False(batcher.TryDrain(out _, out _, out _));
    }

    [Fact]
    public void FileChangeBatcher_TryDrainImmediately_ClosesStartupSnapshotBeforeDebounce_Issue4594()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromSeconds(30), timeProvider);
        batcher.Add("/repo/handoff.cs");

        Assert.True(batcher.TryDrainImmediately(out var batch, out var rescan, out var reason));
        Assert.Equal(["/repo/handoff.cs"], batch);
        Assert.False(rescan);
        Assert.Null(reason);
        Assert.False(batcher.TryDrainImmediately(out _, out _, out _));
    }

    [Fact]
    public void FileChangeBatcher_CaseSensitive_KeepsDistinctPaths()
    {
        // On case-sensitive filesystems (Linux ext4), `foo.py` and `Foo.py` are different
        // files; a rename event arrives as Add("foo.py") + Add("Foo.py") and BOTH must be
        // surfaced so the sub-update can purge the old name and index the new one.
        // 大小区別 FS では rename の old/new を別エントリで保持する必要がある。
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), timeProvider, ignoreCase: false);

        batcher.Add("/repo/foo.py");
        batcher.Add("/repo/Foo.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(600));
        Assert.True(batcher.TryDrain(out var batch, out _, out _));
        Assert.Equal(2, batch.Count);
        Assert.Contains("/repo/foo.py", batch);
        Assert.Contains("/repo/Foo.py", batch);
    }

    [Fact]
    public void FileChangeBatcher_RequestFullRescan_DrainsOverflowAndReason()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(100), timeProvider);
        batcher.Add("/repo/a.py");
        batcher.RequestFullRescan("buffer overflowed");

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out var reason));
        Assert.True(rescan);
        Assert.Equal("buffer overflowed", reason);
        Assert.Empty(batch);
    }

    [Fact]
    public void FileChangeBatcher_RequestFullRescan_SanitizesAndBoundsReason_Issue3804()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(100), timeProvider);
        var rawReason = "watch failed for /Users/alice/private/project/secret.txt token=ghp_"
            + new string('a', 40)
            + " "
            + new string('x', IndexWatchRunner.MaxWatchDiagnosticChars * 2);

        batcher.RequestFullRescan(rawReason);

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(batcher.TryDrain(out _, out var rescan, out var reason));
        Assert.True(rescan);
        Assert.NotNull(reason);
        Assert.True(reason!.Length <= IndexWatchRunner.MaxWatchDiagnosticChars);
        Assert.Contains("[redacted]", reason);
        Assert.Contains("[truncated]", reason);
        Assert.DoesNotContain("/Users/alice/private/project/secret.txt", reason);
        Assert.DoesNotContain("ghp_", reason);
    }

    [Fact]
    public void DeleteSpoolFile_DeleteFailureWarnsAndSuppresses_Issue3962()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("watch_spool_cleanup");
        try
        {
            var spoolPath = Path.Combine(projectRoot, "cdidx-watch-subrun-test.jsonl");
            File.WriteAllText(spoolPath, "{}");

            var stderr = ConsoleCapture.CaptureError(() =>
            {
                var deleted = IndexWatchRunner.DeleteSpoolFileForTesting(
                    spoolPath,
                    _ => throw new IOException("delete denied"));

                Assert.False(deleted);
            });

            Assert.True(File.Exists(spoolPath));
            Assert.Contains("Warning [watch_spool_cleanup_failed]", stderr, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(spoolPath), stderr, StringComparison.Ordinal);
            Assert.Contains("io", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FileChangeBatcher_Add_WhenPendingPathLimitExceeded_CollapsesToFullRescan()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(
            TimeSpan.FromMilliseconds(100),
            timeProvider,
            maxPendingPaths: 2);

        batcher.Add("/repo/a.py");
        batcher.Add("/repo/b.py");
        batcher.Add("/repo/c.py");
        batcher.Add("/repo/d.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out var reason));
        Assert.True(rescan);
        Assert.Empty(batch);
        Assert.Contains("pending path limit exceeded", reason);
        Assert.Contains("2", reason);
    }

    [Fact]
    public void ShouldIgnoreWatchInternalPath_DefaultDataDir_IgnoresOnlyOwnedArtifacts_Issue4592()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var internalPaths = new[]
            {
                dbPath,
                dbPath + "-wal",
                dbPath + "-shm",
                dbPath + ".lock",
                dbPath + ".lock.info",
                dbPath + ".lock.tmp",
            };

            Assert.All(internalPaths, path =>
                Assert.True(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                    projectRoot,
                    dbPath,
                    path,
                    ignoreCase: false,
                    dbPathExplicit: false)));
            Assert.False(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, ".cdidx", "lock.info"),
                ignoreCase: false,
                dbPathExplicit: false));
            Assert.False(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, ".cdidx", "nested", "state.tmp"),
                ignoreCase: false,
                dbPathExplicit: false));
            Assert.False(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, "src", "app.cs"),
                ignoreCase: false,
                dbPathExplicit: false));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ShouldIgnoreWatchInternalPath_ExplicitDb_IgnoresOnlyDbSidecars_Issue4592()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var dbPath = Path.Combine(projectRoot, "src", "watch.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            Assert.True(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                dbPath + "-wal",
                ignoreCase: false,
                dbPathExplicit: true));
            Assert.True(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                dbPath + ".lock.info",
                ignoreCase: false,
                dbPathExplicit: true));
            Assert.False(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, ".cdidx", "suggestions-codeindex.json"),
                ignoreCase: false,
                dbPathExplicit: true));
            Assert.False(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, "src", "app.cs"),
                ignoreCase: false,
                dbPathExplicit: true));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ClassifyWatchPath_ReconcilesInputsAndUsesSharedCdidxMembership_Issue4592()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            Assert.Equal(
                IndexWatchRunner.WatchPathDisposition.Reconcile,
                IndexWatchRunner.ClassifyWatchPathForTesting(
                    projectRoot,
                    dbPath,
                    Path.Combine(projectRoot, ".gitignore"),
                    ignoreCase: false,
                    dbPathExplicit: false));
            Assert.Equal(
                IndexWatchRunner.WatchPathDisposition.Reconcile,
                IndexWatchRunner.ClassifyWatchPathForTesting(
                    projectRoot,
                    dbPath,
                    Path.Combine(projectRoot, ".cdidx", "patterns", "custom.yaml"),
                    ignoreCase: false,
                    dbPathExplicit: false));
            Assert.Equal(
                IndexWatchRunner.WatchPathDisposition.Reconcile,
                IndexWatchRunner.ClassifyWatchPathForTesting(
                    projectRoot,
                    dbPath,
                    Path.Combine(projectRoot, ".cdidx", "plugins", "custom.dll"),
                    ignoreCase: false,
                    dbPathExplicit: false));
            Assert.Equal(
                IndexWatchRunner.WatchPathDisposition.Ignore,
                IndexWatchRunner.ClassifyWatchPathForTesting(
                    projectRoot,
                    dbPath,
                    Path.Combine(projectRoot, ".cdidx", "audit-notes.md"),
                    ignoreCase: false,
                    dbPathExplicit: false));
            Assert.Equal(
                IndexWatchRunner.WatchPathDisposition.Ignore,
                IndexWatchRunner.ClassifyWatchPathForTesting(
                    projectRoot,
                    dbPath,
                    dbPath + "-wal",
                    ignoreCase: false,
                    dbPathExplicit: false));
            Assert.Equal(
                IndexWatchRunner.WatchPathDisposition.Index,
                IndexWatchRunner.ClassifyWatchPathForTesting(
                    projectRoot,
                    dbPath,
                    Path.Combine(projectRoot, "src", "app.cs"),
                    ignoreCase: false,
                    dbPathExplicit: false));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ClassifyWatchPath_CustomDbArtifactsUseSharedScannerExclusions_Issue4611()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var dbPath = Path.Combine(projectRoot, "index.json");
            Directory.CreateDirectory(dbPath + ".checkpoints");
            File.WriteAllText(dbPath + ".checkpoints/state.json", "{}\n");
            File.WriteAllText(dbPath + ".restore-tmp-session", string.Empty);
            File.WriteAllText(dbPath + ".restore-backup-session", string.Empty);
            foreach (var path in new[]
            {
                dbPath + ".checkpoints",
                dbPath + ".checkpoints/state.json",
                dbPath + ".restore-tmp-session",
                dbPath + ".restore-backup-session",
            })
            {
                Assert.Equal(
                    IndexWatchRunner.WatchPathDisposition.Ignore,
                    IndexWatchRunner.ClassifyWatchPathForTesting(
                        projectRoot,
                        dbPath,
                        path,
                        ignoreCase: false,
                        dbPathExplicit: true));
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ShouldIgnoreWatchInternalPath_DataDirResolution_IgnoresResolvedDbParent_Issue4351()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var dbPath = Path.Combine(projectRoot, ".custom-cdidx", "codeindex.db");

            Assert.True(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, ".custom-cdidx", "lock.info"),
                ignoreCase: false,
                dbPathExplicit: false));
            Assert.False(IndexWatchRunner.ShouldIgnoreWatchInternalPathForTesting(
                projectRoot,
                dbPath,
                Path.Combine(projectRoot, ".custom-cdidx-source", "app.cs"),
                ignoreCase: false,
                dbPathExplicit: false));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FileChangeBatcher_NewEventDuringWait_ExtendsDebounce()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), timeProvider);
        batcher.Add("/repo/a.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        // A new event before the window closes resets the timer.
        batcher.Add("/repo/b.py");
        Assert.False(batcher.TryDrain(out _, out _, out _));

        timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        // 400ms after the second event is still < 500ms; not ready yet.
        Assert.False(batcher.TryDrain(out _, out _, out _));

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        // Now > 500ms after the second event.
        Assert.True(batcher.TryDrain(out var batch, out _, out _));
        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public void FileChangeBatcher_TryDrain_UsesMonotonicElapsedTime_Issue4129()
    {
        var timeProvider = new ManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), timeProvider);
        batcher.Add("/repo/a.py");

        timeProvider.AdjustUtc(TimeSpan.FromHours(-1));
        timeProvider.Advance(TimeSpan.FromMilliseconds(600));

        Assert.True(batcher.TryDrain(out var batch, out var rescan, out _));
        Assert.False(rescan);
        Assert.Equal(["/repo/a.py"], batch);
    }

    [Fact]
    public void BuildSubRunArgs_JsonSubRun_IsQuiet()
    {
        var options = new IndexCommandOptions
        {
            ProjectPath = "/repo",
            Json = true,
            Watch = true,
        };
        var method = typeof(IndexWatchRunner).GetMethod("BuildSubRunArgs", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var args = Assert.IsType<List<string>>(method.Invoke(null, [options, "/repo/.cdidx/codeindex.db"]));

        Assert.Contains("--json", args);
        Assert.Contains("--quiet", args);
        AssertOptionValue(args, "--db", "/repo/.cdidx/codeindex.db");
    }

    [Fact]
    public void BuildSubRunArgs_MaxFileBytes_PreservesWatchOverride()
    {
        var options = new IndexCommandOptions
        {
            ProjectPath = "/repo",
            Json = true,
            Watch = true,
            MaxFileSizeBytes = 50L * 1024L * 1024L,
        };
        var method = typeof(IndexWatchRunner).GetMethod("BuildSubRunArgs", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var args = Assert.IsType<List<string>>(method.Invoke(null, [options, "/repo/.cdidx/codeindex.db"]));

        var flagIndex = args.IndexOf("--max-file-bytes");
        Assert.True(flagIndex >= 0);
        Assert.Equal((50L * 1024L * 1024L).ToString(System.Globalization.CultureInfo.InvariantCulture), args[flagIndex + 1]);
    }

    [Fact]
    public void BuildSubRunArgs_MaxSymbolsPerFile_PreservesWatchOverride()
    {
        var options = new IndexCommandOptions
        {
            ProjectPath = "/repo",
            Json = true,
            Watch = true,
            MaxSymbolsPerFile = 42,
        };
        var method = typeof(IndexWatchRunner).GetMethod("BuildSubRunArgs", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var args = Assert.IsType<List<string>>(method.Invoke(null, [options, "/repo/.cdidx/codeindex.db"]));

        var flagIndex = args.IndexOf("--max-symbols-per-file");
        Assert.True(flagIndex >= 0);
        Assert.Equal("42", args[flagIndex + 1]);
    }

    [Fact]
    public void BuildPartialUpdateBatches_SplitsBeforeArgumentLimit_Issue3803()
    {
        var baseArgs = new List<string> { "/repo", "--json", "--quiet" };
        var longPath = "/" + new string('a', IndexWatchRunner.MaxSubRunArgumentChars / 2);

        var batches = IndexWatchRunner.BuildPartialUpdateBatches(
            baseArgs,
            [longPath + "1", longPath + "2", longPath + "3"]);

        Assert.NotNull(batches);
        Assert.Equal(3, batches.Count);
        Assert.All(batches, batch => Assert.Single(batch));
    }

    [Fact]
    public void BuildPartialUpdateBatches_OversizedSinglePathRequestsFullRescan_Issue3803()
    {
        var baseArgs = new List<string> { "/repo", "--json", "--quiet" };
        var oversizedPath = "/" + new string('p', IndexWatchRunner.MaxSubRunArgumentChars);

        var batches = IndexWatchRunner.BuildPartialUpdateBatches(baseArgs, [oversizedPath]);

        Assert.Null(batches);
    }

    [Fact]
    public void WatchSubRunCaptureWriter_CapsCaptureAndPreservesSpooledOutput_Issue3803()
    {
        using var spool = new StringWriter();
        using var writer = new IndexWatchRunner.WatchSubRunCaptureWriter(10, spool);
        var payload = new string('x', 32);

        writer.Write(payload);
        writer.Flush();

        Assert.Equal(new string('x', 10), writer.CapturedText);
        Assert.True(writer.Truncated);
        Assert.Equal(payload, spool.ToString());
    }

    [Fact]
    public void CreateSubRunSpoolFileStream_OnPosix_CreatesPrivateFileUpFront_Issue3984()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var spoolPath = Path.Combine(projectRoot, "subrun.jsonl");

            using var stream = IndexWatchRunner.CreateSubRunSpoolFileStream(spoolPath);

            Assert.True(stream.CanWrite);
            AssertPrivateFileMode(spoolPath);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BuildBatchPathSamples_BoundsLongRelativePaths_Issue3804()
    {
        var method = typeof(IndexWatchRunner).GetMethod("BuildBatchPathSamples", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var longSegment = string.Concat(Enumerable.Repeat("very-long-segment-", 32));
        var longRelative = Path.Combine("src", longSegment + ".cs");
        object?[] parameters =
        [
            "/repo",
            new[] { Path.Combine("/repo", longRelative) },
            false,
        ];

        var samples = Assert.IsType<List<string>>(method.Invoke(null, parameters));

        var sample = Assert.Single(samples);
        Assert.True((bool)parameters[2]!);
        Assert.True(sample.Length <= IndexWatchRunner.BatchPathSampleMaxChars);
        Assert.Contains("[truncated]", sample);
        Assert.DoesNotContain(longSegment, sample);
    }

    [Fact]
    public void InvokeSubRunAndEmit_JsonSubRunFailure_EmitsFailedStatusAndExitCode()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                Json = true,
                Watch = true,
            };
            var method = typeof(IndexWatchRunner).GetMethod("InvokeSubRunAndEmit", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var args = new List<string> { projectRoot, "--json", "--quiet", "--unknown-watch-test-option" };
            string capturedOut;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                var originalErr = Console.Error;
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try
                {
                    exitCode = Assert.IsType<int>(method.Invoke(
                        null,
                        [
                            options,
                            _jsonOptions,
                            args,
                            Stopwatch.StartNew(),
                            "updated",
                            3,
                            "incremental",
                            new[] { Path.Combine(projectRoot, "a.cs"), Path.Combine(projectRoot, "b.cs") },
                            CancellationToken.None,
                        ]));
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }
                capturedOut = stdout.ToString();
            }

            Assert.NotEqual(CommandExitCodes.Success, exitCode);
            var firstLine = Assert.Single(capturedOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(1));
            using var doc = JsonDocument.Parse(firstLine);
            Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("incremental", doc.RootElement.GetProperty("phase").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("batch_size").GetInt32());
            Assert.Equal(IndexWatchRunner.BatchPathSampleLimit, doc.RootElement.GetProperty("batch_path_sample_limit").GetInt32());
            Assert.False(doc.RootElement.GetProperty("batch_path_samples_truncated").GetBoolean());
            Assert.Equal(2, doc.RootElement.GetProperty("batch_path_samples").GetArrayLength());
            Assert.Equal("a.cs", doc.RootElement.GetProperty("batch_path_samples")[0].GetString());
            Assert.Equal(exitCode, doc.RootElement.GetProperty("exit_code").GetInt32());
            Assert.Equal("missing_summary", doc.RootElement.GetProperty("sub_run_parse_status").GetString());
            var reason = doc.RootElement.GetProperty("reason").GetString();
            Assert.NotNull(reason);
            Assert.Contains("updated sub-run exited with code", reason);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void InvokeSubRunAndEmit_JsonFullRescan_EmitsCompletionAndScanCounters_Issue4356()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "A.cs"), "namespace Demo; public sealed class A { public string Name => \"A\"; }\n");

            var prebuildJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var prebuildExit);
            Assert.Equal(CommandExitCodes.Success, prebuildExit);
            Assert.Contains("\"status\"", prebuildJson, StringComparison.Ordinal);

            var bPath = Path.Combine(srcDir, "B.cs");
            var cPath = Path.Combine(srcDir, "C.cs");
            File.WriteAllText(bPath, "namespace Demo; public sealed class B { public string Name => \"B\"; }\n");
            File.WriteAllText(cPath, "namespace Demo; public sealed class C { public string Name => \"C\"; }\n");

            var timeProvider = new ManualTimeProvider();
            var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(100), timeProvider, maxPendingPaths: 1);
            batcher.Add(bPath);
            batcher.Add(cPath);
            timeProvider.Advance(TimeSpan.FromMilliseconds(200));
            Assert.True(batcher.TryDrain(out var overflowBatch, out var fullRescan, out var overflowReason));
            Assert.Empty(overflowBatch);
            Assert.True(fullRescan);
            Assert.Contains("pending path limit exceeded", overflowReason);

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Watch = true,
                WatchPendingPathLimit = 1,
            };
            var method = typeof(IndexWatchRunner).GetMethod("InvokeSubRunAndEmit", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var args = new List<string> { projectRoot, "--json", "--quiet", "--db", dbPath };
            string capturedOut;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                var originalErr = Console.Error;
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try
                {
                    exitCode = Assert.IsType<int>(method.Invoke(
                        null,
                        [options, _jsonOptions, args, Stopwatch.StartNew(), "rescanned", null, "incremental", null, CancellationToken.None]));
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }
                capturedOut = stdout.ToString();
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var firstLine = Assert.Single(capturedOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(1));
            using var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            Assert.Equal("rescanned", root.GetProperty("status").GetString());
            Assert.Equal("incremental", root.GetProperty("phase").GetString());
            Assert.Equal("full_workspace", root.GetProperty("rescan_scope").GetString());
            Assert.True(root.GetProperty("rescan_completed").GetBoolean());
            Assert.True(root.GetProperty("files_total").GetInt64() >= 3);
            Assert.True(root.GetProperty("files_scanned").GetInt32() >= 2);
            Assert.True(root.GetProperty("files_skipped").GetInt32() >= 0);
            Assert.True(root.GetProperty("files_purged").GetInt32() >= 0);
            if (root.GetProperty("files_skipped").GetInt32() > 0)
                Assert.Equal("unchanged_or_reused_files", root.GetProperty("files_skipped_category").GetString());

            Assert.True(HasIndexedFile(dbPath, "src/B.cs"));
            Assert.True(HasIndexedFile(dbPath, "src/C.cs"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void InvokeSubRunAndEmit_HumanSubRunFailure_IncludesExitCode()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                Json = false,
                Watch = true,
            };
            var method = typeof(IndexWatchRunner).GetMethod("InvokeSubRunAndEmit", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var args = new List<string> { projectRoot, "--json", "--quiet", "--unknown-watch-test-option" };
            string capturedErr;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                var originalErr = Console.Error;
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderr);
                try
                {
                    exitCode = Assert.IsType<int>(method.Invoke(
                        null,
                        [options, _jsonOptions, args, Stopwatch.StartNew(), "updated", 3, "incremental", Array.Empty<string>(), CancellationToken.None]));
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }
                capturedErr = stderr.ToString();
            }

            Assert.NotEqual(CommandExitCodes.Success, exitCode);
            Assert.Contains("[watch] failed", capturedErr);
            Assert.Contains($"exit code {exitCode}", capturedErr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void InvokeSubRunAndEmit_CancelDuringActiveUpdate_DoesNotCaptureUnrelatedStdout_Issue4591()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "target.py");
            File.WriteAllText(sourcePath, "print('before')\n");
            var prebuildJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var prebuildExit);
            Assert.Equal(CommandExitCodes.Success, prebuildExit);
            Assert.Contains("\"status\"", prebuildJson, StringComparison.Ordinal);
            File.WriteAllText(sourcePath, "print('after')\n");

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = false,
                Watch = true,
            };
            var args = new List<string>
            {
                projectRoot,
                "--json",
                "--quiet",
                "--db",
                dbPath,
                "--files",
                sourcePath,
            };

            using var cts = new CancellationTokenSource();
            using var extractionStarted = new ManualResetEventSlim();
            const string unrelatedOutput = "unrelated-runner-stdout-4591";
            string capturedOut;
            string capturedErr;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                var originalErr = Console.Error;
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                Task<int>? subRunTask = null;
                Console.SetOut(stdout);
                Console.SetError(stderr);
                IndexCommandRunner.UpdateExtractionWorkStartedForTesting = () =>
                {
                    extractionStarted.Set();
                    cts.Token.WaitHandle.WaitOne();
                    cts.Token.ThrowIfCancellationRequested();
                };
                try
                {
                    subRunTask = Task.Run(() => IndexWatchRunner.InvokeSubRunAndEmit(
                        options,
                        _jsonOptions,
                        args,
                        Stopwatch.StartNew(),
                        "updated",
                        1,
                        "incremental",
                        [sourcePath],
                        cts.Token));

                    Assert.True(extractionStarted.Wait(TimeSpan.FromSeconds(10)), "The watch sub-run did not start extraction work.");
                    Console.Out.WriteLine(unrelatedOutput);
                    cts.Cancel();
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                    exitCode = subRunTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    cts.Cancel();
                    if (subRunTask is { IsCompleted: false })
                        SpinWait.SpinUntil(() => subRunTask.IsCompleted, TimeSpan.FromSeconds(10));
                    IndexCommandRunner.UpdateExtractionWorkStartedForTesting = null;
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }
                capturedOut = stdout.ToString();
                capturedErr = stderr.ToString();
            }

            Assert.Equal(CommandExitCodes.Interrupted, exitCode);
            Assert.Contains(unrelatedOutput, capturedOut, StringComparison.Ordinal);
            Assert.DoesNotContain(CommandErrorCodes.Interrupted, capturedOut, StringComparison.Ordinal);
            Assert.Contains("[watch] failed", capturedErr, StringComparison.Ordinal);
            Assert.Contains($"exit code {CommandExitCodes.Interrupted}", capturedErr, StringComparison.Ordinal);
        }
        finally
        {
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void EmitWatchOverflow_Json_EmitsStructuredRecoveryCommand()
    {
        var parallelism = IndexCommandRunner.DefaultIndexParallelism() == 1 ? 2 : 1;
        var options = new IndexCommandOptions
        {
            ProjectPath = "/repo",
            DataDir = "/custom-data",
            Json = true,
            Watch = true,
            MaxFileSizeBytes = 4096,
            MaxSymbolsPerFile = 42,
            Parallelism = parallelism,
            WatchPendingPathLimit = 1234,
            SymlinkPolicy = FileIndexer.SymlinkPolicy.All,
            SymbolKindFilter = SymbolKindFilter.Create(["class", "function"], ["test.method"], parseError: null),
        };
        var method = typeof(IndexWatchRunner).GetMethod(
            "EmitWatchOverflow",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [
                typeof(IndexCommandOptions),
                typeof(JsonSerializerOptions),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
            ],
            modifiers: null);
        Assert.NotNull(method);
        const string resolvedDbPath = "/custom-data/codeindex.db";

        string capturedOut;
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            Console.SetOut(stdout);
            try
            {
                method.Invoke(
                    null,
                    [
                        options,
                        _jsonOptions,
                        "buffer full",
                        resolvedDbPath,
                        "incremental",
                        "fsevents",
                        "event_stream_overflow",
                    ]);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            capturedOut = stdout.ToString();
        }

        using var doc = JsonDocument.Parse(capturedOut);
        Assert.Equal(JsonOutputContract.ApiVersion, doc.RootElement.GetProperty("api_version").GetString());
        Assert.Equal("overflow", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("phase").GetString());
        Assert.Equal("fsevents", doc.RootElement.GetProperty("backend").GetString());
        Assert.Equal("event_stream_overflow", doc.RootElement.GetProperty("recovery_reason").GetString());
        Assert.Equal("buffer full", doc.RootElement.GetProperty("overflow_reason").GetString());
        Assert.Equal(1234, doc.RootElement.GetProperty("watch_pending_path_limit").GetInt32());
        var recovery = doc.RootElement.GetProperty("recovery_command");
        Assert.Equal("cdidx", recovery.GetProperty("command").GetString());
        var args = recovery.GetProperty("args").EnumerateArray().Select(static item => item.GetString()).ToList();
        Assert.Equal("index", args[0]);
        Assert.Equal("[redacted]", args[1]);
        Assert.Contains("--json", args);
        Assert.Contains("--quiet", args);
        AssertOptionValue(args, "--db", "[redacted]");
        Assert.DoesNotContain("/repo", args);
        Assert.DoesNotContain(resolvedDbPath, args);
        AssertOptionValue(args, "--max-file-bytes", "4096");
        AssertOptionValue(args, "--max-symbols-per-file", "42");
        AssertOptionValue(args, "--parallelism", parallelism.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AssertOptionValue(args, "--follow-symlinks", "all");
        AssertOptionValue(args, "--include-symbol-kind", "class,function");
        AssertOptionValue(args, "--exclude-symbol-kind", "test.method");
    }

    [Fact]
    public void FormatHumanSummary_BoundedSubRunJson_ExtractsCounts()
    {
        var summary = InvokeFormatHumanSummary(
            "updated",
            2,
            123,
            """{"summary":{"updated":1,"removed":2,"errors":3}}""" + Environment.NewLine,
            CommandExitCodes.Success);

        Assert.Contains("[watch] updated 2 paths", summary);
        Assert.Contains("exit code 0", summary);
        Assert.Contains("updated 1", summary);
        Assert.Contains("removed 2", summary);
        Assert.Contains("errors 3", summary);
    }

    [Fact]
    public void FormatHumanSummary_OversizedSubRunJson_UsesTerseSummary()
    {
        var oversized = $$"""{"summary":{"updated":1,"removed":2,"errors":3},"padding":"{{new string('x', IndexWatchRunner.MaxHumanSummarySubRunJsonChars + 1)}}"}""";

        var summary = InvokeFormatHumanSummary(
            "updated",
            2,
            123,
            oversized,
            CommandExitCodes.Success);

        Assert.Contains("exit code 0", summary);
        Assert.DoesNotContain("updated 1", summary);
        Assert.DoesNotContain("removed 2", summary);
        Assert.DoesNotContain("errors 3", summary);
    }

    [Fact]
    public void FormatHumanSummary_DeepSubRunJson_UsesTerseSummary()
    {
        var depth = IndexWatchRunner.MaxHumanSummaryJsonDepth + 4;
        var nestedStart = new string('[', depth);
        var nestedEnd = new string(']', depth);
        var deepJson = $$"""{"summary":{"updated":1,"removed":2,"errors":3},"deep":{{nestedStart}}0{{nestedEnd}}}""";

        var summary = InvokeFormatHumanSummary(
            "updated",
            2,
            123,
            deepJson,
            CommandExitCodes.Success);

        Assert.Contains("exit code 0", summary);
        Assert.DoesNotContain("updated 1", summary);
        Assert.DoesNotContain("removed 2", summary);
        Assert.DoesNotContain("errors 3", summary);
    }

    [Fact]
    public void RunCore_CancellationToken_StopsImmediately()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.RunGit(projectRoot, "init");
            TestProjectHelper.RunGit(projectRoot, "config", "core.ignorecase", "false");
            File.WriteAllText(Path.Combine(projectRoot, "hello.py"), "print('hi')\n");

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Watch = true,
                WatchDebounceMs = 50,
                WatchPendingPathLimit = 123,
            };

            using var cts = new CancellationTokenSource();
            string capturedOut;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                Task<int>? loopTask = null;
                Console.SetOut(stdout);
                try
                {
                    loopTask = IndexWatchRunner.RunCoreAsync(options, _jsonOptions, projectRoot, dbPath, cts.Token);
                    cts.Cancel();
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                    exitCode = loopTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    CancelAndDrainWatchLoop(cts, loopTask);
                    Console.SetOut(originalOut);
                }
                capturedOut = stdout.ToString();
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);

            // Verify at least the "watching" and "stopped" lifecycle JSON lines were emitted.
            // 起動と停止のライフサイクル JSON が出力されていることを検証する。
            var statuses = capturedOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ExtractStatus)
                .Where(s => s is not null)
                .ToList();
            Assert.Contains("watching", statuses);
            Assert.Contains("stopped", statuses);

            var watchingLine = capturedOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .First(line => line.Contains("\"status\":\"watching\"", StringComparison.Ordinal));
            Assert.DoesNotContain(projectRoot, watchingLine);
            Assert.DoesNotContain(dbPath, watchingLine);
            using var watchStarted = JsonDocument.Parse(watchingLine);
            Assert.Equal(JsonOutputContract.ApiVersion, watchStarted.RootElement.GetProperty("api_version").GetString());
            Assert.Equal("[redacted]", watchStarted.RootElement.GetProperty("project_root").GetString());
            Assert.Equal("[redacted]", watchStarted.RootElement.GetProperty("db").GetString());
            Assert.Equal(50, watchStarted.RootElement.GetProperty("debounce_ms").GetInt32());
            Assert.Equal(123, watchStarted.RootElement.GetProperty("watch_pending_path_limit").GetInt32());
            var contract = watchStarted.RootElement.GetProperty("watch_contract");
            Assert.Equal("quiet_window", contract.GetProperty("debounce").GetString());
            Assert.Equal(50, contract.GetProperty("debounce_ms").GetInt32());
            Assert.Equal(IndexWatchRunner.MaxDebounceMs, contract.GetProperty("max_debounce_ms").GetInt32());
            Assert.Equal(50, contract.GetProperty("poll_interval_ms").GetInt32());
            Assert.Equal(123, contract.GetProperty("watch_pending_path_limit").GetInt32());
            Assert.Equal("ordinal", contract.GetProperty("path_comparison").GetString());
            Assert.Equal("distinct_paths_refresh_debounce", contract.GetProperty("change_coalescing").GetString());
            Assert.Equal("old_and_new_paths", contract.GetProperty("rename_events").GetString());
            Assert.Equal("full_rescan_after_debounce", contract.GetProperty("overflow_recovery").GetString());
            Assert.Equal("full_rescan_after_debounce", contract.GetProperty("watcher_error_recovery").GetString());
            Assert.Equal("cancel_active_sub_run_then_emit_stopped", contract.GetProperty("cancellation").GetString());
            Assert.Equal("json_quiet_sub_runs", contract.GetProperty("sub_run_output").GetString());
            Assert.Equal("unsupported", contract.GetProperty("mcp_watch_mode").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task IndexCommandRun_WatchIdle_PropagatesTopLevelCancellation_Issue4591()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? runTask = null;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "idle.py"), "print('idle')\n");
            IndexWatchRunner.WatchReadyForTesting = _ =>
            {
                ready.TrySetResult();
                cts.Cancel();
            };

            runTask = StartTopLevelWatch(
                [projectRoot, "--watch", "--quiet", "--db", dbPath],
                cts);

            await WaitForWatchSignalAsync(ready.Task, runTask, "top-level watch readiness");
            var exitCode = await runTask.WaitAsync(TopLevelWatchTimeout);
            Assert.Equal(CommandExitCodes.Success, exitCode);
        }
        finally
        {
            cts.Cancel();
            IndexWatchRunner.WatchReadyForTesting = null;
            await WaitForTopLevelWatchCleanupAsync(runTask);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task IndexCommandRun_WatchActiveUpdate_PropagatesTopLevelCancellation_Issue4591()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var sourcePath = Path.Combine(projectRoot, "active.py");
        using var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? runTask = null;
        try
        {
            File.WriteAllText(sourcePath, "print('before')\n");
            IndexWatchRunner.WatchReadyForTesting = enqueue =>
            {
                File.WriteAllText(sourcePath, "print('after')\n");
                enqueue(sourcePath);
                ready.TrySetResult();
            };
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = () =>
            {
                extractionStarted.TrySetResult();
                cts.Token.WaitHandle.WaitOne();
                cts.Token.ThrowIfCancellationRequested();
            };

            runTask = StartTopLevelWatch(
                [projectRoot, "--watch", "--quiet", "--debounce", "50", "--db", dbPath],
                cts);

            await WaitForWatchSignalAsync(ready.Task, runTask, "top-level watch readiness");
            await WaitForWatchSignalAsync(extractionStarted.Task, runTask, "watch update extraction start");
            cts.Cancel();
            var exitCode = await runTask.WaitAsync(TopLevelWatchTimeout);
            Assert.Equal(CommandExitCodes.Interrupted, exitCode);
        }
        finally
        {
            cts.Cancel();
            IndexWatchRunner.WatchReadyForTesting = null;
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = null;
            await WaitForTopLevelWatchCleanupAsync(runTask);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task WaitForWatchSignalAsync_DistinguishesEarlyExitAndObservationTimeout_Issue4752()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var earlyExit = Task.FromResult(42);

        var exitFailure = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
            WaitForWatchSignalAsync(
                signal.Task,
                earlyExit,
                "test readiness",
                TimeSpan.FromSeconds(1)));

        Assert.Contains("exited before test readiness", exitFailure.Message, StringComparison.Ordinal);
        Assert.Contains("Exit code: 42", exitFailure.Message, StringComparison.Ordinal);

        var pendingRun = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutFailure = await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() =>
            WaitForWatchSignalAsync(
                signal.Task,
                pendingRun.Task,
                "test readiness",
                TimeSpan.FromMilliseconds(10)));

        Assert.Contains("Timed out waiting for test readiness", timeoutFailure.Message, StringComparison.Ordinal);
        Assert.Contains("the watch run is still", timeoutFailure.Message, StringComparison.Ordinal);
        pendingRun.TrySetResult(CommandExitCodes.Success);
    }

    [Fact]
    public async Task IndexCommandRun_WatchSuccessfulStartup_PerformsExactlyOneBaseline_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var baselineScans = 0;
        Task<int>? runTask = null;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "baseline.py"), "print('baseline')\n");
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () => Interlocked.Increment(ref baselineScans);
            IndexWatchRunner.WatchReadyForTesting = _ =>
            {
                ready.TrySetResult();
                cts.Cancel();
            };

            runTask = StartTopLevelWatch(
                [projectRoot, "--watch", "--quiet", "--db", dbPath],
                cts);

            await WaitForWatchSignalAsync(ready.Task, runTask, "single-baseline watch readiness");
            var exitCode = await runTask.WaitAsync(TopLevelWatchTimeout);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
        }
        finally
        {
            cts.Cancel();
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            await WaitForTopLevelWatchCleanupAsync(runTask);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_BackendStartFallback_ReusesSingleBaseline_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var firstBackend = new FakeWatchBackend(
            "fsevents",
            new IOException("simulated EventStream start failure"));
        var fallbackBackend = new FakeWatchBackend(
            "polling",
            onStart: () => firstBackend.ReportError(
                new IOException("stale error from disposed EventStream backend")));
        var backends = new Queue<IndexWatchRunner.IWatchBackend>([firstBackend, fallbackBackend]);
        var baselineScans = 0;
        var recoveryScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backends.Dequeue();
            IndexWatchRunner.WatchReadyForTesting = _ => cts.Cancel();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    return CommandExitCodes.Success;
                },
                recoveryScan: _ =>
                {
                    recoveryScans++;
                    return CommandExitCodes.Success;
                },
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
            Assert.Equal(0, recoveryScans);
            Assert.Equal(1, firstBackend.StartCount);
            Assert.Equal(1, fallbackBackend.StartCount);
            Assert.True(firstBackend.Disposed);
            Assert.True(fallbackBackend.Disposed);
            var fallbackEvent = FindWatchEvent(capturedOut, "backend_fallback");
            Assert.Equal("startup", fallbackEvent.GetProperty("phase").GetString());
            Assert.Equal("fsevents", fallbackEvent.GetProperty("backend").GetString());
            Assert.Equal("backend_start_failed", fallbackEvent.GetProperty("recovery_reason").GetString());
            var watchingEvent = FindWatchEvent(capturedOut, "watching");
            Assert.Equal("initial_scan", watchingEvent.GetProperty("phase").GetString());
            Assert.Equal("polling", watchingEvent.GetProperty("backend").GetString());
            Assert.Equal("backend_start_failed", watchingEvent.GetProperty("recovery_reason").GetString());
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_AsynchronousStartupError_FallsBackBeforeBaseline_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var firstBackend = new DeferredStartupErrorWatchBackend("fsevents");
        var fallbackBackend = new FakeWatchBackend("polling");
        var backends = new Queue<IndexWatchRunner.IWatchBackend>([firstBackend, fallbackBackend]);
        var baselineScans = 0;
        var recoveryScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backends.Dequeue();
            IndexWatchRunner.WatchReadyForTesting = _ => cts.Cancel();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    return CommandExitCodes.Success;
                },
                recoveryScan: _ =>
                {
                    recoveryScans++;
                    return CommandExitCodes.Success;
                },
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
            Assert.Equal(0, recoveryScans);
            Assert.Equal("polling", FindWatchEvent(capturedOut, "watching").GetProperty("backend").GetString());
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_EventLossDuringBaseline_PerformsOneJustifiedRecovery_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var backend = new FakeWatchBackend("fsevents");
        var baselineScans = 0;
        var recoveryScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backend;
            IndexWatchRunner.WatchReadyForTesting = _ => cts.Cancel();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    backend.ReportError(new InternalBufferOverflowException("simulated event loss"));
                    backend.ReportError(new InternalBufferOverflowException("duplicate simulated event loss"));
                    return CommandExitCodes.Success;
                },
                recoveryScan: phase =>
                {
                    Assert.Equal("startup", phase);
                    recoveryScans++;
                    return CommandExitCodes.Success;
                },
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
            Assert.Equal(1, recoveryScans);
            var overflowEvent = FindWatchEvent(capturedOut, "overflow");
            Assert.Equal("startup", overflowEvent.GetProperty("phase").GetString());
            Assert.Equal("fsevents", overflowEvent.GetProperty("backend").GetString());
            Assert.Equal("event_stream_overflow", overflowEvent.GetProperty("recovery_reason").GetString());
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_BackendFailureDuringBaseline_PreservesBaselineAndRecoversOnce_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var firstBackend = new FakeWatchBackend("fsevents");
        var fallbackBackend = new FakeWatchBackend("polling");
        var backends = new Queue<IndexWatchRunner.IWatchBackend>([firstBackend, fallbackBackend]);
        var baselineScans = 0;
        var recoveryScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backends.Dequeue();
            IndexWatchRunner.WatchReadyForTesting = _ => cts.Cancel();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    firstBackend.ReportError(new IOException("late EventStream startup failure"));
                    return CommandExitCodes.Success;
                },
                recoveryScan: phase =>
                {
                    Assert.Equal("startup", phase);
                    recoveryScans++;
                    return CommandExitCodes.Success;
                },
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
            Assert.Equal(1, recoveryScans);
            Assert.Equal("polling", FindWatchEvent(capturedOut, "watching").GetProperty("backend").GetString());
            Assert.Equal(
                "backend_start_failed",
                FindWatchEvent(capturedOut, "overflow").GetProperty("recovery_reason").GetString());
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_LateFatalBackendError_ReplacesBackendAndRecoversOnce_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var firstBackend = new FakeWatchBackend("fsevents");
        var fallbackBackend = new FakeWatchBackend("polling");
        var backends = new Queue<IndexWatchRunner.IWatchBackend>([firstBackend, fallbackBackend]);
        var baselineScans = 0;
        var recoveryScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backends.Dequeue();
            IndexWatchRunner.WatchReadyForTesting = _ =>
                firstBackend.ReportError(new IOException("late fatal EventStream failure"));

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    return CommandExitCodes.Success;
                },
                recoveryScan: phase =>
                {
                    Assert.Equal("incremental", phase);
                    recoveryScans++;
                    cts.Cancel();
                    return CommandExitCodes.Success;
                },
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
            Assert.Equal(1, recoveryScans);
            Assert.True(firstBackend.Disposed);
            Assert.True(fallbackBackend.Disposed);
            var fallbackEvent = FindWatchEvent(capturedOut, "backend_fallback");
            Assert.Equal("incremental", fallbackEvent.GetProperty("phase").GetString());
            Assert.Equal("fsevents", fallbackEvent.GetProperty("backend").GetString());
            Assert.Equal("backend_error", fallbackEvent.GetProperty("recovery_reason").GetString());
            var overflowEvent = FindWatchEvent(capturedOut, "overflow");
            Assert.Equal("incremental", overflowEvent.GetProperty("phase").GetString());
            Assert.Equal("polling", overflowEvent.GetProperty("backend").GetString());
            Assert.Equal("backend_error", overflowEvent.GetProperty("recovery_reason").GetString());
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_PollingFallbackCancellation_EmitsStoppedWithoutBaseline_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var firstBackend = new FakeWatchBackend(
            "fsevents",
            new IOException("simulated EventStream start failure"));
        var pollingBackend = new CancelingWatchBackend("polling", cts);
        var backends = new Queue<IndexWatchRunner.IWatchBackend>([firstBackend, pollingBackend]);
        var baselineScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backends.Dequeue();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    return CommandExitCodes.Success;
                },
                recoveryScan: _ => CommandExitCodes.Success,
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, baselineScans);
            Assert.True(firstBackend.Disposed);
            Assert.True(pollingBackend.Disposed);
            Assert.Equal(
                "backend_start_failed",
                FindWatchEvent(capturedOut, "backend_fallback")
                    .GetProperty("recovery_reason")
                    .GetString());
            Assert.Equal("stopped", FindWatchEvent(capturedOut, "stopped").GetProperty("status").GetString());
            Assert.DoesNotContain("\"status\":\"watching\"", capturedOut, StringComparison.Ordinal);
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PollingSnapshot_PrunesIgnoredAndInternalTrees_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
            Directory.CreateDirectory(Path.Combine(projectRoot, ".cdidx"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "ignored", "deep"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "bin", "deep"));
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "ignored/\nbin/\n");
            File.WriteAllText(Path.Combine(projectRoot, ".git", "config"), "[core]\n");
            File.WriteAllText(dbPath, "not a database");
            File.WriteAllText(Path.Combine(projectRoot, "ignored", "deep", "hidden.cs"), "class Hidden {}\n");
            File.WriteAllText(Path.Combine(projectRoot, "bin", "deep", "generated.cs"), "class Generated {}\n");
            File.WriteAllText(Path.Combine(projectRoot, "visible.cs"), "class Visible {}\n");

            var relativePaths = IndexWatchRunner.CapturePollingSnapshotPathsForTesting(
                    projectRoot,
                    projectRoot,
                    dbPath,
                    ignoreCase: false,
                    dbPathExplicit: true)
                .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
                .ToArray();

            Assert.Contains("visible.cs", relativePaths);
            Assert.Contains(".gitignore", relativePaths);
            Assert.DoesNotContain(relativePaths, path => path.StartsWith(".git/", StringComparison.Ordinal));
            Assert.DoesNotContain(relativePaths, path => path.StartsWith(".cdidx/", StringComparison.Ordinal));
            Assert.DoesNotContain(relativePaths, path => path.StartsWith("ignored/", StringComparison.Ordinal));
            Assert.DoesNotContain(relativePaths, path => path.StartsWith("bin/", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(true, 8, 0, true, true)]
    [InlineData(true, 8, 0, false, false)]
    [InlineData(true, 9, 0, true, false)]
    [InlineData(true, 8, 1, true, false)]
    [InlineData(false, 8, 0, true, false)]
    public void ShouldPollAncestorIgnorePaths_Net8MacSubprojectAvoidsMissedEvents_Issue4955(
        bool isMacOs,
        int runtimeMajorVersion,
        int attempt,
        bool hasAncestorIgnorePaths,
        bool expected)
    {
        Assert.Equal(
            expected,
            IndexWatchRunner.ShouldPollAncestorIgnorePathsForTesting(
                isMacOs,
                runtimeMajorVersion,
                attempt,
                hasAncestorIgnorePaths));
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 1, false)]
    public void ShouldUseFullPollingWatchBackend_OnlyForMacFallbackAttempts_Issue4955(
        bool isMacOs,
        int attempt,
        bool expected)
    {
        Assert.Equal(
            expected,
            IndexWatchRunner.ShouldUseFullPollingWatchBackendForTesting(isMacOs, attempt));
    }

    [Fact]
    public void AncestorIgnorePollingPaths_AreBoundedToExactAncestorFiles_Issue4955()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "parent", "subproject");
        try
        {
            Directory.CreateDirectory(projectRoot);

            var relativePaths = IndexWatchRunner.CaptureAncestorIgnorePollingPathsForTesting(
                    projectRoot,
                    repoRoot,
                    ignoreCase: false)
                .Select(path => Path.GetRelativePath(repoRoot, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    ".cdidxignore",
                    ".gitignore",
                    "parent/.cdidxignore",
                    "parent/.gitignore",
                ],
                relativePaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void RunCore_BackendStartFailureAfterFallback_StopsBeforeBaseline_Issue4858()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var backends = new Queue<IndexWatchRunner.IWatchBackend>(
        [
            new FakeWatchBackend("fsevents", new IOException("first simulated start failure")),
            new FakeWatchBackend("polling", new IOException("second simulated start failure")),
        ]);
        var baselineScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backends.Dequeue();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    return CommandExitCodes.Success;
                },
                recoveryScan: _ => CommandExitCodes.Success,
                out var exitCode);

            Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
            Assert.Equal(0, baselineScans);
            var failureEvent = FindWatchEvent(capturedOut, "failed");
            Assert.Equal("startup", failureEvent.GetProperty("phase").GetString());
            Assert.Equal("polling", failureEvent.GetProperty("backend").GetString());
            Assert.Equal("backend_start_failed", failureEvent.GetProperty("recovery_reason").GetString());
            Assert.DoesNotContain("\"status\":\"watching\"", capturedOut, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"stopped\"", capturedOut, StringComparison.Ordinal);
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_MacOsBackend_StartsBeforeSingleBaseline_Issue4858()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        var baselineScans = 0;
        var recoveryScans = 0;
        try
        {
            var options = CreateIssue4858WatchOptions(projectRoot, dbPath);
            IndexWatchRunner.WatchReadyForTesting = _ => cts.Cancel();

            var capturedOut = RunWatchCoreAndCapture(
                options,
                projectRoot,
                dbPath,
                cts,
                baselineScan: () =>
                {
                    baselineScans++;
                    return CommandExitCodes.Success;
                },
                recoveryScan: _ =>
                {
                    recoveryScans++;
                    return CommandExitCodes.Success;
                },
                out var exitCode);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, baselineScans);
            Assert.InRange(recoveryScans, 0, 1);
            Assert.Contains("\"status\":\"watching\"", capturedOut, StringComparison.Ordinal);
            var watchingEvent = FindWatchEvent(capturedOut, "watching");
            Assert.Contains(
                watchingEvent.GetProperty("backend").GetString(),
                ["fsevents", "polling"]);
            if (watchingEvent.GetProperty("backend").GetString() == "polling")
                Assert.Contains("\"status\":\"backend_fallback\"", capturedOut, StringComparison.Ordinal);
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_StartupHandoff_ReconcilesMutationAndDrainsBeforeReady_Issue4594()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            TestProjectHelper.RunGit(projectRoot, "init");
            TestProjectHelper.RunGit(projectRoot, "config", "core.ignorecase", "false");
            var sourcePath = Path.Combine(projectRoot, "Handoff.cs");
            File.WriteAllText(sourcePath, "public sealed class BeforeHandoff { }\n");

            var initialJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var initialExitCode);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("\"status\":\"success\"", initialJson, StringComparison.Ordinal);
            Assert.True(HasIndexedSymbol(dbPath, "BeforeHandoff"));

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Watch = true,
                WatchDebounceMs = 50,
            };

            using var cts = new CancellationTokenSource();
            var handoffInvoked = false;
            string capturedOut;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                Task<int>? loopTask = null;
                Console.SetOut(stdout);
                try
                {
                    loopTask = IndexWatchRunner.RunCoreAsync(
                        options,
                        _jsonOptions,
                        projectRoot,
                        dbPath,
                        cts.Token,
                        enqueue =>
                        {
                            handoffInvoked = true;
                            File.WriteAllText(sourcePath, "public sealed class AfterHandoff { }\n");
                            enqueue(sourcePath);
                        });
                    cts.Cancel();
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                    exitCode = loopTask.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    CancelAndDrainWatchLoop(cts, loopTask);
                    Console.SetOut(originalOut);
                }
                capturedOut = stdout.ToString();
            }

            Assert.True(handoffInvoked);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(HasIndexedSymbol(dbPath, "AfterHandoff"));
            Assert.False(HasIndexedSymbol(dbPath, "BeforeHandoff"));

            var startupRescan = capturedOut.IndexOf("\"status\":\"rescanned\",\"phase\":\"startup\"", StringComparison.Ordinal);
            var startupDrain = capturedOut.IndexOf("\"status\":\"updated\",\"phase\":\"startup\"", StringComparison.Ordinal);
            var ready = capturedOut.IndexOf("\"status\":\"watching\"", StringComparison.Ordinal);
            Assert.True(startupRescan >= 0, capturedOut);
            Assert.True(startupDrain > startupRescan, capturedOut);
            Assert.True(ready > startupDrain, capturedOut);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_StartupReconciliationFailureDoesNotEmitWatching_Issue4594()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('startup failure')\n");
            var initialJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var initialExitCode);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("\"status\":\"success\"", initialJson, StringComparison.Ordinal);

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Watch = true,
                WatchDebounceMs = 50,
            };
            using var heldLock = IndexLock.Acquire(IndexLock.GetLockPath(dbPath), projectRoot);
            string capturedOut;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                Console.SetOut(stdout);
                try
                {
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                    exitCode = IndexWatchRunner.RunCoreAsync(
                            options,
                            _jsonOptions,
                            projectRoot,
                            dbPath,
                            CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(10))
                        .GetAwaiter()
                        .GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
                capturedOut = stdout.ToString();
            }

            Assert.NotEqual(CommandExitCodes.Success, exitCode);
            Assert.Contains("\"status\":\"failed\"", capturedOut, StringComparison.Ordinal);
            Assert.DoesNotContain("\"status\":\"watching\"", capturedOut, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"stopped\"", capturedOut, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_JsonLifecycleEventsHonorIndentedJsonOptions()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var backend = new FakeWatchBackend("fsevents");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "hello.py"), "print('hi')\n");
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backend;
            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Watch = true,
                WatchDebounceMs = 50,
            };
            var indentedOptions = new JsonSerializerOptions(_jsonOptions)
            {
                WriteIndented = true,
            };

            using var cts = new CancellationTokenSource();
            string capturedOut;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                Task<int>? loopTask = null;
                Console.SetOut(stdout);
                try
                {
                    loopTask = IndexWatchRunner.RunCoreAsync(options, indentedOptions, projectRoot, dbPath, cts.Token);
                    cts.Cancel();
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                    exitCode = loopTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    CancelAndDrainWatchLoop(cts, loopTask);
                    Console.SetOut(originalOut);
                }
                capturedOut = stdout.ToString();
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var normalized = capturedOut.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains("{\n  \"api_version\": \"" + JsonOutputContract.ApiVersion + "\"", normalized, StringComparison.Ordinal);
            Assert.Contains("\n  \"status\": \"watching\"", normalized, StringComparison.Ordinal);
            Assert.Contains("\n  \"status\": \"stopped\"", normalized, StringComparison.Ordinal);
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_EmitsHumanFriendlyStartStop_WhenJsonDisabled()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var backend = new FakeWatchBackend("fsevents");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "hello.py"), "print('hi')\n");
            IndexWatchRunner.WatchBackendFactoryForTesting = (_, _, _) => backend;
            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = false,
                Watch = true,
                WatchDebounceMs = 50,
                WatchPendingPathLimit = 123,
            };

            using var cts = new CancellationTokenSource();
            string capturedErr;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalErr = Console.Error;
                var originalOut = Console.Out;
                using var stderr = new StringWriter();
                using var stdout = new StringWriter();
                Task<int>? loopTask = null;
                Console.SetError(stderr);
                Console.SetOut(stdout);
                try
                {
                    loopTask = IndexWatchRunner.RunCoreAsync(options, _jsonOptions, projectRoot, dbPath, cts.Token);
                    cts.Cancel();
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                    exitCode = loopTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    CancelAndDrainWatchLoop(cts, loopTask);
                    Console.SetError(originalErr);
                    Console.SetOut(originalOut);
                }
                capturedErr = stderr.ToString();
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("[watch] Watching", capturedErr);
            Assert.Contains("backend ", capturedErr);
            Assert.Contains("recovery none", capturedErr);
            Assert.Contains("debounce 50 ms", capturedErr);
            Assert.Contains("pending path limit 123", capturedErr);
            Assert.Contains("[watch] Stopped.", capturedErr);
        }
        finally
        {
            IndexWatchRunner.WatchBackendFactoryForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunCore_SubprojectObservesAncestorIgnoreFileChanges_Issue4592()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        using var ready = new ManualResetEventSlim();
        Action<string>? enqueue = null;
        Task<int>? loopTask = null;
        try
        {
            TestProjectHelper.RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('initially indexed')\n");
            var initialJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var initialExitCode);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("\"status\":\"success\"", initialJson, StringComparison.Ordinal);
            Assert.True(HasIndexedFile(dbPath, "ignored.py"));

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Quiet = true,
                Watch = true,
                WatchDebounceMs = 50,
            };
            IndexWatchRunner.WatchReadyForTesting = callback =>
            {
                enqueue = callback;
                ready.Set();
            };
            loopTask = IndexWatchRunner.RunCoreAsync(options, _jsonOptions, projectRoot, dbPath, cts.Token);

            Assert.True(ready.Wait(TimeSpan.FromSeconds(15)), "The subproject watcher did not become ready.");
            var ignorePath = Path.Combine(repoRoot, ".gitignore");
            File.WriteAllText(ignorePath, "subproj/ignored.py\n");
            if (OperatingSystem.IsMacOS() && Environment.Version.Major >= 9)
            {
                // .NET 9 keeps FSEvents selection, but host delivery is not a deterministic test oracle on macOS.
                // .NET 9 は FSEvents 選択を維持するが、macOS の host 配信は決定的な test oracle ではない。
                var enqueueCallback = Assert.IsType<Action<string>>(enqueue);
                enqueueCallback(ignorePath);
            }
            Assert.True(
                SpinWait.SpinUntil(
                    () =>
                    {
                        try { return !HasIndexedFile(dbPath, "ignored.py"); }
                        catch (SqliteException) { return false; }
                    },
                    TimeSpan.FromSeconds(15)),
                "The ancestor .gitignore change was not reconciled by the subproject watcher.");
        }
        finally
        {
            cts.Cancel();
            if (loopTask is { IsCompleted: false })
                SpinWait.SpinUntil(() => loopTask.IsCompleted, TimeSpan.FromSeconds(10));
            IndexWatchRunner.WatchReadyForTesting = null;
            DeleteDirectory(repoRoot);
        }
    }

    private static void AssertOptionValue(IReadOnlyList<string?> args, string option, string expectedValue)
    {
        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], option, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0, $"Expected option {option} in recovery command.");
        Assert.True(index + 1 < args.Count, $"Expected value after option {option}.");
        Assert.Equal(expectedValue, args[index + 1]);
    }

    private static string? ExtractStatus(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var s))
                return s.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string InvokeFormatHumanSummary(
        string status,
        int? batchSize,
        long elapsedMs,
        string subRunJson,
        int exitCode)
    {
        var method = typeof(IndexWatchRunner).GetMethod("FormatHumanSummary", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method.Invoke(null, [status, batchSize, elapsedMs, subRunJson, exitCode]));
    }

    private static void CancelAndDrainWatchLoop(CancellationTokenSource cts, Task<int>? loopTask)
    {
        cts.Cancel();
        if (loopTask is { IsCompleted: false })
            SpinWait.SpinUntil(() => loopTask.IsCompleted, TimeSpan.FromSeconds(10));
    }

    private Task<int> StartTopLevelWatch(string[] args, CancellationTokenSource cts)
        => Task.Factory.StartNew(
            () => IndexCommandRunner.Run(args, _jsonOptions, cts),
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private static async Task WaitForWatchSignalAsync(
        Task signalTask,
        Task<int> runTask,
        string signalDescription,
        TimeSpan? timeout = null)
    {
        var timeoutTask = Task.Delay(timeout ?? TopLevelWatchTimeout);
        await Task.WhenAny(signalTask, runTask, timeoutTask);

        if (signalTask.IsCompleted)
        {
            await signalTask;
            return;
        }

        if (runTask.IsCompleted)
        {
            if (runTask.IsFaulted)
            {
                var failure = runTask.Exception?.GetBaseException();
                Assert.Fail(
                    $"The watch run failed before {signalDescription}. "
                    + $"Failure: {failure?.GetType().Name ?? "unknown"}.");
            }

            if (runTask.IsCanceled)
                Assert.Fail($"The watch run was canceled before {signalDescription}.");

            Assert.Fail(
                $"The watch run exited before {signalDescription}. "
                + $"Exit code: {runTask.Result}.");
        }

        Assert.Fail(
            $"Timed out waiting for {signalDescription}; "
            + $"the watch run is still {runTask.Status} and the signal is {signalTask.Status}.");
    }

    private static async Task WaitForTopLevelWatchCleanupAsync(Task<int>? runTask)
    {
        if (runTask is null)
            return;

        await Task.WhenAny(runTask, Task.Delay(TopLevelWatchTimeout));
        if (!runTask.IsCompleted)
        {
            Assert.Fail(
                $"Timed out stopping the top-level watch run during cleanup. "
                + $"Task status: {runTask.Status}.");
        }

        _ = runTask.Exception;
    }

    private string RunIndexAndCapture(string[] args, out int exitCode)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            Console.SetOut(stdout);
            try
            {
                exitCode = IndexCommandRunner.Run(args, _jsonOptions);
                return stdout.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private static bool HasIndexedFile(string dbPath, string filePath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", filePath);
        return cmd.ExecuteScalar() != null;
    }

    private static bool HasIndexedSymbol(string dbPath, string symbolName)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.TryMigrateForRead();
        var reader = new DbReader(db.Connection, db.IsReadOnly);
        return reader.SearchSymbols(symbolName, limit: 1, exact: true).Count == 1;
    }

    private static string CreateTempProject()
        => TestProjectHelper.CreateTempProject("cdidx_watch_runner");

    private static IndexCommandOptions CreateIssue4858WatchOptions(string projectRoot, string dbPath)
        => new()
        {
            ProjectPath = projectRoot,
            DbPath = dbPath,
            Json = true,
            Quiet = true,
            Watch = true,
            WatchDebounceMs = 0,
        };

    private string RunWatchCoreAndCapture(
        IndexCommandOptions options,
        string projectRoot,
        string dbPath,
        CancellationTokenSource cts,
        Func<int> baselineScan,
        Func<string, int> recoveryScan,
        out int exitCode)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            Task<int>? loopTask = null;
            Console.SetOut(stdout);
            try
            {
                loopTask = IndexWatchRunner.RunCoreAsync(
                    options,
                    _jsonOptions,
                    projectRoot,
                    dbPath,
                    cts.Token,
                    baselineScan: baselineScan,
                    recoveryScan: recoveryScan);
#pragma warning disable xUnit1031 // Console redirection lock requires synchronous bounded drain.
                exitCode = loopTask.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                return stdout.ToString();
            }
            finally
            {
                CancelAndDrainWatchLoop(cts, loopTask);
                Console.SetOut(originalOut);
            }
        }
    }

    private static JsonElement FindWatchEvent(string output, string status)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("status", out var statusElement)
                && string.Equals(statusElement.GetString(), status, StringComparison.Ordinal))
            {
                return document.RootElement.Clone();
            }
        }

        Assert.Fail($"Watch event '{status}' was not found. Output: {output}");
        return default;
    }

    private static void DeleteDirectory(string path)
        => TestProjectHelper.DeleteDirectory(path);

    private static void AssertPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
        Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must be non-negative.");

            utcNow += elapsed;
            timestamp += elapsed.Ticks;
        }

        internal void AdjustUtc(TimeSpan offset)
        {
            utcNow += offset;
        }
    }

    private sealed class SignalingStringWriter : StringWriter
    {
        private readonly Func<string, bool> _predicate;
        private readonly ManualResetEventSlim _signal = new();

        internal SignalingStringWriter(Func<string, bool> predicate)
        {
            _predicate = predicate;
        }

        internal bool WaitForSignal(TimeSpan timeout)
            => _signal.Wait(timeout);

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value is not null && _predicate(value))
                _signal.Set();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _signal.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeWatchBackend : IndexWatchRunner.IWatchBackend
    {
        private readonly Exception? _startException;
        private readonly Action? _onStart;
        private Action<Exception?>? _reportError;

        internal FakeWatchBackend(
            string name,
            Exception? startException = null,
            Action? onStart = null)
        {
            Name = name;
            _startException = startException;
            _onStart = onStart;
        }

        public string Name { get; }

        internal int StartCount { get; private set; }

        internal bool Disposed { get; private set; }

        public Task StartAsync(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken)
        {
            StartCount++;
            _reportError = reportError;
            _onStart?.Invoke();
            if (_startException != null)
                return Task.FromException(_startException);
            return Task.CompletedTask;
        }

        internal void ReportError(Exception exception)
        {
            Assert.NotNull(_reportError);
            _reportError(exception);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class DeferredStartupErrorWatchBackend(string name) : IndexWatchRunner.IWatchBackend
    {
        public string Name { get; } = name;

        public async Task StartAsync(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            reportError(new IOException("asynchronous simulated EventStream start failure"));
        }

        public void Dispose()
        {
        }
    }

    private sealed class CancelingWatchBackend(
        string name,
        CancellationTokenSource cancellation) : IndexWatchRunner.IWatchBackend
    {
        public string Name { get; } = name;

        internal bool Disposed { get; private set; }

        public Task StartAsync(
            Action<string> enqueue,
            Action<Exception?> reportError,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
