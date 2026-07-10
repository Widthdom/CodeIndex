using System.Text;

namespace CodeIndex.Tests;

public class TestProjectHelperTests
{
    [Fact]
    public void WriteTextFile_CreatesParentDirectoriesAndReadTextFileReadsFixtureContent()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_fixture_write");
        var projectRoot = project.Root;
        var directoryPath = TestProjectHelper.CreateDirectory(projectRoot, "generated", "nested");
        var filePath = TestProjectHelper.WriteTextFile(projectRoot, Path.Combine("src", "App.cs"), "class App {}\n");
        TestProjectHelper.AppendTextFile(projectRoot, Path.Combine("src", "App.cs"), "// appended\n");
        var encodedPath = TestProjectHelper.WriteTextFile(projectRoot, Path.Combine("encoded", "unicode.txt"), "雪\n", Encoding.Unicode);
        TestProjectHelper.WriteTextFiles(
            projectRoot,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Path.Combine("fixtures", "one.txt")] = "one\n",
                [Path.Combine("fixtures", "nested", "two.txt")] = "two\n",
            });

        Assert.True(Directory.Exists(directoryPath));
        Assert.Equal(Path.Combine(projectRoot, "src", "App.cs"), filePath);
        Assert.True(Directory.Exists(TestProjectHelper.ProjectPath(projectRoot, "src")));
        Assert.Equal("class App {}\n// appended\n", TestProjectHelper.ReadTextFile(projectRoot, Path.Combine("src", "App.cs")));
        Assert.Equal(
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("雪\n")).ToArray(),
            File.ReadAllBytes(encodedPath));
        Assert.Equal("one\n", TestProjectHelper.ReadTextFile(projectRoot, Path.Combine("fixtures", "one.txt")));
        Assert.Equal("two\n", TestProjectHelper.ReadTextFile(projectRoot, Path.Combine("fixtures", "nested", "two.txt")));
    }

    [Fact]
    public void ProjectPath_RejectsFixturePathsOutsideProjectRoot()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_fixture_escape");
        var projectRoot = project.Root;

        Assert.Throws<ArgumentException>(() => TestProjectHelper.ProjectPath(projectRoot, "..", "escape.txt"));
        Assert.Throws<ArgumentException>(() => TestProjectHelper.ProjectPath(projectRoot, Path.GetFullPath(Path.Combine(projectRoot, "..", "escape.txt"))));
    }

    [Fact]
    public void CreateTempProjectScope_DeletesProjectOnDispose()
    {
        string projectRoot;
        using (var project = TestProjectHelper.CreateTempProjectScope("cdidx_fixture_scope"))
        {
            projectRoot = project.Root;
            TestProjectHelper.WriteTextFile(projectRoot, "created.txt", "content\n");
            Assert.True(Directory.Exists(projectRoot));
        }

        Assert.False(Directory.Exists(projectRoot));
    }
}
