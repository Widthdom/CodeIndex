using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Tests;

public class ExtractorPluginRegistryTests
{
    [Fact]
    public void LoadPatternConfigs_BoundsDiagnosticsAndCountsSkippedFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_diagnostics");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var patternsDir = Path.Combine(projectRoot, ".cdidx", "patterns");
                Directory.CreateDirectory(patternsDir);
                for (var i = 0; i < 25; i++)
                {
                    File.WriteAllText(
                        Path.Combine(patternsDir, $"broken-{i:D2}.yaml"),
                        "language: \"broken\"\npatterns:\n  - kind: \"class\"\n    regex: \"(?<name>\"\n");
                }

                ExtractorPluginRegistry.LoadPatternConfigsForPath(Path.Combine(projectRoot, "sample.broken"));
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(0, status.PatternConfigCount);
                Assert.Equal(25, status.SkippedFileCount);
                Assert.Equal(25, status.DiagnosticCount);
                Assert.Equal(20, status.DiagnosticLimit);
                Assert.True(status.DiagnosticsTruncated);
                Assert.NotNull(status.Diagnostics);
                Assert.Equal(20, status.Diagnostics.Count);
                Assert.All(status.Diagnostics, diagnostic =>
                {
                    Assert.Equal("pattern", diagnostic.Kind);
                    Assert.Equal("error", diagnostic.Severity);
                    Assert.EndsWith(".yaml", diagnostic.Path);
                });
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }
}
