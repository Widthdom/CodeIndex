using System.Diagnostics;
using System.Runtime.InteropServices;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void EnumeratePluginAssemblyPaths_UsesExplicitProjectRootForWorkspacePlugins()
    {
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            var projectRoot = Path.Combine(Path.GetTempPath(), $"cdidx_workspace_plugins_project_{Guid.NewGuid():N}");
            var cwdRoot = Path.Combine(Path.GetTempPath(), $"cdidx_workspace_plugins_cwd_{Guid.NewGuid():N}");
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                var projectPluginDir = Path.Combine(projectRoot, ".cdidx", "plugins");
                var cwdPluginDir = Path.Combine(cwdRoot, ".cdidx", "plugins");
                Directory.CreateDirectory(projectPluginDir);
                Directory.CreateDirectory(cwdPluginDir);
                var projectPluginFileName = $"project_{Guid.NewGuid():N}.dll";
                var cwdPluginFileName = $"cwd_{Guid.NewGuid():N}.dll";
                File.WriteAllText(Path.Combine(projectPluginDir, projectPluginFileName), "not a real dll");
                File.WriteAllText(Path.Combine(cwdPluginDir, cwdPluginFileName), "not a real dll");
                Environment.CurrentDirectory = cwdRoot;
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, null);

                var untrustedPaths = ExtractorPluginRegistry.EnumeratePluginAssemblyPathsForTests(projectRoot);

                Assert.DoesNotContain(untrustedPaths, path => Path.GetFileName(path) == projectPluginFileName);

                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");

                var trustedPaths = ExtractorPluginRegistry.EnumeratePluginAssemblyPathsForTests(projectRoot);
                var defaultPaths = ExtractorPluginRegistry.EnumeratePluginAssemblyPathsForTests();

                Assert.Contains(trustedPaths, path => Path.GetFileName(path) == projectPluginFileName);
                Assert.DoesNotContain(trustedPaths, path => Path.GetFileName(path) == cwdPluginFileName);
                Assert.DoesNotContain(defaultPaths, path => Path.GetFileName(path) == cwdPluginFileName);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                if (Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, recursive: true);
                if (Directory.Exists(cwdRoot))
                    Directory.Delete(cwdRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsInvalidRegexWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_invalid_{Guid.NewGuid():N}");
            try
            {
                WritePatternConfig(
                    tempDir,
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(?<name>\"\n");
                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern config", stderr, StringComparison.Ordinal);
                Assert.Contains("invalid regex", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsOversizeConfigWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_large_{Guid.NewGuid():N}");
            try
            {
                WritePatternConfig(tempDir, new string('x', ExtractorPluginRegistry.MaxPatternConfigBytes + 1));
                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern config", stderr, StringComparison.Ordinal);
                Assert.Contains("file is too large", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsSymlinkedConfigWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_symlink_{Guid.NewGuid():N}");
            try
            {
                var patternDir = Path.Combine(tempDir, ".cdidx", "patterns");
                Directory.CreateDirectory(patternDir);
                var targetPath = Path.Combine(tempDir, "target.yaml");
                File.WriteAllText(
                    targetPath,
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");
                var linkPath = Path.Combine(patternDir, "toydsl.yaml");
                try
                {
                    File.CreateSymbolicLink(linkPath, targetPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    return;
                }

                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern config", stderr, StringComparison.Ordinal);
                Assert.Contains("symbolic links", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsFifoConfigWithDiagnostic()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_fifo_{Guid.NewGuid():N}");
            try
            {
                var patternDir = Path.Combine(tempDir, ".cdidx", "patterns");
                Directory.CreateDirectory(patternDir);
                var fifoPath = Path.Combine(patternDir, "toydsl.yaml");
                try
                {
                    if (Mkfifo(fifoPath, 0x180) != 0)
                        return;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    return;
                }

                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern config", stderr, StringComparison.Ordinal);
                Assert.Contains("not a regular file", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsSymlinkedPatternDirectoryWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_dir_symlink_{Guid.NewGuid():N}");
            try
            {
                var externalPatternDir = Path.Combine(tempDir, "external-patterns");
                Directory.CreateDirectory(externalPatternDir);
                File.WriteAllText(
                    Path.Combine(externalPatternDir, "toydsl.yaml"),
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");

                var cdidxDir = Path.Combine(tempDir, ".cdidx");
                Directory.CreateDirectory(cdidxDir);
                var patternDir = Path.Combine(cdidxDir, "patterns");
                try
                {
                    Directory.CreateSymbolicLink(patternDir, externalPatternDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    return;
                }

                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern directory", stderr, StringComparison.Ordinal);
                Assert.Contains("symbolic links", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsSymlinkedCdidxPatternParentWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_parent_symlink_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var externalCdidxDir = Path.Combine(tempDir, "external-cdidx");
                var externalPatternDir = Path.Combine(externalCdidxDir, "patterns");
                Directory.CreateDirectory(externalPatternDir);
                File.WriteAllText(
                    Path.Combine(externalPatternDir, "toydsl.yaml"),
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");

                var cdidxDir = Path.Combine(tempDir, ".cdidx");
                try
                {
                    Directory.CreateSymbolicLink(cdidxDir, externalCdidxDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    return;
                }

                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern directory", stderr, StringComparison.Ordinal);
                Assert.Contains("symbolic links", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsExcessPatternCountWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_count_{Guid.NewGuid():N}");
            try
            {
                var rules = string.Join(
                    "\n",
                    Enumerable.Range(0, ExtractorPluginRegistry.MaxPatternRulesPerConfig + 1)
                        .Select(i => $"  - kind: \"class{i}\"\n    regex: \"^entity{i} (?<name>\\\\w+)\""));
                WritePatternConfig(
                    tempDir,
                    $"language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n{rules}\n");
                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entity0 Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern config", stderr, StringComparison.Ordinal);
                Assert.Contains("too many pattern rules", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_RejectsAggregatePatternCountWithDiagnostic()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_total_count_{Guid.NewGuid():N}");
            try
            {
                var rules = string.Join(
                    "\n",
                    Enumerable.Range(0, ExtractorPluginRegistry.MaxPatternRulesTotal)
                        .Select(i => $"  - kind: \"class{i}\"\n    regex: \"^entity{i} (?<name>\\\\w+)\""));
                WritePatternConfig(
                    tempDir,
                    "first.yaml",
                    $"language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n{rules}\n");
                WritePatternConfig(
                    tempDir,
                    "second.yaml",
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"overflow\"\n    regex: \"^never (?<name>\\\\w+)\"\n  - kind: \"overflow2\"\n    regex: \"^alsoNever (?<name>\\\\w+)\"\n");
                ExtractorPluginRegistry.ReloadForTests();

                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    var symbols = SymbolExtractor.Extract(2, "toydsl", "entityOverflow Widget", "demo.toy", tempDir);
                    Assert.Empty(symbols);
                });

                Assert.Contains("Skipped pattern config", stderr, StringComparison.Ordinal);
                Assert.Contains("too many pattern rules", stderr, StringComparison.Ordinal);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_DisablesPatternAfterRegexTimeout()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_timeout_{Guid.NewGuid():N}");
            try
            {
                WritePatternConfig(
                    tempDir,
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^(a+)+$\"\n");
                ExtractorPluginRegistry.ReloadForTests();
                const int extractionCount = 25;
                var slowLine = new string('a', 10_000) + "!";

                var stopwatch = Stopwatch.StartNew();
                var stderr = ConsoleCapture.CaptureError(() =>
                {
                    for (var i = 0; i < extractionCount; i++)
                    {
                        var symbols = SymbolExtractor.Extract(2, "toydsl", slowLine, $"demo{i}.toy", tempDir);
                        Assert.Empty(symbols);
                    }
                });
                stopwatch.Stop();

                var timeoutDiagnostics = stderr
                    .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("timed out", StringComparison.Ordinal))
                    .ToArray();
                Assert.Single(timeoutDiagnostics);
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                    $"Expected timed-out pattern to be disabled after the first extraction timeout, elapsed {stopwatch.Elapsed}.");
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_LinuxStatModeOffsetsCoverReleaseArchitectures()
    {
        Assert.Equal(24, ExtractorPluginRegistry.LinuxStatModeOffsetForTests(Architecture.X64));
        Assert.Equal(16, ExtractorPluginRegistry.LinuxStatModeOffsetForTests(Architecture.Arm64));
    }

    private static void WritePatternConfig(string projectRoot, string content)
        => WritePatternConfig(projectRoot, "toydsl.yaml", content);

    private static void WritePatternConfig(string projectRoot, string fileName, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string path, uint mode);
}
