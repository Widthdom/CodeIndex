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

/// <summary>
/// Tests for indexing command argument handling.
/// インデックスコマンドの引数処理テスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public partial class IndexCommandRunnerTests
{
    private static readonly TimeSpan SymbolWorkerStartupBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SymbolWorkerRequestBudget = TimeSpan.FromSeconds(5);
    private static readonly object FullScanContentLoadHookGate = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Theory]
    [InlineData("..")]
    [InlineData("../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"..\..\evil.txt")]
    public void IsOutsideProjectRoot_ParentTraversalSeparators_ReturnsTrue(string relativePath)
    {
        if (relativePath.Contains('\\') && !OperatingSystem.IsWindows())
            return;

        Assert.True(IndexCommandRunner.IsOutsideProjectRoot(relativePath));
    }

    [Fact]
    public void IsOutsideProjectRoot_PosixLiteralBackslashPath_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.False(IndexCommandRunner.IsOutsideProjectRoot(@"..\evil.txt"));
    }

    [Fact]
    public void IsOutsideProjectRoot_RootedPath_ReturnsTrue()
    {
        var rootedPath = OperatingSystem.IsWindows()
            ? @"C:\Windows\evil.txt"
            : "/etc/passwd";

        Assert.True(IndexCommandRunner.IsOutsideProjectRoot(rootedPath));
    }

    [Fact]
    public void IsOutsideProjectRoot_NormalRelativePath_ReturnsFalse()
    {
        Assert.False(IndexCommandRunner.IsOutsideProjectRoot("src/app.cs"));
    }

    [Fact]
    public void StopObservedJsonPhaseHeartbeat_CancelsPendingTaskWithoutBlocking_Issue3771()
    {
        var cts = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        IndexCommandRunner.StopObservedJsonPhaseHeartbeat((cts, pending.Task));

        stopwatch.Stop();
        Assert.True(cts.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        pending.SetResult();
    }

    [Fact]
    public void ParseArgs_HelpFlagSetsShowHelp()
    {
        var options = IndexCommandRunner.ParseArgs(["--help"]);

        Assert.True(options.ShowHelp);
        Assert.Null(options.ProjectPath);
    }

    [Fact]
    public void ParseArgs_AllowPartialSetsIndexOptIn_Issue4609()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--allow-partial"]);

        Assert.True(options.AllowPartial);
    }

    [Fact]
    public void Run_UnknownIndexOption_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--verbos"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("unknown option '--verbos'", stderr);
            Assert.Contains("Did you mean: --verbose?", stderr);
            Assert.DoesNotContain("Warning: unknown option", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBackfillFold_UnknownOption_ReturnsUsageError()
    {
        var missingDb = CreateTempDbPath("cdidx_backfill_unknown");

        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = IndexCommandRunner.RunBackfillFold(["--db", missingDb, "--dryrun"], _jsonOptions);

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Equal(string.Empty, stdout.ToString());
                Assert.Contains("unknown option '--dryrun'", stderr.ToString());
                Assert.Contains("Did you mean: --dry-run?", stderr.ToString());
                Assert.DoesNotContain("Warning: unknown option", stderr.ToString());
                Assert.DoesNotContain("database not found", stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    [Fact]
    public void RunBackfillFold_UnknownOptionBeforeJson_ReturnsJsonUsageError()
    {
        var missingDb = CreateTempDbPath("cdidx_backfill_unknown_json");

        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = IndexCommandRunner.RunBackfillFold(["--db", missingDb, "--dryrun", "--json"], _jsonOptions);

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Equal(string.Empty, stderr.ToString());
                using var document = JsonDocument.Parse(stdout.ToString());
                var json = document.RootElement;
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Contains("unknown option '--dryrun'", json.GetProperty("message").GetString());
                Assert.Contains("Run `cdidx backfill-fold --help`", json.GetProperty("hint").GetString());
                Assert.Equal(CommandErrorCodes.UsageError, json.GetProperty("error_code").GetString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    [Fact]
    public void RunBackfillFold_ConflictingCheckpointOptions_ReturnJsonUsageError_Issue4889()
    {
        var missingDb = CreateTempDbPath("cdidx_backfill_checkpoint_conflict_4889");

        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                var exitCode = IndexCommandRunner.RunBackfillFold(
                    ["--db", missingDb, "--checkpoint", "--no-checkpoint", "--json"],
                    _jsonOptions);

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                using var document = JsonDocument.Parse(stdout.ToString());
                Assert.Equal(
                    "--checkpoint and --no-checkpoint cannot be used together",
                    document.RootElement.GetProperty("message").GetString());
                Assert.False(Directory.Exists(missingDb + ".checkpoints"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [Fact]
    public void FormatIndexFileException_RegexTimeout_UsesBoundedExtractionMessage()
    {
        var ex = new RegexMatchTimeoutException("raw-sensitive-content", "raw-sensitive-pattern", TimeSpan.FromSeconds(2));

        var message = IndexCommandRunner.FormatIndexFileException(ex);

        Assert.Contains("Regex extraction timed out after 2s", message);
        Assert.Contains("file was skipped", message);
        Assert.DoesNotContain("raw-sensitive", message);
    }

    [Fact]
    public void FormatIndexFileException_GenericExceptionSanitizesRawMessage_Issue3796()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), "secret-project-token-ghp_1234567890abcdef-private", "Generated.cs");
        var ex = new IOException($"failed to read {secretPath} with payload token=ghp_1234567890abcdef_private");

        var message = IndexCommandRunner.FormatIndexFileException(ex);

        Assert.Equal("IOException", message);
        Assert.DoesNotContain(secretPath, message);
        Assert.DoesNotContain("ghp_1234567890abcdef", message);
        Assert.DoesNotContain("Generated.cs", message);
    }

    [Fact]
    public void FormatIndexFileException_SymbolWorkerFailurePreservesSafeDiagnostic()
    {
        const string workerDiagnostic = "worker_execution_failed: InvalidOperationException; origin=at CodeIndex.Indexer.SymbolExtractor.Extract in <redacted>:line 42";
        var ex = new SymbolExtractionWorkerFailureException(workerDiagnostic);

        var message = IndexCommandRunner.FormatIndexFileException(ex);
        var fileError = IndexCommandRunner.BuildIndexFileError("src/App.cs", "symbols", ex);

        Assert.Equal($"Symbol extraction worker failed. Worker diagnostic: {workerDiagnostic}", message);
        Assert.Equal("extraction_error", fileError.Category);
        Assert.Equal("symbols", fileError.Phase);
        Assert.Equal(message, fileError.Detail);
    }

    [Fact]
    public void FormatIndexPhasePath_AppendsPhaseSuffixForJsonLiveness()
    {
        var message = IndexCommandRunner.FormatIndexPhasePath("src/App.cs", "references");

        Assert.Equal("src/App.cs (references)", message);
    }

    [Fact]
    public void GetActiveCSharpPrepassPath_CompletedCandidateDoesNotClearRemainingWorker()
    {
        string?[] activePaths = ["src/Slow.cs", "src/Fast.cs"];

        IndexCommandRunner.SetActiveCSharpPrepassPath(activePaths, 1, null);

        Assert.Equal("src/Slow.cs", IndexCommandRunner.GetActiveCSharpPrepassPath(activePaths));
    }

    [Fact]
    public async Task ActiveCSharpPrepassPath_CrossThreadPublishAndClearRemainVisible()
    {
        string?[] activePaths = new string?[1];
        using var published = new ManualResetEventSlim();
        using var clear = new ManualResetEventSlim();
        var worker = Task.Run(() =>
        {
            IndexCommandRunner.SetActiveCSharpPrepassPath(activePaths, 0, "src/Worker.cs");
            published.Set();
            clear.Wait();
            IndexCommandRunner.SetActiveCSharpPrepassPath(activePaths, 0, null);
        });

        Assert.True(published.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("src/Worker.cs", IndexCommandRunner.GetActiveCSharpPrepassPath(activePaths));
        clear.Set();
        await worker;
        Assert.Null(IndexCommandRunner.GetActiveCSharpPrepassPath(activePaths));
    }

    [Fact]
    public void Run_FilesMode_WhenSymbolExtractionStalls_ReportsStallInsteadOfInterrupt()
    {
        var priorTimeout = IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        IndexCommandRunner.IndexExtractionStallTimeoutForTesting = () => TimeSpan.FromMilliseconds(1);
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_symbol_timeout");
        try
        {
            var source = Path.Combine(
                GetRepositoryRoot(),
                "src",
                "CodeIndex",
                "Indexer",
                "Symbols",
                "SymbolExtractor.JavaScriptTypeScriptSupport.cs");
            File.Copy(source, Path.Combine(projectRoot, "slow.cs"));

            var (exitCode, json, stderr) = RunAndCaptureJsonWithStderr([projectRoot, "--files", "slow.cs", "--db", dbPath, "--json", "--force"]);

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(CommandErrorCodes.IndexExtractionStalled, json.GetProperty("error_code").GetString());
            Assert.Contains("Index extraction made no progress", json.GetProperty("message").GetString());
            Assert.DoesNotContain(CommandErrorCodes.Interrupted, stderr);
        }
        finally
        {
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = priorTimeout;
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_TimeoutKillsWorkerBeforeDelayedExtractionContinues()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                SymbolExtractionWorker.DelayMillisecondsForTesting = 500;

                using var worker = new SymbolExtractionWorkerClient();
                var result = worker.Invoke(
                    0,
                    "csharp",
                    "public class App { }\n",
                    Path.Combine(projectRoot, "App.cs"),
                    projectRoot,
                    TimeSpan.FromMilliseconds(50));

                Assert.True(result.TimedOut);
                Assert.False(result.Success);
            }
            finally
            {
                SymbolExtractionWorker.DelayMillisecondsForTesting = null;
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_UnknownSymlinkPolicyFailsClosedForTypeScriptConfig_Issue5091()
    {
        var projectRoot = CreateTempProject();
        var outsideRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.ts");
            var configPath = Path.Combine(projectRoot, "tsconfig.json");
            var configTarget = Path.Combine(outsideRoot, "real-tsconfig.json");
            var moduleTarget = Path.Combine(outsideRoot, "sentinel-target.ts");
            var configJson = JsonSerializer.Serialize(new
            {
                compilerOptions = new
                {
                    baseUrl = ".",
                    paths = new Dictionary<string, string[]>
                    {
                        ["sentinel-alias"] = [moduleTarget],
                    },
                },
            });
            File.WriteAllText(configTarget, configJson);
            File.WriteAllText(moduleTarget, "export const sentinel = 1;\n");
            try
            {
                File.CreateSymbolicLink(configPath, configTarget);
            }
            catch (Exception ex) when (ShouldSkipSymlinkFixtureFailure(ex))
            {
                return;
            }

            using var worker = new SymbolExtractionWorkerClient();
            var result = worker.Invoke(
                0,
                "typescript",
                "import { sentinel } from \"sentinel-alias\";\nexport const value = sentinel;\n",
                sourcePath,
                projectRoot,
                SymbolWorkerStartupBudget,
                symlinkPolicy: (FileIndexer.SymlinkPolicy)int.MaxValue);

            Assert.False(result.TimedOut);
            Assert.True(result.Success, result.WorkerError);
            Assert.Contains(
                result.Symbols!,
                symbol => symbol.Kind == "import" && symbol.Name == "sentinel-alias");
            Assert.DoesNotContain(
                result.Symbols!,
                symbol => symbol.Kind == "import"
                    && symbol.Name == FileIndexer.NormalizePathSeparators(
                        Path.GetRelativePath(projectRoot, moduleTarget)));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteDirectory(outsideRoot);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_ExecutionFailureIncludesRedactedOrigin()
    {
        Exception exception;
        try
        {
            ThrowForSymbolWorkerDiagnosticTest();
            throw new UnreachableException();
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var diagnostic = SymbolExtractionWorker.FormatExecutionFailure(exception);

        Assert.StartsWith("worker_execution_failed: InvalidOperationException; origin=", diagnostic, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowForSymbolWorkerDiagnosticTest), diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", diagnostic, StringComparison.Ordinal);
    }

    private static void ThrowForSymbolWorkerDiagnosticTest()
    {
        throw new InvalidOperationException("secret-token");
    }

    [Fact]
    public void SymbolExtractionWorker_RunCommand_CancellationTokenStopsDelayedExtraction_Issue3399()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var request = new SymbolExtractionWorker.WorkerRequest(
                0,
                "csharp",
                "public class App { }\n",
                Path.Combine(projectRoot, "App.cs"),
                projectRoot);
            using var input = new StringReader(JsonSerializer.Serialize(request, SymbolExtractionWorker.JsonOptions) + Environment.NewLine);
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
            var stopwatch = Stopwatch.StartNew();

            var handled = SymbolExtractionWorker.TryRunCommand(
                [SymbolExtractionWorker.CommandName, "--test-delay-ms", "500"],
                input,
                output,
                error,
                out var exitCode,
                cancellationToken: cts.Token);

            stopwatch.Stop();
            Assert.True(handled);
            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cancellation took {stopwatch.Elapsed}.");
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WorkerProcessCleanupDiagnostics_ReturnBoundedProcessStateCategories_Issue3468()
    {
        using var symbolProcess = new Process();

        var symbolWait = SymbolExtractionWorkerClient.WaitForWorkerExit(symbolProcess, 1);
        var symbolKillDiagnostic = SymbolExtractionWorker.TryKillProcess(symbolProcess);

        Assert.False(symbolWait.Exited);
        Assert.Equal("worker_wait_failed: InvalidOperationException", symbolWait.Diagnostic);
        Assert.Equal(
            "worker_kill_failed: InvalidOperationException; worker_kill_wait_failed: InvalidOperationException",
            symbolKillDiagnostic);
        Assert.DoesNotContain("No process", symbolKillDiagnostic, StringComparison.Ordinal);

        using var hookProcess = new Process();

        var hookWait = PostExtractionHookCallbackWorkerClient.WaitForWorkerExit(hookProcess, 1);
        var hookKillDiagnostic = PostExtractionHookCallbackWorker.TryKillProcess(hookProcess);

        Assert.False(hookWait.Exited);
        Assert.Equal("worker_wait_failed: InvalidOperationException", hookWait.Diagnostic);
        Assert.Equal(
            "worker_kill_failed: InvalidOperationException; worker_kill_wait_failed: InvalidOperationException",
            hookKillDiagnostic);
        Assert.DoesNotContain("No process", hookKillDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolExtractionWorker_LegacyEnvironmentHooksAreIgnored_Issue3398()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_DELAY_MS",
                "CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_DONE_PATH");
            try
            {
                var completionPath = Path.Combine(projectRoot, "symbol-worker.done");
                env.Set("CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_DELAY_MS", "500");
                env.Set("CDIDX_TEST_SYMBOL_EXTRACTION_WORKER_DONE_PATH", completionPath);

                using var worker = new SymbolExtractionWorkerClient();
                var result = worker.Invoke(
                    0,
                    "csharp",
                    "public class App { }\n",
                    Path.Combine(projectRoot, "App.cs"),
                    projectRoot,
                    SymbolWorkerStartupBudget);

                Assert.True(result.Success, result.WorkerError);
                Assert.False(result.TimedOut);
                Assert.False(File.Exists(completionPath));
            }
            finally
            {
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_Utf8RequestsPreserveUnicodeAcrossLanguages()
    {
        var projectRoot = CreateTempProject();
        var sourceDirectory = Path.Combine(projectRoot, "ソース");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            var cases = new[]
            {
                (Lang: "csharp", Extension: ".cs", Content: "// 顧客\npublic class Customer { }\n"),
                (Lang: "java", Extension: ".java", Content: "// 顧客\npublic class Customer { }\n"),
                (Lang: "typescript", Extension: ".ts", Content: "// 顧客\nexport class Customer { }\n"),
                (Lang: "python", Extension: ".py", Content: "# 顧客\nclass Customer:\n    pass\n"),
                (Lang: "go", Extension: ".go", Content: "// 顧客\ntype Customer struct {}\n"),
                (Lang: "rust", Extension: ".rs", Content: "// 顧客\npub struct Customer {}\n"),
            };
            using var worker = new SymbolExtractionWorkerClient();
            var warmup = worker.Invoke(
                0,
                "csharp",
                "public class StartupProbe { }\n",
                Path.Combine(projectRoot, "StartupProbe.cs"),
                projectRoot,
                SymbolWorkerStartupBudget);

            Assert.False(warmup.TimedOut, "symbol worker startup exceeded the test-only startup budget");
            Assert.True(warmup.Success, $"symbol worker startup: {warmup.WorkerError}");

            foreach (var testCase in cases)
            {
                var result = worker.Invoke(
                    0,
                    testCase.Lang,
                    testCase.Content,
                    Path.Combine(sourceDirectory, "顧客" + testCase.Extension),
                    projectRoot,
                    SymbolWorkerRequestBudget);

                Assert.False(
                    result.TimedOut,
                    $"{testCase.Lang}: symbol worker callback exceeded the request budget after startup warm-up");
                Assert.True(result.Success, $"{testCase.Lang}: {result.WorkerError}");
                Assert.Contains(result.Symbols!, symbol => symbol.Name == "Customer");
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_NimIdentityKeySurvivesProtocolRoundTrip_Issue4738()
    {
        var projectRoot = CreateTempProject();
        try
        {
            using var worker = new SymbolExtractionWorkerClient();
            var result = worker.Invoke(
                0,
                "nim",
                "proc my_proc() = discard\n",
                Path.Combine(projectRoot, "sample.nim"),
                projectRoot,
                TimeSpan.FromSeconds(5));

            Assert.True(result.Success, result.WorkerError);
            var symbol = Assert.Single(result.Symbols!);
            Assert.Equal("my_proc", symbol.Name);
            Assert.Equal("myproc", symbol.IdentityNameFolded);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StreamResponseWritesBomlessUtf8Frame()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var request = new SymbolExtractionWorker.WorkerRequest(
                0,
                "csharp",
                "public class 顧客 { }\n",
                Path.Combine(projectRoot, "顧客.cs"),
                projectRoot);
            var requestUtf8 = JsonSerializer.SerializeToUtf8Bytes(request, SymbolExtractionWorker.JsonOptions);
            using var input = new MemoryStream();
            input.Write(requestUtf8);
            input.WriteByte((byte)'\n');
            input.Position = 0;
            using var output = new MemoryStream();
            using var error = new StringWriter();

            var handled = SymbolExtractionWorker.TryRunCommand(
                [SymbolExtractionWorker.CommandName],
                input,
                output,
                error,
                out var exitCode);

            Assert.True(handled);
            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            var responseUtf8 = output.ToArray();
            Assert.Equal((byte)'{', responseUtf8[0]);
            Assert.Equal((byte)'\n', responseUtf8[^1]);
            using var document = JsonDocument.Parse(responseUtf8.AsMemory(0, responseUtf8.Length - 1));
            var symbols = document.RootElement.GetProperty("Symbols");
            Assert.Contains(symbols.EnumerateArray(), symbol => symbol.GetProperty("Name").GetString() == "顧客");
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_CapturesStdoutAndForwardsStderrDiagnostics()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                WriteSymbolWorkerPatternConfig(
                    projectRoot,
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(a+)+$\"\n");
                SymbolExtractionWorker.ConsoleStdoutForTesting = "not-json-protocol";
                var slowLine = new string('a', 10_000) + "!";
                SymbolExtractionWorkerResult? result = null;

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    using var worker = new SymbolExtractionWorkerClient();
                    result = worker.Invoke(
                        0,
                        "toydsl",
                        slowLine,
                        Path.Combine(projectRoot, "demo.toy"),
                        projectRoot,
                        TimeSpan.FromSeconds(5));
                });

                Assert.NotNull(result);
                Assert.True(result.Success, result.WorkerError);
                Assert.False(result.TimedOut);
                Assert.Empty(result.Symbols!);
                Assert.Contains("Pattern extractor", stderr, StringComparison.Ordinal);
                Assert.Contains("timed out", stderr, StringComparison.Ordinal);
                Assert.DoesNotContain("not-json-protocol", stderr, StringComparison.Ordinal);
            }
            finally
            {
                SymbolExtractionWorker.ConsoleStdoutForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_ReusesPatternConfigDiscoveryPerProjectRoot()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                WriteSymbolWorkerPatternConfig(
                    projectRoot,
                    "toydsl.yaml",
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");
                using var worker = new SymbolExtractionWorkerClient();

                var first = worker.Invoke(
                    0,
                    "toydsl",
                    "entity First",
                    Path.Combine(projectRoot, "first.toy"),
                    projectRoot,
                    contentIsNormalized: true,
                    hasOversizeLine: false,
                    conflictMarkerLine: null,
                    TimeSpan.FromSeconds(5));

                Assert.True(first.Success, first.WorkerError);
                var firstSymbol = Assert.Single(first.Symbols!);
                Assert.Equal("First", firstSymbol.Name);

                WriteSymbolWorkerPatternConfig(
                    projectRoot,
                    "laterdsl.yaml",
                    "language: \"laterdsl\"\nextensions:\n  - extension: \".later\"\npatterns:\n  - kind: \"class\"\n    regex: \"^later (?<name>\\\\w+)\"\n");

                var second = worker.Invoke(
                    0,
                    "laterdsl",
                    "later Second",
                    Path.Combine(projectRoot, "second.later"),
                    projectRoot,
                    contentIsNormalized: true,
                    hasOversizeLine: false,
                    conflictMarkerLine: null,
                    TimeSpan.FromSeconds(5));

                Assert.True(second.Success, second.WorkerError);
                Assert.Empty(second.Symbols!);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_CachesUserRootAndAncestorPatternDiscoveryForRun()
    {
        var projectRoot = CreateTempProject();
        var userRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            WorkerPatternConfigDiscoveryCache? cache = null;
            var inspected = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var userPatternDirectory = Path.Combine(userRoot, ".cdidx", "patterns");
                var firstDirectory = Path.Combine(projectRoot, "src", "shared", "first");
                var secondDirectory = Path.Combine(projectRoot, "src", "shared", "second");
                Directory.CreateDirectory(firstDirectory);
                Directory.CreateDirectory(secondDirectory);
                WriteSymbolWorkerPatternConfig(
                    userRoot,
                    "user.yaml",
                    "language: \"workeruserdsl\"\nextensions:\n  - extension: \".workeruser\"\npatterns:\n  - kind: \"class\"\n    regex: \"^user (?<name>\\\\w+)\"\n");
                WriteSymbolWorkerPatternConfig(
                    projectRoot,
                    "root.yaml",
                    "language: \"workerrootdsl\"\nextensions:\n  - extension: \".workerroot\"\npatterns:\n  - kind: \"class\"\n    regex: \"^root (?<name>\\\\w+)\"\n");
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests = userPatternDirectory;
                ExtractorPluginRegistry.InspectPatternDirectoryForTesting = path =>
                {
                    var normalized = PathCasing.NormalizeBoundaryPath(path);
                    inspected[normalized] = inspected.GetValueOrDefault(normalized) + 1;
                };
                SymbolExtractionWorker.PatternConfigDiscoveryCacheFactoryForTesting = () =>
                    cache = new WorkerPatternConfigDiscoveryCache();

                var responses = RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(projectRoot, Path.Combine(firstDirectory, "one.cs")),
                    CreateSymbolWorkerRequest(projectRoot, Path.Combine(secondDirectory, "two.cs")),
                    CreateSymbolWorkerRequest(projectRoot, Path.Combine(firstDirectory, "three.cs")));

                Assert.All(responses, response => Assert.Null(response.WorkerError));
                Assert.NotNull(cache);
                Assert.Equal(1, cache.RootCount);
                Assert.Equal(4, cache.RetainedDirectoryCount);
                Assert.Equal(1, inspected[PathCasing.NormalizeBoundaryPath(userPatternDirectory)]);
                Assert.Equal(
                    1,
                    inspected[PathCasing.NormalizeBoundaryPath(Path.Combine(projectRoot, ".cdidx"))]);
                Assert.Equal(
                    1,
                    inspected[PathCasing.NormalizeBoundaryPath(Path.Combine(projectRoot, ".cdidx", "patterns"))]);
            }
            finally
            {
                SymbolExtractionWorker.PatternConfigDiscoveryCacheFactoryForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
                DeleteDirectory(userRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_MissingNestedSidecarRemainsAbsentUntilNewWorkerSnapshot()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "nested");
                var filePath = Path.Combine(sourceDirectory, "sample.laternested");
                Directory.CreateDirectory(sourceDirectory);
                ExtractorPluginRegistry.ResetForTests();

                using (var worker = new SymbolExtractionWorkerClient())
                {
                    var beforeConfig = worker.Invoke(
                        0,
                        "laternesteddsl",
                        "entity Before",
                        filePath,
                        projectRoot,
                        contentIsNormalized: true,
                        hasOversizeLine: false,
                        conflictMarkerLine: null,
                        TimeSpan.FromSeconds(5));
                    Assert.True(beforeConfig.Success, beforeConfig.WorkerError);
                    Assert.Empty(beforeConfig.Symbols!);

                    WriteSymbolWorkerPatternConfig(
                        sourceDirectory,
                        "later.yaml",
                        "language: \"laternesteddsl\"\nextensions:\n  - extension: \".laternested\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");

                    var sameSnapshot = worker.Invoke(
                        0,
                        "laternesteddsl",
                        "entity SameSnapshot",
                        filePath,
                        projectRoot,
                        contentIsNormalized: true,
                        hasOversizeLine: false,
                        conflictMarkerLine: null,
                        TimeSpan.FromSeconds(5));
                    Assert.True(sameSnapshot.Success, sameSnapshot.WorkerError);
                    Assert.Empty(sameSnapshot.Symbols!);
                }

                using var nextWorker = new SymbolExtractionWorkerClient();
                var nextSnapshot = nextWorker.Invoke(
                    0,
                    "laternesteddsl",
                    "entity NextSnapshot",
                    filePath,
                    projectRoot,
                    contentIsNormalized: true,
                    hasOversizeLine: false,
                    conflictMarkerLine: null,
                    TimeSpan.FromSeconds(5));
                Assert.True(nextSnapshot.Success, nextSnapshot.WorkerError);
                Assert.Equal("NextSnapshot", Assert.Single(nextSnapshot.Symbols!).Name);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_NewRunCommandResetsPatternDiscoverySnapshot()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "run-reset");
                var filePath = Path.Combine(sourceDirectory, "sample.runreset");
                Directory.CreateDirectory(sourceDirectory);
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests =
                    Path.Combine(projectRoot, "missing-user-patterns");

                var beforeConfig = Assert.Single(RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        filePath,
                        "runresetdsl",
                        "reset Before")));
                Assert.Null(beforeConfig.WorkerError);
                Assert.Empty(beforeConfig.Symbols!);

                WriteSymbolWorkerPatternConfig(
                    sourceDirectory,
                    "run-reset.yaml",
                    "language: \"runresetdsl\"\nextensions:\n  - extension: \".runreset\"\npatterns:\n  - kind: \"class\"\n    regex: \"^reset (?<name>\\\\w+)\"\n");

                var nextRun = Assert.Single(RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        filePath,
                        "runresetdsl",
                        "reset NextRun")));
                Assert.Null(nextRun.WorkerError);
                Assert.Equal("NextRun", Assert.Single(nextRun.Symbols!).Name);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_CachesKnownPatternDiscoveryFailureForRun()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "known-failure");
                WriteSymbolWorkerPatternConfig(
                    sourceDirectory,
                    "known.yaml",
                    "language: \"knownfailuredsl\"\nextensions:\n  - extension: \".knownfailure\"\npatterns:\n  - kind: \"class\"\n    regex: \"^known (?<name>\\\\w+)\"\n");
                var patternDirectory = PathCasing.NormalizeBoundaryPath(
                    Path.Combine(sourceDirectory, ".cdidx", "patterns"));
                var yamlAttempts = 0;
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests =
                    Path.Combine(projectRoot, "missing-user-patterns");
                ExtractorPluginRegistry.EnumeratePatternFilesForTesting = (directory, searchPattern) =>
                {
                    if (PathCasing.PathsEqual(patternDirectory, PathCasing.NormalizeBoundaryPath(directory))
                        && string.Equals(searchPattern, "*.yaml", StringComparison.Ordinal))
                    {
                        yamlAttempts++;
                        throw new IOException("simulated known pattern discovery failure");
                    }

                    return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
                };

                var responses = RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        Path.Combine(sourceDirectory, "first.knownfailure"),
                        "knownfailuredsl",
                        "known First"),
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        Path.Combine(sourceDirectory, "second.knownfailure"),
                        "knownfailuredsl",
                        "known Second"));

                Assert.Equal(1, yamlAttempts);
                Assert.Contains("pattern directory", responses[0].CapturedStderr, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(string.Empty, responses[1].CapturedStderr);
                Assert.Empty(responses[0].Symbols!);
                Assert.Empty(responses[1].Symbols!);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_CachesRejectedSymlinkPatternDirectoryForRun()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "unsafe");
                var externalPatternDirectory = Path.Combine(projectRoot, "external-patterns");
                Directory.CreateDirectory(sourceDirectory);
                Directory.CreateDirectory(externalPatternDirectory);
                var cdidxDirectory = Path.Combine(sourceDirectory, ".cdidx");
                Directory.CreateDirectory(cdidxDirectory);
                var patternDirectory = Path.Combine(cdidxDirectory, "patterns");
                try
                {
                    Directory.CreateSymbolicLink(patternDirectory, externalPatternDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    return;
                }

                var normalizedPatternDirectory = PathCasing.NormalizeBoundaryPath(patternDirectory);
                var patternInspections = 0;
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests =
                    Path.Combine(projectRoot, "missing-user-patterns");
                ExtractorPluginRegistry.InspectPatternDirectoryForTesting = path =>
                {
                    if (PathCasing.PathsEqual(
                            normalizedPatternDirectory,
                            PathCasing.NormalizeBoundaryPath(path)))
                    {
                        patternInspections++;
                    }
                };

                var responses = RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(projectRoot, Path.Combine(sourceDirectory, "first.cs")),
                    CreateSymbolWorkerRequest(projectRoot, Path.Combine(sourceDirectory, "second.cs")));

                Assert.Equal(1, patternInspections);
                Assert.Contains("symbolic links", responses[0].CapturedStderr, StringComparison.Ordinal);
                Assert.Equal(string.Empty, responses[1].CapturedStderr);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_RetriesUnexpectedPatternDiscoveryFailure()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "unexpected-failure");
                WriteSymbolWorkerPatternConfig(
                    sourceDirectory,
                    "retry.yaml",
                    "language: \"retryworkerdsl\"\nextensions:\n  - extension: \".retryworker\"\npatterns:\n  - kind: \"class\"\n    regex: \"^retry (?<name>\\\\w+)\"\n");
                var patternDirectory = PathCasing.NormalizeBoundaryPath(
                    Path.Combine(sourceDirectory, ".cdidx", "patterns"));
                var yamlAttempts = 0;
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests =
                    Path.Combine(projectRoot, "missing-user-patterns");
                ExtractorPluginRegistry.EnumeratePatternFilesForTesting = (directory, searchPattern) =>
                {
                    if (PathCasing.PathsEqual(patternDirectory, PathCasing.NormalizeBoundaryPath(directory))
                        && string.Equals(searchPattern, "*.yaml", StringComparison.Ordinal)
                        && ++yamlAttempts == 1)
                    {
                        throw new InvalidOperationException("simulated unexpected pattern discovery failure");
                    }

                    return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
                };

                var responses = RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        Path.Combine(sourceDirectory, "first.retryworker"),
                        "retryworkerdsl",
                        "retry First"),
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        Path.Combine(sourceDirectory, "second.retryworker"),
                        "retryworkerdsl",
                        "retry Second"));

                Assert.NotNull(responses[0].WorkerError);
                Assert.Null(responses[1].WorkerError);
                Assert.Equal(2, yamlAttempts);
                Assert.Equal("Second", Assert.Single(responses[1].Symbols!).Name);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_SaturatedPatternCacheFallsBackToUncachedDiscovery()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            WorkerPatternConfigDiscoveryCache? cache = null;
            try
            {
                var firstDirectory = Path.Combine(projectRoot, "src", "first");
                var overflowDirectory = Path.Combine(projectRoot, "src", "overflow");
                Directory.CreateDirectory(firstDirectory);
                WriteSymbolWorkerPatternConfig(
                    overflowDirectory,
                    "overflow.yaml",
                    "language: \"overflowworkerdsl\"\nextensions:\n  - extension: \".overflowworker\"\npatterns:\n  - kind: \"class\"\n    regex: \"^overflow (?<name>\\\\w+)\"\n");
                var overflowPatternDirectory = PathCasing.NormalizeBoundaryPath(
                    Path.Combine(overflowDirectory, ".cdidx", "patterns"));
                var overflowYamlEnumerations = 0;
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests =
                    Path.Combine(projectRoot, "missing-user-patterns");
                ExtractorPluginRegistry.EnumeratePatternFilesForTesting = (directory, searchPattern) =>
                {
                    if (PathCasing.PathsEqual(
                            overflowPatternDirectory,
                            PathCasing.NormalizeBoundaryPath(directory))
                        && string.Equals(searchPattern, "*.yaml", StringComparison.Ordinal))
                    {
                        overflowYamlEnumerations++;
                    }

                    return Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
                };
                SymbolExtractionWorker.PatternConfigDiscoveryCacheFactoryForTesting = () =>
                    cache = new WorkerPatternConfigDiscoveryCache(
                        maxRootSnapshots: 1,
                        maxDirectoriesPerRoot: 1,
                        maxDirectoriesPerWorker: 1);

                var responses = RunSymbolWorkerRequestsInProcess(
                    CreateSymbolWorkerRequest(projectRoot, Path.Combine(firstDirectory, "first.cs")),
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        Path.Combine(overflowDirectory, "first.overflowworker"),
                        "overflowworkerdsl",
                        "overflow First"),
                    CreateSymbolWorkerRequest(
                        projectRoot,
                        Path.Combine(overflowDirectory, "second.overflowworker"),
                        "overflowworkerdsl",
                        "overflow Second"));

                Assert.All(responses, response => Assert.Null(response.WorkerError));
                Assert.Equal("First", Assert.Single(responses[1].Symbols!).Name);
                Assert.Equal("Second", Assert.Single(responses[2].Symbols!).Name);
                Assert.Equal(2, overflowYamlEnumerations);
                Assert.NotNull(cache);
                Assert.Equal(1, cache.RetainedDirectoryCount);
            }
            finally
            {
                SymbolExtractionWorker.PatternConfigDiscoveryCacheFactoryForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void WorkerPatternConfigDiscoveryCache_UsesFilesystemCasingAndBoundedRootLru()
    {
        var projectRoot = CreateTempProject();
        lock (PathCasingTestLock.Gate)
        {
            var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
            try
            {
                PathCasing.ResetCacheForTests();
                PathCasing.IgnoreCaseProbeForTesting = _ => true;
                var cache = new WorkerPatternConfigDiscoveryCache(
                    maxRootSnapshots: 2,
                    maxDirectoriesPerRoot: 1,
                    maxDirectoriesPerWorker: 2);
                var rootAPath = Path.Combine(projectRoot, "root-a");
                var rootBPath = Path.Combine(projectRoot, "root-b");
                var rootCPath = Path.Combine(projectRoot, "root-c");
                var rootA = cache.AddReloadedRoot(rootAPath);
                var upperDirectory = Path.Combine(rootAPath, "Src", ".cdidx", "patterns");
                var lowerDirectory = Path.Combine(rootAPath, "src", ".cdidx", "patterns");
                Assert.True(cache.ShouldInspectPatternDirectory(rootA, upperDirectory));
                cache.RecordInspectedPatternDirectory(rootA, upperDirectory);
                Assert.False(cache.ShouldInspectPatternDirectory(rootA, lowerDirectory));

                var overflowDirectory = Path.Combine(rootAPath, "other", ".cdidx", "patterns");
                Assert.True(cache.ShouldInspectPatternDirectory(rootA, overflowDirectory));
                cache.RecordInspectedPatternDirectory(rootA, overflowDirectory);
                Assert.True(cache.ShouldInspectPatternDirectory(rootA, overflowDirectory));

                _ = cache.AddReloadedRoot(rootBPath);
                Assert.True(cache.TryGetRoot(rootAPath, out _));
                _ = cache.AddReloadedRoot(rootCPath);
                Assert.True(cache.TryGetRoot(rootAPath, out _));
                Assert.False(cache.TryGetRoot(rootBPath, out _));
                Assert.True(cache.TryGetRoot(rootCPath, out _));
                Assert.Equal(2, cache.RootCount);

                PathCasing.ResetCacheForTests();
                PathCasing.IgnoreCaseProbeForTesting = _ => false;
                var sensitiveCache = new WorkerPatternConfigDiscoveryCache();
                var sensitiveRoot = sensitiveCache.AddReloadedRoot(rootAPath);
                sensitiveCache.RecordInspectedPatternDirectory(sensitiveRoot, upperDirectory);
                Assert.True(sensitiveCache.ShouldInspectPatternDirectory(sensitiveRoot, lowerDirectory));
            }
            finally
            {
                PathCasing.IgnoreCaseProbeForTesting = previousProbe;
                PathCasing.ResetCacheForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void WorkerPatternConfigDiscoveryCache_DoesNotProbeSymlinkedPatternTargetForCasing()
    {
        var projectRoot = CreateTempProject();
        lock (PathCasingTestLock.Gate)
        {
            var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "unsafe-case-probe");
                var cdidxDirectory = Path.Combine(sourceDirectory, ".cdidx");
                var externalPatternDirectory = Path.Combine(projectRoot, "external-patterns");
                Directory.CreateDirectory(cdidxDirectory);
                Directory.CreateDirectory(externalPatternDirectory);
                var patternDirectory = Path.Combine(cdidxDirectory, "patterns");
                try
                {
                    Directory.CreateSymbolicLink(patternDirectory, externalPatternDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    return;
                }

                var probedAnchors = new List<string>();
                PathCasing.ResetCacheForTests();
                PathCasing.IgnoreCaseProbeForTesting = anchor =>
                {
                    probedAnchors.Add(PathCasing.NormalizeBoundaryPath(anchor));
                    return false;
                };
                var cache = new WorkerPatternConfigDiscoveryCache();
                var root = cache.AddReloadedRoot(projectRoot);

                Assert.True(cache.ShouldInspectPatternDirectory(root, patternDirectory));
                cache.RecordInspectedPatternDirectory(root, patternDirectory);

                Assert.Contains(PathCasing.NormalizeBoundaryPath(sourceDirectory), probedAnchors);
                Assert.DoesNotContain(PathCasing.NormalizeBoundaryPath(patternDirectory), probedAnchors);
                Assert.DoesNotContain(PathCasing.NormalizeBoundaryPath(externalPatternDirectory), probedAnchors);
            }
            finally
            {
                PathCasing.IgnoreCaseProbeForTesting = previousProbe;
                PathCasing.ResetCacheForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Run_NestedPatternSidecarReachesBoundedSymbolWorker_Issue4597()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_nested_pattern_worker_4597");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var sourceDirectory = Path.Combine(projectRoot, "src", "nested");
                Directory.CreateDirectory(sourceDirectory);
                WriteSymbolWorkerPatternConfig(
                    sourceDirectory,
                    "nested.yaml",
                    "language: \"nesteddsl\"\nextensions:\n  - extension: \".nested\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");
                File.WriteAllText(Path.Combine(sourceDirectory, "sample.nested"), "entity NestedEntity\n");

                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json", "--force"]);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("success", json.GetProperty("status").GetString());
                using var connection = OpenNonPoolingConnection(dbPath);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT s.name
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE f.path = 'src/nested/sample.nested'
                    """;
                Assert.Equal("NestedEntity", command.ExecuteScalar() as string);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
                DeleteFile(dbPath);
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_InvalidRequestJsonDoesNotEchoParserMessage_Issue3425()
    {
        const string secret = "SECRET_SYMBOL_WORKER_3425";
        using var input = new StringReader("{\"Content\":\"" + secret + "\",\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = SymbolExtractionWorker.TryRunCommand(
            [SymbolExtractionWorker.CommandName],
            input,
            output,
            error,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var workerError = document.RootElement.GetProperty("WorkerError").GetString();
        Assert.Equal("worker_protocol_error: JsonException", workerError);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolExtractionWorker_OversizedRequestLineReturnsProtocolError_Issue3506()
    {
        using var input = new StringReader("abcdef\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = SymbolExtractionWorker.TryRunCommand(
            [SymbolExtractionWorker.CommandName],
            input,
            output,
            error,
            out var exitCode,
            maxProtocolLineCharacters: 5,
            maxProtocolLineUtf8Bytes: 100);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var workerError = document.RootElement.GetProperty("WorkerError").GetString();
        Assert.Equal("worker_protocol_error: BoundedLineLengthException", workerError);
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_InvalidRequestJsonDoesNotEchoParserMessage_Issue3425()
    {
        const string secret = "SECRET_HOOK_WORKER_3425";
        using var input = new StringReader("{\"Callback\":\"" + secret + "\",\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = PostExtractionHookCallbackWorker.TryRunCommand(
            [PostExtractionHookCallbackWorker.CommandName, "/tmp/demo-hook.dll", "Demo.Hook"],
            input,
            output,
            error,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var workerError = document.RootElement.GetProperty("WorkerError").GetString();
        Assert.Equal("worker_protocol_error: JsonException", workerError);
        Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_OversizedRequestLineReturnsProtocolError_Issue3506()
    {
        using var input = new StringReader("abcdef\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = PostExtractionHookCallbackWorker.TryRunCommand(
            [PostExtractionHookCallbackWorker.CommandName, "/tmp/demo-hook.dll", "Demo.Hook"],
            input,
            output,
            error,
            out var exitCode,
            maxProtocolLineCharacters: 5,
            maxProtocolLineUtf8Bytes: 100);

        Assert.True(handled);
        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var workerError = document.RootElement.GetProperty("WorkerError").GetString();
        Assert.Equal("worker_protocol_error: BoundedLineLengthException", workerError);
    }

    [Fact]
    public void WorkerProtocol_RejectsExcessiveJsonProperties_Issue3759()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                WorkerProtocolJsonValidator.MaxJsonPropertiesForTesting = 1;
                using var input = new StringReader("{\"FileId\":0,\"Lang\":\"csharp\"}\n");
                using var output = new StringWriter();
                using var error = new StringWriter();

                var handled = SymbolExtractionWorker.TryRunCommand(
                    [SymbolExtractionWorker.CommandName],
                    input,
                    output,
                    error,
                    out var exitCode);

                Assert.True(handled);
                Assert.Equal(0, exitCode);
                Assert.Equal(string.Empty, error.ToString());
                using var document = JsonDocument.Parse(output.ToString());
                var workerError = document.RootElement.GetProperty("WorkerError").GetString();
                Assert.Equal("worker_protocol_error: json_property_limit_exceeded", workerError);
            }
            finally
            {
                WorkerProtocolJsonValidator.MaxJsonPropertiesForTesting = null;
            }
        }
    }

    [Fact]
    public void WorkerProtocol_RejectsExcessiveJsonDepth_Issue3908()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                WorkerProtocolJsonValidator.MaxJsonDepthForTesting = 4;
                using var input = new StringReader("{\"FileId\":0,\"Lang\":{\"nested\":{\"too\":{\"deep\":\"csharp\"}}}}\n");
                using var output = new StringWriter();
                using var error = new StringWriter();

                var handled = SymbolExtractionWorker.TryRunCommand(
                    [SymbolExtractionWorker.CommandName],
                    input,
                    output,
                    error,
                    out var exitCode);

                Assert.True(handled);
                Assert.Equal(0, exitCode);
                Assert.Equal(string.Empty, error.ToString());
                using var document = JsonDocument.Parse(output.ToString());
                var workerError = document.RootElement.GetProperty("WorkerError").GetString();
                Assert.Equal("worker_protocol_error: JsonException", workerError);
            }
            finally
            {
                WorkerProtocolJsonValidator.MaxJsonDepthForTesting = null;
            }
        }
    }

    [Fact]
    public void WorkerProtocol_RejectsOversizedJsonStrings_Issue3759()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                WorkerProtocolJsonValidator.MaxStringCharactersForTesting = 4;
                using var input = new StringReader("{\"Callback\":\"OnSymbolsExtracted\"}\n");
                using var output = new StringWriter();
                using var error = new StringWriter();

                var handled = PostExtractionHookCallbackWorker.TryRunCommand(
                    [PostExtractionHookCallbackWorker.CommandName, "/tmp/demo-hook.dll", "Demo.Hook"],
                    input,
                    output,
                    error,
                    out var exitCode);

                Assert.True(handled);
                Assert.Equal(0, exitCode);
                Assert.Equal(string.Empty, error.ToString());
                using var document = JsonDocument.Parse(output.ToString());
                var workerError = document.RootElement.GetProperty("WorkerError").GetString();
                Assert.Equal("worker_protocol_error: json_string_length_exceeded", workerError);
            }
            finally
            {
                WorkerProtocolJsonValidator.MaxStringCharactersForTesting = null;
            }
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StartInfo_UsesCurrentCdidxExecutableWhenAvailable()
    {
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");

        var created = SymbolExtractionWorker.TryCreateStartInfo(
            currentProcessPath,
            runnerAssemblyPath: string.Empty,
            out var startInfo,
            out var error);

        Assert.True(created, error);
        Assert.Equal(currentProcessPath, startInfo.FileName);
        Assert.Equal(
            [
                SymbolExtractionWorker.CommandName,
                "--protocol-max-line-bytes",
                WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
            ],
            startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void IsolatedWorkers_StartInfo_ScrubsEnvironmentByAllowlist_Issue3759()
    {
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_TEST_WORKER_ALLOWLIST_3759",
                "CDIDX_SECRET_WORKER_3759");
            env.Set("CDIDX_TEST_WORKER_ALLOWLIST_3759", "allowed");
            env.Set("CDIDX_SECRET_WORKER_3759", "secret");
            var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");

            var created = SymbolExtractionWorker.TryCreateStartInfo(
                currentProcessPath,
                runnerAssemblyPath: string.Empty,
                out var startInfo,
                out var error);

            Assert.True(created, error);
            Assert.Equal("allowed", startInfo.Environment["CDIDX_TEST_WORKER_ALLOWLIST_3759"]);
            Assert.False(startInfo.Environment.ContainsKey("CDIDX_SECRET_WORKER_3759"));
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StartInfo_BoundsInternalTestDelay_Issue3398()
    {
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");
        try
        {
            SymbolExtractionWorker.DelayMillisecondsForTesting =
                SymbolExtractionWorker.MaxDelayMillisecondsForTesting + 1;

            var created = SymbolExtractionWorker.TryCreateStartInfo(
                currentProcessPath,
                runnerAssemblyPath: string.Empty,
                out var startInfo,
                out var error);

            Assert.True(created, error);
            Assert.Equal(
                [
                    SymbolExtractionWorker.CommandName,
                    "--protocol-max-line-bytes",
                    WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                    "--test-delay-ms",
                    SymbolExtractionWorker.MaxDelayMillisecondsForTesting.ToString(CultureInfo.InvariantCulture),
                ],
                startInfo.ArgumentList);
        }
        finally
        {
            SymbolExtractionWorker.DelayMillisecondsForTesting = null;
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StartInfo_UsesFrameworkDependentDllWithTrustedDotnetHost()
    {
        var currentProcessPath = CreateTemporaryDotnetHostPath();
        var runnerAssemblyPath = Path.Combine(Path.GetTempPath(), "cdidx.dll");

        try
        {
            var created = SymbolExtractionWorker.TryCreateStartInfo(
                currentProcessPath,
                runnerAssemblyPath,
                out var startInfo,
                out var error);

            Assert.True(created, error);
            Assert.Equal(currentProcessPath, startInfo.FileName);
            Assert.Equal(
                [
                    runnerAssemblyPath,
                    SymbolExtractionWorker.CommandName,
                    "--protocol-max-line-bytes",
                    WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                ],
                startInfo.ArgumentList);
        }
        finally
        {
            DeleteTemporaryDotnetHostPath(currentProcessPath);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StartInfo_UsesTrustedDotnetCandidateWhenCurrentProcessIsTestHost_Issue3455()
    {
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "testhost.exe" : "testhost");
        var trustedDotnetPath = CreateTemporaryDotnetHostPath();
        var runnerAssemblyPath = Path.Combine(Path.GetTempPath(), "cdidx.dll");
        var originalCandidatesOverride = DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride;

        try
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = [trustedDotnetPath];

            var created = SymbolExtractionWorker.TryCreateStartInfo(
                currentProcessPath,
                runnerAssemblyPath,
                out var startInfo,
                out var error);

            Assert.True(created, error);
            Assert.Equal(trustedDotnetPath, startInfo.FileName);
            Assert.Equal(
                [
                    runnerAssemblyPath,
                    SymbolExtractionWorker.CommandName,
                    "--protocol-max-line-bytes",
                    WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                ],
                startInfo.ArgumentList);
        }
        finally
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = originalCandidatesOverride;
            DeleteTemporaryDotnetHostPath(trustedDotnetPath);
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StartInfo_FailsWithoutTrustedDotnetHost_Issue3455()
    {
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "testhost.exe" : "testhost");
        var runnerAssemblyPath = Path.Combine(Path.GetTempPath(), "cdidx.dll");
        var originalCandidatesOverride = DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride;

        try
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = [];

            var created = SymbolExtractionWorker.TryCreateStartInfo(
                currentProcessPath,
                runnerAssemblyPath,
                out _,
                out var error);

            Assert.False(created);
            Assert.Contains("trusted dotnet host path", error);
        }
        finally
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = originalCandidatesOverride;
        }
    }

    [Fact]
    public void DotnetHostPathResolver_RejectsMissingDotnetHost_Issue3455()
    {
        var missingDotnetPath = Path.Combine(Path.GetTempPath(), $"cdidx_missing_dotnet_{Guid.NewGuid():N}", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var originalCandidatesOverride = DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride;

        try
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = [];

            var resolved = DotnetHostPathResolver.Resolve(missingDotnetPath);

            Assert.Null(resolved);
        }
        finally
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = originalCandidatesOverride;
        }
    }

    [Fact]
    public void SymbolExtractionWorker_StartInfo_RaisesProtocolLimitForLargeFileCap_Issue3506()
    {
        const long maxFileSizeBytes = 50L * 1024L * 1024L;
        var protocolLimit = WorkerProtocolLineLimits.ResolveForSourceFileBytes(maxFileSizeBytes);
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");

        var created = SymbolExtractionWorker.TryCreateStartInfo(
            currentProcessPath,
            runnerAssemblyPath: string.Empty,
            protocolLimit,
            out var startInfo,
            out var error);

        Assert.True(created, error);
        Assert.True(protocolLimit > WorkerProtocolLineLimits.MaxLineUtf8Bytes);
        Assert.Equal(
            [
                SymbolExtractionWorker.CommandName,
                "--protocol-max-line-bytes",
                protocolLimit.ToString(CultureInfo.InvariantCulture),
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    public void IsolatedWorkers_StartInfo_ShareDefaultsAndProtocolArguments_Issue3703()
    {
        var protocolLimit = WorkerProtocolLineLimits.MaxLineUtf8Bytes + 1024;
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");
        var hook = new PostExtractionHookInfo(
            "demo",
            Path.Combine(Path.GetTempPath(), "demo-hook.dll"),
            "Demo.Hook");

        var symbolCreated = SymbolExtractionWorker.TryCreateStartInfo(
            currentProcessPath,
            runnerAssemblyPath: string.Empty,
            protocolLimit,
            out var symbolStartInfo,
            out var symbolError);
        var hookCreated = PostExtractionHookCallbackWorker.TryCreateStartInfo(
            hook,
            currentProcessPath,
            runnerAssemblyPath: string.Empty,
            protocolLimit,
            out var hookStartInfo,
            out var hookError);

        Assert.True(symbolCreated, symbolError);
        Assert.True(hookCreated, hookError);
        AssertIsolatedWorkerStartInfoDefaults(symbolStartInfo);
        AssertIsolatedWorkerStartInfoDefaults(hookStartInfo);
        Assert.Equal(currentProcessPath, symbolStartInfo.FileName);
        Assert.Equal(currentProcessPath, hookStartInfo.FileName);
        Assert.Equal(
            [
                SymbolExtractionWorker.CommandName,
                "--protocol-max-line-bytes",
                protocolLimit.ToString(CultureInfo.InvariantCulture),
            ],
            symbolStartInfo.ArgumentList);
        Assert.Equal(
            [
                PostExtractionHookCallbackWorker.CommandName,
                hook.AssemblyPath,
                hook.TypeName,
                "--protocol-max-line-bytes",
                protocolLimit.ToString(CultureInfo.InvariantCulture),
            ],
            hookStartInfo.ArgumentList);
    }

    private static void AssertIsolatedWorkerStartInfoDefaults(ProcessStartInfo startInfo)
    {
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardInputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardOutputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardErrorEncoding?.WebName);
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_StartInfo_UsesCurrentCdidxExecutableWhenAvailable()
    {
        var hook = new PostExtractionHookInfo(
            "demo",
            Path.Combine(Path.GetTempPath(), "demo-hook.dll"),
            "Demo.Hook");
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");

        var created = PostExtractionHookCallbackWorker.TryCreateStartInfo(
            hook,
            currentProcessPath,
            runnerAssemblyPath: string.Empty,
            out var startInfo,
            out var error);

        Assert.True(created, error);
        Assert.Equal(currentProcessPath, startInfo.FileName);
        Assert.Equal(
            [
                PostExtractionHookCallbackWorker.CommandName,
                hook.AssemblyPath,
                hook.TypeName,
                "--protocol-max-line-bytes",
                WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
            ],
            startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_StartInfo_UsesFrameworkDependentDllWithTrustedDotnetHost()
    {
        var hook = new PostExtractionHookInfo(
            "demo",
            Path.Combine(Path.GetTempPath(), "demo-hook.dll"),
            "Demo.Hook");
        var currentProcessPath = CreateTemporaryDotnetHostPath();
        var runnerAssemblyPath = Path.Combine(Path.GetTempPath(), "cdidx.dll");

        try
        {
            var created = PostExtractionHookCallbackWorker.TryCreateStartInfo(
                hook,
                currentProcessPath,
                runnerAssemblyPath,
                out var startInfo,
                out var error);

            Assert.True(created, error);
            Assert.Equal(currentProcessPath, startInfo.FileName);
            Assert.Equal(
                [
                    runnerAssemblyPath,
                    PostExtractionHookCallbackWorker.CommandName,
                    hook.AssemblyPath,
                    hook.TypeName,
                    "--protocol-max-line-bytes",
                    WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                ],
                startInfo.ArgumentList);
        }
        finally
        {
            DeleteTemporaryDotnetHostPath(currentProcessPath);
        }
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_StartInfo_UsesTrustedDotnetCandidateWhenCurrentProcessIsTestHost_Issue3455()
    {
        var hook = new PostExtractionHookInfo(
            "demo",
            Path.Combine(Path.GetTempPath(), "demo-hook.dll"),
            "Demo.Hook");
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "testhost.exe" : "testhost");
        var trustedDotnetPath = CreateTemporaryDotnetHostPath();
        var runnerAssemblyPath = Path.Combine(Path.GetTempPath(), "cdidx.dll");
        var originalCandidatesOverride = DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride;

        try
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = [trustedDotnetPath];

            var created = PostExtractionHookCallbackWorker.TryCreateStartInfo(
                hook,
                currentProcessPath,
                runnerAssemblyPath,
                out var startInfo,
                out var error);

            Assert.True(created, error);
            Assert.Equal(trustedDotnetPath, startInfo.FileName);
            Assert.Equal(
                [
                    runnerAssemblyPath,
                    PostExtractionHookCallbackWorker.CommandName,
                    hook.AssemblyPath,
                    hook.TypeName,
                    "--protocol-max-line-bytes",
                    WorkerProtocolLineLimits.MaxLineUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                ],
                startInfo.ArgumentList);
        }
        finally
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = originalCandidatesOverride;
            DeleteTemporaryDotnetHostPath(trustedDotnetPath);
        }
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_StartInfo_FailsWithoutTrustedDotnetHost_Issue3455()
    {
        var hook = new PostExtractionHookInfo(
            "demo",
            Path.Combine(Path.GetTempPath(), "demo-hook.dll"),
            "Demo.Hook");
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "testhost.exe" : "testhost");
        var runnerAssemblyPath = Path.Combine(Path.GetTempPath(), "cdidx.dll");
        var originalCandidatesOverride = DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride;

        try
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = [];

            var created = PostExtractionHookCallbackWorker.TryCreateStartInfo(
                hook,
                currentProcessPath,
                runnerAssemblyPath,
                out _,
                out var error);

            Assert.False(created);
            Assert.Contains("trusted dotnet host path", error);
        }
        finally
        {
            DotnetHostPathResolver.TrustedDotnetHostCandidatesOverride = originalCandidatesOverride;
        }
    }

    [Fact]
    public void PostExtractionHookCallbackWorker_StartInfo_RaisesProtocolLimitForLargeFileCap_Issue3506()
    {
        const long maxFileSizeBytes = 50L * 1024L * 1024L;
        var protocolLimit = WorkerProtocolLineLimits.ResolveForSourceFileBytes(maxFileSizeBytes);
        var hook = new PostExtractionHookInfo(
            "demo",
            Path.Combine(Path.GetTempPath(), "demo-hook.dll"),
            "Demo.Hook");
        var currentProcessPath = Path.Combine(Path.GetTempPath(), OperatingSystem.IsWindows() ? "cdidx.exe" : "cdidx");

        var created = PostExtractionHookCallbackWorker.TryCreateStartInfo(
            hook,
            currentProcessPath,
            runnerAssemblyPath: string.Empty,
            protocolLimit,
            out var startInfo,
            out var error);

        Assert.True(created, error);
        Assert.True(protocolLimit > WorkerProtocolLineLimits.MaxLineUtf8Bytes);
        Assert.Equal(
            [
                PostExtractionHookCallbackWorker.CommandName,
                hook.AssemblyPath,
                hook.TypeName,
                "--protocol-max-line-bytes",
                protocolLimit.ToString(CultureInfo.InvariantCulture),
            ],
            startInfo.ArgumentList);
    }

    [PublishedTrimmedCliFact]
    public void Run_PublishedSingleFileBinary_IndexesWithIsolatedSymbolWorker()
    {
        var projectRoot = CreateTempProject();
        var publishDir = Path.Combine(Path.GetTempPath(), $"cdidx_single_file_publish_{Guid.NewGuid():N}");
        var dbPath = CreateTempDbPath("cdidx_single_file_index");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "App.cs"),
                "public class PublishedSingleFileApp { public void Run() { } }\n");

            var publishedCli = TrimmedCliTestHelper.PublishTrimmedCli(publishDir, publishSingleFile: true);

            var (exitCode, stdout, stderr) = TrimmedCliTestHelper.RunPublishedCli(publishedCli, projectRoot, projectRoot, "--db", dbPath, "--json", "--force");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("cdidx: scanning files...", stderr);
            Assert.Contains("cdidx: preparing index writes...", stderr);
            using (var document = JsonDocument.Parse(stdout))
                Assert.Equal("success", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.True(CountRows(dbPath, "symbols") >= 1);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteDirectory(publishDir);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void GetJsonIndexHeartbeatPath_UsesWorkerPhaseWhenMainThreadIsIdle()
    {
        var message = IndexCommandRunner.GetJsonIndexHeartbeatPath(
            currentFile: null,
            activeExtractionPhases: ["src/App.cs (references)"]);

        Assert.Equal("src/App.cs (references)", message);
    }

    [Fact]
    public void GetJsonIndexHeartbeatPath_PrefersMainThreadPhaseWhenCommittingResults()
    {
        var message = IndexCommandRunner.GetJsonIndexHeartbeatPath(
            "src/App.cs (committing)",
            ["src/Other.cs (references)"]);

        Assert.Equal("src/App.cs (committing)", message);
    }

    [Fact]
    public void LoadScanCheckpoint_ValidPayload_ReturnsBoundedDirectorySet()
    {
        var projectRoot = CreateTempProject();
        var checkpointPath = Path.Combine(projectRoot, "scan-checkpoint.json");
        try
        {
            File.WriteAllText(checkpointPath, JsonSerializer.Serialize(new
            {
                Version = 1,
                GitHead = "abc123",
                Directories = new[] { "src", string.Empty, "tests", "src" },
            }));

            var directories = IndexCommandRunner.LoadScanCheckpoint(checkpointPath, "abc123");

            Assert.Equal(2, directories.Count);
            Assert.Contains("src", directories);
            Assert.Contains("tests", directories);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadScanCheckpoint_DirectoryPayloadOutsideBounds_ReturnsEmpty()
    {
        var projectRoot = CreateTempProject();
        var checkpointPath = Path.Combine(projectRoot, "scan-checkpoint.json");
        try
        {
            File.WriteAllText(checkpointPath, JsonSerializer.Serialize(new
            {
                Version = 1,
                GitHead = "abc123",
                Directories = Enumerable.Range(0, IndexCommandRunner.MaxScanCheckpointDirectories + 1)
                    .Select(i => $"dir{i}")
                    .ToArray(),
            }));
            Assert.Empty(IndexCommandRunner.LoadScanCheckpoint(checkpointPath, "abc123"));

            File.WriteAllText(checkpointPath, JsonSerializer.Serialize(new
            {
                Version = 1,
                GitHead = "abc123",
                Directories = new[] { new string('x', IndexCommandRunner.MaxScanCheckpointDirectoryLength + 1) },
            }));
            Assert.Empty(IndexCommandRunner.LoadScanCheckpoint(checkpointPath, "abc123"));

            File.WriteAllText(checkpointPath, """{"Version":1,"GitHead":"abc123","Directories":[null]}""");
            Assert.Empty(IndexCommandRunner.LoadScanCheckpoint(checkpointPath, "abc123"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadScanCheckpointDetailed_InvalidPayloads_ReturnsReasonedWarning()
    {
        var projectRoot = CreateTempProject();
        var checkpointPath = Path.Combine(projectRoot, "scan-checkpoint.json");
        try
        {
            File.WriteAllText(checkpointPath, """{"Version":999,"GitHead":"abc123","Directories":["src"]}""");
            var futureVersion = IndexCommandRunner.LoadScanCheckpointDetailed(checkpointPath, "abc123");
            Assert.Empty(futureVersion.Directories);
            Assert.Contains("future checkpoint version", futureVersion.WarningMessage);

            File.WriteAllText(checkpointPath, """{"Version":1,"GitHead":"old","Directories":["src"]}""");
            var stale = IndexCommandRunner.LoadScanCheckpointDetailed(checkpointPath, "abc123");
            Assert.Empty(stale.Directories);
            Assert.Contains("checkpoint GitHead does not match current HEAD", stale.WarningMessage);

            File.WriteAllText(checkpointPath, """{"Version":1,"GitHead":"abc123","Directories":["src"]""");
            var malformed = IndexCommandRunner.LoadScanCheckpointDetailed(checkpointPath, "abc123");
            Assert.Empty(malformed.Directories);
            Assert.Contains("malformed checkpoint JSON", malformed.WarningMessage);

            File.WriteAllText(checkpointPath, """{"Version":1,"GitHead":"abc123","Directories":[null]}""");
            var invalidDirectories = IndexCommandRunner.LoadScanCheckpointDetailed(checkpointPath, "abc123");
            Assert.Empty(invalidDirectories.Directories);
            Assert.Contains("Directories contains a null entry", invalidDirectories.WarningMessage);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadScanCheckpoint_JsonDepthOutsideBounds_ReturnsEmpty()
    {
        var projectRoot = CreateTempProject();
        var checkpointPath = Path.Combine(projectRoot, "scan-checkpoint.json");
        try
        {
            var depth = IndexCommandRunner.MaxScanCheckpointJsonDepth + 4;
            var nestedStart = new string('[', depth);
            var nestedEnd = new string(']', depth);
            File.WriteAllText(
                checkpointPath,
                $$"""{"Version":1,"GitHead":"abc123","Directories":{{nestedStart}}"src"{{nestedEnd}}}""");

            Assert.Empty(IndexCommandRunner.LoadScanCheckpoint(checkpointPath, "abc123"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_NullByteFile_PersistsNullByteIssueWithoutPartialRows()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "binary.cs");
            var bytes = new byte[(3 * 1024 * 1024) + 1];
            var prefix = System.Text.Encoding.UTF8.GetBytes("public class Polluted { public void Run() { } }\n");
            Array.Copy(prefix, bytes, prefix.Length);
            bytes[^1] = 0;
            File.WriteAllBytes(filePath, bytes);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("warnings").GetInt32());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.Equal(0, CountRows(dbPath, "chunks"));
            Assert.Equal(0, CountRows(dbPath, "symbols"));
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));
            var issue = Assert.Single(ReadFileIssues(dbPath, "null_byte"));
            Assert.Equal("binary.cs", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("byte offset", issue.Message);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }


    [Fact]
    public void Run_FileAboveMaxFileBytes_PersistsFileTooLargeIssue()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "large.py");
            File.WriteAllText(filePath, "print('start')\n" + new string('a', 256));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--max-file-bytes", "128", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.Equal(0, CountRows(dbPath, "chunks"));
            Assert.Equal(0, CountRows(dbPath, "symbols"));
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("file_too_large"));
            Assert.Equal("large.py", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("File too large", issue.Message);
            Assert.Contains("--max-file-bytes", issue.Message);
            Assert.Contains(FileIndexer.MaxFileSizeEnvironmentVariable, issue.Message);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FileAboveMaxSymbolsPerFile_PersistsSymbolCountExceededIssueOnly()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "generated.py");
            File.WriteAllText(filePath, string.Join('\n', Enumerable.Range(0, 4).Select(i => $"def f{i}(): pass")));

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--max-symbols-per-file", "10", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--max-symbols-per-file", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.Equal(0, CountRows(dbPath, "chunks"));
            Assert.Equal(0, CountRows(dbPath, "symbols"));
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("symbol_count_exceeded"));
            Assert.Equal("generated.py", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("--max-symbols-per-file", issue.Message);

            var (raisedExitCode, raisedJson) = RunAndCaptureJson([projectRoot, "--max-symbols-per-file", "10", "--json"]);

            Assert.Equal(CommandExitCodes.Success, raisedExitCode);
            Assert.Equal("success", raisedJson.GetProperty("status").GetString());
            Assert.True(CountRows(dbPath, "chunks") > 0);
            Assert.True(CountRows(dbPath, "symbols") > 0);
            Assert.Empty(reader.GetIssues("symbol_count_exceeded"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FileAboveMaxReferencesPerFile_FullScanAndUpdatePersistReferenceCountExceededIssueOnly_Issue3719()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "DenseReferences.cs");
            File.WriteAllText(filePath, BuildDenseReferenceCSharpSource(3));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--max-references-per-file", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.True(CountRows(dbPath, "chunks") > 0);
            Assert.True(CountRows(dbPath, "symbols") > 0);
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("reference_count_exceeded"));
            Assert.Equal("DenseReferences.cs", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("--max-references-per-file", issue.Message);

            var (raisedExitCode, raisedJson) = RunAndCaptureJson([projectRoot, "--max-references-per-file", "10", "--json"]);

            Assert.Equal(CommandExitCodes.Success, raisedExitCode);
            Assert.Equal("success", raisedJson.GetProperty("status").GetString());
            Assert.True(CountRows(dbPath, "symbol_references") > 0);
            Assert.Empty(reader.GetIssues("reference_count_exceeded"));

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", filePath, "--max-references-per-file", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.True(CountRows(dbPath, "chunks") > 0);
            Assert.True(CountRows(dbPath, "symbols") > 0);
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            var updateIssue = Assert.Single(reader.GetIssues("reference_count_exceeded"));
            Assert.Equal("DenseReferences.cs", updateIssue.Path);
            Assert.Contains("--max-references-per-file", updateIssue.Message);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private static string BuildDenseReferenceCSharpSource(int callCount)
    {
        var calls = string.Join('\n', Enumerable.Range(0, callCount).Select(static _ => "            Target.Ping();"));
        return $$"""
namespace DenseReferences;

public static class Target
{
    public static void Ping()
    {
    }
}

public sealed class Caller
{
    public void Run()
    {
{{calls}}
    }
}
""";
    }

    [Fact]
    public void Run_SymbolsOnly_FullScanSkipsReferenceGraphUntilNormalIndex()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { public void Run() { Helper(); } private void Helper() { } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "query.sql"),
                "CREATE TABLE users (id INTEGER PRIMARY KEY);\nSELECT id FROM users;\n");

            var (symbolsOnlyExitCode, symbolsOnlyJson) = RunAndCaptureJson([projectRoot, "--symbols-only", "--json"]);

            Assert.Equal(CommandExitCodes.Success, symbolsOnlyExitCode);
            Assert.Equal("success", symbolsOnlyJson.GetProperty("status").GetString());
            Assert.False(symbolsOnlyJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(symbolsOnlyJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(symbolsOnlyJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(symbolsOnlyJson.GetProperty("hotspot_family_ready").GetBoolean());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(2, CountRows(dbPath, "files"));
            Assert.True(CountRows(dbPath, "chunks") > 0);
            Assert.True(CountRows(dbPath, "symbols") > 0);
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));
            Assert.Equal(0, CountRows(dbPath, "reference_lines"));

            var (normalExitCode, normalJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, normalExitCode);
            Assert.True(normalJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(normalJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(normalJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(CountRows(dbPath, "symbol_references") > 0);
            Assert.True(CountRows(dbPath, "reference_lines") > 0);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_SymbolsOnly_OnGraphReadyDbDemotesReferencesAndSqlContract()
    {
        var projectRoot = CreateTempProject();
        var previousTypeScriptRebuildHook = IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting;
        var rebuiltTypeScriptAugmentation = false;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { public void Run() { Helper(); } private void Helper() { } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "query.sql"),
                "CREATE TABLE users (id INTEGER PRIMARY KEY);\nSELECT id FROM users;\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "types.ts"),
                "interface SharedSymbolsOnly { first: number }\ninterface SharedSymbolsOnly { second: number }\n");

            var (normalExitCode, normalJson) = RunAndCaptureJson([projectRoot, "--json"]);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(CommandExitCodes.Success, normalExitCode);
            Assert.True(normalJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(normalJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(CountRows(dbPath, "symbol_references") > 0);
            Assert.True(CountRows(dbPath, "reference_lines") > 0);
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = () =>
            {
                rebuiltTypeScriptAugmentation = true;
                previousTypeScriptRebuildHook?.Invoke();
            };

            var (symbolsOnlyExitCode, symbolsOnlyJson) = RunAndCaptureJson([projectRoot, "--symbols-only", "--json"]);

            Assert.Equal(CommandExitCodes.Success, symbolsOnlyExitCode);
            Assert.False(symbolsOnlyJson.GetProperty("graph_table_available").GetBoolean());
            Assert.False(symbolsOnlyJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(symbolsOnlyJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));
            Assert.Equal(0, CountRows(dbPath, "reference_lines"));
            Assert.False(rebuiltTypeScriptAugmentation);
            using var verify = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.False(new DbWriter(verify).TypeScriptAugmentationVersionMatchesCurrent());
        }
        finally
        {
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = previousTypeScriptRebuildHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFiles_FileAboveMaxFileBytes_PersistsFileTooLargeIssue()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "large.py");
            File.WriteAllText(filePath, "print('start')\n" + new string('a', 256));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "large.py", "--max-file-bytes", "128", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.Equal(0, CountRows(dbPath, "chunks"));
            Assert.Equal(0, CountRows(dbPath, "symbols"));
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            var issue = Assert.Single(ReadFileIssues(dbPath, "file_too_large"));
            Assert.Equal("large.py", issue.Path);
            Assert.Contains("File too large", issue.Message);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_NewIndexDatabase_RunsAnalyzeAfterSuccessfulIndex()
    {
        var projectRoot = CreateTempProject();
        var commands = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            DbContext.PlannerStatisticsCommandExecutedForTesting = (dataSource, commandText) =>
            {
                if (dataSource.Contains(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), StringComparison.Ordinal))
                    commands.Add(commandText);
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Contains("ANALYZE", commands);
        }
        finally
        {
            DbContext.PlannerStatisticsCommandExecutedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_PlannerStatisticsMaintenanceFailure_AddsLastIndexRunDiagnostic_Issue3718()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            DbContext.PlannerStatisticsCommandCreatedForTesting = command =>
            {
                command.CommandText = "SELECT * FROM cdidx_missing_planner_statistics_table";
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var status = new DbReader(db.Connection).GetStatus();
            Assert.NotNull(status.LastIndexRun);
            var lastIndexRun = status.LastIndexRun!;
            var diagnostic = Assert.Single(lastIndexRun.Diagnostics ?? []);
            Assert.Contains("planner_statistics_maintenance_failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("SELECT * FROM cdidx_missing_planner_statistics_table", diagnostic, StringComparison.Ordinal);
            Assert.Contains(nameof(SqliteException), diagnostic, StringComparison.Ordinal);
            Assert.Equal(1, lastIndexRun.DiagnosticCount);
            Assert.False(lastIndexRun.DiagnosticsTruncated);
        }
        finally
        {
            DbContext.PlannerStatisticsCommandCreatedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_PlannerStatisticsMaintenanceDiagnosticStampFailure_DoesNotFailSuccessfulIndex_Issue3718()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            DbContext.PlannerStatisticsCommandCreatedForTesting = command =>
            {
                command.CommandText = "SELECT * FROM cdidx_missing_planner_statistics_table";
            };
            var stampCalls = 0;
            string[] capturedDiagnostics = [];
            IndexCommandRunner.PlannerStatisticsMaintenanceDiagnosticStampingForTesting = (_, diagnostics) =>
            {
                stampCalls++;
                capturedDiagnostics = diagnostics.ToArray();
                throw new IOException("metadata store became unavailable");
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, stampCalls);
            var diagnostic = Assert.Single(capturedDiagnostics);
            Assert.Contains("planner_statistics_maintenance_failed", diagnostic, StringComparison.Ordinal);
            Assert.Contains("SELECT * FROM cdidx_missing_planner_statistics_table", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            DbContext.PlannerStatisticsCommandCreatedForTesting = null;
            IndexCommandRunner.PlannerStatisticsMaintenanceDiagnosticStampingForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_CancelDuringFreshIndex_ReturnsInterruptedJson()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = (_, _) => cancellation.Cancel();

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions, cancellation);

                    Assert.Equal(CommandExitCodes.Interrupted, exitCode);
                    using var doc = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
                    Assert.Equal(CommandErrorCodes.Interrupted, doc.RootElement.GetProperty("error_code").GetString());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_CancelDuringDryRunScan_ReturnsInterruptedJson()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            cancellation.Cancel();

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var exitCode = IndexCommandRunner.Run([projectRoot, "--dry-run", "--json"], _jsonOptions, cancellation);

                    Assert.Equal(CommandExitCodes.Interrupted, exitCode);
                    using var doc = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
                    Assert.Equal(CommandErrorCodes.Interrupted, doc.RootElement.GetProperty("error_code").GetString());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_CancelBeforeFreshScan_ReturnsInterruptedJson()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            cancellation.Cancel();

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions, cancellation);

                    Assert.Equal(CommandExitCodes.Interrupted, exitCode);
                    using var doc = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
                    Assert.Equal(CommandErrorCodes.Interrupted, doc.RootElement.GetProperty("error_code").GetString());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_CancelAtFtsOptimize_InterruptsAndLeavesRecoverableState_Issue4591()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var cts = new CancellationTokenSource();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('fts cancellation')\n");
            IndexCommandRunner.FullScanFtsOptimizeForTesting = cts.Cancel;

            var (cancelledExitCode, _) = RunAndCaptureJson([projectRoot, "--json"], cts);

            Assert.Equal(CommandExitCodes.Interrupted, cancelledExitCode);
            using (var interruptedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var marker = interruptedDb.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey);
                Assert.True(marker is null or "true", $"Unexpected active-owner FTS marker: {marker}");
            }

            IndexCommandRunner.FullScanFtsOptimizeForTesting = null;
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            using var recoveredDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Null(recoveredDb.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey));
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_CancelAtPlannerMaintenance_StopsWithoutDuplicateResult_Issue4591()
    {
        var projectRoot = CreateTempProject();
        using var cts = new CancellationTokenSource();
        var plannerCallCount = 0;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('planner cancellation')\n");
            DbContext.PlannerStatisticsCommandCreatedForTesting = _ =>
            {
                plannerCallCount++;
                cts.Cancel();
            };

            var (exitCode, _) = RunAndCaptureJson([projectRoot, "--json"], cts);

            Assert.Equal(1, plannerCallCount);
            Assert.Equal(CommandExitCodes.Success, exitCode);
        }
        finally
        {
            DbContext.PlannerStatisticsCommandCreatedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ExistingIndexDatabase_RunsPragmaOptimizeAfterSuccessfulIndex()
    {
        var projectRoot = CreateTempProject();
        var commands = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DbContext.PlannerStatisticsCommandExecutedForTesting = (dataSource, commandText) =>
            {
                if (dataSource.Contains(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), StringComparison.Ordinal))
                    commands.Add(commandText);
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Contains("PRAGMA optimize", commands);
            Assert.DoesNotContain("ANALYZE", commands);
        }
        finally
        {
            DbContext.PlannerStatisticsCommandExecutedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    // `cdidx index . --rebild` should not just say "unknown option"; surface the closest accepted
    // flag (`--rebuild`) so MCP callers can self-correct without re-reading docs (#1582).
    // `cdidx index . --rebild` のような単純なミスタイプから `--rebuild` を提案できることを確認する (#1582)。
    [Fact]
    public void ParseArgs_UnknownIndexOption_SuggestsClosestFlag()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--rebild"]);

        Assert.Contains("unknown option '--rebild'", options.ParseError);
        Assert.Contains("Did you mean: --rebuild?", options.ParseError);
    }

    [Fact]
    public void ParseArgs_UnknownIndexOption_NoSuggestionWhenFarFromAnyFlag()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--zzzzzzzz"]);

        Assert.Contains("unknown option '--zzzzzzzz'", options.ParseError);
        Assert.DoesNotContain("Did you mean:", options.ParseError);
    }

    [Fact]
    public void ParseArgs_UnknownIndexOption_TruncatesOversizedToken()
    {
        var token = "--" + new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 20);

        var options = IndexCommandRunner.ParseArgs([".", token]);

        Assert.Contains("unknown option", options.ParseError);
        Assert.Contains("<truncated; original length", options.ParseError);
        Assert.DoesNotContain(token, options.ParseError);
    }

    [Fact]
    public void ParseArgs_ForceFlag_SetsForce()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--force"]);
        Assert.True(options.Force);
        Assert.NotNull(options.ProjectPath);
        Assert.True(Path.IsPathRooted(options.ProjectPath));
        Assert.Equal(Path.GetFullPath("."), options.ProjectPath);
    }

    [Fact]
    public void TryGetFullScanExtractionStallPath_ReportsActivePhaseAfterTimeout()
    {
        var staleTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;

        var stalled = IndexCommandRunner.TryGetFullScanExtractionStallPath(
            filesProcessed: 23,
            filesTotal: 376,
            timeout: TimeSpan.FromMilliseconds(1),
            lastProgressTimestamp: staleTimestamp,
            currentFile: null,
            activeExtractionPhases: ["src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs (symbols)"],
            out var activePath);

        Assert.True(stalled);
        Assert.Equal("src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs (symbols)", activePath);
    }

    [Fact]
    public void TryGetFullScanExtractionStallPath_DoesNotReportWhenComplete()
    {
        var staleTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;

        var stalled = IndexCommandRunner.TryGetFullScanExtractionStallPath(
            filesProcessed: 376,
            filesTotal: 376,
            timeout: TimeSpan.FromMilliseconds(1),
            lastProgressTimestamp: staleTimestamp,
            currentFile: "src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs",
            activeExtractionPhases: ["src/CodeIndex/Indexer/Symbols/SymbolExtractor.cs (symbols)"],
            out var activePath);

        Assert.False(stalled);
        Assert.Null(activePath);
    }

    [Fact]
    public void ParseArgs_YesFlag_SetsYes()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--rebuild", "--yes"]);
        Assert.True(options.Rebuild);
        Assert.True(options.Yes);
    }

    [Fact]
    public void ParseArgs_NoForceFlag_DefaultsToFalse()
    {
        var options = IndexCommandRunner.ParseArgs(["."]);
        Assert.False(options.Force);
    }

    [Fact]
    public void ParseArgs_SymbolKindFilters_AcceptCommaSeparatedValues()
    {
        var options = IndexCommandRunner.ParseArgs([
            ".",
            "--include-symbol-kind", "class,function",
            "--exclude-symbol-kind=test_method",
        ]);

        Assert.Equal(["class", "function"], options.SymbolKindFilter.Include);
        Assert.Equal(["test_method"], options.SymbolKindFilter.Exclude);
        Assert.Null(options.SymbolKindFilter.ParseError);
    }

    [Theory]
    [InlineData("include=;exclude=", true)]
    [InlineData("include=class,FUNCTION,operator,property;exclude=", true)]
    [InlineData("include=interface,operator,property;exclude=", false)]
    [InlineData("include=;exclude=function", false)]
    [InlineData(null, false)]
    [InlineData("include=;exclude=;malformed", false)]
    public void SymbolKindFilter_ContractMemberRetentionSignatureIsConservative(
        string? signature,
        bool expected)
    {
        Assert.Equal(
            expected,
            SymbolKindFilter.SignatureRetainsCSharpStaticInterfaceContractMembers(signature));
    }

    [Fact]
    public void ParseArgs_SymbolKindFilterRejectsOverlongCsv_Issue2906()
    {
        var tooLong = new string('c', IndexCommandRunner.MaxSymbolKindFilterCsvLength + 1);

        var options = IndexCommandRunner.ParseArgs([".", "--include-symbol-kind", tooLong]);

        Assert.Contains("--include-symbol-kind value is too long", options.SymbolKindFilter.ParseError);
        Assert.Empty(options.SymbolKindFilter.Include);
    }

    [Fact]
    public void ParseArgs_SymbolKindFilterRejectsTooManyCsvEntries_Issue2906()
    {
        var tooMany = TestProjectHelper.RepeatCsvEntry("class", IndexCommandRunner.MaxSymbolKindFilterCsvEntries + 1);

        var options = IndexCommandRunner.ParseArgs([".", "--exclude-symbol-kind", tooMany]);

        Assert.Contains("--exclude-symbol-kind accepts at most", options.SymbolKindFilter.ParseError);
        Assert.Empty(options.SymbolKindFilter.Exclude);
    }

    [Fact]
    public void ParseArgs_SymbolKindEnvironmentFilterRejectsTooManyCsvEntries_Issue2906()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable);
        Environment.SetEnvironmentVariable(
            IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable,
            TestProjectHelper.RepeatCsvEntry("function", IndexCommandRunner.MaxSymbolKindFilterCsvEntries + 1));

        var options = IndexCommandRunner.ParseArgs(["."]);

        Assert.Contains(IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable, options.SymbolKindFilter.ParseError);
        Assert.Contains("accepts at most", options.SymbolKindFilter.ParseError);
        Assert.Empty(options.SymbolKindFilter.Include);
    }

    [Fact]
    public void ParseArgs_SymbolKindCliFilters_ReplaceEnvironmentDefaults()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalInclude = Environment.GetEnvironmentVariable(IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable);
            var originalExclude = Environment.GetEnvironmentVariable(IndexCommandRunner.ExcludeSymbolKindsEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable, "class");
                Environment.SetEnvironmentVariable(IndexCommandRunner.ExcludeSymbolKindsEnvironmentVariable, "test_method");

                var options = IndexCommandRunner.ParseArgs([
                    ".",
                    "--include-symbol-kind", "function",
                    "--exclude-symbol-kind", "generated_parser",
                ]);

                Assert.Equal(["function"], options.SymbolKindFilter.Include);
                Assert.Equal(["generated_parser"], options.SymbolKindFilter.Exclude);
            }
            finally
            {
                Environment.SetEnvironmentVariable(IndexCommandRunner.IncludeSymbolKindsEnvironmentVariable, originalInclude);
                Environment.SetEnvironmentVariable(IndexCommandRunner.ExcludeSymbolKindsEnvironmentVariable, originalExclude);
            }
        }
    }













    [Fact]
    public void Run_FreshAndRebuildWithoutTypeScript_SkipTypeScriptAugmentationRebuild()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fresh_no_ts_augmentation");
        var dbPath = CreateTempDbPath("cdidx_fresh_no_ts_augmentation");
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var rebuiltTypeScriptAugmentation = false;
        var refreshCount = 0;
        var foldBackfillVerifications = 0;
        var foldValueVerifications = 0;
        var languagePresenceChecks = 0;
        var indexedLanguageReads = 0;
        var statReuseLookups = 0;
        var reusableLookups = 0;
        var countReads = 0;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 1\n");

            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };
            DbWriter.FoldBackfillVerificationForTesting = () => foldBackfillVerifications++;
            DbWriter.FoldValueVerificationForTesting = () => foldValueVerifications++;
            DbWriter.LanguagePresenceCheckForTesting = _ => languagePresenceChecks++;
            DbWriter.IndexedLanguagesReadForTesting = () => indexedLanguageReads++;
            DbWriter.ReusableUnchangedFileLookupForTesting = _ => reusableLookups++;
            DbWriter.CountsReadForTesting = () => countReads++;
            IndexedFileStatReuse.LookupForTesting = _ => statReuseLookups++;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(rebuiltTypeScriptAugmentation);
            Assert.Equal(1, refreshCount);
            Assert.Equal(1, foldBackfillVerifications);
            Assert.Equal(0, foldValueVerifications);
            Assert.Equal(0, languagePresenceChecks);
            Assert.Equal(0, indexedLanguageReads);
            Assert.Equal(0, statReuseLookups);
            Assert.Equal(0, reusableLookups);
            Assert.Equal(0, countReads);
            Assert.Equal(2, json.GetProperty("summary").GetProperty("files_total").GetInt64());

            refreshCount = 0;
            foldValueVerifications = 0;
            var (rebuildExitCode, rebuildJson) = RunAndCaptureJson([
                projectRoot,
                "--db",
                dbPath,
                "--rebuild",
                "--yes",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, rebuildExitCode);
            Assert.Equal("success", rebuildJson.GetProperty("status").GetString());
            Assert.False(rebuiltTypeScriptAugmentation);
            Assert.Equal(1, refreshCount);
            Assert.Equal(1, foldValueVerifications);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                DbContext.TypeScriptAugmentationVersion.ToString(CultureInfo.InvariantCulture),
                db.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
        }
        finally
        {
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = null;
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DbWriter.FoldBackfillVerificationForTesting = null;
            DbWriter.FoldValueVerificationForTesting = null;
            DbWriter.LanguagePresenceCheckForTesting = null;
            DbWriter.IndexedLanguagesReadForTesting = null;
            DbWriter.ReusableUnchangedFileLookupForTesting = null;
            DbWriter.CountsReadForTesting = null;
            IndexedFileStatReuse.LookupForTesting = null;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_StaleTypeScriptMarkerWithoutAugmentationEdges_SkipsGraphRefresh()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_marker_only_refresh");
        var dbPath = CreateTempDbPath("cdidx_ts_marker_only_refresh");
        var previousAugmentationHook =
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting;
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var augmentationRebuildCount = 0;
        var refreshCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "contract.ts"),
                "interface SingletonContract { value: number }\n");
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = () =>
            {
                augmentationRebuildCount++;
                previousAugmentationHook?.Invoke();
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };

            var (initialExitCode, initialJson) = RunAndCaptureJson(
                [projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal("success", initialJson.GetProperty("status").GetString());
            Assert.Equal(1, augmentationRebuildCount);
            Assert.Equal(1, refreshCount);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db).ClearTypeScriptAugmentationReady();
            augmentationRebuildCount = 0;
            refreshCount = 0;

            var (refreshExitCode, refreshJson) = RunAndCaptureJson(
                [projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal("success", refreshJson.GetProperty("status").GetString());
            Assert.Equal(1, augmentationRebuildCount);
            Assert.Equal(0, refreshCount);
            using var completedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                DbContext.TypeScriptAugmentationVersion.ToString(CultureInfo.InvariantCulture),
                completedDb.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
        }
        finally
        {
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting =
                previousAugmentationHook;
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_StaleMergedTypeScriptMarker_AttributesGraphMemoryAfterTextIndex()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_marker_graph_memory");
        var dbPath = CreateTempDbPath("cdidx_ts_marker_graph_memory");
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var refreshCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "contract-a.ts"),
                "interface SharedContract { first: number }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "contract-b.ts"),
                "interface SharedContract { second: number }\n");

            var (initialExitCode, _) = RunAndCaptureJson(
                [projectRoot, "--db", dbPath, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db).ClearTypeScriptAugmentationReady();
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([
                projectRoot,
                "--db",
                dbPath,
                "--memory-trace",
                "--json",
                "--quiet",
            ]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal("success", refreshJson.GetProperty("status").GetString());
            Assert.Equal(1, refreshCount);
            var phases = refreshJson
                .GetProperty("memory_timeline")
                .GetProperty("samples")
                .EnumerateArray()
                .Select(sample => sample.GetProperty("phase").GetString())
                .ToArray();
            Assert.Contains("text_index", phases);
            Assert.Contains("reference_graph", phases);
            Assert.Equal(1, phases.Count(phase => phase == "reference_graph"));
            Assert.True(
                Array.IndexOf(phases, "text_index") < Array.IndexOf(phases, "reference_graph"));
        }
        finally
        {
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            TestProjectHelper.DeleteSqliteDatabaseFiles(dbPath);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_NoOpFullScan_DoesNotOptimizeFts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_noop_fullscan_no_fts_optimize");
        var optimized = false;
        var resolvedMetadataTargets = false;
        var rebuiltTypeScriptAugmentation = false;
        var startedExtractionWork = false;
        bool? parallelized = null;
        string? parallelizeReason = null;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "interface AppApi { run(): void; }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () => optimized = true;
            IndexCommandRunner.FullScanCSharpMetadataResolveForTesting = () => resolvedMetadataTargets = true;
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;
            IndexCommandRunner.FullScanExtractionWorkStartedForTesting = () => startedExtractionWork = true;
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, reason) =>
            {
                parallelized = enabled;
                parallelizeReason = reason;
            };

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal("success", refreshJson.GetProperty("status").GetString());
            Assert.Equal(2, refreshJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.False(parallelized);
            Assert.Null(parallelizeReason);
            Assert.False(startedExtractionWork);
            Assert.False(optimized);
            Assert.False(resolvedMetadataTargets);
            Assert.False(rebuiltTypeScriptAugmentation);
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = null;
            IndexCommandRunner.FullScanCSharpMetadataResolveForTesting = null;
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = null;
            IndexCommandRunner.FullScanExtractionWorkStartedForTesting = null;
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_ScopesTypeScriptAugmentationToDirtyNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_ts_augmentation_dirty_names");
        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        var refreshCount = 0;
        try
        {
            var changedPath = Path.Combine(projectRoot, "changed.ts");
            File.WriteAllText(changedPath, "interface OldMerge { changed: number }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "peer.ts"),
                "interface OldMerge { oldPeer: number }\ninterface NewMerge { newPeer: number }\n");
            var singletonSource = new StringBuilder();
            for (var index = 0; index < 1_000; index++)
                singletonSource.Append("interface Unchanged").Append(index).Append(" { value: number }\n");
            File.WriteAllText(Path.Combine(projectRoot, "singletons.ts"), singletonSource.ToString());

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(changedPath, "interface NewMerge { changed: number }\n");
            File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(2));
            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.NotNull(groupingStats);
            Assert.Equal(3, groupingStats!.DeclarationCount);
            Assert.Equal(2, groupingStats.GroupCount);
            Assert.Equal(1, groupingStats.MergedGroupCount);
            Assert.Equal(2, groupingStats.ScopedNameCount);
            Assert.Equal(1, refreshCount);
        }
        finally
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_TypeScriptToCSharpLanguageTransitionRemovesAugmentation()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_ts_language_transition");
        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        try
        {
            var changedPath = Path.Combine(projectRoot, "changed.cs");
            File.WriteAllText(changedPath, "public interface SharedTransition { int Changed { get; } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "peer.ts"),
                "interface SharedTransition { peer: number }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(
                2,
                TestProjectHelper.ReclassifyIndexedFileAsTypeScriptAndRebuildAugmentations(
                    dbPath,
                    projectRoot,
                    "changed.cs"));

            File.WriteAllText(changedPath, "public class Changed { }\n");
            File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(2));
            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.NotNull(groupingStats);
            Assert.Equal(1, groupingStats!.DeclarationCount);
            Assert.Equal(1, groupingStats.ScopedNameCount);
            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_DefersIncrementalFtsMergeUntilWriteThreshold()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_threshold");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var sourcePath = Path.Combine(projectRoot, "app.py");
        var stablePath = Path.Combine(projectRoot, "stable.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var previousMergeHook = IndexCommandRunner.FullScanFtsMergeForTesting;
        var optimizeCount = 0;
        var mergeCount = 0;
        try
        {
            File.WriteAllText(sourcePath, "def run():\n    return 1\n");
            File.WriteAllText(stablePath, "# " + new string('s', 1_000) + "\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            IndexCommandRunner.FullScanFtsMergeForTesting = () =>
            {
                mergeCount++;
                previousMergeHook?.Invoke();
            };
            File.WriteAllText(sourcePath, "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (deferredExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, deferredExitCode);
            Assert.Equal(0, optimizeCount);
            Assert.Equal(0, mergeCount);
            using (var deferredDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var deferredWriter = new DbWriter(deferredDb);
                Assert.Equal(1, deferredWriter.GetFtsIncrementalWritesSinceOptimize());
                Assert.Equal(1, deferredWriter.GetFtsIncrementalWritesSinceMerge());
            }

            using (var thresholdDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                new DbWriter(thresholdDb).SetMeta(
                    DbWriter.FtsIncrementalWritesSinceMergeMetaKey,
                    (DbWriter.DefaultFtsMergeIncrementalWriteThreshold - 1).ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllText(sourcePath, "def run():\n    return 3\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(4));

            var (mergedExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, mergedExitCode);
            Assert.Equal(0, optimizeCount);
            Assert.Equal(1, mergeCount);
            using var mergedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var mergedWriter = new DbWriter(mergedDb);
            Assert.Equal(2, mergedWriter.GetFtsIncrementalWritesSinceOptimize());
            Assert.Equal(0, mergedWriter.GetFtsIncrementalWritesSinceMerge());
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            IndexCommandRunner.FullScanFtsMergeForTesting = previousMergeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_UsesBulkFtsAtThreeFifthsDirtyBytes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_bulk_boundary");
        var dirtyPath = Path.Combine(projectRoot, "dirty.py");
        var stablePath = Path.Combine(projectRoot, "stable.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var previousMergeHook = IndexCommandRunner.FullScanFtsMergeForTesting;
        var optimizeCount = 0;
        var mergeCount = 0;
        static string SizedSource(char fill, int size)
            => "# " + new string(fill, size - 3) + "\n";
        try
        {
            File.WriteAllText(dirtyPath, SizedSource('a', 600));
            File.WriteAllText(stablePath, SizedSource('s', 400));
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            IndexCommandRunner.FullScanFtsMergeForTesting = () =>
            {
                mergeCount++;
                previousMergeHook?.Invoke();
            };
            File.WriteAllText(dirtyPath, SizedSource('b', 600));
            File.SetLastWriteTimeUtc(dirtyPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.Equal(1, optimizeCount);
            Assert.Equal(0, mergeCount);
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            IndexCommandRunner.FullScanFtsMergeForTesting = previousMergeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(600, 400, true)]
    [InlineData(599, 401, false)]
    public void Run_IncrementalFullScan_UsesOldSizeForShrinkingFileDirtyByteBoundary(
        int oldDirtySize,
        int stableSize,
        bool expectBulk)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_shrink_boundary");
        var dirtyPath = Path.Combine(projectRoot, "dirty.py");
        var stablePath = Path.Combine(projectRoot, "stable.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var optimizeCount = 0;
        static string SizedSource(char fill, int size)
            => "# " + new string(fill, size - 3) + "\n";
        try
        {
            File.WriteAllText(dirtyPath, SizedSource('a', oldDirtySize));
            File.WriteAllText(stablePath, SizedSource('s', stableSize));
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            File.WriteAllText(dirtyPath, SizedSource('b', 100));
            File.SetLastWriteTimeUtc(dirtyPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.Equal(expectBulk ? 1 : 0, optimizeCount);
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_RolledBackBulkCandidateDoesNotRebuildOrOptimizeFts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_rollback_only");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var dirtyPath = Path.Combine(projectRoot, "dirty.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var previousFilePhaseHook = IndexCommandRunner.FullScanFilePhaseForTesting;
        var optimizeCount = 0;
        var symbolPhaseCalls = 0;
        static string SizedSource(string token, char fill, int size)
        {
            var prefix = $"# {token} ";
            return prefix + new string(fill, size - prefix.Length - 1) + "\n";
        }
        try
        {
            File.WriteAllText(dirtyPath, SizedSource("rollback_old_token", 'a', 600));
            File.WriteAllText(Path.Combine(projectRoot, "stable.py"), SizedSource("rollback_stable_token", 's', 400));
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
                    {
                        optimizeCount++;
                        previousOptimizeHook?.Invoke();
                    };
                    IndexCommandRunner.FullScanFilePhaseForTesting = (path, phase) =>
                    {
                        previousFilePhaseHook?.Invoke(path, phase);
                        if (path == "dirty.py"
                            && phase == "symbols"
                            && Interlocked.Increment(ref symbolPhaseCalls) == 2)
                        {
                            throw new InvalidOperationException("Simulated post-upsert symbol extraction failure.");
                        }
                    };
                    File.WriteAllText(dirtyPath, SizedSource("rollback_new_token", 'b', 600));
                    File.SetLastWriteTimeUtc(dirtyPath, DateTime.UtcNow.AddSeconds(2));

                    var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
                    Assert.Equal(2, symbolPhaseCalls);
                    Assert.Equal(0, optimizeCount);
                }
                finally
                {
                    IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
                    IndexCommandRunner.FullScanFilePhaseForTesting = previousFilePhaseHook;
                }
            }

            using var verificationDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(verificationDb);
            Assert.Equal(0, writer.GetFtsIncrementalWritesSinceOptimize());
            Assert.Equal(0, writer.GetFtsIncrementalWritesSinceMerge());
            using var command = verificationDb.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'rollback_old_token'";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'rollback_new_token'";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            IndexCommandRunner.FullScanFilePhaseForTesting = previousFilePhaseHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(600, 400, true, false)]
    [InlineData(599, 401, false, false)]
    [InlineData(600, 400, true, true)]
    public void Run_IncrementalFullScan_AccountsForDeletedAndRenamedBytesBeforeFtsPurge(
        int removedSize,
        int retainedSize,
        bool expectBulk,
        bool rename)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_delete_boundary");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var removedPath = Path.Combine(projectRoot, "removed.py");
        var retainedPath = Path.Combine(projectRoot, "retained.py");
        var renamedPath = Path.Combine(projectRoot, "renamed.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var previousPurgeHook = IndexCommandRunner.FullScanStaleFilePurgeForTesting;
        var previousReferencePurgeHook = IndexCommandRunner.FullScanReferencePurgeForTesting;
        var optimizeCount = 0;
        var purgeBulkStates = new List<bool>();
        var purgeOrder = new List<string>();
        static string SizedSource(string token, char fill, int size)
        {
            var prefix = $"# {token} ";
            return prefix + new string(fill, size - prefix.Length - 1) + "\n";
        }
        try
        {
            File.WriteAllText(removedPath, SizedSource("removed_boundary_token", 'r', removedSize));
            File.WriteAllText(retainedPath, SizedSource("retained_boundary_token", 's', retainedSize));
            Assert.Equal(removedSize, new FileInfo(removedPath).Length);
            Assert.Equal(retainedSize, new FileInfo(retainedPath).Length);
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            IndexCommandRunner.FullScanStaleFilePurgeForTesting = bulkEnabled =>
            {
                purgeOrder.Add("stale_files");
                purgeBulkStates.Add(bulkEnabled);
                previousPurgeHook?.Invoke(bulkEnabled);
            };
            IndexCommandRunner.FullScanReferencePurgeForTesting = () =>
            {
                purgeOrder.Add("references");
                previousReferencePurgeHook?.Invoke();
            };
            if (rename)
                File.Move(removedPath, renamedPath);
            else
                File.Delete(removedPath);

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(new[] { expectBulk }, purgeBulkStates);
            Assert.Equal(new[] { "stale_files", "references" }, purgeOrder);
            Assert.Equal(expectBulk ? 1 : 0, optimizeCount);
            using var verificationDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(verificationDb);
            Assert.Equal(expectBulk ? 0 : 1, writer.GetFtsIncrementalWritesSinceOptimize());
            Assert.Equal(expectBulk ? 0 : 1, writer.GetFtsIncrementalWritesSinceMerge());
            using var command = verificationDb.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE path = 'removed.py'";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT COUNT(*) FROM files WHERE path = 'renamed.py'";
            Assert.Equal(rename ? 1L : 0L, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'removed_boundary_token'";
            Assert.Equal(rename ? 1L : 0L, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'retained_boundary_token'";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('fts_chunks_ai', 'fts_chunks_ad', 'fts_chunks_au')";
            Assert.Equal(3L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            IndexCommandRunner.FullScanStaleFilePurgeForTesting = previousPurgeHook;
            IndexCommandRunner.FullScanReferencePurgeForTesting = previousReferencePurgeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_CombinesDeletedAndModifiedBytesAtBulkBoundary()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_combined_boundary");
        var deletedPath = Path.Combine(projectRoot, "deleted.py");
        var modifiedPath = Path.Combine(projectRoot, "modified.py");
        var stablePath = Path.Combine(projectRoot, "stable.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var previousPurgeHook = IndexCommandRunner.FullScanStaleFilePurgeForTesting;
        var optimizeCount = 0;
        var purgeBulkEnabled = false;
        static string SizedSource(char fill, int size)
            => "# " + new string(fill, size - 3) + "\n";
        try
        {
            File.WriteAllText(deletedPath, SizedSource('d', 500));
            File.WriteAllText(modifiedPath, SizedSource('m', 100));
            File.WriteAllText(stablePath, SizedSource('s', 400));
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            IndexCommandRunner.FullScanStaleFilePurgeForTesting = bulkEnabled =>
            {
                purgeBulkEnabled = bulkEnabled;
                previousPurgeHook?.Invoke(bulkEnabled);
            };
            File.Delete(deletedPath);
            File.WriteAllText(modifiedPath, SizedSource('n', 100));
            File.SetLastWriteTimeUtc(modifiedPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.True(purgeBulkEnabled);
            Assert.Equal(1, optimizeCount);
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            IndexCommandRunner.FullScanStaleFilePurgeForTesting = previousPurgeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IncrementalFullScan_InvalidDeletedSizeFallsBackBeforeFtsPurge()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_incremental_fullscan_fts_invalid_deleted_size");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var deletedPath = Path.Combine(projectRoot, "deleted.py");
        var stablePath = Path.Combine(projectRoot, "stable.py");
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var previousPurgeHook = IndexCommandRunner.FullScanStaleFilePurgeForTesting;
        var optimizeCount = 0;
        var purgeBulkStates = new List<bool>();
        static string SizedSource(char fill, int size)
            => "# " + new string(fill, size - 3) + "\n";
        try
        {
            File.WriteAllText(deletedPath, SizedSource('d', 600));
            File.WriteAllText(stablePath, SizedSource('s', 400));
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            using (var corrupt = db.Connection.CreateCommand())
            {
                corrupt.CommandText = "UPDATE files SET size = 'invalid' WHERE path = 'deleted.py'";
                Assert.Equal(1, corrupt.ExecuteNonQuery());
            }

            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            IndexCommandRunner.FullScanStaleFilePurgeForTesting = bulkEnabled =>
            {
                purgeBulkStates.Add(bulkEnabled);
                previousPurgeHook?.Invoke(bulkEnabled);
            };
            File.Delete(deletedPath);

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(new[] { false }, purgeBulkStates);
            Assert.Equal(0, optimizeCount);
            using var verificationDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(verificationDb);
            Assert.Equal(1, writer.GetFtsIncrementalWritesSinceOptimize());
            Assert.Equal(1, writer.GetFtsIncrementalWritesSinceMerge());
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            IndexCommandRunner.FullScanStaleFilePurgeForTesting = previousPurgeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FilesUpdate_ReportsIncrementalFtsMergeAndPreservesOptimizeRecommendationCounter()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_files_update_fts_merge");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var sourcePath = Path.Combine(projectRoot, "app.py");
        try
        {
            File.WriteAllText(sourcePath, "def run():\n    return 1\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db);
                writer.SetMeta(
                    DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey,
                    (DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold - 1).ToString(CultureInfo.InvariantCulture));
                writer.SetMeta(
                    DbWriter.FtsIncrementalWritesSinceMergeMetaKey,
                    (DbWriter.DefaultFtsMergeIncrementalWriteThreshold - 1).ToString(CultureInfo.InvariantCulture));
            }

            File.WriteAllText(sourcePath, "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--db", dbPath, "--files", sourcePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            var summary = updateJson.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("updated").GetInt32());
            Assert.False(summary.GetProperty("fts_optimize_ran").GetBoolean());
            Assert.True(summary.GetProperty("fts_merge_ran").GetBoolean());
            using (var verificationDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var verificationWriter = new DbWriter(verificationDb);
                Assert.Equal(DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold, verificationWriter.GetFtsIncrementalWritesSinceOptimize());
                Assert.Equal(0, verificationWriter.GetFtsIncrementalWritesSinceMerge());
            }

            SqliteConnection.ClearAllPools();
            int previewExitCode;
            JsonElement previewJson;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    previewExitCode = IndexCommandRunner.RunOptimizeFts(
                        ["--db", dbPath, "--dry-run", "--json"],
                        _jsonOptions,
                        forceLogicalObjectSizeFallbackForTesting: true);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    previewJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, previewExitCode);
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                previewJson.GetProperty("writes_since_optimize_before").GetInt32());
            Assert.True(previewJson.GetProperty("optimization_recommended").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_NoOpUpdate_DoesNotStartExtractionWork()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_noop_update_no_extraction_work");
        var startedExtractionWork = false;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 1\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = () => startedExtractionWork = true;

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "tool.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.False(startedExtractionWork);
        }
        finally
        {
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_NotifyFlag_ParsesCompletionNotificationMode()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--notify=osc9"]);

        Assert.Equal(CompletionNotificationMode.Osc9, options.NotifyMode);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_InvalidNotifyEnvironmentWarnsAndFallsBack_Issue3135()
    {
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.CompletionNotificationEnvironmentVariable);
            var originalErr = Console.Error;
            using var stderr = new StringWriter();
            var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);
            try
            {
                env.Set(IndexCommandRunner.CompletionNotificationEnvironmentVariable, value);
                Console.SetError(stderr);

                var options = IndexCommandRunner.ParseArgs(["."]);

                Assert.Equal(CompletionNotificationMode.Auto, options.NotifyMode);
                Assert.Null(options.ParseError);
                var warning = stderr.ToString();
                Assert.Contains($"invalid {IndexCommandRunner.CompletionNotificationEnvironmentVariable} value", warning);
                Assert.Contains("ignored; use auto, bell, osc9, desktop, or none", warning);
                Assert.Contains("<truncated; original length", warning);
                Assert.DoesNotContain(value, warning);
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
    }


    [Fact]
    public void Run_Help_IncludesSymbolKindFilterFlags()
    {
        var (exitCode, stdout, stderr) = RunAndCaptureStreams(["--help"]);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains("--include-symbol-kind <kind>[,<kind>]", stdout);
        Assert.Contains("--exclude-symbol-kind <kind>[,<kind>]", stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void Run_UnresolvedMergeState_RejectsIndexingBeforeScanning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_unresolved_merge");
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "class Program { void Base() {} }\n");
            RunGit(projectRoot, "add", "Program.cs");
            RunGit(projectRoot, "commit", "-m", "initial");
            var defaultBranch = RunGitCaptureStdOut(projectRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            RunGit(projectRoot, "switch", "-c", "feature");
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "class Program { void Feature() {} }\n");
            RunGit(projectRoot, "commit", "-am", "feature");
            RunGit(projectRoot, "switch", defaultBranch);
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "class Program { void Mainline() {} }\n");
            RunGit(projectRoot, "commit", "-am", "mainline");

            Assert.Throws<InvalidOperationException>(() => RunGit(projectRoot, "merge", "feature"));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("unresolved merge conflicts", json.GetProperty("message").GetString());
            Assert.Contains("Program.cs", json.GetProperty("message").GetString());
            Assert.Equal(CommandErrorCodes.UsageError, json.GetProperty("error_code").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_RebuildWithoutConfirmationOnNonTty_ReturnsExUsage()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            IndexCommandRunner.IsInputRedirectedForTesting = () => true;
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--json"]);

            Assert.Equal(CommandExitCodes.ExUsage, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("Pass --yes", json.GetProperty("message").GetString());
            Assert.Contains("--files", json.GetProperty("hint").GetString());
        }
        finally
        {
            IndexCommandRunner.IsInputRedirectedForTesting = () => Console.IsInputRedirected;
            IndexCommandRunner.ReadLineForTesting = Console.ReadLine;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_RebuildWithYesOnNonTty_Succeeds()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            IndexCommandRunner.IsInputRedirectedForTesting = () => true;
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("rebuild", json.GetProperty("mode").GetString());
        }
        finally
        {
            IndexCommandRunner.IsInputRedirectedForTesting = () => Console.IsInputRedirected;
            IndexCommandRunner.ReadLineForTesting = Console.ReadLine;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_ProjectFilterExpandsToProjectFiles_Issue1707()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_index_project_filter");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Lib"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Other"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Lib", "Ignored"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Lib", "bin", "Debug"));
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "src/Lib/Ignored/\n");
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Other", "src\Other\Other.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Lib", "Class1.cs"), "class Class1 {}");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Lib", "Ignored", "Ignored.cs"), "class Ignored {}");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Lib", "bin", "Debug", "Generated.cs"), "class Generated {}");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Other", "Other.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Other", "Class2.cs"), "class Class2 {}");

            var options = IndexCommandRunner.ParseArgs([projectRoot, "--solution", "Repo.sln", "--project", "Lib"]);

            Assert.Equal(["Lib"], options.ProjectFilters);
            Assert.Equal("Repo.sln", options.SolutionPath);
            Assert.Contains("src/Lib/Lib.csproj", options.UpdateFiles);
            Assert.Contains("src/Lib/Class1.cs", options.UpdateFiles);
            Assert.DoesNotContain("src/Lib/Ignored/Ignored.cs", options.UpdateFiles);
            Assert.DoesNotContain("src/Lib/bin/Debug/Generated.cs", options.UpdateFiles);
            Assert.DoesNotContain("src/Other/Class2.cs", options.UpdateFiles);
            Assert.False(options.ExplicitFilesSpecified);
            Assert.NotNull(options.ExplicitFiles);
            Assert.Empty(options.ExplicitFileInputs);

            var mixedOptions = IndexCommandRunner.ParseArgs(
                [projectRoot, "--files", "manual.cs", "--solution", "Repo.sln", "--project", "Lib"]);

            Assert.True(mixedOptions.ExplicitFilesSpecified);
            Assert.Equal(["manual.cs"], mixedOptions.ExplicitFiles);
            Assert.Equal(["manual.cs"], mixedOptions.ExplicitFileInputs);
            Assert.Contains("manual.cs", mixedOptions.UpdateFiles);
            Assert.Contains("src/Lib/Class1.cs", mixedOptions.UpdateFiles);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_FilesFlag_RejectsEachEmptyOccurrenceAndRetainsProvenance()
    {
        var bare = IndexCommandRunner.ParseArgs([".", "--files"]);
        var repeatedWithEmptyOccurrence = IndexCommandRunner.ParseArgs(
            [".", "--files", "first.cs", "--files", "--json"]);

        Assert.True(bare.ExplicitFilesSpecified);
        Assert.NotNull(bare.ExplicitFiles);
        Assert.Empty(bare.ExplicitFileInputs);
        Assert.Contains("--files requires at least one file path", bare.ParseError);

        Assert.True(repeatedWithEmptyOccurrence.ExplicitFilesSpecified);
        Assert.Equal(["first.cs"], repeatedWithEmptyOccurrence.ExplicitFiles);
        Assert.Equal(["first.cs"], repeatedWithEmptyOccurrence.ExplicitFileInputs);
        Assert.Equal(["first.cs"], repeatedWithEmptyOccurrence.UpdateFiles);
        Assert.Contains("--files requires at least one file path", repeatedWithEmptyOccurrence.ParseError);
    }

    [Fact]
    public void IndexCommandOptions_ProgrammaticUpdateFilesRemainExplicitInputFallback()
    {
        var options = new IndexCommandOptions
        {
            UpdateFiles = ["legacy.cs"],
        };

        Assert.False(options.ExplicitFilesSpecified);
        Assert.Null(options.ExplicitFiles);
        Assert.Equal(["legacy.cs"], options.ExplicitFileInputs);
    }

    [Fact]
    public void ResolveProjects_SkipsIgnoredAndDefaultExcludedProjectDirectories_Issue2862()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_index_project_filter_discovery");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "ignored", "Hidden"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "node_modules", "Package"));
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "ignored/\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "ignored", "Hidden", "Hidden.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(projectRoot, "node_modules", "Package", "Package.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var projects = SolutionProjectResolver.ResolveProjects(projectRoot);

            Assert.Contains(projects, project => project.ProjectPath == "src/App/App.csproj");
            Assert.DoesNotContain(projects, project => project.ProjectPath == "ignored/Hidden/Hidden.csproj");
            Assert.DoesNotContain(projects, project => project.ProjectPath == "node_modules/Package/Package.csproj");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_SkipsFallbackTraversalDirectoryErrors_Issue3214()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_fallback_directory_error");
        var lockedDirectory = Path.Combine(projectRoot, "locked");
        var restoreLockedDirectory = false;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            Directory.CreateDirectory(lockedDirectory);
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            try
            {
                File.SetUnixFileMode(lockedDirectory, UnixFileMode.None);
                restoreLockedDirectory = true;
                _ = Directory.EnumerateFiles(lockedDirectory).ToList();
                return;
            }
            catch (Exception permissionEx) when (permissionEx is UnauthorizedAccessException or IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }

            var diagnostics = new List<string>();
            var projects = SolutionProjectResolver.ResolveProjects(
                projectRoot,
                solutionPath: null,
                SolutionProjectResolverLimits.Default,
                diagnostics);

            Assert.Contains(projects, project => project.ProjectPath == "src/App/App.csproj");
            Assert.Contains(diagnostics, diagnostic => diagnostic.Contains("locked", StringComparison.Ordinal)
                && diagnostic.Contains("permissions", StringComparison.Ordinal));

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjectFiles(projectRoot, ["Missing"]));
            Assert.Contains("Traversal diagnostics:", ex.Message);
            Assert.Contains("locked", ex.Message);
        }
        finally
        {
            if (restoreLockedDirectory)
                File.SetUnixFileMode(lockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_SkipsAutomaticSolutionDiscoveryFilesystemErrors_Issue3513()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_auto_discovery_error");
        var lockedRoot = Path.Combine(projectRoot, "locked-root");
        var restoreLockedRoot = false;
        try
        {
            Directory.CreateDirectory(lockedRoot);
            try
            {
                File.SetUnixFileMode(lockedRoot, UnixFileMode.None);
                restoreLockedRoot = true;
                _ = Directory.EnumerateFiles(lockedRoot, "*.sln", SearchOption.TopDirectoryOnly).ToList();
                return;
            }
            catch (Exception permissionEx) when (permissionEx is UnauthorizedAccessException or IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }

            var diagnostics = new List<string>();
            var projects = SolutionProjectResolver.ResolveProjects(
                lockedRoot,
                solutionPath: null,
                SolutionProjectResolverLimits.Default,
                diagnostics);

            Assert.Empty(projects);
            Assert.Contains(
                diagnostics,
                diagnostic => diagnostic.Contains("solution files", StringComparison.Ordinal)
                    && diagnostic.Contains("permissions", StringComparison.Ordinal)
                    && diagnostic.Contains("pass --solution <path>", StringComparison.Ordinal));
        }
        finally
        {
            if (restoreLockedRoot)
                File.SetUnixFileMode(lockedRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_SkipsSolutionProjectsOutsideWorkspaceRoot_Issue3063()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_outside_root");
        var externalRoot = Path.Combine(Path.GetDirectoryName(projectRoot)!, Path.GetFileName(projectRoot) + "_external");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            Directory.CreateDirectory(externalRoot);
            var externalProject = Path.Combine(externalRoot, "External.csproj");
            var externalProjectReference = Path.GetRelativePath(projectRoot, externalProject).Replace('/', '\\');
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), $$"""
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "External", "{{externalProjectReference}}", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(externalProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var projects = SolutionProjectResolver.ResolveProjects(projectRoot, "Repo.sln");

            Assert.Contains(projects, project => project.ProjectPath == "src/App/App.csproj");
            Assert.DoesNotContain(projects, project => project.Name == "External");
            Assert.DoesNotContain(projects, project => project.ProjectPath.Contains("..", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsOversizedSolutionFile_Issue3064()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_size_limit");
        try
        {
            var solutionPath = Path.Combine(projectRoot, "Repo.sln");
            using (var stream = new FileStream(solutionPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(SolutionProjectResolver.MaxSolutionFileBytes + 1);

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, "Repo.sln"));

            Assert.Contains("solution file is too large", ex.Message);
            Assert.Contains(SolutionProjectResolver.MaxSolutionFileBytes.ToString(), ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsOverlongSolutionLine_Issue3064()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_line_limit");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Repo.sln"),
                new string('x', SolutionProjectResolver.MaxSolutionLineChars + 1));

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, "Repo.sln"));

            Assert.Contains("solution line is too long", ex.Message);
            Assert.Contains(":1", ex.Message);
            Assert.Contains(SolutionProjectResolver.MaxSolutionLineChars.ToString(), ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsTooManySolutionLines_Issue3706()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_line_count_limit");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Repo.sln"),
                string.Concat(Enumerable.Repeat("# comment\n", SolutionProjectResolver.MaxSolutionFileLines + 1)));

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, "Repo.sln"));

            Assert.Contains("solution file contains too many lines", ex.Message);
            Assert.Contains(SolutionProjectResolver.MaxSolutionFileLines.ToString(CultureInfo.InvariantCulture), ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsTooManySolutionProjectReferences_Issue3064()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_project_limit");
        try
        {
            var lines = Enumerable.Range(0, SolutionProjectResolver.MaxSolutionProjectReferences + 1)
                .Select(i => $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"P{i}\", \"src\\P{i}\\P{i}.csproj\", \"{{11111111-1111-1111-1111-111111111111}}\"");
            File.WriteAllLines(Path.Combine(projectRoot, "Repo.sln"), lines);

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, "Repo.sln"));

            Assert.Contains("solution contains too many .NET project references", ex.Message);
            Assert.Contains(SolutionProjectResolver.MaxSolutionProjectReferences.ToString(), ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsTooManyAutomaticSolutionCandidates_Issue3065()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_candidate_limit");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "A.sln"), string.Empty);
            File.WriteAllText(Path.Combine(projectRoot, "B.sln"), string.Empty);
            File.WriteAllText(Path.Combine(projectRoot, "C.sln"), string.Empty);
            var limits = SolutionProjectResolverLimits.Default with { MaxAutomaticSolutionCandidates = 2 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, solutionPath: null, limits));

            Assert.Contains("automatic solution discovery found more than 2 .sln files", ex.Message);
            Assert.Contains("pass --solution <path>", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsFallbackDiscoveryDirectoryTraversalLimit_Issue3213()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_fallback_directory_limit");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var limits = SolutionProjectResolverLimits.Default with { MaxFallbackDiscoveryDirectories = 1 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, solutionPath: null, limits));

            Assert.Contains("fallback project discovery traversed more than 1 directories", ex.Message);
            Assert.Contains("pass --solution <path>", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsFallbackDiscoveryFileTraversalLimit_Issue3213()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_fallback_file_limit");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "A.txt"), "a");
            File.WriteAllText(Path.Combine(projectRoot, "B.txt"), "b");
            var limits = SolutionProjectResolverLimits.Default with { MaxFallbackDiscoveryFiles = 1 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, solutionPath: null, limits));

            Assert.Contains("fallback project discovery traversed more than 1 files", ex.Message);
            Assert.Contains("pass --solution <path>", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    public static IEnumerable<object[]> SolutionProjectTraversalFailures()
    {
        yield return [new IOException("blocked"), "an I/O error"];
        yield return [new UnauthorizedAccessException("blocked"), "permissions"];
        yield return [new NotSupportedException("blocked"), "an unsupported path"];
        yield return [new PathTooLongException("blocked"), "a path that is too long"];
        yield return [new ArgumentException("blocked"), "an invalid path"];
    }

    [Theory]
    [MemberData(nameof(SolutionProjectTraversalFailures))]
    public void ResolveProjects_FallbackTraversalReportsExpectedFilesystemDiagnostics_Issue3707(
        Exception exception,
        string expectedReason)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_fallback_exception");
        var previousEnumerateDirectories = SolutionProjectResolver.EnumerateDirectoriesForTesting;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            SolutionProjectResolver.EnumerateDirectoriesForTesting = _ => throw exception;
            var diagnostics = new List<string>();

            var projects = SolutionProjectResolver.ResolveProjects(
                projectRoot,
                solutionPath: null,
                SolutionProjectResolverLimits.Default,
                diagnostics);

            Assert.Empty(projects);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("Could not enumerate subdirectories in .", diagnostic, StringComparison.Ordinal);
            Assert.Contains(expectedReason, diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("blocked", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            SolutionProjectResolver.EnumerateDirectoriesForTesting = previousEnumerateDirectories;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjects_RejectsFallbackDiscoveryDirectoryTraversalLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_solution_fallback_directory_limit");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "A"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "B"));
            var limits = SolutionProjectResolverLimits.Default with { MaxFallbackDiscoveryDirectories = 1 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjects(projectRoot, solutionPath: null, limits));

            Assert.Contains("fallback project discovery traversed more than 1 directories", ex.Message);
            Assert.Contains("pass --solution <path>", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjectFiles_HonorsGitRootIgnoreRulesForNestedWorkspace_Issue2862()
    {
        var repoRoot = TestProjectHelper.CreateTempProject("cdidx_index_project_filter_git_root");
        try
        {
            RunGit(repoRoot, "init");
            var projectRoot = Path.Combine(repoRoot, "Sub");
            var libDir = Path.Combine(projectRoot, "src", "Lib");
            Directory.CreateDirectory(Path.Combine(libDir, "Ignored"));
            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "Sub/src/Lib/Ignored/\n");
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(libDir, "Class1.cs"), "class Class1 {}");
            File.WriteAllText(Path.Combine(libDir, "Ignored", "Ignored.cs"), "class Ignored {}");

            var files = SolutionProjectResolver.ResolveProjectFiles(projectRoot, ["Lib"], "Repo.sln");

            Assert.Contains("src/Lib/Class1.cs", files);
            Assert.DoesNotContain("src/Lib/Ignored/Ignored.cs", files);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void ResolveProjectFiles_RejectsPerProjectExpansionFileLimit_Issue3066()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_index_project_filter_per_project_limit");
        try
        {
            var libDir = Path.Combine(projectRoot, "src", "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(libDir, "Class1.cs"), "class Class1 {}");
            var limits = SolutionProjectResolverLimits.Default with { MaxProjectExpansionFilesPerProject = 1 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjectFiles(projectRoot, ["Lib"], "Repo.sln", limits));

            Assert.Contains("project filter expansion for Lib (src/Lib/Lib.csproj) materialized more than 1 files", ex.Message);
            Assert.Contains("explicit --files", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjectFiles_RejectsTotalExpansionFileLimit_Issue3066()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_index_project_filter_total_limit");
        try
        {
            var libDir = Path.Combine(projectRoot, "src", "Lib");
            var appDir = Path.Combine(projectRoot, "src", "App");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(libDir, "Class1.cs"), "class Class1 {}");
            File.WriteAllText(Path.Combine(appDir, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(appDir, "Class2.cs"), "class Class2 {}");
            var limits = SolutionProjectResolverLimits.Default with { MaxProjectExpansionFilesTotal = 3 };

            var ex = Assert.Throws<InvalidOperationException>(
                () => SolutionProjectResolver.ResolveProjectFiles(projectRoot, ["Lib", "App"], "Repo.sln", limits));

            Assert.Contains("project filter expansion materialized more than 3 unique files across requested projects", ex.Message);
            Assert.Contains("explicit --files", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ResolveProjectFiles_SkipsDirectorySymlinkLoops_Issue2862()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_index_project_filter_symlink_loop");
        try
        {
            var libDir = Path.Combine(projectRoot, "src", "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(projectRoot, "Repo.sln"), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(libDir, "Class1.cs"), "class Class1 {}");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(libDir, "loop"), libDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var files = SolutionProjectResolver.ResolveProjectFiles(projectRoot, ["Lib"], "Repo.sln");

            Assert.Contains("src/Lib/Class1.cs", files);
            Assert.DoesNotContain(files, file => file.Contains("/loop/", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void IsUpdateMode_ProjectFilterWithoutResolvedFiles_ReturnsTrue_Issue2862()
    {
        var options = new IndexCommandOptions { ProjectFilters = ["Lib"] };

        Assert.True(IndexCommandRunner.IsUpdateMode(options));
    }



    [Fact]
    public void MayContainCSharpStaticInterfaceContract_RealContract_ReturnsTrue()
    {
        const string content = """
        public interface IFixture<T>
        {
            static abstract T Create();
            static virtual int Count => 0;
        }
        """;

        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(content));
    }

    [Fact]
    public void MayContainCSharpStaticInterfaceContract_HelperNamesAndStrings_ReturnsFalse()
    {
        var content = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "CodeIndex",
            "Database",
            "DbWriter.cs"));

        Assert.False(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(content));
    }

    [Theory]
    [InlineData("class C { const string Value = \"interface I { static abstract int M(); }\"; }")]
    [InlineData("class C { const string Value = @\"interface I { static abstract int M(); }\"; }")]
    [InlineData("class C { const string Value = \"\"\"interface I { static abstract int M(); }\"\"\"; }")]
    [InlineData("class C { const string Value = $\"\"\"interface I { static abstract int M(); }\"\"\"; }")]
    [InlineData("class C { const string Value = $@\"interface I { static abstract int M(); }\"; }")]
    [InlineData("class C { const string Value = @$\"interface I { static abstract int M(); }\"; }")]
    [InlineData("class C { // interface I { static abstract int M(); }\n }")]
    [InlineData("class C { /* interface I { static abstract int M(); } */ }")]
    [InlineData("interfaceFactory I { static abstract int M(); }")]
    [InlineData("interface I { staticValue abstract int M(); }")]
    [InlineData("interface I { static abstractValue int M(); }")]
    public void MayContainCSharpStaticInterfaceContract_LexicalDecoysAndTokenPrefixes_ReturnFalse(string content)
    {
        Assert.False(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(content));
    }

    [Theory]
    [InlineData("/* } ; */ public interface I { static abstract int M(); }")]
    [InlineData("public interface I { string Text => \"} ;\"; static virtual int M() => 0; }")]
    [InlineData("public interface I { string Text => \"\"\"} ; interface Fake { static abstract int X(); }\"\"\"; static abstract int M(); }")]
    [InlineData("public interface I { string Text => \"\"\"\"embedded \"\"\" quote\"\"\"\"; static abstract int M(); }")]
    [InlineData("public interface Outer { interface Inner { static virtual int M() => 0; } }")]
    [InlineData("public interface I { char Quote => '\\''; static abstract int M(); }")]
    public void MayContainCSharpStaticInterfaceContract_LexicalBoundariesPreserveRealContracts(string content)
    {
        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(content));
    }

    [Fact]
    public void MayContainCSharpStaticInterfaceContract_DeeplyNestedInterfacesPreserveContract()
    {
        var content = string.Concat(
                          Enumerable.Range(0, 40).Select(index => $"interface I{index} {{ "))
                      + "static abstract int M();"
                      + new string('}', 40);

        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(content));
    }

    [Fact]
    public void CSharpPrepassFileTargetCreate_TrailingRootSeparatorKeepsRelativePaths()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "cdidx_prepass_target");
        var filePath = Path.Combine(projectRoot, "src", "Api.cs");
        var rootWithSeparator = Path.EndsInDirectorySeparator(projectRoot)
            ? projectRoot
            : projectRoot + Path.DirectorySeparatorChar;

        var target = CSharpStaticInterfacePrepass.FileTarget.Create(rootWithSeparator, filePath, "csharp");

        Assert.Equal(Path.Combine("src", "Api.cs"), target.RelativePath);
        Assert.Equal("src/Api.cs", target.DisplayRelativePath);
        Assert.Equal("src/Api.cs", target.IndexPath);
        Assert.Equal("csharp", target.Language);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public void RawBytesMayContainCSharpStaticInterfaceContract_ContractEncodings_ReturnsTrue(string encodingName)
    {
        const string content = """
        public interface IFixture<T>
        {
            static abstract T Create();
        }
        """;

        var bytes = EncodeCSharpPrepassContent(content, encodingName);

        Assert.True(CSharpStaticInterfacePrepass.RawBytesMayContainCSharpStaticInterfaceContract(bytes));
    }

    [Fact]
    public void RawBytesMayContainCSharpStaticInterfaceContract_UnalignedUtf16Payload_ReturnsTrue()
    {
        const string content = """
        public interface IFixture<T>
        {
            static abstract T Create();
        }
        """;
        var encoded = EncodeCSharpPrepassContent(content, "utf16-le");
        var bytes = new byte[encoded.Length + 1];
        bytes[0] = 0x20;
        Buffer.BlockCopy(encoded, 0, bytes, 1, encoded.Length);

        Assert.True(CSharpStaticInterfacePrepass.RawBytesMayContainCSharpStaticInterfaceContract(bytes));
    }

    [Theory]
    [InlineData("utf8", 3)]
    [InlineData("utf8-bom", 5)]
    [InlineData("utf16-le", 7)]
    [InlineData("utf16-be", 11)]
    public void RawByteChunksMayContainCSharpStaticInterfaceContract_ContractTokensAcrossChunkBoundaries_ReturnsTrue(
        string encodingName,
        int chunkSize)
    {
        const string content = """
        public interface IFixture<T>
        {
            static abstract T Create();
        }
        """;

        var bytes = EncodeCSharpPrepassContent(content, encodingName);

        Assert.True(CSharpStaticInterfacePrepass.RawByteChunksMayContainCSharpStaticInterfaceContract(ChunkBytes(bytes, chunkSize)));
    }

    [Fact]
    public void RawByteChunksMayContainCSharpStaticInterfaceContract_MissingContractTokens_ReturnsFalse()
    {
        var bytes = Encoding.UTF8.GetBytes("public interface IFixture { static int Count => 0; }");

        Assert.False(CSharpStaticInterfacePrepass.RawByteChunksMayContainCSharpStaticInterfaceContract(ChunkBytes(bytes, 4)));
    }

    [Theory]
    [InlineData("public class C { public static void Run() { } }")]
    [InlineData("public interface IFixture { void Run(); }")]
    [InlineData("public interface IFixture { static int Count => 0; }")]
    [InlineData("public abstract class C { public static void Run() { } }")]
    public void RawBytesMayContainCSharpStaticInterfaceContract_MissingContractTokens_ReturnsFalse(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);

        Assert.False(CSharpStaticInterfacePrepass.RawBytesMayContainCSharpStaticInterfaceContract(bytes));
    }

    private static byte[] EncodeCSharpPrepassContent(string content, string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName), encodingName, null),
        };
        return encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
    }

    private static IEnumerable<byte[]> ChunkBytes(byte[] bytes, int chunkSize)
    {
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            yield return bytes.Skip(offset).Take(Math.Min(chunkSize, bytes.Length - offset)).ToArray();
    }

    [Fact]
    public void HandleIndexCancelKeyPress_FirstCancelRequestsCooperativeCancellation_SecondAllowsForceExit()
    {
        using var cts = new CancellationTokenSource();
        var firstCancelHandled = false;

        var firstEventShouldCancelDefaultExit = IndexCommandRunner.HandleIndexCancelKeyPress(cts, ref firstCancelHandled);
        var secondEventShouldCancelDefaultExit = IndexCommandRunner.HandleIndexCancelKeyPress(cts, ref firstCancelHandled);

        Assert.True(firstEventShouldCancelDefaultExit);
        Assert.True(cts.IsCancellationRequested);
        Assert.False(secondEventShouldCancelDefaultExit);
    }

    [Fact]
    public void Run_IndexCancellation_ReturnsInterruptedExitCodeAndMessage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_cancel_index");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "class Program { static void Main() {} }");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                var originalErr = Console.Error;
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);

                    var exitCode = IndexCommandRunner.Run([projectRoot], _jsonOptions, cts);

                    Assert.Equal(CommandExitCodes.Interrupted, exitCode);
                    Assert.Contains($"Error [{CommandErrorCodes.Interrupted}]: Interrupted; full-scan progress was rolled back", stderr.ToString());
                }
                finally
                {
                    Console.SetOut(originalOut);
                    Console.SetError(originalErr);
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ParseArgs_WatchFlag_SetsWatch()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--watch"]);
        Assert.True(options.Watch);
        Assert.Null(options.WatchDebounceMs);
        Assert.Equal(IndexWatchRunner.DefaultWatchPendingPathLimit, options.WatchPendingPathLimit);
    }

    [Fact]
    public void ParseArgs_NoWatchFlag_DefaultsToFalse()
    {
        var options = IndexCommandRunner.ParseArgs(["."]);
        Assert.False(options.Watch);
        Assert.Null(options.WatchDebounceMs);
    }

    [Fact]
    public void ParseArgs_DebounceFlag_ParsesValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--watch", "--debounce", "250"]);
        Assert.True(options.Watch);
        Assert.Equal(250, options.WatchDebounceMs);
    }

    [Fact]
    public void ParseArgs_DebounceFlag_RejectsValueAboveMaximum_Issue3173()
    {
        var oversized = $"{IndexWatchRunner.MaxDebounceMs + 1}";

        var options = IndexCommandRunner.ParseArgs([".", "--watch", "--debounce", oversized]);

        Assert.True(options.Watch);
        Assert.Null(options.WatchDebounceMs);
        Assert.Contains("--debounce", options.ParseError);
        Assert.Contains($"{IndexWatchRunner.MaxDebounceMs}", options.ParseError);
    }

    [Fact]
    public void ParseArgs_DebounceFlag_InvalidValue_IsRejected()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--watch", "--debounce", "not-a-number"]);

        Assert.True(options.Watch);
        Assert.Null(options.WatchDebounceMs);
        Assert.Contains("--debounce value 'not-a-number' must be between 0", options.ParseError);
    }

    [Fact]
    public void ParseArgs_DryRunPathLimitFlag_ParsesValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--dry-run", "--dry-run-path-limit", "42"]);

        Assert.True(options.DryRun);
        Assert.Equal(42, options.DryRunPathLimit);
    }

    [Fact]
    public void ParseArgs_DryRunPathLimitFlag_RejectsValueAboveMaximum()
    {
        var oversized = $"{IndexCommandRunner.MaxDryRunPathLimit + 1}";

        var options = IndexCommandRunner.ParseArgs([".", "--dry-run", "--dry-run-path-limit", oversized]);

        Assert.Equal(IndexCommandRunner.DefaultDryRunPathLimit, options.DryRunPathLimit);
        Assert.Contains("--dry-run-path-limit", options.ParseError);
        Assert.Contains($"{IndexCommandRunner.MaxDryRunPathLimit}", options.ParseError);
    }

    [Fact]
    public void ParseArgs_WatchPendingPathLimitFlag_ParsesValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--watch", "--watch-pending-path-limit", "8192"]);

        Assert.True(options.Watch);
        Assert.Equal(8192, options.WatchPendingPathLimit);
    }

    [Fact]
    public void ParseArgs_WatchPendingPathLimitEnv_ParsesValue()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable);
        Environment.SetEnvironmentVariable(IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable, "6144");

        var options = IndexCommandRunner.ParseArgs([".", "--watch"]);

        Assert.Equal(6144, options.WatchPendingPathLimit);
    }

    [Fact]
    public void ParseArgs_WatchPendingPathLimitFlag_RejectsValueAboveMaximum()
    {
        var oversized = $"{IndexWatchRunner.MaxWatchPendingPathLimit + 1}";

        var options = IndexCommandRunner.ParseArgs([".", "--watch", "--watch-pending-path-limit", oversized]);

        Assert.Equal(IndexWatchRunner.DefaultWatchPendingPathLimit, options.WatchPendingPathLimit);
        Assert.Contains("--watch-pending-path-limit", options.ParseError);
        Assert.Contains($"{IndexWatchRunner.MaxWatchPendingPathLimit}", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxFileBytesFlag_ParsesSuffixValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-file-bytes", "2M"]);

        Assert.Equal(2L * 1024L * 1024L, options.MaxFileSizeBytes);
    }

    [Fact]
    public void ParseArgs_MaxFileBytesInlineFlag_ParsesBytesValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-file-bytes=12345"]);

        Assert.Equal(12345, options.MaxFileSizeBytes);
    }

    [Fact]
    public void ParseArgs_MaxSymbolsPerFileFlag_ParsesPositiveValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-symbols-per-file", "42"]);

        Assert.Equal(42, options.MaxSymbolsPerFile);
    }

    [Fact]
    public void ParseArgs_MaxSymbolsPerFileInlineFlag_ParsesPositiveValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-symbols-per-file=43"]);

        Assert.Equal(43, options.MaxSymbolsPerFile);
    }

    [Fact]
    public void ParseArgs_MaxReferencesPerFileFlag_ParsesPositiveValue_Issue3719()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-references-per-file", "42"]);

        Assert.Equal(42, options.MaxReferencesPerFile);
    }

    [Fact]
    public void ParseArgs_MaxReferencesPerFileInlineFlag_ParsesPositiveValue_Issue3719()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-references-per-file=43"]);

        Assert.Equal(43, options.MaxReferencesPerFile);
    }

    [Fact]
    public void ParseArgs_SymbolsOnlyFlag_SetsOption()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--symbols-only"]);

        Assert.True(options.SymbolsOnly);
    }

    [Fact]
    public void ParseArgs_MaxSymbolsPerFileFlag_AcceptsMaximum_Issue3172()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--max-symbols-per-file", $"{IndexCommandRunner.MaxSymbolsPerFileLimit}"]);

        Assert.Equal(IndexCommandRunner.MaxSymbolsPerFileLimit, options.MaxSymbolsPerFile);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxSymbolsPerFileFlag_RejectsValueAboveMaximum_Issue3172()
    {
        var aboveMaximum = $"{IndexCommandRunner.MaxSymbolsPerFileLimit + 1}";
        var options = IndexCommandRunner.ParseArgs([".", $"--max-symbols-per-file={aboveMaximum}"]);

        Assert.Equal(IndexCommandRunner.DefaultMaxSymbolsPerFile, options.MaxSymbolsPerFile);
        Assert.Contains("--max-symbols-per-file value '50001' must be between 1 and 50000 inclusive", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxReferencesPerFileFlag_RejectsValueAboveMaximum_Issue3719()
    {
        var aboveMaximum = $"{IndexCommandRunner.MaxReferencesPerFileLimit + 1}";
        var options = IndexCommandRunner.ParseArgs([".", $"--max-references-per-file={aboveMaximum}"]);

        Assert.Equal(IndexCommandRunner.DefaultMaxReferencesPerFile, options.MaxReferencesPerFile);
        Assert.Contains("--max-references-per-file value '1000001' must be between 1 and 1000000 inclusive", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxFileBytesInvalidValue_IsRejected()
    {
        using var env = EnvironmentVariableScope.Capture(FileIndexer.MaxFileSizeEnvironmentVariable);
        env.Set(FileIndexer.MaxFileSizeEnvironmentVariable, null);

        var options = IndexCommandRunner.ParseArgs([".", "--max-file-bytes", "0"]);

        Assert.Null(options.MaxFileSizeBytes);
        Assert.Contains("--max-file-bytes value '0' must be between 1", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxFileBytesInvalidValue_TruncatesOversizedValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var options = IndexCommandRunner.ParseArgs([".", "--max-file-bytes", value]);

        Assert.Contains("--max-file-bytes value", options.ParseError);
        Assert.Contains("<truncated; original length", options.ParseError);
        Assert.DoesNotContain(value, options.ParseError);
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("v1.0.0")]
    [InlineData("main..feature")]
    [InlineData("HEAD")]
    public void ParseArgs_CommitsAcceptsCommitishRefsForGitValidation(string commitRef)
    {
        var options = IndexCommandRunner.ParseArgs([".", "--commits", commitRef]);

        Assert.Equal([commitRef], options.Commits);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_CommitsAcceptsHexCommitId()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--commits", "0123456789abcdef"]);

        Assert.Equal(["0123456789abcdef"], options.Commits);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_CommitsRejectsTooManyRefs_Issue3177()
    {
        var refs = Enumerable
            .Range(0, IndexCommandRunner.MaxCommitRefCount + 1)
            .Select(i => $"HEAD~{i}")
            .ToArray();
        var args = new[] { ".", "--commits" }.Concat(refs).ToArray();

        var options = IndexCommandRunner.ParseArgs(args);

        Assert.Equal(IndexCommandRunner.MaxCommitRefCount, options.Commits.Count);
        Assert.Contains($"at most {IndexCommandRunner.MaxCommitRefCount}", options.ParseError);
    }

    [Fact]
    public void ParseArgs_CommitsRejectsOversizedRef_Issue3177()
    {
        var oversizedRef = new string('a', IndexCommandRunner.MaxCommitRefLength + 1);

        var options = IndexCommandRunner.ParseArgs([".", "--commits", oversizedRef]);

        Assert.Empty(options.Commits);
        Assert.Contains("commit ref is too long", options.ParseError);
        Assert.Contains($"max {IndexCommandRunner.MaxCommitRefLength}", options.ParseError);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(10, 8)]
    [InlineData(64, 8)]
    public void CalculateDefaultIndexParallelism_CapsAutomaticWorkersWithoutLoweringSmallHosts(
        int processorCount,
        int expected)
    {
        Assert.Equal(expected, IndexCommandRunner.CalculateDefaultIndexParallelism(processorCount));
    }

    [Fact]
    public void ParseArgs_ParallelismFlag_ParsesPositiveValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--parallelism", "3"]);

        Assert.Equal(3, options.Parallelism);
    }

    [Fact]
    public void ParseArgs_ParallelismInlineFlag_ParsesPositiveValue()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--parallelism=4"]);

        Assert.Equal(4, options.Parallelism);
    }

    [Fact]
    public void ParseArgs_ParallelismFlagRejectsOversizedValue_Issue5097()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.IndexParallelismEnvironmentVariable);
        env.Set(IndexCommandRunner.IndexParallelismEnvironmentVariable, null);

        var options = IndexCommandRunner.ParseArgs([".", "--parallelism", "999"]);

        Assert.Equal(IndexCommandRunner.DefaultIndexParallelism(), options.Parallelism);
        Assert.Contains("--parallelism value '999' must be between 1 and 16 inclusive", options.ParseError);
    }

    [Fact]
    public void ParseArgs_IndexParallelismEnvironment_ProvidesDefault()
    {
        var original = Environment.GetEnvironmentVariable(IndexCommandRunner.IndexParallelismEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(IndexCommandRunner.IndexParallelismEnvironmentVariable, "2");

            var options = IndexCommandRunner.ParseArgs(["."]);

            Assert.Equal(2, options.Parallelism);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexCommandRunner.IndexParallelismEnvironmentVariable, original);
        }
    }

    [Fact]
    public void ParseArgs_IndexParallelismEnvironmentClampsOversizedValue_Issue2904()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.IndexParallelismEnvironmentVariable);
            try
            {
                Console.SetError(stderr);
                Environment.SetEnvironmentVariable(IndexCommandRunner.IndexParallelismEnvironmentVariable, "999");

                var options = IndexCommandRunner.ParseArgs(["."]);

                Assert.Equal(IndexCommandRunner.MaxIndexParallelism, options.Parallelism);
                Assert.Contains(IndexCommandRunner.IndexParallelismEnvironmentVariable, stderr.ToString());
                Assert.Contains($"maximum clamp {IndexCommandRunner.MaxIndexParallelism}", stderr.ToString());
                var warning = Assert.Single(options.OptionWarnings);
                Assert.Equal($"<environment:{IndexCommandRunner.IndexParallelismEnvironmentVariable}>", warning.File);
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    public static IEnumerable<object[]> ValidIndexNumericOptionBoundaries()
    {
        foreach (var option in IndexNumericOptionCases())
        {
            yield return [option.Flag, option.Minimum.ToString(CultureInfo.InvariantCulture), option.Minimum];
            yield return [option.Flag, option.Maximum.ToString(CultureInfo.InvariantCulture), option.Maximum];
        }
    }

    public static IEnumerable<object[]> InvalidIndexNumericOptionValues()
    {
        foreach (var option in IndexNumericOptionCases())
        {
            if (option.Minimum > 0)
                yield return [option.Flag, "0", option.Minimum, option.Maximum];
            yield return [option.Flag, "-1", option.Minimum, option.Maximum];
            yield return [option.Flag, "2147483648", option.Minimum, option.Maximum];
            yield return [option.Flag, "not-a-number", option.Minimum, option.Maximum];
            if (option.Maximum < int.MaxValue)
                yield return [option.Flag, (option.Maximum + 1).ToString(CultureInfo.InvariantCulture), option.Minimum, option.Maximum];
        }
    }

    public static IEnumerable<object[]> InvalidIndexNumericOptionRunValues()
    {
        foreach (var option in IndexNumericOptionCases())
        {
            yield return
            [
                option.Flag,
                option.Minimum == 0 ? "-1" : "0",
                option.Minimum,
                option.Maximum,
            ];
        }
    }

    [Theory]
    [MemberData(nameof(ValidIndexNumericOptionBoundaries))]
    public void ParseArgs_IndexNumericOptions_AcceptInclusiveBoundaries_Issue5097(
        string flag,
        string value,
        long expected)
    {
        var options = IndexCommandRunner.ParseArgs([".", flag, value]);

        Assert.Null(options.ParseError);
        Assert.Equal(expected, GetIndexNumericOptionValue(options, flag));
    }

    [Theory]
    [MemberData(nameof(InvalidIndexNumericOptionValues))]
    public void ParseArgs_IndexNumericOptions_RejectInvalidExplicitValues_Issue5097(
        string flag,
        string value,
        long minimum,
        long maximum)
    {
        var options = IndexCommandRunner.ParseArgs([".", flag, value]);

        Assert.NotNull(options.ParseError);
        Assert.Contains(flag, options.ParseError);
        Assert.Contains($"value '{value}'", options.ParseError);
        Assert.Contains($"between {minimum} and {maximum} inclusive", options.ParseError);
    }

    [Theory]
    [InlineData("--parallelism", " +1 ", 1)]
    [InlineData("--dry-run-path-limit", " +1 ", 1)]
    [InlineData("--max-file-bytes", " +1 ", 1)]
    [InlineData("--debounce", " +0 ", 0)]
    public void ParseArgs_IndexNumericOptions_AcceptWhitespaceAndExplicitPositiveSign_Issue5097(
        string flag,
        string value,
        long expected)
    {
        var options = IndexCommandRunner.ParseArgs([".", flag, value]);

        Assert.Null(options.ParseError);
        Assert.Equal(expected, GetIndexNumericOptionValue(options, flag));
    }

    [Theory]
    [InlineData("--parallelism", "2", "3", 3)]
    [InlineData("--max-file-bytes", "2K", "3K", 3072)]
    [InlineData("--max-references-per-file", "2", "3", 3)]
    public void ParseArgs_IndexNumericOptions_LastValidDuplicateWins_Issue5097(
        string flag,
        string first,
        string last,
        long expected)
    {
        var options = IndexCommandRunner.ParseArgs([".", flag, first, flag, last]);

        Assert.Null(options.ParseError);
        Assert.Equal(expected, GetIndexNumericOptionValue(options, flag));
    }

    [Theory]
    [InlineData("--parallelism", "2", "0")]
    [InlineData("--parallelism", "0", "2")]
    [InlineData("--max-file-bytes", "2K", "invalid")]
    [InlineData("--max-file-bytes", "invalid", "2K")]
    public void ParseArgs_IndexNumericOptions_AnyInvalidDuplicateCausesFailure_Issue5097(
        string flag,
        string first,
        string last)
    {
        var options = IndexCommandRunner.ParseArgs([".", flag, first, flag, last]);

        Assert.NotNull(options.ParseError);
        Assert.Contains(flag, options.ParseError);
    }

    [Theory]
    [InlineData(IndexCommandRunner.IndexParallelismEnvironmentVariable, "--parallelism", "2", 2)]
    [InlineData(IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable, "--watch-pending-path-limit", "2", 2)]
    [InlineData(FileIndexer.MaxFileSizeEnvironmentVariable, "--max-file-bytes", "2K", 2048)]
    public void ParseArgs_ExplicitNumericOptionOverridesInvalidEnvironmentWithoutWarning_Issue5097(
        string environmentVariable,
        string flag,
        string cliValue,
        long expected)
    {
        using var env = EnvironmentVariableScope.Capture(environmentVariable);
        Environment.SetEnvironmentVariable(environmentVariable, "invalid");

        var options = IndexCommandRunner.ParseArgs([".", flag, cliValue]);

        Assert.Null(options.ParseError);
        Assert.Empty(options.OptionWarnings);
        Assert.Equal(expected, GetIndexNumericOptionValue(options, flag));
    }

    [Theory]
    [InlineData(IndexCommandRunner.IndexParallelismEnvironmentVariable, "automatic CPU default")]
    [InlineData(IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable, "built-in default")]
    [InlineData(FileIndexer.MaxFileSizeEnvironmentVariable, "built-in default")]
    public void ParseArgs_InvalidNumericEnvironmentFallsBackWithStructuredProvenance_Issue5097(
        string environmentVariable,
        string expectedSource)
    {
        using var env = EnvironmentVariableScope.Capture(environmentVariable);
        Environment.SetEnvironmentVariable(environmentVariable, "invalid");

        var options = IndexCommandRunner.ParseArgs(["."]);

        Assert.Null(options.ParseError);
        var warning = Assert.Single(options.OptionWarnings);
        Assert.Equal($"<environment:{environmentVariable}>", warning.File);
        Assert.Contains("value 'invalid'", warning.Message);
        Assert.Contains(expectedSource, warning.Message);
    }

    [Theory]
    [InlineData(IndexCommandRunner.IndexParallelismEnvironmentVariable)]
    [InlineData(IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable)]
    [InlineData(FileIndexer.MaxFileSizeEnvironmentVariable)]
    public void ParseArgs_OptimizeSuppressesIrrelevantNumericEnvironmentWarnings_Issue5097(
        string environmentVariable)
    {
        using var env = EnvironmentVariableScope.Capture(environmentVariable);
        env.Set(environmentVariable, "invalid");

        var options = IndexCommandRunner.ParseArgs([".", "--optimize", "--dry-run", "--json"]);

        Assert.True(options.OptimizeOnly);
        Assert.Null(options.ParseError);
        Assert.Empty(options.OptionWarnings);
    }

    [Theory]
    [MemberData(nameof(InvalidIndexNumericOptionRunValues))]
    public void Run_InvalidExplicitNumericOptionReturnsStructuredUsageErrorBeforeDbMutation_Issue5097(
        string flag,
        string value,
        long minimum,
        long maximum)
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, json) = RunAndCaptureJson([projectRoot, flag, value, "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.UsageError, json.GetProperty("error_code").GetString());
            var message = json.GetProperty("message").GetString();
            Assert.Contains(flag, message);
            Assert.Contains($"value '{value}'", message);
            Assert.Contains($"between {minimum} and {maximum} inclusive", message);
            Assert.False(File.Exists(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_InvalidExplicitNumericOption_TextReturnsUsageErrorWithoutWarning_Issue5097()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--parallelism", "0"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"Error [{CommandErrorCodes.UsageError}]", stderr);
            Assert.Contains("--parallelism value '0' must be between 1 and 16 inclusive", stderr);
            Assert.DoesNotContain("Warning: invalid --parallelism", stderr);
            Assert.False(File.Exists(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_InvalidNumericEnvironmentWarningAppearsInDryRunJson_Issue5097()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.IndexParallelismEnvironmentVariable);
        Environment.SetEnvironmentVariable(IndexCommandRunner.IndexParallelismEnvironmentVariable, "invalid");
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("warnings_total").GetInt32() >= 1);
            var warning = json.GetProperty("warnings")
                .EnumerateArray()
                .Single(item => item.GetProperty("file").GetString() == $"<environment:{IndexCommandRunner.IndexParallelismEnvironmentVariable}>");
            Assert.Contains("automatic CPU default", warning.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private static IEnumerable<(string Flag, int Minimum, int Maximum)> IndexNumericOptionCases()
    {
        yield return ("--parallelism", 1, IndexCommandRunner.MaxIndexParallelism);
        yield return ("--max-file-bytes", 1, int.MaxValue);
        yield return ("--max-symbols-per-file", 1, IndexCommandRunner.MaxSymbolsPerFileLimit);
        yield return ("--max-references-per-file", 1, IndexCommandRunner.MaxReferencesPerFileLimit);
        yield return ("--dry-run-path-limit", 1, IndexCommandRunner.MaxDryRunPathLimit);
        yield return ("--watch-pending-path-limit", 1, IndexWatchRunner.MaxWatchPendingPathLimit);
        yield return ("--debounce", 0, IndexWatchRunner.MaxDebounceMs);
    }

    private static long GetIndexNumericOptionValue(IndexCommandOptions options, string flag)
        => flag switch
        {
            "--parallelism" => options.Parallelism,
            "--max-file-bytes" => options.MaxFileSizeBytes ?? FileIndexer.DefaultMaxFileSizeBytes,
            "--max-symbols-per-file" => options.MaxSymbolsPerFile,
            "--max-references-per-file" => options.MaxReferencesPerFile,
            "--dry-run-path-limit" => options.DryRunPathLimit,
            "--watch-pending-path-limit" => options.WatchPendingPathLimit,
            "--debounce" => options.WatchDebounceMs ?? IndexWatchRunner.DefaultDebounceMs,
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Unknown numeric index option."),
        };

    [Theory]
    [InlineData("--duration-format", "auto", DurationOutputFormat.Auto)]
    [InlineData("--duration-format", "seconds", DurationOutputFormat.Seconds)]
    [InlineData("--duration-format", "hms", DurationOutputFormat.Hms)]
    [InlineData("--duration-format=hms", null, DurationOutputFormat.Hms)]
    public void ParseArgs_DurationFormatFlag_ParsesValue(string flag, string? value, DurationOutputFormat expected)
    {
        string[] args = value is null
            ? [".", flag]
            : [".", flag, value];

        var options = IndexCommandRunner.ParseArgs(args);

        Assert.Equal(expected, options.DurationFormat);
    }

    [Fact]
    public void ParseArgs_DurationFormatFlag_InvalidValue_IsIgnored()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalErr = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var options = IndexCommandRunner.ParseArgs([".", "--duration-format", "bogus"]);
                Assert.Equal(DurationOutputFormat.Auto, options.DurationFormat);
                Assert.Contains("invalid --duration-format value", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
    }

    [Fact]
    public void ParseArgs_AbsolutizesRelativeProjectPath()
    {
        var options = IndexCommandRunner.ParseArgs(["./sub/path"]);
        Assert.NotNull(options.ProjectPath);
        Assert.True(Path.IsPathRooted(options.ProjectPath));
        Assert.Equal(Path.GetFullPath("./sub/path"), options.ProjectPath);
    }

    [Fact]
    public void ParseArgs_AbsolutizesRelativeDbPath()
    {
        var options = IndexCommandRunner.ParseArgs([".", "--db", "./.cdidx/codeindex.db"]);
        Assert.NotNull(options.DbPath);
        Assert.True(Path.IsPathRooted(options.DbPath));
        Assert.Equal(Path.GetFullPath("./.cdidx/codeindex.db"), options.DbPath);
    }

    [Fact]
    public void ParseArgs_PreservesFileUriDbPath()
    {
        var uri = "file:///tmp/example.db?immutable=1";
        var options = IndexCommandRunner.ParseArgs([".", "--db", uri]);
        Assert.Equal(uri, options.DbPath);
    }

    [Fact]
    public void BuildCwdDriftNotice_ReturnsNullWhenCwdUnchanged()
    {
        var notice = IndexCommandRunner.BuildCwdDriftNotice("/tmp/project", "/tmp/project");
        Assert.Null(notice);
    }

    [Fact]
    public void BuildCwdDriftNotice_ReturnsNullWhenEitherSnapshotMissing()
    {
        Assert.Null(IndexCommandRunner.BuildCwdDriftNotice(null, "/tmp/project"));
        Assert.Null(IndexCommandRunner.BuildCwdDriftNotice("/tmp/project", null));
        Assert.Null(IndexCommandRunner.BuildCwdDriftNotice(string.Empty, "/tmp/project"));
    }

    [Fact]
    public void BuildCwdDriftNotice_DescribesDriftWhenCwdChanged()
    {
        var notice = IndexCommandRunner.BuildCwdDriftNotice("/tmp/project", "/tmp/other");
        Assert.NotNull(notice);
        Assert.Contains("/tmp/project", notice);
        Assert.Contains("/tmp/other", notice);
        Assert.Contains("working directory changed", notice);
    }

    [Fact]
    public void FormatPerFileErrorLine_OmitsStackTrace_ToKeepStderrSafeForMcpConsumers()
    {
        // Issue #1578: the verbose-mode error path previously appended `ex.StackTrace`
        // to stderr, leaking internal type names, source paths, and line numbers to
        // anyone capturing the indexer's stderr (notably MCP clients). The shared
        // formatter must emit a single user-facing line regardless of verbose state.
        // Issue #1578: verbose 時の stderr に `ex.StackTrace` が乗ると内部型名・パス・
        // 行番号が MCP クライアントなど stderr 取り込み側へ漏れていた。共通フォーマッタ
        // は verbose に関係なく 1 行のユーザー向けメッセージのみ出力すること。
        Exception captured;
        try
        {
            throw new InvalidOperationException("simulated indexing failure");
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        Assert.NotNull(captured.StackTrace);

        var line = IndexCommandRunner.FormatPerFileErrorLine("ERR ", "src/foo.cs", captured);

        Assert.Equal("  [ERR ] src/foo.cs: InvalidOperationException", line);
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain(captured.StackTrace!, line);
        Assert.DoesNotContain("FormatPerFileErrorLine_OmitsStackTrace", line);
        Assert.DoesNotContain("simulated indexing failure", line);
        Assert.DoesNotContain(typeof(InvalidOperationException).FullName!, line);
    }

    [Fact]
    public void FormatPerFileErrorLine_CollapsesNewlinesInPathAndMessage_PreventingInjection()
    {
        // Issue #1578 follow-up: even without ex.StackTrace, a multiline exception
        // message (or a CR/LF-bearing path) could still inject pseudo-stack lines
        // into stderr that MCP clients then misinterpret. The formatter must keep
        // the output on a single line.
        // Issue #1578 派生: ex.StackTrace を外しても、`ex.Message` や `path` に CR/LF が
        // 含まれると疑似スタック行が stderr に注入されうる。フォーマッタは 1 行に保つこと。
        var ex = new InvalidOperationException("first line\nat Internal.Type.Method() in /home/secret.cs:42");
        var line = IndexCommandRunner.FormatPerFileErrorLine("ERR ", "weird\r\npath.cs", ex);

        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Equal("  [ERR ] weird  path.cs: InvalidOperationException", line);
        Assert.DoesNotContain("first line", line);
        Assert.DoesNotContain("/home/secret.cs", line);
    }

    [Fact]
    public void Run_HelpFlagReturnsSuccess()
    {
        int exitCode;
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                exitCode = IndexCommandRunner.Run(["--help"], new JsonSerializerOptions());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        Assert.Equal(CommandExitCodes.Success, exitCode);
    }

    [Fact]
    public void Run_MissingDirectory_PrintsActionableHint()
    {
        var missingProject = Path.Combine(Path.GetTempPath(), $"cdidx_missing_project_{Guid.NewGuid():N}");
        var (exitCode, _, stderr) = RunAndCaptureStreams([missingProject]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Contains("Error [E011_DIRECTORY_NOT_FOUND]: directory not found", stderr);
        Assert.Contains("rerun `cdidx index <projectPath>` with an existing directory", stderr);
    }

    [Fact]
    public void Run_MissingDirectory_JsonIncludesHint()
    {
        var missingProject = Path.Combine(Path.GetTempPath(), $"cdidx_missing_project_{Guid.NewGuid():N}");

        var (exitCode, json) = RunAndCaptureJson([missingProject, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("directory not found", json.GetProperty("message").GetString());
        Assert.Contains("rerun `cdidx index <projectPath>`", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_RebuildWithCommits_PrintsActionableHint()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--rebuild", "--commits", "HEAD"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--rebuild cannot be used with --commits, --changed-between, or --files", stderr);
            Assert.Contains("Hint: use one of:", stderr);
            Assert.Contains("`cdidx index <projectPath> --rebuild`", stderr);
            Assert.Contains("`cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`", stderr);
            Assert.Contains("`cdidx index <projectPath> --changed-between <old-ref> <new-ref>`", stderr);
            Assert.Contains("`cdidx index <projectPath> --files <path> [path ...]`", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_RebuildWithCommits_JsonIncludesHint()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--commits", "HEAD", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("--rebuild cannot be used with --commits, --changed-between, or --files", json.GetProperty("message").GetString());
            var hint = json.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.StartsWith("Use one of:", hint);
            Assert.Contains("`cdidx index <projectPath> --rebuild`", hint);
            Assert.Contains("`cdidx index <projectPath> --commits <commit-ref> [commit-ref ...]`", hint);
            Assert.Contains("`cdidx index <projectPath> --changed-between <old-ref> <new-ref>`", hint);
            Assert.Contains("`cdidx index <projectPath> --files <path> [path ...]`", hint);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("HEAD..HEAD", false)]
    [InlineData("v1.0.0", true)]
    public void Run_CommitsInvalidCommitRef_JsonRejectsBeforeDbSetup(string commitRef, bool createTag)
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            if (createTag)
                RunGit(projectRoot, "tag", commitRef);

            var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
            var excludeBefore = File.Exists(excludePath) ? File.ReadAllText(excludePath) : null;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitRef, "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("failed to resolve changed files from git commits", json.GetProperty("message").GetString());
            Assert.False(File.Exists(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
            var excludeAfter = File.Exists(excludePath) ? File.ReadAllText(excludePath) : null;
            Assert.Equal(excludeBefore, excludeAfter);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WatchWithCommits_PrintsActionableHint()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--watch", "--commits", "HEAD"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--watch cannot be combined with --commits, --changed-between, --files, or --dry-run", stderr);
            Assert.Contains("`cdidx index <projectPath> --watch", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WatchWithFiles_PrintsActionableHint()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--watch", "--files", "app.py"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--watch cannot be combined with --commits, --changed-between, --files, or --dry-run", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_SymbolsOnlyWithFiles_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--symbols-only", "--files", "app.cs"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--symbols-only can only be combined with a full index scan", stderr);
            Assert.Contains("fast symbol/search-only bootstrap", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WatchWithDryRun_JsonIncludesHint()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--watch", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("--watch cannot be combined", json.GetProperty("message").GetString());
            var hint = json.GetProperty("hint").GetString();
            Assert.NotNull(hint);
            Assert.Contains("`cdidx index <projectPath> --watch", hint);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ExplicitDb_PersistsIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_explicit_root");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(Path.GetFullPath(projectRoot), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_CompletionAndStatusReportBoundedUnknownExtensionDiagnostics_Issue5100()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App { }\n");
            File.WriteAllText(Path.Combine(projectRoot, ".cdidxignore"), "# fixture ignore config\n");
            File.WriteAllText(Path.Combine(projectRoot, "notes.mystery"), "unknown extension\n");
            File.WriteAllText(Path.Combine(projectRoot, "data.unmapped"), "also unknown\n");

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--verbose"]);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            var (jsonExitCode, completionJson) = RunAndCaptureJson([projectRoot, "--json"]);
            var (compactExitCode, compactJson) = RunProgramAndCaptureJson(
                ["status", "--db", dbPath, "--compact", "--max-json-bytes", "50000"],
                projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Unknown extensions", stdout);
            Assert.Contains(".mystery: 1 (language_support)", stdout);
            Assert.Contains("sample: data.unmapped", stdout);
            Assert.Contains("2 file(s) were excluded", stderr);
            Assert.Contains("languages --extension", stderr);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(3, statusJson.GetProperty("unknown_extension_file_count").GetInt64());
            Assert.False(statusJson.GetProperty("unknown_extension_files_truncated").GetBoolean());
            Assert.Equal(50, statusJson.GetProperty("unknown_extension_file_path_limit").GetInt64());
            var paths = statusJson.GetProperty("unknown_extension_files")
                .EnumerateArray()
                .Select(path => path.GetString())
                .ToArray();
            Assert.Equal([".cdidxignore", "data.unmapped", "notes.mystery"], paths);
            var extensionCounts = statusJson.GetProperty("unknown_extension_extension_counts");
            Assert.Equal(1, extensionCounts.GetProperty(".cdidxignore").GetInt64());
            Assert.Equal(1, extensionCounts.GetProperty(".mystery").GetInt64());
            Assert.Equal(1, extensionCounts.GetProperty(".unmapped").GetInt64());
            var categoryCounts = statusJson.GetProperty("unknown_extension_category_counts");
            Assert.Equal(2, categoryCounts.GetProperty("language_support_candidate").GetInt64());
            Assert.Equal(1, categoryCounts.GetProperty("repository_metadata").GetInt64());
            var groups = statusJson.GetProperty("unknown_extension_groups").EnumerateArray().ToArray();
            Assert.Equal(3, groups.Length);
            var repositoryGroup = groups.Single(group => group.GetProperty("extension").GetString() == ".cdidxignore");
            Assert.Equal("repository_metadata", repositoryGroup.GetProperty("category").GetString());
            Assert.Equal("ignore_configuration", repositoryGroup.GetProperty("recommended_action").GetString());
            Assert.Equal(1, repositoryGroup.GetProperty("count").GetInt64());
            Assert.All(groups.Where(group => group.GetProperty("extension").GetString() != ".cdidxignore"), group =>
            {
                Assert.Equal("language_support_candidate", group.GetProperty("category").GetString());
                Assert.Equal("language_support", group.GetProperty("recommended_action").GetString());
                Assert.Equal(1, group.GetProperty("count").GetInt64());
            });
            Assert.Equal(3, statusJson.GetProperty("unknown_extension_group_count").GetInt64());
            Assert.False(statusJson.GetProperty("unknown_extension_groups_truncated").GetBoolean());
            Assert.Equal(UnknownExtensionClassifier.MaxPersistedGroups, statusJson.GetProperty("unknown_extension_group_limit").GetInt64());
            Assert.Equal(0, statusJson.GetProperty("unknown_extension_group_omitted_count").GetInt64());
            Assert.Contains(".cdidx-langmap.yaml", statusJson.GetProperty("unknown_extension_guidance").GetString());

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(3, completionJson.GetProperty("unknown_extension_file_count").GetInt32());
            Assert.Equal("workspace", completionJson.GetProperty("unknown_extension_diagnostics_scope").GetString());
            Assert.False(completionJson.GetProperty("unknown_extension_file_count_lower_bound").GetBoolean());
            Assert.Equal(3, completionJson.GetProperty("unknown_extension_group_count").GetInt32());
            Assert.Equal(UnknownExtensionClassifier.MaxCompletionGroups, completionJson.GetProperty("unknown_extension_group_limit").GetInt32());
            Assert.Equal(3, completionJson.GetProperty("unknown_extension_groups").GetArrayLength());
            Assert.Equal(1, completionJson.GetProperty("summary").GetProperty("warnings").GetInt32());

            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            var compactResult = Assert.Single(compactJson.GetProperty("results").EnumerateArray());
            Assert.Equal(3, compactResult.GetProperty("unknown_extension_file_count").GetInt64());
            Assert.Equal(3, compactResult.GetProperty("unknown_extension_groups").GetArrayLength());
            Assert.Equal(3, compactResult.GetProperty("unknown_extension_group_count").GetInt64());
            Assert.Contains("languages --extension", compactResult.GetProperty("unknown_extension_guidance").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UnsupportedOnlyPreservesCaseAndExtensionlessGroups_Issue5100()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "source.MYSTERY"), "unknown extension\n");
            File.WriteAllText(Path.Combine(projectRoot, "archive.part.UNMAPPED"), "unknown extension\n");
            File.WriteAllText(Path.Combine(projectRoot, "extensionless_source"), "unknown extension\n");
            for (var index = 0; index < 9; index++)
                File.WriteAllText(Path.Combine(projectRoot, $"source.unknown{index:D3}"), "unknown extension\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_total").GetInt64());
            Assert.Equal(12, json.GetProperty("unknown_extension_file_count").GetInt32());
            var groups = json.GetProperty("unknown_extension_groups").EnumerateArray().ToArray();
            Assert.Equal(10, groups.Length);
            Assert.Contains(groups, group => group.GetProperty("extension").GetString() == ".mystery");
            Assert.Equal(12, json.GetProperty("unknown_extension_group_count").GetInt32());
            Assert.True(json.GetProperty("unknown_extension_groups_truncated").GetBoolean());
            Assert.Equal(2, json.GetProperty("unknown_extension_group_omitted_count").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Contains(
                "for zsh completion functions, a first-line `#compdef` directive",
                json.GetProperty("unknown_extension_guidance").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IgnoredOnlyDoesNotWarnAndCustomMappingsClearDiagnostics_Issue5100()
    {
        var ignoredRoot = CreateTempProject();
        var mappedRoot = CreateTempProject();
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            File.WriteAllText(Path.Combine(ignoredRoot, ".cdidxignore"), "ignored.mystery\n");
            File.WriteAllText(Path.Combine(ignoredRoot, "ignored.mystery"), "ignored\n");

            var (ignoredExitCode, ignoredJson) = RunAndCaptureJson([ignoredRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, ignoredExitCode);
            Assert.Equal(1, ignoredJson.GetProperty("unknown_extension_file_count").GetInt32());
            Assert.Equal("ignore_configuration", ignoredJson.GetProperty("unknown_extension_groups")[0].GetProperty("recommended_action").GetString());
            Assert.Equal(0, ignoredJson.GetProperty("summary").GetProperty("warnings").GetInt32());

            File.WriteAllText(
                Path.Combine(mappedRoot, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n  - extension: \".custom\"\n    language: \"csharp\"\n  - extension: \".kts.in\"\n    language: \"kotlin\"\n");
            File.WriteAllText(Path.Combine(mappedRoot, "App.CUSTOM"), "public class App { }\n");
            File.WriteAllText(Path.Combine(mappedRoot, "build.KTS.IN"), "fun main() = Unit\n");

            var (mappedExitCode, mappedJson) = RunAndCaptureJson([mappedRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, mappedExitCode);
            Assert.Equal(0, mappedJson.GetProperty("unknown_extension_file_count").GetInt32());
            Assert.Equal(0, mappedJson.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.True(
                !mappedJson.TryGetProperty("unknown_extension_guidance", out var mappedGuidance)
                || mappedGuidance.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            DeleteDirectory(ignoredRoot);
            DeleteDirectory(mappedRoot);
        }
    }

    [Fact]
    public void Run_StatusJsonCapsUnknownExtensionPathSample()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App { }\n");
            for (var i = 0; i < 52; i++)
                File.WriteAllText(Path.Combine(projectRoot, $"unknown-{i:D2}.mystery"), "unknown extension\n");

            var (exitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(52, statusJson.GetProperty("unknown_extension_file_count").GetInt64());
            Assert.True(statusJson.GetProperty("unknown_extension_files_truncated").GetBoolean());
            Assert.Equal(50, statusJson.GetProperty("unknown_extension_file_path_limit").GetInt64());
            var paths = statusJson.GetProperty("unknown_extension_files").EnumerateArray().ToArray();
            Assert.Equal(50, paths.Length);
            Assert.Equal("unknown-00.mystery", paths[0].GetString());
            Assert.Equal("unknown-49.mystery", paths[^1].GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_StatusJsonIncludesExtractorPluginDiagnostics()
    {
        var projectRoot = CreateTempProject();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var patternsDir = Path.Combine(projectRoot, ".cdidx", "patterns");
                Directory.CreateDirectory(patternsDir);
                File.WriteAllText(
                    Path.Combine(patternsDir, "toydsl.yaml"),
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");
                File.WriteAllText(
                    Path.Combine(patternsDir, "broken.yaml"),
                    "language: \"broken\"\npatterns:\n  - kind: \"class\"\n    regex: \"(?<name>\"\n");
                File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App { }\n");

                var (exitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
                var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
                var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(CommandExitCodes.Success, statusExitCode);
                var extractors = statusJson.GetProperty("extractors");
                Assert.True(extractors.GetProperty("pattern_config_count").GetInt32() >= 1);
                Assert.True(extractors.GetProperty("skipped_file_count").GetInt32() >= 1);
                Assert.True(extractors.GetProperty("diagnostic_count").GetInt32() >= 1);
                Assert.Equal(20, extractors.GetProperty("diagnostic_limit").GetInt32());
                Assert.True(extractors.GetProperty("symbol_extractor_count").GetInt32() >= 1);
                var diagnostic = Assert.Single(
                    extractors.GetProperty("diagnostics").EnumerateArray(),
                    item => item.GetProperty("path").GetString()?.EndsWith("broken.yaml", StringComparison.Ordinal) == true);
                Assert.Equal("pattern", diagnostic.GetProperty("kind").GetString());
                Assert.Equal("error", diagnostic.GetProperty("severity").GetString());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void MeasureReadableFileBytes_ReportsSkippedUnreadablePaths()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var readable = Path.Combine(projectRoot, "readable.txt");
            File.WriteAllText(readable, "abc");
            var diagnostics = new List<string>();

            var summary = IndexCommandRunner.MeasureReadableFileBytes(
                [readable, "bad\0path.txt"],
                projectRoot,
                diagnostics);

            Assert.Equal(3, summary.BytesRead);
            Assert.Equal(1, summary.SkippedFileCount);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Contains("file_size_bytes_skipped", diagnostic, StringComparison.Ordinal);
            Assert.Contains("ArgumentException", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void MeasureReadableFileBytes_UsesKnownSizeWithoutProbingPath()
    {
        var diagnostics = new List<string>();

        var summary = IndexCommandRunner.MeasureReadableFileBytes(
            ["bad\0path.txt"],
            diagnostics: diagnostics,
            knownFileSizes: new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["bad\0path.txt"] = 7,
            });

        Assert.Equal(7, summary.BytesRead);
        Assert.Equal(0, summary.SkippedFileCount);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MeasureReadableFileBytes_AppliesSelectorBeforeKnownSizeLookup()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var readable = Path.Combine(projectRoot, "readable.txt");
            var diagnostics = new List<string>();

            var summary = IndexCommandRunner.MeasureReadableFileBytes(
                ["readable.txt"],
                path => Path.Combine(projectRoot, path),
                projectRoot,
                diagnostics,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    [readable] = 11,
                });

            Assert.Equal(11, summary.BytesRead);
            Assert.Equal(0, summary.SkippedFileCount);
            Assert.Empty(diagnostics);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void FormatIndexRunDiagnostic_CollapsesAndBoundsExceptionMessages()
    {
        var message = "first line\n" + new string('x', 700);

        var diagnostic = IndexCommandRunner.FormatIndexRunDiagnostic(
            "indexed_head_metadata_write_failed",
            new IOException(message));

        Assert.StartsWith("indexed_head_metadata_write_failed: IOException: first line ", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', diagnostic);
        Assert.EndsWith("...<truncated>", diagnostic, StringComparison.Ordinal);
        Assert.True(diagnostic.Length <= 512 + "...<truncated>".Length);
    }

    [Fact]
    public void Run_GitRepo_PersistsIndexedHeadMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_head_meta");
        var fixedNow = new DateTimeOffset(2026, 6, 20, 12, 34, 56, TimeSpan.Zero);
        IndexCommandRunner.TimeProvider = new ManualTimeProvider(fixedNow);
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "checkout", "-B", "main");
            RunGit(projectRoot, "add", "app.py");
            RunGit(projectRoot, "commit", "-m", "initial");

            var expectedSha = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(expectedSha, db.GetMetaString(DbContext.IndexedHeadShaMetaKey));
            Assert.Equal("main", db.GetMetaString(DbContext.IndexedHeadBranchMetaKey));
            var stamp = db.GetMetaString(DbContext.IndexedHeadTimestampMetaKey);
            Assert.Equal(fixedNow.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture), stamp);
            Assert.True(
                DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _),
                $"timestamp not ISO-8601 parseable: {stamp}");
        }
        finally
        {
            IndexCommandRunner.TimeProvider = TimeProvider.System;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_NonGitRepo_DoesNotPersistIndexedHeadMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_head_meta_none");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Null(db.GetMetaString(DbContext.IndexedHeadShaMetaKey));
            Assert.Null(db.GetMetaString(DbContext.IndexedHeadBranchMetaKey));
            Assert.Null(db.GetMetaString(DbContext.IndexedHeadTimestampMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_PersistsWorkspacePathCaseSensitivity()
    {
        // #1546: every successful `cdidx index` stamps the workspace's filesystem
        // case-sensitivity so `status` can audit the trust decision that
        // `PathsEqual` / `IsPathEqualOrParent` made at index time.
        // #1546: index 成功時に case-sensitivity を stamp し、status から監査可能にする。
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_path_case");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");

            var (exitCode, _) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var stamp = db.GetMetaString(DbContext.WorkspacePathCaseSensitiveMetaKey);
            Assert.False(string.IsNullOrWhiteSpace(stamp));
            Assert.True(
                bool.TryParse(stamp, out _),
                $"path-case stamp must be a parseable bool: {stamp}");
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_GitRepo_DetachedHead_PersistsShaButNotBranch()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_head_meta_detached");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "checkout", "-B", "main");
            RunGit(projectRoot, "add", "app.py");
            RunGit(projectRoot, "commit", "-m", "initial");

            var expectedSha = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            RunGit(projectRoot, "checkout", "--detach", expectedSha);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(expectedSha, db.GetMetaString(DbContext.IndexedHeadShaMetaKey));
            Assert.Null(db.GetMetaString(DbContext.IndexedHeadBranchMetaKey));
            Assert.False(string.IsNullOrWhiteSpace(db.GetMetaString(DbContext.IndexedHeadTimestampMetaKey)));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_PlainPathContainingImmutableSuffix_IndexesSuccessfully()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_plain_path_{Guid.NewGuid():N}?immutable=1");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_PlainPathContainingReadOnlyModeSuffix_IndexesSuccessfully()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_plain_path_{Guid.NewGuid():N}?mode=ro");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(File.Exists(dbPath));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }














    [Fact]
    public void RunBackfillFold_MissingDb_PrintsActionableHint()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_missing_db_{Guid.NewGuid():N}.db");
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = IndexCommandRunner.RunBackfillFold(["--db", missingDb], _jsonOptions);

                Assert.Equal(CommandExitCodes.NotFound, exitCode);
                Assert.Contains("database file was not found", stderr.ToString());
                Assert.Contains("Create or refresh the index", stderr.ToString());
                Assert.Contains("<redacted>", stderr.ToString());
                Assert.DoesNotContain(missingDb, stderr.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    [Fact]
    public void RunBackfillFold_MissingDb_JsonIncludesHint()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_missing_db_{Guid.NewGuid():N}.db");
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var stdout = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                var exitCode = IndexCommandRunner.RunBackfillFold(["--db", missingDb, "--json"], _jsonOptions);
                using var document = JsonDocument.Parse(stdout.ToString());
                var json = document.RootElement;

                Assert.Equal(CommandExitCodes.NotFound, exitCode);
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.DbNotFound, json.GetProperty("error_code").GetString());
                Assert.Equal("database_missing", json.GetProperty("category").GetString());
                Assert.Equal("1", json.GetProperty("database_error_classifier_version").GetString());
                Assert.Equal("<redacted>", json.GetProperty("path").GetString());
                Assert.True(json.GetProperty("path_redacted").GetBoolean());
                Assert.Contains("database file was not found", json.GetProperty("message").GetString());
                Assert.Contains("Create or refresh the index", json.GetProperty("hint").GetString());
                Assert.False(Directory.Exists(missingDb + ".checkpoints"));
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [PublishedTrimmedCliFact]
    public void RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson()
    {
        var dbPath = CreateTempDbPath("cdidx_trimmed_backfill");
        var missingDbPath = CreateTempDbPath("cdidx_trimmed_missing");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "bootstrap", Line = 2, StartLine = 2, EndLine = 2 },
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "CAFÉ_INIT",
                        ReferenceKind = "call",
                        Line = 2,
                        Column = 5,
                        Context = "CAFÉ_INIT()",
                        ContainerKind = "function",
                        ContainerName = "bootstrap",
                    },
                ]);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
            }

            var publishedCli = TrimmedCliTestHelper.SharedTrimmedCli;

            JsonElement successJson;
            int successExitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var (exitCode, stdoutText, stderrText) = TrimmedCliTestHelper.RunPublishedCli(publishedCli, publishedCli.PublishDirectory, "backfill-fold", "--db", dbPath, "--json");
                    successExitCode = exitCode;
                    Assert.True(!string.IsNullOrWhiteSpace(stdoutText), $"published backfill-fold produced no stdout. stderr={stderrText}");
                    using var document = JsonDocument.Parse(stdoutText);
                    successJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, successExitCode);
            Assert.Equal(2, successJson.GetProperty("symbols").GetInt32());
            Assert.Equal(1, successJson.GetProperty("symbol_references").GetInt32());
            Assert.True(successJson.GetProperty("fold_ready").GetBoolean());

            JsonElement errorJson;
            int errorExitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var (exitCode, stdoutText, stderrText) = TrimmedCliTestHelper.RunPublishedCli(publishedCli, publishedCli.PublishDirectory, "backfill-fold", "--db", missingDbPath, "--json");
                    errorExitCode = exitCode;
                    Assert.True(!string.IsNullOrWhiteSpace(stdoutText), $"published backfill-fold error path produced no stdout. stderr={stderrText}");
                    using var document = JsonDocument.Parse(stdoutText);
                    errorJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.NotFound, errorExitCode);
            Assert.Equal("error", errorJson.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.DbNotFound, errorJson.GetProperty("error_code").GetString());
            Assert.Equal("database_missing", errorJson.GetProperty("category").GetString());
            Assert.Equal("1", errorJson.GetProperty("database_error_classifier_version").GetString());
            Assert.Equal("<redacted>", errorJson.GetProperty("path").GetString());
            Assert.True(errorJson.GetProperty("path_redacted").GetBoolean());
            Assert.Contains("Create or refresh the index", errorJson.GetProperty("hint").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(missingDbPath);
        }
    }

    [ProductionRuntimeFact]
    public void Run_ReadOnlyUriDbPath_PrintsActionableErrorInsteadOfCrashing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (exitCode, stdout, stderr) = RunCliInSubprocess([projectRoot, "--db", readOnlyUri, "--json"], projectRoot);

            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("database must be writable for index", json.GetProperty("message").GetString());
            Assert.Contains("Point `--db` at a writable filesystem path", json.GetProperty("hint").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }


    [ProductionRuntimeFact]
    public void Run_ReadOnlyDbFile_ReturnsDatabaseErrorWithoutStackTrace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SqliteConnection.ClearAllPools();
            SetUnixPermissions(dbPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "app.cs"), DateTime.UtcNow.AddSeconds(2));

            var (exitCode, stdout, stderr) = RunCliInSubprocess([projectRoot, "--files", "app.cs", "--json"], projectRoot);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            AssertNoRawStackTrace(stderr);
            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.DbNotWritable, json.GetProperty("error_code").GetString());
            Assert.Contains(dbPath, json.GetProperty("message").GetString());
            Assert.Contains("writable", json.GetProperty("hint").GetString());
        }
        finally
        {
            if (File.Exists(dbPath))
                SetUnixPermissions(dbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_OversizedFileUriQueryDbPath_ReturnsBoundedJsonError_Issue3140()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var dbUri = new Uri(dbPath).AbsoluteUri + "?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

            var (exitCode, stdout, stderr) = RunCliInSubprocess([projectRoot, "--db", dbUri, "--json"], projectRoot);

            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.DbError, json.GetProperty("error_code").GetString());
            Assert.Contains("invalid --db file URI for index", json.GetProperty("message").GetString());
            Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", json.GetProperty("message").GetString());
            Assert.Contains("supported limits", json.GetProperty("hint").GetString());
            Assert.DoesNotContain(new string('a', SqliteFileUri.MaxDiagnosticValueLength + 1), stdout);
            Assert.False(Directory.Exists(Path.Combine(projectRoot, ".cdidx")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_MissingCdidxDirectoryInReadOnlyProject_ReturnsDatabaseErrorWithoutStackTrace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            SetUnixPermissions(projectRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var (exitCode, stdout, stderr) = RunCliInSubprocess([projectRoot, "--json"], projectRoot);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            AssertNoRawStackTrace(stderr);
            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.DbNotWritable, json.GetProperty("error_code").GetString());
            Assert.Contains(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), json.GetProperty("message").GetString());
            Assert.Contains("writable", json.GetProperty("hint").GetString());
        }
        finally
        {
            if (Directory.Exists(projectRoot))
                SetUnixPermissions(projectRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_ExplicitDbInReadOnlyParent_ReturnsDatabaseErrorWithoutStackTrace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var dbParent = TestProjectHelper.CreateTempProject("cdidx_readonly_db_parent");
        var dbPath = Path.Combine(dbParent, "codeindex.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            SetUnixPermissions(dbParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var (exitCode, stdout, stderr) = RunCliInSubprocess([projectRoot, "--db", dbPath, "--json"], projectRoot);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            AssertNoRawStackTrace(stderr);
            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.DbNotWritable, json.GetProperty("error_code").GetString());
            Assert.Contains(dbPath, json.GetProperty("message").GetString());
            Assert.Contains("writable", json.GetProperty("hint").GetString());
        }
        finally
        {
            if (Directory.Exists(dbParent))
                SetUnixPermissions(dbParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
            DeleteDirectory(dbParent);
        }
    }

    [Fact]
    public void RunOptimizeFts_DryRunPreviewsWithoutWritingThenOptimizeMutates_Issue4577()
    {
        var dbPath = CreateTempDbPath("cdidx_optimize_fts");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/Preview.cs",
                    Lang = "csharp",
                    Size = 25,
                    Lines = 1,
                    Checksum = "preview-checksum",
                    Modified = DateTime.UtcNow,
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 1,
                        Content = "public sealed class Preview; // 日本語",
                    },
                ]);
                for (var i = 0; i < DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold; i++)
                    writer.RecordFtsIncrementalWrite();
            }

            SqliteConnection.ClearAllPools();
            var dbBytesBeforePreview = File.ReadAllBytes(dbPath);
            int previewExitCode;
            JsonElement previewJson;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    previewExitCode = IndexCommandRunner.RunOptimizeFts(
                        ["--db", dbPath, "--dry-run", "--json"],
                        _jsonOptions,
                        forceLogicalObjectSizeFallbackForTesting: true);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    previewJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, previewExitCode);
            Assert.Equal("dry_run", previewJson.GetProperty("status").GetString());
            Assert.True(previewJson.GetProperty("dry_run").GetBoolean());
            Assert.Equal(dbPath, previewJson.GetProperty("db_path").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                previewJson.GetProperty("writes_since_optimize_before").GetInt32());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                previewJson.GetProperty("writes_since_optimize_after").GetInt32());
            var previewRecommendation = previewJson.GetProperty("fts_optimization");
            Assert.True(previewRecommendation.GetProperty("recommended").GetBoolean());
            Assert.Equal("optimize", previewRecommendation.GetProperty("action").GetString());
            Assert.Equal("incremental_write_threshold_reached", previewRecommendation.GetProperty("reason").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                previewRecommendation.GetProperty("threshold_writes").GetInt32());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                previewRecommendation.GetProperty("observed_writes").GetInt64());
            Assert.Equal("current", previewRecommendation.GetProperty("state").GetString());
            Assert.True(previewJson.GetProperty("db_size_bytes").GetInt64() > 0);
            Assert.True(previewJson.GetProperty("page_count").GetInt64() > 0);
            Assert.True(previewJson.GetProperty("object_sizes_available").GetBoolean());
            Assert.Equal("logical_payload_bytes", previewJson.GetProperty("object_sizes_measurement").GetString());
            Assert.True(
                previewJson.GetProperty("object_size_bytes").GetProperty("chunks").GetInt64()
                >= Encoding.UTF8.GetByteCount("public sealed class Preview; // 日本語"));
            Assert.True(previewJson.GetProperty("fts_size_bytes").GetInt64() > 0);
            Assert.Equal("available", previewJson.GetProperty("lock_state").GetString());
            Assert.True(previewJson.GetProperty("would_acquire_exclusive_index_lock").GetBoolean());
            Assert.True(previewJson.GetProperty("source_database_unchanged").GetBoolean());
            Assert.False(previewJson.GetProperty("readiness").GetProperty("fold_ready").GetBoolean());
            Assert.Contains(
                previewJson.GetProperty("planned_operations").EnumerateArray().Select(item => item.GetString()),
                operation => operation == "merge_fts5_segments");
            Assert.Contains(
                previewJson.GetProperty("planned_operations").EnumerateArray().Select(item => item.GetString()),
                operation => operation == "initialize_or_migrate_schema");
            Assert.Equal(dbBytesBeforePreview, File.ReadAllBytes(dbPath));
            Assert.False(File.Exists(IndexLock.GetLockPath(dbPath)));
            Assert.False(File.Exists(IndexLock.GetInfoPath(IndexLock.GetLockPath(dbPath))));

            using (var statusDb = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            {
                var statusRecommendation = new DbReader(statusDb).GetStatus().MaintenanceGuidance.FtsOptimization;
                Assert.Equal(previewRecommendation.GetProperty("recommended").GetBoolean(), statusRecommendation.Recommended);
                Assert.Equal(previewRecommendation.GetProperty("action").GetString(), statusRecommendation.Action);
                Assert.Equal(previewRecommendation.GetProperty("reason").GetString(), statusRecommendation.Reason);
                Assert.Equal(previewRecommendation.GetProperty("threshold_writes").GetInt32(), statusRecommendation.ThresholdWrites);
                Assert.Equal(previewRecommendation.GetProperty("observed_writes").GetInt64(), statusRecommendation.ObservedWrites);
                Assert.Equal(previewRecommendation.GetProperty("state").GetString(), statusRecommendation.State);
            }

            int humanPreviewExitCode;
            string humanPreviewOutput;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    humanPreviewExitCode = IndexCommandRunner.RunOptimizeFts(
                        ["--db", dbPath, "--dry-run"],
                        _jsonOptions,
                        forceLogicalObjectSizeFallbackForTesting: true);
                    humanPreviewOutput = stdout.ToString();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, humanPreviewExitCode);
            Assert.Contains("Core size", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("Readiness", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("Est. duration", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("action=optimize", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("reason=incremental_write_threshold_reached", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("threshold=25", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("observed=25", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("state=current", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("Planned operations", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Contains("initialize_or_migrate_schema", humanPreviewOutput, StringComparison.Ordinal);
            Assert.Equal(dbBytesBeforePreview, File.ReadAllBytes(dbPath));

            int exitCode;
            JsonElement json;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    exitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                json.GetProperty("writes_since_optimize_before").GetInt32());
            Assert.Equal(0, json.GetProperty("writes_since_optimize_after").GetInt32());
            Assert.Equal(8, json.EnumerateObject().Count());
            Assert.False(json.TryGetProperty("dry_run", out _));
            Assert.False(json.TryGetProperty("planned_operations", out _));
            var beforeRecommendation = json.GetProperty("fts_optimization_before");
            Assert.True(beforeRecommendation.GetProperty("recommended").GetBoolean());
            Assert.Equal("optimize", beforeRecommendation.GetProperty("action").GetString());
            Assert.Equal("incremental_write_threshold_reached", beforeRecommendation.GetProperty("reason").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                beforeRecommendation.GetProperty("threshold_writes").GetInt32());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                beforeRecommendation.GetProperty("observed_writes").GetInt64());
            var afterRecommendation = json.GetProperty("fts_optimization_after");
            Assert.False(afterRecommendation.GetProperty("recommended").GetBoolean());
            Assert.Equal("none", afterRecommendation.GetProperty("action").GetString());
            Assert.Equal("incremental_write_threshold_not_reached", afterRecommendation.GetProperty("reason").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                afterRecommendation.GetProperty("threshold_writes").GetInt32());
            Assert.Equal(0, afterRecommendation.GetProperty("observed_writes").GetInt64());
            Assert.Equal("current", afterRecommendation.GetProperty("state").GetString());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal("0", verifyDb.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbWriter.FtsLastOptimizedAtMetaKey)));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbWriter.FtsLastOptimizeDurationMsMetaKey)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunOptimizeFts_DryRunCountsTrigramFtsSizes_Issue4725(
        bool forceLogicalObjectSizeFallbackForTesting)
    {
        var dbPath = CreateTempDbPath("cdidx_optimize_trigram_preview");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/TrigramPreview.cs",
                    Lang = "csharp",
                    Size = 31,
                    Lines = 1,
                    Checksum = "trigram-preview-checksum",
                    Modified = DateTime.UtcNow,
                });
                writer.InsertChunks([
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 1,
                        Content = "public sealed class TrigramPreview;",
                    },
                ]);
            }

            SqliteConnection.ClearAllPools();
            int exitCode;
            JsonElement json;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    exitCode = IndexCommandRunner.RunOptimizeFts(
                        ["--db", dbPath, "--dry-run", "--json"],
                        _jsonOptions,
                        forceLogicalObjectSizeFallbackForTesting);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var measurement = json.GetProperty("object_sizes_measurement").GetString();
            Assert.Contains(measurement, new[] { "dbstat_page_bytes", "logical_payload_bytes" });
            if (forceLogicalObjectSizeFallbackForTesting)
                Assert.Equal("logical_payload_bytes", measurement);
            var objectSizes = json.GetProperty("object_size_bytes");
            Assert.True(objectSizes.GetProperty("fts_chunks_trigram_data").GetInt64() > 0);
            var measuredFtsSize = objectSizes
                .EnumerateObject()
                .Where(property => property.Name.StartsWith("fts_chunks_", StringComparison.Ordinal))
                .Sum(property => property.Value.GetInt64());
            Assert.Equal(measuredFtsSize, json.GetProperty("fts_size_bytes").GetInt64());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunOptimizeFts_DryRunLogicalFallbackMeasuresLegacySchema_Issue4577()
    {
        var dbPath = CreateTempDbPath("cdidx_optimize_legacy_preview");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    PRAGMA application_id={DbContext.ApplicationId};
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY,
                        path TEXT NOT NULL,
                        lang TEXT,
                        size INTEGER,
                        lines INTEGER,
                        checksum TEXT,
                        modified TEXT,
                        indexed_at TEXT
                    );
                    CREATE TABLE chunks (
                        id INTEGER PRIMARY KEY,
                        file_id INTEGER,
                        chunk_index INTEGER,
                        start_line INTEGER,
                        end_line INTEGER,
                        content TEXT
                    );
                    CREATE TABLE symbols (
                        id INTEGER PRIMARY KEY,
                        file_id INTEGER,
                        kind TEXT,
                        name TEXT,
                        line INTEGER,
                        signature TEXT
                    );
                    INSERT INTO files (id, path, lang, size, lines, checksum, modified, indexed_at)
                    VALUES (1, 'src/Legacy.cs', 'csharp', 12, 1, 'legacy', '2020-01-01T00:00:00Z', '2020-01-01T00:00:00Z');
                    INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content)
                    VALUES (1, 0, 1, 1, '古いチャンク');
                    INSERT INTO symbols (file_id, kind, name, line, signature)
                    VALUES (1, 'class', '旧型', 1, 'class 旧型');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();
            var bytesBefore = File.ReadAllBytes(dbPath);
            int exitCode;
            JsonElement json;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    exitCode = IndexCommandRunner.RunOptimizeFts(
                        ["--db", dbPath, "--dry-run", "--json"],
                        _jsonOptions,
                        forceLogicalObjectSizeFallbackForTesting: true);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("logical_payload_bytes", json.GetProperty("object_sizes_measurement").GetString());
            Assert.True(
                json.GetProperty("object_size_bytes").GetProperty("symbols").GetInt64()
                >= Encoding.UTF8.GetByteCount("class旧型class 旧型"));
            Assert.Contains(
                json.GetProperty("planned_operations").EnumerateArray().Select(item => item.GetString()),
                operation => operation == "initialize_or_migrate_schema");
            var recommendation = json.GetProperty("fts_optimization");
            Assert.False(recommendation.GetProperty("recommended").GetBoolean());
            Assert.Equal("none", recommendation.GetProperty("action").GetString());
            Assert.Equal("incremental_write_count_unavailable", recommendation.GetProperty("reason").GetString());
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold,
                recommendation.GetProperty("threshold_writes").GetInt32());
            Assert.Equal(0, recommendation.GetProperty("observed_writes").GetInt64());
            Assert.Equal("unavailable", recommendation.GetProperty("state").GetString());
            Assert.Equal(bytesBefore, File.ReadAllBytes(dbPath));
            Assert.False(File.Exists(IndexLock.GetLockPath(dbPath)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunOptimizeFts_LockHeld_ReportsDbLocked()
    {
        var dbPath = CreateTempDbPath("cdidx_optimize_locked");
        var lockPath = dbPath + ".lock";
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                db.InitializeSchema();

            using (var holder = IndexLock.Acquire(lockPath, Path.GetDirectoryName(dbPath)!))
            {
                int previewExitCode;
                JsonElement previewJson;
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    try
                    {
                        using var stdout = new StringWriter();
                        Console.SetOut(stdout);
                        previewExitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbPath, "--dry-run", "--json"], _jsonOptions);
                        using var document = JsonDocument.Parse(stdout.ToString());
                        previewJson = document.RootElement.Clone();
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }

                Assert.Equal(CommandExitCodes.Success, previewExitCode);
                Assert.Equal("locked", previewJson.GetProperty("lock_state").GetString());
                Assert.True(previewJson.GetProperty("source_database_unchanged").GetBoolean());

                int exitCode;
                JsonElement json;
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    try
                    {
                        using var stdout = new StringWriter();
                        Console.SetOut(stdout);
                        exitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbPath, "--json"], _jsonOptions);
                        using var document = JsonDocument.Parse(stdout.ToString());
                        json = document.RootElement.Clone();
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }

                Assert.Equal(CommandExitCodes.TransientDatabaseError, exitCode);
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.DbLocked, json.GetProperty("error_code").GetString());
                Assert.Equal("database_locked", json.GetProperty("category").GetString());
                Assert.Equal("database is locked or busy", json.GetProperty("message").GetString());
                Assert.Contains("retry with backoff", json.GetProperty("hint").GetString());
                Assert.Equal("<redacted>", json.GetProperty("path").GetString());
                Assert.True(json.GetProperty("path_redacted").GetBoolean());
                var detail = Assert.Single(json.GetProperty("details").EnumerateArray());
                Assert.Contains($"PID {Environment.ProcessId}", detail.GetString());
                Assert.DoesNotContain(dbPath, detail.GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(lockPath + ".info");
            DeleteFile(lockPath);
        }
    }

    [Fact]
    public void RunOptimizeFts_MissingRelativePath_PreservesCallerSpelling_Issue4856()
    {
        var dbPath = $"cdidx_optimize_relative_missing_{Guid.NewGuid():N}.db";
        try
        {
            int exitCode;
            JsonElement json;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                try
                {
                    using var stdout = new StringWriter();
                    Console.SetOut(stdout);
                    exitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Equal(CommandErrorCodes.DbNotFound, json.GetProperty("error_code").GetString());
            Assert.Equal(dbPath, json.GetProperty("path").GetString());
            Assert.False(json.GetProperty("path_redacted").GetBoolean());
        }
        finally
        {
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_IndexOptimizeMissingDatabase_RedactsHumanPreambleUnlessEnabled_Issue4856()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, "private", "missing.db");
        try
        {
            var (exitCode, stdout, stderr) = RunAndCaptureStreams(
                [projectRoot, "--optimize", "--db", dbPath]);
            var output = stdout + stderr;

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Contains("<redacted>", output);
            Assert.Contains(CommandErrorCodes.DbNotFound, output);
            Assert.DoesNotContain(projectRoot, output, StringComparison.Ordinal);
            Assert.DoesNotContain(dbPath, output, StringComparison.Ordinal);

            var (diagnosticExitCode, diagnosticStdout, diagnosticStderr) = RunAndCaptureStreams(
                [projectRoot, "--optimize", "--db", dbPath, "--show-paths"]);
            var diagnosticOutput = diagnosticStdout + diagnosticStderr;

            Assert.Equal(CommandExitCodes.NotFound, diagnosticExitCode);
            Assert.Contains(projectRoot, diagnosticOutput, StringComparison.Ordinal);
            Assert.Contains(dbPath, diagnosticOutput, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunOptimizeFts_ReadOnlyUri_PreservesImmutableFreshnessAndReturnsDbNotWritable_Issue4887()
    {
        var dbPath = CreateTempDbPath("cdidx_optimize_readonly");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                using var checkpoint = db.Connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            using (var hotWalConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString()))
            {
                hotWalConnection.Open();
                using (var command = hotWalConnection.CreateCommand())
                {
                    command.CommandText = """
                        PRAGMA journal_mode=WAL;
                        PRAGMA wal_autocheckpoint=0;
                        INSERT INTO codeindex_meta(key, value)
                        VALUES (@key, @value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    command.Parameters.AddWithValue(
                        "@key",
                        DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey);
                    command.Parameters.AddWithValue(
                        "@value",
                        DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold.ToString(CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }
                Assert.True(File.Exists(dbPath + "-wal"));

                FtsOptimizationRecommendation statusRecommendation;
                using (var statusDb = new DbContext(DbOpenIntent.QueryOnly, dbUri))
                    statusRecommendation = new DbReader(statusDb).GetStatus().MaintenanceGuidance.FtsOptimization;
                Assert.False(statusRecommendation.Recommended);
                Assert.Equal(
                    FtsOptimizationRecommendationEvaluator.MaintenanceSnapshotStaleReason,
                    statusRecommendation.Reason);
                Assert.Equal(
                    FtsOptimizationRecommendationEvaluator.StaleState,
                    statusRecommendation.State);

                int previewExitCode;
                JsonElement previewJson;
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    try
                    {
                        using var stdout = new StringWriter();
                        Console.SetOut(stdout);
                        previewExitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbUri, "--dry-run", "--json"], _jsonOptions);
                        using var document = JsonDocument.Parse(stdout.ToString());
                        previewJson = document.RootElement.Clone();
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }

                Assert.Equal(CommandExitCodes.Success, previewExitCode);
                Assert.Equal("dry_run", previewJson.GetProperty("status").GetString());
                Assert.True(previewJson.GetProperty("source_database_unchanged").GetBoolean());
                var previewRecommendation = previewJson.GetProperty("fts_optimization");
                Assert.Equal(
                    statusRecommendation.Recommended,
                    previewRecommendation.GetProperty("recommended").GetBoolean());
                Assert.Equal(
                    statusRecommendation.Action,
                    previewRecommendation.GetProperty("action").GetString());
                Assert.Equal(
                    statusRecommendation.Reason,
                    previewRecommendation.GetProperty("reason").GetString());
                Assert.Equal(
                    statusRecommendation.ThresholdWrites,
                    previewRecommendation.GetProperty("threshold_writes").GetInt32());
                Assert.Equal(
                    statusRecommendation.ObservedWrites,
                    previewRecommendation.GetProperty("observed_writes").GetInt64());
                Assert.Equal(
                    statusRecommendation.State,
                    previewRecommendation.GetProperty("state").GetString());

                int aliasPreviewExitCode;
                JsonElement aliasPreviewJson;
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    try
                    {
                        using var stdout = new StringWriter();
                        Console.SetOut(stdout);
                        aliasPreviewExitCode = IndexCommandRunner.Run(
                            [
                                Path.GetDirectoryName(dbPath)!,
                                "--optimize",
                                "--dry-run",
                                "--db",
                                dbUri,
                                "--json",
                            ],
                            _jsonOptions);
                        using var document = JsonDocument.Parse(stdout.ToString());
                        aliasPreviewJson = document.RootElement.Clone();
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }

                Assert.Equal(CommandExitCodes.Success, aliasPreviewExitCode);
                Assert.Equal("dry_run", aliasPreviewJson.GetProperty("status").GetString());
                Assert.True(aliasPreviewJson.GetProperty("source_database_unchanged").GetBoolean());
                var aliasPreviewRecommendation = aliasPreviewJson.GetProperty("fts_optimization");
                Assert.Equal(
                    statusRecommendation.Recommended,
                    aliasPreviewRecommendation.GetProperty("recommended").GetBoolean());
                Assert.Equal(
                    statusRecommendation.Action,
                    aliasPreviewRecommendation.GetProperty("action").GetString());
                Assert.Equal(
                    statusRecommendation.Reason,
                    aliasPreviewRecommendation.GetProperty("reason").GetString());
                Assert.Equal(
                    statusRecommendation.ThresholdWrites,
                    aliasPreviewRecommendation.GetProperty("threshold_writes").GetInt32());
                Assert.Equal(
                    statusRecommendation.ObservedWrites,
                    aliasPreviewRecommendation.GetProperty("observed_writes").GetInt64());
                Assert.Equal(
                    statusRecommendation.State,
                    aliasPreviewRecommendation.GetProperty("state").GetString());

                int exitCode;
                JsonElement json;
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    try
                    {
                        using var stdout = new StringWriter();
                        Console.SetOut(stdout);
                        exitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbUri, "--json"], _jsonOptions);
                        using var document = JsonDocument.Parse(stdout.ToString());
                        json = document.RootElement.Clone();
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.DbNotWritable, json.GetProperty("error_code").GetString());
                Assert.Equal("database_not_writable", json.GetProperty("category").GetString());
                Assert.Equal("database is not writable", json.GetProperty("message").GetString());
            }

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                DbWriter.DefaultFtsOptimizeIncrementalWriteThreshold.ToString(CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunOptimizeFts_OversizedFileUriQuery_ReturnsBoundedJsonError_Issue3140()
    {
        var dbPath = CreateTempDbPath("cdidx_optimize_uri_cap");
        var dbUri = new Uri(dbPath).AbsoluteUri + "?" + new string('a', SqliteFileUri.MaxQueryLength + 1);
        int exitCode;
        JsonElement json;
        string stderr;
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            try
            {
                using var stdout = new StringWriter();
                using var stderrWriter = new StringWriter();
                Console.SetOut(stdout);
                Console.SetError(stderrWriter);
                exitCode = IndexCommandRunner.RunOptimizeFts(["--db", dbUri, "--json"], _jsonOptions);
                stderr = stderrWriter.ToString();
                using var document = JsonDocument.Parse(stdout.ToString());
                json = document.RootElement.Clone();
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal(CommandErrorCodes.DbError, json.GetProperty("error_code").GetString());
        Assert.Contains("invalid --db file URI for optimize", json.GetProperty("message").GetString());
        Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", json.GetProperty("message").GetString());
        Assert.Contains("supported limits", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_IndexOptimizeWithDryRun_PreviewsWithoutWriting_Issue4577()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db);
                writer.RecordFtsIncrementalWrite();
                writer.SetMeta(DbWriter.FtsLastOptimizedAtMetaKey, "sentinel");
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--optimize", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.True(json.GetProperty("source_database_unchanged").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal("1", verifyDb.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
            Assert.Equal("sentinel", verifyDb.GetMetaString(DbWriter.FtsLastOptimizedAtMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBackfillFold_BackfillsLegacyRowsAndStampsFoldReady()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "bootstrap", Line = 2, StartLine = 2, EndLine = 2 },
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "CAFÉ_INIT",
                        ReferenceKind = "call",
                        Line = 2,
                        Column = 5,
                        Context = "CAFÉ_INIT()",
                        ContainerKind = "function",
                        ContainerName = "bootstrap",
                    },
                ]);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
            }

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL; UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL; PRAGMA user_version = 3";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(2, json.GetProperty("symbols").GetInt32());
            Assert.Equal(1, json.GetProperty("symbol_references").GetInt32());
            Assert.True(json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(json.GetProperty("verified").GetBoolean());
            Assert.Equal(27, json.GetProperty("user_version_before").GetInt32());
            Assert.Equal(31, json.GetProperty("user_version_after").GetInt32());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.False(json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.False(json.TryGetProperty("checkpoint_skipped_reason", out _));
            var checkpointPath = Assert.Single(Directory.GetDirectories(dbPath + ".checkpoints"));
            Assert.True(File.Exists(Path.Combine(checkpointPath, Path.GetFileName(dbPath))));
            Assert.True(File.Exists(Path.Combine(checkpointPath, "manifest.txt")));

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            verifyDb.TryMigrateForRead();
            var reader = new DbReader(verifyDb.Connection);
            Assert.True(reader._foldReady);
            Assert.Single(reader.SearchSymbols(["ＣＡＦÉ_ＩＮＩＴ"], limit: 10, exact: true));
            Assert.Single(reader.GetCallers("ＣＡＦÉ_ＩＮＩＴ", limit: 10, exact: true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(dbPath + ".checkpoints");
        }
    }

    [Fact]
    public void RunBackfillFold_MixedMissingAndNonCurrentRows_RewritesAll_Issue4946()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_mixed_4946");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var csharpFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/App.cs",
                    Lang = "csharp",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                var markdownFileId = writer.UpsertFile(new FileRecord
                {
                    Path = "changelog.d/unreleased/4946.fixed.md",
                    Lang = "markdown",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = csharpFileId, Kind = "function", Name = "MissingMember", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = markdownFileId, Kind = "heading", Name = "Current heading", Line = 1, StartLine = 1, EndLine = 2 },
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = csharpFileId,
                        SymbolName = "MissingMember",
                        ReferenceKind = "call",
                        Line = 2,
                        Column = 5,
                        Context = "MissingMember()",
                        ContainerKind = "function",
                        ContainerName = "CurrentContainer",
                    },
                ]);
                writer.BackfillFoldedColumns(rewriteAll: true);
                Assert.True(writer.MarkFoldReady());
                writer.MarkCSharpSymbolNameContractReady();

                using var corrupt = db.Connection.CreateCommand();
                corrupt.CommandText = """
                    UPDATE symbols
                    SET name_folded = CASE
                        WHEN name = 'MissingMember' THEN NULL
                        ELSE 'stale-non-current-fold'
                    END;
                    UPDATE symbol_references
                    SET symbol_name_folded = NULL,
                        container_name_folded = 'stale-non-current-fold';
                    """;
                corrupt.ExecuteNonQuery();
            }

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(output.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(2, json.GetProperty("symbols").GetInt32());
            Assert.Equal(1, json.GetProperty("symbol_references").GetInt32());
            Assert.True(json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(json.GetProperty("verified").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var verifyWriter = new DbWriter(verifyDb.Connection);
            Assert.True(verifyWriter.AllFoldedColumnsBackfilled(requireCurrentFoldKeys: true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(dbPath + ".checkpoints");
        }
    }

    [Fact]
    public void RunBackfillFold_InterruptedPromotedRewriteResumesBeforeTargetedMode_Issue4946Review()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_promoted_resume_4946");
        using var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "first", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "second", Line = 2, StartLine = 2, EndLine = 2 },
                ]);
                writer.BackfillFoldedColumns(rewriteAll: true);
                Assert.True(writer.MarkFoldReady());

                using (var corrupt = db.Connection.CreateCommand())
                {
                    corrupt.CommandText = """
                        UPDATE symbols
                        SET name_folded = CASE
                            WHEN name = 'first' THEN 'stale-non-current-fold'
                            ELSE NULL
                        END
                        """;
                    corrupt.ExecuteNonQuery();
                }

                DbWriter.FoldBackfillRowUpdatedForTesting = cts.Cancel;
                Assert.Throws<OperationCanceledException>(
                    () => writer.BackfillFoldedColumns(rewriteAll: true, cts.Token));
                DbWriter.FoldBackfillRowUpdatedForTesting = null;
                Assert.True(writer.HasFoldBackfillRewriteCheckpoint());
            }

            var resumed = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, resumed.ExitCode);
            Assert.Equal(1, resumed.Json.GetProperty("symbols").GetInt32());
            Assert.True(resumed.Json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(resumed.Json.GetProperty("verified").GetBoolean());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                Assert.False(writer.HasFoldBackfillRewriteCheckpoint());
                Assert.True(writer.AllFoldedColumnsBackfilled(requireCurrentFoldKeys: true));

                using var corrupt = db.Connection.CreateCommand();
                corrupt.CommandText = "UPDATE symbols SET name_folded = 'stale-again' WHERE name = 'first'";
                corrupt.ExecuteNonQuery();
            }

            var laterRewrite = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, laterRewrite.ExitCode);
            Assert.Equal(2, laterRewrite.Json.GetProperty("symbols").GetInt32());
            Assert.True(laterRewrite.Json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(laterRewrite.Json.GetProperty("verified").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var verifyWriter = new DbWriter(verifyDb.Connection);
            Assert.True(verifyWriter.AllFoldedColumnsBackfilled(requireCurrentFoldKeys: true));

            (int ExitCode, JsonElement Json) RunBackfill()
            {
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        var exitCode = IndexCommandRunner.RunBackfillFold(
                            ["--db", dbPath, "--json", "--no-checkpoint"],
                            _jsonOptions);
                        using var document = JsonDocument.Parse(output.ToString());
                        return (exitCode, document.RootElement.Clone());
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }
            }
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(dbPath + ".checkpoints");
        }
    }

    [Fact]
    public void RunBackfillFold_ClearsCompletedRewriteCheckpointBeforeLaterScopedRepair_Issue4946Review2()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_completed_checkpoint_4946");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "first", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "second", Line = 2, StartLine = 2, EndLine = 2 },
                ]);
                writer.BackfillFoldedColumns(rewriteAll: true);
                Assert.True(writer.MarkFoldReady());

                // Model cancellation after the graph refresh but before the completed
                // full-rewrite checkpoint is cleared.
                writer.SetMeta("fold_backfill_phase", "references");
                writer.SetMeta("fold_backfill_last_symbol_id", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.SetMeta("fold_backfill_last_reference_id", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(writer.HasFoldBackfillRewriteCheckpoint());
            }

            var cleanup = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, cleanup.ExitCode);
            Assert.Equal(0, cleanup.Json.GetProperty("symbols").GetInt32());
            Assert.Equal(0, cleanup.Json.GetProperty("symbol_references").GetInt32());
            Assert.True(cleanup.Json.GetProperty("rewrite_all").GetBoolean());
            Assert.False(cleanup.Json.GetProperty("was_already_complete").GetBoolean());
            Assert.True(cleanup.Json.GetProperty("verified").GetBoolean());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                Assert.False(writer.HasFoldBackfillRewriteCheckpoint());
                using var scopedUpdate = db.Connection.CreateCommand();
                scopedUpdate.CommandText = "UPDATE symbols SET name_folded = NULL WHERE name = 'first'";
                Assert.Equal(1, scopedUpdate.ExecuteNonQuery());
            }

            var repair = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, repair.ExitCode);
            Assert.Equal(1, repair.Json.GetProperty("symbols").GetInt32());
            Assert.False(repair.Json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(repair.Json.GetProperty("verified").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var verifyWriter = new DbWriter(verifyDb.Connection);
            Assert.False(verifyWriter.HasFoldBackfillRewriteCheckpoint());
            Assert.True(verifyWriter.AllFoldedColumnsBackfilled(requireCurrentFoldKeys: true));

            (int ExitCode, JsonElement Json) RunBackfill()
            {
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        var exitCode = IndexCommandRunner.RunBackfillFold(
                            ["--db", dbPath, "--json", "--no-checkpoint"],
                            _jsonOptions);
                        using var document = JsonDocument.Parse(output.ToString());
                        return (exitCode, document.RootElement.Clone());
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(dbPath + ".checkpoints");
        }
    }

    [Fact]
    public void RunBackfillFold_DryRunReportsRowsWithoutWriting()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_dry");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
                ]);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL; PRAGMA user_version = 3";
                cmd.ExecuteNonQuery();
            }

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--dry-run", "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.Equal(1, json.GetProperty("symbols").GetInt32());
            Assert.False(json.GetProperty("verified").GetBoolean());
            Assert.False(json.GetProperty("fold_ready_after").GetBoolean());
            Assert.True(json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.Equal("dry_run", json.GetProperty("checkpoint_skipped_reason").GetString());
            Assert.False(Directory.Exists(dbPath + ".checkpoints"));

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var count = verifyDb.Connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbols WHERE name_folded IS NULL";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
            Assert.Equal(3, verifyDb.GetUserVersion());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_RewritesPreviousCSharpExplicitInterfaceIdentityContract_Issue4866()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_csharp_v2");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                Assert.True(writer.MarkFoldReady());
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/Explicit.cs",
                    Lang = "csharp",
                    Size = 64,
                    Lines = 1,
                    Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "Run",
                        Signature = "void IFoo.Run() { }",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "namespace",
                        Name = "CodeIndex.Tests",
                        Signature = "namespace CodeIndex.Tests;",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "class",
                        Name = "Runner",
                        Signature = "public class Runner : IFoo.Runner { }",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "Execute",
                        Signature = "public void Execute([Foo.Execute()] int value) { }",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                ]);
                writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, "2");
            }

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    exitCode = IndexCommandRunner.RunBackfillFold(
                        ["--db", dbPath, "--json"],
                        _jsonOptions);
                    using var document = JsonDocument.Parse(output.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("rewrite_all").GetBoolean());
            Assert.Equal(4, json.GetProperty("symbols").GetInt32());
            Assert.True(json.GetProperty("verified").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                DbContext.CSharpSymbolNameContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey));
            using var identity = verifyDb.Connection.CreateCommand();
            identity.CommandText = """
                SELECT name_folded, display_name_folded
                FROM symbols
                WHERE name = 'Run'
                """;
            using var identityReader = identity.ExecuteReader();
            Assert.True(identityReader.Read());
            Assert.Equal("ifoo.run", identityReader.GetString(0));
            Assert.Equal("run", identityReader.GetString(1));
            using var namespaceAlias = verifyDb.Connection.CreateCommand();
            namespaceAlias.CommandText = """
                SELECT display_name_folded
                FROM symbols
                WHERE kind = 'namespace'
                """;
            Assert.Equal(DBNull.Value, namespaceAlias.ExecuteScalar());
            using var ordinaryTypeIdentity = verifyDb.Connection.CreateCommand();
            ordinaryTypeIdentity.CommandText = """
                SELECT name_folded, display_name_folded
                FROM symbols
                WHERE kind = 'class'
                """;
            using var ordinaryTypeIdentityReader = ordinaryTypeIdentity.ExecuteReader();
            Assert.True(ordinaryTypeIdentityReader.Read());
            Assert.Equal("runner", ordinaryTypeIdentityReader.GetString(0));
            Assert.True(ordinaryTypeIdentityReader.IsDBNull(1));
            using var ordinaryMethodIdentity = verifyDb.Connection.CreateCommand();
            ordinaryMethodIdentity.CommandText = """
                SELECT name_folded, display_name_folded
                FROM symbols
                WHERE name = 'Execute'
                """;
            using var ordinaryMethodIdentityReader = ordinaryMethodIdentity.ExecuteReader();
            Assert.True(ordinaryMethodIdentityReader.Read());
            Assert.Equal("execute", ordinaryMethodIdentityReader.GetString(0));
            Assert.True(ordinaryMethodIdentityReader.IsDBNull(1));

            using var reader = new DbReader(verifyDb.Connection);
            Assert.Single(reader.SearchSymbols(
                "IFoo.Run",
                lang: "csharp",
                exact: true));
            Assert.Single(reader.SearchSymbols(
                "Run",
                lang: "csharp",
                exact: true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_RejectsNewerCSharpIdentityContractWithoutRewriting_Issue4866Review()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_csharp_future");
        var futureVersion = DbContext.CSharpSymbolNameContractVersion + 1;
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/Future.cs",
                    Lang = "csharp",
                    Size = 32,
                    Lines = 1,
                    Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "Run",
                        IdentityNameFolded = "future::ifoo.run",
                        DisplayNameFolded = "run",
                        Signature = "void IFoo.Run() { }",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                ]);
                writer.SetMeta(
                    DbContext.CSharpSymbolNameContractVersionMetaKey,
                    futureVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            string outputText;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    exitCode = IndexCommandRunner.RunBackfillFold(
                        ["--db", dbPath, "--json"],
                        _jsonOptions);
                    outputText = output.ToString();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains("newer than supported version", outputText, StringComparison.Ordinal);

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                futureVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey));
            using var identity = verifyDb.Connection.CreateCommand();
            identity.CommandText = """
                SELECT name_folded, display_name_folded
                FROM symbols
                WHERE name = 'Run'
                """;
            using var identityReader = identity.ExecuteReader();
            Assert.True(identityReader.Read());
            Assert.Equal("future::ifoo.run", identityReader.GetString(0));
            Assert.Equal("run", identityReader.GetString(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_PreservesNewerCSharpIdentityContractWithoutCSharpFiles_Issue4866Review()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_csharp_future_without_csharp");
        var futureVersion = DbContext.CSharpSymbolNameContractVersion + 1;
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 32,
                    Lines = 1,
                    Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "run",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                ]);
                Assert.True(writer.MarkFoldReady());
                writer.SetMeta(
                    DbContext.CSharpSymbolNameContractVersionMetaKey,
                    futureVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            (int ExitCode, string Output) RunBackfill(params string[] additionalArguments)
            {
                var arguments = new List<string> { "--db", dbPath, "--json" };
                arguments.AddRange(additionalArguments);
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        var exitCode = IndexCommandRunner.RunBackfillFold(
                            arguments.ToArray(),
                            _jsonOptions);
                        return (exitCode, output.ToString());
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }
            }

            var dryRun = RunBackfill("--dry-run");
            Assert.Equal(CommandExitCodes.DatabaseError, dryRun.ExitCode);
            Assert.Contains("newer than supported version", dryRun.Output, StringComparison.Ordinal);

            var run = RunBackfill();
            Assert.Equal(CommandExitCodes.DatabaseError, run.ExitCode);
            Assert.Contains("newer than supported version", run.Output, StringComparison.Ordinal);

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                futureVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey));
            using var identity = verifyDb.Connection.CreateCommand();
            identity.CommandText = "SELECT name_folded FROM symbols WHERE name = 'run'";
            Assert.Equal("run", identity.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_DoesNotRewriteCurrentFoldRowsWhenCSharpIsAbsent_Issue4866Review()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_without_csharp");
        var checkpointRoot = dbPath + ".checkpoints";
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 32,
                    Lines = 1,
                    Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "run",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                ]);
                writer.BackfillFoldedColumns(rewriteAll: true);
                Assert.True(writer.MarkFoldReady());
                writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);
            }

            (int ExitCode, JsonElement Json) RunBackfill(params string[] additionalArguments)
            {
                var arguments = new List<string> { "--db", dbPath, "--json" };
                arguments.AddRange(additionalArguments);
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        var exitCode = IndexCommandRunner.RunBackfillFold(
                            arguments.ToArray(),
                            _jsonOptions);
                        using var document = JsonDocument.Parse(output.ToString());
                        return (exitCode, document.RootElement.Clone());
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }
            }

            var noOp = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, noOp.ExitCode);
            Assert.False(noOp.Json.GetProperty("rewrite_all").GetBoolean());
            Assert.Equal(0, noOp.Json.GetProperty("symbols").GetInt32());
            Assert.Equal(0, noOp.Json.GetProperty("symbol_references").GetInt32());
            Assert.True(noOp.Json.GetProperty("was_already_complete").GetBoolean());
            Assert.True(noOp.Json.GetProperty("verified").GetBoolean());
            Assert.True(noOp.Json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.Equal(
                "already_complete",
                noOp.Json.GetProperty("checkpoint_skipped_reason").GetString());
            Assert.False(Directory.Exists(checkpointRoot));

            using (var walKeeper = new SqliteConnection($"Data Source={dbPath}"))
            {
                walKeeper.Open();
                using var walWrite = walKeeper.CreateCommand();
                walWrite.CommandText = """
                    CREATE TABLE IF NOT EXISTS issue4889_wal_probe(value INTEGER NOT NULL);
                    INSERT INTO issue4889_wal_probe(value) VALUES (1);
                    """;
                walWrite.ExecuteNonQuery();

                var forced = RunBackfill("--checkpoint");
                Assert.Equal(CommandExitCodes.Success, forced.ExitCode);
                Assert.True(forced.Json.GetProperty("was_already_complete").GetBoolean());
                Assert.False(forced.Json.GetProperty("checkpoint_skipped").GetBoolean());
                Assert.False(forced.Json.TryGetProperty("checkpoint_skipped_reason", out _));

                var forcedCheckpoint = Assert.Single(Directory.GetDirectories(checkpointRoot));
                Assert.True(File.Exists(Path.Combine(forcedCheckpoint, Path.GetFileName(dbPath))));
                Assert.True(File.Exists(Path.Combine(forcedCheckpoint, Path.GetFileName(dbPath) + "-wal")));
                Assert.True(File.Exists(Path.Combine(forcedCheckpoint, Path.GetFileName(dbPath) + "-shm")));
                Assert.True(File.Exists(Path.Combine(forcedCheckpoint, "manifest.txt")));
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var invalidate = conn.CreateCommand();
                invalidate.CommandText = "UPDATE symbols SET name_folded = NULL WHERE name = 'run'";
                Assert.Equal(1, invalidate.ExecuteNonQuery());
            }

            var disabled = RunBackfill("--no-checkpoint");
            Assert.Equal(CommandExitCodes.Success, disabled.ExitCode);
            Assert.Equal(1, disabled.Json.GetProperty("symbols").GetInt32());
            Assert.False(disabled.Json.GetProperty("was_already_complete").GetBoolean());
            Assert.True(disabled.Json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.Equal(
                "disabled_by_option",
                disabled.Json.GetProperty("checkpoint_skipped_reason").GetString());
            Assert.Single(Directory.GetDirectories(checkpointRoot));

            string humanOutput;
            int humanExitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    humanExitCode = IndexCommandRunner.RunBackfillFold(
                        ["--db", dbPath],
                        _jsonOptions);
                    humanOutput = output.ToString();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("checkpoint:         skipped (already complete)", humanOutput, StringComparison.Ordinal);
            Assert.Single(Directory.GetDirectories(checkpointRoot));

            using (var driftedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var drift = driftedDb.Connection.CreateCommand();
                drift.CommandText = "UPDATE symbols SET name_folded = 'definitely_wrong' WHERE name = 'run'";
                Assert.Equal(1, drift.ExecuteNonQuery());
            }

            var repaired = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, repaired.ExitCode);
            Assert.True(repaired.Json.GetProperty("rewrite_all").GetBoolean());
            Assert.Equal(1, repaired.Json.GetProperty("symbols").GetInt32());
            Assert.False(repaired.Json.GetProperty("was_already_complete").GetBoolean());
            Assert.False(repaired.Json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.Equal(2, Directory.GetDirectories(checkpointRoot).Length);
            using (var repairedDb = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            {
                using var folded = repairedDb.Connection.CreateCommand();
                folded.CommandText = "SELECT name_folded FROM symbols WHERE name = 'run'";
                Assert.Equal("run", folded.ExecuteScalar());
            }

            using (var pendingDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var pendingWriter = new DbWriter(pendingDb.Connection);
                pendingWriter.SetMeta(DbWriter.FoldBackfillGraphRefreshPendingMetaKey, "1");
            }

            var resumed = RunBackfill();
            Assert.Equal(CommandExitCodes.Success, resumed.ExitCode);
            Assert.Equal(0, resumed.Json.GetProperty("symbols").GetInt32());
            Assert.Equal(0, resumed.Json.GetProperty("symbol_references").GetInt32());
            Assert.False(resumed.Json.GetProperty("was_already_complete").GetBoolean());
            Assert.False(resumed.Json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.Equal(3, Directory.GetDirectories(checkpointRoot).Length);
            using var verifyPending = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            Assert.Null(verifyPending.GetMetaString(DbWriter.FoldBackfillGraphRefreshPendingMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(checkpointRoot);
        }
    }

    [Fact]
    public void RunBackfillFold_ForcedCheckpointRespectsIndexLock_Issue4889()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_checkpoint_locked_4889");
        var checkpointRoot = dbPath + ".checkpoints";
        var lockPath = IndexLock.GetLockPath(dbPath);
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                Assert.True(writer.MarkFoldReady());
            }

            using (IndexLock.Acquire(lockPath, Path.GetDirectoryName(dbPath)!))
            {
                JsonElement json;
                int exitCode;
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        exitCode = IndexCommandRunner.RunBackfillFold(
                            ["--db", dbPath, "--checkpoint", "--json"],
                            _jsonOptions);
                        using var document = JsonDocument.Parse(output.ToString());
                        json = document.RootElement.Clone();
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }

                Assert.Equal(CommandExitCodes.TransientDatabaseError, exitCode);
                Assert.Equal(CommandErrorCodes.DbLocked, json.GetProperty("error_code").GetString());
                Assert.Equal("database_locked", json.GetProperty("category").GetString());
                Assert.False(Directory.Exists(checkpointRoot));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(lockPath + ".info");
            DeleteFile(lockPath);
            TestProjectHelper.DeleteDirectory(checkpointRoot);
        }
    }

    [Fact]
    public void RunBackfillFold_DryRunSupportsPreDisplayAliasSchemaWithoutCSharp_Issue4866Review()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_without_csharp_legacy_schema");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 32,
                    Lines = 1,
                    Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "run",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                ]);
                writer.BackfillFoldedColumns(rewriteAll: true);
                Assert.True(writer.MarkFoldReady());
                writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, null);

                using var legacySchema = db.Connection.CreateCommand();
                legacySchema.CommandText = """
                    DROP INDEX IF EXISTS idx_symbols_display_name_folded;
                    ALTER TABLE symbols DROP COLUMN display_name_folded;
                    """;
                legacySchema.ExecuteNonQuery();
            }

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var output = new StringWriter();
                try
                {
                    Console.SetOut(output);
                    exitCode = IndexCommandRunner.RunBackfillFold(
                        ["--db", dbPath, "--dry-run", "--json"],
                        _jsonOptions);
                    using var document = JsonDocument.Parse(output.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.Equal(0, json.GetProperty("symbols").GetInt32());
            Assert.Equal(0, json.GetProperty("symbol_references").GetInt32());
            Assert.True(json.GetProperty("was_already_complete").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_RefusesCSharpV3StampWhenLegacySignaturesAreMissing_Issue4866()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_csharp_v2_missing_signature");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/LegacyExplicit.cs",
                    Lang = "csharp",
                    Size = 32,
                    Lines = 1,
                    Modified = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "Run",
                        Signature = null,
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "Stable",
                        IdentityNameFolded = "sentinel::stable",
                        DisplayNameFolded = "stable",
                        Signature = "void IFoo.Stable() { }",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                    },
                ]);
                writer.SetMeta(DbContext.CSharpSymbolNameContractVersionMetaKey, "2");
                Assert.False(
                    writer.CanReconstructCSharpExplicitInterfaceIdentitiesFromPersistedRows());
            }

            (int ExitCode, string Output) RunBackfill(params string[] additionalArguments)
            {
                var arguments = new List<string> { "--db", dbPath };
                arguments.AddRange(additionalArguments);
                lock (TestConsoleLock.Gate)
                {
                    var originalOut = Console.Out;
                    using var output = new StringWriter();
                    try
                    {
                        Console.SetOut(output);
                        var exitCode = IndexCommandRunner.RunBackfillFold(
                            arguments.ToArray(),
                            _jsonOptions);
                        return (exitCode, output.ToString());
                    }
                    finally
                    {
                        Console.SetOut(originalOut);
                    }
                }
            }

            var dryRun = RunBackfill("--dry-run", "--json");
            Assert.Equal(CommandExitCodes.DatabaseError, dryRun.ExitCode);
            Assert.Contains(
                "C# explicit-interface identities cannot be reconstructed",
                dryRun.Output,
                StringComparison.Ordinal);

            var run = RunBackfill("--json");
            Assert.Equal(CommandExitCodes.DatabaseError, run.ExitCode);
            Assert.Contains(
                "C# explicit-interface identities cannot be reconstructed",
                run.Output,
                StringComparison.Ordinal);

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                "2",
                verifyDb.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey));
            using var identity = verifyDb.Connection.CreateCommand();
            identity.CommandText = "SELECT name_folded FROM symbols WHERE name = 'Stable'";
            Assert.Equal("sentinel::stable", identity.ExecuteScalar());
            Assert.Null(verifyDb.GetMetaString(DbWriter.FoldBackfillGraphRefreshPendingMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_DryRunReportsEffectiveFoldReadyWhenMetadataStale()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_stale_dry");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 1,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
                ]);
                writer.BackfillFoldedColumns(rewriteAll: true);
                writer.MarkFoldReady();
                writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");
            }

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--dry-run", "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.True(json.GetProperty("rewrite_all").GetBoolean());
            Assert.False(json.GetProperty("fold_ready_before").GetBoolean());
            Assert.False(json.GetProperty("fold_ready_after").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal("DEADBEEFDEADBEEF", verifyDb.GetMetaString("fold_key_fingerprint"));
            Assert.Equal(
                DbContext.FoldReadyFlag | DbContext.HotspotReferenceAggregateFlags,
                verifyDb.GetUserVersion());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_RewritesAllWhenOnlyFingerprintDrifted()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_fold_fp");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "bootstrap", Line = 2, StartLine = 2, EndLine = 2 },
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "CAFÉ_INIT",
                        ReferenceKind = "call",
                        Line = 2,
                        Column = 5,
                        Context = "CAFÉ_INIT()",
                        ContainerKind = "function",
                        ContainerName = "bootstrap",
                    },
                ]);
                writer.MarkGraphReady();
                writer.MarkIssuesReady();
                writer.MarkFoldReady();
                writer.SetMeta("fold_key_fingerprint", "DEADBEEFDEADBEEF");
            }

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(2, json.GetProperty("symbols").GetInt32());
            Assert.Equal(1, json.GetProperty("symbol_references").GetInt32());
            Assert.True(json.GetProperty("rewrite_all").GetBoolean());
            Assert.True(json.GetProperty("verified").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.False(json.GetProperty("checkpoint_skipped").GetBoolean());
            Assert.Single(Directory.GetDirectories(dbPath + ".checkpoints"));

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            verifyDb.TryMigrateForRead();
            Assert.Equal(NameFold.Fingerprint(), verifyDb.GetMetaString("fold_key_fingerprint"));
            var reader = new DbReader(verifyDb.Connection);
            Assert.True(reader._foldReady);
            Assert.Single(reader.SearchSymbols(["ＣＡＦÉ_ＩＮＩＴ"], limit: 10, exact: true));
            Assert.Single(reader.GetCallers("ＣＡＦÉ_ＩＮＩＴ", limit: 10, exact: true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            TestProjectHelper.DeleteDirectory(dbPath + ".checkpoints");
        }
    }

    [Fact]
    public void BackfillFoldedColumns_CancelledDuringSymbolLoop_KeepsCompletedRowsForResume()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_cancel_symbols");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "first", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "second", Line = 2, StartLine = 2, EndLine = 2 },
                ]);

                using (var clear = db.Connection.CreateCommand())
                {
                    clear.CommandText = "UPDATE symbols SET name_folded = NULL";
                    clear.ExecuteNonQuery();
                }

                DbWriter.FoldBackfillRowUpdatedForTesting = cts.Cancel;

                Assert.Throws<OperationCanceledException>(() => writer.BackfillFoldedColumns(rewriteAll: false, cts.Token));

                using var count = db.Connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM symbols WHERE name_folded IS NOT NULL";
                Assert.Equal(1L, (long)count.ExecuteScalar()!);
            }
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            cts.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void BackfillFoldedColumns_CancelledDuringReferenceLoop_KeepsCompletedRowsForResume()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_cancel_refs");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertReferences([
                    new ReferenceRecord { FileId = fileId, SymbolName = "first", ReferenceKind = "call", Line = 1, Column = 1, Context = "first()" },
                    new ReferenceRecord { FileId = fileId, SymbolName = "second", ReferenceKind = "call", Line = 2, Column = 1, Context = "second()" },
                ]);

                using (var clear = db.Connection.CreateCommand())
                {
                    clear.CommandText = "UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL";
                    clear.ExecuteNonQuery();
                }

                DbWriter.FoldBackfillRowUpdatedForTesting = cts.Cancel;

                Assert.Throws<OperationCanceledException>(() => writer.BackfillFoldedColumns(rewriteAll: false, cts.Token));

                using var count = db.Connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE symbol_name_folded IS NOT NULL OR container_name_folded IS NOT NULL";
                Assert.Equal(1L, (long)count.ExecuteScalar()!);
            }
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            cts.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void BackfillFoldedColumns_RewriteAllResumesAfterCheckpoint()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_rewrite_resume");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "first", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "second", Line = 2, StartLine = 2, EndLine = 2 },
                ]);

                DbWriter.FoldBackfillRowUpdatedForTesting = cts.Cancel;
                Assert.Throws<OperationCanceledException>(() => writer.BackfillFoldedColumns(rewriteAll: true, cts.Token));

                DbWriter.FoldBackfillRowUpdatedForTesting = null;
                var resumed = writer.BackfillFoldedColumns(rewriteAll: true);

                Assert.Equal(1, resumed.Symbols);
                using var count = db.Connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM symbols WHERE name_folded IS NOT NULL";
                Assert.Equal(2L, (long)count.ExecuteScalar()!);
            }
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            cts.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void BackfillFoldedColumns_RewriteAllResumesReferencePhaseCheckpoint()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_rewrite_refs_resume");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertReferences([
                    new ReferenceRecord { FileId = fileId, SymbolName = "first", ReferenceKind = "call", Line = 1, Column = 1, Context = "first()" },
                    new ReferenceRecord { FileId = fileId, SymbolName = "second", ReferenceKind = "call", Line = 2, Column = 1, Context = "second()" },
                ]);

                DbWriter.FoldBackfillRowUpdatedForTesting = cts.Cancel;
                Assert.Throws<OperationCanceledException>(() => writer.BackfillFoldedColumns(rewriteAll: true, cts.Token));

                DbWriter.FoldBackfillRowUpdatedForTesting = null;
                var resumed = writer.BackfillFoldedColumns(rewriteAll: true);

                Assert.Equal(0, resumed.Symbols);
                Assert.Equal(1, resumed.SymbolReferences);
                using var count = db.Connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE symbol_name_folded IS NOT NULL";
                Assert.Equal(2L, (long)count.ExecuteScalar()!);
            }
        }
        finally
        {
            DbWriter.FoldBackfillRowUpdatedForTesting = null;
            cts.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_Cancelled_ReturnsInterruptedErrorCode()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_cancel_cli");
        using var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 1,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "cancel_me", Line = 1, StartLine = 1, EndLine = 1 },
                ]);

                using var clear = db.Connection.CreateCommand();
                clear.CommandText = "UPDATE symbols SET name_folded = NULL";
                clear.ExecuteNonQuery();
            }

            cts.Cancel();

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--json"], _jsonOptions, cts);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, json.GetProperty("error_code").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_BlankFile_ReturnsDatabaseError()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_blank");
        File.WriteAllText(dbPath, string.Empty);

        try
        {
            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.DbNotDatabase, json.GetProperty("error_code").GetString());
            Assert.Equal("database_not_a_database", json.GetProperty("category").GetString());
            Assert.Contains("not a valid SQLite CodeIndex database", json.GetProperty("message").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void RunBackfillFold_NonexistentFileUri_ReturnsNotFound()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_missing");
        var dbUri = new Uri(dbPath).AbsoluteUri;

        JsonElement json;
        int exitCode;
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbUri, "--json"], _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                json = document.RootElement.Clone();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal(CommandErrorCodes.DbNotFound, json.GetProperty("error_code").GetString());
        Assert.Equal("database_missing", json.GetProperty("category").GetString());
        Assert.Contains("database file was not found", json.GetProperty("message").GetString());
    }

    [Fact]
    public void RunBackfillFold_LegacyDbWithoutCodeIndexMeta_Succeeds()
    {
        var dbPath = CreateTempDbPath("cdidx_backfill_legacy_no_meta");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "src/app.py",
                    Lang = "python",
                    Size = 64,
                    Lines = 2,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                writer.InsertSymbols([
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "café_init", Line = 1, StartLine = 1, EndLine = 1 },
                    new SymbolRecord { FileId = fileId, Kind = "function", Name = "bootstrap", Line = 2, StartLine = 2, EndLine = 2 },
                ]);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "CAFÉ_INIT",
                        ReferenceKind = "call",
                        Line = 2,
                        Column = 5,
                        Context = "CAFÉ_INIT()",
                        ContainerKind = "function",
                        ContainerName = "bootstrap",
                    },
                ]);
                using var dropMeta = db.Connection.CreateCommand();
                dropMeta.CommandText = "DROP TABLE codeindex_meta; UPDATE symbols SET name_folded = NULL; UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL; PRAGMA user_version = 3";
                dropMeta.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            JsonElement json;
            int exitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    exitCode = IndexCommandRunner.RunBackfillFold(["--db", dbPath, "--json"], _jsonOptions);
                    using var document = JsonDocument.Parse(writer.ToString());
                    json = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(2, json.GetProperty("symbols").GetInt32());
            Assert.Equal(1, json.GetProperty("symbol_references").GetInt32());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            verifyDb.TryMigrateForRead();
            Assert.Equal(NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString("fold_key_version"));
            Assert.Equal(NameFold.Fingerprint(), verifyDb.GetMetaString("fold_key_fingerprint"));
            var reader = new DbReader(verifyDb.Connection);
            Assert.True(reader._foldReady);
            Assert.Single(reader.SearchSymbols(["ＣＡＦÉ_ＩＮＩＴ"], limit: 10, exact: true));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }













    [Fact]
    public void Run_Rebuild_CancelledAfterReadinessDemotion_PreservesExistingIndex_Issue4854()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            int initialReadiness;
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                initialReadiness = db.GetUserVersion();
            Assert.Equal(DbContext.CurrentSchemaVersion, initialReadiness);
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "later.cs"), "public class Later { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "add later");
            var laterHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(initialHead, laterHead);
            using var cancellation = new CancellationTokenSource();
            var hookInvoked = false;
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () =>
            {
                hookInvoked = true;
                cancellation.Cancel();
            };

            int interruptedExitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--rebuild", "--yes", "--json"], _jsonOptions, cancellation);
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            var reopenWarning = ConsoleCapture.CaptureError(() =>
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                Assert.Equal(initialReadiness, db.GetUserVersion());
                Assert.Equal(initialHead, db.GetMetaString(DbContext.IndexedHeadShaMetaKey));
            });
            Assert.DoesNotContain("Last batch did not complete", reopenWarning);
            Assert.DoesNotContain("later.cs", ReadIndexedPaths(dbPath));
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));
        }
        finally
        {
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }





    [Fact]
    public void Run_Rebuild_WhenIndexedFileBecomesBinary_PersistsNullByteIssue()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(sourcePath, "def run():\n    return 1\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("app.py", ReadIndexedPaths(dbPath));

            File.WriteAllBytes(sourcePath, [0, 1, 2, 3]);

            var rebuildExitCode = IndexCommandRunner.Run([projectRoot, "--rebuild", "--yes", "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, rebuildExitCode);
            Assert.Contains("app.py", ReadIndexedPaths(dbPath));
            var issue = Assert.Single(ReadFileIssues(dbPath).Where(issue => issue.Path == "app.py" && issue.Kind == "null_byte"));
            Assert.Contains("byte offset 0", issue.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }






























































    [Fact]
    public void Run_FilesUpdate_ReindexesUnchangedFileWhenLanguageExtractorVersionChanged()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "lib.py");
            File.WriteAllText(
                sourcePath,
                """
                def target():
                    return 1
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET signature = 'def stale():';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'symbol_extractor_version_python';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", sourcePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var signatureCmd = verify.CreateCommand();
            signatureCmd.CommandText = "SELECT signature FROM symbols WHERE name = 'target'";
            Assert.Equal("def target():", signatureCmd.ExecuteScalar() as string);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'symbol_extractor_version_python'";
            Assert.Equal("0", versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FilesUpdate_ReindexesUnchangedNuGetConfigWhenXmlExtractorVersionChanged_Issue4459()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "nuget.config");
            File.WriteAllText(
                sourcePath,
                """
                <configuration>
                  <config>
                    <add key="signatureValidationMode" value="require" />
                  </config>
                </configuration>
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM symbols WHERE sub_kind = 'nuget.signature_validation_mode';
                    UPDATE codeindex_meta SET value = '2' WHERE key = 'symbol_extractor_version_xml';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", sourcePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var symbolCmd = verify.CreateCommand();
            symbolCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'require' AND sub_kind = 'nuget.signature_validation_mode'";
            Assert.Equal(1L, (long)symbolCmd.ExecuteScalar()!);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FilesUpdate_ReindexesUnchangedJsonFileWhenJsonExtractorVersionChanged_Issue4874()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "settings.json");
            File.WriteAllText(
                sourcePath,
                """
                {
                  "features": {
                    "preview": true
                  }
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM symbols WHERE file_id = (SELECT id FROM files WHERE path = 'settings.json');
                    DELETE FROM symbol_references WHERE file_id = (SELECT id FROM files WHERE path = 'settings.json');
                    INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES('symbol_extractor_version_json', '2');
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", sourcePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var symbolCmd = verify.CreateCommand();
            symbolCmd.CommandText = """
                SELECT COUNT(*)
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'settings.json'
                  AND s.name = 'features.preview'
                """;
            Assert.Equal(1L, (long)symbolCmd.ExecuteScalar()!);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }


    [Fact]
    public void RunStatus_JsonReportsDegradedCSharpCanonicalNameTrust()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "money.cs"),
                """
                public struct Money
                {
                    public static explicit operator Money(decimal d) => new();
                }

                public class Bag
                {
                    public string this[int index] => "";
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name = 'explicit' WHERE name = 'explicit operator Money';
                    UPDATE symbols SET name = 'this' WHERE name = 'Item';
                    DELETE FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.False(statusJson.GetProperty("csharp_symbol_name_ready").GetBoolean());

            int humanExitCode;
            string output;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var writer = new StringWriter();
                try
                {
                    Console.SetOut(writer);
                    humanExitCode = QueryCommandRunner.RunStatus(["--db", dbPath], _jsonOptions);
                    output = writer.ToString();
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("WARN     : C# exact-name for operators / conversion operators / indexers is degraded.", output);
            Assert.Contains("--db", output);
            Assert.Contains(Path.GetFullPath(projectRoot), output);
            Assert.Contains(Path.GetFullPath(dbPath), output);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }




    [Fact]
    public void Run_IncrementalJson_ReportsFoldOnlyRemediation()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("incremental", json.GetProperty("mode").GetString());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains("older fold-key version", json.GetProperty("degraded_reason").GetString());
            Assert.Contains("cdidx backfill-fold --db", json.GetProperty("recommended_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("recommended_action").GetString());
            Assert.Contains("--rebuild", json.GetProperty("alternative_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("alternative_action").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }


    [Fact]
    public void Run_WithAbsoluteDbPathInsideProject_WritesRepoRelativePatternToGitExclude()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            var exitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
            var excludeContent = File.ReadAllText(excludePath);
            Assert.Contains(".cdidx/", excludeContent);
            Assert.DoesNotContain(dbPath.Replace('\\', '/'), excludeContent);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [ExternalProcessFact]
    public void Run_WithSymlinkedGitInfoDescendant_RefusesExternalExcludeWrite_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var externalInfoDirectory = TestProjectHelper.CreateTempProject("cdidx_external_git_info");
        var infoLink = Path.Combine(projectRoot, ".git", "info");
        try
        {
            RunGit(projectRoot, "init");
            TestProjectHelper.DeleteDirectory(infoLink);
            Directory.CreateSymbolicLink(infoLink, externalInfoDirectory);

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(File.Exists(Path.Combine(externalInfoDirectory, "exclude")));
        }
        finally
        {
            TestProjectHelper.DeleteFile(infoLink);
            DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalInfoDirectory);
        }
    }

    [ExternalProcessFact]
    public void Run_WithSymlinkedGitExcludeFile_RefusesExternalExcludeWrite_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var externalDirectory = TestProjectHelper.CreateTempProject("cdidx_external_exclude_file");
        var externalExclude = Path.Combine(externalDirectory, "exclude");
        var excludeLink = Path.Combine(projectRoot, ".git", "info", "exclude");
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(externalExclude, "external sentinel\n");
            TestProjectHelper.DeleteFile(excludeLink);
            File.CreateSymbolicLink(excludeLink, externalExclude);

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("external sentinel\n", File.ReadAllText(externalExclude));
        }
        finally
        {
            TestProjectHelper.DeleteFile(excludeLink);
            DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalDirectory);
        }
    }

    [ExternalProcessFact]
    public void Run_WithHardLinkedGitExcludeFile_RefusesExternalExcludeWrite_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var externalDirectory = TestProjectHelper.CreateTempProject("cdidx_external_hardlink_exclude");
        var externalExclude = Path.Combine(externalDirectory, "exclude");
        var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(externalExclude, "external sentinel\n");
            TestProjectHelper.DeleteFile(excludePath);
            CreateHardLink(externalExclude, excludePath);

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("external sentinel\n", File.ReadAllText(externalExclude));
        }
        finally
        {
            TestProjectHelper.DeleteFile(excludePath);
            DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalDirectory);
        }
    }

    [Fact]
    public void Run_WithAbsoluteDbPathOutsideProject_DoesNotWriteAbsolutePathToGitExclude()
    {
        var projectRoot = CreateTempProject();
        var outsideDir = TestProjectHelper.CreateTempProject("cdidx_external_db");
        try
        {
            RunGit(projectRoot, "init");
            var dbPath = Path.Combine(outsideDir, "external.db");

            var exitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
            var excludeContent = File.ReadAllText(excludePath);
            Assert.DoesNotContain(dbPath.Replace('\\', '/'), excludeContent);
            Assert.DoesNotContain("/external.db", excludeContent);
        }
        finally
        {
            DeleteDirectory(outsideDir);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WithCommits_PrintsFullSyncGuidanceForHistoryRewrites()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "tracked.cs"), "class Sample {}\n");
            RunGit(projectRoot, "add", "tracked.cs");
            RunGit(projectRoot, "commit", "-m", "initial");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, output) = RunAndCaptureOutput([projectRoot, "--commits", commitId]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("prefer `cdidx .` over `--commits`", output);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }


















    [Fact]
    public void Run_InWorktreeWithAbsoluteDbPathInsideProject_WritesRelativePatternToSharedExclude()
    {
        var tempRoot = TestProjectHelper.CreateTempProject("cdidx_worktree");
        var mainGitDir = Path.Combine(tempRoot, "main", ".git");
        var worktreeRoot = Path.Combine(tempRoot, "wt");
        try
        {
            Directory.CreateDirectory(Path.Combine(mainGitDir, "info"));
            var worktreeGitDir = Path.Combine(mainGitDir, "worktrees", "wt");
            Directory.CreateDirectory(worktreeGitDir);
            File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");

            Directory.CreateDirectory(worktreeRoot);
            File.WriteAllText(Path.Combine(worktreeRoot, ".git"), $"gitdir: {worktreeGitDir}");

            var dbPath = Path.Combine(worktreeRoot, ".cdidx", "codeindex.db");
            var exitCode = IndexCommandRunner.Run([worktreeRoot, "--db", dbPath, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var sharedExcludePath = Path.Combine(mainGitDir, "info", "exclude");
            var excludeContent = File.ReadAllText(sharedExcludePath);
            Assert.Contains(".cdidx/", excludeContent);
            Assert.DoesNotContain(dbPath.Replace('\\', '/'), excludeContent);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void Run_RebuildFlag_DropsAndRebuildsIndex()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");

            // First index / 初回インデックス
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);

            // Add another file / ファイル追加
            File.WriteAllText(Path.Combine(projectRoot, "extra.cs"), "public class Extra { }");

            // Rebuild: should drop and re-scan all files / rebuild: 全削除して全ファイル再スキャン
            var (exitCode2, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("rebuild", json.GetProperty("mode").GetString());
            // After rebuild, all files should be scanned (not skipped)
            // rebuild 後、全ファイルがスキャンされるべき（スキップなし）
            Assert.True(json.GetProperty("summary").GetProperty("files_total").GetInt32() >= 2);
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_RebuildFlag_SucceedsOnFreshDb()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("rebuild", json.GetProperty("mode").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("files_total").GetInt32() >= 1);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }












    [Fact]
    public void Run_Rebuild_UnreadableDirectoryPreservesPriorRowsAndTrust()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var unreadableDir = Path.Combine(projectRoot, "secret");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }");
            Directory.CreateDirectory(unreadableDir);
            File.WriteAllText(Path.Combine(unreadableDir, "Hidden.csproj"), "<Project />");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var priorTrust = ReadFullScanTrustSnapshot(dbPath);
            var priorAppChecksum = ReadIndexedChecksum(dbPath, "app.cs");

            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { public void Changed() { } }");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "app.cs"), DateTime.UtcNow.AddSeconds(3));
            originalMode = File.GetUnixFileMode(unreadableDir);
            File.SetUnixFileMode(unreadableDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());

            Assert.Equal(priorTrust, ReadFullScanTrustSnapshot(dbPath));
            Assert.Equal(priorAppChecksum, ReadIndexedChecksum(dbPath, "app.cs"));
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("app.cs", indexedPaths);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(unreadableDir))
                File.SetUnixFileMode(unreadableDir, originalMode.Value);
            DeleteDirectory(projectRoot);
        }
    }








    [Fact]
    public void Run_RebuildWithCommits_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class A {}");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            // --rebuild + --commits should conflict / --rebuild + --commits は矛盾
            var (exitCode, output) = RunAndCaptureOutput([projectRoot, "--rebuild", "--commits", "HEAD"]);
            Assert.Equal(CommandExitCodes.UsageError, exitCode);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }








    [Fact]
    public void RunStatusCheck_AfterBranchSwitch_ReportsHeadChanged()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            // Advance HEAD without touching the only indexed file. Without HEAD-aware
            // freshness, status --check would erroneously report matches_workspace=true.
            // HEAD だけを進めて唯一のインデックス対象ファイルを変更しない。HEAD 認識がないと
            // status --check は matches_workspace=true を誤って返してしまう。
            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            var check = statusJson.GetProperty("workspace_check");
            Assert.True(check.GetProperty("head_changed").GetBoolean());
            Assert.False(check.GetProperty("matches_workspace").GetBoolean());
            // The status check reports the most specific reason first: an unindexed workspace
            // file outranks `head_changed`, but the head_changed flag still flips so callers
            // know to rerun `--rebuild`. Issue #1508.
            // 不一致は具体的な reason を優先表示する。HEAD 差分は head_changed フラグで通知する。
            Assert.Equal("unindexed_workspace_files", check.GetProperty("reason").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_CdidxSidecarIsExcludedFromScanAndWorkspaceMembership_Issue4592()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var dataDir = Path.Combine(projectRoot, ".cdidx");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "audit-notes.md"), "local notes\n");

            var (indexExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var dbPath = Path.Combine(dataDir, "codeindex.db");
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("app.cs", indexedPaths);
            Assert.DoesNotContain(".cdidx/audit-notes.md", indexedPaths);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var check = statusJson.GetProperty("workspace_check");
            Assert.True(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal(0, check.GetProperty("unindexed_file_count").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_FollowSymlinksAll_UsesPersistedPolicy_Issue4352()
    {
        var projectRoot = CreateTempProject();
        var outsideRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(outsideRoot, "Outside.cs"), "public class Outside { }\n");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(projectRoot, "src", "outside-link"), outsideRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var (indexExitCode, _) = RunAndCaptureJson([projectRoot, "--follow-symlinks", "all", "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal("all", db.GetMetaString(DbContext.IndexedFollowSymlinksPolicyMetaKey));
            }

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            var check = statusJson.GetProperty("workspace_check");

            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("index_matches_workspace").GetBoolean());
            Assert.True(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("matched", check.GetProperty("reason").GetString());
            Assert.Equal(0, check.GetProperty("missing_file_count").GetInt32());
            Assert.Equal(1, check.GetProperty("indexed_file_count").GetInt32());
            Assert.Contains("src/outside-link/Outside.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(outsideRoot, "NewOutside.cs"), "public class NewOutside { }\n");
            var (staleExitCode, staleStatusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            var repairCommand = Assert.Single(staleStatusJson.GetProperty("repair_commands").EnumerateArray());
            var repairArgs = repairCommand.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()).ToArray();
            var followSymlinksIndex = Array.IndexOf(repairArgs, "--follow-symlinks");

            Assert.Equal(1, staleExitCode);
            Assert.Equal("workspace_stale", staleStatusJson.GetProperty("failed_checks")[0].GetString());
            Assert.InRange(followSymlinksIndex, 0, repairArgs.Length - 2);
            Assert.Equal("all", repairArgs[followSymlinksIndex + 1]);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteDirectory(outsideRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_FilesRefreshStaysStaleUntilCommitScopedRefreshAtHead()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "add run");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (filesRefreshExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, filesRefreshExitCode);

            var (staleStatusExitCode, staleStatusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.UsageError, staleStatusExitCode);

            var staleCheck = staleStatusJson.GetProperty("workspace_check");
            Assert.True(staleCheck.GetProperty("head_changed").GetBoolean());
            Assert.False(staleCheck.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("head_changed", staleCheck.GetProperty("reason").GetString());

            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            var (commitRefreshExitCode, _) = RunAndCaptureJson([projectRoot, "--commits", "HEAD", "--json"]);
            Assert.Equal(CommandExitCodes.Success, commitRefreshExitCode);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);

            var check = statusJson.GetProperty("workspace_check");
            Assert.False(check.GetProperty("head_changed").GetBoolean());
            Assert.True(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("matched", check.GetProperty("reason").GetString());
            Assert.Equal(currentHead, statusJson.GetProperty("indexed_head_sha").GetString());
            Assert.Equal(0, statusJson.GetProperty("commits_ahead_of_indexed_head").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_AfterChangedBetweenRefreshAtHead_TreatsCurrentIndexedHeadShaAsFresh_2808_Issue4854()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "add run");
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (refreshExitCode, _) = RunAndCaptureJson([projectRoot, "--changed-between", initialHead, "HEAD", "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                // `--changed-between` proves the current HEAD is covered for status freshness,
                // but it must not overwrite the full-scan-only HEAD stamp.
                // `--changed-between` は status freshness だけを満たし、full-scan 専用 HEAD は進めない。
                Assert.Equal(initialHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
            }

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(currentHead, statusJson.GetProperty("indexed_head_sha").GetString());
            var check = statusJson.GetProperty("workspace_check");
            Assert.False(check.GetProperty("head_changed").GetBoolean());
            Assert.True(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("matched", check.GetProperty("reason").GetString());
            Assert.Equal(currentHead, check.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(currentHead, check.GetProperty("workspace_head_commit").GetString());

            var (queryExitCode, queryEnvelope) = RunProgramAndCaptureJson(
                ["search", "Run", "--db", dbPath, "--json-envelope"],
                projectRoot);
            Assert.Equal(CommandExitCodes.Success, queryExitCode);
            Assert.Equal(
                statusJson.GetProperty("indexed_head_sha").GetString(),
                queryEnvelope.GetProperty("metadata").GetProperty("indexed_at_head_sha").GetString());

            var (dotExitCode, dotJson) = RunProgramAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, dotExitCode);
            Assert.Equal("success", dotJson.GetProperty("status").GetString());
            Assert.False(dotJson.GetProperty("head_changed").GetBoolean());
            Assert.Equal(initialHead, dotJson.GetProperty("prior_indexed_head_commit").GetString());
            Assert.Equal(currentHead, dotJson.GetProperty("current_head_commit").GetString());
            Assert.Equal(JsonValueKind.Null, dotJson.GetProperty("head_change_notice").ValueKind);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(currentHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
                Assert.Equal(currentHead, db.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
            }

            var (postDotStatusExitCode, postDotStatusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, postDotStatusExitCode);
            Assert.True(postDotStatusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
            var (postDotQueryExitCode, postDotQueryEnvelope) = RunProgramAndCaptureJson(
                ["search", "Run", "--db", dbPath, "--json-envelope"],
                projectRoot);
            Assert.Equal(CommandExitCodes.Success, postDotQueryExitCode);
            Assert.Equal(
                postDotStatusJson.GetProperty("indexed_head_sha").GetString(),
                postDotQueryEnvelope.GetProperty("metadata").GetProperty("indexed_at_head_sha").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunChangedBetween_ReconcilesPersistedWorkspaceBaselineOlderThanOldRef_Issue5054()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var firstPath = Path.Combine(projectRoot, "first.cs");
            var secondPath = Path.Combine(projectRoot, "second.cs");
            File.WriteAllText(firstPath, "public class First { }\n");
            File.WriteAllText(secondPath, "public class Second { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            File.WriteAllText(firstPath, "public class First { public void FromMiddleCommit() { } }\n");
            RunGit(projectRoot, "add", "first.cs");
            RunGit(projectRoot, "commit", "-m", "change first");
            var suppliedOldHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            File.WriteAllText(secondPath, "public class Second { public void FromCurrentCommit() { } }\n");
            RunGit(projectRoot, "add", "second.cs");
            RunGit(projectRoot, "commit", "-m", "change second");
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (refreshExitCode, _) = RunAndCaptureJson(
                [projectRoot, "--changed-between", suppliedOldHead, currentHead, "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(initialHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
                Assert.Equal(currentHead, db.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
            }

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
            Assert.Equal(
                currentHead,
                statusJson.GetProperty("workspace_check").GetProperty("indexed_head_commit").GetString());
            Assert.Equal(currentHead, statusJson.GetProperty("workspace_verified_head_sha").GetString());
            Assert.Equal(
                "workspace_verified",
                statusJson.GetProperty("head_freshness").GetProperty("indexed_head_source").GetString());

            var (filesExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "first.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, filesExitCode);
            var (postFilesStatusExitCode, postFilesStatusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, postFilesStatusExitCode);
            Assert.False(postFilesStatusJson.GetProperty("worktree_head_changed").GetBoolean());
            Assert.Equal(currentHead, postFilesStatusJson.GetProperty("workspace_verified_head_sha").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunChangedBetween_RevisitsPriorScopedPathWhenNetGitDiffReturnsToBaseline_Issue5054()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            const string baselineSource = "public class App { }\n";
            File.WriteAllText(sourcePath, baselineSource);
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "baseline");
            var baselineHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            File.WriteAllText(sourcePath, "public class App { public void FromBOnly() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "intermediate scoped content");
            Assert.Equal(
                CommandExitCodes.Success,
                RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]).ExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var scopedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var pendingPaths = JsonStringListCodec.Deserialize(
                    scopedDb.GetMetaString(DbContext.WorkspaceVerificationPendingPathsMetaKey));
                Assert.NotNull(pendingPaths);
                Assert.Contains("app.cs", pendingPaths);
                Assert.Equal(
                    bool.TrueString,
                    scopedDb.GetMetaString(DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey));
            }

            File.WriteAllText(sourcePath, baselineSource);
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "return tree to baseline");
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (refreshExitCode, _) = RunAndCaptureJson(
                [projectRoot, "--changed-between", baselineHead, currentHead, "--json"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
            Assert.Equal(currentHead, statusJson.GetProperty("workspace_verified_head_sha").GetString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Null(db.GetMetaString(DbContext.WorkspaceVerificationPendingPathsMetaKey));
            Assert.Null(db.GetMetaString(DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunChangedBetween_ReconcilesDivergentPersistedWorkspaceBaseline_Issue5054()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var firstPath = Path.Combine(projectRoot, "first.cs");
            var secondPath = Path.Combine(projectRoot, "second.cs");
            File.WriteAllText(firstPath, "public class First { }\n");
            File.WriteAllText(secondPath, "public class Second { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            RunGit(projectRoot, "checkout", "-b", "supplied-range");
            File.WriteAllText(secondPath, "public class Second { public void OnSuppliedBranch() { } }\n");
            RunGit(projectRoot, "add", "second.cs");
            RunGit(projectRoot, "commit", "-m", "change supplied branch");
            var suppliedHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            RunGit(projectRoot, "checkout", "-b", "indexed-baseline", initialHead);
            File.WriteAllText(firstPath, "public class First { public void OnlyOnIndexedBranch() { } }\n");
            RunGit(projectRoot, "add", "first.cs");
            RunGit(projectRoot, "commit", "-m", "change indexed branch");
            var divergentIndexedHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            RunGit(projectRoot, "checkout", "supplied-range");
            var (refreshExitCode, _) = RunAndCaptureJson(
                [projectRoot, "--changed-between", initialHead, suppliedHead, "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(divergentIndexedHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
                Assert.Equal(suppliedHead, db.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
            }

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());

            var (queryExitCode, queryEnvelope) = RunProgramAndCaptureJson(
                ["search", "OnSuppliedBranch", "--db", dbPath, "--json-envelope"],
                projectRoot);
            Assert.Equal(CommandExitCodes.Success, queryExitCode);
            Assert.Equal(
                suppliedHead,
                queryEnvelope.GetProperty("metadata").GetProperty("indexed_at_head_sha").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunChangedBetween_UnresolvablePersistedWorkspaceBaselineReturnsGuidanceWithoutAdvancingProvenance_Issue5054()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var unavailableBaseline = new string('f', 40);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db.Connection).SetMeta(DbContext.WorkspaceVerifiedHeadShaMetaKey, unavailableBaseline);

            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "change app");
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--changed-between", initialHead, currentHead, "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("persisted workspace verification baseline", json.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("full-workspace refresh", json.GetProperty("hint").GetString(), StringComparison.Ordinal);
            using var postFailureDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(initialHead, postFailureDb.GetMetaString(DbContext.IndexedHeadShaMetaKey));
            Assert.Equal(unavailableBaseline, postFailureDb.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunChangedBetween_IncompletePendingPathCoverageFailsClosedWithoutAdvancingProvenance_Issue5054()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                new DbWriter(db.Connection).SetMetaValues(
                    (DbContext.WorkspaceVerificationPendingPathsMetaKey,
                        JsonStringListCodec.Serialize(["app.cs"])),
                    (DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey,
                        bool.FalseString));
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "change app");
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--changed-between", initialHead, currentHead, "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("pending-path coverage is incomplete", json.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("verified full-workspace refresh", json.GetProperty("hint").GetString(), StringComparison.Ordinal);
            using var postFailureDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(initialHead, postFailureDb.GetMetaString(DbContext.IndexedHeadShaMetaKey));
            Assert.Equal(initialHead, postFailureDb.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
            Assert.Equal(
                bool.FalseString,
                postFailureDb.GetMetaString(DbContext.WorkspaceVerificationPendingPathsCompleteMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private (int ExitCode, JsonElement Json) RunAndCaptureJson(
        string[] args,
        CancellationTokenSource? cancellationForTesting = null)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                var exitCode = IndexCommandRunner.Run(args, _jsonOptions, cancellationForTesting);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunProgramAndCaptureJson(
        string[] args,
        string? configStartDirectory = null)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = ProgramRunner.Run(
                    args,
                    _jsonOptions,
                    appVersion: "1.0.0-test",
                    configStartDirectory: configStartDirectory ?? args[0]);
                using var document = JsonDocument.Parse(stdout.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }
        }
    }

    private static int CountRows(string dbPath, string tableName)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountMutualRecursionReferences(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static List<FileIssue> ReadFileIssues(string dbPath, string kind)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, i.kind, i.line, i.message
            FROM file_issues i
            JOIN files f ON f.id = i.file_id
            WHERE i.kind = @kind
            ORDER BY f.path
            """;
        command.Parameters.AddWithValue("@kind", kind);
        using var reader = command.ExecuteReader();
        var issues = new List<FileIssue>();
        while (reader.Read())
        {
            issues.Add(new FileIssue
            {
                Path = reader.GetString(0),
                Kind = reader.GetString(1),
                Line = reader.GetInt32(2),
                Message = reader.GetString(3),
            });
        }

        return issues;
    }

    private (int ExitCode, JsonElement Json, string Stderr) RunAndCaptureJsonWithStderr(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = IndexCommandRunner.Run(args, _jsonOptions);
                using var document = JsonDocument.Parse(stdout.ToString());
                return (exitCode, document.RootElement.Clone(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private static (int ExitCode, string Output) RunAndCaptureOutput(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                var exitCode = IndexCommandRunner.Run(args, new JsonSerializerOptions());
                return (exitCode, writer.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunStatusAndCaptureJson(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                var exitCode = QueryCommandRunner.RunStatus(args, _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunHotspotsJson(string dbPath, string lang, string kind)
        => RunHotspotsJsonWithPaths(dbPath, lang, kind, null);

    private (int ExitCode, JsonElement Json) RunHotspotsJsonWithPaths(string dbPath, string lang, string kind, IReadOnlyList<string>? paths)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                var args = new List<string> { "--db", dbPath, "--json", "--lang", lang, "--kind", kind };
                if (paths != null)
                {
                    foreach (var path in paths)
                    {
                        args.Add("--path");
                        args.Add(path);
                    }
                }

                var exitCode = QueryCommandRunner.RunHotspots(args.ToArray(), _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunAndCaptureStreams(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = IndexCommandRunner.Run(args, new JsonSerializerOptions());
                return (exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCliInSubprocess(string[] args, string workingDirectory)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(GetBuiltCliDllPath());
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start cdidx subprocess / cdidx サブプロセスの起動に失敗");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static (int ExitCode, string StdOut, string StdErr, bool TimedOut) RunCliInSubprocessWithTimeout(string[] args, string workingDirectory, TimeSpan timeout)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(GetBuiltCliDllPath());
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start cdidx subprocess / cdidx サブプロセスの起動に失敗");

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd(), true);
        }

        return (process.ExitCode, process.StandardOutput.ReadToEnd(), process.StandardError.ReadToEnd(), false);
    }

    private static string GetBuiltCliDllPath()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var fallbackTfms = new[] { tfm, "net8.0" }.Distinct(StringComparer.Ordinal);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        var fallbackConfigurations = new[] { configuration, "Debug", "Release" }
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (var candidateConfiguration in fallbackConfigurations)
            {
                foreach (var candidateTfm in fallbackTfms)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "CodeIndex", "bin", candidateConfiguration!, candidateTfm, "cdidx.dll");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate built cdidx.dll from test output path / テスト出力パスから cdidx.dll を特定できませんでした");
    }

    private static void WriteSymbolWorkerPatternConfig(string projectRoot, string content)
        => WriteSymbolWorkerPatternConfig(projectRoot, "toydsl.yaml", content);

    private static SymbolExtractionWorker.WorkerRequest CreateSymbolWorkerRequest(
        string projectRoot,
        string filePath,
        string lang = "csharp",
        string content = "class WorkerCacheSample { }")
        => new(
            0,
            lang,
            content,
            filePath,
            projectRoot,
            ContentIsNormalized: true,
            HasOversizeLine: false,
            ConflictMarkerLine: null);

    private static List<SymbolExtractionWorker.WorkerResponse> RunSymbolWorkerRequestsInProcess(
        params SymbolExtractionWorker.WorkerRequest[] requests)
    {
        var frames = string.Join(
            '\n',
            requests.Select(request => JsonSerializer.Serialize(request, SymbolExtractionWorker.JsonOptions)));
        using var input = new StringReader(frames + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var handled = SymbolExtractionWorker.TryRunCommand(
            [SymbolExtractionWorker.CommandName],
            input,
            output,
            error,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<SymbolExtractionWorker.WorkerResponse>(
                line,
                SymbolExtractionWorker.JsonOptions)!)
            .ToList();
    }

    private static void WriteSymbolWorkerPatternConfig(string projectRoot, string fileName, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string GetRepositoryRoot()
        => RepositoryTestPaths.Root;

    private static SqliteConnection OpenNonPoolingConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
        };
        return new SqliteConnection(builder.ToString());
    }

    private static HashSet<string> ReadIndexedPaths(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.TryMigrateForRead();
        var reader = new DbReader(db.Connection, db.IsReadOnly);
        return reader.ListFiles(limit: 1000)
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<FileIssue> ReadFileIssues(string dbPath)
    {
        using var connection = OpenNonPoolingConnection(dbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, i.kind, i.line, i.message
            FROM file_issues i
            JOIN files f ON f.id = i.file_id
            ORDER BY f.path, i.kind, i.line, i.message
            """;
        using var reader = command.ExecuteReader();
        var issues = new List<FileIssue>();
        while (reader.Read())
        {
            issues.Add(new FileIssue
            {
                Path = reader.GetString(0),
                Kind = reader.GetString(1),
                Line = reader.GetInt32(2),
                Message = reader.GetString(3),
            });
        }
        return issues;
    }

    private static Dictionary<string, int> ReadSymbolKindCounts(string dbPath)
    {
        using var connection = OpenNonPoolingConnection(dbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, COUNT(*) FROM symbols GROUP BY kind";
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);
        return counts;
    }

    private static HashSet<string> ReadImportSymbolNames(string dbPath)
    {
        using var connection = OpenNonPoolingConnection(dbPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols WHERE kind = 'import'";
        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static string? ReadIndexedChecksum(string dbPath, string relativePath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.TryMigrateForRead();
        var reader = new DbReader(db.Connection, db.IsReadOnly);
        return reader.GetFileByPath(relativePath)?.Checksum;
    }

    private static int CountMoneyParseImplicitImplementationReferences(string projectRoot)
    {
        using var conn = OpenNonPoolingConnection(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM symbol_references r
            JOIN files f ON f.id = r.file_id
            JOIN reference_lines rl ON rl.id = r.reference_line_id
            WHERE f.path = 'Money.cs'
              AND r.symbol_name = 'Parse'
              AND r.reference_kind = 'implicit_implementation'
              AND rl.context = 'public static Money Parse(string s) => new();'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool IndexedFileExists(string projectRoot, string relativePath)
    {
        using var conn = OpenNonPoolingConnection(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE path = @path LIMIT 1";
        cmd.Parameters.AddWithValue("@path", relativePath);
        return cmd.ExecuteScalar() != null;
    }

    private static void DeleteIndexedProjectRootMetadata(string dbPath)
    {
        using var conn = OpenNonPoolingConnection(dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", DbContext.IndexedProjectRootMetaKey);
        cmd.ExecuteNonQuery();
    }

    private static string CreateTempProject()
        => TestProjectHelper.CreateTempProject("cdidx_index_runner");

    private static string CreateTemporaryDotnetHostPath()
    {
        var hostDir = TestProjectHelper.CreateTempProject("cdidx_dotnet_host");
        var hostPath = Path.Combine(hostDir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(hostPath, string.Empty);
        return hostPath;
    }

    private static void DeleteTemporaryDotnetHostPath(string hostPath)
    {
        var hostDir = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(hostDir))
            TestProjectHelper.DeleteDirectory(hostDir);
    }

    private static int CountOccurrences(string text, string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("value must be non-empty", nameof(value));

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static void AssertNoRawStackTrace(string stderr)
    {
        Assert.DoesNotContain("Unhandled exception", stderr);
        Assert.DoesNotContain("System.UnauthorizedAccessException", stderr);
        Assert.DoesNotContain("Microsoft.Data.Sqlite.SqliteException", stderr);
        Assert.DoesNotContain(" at CodeIndex.", stderr);
        Assert.DoesNotContain(".cs:line ", stderr);
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetUnixPermissions(string path, UnixFileMode mode)
    {
        File.SetUnixFileMode(path, mode);
    }

    private static void CreateUnixFifo(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "mkfifo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(path);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start mkfifo / mkfifo の起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mkfifo failed: {stderr.Trim()}");
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ln",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(existingPath);
        psi.ArgumentList.Add(newPath);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ln / ln の起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ln failed: {stderr.Trim()}");
    }

    private static void WriteOversizedAsciiFile(string path)
    {
        const int targetBytes = 10 * 1024 * 1024 + 1;
        var chunk = new byte[8192];
        Array.Fill(chunk, (byte)'a');

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        int written = 0;
        while (written < targetBytes)
        {
            var toWrite = Math.Min(chunk.Length, targetBytes - written);
            stream.Write(chunk, 0, toWrite);
            written += toWrite;
        }
    }

    private static void RunGit(string workDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");

        if (args.Length == 1 && args[0] == "init")
        {
            RunGit(workDir, "config", "user.name", "CodeIndex Tests");
            RunGit(workDir, "config", "user.email", "tests@codeindex.local");
            RunGit(workDir, "config", "commit.gpgsign", "false");
            RunGit(workDir, "config", "tag.gpgsign", "false");
        }
    }

    private static string RunGitCaptureStdOut(string workDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");

        return stdout;
    }

    private static void DeleteDirectory(string path)
        => TestProjectHelper.DeleteDirectory(path);

    private static void DeleteFile(string path)
        => TestProjectHelper.DeleteFile(path);

    private static string CreateTempDbPath(string prefix)
        => TestProjectHelper.CreateTempDbPath(prefix);

    [Fact]
    public void IndexLock_Acquire_OnPosix_WritesPrivateInfoFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_private_lock");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            using var indexLock = IndexLock.Acquire(lockPath, projectRoot);

            Assert.True(File.Exists(infoPath));
            var info = File.ReadAllText(infoPath);
            Assert.Contains("pid=", info);
            Assert.Contains("started_at=", info);
            Assert.DoesNotContain("host=", info);
            Assert.DoesNotContain("project=", info);
            Assert.DoesNotContain(projectRoot, info);
            Assert.Equal(
                DataDirectorySecurity.PrivateFileMode,
                File.GetUnixFileMode(infoPath) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(infoPath);
            DeleteFile(lockPath);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void IndexLock_Dispose_WhenMetadataCleanupFails_ReportsSanitizedDiagnostic_Issue3462()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_cleanup_diag");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        var diagnostics = new List<LockCleanupDiagnostic>();
        try
        {
            IndexLock.CleanupDiagnosticSinkForTesting = diagnostics.Add;
            IndexLock.DeleteFileForTesting = path =>
            {
                if (string.Equals(path, infoPath, StringComparison.Ordinal))
                    throw new IOException($"sensitive cleanup path {path}");
                File.Delete(path);
            };

            using (IndexLock.Acquire(lockPath, projectRoot))
            {
            }

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("index_lock", diagnostic.Component);
            Assert.Equal("metadata", diagnostic.Target);
            Assert.Equal("io_error", diagnostic.Reason);
            Assert.DoesNotContain("sensitive", diagnostic.ToLogMessage(), StringComparison.Ordinal);
            Assert.DoesNotContain(infoPath, diagnostic.ToLogMessage(), StringComparison.Ordinal);
        }
        finally
        {
            IndexLock.CleanupDiagnosticSinkForTesting = null;
            IndexLock.DeleteFileForTesting = File.Delete;
            DeleteDirectory(projectRoot);
            DeleteFile(infoPath);
            DeleteFile(lockPath);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void IndexLock_TryReadHolderInfo_WhenInfoFileTooLarge_ReturnsNull()
    {
        var dbPath = CreateTempDbPath("cdidx_large_lock");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            File.WriteAllText(infoPath, new string('x', 17 * 1024));

            Assert.Null(IndexLock.TryReadHolderInfo(lockPath));
        }
        finally
        {
            DeleteFile(infoPath);
            DeleteFile(lockPath);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void IndexLock_TryReadHolderInfo_WhenProcessDoesNotMatch_MarksMetadataStale_Issue3825()
    {
        var dbPath = CreateTempDbPath("cdidx_stale_holder");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            File.WriteAllText(infoPath, "pid=2147483647\nstarted_at=2020-01-01T00:00:00.000Z\n");

            var holder = IndexLock.TryReadHolderInfo(lockPath);

            Assert.NotNull(holder);
            Assert.Equal(2147483647, holder.Pid);
            Assert.Equal(IndexLockHolderVerification.Stale, holder.Verification);
        }
        finally
        {
            DeleteFile(infoPath);
            DeleteFile(lockPath);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_LockHeldByAnotherHolder_RejectedWithHolderInfo()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_lock_held");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            // FileShare.None matches the holder mode IndexLock.Acquire uses, so a
            // competing cdidx process gets EWOULDBLOCK on its acquire attempt.
            // Sidecar .info file holds the metadata IndexLock.TryReadHolderInfo reads.
            File.WriteAllText(infoPath, "pid=98765\nstarted_at=2026-05-15T10:00:00.000Z\nhost=test-host\nproject=/tmp/xyz\n");
            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (exitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--db", dbPath]);

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Contains("another cdidx index is already running", stderr);
                Assert.Contains("PID 98765", stderr);
                Assert.Contains("--force", stderr);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(infoPath);
            DeleteFile(lockPath);
        }
    }

    [Fact]
    public void Run_LockHeldByAnotherHolder_JsonIncludesHint()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_lock_json");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            File.WriteAllText(infoPath, "pid=12345\nstarted_at=2026-05-15T10:00:00.000Z\nhost=h\nproject=/p\n");
            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Equal("error", json.GetProperty("status").GetString());
                var message = json.GetProperty("message").GetString();
                Assert.NotNull(message);
                Assert.Contains("another cdidx index is already running", message);
                Assert.Contains("PID 12345", message);
                var hint = json.GetProperty("hint").GetString();
                Assert.NotNull(hint);
                Assert.Contains("--force", hint);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(infoPath);
            DeleteFile(lockPath);
        }
    }

    [Fact]
    public void Run_LockHeldWithoutHolderInfo_ReportsDbLocked()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_lock_no_info");
        var lockPath = dbPath + ".lock";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.DbLocked, json.GetProperty("error_code").GetString());
                Assert.Contains("another cdidx index is already running", json.GetProperty("message").GetString());
                Assert.Contains("--force", json.GetProperty("hint").GetString());
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(lockPath + ".info");
            DeleteFile(lockPath);
        }
    }

    [Fact]
    public void Run_ForceFlag_BypassesLockEvenWhenHeld()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_lock_force");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--force", "--json"]);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("success", json.GetProperty("status").GetString());
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(infoPath);
            DeleteFile(lockPath);
        }
    }

    [Fact]
    public void Run_StaleLockFile_ReclaimedWithoutDeletingLockFileAfterSuccess_Issue3825()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_lock_stale");
        var lockPath = dbPath + ".lock";
        var infoPath = lockPath + ".info";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            // Stale on-disk lockfile with no live holder; OS releases handles on
            // process death so a fresh acquire must succeed. The lockfile path is
            // intentionally left in place after release to avoid racing a later holder.
            File.WriteAllText(lockPath, string.Empty);
            File.WriteAllText(infoPath, "pid=99999\nstarted_at=2020-01-01T00:00:00.000Z\nhost=stale\nproject=/old\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(File.Exists(lockPath), "lock file should remain after a clean exit");
            Assert.False(File.Exists(infoPath), "lock metadata sidecar should be removed after a clean exit");
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(infoPath);
            DeleteFile(lockPath);
        }
    }

    [Fact]
    public void WorkerProtocol_RejectsPayloadOverUtf8FrameLimitBeforeDomParse_Issue4058()
    {
        var json = "{\"x\":\"あ\"}";

        var valid = WorkerProtocolJsonValidator.TryValidate(json, json.Length, out var error);

        Assert.False(valid);
        Assert.Equal("worker_protocol_error: json_payload_length_exceeded", error);
    }

    [Fact]
    public void WorkerProtocolLineLimits_ClampHugeFileCapToExtendedProtocolLimit_Issue4058()
    {
        var protocolLimit = WorkerProtocolLineLimits.ResolveForSourceFileBytes(long.MaxValue);

        Assert.Equal(WorkerProtocolLineLimits.MaxExtendedLineUtf8Bytes, protocolLimit);
        Assert.True(protocolLimit < int.MaxValue);
    }


    [Fact]
    public void Run_ReadOnlyFlag_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        var dbPath = CreateTempDbPath("cdidx_index_readonly");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--read-only", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("query commands", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
        }
    }
}
