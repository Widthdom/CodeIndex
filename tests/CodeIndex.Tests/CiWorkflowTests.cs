using System.Text.Json;
using System.Xml.Linq;

namespace CodeIndex.Tests;

public class CiWorkflowTests
{
    [Fact]
    public void DotnetWorkflow_RunsTestsWithRunsettingsBlameRetryAndArtifacts()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("dotnet.yml");
        var normalizedWorkflow = workflow.ReplaceLineEndings("\n");

        Assert.Contains("--settings\", \"tests/CodeIndex.Tests/CodeIndex.Tests.runsettings", workflow);
        Assert.DoesNotContain("--results-directory\", \"./TestResults", workflow);
        Assert.Contains(
            "- name: Select CI lane\n        id: lane",
            normalizedWorkflow);
        Assert.Contains(
            "\"primary_lane=$primaryLaneText\" | Out-File -FilePath $env:GITHUB_OUTPUT",
            workflow);
        Assert.DoesNotContain("collect_coverage", workflow);
        Assert.Contains("key: ${{ runner.os }}-nuget-${{ hashFiles('**/packages.lock.json', 'global.json') }}", workflow, StringComparison.Ordinal);
        Assert.Contains("restore-keys: |\n            ${{ runner.os }}-nuget-", normalizedWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("'**/*.csproj'", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "- name: Audit NuGet package vulnerabilities\n        if: steps.lane.outputs.primary_lane == 'true'",
            normalizedWorkflow);
        Assert.Contains(
            "- name: Verify developer task wrapper\n        if: steps.lane.outputs.primary_lane == 'true'\n        run: make lint",
            normalizedWorkflow);
        Assert.DoesNotContain("- name: Verify formatting", normalizedWorkflow);
        Assert.Contains("\"${{ matrix.os }}\" -eq \"ubuntu-latest\" -and \"${{ matrix.test-framework }}\" -eq \"net8.0\"", workflow);
        Assert.Contains(
            "- name: Build\n        if: steps.lane.outputs.primary_lane != 'true'",
            normalizedWorkflow);
        Assert.Contains(
            "$collectCoverage = \"${{ steps.lane.outputs.primary_lane }}\" -eq \"true\"",
            workflow);
        Assert.Contains("Skipping XPlat Code Coverage outside ubuntu-latest/net8.0", workflow);
        Assert.DoesNotContain("\"--no-build\",\n            \"--no-restore\"", normalizedWorkflow);
        Assert.Contains("--blame-crash", workflow);
        Assert.Contains("--blame-hang", workflow);
        Assert.Contains("--blame-hang-timeout\", \"5m", workflow);
        Assert.Contains("test-output-first.txt", workflow);
        Assert.Contains("[System.Collections.Generic.List[string]]::new()", workflow);
        Assert.Contains("if ($exitCode -ne 0)", workflow);
        Assert.Contains("$logDirectory = Split-Path -Parent $LogPath", workflow);
        Assert.Contains("New-Item -ItemType Directory -Force -Path $logDirectory", workflow);
        Assert.Contains("[System.IO.File]::WriteAllLines($LogPath, [string[]]$capturedOutput)", workflow);
        Assert.DoesNotContain("New-Item -ItemType Directory -Force -Path ./TestResults", workflow);
        Assert.DoesNotContain("Tee-Object", workflow);
        Assert.Contains("id: test", workflow);
        Assert.Contains("\"summarize=true\" | Out-File -FilePath $env:GITHUB_OUTPUT", workflow);
        Assert.Contains("steps.test.outputs.summarize == 'true' || failure()", workflow);
        Assert.Contains(
            "- name: Upload test results\n        if: always() && (steps.test.outputs.summarize == 'true' || failure())",
            normalizedWorkflow);
        Assert.Contains(
            "run: dotnet run --project tools/CodeIndex.TestTelemetry --configuration Release -- summarize",
            workflow);
        Assert.DoesNotContain(
            "tools/CodeIndex.TestTelemetry --configuration Release --no-build",
            workflow);
        Assert.DoesNotContain(
            "if: always()\n        run: dotnet run --project tools/CodeIndex.TestTelemetry",
            normalizedWorkflow);
        Assert.DoesNotContain(
            "- name: Upload test results\n        if: always()\n",
            normalizedWorkflow);
        Assert.Contains("Initial test run hit TestSessionTimeout; skipping flaky retry", workflow);
        Assert.Contains("Rerunning once to classify possible flakiness.", workflow);
        Assert.Contains("flaky-retry.txt", workflow);
        Assert.Contains("TestResults/**/*.trx", workflow);
        Assert.Contains("TestResults/**/*.txt", workflow);
        Assert.Contains("TestResults/**/*.xml", workflow);
        Assert.DoesNotContain("TestResults/**/*Sequence*.xml", workflow);
        Assert.Contains("TestResults/**/*.dmp", workflow);
        Assert.Contains("TestResults/**/*.dump", workflow);
        Assert.Contains("if: always() && steps.lane.outputs.primary_lane == 'true'", workflow);
        Assert.Contains(
            "- name: Publish\n        if: steps.lane.outputs.primary_lane == 'true'",
            normalizedWorkflow);
        Assert.Contains(
            "- name: Upload build artifact\n        if: steps.lane.outputs.primary_lane == 'true'",
            normalizedWorkflow);
        Assert.DoesNotContain("if: matrix.os == 'ubuntu-latest' && matrix.test-framework == 'net8.0'", workflow);
        Assert.DoesNotContain("always() && matrix.os == 'ubuntu-latest' && matrix.test-framework == 'net8.0'", workflow);
        Assert.DoesNotContain("always() && !(matrix.os == 'windows-latest' && matrix.test-framework == 'net9.0')", workflow);
    }

    [Fact]
    public void Runsettings_DefinesSessionTimeoutAndXunitLongRunningDiagnostics()
    {
        var path = RepositoryTestPaths.Combine("tests", "CodeIndex.Tests", "CodeIndex.Tests.runsettings");
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
        var codeowners = RepositoryTestPaths.ReadText(".github", "CODEOWNERS");
        using var globalJson = JsonDocument.Parse(RepositoryTestPaths.ReadText("global.json"));
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
            var workflow = RepositoryTestPaths.ReadWorkflow(workflowName);
            Assert.Contains("8.0.413", workflow);
            Assert.Contains("9.0.301", workflow);
            Assert.DoesNotContain("8.0.x", workflow);
            Assert.DoesNotContain("9.0.x", workflow);
        }

        var mutationWorkflow = RepositoryTestPaths.ReadWorkflow("mutation-testing.yml");
        Assert.Contains("dotnet tool update --global dotnet-stryker --version 4.14.0", mutationWorkflow);
        Assert.Contains("if: steps.mutation-cache.outputs.cache-hit != 'true'", mutationWorkflow);
        Assert.Contains("mutation-stryker-4.14.0", mutationWorkflow);
        Assert.DoesNotContain("dotnet tool install --global dotnet-stryker", mutationWorkflow);
    }

    [Fact]
    public void DotnetWorkflow_UsesSdkCompatibleNuGetAudit()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("dotnet.yml");

        Assert.Contains(
            "dotnet list src/CodeIndex/CodeIndex.csproj package --vulnerable --include-transitive 2>&1",
            workflow);
        Assert.DoesNotContain(
            "dotnet list src/CodeIndex/CodeIndex.csproj package --vulnerable --include-transitive --no-restore",
            workflow);
    }

    [Fact]
    public void TestingGuide_DocumentsSharedStateParallelismInventory()
    {
        var guide = RepositoryTestPaths.ReadText("TESTING_GUIDE.md");

        Assert.Contains("Shared state and parallelism audit", guide);
        Assert.Contains("SQLite pool sensitive", guide);
        Assert.Contains("EnvironmentVariableScope.Capture", guide);
        Assert.Contains("TestConsoleLock.Gate", guide);
        Assert.Contains("TestProjectHelper", guide);
        Assert.Contains("共有状態と並列実行の監査", guide);
    }

}
