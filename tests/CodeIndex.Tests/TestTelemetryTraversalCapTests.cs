using CodeIndex.TestTelemetry;

namespace CodeIndex.Tests;

public sealed class TestTelemetryTraversalCapTests
{
    [Fact]
    public void Load_CapsTrxEntryTraversal_Issue4180()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trx_telemetry_entry_cap");
        try
        {
            var resultsDirectory = Path.Combine(projectRoot, "TestResults");
            Directory.CreateDirectory(resultsDirectory);

            for (var i = 0; i < TrxTelemetry.MaxTraversalEntries + 1; i++)
            {
                File.WriteAllText(Path.Combine(resultsDirectory, $"ignored-{i:D4}.txt"), "not trx");
            }

            var summary = TrxTelemetry.Load(resultsDirectory, top: 1);

            Assert.Equal(0, summary.TrxFileCount);
            Assert.Equal(0, summary.Total);
            Assert.Contains(summary.Warnings, warning =>
                warning.Contains("entry traversal cap", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
