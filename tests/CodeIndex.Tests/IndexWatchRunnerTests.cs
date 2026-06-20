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
    public void FileChangeBatcher_RequestFullRescan_SanitizesAndBoundsReason_Issue3804()
    {
        var clock = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(100), () => clock);
        var rawReason = "watch failed for /Users/alice/private/project/secret.txt token=ghp_"
            + new string('a', 40)
            + " "
            + new string('x', IndexWatchRunner.MaxWatchDiagnosticChars * 2);

        batcher.RequestFullRescan(rawReason);

        clock = clock.AddMilliseconds(200);
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
                        [options, _jsonOptions, args, Stopwatch.StartNew(), "updated", 3, "incremental", Array.Empty<string>()]));
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
            SymlinkPolicy = FileIndexer.SymlinkPolicy.All,
            SymbolKindFilter = SymbolKindFilter.Create(["class", "function"], ["test.method"], parseError: null),
        };
        var method = typeof(IndexWatchRunner).GetMethod("EmitWatchOverflow", BindingFlags.NonPublic | BindingFlags.Static);
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
                method.Invoke(null, [options, "buffer full", resolvedDbPath]);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            capturedOut = stdout.ToString();
        }

        using var doc = JsonDocument.Parse(capturedOut);
        Assert.Equal("overflow", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("incremental", doc.RootElement.GetProperty("phase").GetString());
        Assert.Equal("buffer full", doc.RootElement.GetProperty("overflow_reason").GetString());
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
        var nestedStart = string.Concat(Enumerable.Repeat("[", depth));
        var nestedEnd = string.Concat(Enumerable.Repeat("]", depth));
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
                using var stdout = new SignalingStringWriter(
                    line => line.Contains("\"status\":\"watching\"", StringComparison.Ordinal));
                Task<int>? loopTask = null;
                Console.SetOut(stdout);
                try
                {
                    loopTask = StartWatchLoop(options, projectRoot, dbPath, cts.Token);
                    var started = stdout.WaitForSignal(TimeSpan.FromSeconds(10));
                    cts.Cancel();
                    Assert.True(started,
                        "Watch loop did not emit the watching event before cancellation / 取り消し前に watching イベントが出力されなかった");
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
                using var stderr = new SignalingStringWriter(
                    line => line.Contains("[watch] Watching", StringComparison.Ordinal));
                using var stdout = new StringWriter();
                Task<int>? loopTask = null;
                Console.SetError(stderr);
                Console.SetOut(stdout);
                try
                {
                    loopTask = StartWatchLoop(options, projectRoot, dbPath, cts.Token);
                    var started = stderr.WaitForSignal(TimeSpan.FromSeconds(10));
                    cts.Cancel();
                    Assert.True(started,
                        "Watch loop did not emit the human start line before cancellation / 取り消し前に human start 行が出力されなかった");
#pragma warning disable xUnit1031
                    Assert.True(loopTask.Wait(TimeSpan.FromSeconds(10)));
                    exitCode = loopTask.Result;
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
}
