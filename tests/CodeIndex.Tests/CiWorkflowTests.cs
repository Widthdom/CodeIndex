using System.Text.Json;
using System.Xml.Linq;

namespace CodeIndex.Tests;

public class CiWorkflowTests
{
    [Fact]
    public void DotnetWorkflow_RunsTestsWithRunsettingsBlameRetryAndArtifacts()
    {
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "dotnet.yml"));

        Assert.Contains("--settings\", \"tests/CodeIndex.Tests/CodeIndex.Tests.runsettings", workflow);
        Assert.Contains("Skipping XPlat Code Coverage for windows-latest/net9.0", workflow);
        Assert.Contains("--blame-crash", workflow);
        Assert.Contains("--blame-hang", workflow);
        Assert.Contains("--blame-hang-timeout\", \"5m", workflow);
        Assert.Contains("test-output-first.txt", workflow);
        Assert.Contains("Initial test run hit TestSessionTimeout; skipping flaky retry", workflow);
        Assert.Contains("Rerunning once to classify possible flakiness.", workflow);
        Assert.Contains("flaky-retry.txt", workflow);
        Assert.Contains("TestResults/**/*.trx", workflow);
        Assert.Contains("TestResults/**/*.txt", workflow);
        Assert.Contains("TestResults/**/*Sequence*.xml", workflow);
        Assert.Contains("TestResults/**/*.dmp", workflow);
        Assert.Contains("TestResults/**/*.dump", workflow);
        Assert.Contains("always() && !(matrix.os == 'windows-latest' && matrix.test-framework == 'net9.0')", workflow);
    }

    [Fact]
    public void Runsettings_DefinesSessionTimeoutAndXunitLongRunningDiagnostics()
    {
        var path = Path.Combine(GetRepositoryRoot(), "tests", "CodeIndex.Tests", "CodeIndex.Tests.runsettings");
        var document = XDocument.Load(path);

        Assert.Equal(
            "2700000",
            document.Root?.Element("RunConfiguration")?.Element("TestSessionTimeout")?.Value);
        Assert.Equal(
            "60",
            document.Root?.Element("xUnit")?.Element("LongRunningTestSeconds")?.Value);
        Assert.Equal(
            "./TestResults",
            document.Root?.Element("RunConfiguration")?.Element("ResultsDirectory")?.Value);
    }

    [Fact]
    public void DotnetSdkAndMutationToolVersions_ArePinned()
    {
        var root = GetRepositoryRoot();
        var codeowners = File.ReadAllText(Path.Combine(root, ".github", "CODEOWNERS"));
        using var globalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        var sdk = globalJson.RootElement.GetProperty("sdk");

        Assert.Equal("9.0.301", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.Contains("/global.json @Widthdom", codeowners);

        foreach (var workflowName in new[]
        {
            "changelog-fragments.yml",
            "codeql.yml",
            "dotnet.yml",
            "mutation-testing.yml",
            "release.yml",
        })
        {
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", workflowName));
            Assert.Contains("8.0.413", workflow);
            Assert.Contains("9.0.301", workflow);
            Assert.DoesNotContain("8.0.x", workflow);
            Assert.DoesNotContain("9.0.x", workflow);
        }

        var mutationWorkflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "mutation-testing.yml"));
        Assert.Contains("dotnet tool install --global dotnet-stryker --version 4.14.0", mutationWorkflow);
    }

    [Fact]
    public void TestingGuide_DocumentsSharedStateParallelismInventory()
    {
        var guide = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "TESTING_GUIDE.md"));

        Assert.Contains("Shared state and parallelism audit", guide);
        Assert.Contains("SQLite pool sensitive", guide);
        Assert.Contains("EnvironmentVariableScope.Capture", guide);
        Assert.Contains("TestConsoleLock.Gate", guide);
        Assert.Contains("TestProjectHelper", guide);
        Assert.Contains("共有状態と並列実行の監査", guide);
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }
}
