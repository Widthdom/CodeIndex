using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunStatus_Json_ReportsHotspotFamilyReadinessDegradationRebuild_2959()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hotspots_family_degradation_2959");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: false);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var hotspotDegradation = json.GetProperty("readiness_degradations")
                .EnumerateArray()
                .Single(item => item.GetProperty("field").GetString() == "hotspot_family_ready");
            Assert.Equal("hotspot_family_ready=false", hotspotDegradation.GetProperty("root_cause").GetString());
            Assert.Contains("--rebuild", hotspotDegradation.GetProperty("recommended_action").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Explain_HotspotFamilyReadyRecommendsRebuild()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
            ["--explain", "hotspot_family_ready"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Hotspot family contract (hotspot_family_ready)", stdout);
        Assert.Contains("Remediation:", stdout);
        Assert.Contains("cdidx index <projectPath> --rebuild", stdout);
        Assert.Contains("every indexed row", stdout);
    }
}
