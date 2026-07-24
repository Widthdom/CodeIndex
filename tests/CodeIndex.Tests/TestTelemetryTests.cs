using CodeIndex.TestTelemetry;
using System.Text.Json;
using System.Xml.Linq;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class TestTelemetryTests
{
    [Fact]
    public void Load_SummarizesTrxResultsAndSlowestTests()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);

            File.WriteAllText(Path.Combine(resultsDirectory, "test_results.trx"), """
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <Results>
                    <UnitTestResult testName="FastPass" outcome="Passed" duration="00:00:00.1000000" />
                    <UnitTestResult testName="SlowPass" outcome="Passed" duration="00:00:03.5000000" />
                    <UnitTestResult testName="BrokenTest" outcome="Failed" duration="00:00:01.2500000" />
                    <UnitTestResult testName="TimeoutTest" outcome="Timeout" duration="00:00:02.0000000" />
                    <UnitTestResult testName="AbortedTest" outcome="Aborted" duration="00:00:01.5000000" />
                    <UnitTestResult testName="SkippedTest" outcome="NotExecuted" />
                  </Results>
                </TestRun>
                """);

            var summary = TrxTelemetry.Load(resultsDirectory, top: 2);

            Assert.Equal(1, summary.TrxFileCount);
            Assert.Equal(6, summary.Total);
            Assert.Equal(2, summary.Passed);
            Assert.Equal(3, summary.Failed);
            Assert.Equal(1, summary.Skipped);
            Assert.Equal(["SlowPass", "TimeoutTest"], summary.Slowest.Select(result => result.TestName));
            Assert.Equal(["TimeoutTest", "AbortedTest"], summary.Failures.Select(result => result.TestName));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Render_IncludesFailureAndRuntimeTelemetry()
    {
        var summary = new TrxTelemetrySummary(
            ResultsDirectory: "TestResults",
            TrxFileCount: 1,
            Total: 2,
            Passed: 1,
            Failed: 1,
            Skipped: 0,
            Other: 0,
            Slowest: [new TrxTestResult("SlowTest", "Passed", TimeSpan.FromSeconds(2.25))],
            Failures: [new TrxTestResult("FailedTest", "Failed", TimeSpan.FromMilliseconds(500))],
            Warnings: []);

        var output = TrxTelemetryRenderer.Render(summary);

        Assert.Contains("TRX telemetry summary", output, StringComparison.Ordinal);
        Assert.Contains("Tests: 2; passed: 1; failed: 1; skipped: 0; other: 0", output, StringComparison.Ordinal);
        Assert.Contains("- FailedTest (Failed, 500ms)", output, StringComparison.Ordinal);
        Assert.Contains("- SlowTest (Passed, 2.250s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRetryFilter_BuildsDeterministicDistinctFullyQualifiedNameFilter()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter");
        try
        {
            var trxPath = Path.Combine(projectRoot, "first.trx");
            File.WriteAllText(
                trxPath,
                RetryTrx(
                [
                    new RetryTrxCase("zeta", "Sample.ZetaTests", "Fails", "Failed"),
                    new RetryTrxCase("alpha-2", "Sample.AlphaTests", "Theory", "Failed"),
                    new RetryTrxCase("alpha-1", "Sample.AlphaTests", "Theory", "Failed"),
                    new RetryTrxCase("passing", "Sample.OtherTests", "Passes", "Passed"),
                    new RetryTrxCase("skipped", "Sample.OtherTests", "Skips", "NotExecuted")
                ],
                runInfos: [new RetryRunInfo("Warning", "adapter diagnostic")]));

            var decision = TrxRetryFilter.Load(trxPath);

            Assert.True(decision.UseFocusedRetry);
            Assert.Equal(TrxRetryFilterReasons.Focused, decision.Reason);
            Assert.Equal(3, decision.FailedResultCount);
            Assert.Equal(2, decision.TestMethodCount);
            Assert.Equal(
                "FullyQualifiedName=Sample.AlphaTests.Theory|FullyQualifiedName=Sample.ZetaTests.Fails",
                decision.Filter);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_MissingTrxFallsBackToFullSuite()
    {
        var decision = TrxRetryFilter.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.trx"));

        Assert.False(decision.UseFocusedRetry);
        Assert.Null(decision.Filter);
        Assert.Equal(TrxRetryFilterReasons.TrxMissing, decision.Reason);
    }

    [Fact]
    public void LoadRetryFilter_MalformedOrDtdTrxFallsBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_xml");
        try
        {
            var malformedPath = Path.Combine(projectRoot, "malformed.trx");
            File.WriteAllText(malformedPath, "<TestRun><Results>");
            var dtdPath = Path.Combine(projectRoot, "dtd.trx");
            File.WriteAllText(dtdPath, "<!DOCTYPE TestRun [<!ELEMENT TestRun ANY>]><TestRun />");

            Assert.Equal(
                TrxRetryFilterReasons.InvalidXml,
                TrxRetryFilter.Load(malformedPath).Reason);
            Assert.Equal(
                TrxRetryFilterReasons.InvalidXml,
                TrxRetryFilter.Load(dtdPath).Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_AbortedOrIncompleteRunFallsBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_aborted");
        try
        {
            var trxPath = Path.Combine(projectRoot, "aborted.trx");
            File.WriteAllText(
                trxPath,
                RetryTrx(
                    [new RetryTrxCase("aborted", "Sample.AbortedTests", "Stops", "Aborted")],
                    summaryOutcome: "Aborted"));

            var decision = TrxRetryFilter.Load(trxPath);

            Assert.False(decision.UseFocusedRetry);
            Assert.Equal(TrxRetryFilterReasons.RunIncomplete, decision.Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_CorrelatedXunitFailureRunInfosUseFocusedRetry()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_xunit_failure");
        try
        {
            var scenarios = new[]
            {
                (
                    FileName: "fact.trx",
                    TestCase: new RetryTrxCase(
                        "fact",
                        "CodeIndex.Tests.TestDeterminismTests",
                        "WaitUntilAsync_PollsUntilConditionIsTrue",
                        "Failed"),
                    DisplayName: "CodeIndex.Tests.TestDeterminismTests.WaitUntilAsync_PollsUntilConditionIsTrue",
                    ExpectedFilter: "FullyQualifiedName=CodeIndex.Tests.TestDeterminismTests.WaitUntilAsync_PollsUntilConditionIsTrue"),
                (
                    FileName: "theory.trx",
                    TestCase: new RetryTrxCase(
                        "theory",
                        "Sample.TheoryTests",
                        "Theory",
                        "Failed",
                        DisplayName: "Sample.TheoryTests.Theory(value: \"quoted\")"),
                    DisplayName: "Sample.TheoryTests.Theory(value: \"quoted\")",
                    ExpectedFilter: "FullyQualifiedName=Sample.TheoryTests.Theory")
            };

            foreach (var scenario in scenarios)
            {
                var trxPath = Path.Combine(projectRoot, scenario.FileName);
                File.WriteAllText(
                    trxPath,
                    RetryTrx(
                        [scenario.TestCase],
                        runInfos: [new RetryRunInfo("Error", XunitFailureRunInfo(scenario.DisplayName))]));

                var decision = TrxRetryFilter.Load(trxPath);

                Assert.True(decision.UseFocusedRetry);
                Assert.Equal(TrxRetryFilterReasons.Focused, decision.Reason);
                Assert.Equal(scenario.ExpectedFilter, decision.Filter);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_RunLevelFailuresFallBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_run_info");
        try
        {
            var correlatedFailure = new RetryRunInfo(
                "Error",
                XunitFailureRunInfo("Sample.AdapterTests.Fails"));
            var scenarios = new (string FileName, IReadOnlyCollection<RetryRunInfo> RunInfos)[]
            {
                ("generic-error.trx", [new RetryRunInfo("Error", "adapter diagnostic")]),
                ("uncorrelated-xunit.trx", [new RetryRunInfo("Error", XunitFailureRunInfo("Sample.OtherTests.Fails"))]),
                ("case-mismatched-xunit.trx", [new RetryRunInfo("Error", XunitFailureRunInfo("sample.AdapterTests.Fails"))]),
                ("malformed-xunit.trx", [new RetryRunInfo("Error", "[xUnit.net 00:00:01.23]    Sample.AdapterTests.Fails [FAIL]")]),
                ("multiple-text.trx", [new RetryRunInfo("Error", correlatedFailure.Text, IncludeSecondText: true)]),
                ("nested-text.trx", [new RetryRunInfo("Error", correlatedFailure.Text, NestText: true)]),
                ("failed.trx", [new RetryRunInfo("Failed", "adapter diagnostic")]),
                ("aborted.trx", [new RetryRunInfo("Aborted", "adapter diagnostic")]),
                ("mixed.trx", [correlatedFailure, new RetryRunInfo("Error", "adapter diagnostic")])
            };

            foreach (var scenario in scenarios)
            {
                var trxPath = Path.Combine(projectRoot, scenario.FileName);
                File.WriteAllText(
                    trxPath,
                    RetryTrx(
                        [new RetryTrxCase("failed", "Sample.AdapterTests", "Fails", "Failed")],
                        runInfos: scenario.RunInfos));

                var decision = TrxRetryFilter.Load(trxPath);

                Assert.False(decision.UseFocusedRetry);
                Assert.Equal(TrxRetryFilterReasons.RunIncomplete, decision.Reason);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_InconsistentCountersFallBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_counters");
        try
        {
            var trxPath = Path.Combine(projectRoot, "inconsistent.trx");
            File.WriteAllText(
                trxPath,
                RetryTrx(
                    [new RetryTrxCase("failed", "Sample.CounterTests", "Fails", "Failed")],
                    failedCounter: 2));

            var decision = TrxRetryFilter.Load(trxPath);

            Assert.False(decision.UseFocusedRetry);
            Assert.Equal(TrxRetryFilterReasons.TrxInconsistent, decision.Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_MissingOrUnsafeFailureIdentityFallsBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_identity");
        try
        {
            var missingPath = Path.Combine(projectRoot, "missing-definition.trx");
            File.WriteAllText(
                missingPath,
                RetryTrx(
                    [new RetryTrxCase("missing", "Sample.IdentityTests", "Fails", "Failed", IncludeDefinition: false)]));
            var unsafePath = Path.Combine(projectRoot, "unsafe-name.trx");
            File.WriteAllText(
                unsafePath,
                RetryTrx(
                    [new RetryTrxCase("unsafe", "Sample.IdentityTests", "Fails|Everything", "Failed")]));

            Assert.Equal(
                TrxRetryFilterReasons.FailureIdentityUnavailable,
                TrxRetryFilter.Load(missingPath).Reason);
            Assert.Equal(
                TrxRetryFilterReasons.FailureNameUnsafe,
                TrxRetryFilter.Load(unsafePath).Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_TooManyFailuresFallBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_count");
        try
        {
            var cases = Enumerable.Range(0, TrxRetryFilter.MaxFailedResults + 1)
                .Select(index => new RetryTrxCase($"failure-{index}", "Sample.ManyTests", $"Fails{index}", "Failed"))
                .ToArray();
            var trxPath = Path.Combine(projectRoot, "too-many.trx");
            File.WriteAllText(trxPath, RetryTrx(cases));

            var decision = TrxRetryFilter.Load(trxPath);

            Assert.False(decision.UseFocusedRetry);
            Assert.Equal(TrxRetryFilter.MaxFailedResults + 1, decision.FailedResultCount);
            Assert.Equal(TrxRetryFilterReasons.FailureLimitExceeded, decision.Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadRetryFilter_TooLongFilterFallsBackToFullSuite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_length");
        try
        {
            var longClassName = $"Sample.{new string('A', TrxRetryFilter.MaxFilterLength)}";
            var trxPath = Path.Combine(projectRoot, "too-long.trx");
            File.WriteAllText(
                trxPath,
                RetryTrx([new RetryTrxCase("long", longClassName, "Fails", "Failed")]));

            var decision = TrxRetryFilter.Load(trxPath);

            Assert.False(decision.UseFocusedRetry);
            Assert.Equal(TrxRetryFilterReasons.FilterLengthExceeded, decision.Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RenderRetryFilter_ProducesSingleMachineReadableDecision()
    {
        var decision = new TrxRetryFilterDecision(
            UseFocusedRetry: true,
            Filter: "FullyQualifiedName=Sample.Tests.Fails",
            FailedResultCount: 2,
            TestMethodCount: 1,
            Reason: TrxRetryFilterReasons.Focused);

        using var document = JsonDocument.Parse(TrxRetryFilterRenderer.Render(decision));

        Assert.True(document.RootElement.GetProperty("useFocusedRetry").GetBoolean());
        Assert.Equal("FullyQualifiedName=Sample.Tests.Fails", document.RootElement.GetProperty("filter").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("failedResultCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("testMethodCount").GetInt32());
        Assert.Equal("focused", document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void RetryFilterCommand_EmitsDecisionForPowerShellCaller()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_retry_filter_command");
        try
        {
            var trxPath = Path.Combine(projectRoot, "first.trx");
            File.WriteAllText(
                trxPath,
                RetryTrx([new RetryTrxCase("failed", "Sample.CommandTests", "Fails", "Failed")]));

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                CodeIndex.TestTelemetry.Program.Main(["retry-filter", "--trx-file", trxPath]));
            using var document = JsonDocument.Parse(stdout);

            Assert.Equal(0, exitCode);
            Assert.Empty(stderr);
            Assert.True(document.RootElement.GetProperty("useFocusedRetry").GetBoolean());
            Assert.Equal(
                "FullyQualifiedName=Sample.CommandTests.Fails",
                document.RootElement.GetProperty("filter").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_MissingDirectoryReturnsWarningInsteadOfFailingCiSummary()
    {
        var summary = TrxTelemetry.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), top: 10);

        Assert.Equal(0, summary.Total);
        Assert.Single(summary.Warnings);
        Assert.Contains("Results directory not found", summary.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsTopValuesAboveTelemetryCap()
    {
        var exception = Assert.Throws<TelemetryException>(() =>
            TrxTelemetry.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), TrxTelemetry.MaxTop + 1));

        Assert.Contains($"between 1 and {TrxTelemetry.MaxTop}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CapsTrxDiscovery()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_cap");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);

            for (var i = 0; i < TrxTelemetry.MaxTrxFiles + 1; i++)
            {
                File.WriteAllText(Path.Combine(resultsDirectory, $"results-{i:D4}.trx"), MinimalTrx($"Test{i:D4}"));
            }

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(TrxTelemetry.MaxTrxFiles, summary.TrxFileCount);
            Assert.Equal(TrxTelemetry.MaxTrxFiles, summary.Total);
            Assert.Contains(summary.Warnings, warning =>
                warning.Contains("TRX file cap reached", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_CapsTrxDirectoryTraversal()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_directory_cap");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);

            for (var i = 0; i < TrxTelemetry.MaxTraversalDirectories + 1; i++)
            {
                Directory.CreateDirectory(Path.Combine(resultsDirectory, $"dir-{i:D4}"));
            }

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(0, summary.TrxFileCount);
            Assert.Equal(0, summary.Total);
            Assert.Contains(summary.Warnings, warning =>
                warning.Contains("directory traversal cap", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_SkipsTrxFilesAboveSizeCap()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_size");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            var nestedDirectory = Path.Combine(resultsDirectory, "nested");
            Directory.CreateDirectory(nestedDirectory);
            var largeTrx = Path.Combine(nestedDirectory, "too-large.trx");
            using (var stream = File.Create(largeTrx))
            {
                stream.SetLength(TrxTelemetry.MaxTrxFileBytes + 1);
            }

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(1, summary.TrxFileCount);
            Assert.Equal(0, summary.Total);
            var warning = Assert.Single(summary.Warnings);
            Assert.Contains("byte cap", warning, StringComparison.Ordinal);
            Assert.Contains("nested/too-large.trx", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot, warning, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_SkipsUnixFifoTrxEntries()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_fifo");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);
            var fifoPath = Path.Combine(resultsDirectory, "pipe.trx");
            if (Mkfifo(fifoPath, Convert.ToUInt32("600", 8)) != 0)
                throw new IOException($"mkfifo failed with errno {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}.");

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(0, summary.TrxFileCount);
            Assert.Equal(0, summary.Total);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_RejectsTrxDtds()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_dtd");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);
            File.WriteAllText(Path.Combine(resultsDirectory, "with-dtd.trx"), """
                <!DOCTYPE TestRun [
                  <!ELEMENT TestRun ANY>
                ]>
                <TestRun>
                  <Results>
                    <UnitTestResult testName="Unsafe" outcome="Passed" />
                  </Results>
                </TestRun>
                """);

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(0, summary.Total);
            Assert.Contains(summary.Warnings, warning =>
                string.Equals(warning, "Could not parse with-dtd.trx: invalid_xml", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_DiscardsPartialResultsFromMalformedTrx()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_partial_xml");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);
            File.WriteAllText(Path.Combine(resultsDirectory, "partial.trx"), """
                <TestRun>
                  <Results>
                    <UnitTestResult testName="ShouldNotCount" outcome="Passed" duration="00:00:09.0000000" />
                    <UnitTestResult testName="Broken" outcome="Failed">
                  </Results>
                </TestRun>
                """);

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(0, summary.Total);
            Assert.Equal(0, summary.Passed);
            Assert.Empty(summary.Slowest);
            Assert.Contains(summary.Warnings, warning =>
                string.Equals(warning, "Could not parse partial.trx: invalid_xml", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Load_SanitizesInvalidXmlWarnings()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_xml_warning");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            var nestedDirectory = Path.Combine(resultsDirectory, "nested");
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(Path.Combine(nestedDirectory, "broken.trx"), """
                <TestRun>
                  <Results>
                    <UnitTestResult testName="Broken" outcome="Passed">
                  </Results>
                </TestRun>
                """);

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            var warning = Assert.Single(summary.Warnings);
            Assert.Equal("Could not parse nested/broken.trx: invalid_xml", warning);
            Assert.DoesNotContain(projectRoot, warning, StringComparison.Ordinal);
            Assert.DoesNotContain("Name cannot begin", warning, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("position", warning, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadSkips_ReportsOnlyXunitSkipAttributesWithGovernanceMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_skip_telemetry");
        try
        {
            var testsDirectory = Path.Combine(projectRoot, "tests");
            Directory.CreateDirectory(testsDirectory);
            File.WriteAllText(Path.Combine(testsDirectory, "ParserTests.cs"), """
                namespace Sample;

                public sealed class ParserTests
                {
                    // [Fact(Skip = "comment-only")]
                    private const string Fixture = "[Theory(Skip = \"string-only\")]";

                    [Trait("Scenario", "Parser")]
                    [Trait("Area", "Search")]
                    [Fact(Skip = "owner: @Widthdom; expires: 2026-12-31; blocked by #4143")]
                    public void SkipsWithGovernance()
                    {
                    }

                    [Theory(Skip = SkipReasons.Platform)]
                    [InlineData(1)]
                    public async Task UsesExpressionSkip()
                    {
                        await Task.CompletedTask;
                    }

                    [Fact]
                    public void ActiveTest()
                    {
                    }
                }
                """);

            var summary = SkipTelemetry.Load(testsDirectory, top: 10);

            Assert.Equal(1, summary.CSharpFileCount);
            Assert.Equal(2, summary.SkippedTests);
            Assert.Equal(1, summary.WithOwner);
            Assert.Equal(1, summary.WithExpiry);
            Assert.Equal(1, summary.WithScenario);
            Assert.Empty(summary.Warnings);

            var governed = Assert.Single(summary.Entries, entry => entry.TestName == "SkipsWithGovernance");
            Assert.Equal("ParserTests.cs", governed.FilePath);
            Assert.Equal("Search", governed.Area);
            Assert.Equal("Parser", governed.Scenario);
            Assert.Equal("owner: @Widthdom; expires: 2026-12-31; blocked by #4143", governed.Reason);
            Assert.True(governed.HasOwner);
            Assert.True(governed.HasExpiry);
            Assert.True(governed.HasScenario);

            var expression = Assert.Single(summary.Entries, entry => entry.TestName == "UsesExpressionSkip");
            Assert.Equal("Parser", expression.Area);
            Assert.Equal("Uncategorized", expression.Scenario);
            Assert.Equal("SkipReasons.Platform", expression.Reason);
            Assert.False(expression.HasOwner);
            Assert.False(expression.HasExpiry);
            Assert.False(expression.HasScenario);

            Assert.Contains(summary.ByArea, count => count.Name == "Search" && count.Count == 1);
            Assert.Contains(summary.ByArea, count => count.Name == "Parser" && count.Count == 1);
            Assert.Contains(summary.ByScenario, count => count.Name == "Parser" && count.Count == 1);
            Assert.Contains(summary.ByScenario, count => count.Name == "Uncategorized" && count.Count == 1);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RenderSkips_IncludesSkipGovernanceBreakdown()
    {
        var summary = new SkipTelemetrySummary(
            TestsDirectory: "tests/CodeIndex.Tests",
            CSharpFileCount: 1,
            SkippedTests: 1,
            WithOwner: 1,
            WithExpiry: 1,
            WithScenario: 1,
            DisplayLimit: 10,
            Entries:
            [
                new SkipTelemetryEntry(
                    "ParserTests.cs",
                    8,
                    "SkipsWithGovernance",
                    "Search",
                    "Parser",
                    "owner: @Widthdom; expires: 2026-12-31",
                    HasOwner: true,
                    HasExpiry: true,
                    HasScenario: true)
            ],
            ByArea: [new SkipTelemetryCount("Search", 1)],
            ByScenario: [new SkipTelemetryCount("Parser", 1)],
            ByReason: [new SkipTelemetryCount("owner: @Widthdom; expires: 2026-12-31", 1)],
            Warnings: []);

        var output = SkipTelemetryRenderer.Render(summary);

        Assert.Contains("Test skip governance summary", output, StringComparison.Ordinal);
        Assert.Contains("Skipped test annotations: 1", output, StringComparison.Ordinal);
        Assert.Contains("Governance tokens: owner: 1; expires: 1; scenario/category trait: 1", output, StringComparison.Ordinal);
        Assert.Contains("By area:", output, StringComparison.Ordinal);
        Assert.Contains("- Search: 1", output, StringComparison.Ordinal);
        Assert.Contains("[area: Search; scenario: Parser; owner: yes; expires: yes]", output, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadSkips_MissingDirectoryReturnsWarning()
    {
        var summary = SkipTelemetry.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), top: 10);

        Assert.Equal(0, summary.SkippedTests);
        var warning = Assert.Single(summary.Warnings);
        Assert.Contains("Tests directory not found", warning, StringComparison.Ordinal);
    }

    private static string MinimalTrx(string testName) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="{{testName}}" outcome="Passed" duration="00:00:00.1000000" />
          </Results>
        </TestRun>
        """;

    private static string RetryTrx(
        IReadOnlyCollection<RetryTrxCase> testCases,
        string summaryOutcome = "Failed",
        int? failedCounter = null,
        IReadOnlyCollection<RetryRunInfo>? runInfos = null)
    {
        XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var executed = testCases.Count(testCase =>
            !string.Equals(testCase.Outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(testCase.Outcome, "Skipped", StringComparison.OrdinalIgnoreCase));

        var document = new XDocument(
            new XElement(
                trx + "TestRun",
                new XElement(
                    trx + "Results",
                    testCases.Select((testCase, index) =>
                        new XElement(
                            trx + "UnitTestResult",
                            new XAttribute("executionId", $"execution-{index}"),
                            new XAttribute("testId", testCase.Id),
                            new XAttribute(
                                "testName",
                                testCase.DisplayName ?? $"{testCase.ClassName}.{testCase.MethodName}"),
                            new XAttribute("outcome", testCase.Outcome)))),
                new XElement(
                    trx + "TestDefinitions",
                    testCases.Where(testCase => testCase.IncludeDefinition).Select((testCase, index) =>
                        new XElement(
                            trx + "UnitTest",
                            new XAttribute("id", testCase.Id),
                            new XAttribute("name", $"{testCase.ClassName}.{testCase.MethodName}"),
                            new XElement(trx + "Execution", new XAttribute("id", $"execution-{index}")),
                            new XElement(
                                trx + "TestMethod",
                                new XAttribute("className", testCase.ClassName),
                                new XAttribute("name", testCase.MethodName))))),
                new XElement(
                    trx + "ResultSummary",
                    new XAttribute("outcome", summaryOutcome),
                    new XElement(
                        trx + "Counters",
                        new XAttribute("total", testCases.Count),
                        new XAttribute("executed", executed),
                        new XAttribute(
                            "passed",
                            testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "Passed", StringComparison.OrdinalIgnoreCase))),
                        new XAttribute(
                            "failed",
                            failedCounter ?? testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "Failed", StringComparison.OrdinalIgnoreCase))),
                        new XAttribute(
                            "error",
                            testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "Error", StringComparison.OrdinalIgnoreCase))),
                        new XAttribute(
                            "timeout",
                            testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "Timeout", StringComparison.OrdinalIgnoreCase))),
                        new XAttribute(
                            "aborted",
                            testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "Aborted", StringComparison.OrdinalIgnoreCase))),
                        new XAttribute("inconclusive", 0),
                        new XAttribute("passedButRunAborted", 0),
                        new XAttribute(
                            "notRunnable",
                            testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "NotRunnable", StringComparison.OrdinalIgnoreCase))),
                        // The xUnit VSTest adapter currently reports skipped UnitTestResult entries
                        // while leaving this TRX counter at zero.
                        new XAttribute("notExecuted", 0),
                        new XAttribute(
                            "disconnected",
                            testCases.Count(testCase =>
                                string.Equals(testCase.Outcome, "Disconnected", StringComparison.OrdinalIgnoreCase))),
                        new XAttribute("warning", 0),
                        new XAttribute("completed", 0),
                        new XAttribute("inProgress", 0),
                        new XAttribute("pending", 0)),
                    runInfos is null
                        ? null
                        : new XElement(
                            trx + "RunInfos",
                            runInfos.Select(runInfo =>
                                new XElement(
                                    trx + "RunInfo",
                                    new XAttribute("outcome", runInfo.Outcome),
                                    new XElement(
                                        trx + "Text",
                                        runInfo.NestText
                                            ? new XElement(trx + "Detail", runInfo.Text)
                                            : runInfo.Text),
                                    runInfo.IncludeSecondText
                                        ? new XElement(trx + "Text", runInfo.Text)
                                        : null))))));

        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string XunitFailureRunInfo(string displayName) =>
        $"[xUnit.net 00:00:01.23]     {displayName} [FAIL]";

    private sealed record RetryTrxCase(
        string Id,
        string ClassName,
        string MethodName,
        string Outcome,
        bool IncludeDefinition = true,
        string? DisplayName = null);

    private sealed record RetryRunInfo(
        string Outcome,
        string Text,
        bool IncludeSecondText = false,
        bool NestText = false);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string path, uint mode);
}
