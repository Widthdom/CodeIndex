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
        var workflow = RepositoryTestPaths.ReadNormalizedDotnetWorkflow();
        var testScript = RepositoryTestPaths.ReadText(".github", "scripts", "run-dotnet-tests.ps1");

        AssertContainsAll(
            testScript,
            "--settings\", \"tests/CodeIndex.Tests/CodeIndex.Tests.runsettings",
            "--results-directory\", $resultsDirectory");
        AssertContainsAll(
            workflow,
            "include:\n" +
            "          - os: ubuntu-24.04\n" +
            "            test-framework: net8.0\n" +
            "            primary_lane: true\n" +
            "          - os: ubuntu-24.04\n" +
            "            test-framework: net9.0\n" +
            "            primary_lane: false\n" +
            "          - os: windows-2022\n" +
            "            test-framework: net8.0\n" +
            "            primary_lane: false\n" +
            "          - os: macos-14\n" +
            "            test-framework: net8.0\n" +
            "            primary_lane: false",
            "- name: Set up .NET SDKs\n        id: setup-dotnet\n        continue-on-error: true\n        uses: actions/setup-dotnet@9a946fdbd5fb07b82b2f5a4466058b876ab72bb2 # v5.3.0\n        with:\n          dotnet-version: |\n            8.0.413\n            9.0.301",
            "- name: Retry .NET SDK setup\n        if: steps.setup-dotnet.outcome == 'failure'\n        uses: actions/setup-dotnet@9a946fdbd5fb07b82b2f5a4466058b876ab72bb2 # v5.3.0\n        with:\n          dotnet-version: |\n            8.0.413\n            9.0.301",
            "- name: Restore dependencies\n        if: matrix.primary_lane\n        run: dotnet restore CodeIndex.sln --locked-mode",
            "- name: Restore test dependencies\n        if: ${{ !matrix.primary_lane }}\n        run: dotnet restore tests/CodeIndex.Tests/CodeIndex.Tests.csproj -p:RestoreTargetFrameworks=${{ matrix.test-framework }} --locked-mode");
        AssertDoesNotContainAny(
            workflow,
            "collect_coverage",
            "restore-keys:",
            "'**/*.csproj'",
            "function Invoke-TestRun");
        AssertContainsAll(
            workflow,
            "key: ${{ runner.os }}-dotnet-nuget-${{ hashFiles('**/packages.lock.json', 'global.json') }}",
            "primary_lane: true");
        AssertContainsAll(
            workflow,
            "- name: Audit NuGet package vulnerabilities\n        if: matrix.primary_lane",
            "- name: Verify Release test build\n        if: matrix.primary_lane\n        run: dotnet build tests/CodeIndex.Tests/CodeIndex.Tests.csproj --configuration Release --framework ${{ matrix.test-framework }} --no-restore -p:UseSharedCompilation=false",
            "- name: Verify developer task wrapper\n        if: matrix.primary_lane\n        run: make lint",
            "- name: Build\n        if: ${{ !matrix.primary_lane }}",
            "run: |\n          ./.github/scripts/run-dotnet-tests.ps1 `\n            -Framework \"${{ matrix.test-framework }}\" `\n            -CollectCoverage \"${{ matrix.primary_lane }}\"");
        AssertDoesNotContainAny(
            workflow,
            "- name: Verify Release solution build",
            "dotnet build CodeIndex.sln --configuration Release --no-restore",
            "- name: Verify formatting");
        AssertContainsAll(
            testScript,
            "$includeCoverage = $CollectCoverage -eq \"true\"",
            "if ($includeCoverage)",
            "[ValidateSet(\"true\", \"false\")]",
            "Skipping XPlat Code Coverage outside ubuntu-24.04/net8.0",
            "$firstExitCode = Invoke-TestRun -LogPath $firstLogPath -ResultFileName \"test_results_first.trx\" -IncludeCoverage $includeCoverage",
            "Skipping XPlat Code Coverage on the flaky-classification retry.",
            "$retryExitCode = Invoke-TestRun -LogPath $retryLogPath -ResultFileName \"test_results_retry.trx\" -IncludeCoverage $false",
            "\"--no-build\"",
            "\"--no-restore\"",
            "--blame-crash",
            "--blame-hang",
            "--blame-hang-timeout\", \"5m",
            "test-output-first.txt",
            "[string]$ResultFileName",
            "trx;LogFileName=$ResultFileName",
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
            "New-Item -ItemType Directory -Force -Path ./TestResults",
            "Tee-Object",
            "steps.lane.outputs.primary_lane",
            "matrix.test-framework");
        AssertContainsAll(
            workflow,
            "-Framework \"${{ matrix.test-framework }}\"",
            "id: test",
            "- name: Summarize TRX telemetry\n        if: always() && (steps.test.outputs.summarize == 'true' || failure())",
            "run: dotnet run --project tools/CodeIndex.TestTelemetry --configuration Release --no-build --no-restore -- summarize",
            "TestResults/**/*.trx",
            "TestResults/**/*.txt",
            "TestResults/**/*.xml",
            "TestResults/**/*.dmp",
            "TestResults/**/*.dump",
            "if: always() && matrix.primary_lane");
        AssertContainsAll(
            workflow,
            "- name: Upload test results\n        if: always() && (steps.test.outputs.summarize == 'true' || failure())",
            "- name: Publish\n        if: matrix.primary_lane\n        run: dotnet publish src/CodeIndex/CodeIndex.csproj --configuration Release --no-build --no-restore --output publish",
            "- name: Upload build artifact\n        if: matrix.primary_lane");
        AssertDoesNotContainAny(
            workflow,
            "TestResults/**/*Sequence*.xml",
            "if: matrix.os == 'ubuntu-24.04' && matrix.test-framework == 'net8.0'",
            "always() && matrix.os == 'ubuntu-24.04' && matrix.test-framework == 'net8.0'",
            "always() && !(matrix.os == 'windows-2022' && matrix.test-framework == 'net9.0')");
        AssertDoesNotContainAny(
            workflow,
            "- name: Summarize TRX telemetry\n        if: always()\n",
            "- name: Upload test results\n        if: always()\n");
        Assert.Contains("function Invoke-TestRun", testScript);
    }

    [Fact]
    public void WindowsTestHostSetup_IsSharedAcrossDotnetAndReleaseWorkflows()
    {
        var dotnetWorkflow = RepositoryTestPaths.ReadNormalizedDotnetWorkflow();
        var releaseWorkflow = RepositoryTestPaths.ReadNormalizedReleaseWorkflow();
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
    public void BuildAndCodeqlWorkflows_IgnoreLicenseTextHandledByFocusedPolicyWorkflow()
    {
        var dotnetWorkflow = RepositoryTestPaths.ReadNormalizedDotnetWorkflow();
        var codeqlWorkflow = RepositoryTestPaths.ReadWorkflow("codeql.yml").Replace("\r\n", "\n");
        var licensePolicyWorkflow = RepositoryTestPaths.ReadWorkflow("license-policy.yml");
        const string licenseTextIgnoreBlock =
            "paths-ignore:\n" +
            "      - '**.md'\n" +
            "      - 'LICENSE'\n" +
            "      - 'LICENSES/**'";

        Assert.Equal(2, CountOccurrences(dotnetWorkflow, licenseTextIgnoreBlock));
        Assert.Equal(2, CountOccurrences(codeqlWorkflow, licenseTextIgnoreBlock));
        AssertContainsAll(licensePolicyWorkflow, "- 'LICENSE'", "- 'LICENSES/**'");
    }

    [Fact]
    public void TestWorkflows_CancelSupersededPullRequestRunsOnly()
    {
        const string concurrencyPolicy =
            "concurrency:\n" +
            "  group: ${{ github.workflow }}-${{ github.event.pull_request.number || github.run_id }}\n" +
            "  cancel-in-progress: ${{ github.event_name == 'pull_request' }}";

        foreach (var workflowName in new[]
        {
            "changelog-fragments.yml",
            "codeql.yml",
            "dotnet.yml",
            "license-policy.yml",
        })
        {
            var workflow = RepositoryTestPaths.ReadWorkflow(workflowName).Replace("\r\n", "\n");
            Assert.Equal(1, CountOccurrences(workflow, concurrencyPolicy));
        }
    }

    [Fact]
    public void ChangelogWorkflow_CachesAndRestoresLockedToolDependenciesOnce()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("changelog-fragments.yml").Replace("\r\n", "\n");

        AssertContainsAll(
            workflow,
            "cache: true",
            "cache-dependency-path: tools/CodeIndex.Changelog/packages.lock.json",
            "dotnet restore tools/CodeIndex.Changelog/CodeIndex.Changelog.csproj --locked-mode",
            "dotnet run --project tools/CodeIndex.Changelog --no-restore -- check");
    }

    [Fact]
    public void GitHubActionsWorkflows_FollowRunnerArtifactCacheAndContinueOnErrorPolicy()
    {
        var workflows = RepositoryTestPaths.ReadNormalizedWorkflows();
        var allWorkflows = string.Join("\n", workflows.Select(static workflow => workflow.Content));
        var stepBlocks = ReadStepBlocks(workflows);

        AssertContainsAll(allWorkflows, "ubuntu-24.04", "windows-2022", "macos-14");

        foreach (var workflow in workflows)
        {
            AssertTopLevelContentsPermissionStaysReadOnly(workflow.FileName, workflow.Content);
            AssertDoesNotContainAny(
                workflow.Content,
                StringComparison.Ordinal,
                "ubuntu-latest",
                "windows-latest",
                "macos-latest");
        }

        var continueOnErrorBlocks = FindStepBlocks(stepBlocks, "continue-on-error: true").ToArray();
        Assert.Equal(2, continueOnErrorBlocks.Length);
        var sdkSetupBlock = Assert.Single(
            continueOnErrorBlocks,
            block => block.Text.Contains("- name: Set up .NET SDKs", StringComparison.Ordinal));
        Assert.Equal("dotnet.yml", sdkSetupBlock.FileName);
        AssertContainsAll(
            sdkSetupBlock.Text,
            StringComparison.Ordinal,
            "id: setup-dotnet",
            "actions/setup-dotnet@");
        AssertContainsAll(
            allWorkflows,
            "- name: Retry .NET SDK setup\n        if: steps.setup-dotnet.outcome == 'failure'");

        var diagnosticUploadBlock = Assert.Single(
            continueOnErrorBlocks,
            block => block.Text.Contains("- name: Upload diagnostic dumps", StringComparison.Ordinal));
        Assert.Equal("dotnet.yml", diagnosticUploadBlock.FileName);
        AssertContainsAll(
            diagnosticUploadBlock.Text,
            StringComparison.Ordinal,
            "- name: Upload diagnostic dumps",
            "if: failure()",
            "actions/upload-artifact@");

        foreach (var uploadBlock in FindStepBlocks(stepBlocks, "actions/upload-artifact@"))
        {
            AssertContainsAll(uploadBlock.Text, StringComparison.Ordinal, "retention-days:");
        }

        foreach (var downloadBlock in FindStepBlocks(stepBlocks, "actions/download-artifact@"))
        {
            AssertContainsAll(downloadBlock.Text, StringComparison.Ordinal, "pattern:", "path:");
        }

        foreach (var cacheBlock in FindStepBlocks(stepBlocks, "actions/cache@"))
        {
            AssertContainsAll(
                cacheBlock.Text,
                StringComparison.Ordinal,
                "hashFiles('**/packages.lock.json', 'global.json')");
            AssertDoesNotContainAny(cacheBlock.Text, StringComparison.Ordinal, "restore-keys:", "'**/*.csproj'");
        }

        AssertContainsAll(
            GetWorkflow(workflows, "dotnet.yml"),
            StringComparison.Ordinal,
            "key: ${{ runner.os }}-dotnet-nuget-");
        AssertContainsAll(
            GetWorkflow(workflows, "release.yml"),
            StringComparison.Ordinal,
            "key: ${{ runner.os }}-release-nuget-");
        AssertContainsAll(
            GetWorkflow(workflows, "mutation-testing.yml"),
            StringComparison.Ordinal,
            "key: ${{ runner.os }}-mutation-stryker-4.14.0-");
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
        var workflow = RepositoryTestPaths.ReadDotnetWorkflow();

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

    private static void AssertContainsAll(string text, StringComparison comparisonType, params string[] expectedValues)
    {
        foreach (var expected in expectedValues)
            Assert.Contains(expected, text, comparisonType);
    }

    private static void AssertDoesNotContainAny(string text, params string[] excludedValues)
    {
        foreach (var excluded in excludedValues)
            Assert.DoesNotContain(excluded, text);
    }

    private static void AssertDoesNotContainAny(
        string text,
        StringComparison comparisonType,
        params string[] excludedValues)
    {
        foreach (var excluded in excludedValues)
            Assert.DoesNotContain(excluded, text, comparisonType);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static IReadOnlyList<(string FileName, string Text)> ReadStepBlocks(
        IEnumerable<(string FileName, string Content)> workflows)
    {
        var blocks = new List<(string FileName, string Text)>();
        foreach (var workflow in workflows)
        {
            foreach (Match block in StepBlockPattern.Matches(workflow.Content))
                blocks.Add((workflow.FileName, block.Value));
        }

        return blocks;
    }

    private static IEnumerable<(string FileName, string Text)> FindStepBlocks(
        IEnumerable<(string FileName, string Text)> stepBlocks,
        string requiredText)
        => stepBlocks.Where(block => block.Text.Contains(requiredText, StringComparison.Ordinal));

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
