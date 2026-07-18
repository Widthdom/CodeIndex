using System.Reflection;
using System.Runtime.Loader;
using CodeIndex.Cli;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

[assembly: CdidxPlugin(ExtractorPluginRegistry.CurrentApiVersion, ExtractorPluginRegistry.CurrentApiVersion)]

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
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
                var status = ExtractorPluginRegistry.GetStatusSnapshot(projectRoot);

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
    public void LoadPlugin_RejectsSymlinkAssemblyCandidate_Issue3970()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_plugin_symlink_3970");
        lock (TestConsoleLock.Gate)
        {
            var target = Path.Combine(projectRoot, "target.dll");
            var link = Path.Combine(projectRoot, "link.dll");
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                File.WriteAllText(target, "not a real dll");
                File.CreateSymbolicLink(link, target);

                ExtractorPluginRegistry.LoadPluginForTests(link);
                var status = ExtractorPluginRegistry.GetStatusSnapshot();

                Assert.Equal(0, status.PluginAssemblyCount);
                Assert.Equal(1, status.SkippedFileCount);
                var diagnostic = Assert.Single(status.Diagnostics!);
                Assert.Equal("plugin", diagnostic.Kind);
                Assert.Equal("error", diagnostic.Severity);
                Assert.Equal("plugin_reparse_point", diagnostic.Category);
                Assert.Equal("link.dll", diagnostic.Path);
                Assert.Contains("symbolic links and reparse points", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (File.Exists(link))
                    File.Delete(link);
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
                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(1, status.RetainedLoadContextCount);
                Assert.Equal(ExtractorPluginRegistry.PluginLoadContextLifecycle, status.LoadContextLifecycle);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void LoadPlugin_DualRoleExtractorType_IsConstructedOnce_Issue3971()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();

                ExtractorPluginRegistry.LoadPluginForTests(Assembly.GetExecutingAssembly().Location);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("dualroleplugindsl", out var symbolExtractor));
                Assert.True(ExtractorPluginRegistry.TryGetReferenceExtractor("dualroleplugindsl", out var referenceExtractor));
                Assert.Same(symbolExtractor, referenceExtractor);
                var constructorCount = Assert.IsType<int>(
                    symbolExtractor.GetType()
                        .GetProperty(nameof(DualRolePluginExtractor.ConstructorCount), BindingFlags.Public | BindingFlags.Static)!
                        .GetValue(null));
                Assert.Equal(1, constructorCount);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().RetainedLoadContextCount);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void ResetForTests_UnloadsRetainedPluginAssemblyContexts_Issue3971()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.LoadPluginForTests(Assembly.GetExecutingAssembly().Location);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().RetainedLoadContextCount);

                ExtractorPluginRegistry.ResetForTests();

                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(0, status.RetainedLoadContextCount);
                Assert.Equal(ExtractorPluginRegistry.PluginLoadContextLifecycle, status.LoadContextLifecycle);
                Assert.Empty(ExtractorPluginRegistry.PluginAssemblyLoadContextsForTests());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void LoadPlugin_RepeatedDiscoveryRetainsSingleContext_Issue3971()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginPath = Assembly.GetExecutingAssembly().Location;

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);
                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);

                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(1, status.PluginAssemblyCount);
                Assert.Equal(1, status.RetainedLoadContextCount);
                Assert.Single(ExtractorPluginRegistry.PluginAssemblyLoadContextsForTests());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void PluginAssemblyPathIdentity_FollowsPathCasingPolicy_Issue3790()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_path_casing_3790");
        lock (TestConsoleLock.Gate)
        {
            lock (PathCasingTestLock.Gate)
            {
                var originalProbe = PathCasing.IgnoreCaseProbeForTesting;
                try
                {
                    ExtractorPluginRegistry.ResetForTests();
                    PathCasing.ResetCacheForTests();
                    PathCasing.IgnoreCaseProbeForTesting = _ => true;
                    var pluginPath = Path.Combine(projectRoot, "Plugin.dll");
                    var caseVariant = Path.Combine(projectRoot, "plugin.dll");

                    Assert.True(ExtractorPluginRegistry.TryMarkPluginAssemblyPathLoadedForTests(pluginPath));
                    Assert.False(ExtractorPluginRegistry.TryMarkPluginAssemblyPathLoadedForTests(caseVariant));

                    ExtractorPluginRegistry.ResetForTests();
                    PathCasing.ResetCacheForTests();
                    PathCasing.IgnoreCaseProbeForTesting = _ => false;

                    Assert.True(ExtractorPluginRegistry.TryMarkPluginAssemblyPathLoadedForTests(pluginPath));
                    Assert.True(ExtractorPluginRegistry.TryMarkPluginAssemblyPathLoadedForTests(caseVariant));
                }
                finally
                {
                    PathCasing.IgnoreCaseProbeForTesting = originalProbe;
                    PathCasing.ResetCacheForTests();
                    ExtractorPluginRegistry.ResetForTests();
                    TestProjectHelper.DeleteDirectory(projectRoot);
                }
            }
        }
    }

    [Fact]
    public void PatternConfigPathIdentity_FollowsFilesystemCasingPolicy_Issue4597()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_pattern_path_casing_4597");
        lock (TestConsoleLock.Gate)
        {
            lock (PathCasingTestLock.Gate)
            {
                var originalProbe = PathCasing.IgnoreCaseProbeForTesting;
                try
                {
                    var upperPath = Path.Combine(projectRoot, "Patterns", "Rules.yaml");
                    var lowerPath = Path.Combine(projectRoot, "patterns", "rules.yaml");

                    ExtractorPluginRegistry.ResetForTests();
                    PathCasing.ResetCacheForTests();
                    PathCasing.IgnoreCaseProbeForTesting = _ => true;
                    Assert.True(ExtractorPluginRegistry.TryMarkPatternConfigPathLoadedForTests(upperPath));
                    Assert.False(ExtractorPluginRegistry.TryMarkPatternConfigPathLoadedForTests(lowerPath));

                    ExtractorPluginRegistry.ResetForTests();
                    PathCasing.ResetCacheForTests();
                    PathCasing.IgnoreCaseProbeForTesting = _ => false;
                    Assert.True(ExtractorPluginRegistry.TryMarkPatternConfigPathLoadedForTests(upperPath));
                    Assert.True(ExtractorPluginRegistry.TryMarkPatternConfigPathLoadedForTests(lowerPath));
                }
                finally
                {
                    PathCasing.IgnoreCaseProbeForTesting = originalProbe;
                    PathCasing.ResetCacheForTests();
                    ExtractorPluginRegistry.ResetForTests();
                    TestProjectHelper.DeleteDirectory(projectRoot);
                }
            }
        }
    }

    [Fact]
    public void LoadPlugin_SkipsAssembliesAboveTypeInspectionLimit_Issue3790()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.TypeInspectionLimitForTesting = 1;

                ExtractorPluginRegistry.LoadPluginForTests(Assembly.GetExecutingAssembly().Location);
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);

                Assert.Equal("plugin", diagnostic.Kind);
                Assert.Equal("skipped", diagnostic.Severity);
                Assert.Equal("plugin_type_limit_exceeded", diagnostic.Category);
                Assert.Contains("too many loadable types", diagnostic.Message, StringComparison.Ordinal);
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

                ExtractorPluginRegistry.LoadPatternConfigsForPath(Path.Combine(projectRoot, "sample.broken"), projectRoot);
                var status = ExtractorPluginRegistry.GetStatusSnapshot(projectRoot);

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
                var extensions = ExtractorPluginRegistry.GetLanguageExtensions(projectRoot);

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
    public void LoadPatternConfigsForPath_StopsAtWorkspaceRootAndReportsProvenance_Issue4597()
    {
        var parentRoot = TestProjectHelper.CreateTempProject("extractor_registry_pattern_boundary_4597");
        var workspaceRoot = Path.Combine(parentRoot, "workspace");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(workspaceRoot, "src"));
                ExtractorPluginRegistry.ResetForTests();
                WritePatternConfig(
                    parentRoot,
                    "parent.yaml",
                    "language: \"parentdsl\"\nextensions:\n  - extension: \".parent\"\npatterns:\n  - kind: \"class\"\n    regex: \"^parent (?<name>\\\\w+)\"\n");
                WritePatternConfig(
                    workspaceRoot,
                    "workspace.yaml",
                    "language: \"workspacedsl\"\nextensions:\n  - extension: \".workspace\"\npatterns:\n  - kind: \"class\"\n    regex: \"^workspace (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForPath(
                    Path.Combine(workspaceRoot, "src", "sample.workspace"),
                    workspaceRoot);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("workspacedsl", workspaceRoot, out _));
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("parentdsl", workspaceRoot, out _));
                var config = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot(workspaceRoot).PatternConfigs!);
                Assert.Equal("workspace", config.Source);
                Assert.Equal("workspacedsl", config.Language);
                Assert.Equal(1, config.RuleCount);
                Assert.Equal(".cdidx/patterns/workspace.yaml", config.Path);
                Assert.DoesNotContain(parentRoot, config.Path, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(parentRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigsForProjectRoot_LoadsCaseDistinctFilesWhenFilesystemIsCaseSensitive_Issue4597()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_case_distinct_patterns_4597");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                if (PathCasing.IsIgnoreCase(projectRoot))
                    return;

                ExtractorPluginRegistry.ResetForTests();
                WritePatternConfig(
                    projectRoot,
                    "Rules.yaml",
                    "language: \"upperdsl\"\nextensions:\n  - extension: \".upper\"\npatterns:\n  - kind: \"class\"\n    regex: \"^upper (?<name>\\\\w+)\"\n");
                WritePatternConfig(
                    projectRoot,
                    "rules.yaml",
                    "language: \"lowerdsl\"\nextensions:\n  - extension: \".lower\"\npatterns:\n  - kind: \"class\"\n    regex: \"^lower (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("upperdsl", projectRoot, out _));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("lowerdsl", projectRoot, out _));
                Assert.Equal(2, ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).PatternConfigCount);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void PatternRuleBudgets_AreReleasedAndScopedPerWorkspace_Issue4595()
    {
        var workspaceA = TestProjectHelper.CreateTempProject("extractor_registry_pattern_budget_a_4595");
        var workspaceB = TestProjectHelper.CreateTempProject("extractor_registry_pattern_budget_b_4595");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var rulesA = string.Join(
                    "\n",
                    Enumerable.Range(0, ExtractorPluginRegistry.MaxPatternRulesTotal)
                        .Select(i => $"  - kind: \"class\"\n    regex: \"^a{i} (?<name>\\\\w+)\""));
                WritePatternConfig(
                    workspaceA,
                    "shared.yaml",
                    $"language: \"shareddsl\"\nextensions:\n  - extension: \".shared\"\npatterns:\n{rulesA}\n");
                WritePatternConfig(
                    workspaceB,
                    "shared.yaml",
                    "language: \"shareddsl\"\nextensions:\n  - extension: \".shared\"\npatterns:\n  - kind: \"class\"\n    regex: \"^b (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceA);
                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceB);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var extractorA));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var extractorB));
                Assert.Equal(
                    ExtractorPluginRegistry.MaxPatternRulesTotal,
                    Assert.IsType<ConfiguredSymbolExtractor>(extractorA).PatternsForTests.Count);
                Assert.Single(Assert.IsType<ConfiguredSymbolExtractor>(extractorB).PatternsForTests);
                Assert.Single(extractorB.Extract(1, "b Beta", new ExtractionContext("shareddsl", "sample.shared")));

                WritePatternConfig(
                    workspaceA,
                    "shared.yaml",
                    "language: \"shareddsl\"\nextensions:\n  - extension: \".shared\"\npatterns:\n  - kind: \"class\"\n    regex: \"^reloaded (?<name>\\\\w+)\"\n");
                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspaceA);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var reloadedA));
                Assert.Single(Assert.IsType<ConfiguredSymbolExtractor>(reloadedA).PatternsForTests);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var unchangedB));
                Assert.Same(extractorB, unchangedB);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspaceA);
                TestProjectHelper.DeleteDirectory(workspaceB);
            }
        }
    }

    [Fact]
    public void PatternTimeoutCooldown_DoesNotCrossWorkspaceSnapshots_Issue4595()
    {
        var workspaceA = TestProjectHelper.CreateTempProject("extractor_registry_pattern_timeout_a_4595");
        var workspaceB = TestProjectHelper.CreateTempProject("extractor_registry_pattern_timeout_b_4595");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                const string config = "language: \"shareddsl\"\nextensions:\n  - extension: \".shared\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(a+)+$\"\n";
                WritePatternConfig(workspaceA, "shared.yaml", config);
                WritePatternConfig(workspaceB, "shared.yaml", config);
                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceA);
                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspaceB);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var extractorA));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var extractorB));
                Assert.NotSame(extractorA, extractorB);

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var slowLine = new string('a', 10_000) + "!";
                    Assert.Empty(extractorA.Extract(1, slowLine, new ExtractionContext("shareddsl", "a.shared")));
                    Assert.Empty(extractorA.Extract(2, slowLine, new ExtractionContext("shareddsl", "a2.shared")));
                });

                var recovered = Assert.Single(
                    extractorB.Extract(3, "aaaa", new ExtractionContext("shareddsl", "b.shared")));
                Assert.Equal("aaaa", recovered.Name);
                Assert.Single(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("timed out", StringComparison.Ordinal)));
                Assert.Contains(
                    ExtractorPluginRegistry.GetStatusSnapshot(workspaceA).Diagnostics!,
                    diagnostic => diagnostic.Category == "pattern_regex_timeout");
                Assert.DoesNotContain(
                    ExtractorPluginRegistry.GetStatusSnapshot(workspaceB).Diagnostics ?? [],
                    diagnostic => diagnostic.Category == "pattern_regex_timeout");
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspaceA);
                TestProjectHelper.DeleteDirectory(workspaceB);
            }
        }
    }

    [Fact]
    public void WorkspaceSnapshots_IsolateSameLanguageSequentiallyAndAcrossReload_Issue4602()
    {
        var workspaceA = TestProjectHelper.CreateTempProject("extractor_registry_snapshot_a_4602");
        var workspaceB = TestProjectHelper.CreateTempProject("extractor_registry_snapshot_b_4602");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var extractorA = new SnapshotSymbolExtractor("shareddsl", "workspace-a");
                var extractorB = new SnapshotSymbolExtractor("shareddsl", "workspace-b");
                var referenceA = new SnapshotReferenceExtractor("shareddsl", "reference-a");
                var referenceB = new SnapshotReferenceExtractor("shareddsl", "reference-b");
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspaceA, extractorA);
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspaceB, extractorB);
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspaceA, referenceA);
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspaceB, referenceB);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var resolvedA));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var resolvedB));
                Assert.Same(extractorA, resolvedA);
                Assert.Same(extractorB, resolvedB);
                Assert.True(ExtractorPluginRegistry.TryGetReferenceExtractor(
                    "shareddsl",
                    workspaceA,
                    "a.shared",
                    out var resolvedReferenceA));
                Assert.True(ExtractorPluginRegistry.TryGetReferenceExtractor(
                    "shareddsl",
                    workspaceB,
                    "b.shared",
                    out var resolvedReferenceB));
                Assert.Same(referenceA, resolvedReferenceA);
                Assert.Same(referenceB, resolvedReferenceB);
                Assert.Equal(
                    "reference-a",
                    Assert.Single(CodeIndex.Indexer.ReferenceExtractor.ExtractNormalized(
                        1,
                        "shareddsl",
                        "reference-a",
                        hasOversizeLine: false,
                        symbols: [],
                        path: "a.shared",
                        workspaceRoot: workspaceA)).SymbolName);
                Assert.Equal(
                    "reference-b",
                    Assert.Single(CodeIndex.Indexer.ReferenceExtractor.ExtractNormalized(
                        2,
                        "shareddsl",
                        "reference-b",
                        hasOversizeLine: false,
                        symbols: [],
                        path: "b.shared",
                        workspaceRoot: workspaceB)).SymbolName);
                Assert.Equal("workspace-a", Assert.Single(resolvedA.Extract(1, "", new ExtractionContext("shareddsl", "a.shared"))).Name);
                Assert.Equal("workspace-b", Assert.Single(resolvedB.Extract(2, "", new ExtractionContext("shareddsl", "b.shared"))).Name);

                var lateUserExtractor = new SnapshotSymbolExtractor("shareddsl", "late-user");
                ExtractorPluginRegistry.Register(lateUserExtractor);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var stillActiveA));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var stillActiveB));
                Assert.Same(extractorA, stillActiveA);
                Assert.Same(extractorB, stillActiveB);

                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspaceA);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var reloadedA));
                Assert.Same(lateUserExtractor, reloadedA);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var unchangedB));
                Assert.Same(extractorB, unchangedB);
                Assert.Equal("workspace-b", Assert.Single(unchangedB.Extract(3, "", new ExtractionContext("shareddsl", "b2.shared"))).Name);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspaceA);
                TestProjectHelper.DeleteDirectory(workspaceB);
            }
        }
    }

    [Fact]
    public void WorkspaceSnapshots_IsolateSameLanguageDuringConcurrentRegistration_Issue4602()
    {
        var workspaceA = TestProjectHelper.CreateTempProject("extractor_registry_snapshot_concurrent_a_4602");
        var workspaceB = TestProjectHelper.CreateTempProject("extractor_registry_snapshot_concurrent_b_4602");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var extractorA = new SnapshotSymbolExtractor("shareddsl", "workspace-a");
                var extractorB = new SnapshotSymbolExtractor("shareddsl", "workspace-b");

                Parallel.Invoke(
                    () => ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspaceA, extractorA),
                    () => ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspaceB, extractorB));

                Parallel.For(0, 64, _ =>
                {
                    Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceA, out var resolvedA));
                    Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspaceB, out var resolvedB));
                    Assert.Same(extractorA, resolvedA);
                    Assert.Same(extractorB, resolvedB);
                });
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspaceA);
                TestProjectHelper.DeleteDirectory(workspaceB);
            }
        }
    }

    [Fact]
    public void WorkspacePluginAssemblies_AreOwnedByTheirImmutableSnapshots_Issue4602()
    {
        var workspaceA = TestProjectHelper.CreateTempProject("extractor_registry_plugin_snapshot_a_4602");
        var workspaceB = TestProjectHelper.CreateTempProject("extractor_registry_plugin_snapshot_b_4602");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                var pluginA = Path.Combine(workspaceA, ".cdidx", "plugins", "snapshot-plugin.dll");
                var pluginB = Path.Combine(workspaceB, ".cdidx", "plugins", "snapshot-plugin.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(pluginA)!);
                Directory.CreateDirectory(Path.GetDirectoryName(pluginB)!);
                File.Copy(Assembly.GetExecutingAssembly().Location, pluginA);
                File.Copy(Assembly.GetExecutingAssembly().Location, pluginB);

                ExtractorPluginRegistry.LoadPluginsForProjectRoot(workspaceA);
                ExtractorPluginRegistry.LoadPluginsForProjectRoot(workspaceB);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspaceA, out var extractorA));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspaceB, out var extractorB));
                Assert.NotSame(extractorA, extractorB);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot(workspaceA).PluginAssemblyCount);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot(workspaceB).PluginAssemblyCount);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot(workspaceA).RetainedLoadContextCount);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot(workspaceB).RetainedLoadContextCount);

                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspaceA);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspaceB, out var unchangedB));
                Assert.Same(extractorB, unchangedB);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspaceA);
                TestProjectHelper.DeleteDirectory(workspaceB);
            }
        }
    }

    [Fact]
    public void WorkspaceSnapshots_ExposeAndApplyRegistrationPrecedence_Issue4602()
    {
        var workspace = TestProjectHelper.CreateTempProject("extractor_registry_snapshot_precedence_4602");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var userExtractor = new SnapshotSymbolExtractor("shareddsl", "user", ".precedence");
                var workspaceExtractor = new SnapshotSymbolExtractor("shareddsl", "workspace", ".precedence");
                var workspaceCollisionExtractor = new SnapshotSymbolExtractor("workspacecollision", "workspace-collision", ".precedence");
                ExtractorPluginRegistry.Register(userExtractor);
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspace, workspaceExtractor);
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(workspace, workspaceCollisionExtractor);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("shareddsl", workspace, out var resolved));
                Assert.Same(userExtractor, resolved);
                Assert.True(ExtractorPluginRegistry.TryGetLanguageForExtension(".precedence", workspace, out var extensionLanguage));
                Assert.Equal("shareddsl", extensionLanguage);
                var status = ExtractorPluginRegistry.GetStatusSnapshot(workspace);
                Assert.Equal("workspace", status.SnapshotScope);
                Assert.Equal(
                    ["built_in", "user_plugin", "user_pattern", "workspace_plugin", "workspace_pattern"],
                    status.RegistrationPrecedence);

                var builtInOverride = new SnapshotSymbolExtractor("csharp", "plugin-override");
                ExtractorPluginRegistry.Register(builtInOverride);
                var symbols = CodeIndex.Indexer.SymbolExtractor.Extract(
                    1,
                    "csharp",
                    "class BuiltInWins {}",
                    "sample.cs",
                    workspace);
                Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "BuiltInWins");
                Assert.Equal(0, builtInOverride.ExtractionCount);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspace);
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
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).Diagnostics!);

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

    [Fact]
    public void LoadPatternConfig_InvalidRegexDoesNotConsumeBudgetAndRepairedContentRetries_Issue4593()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_transactional_pattern_4593");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var patternPath = Path.Combine(projectRoot, ".cdidx", "patterns", "transactional.yaml");
                WritePatternConfig(
                    projectRoot,
                    "transactional.yaml",
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"(?<name>\"\n");

                Parallel.For(
                    fromInclusive: 0,
                    toExclusive: 4,
                    _ => ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath));
                ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath);

                var rejectedStatus = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(0, rejectedStatus.PatternConfigCount);
                Assert.Equal(1, rejectedStatus.DiagnosticCount);
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("toydsl", out _));

                var rules = string.Join(
                    "\n",
                    Enumerable.Range(0, ExtractorPluginRegistry.MaxPatternRulesTotal)
                        .Select(i => $"  - kind: \"class\"\n    regex: \"^entity{i} (?<name>\\\\w+)\""));
                WritePatternConfig(
                    projectRoot,
                    "transactional.yaml",
                    $"language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n{rules}\n");

                Parallel.For(
                    fromInclusive: 0,
                    toExclusive: 4,
                    _ => ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath));

                var loadedStatus = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(1, loadedStatus.PatternConfigCount);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("toydsl", out var extractor));
                Assert.Equal(
                    ExtractorPluginRegistry.MaxPatternRulesTotal,
                    Assert.IsType<ConfiguredSymbolExtractor>(extractor).PatternsForTests.Count);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfig_AcceptedSidecarIsNotReopenedDuringLaterProbes_Issue4593()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_loaded_pattern_probe_4593");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var patternPath = Path.Combine(projectRoot, ".cdidx", "patterns", "accepted.yaml");
                WritePatternConfig(
                    projectRoot,
                    "accepted.yaml",
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath);
                File.Delete(patternPath);
                ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath);

                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(1, status.PatternConfigCount);
                Assert.Equal(0, status.DiagnosticCount);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("toydsl", out _));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfig_RejectsUnknownSymbolKindBeforeRegistration_Issue4593()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_unknown_kind_4593");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                WritePatternConfig(
                    projectRoot,
                    "unknown-kind.yaml",
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"not_a_symbol_kind\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);

                var status = ExtractorPluginRegistry.GetStatusSnapshot(projectRoot);
                Assert.Equal(0, status.PatternConfigCount);
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("toydsl", out _));
                var diagnostic = Assert.Single(status.Diagnostics!);
                Assert.Equal("invalid_pattern_config", diagnostic.Category);
                Assert.Contains("unknown symbol kind", diagnostic.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfig_TransientMissingFileCanBeLoadedAfterCreation_Issue4593()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_transient_read_4593");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var patternPath = Path.Combine(projectRoot, ".cdidx", "patterns", "later.yaml");

                ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath);
                WritePatternConfig(
                    projectRoot,
                    "later.yaml",
                    "language: \"laterdsl\"\nextensions:\n  - extension: \".later\"\npatterns:\n  - kind: \"class\"\n    regex: \"^later (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigForTests(patternPath);

                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().PatternConfigCount);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("laterdsl", out _));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPatternConfigs_RejectsOverlongScalarValues_3245()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_scalar_caps");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                foreach (var scalarName in new[] { "language", "extension", "kind" })
                {
                    ExtractorPluginRegistry.ResetForTests();
                    var content = BuildPatternConfigWithOverlongScalar(scalarName);
                    WritePatternConfig(projectRoot, "case.yaml", content);

                    ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                    var status = ExtractorPluginRegistry.GetStatusSnapshot(projectRoot);

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
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).Diagnostics!);

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
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("timeoutdsl", projectRoot, out var extractor));

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = extractor.Extract(
                        1,
                        new string('a', 10_000) + "!",
                        new ExtractionContext("timeoutdsl", Path.Combine(projectRoot, "sample.timeouttoy")));

                    Assert.Empty(symbols);
                });
                var diagnostic = Assert.Single(
                    ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).Diagnostics!,
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
    public void LoadPatternConfigs_SanitizesUnknownKindRejection_Issues3821And4593()
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
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).Diagnostics!);

                Assert.Equal("invalid_pattern_config", diagnostic.Category);
                Assert.Contains("unknown symbol kind", diagnostic.Message, StringComparison.Ordinal);
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

    private sealed class SnapshotSymbolExtractor(string language, string symbolName, string extension = ".shared") : ISymbolExtractor
    {
        private int extractionCount;

        public string Language { get; } = language;
        public IReadOnlyCollection<string> FileExtensions { get; } = [extension];
        internal int ExtractionCount => Volatile.Read(ref extractionCount);

        public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
        {
            Interlocked.Increment(ref extractionCount);
            return
            [
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = symbolName,
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                    Signature = symbolName,
                },
            ];
        }
    }

    private sealed class SnapshotReferenceExtractor(string language, string symbolName) : CodeIndex.Indexer.Extensibility.IReferenceExtractor
    {
        public string Language { get; } = language;
        public IReadOnlyCollection<string> FileExtensions { get; } = [".shared"];

        public IReadOnlyList<ReferenceRecord> Extract(long fileId, string source, ExtractionContext context)
            =>
            [
                new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = symbolName,
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = source,
                },
            ];
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

public sealed class DualRolePluginExtractor : ISymbolExtractor, IReferenceExtractor
{
    public DualRolePluginExtractor()
    {
        ConstructorCount++;
    }

    public static int ConstructorCount { get; private set; }

    public string Language => "dualroleplugindsl";

    public IReadOnlyCollection<string> FileExtensions => [".dualroleplugin"];

    IReadOnlyList<SymbolRecord> ISymbolExtractor.Extract(long fileId, string source, ExtractionContext context)
        => [];

    IReadOnlyList<ReferenceRecord> IReferenceExtractor.Extract(long fileId, string source, ExtractionContext context)
        => [];
}
