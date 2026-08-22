using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class LocalJsonlJsonWriterOptionsTests
{
    [Fact]
    public void Create_ReturnsRelaxedLocalJsonlOptions()
    {
        var options = LocalJsonlJsonWriterOptions.Create();

        Assert.False(options.Indented);
        Assert.Same(JavaScriptEncoder.UnsafeRelaxedJsonEscaping, options.Encoder);
    }

    [Fact]
    public void RelaxedEncoderAndHelperStayLimitedToLocalJsonlSinks()
    {
        var repositoryRoot = RepositoryTestPaths.Root;
        AssertOnlyAllowedFilesContain(
            repositoryRoot,
            "UnsafeRelaxedJsonEscaping",
            new[]
            {
                "src/CodeIndex/Diagnostics/LocalJsonlJsonWriterOptions.cs",
                "src/CodeIndex/Cli/SearchAuditRecipes.cs",
                "tests/CodeIndex.Tests/LocalJsonlJsonWriterOptionsTests.cs",
                "tests/CodeIndex.Tests/QueryCommandRunnerSearchTests.cs",
            });
        AssertOnlyAllowedFilesContain(
            repositoryRoot,
            "LocalJsonlJsonWriterOptions.Create()",
            new[]
            {
                "src/CodeIndex/Cli/MetricsSink.cs",
                "src/CodeIndex/Mcp/AuditLogSink.cs",
                "tests/CodeIndex.Tests/LocalJsonlJsonWriterOptionsTests.cs",
            });
    }

    [Fact]
    public void SourceEnumerationPrunesBuildOutputBeforeTraversingChildren()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "repository");
        var sourceRoot = Path.Combine(repositoryRoot, "src", "CodeIndex");
        var sourceFeatureRoot = Path.Combine(sourceRoot, "Feature");
        var testRoot = Path.Combine(repositoryRoot, "tests", "CodeIndex.Tests");
        var sourceBinRoot = Path.Combine(sourceRoot, "bin");
        var testObjRoot = Path.Combine(testRoot, "obj");
        var buildOutputRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            sourceBinRoot,
            testObjRoot,
        };
        var directoryChildren = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [sourceRoot] = [sourceFeatureRoot, sourceBinRoot],
            [sourceFeatureRoot] = [],
            [testRoot] = [testObjRoot],
        };
        var filesByDirectory = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [sourceRoot] = [Path.Combine(sourceRoot, "Program.cs")],
            [sourceFeatureRoot] = [Path.Combine(sourceFeatureRoot, "Worker.cs")],
            [testRoot] = [Path.Combine(testRoot, "GuardTests.cs")],
        };

        IEnumerable<string> EnumerateDirectories(string directory)
        {
            Assert.DoesNotContain(directory, buildOutputRoots);
            return directoryChildren.TryGetValue(directory, out var children) ? children : [];
        }

        IEnumerable<string> EnumerateFiles(string directory)
        {
            Assert.DoesNotContain(directory, buildOutputRoots);
            return filesByDirectory.TryGetValue(directory, out var files) ? files : [];
        }

        var files = EnumerateSourceFiles(
                repositoryRoot,
                enumerateFiles: EnumerateFiles,
                enumerateDirectories: EnumerateDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                Path.Combine(sourceFeatureRoot, "Worker.cs"),
                Path.Combine(sourceRoot, "Program.cs"),
                Path.Combine(testRoot, "GuardTests.cs"),
            }.OrderBy(path => path, StringComparer.Ordinal),
            files);
    }

    [Fact]
    public void Create_PreservesHtmlLikeCharactersWhileKeepingJsonlLineParseable()
    {
        const string message = "tail<>&\"' / </script><script>alert(\"x\")</script>\nnext";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, LocalJsonlJsonWriterOptions.Create()))
        {
            writer.WriteStartObject();
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        var jsonl = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.Contains("</script><script>", jsonl);
        Assert.Contains("<>&", jsonl);
        Assert.DoesNotContain("\\u003C", jsonl);
        Assert.DoesNotContain("\\u003E", jsonl);
        Assert.DoesNotContain("\\u0026", jsonl);
        Assert.DoesNotContain("\n", jsonl);
        Assert.Contains("\\n", jsonl);

        using var document = JsonDocument.Parse(jsonl);
        Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
    }

    private static void AssertOnlyAllowedFilesContain(
        string repositoryRoot,
        string text,
        IEnumerable<string> allowedRelativePaths)
    {
        var allowed = new HashSet<string>(
            allowedRelativePaths.Select(NormalizeRelativePath),
            StringComparer.Ordinal);
        var offenders = EnumerateSourceFiles(repositoryRoot)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, path)),
            })
            .Where(file => !allowed.Contains(file.RelativePath))
            .Where(file => File.ReadAllText(file.FullPath).Contains(text, StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> EnumerateSourceFiles(
        string repositoryRoot,
        Func<string, IEnumerable<string>>? enumerateFiles = null,
        Func<string, IEnumerable<string>>? enumerateDirectories = null)
    {
        enumerateFiles ??= directory =>
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly);
        enumerateDirectories ??= Directory.EnumerateDirectories;

        foreach (var root in new[] { "src/CodeIndex", "tests/CodeIndex.Tests" })
        {
            var fullRoot = Path.Combine(repositoryRoot, root);
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(fullRoot);

            while (pendingDirectories.TryPop(out var directory))
            {
                foreach (var path in enumerateFiles(directory))
                    yield return path;

                foreach (var childDirectory in enumerateDirectories(directory))
                {
                    if (!IsBuildOutputDirectory(childDirectory))
                        pendingDirectories.Push(childDirectory);
                }
            }
        }
    }

    private static bool IsBuildOutputDirectory(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is "bin" or "obj";

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

}
