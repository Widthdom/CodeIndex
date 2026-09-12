using System.Globalization;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_ChangedBetween_NarrowingPreservesVerifiedHead_Issue5347()
    {
        var root = CreateExpansionProject5347();
        try
        {
            RunGit(root, "init");
            RunGit(root, "add", ".");
            RunGit(root, "commit", "-m", "initial contracts");
            Assert.Equal(0, RunAndCaptureJson([root, "--json"]).ExitCode);
            File.WriteAllText(Path.Combine(root, "Worker.cs"), WorkerSource5347(3));
            RunGit(root, "add", "Worker.cs");
            RunGit(root, "commit", "-m", "prime contract proof with verified update");
            Assert.Equal(0, RunAndCaptureJson([root, "--commits", "HEAD", "--json"]).ExitCode);
            RunGit(root, "branch", "before-independent");
            File.WriteAllText(Path.Combine(root, "Worker.cs"), WorkerSource5347(2));
            RunGit(root, "add", "Worker.cs");
            RunGit(root, "commit", "-m", "independent method edit");
            var (exitCode, json) = RunAndCaptureJson(
                [root, "--changed-between", "before-independent", "HEAD", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            AssertExpansion5347(json, "narrowed", "contract_inputs_unchanged", 1, 5, 1);
            var (statusCode, status) = RunStatusAndCaptureJson(
                ["--db", Path.Combine(root, ".cdidx", "codeindex.db"), "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusCode);
            Assert.True(status.GetProperty("index_matches_workspace").GetBoolean());
            Assert.True(status.GetProperty("reference_graph_complete").GetBoolean());
            Assert.Equal(GitHelper.TryGetHeadCommit(root), status.GetProperty("workspace_verified_head_sha").GetString());
            AssertFullParity5347(root);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Run_Update_GeneratedPolicyAndFilteredEvidencePreventNarrowing_Issue5347()
    {
        var root = CreateExpansionProject5347();
        using var environment = EnvironmentVariableScope.Capture(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        try
        {
            Assert.Equal(0, RunAndCaptureJson([root, "--json"]).ExitCode);
            Update5347(root, "Worker.cs");
            environment.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, "Money.cs");
            var generated = Update5347(root, "Worker.cs");
            AssertExpansion5347(generated, "expanded", "contract_inputs_changed", 1, 5, 5);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(root));
            AssertFullParity5347(root);

            var (initialCode, _) = RunAndCaptureJson(
                [root, "--exclude-symbol-kind", "field", "--allow-partial", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialCode);
            var (filteredCode, filtered) = RunAndCaptureJson(
                [root, "--files", "Worker.cs", "--exclude-symbol-kind", "field", "--allow-partial", "--json"]);
            Assert.Equal(CommandExitCodes.Success, filteredCode);
            Assert.False(filtered.GetProperty("index_complete").GetBoolean());
            AssertExpansion5347(filtered, "expanded", "safety_checks_required", 1, 5, 5);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Run_Update_ContractIndependentEditMatchesFullIndex_Issue5347()
    {
        var projectRoot = CreateExpansionProject5347();
        try
        {
            Assert.Equal(0, RunAndCaptureJson([projectRoot, "--json"]).ExitCode);
            var first = Update5347(projectRoot, "Worker.cs");
            AssertExpansion5347(first, "expanded", "baseline_unavailable", 1, 5, 5);
            AssertExpansionPersisted5347(projectRoot, first);

            File.WriteAllText(Path.Combine(projectRoot, "Worker.cs"), WorkerSource5347(2));
            var independent = Update5347(projectRoot, "Worker.cs");
            AssertExpansion5347(independent, "narrowed", "contract_inputs_unchanged", 1, 5, 1);
            Assert.Equal(1, independent.GetProperty("summary").GetProperty("updated").GetInt32());
            AssertExpansionPersisted5347(projectRoot, independent);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            AssertFullParity5347(projectRoot);

            var noCSharp = Update5347(projectRoot, "notes.md");
            AssertExpansion5347(noCSharp, "not_expanded", "no_csharp_targets", 1, 1, 1);
            AssertExpansionPersisted5347(projectRoot, noCSharp);
        }
        finally { DeleteDirectory(projectRoot); }
    }

    [Fact]
    public void Run_Update_ContractChangesRenamesRemovalsAndConfigurationStayConservative_Issue5347()
    {
        var projectRoot = CreateExpansionProject5347();
        try
        {
            Assert.Equal(0, RunAndCaptureJson([projectRoot, "--json"]).ExitCode);
            Update5347(projectRoot, "Worker.cs");

            File.WriteAllText(Path.Combine(projectRoot, "Settings.cs"), "public class Settings { public const int Other = 7; }\n");
            var members = Update5347(projectRoot, "Settings.cs");
            AssertExpansion5347(members, "expanded", "contract_inputs_changed", 1, 5, 5);
            AssertFullParity5347(projectRoot);

            Update5347(projectRoot, "Worker.cs");
            WriteParseableInterface(Path.Combine(projectRoot, "IParseable.cs"), hasStaticContract: false);
            var contracts = Update5347(projectRoot, "IParseable.cs");
            Assert.Equal("expanded", contracts.GetProperty("csharp_workspace_expansion").GetProperty("decision").GetString());
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            AssertFullParity5347(projectRoot);

            Update5347(projectRoot, "Settings.cs");
            File.Move(Path.Combine(projectRoot, "Settings.cs"), Path.Combine(projectRoot, "Renamed.cs"));
            var rename = Update5347(projectRoot, "Settings.cs", "Renamed.cs");
            Assert.Equal("expanded", rename.GetProperty("csharp_workspace_expansion").GetProperty("decision").GetString());
            Assert.False(IndexedFileExists(projectRoot, "Settings.cs"));
            AssertFullParity5347(projectRoot);

            Update5347(projectRoot, "Renamed.cs");
            File.Delete(Path.Combine(projectRoot, "Renamed.cs"));
            var removal = Update5347(projectRoot, "Renamed.cs");
            Assert.Equal("expanded", removal.GetProperty("csharp_workspace_expansion").GetProperty("decision").GetString());
            Assert.False(IndexedFileExists(projectRoot, "Renamed.cs"));
            AssertFullParity5347(projectRoot);

            File.WriteAllText(Path.Combine(projectRoot, ".cdidxignore"), "Consumer.cs\n");
            var config = Update5347(projectRoot, ".cdidxignore");
            Assert.Equal("full_scan", config.GetProperty("csharp_workspace_expansion").GetProperty("decision").GetString());
            Assert.Equal("configuration_changed", config.GetProperty("csharp_workspace_expansion").GetProperty("reason").GetString());
            AssertExpansionPersisted5347(projectRoot, config);
            Assert.False(IndexedFileExists(projectRoot, "Consumer.cs"));
            AssertFullParity5347(projectRoot);
        }
        finally { DeleteDirectory(projectRoot); }
    }

    [Fact]
    public void Run_Update_NarrowingRetainsWorkspaceRaceBarrierAndCancellation_Issue5347()
    {
        var projectRoot = CreateExpansionProject5347();
        var previousBarrier = IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting;
        var previousPrepass = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        try
        {
            Assert.Equal(0, RunAndCaptureJson([projectRoot, "--json"]).ExitCode);
            Update5347(projectRoot, "Worker.cs");
            var before = ReadSemanticRows5347(projectRoot);
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = phase =>
            {
                if (phase == "before_write")
                    File.WriteAllText(Path.Combine(projectRoot, ".cdidxignore"), "Settings.cs\n");
            };
            var (exitCode, raced) = RunAndCaptureJson([projectRoot, "--files", "Worker.cs", "--json"]);
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("deferred", raced.GetProperty("csharp_workspace_expansion").GetProperty("decision").GetString());
            Assert.Equal(before, ReadSemanticRows5347(projectRoot));
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = previousBarrier;
            File.Delete(Path.Combine(projectRoot, ".cdidxignore"));

            using var cancellation = new CancellationTokenSource();
            IndexCommandRunner.UpdateCSharpPrepassForTesting = cancellation.Cancel;
            var cancelled = IndexCommandRunner.Run([projectRoot, "--files", "Worker.cs", "--json", "--quiet"], _jsonOptions, cancellation);
            Assert.NotEqual(CommandExitCodes.Success, cancelled);
            Assert.Equal(before, ReadSemanticRows5347(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = previousBarrier;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepass;
            DeleteDirectory(projectRoot);
        }
    }

    private static string WorkerSource5347(int value)
        => $"public class Worker\n{{\n    public int Read()\n    {{\n        return {value};\n    }}\n}}\n";

    private static string CreateExpansionProject5347()
    {
        var root = CreateTempProject();
        WriteParseableInterface(Path.Combine(root, "IParseable.cs"), hasStaticContract: true);
        File.WriteAllText(Path.Combine(root, "Money.cs"), "public readonly struct Money : IParseable<Money>\n{\n    public static Money Parse(string s) => new();\n}\n");
        File.WriteAllText(Path.Combine(root, "Settings.cs"), "public class Settings { public const int Limit = 7; }\n");
        File.WriteAllText(Path.Combine(root, "Consumer.cs"), "public class Consumer { public int Read() => Settings.Limit; }\n");
        File.WriteAllText(Path.Combine(root, "Worker.cs"), WorkerSource5347(1));
        File.WriteAllText(Path.Combine(root, "notes.md"), "# Notes\n");
        return root;
    }

    private JsonElement Update5347(string root, params string[] paths)
    {
        var (exitCode, json) = RunAndCaptureJson([root, "--files", .. paths, "--json"]);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.True(json.GetProperty("index_complete").GetBoolean());
        Assert.True(json.GetProperty("reference_graph_complete").GetBoolean());
        return json;
    }

    private static void AssertExpansion5347(JsonElement json, string decision, string reason, int original, int expanded, int final)
    {
        var value = json.GetProperty("csharp_workspace_expansion");
        Assert.True(decision == value.GetProperty("decision").GetString(), value.ToString());
        Assert.Equal(reason, value.GetProperty("reason").GetString());
        Assert.Equal(original, value.GetProperty("original_target_count").GetInt32());
        Assert.Equal(expanded, value.GetProperty("expanded_target_count").GetInt32());
        Assert.Equal(final, value.GetProperty("final_target_count").GetInt32());
        foreach (var phase in new[] { "initial_prepass_ms", "workspace_scan_ms", "workspace_prepass_ms" })
            Assert.True(value.GetProperty(phase).GetInt64() >= 0);
    }

    private void AssertExpansionPersisted5347(string root, JsonElement immediate)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(root, ".cdidx", "codeindex.db"));
        using var reader = new DbReader(db.Connection);
        var status = reader.GetStatus();
        Assert.NotNull(status.LastIndexRun?.CSharpWorkspaceExpansion);
        Assert.Equal(immediate.GetProperty("csharp_workspace_expansion").ToString(),
            JsonSerializer.SerializeToElement(status.LastIndexRun.CSharpWorkspaceExpansion, _jsonOptions).ToString());
        Assert.True(status.IndexComplete);
        Assert.True(status.ReferenceGraphComplete);
    }

    private void AssertFullParity5347(string root)
    {
        var scoped = ReadSemanticRows5347(root);
        var (exitCode, full) = RunAndCaptureJson([root, "--rebuild", "--yes", "--json"]);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.True(full.GetProperty("index_complete").GetBoolean());
        Assert.True(full.GetProperty("reference_graph_complete").GetBoolean());
        Assert.Equal(scoped, ReadSemanticRows5347(root));
    }

    private static string[] ReadSemanticRows5347(string root)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(root, ".cdidx", "codeindex.db"));
        var rows = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT path, lang, checksum FROM files",
            "SELECT f.path, s.* FROM symbols s JOIN files f ON f.id = s.file_id",
            """
            SELECT f.path, r.*, sf.path AS source_file, ss.name AS source_name,
                   tf.path AS target_file, ts.name AS target_name
            FROM symbol_references r JOIN files f ON f.id = r.file_id
            LEFT JOIN symbols ss ON ss.id = r.source_symbol_id LEFT JOIN files sf ON sf.id = ss.file_id
            LEFT JOIN symbols ts ON ts.id = r.target_symbol_id LEFT JOIN files tf ON tf.id = ts.file_id
            """,
        })
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var fields = new List<string?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.GetName(i) is "id" or "file_id" or "reference_line_id" or "source_symbol_id" or "target_symbol_id")
                        continue;
                    fields.Add(reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
                }
                rows.Add(JsonSerializer.Serialize(fields));
            }
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
