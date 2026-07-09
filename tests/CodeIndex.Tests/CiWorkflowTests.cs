using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeIndex.Tests;

public class CiWorkflowTests
{
    private static readonly Regex StepBlockPattern = new(
        @"(?ms)^      - .*?(?=^      - |\z)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void DotnetWorkflow_RunsTestsWithRunsettingsBlameRetryAndArtifacts()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("dotnet.yml");
        var normalizedWorkflow = workflow.ReplaceLineEndings("\n");
        var testScript = RepositoryTestPaths.ReadText(".github", "scripts", "run-dotnet-tests.ps1");
        var normalizedTestScript = testScript.ReplaceLineEndings("\n");

        AssertContainsAll(
            testScript,
            "--settings\", \"tests/CodeIndex.Tests/CodeIndex.Tests.runsettings");
        AssertDoesNotContainAny(
            testScript,
            "--results-directory\", \"./TestResults");
        AssertContainsAll(
            normalizedWorkflow,
            "- name: Select CI lane\n        id: lane");
        AssertContainsAll(
            workflow,
            "\"primary_lane=$primaryLaneText\" | Out-File -FilePath $env:GITHUB_OUTPUT");
        Assert.True(
            normalizedWorkflow.IndexOf("- name: Select CI lane\n        id: lane", StringComparison.Ordinal)
            < normalizedWorkflow.IndexOf("- name: Set up .NET SDKs", StringComparison.Ordinal));
        AssertContainsAll(
            normalizedWorkflow,
            "- name: Set up .NET SDKs\n        uses: actions/setup-dotnet@9a946fdbd5fb07b82b2f5a4466058b876ab72bb2 # v5.3.0\n        with:\n          dotnet-version: |\n            8.0.413\n            9.0.301",
            "- name: Restore dependencies\n        if: steps.lane.outputs.primary_lane == 'true'\n        run: dotnet restore CodeIndex.sln --locked-mode",
            "- name: Restore test dependencies\n        if: steps.lane.outputs.primary_lane != 'true'\n        run: dotnet restore tests/CodeIndex.Tests/CodeIndex.Tests.csproj -p:RestoreTargetFrameworks=${{ matrix.test-framework }} --locked-mode");
        Assert.True(
            normalizedWorkflow.IndexOf("- name: Select CI lane\n        id: lane", StringComparison.Ordinal)
            < normalizedWorkflow.IndexOf("- name: Restore dependencies", StringComparison.Ordinal));
        AssertDoesNotContainAny(
            workflow,
            "collect_coverage",
            "restore-keys:",
            "'**/*.csproj'",
            "function Invoke-TestRun");
        AssertContainsAll(
            workflow,
            "key: ${{ runner.os }}-dotnet-nuget-${{ hashFiles('**/packages.lock.json', 'global.json') }}",
            "\"${{ matrix.os }}\" -eq \"ubuntu-24.04\" -and \"${{ matrix.test-framework }}\" -eq \"net8.0\"");
        AssertContainsAll(
            normalizedWorkflow,
            "exclude:\n          - os: windows-2022\n            test-framework: net9.0\n          - os: macos-14\n            test-framework: net9.0",
            "- name: Audit NuGet package vulnerabilities\n        if: steps.lane.outputs.primary_lane == 'true'",
            "- name: Verify Release test build\n        if: steps.lane.outputs.primary_lane == 'true'\n        run: dotnet build tests/CodeIndex.Tests/CodeIndex.Tests.csproj --configuration Release --framework ${{ matrix.test-framework }} --no-restore -p:UseSharedCompilation=false",
            "- name: Verify developer task wrapper\n        if: steps.lane.outputs.primary_lane == 'true'\n        run: make lint",
            "- name: Build\n        if: steps.lane.outputs.primary_lane != 'true'",
            "run: |\n          ./.github/scripts/run-dotnet-tests.ps1 `\n            -Framework \"${{ matrix.test-framework }}\" `\n            -CollectCoverage \"${{ steps.lane.outputs.primary_lane }}\"");
        AssertDoesNotContainAny(
            normalizedWorkflow,
            "- name: Verify Release solution build",
            "dotnet build CodeIndex.sln --configuration Release --no-restore",
            "- name: Verify formatting");
        AssertContainsAll(
            testScript,
            "$collectCoverage = $CollectCoverage -eq \"true\"",
            "[ValidateSet(\"true\", \"false\")]",
            "Skipping XPlat Code Coverage outside ubuntu-24.04/net8.0",
            "--blame-crash",
            "--blame-hang",
            "--blame-hang-timeout\", \"5m",
            "test-output-first.txt",
            "$resultsDirectory = \"./TestResults\"",
            "Join-Path $resultsDirectory \"test-output-first.txt\"",
            "Join-Path $resultsDirectory \"test-output-retry.txt\"",
            "[System.Collections.Generic.List[string]]::new()",
            "if ($exitCode -ne 0)",
            "$logDirectory = Split-Path -Parent $LogPath",
            "New-Item -ItemType Directory -Force -Path $logDirectory",
            "[System.IO.File]::WriteAllLines($LogPath, [string[]]$capturedOutput)",
            "Write-StepOutput -Name \"summarize\" -Value \"true\"",
            "$env:GITHUB_OUTPUT",
            "Initial test run hit TestSessionTimeout; skipping flaky retry",
            "Rerunning once to classify possible flakiness.",
            "flaky-retry.txt");
        AssertDoesNotContainAny(
            testScript,
            "\"--no-restore\"",
            "New-Item -ItemType Directory -Force -Path ./TestResults",
            "Tee-Object",
            "steps.lane.outputs.primary_lane",
            "matrix.test-framework");
        AssertContainsAll(
            workflow,
            "-Framework \"${{ matrix.test-framework }}\"",
            "id: test",
            "steps.test.outputs.summarize == 'true' || failure()",
            "run: dotnet run --project tools/CodeIndex.TestTelemetry --configuration Release -- summarize",
            "TestResults/**/*.trx",
            "TestResults/**/*.txt",
            "TestResults/**/*.xml",
            "TestResults/**/*.dmp",
            "TestResults/**/*.dump",
            "if: always() && steps.lane.outputs.primary_lane == 'true'");
        AssertContainsAll(
            normalizedWorkflow,
            "- name: Upload test results\n        if: always() && (steps.test.outputs.summarize == 'true' || failure())",
            "- name: Publish\n        if: steps.lane.outputs.primary_lane == 'true'",
            "- name: Upload build artifact\n        if: steps.lane.outputs.primary_lane == 'true'");
        AssertDoesNotContainAny(
            workflow,
            "tools/CodeIndex.TestTelemetry --configuration Release --no-build",
            "TestResults/**/*Sequence*.xml",
            "if: matrix.os == 'ubuntu-24.04' && matrix.test-framework == 'net8.0'",
            "always() && matrix.os == 'ubuntu-24.04' && matrix.test-framework == 'net8.0'",
            "always() && !(matrix.os == 'windows-2022' && matrix.test-framework == 'net9.0')");
        AssertDoesNotContainAny(
            normalizedWorkflow,
            "if: always()\n        run: dotnet run --project tools/CodeIndex.TestTelemetry",
            "- name: Upload test results\n        if: always()\n");
        Assert.Contains("function Invoke-TestRun", normalizedTestScript);
    }

    [Fact]
    public void WindowsTestHostSetup_IsSharedAcrossDotnetAndReleaseWorkflows()
    {
        var dotnetWorkflow = RepositoryTestPaths.ReadWorkflow("dotnet.yml").ReplaceLineEndings("\n");
        var releaseWorkflow = RepositoryTestPaths.ReadWorkflow("release.yml").ReplaceLineEndings("\n");
        var setupScript = RepositoryTestPaths.ReadText(".github", "scripts", "configure-windows-test-host.ps1");
        const string expectedStep =
            "- name: Configure Windows test host\n" +
            "        if: runner.os == 'Windows'\n" +
            "        shell: pwsh\n" +
            "        run: ./.github/scripts/configure-windows-test-host.ps1 -Workspace \"${{ github.workspace }}\"";

        AssertContainsAll(dotnetWorkflow, expectedStep);
        AssertContainsAll(releaseWorkflow, expectedStep);
        AssertDoesNotContainAny(dotnetWorkflow, "Add-MpPreference", "Get-MpPreference");
        AssertDoesNotContainAny(releaseWorkflow, "Add-MpPreference", "Get-MpPreference");
        AssertContainsAll(
            setupScript,
            "\"TMP=$tempRoot\"",
            "\"TEMP=$tempRoot\"",
            "Add-MpPreference -ExclusionPath $entry.Path -ErrorAction Stop",
            "Get-MpPreference",
            "Windows Defender exclusion audit:",
            "GitHub-hosted runner temp root used by actions and pinned TMP/TEMP.");
    }

    [Fact]
    public void GitHubActionsWorkflows_FollowRunnerArtifactCacheAndContinueOnErrorPolicy()
    {
        var workflows = ReadWorkflowFiles();
        var allWorkflows = string.Join("\n", workflows.Select(static workflow => workflow.Content));

        Assert.Contains("ubuntu-24.04", allWorkflows);
        Assert.Contains("windows-2022", allWorkflows);
        Assert.Contains("macos-14", allWorkflows);

        foreach (var workflow in workflows)
        {
            AssertTopLevelContentsPermissionStaysReadOnly(workflow.FileName, workflow.Content);
            Assert.DoesNotContain("ubuntu-latest", workflow.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("windows-latest", workflow.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("macos-latest", workflow.Content, StringComparison.Ordinal);
        }

        var continueOnErrorBlocks = FindStepBlocks(workflows, "continue-on-error: true").ToArray();
        var continueOnErrorBlock = Assert.Single(continueOnErrorBlocks);
        Assert.Equal("dotnet.yml", continueOnErrorBlock.FileName);
        Assert.Contains("- name: Upload diagnostic dumps", continueOnErrorBlock.Text, StringComparison.Ordinal);
        Assert.Contains("if: failure()", continueOnErrorBlock.Text, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@", continueOnErrorBlock.Text, StringComparison.Ordinal);

        foreach (var uploadBlock in FindStepBlocks(workflows, "actions/upload-artifact@"))
        {
            Assert.Contains("retention-days:", uploadBlock.Text, StringComparison.Ordinal);
        }

        foreach (var downloadBlock in FindStepBlocks(workflows, "actions/download-artifact@"))
        {
            Assert.Contains("pattern:", downloadBlock.Text, StringComparison.Ordinal);
            Assert.Contains("path:", downloadBlock.Text, StringComparison.Ordinal);
        }

        foreach (var cacheBlock in FindStepBlocks(workflows, "actions/cache@"))
        {
            Assert.Contains("hashFiles('**/packages.lock.json', 'global.json')", cacheBlock.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("restore-keys:", cacheBlock.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("'**/*.csproj'", cacheBlock.Text, StringComparison.Ordinal);
        }

        Assert.Contains("key: ${{ runner.os }}-dotnet-nuget-", GetWorkflow(workflows, "dotnet.yml"), StringComparison.Ordinal);
        Assert.Contains("key: ${{ runner.os }}-release-nuget-", GetWorkflow(workflows, "release.yml"), StringComparison.Ordinal);
        Assert.Contains("key: ${{ runner.os }}-mutation-stryker-4.14.0-", GetWorkflow(workflows, "mutation-testing.yml"), StringComparison.Ordinal);
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
            AssertContainsAll(workflow, "8.0.413", "9.0.301");
            AssertDoesNotContainAny(workflow, "8.0.x", "9.0.x");
        }

        var mutationWorkflow = RepositoryTestPaths.ReadWorkflow("mutation-testing.yml");
        AssertContainsAll(
            mutationWorkflow,
            "dotnet tool update --global dotnet-stryker --version 4.14.0",
            "if: steps.mutation-cache.outputs.cache-hit != 'true'",
            "mutation-stryker-4.14.0");
        AssertDoesNotContainAny(mutationWorkflow, "dotnet tool install --global dotnet-stryker");
    }

    [Fact]
    public void DotnetWorkflow_UsesSdkCompatibleNuGetAudit()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("dotnet.yml");

        AssertContainsAll(
            workflow,
            "dotnet list src/CodeIndex/CodeIndex.csproj package --vulnerable --include-transitive 2>&1");
        AssertDoesNotContainAny(
            workflow,
            "dotnet list src/CodeIndex/CodeIndex.csproj package --vulnerable --include-transitive --no-restore");
    }

    [Fact]
    public void TestingGuide_DocumentsSharedStateParallelismInventory()
    {
        var guide = RepositoryTestPaths.ReadText("TESTING_GUIDE.md");

        AssertContainsAll(
            guide,
            "Shared state and parallelism audit",
            "SQLite pool sensitive",
            "EnvironmentVariableScope.Capture",
            "TestConsoleLock.Gate",
            "TestProjectHelper",
            ".github/scripts/run-dotnet-tests.ps1",
            ".github/scripts/configure-windows-test-host.ps1",
            "共有状態と並列実行の監査");
    }

    private static void AssertContainsAll(string text, params string[] expectedValues)
    {
        foreach (var expected in expectedValues)
            Assert.Contains(expected, text);
    }

    private static void AssertDoesNotContainAny(string text, params string[] excludedValues)
    {
        foreach (var excluded in excludedValues)
            Assert.DoesNotContain(excluded, text);
    }

    private static IReadOnlyList<(string FileName, string Content)> ReadWorkflowFiles()
    {
        var workflowsDirectory = RepositoryTestPaths.Combine(".github", "workflows");
        return Directory
            .EnumerateFiles(workflowsDirectory, "*.yml")
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(static path => (Path.GetFileName(path), File.ReadAllText(path).ReplaceLineEndings("\n")))
            .ToArray();
    }

    private static IEnumerable<(string FileName, string Text)> FindStepBlocks(
        IEnumerable<(string FileName, string Content)> workflows,
        string requiredText)
    {
        foreach (var workflow in workflows)
        {
            foreach (Match block in StepBlockPattern.Matches(workflow.Content))
            {
                if (block.Value.Contains(requiredText, StringComparison.Ordinal))
                    yield return (workflow.FileName, block.Value);
            }
        }
    }

    private static void AssertTopLevelContentsPermissionStaysReadOnly(string fileName, string workflow)
    {
        var permissionsIndex = workflow.IndexOf("\npermissions:\n", StringComparison.Ordinal);
        var jobsIndex = workflow.IndexOf("\njobs:\n", StringComparison.Ordinal);

        Assert.True(
            permissionsIndex >= 0 && jobsIndex >= 0 && permissionsIndex < jobsIndex,
            $"{fileName} must declare top-level permissions before jobs so contents stays read-only by default.");
        Assert.Contains("\n  contents: read\n", workflow[..jobsIndex], StringComparison.Ordinal);
    }

    private static string GetWorkflow(IReadOnlyList<(string FileName, string Content)> workflows, string fileName)
        => workflows.Single(workflow => string.Equals(workflow.FileName, fileName, StringComparison.Ordinal)).Content;
}
