using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
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
    private const string TimedOutHookDelayMilliseconds = "250";
    private static readonly TimeSpan TimedOutHookLeakObservationWindow = TimeSpan.FromMilliseconds(600);

    [Fact]
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

    [Fact]
    public void Discover_LoadsHookAssemblyInCollectibleContext_3413()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-collectible-load");
        try
        {
            var hooksDir = Path.Combine(projectRoot, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(hooksDir, "CodeIndex.Tests.dll"));

            AssertHookAssemblyLoadsInCollectibleContext(hooksDir);
        }
        finally
        {
            CollectUnloadedHookAssemblies();
            TestProjectHelper.DeleteDirectory(projectRoot);
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void CallbackBudgetExceeded_KillsSlowConstructorAfterLargeRequestIsSent()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-slow-ctor");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(SlowConstructorHookDelayEnvironmentVariable);
            var originalBudget = PostExtractionHookRunner.CallbackBudgetForTesting;
            try
            {
                env.Set(SlowConstructorHookDelayEnvironmentVariable, "200");
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public void Discover_UnloadsAssemblyWithoutRetainedHooks_Issue3790()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("post-extraction-hook-no-hook-unload-3790");
        WeakReference? weakLoadContext;
        lock (TestConsoleLock.Gate)
        {
            try
            {
                PostExtractionHookRunner.LastUnretainedLoadContextForTesting = null;
                var hooksDir = Path.Combine(projectRoot, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(typeof(PostExtractionHookRunner).Assembly.Location, Path.Combine(hooksDir, "CodeIndex.dll"));

                using (var runner = PostExtractionHookRunner.Discover(hooksDir))
                {
                    Assert.Empty(runner.Hooks);
                    Assert.Empty(runner.Diagnostics);
                    weakLoadContext = PostExtractionHookRunner.LastUnretainedLoadContextForTesting;
                    Assert.NotNull(weakLoadContext);
                }

                CollectUnloadedHookAssemblies();
                Assert.False(weakLoadContext!.IsAlive);
            }
            finally
            {
                PostExtractionHookRunner.LastUnretainedLoadContextForTesting = null;
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

    private static void AssertHookAssemblyLoadsInCollectibleContext(string hooksDir)
    {
        using var runner = PostExtractionHookRunner.Discover(hooksDir);

        var loadContext = Assert.Single(
            runner.LoadContextsForTests
                .Where(context => context != null)
                .Distinct());
        Assert.True(loadContext!.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, loadContext);
        Assert.Equal("collectible_unloaded_on_runner_dispose", PostExtractionHookRunner.HookLoadContextLifecycle);
    }

    private static void AssertFileDoesNotAppear(string path, TimeSpan duration)
    {
        var deadline = DateTimeOffset.UtcNow.Add(duration);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
                throw new InvalidOperationException("The timed-out post-extraction hook continued running after the callback returned.");

            Thread.Sleep(25);
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
