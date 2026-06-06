using System.Reflection;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class PostExtractionHookTests
{
    internal const string SlowHookDelayEnvironmentVariable = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_MS";
    internal const string SlowHookCompletionPathEnvironmentVariable = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_DONE_PATH";
    internal const string SlowConstructorHookDelayEnvironmentVariable = "CDIDX_TEST_SLOW_CTOR_POST_EXTRACTION_HOOK_MS";
    internal const string StatefulHookEnvironmentVariable = "CDIDX_TEST_STATEFUL_POST_EXTRACTION_HOOK";
    internal const string ThrowingConstructorHookEnvironmentVariable = "CDIDX_TEST_THROWING_CTOR_POST_EXTRACTION_HOOK";

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
                env.Set(SlowHookDelayEnvironmentVariable, "500");
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
                    AssertFileDoesNotAppear(completionPath, TimeSpan.FromMilliseconds(1000));

                    Assert.DoesNotContain(symbols, symbol => symbol.Name == "SlowHookTag");
                    var diagnostic = Assert.Single(
                        runner.Diagnostics,
                        item => item.TypeName == typeof(SlowPostExtractionHook).FullName
                                && item.Callback == nameof(IPostExtractionHook.OnSymbolsExtracted));
                    Assert.True(
                        diagnostic.Message.Contains("exceeded", StringComparison.Ordinal),
                        diagnostic.Message);
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
                Assert.Equal(int.MaxValue, (long)Math.Round(capped.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero));
            }
            finally
            {
                PostExtractionHookRunner.CallbackBudgetForTesting = originalBudget;
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
                                  && !diagnostic.AssemblyPath.Contains(projectRoot, StringComparison.Ordinal)
                                  && diagnostic.Message.Contains("candidate limit", StringComparison.Ordinal));
                Assert.Equal(
                    2,
                    runner.Diagnostics.Count(diagnostic => diagnostic.Message.StartsWith("Failed to load hook assembly", StringComparison.Ordinal)));
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

    private static void CollectUnloadedHookAssemblies()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
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
