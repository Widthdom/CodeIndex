using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodeIndex.Database;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for FileIndexer.
/// FileIndexerのテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public partial class FileIndexerTests
{
    [Theory]
    [InlineData(null, 256)]
    [InlineData(1, 256)]
    [InlineData(50_000, 50_000)]
    [InlineData(int.MaxValue, FileIndexer.MaxInitialScanFileCapacity)]
    public void ResolveInitialScanFileCapacity_BoundsHints(int? hint, int expected)
    {
        Assert.Equal(expected, FileIndexer.ResolveInitialScanFileCapacity(hint));
    }

    [Theory]
    [InlineData(256, 64)]
    [InlineData(8_000, 1_000)]
    [InlineData(int.MaxValue, FileIndexer.MaxInitialScanDirectoryCapacity)]
    public void ResolveInitialScanDirectoryCapacity_BoundsEstimatedDirectories(int hint, int expected)
    {
        Assert.Equal(expected, FileIndexer.ResolveInitialScanDirectoryCapacity(hint));
    }

    [Fact]
    public void NormalizeIgnorePath_PosixPreservesLiteralBackslash()
    {
        var normalized = FileIndexer.NormalizeIgnorePath(@"weird\name.py/");

        if (OperatingSystem.IsWindows())
            Assert.Equal("weird/name.py", normalized);
        else
            Assert.Equal(@"weird\name.py", normalized);
    }

    [Fact]
    public void NormalizeIgnorePath_AlreadyNormalizedPathUsesFastPath()
    {
        var path = "src/CodeIndex/Indexer";

        var normalized = FileIndexer.NormalizeIgnorePath(path);

        Assert.Same(path, normalized);
    }

    [Fact]
    public void NormalizeIgnorePath_TrimsTrailingSlashes()
    {
        Assert.Equal("src/CodeIndex", FileIndexer.NormalizeIgnorePath("src/CodeIndex///"));
    }

    [Fact]
    public void NormalizeIndexPath_AsciiForwardSlashPathUsesFastPath()
    {
        var path = "src/CodeIndex/Indexer/FileIndexer.cs";

        var normalized = FileIndexer.NormalizeIndexPath(path);

        Assert.Same(path, normalized);
    }

    [Fact]
    public void NormalizeIndexPath_NormalizesUnicodeToNfc()
    {
        var normalized = FileIndexer.NormalizeIndexPath("Cafe\u0301.cs");

        Assert.Equal("Caf\u00e9.cs", normalized);
        Assert.True(normalized.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void NormalizeIndexPath_UsesPlatformSeparatorSemantics()
    {
        var normalized = FileIndexer.NormalizeIndexPath(@"nested\file.cs");

        if (OperatingSystem.IsWindows())
            Assert.Equal("nested/file.cs", normalized);
        else
            Assert.Equal(@"nested\file.cs", normalized);
    }

    [Fact]
    public void EvaluatePathFilter_InternalSymlinkRejectsCaseOnlyExternalSibling_Issue5091()
    {
        lock (PathCasingTestLock.Gate)
        {
            using var parent = TestProjectHelper.CreateTempProjectScope("cdidx-case-prefix-containment");
            var parentRoot = Path.GetFullPath(parent.Root);
            var projectRoot = Path.Combine(parentRoot, "Project");
            var externalRoot = Path.Combine(parentRoot, "project");
            Directory.CreateDirectory(projectRoot);
            if (Directory.Exists(externalRoot))
                return;

            Directory.CreateDirectory(externalRoot);
            var externalPath = Path.Combine(externalRoot, "outside.cs");
            File.WriteAllText(externalPath, "public class Outside5091 { }\n");
            var selectedPath = Path.Combine(projectRoot, "link.cs");
            try
            {
                File.CreateSymbolicLink(selectedPath, externalPath);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException)
            {
                return;
            }

            var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
            PathCasing.ResetCacheForTests();
            PathCasing.IgnoreCaseProbeForTesting = path =>
                !string.Equals(Path.GetFullPath(path), parentRoot, StringComparison.Ordinal);
            try
            {
                var indexer = new FileIndexer(
                    projectRoot,
                    ignoreCase: true,
                    ignoreRuleRoot: projectRoot,
                    maxFileSizeBytes: null,
                    directoryIgnoreCaseProbe: _ => true,
                    symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

                var result = indexer.EvaluatePathFilter(selectedPath);

                Assert.True(result.ShouldSkip);
                Assert.Equal(FileIndexer.PathFilterKind.OutsideProjectRoot, result.FilterKind);
            }
            finally
            {
                PathCasing.IgnoreCaseProbeForTesting = previousProbe;
                PathCasing.ResetCacheForTests();
            }
        }
    }

    [Fact]
    public void ScanFilesDetailed_CancelledToken_ThrowsBeforeEnumeration()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-cancel-scan");
        var tempDir = project.Root;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new FileIndexer(tempDir).ScanFilesDetailed(cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ScanFilesDetailed_DanglingFileSystemEntryScanCapsCandidatesWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-dangling-cap");
        var tempDir = project.Root;
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(tempDir, $"file{i}.cs"), $"public class C{i} {{ }}\n");

        var result = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            maxDanglingFileSystemEntryScanCandidates: 3).ScanFilesDetailed();

        Assert.Equal(5, result.Files.Count);
        var warning = Assert.Single(
            result.Errors,
            error => error.Message.Contains("Dangling filesystem entry scan truncated", StringComparison.Ordinal));
        Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("Dangling filesystem entry scan truncated after 3 candidate", warning.Message);
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void Constructor_CaseProbeAvoidsRootProbeArtifacts_Issue3174()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-probe-indexer");
        var tempDir = project.Root;

        _ = new FileIndexer(tempDir);

        Assert.Empty(Directory.GetFiles(tempDir, ".cdidx_case_probe_*", SearchOption.TopDirectoryOnly));
        Assert.False(Directory.Exists(Path.Combine(tempDir, CaseSensitivityProbeDirectory.DataDirectoryName)));
    }

    [Fact]
    public void Constructor_CaseProbePreservesExistingCdidxDirectory_Issue3174()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-probe-existing");
        var tempDir = project.Root;
        var dataDirectory = Path.Combine(tempDir, CaseSensitivityProbeDirectory.DataDirectoryName);
        Directory.CreateDirectory(dataDirectory);

        _ = new FileIndexer(tempDir);

        Assert.True(Directory.Exists(dataDirectory));
        Assert.False(Directory.Exists(Path.Combine(dataDirectory, CaseSensitivityProbeDirectory.ProbeDirectoryName)));
    }

    [Fact]
    public void Constructor_CaseProbeFailureThrowsInsteadOfOsFallback_Issue3439()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-probe-failure");
        var tempDir = project.Root;
        var previousProbe = FileIndexer.FileSystemIgnoreCaseProbeForTesting;
        FileIndexer.FileSystemIgnoreCaseProbeForTesting = _ => throw new IOException("probe blocked");
        try
        {
            var ex = Assert.Throws<CaseSensitivityProbeException>(() => new FileIndexer(tempDir));

            Assert.Equal(Path.GetFullPath(tempDir), ex.ProjectRoot);
            Assert.IsType<IOException>(ex.InnerException);
        }
        finally
        {
            FileIndexer.FileSystemIgnoreCaseProbeForTesting = previousProbe;
        }
    }

    [Fact]
    public void CaseSensitivityProbeDirectory_CleanupFailureDowngradesToDiagnostic_Issue3828()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-probe-cleanup");
        var tempDir = project.Root;
        var previousDelete = CaseSensitivityProbeDirectory.DeleteCreatedEmptyDirectoryForTesting;
        var previousSink = CaseSensitivityProbeDirectory.CleanupDiagnosticSinkForTesting;
        var diagnostics = new List<CaseSensitivityProbeCleanupDiagnostic>();
        try
        {
            var scope = CaseSensitivityProbeDirectory.CreateProbePathScope(tempDir, "case-probe-test-");
            CaseSensitivityProbeDirectory.DeleteCreatedEmptyDirectoryForTesting = path => throw new IOException($"blocked: {path}");
            CaseSensitivityProbeDirectory.CleanupDiagnosticSinkForTesting = diagnostics.Add;

            scope.Dispose();

            Assert.Contains(scope.CleanupDiagnostics, diagnostic =>
                diagnostic.RelativePath == $"{CaseSensitivityProbeDirectory.DataDirectoryName}/{CaseSensitivityProbeDirectory.ProbeDirectoryName}");
            Assert.Contains(diagnostics, diagnostic =>
                diagnostic.RelativePath == $"{CaseSensitivityProbeDirectory.DataDirectoryName}/{CaseSensitivityProbeDirectory.ProbeDirectoryName}");
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.RelativePath.Contains(tempDir, StringComparison.Ordinal));
        }
        finally
        {
            CaseSensitivityProbeDirectory.DeleteCreatedEmptyDirectoryForTesting = previousDelete;
            CaseSensitivityProbeDirectory.CleanupDiagnosticSinkForTesting = previousSink;
        }
    }

    [Fact]
    public void CaseSensitivityProbeDirectory_CleanupRejectsReplacedProbeSymlink_Issue4131()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-probe-boundary");
        using var external = TestProjectHelper.CreateTempProjectScope("cdidx-case-probe-external");
        var tempDir = project.Root;
        var externalDir = external.Root;
        var probeDirectory = Path.Combine(
            tempDir,
            CaseSensitivityProbeDirectory.DataDirectoryName,
            CaseSensitivityProbeDirectory.ProbeDirectoryName);
        var previousSink = CaseSensitivityProbeDirectory.CleanupDiagnosticSinkForTesting;
        var diagnostics = new List<CaseSensitivityProbeCleanupDiagnostic>();
        try
        {
            var scope = CaseSensitivityProbeDirectory.CreateProbePathScope(tempDir, "case-probe-test-");
            Directory.Delete(probeDirectory);
            Directory.CreateSymbolicLink(probeDirectory, externalDir);
            CaseSensitivityProbeDirectory.CleanupDiagnosticSinkForTesting = diagnostics.Add;

            scope.Dispose();

            Assert.True(Directory.Exists(externalDir));
            Assert.True(Directory.Exists(probeDirectory));
            Assert.Contains(diagnostics, diagnostic =>
                diagnostic.RelativePath == $"{CaseSensitivityProbeDirectory.DataDirectoryName}/{CaseSensitivityProbeDirectory.ProbeDirectoryName}"
                && diagnostic.ExceptionType == "CleanupTargetRejected");
        }
        finally
        {
            CaseSensitivityProbeDirectory.CleanupDiagnosticSinkForTesting = previousSink;
            DeleteDirectorySymlinkIfPresent(probeDirectory);
        }
    }

    [Fact]
    public void ScanFilesDetailed_DefaultCaseProbeSharesOneEntrySnapshotPerDirectory()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-entry-snapshot");
        var tempDir = project.Root;
        var childDir = Directory.CreateDirectory(Path.Combine(tempDir, "src")).FullName;
        TestProjectHelper.WriteTextFile(tempDir, "root.cs", "class RootSnapshotFixture { }\n");
        TestProjectHelper.WriteTextFile(tempDir, "src/child.cs", "class ChildSnapshotFixture { }\n");
        var enumerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        IEnumerable<string> EnumerateEntries(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            enumerationCounts[fullPath] = enumerationCounts.GetValueOrDefault(fullPath) + 1;
            return Directory.EnumerateFileSystemEntries(directory);
        }

        var result = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            enumerateFileSystemEntries: EnumerateEntries).ScanFilesDetailed();

        Assert.Equal(["root.cs", "src/child.cs"], ToSortedRelativePaths(tempDir, result.Files));
        Assert.Equal(2, enumerationCounts.Count);
        Assert.Equal(1, enumerationCounts[Path.GetFullPath(tempDir)]);
        Assert.Equal(1, enumerationCounts[Path.GetFullPath(childDir)]);
    }

    [Fact]
    public void ScanFilesDetailed_DefaultCaseProbeUsesTheInjectedEntrySnapshot()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-entry-snapshot");
        var tempDir = project.Root;
        var existingVariant = TestProjectHelper.WriteTextFile(tempDir, "probeA", "probe\n");
        var sourceFile = TestProjectHelper.WriteTextFile(tempDir, "source.cs", "class SnapshotCaseProbeFixture { }\n");
        var snapshotSpelling = Path.Combine(tempDir, "probea");

        var result = new FileIndexer(
            tempDir,
            ignoreCase: true,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            enumerateFileSystemEntries: _ => [snapshotSpelling, sourceFile]).ScanFilesDetailed();

        Assert.Equal(["source.cs"], ToSortedRelativePaths(tempDir, result.Files));
        Assert.DoesNotContain(result.Errors, error =>
            error.Path == string.Empty
            && error.Message.Contains("case-sensitivity differs", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(existingVariant));
    }

    [Fact]
    public void ScanFilesDetailed_CustomDirectoryCaseProbeRemainsAuthoritativeAndRunsOncePerDirectory()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-custom-case-probe");
        var tempDir = project.Root;
        var childDir = Directory.CreateDirectory(Path.Combine(tempDir, "src")).FullName;
        TestProjectHelper.WriteTextFile(tempDir, "root.cs", "class RootCustomProbeFixture { }\n");
        TestProjectHelper.WriteTextFile(tempDir, "src/child.cs", "class ChildCustomProbeFixture { }\n");
        var probeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        bool? ProbeDirectory(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            probeCounts[fullPath] = probeCounts.GetValueOrDefault(fullPath) + 1;
            return false;
        }

        var result = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: ProbeDirectory).ScanFilesDetailed();

        Assert.Equal(["root.cs", "src/child.cs"], ToSortedRelativePaths(tempDir, result.Files));
        Assert.Equal(2, probeCounts.Count);
        Assert.Equal(1, probeCounts[Path.GetFullPath(tempDir)]);
        Assert.Equal(1, probeCounts[Path.GetFullPath(childDir)]);
        Assert.DoesNotContain(result.Errors, error =>
            error.Message.Contains("case-sensitivity differs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ScanFilesDetailed_EntrySnapshotEnumerationFailureReportsOneErrorWithoutCaseWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-entry-snapshot-failure");
        var tempDir = project.Root;
        var blockedDir = Directory.CreateDirectory(Path.Combine(tempDir, "blocked")).FullName;
        TestProjectHelper.WriteTextFile(tempDir, "root.cs", "class SnapshotFailureFixture { }\n");
        var enumerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var probeCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        IEnumerable<string> EnumerateEntries(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            enumerationCounts[fullPath] = enumerationCounts.GetValueOrDefault(fullPath) + 1;
            if (fullPath == Path.GetFullPath(blockedDir))
                throw new UnauthorizedAccessException("blocked snapshot");
            return Directory.EnumerateFileSystemEntries(directory);
        }

        bool? ProbeDirectory(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            probeCounts[fullPath] = probeCounts.GetValueOrDefault(fullPath) + 1;
            return false;
        }

        var result = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: ProbeDirectory,
            enumerateFileSystemEntries: EnumerateEntries).ScanFilesDetailed();

        Assert.Equal(["root.cs"], ToSortedRelativePaths(tempDir, result.Files));
        var error = Assert.Single(result.Errors);
        Assert.Equal("blocked", error.Path);
        Assert.Equal("Could not scan directory due to permissions.", error.Message);
        Assert.Equal(1, enumerationCounts[Path.GetFullPath(tempDir)]);
        Assert.Equal(1, enumerationCounts[Path.GetFullPath(blockedDir)]);
        Assert.Equal(1, probeCounts[Path.GetFullPath(tempDir)]);
        Assert.Equal(1, probeCounts[Path.GetFullPath(blockedDir)]);
    }

    [Fact]
    public void ScanFilesDetailed_DefaultCaseProbeEnumerationFailureReportsOneErrorWithoutCaseWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-default-entry-snapshot-failure");
        var tempDir = project.Root;
        var blockedDir = Directory.CreateDirectory(Path.Combine(tempDir, "blocked")).FullName;
        TestProjectHelper.WriteTextFile(tempDir, "root.cs", "class DefaultSnapshotFailureFixture { }\n");
        var rootIgnoreCase = CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(tempDir) ?? false;
        var enumerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        IEnumerable<string> EnumerateEntries(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            enumerationCounts[fullPath] = enumerationCounts.GetValueOrDefault(fullPath) + 1;
            if (fullPath == Path.GetFullPath(blockedDir))
                throw new UnauthorizedAccessException("blocked default snapshot");
            return Directory.EnumerateFileSystemEntries(directory);
        }

        var result = new FileIndexer(
            tempDir,
            ignoreCase: rootIgnoreCase,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            enumerateFileSystemEntries: EnumerateEntries).ScanFilesDetailed();

        Assert.Equal(["root.cs"], ToSortedRelativePaths(tempDir, result.Files));
        var error = Assert.Single(result.Errors);
        Assert.Equal("blocked", error.Path);
        Assert.Equal("Could not scan directory due to permissions.", error.Message);
        Assert.Equal(1, enumerationCounts[Path.GetFullPath(tempDir)]);
        Assert.Equal(1, enumerationCounts[Path.GetFullPath(blockedDir)]);
    }

    [Fact]
    public void FileWriteProbe_TryWriteAndDeleteEmptyFile_RemovesProbeAfterSuccess_Issue3689()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-write-probe-success");
        var tempDir = project.Root;
        var probePath = Path.Combine(tempDir, ".cdidx-write-probe.tmp");

        var result = FileWriteProbe.TryWriteAndDeleteEmptyFile(probePath, Encoding.UTF8);

        Assert.True(result);
        Assert.False(File.Exists(probePath));
    }

    private static void DeleteDirectorySymlinkIfPresent(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void FileWriteProbe_TryWriteAndDeleteEmptyFile_ReturnsFalseForDirectoryPath_Issue3689()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-write-probe-failure");
        var tempDir = project.Root;

        var result = FileWriteProbe.TryWriteAndDeleteEmptyFile(tempDir, Encoding.UTF8);

        Assert.False(result);
        Assert.True(Directory.Exists(tempDir));
    }

    [Fact]
    public void FileWriteProbe_TryWriteAndDeleteEmptyFile_DoesNotOverwriteOrDeleteExistingProbe_Issue3777()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-write-probe-existing");
        var tempDir = project.Root;
        var probePath = Path.Combine(tempDir, ".cdidx-write-probe.tmp");
        File.WriteAllText(probePath, "existing", Encoding.UTF8);

        var result = FileWriteProbe.TryWriteAndDeleteEmptyFile(probePath, Encoding.UTF8);

        Assert.False(result);
        Assert.True(File.Exists(probePath));
        Assert.Equal("existing", File.ReadAllText(probePath, Encoding.UTF8));
    }

    [Fact]
    public void ScanFilesDetailed_CaseInsensitiveChildDirectory_SkipsCaseOnlyDuplicatePathWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-case-dedupe");
        var tempDir = project.Root;
        var childDir = TestProjectHelper.ProjectPath(tempDir, "LinkedVolume");
        var sourceFile = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/File.cs", "class CaseDuplicateFixture { }\n");
        var duplicateCasePath = TestProjectHelper.ProjectPath(tempDir, "LinkedVolume", "file.cs");

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: dir => Path.GetFullPath(dir) == Path.GetFullPath(childDir),
            enumerateFiles: dir => Path.GetFullPath(dir) == Path.GetFullPath(childDir)
                ? [sourceFile, duplicateCasePath]
                : Directory.EnumerateFiles(dir));

        var result = indexer.ScanFilesDetailed();

        var file = Assert.Single(result.Files);
        Assert.Equal(sourceFile, file);
        Assert.Contains("LinkedVolume/file.cs", result.NonIndexablePaths);
        Assert.Contains(
            result.Errors,
            error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("case-sensitivity differs", StringComparison.OrdinalIgnoreCase)
                && error.Path == "LinkedVolume");
        Assert.Contains(
            result.Errors,
            error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("differs only by case", StringComparison.OrdinalIgnoreCase)
                && error.Path == "LinkedVolume/file.cs");
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_SkipsBuiltInDirectoriesWithCaseInsensitiveNames()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-skipdir-case");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                ["Node_Modules/ignored.js"] = "export const ignored = true;",
                ["app.js"] = "export const app = true;",
            });

        var indexer = new FileIndexer(tempDir, ignoreCase: true);
        var files = indexer.ScanFiles()
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["app.js"], files);
    }

    [Fact]
    public void ScanFiles_PerDirectoryCdidxIgnore_AppliesChildRulesWithoutLeakingToSiblings()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-per-dir-ignore");
        var tempDir = project.Root;
        Directory.CreateDirectory(Path.Combine(tempDir, "left"));
        Directory.CreateDirectory(Path.Combine(tempDir, "right"));
        File.WriteAllText(Path.Combine(tempDir, ".cdidxignore"), "*.generated.py\n");
        File.WriteAllText(Path.Combine(tempDir, "root.generated.py"), "print('ignored root')\n");
        File.WriteAllText(Path.Combine(tempDir, "left", ".cdidxignore"), "!keep.generated.py\nlocal.py\n");
        File.WriteAllText(Path.Combine(tempDir, "left", "keep.generated.py"), "print('kept child')\n");
        File.WriteAllText(Path.Combine(tempDir, "left", "local.py"), "print('ignored child')\n");
        File.WriteAllText(Path.Combine(tempDir, "right", "keep.generated.py"), "print('ignored sibling')\n");
        File.WriteAllText(Path.Combine(tempDir, "right", "plain.py"), "print('kept sibling')\n");

        var files = new FileIndexer(tempDir)
            .ScanFiles()
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["left/keep.generated.py", "right/plain.py"], files);
    }

    [Fact]
    public void ScanFilesDetailed_OversizedGitignoreFailsClosedWithError()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-oversize-gitignore");
        var tempDir = project.Root;
        TestProjectHelper.WriteSparseFile(tempDir, ".gitignore", 300 * 1024);
        File.WriteAllText(Path.Combine(tempDir, "generated.py"), "print('generated')\n");

        var result = new FileIndexer(tempDir).ScanFilesDetailed();
        var files = result.Files
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .ToList();

        Assert.Empty(files);
        Assert.Contains(
            result.Errors,
            error => error.Path == ".gitignore"
                && error.Severity == FileIndexer.ScanIssueSeverity.Error
                && error.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.HadErrors);
    }

    [Fact]
    public void ScanFilesDetailed_GitignoreRuleCountCapFailsClosedWithError()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-gitignore-rule-cap");
        var tempDir = project.Root;
        const int ruleBudget = 2;
        FileIndexer.IgnoreRulesPerFileBudgetForTesting = ruleBudget;
        var rules = Enumerable.Range(0, ruleBudget)
            .Select(i => $"unused{i}.py")
            .Concat(["late.py"]);
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), string.Join('\n', rules) + "\n");
        File.WriteAllText(Path.Combine(tempDir, "late.py"), "print('late')\n");

        (IReadOnlyList<string> Files, IReadOnlyList<FileIndexer.ScanError> Errors, bool HadErrors) result;
        try
        {
            var scan = new FileIndexer(tempDir).ScanFilesDetailed();
            result = (scan.Files, scan.Errors, scan.HadErrors);
        }
        finally
        {
            FileIndexer.IgnoreRulesPerFileBudgetForTesting = null;
        }
        var files = result.Files
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .ToList();

        Assert.Empty(files);
        Assert.Contains(
            result.Errors,
            error => error.Path == ".gitignore:3"
                && error.Severity == FileIndexer.ScanIssueSeverity.Error
                && error.Message.Contains("2 rules", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.HadErrors);
    }

    [Fact]
    public void ScanFilesDetailed_HardlinkedFiles_SkipsDuplicatePathWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-hardlink");
        var tempDir = project.Root;
        var original = Path.Combine(tempDir, "original.cs");
        var duplicate = Path.Combine(tempDir, "duplicate.cs");
        File.WriteAllText(original, "class HardlinkFixture { }\n");
        CreateHardLink(original, duplicate);

        var result = new FileIndexer(tempDir).ScanFilesDetailed();

        var files = result.Files.Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Single(files);
        Assert.Contains(files[0], new[] { "duplicate.cs", "original.cs" });
        Assert.Contains(result.NonIndexablePaths, path => path is "duplicate.cs" or "original.cs");
        var warning = Assert.Single(
            result.Errors,
            error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("hardlinked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warning.Path, new[] { "duplicate.cs", "original.cs" });
        Assert.False(result.HadErrors);
    }

    [Theory]
    [InlineData("test.py", "python")]
    [InlineData("app.js", "javascript")]
    [InlineData("app.cjs", "javascript")]
    [InlineData("app.mjs", "javascript")]
    [InlineData("main.ts", "typescript")]
    [InlineData("main.cts", "typescript")]
    [InlineData("main.mts", "typescript")]
    [InlineData("types.d.cts", "typescript")]
    [InlineData("types.d.mts", "typescript")]
    [InlineData("lib.go", "go")]
    [InlineData("mod.rs", "rust")]
    [InlineData("App.java", "java")]
    [InlineData("Service.cs", "csharp")]
    [InlineData("Script.kts", "kotlin")]
    [InlineData("style.css", "css")]
    [InlineData("style.scss", "css")]
    [InlineData("page.vue", "vue")]
    [InlineData("page.svelte", "svelte")]
    [InlineData("main.tf", "terraform")]
    [InlineData("app.dart", "dart")]
    [InlineData("Main.scala", "scala")]
    [InlineData("analysis.r", "r")]
    [InlineData("analysis.R", "r")]
    [InlineData("web.ex", "elixir")]
    [InlineData("test.exs", "elixir")]
    [InlineData("script.lua", "lua")]
    [InlineData("Program.fs", "fsharp")]
    [InlineData("Script.fsx", "fsharp")]
    [InlineData("Module.bas", "vb")]
    [InlineData("Customer.cls", "vb")]
    [InlineData("UserControl.ctl", "vb")]
    [InlineData("Document.dob", "vb")]
    [InlineData("DataReport.dsr", "vb")]
    [InlineData("Form1.frm", "vb")]
    [InlineData("SettingsPage.pag", "vb")]
    [InlineData("Macro.vba", "vb")]
    [InlineData("Module1.vb", "vb")]
    [InlineData("Index.vbhtml", "vb")]
    [InlineData("script.vbs", "vb")]
    [InlineData("index.html", "html")]
    [InlineData("legacy.htm", "html")]
    [InlineData("doc.xhtml", "html")]
    [InlineData("page.shtml", "html")]
    [InlineData("Index.cshtml", "csharp")]
    [InlineData("Counter.razor", "csharp")]
    [InlineData("MainWindow.xaml", "xml")]
    [InlineData("App.axaml", "xml")]
    [InlineData("Point.st", "smalltalk")]
    [InlineData("Point.smalltalk", "smalltalk")]
    [InlineData("MyApp.csproj", "msbuild")]
    [InlineData("MyApp.fsproj", "msbuild")]
    [InlineData("MyApp.vbproj", "msbuild")]
    [InlineData("Directory.Build.props", "msbuild")]
    [InlineData("Directory.Build.targets", "msbuild")]
    [InlineData("Main.hs", "haskell")]
    [InlineData("main.zig", "zig")]
    [InlineData("schema.proto", "protobuf")]
    [InlineData("schema.graphql", "graphql")]
    [InlineData("build.gradle", "gradle")]
    [InlineData("build.cmake", "cmake")]
    [InlineData("script.ps1", "powershell")]
    [InlineData("run.bat", "batch")]
    [InlineData("run.cmd", "batch")]
    [InlineData("script.bash", "shell")]
    [InlineData("script.zsh", "shell")]
    [InlineData("script.fish", "shell")]
    [InlineData("Dockerfile", "dockerfile")]
    [InlineData(".dockerfile", "dockerfile")]
    [InlineData("api.Dockerfile", "dockerfile")]
    [InlineData("api.Containerfile", "dockerfile")]
    [InlineData(".containerfile", "dockerfile")]
    [InlineData("Dockerfile-prod", "dockerfile")]
    [InlineData("Dockerfile_prod", "dockerfile")]
    [InlineData("Containerfile-prod", "dockerfile")]
    [InlineData("Containerfile_prod", "dockerfile")]
    [InlineData("Makefile", "makefile")]
    [InlineData("Justfile", "justfile")]
    [InlineData("CMakeLists.txt", "cmake")]
    [InlineData("Vagrantfile", "ruby")]
    // Issue #189: additional filename maps / 追加ファイル名マッピング
    [InlineData("Gemfile", "dependency_manifest")]
    [InlineData("Rakefile", "ruby")]
    [InlineData("Podfile", "dependency_manifest")]
    [InlineData("Guardfile", "ruby")]
    [InlineData("Capfile", "ruby")]
    [InlineData("NAMESPACE", "r")]
    [InlineData(".Rprofile", "r")]
    [InlineData("Rprofile.site", "r")]
    [InlineData("GNUmakefile", "makefile")]
    [InlineData("Containerfile", "dockerfile")]
    [InlineData("BUILD", "python")]
    [InlineData("BUILD.bazel", "python")]
    [InlineData("WORKSPACE", "python")]
    [InlineData("WORKSPACE.bazel", "python")]
    [InlineData("package.json", "dependency_manifest")]
    [InlineData("pyproject.toml", "dependency_manifest")]
    [InlineData("requirements.txt", "dependency_manifest")]
    [InlineData("Pipfile", "dependency_manifest")]
    [InlineData("poetry.toml", "dependency_manifest")]
    [InlineData("Cargo.toml", "dependency_manifest")]
    [InlineData("composer.json", "dependency_manifest")]
    [InlineData("go.mod", "dependency_manifest")]
    [InlineData("go.work", "dependency_manifest")]
    [InlineData("packages.config", "dependency_manifest")]
    [InlineData("Directory.Packages.props", "dependency_manifest")]
    [InlineData("package-lock.json", "dependency_lock")]
    [InlineData("npm-shrinkwrap.json", "dependency_lock")]
    [InlineData("yarn.lock", "dependency_lock")]
    [InlineData("pnpm-lock.yaml", "dependency_lock")]
    [InlineData("bun.lock", "dependency_lock")]
    [InlineData("bun.lockb", "dependency_lock")]
    [InlineData("Gemfile.lock", "dependency_lock")]
    [InlineData("Cargo.lock", "dependency_lock")]
    [InlineData("composer.lock", "dependency_lock")]
    [InlineData("poetry.lock", "dependency_lock")]
    [InlineData("Pipfile.lock", "dependency_lock")]
    [InlineData("go.sum", "dependency_lock")]
    [InlineData("uv.lock", "dependency_lock")]
    [InlineData("packages.lock.json", "dependency_lock")]
    // Issue #189: additional extensions / 追加拡張子
    [InlineData("types.pyi", "python")]
    [InlineData("windowed.pyw", "python")]
    [InlineData("module.pyx", "cython")]
    [InlineData("module.pxd", "cython")]
    [InlineData("tasks.rake", "ruby")]
    [InlineData("mygem.gemspec", "ruby")]
    [InlineData("MyPod.podspec", "ruby")]
    [InlineData("build.groovy", "groovy")]
    [InlineData("build.gvy", "groovy")]
    [InlineData("build.gy", "groovy")]
    [InlineData("build.gsh", "groovy")]
    [InlineData("common.mk", "makefile")]
    [InlineData("page.htm", "html")]
    [InlineData("style.less", "css")]
    [InlineData("style.sass", "sass")]
    [InlineData("style.styl", "stylus")]
    [InlineData("style.pcss", "css")]
    [InlineData("schema.pgsql", "sql")]
    [InlineData("proc.tsql", "sql")]
    [InlineData("pkg.plsql", "sql")]
    [InlineData("orders_pkg.pls", "sql")]
    [InlineData("orders_pkg.pks", "sql")]
    [InlineData("orders_pkg.pkb", "sql")]
    [InlineData("orders_pkg.plb", "sql")]
    [InlineData("migrate.psql", "sql")]
    // Issue #189: filename prefix matching for Dockerfile.* / Makefile.* / GNUmakefile.*
    [InlineData("Dockerfile.dev", "dockerfile")]
    [InlineData("Dockerfile.prod", "dockerfile")]
    [InlineData("Dockerfile.test", "dockerfile")]
    [InlineData("Containerfile.dev", "dockerfile")]
    [InlineData("Makefile.am", "makefile")]
    [InlineData("Makefile.in", "makefile")]
    [InlineData("Makefile.common", "makefile")]
    [InlineData("GNUmakefile.am", "makefile")]
    [InlineData("kernel.cu", "cuda")]
    [InlineData("kernel.cuh", "cuda")]
    [InlineData("header.hh", "cpp")]
    [InlineData("shader.glsl", "glsl")]
    [InlineData("shader.vert", "glsl")]
    [InlineData("shader.frag", "glsl")]
    [InlineData("shader.hlsl", "hlsl")]
    [InlineData("shader.wgsl", "wgsl")]
    [InlineData("shader.metal", "metal")]
    [InlineData("CodeIndex.sln", "solution")]
    [InlineData("app.manifest", "app_manifest")]
    [InlineData("cpu.s", "assembly")]
    [InlineData("cpu.S", "assembly")]
    [InlineData("cpu.asm", "assembly")]
    [InlineData("cpu.nasm", "assembly")]
    [InlineData("cpu.v", "verilog")]
    [InlineData("cpu.sv", "systemverilog")]
    [InlineData("cpu.svh", "systemverilog")]
    [InlineData("cpu.vhd", "vhdl")]
    [InlineData("cpu.vhdl", "vhdl")]
    [InlineData("demo.lisp", "commonlisp")]
    [InlineData("demo.lsp", "commonlisp")]
    [InlineData("demo.cl", "commonlisp")]
    [InlineData("demo.rkt", "racket")]
    [InlineData("demo.pas", "pascal")]
    [InlineData("demo.pp", "pascal")]
    [InlineData("demo.dpr", "pascal")]
    [InlineData("demo.ada", "ada")]
    [InlineData("demo.adb", "ada")]
    [InlineData("demo.ads", "ada")]
    [InlineData("demo.f", "fortran")]
    [InlineData("demo.f77", "fortran")]
    [InlineData("demo.f90", "fortran")]
    [InlineData("demo.f95", "fortran")]
    [InlineData("demo.f03", "fortran")]
    [InlineData("demo.f08", "fortran")]
    [InlineData("demo.for", "fortran")]
    [InlineData("demo.ftn", "fortran")]
    [InlineData("demo.cbl", "cobol")]
    [InlineData("demo.cob", "cobol")]
    [InlineData("demo.cobol", "cobol")]
    [InlineData("demo.cpy", "cobol")]
    [InlineData("demo.raku", "raku")]
    [InlineData("demo.rakumod", "raku")]
    [InlineData("demo.rakutest", "raku")]
    [InlineData("test.t", "perl")]
    [InlineData("app.psgi", "perl")]
    [InlineData("index.cgi", "perl")]
    [InlineData("index.fcgi", "perl")]
    public void DetectLanguage_KnownExtensions_ReturnsCorrectLang(string filename, string expected)
    {
        Assert.Equal(expected, FileIndexer.DetectLanguage(filename));
    }

    [Fact]
    public void DetectLanguage_WorkspaceLangMapYaml_AliasesExtension()
    {
        lock (TestConsoleLock.Gate)
        {
            using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap");
            var tempDir = project.Root;
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                TestProjectHelper.WriteTextFile(
                    tempDir,
                    LanguageMapOverrides.WorkspaceFileName,
                    "entries:\n  - extension: \".in\"\n    language: \"text\"\n  - extension: \".kts.in\"\n    language: \"kotlin\"\n");
                var outsideDir = TestProjectHelper.CreateDirectory(tempDir, "outside");
                Environment.CurrentDirectory = outsideDir;

                Assert.Equal("kotlin", FileIndexer.DetectLanguage(Path.Combine(tempDir, "build.kts.in")));
                Assert.Equal("text", FileIndexer.DetectLanguage(Path.Combine(tempDir, "template.in")));
                Assert.Equal("kotlin", FileIndexer.GetLanguageExtensions()[".kts.in"]);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
            }
        }
    }

    [Fact]
    public void DetectLanguage_ExplicitOverrideWinsBuiltInExactPrefixAndExtensionRules_Issue4613()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_authoritative");
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            TestProjectHelper.WriteTextFile(
                project.Root,
                LanguageMapOverrides.WorkspaceFileName,
                "entries:\n"
                + "- extension: .toml\n  language: exact_override\n"
                + "- extension: .dev\n  language: prefix_override\n"
                + "- extension: .cs\n  language: extension_override\n");
            var exactPath = TestProjectHelper.WriteTextFile(project.Root, "pyproject.toml", "[project]\n");
            var prefixPath = TestProjectHelper.WriteTextFile(project.Root, "Dockerfile.dev", "FROM scratch\n");
            var extensionPath = TestProjectHelper.WriteTextFile(project.Root, "Program.cs", "class Program {}\n");

            Assert.Equal("exact_override", FileIndexer.DetectLanguage(exactPath));
            Assert.Equal("prefix_override", FileIndexer.DetectLanguage(prefixPath));
            Assert.Equal("extension_override", FileIndexer.DetectLanguage(extensionPath));
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void DetectLanguage_ExtensionlessFile_DoesNotLoadLanguageMapOverrides()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_extensionless");
        var tempDir = project.Root;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        var openCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: ruby\n");
            var extensionlessPath = Path.Combine(tempDir, "script");
            File.WriteAllText(extensionlessPath, "echo hello\n");
            LanguageMapOverrides.OpenOverrideFileForTesting = path =>
            {
                openCount++;
                return File.OpenRead(path);
            };

            Assert.Null(FileIndexer.DetectLanguage(extensionlessPath));
            Assert.Equal(0, openCount);
        }
        finally
        {
            LanguageMapOverrides.OpenOverrideFileForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void GetReusableDetectedLanguage_ExtensionFile_DoesNotReloadLanguageMapOverrides()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_reusable");
        var tempDir = project.Root;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        var openCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: ruby\n");
            var path = Path.Combine(tempDir, "template.custom");
            var detectedLanguages = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [path] = "ruby",
            };
            LanguageMapOverrides.OpenOverrideFileForTesting = overridePath =>
            {
                openCount++;
                return File.OpenRead(overridePath);
            };

            Assert.Equal("ruby", FileIndexer.GetReusableDetectedLanguage(path, detectedLanguages));
            Assert.Equal(0, openCount);
        }
        finally
        {
            LanguageMapOverrides.OpenOverrideFileForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void TryDetectLanguageForIndexing_CachesLanguageMapOverridesPerDirectory()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_indexer_cache");
        var tempDir = project.Root;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        var stampProbeCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: ruby\n");
            var firstPath = Path.Combine(tempDir, "first.custom");
            var secondPath = Path.Combine(tempDir, "second.custom");
            File.WriteAllText(firstPath, "first");
            File.WriteAllText(secondPath, "second");
            LanguageMapOverrides.ConfigPathStampProbeForTesting = _ => stampProbeCount++;

            var indexer = new FileIndexer(tempDir);

            Assert.Equal("ruby", indexer.TryDetectLanguageForIndexing(firstPath).Language);
            var firstProbeCount = stampProbeCount;
            Assert.True(firstProbeCount > 0);

            Assert.Equal("ruby", indexer.TryDetectLanguageForIndexing(secondPath).Language);
            Assert.Equal(firstProbeCount, stampProbeCount);
        }
        finally
        {
            LanguageMapOverrides.ConfigPathStampProbeForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void TryDetectLanguageForIndexing_ReusesParentLanguageMapOverridesWhenChildHasNoConfig()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_parent_cache");
        var tempDir = project.Root;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        var stampProbeCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: ruby\n");
            var childDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(childDir);
            var parentPath = Path.Combine(tempDir, "parent.custom");
            var childPath = Path.Combine(childDir, "child.custom");
            File.WriteAllText(parentPath, "parent");
            File.WriteAllText(childPath, "child");
            LanguageMapOverrides.ConfigPathStampProbeForTesting = _ => stampProbeCount++;

            var indexer = new FileIndexer(tempDir);

            Assert.Equal("ruby", indexer.TryDetectLanguageForIndexing(parentPath).Language);
            var parentProbeCount = stampProbeCount;
            Assert.True(parentProbeCount > 0);

            Assert.Equal("ruby", indexer.TryDetectLanguageForIndexing(childPath).Language);
            Assert.Equal(parentProbeCount + 1, stampProbeCount);
        }
        finally
        {
            LanguageMapOverrides.ConfigPathStampProbeForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void TryDetectLanguageForIndexing_DoesNotReuseParentLanguageMapOverridesWhenChildHasConfig()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_child_cache");
        var tempDir = project.Root;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        var stampProbeCount = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: ruby\n");
            var childDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(childDir);
            File.WriteAllText(
                Path.Combine(childDir, LanguageMapOverrides.WorkspaceFileName),
                "entries:\n- extension: custom\n  language: python\n");
            var parentPath = Path.Combine(tempDir, "parent.custom");
            var childPath = Path.Combine(childDir, "child.custom");
            File.WriteAllText(parentPath, "parent");
            File.WriteAllText(childPath, "child");
            LanguageMapOverrides.ConfigPathStampProbeForTesting = _ => stampProbeCount++;

            var indexer = new FileIndexer(tempDir);

            Assert.Equal("ruby", indexer.TryDetectLanguageForIndexing(parentPath).Language);
            var parentProbeCount = stampProbeCount;
            Assert.True(parentProbeCount > 0);

            Assert.Equal("python", indexer.TryDetectLanguageForIndexing(childPath).Language);
            Assert.True(stampProbeCount > parentProbeCount);
        }
        finally
        {
            LanguageMapOverrides.ConfigPathStampProbeForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void TryDetectLanguageForIndexing_ProbeFailureBlocksParentOverrideInheritance_Issue4613()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_child_probe_failure");
        var parentDir = project.Root;
        var childDir = TestProjectHelper.CreateDirectory(parentDir, "src");
        var childConfigPath = Path.Combine(childDir, LanguageMapOverrides.WorkspaceFileName);
        const string extension = ".issue4613blocked";
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            TestProjectHelper.WriteTextFile(
                parentDir,
                LanguageMapOverrides.WorkspaceFileName,
                $"entries:\n- extension: {extension}\n  language: ruby\n");
            File.WriteAllText(
                childConfigPath,
                $"entries:\n- extension: {extension}\n  language: python\n");
            var parentPath = TestProjectHelper.WriteTextFile(parentDir, "parent" + extension, "parent\n");
            var childPath = TestProjectHelper.WriteTextFile(childDir, "child" + extension, "child\n");
            var indexer = new FileIndexer(parentDir);

            Assert.Equal("ruby", indexer.TryDetectLanguageForIndexing(parentPath).Language);

            LanguageMapOverrides.ConfigPathStampProbeForTesting = path =>
            {
                if (string.Equals(path, childConfigPath, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("simulated access denial");
            };

            Assert.Null(indexer.TryDetectLanguageForIndexing(childPath).Language);
            var result = LanguageMapOverrides.LoadEffectiveMapFromDirectoryWithDiagnostics(childDir);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                item => item.Code == "language_map_probe_failed");
            Assert.Equal("access_denied", diagnostic.Reason);
            Assert.True(diagnostic.BlocksParentFallback);
            Assert.False(result.Map.ContainsKey(extension));
        }
        finally
        {
            LanguageMapOverrides.ConfigPathStampProbeForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void TryDetectLanguageForIndexing_NonFileChildConfigBlocksParentOverrideInheritance_Issue4613()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_child_non_file");
        var parentDir = project.Root;
        var childDir = TestProjectHelper.CreateDirectory(parentDir, "src");
        const string extension = ".issue4613nonfile";
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            TestProjectHelper.WriteTextFile(
                parentDir,
                LanguageMapOverrides.WorkspaceFileName,
                $"entries:\n- extension: {extension}\n  language: ruby\n");
            Directory.CreateDirectory(Path.Combine(childDir, LanguageMapOverrides.WorkspaceFileName));
            var childPath = TestProjectHelper.WriteTextFile(childDir, "child" + extension, "child\n");

            var indexer = new FileIndexer(parentDir);

            Assert.Null(indexer.TryDetectLanguageForIndexing(childPath).Language);
            var result = LanguageMapOverrides.LoadEffectiveMapFromDirectoryWithDiagnostics(childDir);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                item => item.Code == "language_map_probe_failed");
            Assert.Equal("not_regular_file", diagnostic.Reason);
            Assert.True(diagnostic.BlocksParentFallback);
            Assert.False(result.Map.ContainsKey(extension));
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void LanguageMapOverrides_LoadEffectiveMapReloadsWhenWorkspaceConfigChanges()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_cache");
        var tempDir = project.Root;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            var configPath = Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName);
            File.WriteAllText(configPath, "entries:\n- extension: one\n  language: ruby\n");
            var first = LanguageMapOverrides.LoadEffectiveMap(Path.Combine(tempDir, "first.one"));

            File.WriteAllText(configPath, "entries:\n- extension: two\n  language: python\n");
            File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(1));
            var second = LanguageMapOverrides.LoadEffectiveMap(Path.Combine(tempDir, "second.two"));

            Assert.Equal("ruby", first[".one"]);
            Assert.False(second.ContainsKey(".one"));
            Assert.Equal("python", second[".two"]);
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void LanguageMapOverrides_OversizedFileSkipsOverridesWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_caps");
        var tempDir = project.Root;
        var oversizedPath = Path.Combine(tempDir, "large-langmap.yaml");
        var fallbackPath = Path.Combine(tempDir, "fallback-langmap.yaml");
        File.WriteAllText(oversizedPath, "entries:\n" + new string('a', 132 * 1024));
        File.WriteAllText(fallbackPath, "entries:\n- extension: ok\n  language: ruby\n");

        var warnings = new List<string>();
        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
            new[] { oversizedPath, fallbackPath },
            warnings.Add);

        Assert.False(map.ContainsKey(".skip"));
        Assert.Equal("ruby", map[".ok"]);
        Assert.Contains(warnings, warning => warning.Contains("exceeds", StringComparison.Ordinal));
    }

    [Fact]
    public void LanguageMapOverrides_ReadFailureSkipsOverridesWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_read_failure");
        var tempDir = project.Root;
        try
        {
            var unreadablePath = Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName);
            var fallbackPath = Path.Combine(tempDir, "fallback-langmap.yaml");
            File.WriteAllText(unreadablePath, "entries:\n- extension: bad\n  language: ruby\n");
            File.WriteAllText(fallbackPath, "entries:\n- extension: ok\n  language: python\n");
            LanguageMapOverrides.OpenOverrideFileForTesting = path =>
                path == unreadablePath
                    ? throw new IOException("share denied")
                    : File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var warnings = new List<string>();
            var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
                new[] { unreadablePath, fallbackPath },
                warnings.Add);

            Assert.False(map.ContainsKey(".bad"));
            Assert.Equal("python", map[".ok"]);
            Assert.Contains(
                warnings,
                warning => warning.Contains("could not be read", StringComparison.Ordinal)
                    && warning.Contains("IOException", StringComparison.Ordinal));

            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            var result = LanguageMapOverrides.LoadEffectiveMapFromDirectoryWithDiagnostics(tempDir);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                item => item.Code == "language_map_read_failed");
            Assert.Equal("io_error", diagnostic.Reason);
            Assert.True(diagnostic.BlocksParentFallback);
        }
        finally
        {
            LanguageMapOverrides.OpenOverrideFileForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void LanguageMapOverrides_TooManyLinesSkipsOverridesWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_lines");
        var tempDir = project.Root;
        var tooManyLinesPath = Path.Combine(tempDir, "many-lines-langmap.yaml");
        var fallbackPath = Path.Combine(tempDir, "fallback-langmap.yaml");
        File.WriteAllText(tooManyLinesPath, string.Concat(Enumerable.Repeat("#\n", 16385)));
        File.WriteAllText(fallbackPath, "entries:\n- extension: ok\n  language: ruby\n");

        var warnings = new List<string>();
        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
            new[] { tooManyLinesPath, fallbackPath },
            warnings.Add);

        Assert.Equal("ruby", map[".ok"]);
        Assert.Contains(warnings, warning => warning.Contains("lines", StringComparison.Ordinal));
    }

    [Fact]
    public void LanguageMapOverrides_OverlongLineSkipsOverridesWithWarning_Issue3706()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_line_length");
        var tempDir = project.Root;
        var overlongLinePath = Path.Combine(tempDir, "long-line-langmap.yaml");
        var fallbackPath = Path.Combine(tempDir, "fallback-langmap.yaml");
        File.WriteAllText(overlongLinePath, new string('x', 16 * 1024 + 1));
        File.WriteAllText(fallbackPath, "entries:\n- extension: ok\n  language: ruby\n");

        var warnings = new List<string>();
        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
            new[] { overlongLinePath, fallbackPath },
            warnings.Add);

        Assert.Equal("ruby", map[".ok"]);
        Assert.Contains(warnings, warning => warning.Contains("line 1 exceeds", StringComparison.Ordinal));
    }

    [Fact]
    public void LanguageMapOverrides_WarningsSanitizeConfigPath_Issue3819()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_sanitize");
        var tempDir = project.Root;
        var configPath = Path.Combine(tempDir, "secret-langmap.yaml");
        File.WriteAllText(configPath, new string('x', 16 * 1024 + 1));

        var warnings = new List<string>();
        _ = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting([configPath], warnings.Add);

        var warning = Assert.Single(warnings);
        Assert.Contains("secret-langmap.yaml", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(tempDir, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(tempDir.Replace('\\', '/'), warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageMapOverrides_EntryCountCapTruncatesRemainingOverridesWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_entries");
        var tempDir = project.Root;
        var configPath = Path.Combine(tempDir, "langmap.yaml");
        var builder = new StringBuilder("entries:\n");
        for (var i = 0; i <= 4096; i++)
            builder.Append("- extension:x").Append(i).Append('\n').Append("language:l\n");
        File.WriteAllText(configPath, builder.ToString());

        var warnings = new List<string>();
        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
            new[] { configPath },
            warnings.Add);

        Assert.Equal("l", map[".x0"]);
        Assert.Equal("l", map[".x4095"]);
        Assert.False(map.ContainsKey(".x4096"));
        Assert.Contains(warnings, warning => warning.Contains("4096", StringComparison.Ordinal));
    }

    [Fact]
    public void LanguageMapOverrides_PatternCountCapTruncatesRemainingOverridesWithWarning_Issue3764()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_patterns");
        var tempDir = project.Root;
        const int maxPatterns = 8192;
        var configPath = Path.Combine(tempDir, "langmap.yaml");
        var builder = new StringBuilder("entries:\n");
        for (var i = 0; i <= maxPatterns; i++)
            builder.Append("extension:x").Append(i).Append('\n');
        File.WriteAllText(configPath, builder.ToString());

        var warnings = new List<string>();
        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
            new[] { configPath },
            warnings.Add);

        Assert.Empty(map);
        Assert.Contains(warnings, warning => warning.Contains("pattern count exceeds 8192", StringComparison.Ordinal));
    }

    [Fact]
    public void LanguageMapOverrides_EntryCountCapIsPerFileSoWorkspaceOverridesStillLoad()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_per_file_entries");
        var tempDir = project.Root;
        var userConfigPath = Path.Combine(tempDir, "user-langmap.yaml");
        var workspaceConfigPath = Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName);
        var builder = new StringBuilder("entries:\n");
        for (var i = 0; i <= 4096; i++)
            builder.Append("- extension:x").Append(i).Append('\n').Append("language:u\n");
        File.WriteAllText(userConfigPath, builder.ToString());
        File.WriteAllText(
            workspaceConfigPath,
            "entries:\n- extension:x0\n  language:workspace\n- extension:workspace\n  language:ruby\n");

        var warnings = new List<string>();
        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(
            new[] { userConfigPath, workspaceConfigPath },
            warnings.Add);

        Assert.Equal("workspace", map[".x0"]);
        Assert.Equal("ruby", map[".workspace"]);
        Assert.False(map.ContainsKey(".x4096"));
        Assert.Contains(warnings, warning => warning.Contains("4096", StringComparison.Ordinal));
    }

    [Fact]
    public void LanguageMapOverrides_BomPrefixedFileLoadsOverrides()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_langmap_bom");
        var tempDir = project.Root;
        var configPath = Path.Combine(tempDir, LanguageMapOverrides.WorkspaceFileName);
        File.WriteAllText(configPath, "\uFEFFentries:\n- extension:bom\n  language:ruby\n");

        var map = LanguageMapOverrides.LoadEffectiveMapFromPathsForTesting(new[] { configPath });

        Assert.Equal("ruby", map[".bom"]);
    }

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Library.fsproj")]
    [InlineData("Project.vbproj")]
    public void GetProjectMarkerFingerprint_RecognizesMsbuildProjectMarkers(string markerFileName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, markerFileName), "<Project />");

        var indexer = new FileIndexer(tempDir);

        Assert.True(FileIndexer.SupportsHotspotFamilyMarkerLanguage("msbuild"));
        Assert.False(string.IsNullOrWhiteSpace(indexer.GetProjectMarkerFingerprint("msbuild")));
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_UsesKnownHashesAndSingleLanguageApiDelegates()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_exact");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "Library.vbproj"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "Tools.fsproj"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "Directory.Build.targets"), "<Project />");

        var indexer = new FileIndexer(tempDir);
        var results = indexer.GetProjectMarkerFingerprintResults();

        Assert.Equal(FileIndexer.GetHotspotFamilyMarkerLanguages(), results.Keys);
        Assert.Equal("2e18211a956f0514a6ed2c7e5ba4f90b99c9910b37932c5c4f05faab42d56c15", results["csharp"].Fingerprint);
        Assert.Equal("5468decb0f22c9efc117ce5fd6907feb3c15bf9255a644995977ee214d36c95f", results["vb"].Fingerprint);
        Assert.Equal("43fd0eb67433addec02c0f48e376d69b7891d7223cdc60b0da45bc35bbb8175e", results["fsharp"].Fingerprint);
        Assert.Equal("88dbe811de43e74ce7605fe7c9362d171b75e2727c3fb72ca2ca88875444e5bd", results["msbuild"].Fingerprint);
        foreach (var language in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            Assert.True(results[language].IsComplete);
            Assert.Empty(results[language].Warnings);
            Assert.Equal(results[language].Fingerprint, indexer.GetProjectMarkerFingerprint(language));
        }
    }

    [Fact]
    public void ScanFilesDetailed_CapturesAllProjectMarkerFingerprintsFromOneDirectoryEnumeration()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_scan_snapshot");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["App.csproj"] = "<Project />",
                ["Directory.Build.props"] = "<Project />",
                ["src/Library.vbproj"] = "<Project />",
                ["src/Tools.fsproj"] = "<Project />",
                ["src/app.py"] = "print('ok')\n",
            });
        var enumerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: _ => false,
            enumerateFileSystemEntries: directory =>
            {
                var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(tempDir, directory));
                enumerationCounts[relativePath] = enumerationCounts.GetValueOrDefault(relativePath) + 1;
                return FileSystemTraversalPolicy.EnumerateFileSystemEntries(directory);
            });

        var scanResult = indexer.ScanFilesDetailed();

        Assert.Equal(2, enumerationCounts.Count);
        Assert.All(enumerationCounts.Values, count => Assert.Equal(1, count));
        Assert.Equal(
            ExpectedProjectMarkerFingerprint("App.csproj"),
            scanResult.ProjectMarkerFingerprints["csharp"].Fingerprint);
        Assert.Equal(
            ExpectedProjectMarkerFingerprint("src/Library.vbproj"),
            scanResult.ProjectMarkerFingerprints["vb"].Fingerprint);
        Assert.Equal(
            ExpectedProjectMarkerFingerprint("src/Tools.fsproj"),
            scanResult.ProjectMarkerFingerprints["fsharp"].Fingerprint);
        Assert.Equal(
            ExpectedProjectMarkerFingerprint(
                "App.csproj",
                "Directory.Build.props",
                "src/Library.vbproj",
                "src/Tools.fsproj"),
            scanResult.ProjectMarkerFingerprints["msbuild"].Fingerprint);
        Assert.All(scanResult.ProjectMarkerFingerprints.Values, result => Assert.True(result.IsComplete));
    }

    [Fact]
    public void GetFamilyScopeKey_AfterScanUsesCollectedMarkerCountsWithoutAncestorProbes()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_scope_snapshot");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/First.csproj"] = "<Project />",
                ["src/Second.csproj"] = "<Project />",
                ["src/feature/Api.cs"] = "public class Api { }\n",
                ["vb/App.vbproj"] = "<Project />",
                ["vb/Module.vb"] = "Public Class [Module]\nEnd Class\n",
                ["build/Directory.Build.props"] = "<Project />",
                ["build/Directory.Build.targets"] = "<Project />",
            });
        var countPostScanAuthorizations = false;
        var postScanAuthorizationCount = 0;
        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: _ => false,
            pathAccessValidator: _ =>
            {
                if (countPostScanAuthorizations)
                    postScanAuthorizationCount++;
            });

        indexer.ScanFilesDetailed();
        countPostScanAuthorizations = true;

        Assert.Equal(
            "src/feature",
            indexer.GetFamilyScopeKey(Path.Combine(tempDir, "src", "feature", "Api.cs"), "csharp"));
        Assert.Equal(
            "vb",
            indexer.GetFamilyScopeKey(Path.Combine(tempDir, "vb", "Module.vb"), "vb"));
        Assert.Equal(
            "build",
            indexer.GetFamilyScopeKey(Path.Combine(tempDir, "build", "Directory.Build.props"), "msbuild"));
        Assert.Equal(0, postScanAuthorizationCount);
    }

    [Fact]
    public void GetFamilyScopeKey_AfterScanSeparatesCaseOnlyMarkerDirectoriesUnderCaseSensitiveChild()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_scope_mixed_sensitive");
        var tempDir = project.Root;
        var linkedVolume = TestProjectHelper.CreateDirectory(tempDir, "LinkedVolume");
        var upperDirectory = TestProjectHelper.CreateDirectory(tempDir, "LinkedVolume/Foo");
        var lowerDirectory = TestProjectHelper.CreateDirectory(tempDir, "LinkedVolume/foo");
        var upperMarker = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/Foo/App.csproj", "<Project />");
        var upperSource = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/Foo/Api.cs", "public class Api { }\n");
        var lowerMarker = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/foo/Other.csproj", "<Project />");
        var lowerSource = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/foo/Other.cs", "public class Other { }\n");
        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Path.GetFullPath(tempDir)] = [linkedVolume],
            [Path.GetFullPath(linkedVolume)] = [upperDirectory, lowerDirectory],
            [Path.GetFullPath(upperDirectory)] = [upperMarker, upperSource],
            [Path.GetFullPath(lowerDirectory)] = [lowerMarker, lowerSource],
        };
        var probeCount = 0;
        var countPostScanAuthorizations = false;
        var postScanAuthorizationCount = 0;

        bool? ProbeDirectory(string directory)
        {
            probeCount++;
            return string.Equals(Path.GetFullPath(directory), Path.GetFullPath(tempDir), StringComparison.Ordinal);
        }

        IEnumerable<string> EnumerateEntries(string directory)
        {
            var normalizedDirectory = Path.GetFullPath(LongPath.RemoveWindowsPrefix(directory));
            return entries.GetValueOrDefault(normalizedDirectory) ?? [];
        }

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: true,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: ProbeDirectory,
            enumerateFileSystemEntries: EnumerateEntries,
            pathAccessValidator: _ =>
            {
                if (countPostScanAuthorizations)
                    postScanAuthorizationCount++;
            });

        indexer.ScanFilesDetailed();
        Assert.Equal(4, probeCount);
        countPostScanAuthorizations = true;

        var upperScope = indexer.GetFamilyScopeKey(upperSource, "csharp");
        var lowerScope = indexer.GetFamilyScopeKey(lowerSource, "csharp");

        Assert.Equal("LinkedVolume/Foo", upperScope);
        Assert.Equal("LinkedVolume/foo", lowerScope);
        Assert.NotEqual(upperScope, lowerScope);
        Assert.DoesNotContain("__file__", upperScope, StringComparison.Ordinal);
        Assert.DoesNotContain("__file__", lowerScope, StringComparison.Ordinal);
        Assert.Equal(4, probeCount);
        Assert.Equal(0, postScanAuthorizationCount);
    }

    [Fact]
    public void GetFamilyScopeKey_AfterScanUsesCaseInsensitiveChildAlias()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_scope_mixed_insensitive");
        var tempDir = project.Root;
        var linkedVolume = TestProjectHelper.CreateDirectory(tempDir, "LinkedVolume");
        var markerDirectory = TestProjectHelper.CreateDirectory(tempDir, "LinkedVolume/Foo");
        var marker = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/Foo/App.csproj", "<Project />");
        var source = TestProjectHelper.WriteTextFile(tempDir, "LinkedVolume/Foo/Api.cs", "public class Api { }\n");
        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Path.GetFullPath(tempDir)] = [linkedVolume],
            [Path.GetFullPath(linkedVolume)] = [markerDirectory],
            [Path.GetFullPath(markerDirectory)] = [marker, source],
        };
        var probeCount = 0;
        var countPostScanAuthorizations = false;
        var postScanAuthorizationCount = 0;

        bool? ProbeDirectory(string directory)
        {
            probeCount++;
            return string.Equals(Path.GetFullPath(directory), Path.GetFullPath(linkedVolume), StringComparison.Ordinal);
        }

        IEnumerable<string> EnumerateEntries(string directory)
        {
            var normalizedDirectory = Path.GetFullPath(LongPath.RemoveWindowsPrefix(directory));
            return entries.GetValueOrDefault(normalizedDirectory) ?? [];
        }

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: ProbeDirectory,
            enumerateFileSystemEntries: EnumerateEntries,
            pathAccessValidator: _ =>
            {
                if (countPostScanAuthorizations)
                    postScanAuthorizationCount++;
            });

        indexer.ScanFilesDetailed();
        Assert.Equal(3, probeCount);
        countPostScanAuthorizations = true;
        var aliasSource = Path.Combine(tempDir, "LinkedVolume", "foo", "Api.cs");

        var scope = indexer.GetFamilyScopeKey(aliasSource, "csharp");

        Assert.Equal("LinkedVolume/foo", scope);
        Assert.Equal(3, probeCount);
        Assert.Equal(0, postScanAuthorizationCount);
    }

    [Fact]
    public void GetFamilyScopeKey_ScanSnapshotContinuesBeyondFingerprintDirectoryBudget()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_scope_budget");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nested/App.csproj"] = "<Project />",
                ["nested/Api.cs"] = "public class Api { }\n",
            });
        var previousBudget = FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting;
        var countPostScanAuthorizations = false;
        var postScanAuthorizationCount = 0;
        try
        {
            FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting = 1;
            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: _ => false,
                pathAccessValidator: _ =>
                {
                    if (countPostScanAuthorizations)
                        postScanAuthorizationCount++;
                });

            var scanResult = indexer.ScanFilesDetailed();
            countPostScanAuthorizations = true;

            Assert.False(scanResult.ProjectMarkerFingerprints["csharp"].IsComplete);
            Assert.Equal(
                "nested",
                indexer.GetFamilyScopeKey(Path.Combine(tempDir, "nested", "Api.cs"), "csharp"));
            Assert.Equal(0, postScanAuthorizationCount);
        }
        finally
        {
            FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting = previousBudget;
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_EnumeratesEachDirectoryOnceForAllLanguages()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_snapshot_count");
        var tempDir = project.Root;
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(tempDir, "tests"));
        File.WriteAllText(Path.Combine(tempDir, "App.csproj"), "<Project />");

        var previousEnumerator = FileIndexer.EnumerateProjectMarkerDirectoriesForTesting;
        var enumerationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = directory =>
            {
                var relativePath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(tempDir, directory));
                enumerationCounts.TryGetValue(relativePath, out var count);
                enumerationCounts[relativePath] = count + 1;
                return Directory.EnumerateDirectories(directory);
            };

            var results = new FileIndexer(tempDir, ignoreCase: false).GetProjectMarkerFingerprintResults();

            Assert.Equal(4, results.Count);
            Assert.Equal(3, enumerationCounts.Count);
            Assert.All(enumerationCounts.Values, count => Assert.Equal(1, count));
            Assert.Contains(".", enumerationCounts.Keys);
            Assert.Contains("src", enumerationCounts.Keys);
            Assert.Contains("tests", enumerationCounts.Keys);
        }
        finally
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = previousEnumerator;
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_LanguageBudgetsTruncateIndependently()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_snapshot_budgets");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "Extra.csproj"), "<Project />");
        var nestedDir = Path.Combine(tempDir, "nested");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "Library.vbproj"), "<Project />");
        File.WriteAllText(Path.Combine(nestedDir, "Tools.fsproj"), "<Project />");
        var budgets = new Dictionary<string, FileIndexer.ProjectMarkerFingerprintBudget>(StringComparer.Ordinal)
        {
            ["csharp"] = new(MaxDirectories: 100, MaxMarkerFiles: 1),
            ["vb"] = new(MaxDirectories: 1, MaxMarkerFiles: 100),
            ["fsharp"] = new(MaxDirectories: 100, MaxMarkerFiles: 100),
            ["msbuild"] = new(MaxDirectories: 100, MaxMarkerFiles: 100),
        };

        var indexer = new FileIndexer(tempDir);
        var results = indexer.GetProjectMarkerFingerprintResultsForTesting(budgets);

        Assert.False(results["csharp"].IsComplete);
        Assert.Contains(results["csharp"].Warnings, warning =>
            warning.Message.Contains("marker file budget 1", StringComparison.Ordinal));
        Assert.False(results["vb"].IsComplete);
        Assert.Contains(results["vb"].Warnings, warning =>
            warning.Message.Contains("directory budget 1", StringComparison.Ordinal));
        Assert.True(results["fsharp"].IsComplete);
        Assert.Empty(results["fsharp"].Warnings);
        Assert.True(results["msbuild"].IsComplete);
        Assert.Empty(results["msbuild"].Warnings);
        Assert.Equal(indexer.GetProjectMarkerFingerprint("fsharp"), results["fsharp"].Fingerprint);
        Assert.Equal(indexer.GetProjectMarkerFingerprint("msbuild"), results["msbuild"].Fingerprint);
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_PreservesPerLanguageWarningOrder()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_snapshot_warning_order");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "[z-a].tmp\n");
        Directory.CreateDirectory(Path.Combine(tempDir, "src"));
        var budgets = FileIndexer.GetHotspotFamilyMarkerLanguages().ToDictionary(
            language => language,
            _ => new FileIndexer.ProjectMarkerFingerprintBudget(MaxDirectories: 1, MaxMarkerFiles: 100),
            StringComparer.Ordinal);

        var results = new FileIndexer(tempDir).GetProjectMarkerFingerprintResultsForTesting(budgets);

        foreach (var result in results.Values)
        {
            Assert.False(result.IsComplete);
            Assert.Collection(
                result.Warnings,
                warning => Assert.Contains("Invalid ignore rule skipped", warning.Message, StringComparison.Ordinal),
                warning => Assert.Contains("directory budget 1", warning.Message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_HonorIgnoreNestedRepositoryAndSubmoduleBoundaries()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_snapshot_boundaries");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "ignored/\n",
                [".gitmodules"] = "[submodule \"lib\"]\n\tpath = modules/lib\n\turl = https://example.invalid/lib.git\n",
                ["App.csproj"] = "<Project />",
                ["Directory.Build.props"] = "<Project />",
                ["ignored/Ignored.csproj"] = "<Project />",
                ["nested/.git/HEAD"] = "ref: refs/heads/main\n",
                ["nested/Nested.vbproj"] = "<Project />",
                ["modules/lib/.git"] = "gitdir: ../../.git/modules/lib\n",
                ["modules/lib/Submodule.fsproj"] = "<Project />",
            });

        var results = new FileIndexer(tempDir).GetProjectMarkerFingerprintResults();

        Assert.Equal(ExpectedProjectMarkerFingerprint("App.csproj"), results["csharp"].Fingerprint);
        Assert.Equal(ExpectedProjectMarkerFingerprint(), results["vb"].Fingerprint);
        Assert.Equal(ExpectedProjectMarkerFingerprint("modules/lib/Submodule.fsproj"), results["fsharp"].Fingerprint);
        Assert.Equal(
            ExpectedProjectMarkerFingerprint("App.csproj", "Directory.Build.props", "modules/lib/Submodule.fsproj"),
            results["msbuild"].Fingerprint);
        Assert.All(results.Values, result => Assert.True(result.IsComplete));
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_PreservesPlatformPatternCasing()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_snapshot_casing");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "Upper.CSPROJ"), "<Project />");
        var expectedMarkerPaths = FileSystemTraversalPolicy
            .EnumerateFiles(tempDir, "*.csproj")
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var results = new FileIndexer(tempDir).GetProjectMarkerFingerprintResults();
        var expectedFingerprint = ExpectedProjectMarkerFingerprint(expectedMarkerPaths);

        Assert.Equal(expectedFingerprint, results["csharp"].Fingerprint);
        Assert.Equal(expectedFingerprint, results["msbuild"].Fingerprint);
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_CancelledToken_ThrowsBeforeTraversal()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_cancel");
        var tempDir = project.Root;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var previousEnumerator = FileIndexer.EnumerateProjectMarkerDirectoriesForTesting;
        var enumerationCount = 0;
        try
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = directory =>
            {
                enumerationCount++;
                return Directory.EnumerateDirectories(directory);
            };

            var indexer = new FileIndexer(tempDir);

            Assert.Throws<OperationCanceledException>(() =>
                indexer.GetProjectMarkerFingerprintResults(cancellation.Token));
            Assert.Equal(0, enumerationCount);
        }
        finally
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = previousEnumerator;
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprintResults_CancellationDuringSharedTraversal_Propagates()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_marker_snapshot_cancel_mid_walk");
        var tempDir = project.Root;
        var childDirectory = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(childDirectory);
        using var cancellation = new CancellationTokenSource();

        var previousEnumerator = FileIndexer.EnumerateProjectMarkerDirectoriesForTesting;
        var enumerationCount = 0;
        try
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = _ => EnumerateAndCancel();
            var indexer = new FileIndexer(tempDir, ignoreCase: false);

            Assert.Throws<OperationCanceledException>(() =>
                indexer.GetProjectMarkerFingerprintResults(cancellation.Token));
            Assert.Equal(1, enumerationCount);
        }
        finally
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = previousEnumerator;
        }

        IEnumerable<string> EnumerateAndCancel()
        {
            enumerationCount++;
            cancellation.Cancel();
            yield return childDirectory;
        }
    }

    private static string ExpectedProjectMarkerFingerprint(params string[] markerPaths)
    {
        Array.Sort(markerPaths, StringComparer.Ordinal);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', markerPaths))))
            .ToLowerInvariant();
    }

    [Fact]
    public void GetProjectMarkerFingerprint_DirectoryCapTruncatesTraversal()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_dir_cap");
        var tempDir = project.Root;
        var nestedDir = Path.Combine(tempDir, "src", "App");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "App.csproj"), "<Project />");

        var indexer = new FileIndexer(tempDir);

        var fullFingerprint = indexer.GetProjectMarkerFingerprint("msbuild");
        var cappedFingerprint = indexer.GetProjectMarkerFingerprintForTesting("msbuild", maxDirectories: 1, maxMarkerFiles: 100);

        Assert.False(string.IsNullOrWhiteSpace(fullFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(cappedFingerprint));
        Assert.NotEqual(fullFingerprint, cappedFingerprint);
    }

    [Fact]
    public void GetProjectMarkerFingerprint_DirectoryCapReportsIncompleteTraversal()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_incomplete");
        var tempDir = project.Root;
        for (var i = 0; i < 4; i++)
            Directory.CreateDirectory(Path.Combine(tempDir, $"project-{i}"));

        var indexer = new FileIndexer(tempDir);

        var result = indexer.GetProjectMarkerFingerprintResultForTesting("msbuild", maxDirectories: 1, maxMarkerFiles: 100);

        Assert.False(result.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(result.Fingerprint));
        var warning = Assert.Single(
            result.Warnings,
            error => error.Message.Contains("directory budget 1", StringComparison.Ordinal));
        Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("Project marker discovery truncated", warning.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ProjectMarkerTraversalFailures()
    {
        yield return [new IOException("blocked")];
        yield return [new UnauthorizedAccessException("blocked")];
        yield return [new NotSupportedException("blocked")];
        yield return [new PathTooLongException("blocked")];
        yield return [new ArgumentException("blocked")];
    }

    [Theory]
    [MemberData(nameof(ProjectMarkerTraversalFailures))]
    public void GetProjectMarkerFingerprintResults_TraversalFailureReportsWarningForEveryLanguage_Issue3473(Exception exception)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_warning");
        var tempDir = project.Root;
        var previousEnumerator = FileIndexer.EnumerateProjectMarkerDirectoriesForTesting;
        try
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = _ => throw exception;
            var indexer = new FileIndexer(tempDir, ignoreCase: false);

            var results = indexer.GetProjectMarkerFingerprintResults();

            Assert.Equal(4, results.Count);
            foreach (var result in results.Values)
            {
                Assert.False(result.IsComplete);
                var warning = Assert.Single(result.Warnings);
                Assert.Equal(".", warning.Path);
                Assert.Contains("Project marker discovery skipped this subtree", warning.Message, StringComparison.Ordinal);
                Assert.Contains(exception.GetType().Name, warning.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = previousEnumerator;
        }
    }

    [Fact]
    public void GetProjectMarkerFingerprint_IgnoredGeneratedTreeDoesNotExhaustDirectoryCap()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_ignored_cap");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "generated/\n");
        var generatedDir = Path.Combine(tempDir, "generated");
        Directory.CreateDirectory(generatedDir);
        for (var i = 0; i < 8; i++)
            Directory.CreateDirectory(Path.Combine(generatedDir, $"project-{i}"));

        var appDir = Path.Combine(tempDir, "src", "App");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "App.csproj"), "<Project />");

        var indexer = new FileIndexer(tempDir);

        var result = indexer.GetProjectMarkerFingerprintResultForTesting("msbuild", maxDirectories: 4, maxMarkerFiles: 100);

        Assert.True(result.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(result.Fingerprint));
    }

    [Fact]
    public void GetProjectMarkerFingerprint_FileCapTruncatesMarkerCollection()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_msbuild_marker_file_cap");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(tempDir, "Lib.csproj"), "<Project />");

        var indexer = new FileIndexer(tempDir);

        var fullFingerprint = indexer.GetProjectMarkerFingerprint("msbuild");
        var cappedFingerprint = indexer.GetProjectMarkerFingerprintForTesting("msbuild", maxDirectories: 100, maxMarkerFiles: 1);
        var cappedResult = indexer.GetProjectMarkerFingerprintResultForTesting("msbuild", maxDirectories: 100, maxMarkerFiles: 1);

        Assert.False(string.IsNullOrWhiteSpace(fullFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(cappedFingerprint));
        Assert.NotEqual(fullFingerprint, cappedFingerprint);
        Assert.False(cappedResult.IsComplete);
        Assert.Contains(
            cappedResult.Warnings,
            error => error.Message.Contains("marker file budget 1", StringComparison.Ordinal));
    }

    [Fact]
    public void GetFamilyScopeKey_MsbuildProjectFileIgnoresDirectoryBuildMarkersForScope()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var srcDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Directory.Build.targets"), "<Project />");

        var indexer = new FileIndexer(tempDir);

        Assert.Equal("src", indexer.GetFamilyScopeKey(Path.Combine(srcDir, "App.csproj"), "msbuild"));
    }

    [Fact]
    public void BuildRecordWithRawBytes_OverExplicitMaxFileBytes_ThrowsActionableOverrideMessage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "Program.cs");
        File.WriteAllText(path, "class Program {}\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false, ignoreRuleRoot: null, maxFileSizeBytes: 4);

        var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() => indexer.BuildRecordWithRawBytes(path));
        Assert.Contains("File too large", ex.Message);
        Assert.Contains("--max-file-bytes", ex.Message);
        Assert.Contains(FileIndexer.MaxFileSizeEnvironmentVariable, ex.Message);
    }

    [Fact]
    public void RawFileMayContainCSharpStaticInterfaceContract_TokenSplitAcrossReadBuffer_ReturnsTrue()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "Program.cs");
        var prefix = new string('x', 81920 - "inter".Length);
        File.WriteAllText(
            path,
            prefix + """
            interface IFixture
            {
                static abstract int Count { get; }
            }
            """);

        var indexer = new FileIndexer(tempDir, ignoreCase: false);

        Assert.True(indexer.RawFileMayContainCSharpStaticInterfaceContract(path, "Program.cs"));
    }

    [Fact]
    public void RawFileMayContainCSharpStaticInterfaceContract_MissingContractTokens_ReturnsFalse()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "Program.cs");
        File.WriteAllText(path, "public interface IFixture { static int Count => 0; }\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false);

        Assert.False(indexer.RawFileMayContainCSharpStaticInterfaceContract(path, "Program.cs"));
    }

    [Fact]
    public void RawFileMayContainCSharpStaticInterfaceContract_OverExplicitMaxFileBytes_ThrowsActionableOverrideMessage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "Program.cs");
        File.WriteAllText(path, "public interface IFixture { static abstract int Count { get; } }\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false, ignoreRuleRoot: null, maxFileSizeBytes: 4);

        var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(
            () => indexer.RawFileMayContainCSharpStaticInterfaceContract(path, "Program.cs"));
        Assert.Contains("File too large", ex.Message);
        Assert.Contains("--max-file-bytes", ex.Message);
        Assert.Contains(FileIndexer.MaxFileSizeEnvironmentVariable, ex.Message);
    }

    [Theory]
    [InlineData("raw-negative", false, false)]
    [InlineData("workspace-member", true, false)]
    [InlineData("semantic-negative", true, false)]
    [InlineData("contract", true, true)]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_ProbeShapesUseOneAuthorizedBoundedSnapshot(
        string shape,
        bool expectsRawCandidate,
        bool expectsSemanticContract)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_snapshot");
        var filler = new string('x', 128 * 1024);
        var source = shape switch
        {
            "raw-negative" => $"public class C {{ int M() => 0; {filler} }}",
            "workspace-member" => $"public class C {{ static int M() => 0; {filler} }}",
            "semantic-negative" => $"public class C {{ const string S = \"interface I {{ static abstract int M(); }}\"; {filler} }}",
            "contract" => $"public interface I {{ static abstract int M(); {filler} }}",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
        var bytes = Encoding.UTF8.GetBytes(source);
        var path = TestProjectHelper.WriteBinaryFile(project.Root, "Fixture.cs", bytes);
        var openCount = 0;
        var authorizationCount = 0;
        CountingCSharpPrepassFileStream? openedStream = null;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            pathAccessValidator: candidate =>
            {
                if (string.Equals(candidate, path, StringComparison.Ordinal))
                    authorizationCount++;
            },
            openReadForIndexContent: candidate =>
            {
                openCount++;
                openedStream = new CountingCSharpPrepassFileStream(candidate, maxReadBytes: 4 * 1024);
                return openedStream;
            });

        var candidateContent = indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs");

        Assert.Equal(1, openCount);
        Assert.Equal(1, authorizationCount);
        Assert.NotNull(openedStream);
        Assert.Equal(expectsRawCandidate, candidateContent is not null);
        Assert.Equal(
            expectsSemanticContract,
            candidateContent is not null
            && CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(candidateContent));
        if (expectsRawCandidate)
        {
            Assert.Equal(1, openedStream.RewindCount);
            Assert.InRange(openedStream.RawProbeBytes, 1, bytes.Length - 1);
            Assert.Equal(bytes.Length + openedStream.RawProbeBytes, openedStream.BytesRead);
        }
        else
        {
            Assert.Equal(0, openedStream.RewindCount);
            Assert.Equal(bytes.Length, openedStream.BytesRead);
        }
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_ContractEncodingsPreserveSemanticProbe(string encodingName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_encoding");
        const string source = "public interface I { static abstract int M(); }\n";
        Encoding encoding = encodingName switch
        {
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf16-le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf16-be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName), encodingName, null),
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        var path = TestProjectHelper.WriteBinaryFile(project.Root, "Fixture.cs", bytes);
        var openCount = 0;
        var authorizationCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            pathAccessValidator: candidate =>
            {
                if (string.Equals(candidate, path, StringComparison.Ordinal))
                    authorizationCount++;
            },
            openReadForIndexContent: candidate =>
            {
                openCount++;
                return new CountingCSharpPrepassFileStream(candidate, maxReadBytes: 7);
            });

        var candidateContent = indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs");

        Assert.Equal(1, openCount);
        Assert.Equal(1, authorizationCount);
        Assert.Equal(source, candidateContent);
        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(candidateContent!));
    }

    [Theory]
    [InlineData("utf8-normalized")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("invalid-utf8")]
    public void LoadCSharpStaticInterfaceCandidateContentWithChecksumForPrepass_MatchesAuthoritativeLoad(
        string encodingName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope(
            "cdidx_csharp_prepass_checksum_encoding");
        const string source =
            "\uFEFFpublic interface I { static abstract int M(); }\r\n"
            + "\u200Bpublic static class C { }\r\n";
        byte[] bytes = encodingName switch
        {
            "utf8-normalized" => Encoding.UTF8.GetBytes(source),
            "utf16-le" => new UnicodeEncoding(
                    bigEndian: false,
                    byteOrderMark: true)
                .GetPreamble()
                .Concat(new UnicodeEncoding(false, true).GetBytes(source))
                .ToArray(),
            "utf16-be" => new UnicodeEncoding(
                    bigEndian: true,
                    byteOrderMark: true)
                .GetPreamble()
                .Concat(new UnicodeEncoding(true, true).GetBytes(source))
                .ToArray(),
            "invalid-utf8" => Encoding.UTF8
                .GetBytes(source)
                .Concat([(byte)0xFF, (byte)'\n'])
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(encodingName),
                encodingName,
                null),
        };
        var path = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "Fixture.cs",
            bytes);
        var indexer = new FileIndexer(project.Root, ignoreCase: false);

        var prepass = indexer
            .LoadCSharpStaticInterfaceCandidateContentWithChecksumForPrepass(
                path,
                "Fixture.cs",
                includeQualifiedMemberAccessCandidate: false);
        var authoritative = indexer.BuildLoadedRecordWithRawBytes(
            path,
            "Fixture.cs",
            knownLanguage: "csharp");

        Assert.NotNull(prepass);
        Assert.Equal(authoritative.Content, prepass.Value.Content);
        Assert.Equal(authoritative.Record.Checksum, prepass.Value.Checksum);
    }

    [Fact]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_IndexBlockingNullPreservesBinaryRejection()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_null");
        var bytes = Encoding.UTF8.GetBytes("public interface I { static abstract int M(); }\0binary");
        var path = TestProjectHelper.WriteBinaryFile(project.Root, "Fixture.cs", bytes);
        var openCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                return new CountingCSharpPrepassFileStream(candidate, maxReadBytes: 8);
            });

        var ex = Assert.Throws<FileIndexer.BinaryFileSkippedException>(
            () => indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs"));

        Assert.Equal(1, openCount);
        Assert.Equal(Array.IndexOf(bytes, (byte)0), ex.NullByteOffset);
    }

    [Fact]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_GrowthBeyondLimitIsRejectedOnSameHandle()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_growth");
        const string source = "interface I{static abstract int M();}";
        var path = TestProjectHelper.WriteTextFile(project.Root, "Fixture.cs", source);
        var initialBytes = new FileInfo(path).Length;
        var openCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: initialBytes + 8,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                return new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 64,
                    afterFirstRead: () => File.AppendAllText(path, new string('x', 32)));
            });

        var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(
            () => indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs"));

        Assert.Equal(1, openCount);
        Assert.Contains("grew during read", ex.Message);
        Assert.Contains("--max-file-bytes", ex.Message);
    }

    [Fact]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_TruncationReauthorizesAndReopensSafely()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_truncation");
        const string replacementSource = "interface I{static abstract int M();}";
        var originalSource = replacementSource + new string('x', 32 * 1024);
        var path = TestProjectHelper.WriteTextFile(project.Root, "Fixture.cs", originalSource);
        var originalModifiedUtc = File.GetLastWriteTimeUtc(path);
        var replacementBytes = Encoding.UTF8.GetBytes(replacementSource);
        var openCount = 0;
        var authorizationCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            pathAccessValidator: candidate =>
            {
                if (string.Equals(candidate, path, StringComparison.Ordinal))
                    authorizationCount++;
            },
            openReadForIndexContent: candidate =>
            {
                openCount++;
                Action? afterFirstRead = null;
                if (openCount == 1)
                {
                    afterFirstRead = () =>
                    {
                        using (var replacement = new FileStream(
                                   path,
                                   FileMode.Create,
                                   FileAccess.Write,
                                   FileShare.ReadWrite | FileShare.Delete))
                        {
                            replacement.Write(replacementBytes);
                        }
                        File.SetLastWriteTimeUtc(path, originalModifiedUtc);
                    };
                }
                return new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 4 * 1024,
                    afterFirstRead);
            });

        var candidateContent = indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs");

        Assert.Equal(2, openCount);
        Assert.Equal(2, authorizationCount);
        Assert.Equal(replacementSource, candidateContent);
        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(candidateContent!));
    }

    [Fact]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_AtomicReplacementReauthorizesAndReadsNewFile()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_atomic_replace");
        var originalSource = "class C { static int M() => 0; }" + new string('x', 32 * 1024);
        const string replacementSource = "interface I { static abstract int M(); }";
        var path = TestProjectHelper.WriteTextFile(project.Root, "Fixture.cs", originalSource);
        var replacementPath = TestProjectHelper.WriteTextFile(project.Root, "Replacement.cs", replacementSource);
        File.SetLastWriteTimeUtc(replacementPath, DateTime.UtcNow.AddSeconds(5));
        var openCount = 0;
        var authorizationCount = 0;
        var snapshotCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            pathAccessValidator: candidate =>
            {
                if (string.Equals(candidate, path, StringComparison.Ordinal))
                    authorizationCount++;
            },
            openReadForIndexContent: candidate =>
            {
                openCount++;
                var stream = new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 4 * 1024);
                if (openCount == 1)
                    File.Replace(replacementPath, path, destinationBackupFileName: null);
                return stream;
            },
            fileHandleSnapshotCapturedForTesting: () => snapshotCount++);

        var candidateContent = indexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs");

        Assert.Equal(2, openCount);
        Assert.Equal(2, authorizationCount);
        Assert.Equal(4, snapshotCount);
        Assert.Equal(replacementSource, candidateContent);
        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(candidateContent!));
    }

    [Fact]
    public void LoadCSharpStaticInterfaceCandidateContentForPrepass_CancellationAndAccessFailuresDoNotReopen()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_prepass_failures");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "Fixture.cs",
            "interface I { static int M(); }" + new string('x', 16 * 1024));
        using var cancellation = new CancellationTokenSource();
        var cancellationOpens = 0;
        var cancellingIndexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                cancellationOpens++;
                return new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 64,
                    afterFirstRead: cancellation.Cancel);
            });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            cancellingIndexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(
                path,
                "Fixture.cs",
                cancellation.Token));
        Assert.Equal(1, cancellationOpens);

        var authorizationCount = 0;
        var authorizationOpens = 0;
        var rejectingIndexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            pathAccessValidator: candidate =>
            {
                if (!string.Equals(candidate, path, StringComparison.Ordinal))
                    return;
                authorizationCount++;
                throw new UnauthorizedAccessException("fixture rejection");
            },
            openReadForIndexContent: candidate =>
            {
                authorizationOpens++;
                return new CountingCSharpPrepassFileStream(candidate, maxReadBytes: 64);
            });

        Assert.Throws<UnauthorizedAccessException>(() =>
            rejectingIndexer.LoadCSharpStaticInterfaceCandidateContentForPrepass(path, "Fixture.cs"));
        Assert.Equal(1, authorizationCount);
        Assert.Equal(0, authorizationOpens);
    }

    [Fact]
    public void BuildRecordWithRawBytes_ExplicitMaxFileBytes_AllowsLargerSourceFile()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "Program.cs");
        File.WriteAllText(path, "class Program {}\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false, ignoreRuleRoot: null, maxFileSizeBytes: 64);
        var (record, content, rawBytes, warning) = indexer.BuildRecordWithRawBytes(path);

        Assert.Equal("Program.cs", record.Path);
        Assert.Equal("csharp", record.Lang);
        Assert.Equal("class Program {}\n", content);
        Assert.Equal(content.Length, rawBytes.Length);
        Assert.Null(warning);
    }

    [Fact]
    public void BuildLoadedRecordWithRawBytes_KnownLanguage_UsesScanResultLanguage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "script");
        File.WriteAllText(path, "#!/bin/sh\necho hi\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false);
        var loaded = indexer.BuildLoadedRecordWithRawBytes(path, "script", knownLanguage: "python");

        Assert.Equal("python", loaded.Record.Lang);
    }

    [Fact]
    public void GetReusableDetectedLanguage_ExtensionlessScriptReturnsNullToPreserveContentDetection()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "script");
        File.WriteAllText(path, "#!/usr/bin/env python\nprint('hi')\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false);
        var scanResult = indexer.ScanFilesDetailed();
        File.WriteAllText(path, "#!/usr/bin/env ruby\nputs 'hi'\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        var knownLanguage = FileIndexer.GetReusableDetectedLanguage(path, scanResult.FileLanguages);
        var loaded = indexer.BuildLoadedRecordWithRawBytes(path, "script", knownLanguage);

        Assert.Equal("python", scanResult.FileLanguages[path]);
        Assert.Null(knownLanguage);
        Assert.Equal("ruby", loaded.Record.Lang);
    }

    [Fact]
    public void GetReusableDetectedLanguage_ExtensionlessFileNameMappingReusesScanResultLanguage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "Makefile");
        File.WriteAllText(path, "all:\n\t@echo hi\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false);
        var scanResult = indexer.ScanFilesDetailed();

        Assert.Equal("makefile", FileIndexer.GetReusableDetectedLanguage(path, scanResult.FileLanguages));
    }

    [Fact]
    public void GetReusableDetectedLanguage_CHeaderReturnsNullToPreserveContentDetection()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "widget.h");
        File.WriteAllText(path, "template <typename T>\nclass Widget {};\n");

        var indexer = new FileIndexer(tempDir, ignoreCase: false);
        var scanResult = indexer.ScanFilesDetailed();
        var knownLanguage = FileIndexer.GetReusableDetectedLanguage(path, scanResult.FileLanguages);
        var loaded = indexer.BuildLoadedRecordWithRawBytes(path, "widget.h", knownLanguage);

        Assert.Equal("c", scanResult.FileLanguages[path]);
        Assert.Null(knownLanguage);
        Assert.Equal("cpp", loaded.Record.Lang);
    }

    [Fact]
    public void ScanFilesDetailedWithIndexingTargets_SharesPathViewAndPreservesReuseBoundaries()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_scan_targets");
        var sourceDirectory = Path.Combine(project.Root, "src");
        var generatedDirectory = Path.Combine(sourceDirectory, "generated");
        var unicodeDirectory = Path.Combine(sourceDirectory, "日本語");
        Directory.CreateDirectory(generatedDirectory);
        Directory.CreateDirectory(unicodeDirectory);
        var dockerfilePath = Path.Combine(project.Root, "Dockerfile");
        var generatedPath = Path.Combine(generatedDirectory, "Widget.g.cs");
        var headerPath = Path.Combine(sourceDirectory, "widget.h");
        var scriptPath = Path.Combine(sourceDirectory, "tool");
        var perlPath = Path.Combine(sourceDirectory, "tool.pl");
        var customScriptPath = Path.Combine(sourceDirectory, "tool.custom");
        var pythonPath = Path.Combine(sourceDirectory, "helper.py");
        var typescriptPath = Path.Combine(unicodeDirectory, "Cafe\u0301.ts");
        File.WriteAllText(dockerfilePath, "FROM scratch\n");
        File.WriteAllText(generatedPath, "public sealed class Widget { }\n");
        File.WriteAllText(headerPath, "template <typename T> class WidgetTemplate { };\n");
        File.WriteAllText(scriptPath, "#!/usr/bin/env python\nprint('ok')\n");
        File.WriteAllText(perlPath, "#!/usr/bin/env perl\nuse strict;\n");
        File.WriteAllText(customScriptPath, "#!/bin/sh\necho ok\n");
        File.WriteAllText(pythonPath, "def run():\n    return True\n");
        File.WriteAllText(typescriptPath, "export const enabled = true;\n");
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            generatedCodePatterns: ["src/generated/**"]);

        var captured = indexer.ScanFilesDetailedWithIndexingTargets();

        Assert.Same(captured.IndexingTargets, captured.ScanResult.Files);
        Assert.Equal(captured.ScanResult.Files.Count, captured.IndexingTargets.Count);
        var targetsByPath = new Dictionary<string, FileIndexer.IndexingFileTarget>(StringComparer.Ordinal);
        foreach (var target in captured.IndexingTargets)
            targetsByPath.Add(target.IndexPath, target);
        for (var index = 0; index < captured.IndexingTargets.Count; index++)
        {
            Assert.Equal(
                captured.ScanResult.Files[index],
                captured.IndexingTargets[index].FilePath);
        }
        var generatedTarget = targetsByPath["src/generated/Widget.g.cs"];
        Assert.Equal(Path.Combine("src", "generated", "Widget.g.cs"), generatedTarget.RelativePath);
        Assert.Equal("src/generated/Widget.g.cs", generatedTarget.DisplayRelativePath);
        Assert.Equal("csharp", generatedTarget.ReusableLanguage);
        Assert.True(generatedTarget.GeneratedExtractionSuppressed);
        Assert.Null(targetsByPath["src/widget.h"].ReusableLanguage);
        Assert.Null(targetsByPath["src/tool"].ReusableLanguage);
        Assert.Equal("perl", targetsByPath["src/tool.pl"].ReusableLanguage);
        Assert.Equal("shell", targetsByPath["src/tool.custom"].ReusableLanguage);
        Assert.Equal("dockerfile", targetsByPath["Dockerfile"].ReusableLanguage);
        Assert.Equal("python", targetsByPath["src/helper.py"].ReusableLanguage);
        Assert.Equal("typescript", targetsByPath["src/日本語/Café.ts"].ReusableLanguage);
        Assert.Equal("src/日本語/Café.ts", targetsByPath["src/日本語/Café.ts"].IndexPath);
        Assert.All(
            ((System.Collections.IEnumerable)captured.ScanResult.Files).Cast<object>(),
            static value => Assert.IsType<string>(value));

        Assert.IsType<List<string>>(indexer.ScanFiles());
        Assert.IsType<List<string>>(indexer.ScanFilesDetailed().Files);
    }

    [Theory]
    // Bare trailing-dot forms should not match prefix rules — suffix must be non-empty.
    // 末尾ドットだけの形はプレフィックス規則に一致しない（サフィックス必須）。
    [InlineData("Dockerfile.")]
    [InlineData("Containerfile.")]
    [InlineData("Makefile.")]
    [InlineData("GNUmakefile.")]
    public void DetectLanguage_BareTrailingDot_DoesNotMatchPrefix(string filename)
    {
        Assert.Null(FileIndexer.DetectLanguage(filename));
    }

    [Theory]
    [InlineData("image.png")]
    [InlineData("data.bin")]
    [InlineData("archive.zip")]
    public void DetectLanguage_UnknownExtensions_ReturnsNull(string filename)
    {
        Assert.Null(FileIndexer.DetectLanguage(filename));
    }

    [Theory]
    [InlineData("rbenv", "#!/usr/bin/env bash\nexit 0\n", "shell")]
    [InlineData("tool", "#!/bin/sh\necho hi\n", "shell")]
    [InlineData("worker", "#!/usr/bin/python3\nprint('hi')\n", "python")]
    [InlineData("bundle", "#!/usr/bin/env ruby\nputs 'hi'\n", "ruby")]
    [InlineData("cli", "#!/usr/bin/env node\nconsole.log('hi')\n", "javascript")]
    [InlineData("envsplit", "#!/usr/bin/env -S python -O\nprint('hi')\n", "python")]
    [InlineData("envquoted", "#!/usr/bin/env -S \"python -O\"\nprint('hi')\n", "python")]
    [InlineData("script", "#!/usr/bin/env pwsh\nWrite-Host hi\n", "powershell")]
    public void DetectLanguage_ExtensionlessShebangScripts_ReturnCorrectLang(string fileName, string content, string expected)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, fileName);
        File.WriteAllText(path, content);

        Assert.Equal(expected, FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void DetectLanguage_ExtensionlessAndUnknownZshCompdefSignaturesHonorEncodingAndLineEndings_Issue5165()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_zsh_compdef_issue5165");
        var cases = new (string Name, string Content, Encoding Encoding)[]
        {
            ("utf8", "#compdef tool\n_tool() {}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            ("utf8-crlf", "#compdef tool\r\n_tool() {}\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            ("utf8-bom", "#compdef tool\n_tool() {}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
            ("utf16-le", "#compdef tool\n_tool() {}\n", new UnicodeEncoding(bigEndian: false, byteOrderMark: true)),
            ("utf16-be", "#compdef tool\n_tool() {}\n", new UnicodeEncoding(bigEndian: true, byteOrderMark: true)),
            ("directive-only", "#compdef\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            ("unknown.zshcompfixture", "#compdef tool\n_tool() {}\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
        };

        foreach (var testCase in cases)
        {
            var path = TestProjectHelper.ProjectPath(project.Root, testCase.Name);
            File.WriteAllText(path, testCase.Content, testCase.Encoding);

            var detection = FileIndexer.TryDetectLanguage(path);

            Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
            Assert.Equal("shell", detection.Language);
            Assert.Equal(FileIndexer.ZshCompdefDetectionSource, detection.DetectionSource);
        }
    }

    [Fact]
    public void DetectLanguage_ZshCompdefRequiresExactFirstLineDirectiveBoundary_Issue5165()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_zsh_compdef_invalid_issue5165");
        var invalidFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lookalike"] = "#compdefinitely tool\n",
            ["spaced"] = "# compdef tool\n",
            ["late"] = "# generated completion\n#compdef tool\n",
            ["ordinary"] = "plain extensionless text\n",
            ["uppercase"] = "#COMPDEF tool\n",
            ["over-cap"] = "#compdef " + new string('x', FileIndexer.ShebangProbeByteLimit),
        };

        foreach (var (name, content) in invalidFiles)
        {
            var path = TestProjectHelper.WriteTextFile(project.Root, name, content);
            Assert.Null(FileIndexer.DetectLanguage(path));
        }

        var ambiguousPath = TestProjectHelper.WriteTextFile(project.Root, "tool.t", "#compdef tool\n");
        Assert.Equal("perl", FileIndexer.DetectLanguage(ambiguousPath));
    }

    [Fact]
    public void DetectLanguage_ExtensionlessUtf16ShebangScript_ReturnsLanguage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "utf16-script");
        File.WriteAllBytes(path, Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("#!/usr/bin/env python\nprint('hi')\n"))
            .ToArray());

        Assert.Equal("python", FileIndexer.DetectLanguage(path));
    }

    [Theory]
    [InlineData("elf", new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00 })]
    [InlineData("macho", new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01 })]
    [InlineData("pe", new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 })]
    [InlineData("data", new byte[] { (byte)'#', (byte)'!', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/', (byte)'s', (byte)'h', 0x00 })]
    public void DetectLanguage_ExtensionlessBinaryLikeFiles_ReturnsNull(string fileName, byte[] bytes)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, fileName);
        File.WriteAllBytes(path, bytes);

        Assert.Null(FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void DetectLanguage_ExtensionlessOverCapShebangLine_ReturnsNull()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "tool");
        File.WriteAllText(path, "#!/usr/bin/env " + new string('x', 256));

        Assert.Null(FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void DetectLanguage_ExtensionlessNonScript_ReturnsNull()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "README");
        File.WriteAllText(path, "Hello world\n");

        Assert.Null(FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void DetectLanguage_UnknownExtensionWithShebang_ReturnsLanguageAndSource_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "notes.txt");
        File.WriteAllText(path, "#!/usr/bin/env python3\nprint('hi')\n");

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
        Assert.Equal("python", detection.Language);
        Assert.Equal("shebang", detection.DetectionSource);
    }

    [Fact]
    public void DetectLanguage_AmbiguousTclShebangOverridesTFileDefault_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_shebang_tcl");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "command.t",
            "#!/usr/bin/env tclsh\nproc greet {} { puts hello }\n");

        var detection = FileIndexer.TryDetectLanguage(path);
        var scan = new FileIndexer(project.Root).ScanFilesDetailed();

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
        Assert.Equal("tcl", detection.Language);
        Assert.Equal("shebang", detection.DetectionSource);
        Assert.Equal("tcl", scan.FileLanguages[path]);
    }

    [Fact]
    public void DetectLanguage_ExplicitOverrideBeatsAmbiguousShebang_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_shebang_override");
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            TestProjectHelper.WriteTextFile(
                project.Root,
                LanguageMapOverrides.WorkspaceFileName,
                "entries:\n  - extension: \".t\"\n    language: \"ruby\"\n");
            var path = TestProjectHelper.WriteTextFile(
                project.Root,
                "command.t",
                "#!/usr/bin/env tclsh\nputs hello\n");

            Assert.Equal("ruby", FileIndexer.DetectLanguage(path));
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void DetectLanguage_MalformedAmbiguousShebangFallsBackToExtension_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_shebang_malformed");
        var path = TestProjectHelper.WriteTextFile(project.Root, "legacy.t", "#!\nuse strict;\n");

        Assert.Equal("perl", FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void DetectLanguage_BinaryUnknownExtensionDoesNotTrustShebangPrefix_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_shebang_binary");
        var path = TestProjectHelper.ProjectPath(project.Root, "payload.txt");
        File.WriteAllBytes(path, [(byte)'#', (byte)'!', (byte)'/', (byte)'u', (byte)'s', (byte)'r', (byte)'/', (byte)'b', (byte)'i', (byte)'n', (byte)'/', (byte)'p', (byte)'y', (byte)'t', (byte)'h', (byte)'o', (byte)'n', 0]);

        Assert.Null(FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void DetectLanguage_StrongExtensionWinsConflictingShebang_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_shebang_conflict");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "worker.rb",
            "#!/usr/bin/env python3\nputs 'ruby'\n");

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal("ruby", detection.Language);
        Assert.Null(detection.DetectionSource);
    }

    [Theory]
    [InlineData("Widget.M", "#import <Foundation/Foundation.h>\n@interface Widget : NSObject\n@end\n", "objc")]
    [InlineData("add.m", "function result = add(left, right)\nresult = left + right;\nend\n", "matlab")]
    [InlineData("Worker.pl", "use strict;\nuse warnings;\nsub run { return 1; }\n", "perl")]
    [InlineData("family.pl", "ancestor(X, Y) :- parent(X, Y).\n", "prolog")]
    public void DetectLanguage_AmbiguousExtensionsUseStrongContentMarkers_Issue4612(
        string fileName,
        string content,
        string expectedLanguage)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_content");
        var path = TestProjectHelper.WriteTextFile(project.Root, fileName, content);

        var detection = FileIndexer.TryDetectLanguage(path);
        var scan = new FileIndexer(project.Root).ScanFilesDetailed();

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
        Assert.Equal(expectedLanguage, detection.Language);
        Assert.Equal("content", detection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.High, detection.Confidence);
        Assert.Equal(expectedLanguage, scan.FileLanguages[path]);
    }

    [Theory]
    [InlineData("main :- run.\n")]
    [InlineData("sentence --> noun_phrase.\n")]
    public void DetectLanguage_ZeroArityPrologRulesAreStrongContentMarkers_Issue4612(string content)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_zero_arity_prolog");
        var path = TestProjectHelper.WriteTextFile(project.Root, "rules.pl", content);

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
        Assert.Equal("prolog", detection.Language);
        Assert.Equal("content", detection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.High, detection.Confidence);
    }

    [Fact]
    public void DetectLanguage_Utf8CodePointSplitAtBoundedPrefixRemainsSupported_Issue4612()
    {
        const int probeByteLimit = 64 * 1024;
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_utf8_boundary");
        var path = TestProjectHelper.ProjectPath(project.Root, "rules.pl");
        var prologMarker = Encoding.UTF8.GetBytes("ancestor(X, Y) :- parent(X, Y).\n");
        var bytes = new byte[probeByteLimit + 2];
        prologMarker.CopyTo(bytes, 0);
        Array.Fill(bytes, (byte)'x', prologMarker.Length, probeByteLimit - prologMarker.Length - 1);
        bytes[probeByteLimit - 1] = 0xe2;
        bytes[probeByteLimit] = 0x82;
        bytes[probeByteLimit + 1] = 0xac;
        File.WriteAllBytes(path, bytes);

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
        Assert.Equal("prolog", detection.Language);
        Assert.Equal("content", detection.DetectionSource);
    }

    [Theory]
    [InlineData("notes.m", "% deliberately weak markers only\nvalue = 1;\nend\n", "ambiguous_m")]
    [InlineData("conflict.m", "#import <Foundation/Foundation.h>\nfunction result = add(left, right)\n", "ambiguous_m")]
    [InlineData("facts.pl", "parent(alice, bob).\n", "ambiguous_pl")]
    [InlineData("conflict.pl", "use strict;\nancestor(X, Y) :- parent(X, Y).\n", "ambiguous_pl")]
    public void DetectLanguage_WeakOrConflictingMarkersRemainExplicitlyAmbiguous_Issue4612(
        string fileName,
        string content,
        string expectedLanguage)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_unresolved");
        var path = TestProjectHelper.WriteTextFile(project.Root, fileName, content);

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal(expectedLanguage, detection.Language);
        Assert.Equal("ambiguous", detection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.Low, detection.Confidence);
    }

    [Theory]
    [InlineData("Demo.xcodeproj", "source.m", "objc")]
    [InlineData("model.slx", "source.m", "matlab")]
    [InlineData("Makefile.PL", "source.pl", "perl")]
    [InlineData("rules.prolog", "source.pl", "prolog")]
    public void DetectLanguage_AmbiguousExtensionsUseConservativeProjectMarkers_Issue4612(
        string markerName,
        string fileName,
        string expectedLanguage)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_project");
        TestProjectHelper.WriteTextFile(project.Root, markerName, string.Empty);
        var path = TestProjectHelper.WriteTextFile(project.Root, fileName, string.Empty);

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal(expectedLanguage, detection.Language);
        Assert.Equal("project", detection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.Medium, detection.Confidence);
    }

    [Fact]
    public void DetectLanguage_PrologShebangOverridesAmbiguousPl_Issue4612()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_prolog_shebang");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "tool.pl",
            "#!/usr/bin/env swipl\n:- initialization(main).\nmain :- halt.\n");

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal("prolog", detection.Language);
        Assert.Equal("shebang", detection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.High, detection.Confidence);
    }

    [Fact]
    public void DetectLanguage_FilenamePrefixPrecedesAmbiguousExtensionShebang_Issue4901()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_filename_precedence");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "Makefile.pl",
            "#!/usr/bin/env ruby\nputs 1\n");

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal("makefile", detection.Language);
        Assert.Null(detection.DetectionSource);
        Assert.Null(detection.Confidence);
    }

    [Fact]
    public void DetectLanguage_AmbiguousShebangRequiresTerminatorBeforeByteLimit_Issue4901()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_shebang_boundary");
        const string shebang = "#!/usr/bin/env ruby";
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "tool.pl",
            shebang
            + new string(' ', FileIndexer.ShebangProbeByteLimit - shebang.Length)
            + "\nputs 1\n");

        var detection = FileIndexer.TryDetectLanguage(path);

        Assert.Equal("ambiguous_pl", detection.Language);
        Assert.Equal("ambiguous", detection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.Low, detection.Confidence);
    }

    [Fact]
    public void DetectLanguage_AmbiguousExtensionOverrideReportsAuthoritativeReason_Issue4901()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_override");
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            TestProjectHelper.WriteTextFile(
                project.Root,
                LanguageMapOverrides.WorkspaceFileName,
                "entries:\n  - extension: \".m\"\n    language: \"matlab\"\n");
            var path = TestProjectHelper.WriteTextFile(
                project.Root,
                "Widget.M",
                "#import <Foundation/Foundation.h>\n");

            var detection = FileIndexer.TryDetectLanguage(path);

            Assert.Equal("matlab", detection.Language);
            Assert.Equal("language_map_override", detection.DetectionSource);
            Assert.Equal(FileIndexer.LanguageDetectionConfidence.High, detection.Confidence);
        }
        finally
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void DetectLanguage_EmptyAndBinaryAmbiguousFilesHaveExplicitOutcomes_Issue4901()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_ambiguous_inputs");
        var emptyPath = TestProjectHelper.WriteTextFile(project.Root, "empty.M", string.Empty);
        var binaryPath = TestProjectHelper.ProjectPath(project.Root, "binary.m");
        File.WriteAllBytes(binaryPath, [(byte)'x', 0, (byte)'y']);

        var emptyDetection = FileIndexer.TryDetectLanguage(emptyPath);
        var binaryDetection = FileIndexer.TryDetectLanguage(binaryPath);

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, emptyDetection.Status);
        Assert.Equal("ambiguous_m", emptyDetection.Language);
        Assert.Equal("ambiguous", emptyDetection.DetectionSource);
        Assert.Equal(FileIndexer.LanguageDetectionConfidence.Low, emptyDetection.Confidence);
        Assert.Equal(FileIndexer.FileProbeStatus.Unsupported, binaryDetection.Status);
        Assert.Null(binaryDetection.Language);
    }

    [Fact]
    public void DetectLanguage_LeadingWhitespacePseudoShebang_ReturnsNull()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "tool");
        File.WriteAllText(path, "  #!/usr/bin/env bash\necho hi\n");

        Assert.Null(FileIndexer.DetectLanguage(path));
    }

    [Fact]
    public void ScanFiles_IndexesIssue189FilenameAndExtensionCoverage()
    {
        // Locks in the full Issue #189 repro: Ruby / Docker / Makefile / .pyi / .less / .mk /
        // .htm and Dockerfile.* / Makefile.* prefix variants are all indexed (not silently dropped).
        // Issue #189 のリプロを網羅。Ruby / Docker / Makefile / .pyi / .less / .mk / .htm と
        // Dockerfile.* / Makefile.* のプレフィックス変種が黙って落ちないことをロックする。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Gemfile"] = "source 'https://rubygems.org'\ngem 'rails', '~> 7.0'\n",
            ["Rakefile"] = "task :default => [:test]\n",
            ["Containerfile"] = "FROM alpine\nRUN echo hi\n",
            ["Dockerfile.dev"] = "FROM alpine AS builder\nRUN echo dev\n",
            ["GNUmakefile"] = "all:\n\techo hi\n",
            ["common.mk"] = "OBJ = foo.o bar.o\n",
            ["stub.pyi"] = "def foo() -> int: ...\n",
            ["style.less"] = ".foo { color: red; }\n",
            ["page.htm"] = "<html><body>old-school</body></html>\n",
            ["Makefile.am"] = "SUBDIRS = lib\n",
        };
        TestProjectHelper.WriteTextFiles(tempDir, files);

        var scanned = ScanRelativeFiles(tempDir);

        var expected = files.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, scanned);
    }

    [Fact]
    public void ScanFiles_LoadsProjectRootPatternConfigsWithoutIndexingCdidxInputs_Issue4592()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-pattern-scan-project");
        using var cwd = TestProjectHelper.CreateTempProjectScope("cdidx-pattern-scan-cwd");
        var projectRoot = project.Root;
        var cwdRoot = cwd.Root;
        lock (TestConsoleLock.Gate)
        {
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                WriteFileIndexerPatternConfig(
                    projectRoot,
                    "project.yaml",
                    "language: \"projectdsl\"\nextensions:\n  - extension: \".projecttoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^project (?<name>\\\\w+)\"\n");
                WriteFileIndexerPatternConfig(
                    cwdRoot,
                    "cwd.yaml",
                    "language: \"cwddsl\"\nextensions:\n  - extension: \".cwdtoy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^cwd (?<name>\\\\w+)\"\n");
                File.WriteAllText(Path.Combine(projectRoot, "Project.projecttoy"), "project Widget\n");
                File.WriteAllText(Path.Combine(projectRoot, "CwdLeak.cwdtoy"), "cwd Widget\n");
                Environment.CurrentDirectory = cwdRoot;

                var scanned = ScanRelativeFiles(projectRoot);

                Assert.Equal(["Project.projecttoy"], scanned);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                ExtractorPluginRegistry.ResetForTests();
            }
        }
    }

    [Fact]
    public void ScanFiles_IndexesDependencyManifests()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["package.json"] = "{\"dependencies\":{}}\n",
                ["pyproject.toml"] = "[project]\nname = 'sample'\n",
                ["requirements.txt"] = "pytest\n",
                ["Cargo.toml"] = "[package]\nname = 'sample'\n",
                ["composer.json"] = "{}\n",
                ["unknown.txt"] = "ignored\n",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["Cargo.toml", "composer.json", "package.json", "pyproject.toml", "requirements.txt"], files);
    }

    private static readonly (string Entry, string Language)[] ExactLanguageMapEntries =
    [
        ("Dockerfile", "dockerfile"),
        ("Containerfile", "dockerfile"),
        ("Makefile", "makefile"),
        ("GNUmakefile", "makefile"),
        ("Gemfile", "dependency_manifest"),
        ("Rakefile", "ruby"),
        ("Podfile", "dependency_manifest"),
        ("NAMESPACE", "r"),
        (".Rprofile", "r"),
        ("Rprofile.site", "r"),
        ("BUILD.bazel", "python"),
        ("package.json", "dependency_manifest"),
        ("pyproject.toml", "dependency_manifest"),
        ("requirements.txt", "dependency_manifest"),
        ("Cargo.toml", "dependency_manifest"),
        ("go.mod", "dependency_manifest"),
        ("Directory.Packages.props", "dependency_manifest"),
        ("package-lock.json", "dependency_lock"),
        ("npm-shrinkwrap.json", "dependency_lock"),
        ("pnpm-lock.yaml", "dependency_lock"),
        ("Gemfile.lock", "dependency_lock"),
        ("go.sum", "dependency_lock"),
        ("uv.lock", "dependency_lock"),
        ("packages.lock.json", "dependency_lock"),
        (".sln", "solution"),
        (".manifest", "app_manifest"),
        (".config", "xml"),
        (".runsettings", "xml"),
        (".rules", "config"),
        (".gitattributes", "gitattributes"),
        (".s", "assembly"),
        (".S", "assembly")
    ];

    private static readonly (string Entry, string Language)[] PrefixLanguageMapEntries =
    [
        ("Dockerfile.<suffix>", "dockerfile"),
        ("Containerfile.<suffix>", "dockerfile"),
        ("Makefile.<suffix>", "makefile"),
        ("GNUmakefile.<suffix>", "makefile")
    ];

    private static readonly (string Entry, string Language)[] StyleLanguageMapEntries =
    [
        (".sass", "sass"),
        (".styl", "stylus"),
        (".scss", "css"),
        (".less", "css")
    ];

    private static readonly (string Entry, string Language)[] PythonFamilyLanguageMapEntries =
    [
        (".pyx", "cython"),
        (".pxd", "cython"),
        (".py", "python"),
        (".pyi", "python")
    ];

    private static readonly (string Entry, string Language)[] Issue205LanguageMapEntries =
    [
        (".groovy", "groovy"),
        (".cu", "cuda"),
        (".glsl", "glsl"),
        (".hlsl", "hlsl"),
        (".wgsl", "wgsl"),
        (".metal", "metal"),
        (".asm", "assembly"),
        (".v", "verilog"),
        (".sv", "systemverilog"),
        (".vhd", "vhdl"),
        (".lisp", "commonlisp"),
        (".rkt", "racket"),
        (".pas", "pascal"),
        (".ada", "ada"),
        (".f90", "fortran"),
        (".raku", "raku"),
        (".t", "perl"),
        (".cbl", "cobol"),
        (".cob", "cobol"),
        (".cobol", "cobol"),
        (".cpy", "cobol")
    ];

    private static readonly (string Entry, string Language)[] MainstreamExtensionLanguageMapEntries =
    [
        (".ml", "ocaml"),
        (".mli", "ocaml"),
        (".cr", "crystal"),
        (".clj", "clojure"),
        (".cljs", "clojure"),
        (".cljc", "clojure"),
        (".edn", "clojure"),
        (".d", "d"),
        (".erl", "erlang"),
        (".hrl", "erlang"),
        (".jl", "julia"),
        (".nim", "nim"),
        (".nims", "nim"),
        (".pl", "ambiguous_pl"),
        (".pm", "perl"),
        (".pod", "perl"),
        (".psgi", "perl"),
        (".cgi", "perl"),
        (".fcgi", "perl"),
        (".sol", "solidity"),
        (".tcl", "tcl"),
        (".tk", "tcl")
    ];

    private static readonly (string Entry, string Language)[] ObjCLanguageMapEntries =
    [
        (".m", "ambiguous_m"),
        (".mm", "objc"),
        (".hh", "cpp")
    ];

    [Fact]
    public void GetLanguageExtensions_ExposesPrefixAndFileNameVariants()
    {
        // `cdidx languages` (and the MCP listing) should advertise everything TryDetectLanguage
        // actually recognizes, including exact-name Dockerfile / Makefile / Gemfile and the
        // Dockerfile.<suffix> / Makefile.<suffix> prefix variants added for Issue #189.
        // `cdidx languages`（および MCP の一覧）は TryDetectLanguage が実際に解釈するものを
        // 網羅すべき。Dockerfile / Makefile / Gemfile の完全一致に加え、Issue #189 で追加した
        // Dockerfile.<suffix> / Makefile.<suffix> などのプレフィックス変種も露出させる。
        var map = FileIndexer.GetLanguageExtensions();

        AssertLanguageMapEntries(map, ExactLanguageMapEntries);
        AssertLanguageMapEntries(map, PrefixLanguageMapEntries);
        AssertLanguageMapEntries(map, StyleLanguageMapEntries);
        AssertLanguageMapEntries(map, PythonFamilyLanguageMapEntries);
        AssertLanguageMapEntries(map, Issue205LanguageMapEntries);
        AssertLanguageMapEntries(map, MainstreamExtensionLanguageMapEntries);
        AssertLanguageMapEntries(map, ObjCLanguageMapEntries);
    }

    private static void AssertLanguageMapEntries(
        IReadOnlyDictionary<string, string> map,
        params (string Entry, string Language)[] expectedEntries)
    {
        foreach (var (entry, language) in expectedEntries)
        {
            Assert.True(map.TryGetValue(entry, out var actualLanguage), $"Expected language map to contain '{entry}'.");
            Assert.Equal(language, actualLanguage);
        }
    }

    [Fact]
    public void ScanFiles_IndexesCobolExtensions()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hello.cbl"] = "       IDENTIFICATION DIVISION.\n       PROGRAM-ID. HELLO.\n",
            ["copy.cpy"] = "       01  COPY-NAME PIC X(10).\n",
            ["legacy.cob"] = "       PROCEDURE DIVISION.\n",
            ["modern.cobol"] = "       STOP RUN.\n",
        };
        TestProjectHelper.WriteTextFiles(tempDir, files);

        var scanned = new FileIndexer(tempDir).ScanFiles()
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(files.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList(), scanned);
    }

    [Fact]
    public void ScanFiles_IndexesIssue205AdditionalExtensionCoverage()
    {
        // Locks in the Issue #205 extensions that were silently dropped before:
        // Groovy, assembly, CUDA, GPU shaders, HDL, Common Lisp, Racket, Pascal, Ada,
        // Fortran, Raku, and Perl test scripts all need to survive scan-time filtering.
        // Issue #205 で黙って落ちていた拡張子を固定する。
        // Groovy / assembly / CUDA / GPU shaders / HDL / Common Lisp / Racket / Pascal / Ada /
        // Fortran / Raku / Perl test scripts が scan 時のフィルタを通過することを確認する。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build.groovy"] = "println 'hello'\n",
            ["kernel.cu"] = "__global__ void add() {}\n",
            ["shader.glsl"] = "void main() {}\n",
            ["shader.hlsl"] = "float4 main() : SV_Target { return 0; }\n",
            ["shader.wgsl"] = "@vertex fn main() -> @builtin(position) vec4<f32> { return vec4<f32>(); }\n",
            ["shader.metal"] = "kernel void main() {}\n",
            ["boot.s"] = "mov %eax, %eax\n",
            ["cpu.v"] = "module cpu(); endmodule\n",
            ["cpu.sv"] = "module cpu(); endmodule\n",
            ["cpu.vhd"] = "entity cpu is end entity;\n",
            ["demo.lisp"] = "(defun hello ())\n",
            ["demo.rkt"] = "#lang racket\n(displayln \"hi\")\n",
            ["demo.pas"] = "program demo;\nbegin\nend.\n",
            ["demo.ada"] = "procedure Demo is begin null; end Demo;\n",
            ["demo.f90"] = "program demo\nend program demo\n",
            ["demo.raku"] = "say \"hi\";\n",
            ["test.t"] = "use Test::More;\n",
        };

        TestProjectHelper.WriteTextFiles(tempDir, files);

        var scanned = new FileIndexer(tempDir).ScanFiles()
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var expected = files.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, scanned);
    }

    [Fact]
    public void ScanFiles_IndexesKotlinScriptAndExtractsSymbols()
    {
        // Gradle Kotlin DSL files must be indexed as Kotlin, not silently skipped.
        // Gradle Kotlin DSL ファイルは Kotlin として index され、黙って落ちてはいけない。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "build.gradle.kts");
        var content = """
            plugins {
                kotlin("jvm") version "1.9.23"
            }

            val answer = 42
            """;
        File.WriteAllText(path, content);

        var scanned = new FileIndexer(tempDir).ScanFiles().ToList();

        Assert.Single(scanned);
        Assert.Equal(path, scanned[0]);
        Assert.Equal("kotlin", FileIndexer.DetectLanguage(path));

        var symbols = SymbolExtractor.Extract(1, "kotlin", content).ToList();
        Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "answer");
    }

    [Fact]
    public void BuildRecordWithRawBytes_CppStyleHeaderContentIsDetectedAsCpp()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "widget.h");
        var content = """
            #pragma once

            namespace demo {
            template <typename T>
            class Widget {
            public:
                constexpr Widget() = default;
            };
            }
            """;
        File.WriteAllText(path, content);

        var indexer = new FileIndexer(tempDir);
        var (record, decodedContent, _, _) = indexer.BuildRecordWithRawBytes(path);

        Assert.Equal("cpp", record.Lang);
        Assert.Equal(content.Replace("\r\n", "\n"), decodedContent);

        var symbols = SymbolExtractor.Extract(1, record.Lang!, decodedContent).ToList();
        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == "demo");
        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Widget");
    }

    [Fact]
    public void BuildRecordWithRawBytes_CStyleHeaderContentStaysC()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = Path.Combine(tempDir, "legacy.h");
        var content = """
            #ifndef LEGACY_H
            #define LEGACY_H

            struct legacy_point {
                int x;
                int y;
            };

            #endif
            """;
        File.WriteAllText(path, content);

        var indexer = new FileIndexer(tempDir);
        var (record, decodedContent, _, _) = indexer.BuildRecordWithRawBytes(path);

        Assert.Equal("c", record.Lang);
        Assert.Equal(content.Replace("\r\n", "\n"), decodedContent);

        var symbols = SymbolExtractor.Extract(1, record.Lang!, decodedContent).ToList();
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "class");
    }

    [Fact]
    public void TryDetectLanguage_CppHeaderLexicalDetectionMasksCommentsStringsAndSplicedLines()
    {
        AssertHeaderDetection(
            null,
            "c",
            FileIndexer.HeaderExtensionFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        const string commentOnlyHeader = """
            /*
             * namespace phantom {
             * class CommentOnly { public: std::vector<int> values; };
             * }
             */
            struct real_c_type { int value; };
            """;
        AssertHeaderDetection(
            commentOnlyHeader,
            "c",
            FileIndexer.HeaderLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        const string macroHeader = """
            #define CPP_TEXT "namespace phantom { class MacroString { public: std::vector<int> values; }; }"
            #define DECLARE_CPP_TYPE(name) \
                class name
            typedef struct real_c_type { int value; } real_c_type;
            """;
        AssertHeaderDetection(
            macroHeader,
            "c",
            FileIndexer.HeaderLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        const string splicedNonCodeHeader = """
            const char *message = "text \
            namespace string_phantom";
            // comment continues through translation-phase splicing \
            class CommentPhantom {};
            typedef struct real_c_type { int value; } real_c_type;
            """;
        AssertHeaderDetection(
            splicedNonCodeHeader,
            "c",
            FileIndexer.HeaderLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        const string spliceFormedDelimiterHeader = """
            /\
            / namespace line_comment_phantom { class Phantom {}; }
            /\
            * namespace block_comment_phantom { class Phantom {}; } *\
            /
            const char *text = R\
            "tag(namespace raw_string_phantom { class Phantom {}; })tag\
            ";
            typedef struct real_c_type { int value; } real_c_type;
            """;
        AssertHeaderDetection(
            spliceFormedDelimiterHeader,
            "c",
            FileIndexer.HeaderLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        const string spliceFormedBlockCommentCloserHeader = """
            /* comment closed through translation-phase splicing *\
            /
            namespace real_cpp { class RealType {}; }
            """;
        AssertHeaderDetection(
            spliceFormedBlockCommentCloserHeader,
            "cpp",
            FileIndexer.HeaderLexicalMarkerDetectionSource,
            FileIndexer.LanguageDetectionConfidence.High);

        const string mixedHeader = """
            typedef struct legacy_point { int x; int y; } legacy_point;
            #ifdef __cplusplus
            namespace modern {
            template <typename T> class PointAdapter {};
            }
            #endif
            """;
        AssertHeaderDetection(
            mixedHeader,
            "cpp",
            FileIndexer.HeaderLexicalMarkerDetectionSource,
            FileIndexer.LanguageDetectionConfidence.High);
    }

    [Fact]
    public void TryDetectLanguage_CppHeaderStrategicSamplingTracksLexicalStateAndUtf8Bytes()
    {
        var longLicensePrefix = string.Concat(Enumerable.Repeat("// Licensed material for strategic sampling.\n", 1_800));
        var cSuffix = string.Concat(Enumerable.Repeat("int legacy_padding_value_for_sampling;\n", 1_800));
        var longLicenseHeader = longLicensePrefix + """
            namespace sampled {
            template <typename T> class BeyondLegacyLineCutoff {};
            }
            """ + cSuffix;
        AssertHeaderDetection(
            longLicenseHeader,
            "cpp",
            FileIndexer.HeaderSampledLexicalMarkerDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Medium);

        var longBlockCommentHeader = "/*" + new string('x', 55_000)
            + "\nnamespace block_comment_phantom { class Phantom {}; }\n"
            + new string('x', 55_000) + "*/\nstruct real_c_type { int value; };\n";
        AssertHeaderDetection(
            longBlockCommentHeader,
            "c",
            FileIndexer.HeaderSampledLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        var longRawStringHeader = "const char *text = R\"tag(" + new string('x', 55_000)
            + "\nnamespace raw_string_phantom { class Phantom {}; }\n"
            + new string('x', 55_000) + ")tag\";\nstruct real_c_type { int value; };\n";
        AssertHeaderDetection(
            longRawStringHeader,
            "c",
            FileIndexer.HeaderSampledLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        var longMacroHeader = "#define PHANTOM_DECL \\\n"
            + string.Concat(Enumerable.Repeat("    ignored_macro_payload \\\n", 1_600))
            + "    namespace macro_phantom { class Phantom {}; }\n"
            + string.Concat(Enumerable.Repeat("int real_c_value;\n", 2_500));
        AssertHeaderDetection(
            longMacroHeader,
            "c",
            FileIndexer.HeaderSampledLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        var singleLineHeader = new string('x', 55_000)
            + " std::vector<int> sampled_values; "
            + new string('x', 55_000);
        AssertHeaderDetection(
            singleLineHeader,
            "cpp",
            FileIndexer.HeaderSampledLexicalMarkerDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Medium);

        const int boundaryFixtureLength = 100_000;
        const int middleSampleStart = (boundaryFixtureLength / 2) - ((48 * 1024 / 3) / 2);
        var boundarySplitIdentifierPrefix = new string('x', middleSampleStart - 2);
        const string boundarySplitIdentifier = "myclass value;\n";
        var boundarySplitIdentifierHeader = boundarySplitIdentifierPrefix
            + boundarySplitIdentifier
            + new string(
                'x',
                boundaryFixtureLength - boundarySplitIdentifierPrefix.Length - boundarySplitIdentifier.Length);
        Assert.Equal(boundaryFixtureLength, boundarySplitIdentifierHeader.Length);
        AssertHeaderDetection(
            boundarySplitIdentifierHeader,
            "c",
            FileIndexer.HeaderSampledLexicalFallbackDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Low);

        var multibyteHeader = "/*" + new string('界', 18_000) + "*/\n"
            + "namespace utf8_sampled { class Utf8Marker {}; }\n"
            + new string(' ', 18_000);
        Assert.True(multibyteHeader.Length < 48 * 1024);
        Assert.True(Encoding.UTF8.GetByteCount(multibyteHeader) > 48 * 1024);
        AssertHeaderDetection(
            multibyteHeader,
            "cpp",
            FileIndexer.HeaderSampledLexicalMarkerDetectionSource,
            FileIndexer.LanguageDetectionConfidence.Medium);
    }

    private static void AssertHeaderDetection(
        string? content,
        string expectedLanguage,
        string expectedSource,
        FileIndexer.LanguageDetectionConfidence expectedConfidence)
    {
        var detection = FileIndexer.TryDetectLanguage("sample.h", content);

        Assert.Equal(FileIndexer.FileProbeStatus.Supported, detection.Status);
        Assert.Equal(expectedLanguage, detection.Language);
        Assert.Equal(expectedSource, detection.DetectionSource);
        Assert.Equal(expectedConfidence, detection.Confidence);
    }

    [Fact]
    public void ScanFiles_SkipsExcludedDirectories()
    {
        // Create a temp directory structure to test scanning
        // テスト用の一時ディレクトリ構造を作成
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('hello')");

        var nodeModules = Path.Combine(tempDir, "node_modules");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "dep.js"), "module.exports = {}");
        var internalDataDirectory = Path.Combine(tempDir, ".cdidx");
        Directory.CreateDirectory(internalDataDirectory);
        File.WriteAllText(Path.Combine(internalDataDirectory, "suggestions-codeindex.json"), "[]");

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles();

        Assert.Single(files);
        Assert.Contains("app.py", files[0]);
    }

    [Theory]
    [InlineData("node_modules", "dep.js")]
    [InlineData("target", "main.rs")]
    [InlineData("vendor", "dep.go")]
    [InlineData("bin", "app.cs")]
    public void ScanFiles_IndexesExplicitRootEvenWhenRootNameIsSkipped(string rootDirName, string fileName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempParentDir = project.Root;
        var rootDir = Path.Combine(tempParentDir, rootDirName);
        Directory.CreateDirectory(rootDir);
        File.WriteAllText(Path.Combine(rootDir, fileName), "content");

        var nestedNodeModules = Path.Combine(rootDir, "node_modules");
        Directory.CreateDirectory(nestedNodeModules);
        File.WriteAllText(Path.Combine(nestedNodeModules, "nested.js"), "module.exports = {}");

        var indexer = new FileIndexer(rootDir);
        var files = indexer.ScanFiles();

        Assert.Single(files);
        Assert.Contains(fileName, files[0]);
    }

    [Fact]
    public void ScanFiles_SkipsExcludedFiles()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "app.js"), "console.log('hello')");
        File.WriteAllText(Path.Combine(tempDir, ".DS_Store"), "metadata");
        File.WriteAllText(Path.Combine(tempDir, "Thumbs.db"), "metadata");

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles();

        // Only app.js should be found, not platform metadata files.
        // app.jsのみ検出され、platform metadata fileは除外される。
        Assert.Single(files);
        Assert.Contains("app.js", files[0]);
    }

    [Fact]
    public void ScanFiles_IndexesDependencyLockfiles()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["package-lock.json"] = "{}",
                ["npm-shrinkwrap.json"] = "{}",
                ["yarn.lock"] = "# yarn",
                ["pnpm-lock.yaml"] = "lockfileVersion: 9\n",
                ["Gemfile.lock"] = "GEM",
                ["Cargo.lock"] = "# lock",
                ["go.sum"] = "module v1.0.0 h1:hash\n",
                ["Pipfile.lock"] = "{}",
                ["uv.lock"] = "version = 1\n",
            });

        var files = new FileIndexer(tempDir).ScanFiles()
            .Select(path => Path.GetFileName(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "Cargo.lock",
                "Gemfile.lock",
                "Pipfile.lock",
                "go.sum",
                "npm-shrinkwrap.json",
                "package-lock.json",
                "pnpm-lock.yaml",
                "uv.lock",
                "yarn.lock",
            ],
            files);
        Assert.All(files, file => Assert.Equal("dependency_lock", FileIndexer.DetectLanguage(file)));
    }

    [Fact]
    public void ScanFiles_SkipsControlCharacterFileNamesWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "ok.cs"), "class Ok { }\n");
        File.WriteAllText(Path.Combine(tempDir, "bad\nname.cs"), "class Bad { }\n");

        var result = new FileIndexer(tempDir).ScanFilesDetailed();
        var files = ToSortedRelativePaths(tempDir, result.Files);

        Assert.Equal(["ok.cs"], files);
        Assert.Contains(result.NonIndexablePaths, path => path == "bad\\u000Aname.cs");
        var warning = Assert.Single(result.Errors, error => error.Severity == FileIndexer.ScanIssueSeverity.Warning);
        Assert.Equal("bad\\u000Aname.cs", warning.Path);
        Assert.Contains("control characters", warning.Message);
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void BuildRecordWithRawBytes_RejectsControlCharacterPathBeforeIo()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var indexer = new FileIndexer(tempDir);

        var ex = Assert.Throws<InvalidOperationException>(
            () => indexer.BuildRecordWithRawBytes(Path.Combine(tempDir, "bad\0name.cs")));

        Assert.Contains("control characters", ex.Message);
    }

    [Fact]
    public void EvaluatePathFilter_RejectsControlCharacterPathBeforeNormalization()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var indexer = new FileIndexer(tempDir);

        var filter = indexer.EvaluatePathFilter(Path.Combine(tempDir, "bad\0name.cs"));

        Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultFile, filter.FilterKind);
        Assert.True(filter.ShouldDeleteExisting);
        var warning = Assert.Single(filter.Errors);
        Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("control characters", warning.Message);
        Assert.Contains("\\u0000", warning.Path);
    }

    [Fact]
    public void EvaluatePathFilter_RootFileStillUsesRootIgnoreRules()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "ignored.cs\n");
        var ignoredPath = Path.Combine(tempDir, "ignored.cs");
        File.WriteAllText(ignoredPath, "class Ignored { }\n");

        var indexer = new FileIndexer(tempDir);
        var filter = indexer.EvaluatePathFilter(ignoredPath);

        Assert.Equal(FileIndexer.PathFilterKind.IgnoredByRules, filter.FilterKind);
        Assert.True(filter.ShouldDeleteExisting);
    }

    [Fact]
    public void ScanFiles_SkipsAppleDoubleResourceForks()
    {
        // AppleDouble (`._*`) files masquerade as the real file's language (e.g. `._app.js`
        // looks like JavaScript) but are binary metadata blobs. They must be skipped wherever
        // they appear in the tree, including nested directories.
        // AppleDouble (`._*`) は原ファイルと同じ拡張子に見えるバイナリメタデータで、ツリーの
        // どこに置かれていても index 対象にしてはならない。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "app.js"), "console.log('hello')");
        File.WriteAllText(Path.Combine(tempDir, "._app.js"), "\x00\x05\x16\x07AppleDouble");
        File.WriteAllText(Path.Combine(tempDir, "._.gitignore"), "\x00\x05\x16\x07AppleDouble");

        var sub = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "main.py"), "def hello(): pass\n");
        File.WriteAllText(Path.Combine(sub, "._main.py"), "\x00\x05\x16\x07AppleDouble");

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["app.js", "sub/main.py"], files);
    }

    [Fact]
    public void ScanFiles_AllowsRecognizedDotfiles()
    {
        // The AppleDouble denylist must not collateral-damage well-known dotfiles such as
        // .gitignore, .editorconfig, and .cdidxrc.json — they do not start with `._`.
        // AppleDouble の除外は `._` 接頭辞のみで判定するため、.gitignore / .editorconfig /
        // .cdidxrc.json などの既知 dotfile は引き続き走査対象に残る必要がある。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "node_modules\n");
        File.WriteAllText(Path.Combine(tempDir, ".editorconfig"), "root = true\n");
        File.WriteAllText(Path.Combine(tempDir, ".cdidxrc.json"), "{}");

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles()
            .Select(path => Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Contains(".editorconfig", files);
        Assert.Contains(".gitignore", files);
        Assert.Contains(".cdidxrc.json", files);
    }

    [Fact]
    public void EvaluatePathFilter_TreatsAppleDoubleAsDefaultFileExclusion()
    {
        // Update-mode (--files / --commits) must match the walker's denylist so that
        // re-indexing an AppleDouble path explicitly does not bypass the default skip.
        // --files / --commits の更新モードでも AppleDouble を明示的に対象に含められないよう、
        // walker と同じ既定除外を返すこと。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var appleDouble = Path.Combine(tempDir, "._app.js");
        File.WriteAllText(appleDouble, "\x00\x05\x16\x07AppleDouble");

        var indexer = new FileIndexer(tempDir);
        var filter = indexer.EvaluatePathFilter(appleDouble);

        Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultFile, filter.FilterKind);
        Assert.True(filter.ShouldDeleteExisting);
    }

    [Fact]
    public void EvaluatePathFilter_RejectsFileReachedThroughDirectorySymlinkOutsideProject()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var outside = TestProjectHelper.CreateTempProjectScope("codeindex_outside");
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var outsideDir = outside.Root;
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(outsideDir, "outside.cs"), "class Outside { }\n");

        var linkDir = Path.Combine(tempDir, "linked");
        Directory.CreateSymbolicLink(linkDir, outsideDir);
        var linkedFile = Path.Combine(linkDir, "outside.cs");

        var filter = new FileIndexer(tempDir).EvaluatePathFilter(linkedFile);
        var internalFilter = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal).EvaluatePathFilter(linkedFile);
        var allFilter = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.All).EvaluatePathFilter(linkedFile);

        Assert.Equal(FileIndexer.PathFilterKind.OutsideProjectRoot, filter.FilterKind);
        Assert.True(filter.ShouldDeleteExisting);
        Assert.Equal(FileIndexer.PathFilterKind.OutsideProjectRoot, internalFilter.FilterKind);
        Assert.Equal(FileIndexer.PathFilterKind.None, allFilter.FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_DefaultPolicyRejectsActualSymlinkComponentsBelowProjectRoot()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var targetDirectory = Path.Combine(tempDir, "target");
        Directory.CreateDirectory(targetDirectory);
        var targetFile = Path.Combine(targetDirectory, "target.cs");
        File.WriteAllText(targetFile, "class Target { }\n");
        var directoryLink = Path.Combine(tempDir, "directory_alias");
        Directory.CreateSymbolicLink(directoryLink, targetDirectory);
        var fileLink = Path.Combine(tempDir, "file_alias.cs");
        File.CreateSymbolicLink(fileLink, targetFile);

        var defaultIndexer = new FileIndexer(tempDir, ignoreCase: false);
        var internalIndexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

        Assert.Equal(
            FileIndexer.PathFilterKind.SymlinkDisallowed,
            defaultIndexer.EvaluatePathFilter(Path.Combine(directoryLink, "target.cs")).FilterKind);
        Assert.Equal(
            FileIndexer.PathFilterKind.SymlinkDisallowed,
            defaultIndexer.EvaluatePathFilter(fileLink).FilterKind);
        Assert.Equal(
            FileIndexer.PathFilterKind.None,
            internalIndexer.EvaluatePathFilter(Path.Combine(directoryLink, "target.cs")).FilterKind);
        Assert.Equal(
            FileIndexer.PathFilterKind.None,
            internalIndexer.EvaluatePathFilter(fileLink).FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_DefaultPolicyAllowsSymlinkedProjectRoot()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var realRoot = Path.Combine(project.Root, "real_root");
        Directory.CreateDirectory(realRoot);
        File.WriteAllText(Path.Combine(realRoot, "sample.cs"), "class Sample { }\n");
        var linkedRoot = Path.Combine(project.Root, "linked_root");
        Directory.CreateSymbolicLink(linkedRoot, realRoot);

        var filter = new FileIndexer(linkedRoot, ignoreCase: false)
            .EvaluatePathFilter(Path.Combine(linkedRoot, "sample.cs"));

        Assert.Equal(FileIndexer.PathFilterKind.None, filter.FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_DefaultPolicyAllowsMacUnicodeAliasSpelling()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var decomposedPath = Path.Combine(project.Root, "Cafe\u0301.cs");
        var composedAliasPath = Path.Combine(project.Root, "Caf\u00e9.cs");
        File.WriteAllText(decomposedPath, "class Cafe { }\n");
        if (!File.Exists(composedAliasPath))
            return; // The hosting volume is Unicode-normalization-sensitive. / この volume は Unicode 正規化を区別する。

        var filter = new FileIndexer(project.Root, ignoreCase: false)
            .EvaluatePathFilter(composedAliasPath);

        Assert.Equal(FileIndexer.PathFilterKind.None, filter.FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_DefaultPolicyAllowsMacUnicodeAliasProjectRootSpelling()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var fixture = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var projectRoot = Path.Combine(fixture.Root, "Cafe\u0301 Project");
        var projectRootAlias = Path.Combine(fixture.Root, "Caf\u00e9 Project");
        Directory.CreateDirectory(projectRoot);
        var sourcePath = Path.Combine(projectRoot, "sample.cs");
        var sourceAliasPath = Path.Combine(projectRootAlias, "sample.cs");
        File.WriteAllText(sourcePath, "class Sample { }\n");
        if (!File.Exists(sourceAliasPath)
            || !FileIndexer.TryGetFileIdentity(projectRoot, out var projectRootIdentity)
            || !FileIndexer.TryGetFileIdentity(projectRootAlias, out var aliasIdentity)
            || projectRootIdentity != aliasIdentity)
        {
            return; // The hosting volume distinguishes Unicode normalization. / この volume は Unicode 正規化を区別する。
        }

        var defaultIndexer = new FileIndexer(projectRoot, ignoreCase: false);
        Assert.Equal(
            FileIndexer.PathFilterKind.None,
            defaultIndexer.EvaluatePathFilter(sourceAliasPath).FilterKind);

        var targetDirectory = Path.Combine(projectRoot, "target");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "linked.cs"), "class Linked { }\n");
        Directory.CreateSymbolicLink(Path.Combine(projectRoot, "directory_alias"), targetDirectory);
        var linkedAliasPath = Path.Combine(projectRootAlias, "directory_alias", "linked.cs");
        var internalIndexer = new FileIndexer(
            projectRoot,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

        Assert.Equal(
            FileIndexer.PathFilterKind.SymlinkDisallowed,
            defaultIndexer.EvaluatePathFilter(linkedAliasPath).FilterKind);
        Assert.Equal(
            FileIndexer.PathFilterKind.None,
            internalIndexer.EvaluatePathFilter(linkedAliasPath).FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_DefaultPolicyAllowsWindowsShortNameAliasSpelling()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        const string longDirectoryName = "Long Directory Name For Alias";
        var longDirectoryPath = Path.Combine(project.Root, longDirectoryName);
        Directory.CreateDirectory(longDirectoryPath);
        File.WriteAllText(Path.Combine(longDirectoryPath, "sample.cs"), "class Sample { }\n");
        if (!TryGetWindowsShortPath(longDirectoryPath, out var shortDirectoryPath))
            return;

        var shortDirectoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(shortDirectoryPath));
        if (string.Equals(shortDirectoryName, longDirectoryName, StringComparison.OrdinalIgnoreCase))
            return; // 8.3 aliases are disabled on this volume. / この volume では 8.3 alias が無効。

        var aliasPath = Path.Combine(project.Root, shortDirectoryName, "sample.cs");
        Assert.True(File.Exists(aliasPath));

        var filter = new FileIndexer(project.Root, ignoreCase: true).EvaluatePathFilter(aliasPath);

        Assert.Equal(FileIndexer.PathFilterKind.None, filter.FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_DefaultPolicyAllowsWindowsShortProjectRootAliasSpelling()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fixture = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var projectRoot = Path.Combine(fixture.Root, "Long Project Root Name For Alias");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "sample.cs"), "class Sample { }\n");
        if (!TryGetWindowsShortPath(projectRoot, out var projectRootAlias)
            || string.Equals(projectRootAlias, projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return; // 8.3 aliases are disabled on this volume. / この volume では 8.3 alias が無効。
        }

        var aliasPath = Path.Combine(projectRootAlias, "sample.cs");
        Assert.True(File.Exists(aliasPath));

        var filter = new FileIndexer(projectRoot, ignoreCase: true).EvaluatePathFilter(aliasPath);

        Assert.Equal(FileIndexer.PathFilterKind.None, filter.FilterKind);
    }

    [Fact]
    public void EvaluatePathFilter_RejectsCaseOnlySiblingRootAcrossMixedNamespaces_Issue5091()
    {
        using var fixture = TestProjectHelper.CreateTempProjectScope("codeindex_mixed_root_namespace");
        var upperRoot = Path.Combine(fixture.Root, "Foo");
        var lowerRoot = Path.Combine(fixture.Root, "foo");
        Directory.CreateDirectory(upperRoot);
        Directory.CreateDirectory(lowerRoot);
        if (!FileIndexer.TryGetFileIdentity(upperRoot, out var upperIdentity)
            || !FileIndexer.TryGetFileIdentity(lowerRoot, out var lowerIdentity)
            || upperIdentity == lowerIdentity)
        {
            return; // The hosting filesystem aliases the two spellings. / この filesystem では2表記が同一 path。
        }

        var lowerFile = Path.Combine(lowerRoot, "sample.cs");
        File.WriteAllText(lowerFile, "class LowerNamespace { }\n");
        var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
        PathCasing.IgnoreCaseProbeForTesting = path =>
        {
            var fullPath = Path.GetFullPath(path);
            return string.Equals(fullPath, upperRoot, StringComparison.Ordinal)
                || fullPath.StartsWith(upperRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || string.Equals(fullPath, lowerRoot, StringComparison.Ordinal)
                || fullPath.StartsWith(lowerRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        };
        try
        {
            var filter = new FileIndexer(upperRoot, ignoreCase: true)
                .EvaluatePathFilter(lowerFile);

            Assert.Equal(FileIndexer.PathFilterKind.OutsideProjectRoot, filter.FilterKind);
        }
        finally
        {
            PathCasing.IgnoreCaseProbeForTesting = previousProbe;
        }
    }

    [Fact]
    public void ScanFiles_SkipsDirectorySymlinkPointingAtAncestor()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "foo.py"), "def hello(): pass\n");
        // Directory symlink pointing at the ancestor (self-recursion if followed).
        // 先祖を指すディレクトリ symlink（辿ると無限再帰になる）。
        Directory.CreateSymbolicLink(Path.Combine(subDir, "parent_loop"), "..");

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["sub/foo.py"], files);
    }

    [Fact]
    public void ScanFiles_FollowAllSymlinks_SkipsAlreadyVisitedDirectoryTarget()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "foo.py"), "def hello(): pass\n");
        Directory.CreateSymbolicLink(Path.Combine(subDir, "parent_loop"), "..");

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.All);
        var result = indexer.ScanFilesDetailed();
        var files = ToSortedRelativePaths(tempDir, result.Files);

        Assert.Equal(["sub/foo.py"], files);
        Assert.Contains(
            result.Errors,
            error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Path == "sub/parent_loop"
                && error.Message.Contains("already scanned", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_ExcessiveDirectoryDepth_SkipsSubtreeWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var current = tempDir;
        for (var i = 0; i < 130; i++)
        {
            current = Path.Combine(current, $"d{i:D3}");
            Directory.CreateDirectory(current);
        }
        File.WriteAllText(Path.Combine(current, "too_deep.py"), "print('too deep')\n");

        var result = new FileIndexer(tempDir).ScanFilesDetailed();

        Assert.Empty(result.Files);
        Assert.Contains(
            result.Errors,
            error => error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("traversal depth exceeded", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_SkipsFileSymlinkToRealFileInProject()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var nested = Path.Combine(tempDir, "a", "b", "c");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "foo.py"), "def hello(): pass\n");
        // File symlink that would otherwise cause the same content to be indexed under a second path.
        // 同じ内容が2つ目の path としても index されてしまうのを防ぐ確認。
        File.CreateSymbolicLink(Path.Combine(tempDir, "file_symlink.py"), Path.Combine("a", "b", "c", "foo.py"));

        var indexer = new FileIndexer(tempDir);
        var files = ScanRelativeFiles(indexer, tempDir);

        Assert.Equal(["a/b/c/foo.py"], files);
        var result = indexer.ScanFilesDetailed();
        Assert.DoesNotContain("file_symlink.py", result.DanglingSymlinks);
        Assert.DoesNotContain(
            result.Errors,
            error => error.Path == "file_symlink.py"
                && error.Message.Contains("dangling symlink", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFileIndexability_DefaultPolicyRejectsFileSymlinkButFollowPoliciesAllowIt()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var external = TestProjectHelper.CreateTempProjectScope("codeindex_external");
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var externalDir = external.Root;
        var tempDir = project.Root;
        var realFile = Path.Combine(tempDir, "real.py");
        File.WriteAllText(realFile, "x = 1\n");
        var externalFile = Path.Combine(externalDir, "external.py");
        File.WriteAllText(externalFile, "x = 2\n");
        var linkPath = Path.Combine(tempDir, "alias.py");
        File.CreateSymbolicLink(linkPath, realFile);
        var externalLinkPath = Path.Combine(tempDir, "external_alias.py");
        File.CreateSymbolicLink(externalLinkPath, externalFile);

        Assert.True(FileIndexer.CanIndexFile(realFile));
        Assert.False(FileIndexer.CanIndexFile(linkPath));
        Assert.Equal(
            FileIndexer.FileProbeStatus.Supported,
            FileIndexer.GetFileIndexability(linkPath, FileIndexer.SymlinkPolicy.Internal, tempDir));
        Assert.Equal(
            FileIndexer.FileProbeStatus.Supported,
            FileIndexer.GetFileIndexability(linkPath, FileIndexer.SymlinkPolicy.All, tempDir));
        Assert.Equal(
            FileIndexer.FileProbeStatus.Unsupported,
            FileIndexer.GetFileIndexability(externalLinkPath, FileIndexer.SymlinkPolicy.Internal, tempDir));
        Assert.Equal(
            FileIndexer.FileProbeStatus.Supported,
            FileIndexer.GetFileIndexability(externalLinkPath, FileIndexer.SymlinkPolicy.All, tempDir));
    }

    [Fact]
    public void GetFileIndexability_RejectsDirectoriesOnEveryPlatform()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var directory = Path.Combine(project.Root, "directory.cs");
        Directory.CreateDirectory(directory);

        Assert.Equal(
            FileIndexer.FileProbeStatus.Unsupported,
            FileIndexer.GetFileIndexability(directory));
        Assert.False(FileIndexer.CanIndexFile(directory));
    }

    [Fact]
    public void ScanFiles_FollowSymlinksAll_IndexesExternalFileSymlink()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var external = TestProjectHelper.CreateTempProjectScope("codeindex_external");
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var externalDir = external.Root;
        var tempDir = project.Root;
        var realFile = Path.Combine(externalDir, "real.py");
        File.WriteAllText(realFile, "x = 1\n");
        File.CreateSymbolicLink(Path.Combine(tempDir, "alias.py"), realFile);

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.All);
        var result = indexer.ScanFilesDetailed();
        var files = ToSortedRelativePaths(tempDir, result.Files);

        Assert.Equal(["alias.py"], files);
        Assert.DoesNotContain("alias.py", result.DanglingSymlinks);
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_IndexesInTreeFileSymlink()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var skippedTargetDir = Path.Combine(tempDir, "node_modules");
        Directory.CreateDirectory(skippedTargetDir);
        var realFile = Path.Combine(skippedTargetDir, "real.py");
        File.WriteAllText(realFile, "x = 1\n");
        File.CreateSymbolicLink(Path.Combine(tempDir, "alias.py"), realFile);

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);
        var result = indexer.ScanFilesDetailed();
        var files = ToSortedRelativePaths(tempDir, result.Files);

        Assert.Equal(["alias.py"], files);
        Assert.DoesNotContain("alias.py", result.DanglingSymlinks);
        Assert.False(result.HadErrors);
    }

    [Theory]
    [InlineData(@"\\.\COM1")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1")]
    [InlineData(@"//?/GLOBALROOT\Device/HarddiskVolumeShadowCopy1")]
    [InlineData(@"C:\repo\AUX.cs")]
    [InlineData(@"C:/repo/NUL.txt")]
    [InlineData(@"C:\repo\con.txt")]
    [InlineData(@"C:\repo\COM9")]
    [InlineData(@"C:\repo\LPT1.log")]
    public void IsWindowsDevicePath_RejectsReservedDeviceNames(string path)
    {
        Assert.True(FileIndexer.IsWindowsDevicePath(path));
    }

    [Theory]
    [InlineData(@"C:\repo\COM10.cs")]
    [InlineData(@"C:\repo\company.cs")]
    [InlineData(@"C:\repo\template1.cs")]
    public void IsWindowsDevicePath_AllowsOrdinaryNames(string path)
    {
        Assert.False(FileIndexer.IsWindowsDevicePath(path));
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint, false, true)]
    [InlineData(FileAttributes.ReparsePoint, true, true)]
    [InlineData(FileAttributes.Hidden, false, false)]
    [InlineData(FileAttributes.Hidden, true, true)]
    [InlineData(FileAttributes.System, false, false)]
    [InlineData(FileAttributes.System, true, true)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, true, true)]
    [InlineData(FileAttributes.Archive, true, false)]
    public void HasSkippedAttributes_RejectsWindowsHiddenAndSystemOnly(
        FileAttributes attributes,
        bool isWindows,
        bool expected)
    {
        Assert.Equal(expected, FileIndexer.HasSkippedAttributes(attributes, isWindows));
    }

    [Fact]
    public void ScanFiles_OnWindowsSkipsHiddenAndSystemEntries()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, "visible.py", "print('visible')\n");

        var hiddenFile = TestProjectHelper.WriteTextFile(tempDir, "hidden.py", "print('hidden')\n");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

        var systemFile = TestProjectHelper.WriteTextFile(tempDir, "system.py", "print('system')\n");
        File.SetAttributes(systemFile, File.GetAttributes(systemFile) | FileAttributes.System);

        var hiddenDir = TestProjectHelper.CreateDirectory(tempDir, "hidden_dir");
        TestProjectHelper.WriteTextFile(tempDir, "hidden_dir/nested.py", "print('hidden nested')\n");
        File.SetAttributes(hiddenDir, File.GetAttributes(hiddenDir) | FileAttributes.Hidden);

        var systemDir = TestProjectHelper.CreateDirectory(tempDir, "system_dir");
        TestProjectHelper.WriteTextFile(tempDir, "system_dir/nested.py", "print('system nested')\n");
        File.SetAttributes(systemDir, File.GetAttributes(systemDir) | FileAttributes.System);

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["visible.py"], files);
    }

    [Fact]
    public void ScanFiles_SkipsDanglingSymlinksWithoutAbortingScan()
    {
        if (OperatingSystem.IsWindows())
            return; // Creating symlinks on Windows requires admin/developer mode / Windows で symlink 作成には管理者権限が必要

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "real.py"), "def real(): pass\n");
        // Dangling symlinks (target does not exist) must be skipped without aborting the scan.
        // target が存在しない dangling symlink は、scan 全体を落とさずスキップする。
        File.CreateSymbolicLink(Path.Combine(tempDir, "dangling.py"), "missing_target.py");
        var missingDirectoryTarget = Path.Combine(tempDir, "missing_directory_target");
        Directory.CreateSymbolicLink(Path.Combine(tempDir, "dangling_dir"), missingDirectoryTarget);

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["real.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignorePatternsAndNegation()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "secret.py\nbuild_output/\n*.generated.js\n!keep.generated.js\n",
                ["keep.py"] = "print('keep')",
                ["secret.py"] = "print('secret')",
                ["app.generated.js"] = "export const ignored = true;",
                ["keep.generated.js"] = "export const kept = true;",
                ["build_output/inside.py"] = "print('ignored')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.generated.js", "keep.py"], files);
    }

    [Fact]
    public void ScanFiles_TrimsLeadingWhitespaceBeforeParsingIgnoreLines()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, ".gitignore", "  # comment\n  *.tmp\n\\ leading.py\n\\#literal.py\n", Encoding.UTF8);
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["keep.py"] = "print('keep')",
                ["ignored.tmp"] = "ignored",
                [" leading.py"] = "print('literal leading space')",
                ["#literal.py"] = "print('literal hash')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.py"], files);
    }

    [Fact]
    public void ScanFiles_ReportsOverlongIgnorePatternAndContinues()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, ".gitignore", $"{new string('a', 513)}\n*.tmp\n", Encoding.UTF8);
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["keep.py"] = "print('keep')",
                ["ignored.tmp"] = "ignored",
            });

        var result = new FileIndexer(tempDir).ScanFilesDetailed();
        var files = result.Files
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal([".gitignore", "keep.py"], files);
        var warning = Assert.Single(result.Errors);
        Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("pattern exceeds 512 characters", warning.Message);
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_RespectsCdidxignoreAndNestedGitignore()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".cdidxignore"] = "fixtures/\n*.cache.js\n",
                ["src/.gitignore"] = "*.generated.cs\n",
                ["src/Service.cs"] = "public class Service { }",
                ["src/Generated.generated.cs"] = "public class Generated { }",
                ["fixtures/sample.py"] = "print('fixture')",
                ["app.cache.js"] = "export const cache = true;",
                ["app.js"] = "export const app = true;",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["app.js", "src/.gitignore", "src/Service.cs"], files);
    }

    [Fact]
    public void ScanFiles_ReadsGitignoreAndCdidxignoreAsUtf8()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, ".gitignore", "資料/\n", Encoding.UTF8);
        TestProjectHelper.WriteTextFile(tempDir, ".cdidxignore", "cafe/éclair.py\n", Encoding.UTF8);
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["資料/ignored.py"] = "print('ignored')",
                ["cafe/éclair.py"] = "print('ignored')",
                ["keep.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsWorkspaceConfigCdidxignore()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".codeindex/.cdidxignore"] = "generated/\n*.cache.js\n",
                ["generated/Ignored.cs"] = "class Ignored { }",
                ["app.cache.js"] = "export const ignored = true;",
                ["app.js"] = "export const app = true;",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal(["app.js"], files);
    }

    [Fact]
    public void ScanFilesDetailed_SkipsNestedGitRepositoryBoundary()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        Directory.CreateDirectory(Path.Combine(tempDir, "nested", ".git"));
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Root.cs"] = "class Root { }",
                ["nested/Nested.cs"] = "class Nested { }",
            });

        var result = new FileIndexer(tempDir).ScanFilesDetailed();
        var files = ToSortedRelativePaths(tempDir, result.Files);

        Assert.Equal(["Root.cs"], files);
        Assert.Equal(["nested"], result.NestedRepositories);
    }

    [Fact]
    public void EvaluatePathFilter_SkipsNestedGitRepositoryBoundary()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        Directory.CreateDirectory(Path.Combine(tempDir, "nested", ".git"));
        var nestedFile = TestProjectHelper.WriteTextFile(tempDir, "nested/Nested.cs", "class Nested { }");

        var indexer = new FileIndexer(tempDir);
        var filter = indexer.EvaluatePathFilter(nestedFile);

        Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultDirectory, filter.FilterKind);
        Assert.True(filter.ShouldDeleteExisting);
    }

    [Fact]
    public void BuildRecord_NormalizesRelativePathToNfc()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var nfdName = "Cafe\u0301.cs";
        var filePath = TestProjectHelper.WriteTextFile(tempDir, nfdName, "class Cafe { }");

        var indexer = new FileIndexer(tempDir);
        var (record, _, _) = indexer.BuildRecord(filePath);

        Assert.Equal("Caf\u00e9.cs", record.Path);
        Assert.True(record.Path.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void ScanFiles_PreservesInheritedRulesWhenRootIgnoreFileIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var ignorePath = TestProjectHelper.WriteTextFile(tempDir, ".gitignore", "secret.py\n");
        UnixFileMode? originalMode = null;
        try
        {
            TestProjectHelper.WriteTextFiles(
                tempDir,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["secret.py"] = "print('secret')",
                    ["keep.py"] = "print('keep')",
                });
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();

            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal([".gitignore", "keep.py", "secret.py"], files);
            Assert.Contains(result.Errors, error =>
                error.Path == ".gitignore" &&
                error.Message == "Could not read .gitignore due to permissions." &&
                error.Severity == FileIndexer.ScanIssueSeverity.Warning);
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
        }
    }

    [Fact]
    public void ScanFiles_PreservesInheritedRulesWhenNestedIgnoreFileIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var ignorePath = TestProjectHelper.WriteTextFile(tempDir, "src/.gitignore", "secret.py\n");
        UnixFileMode? originalMode = null;
        try
        {
            TestProjectHelper.WriteTextFiles(
                tempDir,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["keep.py"] = "print('keep')",
                    ["src/secret.py"] = "print('secret')",
                    ["src/keep_nested.py"] = "print('nested keep')",
                });
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["keep.py", "src/.gitignore", "src/keep_nested.py", "src/secret.py"], files);
            Assert.Contains(result.Errors, error =>
                error.Path == "src/.gitignore" &&
                error.Message == "Could not read .gitignore due to permissions." &&
                error.Severity == FileIndexer.ScanIssueSeverity.Warning);
            Assert.False(result.HadErrors);
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
        }
    }

    [Fact]
    public void ScanFilesDetailed_LoadsFullAncestorIgnoreChainAndReportsIt()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var workspace = Path.Combine(tempDir, "workspace");
        var projects = Path.Combine(workspace, "projects");
        var projectRoot = Path.Combine(projects, "subA");
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workspace/.cdidxignore"] = "*.cs\n",
                ["workspace/projects/.gitignore"] = "!subA/App.cs\n",
                ["workspace/projects/subA/App.cs"] = "class App { }\n",
                ["workspace/projects/subA/Other.cs"] = "class Other { }\n",
            });

        var indexer = new FileIndexer(projectRoot, ignoreCase: false, ignoreRuleRoot: workspace);
        var result = indexer.ScanFilesDetailed();
        var files = ToSortedRelativePaths(projectRoot, result.Files);

        Assert.Equal(["App.cs"], files);
        Assert.Equal([workspace, projects], result.AncestorIgnoreDirectories);
        Assert.DoesNotContain(result.Errors, error => error.IsFatal);
    }

    [Fact]
    public void ScanFilesDetailed_FailsClosedWhenAncestorIgnoreDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var workspace = Path.Combine(tempDir, "workspace");
        var projects = Path.Combine(tempDir, "workspace", "projects");
        UnixFileMode? originalMode = null;
        try
        {
            var projectRoot = Path.Combine(projects, "subA");
            TestProjectHelper.WriteTextFiles(
                tempDir,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workspace/.cdidxignore"] = "*.cs\n",
                    ["workspace/projects/subA/App.cs"] = "class App { }\n",
                });
            originalMode = File.GetUnixFileMode(projects);
            SetUnixPermissions(projects, UnixFileMode.None);

            var indexer = new FileIndexer(projectRoot, ignoreCase: false, ignoreRuleRoot: workspace);
            var result = indexer.ScanFilesDetailed();

            Assert.Empty(result.Files);
            Assert.Contains(result.Errors, error =>
                error.Path == ".."
                && error.Message == "Could not read ancestor ignore directory: access-denied.");
            Assert.True(result.HadErrors);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(projects))
                SetUnixPermissions(projects, originalMode.Value);
        }
    }

    [Fact]
    public void ScanFilesDetailed_DoesNotMarkParentsFullyScannedWhenNestedDirectoryFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var srcDir = Path.Combine(tempDir, "src");
        var blockedDir = Path.Combine(srcDir, "blocked");
        UnixFileMode? originalMode = null;
        try
        {
            TestProjectHelper.WriteTextFiles(
                tempDir,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["keep.py"] = "print('keep')",
                    ["src/service.py"] = "print('service')",
                    ["src/blocked/secret.py"] = "print('secret')",
                });
            originalMode = File.GetUnixFileMode(blockedDir);
            SetUnixPermissions(blockedDir, UnixFileMode.None);

            var indexer = new FileIndexer(tempDir);
            var result = indexer.ScanFilesDetailed();
            var files = result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(["keep.py", "src/service.py"], files);
            Assert.Contains(result.Errors, error => error.Path == "src/blocked" && error.Message == "Could not scan directory due to permissions.");
            Assert.Contains("", result.ListedDirectories);
            Assert.DoesNotContain("", result.FullyScannedDirectories);
            Assert.DoesNotContain("src", result.FullyScannedDirectories);
            Assert.DoesNotContain("src/blocked", result.FullyScannedDirectories);
        }
        finally
        {
            if (originalMode.HasValue && Directory.Exists(blockedDir))
                SetUnixPermissions(blockedDir, originalMode.Value);
        }
    }

    [Fact]
    public void ScanFilesDetailed_CheckpointedDirectories_AreSkippedAndCarriedForward()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cached/old.py"] = "print('old')",
                ["fresh/new.py"] = "print('new')",
            });

        var result = new FileIndexer(tempDir).ScanFilesDetailed(
            new HashSet<string>(StringComparer.Ordinal) { "cached" });
        var files = result.Files
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["fresh/new.py"], files);
        Assert.Contains("cached", result.CheckpointedDirectories);
        Assert.Contains("fresh", result.CheckpointedDirectories);
        Assert.DoesNotContain("cached", result.ListedDirectories);
        Assert.DoesNotContain("cached", result.FullyScannedDirectories);
    }

    [Fact]
    public void ScanFilesDetailed_DirectoryListingSnapshotsAreOptInAndExcludeSkippedDirectories()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Program.cs"] = "class Program { }\n",
                ["src/Service.cs"] = "class Service { }\n",
                ["node_modules/pkg/Ignored.cs"] = "class Ignored { }\n",
            });

        var indexer = new FileIndexer(tempDir);
        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
        var capturedDirectories = capturedResult.InputSnapshot.DirectoryListings
            .Select(snapshot => snapshot.Path)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(Path.GetFullPath(tempDir), capturedDirectories);
        Assert.Contains(Path.GetFullPath(Path.Combine(tempDir, "src")), capturedDirectories);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(tempDir, "node_modules")), capturedDirectories);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(tempDir, "node_modules", "pkg")), capturedDirectories);
        Assert.DoesNotContain(
            capturedResult.InputSnapshot.ConfigurationInputs,
            input => input.Path == Path.Combine(tempDir, ".gitignore")
                || input.Path == Path.Combine(tempDir, ".cdidxignore"));
    }

    [Fact]
    public void ScanFilesDetailed_CapturesEachDirectoryListingWithOneBaselineProbe()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Program.cs"] = "public sealed class Program { }\n",
                ["src/Service.cs"] = "public sealed class Service { }\n",
            });
        var probeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        FileIndexer.DirectoryListingSnapshotProbeForTesting = path =>
        {
            var normalizedPath = Path.GetFullPath(path);
            probeCounts.TryGetValue(normalizedPath, out var count);
            probeCounts[normalizedPath] = count + 1;
        };
        try
        {
            var indexer = new FileIndexer(tempDir);
            var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.False(capturedResult.ScanResult.HadErrors);
            Assert.All(
                capturedResult.InputSnapshot.DirectoryListings,
                snapshot => Assert.Equal(1, probeCounts[snapshot.Path]));

            Assert.True(indexer.TryValidateScanInputSnapshot(
                capturedResult.InputSnapshot,
                out _));
            Assert.All(
                capturedResult.InputSnapshot.DirectoryListings,
                snapshot => Assert.Equal(2, probeCounts[snapshot.Path]));
        }
        finally
        {
            FileIndexer.DirectoryListingSnapshotProbeForTesting = null;
        }
    }

    [Fact]
    public void ScanFilesDetailed_InPlaceIgnoreChangeWithStableDirectoryMtimeIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var ignoredPath = Path.Combine(tempDir, "IHidden.cs");
        var ignorePath = Path.Combine(tempDir, ".gitignore");
        File.WriteAllText(ignoredPath, "public interface IHidden<T> { static abstract T Create(); }\n");
        File.WriteAllText(ignorePath, "IHidden.cs\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var changed = 0;
        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            enumerateFileSystemEntries: directory =>
            {
                var normalizedDirectory = Path.GetFullPath(LongPath.RemoveWindowsPrefix(directory));
                if (string.Equals(normalizedDirectory, tempDir, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref changed, 1) == 0)
                {
                    File.WriteAllText(ignorePath, "XHidden.cs\n");
                    Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc);
                }

                return Directory.EnumerateFileSystemEntries(normalizedDirectory).ToArray();
            });

        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Assert.Equal(1, changed);
        Assert.Equal(rootModifiedUtc, Directory.GetLastWriteTimeUtc(tempDir));
        Assert.False(capturedResult.ScanResult.HadErrors);
        Assert.True(capturedResult.InputSnapshot.IsComplete);
        Assert.DoesNotContain(ignoredPath, capturedResult.ScanResult.Files);
        Assert.False(indexer.TryValidateScanInputSnapshot(
            capturedResult.InputSnapshot,
            out var changedPath));
        Assert.Equal(ignorePath, changedPath);
    }

    [Fact]
    public void ScanFilesDetailed_PresentIgnoreDisappearingBeforeOpenIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var ignorePath = Path.Combine(tempDir, ".gitignore");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        File.WriteAllText(ignorePath, "ignored.cs\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var failedOpen = 0;
        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: path =>
            {
                var normalizedPath = Path.GetFullPath(LongPath.RemoveWindowsPrefix(path));
                if (string.Equals(normalizedPath, ignorePath, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref failedOpen, 1) == 0)
                {
                    File.Delete(ignorePath);
                    Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc.AddMinutes(1));
                    throw new FileNotFoundException("simulated ignore open race", path);
                }

                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            });

        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Assert.Equal(1, failedOpen);
        Assert.False(capturedResult.ScanResult.HadErrors);
        Assert.True(capturedResult.InputSnapshot.IsComplete);
        Assert.False(indexer.TryValidateScanInputSnapshot(
            capturedResult.InputSnapshot,
            out var changedPath));
        Assert.Equal(tempDir, changedPath);
    }

    [Fact]
    public void ScanInputSnapshot_DifferentLengthConfigurationAbaAfterMaterializationIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var ignorePath = Path.Combine(tempDir, ".gitignore");
        const string originalIgnore = "Program.cs\n";
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        File.WriteAllText(ignorePath, originalIgnore);
        var ignoreModifiedUtc = File.GetLastWriteTimeUtc(ignorePath);
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var indexer = new FileIndexer(tempDir);
        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
        Assert.False(capturedResult.ScanResult.HadErrors);

        File.WriteAllText(ignorePath, "Program.cs\nGenerated.cs\n");
        _ = indexer.EvaluatePathFilter(Path.Combine(tempDir, "Other.cs"));
        File.WriteAllText(ignorePath, originalIgnore);
        File.SetLastWriteTimeUtc(ignorePath, ignoreModifiedUtc);
        Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc);

        Assert.False(indexer.TryValidateScanInputSnapshot(
            capturedResult.InputSnapshot,
            out var changedPath));
        Assert.Equal(ignorePath, changedPath);
    }

    [Fact]
    public void ScanInputSnapshot_MissingConfigurationReplacedByDirectoryIsNotAbsent()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var gitmodulesPath = Path.Combine(tempDir, ".gitmodules");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var indexer = new FileIndexer(tempDir);
        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Directory.CreateDirectory(gitmodulesPath);
        Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc);

        Assert.False(indexer.TryValidateScanInputSnapshot(
            capturedResult.InputSnapshot,
            out var changedPath));
        Assert.Equal(gitmodulesPath, changedPath);
    }

    [Fact]
    public void ScanInputSnapshot_MissingConfigurationReplacedByDanglingLinkIsNotAbsent()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var gitmodulesPath = Path.Combine(tempDir, ".gitmodules");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var indexer = new FileIndexer(tempDir);
        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
        try
        {
            File.CreateSymbolicLink(gitmodulesPath, Path.Combine(tempDir, "missing-gitmodules-target"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }
        Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc);

        Assert.False(indexer.TryValidateScanInputSnapshot(
            capturedResult.InputSnapshot,
            out var changedPath));
        Assert.Equal(gitmodulesPath, changedPath);
    }

    [Fact]
    public void ScanFilesDetailed_NonCapturingScanCallDoesNotObserveOrHashConfigurationInputs()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var ignorePath = Path.Combine(tempDir, ".gitignore");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        File.WriteAllText(ignorePath, "Generated.cs\n");
        var indexer = new FileIndexer(tempDir);
        var generationBefore = indexer.ConfigurationInputSnapshotGenerationForTesting;
        var hashesBefore = indexer.ConfigurationInputContentHashCountForTesting;

        var ordinaryResult = indexer.ScanFilesDetailed();

        Assert.False(ordinaryResult.HadErrors);
        Assert.Equal(generationBefore, indexer.ConfigurationInputSnapshotGenerationForTesting);
        Assert.Equal(hashesBefore, indexer.ConfigurationInputContentHashCountForTesting);

        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
        Assert.False(capturedResult.ScanResult.HadErrors);
        Assert.True(indexer.ConfigurationInputSnapshotGenerationForTesting > generationBefore);
        Assert.True(indexer.ConfigurationInputContentHashCountForTesting > hashesBefore);
        Assert.Contains(
            capturedResult.InputSnapshot.ConfigurationInputs,
            input => input.Path == ignorePath && input.ContentHash is { Length: > 0 });
    }

    [Fact]
    public void ScanFilesDetailed_PatternDirectoryProbeCountIsBoundedByUniqueDirectories()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        for (var index = 0; index < 100; index++)
            File.WriteAllText(Path.Combine(tempDir, $"sample-{index}.__cdidx_probe_unmapped_4711"), "content\n");

        var indexer = new FileIndexer(tempDir);
        var probesBefore = indexer.PatternConfigurationDirectoryProbeCountForTesting;

        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Assert.False(capturedResult.ScanResult.HadErrors);
        var scanProbeCount = indexer.PatternConfigurationDirectoryProbeCountForTesting - probesBefore;
        Assert.InRange(scanProbeCount, 1, 2);
    }

    [Theory]
    [InlineData("enumerator_creation")]
    [InlineData("enumerator_move_next")]
    [InlineData("safety_inspection")]
    public void ScanFilesDetailed_PatternDirectoryDiscoveryFailureMakesSnapshotIncomplete(string failureMode)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var cdidxDirectory = Path.Combine(tempDir, ".cdidx");
        var patternDirectory = Path.Combine(cdidxDirectory, "patterns");
        var patternPath = Path.Combine(patternDirectory, "custom.yaml");
        Directory.CreateDirectory(patternDirectory);
        File.WriteAllText(
            patternPath,
            "language: custom\nextensions:\n  - extension: .custom\npatterns:\n  - kind: class\n    regex: ^class (?<name>[A-Za-z_]+)\n");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");

        ExtractorPluginRegistry.ResetForTests();
        ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests =
            Path.Combine(tempDir, "missing-user-patterns");
        var expectedIncompletePath = patternDirectory;
        switch (failureMode)
        {
            case "enumerator_creation":
                ExtractorPluginRegistry.EnumeratePatternFilesForTesting = (_, _) =>
                    throw new IOException("simulated pattern enumerator creation failure");
                break;
            case "enumerator_move_next":
                ExtractorPluginRegistry.EnumeratePatternFilesForTesting = (_, searchPattern) =>
                    searchPattern == "*.yaml"
                        ? EnumeratePatternFileThenFail(patternPath)
                        : Array.Empty<string>();
                break;
            case "safety_inspection":
                expectedIncompletePath = cdidxDirectory;
                ExtractorPluginRegistry.InspectPatternDirectoryForTesting = _ =>
                    throw new IOException("simulated pattern directory inspection failure");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failureMode), failureMode, null);
        }

        try
        {
            var capturedResult = new FileIndexer(tempDir)
                .ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.True(capturedResult.ScanResult.HadErrors);
            Assert.False(capturedResult.InputSnapshot.IsComplete);
            Assert.Equal(expectedIncompletePath, capturedResult.InputSnapshot.IncompletePath);
        }
        finally
        {
            ExtractorPluginRegistry.ResetForTests();
        }

        static IEnumerable<string> EnumeratePatternFileThenFail(string path)
        {
            yield return path;
            throw new IOException("simulated pattern enumerator MoveNext failure");
        }
    }

    [Fact]
    public void ScanInputSnapshot_UserPatternDirectoryUnderSkippedProjectPathBindsMissingDirectory()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var userPatternDirectory = Path.Combine(tempDir, ".config", "cdidx", "patterns");
        File.WriteAllText(Path.Combine(tempDir, "sample.__cdidx_user_pattern_probe"), "content\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        ExtractorPluginRegistry.ResetForTests();
        ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests = userPatternDirectory;
        try
        {
            var indexer = new FileIndexer(tempDir);
            var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
            Assert.False(capturedResult.ScanResult.HadErrors);
            Assert.Contains(
                capturedResult.InputSnapshot.ConfigurationInputs,
                input => input.Path == userPatternDirectory
                    && input.Kind == FileIndexer.ConfigurationInputKind.MissingDirectory);

            Directory.CreateDirectory(userPatternDirectory);
            Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc);

            Assert.False(indexer.TryValidateScanInputSnapshot(
                capturedResult.InputSnapshot,
                out var changedPath));
            Assert.Equal(userPatternDirectory, changedPath);
        }
        finally
        {
            ExtractorPluginRegistry.UserPatternDirectoryOverrideForTests = null;
            ExtractorPluginRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ScanInputSnapshot_UserLanguageMapUnderSkippedProjectPathBindsMissingFile()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var userLanguageMapPath = Path.Combine(tempDir, ".config", "cdidx", "langmap.yaml");
        File.WriteAllText(Path.Combine(tempDir, "sample.__cdidx_user_langmap_probe"), "content\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        LanguageMapOverrides.UserConfigPathOverrideForTesting = userLanguageMapPath;
        try
        {
            var indexer = new FileIndexer(tempDir);
            var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
            Assert.False(capturedResult.ScanResult.HadErrors);
            Assert.Contains(
                capturedResult.InputSnapshot.ConfigurationInputs,
                input => input.Path == userLanguageMapPath
                    && input.Kind == FileIndexer.ConfigurationInputKind.MissingFile);

            Directory.CreateDirectory(Path.GetDirectoryName(userLanguageMapPath)!);
            File.WriteAllText(userLanguageMapPath, ".__cdidx_user_langmap_probe: custom\n");
            Directory.SetLastWriteTimeUtc(tempDir, rootModifiedUtc);

            Assert.False(indexer.TryValidateScanInputSnapshot(
                capturedResult.InputSnapshot,
                out var changedPath));
            Assert.Equal(userLanguageMapPath, changedPath);
        }
        finally
        {
            LanguageMapOverrides.UserConfigPathOverrideForTesting = null;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        }
    }

    [Fact]
    public void FileIndexer_PatternConfigSymlinkIsRejectedBySafeRegistryOpen()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var patternDirectory = Path.Combine(tempDir, ".cdidx", "patterns");
        Directory.CreateDirectory(patternDirectory);
        var targetPath = Path.Combine(tempDir, "pattern-target.yaml");
        File.WriteAllText(
            targetPath,
            "language: symlinkdsl\nextensions:\n  - extension: .__cdidx_symlink_pattern\npatterns:\n  - kind: class\n    regex: ^entity (?<name>\\w+)\n");
        var linkPath = Path.Combine(patternDirectory, "symlink.yaml");
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        ExtractorPluginRegistry.ResetForTests();
        try
        {
            var capturedResult = new FileIndexer(tempDir)
                .ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.False(capturedResult.ScanResult.HadErrors);
            Assert.False(ExtractorPluginRegistry.TryGetLanguageForExtension(
                ".__cdidx_symlink_pattern",
                tempDir,
                out _));
            Assert.DoesNotContain(
                capturedResult.InputSnapshot.ConfigurationInputs,
                input => input.Path == linkPath && input.ContentHash is { Length: > 0 });
        }
        finally
        {
            ExtractorPluginRegistry.ResetForTests();
        }
    }

    [Fact]
    public void ScanFilesDetailed_NestedRepositoryMarkerRemovedAfterDecisionIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var nestedDirectory = Path.Combine(tempDir, "nested");
        var markerPath = Path.Combine(nestedDirectory, ".git");
        Directory.CreateDirectory(markerPath);
        File.WriteAllText(Path.Combine(nestedDirectory, "Hidden.cs"), "public sealed class Hidden { }\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var removed = 0;
        FileIndexer.NestedRepositoryDetectedBeforeSnapshotForTesting = observedPath =>
        {
            if (observedPath == markerPath && Interlocked.Exchange(ref removed, 1) == 0)
                Directory.Delete(markerPath);
        };
        try
        {
            var indexer = new FileIndexer(tempDir);
            var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.Equal(1, removed);
            Assert.Equal(rootModifiedUtc, Directory.GetLastWriteTimeUtc(tempDir));
            Assert.True(capturedResult.ScanResult.HadErrors);
            Assert.DoesNotContain(Path.Combine(nestedDirectory, "Hidden.cs"), capturedResult.ScanResult.Files);
            Assert.False(capturedResult.InputSnapshot.IsComplete);
            Assert.Equal(markerPath, capturedResult.InputSnapshot.IncompletePath);
        }
        finally
        {
            FileIndexer.NestedRepositoryDetectedBeforeSnapshotForTesting = null;
        }
    }

    [Fact]
    public void ScanFilesDetailed_NestedRepositoryMarkerCreatedAfterListingBaselineIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var nestedDirectory = Path.Combine(tempDir, "nested");
        var markerPath = Path.Combine(nestedDirectory, ".git");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "Hidden.cs"), "public sealed class Hidden { }\n");
        var rootModifiedUtc = Directory.GetLastWriteTimeUtc(tempDir);
        var nestedModifiedUtc = Directory.GetLastWriteTimeUtc(nestedDirectory);
        var created = 0;
        FileIndexer.NestedRepositoryListingCapturedBeforeProbeForTesting = observedPath =>
        {
            if (observedPath == markerPath && Interlocked.Exchange(ref created, 1) == 0)
            {
                Directory.CreateDirectory(markerPath);
                Directory.SetLastWriteTimeUtc(nestedDirectory, nestedModifiedUtc.AddMinutes(1));
            }
        };
        try
        {
            var indexer = new FileIndexer(tempDir);
            var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.Equal(1, created);
            Assert.Equal(rootModifiedUtc, Directory.GetLastWriteTimeUtc(tempDir));
            Assert.False(capturedResult.ScanResult.HadErrors);
            Assert.DoesNotContain(Path.Combine(nestedDirectory, "Hidden.cs"), capturedResult.ScanResult.Files);
            Assert.True(capturedResult.InputSnapshot.IsComplete);
            Assert.False(indexer.TryValidateScanInputSnapshot(
                capturedResult.InputSnapshot,
                out var changedPath));
            Assert.Equal(nestedDirectory, changedPath);
        }
        finally
        {
            FileIndexer.NestedRepositoryListingCapturedBeforeProbeForTesting = null;
        }
    }

    [Fact]
    public void ScanFilesDetailed_NestedRepositoryDecisionCacheIsResetForEachScan()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var nestedDirectory = Path.Combine(tempDir, "nested");
        var nestedFile = Path.Combine(nestedDirectory, "Hidden.cs");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(nestedFile, "public sealed class Hidden { }\n");
        var indexer = new FileIndexer(tempDir);

        var firstResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();
        Assert.False(firstResult.ScanResult.HadErrors);
        Assert.Contains(nestedFile, firstResult.ScanResult.Files);

        Directory.CreateDirectory(Path.Combine(nestedDirectory, ".git"));
        var secondResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Assert.False(secondResult.ScanResult.HadErrors);
        Assert.DoesNotContain(nestedFile, secondResult.ScanResult.Files);
        Assert.Contains("nested", secondResult.ScanResult.NestedRepositories);
    }

    [Fact]
    public void ScanFilesDetailed_DirectoryListingSnapshotLimitReportsBoundedReason()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        TestProjectHelper.WriteTextFiles(
            project.Root,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Program.cs"] = "public sealed class Program { }\n",
                ["src/Service.cs"] = "public sealed class Service { }\n",
            });
        FileIndexer.MaxDirectoryListingSnapshotsForTesting = 1;
        try
        {
            var capturedResult = new FileIndexer(project.Root)
                .ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.True(capturedResult.ScanResult.HadErrors);
            Assert.False(capturedResult.InputSnapshot.IsComplete);
            Assert.Contains(
                "Directory listing snapshot count limit (1) was exceeded",
                capturedResult.InputSnapshot.IncompleteReason,
                StringComparison.Ordinal);
            Assert.Contains(
                capturedResult.ScanResult.Errors,
                error => error.Message.Contains(
                    "Directory listing snapshot count limit (1) was exceeded",
                    StringComparison.Ordinal));
        }
        finally
        {
            FileIndexer.MaxDirectoryListingSnapshotsForTesting = null;
        }
    }

    [Fact]
    public void ScanFilesDetailed_ConfigurationInputSnapshotCountLimitReportsBoundedReason()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        File.WriteAllText(Path.Combine(project.Root, "Program.cs"), "public sealed class Program { }\n");
        FileIndexer.MaxConfigurationInputSnapshotsForTesting = 1;
        try
        {
            var capturedResult = new FileIndexer(project.Root)
                .ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.True(capturedResult.ScanResult.HadErrors);
            Assert.False(capturedResult.InputSnapshot.IsComplete);
            Assert.Contains(
                "Configuration input snapshot count limit (1) was exceeded",
                capturedResult.InputSnapshot.IncompleteReason,
                StringComparison.Ordinal);
            Assert.Contains(
                capturedResult.ScanResult.Errors,
                error => error.Message.Contains(
                    "Configuration input snapshot count limit (1) was exceeded",
                    StringComparison.Ordinal));
        }
        finally
        {
            FileIndexer.MaxConfigurationInputSnapshotsForTesting = null;
        }
    }

    [Fact]
    public void ScanFilesDetailed_ConfigurationInputSnapshotByteLimitReportsBoundedReason()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        File.WriteAllText(Path.Combine(project.Root, "Program.cs"), "public sealed class Program { }\n");
        File.WriteAllText(Path.Combine(project.Root, ".gitignore"), "Generated.cs\n");
        FileIndexer.MaxConfigurationInputSnapshotBytesForTesting = 1;
        try
        {
            var capturedResult = new FileIndexer(project.Root)
                .ScanFilesDetailedWithDirectoryListingSnapshots();

            Assert.True(capturedResult.ScanResult.HadErrors);
            Assert.False(capturedResult.InputSnapshot.IsComplete);
            Assert.Contains(
                "Configuration input snapshot byte limit (1 bytes) was exceeded",
                capturedResult.InputSnapshot.IncompleteReason,
                StringComparison.Ordinal);
            Assert.Contains(
                capturedResult.ScanResult.Errors,
                error => error.Message.Contains(
                    "Configuration input snapshot byte limit (1 bytes) was exceeded",
                    StringComparison.Ordinal));
        }
        finally
        {
            FileIndexer.MaxConfigurationInputSnapshotBytesForTesting = null;
        }
    }

    [Fact]
    public void ScanInputSnapshot_OversizePatternConfigShrinkingToValidIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var patternDirectory = Path.Combine(tempDir, ".cdidx", "patterns");
        Directory.CreateDirectory(patternDirectory);
        var patternPath = Path.Combine(patternDirectory, "oversize.yaml");
        File.WriteAllText(patternPath, new string('#', ExtractorPluginRegistry.MaxPatternConfigBytes + 1));
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        var patternDirectoryModifiedUtc = Directory.GetLastWriteTimeUtc(patternDirectory);
        var indexer = new FileIndexer(tempDir);
        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Assert.False(capturedResult.ScanResult.HadErrors);
        Assert.Contains(
            capturedResult.InputSnapshot.ConfigurationInputs,
            input => input.Path == patternPath
                && input.Kind == FileIndexer.ConfigurationInputKind.RejectedOversizeFile);

        File.WriteAllText(
            patternPath,
            "language: custom\nextensions:\n  - extension: .custom\npatterns:\n  - kind: class\n    regex: ^class (?<name>[A-Za-z_]+)\n");
        Directory.SetLastWriteTimeUtc(patternDirectory, patternDirectoryModifiedUtc);

        Assert.False(indexer.TryValidateScanInputSnapshot(
            capturedResult.InputSnapshot,
            out var changedPath));
        Assert.Equal(patternPath, changedPath);
    }

    [Fact]
    public void ScanFilesDetailed_EnumeratedPatternConfigDisappearingBeforeOpenIsNotAuthoritative()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        var patternDirectory = Path.Combine(tempDir, ".cdidx", "patterns");
        Directory.CreateDirectory(patternDirectory);
        var patternPath = Path.Combine(patternDirectory, "custom.yaml");
        File.WriteAllText(
            patternPath,
            "language: custom\nextensions:\n  - extension: .custom\npatterns:\n  - kind: class\n    regex: class\\s+(?&lt;name&gt;[A-Za-z_]+)\n");
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "public sealed class Program { }\n");
        var patternDirectoryModifiedUtc = Directory.GetLastWriteTimeUtc(patternDirectory);
        var failedOpen = 0;
        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: path =>
            {
                var normalizedPath = Path.GetFullPath(LongPath.RemoveWindowsPrefix(path));
                if (string.Equals(normalizedPath, patternPath, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref failedOpen, 1) == 0)
                {
                    File.Delete(patternPath);
                    Directory.SetLastWriteTimeUtc(patternDirectory, patternDirectoryModifiedUtc);
                    throw new FileNotFoundException("simulated pattern open race", path);
                }

                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            },
            bindConfigurationReadsToFileSystemIdentity: true);

        var capturedResult = indexer.ScanFilesDetailedWithDirectoryListingSnapshots();

        Assert.Equal(1, failedOpen);
        Assert.Equal(patternDirectoryModifiedUtc, Directory.GetLastWriteTimeUtc(patternDirectory));
        Assert.True(capturedResult.ScanResult.HadErrors);
        Assert.False(capturedResult.InputSnapshot.IsComplete);
        Assert.Equal(patternPath, capturedResult.InputSnapshot.IncompletePath);
    }

    [Fact]
    public void ScanFilesDetailed_SubmoduleAncestorPassthroughCapturesListingSnapshot()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = Path.GetFullPath(project.Root);
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitmodules"] = "[submodule \"foo\"]\n    path = vendor/foo\n",
                ["vendor/foo/Library.cs"] = "public sealed class Library { }\n",
            });

        var capturedResult = new FileIndexer(tempDir)
            .ScanFilesDetailedWithDirectoryListingSnapshots();
        var passthroughPath = Path.Combine(tempDir, "vendor");

        Assert.False(capturedResult.ScanResult.HadErrors);
        Assert.Contains(
            capturedResult.InputSnapshot.DirectoryListings,
            snapshot => snapshot.Path == passthroughPath);
        Assert.Contains(Path.Combine(tempDir, "vendor", "foo", "Library.cs"), capturedResult.ScanResult.Files);
    }

    [Fact]
    public void ScanFilesDetailed_ReturnsLanguageCounts()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Program.cs"] = "class Program {}",
                ["Service.cs"] = "class Service {}",
                ["script.py"] = "print('hello')",
            });

        var result = new FileIndexer(tempDir).ScanFilesDetailed();

        Assert.Equal(2, result.LanguageCounts["csharp"]);
        Assert.Equal(1, result.LanguageCounts["python"]);
        Assert.False(result.LanguageCounts.ContainsKey("ruby"));
    }

    [Fact]
    public void ScanFiles_RespectsRootAnchoredGitignorePatterns()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "/root_only_dir/\n/secret.py\n",
                ["root_only_dir/root.py"] = "print('ignored root dir')",
                ["secret.py"] = "print('ignored root file')",
                ["keep.py"] = "print('kept root file')",
                ["src/root_only_dir/nested.py"] = "print('kept nested dir')",
                ["src/secret.py"] = "print('kept nested file')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.py", "src/root_only_dir/nested.py", "src/secret.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGlobstarPrefixPatternAtProjectRoot()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "**/*.min.js\n",
                ["app.min.js"] = "export const ignored = true;",
                ["nested/lib.min.js"] = "export const nestedIgnored = true;",
                ["app.js"] = "export const kept = true;",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "app.js"], files);
    }

    [Fact]
    public void ScanFiles_HandlesGitIgnoreWhitespaceLikeGit()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "  #*.py\n  *.py\n*.cs\t\n",
                ["  #x.py"] = "print('kept because leading-space # is a comment')",
                ["a.py"] = "print('ignored after leading-space trim')",
                ["  a.py"] = "print('ignored by trimmed basename pattern')",
                ["a.cs"] = "public class IgnoredAfterTrailingTabTrim { }",
                ["keep.js"] = "export const kept = true;",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.js"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGlobstarMiddlePatternWithZeroOrMoreDirectories()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "foo/**/bar.py\n",
                ["foo/bar.py"] = "print('ignored shallow')",
                ["foo/deep/bar.py"] = "print('ignored deep')",
                ["foo/keep.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "foo/keep.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsTrailingGlobstarWithoutIgnoringRootDirectoryItself()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "foo/**\n!foo/bar.py\n",
                ["foo/bar.py"] = "print('keep')",
                ["foo/nested/ignored.py"] = "print('ignored')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "foo/bar.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsTrailingGlobstarDirectoryPatternWithoutIgnoringRootDirectoryItself()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "foo/**/\n!foo/bar.py\n",
                ["foo/bar.py"] = "print('keep')",
                ["foo/keep.py"] = "print('keep')",
                ["foo/bar/ignored.py"] = "print('ignored')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "foo/bar.py", "foo/keep.py"], files);
    }

    [Fact]
    public void ScanFiles_TreatsNonSpecialDoubleStarAsSingleSegmentWildcard()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "dir/a**b.py\n",
                ["dir/ab.py"] = "print('ignored')",
                ["dir/axxb.py"] = "print('ignored')",
                ["dir/a/x/b.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "dir/a/x/b.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitIgnoreCaseSettingFromRepository()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        RunGit(tempDir, "init");
        RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
        RunGit(tempDir, "config", "user.email", "tests@example.com");
        RunGit(tempDir, "config", "core.ignorecase", "true");
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "FOO.py\n",
                ["foo.py"] = "print('ignored')",
                ["keep.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(new FileIndexer(tempDir, GitHelper.ResolveIgnoreCase(tempDir)), tempDir);

        Assert.Equal([".gitignore", "keep.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitIgnoreCaseSettingForAsciiOnly()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        RunGit(tempDir, "init");
        RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
        RunGit(tempDir, "config", "user.email", "tests@example.com");
        RunGit(tempDir, "config", "core.ignorecase", "true");
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "Å.py\n[[:upper:]].rb\n[A-Z].cs\n",
                ["å.py"] = "print('kept non-ascii fold')",
                ["a.rb"] = "puts 'ignored lower via ignorecase'",
                ["å.rb"] = "puts 'kept non-ascii lower'",
                ["a.cs"] = "class IgnoredLower { }",
                ["å.cs"] = "class KeptLower { }",
            });

        var files = ScanRelativeFiles(new FileIndexer(tempDir, GitHelper.ResolveIgnoreCase(tempDir)), tempDir);

        Assert.Equal([".gitignore", "å.cs", "å.py", "å.rb"], files);
    }

    [Fact]
    public void ScanFiles_SubdirectoryProjectRoot_RespectsAncestorGitignore()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var projectRoot = Path.Combine(tempDir, "subproj");
        RunGit(tempDir, "init");
        RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
        RunGit(tempDir, "config", "user.email", "tests@example.com");
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "subproj/ignored.py\n",
                ["subproj/ignored.py"] = "print('ignored')",
                ["subproj/keep.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(
            new FileIndexer(projectRoot, GitHelper.ResolveIgnoreCase(projectRoot), GitHelper.TryGetRepositoryRoot(projectRoot)),
            projectRoot);

        Assert.Equal(["keep.py"], files);
    }

    [Fact]
    public void ScanFiles_SubdirectoryProjectRoot_RespectsAncestorGitignoreDirectoryRule()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var projectRoot = Path.Combine(tempDir, "subproj");
        Directory.CreateDirectory(projectRoot);
        RunGit(tempDir, "init");
        RunGit(tempDir, "config", "user.name", "CodeIndex Tests");
        RunGit(tempDir, "config", "user.email", "tests@example.com");
        File.WriteAllText(Path.Combine(tempDir, ".gitignore"), "subproj/\n");
        File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('ignored root dir')");

        var files = ScanRelativeFiles(
            new FileIndexer(projectRoot, GitHelper.ResolveIgnoreCase(projectRoot), GitHelper.TryGetRepositoryRoot(projectRoot)),
            projectRoot);

        Assert.Empty(files);
    }

    [Fact]
    public void ScanFiles_ProjectRootNamedNodeModules_IsIndexedButNestedSkipDirsRemainSkipped()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var projectRoot = Path.Combine(tempDir, "node_modules");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "app.js"), "console.log('ignored root dir');");
        Directory.CreateDirectory(Path.Combine(projectRoot, "node_modules"));
        File.WriteAllText(Path.Combine(projectRoot, "node_modules", "nested.js"), "console.log('skip child');");

        var files = ScanRelativeFiles(projectRoot);

        Assert.Equal(["app.js"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreBracketCharacterClassesAndRanges()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[ab].cs\nfile[0-9].py\n",
                ["a.cs"] = "class A { }",
                ["b.cs"] = "class B { }",
                ["c.cs"] = "class C { }",
                ["file1.py"] = "print('ignored')",
                ["filex.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "c.cs", "filex.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreNegatedBracketCharacterClass()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[!a].cs\n",
                ["a.cs"] = "class A { }",
                ["b.cs"] = "class B { }",
                ["c.cs"] = "class C { }",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "a.cs"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreBracketCharacterClassWithLeadingLiteralRightBracket()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[]].cs\n",
                ["].cs"] = "class Ignored { }",
                ["keep.cs"] = "class Kept { }",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.cs"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreAsciiPosixDigitBracketCharacterClass()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[[:digit:]].py\n",
                ["1.py"] = "print('ignored')",
                ["١.py"] = "print('kept non-ascii digit')",
                ["a.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "a.py", "١.py"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreAsciiPosixUpperBracketCharacterClass()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[[:upper:]].cs\n",
                ["A.cs"] = "class Ignored { }",
                ["É.cs"] = "class KeptNonAscii { }",
                ["keep.cs"] = "class Kept { }",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.cs", "É.cs"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignorePosixPunctBracketCharacterClass()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[[:punct:]].cs\n",
                ["!.cs"] = "class Ignored { }",
                ["a.cs"] = "class Kept { }",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "a.cs"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreNegatedBracketCharacterClassWithLeadingLiteralRightBracket()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[!]].cs\n",
                ["].cs"] = "class Kept { }",
                ["a.cs"] = "class Ignored { }",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "].cs"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreBracketNegationPrefixesAndLiteralCaret()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[!a].js\n[^a].py\n[a^b].cs\n[\\^a].rb\n",
                ["a.js"] = "export const kept = true;",
                ["b.js"] = "export const ignored = true;",
                ["a.py"] = "print('kept')",
                ["b.py"] = "print('ignored')",
                ["a.cs"] = "class IgnoredA { }",
                ["^.cs"] = "class IgnoredCaret { }",
                ["c.cs"] = "class Kept { }",
                ["a.rb"] = "puts 'ignored'",
                ["^.rb"] = "puts 'ignored'",
                ["b.rb"] = "puts 'kept'",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "a.js", "a.py", "b.rb", "c.cs"], files);
    }

    [Fact]
    public void ScanFiles_RespectsGitignoreEscapedLiteralCharacters()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "foo\\ bar.py\nliteral\\[name\\].js\n\\#literal.txt\n\\!important.cs\n",
                ["foo bar.py"] = "print('ignored')",
                ["literal[name].js"] = "export const ignored = true;",
                ["#literal.txt"] = "ignored",
                ["!important.cs"] = "class Ignored { }",
                ["keep.py"] = "print('kept')",
            });

        var files = ScanRelativeFiles(tempDir);

        Assert.Equal([".gitignore", "keep.py"], files);
    }

    [Fact]
    public void ScanFilesDetailed_SkipsMalformedIgnoreRulesWithoutAborting()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "[z-a].py\n[!].cs\n[a.py\n[!a\n[^\n[\n[]\nignored.py\n",
                ["[a.py"] = "print('kept malformed literal')",
                ["ignored.py"] = "print('ignored')",
                ["keep.py"] = "print('kept')",
            });

        var indexer = new FileIndexer(tempDir);
        var scanResult = indexer.ScanFilesDetailed();
        var files = scanResult.Files
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Equal([".gitignore", "[a.py", "keep.py"], files);
        Assert.Equal(7, scanResult.Errors.Count);
        Assert.All(scanResult.Errors, error => Assert.Contains(".gitignore:", error.Path, StringComparison.Ordinal));
        Assert.All(scanResult.Errors, error => Assert.Contains("Invalid ignore rule skipped", error.Message, StringComparison.Ordinal));
        Assert.Contains(scanResult.Errors, error => error.Message == "Invalid ignore rule skipped: reversed character class range");
        Assert.All(scanResult.Errors, error => Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, error.Severity));
    }

    [Fact]
    public void ScanFilesDetailed_SeparatesUnmappedLanguageFilesFromOtherNonIndexableFiles_Issue5100()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [".gitignore"] = "ignored.mystery\n",
                ["app.cs"] = "class App { }\n",
                ["Dockerfile.dev"] = "FROM scratch\n",
                ["script"] = "#!/usr/bin/env python\nprint('hello')\n",
                ["_cdidx"] = "#compdef cdidx\n_cdidx() {}\n",
                ["tool"] = "plain text without a shebang\n",
                ["data.mystery"] = "unknown extension\n",
                ["ignored.mystery"] = "ignored unknown extension\n",
            });
        File.WriteAllBytes(
            TestProjectHelper.ProjectPath(tempDir, "blob.bf"),
            [0x42, 0x00, 0x46]);
        var appPath = TestProjectHelper.ProjectPath(tempDir, "app.cs");
        var scriptPath = TestProjectHelper.ProjectPath(tempDir, "script");
        var completionPath = TestProjectHelper.ProjectPath(tempDir, "_cdidx");
        var toolPath = TestProjectHelper.ProjectPath(tempDir, "tool");
        var dataPath = TestProjectHelper.ProjectPath(tempDir, "data.mystery");
        var ignoredPath = TestProjectHelper.ProjectPath(tempDir, "ignored.mystery");

        var indexer = new FileIndexer(tempDir);
        var scanResult = indexer.ScanFilesDetailed();

        Assert.Equal(["data.mystery", "tool"], scanResult.UnknownExtensionFiles);
        Assert.Equal("csharp", scanResult.FileLanguages[appPath]);
        Assert.Equal("python", scanResult.FileLanguages[scriptPath]);
        Assert.Equal("shell", scanResult.FileLanguages[completionPath]);
        Assert.DoesNotContain("_cdidx", scanResult.UnknownExtensionFiles);
        Assert.DoesNotContain(toolPath, scanResult.FileLanguages.Keys);
        Assert.DoesNotContain(dataPath, scanResult.FileLanguages.Keys);
        Assert.DoesNotContain(ignoredPath, scanResult.FileLanguages.Keys);
        Assert.Contains("data.mystery", scanResult.NonIndexablePaths);
        Assert.Contains("tool", scanResult.NonIndexablePaths);
        Assert.Contains("tool", scanResult.UnknownExtensionFiles);
        Assert.DoesNotContain("blob.bf", scanResult.UnknownExtensionFiles);
        Assert.Contains("blob.bf", scanResult.NonIndexablePaths);
        Assert.DoesNotContain("ignored.mystery", scanResult.UnknownExtensionFiles);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ScanFilesDetailed_UnknownLanguageProbe_UsesOneShortReadHandle(int maxReadBytes)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_short_read");
        var scriptPath = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "script.mystery",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("#!/usr/bin/env python\nprint('ok')\n"))
                .ToArray());
        var completionEncoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var completionPath = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "_cdidx.unknown",
            completionEncoding
                .GetPreamble()
                .Concat(completionEncoding.GetBytes("#compdef cdidx\n_cdidx() {}\n"))
                .ToArray());
        TestProjectHelper.WriteTextFile(
            project.Root,
            "plain.opaque",
            "plain unknown-language coverage text\n" + new string('x', 8 * 1024));
        var openedPaths = new List<string>();
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openedPaths.Add(candidate);
                return new CountingCSharpPrepassFileStream(candidate, maxReadBytes);
            });

        var result = indexer.ScanFilesDetailed();

        Assert.Equal(1, openedPaths.Count(path => string.Equals(path, scriptPath, StringComparison.Ordinal)));
        Assert.Equal(1, openedPaths.Count(path => string.Equals(path, completionPath, StringComparison.Ordinal)));
        Assert.Equal(
            1,
            openedPaths.Count(path => string.Equals(
                path,
                Path.Combine(project.Root, "plain.opaque"),
                StringComparison.Ordinal)));
        Assert.Equal("python", result.FileLanguages[scriptPath]);
        Assert.Equal("shell", result.FileLanguages[completionPath]);
        Assert.Equal(["plain.opaque"], result.UnknownExtensionFiles);
        Assert.Contains(scriptPath, result.Files);
        Assert.Contains(completionPath, result.Files);
    }

    [Fact]
    public void ScanFilesDetailed_RecognizedUnknownHeader_RemainsBoundedAndPreservesPrefixNullPolicy()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_bounded_header");
        var acceptedBytes = Enumerable.Repeat((byte)'x', 2 * 1024).ToArray();
        Encoding.ASCII.GetBytes("#!/bin/sh\n").CopyTo(acceptedBytes, 0);
        acceptedBytes[FileIndexer.ShebangProbeByteLimit] = 0;
        var acceptedPath = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "accepted.scriptx",
            acceptedBytes);

        var rejectedBytes = Enumerable.Repeat((byte)'x', 64).ToArray();
        Encoding.ASCII.GetBytes("#!/bin/sh\n").CopyTo(rejectedBytes, 0);
        rejectedBytes[32] = 0;
        TestProjectHelper.WriteBinaryFile(
            project.Root,
            "rejected.scriptx",
            rejectedBytes);
        var rejectedPath = Path.Combine(project.Root, "rejected.scriptx");
        var openedPaths = new List<string>();
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: 128,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openedPaths.Add(candidate);
                return new CountingCSharpPrepassFileStream(candidate, maxReadBytes: 3);
            });

        var result = indexer.ScanFilesDetailed();

        Assert.Equal(1, openedPaths.Count(path => string.Equals(path, acceptedPath, StringComparison.Ordinal)));
        Assert.Equal(1, openedPaths.Count(path => string.Equals(path, rejectedPath, StringComparison.Ordinal)));
        Assert.Contains(acceptedPath, result.Files);
        Assert.Equal("shell", result.FileLanguages[acceptedPath]);
        Assert.DoesNotContain("accepted.scriptx", result.UnknownExtensionFiles);
        Assert.DoesNotContain("rejected.scriptx", result.UnknownExtensionFiles);
        Assert.Contains("rejected.scriptx", result.NonIndexablePaths);
    }

    [Fact]
    public void ScanFilesDetailed_UnknownLanguageProbe_PreservesCoveragePolicyBoundaries()
    {
        const int maxFileBytes = 8 * 1024;
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_policy");
        TestProjectHelper.WriteTextFile(
            project.Root,
            "valid-lfs.cdidxunknown",
            GitLfsPointerText(new string('a', 64)));

        const string pointerPrefix =
            "version https://git-lfs.github.com/spec/v1\n"
            + "oid sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n"
            + "size ";
        var pointerBoundary = pointerPrefix
            + new string('7', 1024 - Encoding.ASCII.GetByteCount(pointerPrefix));
        Assert.Equal(1024, Encoding.ASCII.GetByteCount(pointerBoundary));
        TestProjectHelper.WriteBinaryFile(
            project.Root,
            "boundary-lfs.cdidxunknown",
            Encoding.ASCII.GetBytes(pointerBoundary));

        var utf16Text = string.Concat(Enumerable.Repeat("plain text coverage\n", 64));
        foreach (var (name, encoding) in new (string, Encoding)[]
                 {
                     ("utf16-le-bom", new UnicodeEncoding(bigEndian: false, byteOrderMark: true)),
                     ("utf16-be-bom", new UnicodeEncoding(bigEndian: true, byteOrderMark: true)),
                     ("utf16-le-parity", new UnicodeEncoding(bigEndian: false, byteOrderMark: false)),
                     ("utf16-be-parity", new UnicodeEncoding(bigEndian: true, byteOrderMark: false)),
                 })
        {
            TestProjectHelper.WriteBinaryFile(
                project.Root,
                $"{name}.cdidxunknown",
                encoding.GetPreamble().Concat(encoding.GetBytes(utf16Text)).ToArray());
        }

        var utf32Encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        TestProjectHelper.WriteBinaryFile(
            project.Root,
            "utf32.cdidxunknown",
            utf32Encoding.GetPreamble().Concat(utf32Encoding.GetBytes("plain text\n")).ToArray());

        var lateNull = Enumerable.Repeat((byte)'a', 5 * 1024).ToArray();
        lateNull[4096] = 0;
        TestProjectHelper.WriteBinaryFile(
            project.Root,
            "late-null.cdidxunknown",
            lateNull);
        TestProjectHelper.WriteBinaryFile(
            project.Root,
            "exact-max.cdidxunknown",
            Enumerable.Repeat((byte)'a', maxFileBytes).ToArray());
        TestProjectHelper.WriteBinaryFile(
            project.Root,
            "over-max.cdidxunknown",
            Enumerable.Repeat((byte)'a', maxFileBytes + 1).ToArray());

        var result = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: maxFileBytes).ScanFilesDetailed();

        Assert.Contains("boundary-lfs.cdidxunknown", result.UnknownExtensionFiles);
        Assert.Contains("utf16-le-bom.cdidxunknown", result.UnknownExtensionFiles);
        Assert.Contains("utf16-be-bom.cdidxunknown", result.UnknownExtensionFiles);
        Assert.Contains("utf16-le-parity.cdidxunknown", result.UnknownExtensionFiles);
        Assert.Contains("utf16-be-parity.cdidxunknown", result.UnknownExtensionFiles);
        Assert.Contains("exact-max.cdidxunknown", result.UnknownExtensionFiles);
        Assert.DoesNotContain("valid-lfs.cdidxunknown", result.UnknownExtensionFiles);
        Assert.DoesNotContain("utf32.cdidxunknown", result.UnknownExtensionFiles);
        Assert.DoesNotContain("late-null.cdidxunknown", result.UnknownExtensionFiles);
        Assert.DoesNotContain("over-max.cdidxunknown", result.UnknownExtensionFiles);
        Assert.Contains("valid-lfs.cdidxunknown", result.NonIndexablePaths);
        Assert.Contains("utf32.cdidxunknown", result.NonIndexablePaths);
        Assert.Contains("late-null.cdidxunknown", result.NonIndexablePaths);
        Assert.Contains("over-max.cdidxunknown", result.NonIndexablePaths);
        Assert.DoesNotContain(
            result.Errors,
            error => error.Path is "late-null.cdidxunknown" or "over-max.cdidxunknown");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProbeUnknownLanguageForIndexing_SameMetadataAtomicReplacementRetriesCurrentPath(
        bool recognizedHeaderFirst)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_atomic");
        var recognizedBytes = CreateFixedUnknownProbePayload("#!/bin/sh\necho ok\n", 4 * 1024);
        var plainBytes = CreateFixedUnknownProbePayload("plain unknown text\n", 4 * 1024);
        var originalBytes = recognizedHeaderFirst ? recognizedBytes : plainBytes;
        var replacementBytes = recognizedHeaderFirst ? plainBytes : recognizedBytes;
        var path = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "subject.cdidxunknown",
            originalBytes);
        var replacementPath = TestProjectHelper.WriteBinaryFile(
            project.Root,
            "replacement.tmp",
            replacementBytes);
        var sharedModifiedUtc = DateTime.UtcNow.AddMinutes(-2);
        File.SetLastWriteTimeUtc(path, sharedModifiedUtc);
        File.SetLastWriteTimeUtc(replacementPath, sharedModifiedUtc);
        var openCount = 0;
        var snapshotCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                var stream = new CountingCSharpPrepassFileStream(candidate, maxReadBytes: 3);
                if (openCount == 1)
                {
                    File.Replace(replacementPath, path, destinationBackupFileName: null);
                    File.SetLastWriteTimeUtc(path, sharedModifiedUtc);
                }
                return stream;
            },
            fileHandleSnapshotCapturedForTesting: () => snapshotCount++);

        var result = indexer.ProbeUnknownLanguageForIndexing(
            path,
            "subject.cdidxunknown",
            CancellationToken.None);

        Assert.Equal(2, openCount);
        Assert.Equal(4, snapshotCount);
        Assert.Equal(
            recognizedHeaderFirst
                ? FileIndexer.FileProbeStatus.Unsupported
                : FileIndexer.FileProbeStatus.Supported,
            result.LanguageDetection.Status);
        Assert.Equal(!recognizedHeaderFirst, result.LanguageDetection.Language == "shell");
        Assert.Equal(recognizedHeaderFirst, result.IsCoverageCandidate);
    }

    [Fact]
    public void ProbeUnknownLanguageForIndexing_SecondMutationStopsAfterBoundedRetry()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_retry_bound");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "subject.cdidxunknown",
            "plain unknown text\n" + new string('x', 1024));
        var openCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                return new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 64,
                    afterFirstRead: () => File.AppendAllText(path, "y"));
            });

        var result = indexer.ProbeUnknownLanguageForIndexing(
            path,
            "subject.cdidxunknown",
            CancellationToken.None);

        Assert.Equal(2, openCount);
        Assert.Equal(FileIndexer.FileProbeStatus.Unsupported, result.LanguageDetection.Status);
        Assert.True(result.IsCoverageCandidate);
    }

    [Fact]
    public void ProbeUnknownLanguageForIndexing_GrowthBeyondLimitIsRejectedOnSameHandle()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_growth");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "subject.cdidxunknown",
            "plain unknown text\n" + new string('x', 1024));
        var initialLength = new FileInfo(path).Length;
        var openCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: initialLength + 8,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                return new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 64,
                    afterFirstRead: () => File.AppendAllText(path, new string('y', 32)));
            });

        var exception = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() =>
            indexer.ProbeUnknownLanguageForIndexing(
                path,
                "subject.cdidxunknown",
                CancellationToken.None));

        Assert.Equal(1, openCount);
        Assert.Contains("grew during read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeUnknownLanguageForIndexing_CancellationDoesNotReopen()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_cancel");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "subject.cdidxunknown",
            "plain unknown text\n" + new string('x', 16 * 1024));
        using var cancellation = new CancellationTokenSource();
        var openCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                return new CountingCSharpPrepassFileStream(
                    candidate,
                    maxReadBytes: 64,
                    afterFirstRead: cancellation.Cancel);
            });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            indexer.ProbeUnknownLanguageForIndexing(
                path,
                "subject.cdidxunknown",
                cancellation.Token));
        Assert.Equal(1, openCount);
    }

    [Fact]
    public void ProbeUnknownLanguageForIndexing_FourMiBPayloadDoesNotAllocatePayloadSizedBuffer()
    {
        const int payloadBytes = 4 * 1024 * 1024;
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_allocation");
        var path = TestProjectHelper.WriteTextFile(
            project.Root,
            "subject.cdidxunknown",
            new string('a', payloadBytes));
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: payloadBytes + 1);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = indexer.ProbeUnknownLanguageForIndexing(
            path,
            "subject.cdidxunknown",
            CancellationToken.None);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(FileIndexer.FileProbeStatus.Unsupported, result.LanguageDetection.Status);
        Assert.True(result.IsCoverageCandidate);
        Assert.True(
            allocated < 1024 * 1024,
            $"Expected a bounded pooled probe allocation, saw {allocated} bytes allocated.");
    }

    [Fact]
    public void ProbeUnknownLanguageForIndexing_InternalSymlinkRetargetOutsideScopeFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_internal_link");
        using var external = TestProjectHelper.CreateTempProjectScope("cdidx_unknown_probe_external_link");
        var internalTarget = TestProjectHelper.WriteTextFile(
            project.Root,
            "inside-target",
            "plain unknown text\n");
        var externalTarget = TestProjectHelper.WriteTextFile(
            external.Root,
            "outside-target",
            "#!/bin/sh\necho outside\n");
        var linkPath = Path.Combine(project.Root, "subject.cdidxunknown");
        try
        {
            File.CreateSymbolicLink(linkPath, internalTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var retargeted = false;
        var openCount = 0;
        var indexer = new FileIndexer(
            project.Root,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: FileIndexer.DefaultMaxFileSizeBytes,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal,
            openReadForIndexContent: candidate =>
            {
                openCount++;
                var stream = BoundedFile.OpenReadForIndexContent(candidate);
                if (!retargeted)
                {
                    File.Delete(linkPath);
                    File.CreateSymbolicLink(linkPath, externalTarget);
                    retargeted = true;
                }
                return stream;
            });

        var result = indexer.ProbeUnknownLanguageForIndexing(
            linkPath,
            "subject.cdidxunknown",
            CancellationToken.None);

        Assert.True(retargeted);
        Assert.Equal(1, openCount);
        Assert.Equal(FileIndexer.FileProbeStatus.ProbeFailed, result.LanguageDetection.Status);
        Assert.False(result.IsCoverageCandidate);
    }

    [Fact]
    public void ScanFilesDetailed_TreatsSolutionAndManifestAsKnownStructuralFiles_Issue3662()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(
            tempDir,
            "App.sln",
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            """);
        TestProjectHelper.WriteTextFile(
            tempDir,
            "app.manifest",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <assemblyIdentity version="1.0.0.0" name="App" type="win32" />
            </assembly>
            """);
        TestProjectHelper.WriteTextFile(tempDir, "data.mystery", "unknown extension\n");

        var indexer = new FileIndexer(tempDir);
        var scanResult = indexer.ScanFilesDetailed();
        var files = scanResult.Files
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["App.sln", "app.manifest"], files);
        Assert.Equal(["data.mystery"], scanResult.UnknownExtensionFiles);
        Assert.DoesNotContain("App.sln", scanResult.UnknownExtensionFiles);
        Assert.DoesNotContain("app.manifest", scanResult.UnknownExtensionFiles);
    }

    [Fact]
    public void DetectLanguage_ExtensionlessShebangs_HonorsUnicodeBomEncodings()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var files = new Dictionary<string, Encoding>
        {
            ["utf8"] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            ["utf8-bom"] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            ["utf16-le"] = new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            ["utf16-be"] = new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        };

        foreach (var (name, encoding) in files)
        {
            var path = TestProjectHelper.ProjectPath(tempDir, name);
            File.WriteAllText(path, "#!/usr/bin/env bash\nprintf 'ok'\n", encoding);
        }

        var detected = files.Keys
            .ToDictionary(name => name, name => FileIndexer.DetectLanguage(Path.Combine(tempDir, name)));

        Assert.All(detected, pair => Assert.Equal("shell", pair.Value));
    }

    [Fact]
    public void ScanFiles_IncludesModernNodeModuleExtensions()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["index.mjs"] = "export const run = () => {};",
                ["cli.cjs"] = "module.exports = {};",
                ["types.cts"] = "export type Config = {};",
                ["types.d.mts"] = "export interface Config {}",
                ["notes.txt"] = "ignored",
            });

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles().Select(Path.GetFileName).OrderBy(name => name).ToList();

        Assert.Equal(["cli.cjs", "index.mjs", "types.cts", "types.d.mts"], files);
    }

    [Fact]
    public void ScanFiles_IncludesExtensionlessShebangScripts()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["rbenv-init"] = "#!/usr/bin/env bash\necho init\n",
                ["python-tool"] = "#!/usr/bin/python3\nprint('hi')\n",
                ["plain-text"] = "Hello world\n",
                ["known.rb"] = "puts 'known'\n",
            });

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles()
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["known.rb", "python-tool", "rbenv-init"], files);
    }

    [Fact]
    public void ScanFiles_IncludesUnknownExtensionWhenShebangLooksSupported_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["notes.txt"] = "#!/usr/bin/env bash\necho hi\n",
                ["script"] = "#!/usr/bin/env bash\necho hi\n",
            });

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles()
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["notes.txt", "script"], files);
    }

    [Fact]
    public void ScanFiles_ExcludesInternalIndexArtifactsBeforeUnknownExtensionShebangProbe_Issue4611()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_internal_artifacts");
        var internalDirectory = TestProjectHelper.ProjectPath(project.Root, ".cdidx");
        Directory.CreateDirectory(internalDirectory);
        File.WriteAllText(
            Path.Combine(internalDirectory, "codeindex.db.lock"),
            "#!/usr/bin/env bash\necho internal\n");
        File.WriteAllText(
            Path.Combine(internalDirectory, "codeindex.db.lock.info"),
            "#!/usr/bin/env bash\necho internal metadata\n");
        File.WriteAllText(TestProjectHelper.ProjectPath(project.Root, "tool.txt"), "#!/usr/bin/env bash\necho public\n");

        var result = new FileIndexer(project.Root).ScanFilesDetailed();

        Assert.Equal(["tool.txt"], result.Files.Select(Path.GetFileName).ToArray());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ScanFiles_IgnoresUnixFifoWithoutHanging()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var extensionlessFifo = TestProjectHelper.ProjectPath(tempDir, "tool");
        var extensionFifo = TestProjectHelper.ProjectPath(tempDir, "tool.sh");
        var knownNameFifo = TestProjectHelper.ProjectPath(tempDir, "Dockerfile");
        CreateUnixFifo(extensionlessFifo);
        CreateUnixFifo(extensionFifo);
        CreateUnixFifo(knownNameFifo);

        Assert.False(FileIndexer.CanIndexFile(extensionlessFifo));
        Assert.False(FileIndexer.CanIndexFile(extensionFifo));
        Assert.False(FileIndexer.CanIndexFile(knownNameFifo));

        var indexer = new FileIndexer(tempDir);
        var scanTask = Task.Run(() => indexer.ScanFiles());
        var completedTask = await Task.WhenAny(scanTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(scanTask, completedTask);
        Assert.Empty(await scanTask);
    }

    [Fact]
    public void BuildRecord_HandlesUnicodeAndCjkContent()
    {
        // Files with Unicode/CJK characters in content should be indexed correctly
        // Unicode/CJK文字を含むファイルが正しくインデックスされること
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var content = "// コメント: 日本語テスト\npublic class 日本語クラス\n{\n    public string 名前 { get; set; }\n    // 中文注释\n    // 한국어 주석\n}\n";
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "unicode.cs", content);

        var indexer = new FileIndexer(tempDir);
        var (record, fileContent, warning) = indexer.BuildRecord(filePath);

        Assert.Equal("unicode.cs", record.Path);
        Assert.Equal("csharp", record.Lang);
        Assert.Null(warning); // Valid UTF-8, no warning / 有効なUTF-8なので警告なし
        Assert.Contains("日本語クラス", fileContent);
        Assert.Contains("中文注释", fileContent);
        Assert.Contains("한국어", fileContent);
    }

    [Theory]
    [InlineData("class A\r\n{\r\n}\r\n", 3)]
    [InlineData("class A\n{\n}\n", 3)]
    [InlineData("class A\r{\r}\r", 3)]
    [InlineData("\uFEFF", 0)]
    public void BuildRecord_CountsPhysicalLinesAfterLineLeadingInvisibleStripping(string content, int expectedLines)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "physical.cs", content);

        var indexer = new FileIndexer(tempDir);
        var (record, _, _) = indexer.BuildRecord(filePath);

        Assert.Equal(expectedLines, record.Lines);
    }

    [Fact]
    public void ValidateSymbolLineRanges_RejectsLinesOutsideFileRange()
    {
        var record = new FileRecord
        {
            Path = "src/drift.cs",
            Lang = "csharp",
            Lines = 2,
        };
        var symbols = new[]
        {
            new SymbolRecord { FileId = 1, Kind = "class", Name = "Drift", Line = 3, StartLine = 3, EndLine = 3 },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => FileIndexer.ValidateSymbolLineRanges(record, symbols));

        Assert.Contains("outside file line range", ex.Message);
        Assert.Contains("src/drift.cs", ex.Message);
    }

    [Fact]
    public void BuildRecord_CjkSymbolsExtractedCorrectly()
    {
        var content = "// 日本語コメント\npublic class ユーザーサービス\n{\n    public string 名前を取得(int id) { return \"\"; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // CJK class and method names should be extracted / CJKのクラス名・メソッド名が抽出されること
        // Note: \w in .NET regex matches Unicode letters, so CJK identifiers work
        // 注: .NET の \w は Unicode 文字にマッチするため CJK 識別子も動作する
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ユーザーサービス");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "名前を取得");
    }

    [Fact]
    public void BuildRecord_NormalizesPathSeparators()
    {
        // Ensure Windows-style backslashes are converted to forward slashes
        // Windows形式のバックスラッシュがフォワードスラッシュに変換されることを確認
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "src/models/user.py", "class User: pass\n");

        var indexer = new FileIndexer(tempDir);
        var (record, _, _) = indexer.BuildRecord(filePath);

        // Path should use forward slashes regardless of OS
        // OSに関わらずフォワードスラッシュを使うべき
        Assert.DoesNotContain("\\", record.Path);
        Assert.Contains("/", record.Path);
        Assert.Equal("src/models/user.py", record.Path);
    }

    [Fact]
    public void BuildRecord_PreservesBackslashInPosixFilename()
    {
        // On POSIX, '\' is a valid filename character and must not be converted to '/'.
        // Otherwise a file named "back\slash.py" becomes a phantom "back/slash.py".
        // POSIX では '\' は正当なファイル名文字であり、'/' に置換すべきでない。
        // 置換すると "back\slash.py" が幻の "back/slash.py" として保存されてしまう。
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "back\\slash.py", "def hu(): pass\n");

        var indexer = new FileIndexer(tempDir);
        var (record, _, _) = indexer.BuildRecord(filePath);

        Assert.Equal("back\\slash.py", record.Path);
    }

    [Fact]
    public void NormalizePathSeparators_OnPosixKeepsBackslashInFilename()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("back\\slash.py", FileIndexer.NormalizePathSeparators("back\\slash.py"));
        Assert.Equal("dir/back\\slash.py", FileIndexer.NormalizePathSeparators("dir/back\\slash.py"));
    }

    [Fact]
    public void NormalizePathSeparators_OnWindowsConvertsBackslashToForwardSlash()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal("src/models/user.py", FileIndexer.NormalizePathSeparators("src\\models\\user.py"));
    }

    [Fact]
    public void ScanFiles_IncludesFileNameBasedLanguages()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Dockerfile"] = "FROM alpine",
                ["Makefile"] = "all: build",
                ["app.py"] = "print('hello')",
                ["unknown.xyz"] = "nothing",
            });

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles();

        // Dockerfile, Makefile, and app.py should be found; unknown.xyz should not
        Assert.Equal(3, files.Count);
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public void ScanFiles_WindowsLongPath_IndexesAndSurvivesStalePurge()
    {
        // Windows-only syscall coverage: POSIX cannot exercise Win32 MAX_PATH behavior.
        if (!OperatingSystem.IsWindows())
            return;

        var tempRoot = TestProjectHelper.CreateTempProject("cdidx_long_path");
        var projectRoot = Path.Combine(tempRoot, "node_modules");
        DbContext? db = null;
        try
        {
            Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(projectRoot));
            var leafPath = CreateWindowsLongPathFixture(projectRoot);
            Assert.True(leafPath.Length >= 260, $"Fixture path length was {leafPath.Length}, expected >= 260.");

            var indexer = new FileIndexer(projectRoot);
            var scannedFiles = indexer.ScanFiles();

            Assert.Contains(scannedFiles, path => PathsEqual(path, leafPath));

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);

            IndexScannedFiles(projectRoot, writer);
            var relativeLeafPath = FileIndexer.NormalizePathSeparators(Path.GetRelativePath(projectRoot, leafPath));

            Assert.True(IndexedFileExists(db, relativeLeafPath));
            Assert.Equal(0, writer.PurgeStaleFiles(projectRoot));

            IndexScannedFiles(projectRoot, writer);
            Assert.True(IndexedFileExists(db, relativeLeafPath));
        }
        finally
        {
            if (db is not null)
            {
                SqliteConnection.ClearPool(db.Connection);
                db.Dispose();
            }

            DeleteLongPathDirectory(tempRoot);
        }
    }

    [Fact]
    public void GetFamilyScopeKey_MarkerlessRootUsesTopLevelSubtreeScope()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var srcFile = TestProjectHelper.WriteTextFile(tempDir, "src/Api.Part1.cs", "public partial class Api {}");
        var generatedFile = TestProjectHelper.WriteTextFile(tempDir, "generated/Api.Part2.cs", "public partial class Api {}");

        var indexer = new FileIndexer(tempDir);

        Assert.Equal("src", indexer.GetFamilyScopeKey(srcFile, "csharp"));
        Assert.Equal("generated", indexer.GetFamilyScopeKey(generatedFile, "csharp"));
    }

    [Fact]
    public void GetFamilyScopeKey_MarkerlessRootLevelFilesShareRootScope()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var firstFile = TestProjectHelper.WriteTextFile(tempDir, "Api.Part1.cs", "public partial class Api {}");
        var secondFile = TestProjectHelper.WriteTextFile(tempDir, "Api.Part2.cs", "public partial class Api {}");

        var indexer = new FileIndexer(tempDir);

        Assert.Equal(".", indexer.GetFamilyScopeKey(firstFile, "csharp"));
        Assert.Equal(".", indexer.GetFamilyScopeKey(secondFile, "csharp"));
    }

    [Fact]
    public void GetFamilyScopeKey_IgnoresIgnoredProjectMarkers()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, ".gitignore", "src/Lib/Lib.csproj\n");
        var projectPath = TestProjectHelper.WriteTextFile(tempDir, "src/Lib/Lib.csproj", "<Project />");
        var sourcePath = TestProjectHelper.WriteTextFile(tempDir, "src/Lib/Feature/Api.Part1.cs", "public partial class Api {}");

        var indexer = new FileIndexer(tempDir);
        var familyScopeKey = indexer.GetFamilyScopeKey(sourcePath, "csharp");
        var ignoredMarkerFingerprint = indexer.GetProjectMarkerFingerprint("csharp");
        File.Delete(projectPath);
        var markerlessFingerprint = new FileIndexer(tempDir).GetProjectMarkerFingerprint("csharp");

        Assert.Equal("src", familyScopeKey);
        Assert.Equal(markerlessFingerprint, ignoredMarkerFingerprint);
    }

    [Fact]
    public void GetFamilyScopeKey_MultipleProjectMarkersInOneDirectoryUseNarrowerSubtreeScope()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/ProjectA.csproj"] = "<Project />",
                ["src/ProjectB.csproj"] = "<Project />",
                ["src/ProjA/Api.Part1.cs"] = "public partial class Api {}",
                ["src/ProjB/Api.Part1.cs"] = "public partial class Api {}",
                ["src/Api.Part1.cs"] = "public partial class Api {}",
            });
        var projAFile = TestProjectHelper.ProjectPath(tempDir, "src/ProjA/Api.Part1.cs");
        var projBFile = TestProjectHelper.ProjectPath(tempDir, "src/ProjB/Api.Part1.cs");
        var ambiguousFile = TestProjectHelper.ProjectPath(tempDir, "src/Api.Part1.cs");

        var indexer = new FileIndexer(tempDir);

        Assert.Equal("src/ProjA", indexer.GetFamilyScopeKey(projAFile, "csharp"));
        Assert.Equal("src/ProjB", indexer.GetFamilyScopeKey(projBFile, "csharp"));
        Assert.Equal("src/__file__/Api.Part1.cs", indexer.GetFamilyScopeKey(ambiguousFile, "csharp"));
    }

    [Fact]
    public void BuildRecord_CreatesCorrectRecord()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "main.py", "def main():\n    print('hello')\n");

        var indexer = new FileIndexer(tempDir);
        var (record, content, _) = indexer.BuildRecord(filePath);

        Assert.Equal("main.py", record.Path);
        Assert.Equal("python", record.Lang);
        Assert.Equal(2, record.Lines); // "def main():\n    print('hello')\n" = 2 lines (trailing newline ignored)
        Assert.NotNull(record.Checksum);
    }

    [Fact]
    public void ScanFiles_SkipsCaseInsensitiveDirectories()
    {
        // SkipDirs should be case-insensitive (e.g. "Build" matches "build")
        // SkipDirsは大文字小文字を区別しない（例: "Build"は"build"にマッチ）
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["app.py"] = "print('hello')",
                ["Build/output.js"] = "var x = 1;",
            });

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles();

        Assert.Single(files);
        Assert.Contains("app.py", files[0]);
    }

    [Theory]
    [InlineData(".pnpm-store", "pkg.js")]
    [InlineData(".turbo", "trace.json")]
    [InlineData(".parcel-cache", "bundle.js")]
    [InlineData(".mypy_cache", "module.meta.json")]
    [InlineData(".ruff_cache", "cache.bin")]
    [InlineData("bazel-out", "generated.cc")]
    [InlineData("CMakeFiles", "compiler_depend.ts")]
    [InlineData(".swiftpm", "workspace-state.json")]
    [InlineData(".dart_tool", "package_config.json")]
    [InlineData(".stack-work", "build.log")]
    public void ScanFiles_SkipsCommonGeneratedCacheDirectories(string directoryName, string fileName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, "app.py", "print('hello')");
        TestProjectHelper.WriteTextFile(tempDir, Path.Combine(directoryName, fileName), "generated");

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles();

        Assert.Single(files);
        Assert.Contains("app.py", files[0]);
    }

    [Fact]
    public void ScanFilesDetailed_FileDeletedAfterEnumeration_RecordsWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-delete-race");
        var tempDir = project.Root;
        var scriptPath = Path.Combine(tempDir, "script");
        File.WriteAllText(scriptPath, "#!/usr/bin/env python\nprint('hello')\n");

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: _ => false,
            enumerateFiles: dir => Path.GetFullPath(dir) == Path.GetFullPath(tempDir)
                ? DeleteBeforeProbe(scriptPath)
                : Directory.EnumerateFiles(dir));

        var result = indexer.ScanFilesDetailed();

        Assert.Empty(result.Files);
        Assert.Contains("script", result.NonIndexablePaths);
        var warning = Assert.Single(result.Errors);
        Assert.Equal("script", warning.Path);
        Assert.Equal(FileIndexer.ScanIssueSeverity.Warning, warning.Severity);
        Assert.Contains("deleted during scanning", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.HadErrors);

        static IEnumerable<string> DeleteBeforeProbe(string path)
        {
            File.Delete(path);
            yield return path;
        }
    }

    [Fact]
    public void ScanFilesDetailed_DanglingDirectorySymlink_RecordsWarningAndCount()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-dangling-symlink");
        var tempDir = project.Root;
        var linkPath = Path.Combine(tempDir, "missing-link");
        Directory.CreateSymbolicLink(linkPath, Path.Combine(tempDir, "missing-target"));

        var result = new FileIndexer(tempDir).ScanFilesDetailed();

        Assert.Contains("missing-link", result.DanglingSymlinks);
        Assert.Contains(
            result.Errors,
            error => error.Path == "missing-link"
                && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("dangling symlink", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_ReportsDirectorySymlinkPermissionFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-symlink-permission");
        var tempDir = project.Root;
        try
        {
            var targetDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(Path.Combine(targetDir, "target.py"), "print('target')\n");
            var linkPath = Path.Combine(tempDir, "blocked-link");
            Directory.CreateSymbolicLink(linkPath, targetDir);
            FileIndexer.ResolveDirectoryLinkTargetForTesting = path =>
            {
                if (string.Equals(path, linkPath, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("denied");
                return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            };

            var indexer = new FileIndexer(
                tempDir,
                ignoreCase: false,
                ignoreRuleRoot: null,
                maxFileSizeBytes: null,
                directoryIgnoreCaseProbe: null,
                symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);
            var result = indexer.ScanFilesDetailed();

            Assert.Equal(["src/target.py"], result.Files
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList());
            Assert.Contains(
                result.Errors,
                error => error.Path == "blocked-link"
                    && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                    && error.Message.Contains("permissions", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("blocked-link", result.DanglingSymlinks);
            Assert.False(result.HadErrors);
        }
        finally
        {
            FileIndexer.ResolveDirectoryLinkTargetForTesting = null;
        }
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_SkipsOutOfTreeDirectorySymlink()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-symlink-policy");
        using var externalProject = TestProjectHelper.CreateTempProjectScope("cdidx-symlink-external");
        var tempDir = project.Root;
        var externalDir = externalProject.Root;
        File.WriteAllText(Path.Combine(externalDir, "external.py"), "print('external')\n");
        var linkPath = Path.Combine(tempDir, "external");
        Directory.CreateSymbolicLink(linkPath, externalDir);

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

        var result = indexer.ScanFilesDetailed();

        Assert.Empty(result.Files);
        Assert.Contains(
            result.Errors,
            error => error.Path == "external"
                && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("symlinked directory", StringComparison.OrdinalIgnoreCase)
                && error.Message.Contains("<outside project root>", StringComparison.Ordinal)
                && !error.Message.Contains(externalDir, StringComparison.Ordinal));
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_FollowsInTreeDirectorySymlinkOnce()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-symlink-internal");
        var tempDir = project.Root;
        var targetDir = Path.Combine(tempDir, "src");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "app.py"), "print('app')\n");
        Directory.CreateSymbolicLink(Path.Combine(tempDir, "src-link"), targetDir);

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

        var files = indexer.ScanFiles()
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.Single(files);
        Assert.Contains(files[0], new[] { "src/app.py", "src-link/app.py" });
    }

    [Fact]
    public void ScanFiles_FollowSymlinksInternal_SkipsCycleToProjectRoot()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx-symlink-cycle");
        var tempDir = project.Root;
        File.WriteAllText(Path.Combine(tempDir, "app.py"), "print('app')\n");
        Directory.CreateSymbolicLink(Path.Combine(tempDir, "self"), tempDir);

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: null,
            symlinkPolicy: FileIndexer.SymlinkPolicy.Internal);

        var result = indexer.ScanFilesDetailed();

        Assert.Single(result.Files);
        Assert.Contains(
            result.Errors,
            error => error.Path == "self"
                && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("already scanned", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_DescendsIntoSubmoduleHostedUnderSkipDir()
    {
        // .gitmodules declared submodule under a SkipDirs-named directory (e.g. vendor/foo)
        // must remain visible: SkipDirs is overridden along the path to the submodule, but
        // unrelated files inside the SkipDir ancestor itself stay excluded. Closes #1511.
        // SkipDirs 名のディレクトリ配下に .gitmodules で宣言された submodule（例: vendor/foo）は
        // 可視化される必要がある。SkipDirs は submodule までの経路でのみ上書きされ、SkipDirs
        // 祖先自身の無関係なファイルは引き続き除外される。Closes #1511.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                ["app.py"] = "print('hello')",
                // .gitmodules at project root declaring submodule path "vendor/foo"
                [".gitmodules"] = "[submodule \"foo\"]\n\tpath = vendor/foo\n\turl = https://example.invalid/foo.git\n",
                // File sitting directly in the SkipDir ancestor — must NOT be indexed
                // SkipDirs 祖先直下のファイル — 索引されてはいけない
                ["vendor/vendor_dep.py"] = "x = 1",
                ["vendor/foo/.git"] = "gitdir: ../../.git/modules/foo\n",
                ["vendor/foo/lib.py"] = "def f(): pass",
                ["vendor/foo/src/nested.py"] = "def g(): pass",
            });

        var indexer = new FileIndexer(tempDir);
        var files = indexer.ScanFiles();

        var rel = files.Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToHashSet();
        Assert.Contains("app.py", rel);
        Assert.Contains("vendor/foo/lib.py", rel);
        Assert.Contains("vendor/foo/src/nested.py", rel);
        Assert.DoesNotContain("vendor/vendor_dep.py", rel);
    }

    [Fact]
    public void ScanFilesDetailed_OversizedGitmodulesSkipsSubmodulePassthroughWithWarning()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                ["app.py"] = "print('hello')",
                [".gitmodules"] = new string('x', 300 * 1024),
                ["vendor/foo/.git"] = "gitdir: ../../.git/modules/foo\n",
                ["vendor/foo/lib.py"] = "def f(): pass",
            });

        var result = new FileIndexer(tempDir).ScanFilesDetailed();
        var rel = result.Files.Select(f => Path.GetRelativePath(tempDir, f).Replace('\\', '/')).ToHashSet();

        Assert.Contains("app.py", rel);
        Assert.DoesNotContain("vendor/foo/lib.py", rel);
        Assert.Contains(
            result.Errors,
            error => error.Path == ".gitmodules"
                && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void ScanFiles_GitmodulesQuotedPathPreservesCommentCharacters_Issue3819()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                [".gitmodules"] = """
                              [submodule "quoted"]
                                  path = "vendor/hash#semi;module" # trailing comment
                              """,
                ["vendor/hash#semi;module/.git"] = "gitdir: ../../.git/modules/quoted\n",
                ["vendor/hash#semi;module/lib.py"] = "def quoted(): pass",
            });

        var rel = ToRelativePathSet(tempDir, new FileIndexer(tempDir).ScanFiles());

        Assert.Contains("vendor/hash#semi;module/lib.py", rel);
    }

    [Fact]
    public void ScanFilesDetailed_GitmodulesSubmodulePathCapWarns_Issue3819()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, "app.py", "print('hello')");

        var maxPaths = FileIndexer.MaxGitmodulesSubmodulePaths;
        var gitmodules = new StringBuilder();
        for (var i = 0; i <= maxPaths; i++)
        {
            gitmodules.AppendLine($"[submodule \"m{i}\"]");
            gitmodules.AppendLine($"    path = vendor/m{i}");
        }

        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                [".gitmodules"] = gitmodules.ToString(),
                [$"vendor/m{maxPaths}/.git"] = $"gitdir: ../../.git/modules/m{maxPaths}\n",
                [$"vendor/m{maxPaths}/lib.py"] = "def over_cap(): pass",
            });

        var result = new FileIndexer(tempDir).ScanFilesDetailed();
        var rel = ToRelativePathSet(tempDir, result.Files);

        Assert.Contains("app.py", rel);
        Assert.DoesNotContain($"vendor/m{maxPaths}/lib.py", rel);
        Assert.Contains(
            result.Errors,
            error => error.Path == ".gitmodules"
                && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains($"after {maxPaths}", StringComparison.Ordinal));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSet_UsesNfcRetainedPaths()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(tempDir);
        var nfcPath = "Caf\u00e9.cs";
        var nfdPath = "Cafe\u0301.cs";
        TestProjectHelper.InsertIndexedFile(dbPath, nfcPath, "csharp", "class CafeFixture { }\n");

        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var retainedPaths = new[]
            {
                Path.Combine(tempDir, nfdPath),
            }
            .Select(path => FileIndexer.NormalizeIndexPath(Path.GetRelativePath(tempDir, path)))
            .ToHashSet(StringComparer.Ordinal);

        var purged = writer.PurgeFilesOutsideRetainedSet(retainedPaths);

        Assert.Equal(0, purged);
        Assert.Equal(1, CountFiles(db.Connection));
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSetWithinListedDirectories_UsesNfcPrunedDirectories()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(tempDir);
        TestProjectHelper.InsertIndexedFile(dbPath, "Caf\u00e9/src/File.cs", "csharp", "class NestedCafe { }\n");

        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.InitializeSchema();
        var writer = new DbWriter(db.Connection);
        var prunedDirectories = new[] { "Cafe\u0301" }
            .Select(FileIndexer.NormalizeIndexPath)
            .ToHashSet(StringComparer.Ordinal);

        var purged = writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            prunedDirectories);

        Assert.Equal(1, purged);
        Assert.Equal(0, CountFiles(db.Connection));
    }

    [Fact]
    public void IndexFilesUpdate_UsesOriginalUnicodePathForIoAndNfcPathForDb()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var nfdPath = "Cafe\u0301.cs";
        var nfdBinaryPath = "Cafe\u0301Binary.cs";
        var sourcePath = TestProjectHelper.WriteTextFile(tempDir, nfdPath, "class FirstCafe { }\n");
        var binaryPath = TestProjectHelper.WriteTextFile(tempDir, nfdBinaryPath, "class BinaryCafe { }\n");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(CommandExitCodes.Success, IndexCommandRunner.Run([tempDir, "--json", "--quiet"], jsonOptions));

        File.WriteAllText(sourcePath, "class UpdatedCafe { }\n");
        File.WriteAllBytes(binaryPath, [0, 1, 2, 3]);
        Assert.Equal(
            CommandExitCodes.Success,
            IndexCommandRunner.Run([tempDir, "--files", nfdPath, nfdBinaryPath, "--json", "--quiet"], jsonOptions));

        var dbPath = Path.Combine(tempDir, ".cdidx", "codeindex.db");
        Assert.Equal("class UpdatedCafe { }", ReadSingleChunkContent(dbPath, "Caf\u00e9.cs"));
        Assert.True(HasIndexedFile(dbPath, "Caf\u00e9Binary.cs"));
        Assert.True(HasFileIssue(dbPath, "Caf\u00e9Binary.cs", "null_byte"));
    }

    [Fact]
    public void ScanFiles_RespectsSubmoduleGitignore()
    {
        // Submodules brought back into the scan must still honor their own .gitignore so
        // build artifacts inside the submodule remain excluded.
        // 可視化された submodule も自身の .gitignore を尊重し、submodule 配下のビルド成果物などは
        // 引き続き除外されること。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                [".gitmodules"] = "[submodule \"foo\"]\n\tpath = vendor/foo\n\turl = https://example.invalid/foo.git\n",
                ["vendor/foo/lib.py"] = "def f(): pass",
                ["vendor/foo/.gitignore"] = "generated.py\n",
                ["vendor/foo/generated.py"] = "# generated",
            });

        var rel = ToRelativePathSet(tempDir, new FileIndexer(tempDir).ScanFiles());
        Assert.Contains("vendor/foo/lib.py", rel);
        Assert.DoesNotContain("vendor/foo/generated.py", rel);
    }

    [Fact]
    public void ScanFiles_StillSkipsSkipDirWithoutMatchingSubmodule()
    {
        // .gitmodules declaring a submodule elsewhere must not relax SkipDirs for unrelated
        // SkipDir-named directories. vendor/ without a declared submodule stays skipped.
        // .gitmodules が別の場所の submodule を宣言していても、無関係な SkipDirs 名ディレクトリ
        // (submodule が宣言されていない vendor/ 等) は引き続きスキップされること。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFiles(
            tempDir,
            new Dictionary<string, string>
            {
                [".gitmodules"] = "[submodule \"foo\"]\n\tpath = third_party/foo\n\turl = https://example.invalid/foo.git\n",
                ["third_party/foo/lib.py"] = "def f(): pass",
                ["vendor/dep.py"] = "x = 1",
            });

        var rel = ToRelativePathSet(tempDir, new FileIndexer(tempDir).ScanFiles());
        Assert.Contains("third_party/foo/lib.py", rel);
        Assert.DoesNotContain("vendor/dep.py", rel);
    }

    [Fact]
    public void ScanFilesDetailed_GitmodulesReadFailureReportsWarning_Issue3473()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var previousReader = FileIndexer.ReadGitmodulesLinesForTesting;
        try
        {
            TestProjectHelper.WriteTextFile(
                tempDir,
                ".gitmodules",
                "[submodule \"foo\"]\n\tpath = vendor/foo\n\turl = https://example.invalid/foo.git\n");
            FileIndexer.ReadGitmodulesLinesForTesting = _ => throw new IOException("blocked");

            var indexer = new FileIndexer(tempDir, ignoreCase: false);
            var result = indexer.ScanFilesDetailed();

            var warning = Assert.Single(result.Errors.Where(static error => error.Path == ".gitmodules"));
            Assert.False(warning.IsFatal);
            Assert.Contains("Skipped .gitmodules because it could not be read", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            FileIndexer.ReadGitmodulesLinesForTesting = previousReader;
        }
    }

    [Fact]
    public void EvaluatePathFilter_AllowsFilesUnderSubmoduleHostedInSkipDir()
    {
        // PathFilter must agree with the walker: files under a submodule declared in
        // .gitmodules are not classified as ExcludedByDefaultDirectory even when an
        // ancestor segment matches SkipDirs. This keeps update-mode (--files / --commits)
        // consistent with full scan output.
        // パスフィルタは walker と整合する必要がある: .gitmodules で宣言された submodule
        // 配下のファイルは、祖先が SkipDirs に該当しても ExcludedByDefaultDirectory に
        // 分類されない。これにより --files / --commits のような更新モードでも
        // フルスキャンと挙動が一致する。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        TestProjectHelper.WriteTextFile(tempDir, ".gitmodules", "[submodule \"foo\"]\n\tpath = vendor/foo\n");
        var libPath = TestProjectHelper.WriteTextFile(tempDir, "vendor/foo/lib.py", "def f(): pass");
        var unrelatedPath = TestProjectHelper.WriteTextFile(tempDir, "vendor/dep.py", "x = 1");

        var indexer = new FileIndexer(tempDir);
        Assert.Equal(FileIndexer.PathFilterKind.None, indexer.EvaluatePathFilter(libPath).FilterKind);
        Assert.Equal(FileIndexer.PathFilterKind.ExcludedByDefaultDirectory, indexer.EvaluatePathFilter(unrelatedPath).FilterKind);
    }

    [Theory]
    [InlineData(".turbo", "run.json")]
    [InlineData(".ruff_cache", "cache.bin")]
    [InlineData("bazel-testlogs", "test.log")]
    [InlineData(".dart_tool", "package_config.json")]
    public void EvaluatePathFilter_SkipsCommonGeneratedCacheDirectories(string directoryName, string fileName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var path = TestProjectHelper.WriteTextFile(tempDir, Path.Combine(directoryName, fileName), "generated");

        var indexer = new FileIndexer(tempDir);

        Assert.Equal(
            FileIndexer.PathFilterKind.ExcludedByDefaultDirectory,
            indexer.EvaluatePathFilter(path).FilterKind);
    }

    [Fact]
    public void BuildRecord_CrlfNormalizedToLf()
    {
        // CRLF line endings in files should be normalized to LF
        // ファイル内のCRLF改行はLFに正規化される
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.ProjectPath(tempDir, "crlf.py");
        File.WriteAllBytes(filePath, System.Text.Encoding.UTF8.GetBytes("line1\r\nline2\r\nline3\r\n"));

        var indexer = new FileIndexer(tempDir);
        var (record, content, _) = indexer.BuildRecord(filePath);

        Assert.DoesNotContain("\r", content);
        Assert.Equal(3, record.Lines);
    }

    [Theory]
    [InlineData("line1\r\nline2\r\n", "line1\nline2\n")]
    [InlineData("line1\rline2\r", "line1\nline2\n")]
    [InlineData("crlf\r\ncr\rlf\n", "crlf\ncr\nlf\n")]
    [InlineData("line1\nline2\n", "line1\nline2\n")]
    public void NormalizeLineEndings_CollapsesCrVariantsToLf(string input, string expected)
    {
        Assert.Equal(expected, FileIndexer.NormalizeLineEndings(input));
    }

    [Fact]
    public void BuildRecord_LeadingBomStrippedFromContent()
    {
        // Files whose on-disk bytes begin with UTF-8 BOM (EF BB BF) must have the BOM
        // stripped from the decoded content so downstream consumers never see a phantom
        // U+FEFF glyph on line 1. The checksum must reflect the same canonical content
        // stored in chunks so BOM-only changes do not desynchronize line metadata. Closes #183/#1467.
        // オンディスク先頭に UTF-8 BOM (EF BB BF) を持つファイルは、デコード後の content
        // から BOM を剥がし、下流に幽霊 U+FEFF を渡さないようにする。checksum は chunk
        // に保存される canonical content と同じ内容から算出し、行メタデータとのずれを防ぐ。
        // Closes #183/#1467.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var rawBytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("using System;\nnamespace BomTest;\n"))
            .ToArray();
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "bom.cs", rawBytes);

        var indexer = new FileIndexer(tempDir);
        var (record, content, _) = indexer.BuildRecord(filePath);

        Assert.StartsWith("using System;", content);
        // Culture-aware IndexOf treats U+FEFF as ignorable and spuriously matches at pos 0,
        // so assert on the raw code-point instead of the string overload.
        // カルチャ依存の IndexOf は U+FEFF を無視扱いで pos 0 に誤マッチするため、
        // 文字列オーバーロードではなくコードポイントで確認する。
        Assert.DoesNotContain('\uFEFF', content);
        Assert.Equal(2, record.Lines);
        var expectedChecksum = RawSha256Hex(Encoding.UTF8.GetBytes(content));
        Assert.Equal(expectedChecksum, record.Checksum);
    }

    [Fact]
    public void BuildRecord_Checksum_CrlfAndLfMatch()
    {
        // The same logical content cloned with CRLF line endings (Windows with
        // core.autocrlf=true) and with LF endings (Linux/macOS) must produce the same
        // checksum, so cross-OS clones / shared NAS workspaces do not trip incremental
        // re-index on every file. Standalone CR (legacy Mac classic) must collapse too.
        // Closes #1544.
        // 同じ論理内容を CRLF (Windows core.autocrlf=true) と LF (Linux/macOS) で clone
        // しても checksum が一致する必要がある。さもないと cross-OS clone や共有 NAS で
        // 初回索引時に全ファイルが「変更あり」扱いとなり再索引が走ってしまう。standalone
        // CR (旧 Mac classic) も同様に LF へ畳む。Closes #1544.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var lfPath = TestProjectHelper.WriteBinaryFile(tempDir, "lf.py", Encoding.UTF8.GetBytes("line1\nline2\nline3\n"));
        var crlfPath = TestProjectHelper.WriteBinaryFile(tempDir, "crlf.py", Encoding.UTF8.GetBytes("line1\r\nline2\r\nline3\r\n"));
        var crPath = TestProjectHelper.WriteBinaryFile(tempDir, "cr.py", Encoding.UTF8.GetBytes("line1\rline2\rline3\r"));

        var indexer = new FileIndexer(tempDir);
        var (lfRecord, _, _) = indexer.BuildRecord(lfPath);
        var (crlfRecord, _, _) = indexer.BuildRecord(crlfPath);
        var (crRecord, _, _) = indexer.BuildRecord(crPath);

        Assert.Equal(lfRecord.Checksum, crlfRecord.Checksum);
        Assert.Equal(lfRecord.Checksum, crRecord.Checksum);
        // Spot-check the expected value: SHA256 of the LF-normalized payload, so a
        // future regression that re-introduces raw-byte hashing fails loudly.
        // 期待値も固定: LF 正規化後 payload の SHA256。生バイトハッシュへ戻ると
        // 落ちるようにしておく。
        var expected = RawSha256Hex(Encoding.UTF8.GetBytes("line1\nline2\nline3\n"));
        Assert.Equal(expected, lfRecord.Checksum);
    }

    [Fact]
    public void BuildRecord_Checksum_BomAddRemoveUsesSameCanonicalContent()
    {
        // BOM-only edits should hash to the same canonical content that chunking and
        // excerpts see. This prevents freshness checks from accepting line metadata
        // produced from a different byte sequence. Closes #1467.
        // BOM のみの差分は chunk / excerpt が見る canonical content と同じ内容として
        // hash される。別のバイト列から作られた行メタデータを freshness が受け入れる
        // ずれを防ぐ。Closes #1467.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var payload = Encoding.UTF8.GetBytes("using System;\n");
        var bomPath = TestProjectHelper.WriteBinaryFile(tempDir, "bom.cs", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray());
        var noBomPath = TestProjectHelper.WriteBinaryFile(tempDir, "nobom.cs", payload);

        var indexer = new FileIndexer(tempDir);
        var (bomRecord, _, _) = indexer.BuildRecord(bomPath);
        var (noBomRecord, _, _) = indexer.BuildRecord(noBomPath);

        Assert.Equal(bomRecord.Checksum, noBomRecord.Checksum);
    }

    [Fact]
    public void BuildRecord_BomOnlyFile_ReportsNoCanonicalLines()
    {
        // A file whose on-disk bytes are exactly the UTF-8 BOM (EF BB BF) and
        // nothing else becomes empty canonical content, so the stored line count must
        // match the chunking/extraction input. Closes #1467/#1890.
        // オンディスクバイト列が UTF-8 BOM (EF BB BF) のみのファイルも、正規化後に
        // chunk/extraction へ渡す canonical content が空になるため、保存する行数も
        // その入力と一致させる。Closes #1467/#1890.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "bomonly.cs", [0xEF, 0xBB, 0xBF]);

        var indexer = new FileIndexer(tempDir);
        var (record, content, _) = indexer.BuildRecord(filePath);

        Assert.Equal(string.Empty, content);
        Assert.Equal(0, record.Lines);
    }

    [Fact]
    public void BuildRecord_MidFileBom_StrippedFromContent()
    {
        // Mid-file UTF-8 BOM (e.g. from accidental file concatenation or tool insertion)
        // must also be stripped from decoded content so `search` / `excerpt` do not emit
        // a phantom glyph. Closes #183.
        // mid-file UTF-8 BOM (ファイル連結やツール挿入) もデコード後の content から
        // 剥がし、search / excerpt に幽霊グリフを漏らさないようにする。Closes #183.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var rawBytes = Encoding.UTF8.GetBytes("using System;\n")
            .Concat(new byte[] { 0xEF, 0xBB, 0xBF })
            .Concat(Encoding.UTF8.GetBytes("namespace MidBom;\n"))
            .ToArray();
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "midbom.cs", rawBytes);

        var indexer = new FileIndexer(tempDir);
        var (_, content, _) = indexer.BuildRecord(filePath);

        Assert.DoesNotContain('\uFEFF', content);
        Assert.Contains("namespace MidBom;", content);
    }

    [Fact]
    public void BuildRecord_Utf16LeBomFile_DecodedAsUtf16()
    {
        // Files written as UTF-16 LE with BOM (FF FE) must be decoded via UTF-16, not
        // through the UTF-8 fallback that mangles every other byte into U+FFFD / NUL.
        // Closes #1540.
        // UTF-16 LE BOM (FF FE) 付きで書かれたソースは UTF-8 fallback ではなく UTF-16 で
        // デコードしなければならない。UTF-8 経路では 1 バイトおきに U+FFFD / NUL に
        // 化けてシンボル抽出が壊れる。Closes #1540.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var payload = "using System;\nnamespace Utf16Le;\n";
        var rawBytes = new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes(payload))
            .ToArray();
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "utf16le.cs", rawBytes);

        var indexer = new FileIndexer(tempDir);
        var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

        Assert.Null(warning);
        Assert.Contains("namespace Utf16Le;", content);
        Assert.DoesNotContain('�', content);
    }

    [Fact]
    public void BuildRecord_Utf16BeBomFile_DecodedAsUtf16()
    {
        // UTF-16 BE BOM (FE FF) must also be decoded via UTF-16 BE so files authored on
        // big-endian Windows or by legacy tooling keep their symbols intact. Closes #1540.
        // UTF-16 BE BOM (FE FF) も UTF-16 BE でデコードし、ビッグエンディアン Windows
        // やレガシツール由来のソースが壊れないようにする。Closes #1540.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var payload = "using System;\nnamespace Utf16Be;\n";
        var rawBytes = new byte[] { 0xFE, 0xFF }
            .Concat(Encoding.BigEndianUnicode.GetBytes(payload))
            .ToArray();
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "utf16be.cs", rawBytes);

        var indexer = new FileIndexer(tempDir);
        var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

        Assert.Null(warning);
        Assert.Contains("namespace Utf16Be;", content);
        Assert.DoesNotContain('�', content);
    }

    [Fact]
    public void BuildRecord_Utf16LeWithoutBomFile_DecodedAsUtf16()
    {
        // Legacy Windows tools can save source files as UTF-16 LE without a BOM. The
        // regular every-other-byte NUL pattern should be treated as an encoding signal,
        // not as binary content. Closes #1829.
        // 古い Windows ツールは BOM なし UTF-16 LE でソースを保存することがある。
        // 1 バイトおきの NUL パターンはバイナリ混入ではなくエンコーディングのシグナルとして扱う。
        // Closes #1829.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var payload = "using System;\nnamespace Utf16LeNoBom;\n";
        var rawBytes = Encoding.Unicode.GetBytes(payload);
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "utf16le-nobom.cs", rawBytes);

        var indexer = new FileIndexer(tempDir);
        var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

        Assert.NotNull(warning);
        Assert.Contains("UTF-16LE without BOM", warning, StringComparison.Ordinal);
        Assert.Contains("NUL-byte heuristic", warning, StringComparison.Ordinal);
        Assert.Contains("namespace Utf16LeNoBom;", content);
        Assert.False(FileIndexer.ContainsIndexBlockingNullByte(rawBytes));
    }

    [Fact]
    public void BuildRecord_Utf16BeWithoutBomFile_DecodedAsUtf16()
    {
        // The heuristic must also handle BOM-less UTF-16 BE text so the fix is not tied
        // to little-endian Windows output only. Closes #1829.
        // BOM なし UTF-16 BE テキストも扱い、little-endian Windows 出力だけに限定しない。
        // Closes #1829.
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var payload = "using System;\nnamespace Utf16BeNoBom;\n";
        var rawBytes = Encoding.BigEndianUnicode.GetBytes(payload);
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "utf16be-nobom.cs", rawBytes);

        var indexer = new FileIndexer(tempDir);
        var (_, content, _, warning) = indexer.BuildRecordWithRawBytes(filePath);

        Assert.NotNull(warning);
        Assert.Contains("UTF-16BE without BOM", warning, StringComparison.Ordinal);
        Assert.Contains("NUL-byte heuristic", warning, StringComparison.Ordinal);
        Assert.Contains("namespace Utf16BeNoBom;", content);
        Assert.False(FileIndexer.ContainsIndexBlockingNullByte(rawBytes));
    }

    [Fact]
    public void ValidateContent_Utf16LeBomFile_EmitsUtf16BomNotRawByteIssues()
    {
        // When a file decodes via UTF-16 LE, the raw bytes are full of NULs (every ASCII
        // codepoint) and the CRLF heuristic sees 0D 00 0A 00. ValidateContent must skip the
        // `bom` / `null_byte` / `mixed_line_endings` paths and emit a single `utf16_bom`
        // issue instead. Closes #1540.
        // UTF-16 LE デコード経路では生バイト列に大量の NUL が並び、CRLF 判定は 0D 00 0A 00
        // を見て誤検出する。ValidateContent は `bom` / `null_byte` / `mixed_line_endings`
        // を出さず `utf16_bom` 1 件に集約する。Closes #1540.
        var payload = "using System;\nclass C { }\n";
        var rawBytes = new byte[] { 0xFF, 0xFE }
            .Concat(System.Text.Encoding.Unicode.GetBytes(payload))
            .ToArray();
        // Simulate the content that BuildRecordWithRawBytes would produce.
        var content = payload;

        var issues = FileIndexer.ValidateContent("utf16le.cs", rawBytes, content);

        Assert.Contains(issues, i => i.Kind == "utf16_bom");
        Assert.DoesNotContain(issues, i => i.Kind == "bom");
        Assert.DoesNotContain(issues, i => i.Kind == "null_byte");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "replacement_char");
        Assert.DoesNotContain(issues, i => i.Kind == "non_utf8_likely");
    }

    [Fact]
    public void ValidateContent_Utf16WithoutBom_DoesNotEmitNullByteIssue()
    {
        var payload = "using System;\nclass C { }\n";
        var rawBytes = System.Text.Encoding.Unicode.GetBytes(payload);

        var issues = FileIndexer.ValidateContent("utf16le-nobom.cs", rawBytes, payload);

        var issue = Assert.Single(issues.Where(i => i.Kind == "utf16_heuristic"));
        Assert.Equal(1, issue.Line);
        Assert.Contains("UTF-16 LE", issue.Message, StringComparison.Ordinal);
        Assert.Contains("NUL-byte heuristic", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(issues, i => i.Kind == "utf16_bom");
        Assert.DoesNotContain(issues, i => i.Kind == "null_byte");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
    }

    [Fact]
    public void BuildRecord_NonUtf16NullByte_ThrowsOffsetDiagnostic()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "binary.cs", [(byte)'c', (byte)'l', (byte)'a', (byte)'s', (byte)'s', (byte)' ', 0x00]);

        var indexer = new FileIndexer(tempDir);
        var ex = Assert.Throws<FileIndexer.BinaryFileSkippedException>(() => indexer.BuildRecordWithRawBytes(filePath));

        Assert.Contains("NULL byte at byte offset 6", ex.Message, StringComparison.Ordinal);
        Assert.Equal("binary.cs", ex.RelativePath);
        Assert.Equal(6, ex.NullByteOffset);
    }

    [Fact]
    public void ValidateContent_HighFffdRatio_EmitsAggregateNonUtf8Likely()
    {
        // A file decoded with many U+FFFD characters (mojibake from SHIFT_JIS / GBK / Latin-1
        // misread as UTF-8) must collapse to one `non_utf8_likely` aggregate issue, not
        // hundreds of per-line `replacement_char` issues that drown the diagnostic. Closes #1540.
        // SHIFT_JIS / GBK / ISO-8859-1 を UTF-8 で読んで化けた content は per-line
        // `replacement_char` で埋め尽くすのではなく `non_utf8_likely` 1 件に集約する。
        // Closes #1540.
        // Build invalid UTF-8 bytes that decode to > 1% U+FFFD ratio and many lines.
        var raw = new List<byte>();
        for (int i = 0; i < 50; i++)
        {
            raw.AddRange(System.Text.Encoding.UTF8.GetBytes("alpha "));
            raw.Add(0xFF);
            raw.AddRange(System.Text.Encoding.UTF8.GetBytes(" beta\n"));
        }
        var rawBytes = raw.ToArray();
        var content = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: false).GetString(rawBytes);

        var issues = FileIndexer.ValidateContent("garbled.cs", rawBytes, content);

        var issue = Assert.Single(issues.Where(i => i.Kind == "non_utf8_likely"));
        Assert.Equal(FileIssue.OriginDecodeReplacement, issue.Origin);
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        // Per-line replacement_char emission must be suppressed when the aggregate fires.
        // アグリゲートが出た場合は per-line replacement_char を抑止する。
        Assert.DoesNotContain(issues, i => i.Kind == "replacement_char");
    }

    [Fact]
    public void ValidateContent_LowFffdRatio_KeepsPerLineReplacementCharIssues()
    {
        // Below the aggregate threshold (a few stray U+FFFD in an otherwise clean file),
        // the existing per-line `replacement_char` issues must still fire so genuine point
        // defects (one stray byte in an otherwise-UTF-8 file) remain actionable. Closes #1540.
        // 集約しきい値未満 (大半が正しく UTF-8 で書かれた中に数文字だけ U+FFFD が残る)
        // の場合は従来の per-line `replacement_char` を出し続け、点の不具合を見逃さない。
        // Closes #1540.
        // 4 U+FFFD chars in a long file → far below 1% ratio AND below the minimum-count
        // floor of 5, so the aggregate must not fire.
        var raw = new List<byte>();
        void AddUtf8(string text) => raw.AddRange(System.Text.Encoding.UTF8.GetBytes(text));

        AddUtf8("line1 clean\n");
        AddUtf8("line2 has ");
        raw.Add(0xFF);
        AddUtf8(" here\n");
        AddUtf8("line3 has ");
        raw.Add(0xFF);
        AddUtf8(" here\n");
        for (int i = 0; i < 200; i++) AddUtf8("filler ascii ascii ascii\n");
        AddUtf8("trailing ");
        raw.Add(0xFF);
        AddUtf8("\n");
        AddUtf8("another ");
        raw.Add(0xFF);
        AddUtf8("\n");
        var rawBytes = raw.ToArray();
        var content = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: false).GetString(rawBytes);

        var issues = FileIndexer.ValidateContent("partial.cs", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "non_utf8_likely");
        Assert.Contains(issues, i =>
            i.Kind == "replacement_char"
            && i.Origin == FileIssue.OriginDecodeReplacement
            && i.Severity == FileIssue.SeverityWarning);
        var replacementLines = issues
            .Where(i => i.Kind == "replacement_char")
            .Select(i => i.Line)
            .ToArray();
        Assert.Equal([2, 3, 204, 205], replacementLines);
    }

    [Fact]
    public void ValidateContent_SourceLiteralFffd_AnnotatesInfoOrigin()
    {
        var content = "line1 clean\nline2 has \uFFFD literal\n";
        var rawBytes = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("literal.cs", rawBytes, content);

        var issue = Assert.Single(issues.Where(i => i.Kind == "replacement_char"));
        Assert.Equal(2, issue.Line);
        Assert.Equal(FileIssue.OriginSourceLiteral, issue.Origin);
        Assert.Equal(FileIssue.SeverityInfo, issue.Severity);
        Assert.Contains("source literal", issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(issues, i => i.Kind == "non_utf8_likely");
    }

    [Fact]
    public void ValidateContent_Utf32LePrefix_NotMisclassifiedAsUtf16()
    {
        // UTF-32 LE shares the first two bytes with UTF-16 LE (FF FE 00 00). The detector
        // must exclude this prefix so a UTF-32 LE file does not get tagged with `utf16_bom`
        // and skip the raw-byte heuristics that would otherwise catch its NUL pattern.
        // Closes #1540.
        // UTF-32 LE は UTF-16 LE と先頭 2 バイトを共有する (FF FE 00 00)。この prefix を
        // 検出器から除外し、UTF-32 LE を `utf16_bom` と誤判定して NUL バイトの生バイト
        // ヒューリスティクスを飛ばさないようにする。Closes #1540.
        var rawBytes = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00 };
        // The content passed in does not matter much — what matters is that the validator
        // does not emit `utf16_bom`. We pass an ASCII placeholder.
        var content = "A";

        var issues = FileIndexer.ValidateContent("utf32le.txt", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "utf16_bom");
    }

    [Fact]
    public void StripLineLeadingInvisibles_InvisibleFreeContent_ReturnsSameInstance()
    {
        // Invisible-free content (the dominant case) must hit the fast path and
        // return the same string instance, asserting no StringBuilder is
        // allocated. Closes #1495/#2117.
        // 対象の不可視文字が無いファイル (支配的ケース) は高速パスで同じ string インスタンスを
        // 返し、StringBuilder を割り当てないことを保証する。Closes #1495/#2117.
        var input = "using System;\nnamespace Plain;\nclass C { }\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);
        Assert.Same(input, output);
    }

    [Fact]
    public void StripLineLeadingInvisibles_MidLineInvisiblesOnly_ReturnsSameInstance()
    {
        // Content that carries U+FEFF/U+200B only mid-line must also hit the
        // no-allocation path and return the same instance, so the no-op case
        // never pays the StringBuilder cost. Closes #1495/#2117.
        // 行頭以外にのみ U+FEFF/U+200B を含むファイルも割り当て無しのパスを通り、
        // 同じインスタンスを返すことを保証する。Closes #1495/#2117.
        var input = "var s = \"A\uFEFFB\";\nvar t = \"A\u200BB\";\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);
        Assert.Same(input, output);
        // Mid-line invisibles stay verbatim so the source-of-truth payload is
        // not silently corrupted for code that embeds ZWNBSP intentionally.
        // 行頭以外の不可視文字はそのまま保持し、意図的に ZWNBSP を埋め込んだ
        // コードの payload を破壊しないことを併せて確認する。
        Assert.Contains('\uFEFF', output);
        Assert.Contains('\u200B', output);
    }

    [Fact]
    public void StripLineLeadingInvisibles_EmptyContent_ReturnsSameInstance()
    {
        // Empty input must short-circuit before any scan or allocation.
        // 空入力は走査・割り当ての前に短絡することを保証する。
        Assert.Same(string.Empty, FileIndexer.StripLineLeadingInvisibles(string.Empty));
    }

    [Fact]
    public void StripLineLeadingInvisibles_LineLeadingInvisibles_StrippedWhileMidLineInvisiblesPreserved()
    {
        // File-leading and post-newline U+FEFF/U+200B markers are stripped, while
        // mid-line markers inside a literal are preserved verbatim.
        // 先頭および `\n` 直後の U+FEFF/U+200B は剥がし、行内の marker は
        // そのまま保持することを確認する。
        var input = "\uFEFFline1\n\u200Bline2 has \"A\uFEFFB\"\n\uFEFF\u200Bline3 has \"A\u200BB\"\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);

        Assert.Equal("line1\nline2 has \"A\uFEFFB\"\nline3 has \"A\u200BB\"\n", output);
    }

    [Fact]
    public void StripLineLeadingInvisibles_ConsecutiveLineLeadingInvisibles_AllStripped()
    {
        // Multiple invisible markers sharing the same logical line-start must all
        // be stripped, preserving the invariant that skipping a marker does not
        // reset `atLineStart`.
        // 同じ論理行頭に重なる連続 marker は全て剥がす。「marker スキップで
        // atLineStart を更新しない」契約を保つ。
        var input = "\uFEFF\u200Bhello\n\u200B\uFEFFworld\n";
        var output = FileIndexer.StripLineLeadingInvisibles(input);

        Assert.Equal("hello\nworld\n", output);
    }

    [Fact]
    public void BuildRecord_ChecksumUsesCanonicalContentAfterLineLeadingBomStrip()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var plainPath = TestProjectHelper.WriteTextFile(tempDir, "plain.cs", "class Plain\n{\n}\n");
        var bomPath = TestProjectHelper.WriteTextFile(tempDir, "bom.cs", "\uFEFFclass Plain\n\uFEFF{\n}\n");

        var indexer = new FileIndexer(tempDir);
        var (plainRecord, plainContent, _) = indexer.BuildRecord(plainPath);
        var (bomRecord, bomContent, _) = indexer.BuildRecord(bomPath);

        Assert.Equal(plainContent, bomContent);
        Assert.Equal(plainRecord.Checksum, bomRecord.Checksum);
        Assert.Equal(plainRecord.Lines, bomRecord.Lines);
    }

    [Fact]
    public void BuildRecord_GitLfsPointerIndexesEmptyBodyAndValidationIssue()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var pointer = """
            version https://git-lfs.github.com/spec/v1
            oid sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            size 12345
            """;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "asset.cs", pointer);

        var indexer = new FileIndexer(tempDir);
        var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
        var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);

        Assert.Equal(string.Empty, content);
        Assert.Equal(0, record.Lines);
        Assert.Equal(FileIndexer.ComputeChecksum(rawBytes), record.Checksum);
        Assert.NotEqual(FileIndexer.ComputeChecksum(Encoding.UTF8.GetBytes(string.Empty)), record.Checksum);
        var issue = Assert.Single(issues, i => i.Kind == "lfs_pointer_skipped");
        Assert.Equal("asset.cs", issue.Path);
        Assert.Equal(1, issue.Line);
    }

    [Fact]
    public void BuildRecord_ConfiguredGeneratedPatternBuildsExtractionIssueWithoutGeneratedFlag()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteTextFile(
            tempDir,
            "src/generated/Client.cs",
            "public class Client { public string Lookup() => \"ok\"; }\n");

        var indexer = new FileIndexer(
            tempDir,
            ignoreCase: false,
            ignoreRuleRoot: null,
            generatedCodePatterns: ["src/generated/**"]);
        var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
        var issue = indexer.BuildGeneratedCodeExtractionSkippedIssue(record.Path);

        Assert.False(record.Generated);
        Assert.Equal("src/generated/Client.cs", record.Path);
        Assert.Contains("public class Client", content, StringComparison.Ordinal);
        Assert.True(rawBytes.Length > 0);
        Assert.NotNull(issue);
        Assert.Equal(FileIndexer.GeneratedCodeExtractionSkippedIssueKind, issue.Kind);
        Assert.Equal("src/generated/Client.cs", issue.Path);
        Assert.Contains("symbols and references were skipped", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecord_GitLfsVersionLineWithoutPointerShapePreservesContent()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var text = """
            version https://git-lfs.github.com/spec/v1
            This is documentation text, not a Git LFS pointer.
            """;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "example.txt", text);

        var indexer = new FileIndexer(tempDir);
        var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
        var issues = FileIndexer.ValidateContent(record.Path, rawBytes, content);

        Assert.Equal(text.Replace("\r\n", "\n", StringComparison.Ordinal), content);
        Assert.DoesNotContain(issues, issue => issue.Kind == "lfs_pointer_skipped");
    }

    [Fact]
    public void BuildRecord_ThrowsFileTooLargeSkippedExceptionForOversizedFile()
    {
        // Files exceeding the default cap should carry structured skip metadata.
        // 既定上限を超えるファイルは structured skip metadata を持つ例外を投げる。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.ProjectPath(tempDir, "large.py");
        // Create a sparse file just over the default cap without allocating a matching test buffer.
        // 既定上限を少し超える sparse file を作り、同サイズのテスト用 buffer 確保を避ける。
        using (var stream = File.Create(filePath))
            stream.SetLength(FileIndexer.DefaultMaxFileSizeBytes + 1);

        var indexer = new FileIndexer(tempDir);
        var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() => indexer.BuildRecord(filePath));
        Assert.Equal("large.py", ex.RelativePath);
        Assert.Equal(FileIndexer.DefaultMaxFileSizeBytes + 1, ex.ActualBytes);
        Assert.Equal(FileIndexer.DefaultMaxFileSizeBytes, ex.LimitBytes);
    }

    [Fact]
    public void BuildRecord_DefaultRejectsTenMiBFileBeforeReadingPayload()
    {
        // Regression for #1695: a 10 MiB source file must be rejected from the
        // observed stream length before the indexer accumulates one contiguous
        // 10 MiB byte array on the LOH.
        // #1695 の回帰: 10 MiB の source file は stream length の確認時点で拒否し、
        // インデクサが LOH 上に連続した 10 MiB byte 配列を累積しないことを固定する。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.ProjectPath(tempDir, "large.py");
        using (var stream = File.Create(filePath))
            stream.SetLength(10 * 1024 * 1024);

        var indexer = new FileIndexer(tempDir);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var ex = Assert.Throws<FileIndexer.FileTooLargeSkippedException>(() => indexer.BuildRecord(filePath));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("File too large", ex.Message);
        Assert.True(allocated < 1024 * 1024, $"Expected rejection before a 10 MiB payload allocation, saw {allocated} bytes allocated.");
    }

    [Fact]
    public void BuildRecord_AcceptsFileAtSizeLimitBoundary()
    {
        // Regression for #1529: the TOCTOU fix reads through one FileStream and caps the
        // accumulator at MaxFileSize. A file at exactly the default cap must still be accepted so
        // the boundary contract documented by the oversize test stays symmetric (>cap
        // throws, ==cap succeeds).
        // #1529 のリグレッション: TOCTOU 修正で 1 本の FileStream を通して MaxFileSize で
        // 累積バッファを打ち切る実装にした際、ちょうど既定上限のファイルは引き続き受け
        // 入れる必要がある (>上限 が throw / ==上限 が成功という対称契約を維持)。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        // Exactly MaxFileSize bytes — ASCII so UTF-8 decode succeeds without warning.
        // ちょうど MaxFileSize バイト — ASCII なら UTF-8 デコードで警告無く成功する。
        var data = new byte[(int)FileIndexer.DefaultMaxFileSizeBytes];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)'a';

        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "boundary.py", data);
        var indexer = new FileIndexer(tempDir);
        var (record, content, _) = indexer.BuildRecord(filePath);

        Assert.Equal(data.Length, record.Size);
        Assert.Equal(data.Length, content.Length);
    }

    [Fact]
    public void BuildRecord_RecordSizeReflectsBytesActuallyRead()
    {
        // Regression for #1529: with the FileStream-based read path, record.Size must
        // come from the bytes streamed through the open handle rather than from a
        // separate FileInfo.Length stat. Asserting record.Size against the byte count
        // documents the contract that downstream consumers (status, freshness checks)
        // see the same value the indexer actually ingested.
        // #1529 のリグレッション: FileStream ベースの読み込み経路では record.Size は
        // 別途取得した FileInfo.Length ではなく、オープンしたハンドル経由で実際に読み
        // 込んだバイト数を反映しなければならない。`record.Size` をバイト数と突き合わ
        // せることで、status や freshness check の下流が実際に取り込まれた値と一致
        // することを契約として固定する。
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var payload = "print('hello world')\n"u8.ToArray();
        var filePath = TestProjectHelper.WriteBinaryFile(tempDir, "sized.py", payload);

        var indexer = new FileIndexer(tempDir);
        var (record, _, _) = indexer.BuildRecord(filePath);

        Assert.Equal(payload.Length, record.Size);
    }

    [Fact]
    public void BuildRecord_ExtensionlessShebangScriptUsesDetectedLanguage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_test");
        var tempDir = project.Root;
        var filePath = TestProjectHelper.WriteTextFile(tempDir, "rbenv-hooks", "#!/usr/bin/env bash\necho hooks\n");

        var indexer = new FileIndexer(tempDir);
        var (record, _, warning) = indexer.BuildRecord(filePath);

        Assert.Equal("shell", record.Lang);
        Assert.Null(warning);
    }

    private static string CreateWindowsLongPathFixture(string projectRoot)
    {
        var current = Path.Combine(
            projectRoot,
            ".pnpm",
            "fixture-pkg@1.0.0",
            "fixture-pkg");
        var segment = 0;

        while (Path.Combine(current, "long-file.js").Length < 260)
            current = Path.Combine(current, $"segment{segment++:D2}");

        Directory.CreateDirectory(LongPath.EnsureWindowsPrefix(current));
        var leafPath = Path.Combine(current, "long-file.js");
        File.WriteAllText(LongPath.EnsureWindowsPrefix(leafPath), "export function longPathFixture() { return 42; }\n");
        return leafPath;
    }

    private static void DeleteLongPathDirectory(string path)
    {
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(path)))
            return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                DeleteLongPathDirectoryRecursive(path);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                TestProjectHelper.WaitForFileSystemReleaseRetry();
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                TestProjectHelper.WaitForFileSystemReleaseRetry();
            }
        }
    }

    private static void DeleteLongPathDirectoryRecursive(string path)
    {
        var prefixedPath = LongPath.EnsureWindowsPrefix(path);
        File.SetAttributes(prefixedPath, FileAttributes.Normal);

        foreach (var file in Directory.EnumerateFiles(prefixedPath))
        {
            var filePath = LongPath.RemoveWindowsPrefix(file);
            var prefixedFilePath = LongPath.EnsureWindowsPrefix(filePath);
            File.SetAttributes(prefixedFilePath, FileAttributes.Normal);
            File.Delete(prefixedFilePath);
        }

        foreach (var dir in Directory.EnumerateDirectories(prefixedPath))
            DeleteLongPathDirectoryRecursive(LongPath.RemoveWindowsPrefix(dir));

        Directory.Delete(prefixedPath);
    }

    private static void IndexScannedFiles(string projectRoot, DbWriter writer)
    {
        var indexer = new FileIndexer(projectRoot);
        foreach (var filePath in indexer.ScanFiles())
        {
            var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
            var fileId = writer.UpsertFile(record);
            writer.DeleteFileData(fileId);
            writer.InsertChunks(ChunkSplitter.Split(fileId, content));
            var symbols = SymbolExtractor.Extract(fileId, record.Lang, content, record.Path);
            writer.InsertSymbols(symbols);
            writer.InsertReferences(ReferenceExtractor.Extract(fileId, record.Lang, content, symbols, record.Path));
            writer.InsertIssues(fileId, FileIndexer.ValidateContent(record.Path, rawBytes, content));
        }
    }

    private static List<string> ScanRelativeFiles(string projectRoot)
        => ScanRelativeFiles(new FileIndexer(projectRoot), projectRoot);

    private static List<string> ScanRelativeFiles(FileIndexer indexer, string projectRoot)
        => ToSortedRelativePaths(projectRoot, indexer.ScanFiles());

    private static List<string> ToSortedRelativePaths(string projectRoot, IEnumerable<string> paths)
        => paths
            .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static HashSet<string> ToRelativePathSet(string projectRoot, IEnumerable<string> paths)
        => paths
            .Select(path => Path.GetRelativePath(projectRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

    private static bool IndexedFileExists(DbContext db, string relativePath)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", relativePath);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void CreateUnixFifo(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "mkfifo",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(path);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start mkfifo / mkfifo の起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mkfifo failed: {stderr.Trim()}");
    }

    private static void CreateHardLink(string existingPath, string newPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ln",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(existingPath);
        psi.ArgumentList.Add(newPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ln / ln の起動に失敗");
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ln failed: {stderr.Trim()}");
    }

    private static string RunGit(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");

        return stdout;
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetUnixPermissions(string path, UnixFileMode mode)
    {
        File.SetUnixFileMode(path, mode);
    }

    // Bare CR (legacy Mac) line endings used to be silently normalized to LF by
    // BuildRecordWithRawBytes, hiding line-counting / regex assumptions that
    // may be wrong elsewhere. Issue #1538: detect CR-only and three-way mixes
    // so they surface in `cdidx validate` / file_issues.
    // BuildRecordWithRawBytes が CR (旧 Mac) 行末を黙って LF に正規化していた問題
    // (Issue #1538) に対し、CR-only と 3 種混在を検出して file_issues に出す。
    [Fact]
    public void ValidateContent_CrOnlyLineEndings_EmitsCrOnlyIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("line1\rline2\rline3\r");
        var content = "line1\nline2\nline3\n";

        var issues = FileIndexer.ValidateContent("legacy_mac.txt", rawBytes, content);

        var crOnly = Assert.Single(issues, i => i.Kind == "cr_only_line_endings");
        Assert.Equal(0, crOnly.Line);
        Assert.Contains("CR-only", crOnly.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
    }

    [Theory]
    [InlineData("VOLUME [\"/var/lib/app\",", "VOLUME")]
    [InlineData("SHELL [\"/bin/sh\",", "SHELL")]
    [InlineData("COPY --chmod=0644 [\"package.json\",", "COPY")]
    [InlineData("ADD [\"archive.tar.gz\",", "ADD")]
    public void ValidateContent_DockerfileMalformedJsonForms_EmitValidationIssue(string line, string instruction)
    {
        var content = line + "\n";
        var rawBytes = Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("Dockerfile", rawBytes, content, "dockerfile");

        var issue = Assert.Single(issues, i => i.Kind == "dockerfile_json_form_invalid");
        Assert.Equal(1, issue.Line);
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        Assert.Contains(instruction, issue.Message);
    }

    [Fact]
    public void ValidateContent_DockerfileJsonFormsBeyondParserDepthLimit_EmitValidationIssue_Issue3713()
    {
        var depth = SymbolExtractor.DockerfileJsonFormMaxDepth + 1;
        var content = "VOLUME " + new string('[', depth) + "\"/too-deep\"" + new string(']', depth) + "\n";
        var rawBytes = Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("Dockerfile", rawBytes, content, "dockerfile");

        var issue = Assert.Single(issues, i => i.Kind == "dockerfile_json_form_invalid");
        Assert.Equal(1, issue.Line);
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        Assert.Contains("VOLUME", issue.Message);
    }

    [Fact]
    public void ValidateContent_MsBuildXmlBudgetExceeded_EmitsValidationIssue_Issue3801()
    {
        var depth = SymbolExtractor.XmlExtractionMaxDepth + 2;
        var content = "<Project>" + string.Concat(Enumerable.Repeat("<PropertyGroup>", depth))
            + string.Concat(Enumerable.Repeat("</PropertyGroup>", depth)) + "</Project>";
        var rawBytes = Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("App.csproj", rawBytes, content, "msbuild");

        var issue = Assert.Single(issues, i => i.Kind == "xml_structure_budget_exceeded");
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        Assert.Contains("depth", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateContent_XmlDtd_EmitsValidationIssue_Issue3801()
    {
        const string content = """
            <!DOCTYPE root [
              <!ENTITY injected "value">
            ]>
            <root />
            """;
        var rawBytes = Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("app.config", rawBytes, content, "xml");

        var issue = Assert.Single(issues, i => i.Kind == "xml_dtd_prohibited");
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        Assert.Contains("DTD", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("VOLUME")]
    [InlineData("COPY")]
    public void ValidateContent_DockerfileJsonFormsBeyondItemCap_EmitTruncationIssue(string instruction)
    {
        var items = Enumerable.Range(0, SymbolExtractor.DockerfileJsonFormMaxItems + 1)
            .Select(i => JsonSerializer.Serialize($"/item{i}"));
        var content = instruction + " [" + string.Join(", ", items) + "]\n";
        var rawBytes = Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("Dockerfile", rawBytes, content, "dockerfile");

        var issue = Assert.Single(issues, i => i.Kind == "dockerfile_json_form_truncated");
        Assert.Equal(1, issue.Line);
        Assert.Equal(FileIssue.SeverityWarning, issue.Severity);
        Assert.Contains(instruction, issue.Message);
    }

    [Fact]
    public void ValidateContent_NonDockerfileJsonLikeLine_DoesNotEmitDockerfileJsonIssue()
    {
        var content = "VOLUME [\"not docker\",";
        var rawBytes = Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("script.sh", rawBytes, content, "shell");

        Assert.DoesNotContain(issues, i => i.Kind.StartsWith("dockerfile_json_form_", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateContent_ThreeWayLineEndings_EmitsThreeWayIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\nlf-only\rcr-only\n");
        var content = "crlf\nlf-only\ncr-only\n";

        var issues = FileIndexer.ValidateContent("three_way.txt", rawBytes, content);

        var threeWay = Assert.Single(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.Equal(0, threeWay.Line);
        Assert.Contains("CRLF", threeWay.Message);
        Assert.Contains("LF", threeWay.Message);
        Assert.Contains("CR", threeWay.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_CrlfPlusCrOnly_EmitsMixedIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\ncr-only\rmore-crlf\r\n");
        var content = "crlf\ncr-only\nmore-crlf\n";

        var issues = FileIndexer.ValidateContent("mixed_crlf_cr.txt", rawBytes, content);

        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal(0, mixed.Line);
        Assert.Contains("CRLF and CR", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
    }

    [Fact]
    public void ValidateContent_LfPlusCrOnly_EmitsMixedIssue()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("lf\nthen-cr\rback-to-lf\n");
        var content = "lf\nthen-cr\nback-to-lf\n";

        var issues = FileIndexer.ValidateContent("mixed_lf_cr.txt", rawBytes, content);

        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal(0, mixed.Line);
        Assert.Contains("LF and CR", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_CrlfPlusLf_StillEmitsExistingMixedIssue()
    {
        // Regression guard: existing CRLF+LF kind / message must not change.
        // 既存の CRLF+LF kind / メッセージが変わっていないことの回帰ガード。
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\nlf\n");
        var content = "crlf\nlf\n";

        var issues = FileIndexer.ValidateContent("mixed.txt", rawBytes, content);

        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal("Mixed line endings (CRLF and LF)", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
    }

    [Fact]
    public void ValidateContent_PureCrlf_DoesNotFlagLineEndings()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("a\r\nb\r\nc\r\n");
        var content = "a\nb\nc\n";

        var issues = FileIndexer.ValidateContent("pure_crlf.txt", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_PureLf_DoesNotFlagLineEndings()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("a\nb\nc\n");
        var content = "a\nb\nc\n";

        var issues = FileIndexer.ValidateContent("pure_lf.txt", rawBytes, content);

        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings");
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_NullByteAndMixedLineEndings_EmitsBothRawByteIssues()
    {
        var rawBytes = System.Text.Encoding.UTF8.GetBytes("crlf\r\npayload\n");
        rawBytes[6] = 0x00;
        var content = System.Text.Encoding.UTF8.GetString(rawBytes).Replace("\r\n", "\n", StringComparison.Ordinal);

        var issues = FileIndexer.ValidateContent("mixed_binary.txt", rawBytes, content);

        Assert.Contains(issues, i => i.Kind == "null_byte");
        var mixed = Assert.Single(issues, i => i.Kind == "mixed_line_endings");
        Assert.Equal("Mixed line endings (CRLF and LF)", mixed.Message);
        Assert.DoesNotContain(issues, i => i.Kind == "mixed_line_endings_three_way");
        Assert.DoesNotContain(issues, i => i.Kind == "cr_only_line_endings");
    }

    [Fact]
    public void ValidateContent_ConflictMarkers_EmitsConflictMarkerIssue()
    {
        var content = """
        class Example
        {
        <<<<<<< HEAD
            void MainVersion() {}
        =======
            void BranchVersion() {}
        >>>>>>> feature
        }
        """;
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("Example.cs", raw, content);

        var conflictMarkers = Assert.Single(issues, i => i.Kind == "conflict_markers");
        Assert.Equal(3, conflictMarkers.Line);
        Assert.Contains("Git conflict markers", conflictMarkers.Message);
    }

    [Fact]
    public void ValidateContent_ConflictEndMarkerOnly_EmitsConflictMarkerIssue()
    {
        var content = "first\n>>>>>>> feature\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("Example.cs", raw, content);

        var conflictMarkers = Assert.Single(issues, i => i.Kind == "conflict_markers");
        Assert.Equal(2, conflictMarkers.Line);
    }

    [Fact]
    public void Extractors_ConflictMarkers_ReturnEmptySymbolsAndReferences()
    {
        var content = """
        class Example
        {
        <<<<<<< HEAD
            void MainVersion() {}
        =======
            void BranchVersion() {}
        >>>>>>> feature
        }
        """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content, "Example.cs");
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols, "Example.cs");

        Assert.Empty(symbols);
        Assert.Empty(references);
    }

    [Fact]
    public void ValidateContent_SetextHeadingSeparator_DoesNotEmitConflictMarkerIssue()
    {
        var content = "Title\n=======\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("README.md", raw, content);

        Assert.DoesNotContain(issues, i => i.Kind == "conflict_markers");
    }

    [Fact]
    public void ValidateContent_OversizeLine_EmitsLineTooLongIssue()
    {
        // A single physical line longer than ChunkSplitter.MaxLineLength (e.g.
        // 1 MB minified `.min.js`) must surface as a `line_too_long` FileIssue
        // pointing at the offending 1-based line number, so the chunk / symbol /
        // reference skip path is observable from the existing issues channel.
        // Closes #1542.
        // ChunkSplitter.MaxLineLength を超える単一物理行 (例: 1 MB minified
        // .min.js) は、対象行を 1-based 行番号で指す `line_too_long` FileIssue
        // として表面化させ、chunk / symbol / reference スキップ経路を既存の
        // issues 経路から観測できるようにする。Closes #1542.
        var oversize = new string('a', ChunkSplitter.MaxLineLength + 1);
        var content = "ok\n" + oversize + "\nok\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("bundle.min.js", raw, content);

        var lineTooLong = Assert.Single(issues, i => i.Kind == "line_too_long");
        Assert.Equal(2, lineTooLong.Line);
        Assert.Contains("exceeds", lineTooLong.Message);
    }

    [Fact]
    public void ValidateContent_NoOversizeLine_DoesNotEmitLineTooLongIssue()
    {
        // Files whose every physical line stays within the cap must not be
        // flagged, even when the total content is large. The cap is per
        // physical line, not per file. Closes #1542.
        // すべての物理行が上限以内なら、ファイル全体のサイズが大きくても
        // フラグは立たない。上限は物理行ごとに適用される。Closes #1542.
        var line = new string('a', 1024);
        var content = string.Join('\n', Enumerable.Repeat(line, 200));
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("ok.js", raw, content);

        Assert.DoesNotContain(issues, i => i.Kind == "line_too_long");
    }

    [Fact]
    public void ValidateContent_OversizeFtsToken_EmitsFtsTokenTooLongIssue()
    {
        var token = new string('x', CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength + 1);
        var content = "ok\nconst value = " + token + ";\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("generated.js", raw, content);

        var issue = Assert.Single(issues, i => i.Kind == "fts_token_too_long");
        Assert.Equal(2, issue.Line);
        Assert.Contains("bounded long-token fallback", issue.Message);
    }

    [Fact]
    public void ValidateContent_OversizeUnicodeFtsToken_EmitsFtsTokenTooLongIssue()
    {
        var token = new string('計', CodeIndex.Database.DbReader.FtsUnicode61MaxTokenLength + 1);
        var content = "ok\n" + token + "\n";
        var raw = System.Text.Encoding.UTF8.GetBytes(content);

        var issues = FileIndexer.ValidateContent("generated_unicode.py", raw, content);

        var issue = Assert.Single(issues, i => i.Kind == "fts_token_too_long");
        Assert.Equal(2, issue.Line);
        Assert.Contains("bounded long-token fallback", issue.Message);
    }

    [Fact]
    public void SymbolExtractor_Extract_OversizeLine_ReturnsEmpty()
    {
        // SymbolExtractor must mirror the ChunkSplitter oversize-line skip so
        // regex-based symbol extraction does not stall on minified payloads.
        // The content below would otherwise expose dozens of `function`
        // signatures to the JavaScript symbol pattern loop. Closes #1542.
        // SymbolExtractor も ChunkSplitter の oversize-line スキップに揃え、
        // 正規表現ベースのシンボル抽出が minified ペイロードで停止しないよう
        // にする。下記の内容は通常なら JavaScript シンボルパターンで
        // 多数の `function` シグネチャを露出させる。Closes #1542.
        var oversize = string.Concat(Enumerable.Repeat("function f(){}", ChunkSplitter.MaxLineLength / 14 + 1));
        var symbols = SymbolExtractor.Extract(fileId: 1, lang: "javascript", content: oversize, filePath: "bundle.min.js");
        Assert.Empty(symbols);
    }

    [Fact]
    public void ReferenceExtractor_Extract_OversizeLine_ReturnsEmpty()
    {
        // ReferenceExtractor must mirror the ChunkSplitter oversize-line skip
        // so regex-based reference extraction does not stall on minified
        // payloads. Closes #1542.
        // ReferenceExtractor も ChunkSplitter の oversize-line スキップに揃え、
        // 正規表現ベースの参照抽出が minified ペイロードで停止しないように
        // する。Closes #1542.
        var oversize = string.Concat(Enumerable.Repeat("foo();bar();", ChunkSplitter.MaxLineLength / 12 + 1));
        var refs = ReferenceExtractor.Extract(fileId: 1, lang: "javascript", content: oversize, symbols: Array.Empty<CodeIndex.Models.SymbolRecord>(), path: "bundle.min.js");
        Assert.Empty(refs);
    }

    [Theory]
    [InlineData("Foo.Designer.cs", "class Foo { }")]
    [InlineData("foo_pb.go", "package foo")]
    [InlineData("foo_pb2.py", "class Foo: pass")]
    [InlineData("model.g.dart", "class Model {}")]
    [InlineData("api.generated.ts", "export const x = 1;")]
    [InlineData("handwritten.cs", "// <auto-generated>\nclass Foo { }")]
    [InlineData("handwritten.go", "// Code generated by protoc. DO NOT EDIT.\npackage foo")]
    [InlineData("handwritten.py", "# @generated\nclass Foo: pass")]
    public void IsGeneratedCodeFile_MarkersAndNames_ReturnsTrue(string path, string content)
    {
        Assert.True(FileIndexer.IsGeneratedCodeFile(path, content));
    }

    [Fact]
    public void IsGeneratedCodeFile_LongFirstLineLimitsHeaderScan()
    {
        var farMarker = new string('x', 20 * 1024) + " // <auto-generated>\nclass Foo { }\n";

        Assert.True(FileIndexer.IsGeneratedCodeFile("src/Foo.cs", "// <auto-generated>\n" + new string('x', 20 * 1024)));
        Assert.False(FileIndexer.IsGeneratedCodeFile("src/Foo.cs", farMarker));
    }

    [Fact]
    public void IsGeneratedCodeFile_HandwrittenFile_ReturnsFalse()
    {
        Assert.False(FileIndexer.IsGeneratedCodeFile("src/Foo.cs", "class Foo { }\n"));
        Assert.False(FileIndexer.IsGeneratedCodeFile("src/Foo.cs", "// This file is not auto-generated.\nclass Foo { }\n"));
    }

    [Fact]
    public void TryComputeChecksum_CancellationDuringRead_ThrowsOperationCanceled_Issue3784()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new CancelAfterFirstReadStream(Encoding.UTF8.GetBytes("first\nsecond\n"), cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            FileContentLoader.TryComputeChecksum(stream, long.MaxValue, out _, cancellation.Token));
    }

    [Fact]
    public void TryComputeChecksum_SeekableOversizeStream_ReturnsFalseWithoutReading()
    {
        using var stream = new MemoryStream(new byte[32]);

        var computed = FileContentLoader.TryComputeChecksum(stream, maxBytes: 8, out var checksum);

        Assert.False(computed);
        Assert.Equal(string.Empty, checksum);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void TryComputeChecksum_FilePathAllowsConcurrentWriterShare_Issue4078()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("codeindex_checksum_share");
        var tempDir = project.Root;
        var bytes = Encoding.UTF8.GetBytes("class Sample {}\n");
        var path = TestProjectHelper.WriteBinaryFile(tempDir, "sample.cs", bytes);

        using var writer = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.True(FileContentLoader.TryComputeChecksum(
            path,
            FileIndexer.DefaultMaxFileSizeBytes,
            out var checksum));
        Assert.Equal(FileIndexer.ComputeChecksum(bytes), checksum);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsShortPath(string path, out string shortPath)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
        {
            shortPath = string.Empty;
            return false;
        }

        shortPath = buffer.ToString();
        return shortPath.Length > 0;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder shortPath,
        uint bufferLength);

    private static byte[] CreateFixedUnknownProbePayload(string prefix, int length)
    {
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        Assert.True(prefixBytes.Length <= length);
        var bytes = Enumerable.Repeat((byte)'x', length).ToArray();
        prefixBytes.CopyTo(bytes, 0);
        return bytes;
    }

    private sealed class CountingCSharpPrepassFileStream : FileStream
    {
        private readonly int _maxReadBytes;
        private readonly Action? _afterFirstRead;
        private bool _firstReadObserved;

        internal CountingCSharpPrepassFileStream(
            string path,
            int maxReadBytes,
            Action? afterFirstRead = null)
            : base(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete)
        {
            _maxReadBytes = maxReadBytes;
            _afterFirstRead = afterFirstRead;
        }

        internal long BytesRead { get; private set; }
        internal long RawProbeBytes { get; private set; }
        internal int RewindCount { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, Math.Min(count, _maxReadBytes));
            RecordRead(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer[..Math.Min(buffer.Length, _maxReadBytes)]);
            RecordRead(read);
            return read;
        }

        public override int ReadByte()
        {
            var value = base.ReadByte();
            if (value >= 0)
                BytesRead++;
            return value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (offset == 0 && origin == SeekOrigin.Begin && Position > 0)
            {
                if (RewindCount == 0)
                    RawProbeBytes = BytesRead;
                RewindCount++;
            }
            return base.Seek(offset, origin);
        }

        private void RecordRead(int read)
        {
            BytesRead += read;
            if (read <= 0 || _firstReadObserved)
                return;

            _firstReadObserved = true;
            _afterFirstRead?.Invoke();
        }
    }

}
