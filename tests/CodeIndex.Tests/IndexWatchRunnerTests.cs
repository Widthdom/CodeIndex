using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for <see cref="IndexWatchRunner"/> (`cdidx index --watch`).
/// `cdidx index --watch` のテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class IndexWatchRunnerTests
{
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
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), () => now);
        batcher.Add("/repo/a.py");
        // Less than the debounce window has elapsed.
        Assert.False(batcher.TryDrain(out var batch, out var rescan, out _));
        Assert.Empty(batch);
        Assert.False(rescan);
    }

    [Fact]
    public void FileChangeBatcher_TryDrain_AfterDebounceElapsed_ReturnsBatchOnce()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime Clock() => clock;
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), Clock);

        batcher.Add("/repo/a.py");
        batcher.Add("/repo/b.py");
        // Coalesce duplicates regardless of casing on case-insensitive filesystems.
        batcher.Add("/repo/A.py");

        clock = clock.AddMilliseconds(600);
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out _));
        Assert.False(rescan);
        Assert.Equal(2, batch.Count);

        // Subsequent drain without new events returns false.
        Assert.False(batcher.TryDrain(out _, out _, out _));
    }

    [Fact]
    public void FileChangeBatcher_CaseSensitive_KeepsDistinctPaths()
    {
        // On case-sensitive filesystems (Linux ext4), `foo.py` and `Foo.py` are different
        // files; a rename event arrives as Add("foo.py") + Add("Foo.py") and BOTH must be
        // surfaced so the sub-update can purge the old name and index the new one.
        // 大小区別 FS では rename の old/new を別エントリで保持する必要がある。
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime Clock() => clock;
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), Clock, ignoreCase: false);

        batcher.Add("/repo/foo.py");
        batcher.Add("/repo/Foo.py");

        clock = clock.AddMilliseconds(600);
        Assert.True(batcher.TryDrain(out var batch, out _, out _));
        Assert.Equal(2, batch.Count);
        Assert.Contains("/repo/foo.py", batch);
        Assert.Contains("/repo/Foo.py", batch);
    }

    [Fact]
    public void FileChangeBatcher_RequestFullRescan_DrainsOverflowAndReason()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(100), () => clock);
        batcher.Add("/repo/a.py");
        batcher.RequestFullRescan("buffer overflowed");

        clock = clock.AddMilliseconds(200);
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out var reason));
        Assert.True(rescan);
        Assert.Equal("buffer overflowed", reason);
        Assert.Empty(batch);
    }

    [Fact]
    public void FileChangeBatcher_Add_WhenPendingPathLimitExceeded_CollapsesToFullRescan()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batcher = new FileChangeBatcher(
            TimeSpan.FromMilliseconds(100),
            () => clock,
            maxPendingPaths: 2);

        batcher.Add("/repo/a.py");
        batcher.Add("/repo/b.py");
        batcher.Add("/repo/c.py");
        batcher.Add("/repo/d.py");

        clock = clock.AddMilliseconds(200);
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out var reason));
        Assert.True(rescan);
        Assert.Empty(batch);
        Assert.Contains("pending path limit exceeded", reason);
        Assert.Contains("2", reason);
    }

    [Fact]
    public void FileChangeBatcher_NewEventDuringWait_ExtendsDebounce()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), () => clock);
        batcher.Add("/repo/a.py");

        clock = clock.AddMilliseconds(400);
        // A new event before the window closes resets the timer.
        batcher.Add("/repo/b.py");
        Assert.False(batcher.TryDrain(out _, out _, out _));

        clock = clock.AddMilliseconds(400);
        // 400ms after the second event is still < 500ms; not ready yet.
        Assert.False(batcher.TryDrain(out _, out _, out _));

        clock = clock.AddMilliseconds(200);
        // Now > 500ms after the second event.
        Assert.True(batcher.TryDrain(out var batch, out _, out _));
        Assert.Equal(2, batch.Count);
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
        var args = Assert.IsType<List<string>>(method.Invoke(null, [options]));

        Assert.Contains("--json", args);
        Assert.Contains("--quiet", args);
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
        var args = Assert.IsType<List<string>>(method.Invoke(null, [options]));

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
        var args = Assert.IsType<List<string>>(method.Invoke(null, [options]));

        var flagIndex = args.IndexOf("--max-symbols-per-file");
        Assert.True(flagIndex >= 0);
        Assert.Equal("42", args[flagIndex + 1]);
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
                        [options, _jsonOptions, args, Stopwatch.StartNew(), "updated", 3]));
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
            Assert.Equal(3, doc.RootElement.GetProperty("batch_size").GetInt32());
            Assert.Equal(exitCode, doc.RootElement.GetProperty("exit_code").GetInt32());
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
                        [options, _jsonOptions, args, Stopwatch.StartNew(), "updated", 3]));
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
    public void RunCore_CancellationToken_StopsImmediately()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "hello.py"), "print('hi')\n");

            // Pre-build the DB so the watcher's "initial scan path" is not exercised here -
            // this test only checks that the watch loop respects cancellation and emits the
            // expected lifecycle events.
            // 初回スキャンは事前に済ませ、watch ループの起動/停止のみ検証する。
            var prebuildJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var prebuildExit);
            Assert.Equal(CommandExitCodes.Success, prebuildExit);

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = true,
                Watch = true,
                WatchDebounceMs = 50,
            };

            using var cts = new CancellationTokenSource();
            string capturedOut;
            int exitCode;

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                Console.SetOut(stdout);
                try
                {
                    var loopTask = StartWatchLoop(options, projectRoot, dbPath, cts.Token);
                    // Give the watcher a moment to emit the "watching" event.
                    Thread.Sleep(500);
                    cts.Cancel();
                    // Blocking wait is intentional: this test verifies the loop terminates within
                    // a wall-clock budget while holding the redirected Console.Out under a lock.
                    // 同期的に待機しているのは、Console.Out リダイレクトを保持したまま停止時間を検証するため。
#pragma warning disable xUnit1031
                    Assert.True(loopTask.Wait(TimeSpan.FromSeconds(10)),
                        "Watch loop did not stop within 10s after cancellation / 取り消し後10秒以内に停止しなかった");
                    exitCode = loopTask.Result;
#pragma warning restore xUnit1031
                }
                finally
                {
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
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void RunCore_EmitsHumanFriendlyStartStop_WhenJsonDisabled()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "hello.py"), "print('hi')\n");
            var prebuildJson = RunIndexAndCapture([projectRoot, "--db", dbPath, "--json"], out var prebuildExit);
            Assert.Equal(CommandExitCodes.Success, prebuildExit);

            var options = new IndexCommandOptions
            {
                ProjectPath = projectRoot,
                DbPath = dbPath,
                Json = false,
                Watch = true,
                WatchDebounceMs = 50,
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
                Console.SetError(stderr);
                Console.SetOut(stdout);
                try
                {
                    var loopTask = StartWatchLoop(options, projectRoot, dbPath, cts.Token);
                    Thread.Sleep(500);
                    cts.Cancel();
#pragma warning disable xUnit1031
                    Assert.True(loopTask.Wait(TimeSpan.FromSeconds(10)));
                    exitCode = loopTask.Result;
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(originalErr);
                    Console.SetOut(originalOut);
                }
                capturedErr = stderr.ToString();
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("[watch] Watching", capturedErr);
            Assert.Contains("debounce 50 ms", capturedErr);
            Assert.Contains("[watch] Stopped.", capturedErr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
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

    private Task<int> StartWatchLoop(
        IndexCommandOptions options,
        string projectRoot,
        string dbPath,
        CancellationToken cancellationToken)
    {
        // Run the watcher on a dedicated thread so this cancellation test does not depend on
        // ThreadPool availability during the full test suite.
        return Task.Factory.StartNew(
            () => IndexWatchRunner.RunCore(options, _jsonOptions, projectRoot, dbPath, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
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

    private static string CreateTempProject()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"cdidx_watch_runner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);
        return projectRoot;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
