using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

[assembly: CdidxPlugin(ExtractorPluginRegistry.CurrentApiVersion, ExtractorPluginRegistry.CurrentApiVersion)]

namespace CodeIndex.Tests;

[Collection("Plugin registry sensitive")]
public class ExtractorPluginRegistryTests
{
    private readonly string pluginAssemblyFixturePath;

    public ExtractorPluginRegistryTests(TrustedPluginAssemblyFixture trustedPluginAssembly)
    {
        pluginAssemblyFixturePath = trustedPluginAssembly.PluginPath;
    }

    internal const string ThrowingPluginConstructorEnvironmentVariable = "CDIDX_TEST_THROWING_PLUGIN_CTOR";
    internal const string SlowPluginConstructorEnvironmentVariable = "CDIDX_TEST_SLOW_PLUGIN_CTOR";
    internal const string CrashingPluginConstructorEnvironmentVariable = "CDIDX_TEST_CRASHING_PLUGIN_CTOR";

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
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_plugin_cap");
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
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_plugin_total_cap");
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
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_plugin_cap_visible");
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
    public void LoadPluginAssemblies_RejectsUnsafeDirectoryModeAndAncestorSymlink_Issue4596()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_boundary_4596");
        lock (TestConsoleLock.Gate)
        {
            var realParent = Path.Combine(projectRoot, "real-parent");
            var pluginDirectory = Path.Combine(realParent, "plugins");
            var linkedParent = Path.Combine(projectRoot, "linked-parent");
            try
            {
                Directory.CreateDirectory(pluginDirectory);
                File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(pluginDirectory, "plugin.dll"));

                ExtractorPluginRegistry.ResetForTests();
                File.SetUnixFileMode(
                    pluginDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupWrite);
                ExtractorPluginRegistry.LoadPluginAssembliesForTests([pluginDirectory]);

                var unsafeMode = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);
                Assert.Equal("extension_boundary_unsafe_permissions", unsafeMode.Category);
                Assert.Contains("group- or world-writable", unsafeMode.Message, StringComparison.Ordinal);

                File.SetUnixFileMode(
                    pluginDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.CreateSymbolicLink(linkedParent, realParent);
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.LoadPluginAssembliesForTests([Path.Combine(linkedParent, "plugins")]);

                var ancestorLink = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);
                Assert.Equal("extension_boundary_unsafe_ancestor", ancestorLink.Category);
                Assert.Contains("every ancestor", ancestorLink.Message, StringComparison.Ordinal);
            }
            finally
            {
                ExecutableExtensionBoundary.StagedForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(linkedParent) || File.Exists(linkedParent))
                    Directory.Delete(linkedParent);
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPlugin_LoadsPrivateStagedBytesAfterSourceRenameSwap_Issue4596()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("extractor_registry_staging_swap_4596");
        lock (TestConsoleLock.Gate)
        {
            var pluginPath = Path.Combine(projectRoot, "plugin.dll");
            var originalPath = Path.Combine(projectRoot, "plugin-original.dll");
            try
            {
                CopyPluginFixture(pluginPath);
                ExtractorPluginRegistry.ResetForTests();
                ExecutableExtensionBoundary.StagedForTesting = (source, staged) =>
                {
                    if (!string.Equals(pluginPath, source, StringComparison.Ordinal))
                        return;
                    Assert.NotEqual(source, staged);
                    File.Move(source, originalPath);
                    File.WriteAllText(source, "replacement bytes that are not an assembly");
                };

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);

                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(1, status.PluginAssemblyCount);
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out _));
                var stagedPath = Assert.Single(ExtractorPluginRegistry.PluginStagedAssemblyPathsForTests());
                Assert.True(File.Exists(stagedPath));
                Assert.NotEqual(pluginPath, stagedPath);
                Assert.Equal("replacement bytes that are not an assembly", File.ReadAllText(pluginPath));
            }
            finally
            {
                ExecutableExtensionBoundary.StagedForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void ExecutableBoundary_WindowsRejectsUntrustedWriteAclAndCleansStaging_Issue4596()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_windows_acl_4596");
        var pluginDirectory = Path.Combine(projectRoot, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        var pluginPath = Path.Combine(pluginDirectory, "plugin.dll");
        try
        {
            CopyPluginFixture(pluginPath);
            var security = new DirectoryInfo(pluginDirectory).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.CreateFiles,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(pluginDirectory).SetAccessControl(security);

            Assert.False(ExecutableExtensionBoundary.TryValidateDirectory(pluginDirectory, out _, out var failure));
            Assert.Equal("extension_boundary_unsafe_permissions", failure.Category);

            security.RemoveAccessRuleAll(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.CreateFiles,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(pluginDirectory).SetAccessControl(security);
            Assert.True(ExecutableExtensionBoundary.TryStageFile(
                pluginDirectory,
                pluginPath,
                ExtractorPluginRegistry.MaxPluginAssemblyBytes,
                out var staging,
                out _));
            var stagingDirectory = staging!.StagingDirectory;
            Assert.True(PluginDependencyStager.TryStageManagedDependencies(
                pluginDirectory,
                staging,
                ExtractorPluginRegistry.MaxPluginAssemblyBytes,
                out _,
                out _));

            staging.Dispose();

            Assert.False(Directory.Exists(stagingDirectory));

            var inheritOnlyDirectory = Path.Combine(projectRoot, "inherit-only");
            Directory.CreateDirectory(inheritOnlyDirectory);
            var inheritOnlySecurity = new DirectoryInfo(inheritOnlyDirectory).GetAccessControl();
            inheritOnlySecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.CreateFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.InheritOnly,
                AccessControlType.Allow));
            new DirectoryInfo(inheritOnlyDirectory).SetAccessControl(inheritOnlySecurity);
            Assert.True(ExecutableExtensionBoundary.TryValidateDirectory(inheritOnlyDirectory, out _, out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LoadPlugin_MetadataRejectsMissingMarkerWithoutStartingWorker_Issue4598()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_metadata_4598");
        lock (TestConsoleLock.Gate)
        {
            var pluginPath = Path.Combine(projectRoot, "no-marker.dll");
            try
            {
                File.Copy(typeof(ExtractorPluginRegistry).Assembly.Location, pluginPath);
                ExtractorPluginRegistry.ResetForTests();
                var processStarts = 0;
                ExtractorPluginWorkerClient.ProcessStartedForTesting = () => processStarts++;

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);

                Assert.Equal(0, processStarts);
                Assert.Equal(0, ExtractorPluginRegistry.PluginWorkerCountForTests());
                var diagnostic = Assert.Single(ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!);
                Assert.Equal("missing_plugin_attribute", diagnostic.Category);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void PluginMetadataInspector_RequiresExactMarkerConstructorSignature_Issue4598()
    {
        Assert.True(PluginMetadataInspector.IsExpectedMarkerConstructorSignatureForTests(
            ".ctor",
            [0x20, 0x02, 0x01, 0x08, 0x08]));
        Assert.False(PluginMetadataInspector.IsExpectedMarkerConstructorSignatureForTests(
            "Invoke",
            [0x20, 0x02, 0x01, 0x08, 0x08]));
        Assert.False(PluginMetadataInspector.IsExpectedMarkerConstructorSignatureForTests(
            ".ctor",
            [0x20, 0x01, 0x01, 0x08]));
        Assert.False(PluginMetadataInspector.IsExpectedMarkerConstructorSignatureForTests(
            ".ctor",
            [0x00, 0x02, 0x01, 0x08, 0x08]));
    }

    [Fact]
    public void LoadPlugin_RetriesFailedFingerprintAfterPartialCopyIsRepaired_Issue4598()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_retry_4598");
        lock (TestConsoleLock.Gate)
        {
            var pluginPath = Path.Combine(projectRoot, "plugin.dll");
            try
            {
                File.WriteAllText(pluginPath, "partial assembly copy");
                ExtractorPluginRegistry.ResetForTests();

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);
                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().DiagnosticCount);
                Assert.Equal(0, ExtractorPluginRegistry.PluginWorkerCountForTests());

                CopyPluginFixture(pluginPath);
                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out _));
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().PluginAssemblyCount);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void DefaultPluginDiscovery_RepairsAndReplacesFingerprintWithoutLeakingWorker_Issue4598()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_production_retry_4598");
        lock (TestConsoleLock.Gate)
        {
            var pluginDirectory = Path.Combine(projectRoot, "plugins");
            var pluginPath = Path.Combine(pluginDirectory, "plugin.dll");
            var mainAssemblyStageCount = 0;
            try
            {
                Directory.CreateDirectory(pluginDirectory);
                File.WriteAllText(pluginPath, "partial assembly copy");
                ExtractorPluginRegistry.ReloadForTests();
                ExtractorPluginRegistry.UserPluginDirectoryForTesting = pluginDirectory;
                ExecutableExtensionBoundary.StagedForTesting = (source, _) =>
                {
                    if (string.Equals(source, pluginPath, StringComparison.Ordinal))
                        mainAssemblyStageCount++;
                };

                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot().PluginAssemblyCount);
                Assert.Equal(0, ExtractorPluginRegistry.PluginWorkerCountForTests());
                Assert.Equal(1, mainAssemblyStageCount);
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot().PluginAssemblyCount);
                Assert.Equal(1, mainAssemblyStageCount);

                CopyPluginFixture(pluginPath);
                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().PluginAssemblyCount);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());
                Assert.Equal(2, mainAssemblyStageCount);
                var firstStagedPath = Assert.Single(ExtractorPluginRegistry.PluginStagedAssemblyPathsForTests());
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out _));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out _));
                Assert.Equal(2, mainAssemblyStageCount);

                File.AppendAllText(pluginPath, "fingerprint replacement padding");
                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot().PluginAssemblyCount);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());
                Assert.Equal(3, mainAssemblyStageCount);
                var replacementStagedPath = Assert.Single(ExtractorPluginRegistry.PluginStagedAssemblyPathsForTests());
                Assert.NotEqual(firstStagedPath, replacementStagedPath);
                Assert.False(File.Exists(firstStagedPath));
                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out _));
            }
            finally
            {
                ExecutableExtensionBoundary.StagedForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void CSharpWorkspacePrepass_ReusesLoadedPatternConfigSnapshot()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject(
            "extractor_registry_csharp_prepass_snapshot");
        lock (TestConsoleLock.Gate)
        {
            var pluginDirectory = Path.Combine(projectRoot, "plugins");
            var pluginPath = Path.Combine(pluginDirectory, "invalid.dll");
            var pluginStageCount = 0;
            try
            {
                Directory.CreateDirectory(pluginDirectory);
                File.WriteAllText(pluginPath, "invalid plugin assembly");
                var sourceDirectory = Path.Combine(projectRoot, "src");
                Directory.CreateDirectory(sourceDirectory);
                var targets = new List<CodeIndex.Indexer.CSharpStaticInterfacePrepass.FileTarget>();
                for (var index = 0; index < 8; index++)
                {
                    var sourcePath = Path.Combine(sourceDirectory, $"Static{index}.cs");
                    File.WriteAllText(
                        sourcePath,
                        $"public static class Static{index} {{ public const int Value = {index}; }}");
                    targets.Add(CodeIndex.Indexer.CSharpStaticInterfacePrepass.FileTarget.Create(
                        projectRoot,
                        sourcePath,
                        "csharp"));
                }

                ExtractorPluginRegistry.ReloadForTests();
                ExtractorPluginRegistry.UserPluginDirectoryForTesting = pluginDirectory;
                ExecutableExtensionBoundary.StagedForTesting = (source, _) =>
                {
                    if (string.Equals(source, pluginPath, StringComparison.Ordinal))
                        pluginStageCount++;
                };
                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
                Assert.Equal(1, pluginStageCount);

                var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                var writer = new DbWriter(db.Connection);
                var indexer = new CodeIndex.Indexer.FileIndexer(projectRoot, ignoreCase: false);

                var workspace = CodeIndex.Indexer.CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    writer,
                    indexer,
                    targets,
                    includeExistingSymbols: false,
                    parallelism: 4,
                    patternConfigsAlreadyLoaded: true);

                Assert.True(workspace.SourceContractEvidenceComplete);
                Assert.Equal(1, pluginStageCount);

                CodeIndex.Indexer.CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
                    writer,
                    indexer,
                    [targets[0]],
                    includeExistingSymbols: false);
                Assert.Equal(2, pluginStageCount);
            }
            finally
            {
                ExecutableExtensionBoundary.StagedForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void LoadPlugin_KillsTimedOutAndCrashedConstructorWorkers_Issue4598()
    {
        lock (TestConsoleLock.Gate)
        {
            using var slow = EnvironmentVariableScope.Capture(SlowPluginConstructorEnvironmentVariable);
            using var crash = EnvironmentVariableScope.Capture(CrashingPluginConstructorEnvironmentVariable);
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.WorkerOperationBudgetForTesting = TimeSpan.FromMilliseconds(500);
                slow.Set(SlowPluginConstructorEnvironmentVariable, "1");

                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);

                Assert.Contains(
                    ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!,
                    diagnostic => diagnostic.Category == "plugin_worker_timeout");
                Assert.Equal(0, ExtractorPluginRegistry.PluginWorkerCountForTests());

                slow.Set(SlowPluginConstructorEnvironmentVariable, null);
                crash.Set(CrashingPluginConstructorEnvironmentVariable, "1");
                ExtractorPluginRegistry.ResetForTests();
                crash.Set(CrashingPluginConstructorEnvironmentVariable, "1");
                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);

                Assert.Contains(
                    ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics!,
                    diagnostic => diagnostic.Category == "plugin_worker_exit");
                Assert.Equal(0, ExtractorPluginRegistry.PluginWorkerCountForTests());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void PluginWorker_EnforcesMemoryAndOutputBudgets_Issue4598()
    {
        lock (TestConsoleLock.Gate)
        {
            using var slow = EnvironmentVariableScope.Capture(SlowPluginConstructorEnvironmentVariable);
            try
            {
                slow.Set(SlowPluginConstructorEnvironmentVariable, "1");
                using (var memoryBounded = new ExtractorPluginWorkerClient(
                           Assembly.GetExecutingAssembly().Location,
                           ExtractorPluginRegistry.MaxExtensionAssemblyTypes,
                           operationBudget: TimeSpan.FromSeconds(5),
                           memoryLimitBytes: 1))
                {
                    var result = memoryBounded.LoadManifest();
                    Assert.False(result.Success);
                    Assert.Equal("plugin_worker_memory_limit", result.ErrorCategory);
                }

                slow.Set(SlowPluginConstructorEnvironmentVariable, null);
                using var outputBounded = new ExtractorPluginWorkerClient(
                    Assembly.GetExecutingAssembly().Location,
                    ExtractorPluginRegistry.MaxExtensionAssemblyTypes,
                    maxProtocolLineBytes: 256);
                var outputResult = outputBounded.LoadManifest();
                Assert.False(outputResult.Success);
                Assert.Equal("plugin_worker_output_limit", outputResult.ErrorCategory);
            }
            finally
            {
                ExtractorPluginWorkerClient.ProcessStartedForTesting = null;
            }
        }
    }

    [Fact]
    public void LoadPlugin_ReportsSanitizedMetadataCategoryBeforeAssemblyLoad_Issue4598()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_plugin_load_category");
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
                Assert.Equal("plugin_metadata_invalid", diagnostic.Category);
                Assert.Equal("broken.dll", diagnostic.Path);
                Assert.DoesNotContain(projectRoot, diagnostic.Path, StringComparison.Ordinal);
                Assert.Contains("Plugin metadata inspection failed", diagnostic.Message, StringComparison.Ordinal);
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

                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);

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
    public void LoadPlugin_UsesIsolatedWorkerWithoutParentLoadContext_Issue4598()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();

                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);

                var loaded = ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", out var extractor);
                Assert.True(
                    loaded,
                    string.Join(
                        Environment.NewLine,
                        ExtractorPluginRegistry.GetStatusSnapshot().Diagnostics?.Select(
                            diagnostic => $"{diagnostic.Category}: {diagnostic.Message}") ?? []));
                Assert.IsType<IsolatedPluginExtractorProxy>(extractor);
                var symbols = extractor.Extract(
                    42,
                    "fixture",
                    new ExtractionContext("collectibledsl", "fixture.collectible"));
                var workerSymbol = Assert.Single(symbols);
                Assert.Equal(42, workerSymbol.FileId);
                Assert.Equal("worker-symbol", workerSymbol.Name);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());
                var stagedMainAssembly = Assert.Single(ExtractorPluginRegistry.PluginStagedAssemblyPathsForTests());
                var stagedXunitDependency = Path.Combine(
                    Path.GetDirectoryName(stagedMainAssembly)!,
                    Path.GetFileName(typeof(Xunit.FactAttribute).Assembly.Location));
                Assert.True(File.Exists(stagedXunitDependency));
                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(0, status.RetainedLoadContextCount);
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

                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("dualroleplugindsl", out var symbolExtractor));
                Assert.True(ExtractorPluginRegistry.TryGetReferenceExtractor("dualroleplugindsl", out var referenceExtractor));
                Assert.Same(symbolExtractor, referenceExtractor);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot().RetainedLoadContextCount);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void ResetForTests_DisposesRetainedPluginWorkers_Issue4598()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());

                ExtractorPluginRegistry.ResetForTests();

                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(0, status.RetainedLoadContextCount);
                Assert.Equal(ExtractorPluginRegistry.PluginLoadContextLifecycle, status.LoadContextLifecycle);
                Assert.Equal(0, ExtractorPluginRegistry.PluginWorkerCountForTests());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void LoadPlugin_RepeatedFingerprintRetainsSingleWorker_Issue4598()
    {
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                var pluginPath = pluginAssemblyFixturePath;

                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);
                ExtractorPluginRegistry.LoadPluginForTests(pluginPath);

                var status = ExtractorPluginRegistry.GetStatusSnapshot();
                Assert.Equal(1, status.PluginAssemblyCount);
                Assert.Equal(0, status.RetainedLoadContextCount);
                Assert.Equal(1, ExtractorPluginRegistry.PluginWorkerCountForTests());
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void ReloadPatternConfigsForProjectRoot_ReloadsDeletedPluginAndPreservesRegisteredFallback_Issue4592()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_refresh_4592");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            var pluginPath = Path.Combine(projectRoot, ".cdidx", "plugins", "CodeIndex.Tests.dll");
            try
            {
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                ExtractorPluginRegistry.ReloadForTests();
                var registeredFallback = new CollectiblePluginSymbolExtractor();
                ExtractorPluginRegistry.Register(registeredFallback);
                Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
                CopyPluginFixture(pluginPath);

                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(projectRoot);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", projectRoot, out var resolvedExtractor));
                Assert.Same(registeredFallback, resolvedExtractor);
                Assert.Equal(1, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(projectRoot));
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).RetainedLoadContextCount);

                File.Delete(pluginPath);
                Assert.False(File.Exists(pluginPath));
                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(projectRoot);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", projectRoot, out var restoredFallback));
                Assert.Same(registeredFallback, restoredFallback);
                Assert.Equal(0, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(projectRoot));
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).RetainedLoadContextCount);
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(projectRoot).PluginAssemblyCount);
            }
            finally
            {
                ExtractorPluginRegistry.ReloadForTests();
                TestProjectHelper.DeleteDirectory(projectRoot);
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

                ExtractorPluginRegistry.LoadPluginForTests(pluginAssemblyFixturePath);
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
    public void PatternRuleBudget_ReservesCapacityForHigherPrecedenceUserPatterns_Issue4595()
    {
        var workspace = TestProjectHelper.CreateTempProject("extractor_registry_pattern_precedence_workspace_4595");
        var userPatterns = TestProjectHelper.CreateTempProject("extractor_registry_pattern_precedence_user_4595");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests = userPatterns;
                var workspaceRules = string.Join(
                    "\n",
                    Enumerable.Range(0, ExtractorPluginRegistry.MaxPatternRulesTotal)
                        .Select(i => $"  - kind: \"class\"\n    regex: \"^workspace{i} (?<name>\\\\w+)\""));
                WritePatternConfig(
                    workspace,
                    "workspace.yaml",
                    $"language: \"workspacedsl\"\nextensions:\n  - extension: \".workspace\"\npatterns:\n{workspaceRules}\n");
                File.WriteAllText(
                    Path.Combine(userPatterns, "user.yaml"),
                    "language: \"userdsl\"\nextensions:\n  - extension: \".user\"\npatterns:\n  - kind: \"class\"\n    regex: \"^user (?<name>\\\\w+)\"\n");

                ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(workspace);

                Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("userdsl", workspace, out var userExtractor));
                Assert.Single(Assert.IsType<ConfiguredSymbolExtractor>(userExtractor).PatternsForTests);
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("workspacedsl", workspace, out _));
                var status = ExtractorPluginRegistry.GetStatusSnapshot(workspace);
                var config = Assert.Single(status.PatternConfigs!);
                Assert.Equal("user", config.Source);
                Assert.Contains(status.Diagnostics!, diagnostic =>
                    diagnostic.Category == "invalid_pattern_config"
                    && diagnostic.Message.Contains("too many pattern rules", StringComparison.Ordinal));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspace);
                TestProjectHelper.DeleteDirectory(userPatterns);
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
    public void WorkspacePluginWorkers_AreOwnedByTheirImmutableSnapshots_Issue4602()
    {
        var workspaceA = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_plugin_snapshot_a_4602");
        var workspaceB = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_plugin_snapshot_b_4602");
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
                CopyPluginFixture(pluginA);
                CopyPluginFixture(pluginB);

                AssertWorkspacePluginWorkersAreOwnedByImmutableSnapshots(workspaceA, workspaceB);
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
    public void WorkspaceSnapshots_EvictLeastRecentlyUsedPluginWorkersAtBound_Issue4602()
    {
        var root = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_snapshot_lru_4602");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                var firstWorkspace = Path.Combine(root, "workspace-0");
                var pluginPath = Path.Combine(firstWorkspace, ".cdidx", "plugins", "snapshot-plugin.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
                CopyPluginFixture(pluginPath);
                AssertLeastRecentlyUsedPluginWorkerIsEvicted(root, firstWorkspace);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(root);
            }
        }
    }

    [Fact]
    public void WorkspacePluginLoad_CannotCommitAfterSnapshotReplacement_Issue4602()
    {
        var workspace = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_snapshot_reload_race_4602");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            using var loaded = new ManualResetEventSlim();
            using var allowCommit = new ManualResetEventSlim();
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                var pluginPath = Path.Combine(workspace, ".cdidx", "plugins", "snapshot-plugin.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
                CopyPluginFixture(pluginPath);
                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = () =>
                {
                    loaded.Set();
                    allowCommit.Wait();
                };

                var loading = new Thread(() => ExtractorPluginRegistry.LoadPluginsForProjectRoot(workspace));
                loading.Start();
                Assert.True(loaded.Wait(TimeSpan.FromSeconds(10)));
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "0");
                ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspace);
                allowCommit.Set();
                Assert.True(loading.Join(TimeSpan.FromSeconds(10)));

                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(workspace).RetainedLoadContextCount);
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspace, out _));
            }
            finally
            {
                allowCommit.Set();
                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspace);
            }
        }
    }

    [Fact]
    public void WorkspaceReload_CannotCommitAfterSnapshotRelease_Issue4602()
    {
        var workspace = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_snapshot_release_race_4602");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            using var loaded = new ManualResetEventSlim();
            using var allowCommit = new ManualResetEventSlim();
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                var pluginPath = Path.Combine(workspace, ".cdidx", "plugins", "snapshot-plugin.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
                CopyPluginFixture(pluginPath);
                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = () =>
                {
                    loaded.Set();
                    allowCommit.Wait();
                };

                var generation = ExtractorPluginRegistry.WorkspaceGenerationForTests();
                var reloading = new Thread(() => ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspace));
                reloading.Start();
                Assert.True(loaded.Wait(TimeSpan.FromSeconds(10)));

                var releasing = new Thread(ExtractorPluginRegistry.ReleaseWorkspaceSnapshots);
                releasing.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => ExtractorPluginRegistry.WorkspaceGenerationForTests() > generation,
                    TimeSpan.FromSeconds(10)));
                allowCommit.Set();

                Assert.True(reloading.Join(TimeSpan.FromSeconds(10)));
                Assert.True(releasing.Join(TimeSpan.FromSeconds(10)));
                Assert.Equal(0, ExtractorPluginRegistry.WorkspaceSnapshotCountForTests());
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(workspace).RetainedLoadContextCount);
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspace, out _));
            }
            finally
            {
                allowCommit.Set();
                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspace);
            }
        }
    }

    [Fact]
    public void WorkspaceReload_OlderGenerationCannotOverwriteNewerReload_Issue4602()
    {
        var workspace = TestProjectHelper.CreateExecutableExtensionTestProject("extractor_registry_snapshot_reload_generation_4602");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            using var loaded = new ManualResetEventSlim();
            using var allowCommit = new ManualResetEventSlim();
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                var pluginPath = Path.Combine(workspace, ".cdidx", "plugins", "snapshot-plugin.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
                CopyPluginFixture(pluginPath);
                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = () =>
                {
                    loaded.Set();
                    allowCommit.Wait();
                };

                var olderReload = new Thread(() => ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspace));
                olderReload.Start();
                Assert.True(loaded.Wait(TimeSpan.FromSeconds(10)));
                var olderSequence = ExtractorPluginRegistry.WorkspaceReloadSequenceForTests();

                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = null;
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "0");
                var newerReload = new Thread(() => ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspace));
                newerReload.Start();
                Assert.True(SpinWait.SpinUntil(
                    () => ExtractorPluginRegistry.WorkspaceReloadSequenceForTests() > olderSequence,
                    TimeSpan.FromSeconds(10)));
                allowCommit.Set();

                Assert.True(olderReload.Join(TimeSpan.FromSeconds(10)));
                Assert.True(newerReload.Join(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, ExtractorPluginRegistry.WorkspaceSnapshotCountForTests());
                Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(workspace).RetainedLoadContextCount);
                Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspace, out _));
            }
            finally
            {
                allowCommit.Set();
                ExtractorPluginRegistry.WorkspacePluginLoadedBeforeCommitForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspace);
            }
        }
    }

    [Fact]
    public void WorkspaceReferenceLanguages_AreResolvedFromTheActiveSnapshot_Issue4602()
    {
        var workspaceA = TestProjectHelper.CreateTempProject("extractor_registry_reference_languages_a_4602");
        var workspaceB = TestProjectHelper.CreateTempProject("extractor_registry_reference_languages_b_4602");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(
                    workspaceA,
                    new SnapshotReferenceExtractor("referencea", "reference-a"));
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(
                    workspaceB,
                    new SnapshotReferenceExtractor("referenceb", "reference-b"));

                Assert.Contains("referencea", CodeIndex.Indexer.ReferenceExtractor.GetSupportedLanguages(workspaceA));
                Assert.DoesNotContain("referenceb", CodeIndex.Indexer.ReferenceExtractor.GetSupportedLanguages(workspaceA));
                Assert.Contains("referenceb", CodeIndex.Indexer.ReferenceExtractor.GetSupportedLanguages(workspaceB));
                Assert.DoesNotContain("referencea", CodeIndex.Indexer.ReferenceExtractor.GetSupportedLanguages(workspaceB));
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
    public void DbReader_ExplicitLanguageSupportUsesIndexedWorkspaceSnapshot_Issue4602()
    {
        var workspace = TestProjectHelper.CreateTempProject("extractor_registry_db_reference_language_4602");
        lock (TestConsoleLock.Gate)
        {
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                ExtractorPluginRegistry.RegisterForWorkspaceForTests(
                    workspace,
                    new SnapshotReferenceExtractor("workspacereference", "workspace-reference"));
                var dbPath = TestProjectHelper.CreateProjectDb(workspace);
                using (var writeDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                {
                    var writer = new DbWriter(writeDb.Connection);
                    var fileId = writer.UpsertFile(new FileRecord
                    {
                        Path = "sample.shared",
                        Lang = "workspacereference",
                        Lines = 1,
                        Modified = new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc),
                    });
                    writer.InsertSymbols([
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "function",
                            Name = "WorkspaceTarget",
                            Line = 1,
                            StartLine = 1,
                            EndLine = 1,
                        },
                    ]);
                    writer.MarkGraphReady();
                }

                using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
                db.TryMigrateForRead();
                using var reader = new DbReader(db.Connection, db.IsReadOnly);

                Assert.False(CodeIndex.Indexer.ReferenceExtractor.SupportsLanguage("workspacereference"));
                Assert.True(reader.SupportsReferenceLanguage("workspacereference"));
                Assert.True(reader.SupportsSymbolGraph("workspacereference", "function", null));
                Assert.Single(reader.GetUnusedSymbols(10, null, "workspacereference", null, null, false));
                Assert.Equal(1, reader.CountUnusedSymbols(null, "workspacereference", null, null, false).Count);
                Assert.True(reader.AnalyzeSymbol("WorkspaceTarget", lang: "workspacereference", exact: true).GraphSupported);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                TestProjectHelper.DeleteDirectory(workspace);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertWorkspacePluginWorkersAreOwnedByImmutableSnapshots(
        string workspaceA,
        string workspaceB)
    {
        ExtractorPluginRegistry.LoadPluginsForProjectRoot(workspaceA);
        ExtractorPluginRegistry.LoadPluginsForProjectRoot(workspaceB);
        var loadedA = ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspaceA, out var extractorA);
        Assert.True(
            loadedA,
            string.Join(
                Environment.NewLine,
                ExtractorPluginRegistry.GetStatusSnapshot(workspaceA).Diagnostics?.Select(
                    diagnostic => $"{diagnostic.Category}: {diagnostic.Message}") ?? []));
        Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspaceB, out var extractorB));
        Assert.NotSame(extractorA, extractorB);
        Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot(workspaceA).PluginAssemblyCount);
        Assert.Equal(1, ExtractorPluginRegistry.GetStatusSnapshot(workspaceB).PluginAssemblyCount);
        Assert.Equal(1, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(workspaceA));
        Assert.Equal(1, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(workspaceB));
        Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(workspaceA).RetainedLoadContextCount);
        Assert.Equal(0, ExtractorPluginRegistry.GetStatusSnapshot(workspaceB).RetainedLoadContextCount);

        ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(workspaceA);

        Assert.True(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", workspaceB, out var unchangedB));
        Assert.Same(extractorB, unchangedB);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertLeastRecentlyUsedPluginWorkerIsEvicted(
        string root,
        string firstWorkspace)
    {
        ExtractorPluginRegistry.LoadPluginsForProjectRoot(firstWorkspace);
        Assert.Equal(1, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(firstWorkspace));

        for (var i = 1; i <= ExtractorPluginRegistry.MaxRetainedWorkspaceSnapshots; i++)
        {
            var workspace = Path.Combine(root, $"workspace-{i}");
            ExtractorPluginRegistry.RegisterForWorkspaceForTests(
                workspace,
                new SnapshotSymbolExtractor($"lru{i}", $"workspace-{i}"));
        }

        Assert.Equal(
            ExtractorPluginRegistry.MaxRetainedWorkspaceSnapshots,
            ExtractorPluginRegistry.WorkspaceSnapshotCountForTests());
        Assert.Equal(0, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(firstWorkspace));
        Assert.False(ExtractorPluginRegistry.TryGetSymbolExtractor("collectibledsl", firstWorkspace, out _));
    }

    private static void WritePatternConfig(string projectRoot, string fileName, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void CopyPluginFixture(string pluginPath)
        => TestProjectHelper.CopyAssemblyFixtureWithDependencies(Assembly.GetExecutingAssembly().Location, pluginPath);

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
        => [new SymbolRecord { FileId = fileId, Kind = "class", Name = "worker-symbol", Line = 1 }];
}

public sealed class SlowPluginSymbolExtractor : ISymbolExtractor
{
    public SlowPluginSymbolExtractor()
    {
        if (Environment.GetEnvironmentVariable(ExtractorPluginRegistryTests.SlowPluginConstructorEnvironmentVariable) == "1")
            Thread.Sleep(TimeSpan.FromSeconds(30));
    }

    public string Language => "slowplugindsl";

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context) => [];
}

public sealed class CrashingPluginSymbolExtractor : ISymbolExtractor
{
    public CrashingPluginSymbolExtractor()
    {
        if (Environment.GetEnvironmentVariable(ExtractorPluginRegistryTests.CrashingPluginConstructorEnvironmentVariable) == "1")
            Environment.FailFast("plugin worker crash fixture");
    }

    public string Language => "crashingplugindsl";

    public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context) => [];
}

public sealed class CollectiblePluginReferenceExtractor : IReferenceExtractor
{
    public string Language => "collectibledsl";

    public IReadOnlyCollection<string> FileExtensions => [".collectible"];

    public IReadOnlyList<ReferenceRecord> Extract(long fileId, string source, ExtractionContext context)
        =>
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "WorkspacePluginTarget",
                ReferenceKind = "call",
                Line = 1,
                Column = 1,
                Context = source,
            },
        ];
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
