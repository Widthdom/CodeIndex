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
        var repositoryRoot = FindRepositoryRootForTest();
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

    private static IEnumerable<string> EnumerateSourceFiles(string repositoryRoot)
    {
        foreach (var root in new[] { "src/CodeIndex", "tests/CodeIndex.Tests" })
        {
            var fullRoot = Path.Combine(repositoryRoot, root);
            foreach (var path in Directory.EnumerateFiles(fullRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(path))
                    continue;
                yield return path;
            }
        }
    }

    private static bool IsBuildOutput(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj")
                return true;
        }

        return false;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string FindRepositoryRootForTest()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CHANGELOG.md")) &&
                File.Exists(Path.Combine(current.FullName, "CodeIndex.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
