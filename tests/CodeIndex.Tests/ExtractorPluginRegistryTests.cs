using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Tests;

public class ExtractorPluginRegistryTests
{
    [Fact]
    public void EnumeratePluginAssemblyPaths_CapsCandidatesPerDirectory()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_plugin_cap");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginDir = Path.Combine(projectRoot, "plugins");
                Directory.CreateDirectory(pluginDir);
                for (var i = 0; i < ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory + 2; i++)
                    File.WriteAllText(Path.Combine(pluginDir, $"plugin-{i:D3}.dll"), "not a real dll");

                var paths = ExtractorPluginRegistry.EnumeratePluginAssemblyPathsForTests([pluginDir]);
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory, paths.Count);
                Assert.Equal(1, status.DiagnosticCount);
                var diagnostic = Assert.Single(status.Diagnostics!);
                Assert.Equal("plugin_directory", diagnostic.Kind);
                Assert.Equal("skipped", diagnostic.Severity);
                Assert.Contains("maximum", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("per directory", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void EnumeratePluginAssemblyPaths_CapsTotalCandidates()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_plugin_total_cap");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginDirs = new[]
                {
                    Path.Combine(projectRoot, "plugins-a"),
                    Path.Combine(projectRoot, "plugins-b"),
                    Path.Combine(projectRoot, "plugins-c"),
                };
                foreach (var pluginDir in pluginDirs)
                    Directory.CreateDirectory(pluginDir);
                for (var i = 0; i < ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory; i++)
                {
                    File.WriteAllText(Path.Combine(pluginDirs[0], $"plugin-a-{i:D3}.dll"), "not a real dll");
                    File.WriteAllText(Path.Combine(pluginDirs[1], $"plugin-b-{i:D3}.dll"), "not a real dll");
                }
                File.WriteAllText(Path.Combine(pluginDirs[2], "plugin-c-000.dll"), "not a real dll");

                var paths = ExtractorPluginRegistry.EnumeratePluginAssemblyPathsForTests(pluginDirs);
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(ExtractorPluginRegistry.MaxPluginAssemblyCandidatesTotal, paths.Count);
                Assert.Equal(1, status.DiagnosticCount);
                var diagnostic = Assert.Single(status.Diagnostics!);
                Assert.Equal("plugin_directory", diagnostic.Kind);
                Assert.Equal("skipped", diagnostic.Severity);
                Assert.Contains("maximum", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("total", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPluginAssemblies_RetainsCapDiagnosticWhenCandidatesAlsoFailToLoad()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_plugin_cap_visible");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginDir = Path.Combine(projectRoot, "plugins");
                Directory.CreateDirectory(pluginDir);
                for (var i = 0; i < ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory + 2; i++)
                    File.WriteAllText(Path.Combine(pluginDir, $"plugin-{i:D3}.dll"), "not a real dll");

                ExtractorPluginRegistry.LoadPluginAssembliesForTests([pluginDir]);
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory, status.SkippedFileCount);
                Assert.Equal(ExtractorPluginRegistry.MaxPluginAssemblyCandidatesPerDirectory + 1, status.DiagnosticCount);
                Assert.True(status.DiagnosticsTruncated);
                Assert.Contains(
                    status.Diagnostics!,
                    diagnostic => diagnostic.Kind == "plugin_directory"
                                  && diagnostic.Message.Contains("per directory", StringComparison.Ordinal));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPlugin_SkipsOversizeAssemblyCandidate()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_plugin_size_cap");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginPath = Path.Combine(projectRoot, "oversize.dll");
                using (var stream = File.Create(pluginPath))
                {
                    stream.SetLength(ExtractorPluginRegistry.MaxPluginAssemblyBytes + 1);
                }

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(0, status.PluginAssemblyCount);
                Assert.Equal(1, status.SkippedFileCount);
                Assert.Equal(1, status.DiagnosticCount);
                var diagnostic = Assert.Single(status.Diagnostics!);
                Assert.Equal("plugin", diagnostic.Kind);
                Assert.Equal("skipped", diagnostic.Severity);
                Assert.Contains("too large", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains(ExtractorPluginRegistry.MaxPluginAssemblyBytes.ToString(), diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

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
