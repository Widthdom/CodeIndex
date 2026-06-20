using System.Reflection;
using System.Runtime.Loader;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

[assembly: CdidxPlugin(ExtractorPluginRegistry.CurrentApiVersion, ExtractorPluginRegistry.CurrentApiVersion)]

namespace CodeIndex.Tests;

public class ExtractorPluginRegistryTests
{
    internal const string ThrowingPluginConstructorEnvironmentVariable = "CDIDX_TEST_THROWING_PLUGIN_CTOR";

    [Fact]
    public void GetAcceptedTrustOverrides_ReportsWorkspacePluginTrust_3735()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_trust_override_3735");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            try
            {
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "yes");

                var trustOverride = Assert.Single(ExtractorPluginRegistry.GetAcceptedTrustOverrides(projectRoot));

                Assert.Equal("workspace_plugin_directory", trustOverride.Kind);
                Assert.Equal(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, trustOverride.EnvironmentVariable);
                Assert.Equal("yes", trustOverride.Value);
                Assert.EndsWith(".cdidx/plugins", trustOverride.Path!, StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, trustOverride.Path!, StringComparison.Ordinal);
                Assert.Contains("workspace plugin", trustOverride.Message, StringComparison.Ordinal);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void GetAcceptedTrustOverrides_IgnoresRejectedWorkspacePluginTrust_3735()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_trust_override_rejected_3735");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            try
            {
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "0");

                Assert.Empty(ExtractorPluginRegistry.GetAcceptedTrustOverrides(projectRoot));
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

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
                Assert.Equal("plugin_candidate_limit_exceeded", diagnostic.Category);
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
                Assert.Equal("plugin_candidate_limit_exceeded", diagnostic.Category);
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
                Assert.Equal("plugin_file_too_large", diagnostic.Category);
                Assert.Equal("oversize.dll", diagnostic.Path);
                Assert.DoesNotContain(projectRoot, diagnostic.Path, StringComparison.Ordinal);
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
    public void LoadPlugin_ReportsSanitizedAssemblyLoadCategory_3414()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_plugin_load_category");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginPath = Path.Combine(projectRoot, "broken.dll");
                File.WriteAllText(pluginPath, $"not a real dll from {projectRoot}");

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);

                Assert.Equal("plugin", diagnostic.Kind);
                Assert.Equal("error", diagnostic.Severity);
                Assert.Equal("assembly_load_failed", diagnostic.Category);
                Assert.Equal("broken.dll", diagnostic.Path);
                Assert.DoesNotContain(projectRoot, diagnostic.Path, StringComparison.Ordinal);
                Assert.Contains("Plugin assembly load failed", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains(nameof(BadImageFormatException), diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPlugin_ReportsSanitizedConstructorFailure_3701()
    {
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ThrowingPluginConstructorEnvironmentVariable);
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ThrowingPluginConstructorEnvironmentVariable, "1");

                ExtractorPluginRegistry.LoadPluginForTests(Assembly.GetExecutingAssembly().Location);

                var diagnostic = Assert.Single(
                    ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!,
                    item => item.TypeName == typeof(ThrowingPluginSymbolExtractor).FullName);
                Assert.Equal("plugin_type", diagnostic.Kind);
                Assert.Equal("error", diagnostic.Severity);
                Assert.Equal("constructor_failed", diagnostic.Category);
                Assert.Contains(nameof(InvalidOperationException), diagnostic.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("plugin ctor boom", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void LoadPlugin_LoadsExtractorAssemblyInCollectibleContext_3413()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();

                ExtractorPluginRegistry.LoadPluginForTests(Assembly.GetExecutingAssembly().Location);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out var extractor));
                var loadContext = Assert.Single(ExtractorPluginRegistry.PluginAssemblyLoadContextsForTests());
                Assert.True(loadContext.IsCollectible);
                Assert.NotSame(AssemblyLoadContext.Default, loadContext);
                Assert.Same(loadContext, AssemblyLoadContext.GetLoadContext(extractor.GetType().Assembly));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
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
                    Assert.Equal("invalid_pattern_config", diagnostic.Category);
                    Assert.EndsWith(".yaml", diagnostic.Path);
                    Assert.DoesNotContain(projectRoot, diagnostic.Path, StringComparison.Ordinal);
                });
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigsForProjectRoot_UsesExplicitRootInsteadOfCurrentDirectory()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_project_patterns");
        var cwdRoot = TestProjectHelper.CreateTempProject("extractor_registry_cwd_patterns");
        lock (TestConsoleLock.Gate)
        {
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                ExtractorPluginRegistry.ReloadForTests();
                WritePatternConfig(
                    projectRoot,
                    "project.yaml",
                    "language: \"projectdsl\"\nextensions:\n  - extension: \".projecttoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^project (?<name>\\\\w+)\"\n");
                WritePatternConfig(
                    cwdRoot,
                    "cwd.yaml",
                    "language: \"cwddsl\"\nextensions:\n  - extension: \".cwdtoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^cwd (?<name>\\\\w+)\"\n");
                Environment.CurrentDirectory = cwdRoot;

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                var extensions = ExtractorPluginRegistry.LanguageExtensions;

                Assert.Equal("projectdsl", extensions[".projecttoy"]);
                Assert.False(extensions.ContainsKey(".cwdtoy"));
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
                TestProjectHelper.DeleteDirectory(cwdRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigs_SanitizesRejectedPathAndReason_3243()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_sanitized_pattern");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                WritePatternConfig(
                    projectRoot,
                    "broken.yaml",
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"(?<name>\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);

                Assert.Equal(".cdidx/patterns/broken.yaml", diagnostic.Path);
                Assert.Equal("invalid_pattern_config", diagnostic.Category);
                Assert.DoesNotContain(projectRoot, diagnostic.Path, StringComparison.Ordinal);
                Assert.Contains("invalid regex", diagnostic.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("(?<name>", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Theory]
    [InlineData("language")]
    [InlineData("extension")]
    [InlineData("kind")]
    public void LoadPatternConfigs_RejectsOverlongScalarValues_3245(string scalarName)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"extractor_registry_scalar_cap_{scalarName}");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var content = BuildPatternConfigWithOverlongScalar(scalarName);
                WritePatternConfig(projectRoot, $"{scalarName}.yaml", content);

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(0, status.PatternConfigCount);
                Assert.Equal(1, status.SkippedFileCount);
                Assert.Equal(1, status.DiagnosticCount);
                var diagnostic = Assert.Single(status.Diagnostics!);
                Assert.Equal("pattern", diagnostic.Kind);
                Assert.Equal("error", diagnostic.Severity);
                Assert.Equal("invalid_pattern_config", diagnostic.Category);
                Assert.Contains($"{scalarName} scalar is too long", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("maximum", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigs_RejectsExtensionNormalizedBeyondLimit_3245()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_extension_normalized_cap");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var extension = new string('e', ExtractorPluginRegistry.MaxPatternExtensionLength);
                WritePatternConfig(
                    projectRoot,
                    "extension-normalized.yaml",
                    $"language: \"toydsl\"\nextensions:\n  - extension: \"{extension}\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);

                Assert.Contains("extension scalar is too long", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains((ExtractorPluginRegistry.MaxPatternExtensionLength + 1).ToString(), diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigs_RecordsPatternTimeoutDiagnostic_Issue3821()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_pattern_timeout_3821");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                WritePatternConfig(
                    projectRoot,
                    "slow.yaml",
                    "language: \"timeoutdsl\"\nextensions:\n  - extension: \".timeouttoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(a+)+$\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("timeoutdsl", out var extractor));

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = extractor.Extract(
                        1,
                        new string('a', 10_000) + "!",
                        new ExtractionContext("timeoutdsl", Path.Combine(projectRoot, "sample.timeouttoy")));

                    Assert.Empty(symbols);
                });
                var diagnostic = Assert.Single(
                    ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!,
                    item => item.Category == "pattern_regex_timeout");

                Assert.Equal("pattern", diagnostic.Kind);
                Assert.Equal("warning", diagnostic.Severity);
                Assert.Equal(".cdidx/patterns/slow.yaml", diagnostic.Path);
                Assert.Contains("timeoutdsl", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("class", diagnostic.Message, StringComparison.Ordinal);
                Assert.Contains("Pattern extractor", stderr, StringComparison.Ordinal);
                Assert.DoesNotContain(projectRoot, diagnostic.Path, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigs_SanitizesKindInRegexLengthRejection_Issue3821()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_pattern_kind_sanitized_3821");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                WritePatternConfig(
                    projectRoot,
                    "long-regex.yaml",
                    $"language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"/private/secret/kind\"\n    regex: \"{new string('x', ExtractorPluginRegistry.MaxPatternRegexLength + 1)}\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);

                Assert.Equal("invalid_pattern_config", diagnostic.Category);
                Assert.Contains("regex for kind", diagnostic.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("/private/secret", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    private static void WritePatternConfig(string projectRoot, string fileName, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string BuildPatternConfigWithOverlongScalar(string scalarName)
    {
        return scalarName switch
        {
            "language" => $"language: \"{new string('l', ExtractorPluginRegistry.MaxPatternLanguageLength + 1)}\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(?<name>\\\\w+)\"\n",
            "extension" => $"language: \"toydsl\"\nextensions:\n  - extension: \".{new string('e', ExtractorPluginRegistry.MaxPatternExtensionLength)}\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(?<name>\\\\w+)\"\n",
            "kind" => $"language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"{new string('k', ExtractorPluginRegistry.MaxPatternKindLength + 1)}\"\n    regex: \"^(?<name>\\\\w+)\"\n",
            _ => throw new ArgumentOutOfRangeException(nameof(scalarName), scalarName, null),
        };
    }
}

public sealed class CollectiblePluginSymbolExtractor : ISymbolExtractor
{
    public string Language => "collectibledsl";

    public IReadOnlyCollection<string> FileExtensions => [".collectible"];

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
        => [];
}

public sealed class ThrowingPluginSymbolExtractor : ISymbolExtractor
{
    public ThrowingPluginSymbolExtractor()
    {
        if (Environment.GetEnvironmentVariable(ExtractorPluginRegistryTests.ThrowingPluginConstructorEnvironmentVariable) == "1")
            throw new InvalidOperationException("plugin ctor boom");
    }

    public string Language => "throwingplugindsl";

    public IReadOnlyCollection<string> FileExtensions => [".throwingplugin"];

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
        => [];
}
