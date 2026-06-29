namespace CodeIndex.Tests;

public class GitTestProjectHelperTests
{
    [Fact]
    public void InitializeGitRepo_DisablesCommitSigningForFixtureCommits()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_git_signing");
        var globalConfig = TestProjectHelper.WriteTextFile(
            projectRoot,
            "global-gitconfig",
            """
            [commit]
                gpgsign = true
            [gpg]
                format = ssh
            [user]
                signingkey = /definitely/missing/signing-key
            """);
        try
        {
            using var env = EnvironmentVariableScope.Capture("GIT_CONFIG_GLOBAL");
            env.Set("GIT_CONFIG_GLOBAL", globalConfig);

            TestProjectHelper.InitializeGitRepo(projectRoot);
            TestProjectHelper.WriteTextFile(projectRoot, "app.cs", "class App {}\n");

            TestProjectHelper.RunGit(projectRoot, "add", "app.cs");
            var commitSigning = TestProjectHelper.RunGit(projectRoot, "config", "--get", "commit.gpgsign").Trim();
            var tagSigning = TestProjectHelper.RunGit(projectRoot, "config", "--get", "tag.gpgsign").Trim();
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "fixture");

            Assert.Equal("false", commitSigning);
            Assert.Equal("false", tagSigning);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
