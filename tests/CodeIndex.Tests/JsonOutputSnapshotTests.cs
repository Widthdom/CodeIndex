using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Golden-file regression fixtures for the CLI `--json` output contracts (issue #1548).
/// Each test runs one CLI command against a small deterministic in-memory fixture, normalizes
/// the volatile fields, and compares the result with a checked-in golden file under
/// <c>tests/CodeIndex.Tests/golden/</c>. Renames, removals, reordered arrays, or new keys
/// will fail the snapshot so the change is forced to land alongside an intentional golden
/// update.
///
/// To regenerate goldens after an intentional shape change, set <c>UPDATE_SNAPSHOTS=1</c>
/// and re-run only these tests, then review the diff before committing.
/// </summary>
[Collection("SQLite pool sensitive")]
public class JsonOutputSnapshotTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string LibSource = @"namespace Demo;

public static class Lib
{
    public static int Add(int a, int b) => a + b;
}
";

    private const string ReferencesSource = @"namespace Demo;

public class TargetType { }

public class Probe
{
    bool Match(object value) => value is TargetType;
    void Use() { _ = typeof(TargetType); }
}
";

    private const string ImpactServiceSource = @"public class FolderDiffService
{
    public void ExecuteFolderDiffAsync() { }
}
";

    private const string ImpactCallerSource = @"public class App
{
    public void Boot(FolderDiffService service)
    {
        service.ExecuteFolderDiffAsync();
    }
}
";

    [Fact]
    public void RunStatus_JsonOutput_MatchesGolden()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_snapshot_status");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Lib.cs", "csharp", LibSource);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            JsonOutputSnapshotHelper.AssertMatches(
                "status.json",
                stdout,
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunSearch_JsonOutput_MatchesGolden()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_snapshot_search");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Lib.cs", "csharp", LibSource);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Add", "--db", dbPath, "--json", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            JsonOutputSnapshotHelper.AssertMatches(
                "search.json",
                stdout,
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunReferences_JsonOutput_MatchesGolden()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_snapshot_references");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Refs.cs", "csharp", ReferencesSource);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
                ["TargetType", "--db", dbPath, "--json", "--lang", "csharp", "--exact-name", "--limit", "5"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            JsonOutputSnapshotHelper.AssertMatches(
                "references.json",
                stdout,
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunImpact_JsonOutput_MatchesGolden()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_snapshot_impact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/FolderDiffService.cs", "csharp", ImpactServiceSource);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", ImpactCallerSource);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["FolderDiffService", "--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            JsonOutputSnapshotHelper.AssertMatches(
                "impact.json",
                stdout,
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RunExcerpt_JsonOutput_MatchesGolden()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_snapshot_excerpt");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Lib.cs", "csharp", LibSource);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunExcerpt(
                ["src/Lib.cs", "--db", dbPath, "--json", "--start", "1", "--end", "6"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);

            JsonOutputSnapshotHelper.AssertMatches(
                "excerpt.json",
                stdout,
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void MarkGraphAndFoldReady(string dbPath)
    {
        using var db = new DbContext(dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
    }

    private static IReadOnlyList<(string Original, string Placeholder)> BuildPathReplacements(string projectRoot)
    {
        var canonical = Path.GetFullPath(projectRoot);
        var replacements = new List<(string, string)>
        {
            (canonical, "<PROJECT_ROOT>"),
        };
        if (!string.Equals(projectRoot, canonical, StringComparison.Ordinal))
            replacements.Add((projectRoot, "<PROJECT_ROOT>"));
        return replacements;
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                return (action(), stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
