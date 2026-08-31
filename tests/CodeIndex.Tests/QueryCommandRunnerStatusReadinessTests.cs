using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerStatusReadinessTests
{
    [Fact]
    public void RunStatus_LegacyMissingSymbolKindPolicyUsesConservativeReadableFallback_Issue5224()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_symbol_policy_legacy_5224");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.MarkIndexComplete();
            }
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    DELETE FROM codeindex_meta WHERE key = '{DbContext.SymbolKindFilterMetaKey}';
                    ALTER TABLE files DROP COLUMN {DbContext.SymbolsDroppedByKindFilterColumn};
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only", "--json"],
                JsonOptions));

            Assert.True(
                exitCode == CommandExitCodes.Success,
                $"exit={exitCode}\nstdout={stdout}\nstderr={stderr}");
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            Assert.False(json.GetProperty("symbol_kind_filter_provenance_available").GetBoolean());
            Assert.False(json.TryGetProperty("symbol_kind_filter", out _));
            Assert.False(json.TryGetProperty("symbols_dropped_by_kind_filter", out _));
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.Contains(
                json.GetProperty("index_incomplete_reasons").EnumerateArray(),
                reason => reason.GetString() == DbReader.SymbolKindFilterProvenanceUnavailableReason);

            var (humanExitCode, humanStdout, _) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only"],
                JsonOptions));
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("Kind policy", humanStdout, StringComparison.Ordinal);
            Assert.Contains("unavailable (legacy generation)", humanStdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_Json_ReportsHotspotFamilyReadinessDegradationRebuild_2959()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_hotspots_family_degradation_2959");
        try
        {
            var dbPath = CreateHotspotFamilyFixtureDb(projectRoot, markHotspotFamilyReady: false);
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                JsonOptions));

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
            JsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("Hotspot family contract (hotspot_family_ready)", stdout);
        Assert.Contains("Remediation:", stdout);
        Assert.Contains("cdidx index <projectPath> --rebuild", stdout);
        Assert.Contains("every indexed row", stdout);
    }
}
