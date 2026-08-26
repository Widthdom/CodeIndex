using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

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
[Collection("Console sensitive")]
public class JsonOutputSnapshotTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
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
                NormalizeStatusSnapshotConnectionMode(stdout),
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string NormalizeStatusSnapshotConnectionMode(string stdout)
    {
        var root = JsonNode.Parse(stdout)?.AsObject()
            ?? throw new InvalidOperationException("Status snapshot output was not a JSON object.");
        if (root["sqlite_connection_policy"] is JsonObject policy
            && string.Equals(
                policy["active_mode"]?.GetValue<string>(),
                SqliteConnectionPolicy.ImmutableReadOnlyUriModeName,
                StringComparison.Ordinal))
        {
            // A closed WAL fixture may retain WAL header bytes on Windows after its sidecars are
            // removed, so query-only status legitimately uses an immutable private snapshot there.
            // Keep this golden focused on the JSON contract; dedicated Issue4557 tests pin both
            // read-only policy variants and their artifact-preservation behavior.
            policy["active_mode"] = SqliteConnectionPolicy.ReadOnlyModeName;
            policy["open_mode"] = SqliteConnectionPolicy.ReadOnlyModeName;
            policy["immutable_uri"] = false;
            if (root["db_pragma_settings"] is JsonObject pragmas)
                pragmas["journal_mode"] = "wal";
        }

        if (root["git_executable"] is JsonObject gitExecutable)
        {
            // Host Git availability and ambient CDIDX_GIT_EXECUTABLE are intentionally outside this
            // golden contract. Normalize the complete selection state; dedicated Issue4599 tests pin
            // accepted/rejected sources, owner/mode/ancestor trust, and execution-probe diagnostics.
            gitExecutable.Clear();
            gitExecutable["source"] = "normalized";
            gitExecutable["accepted"] = true;
            gitExecutable["reason"] = "accepted";
            gitExecutable["path"] = "<GIT_EXECUTABLE>";
            gitExecutable["executable"] = true;
        }

        if (root["github_cli_executable"] is JsonObject githubCliExecutable)
        {
            // Host GitHub CLI availability and ambient CDIDX_GH_EXECUTABLE are intentionally
            // outside this golden contract. Dedicated Issue5184 tests pin resolver diagnostics.
            githubCliExecutable.Clear();
            githubCliExecutable["source"] = "normalized";
            githubCliExecutable["accepted"] = true;
            githubCliExecutable["reason"] = "accepted";
            githubCliExecutable["path"] = "<GH_EXECUTABLE>";
            githubCliExecutable["executable"] = true;
        }

        return root.ToJsonString();
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
        }
    }

    [Fact]
    public void RunSuggestionsCompact_JsonOutput_MatchesGolden_Issue5061()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_snapshot_suggestions_compact");
        try
        {
            var cdidxDir = Path.Combine(projectRoot, ".cdidx");
            Directory.CreateDirectory(cdidxDir);
            var dbPath = Path.Combine(cdidxDir, "codeindex.db");
            var titlePrefix = new string('a', 118);
            var store = new SuggestionStore(cdidxDir);
            Assert.True(store.TryAdd(new SuggestionRecord
            {
                Id = new string('1', 64),
                Category = "output_format",
                Language = "csharp",
                Description = "Snapshot query description",
                SampledTitle = titlePrefix + "😀suffix",
                EvidencePaths = ["src/Snapshot.cs"],
                CreatedAt = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
            }));

            var (exitCode, stdout, stderr) = CaptureConsole(() => SuggestionsCommandRunner.Run(
                ["list", "--db", dbPath, "--query", "snapshot query", "--compact"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var item = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            var compactTitle = item.GetProperty("title").GetString()
                ?? throw new InvalidOperationException("Compact suggestion title was null.");
            Assert.Equal(titlePrefix + "...", compactTitle);
            Assert.DoesNotContain('\uFFFD', compactTitle);

            JsonOutputSnapshotHelper.AssertMatches(
                "suggestions-compact.json",
                stdout,
                BuildPathReplacements(projectRoot));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void MarkGraphAndFoldReady(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
        writer.MarkIssuesReady();
    }

    private static IReadOnlyList<(string Original, string Placeholder)> BuildPathReplacements(string projectRoot)
    {
        var canonical = Path.GetFullPath(projectRoot);
        var canonicalDbPath = Path.Combine(canonical, ".cdidx", "codeindex.db");
        var replacements = new List<(string, string)>
        {
            (canonicalDbPath, "<PROJECT_ROOT>/.cdidx/codeindex.db"),
            (canonical, "<PROJECT_ROOT>"),
        };
        var jsonEscapedCanonicalDbPath = canonicalDbPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (!string.Equals(jsonEscapedCanonicalDbPath, canonicalDbPath, StringComparison.Ordinal))
            replacements.Add((jsonEscapedCanonicalDbPath, "<PROJECT_ROOT>/.cdidx/codeindex.db"));
        var jsonEscapedCanonical = canonical.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (!string.Equals(jsonEscapedCanonical, canonical, StringComparison.Ordinal))
            replacements.Add((jsonEscapedCanonical, "<PROJECT_ROOT>"));
        if (!string.Equals(projectRoot, canonical, StringComparison.Ordinal))
            replacements.Add((projectRoot, "<PROJECT_ROOT>"));
        var jsonEscapedProjectRoot = projectRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        if (!string.Equals(jsonEscapedProjectRoot, projectRoot, StringComparison.Ordinal))
            replacements.Add((jsonEscapedProjectRoot, "<PROJECT_ROOT>"));
        return replacements;
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        lock (TestConsoleLock.Gate)
            return ConsoleCapture.Capture(action);
    }
}
