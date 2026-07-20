using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using CodeIndex.HookIsolationFixture;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class PostExtractionHookTests
{
    internal const string SlowHookDelayEnvironmentVariable = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_MS";
    internal const string SlowHookCompletionPathEnvironmentVariable = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_DONE_PATH";
    internal const string CancellableHookDelayEnvironmentVariable = "CDIDX_TEST_CANCELLABLE_POST_EXTRACTION_HOOK_MS";
    internal const string CancellableHookCompletionPathEnvironmentVariable = "CDIDX_TEST_CANCELLABLE_POST_EXTRACTION_HOOK_DONE_PATH";
    internal const string SlowConstructorHookDelayEnvironmentVariable = "CDIDX_TEST_SLOW_CTOR_POST_EXTRACTION_HOOK_MS";
    internal const string StatefulHookEnvironmentVariable = "CDIDX_TEST_STATEFUL_POST_EXTRACTION_HOOK";
    internal const string ThrowingConstructorHookEnvironmentVariable = "CDIDX_TEST_THROWING_CTOR_POST_EXTRACTION_HOOK";
    internal const string ExpandingHookEnvironmentVariable = "CDIDX_TEST_EXPANDING_POST_EXTRACTION_HOOK";
    internal const string ModuleInitializerDelayEnvironmentVariable = "CDIDX_TEST_HOOK_MODULE_INITIALIZER_DELAY_MS";
    internal const string PersistentDiscoveryWorkerPidPathEnvironmentVariable = "CDIDX_TEST_HOOK_DISCOVERY_PERSISTENT_PID_PATH";
    internal const string PersistentDiscoveryDescendantPidPathEnvironmentVariable = "CDIDX_TEST_HOOK_DISCOVERY_DESCENDANT_PID_PATH";
    private const string TimedOutHookDelayMilliseconds = "150";
    private static readonly TimeSpan TimedOutHookLeakObservationWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DuplicateHookCallbackBudget = TimeSpan.FromSeconds(5);

    [ProductionRuntimeFact]
    public void Discover_LoadsHooksAndAllowsSymbolAndReferenceMutation()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hooks");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

            {
                using var runner = PostExtractionHookRunner.Discover(hooksDir);
                var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                var symbols = new List<SymbolRecord>
                {
                    new() { FileId = 10, Kind = "class", Name = "App", Line = 1, StartLine = 1, EndLine = 1 },
                };
                var references = new List<ReferenceRecord>();

                runner.OnSymbolsExtracted(context, symbols);
                runner.OnReferencesExtracted(context, references);

                Assert.Contains(runner.Hooks, hook => hook.TypeName == typeof(SamplePostExtractionHook).FullName);
                var synthetic = Assert.Single(symbols, symbol => symbol.Name == "AppDomainTag");
                Assert.Equal(10, synthetic.FileId);
                var reference = Assert.Single(references, item => item.SymbolName == "AppDomainTag");
                Assert.Equal(10, reference.FileId);
            }
            CollectUnloadedHookAssemblies();
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Discover_UsesWorkerWithoutParentLoadContext_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-collectible-load");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

            using var runner = PostExtractionHookRunner.Discover(hooksDir);
            Assert.Equal(0, runner.ParentLoadContextCountForTests);
            Assert.Equal(
                "isolated_worker_process_no_parent_load_context",
                PostExtractionHookRunner.HookLoadContextLifecycle);
        }
        finally
        {
            CollectUnloadedHookAssemblies();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void DiscoveryWorker_TerminatesProcessTreeAfterManifest_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-persistent-discovery-4600");
        lock (TestConsoleLock.Gate)
        {
            using var persistent = EnvironmentVariableScope.Capture(PersistentDiscoveryWorkerPidPathEnvironmentVariable);
            using var descendant = EnvironmentVariableScope.Capture(PersistentDiscoveryDescendantPidPathEnvironmentVariable);
            int? descendantPid = null;
            try
            {
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "persistent.dll"));
                var workerPidPath = Path.Combine(projectRoot, "persistent-worker.pid");
                var descendantPidPath = Path.Combine(projectRoot, "persistent-descendant.pid");
                persistent.Set(PersistentDiscoveryWorkerPidPathEnvironmentVariable, workerPidPath);
                descendant.Set(PersistentDiscoveryDescendantPidPathEnvironmentVariable, descendantPidPath);

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                Assert.NotEmpty(runner.Hooks);
                Assert.True(File.Exists(workerPidPath));
                Assert.True(File.Exists(descendantPidPath));
                var workerPid = int.Parse(
                    File.ReadAllText(workerPidPath),
                    System.Globalization.CultureInfo.InvariantCulture);
                descendantPid = int.Parse(
                    File.ReadAllText(descendantPidPath),
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.NotEqual(Environment.ProcessId, workerPid);
                Assert.NotEqual(Environment.ProcessId, descendantPid);
                Assert.NotEqual(workerPid, descendantPid);
                TestDeterminism.WaitUntil(
                    () => !IsProcessRunning(workerPid),
                    "hook discovery worker termination",
                    getDiagnostics: () => $"worker_pid={workerPid}");
                TestDeterminism.WaitUntil(
                    () => !IsProcessRunning(descendantPid.Value),
                    "hook discovery descendant termination",
                    getDiagnostics: () => $"descendant_pid={descendantPid}");
            }
            finally
            {
                TryTerminateProcess(descendantPid);
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void Discover_IsolatesModuleInitializerAndSeparatesDuplicateHookIds_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-identities-4600");
        lock (TestConsoleLock.Gate)
        {
            using var moduleInitializer = EnvironmentVariableScope.Capture(HookIsolationFixtureEnvironment.ModuleInitializerPidPath);
            using var selectiveSlow = EnvironmentVariableScope.Capture(HookIsolationFixtureEnvironment.SelectiveSlowHookAssembly);
            var originalBudget = PostExtractionHookRunner.CallbackBudgetForTesting;
            try
            {
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                var firstAssembly = Path.Combine(hooksDir, "a.dll");
                var secondAssembly = Path.Combine(hooksDir, "b.dll");
                var fixtureAssembly = typeof(PathSelectivePostExtractionHook).Assembly.Location;
                File.Copy(fixtureAssembly, firstAssembly);
                File.Copy(fixtureAssembly, secondAssembly);
                var moduleInitializerPidPath = Path.Combine(projectRoot, "module-initializer.pid");
                moduleInitializer.Set(HookIsolationFixtureEnvironment.ModuleInitializerPidPath, moduleInitializerPidPath);
                selectiveSlow.Set(HookIsolationFixtureEnvironment.SelectiveSlowHookAssembly, Path.GetFileName(firstAssembly));
                PostExtractionHookRunner.CallbackBudgetForTesting = () => DuplicateHookCallbackBudget;

                using var runner = PostExtractionHookRunner.Discover(hooksDir);
                var duplicateHooks = runner.Hooks
                    .Where(hook => hook.TypeName == typeof(PathSelectivePostExtractionHook).FullName)
                    .OrderBy(hook => hook.AssemblyPath, StringComparer.Ordinal)
                    .ToArray();
                Assert.Equal(2, duplicateHooks.Length);
                Assert.Equal(2, duplicateHooks.Select(hook => hook.Id).Distinct(StringComparer.Ordinal).Count());
                Assert.All(duplicateHooks, hook => Assert.StartsWith("hook:", hook.Id, StringComparison.Ordinal));
                Assert.True(File.Exists(moduleInitializerPidPath));
                Assert.NotEqual(
                    Environment.ProcessId,
                    int.Parse(File.ReadAllText(moduleInitializerPidPath), System.Globalization.CultureInfo.InvariantCulture));

                var metadata = PostExtractionHookRunner.DiscoverMetadata(hooksDir);
                var metadataIds = metadata.Hooks
                    .Where(hook => hook.TypeName == typeof(PathSelectivePostExtractionHook).FullName)
                    .ToDictionary(hook => Path.GetFileName(hook.AssemblyPath), hook => hook.Id, StringComparer.Ordinal);
                Assert.Equal(duplicateHooks[0].Id, metadataIds[Path.GetFileName(duplicateHooks[0].AssemblyPath)]);
                Assert.Equal(duplicateHooks[1].Id, metadataIds[Path.GetFileName(duplicateHooks[1].AssemblyPath)]);

                var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                var symbols = new List<SymbolRecord>();
                runner.OnSymbolsExtracted(context, symbols);
                Assert.Single(symbols, symbol => symbol.Name == "Selective:b.dll");

                var slowHook = duplicateHooks.Single(hook => Path.GetFileName(hook.AssemblyPath) == "a.dll");
                var timeout = Assert.Single(
                    runner.Diagnostics,
                    diagnostic => diagnostic.TypeName == typeof(PathSelectivePostExtractionHook).FullName
                                  && diagnostic.Category == "callback_timeout");
                Assert.Equal(slowHook.Id, timeout.HookId);

                runner.OnSymbolsExtracted(context, symbols);
                Assert.Equal(2, symbols.Count(symbol => symbol.Name == "Selective:b.dll"));
                Assert.Single(
                    runner.Diagnostics,
                    diagnostic => diagnostic.TypeName == typeof(PathSelectivePostExtractionHook).FullName
                                  && diagnostic.Category == "callback_timeout");
            }
            finally
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = originalBudget;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void DiscoveryWorker_EnforcesTimeoutMemoryAndOutputBounds_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-discovery-bounds-4600");
        lock (TestConsoleLock.Gate)
        {
            using var delay = EnvironmentVariableScope.Capture(ModuleInitializerDelayEnvironmentVariable);
            var originalBudget = PostExtractionHookDiscoveryWorkerClient.DiscoveryBudgetForTesting;
            try
            {
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                var hookPath = Path.Combine(hooksDir, "bounded.dll");
                File.Copy(Assembly.GetExecutingAssembly().Location, hookPath);
                delay.Set(ModuleInitializerDelayEnvironmentVariable, "30000");
                PostExtractionHookDiscoveryWorkerClient.DiscoveryBudgetForTesting = TimeSpan.FromMilliseconds(500);

                using (var runner = PostExtractionHookRunner.Discover(hooksDir))
                {
                    Assert.Empty(runner.Hooks);
                    Assert.Contains(
                        runner.Diagnostics,
                        diagnostic => diagnostic.Category == "hook_discovery_timeout");
                }

                PostExtractionHookDiscoveryWorkerClient.DiscoveryBudgetForTesting = TimeSpan.FromSeconds(5);
                var memoryResult = PostExtractionHookDiscoveryWorkerClient.Discover(
                    Assembly.GetExecutingAssembly().Location,
                    PostExtractionHookRunner.DefaultTypeInspectionLimit,
                    memoryLimitBytes: 1);
                Assert.False(memoryResult.Success);
                Assert.Equal("hook_discovery_memory_limit", memoryResult.ErrorCategory);

                delay.Set(ModuleInitializerDelayEnvironmentVariable, null);
                var outputResult = PostExtractionHookDiscoveryWorkerClient.Discover(
                    Assembly.GetExecutingAssembly().Location,
                    PostExtractionHookRunner.DefaultTypeInspectionLimit,
                    maxProtocolLineBytes: 256);
                Assert.False(outputResult.Success);
                Assert.Equal("hook_discovery_output_limit", outputResult.ErrorCategory);
            }
            finally
            {
                PostExtractionHookDiscoveryWorkerClient.DiscoveryBudgetForTesting = originalBudget;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void CallbackWorker_LoadsHookAssemblyInCollectibleContext_3413()
    {
        var request = new PostExtractionHookCallbackWorker.WorkerRequest(
            nameof(IPostExtractionHook.OnSymbolsExtracted),
            new FileContext("project", "src/App.cs", "/project/src/App.cs", "csharp"),
            [],
            null);
        using var input = new StringReader(JsonSerializer.Serialize(request, PostExtractionHookCallbackWorker.JsonOptions) + Environment.NewLine);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var handled = PostExtractionHookCallbackWorker.TryRunCommand(
            [
                PostExtractionHookCallbackWorker.CommandName,
                Assembly.GetExecutingAssembly().Location,
                typeof(LoadContextReportingPostExtractionHook).FullName!,
            ],
            input,
            output,
            error,
            out var exitCode);

        Assert.True(handled);
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        var response = JsonSerializer.Deserialize<PostExtractionHookCallbackWorker.WorkerResponse>(
            output.ToString(),
            PostExtractionHookCallbackWorker.JsonOptions);
        Assert.NotNull(response);
        Assert.Null(response.WorkerError);
        Assert.Null(response.CallbackError);
        Assert.Contains(response.Symbols!, symbol => symbol.Name == "CollectibleHookLoadContext");
    }

    [ProductionRuntimeFact]
    public void CallbackExceptions_AreDiagnosticsAndDoNotBlockOtherHooks()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-failure");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

            {
                using var runner = PostExtractionHookRunner.Discover(hooksDir);
                var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                var symbols = new List<SymbolRecord>();

                runner.OnSymbolsExtracted(context, symbols);

                Assert.Contains(symbols, symbol => symbol.Name == "AppDomainTag");
                var diagnostic = Assert.Single(
                    runner.Diagnostics,
                    diagnostic => diagnostic.TypeName == typeof(ThrowingPostExtractionHook).FullName);
                Assert.Equal("hook_callback_failed", diagnostic.Category);
                Assert.DoesNotContain("boom", diagnostic.Message, StringComparison.Ordinal);
            }
            CollectUnloadedHookAssemblies();
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void WorkerConstructionFailure_DisablesHookForCurrentRun()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-ctor-failure");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ThrowingConstructorHookEnvironmentVariable);
            try
            {
                env.Set(ThrowingConstructorHookEnvironmentVariable, "1");
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                {
                    using var runner = PostExtractionHookRunner.Discover(hooksDir);
                    var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                    var symbols = new List<SymbolRecord>();
                    var references = new List<ReferenceRecord>();

                    runner.OnSymbolsExtracted(context, symbols);
                    runner.OnReferencesExtracted(context, references);

                    var diagnostic = Assert.Single(
                        runner.Diagnostics,
                        diagnostic => diagnostic.TypeName == typeof(ThrowingConstructorPostExtractionHook).FullName);
                    Assert.Equal("constructor_failed", diagnostic.Category);
                    Assert.Contains("isolated worker", diagnostic.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("ctor boom", diagnostic.Message, StringComparison.Ordinal);
                }
                CollectUnloadedHookAssemblies();
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void Callbacks_ReuseIsolatedWorkerHookInstance()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-state");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(StatefulHookEnvironmentVariable);
            try
            {
                env.Set(StatefulHookEnvironmentVariable, "1");
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                {
                    using var runner = PostExtractionHookRunner.Discover(hooksDir);
                    var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                    var symbols = new List<SymbolRecord>();
                    var references = new List<ReferenceRecord>();

                    runner.OnSymbolsExtracted(context, symbols);
                    runner.OnReferencesExtracted(context, references);

                    Assert.Contains(references, reference => reference.SymbolName == "StatefulHookSawSymbols");
                }
                CollectUnloadedHookAssemblies();
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void CallbackBudgetExceeded_KillsWorkerAndSkipsTimedOutMutation()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-budget");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                SlowHookDelayEnvironmentVariable,
                SlowHookCompletionPathEnvironmentVariable);
            var originalBudget = PostExtractionHookRunner.CallbackBudgetForTesting;
            try
            {
                env.Set(SlowHookDelayEnvironmentVariable, TimedOutHookDelayMilliseconds);
                PostExtractionHookRunner.CallbackBudgetForTesting = () => TimeSpan.FromMilliseconds(100);
                var hooksDir = Path.Combine(projectRoot, "hooks");
                var completionPath = Path.Combine(projectRoot, "slow-hook.done");
                env.Set(SlowHookCompletionPathEnvironmentVariable, completionPath);
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                {
                    using var runner = PostExtractionHookRunner.Discover(hooksDir);
                    var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                    var symbols = new List<SymbolRecord>();

                    runner.OnSymbolsExtracted(context, symbols);
                    AssertFileDoesNotAppear(completionPath, TimedOutHookLeakObservationWindow);

                    Assert.DoesNotContain(symbols, symbol => symbol.Name == "SlowHookTag");
                    var diagnostic = Assert.Single(
                        runner.Diagnostics,
                        item => item.TypeName == typeof(SlowPostExtractionHook).FullName
                                && item.Callback == nameof(IPostExtractionHook.OnSymbolsExtracted));
                    Assert.Equal("callback_timeout", diagnostic.Category);
                    Assert.True(
                        diagnostic.Message.Contains("exceeded", StringComparison.Ordinal),
                        diagnostic.Message);
                    Assert.Contains("hook disabled for this index run", diagnostic.Message, StringComparison.Ordinal);
                    // The worker wait can time out at the budget boundary before
                    // ElapsedMilliseconds rounds up to the full budget on some CI hosts.
                    Assert.True(diagnostic.DurationMs > 0);
                    Assert.Equal(100, (long)Math.Round(runner.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero));
                }
                CollectUnloadedHookAssemblies();
            }
            finally
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = originalBudget;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void CallbackBudgetExceeded_KillsSlowConstructorAfterLargeRequestIsSent()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-slow-ctor");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(SlowConstructorHookDelayEnvironmentVariable);
            var originalBudget = PostExtractionHookRunner.CallbackBudgetForTesting;
            try
            {
                env.Set(SlowConstructorHookDelayEnvironmentVariable, TimedOutHookDelayMilliseconds);
                PostExtractionHookRunner.CallbackBudgetForTesting = () => TimeSpan.FromMilliseconds(50);
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                {
                    using var runner = PostExtractionHookRunner.Discover(hooksDir);
                    var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                    var symbols = Enumerable
                        .Range(0, 1000)
                        .Select(index => new SymbolRecord
                        {
                            FileId = 10,
                            Kind = "function",
                            Name = $"LargePayloadSymbol{index}",
                            Line = index + 1,
                            StartLine = index + 1,
                            EndLine = index + 1,
                            Signature = new string('x', 512),
                        })
                        .ToList();

                    runner.OnSymbolsExtracted(context, symbols);

                    var diagnostic = Assert.Single(
                        runner.Diagnostics,
                        item => item.TypeName == typeof(SlowConstructorPostExtractionHook).FullName
                                && item.Callback == nameof(IPostExtractionHook.OnSymbolsExtracted));
                    Assert.Equal("callback_timeout", diagnostic.Category);
                    Assert.Contains("exceeded", diagnostic.Message, StringComparison.Ordinal);
                    Assert.True(diagnostic.DurationMs > 0);
                }
                CollectUnloadedHookAssemblies();
            }
            finally
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = originalBudget;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void OnSymbolsExtracted_CancellationWhileWaitingForCallback_KillsWorker_Issue3773()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-cancel");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                CancellableHookDelayEnvironmentVariable,
                CancellableHookCompletionPathEnvironmentVariable);
            var originalBudget = PostExtractionHookRunner.CallbackBudgetForTesting;
            try
            {
                env.Set(CancellableHookDelayEnvironmentVariable, TimedOutHookDelayMilliseconds);
                PostExtractionHookRunner.CallbackBudgetForTesting = () => TimeSpan.FromSeconds(5);
                var hooksDir = Path.Combine(projectRoot, "hooks");
                var completionPath = Path.Combine(projectRoot, "cancellable-hook.done");
                env.Set(CancellableHookCompletionPathEnvironmentVariable, completionPath);
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                using (var runner = PostExtractionHookRunner.Discover(hooksDir))
                using (var cancellation = new CancellationTokenSource())
                {
                    var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                    var symbols = new List<SymbolRecord>();
                    cancellation.CancelAfter(50);

                    Assert.ThrowsAny<OperationCanceledException>(() =>
                        runner.OnSymbolsExtracted(context, symbols, cancellation.Token));
                    AssertFileDoesNotAppear(completionPath, TimedOutHookLeakObservationWindow);
                }
                CollectUnloadedHookAssemblies();
            }
            finally
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = originalBudget;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void CallbackBudget_NormalizesInvalidAndTooLargeValues()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalBudget = PostExtractionHookRunner.CallbackBudgetForTesting;
            try
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = () => TimeSpan.Zero;
                using (var defaulted = PostExtractionHookRunner.Discover(null))
                {
                    Assert.Equal(PostExtractionHookRunner.DefaultCallbackBudget, defaulted.CallbackBudget);
                }

                PostExtractionHookRunner.CallbackBudgetForTesting = () => TimeSpan.FromMilliseconds((double)int.MaxValue + 1);
                using var capped = PostExtractionHookRunner.Discover(null);
                Assert.Equal(
                    PostExtractionHookRunner.MaxCallbackBudgetMilliseconds,
                    (long)Math.Round(capped.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero));
                var diagnostic = Assert.Single(capped.Diagnostics);
                Assert.Equal("hook_callback_budget_clamped", diagnostic.Category);
                Assert.Contains("clamped", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = originalBudget;
            }
        }
    }

    [Fact]
    public void Discover_ClampsOversizedDiscoveryLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-discovery-limit-clamp");
        lock (TestConsoleLock.Gate)
        {
            var originalLimit = PostExtractionHookRunner.DiscoveryLimitForTesting;
            try
            {
                PostExtractionHookRunner.DiscoveryLimitForTesting = () => PostExtractionHookRunner.MaxDiscoveryLimit + 1;
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                var diagnostic = Assert.Single(runner.Diagnostics);
                Assert.Equal("hook_discovery_limit_clamped", diagnostic.Category);
                Assert.Contains(PostExtractionHookRunner.MaxDiscoveryLimit.ToString(), diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                PostExtractionHookRunner.DiscoveryLimitForTesting = originalLimit;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Discover_ClampsOversizedDiscoveryMaxBytes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-discovery-bytes-clamp");
        lock (TestConsoleLock.Gate)
        {
            var originalMaxBytes = PostExtractionHookRunner.DiscoveryMaxBytesForTesting;
            try
            {
                PostExtractionHookRunner.DiscoveryMaxBytesForTesting = () => PostExtractionHookRunner.MaxDiscoveryMaxBytes + 1;
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                var diagnostic = Assert.Single(runner.Diagnostics);
                Assert.Equal("hook_discovery_bytes_clamped", diagnostic.Category);
                Assert.Contains(PostExtractionHookRunner.MaxDiscoveryMaxBytes.ToString(), diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                PostExtractionHookRunner.DiscoveryMaxBytesForTesting = originalMaxBytes;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void DiscoverDefaultMetadata_ReportsAcceptedHooksDirectoryOverride_3415()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-override-accepted");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
            try
            {
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);

                var snapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();

                Assert.Empty(snapshot.Hooks);
                Assert.Contains(
                    snapshot.Diagnostics,
                    diagnostic => diagnostic.AssemblyPath.EndsWith("hooks", StringComparison.Ordinal)
                                  && diagnostic.Category == "hook_directory_override_accepted"
                                  && diagnostic.Message.Contains("override accepted", StringComparison.Ordinal));
                var trustOverride = Assert.Single(snapshot.TrustOverrides);
                Assert.Equal("hook_directory_override", trustOverride.Kind);
                Assert.Equal(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, trustOverride.EnvironmentVariable);
                Assert.EndsWith("hooks", trustOverride.Value, StringComparison.Ordinal);
                Assert.EndsWith("hooks", trustOverride.Path!, StringComparison.Ordinal);
                Assert.Contains("hook assemblies execute", trustOverride.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, trustOverride.Value, StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, trustOverride.Path!, StringComparison.Ordinal);
                Assert.All(
                    snapshot.Diagnostics,
                    diagnostic => Assert.DoesNotContain(projectRoot, diagnostic.AssemblyPath, StringComparison.Ordinal));
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void DiscoverDefaultMetadata_RejectsMissingHooksDirectoryOverride_3415()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-override-missing");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
            try
            {
                var hooksDir = Path.Combine(projectRoot, "missing-hooks");
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);

                var snapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();

                Assert.Empty(snapshot.Hooks);
                var diagnostic = Assert.Single(snapshot.Diagnostics);
                Assert.EndsWith("missing-hooks", diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Equal("hook_directory_override_missing", diagnostic.Category);
                Assert.DoesNotContain(projectRoot, diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Contains("override rejected", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("does not exist", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void DiscoverDefaultMetadata_RejectsUnsafeHooksDirectoryMode_Issue4596()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-unsafe-mode-4596");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
            var hooksDir = Path.Combine(projectRoot, "hooks");
            try
            {
                Directory.CreateDirectory(hooksDir);
                File.SetUnixFileMode(
                    hooksDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.OtherWrite);
                env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);

                var snapshot = PostExtractionHookRunner.DiscoverDefaultMetadata();

                Assert.Empty(snapshot.Hooks);
                Assert.Empty(snapshot.TrustOverrides);
                var diagnostic = Assert.Single(snapshot.Diagnostics);
                Assert.Equal("extension_boundary_unsafe_permissions", diagnostic.Category);
                Assert.Contains("override rejected", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("group- or world-writable", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                File.SetUnixFileMode(
                    hooksDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Discover_CapsHookAssemblyCandidates()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-discovery-cap");
        lock (TestConsoleLock.Gate)
        {
            var originalLimit = PostExtractionHookRunner.DiscoveryLimitForTesting;
            try
            {
                PostExtractionHookRunner.DiscoveryLimitForTesting = () => 2;
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.WriteAllText(Path.Combine(hooksDir, "a.dll"), "not a real dll");
                File.WriteAllText(Path.Combine(hooksDir, "b.dll"), "not a real dll");
                File.WriteAllText(Path.Combine(hooksDir, "c.dll"), "not a real dll");

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                Assert.Empty(runner.Hooks);
                Assert.Equal(3, runner.Diagnostics.Count);
                Assert.Contains(
                    runner.Diagnostics,
                    diagnostic => diagnostic.AssemblyPath.EndsWith("hooks", StringComparison.Ordinal)
                                  && diagnostic.Category == "hook_candidate_limit_exceeded"
                                  && !diagnostic.AssemblyPath.Contains(projectRoot, StringComparison.Ordinal)
                                  && diagnostic.Message.Contains("candidate limit", StringComparison.Ordinal));
                Assert.Equal(
                    2,
                    runner.Diagnostics.Count(diagnostic => diagnostic.Category is "assembly_load_failed" or "dependency_resolution_failed"
                                                           && diagnostic.Message.StartsWith("Hook assembly load failed", StringComparison.Ordinal)));
            }
            finally
            {
                PostExtractionHookRunner.DiscoveryLimitForTesting = originalLimit;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Discover_SkipsOversizeHookAssemblyCandidate()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-size-cap");
        lock (TestConsoleLock.Gate)
        {
            var originalMaxBytes = PostExtractionHookRunner.DiscoveryMaxBytesForTesting;
            try
            {
                PostExtractionHookRunner.DiscoveryMaxBytesForTesting = () => 16;
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                var hookPath = Path.Combine(hooksDir, "oversize.dll");
                using (var stream = File.Create(hookPath))
                {
                    stream.SetLength(17);
                }

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                Assert.Empty(runner.Hooks);
                var diagnostic = Assert.Single(runner.Diagnostics);
                Assert.EndsWith("oversize.dll", diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Equal("hook_file_too_large", diagnostic.Category);
                Assert.DoesNotContain(projectRoot, diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Contains("too large", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("maximum 16", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                PostExtractionHookRunner.DiscoveryMaxBytesForTesting = originalMaxBytes;
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Discover_RejectsSymlinkHookAssemblyCandidate_Issue4133()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-symlink-4133");
        lock (TestConsoleLock.Gate)
        {
            var target = Path.Combine(projectRoot, "target.dll");
            var hooksDir = Path.Combine(projectRoot, "hooks");
            var link = Path.Combine(hooksDir, "link.dll");
            try
            {
                Directory.CreateDirectory(hooksDir);
                File.WriteAllText(target, "not a real dll");
                File.CreateSymbolicLink(link, target);

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                Assert.Empty(runner.Hooks);
                var diagnostic = Assert.Single(runner.Diagnostics);
                Assert.EndsWith("link.dll", diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Equal("hook_reparse_point", diagnostic.Category);
                Assert.DoesNotContain(projectRoot, diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Contains("symbolic links and reparse points", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(link))
                    File.Delete(link);
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Discover_RejectsSymlinkHooksDirectory_Issue4133()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-symlink-dir-4133");
        lock (TestConsoleLock.Gate)
        {
            var targetDir = Path.Combine(projectRoot, "target-hooks");
            var linkDir = Path.Combine(projectRoot, "hooks-link");
            try
            {
                Directory.CreateDirectory(targetDir);
                File.WriteAllText(Path.Combine(targetDir, "target.dll"), "not a real dll");
                Directory.CreateSymbolicLink(linkDir, targetDir);

                using var runner = PostExtractionHookRunner.Discover(linkDir);

                Assert.Empty(runner.Hooks);
                var diagnostic = Assert.Single(runner.Diagnostics);
                Assert.EndsWith("hooks-link", diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Equal("hook_directory_reparse_point", diagnostic.Category);
                Assert.DoesNotContain(projectRoot, diagnostic.AssemblyPath, StringComparison.Ordinal);
                Assert.Contains("symbolic links and reparse points", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                if (Directory.Exists(linkDir))
                    Directory.Delete(linkDir);
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void Callbacks_TruncateHookMaterializationAndReportDiagnostics_Issue3744()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-materialization-cap");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExpandingHookEnvironmentVariable);
            try
            {
                env.Set(ExpandingHookEnvironmentVariable, "1");
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                {
                    using var runner = PostExtractionHookRunner.Discover(
                        hooksDir,
                        maxSymbolCount: 2,
                        maxReferenceCount: 2);
                    var context = new FileContext(projectRoot, "src/App.cs", Path.Combine(projectRoot, "src", "App.cs"), "csharp");
                    var symbols = new List<SymbolRecord>();
                    var references = new List<ReferenceRecord>();

                    runner.OnSymbolsExtracted(context, symbols);
                    runner.OnReferencesExtracted(context, references);

                    Assert.True(symbols.Count <= 2);
                    Assert.True(references.Count <= 2);
                    Assert.Contains(
                        runner.Diagnostics,
                        diagnostic => diagnostic.Category == "hook_symbol_count_truncated"
                                      && diagnostic.Message.Contains("materialization budget", StringComparison.Ordinal));
                    Assert.Contains(
                        runner.Diagnostics,
                        diagnostic => diagnostic.Category == "hook_reference_count_truncated"
                                      && diagnostic.Message.Contains("materialization budget", StringComparison.Ordinal));
                }
                CollectUnloadedHookAssemblies();
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void Discover_SkipsAssembliesAboveTypeInspectionLimit_Issue3790()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-type-cap-3790");
        lock (TestConsoleLock.Gate)
        {
            var originalLimit = PostExtractionHookRunner.TypeInspectionLimitForTesting;
            try
            {
                PostExtractionHookRunner.TypeInspectionLimitForTesting = () => 1;
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

                using var runner = PostExtractionHookRunner.Discover(hooksDir);

                Assert.Empty(runner.Hooks);
                var diagnostic = Assert.Single(runner.Diagnostics);
                Assert.Equal("hook_type_limit_exceeded", diagnostic.Category);
                Assert.Contains("too many loadable types", diagnostic.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, diagnostic.AssemblyPath, StringComparison.Ordinal);
            }
            finally
            {
                PostExtractionHookRunner.TypeInspectionLimitForTesting = originalLimit;
                CollectUnloadedHookAssemblies();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [ProductionRuntimeFact]
    public void Discover_NoHookAssemblyLeavesNoParentLoadContext_Issue4600()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-no-hook-unload-3790");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(typeof(PostExtractionHookRunner).Assembly.Location, Path.Combine(hooksDir, "CodeIndex.dll"));

                using (var runner = PostExtractionHookRunner.Discover(hooksDir))
                {
                    Assert.Empty(runner.Hooks);
                    Assert.Empty(runner.Diagnostics);
                    Assert.Equal(0, runner.ParentLoadContextCountForTests);
                }
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    private static void CollectUnloadedHookAssemblies()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void AssertFileDoesNotAppear(string path, TimeSpan duration)
        => TestDeterminism.AssertConditionRemainsTrue(
            () => !File.Exists(path),
            "timed-out post-extraction hook to remain stopped after the callback returned",
            duration,
            pollInterval: TimeSpan.FromMilliseconds(25),
            getDiagnostics: () => $"path={path}");

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryTerminateProcess(int? processId)
    {
        if (!processId.HasValue)
            return;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException)
        {
            // The discovery worker normally removed the descendant already.
        }
    }
}

public sealed class AWaitingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        DelayAndSignalWhenRequested();
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        DelayAndSignalWhenRequested();
    }

    private static void DelayAndSignalWhenRequested()
    {
        var raw = Environment.GetEnvironmentVariable(PostExtractionHookTests.CancellableHookDelayEnvironmentVariable);
        if (!int.TryParse(raw, out var milliseconds) || milliseconds <= 0)
            return;

        Thread.Sleep(milliseconds);
        var completionPath = Environment.GetEnvironmentVariable(PostExtractionHookTests.CancellableHookCompletionPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(completionPath))
            File.WriteAllText(completionPath, "done");
    }
}

public sealed class SamplePostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        symbols.Add(new SymbolRecord
        {
            FileId = symbols.FirstOrDefault()?.FileId ?? 0,
            Kind = "domain_tag",
            Name = "AppDomainTag",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = $"domain tag for {context.Path}",
        });
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        references.Add(new ReferenceRecord
        {
            FileId = 10,
            SymbolName = "AppDomainTag",
            ReferenceKind = "domain_reference",
            Line = 1,
            Column = 1,
            Context = context.Path,
        });
    }
}

public sealed class ThrowingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
        => throw new InvalidOperationException("boom");

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
        => throw new InvalidOperationException("boom");
}

public sealed class ThrowingConstructorPostExtractionHook : IPostExtractionHook
{
    public ThrowingConstructorPostExtractionHook()
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookTests.ThrowingConstructorHookEnvironmentVariable) == "1")
            throw new InvalidOperationException("ctor boom");
    }

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class SlowConstructorPostExtractionHook : IPostExtractionHook
{
    public SlowConstructorPostExtractionHook()
    {
        var raw = Environment.GetEnvironmentVariable(PostExtractionHookTests.SlowConstructorHookDelayEnvironmentVariable);
        if (int.TryParse(raw, out var milliseconds) && milliseconds > 0)
            Thread.Sleep(milliseconds);
    }

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class StatefulPostExtractionHook : IPostExtractionHook
{
    private bool sawSymbols;

    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookTests.StatefulHookEnvironmentVariable) == "1")
            sawSymbols = true;
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        if (!sawSymbols || Environment.GetEnvironmentVariable(PostExtractionHookTests.StatefulHookEnvironmentVariable) != "1")
            return;

        references.Add(new ReferenceRecord
        {
            SymbolName = "StatefulHookSawSymbols",
            ReferenceKind = "domain_reference",
            Line = 1,
            Column = 1,
            Context = context.Path,
        });
    }
}

public sealed class SlowPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (!DelayWhenRequested())
            return;

        symbols.Add(new SymbolRecord
        {
            Kind = "domain_tag",
            Name = "SlowHookTag",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
        });
        SignalCompletionWhenRequested();
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        if (!DelayWhenRequested())
            return;

        references.Add(new ReferenceRecord
        {
            SymbolName = "SlowHookTag",
            ReferenceKind = "domain_reference",
            Line = 1,
            Column = 1,
            Context = context.Path,
        });
        SignalCompletionWhenRequested();
    }

    private static bool DelayWhenRequested()
    {
        var raw = Environment.GetEnvironmentVariable(PostExtractionHookTests.SlowHookDelayEnvironmentVariable);
        if (!int.TryParse(raw, out var milliseconds) || milliseconds <= 0)
            return false;

        Thread.Sleep(milliseconds);
        return true;
    }

    private static void SignalCompletionWhenRequested()
    {
        var completionPath = Environment.GetEnvironmentVariable(PostExtractionHookTests.SlowHookCompletionPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(completionPath))
            File.WriteAllText(completionPath, "done");
    }
}

public sealed class LoadContextReportingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(GetType().Assembly);
        if (loadContext is { IsCollectible: true } && !ReferenceEquals(loadContext, AssemblyLoadContext.Default))
        {
            symbols.Add(new SymbolRecord
            {
                Kind = "domain_tag",
                Name = "CollectibleHookLoadContext",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
            });
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
    }
}

public sealed class ExpandingPostExtractionHook : IPostExtractionHook
{
    public void OnSymbolsExtracted(FileContext context, IList<SymbolRecord> symbols)
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookTests.ExpandingHookEnvironmentVariable) != "1")
            return;

        for (var index = 0; index < 5; index++)
        {
            symbols.Add(new SymbolRecord
            {
                Kind = "domain_tag",
                Name = $"ExpandedHookSymbol{index}",
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
            });
        }
    }

    public void OnReferencesExtracted(FileContext context, IList<ReferenceRecord> references)
    {
        if (Environment.GetEnvironmentVariable(PostExtractionHookTests.ExpandingHookEnvironmentVariable) != "1")
            return;

        for (var index = 0; index < 5; index++)
        {
            references.Add(new ReferenceRecord
            {
                SymbolName = $"ExpandedHookSymbol{index}",
                ReferenceKind = "domain_reference",
                Line = index + 1,
                Column = 1,
                Context = context.Path,
            });
        }
    }
}
