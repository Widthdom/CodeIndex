using CodeIndex.TestTelemetry;

namespace CodeIndex.Tests;

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

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string path, uint mode);
}
