using System.Reflection;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class PostExtractionHookTests
{
    internal const string SlowHookDelayEnvironmentVariable = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_MS";
    internal const string SlowHookCompletionPathEnvironmentVariable = "CDIDX_TEST_SLOW_POST_EXTRACTION_HOOK_DONE_PATH";

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
    public void CallbackBudgetExceeded_AddsDiagnosticAndSkipsTimedOutMutation()
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
                env.Set(SlowHookDelayEnvironmentVariable, "200");
                PostExtractionHookRunner.CallbackBudgetForTesting = () => TimeSpan.FromMilliseconds(50);
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
                    WaitForSlowHookCompletion(completionPath);

                    Assert.DoesNotContain(symbols, symbol => symbol.Name == "SlowHookTag");
                    var diagnostic = Assert.Single(
                        runner.Diagnostics,
                        item => item.TypeName == typeof(SlowPostExtractionHook).FullName
                                && item.Callback == nameof(IPostExtractionHook.OnSymbolsExtracted));
                    Assert.Contains("exceeded", diagnostic.Message, StringComparison.Ordinal);
                    // Task.Wait can time out at the budget boundary before ElapsedMilliseconds
                    // rounds up to the full budget on some CI hosts.
                    Assert.True(diagnostic.DurationMs > 0);
                    Assert.Equal(50, (long)Math.Round(runner.CallbackBudget.TotalMilliseconds, MidpointRounding.AwayFromZero));
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

    private static void WaitForSlowHookCompletion(string completionPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!File.Exists(completionPath))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the slow post-extraction hook to finish.");

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
