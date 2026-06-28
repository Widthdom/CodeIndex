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

public sealed class SkipOnMacOsArm64FactAttribute : FactAttribute
{
    public SkipOnMacOsArm64FactAttribute()
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Skip = "macOS arm64 SDK/ILLink can crash before this test can exercise cdidx (#2586).";
    }
}

public sealed class SkipOnMacOsArm64TheoryAttribute : TheoryAttribute
{
    public SkipOnMacOsArm64TheoryAttribute()
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Skip = "macOS arm64 SDK/ILLink currently crashes before this test can exercise cdidx (#2606).";
    }
}

/// <summary>
/// Tests for indexing command argument handling.
/// インデックスコマンドの引数処理テスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public partial class IndexCommandRunnerTests
{
    private static readonly TimeSpan LegacyEnvironmentHookWorkerBudget = TimeSpan.FromSeconds(30);
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
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_unknown_{Guid.NewGuid():N}.db");

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
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_unknown_json_{Guid.NewGuid():N}.db");

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
    public void FormatIndexPhasePath_AppendsPhaseSuffixForJsonLiveness()
    {
        var message = IndexCommandRunner.FormatIndexPhasePath("src/App.cs", "references");

        Assert.Equal("src/App.cs (references)", message);
    }

    [Fact]
    public void Run_FilesMode_WhenSymbolExtractionStalls_ReportsStallInsteadOfInterrupt()
    {
        var priorTimeout = IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        IndexCommandRunner.IndexExtractionStallTimeoutForTesting = () => TimeSpan.FromMilliseconds(1);
        var projectRoot = CreateTempProject();
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

            var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_symbol_timeout_{Guid.NewGuid():N}.db");
            var (exitCode, json, stderr) = RunAndCaptureJsonWithStderr([projectRoot, "--files", "slow.cs", "--db", dbPath, "--json", "--force"]);

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(CommandErrorCodes.IndexExtractionStalled, json.GetProperty("error_code").GetString());
            Assert.Contains("Index extraction made no progress", json.GetProperty("message").GetString());
            Assert.DoesNotContain(CommandErrorCodes.Interrupted, stderr);
        }
        finally
        {
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = priorTimeout;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
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
                    LegacyEnvironmentHookWorkerBudget);

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

    [SkipOnMacOsArm64Fact]
    public void Run_PublishedSingleFileBinary_IndexesWithIsolatedSymbolWorker()
    {
        var projectRoot = CreateTempProject();
        var publishDir = Path.Combine(Path.GetTempPath(), $"cdidx_single_file_publish_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_single_file_index_{Guid.NewGuid():N}.db");
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
            SqliteConnection.ClearAllPools();
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
            var nestedStart = string.Concat(Enumerable.Repeat("[", depth));
            var nestedEnd = string.Concat(Enumerable.Repeat("]", depth));
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
            SqliteConnection.ClearAllPools();
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

            using var db = new DbContext(dbPath);
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
            SqliteConnection.ClearAllPools();
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

            using var db = new DbContext(dbPath);
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
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FileAboveMaxReferencesPerFile_FullScanPersistsReferenceCountExceededIssueOnly_Issue3719()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "DenseReferences.cs");
            File.WriteAllText(filePath, BuildDenseReferenceCSharpSource(8));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--max-references-per-file", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.True(CountRows(dbPath, "chunks") > 0);
            Assert.True(CountRows(dbPath, "symbols") > 0);
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("reference_count_exceeded"));
            Assert.Equal("DenseReferences.cs", issue.Path);
            Assert.Equal(0, issue.Line);
            Assert.Contains("--max-references-per-file", issue.Message);

            var (raisedExitCode, raisedJson) = RunAndCaptureJson([projectRoot, "--max-references-per-file", "100", "--json"]);

            Assert.Equal(CommandExitCodes.Success, raisedExitCode);
            Assert.Equal("success", raisedJson.GetProperty("status").GetString());
            Assert.True(CountRows(dbPath, "symbol_references") > 0);
            Assert.Empty(reader.GetIssues("reference_count_exceeded"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FileAboveMaxReferencesPerFile_UpdatePersistsReferenceCountExceededIssueOnly_Issue3719()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var filePath = Path.Combine(projectRoot, "DenseReferences.cs");
            File.WriteAllText(filePath, BuildDenseReferenceCSharpSource(8));

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--max-references-per-file", "100", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.True(CountRows(dbPath, "symbol_references") > 0);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", filePath, "--max-references-per-file", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, CountRows(dbPath, "files"));
            Assert.True(CountRows(dbPath, "chunks") > 0);
            Assert.True(CountRows(dbPath, "symbols") > 0);
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));

            using var db = new DbContext(dbPath);
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var issue = Assert.Single(reader.GetIssues("reference_count_exceeded"));
            Assert.Equal("DenseReferences.cs", issue.Path);
            Assert.Contains("--max-references-per-file", issue.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_SymbolsOnly_OnGraphReadyDbDemotesReferencesAndSqlContract()
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

            var (normalExitCode, normalJson) = RunAndCaptureJson([projectRoot, "--json"]);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(CommandExitCodes.Success, normalExitCode);
            Assert.True(normalJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(normalJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(CountRows(dbPath, "symbol_references") > 0);
            Assert.True(CountRows(dbPath, "reference_lines") > 0);

            var (symbolsOnlyExitCode, symbolsOnlyJson) = RunAndCaptureJson([projectRoot, "--symbols-only", "--json"]);

            Assert.Equal(CommandExitCodes.Success, symbolsOnlyExitCode);
            Assert.False(symbolsOnlyJson.GetProperty("graph_table_available").GetBoolean());
            Assert.False(symbolsOnlyJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.True(symbolsOnlyJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Equal(0, CountRows(dbPath, "symbol_references"));
            Assert.Equal(0, CountRows(dbPath, "reference_lines"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            using var db = new DbContext(dbPath);
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
        var tooMany = string.Join(',', Enumerable.Repeat("class", IndexCommandRunner.MaxSymbolKindFilterCsvEntries + 1));

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
            string.Join(',', Enumerable.Repeat("function", IndexCommandRunner.MaxSymbolKindFilterCsvEntries + 1)));

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
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
    public void ParseArgs_DebounceFlag_InvalidValue_IsIgnored()
    {
        var originalErr = Console.Error;
        using var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);
            var options = IndexCommandRunner.ParseArgs([".", "--watch", "--debounce", "not-a-number"]);
            Assert.True(options.Watch);
            Assert.Null(options.WatchDebounceMs);
            Assert.Contains("invalid --debounce value", stderr.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
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
        Assert.Contains("--max-symbols-per-file must be less than or equal to 50000", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxReferencesPerFileFlag_RejectsValueAboveMaximum_Issue3719()
    {
        var aboveMaximum = $"{IndexCommandRunner.MaxReferencesPerFileLimit + 1}";
        var options = IndexCommandRunner.ParseArgs([".", $"--max-references-per-file={aboveMaximum}"]);

        Assert.Equal(IndexCommandRunner.DefaultMaxReferencesPerFile, options.MaxReferencesPerFile);
        Assert.Contains("--max-references-per-file must be less than or equal to 1000000", options.ParseError);
    }

    [Fact]
    public void ParseArgs_MaxFileBytesInvalidValue_IsIgnored()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalErr = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);
                var options = IndexCommandRunner.ParseArgs([".", "--max-file-bytes", "0"]);

                Assert.True(options.MaxFileSizeBytes is null or > 0);
                Assert.Contains("invalid --max-file-bytes value", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
    }

    [Fact]
    public void ParseArgs_MaxFileBytesInvalidValue_TruncatesOversizedValue()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalErr = Console.Error;
            using var stderr = new StringWriter();
            var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);
            try
            {
                Console.SetError(stderr);

                _ = IndexCommandRunner.ParseArgs([".", "--max-file-bytes", value]);

                var warning = stderr.ToString();
                Assert.Contains("invalid --max-file-bytes value", warning);
                Assert.Contains("<truncated; original length", warning);
                Assert.DoesNotContain(value, warning);
            }
            finally
            {
                Console.SetError(originalErr);
            }
        }
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
    public void ParseArgs_ParallelismFlagClampsOversizedValue_Issue2904()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;
            using var stderr = new StringWriter();
            try
            {
                Console.SetError(stderr);

                var options = IndexCommandRunner.ParseArgs([".", "--parallelism", "999"]);

                Assert.Equal(IndexCommandRunner.MaxIndexParallelism, options.Parallelism);
                Assert.Contains("--parallelism", stderr.ToString());
                Assert.Contains($"maximum {IndexCommandRunner.MaxIndexParallelism}", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
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
                Assert.Contains($"maximum {IndexCommandRunner.MaxIndexParallelism}", stderr.ToString());
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_explicit_root_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var db = new DbContext(dbPath);
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
    public void Run_VerboseReportsUnknownExtensionCountAndStatusJsonStampsCount()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "notes.mystery"), "unknown extension\n");
            File.WriteAllText(Path.Combine(projectRoot, "data.unmapped"), "also unknown\n");

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--verbose"]);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("Unknown extension files: 2", stdout);
            Assert.Contains("data.unmapped", stdout);
            Assert.Contains("notes.mystery", stdout);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(2, statusJson.GetProperty("unknown_extension_file_count").GetInt64());
            Assert.False(statusJson.GetProperty("unknown_extension_files_truncated").GetBoolean());
            Assert.Equal(50, statusJson.GetProperty("unknown_extension_file_path_limit").GetInt64());
            var paths = statusJson.GetProperty("unknown_extension_files")
                .EnumerateArray()
                .Select(path => path.GetString())
                .ToArray();
            Assert.Equal(["data.unmapped", "notes.mystery"], paths);
            var extensionCounts = statusJson.GetProperty("unknown_extension_extension_counts");
            Assert.Equal(1, extensionCounts.GetProperty(".mystery").GetInt64());
            Assert.Equal(1, extensionCounts.GetProperty(".unmapped").GetInt64());
            var categoryCounts = statusJson.GetProperty("unknown_extension_category_counts");
            Assert.Equal(2, categoryCounts.GetProperty("language_support_candidate").GetInt64());
            var groups = statusJson.GetProperty("unknown_extension_groups").EnumerateArray().ToArray();
            Assert.Equal(2, groups.Length);
            Assert.All(groups, group =>
            {
                Assert.Equal("language_support_candidate", group.GetProperty("category").GetString());
                Assert.Equal("language_support", group.GetProperty("recommended_action").GetString());
                Assert.Equal(1, group.GetProperty("count").GetInt64());
            });
        }
        finally
        {
            DeleteDirectory(projectRoot);
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_head_meta_{Guid.NewGuid():N}.db");
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

            using var db = new DbContext(dbPath);
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_head_meta_none_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var db = new DbContext(dbPath);
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_path_case_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");

            var (exitCode, _) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            using var db = new DbContext(dbPath);
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_head_meta_detached_{Guid.NewGuid():N}.db");
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

            using var db = new DbContext(dbPath);
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
                Assert.Contains("database not found", stderr.ToString());
                Assert.Contains("Point `--db` at an existing `codeindex.db`", stderr.ToString());
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
                Assert.Contains("database not found", json.GetProperty("message").GetString());
                Assert.Contains("Point `--db` at an existing `codeindex.db`", json.GetProperty("hint").GetString());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    [SkipOnMacOsArm64Fact]
    public void RunBackfillFold_PublishedTrimmedBinary_SerializesSuccessAndErrorJson()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_trimmed_backfill_{Guid.NewGuid():N}.db");
        var missingDbPath = Path.Combine(Path.GetTempPath(), $"cdidx_trimmed_missing_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
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
            Assert.Contains("database not found", errorJson.GetProperty("message").GetString());
            Assert.Contains("Point `--db` at an existing `codeindex.db`", errorJson.GetProperty("hint").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
            DeleteFile(missingDbPath);
        }
    }

    [Fact]
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


    [Fact]
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
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public void Run_ExplicitDbInReadOnlyParent_ReturnsDatabaseErrorWithoutStackTrace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var dbParent = Path.Combine(Path.GetTempPath(), $"cdidx_readonly_db_parent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dbParent);
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
    public void RunOptimizeFts_ExistingDb_ResetsCounterAndEmitsJson()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_optimize_fts_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db);
                writer.RecordFtsIncrementalWrite();
                writer.RecordFtsIncrementalWrite();
            }

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
            Assert.Equal(2, json.GetProperty("writes_since_optimize_before").GetInt32());
            Assert.Equal(0, json.GetProperty("writes_since_optimize_after").GetInt32());

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal("0", verifyDb.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbWriter.FtsLastOptimizedAtMetaKey)));
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_optimize_locked_{Guid.NewGuid():N}.db");
        var lockPath = dbPath + ".lock";
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();

            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
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

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Equal("error", json.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.DbLocked, json.GetProperty("error_code").GetString());
                Assert.Contains("another cdidx index is already running", json.GetProperty("message").GetString());
                Assert.Contains("retry `cdidx optimize`", json.GetProperty("hint").GetString());
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
    public void RunOptimizeFts_ReadOnlyUri_ReturnsDbNotWritable()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_optimize_readonly_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db);
                writer.RecordFtsIncrementalWrite();
            }

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
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
            Assert.Contains("database must be writable for optimize", json.GetProperty("message").GetString());

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal("1", verifyDb.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_optimize_uri_cap_{Guid.NewGuid():N}.db");
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
    public void Run_IndexOptimizeWithDryRun_ReturnsUsageErrorWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db);
                writer.RecordFtsIncrementalWrite();
                writer.SetMeta(DbWriter.FtsLastOptimizedAtMetaKey, "sentinel");
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--optimize", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.UsageError, json.GetProperty("error_code").GetString());
            Assert.Contains("--optimize cannot be combined", json.GetProperty("message").GetString());

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal("1", verifyDb.GetMetaString(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
            Assert.Equal("sentinel", verifyDb.GetMetaString(DbWriter.FtsLastOptimizedAtMetaKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunBackfillFold_BackfillsLegacyRowsAndStampsFoldReady()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_fold_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
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
            Assert.Equal(3, json.GetProperty("user_version_before").GetInt32());
            Assert.Equal(7, json.GetProperty("user_version_after").GetInt32());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            using var verifyDb = new DbContext(dbPath);
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
        }
    }

    [Fact]
    public void RunBackfillFold_DryRunReportsRowsWithoutWriting()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_fold_dry_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
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

            using var verifyDb = new DbContext(dbPath);
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
    public void RunBackfillFold_DryRunReportsEffectiveFoldReadyWhenMetadataStale()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_fold_stale_dry_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
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

            using var verifyDb = new DbContext(dbPath);
            Assert.Equal("DEADBEEFDEADBEEF", verifyDb.GetMetaString("fold_key_fingerprint"));
            Assert.Equal(DbContext.FoldReadyFlag, verifyDb.GetUserVersion());
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_fold_fp_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
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

            using var verifyDb = new DbContext(dbPath);
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
        }
    }

    [Fact]
    public void BackfillFoldedColumns_CancelledDuringSymbolLoop_KeepsCompletedRowsForResume()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_cancel_symbols_{Guid.NewGuid():N}.db");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(dbPath))
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_cancel_refs_{Guid.NewGuid():N}.db");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(dbPath))
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_rewrite_resume_{Guid.NewGuid():N}.db");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(dbPath))
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_rewrite_refs_resume_{Guid.NewGuid():N}.db");
        var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(dbPath))
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_cancel_cli_{Guid.NewGuid():N}.db");
        using var cts = new CancellationTokenSource();
        try
        {
            using (var db = new DbContext(dbPath))
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_blank_{Guid.NewGuid():N}.db");
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
            Assert.Contains("not an existing CodeIndex DB", json.GetProperty("message").GetString());
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_missing_{Guid.NewGuid():N}.db");
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
        Assert.Contains("database not found", json.GetProperty("message").GetString());
    }

    [Fact]
    public void RunBackfillFold_LegacyDbWithoutCodeIndexMeta_Succeeds()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_backfill_legacy_no_meta_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
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

            using var verifyDb = new DbContext(dbPath);
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
    public void Run_Rebuild_CancelledAfterReadinessDemotion_PreservesExistingIndex()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            int initialReadiness;
            using (var db = new DbContext(dbPath))
                initialReadiness = db.GetUserVersion();
            Assert.Equal(DbContext.CurrentSchemaVersion, initialReadiness);
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "later.cs"), "public class Later { }\n");
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
                using var db = new DbContext(dbPath);
                Assert.Equal(initialReadiness, db.GetUserVersion());
            });
            Assert.DoesNotContain("Last batch did not complete", reopenWarning);
            Assert.DoesNotContain("later.cs", ReadIndexedPaths(dbPath));
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));
        }
        finally
        {
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
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
    public void Run_FilesUpdate_ReindexesUnchangedJsonFileWhenExpandedLanguageExtractorVersionChanged()
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
                    INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES('symbol_extractor_version_json', '1');
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

    [Fact]
    public void Run_WithAbsoluteDbPathOutsideProject_DoesNotWriteAbsolutePathToGitExclude()
    {
        var projectRoot = CreateTempProject();
        var outsideDir = Path.Combine(Path.GetTempPath(), $"cdidx_external_db_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outsideDir);
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
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cdidx_worktree_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
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
    public void Run_Rebuild_IgnoresUnreadableDirectoriesWhenCollectingMarkerFingerprints()
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
            originalMode = File.GetUnixFileMode(unreadableDir);
            File.SetUnixFileMode(unreadableDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("app.cs", indexedPaths);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(unreadableDir))
                File.SetUnixFileMode(unreadableDir, originalMode.Value);
            SqliteConnection.ClearAllPools();
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
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_AfterCommitScopedRefreshAtHead_DoesNotReportHeadChanged()
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
            var currentHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (refreshExitCode, _) = RunAndCaptureJson([projectRoot, "--commits", "HEAD", "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
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
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_AfterChangedBetweenRefreshAtHead_TreatsCurrentIndexedHeadShaAsFresh_2808()
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
            using (var db = new DbContext(dbPath))
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
            Assert.Equal(initialHead, check.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(currentHead, check.GetProperty("workspace_head_commit").GetString());

            var (dotExitCode, dotJson) = RunProgramAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, dotExitCode);
            Assert.Equal("success", dotJson.GetProperty("status").GetString());
            Assert.True(dotJson.GetProperty("head_changed").GetBoolean());
            Assert.Equal(initialHead, dotJson.GetProperty("prior_indexed_head_commit").GetString());
            Assert.Equal(currentHead, dotJson.GetProperty("current_head_commit").GetString());

            using (var db = new DbContext(dbPath))
                Assert.Equal(currentHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));

            var (postDotStatusExitCode, postDotStatusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, postDotStatusExitCode);
            Assert.True(postDotStatusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatusCheck_AfterFilesRefreshAtHead_StillReportsHeadChanged()
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

            var (refreshExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.UsageError, statusExitCode);

            var check = statusJson.GetProperty("workspace_check");
            Assert.True(check.GetProperty("head_changed").GetBoolean());
            Assert.False(check.GetProperty("matches_workspace").GetBoolean());
            Assert.Equal("head_changed", check.GetProperty("reason").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    private (int ExitCode, JsonElement Json) RunAndCaptureJson(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();

            try
            {
                Console.SetOut(writer);
                var exitCode = IndexCommandRunner.Run(args, _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunProgramAndCaptureJson(string[] args)
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
                var exitCode = ProgramRunner.Run(args, _jsonOptions, appVersion: "1.0.0-test", configStartDirectory: args[0]);
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

    private static void AssertFileDoesNotAppear(string path, TimeSpan duration)
    {
        var deadline = DateTimeOffset.UtcNow.Add(duration);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
                throw new InvalidOperationException("The timed-out symbol extraction worker continued running after the callback returned.");

            Thread.Sleep(25);
        }
    }

    private static void WriteSymbolWorkerPatternConfig(string projectRoot, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", "toydsl.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src", "CodeIndex")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }

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
        using var db = new DbContext(dbPath);
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
        using var db = new DbContext(dbPath);
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
        var hostDir = Path.Combine(Path.GetTempPath(), $"cdidx_dotnet_host_{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostDir);
        var hostPath = Path.Combine(hostDir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(hostPath, string.Empty);
        return hostPath;
    }

    private static void DeleteTemporaryDotnetHostPath(string hostPath)
    {
        var hostDir = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrWhiteSpace(hostDir) && Directory.Exists(hostDir))
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

    [Fact]
    public void IndexLock_Acquire_OnPosix_WritesPrivateInfoFile()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_private_lock_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_cleanup_diag_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_large_lock_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_stale_holder_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_held_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_json_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_no_info_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_force_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_stale_{Guid.NewGuid():N}.db");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_index_readonly_{Guid.NewGuid():N}.db");
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
