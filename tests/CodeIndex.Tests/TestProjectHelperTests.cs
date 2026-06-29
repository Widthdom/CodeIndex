namespace CodeIndex.Tests;

public class TestProjectHelperTests
{
    [Fact]
    public void WriteTextFile_CreatesParentDirectoriesAndReadTextFileReadsFixtureContent()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fixture_write");
        try
        {
            var directoryPath = TestProjectHelper.CreateDirectory(projectRoot, "generated", "nested");
            var filePath = TestProjectHelper.WriteTextFile(projectRoot, Path.Combine("src", "App.cs"), "class App {}\n");
            TestProjectHelper.AppendTextFile(projectRoot, Path.Combine("src", "App.cs"), "// appended\n");

            Assert.True(Directory.Exists(directoryPath));
            Assert.Equal(Path.Combine(projectRoot, "src", "App.cs"), filePath);
            Assert.True(Directory.Exists(TestProjectHelper.ProjectPath(projectRoot, "src")));
            Assert.Equal("class App {}\n// appended\n", TestProjectHelper.ReadTextFile(projectRoot, Path.Combine("src", "App.cs")));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ProjectPath_RejectsFixturePathsOutsideProjectRoot()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fixture_escape");
        try
        {
            Assert.Throws<ArgumentException>(() => TestProjectHelper.ProjectPath(projectRoot, "..", "escape.txt"));
            Assert.Throws<ArgumentException>(() => TestProjectHelper.ProjectPath(projectRoot, Path.GetFullPath(Path.Combine(projectRoot, "..", "escape.txt"))));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
