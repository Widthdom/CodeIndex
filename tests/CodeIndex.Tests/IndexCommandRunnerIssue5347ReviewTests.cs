using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_Update_ProjectMarkerChangeMatchesConservativeScope_Issue5347Review(bool duringExpansion)
    {
        var root = CreateExpansionProject5347();
        var priorHook = IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            var part = Path.Combine(root, "src", "Api.Part1.cs");
            File.WriteAllText(part, "public partial class Api { public int Run() => 1; }\n");
            File.WriteAllText(Path.Combine(root, "src", "Api.Part2.cs"),
                "public partial class Api { public int Read() => Run(); }\n");
            Assert.Equal(0, RunAndCaptureJson([root, "--json"]).ExitCode);
            Update5347(root, "src/Api.Part1.cs");

            var controlPath = Path.Combine(root, ".cdidx", "control.db");
            using (var source = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(root, ".cdidx", "codeindex.db")))
            using (var control = new DbContext(DbOpenIntent.WriteIndex, controlPath))
            {
                source.Connection.BackupDatabase(control.Connection);
                new DbWriter(control.Connection).SetMeta(DbContext.CSharpWorkspaceContractBaselineMetaKey, null);
            }

            var markerPath = Path.Combine(root, "App.csproj");
            void AddMarker() => File.WriteAllText(markerPath, "<Project />\n");
            if (duringExpansion)
                IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = AddMarker;
            else
                AddMarker();
            File.WriteAllText(part, "public partial class Api { public int Run() => 2; }\n");
            string[] targets = duringExpansion ? ["src/Api.Part1.cs"] : ["src/Api.Part1.cs", "App.csproj"];
            var (exitCode, actual) = RunAndCaptureJson(
                [root, "--files", .. targets, "--allow-partial", "--json"]);
            Assert.True(exitCode == CommandExitCodes.Success, actual.ToString());
            Assert.Equal("expanded", actual.GetProperty("csharp_workspace_expansion").GetProperty("decision").GetString());
            Assert.Equal("contract_inputs_changed", actual.GetProperty("csharp_workspace_expansion").GetProperty("reason").GetString());
            // Give the old-database control the same marker timing as the subject.
            if (duringExpansion)
                File.Delete(markerPath);
            var (controlCode, conservative) = RunAndCaptureJson(
                [root, "--db", controlPath, "--files", .. targets, "--allow-partial", "--json"]);
            Assert.Equal(CommandExitCodes.Success, controlCode);
            Assert.Equal(ReadSemanticRows5347(root, "control.db"), ReadSemanticRows5347(root));
            foreach (var flag in new[] { "index_complete", "reference_graph_complete", "hotspot_family_ready" })
                Assert.Equal(conservative.GetProperty(flag).GetBoolean(), actual.GetProperty(flag).GetBoolean());
            IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = priorHook;
            // The racing marker is outside the requested paths; compare C# data
            // after a full scan also admits that newly created MSBuild file.
            AssertFullParity5347(root, csharpOnly: duringExpansion);
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = priorHook;
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_Update_IncompleteProjectMarkerEvidencePreventsNarrowing_Issue5347Review(bool duringExpansion)
    {
        var root = CreateExpansionProject5347();
        var priorBudget = FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting;
        var priorHook = IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Assert.Equal(0, RunAndCaptureJson([root, "--json"]).ExitCode);
            Update5347(root, "Worker.cs");
            void ExhaustBudget()
            {
                FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting = 1;
                Assert.False(new FileIndexer(root).GetProjectMarkerFingerprintResult("csharp").IsComplete);
            }
            if (duringExpansion)
                IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = ExhaustBudget;
            else
                ExhaustBudget();
            var (exitCode, json) = RunAndCaptureJson(
                [root, "--files", "Worker.cs", "--allow-partial", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            AssertExpansion5347(json, "expanded", "safety_checks_required", 1, 5, 5);
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = priorHook;
            FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting = priorBudget;
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Run_Update_CaseOnlyContractRenamePersistsPeakCounts_Issue5347Review()
    {
        var root = CreateTempProject();
        try
        {
            RunGit(root, "init");
            var oldPath = Path.Combine(root, "contract.cs");
            WriteParseableInterface(oldPath, hasStaticContract: true);
            RunGit(root, "add", ".");
            RunGit(root, "commit", "-m", "initial contract");
            Assert.Equal(0, RunAndCaptureJson([root, "--json"]).ExitCode);
            RunGit(root, "mv", "contract.cs", "rename.tmp");
            RunGit(root, "mv", "rename.tmp", "Contract.cs");
            RunGit(root, "commit", "-m", "rename contract casing");
            var (exitCode, json) = RunAndCaptureJson([root, "--commits", "HEAD", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            var expansion = json.GetProperty("csharp_workspace_expansion");
            Assert.Equal(2, expansion.GetProperty("original_target_count").GetInt32());
            Assert.Equal(2, expansion.GetProperty("expanded_target_count").GetInt32());
            var finalCount = expansion.GetProperty("final_target_count").GetInt32();
            Assert.InRange(finalCount, 1, 2);
            if (File.Exists(oldPath))
                Assert.Equal(1, finalCount);
            AssertExpansionPersisted5347(root, json);
            Assert.False(IndexedFileExists(root, "contract.cs"));
            Assert.True(IndexedFileExists(root, "Contract.cs"));
            AssertFullParity5347(root);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Run_Update_FullScanFallbackRespectsQuiet_Issue5347Review()
    {
        var root = CreateExpansionProject5347();
        try
        {
            Assert.Equal(0, RunAndCaptureJson([root, "--json"]).ExitCode);
            File.WriteAllText(Path.Combine(root, ".cdidxignore"), "scratch/\n");
            using var stdout = new StringWriter();
            var originalOut = Console.Out;
            try
            {
                Console.SetOut(stdout);
                Assert.Equal(CommandExitCodes.Success,
                    IndexCommandRunner.Run([root, "--files", ".cdidxignore", "--quiet"], _jsonOptions));
            }
            finally { Console.SetOut(originalOut); }
            Assert.DoesNotContain("C# update scope", stdout.ToString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(root, ".cdidx", "codeindex.db"));
            using var reader = new DbReader(db.Connection);
            Assert.Equal("full_scan", reader.GetStatus().LastIndexRun?.CSharpWorkspaceExpansion?.Decision);
        }
        finally { DeleteDirectory(root); }
    }
}
