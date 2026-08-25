using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void ExactCallersAndImpact_ExposeUnresolvedIdentityRootAsNonAuthoritative_Issue5183()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue5183_cli");
        try
        {
            var dbPath = CreateIssue5183CliDatabase(projectRoot);

            var (callersExitCode, callersStdout, callersStderr) = CaptureConsole(() => RunGraphCommand(
                "callers",
                ["MissingLeaf5183", "--db", dbPath, "--json", "--count", "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            using var callersDocument = ParseJsonOutput(callersStdout);
            var callers = callersDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(string.Empty, callersStderr);
            Assert.Equal(0, callers.GetProperty("count").GetInt32());
            Assert.False(callers.GetProperty("identity_root_available").GetBoolean());
            Assert.Equal("no_identity_backed_root", callers.GetProperty("identity_root_unavailable_reason").GetString());
            Assert.Equal("no_identity_root", callers.GetProperty("graph_evidence_confidence").GetString());
            Assert.True(callers.GetProperty("degraded").GetBoolean());
            Assert.False(callers.GetProperty("authoritative_count").GetBoolean());

            var (broadExitCode, broadStdout, broadStderr) = CaptureConsole(() => RunGraphCommand(
                "callers",
                ["MissingLeaf5183", "--db", dbPath, "--json", "--count", "--lang", "csharp"],
                _jsonOptions));
            using var broadDocument = ParseJsonOutput(broadStdout);
            var broad = broadDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, broadExitCode);
            Assert.Equal(string.Empty, broadStderr);
            Assert.Equal(1, broad.GetProperty("count").GetInt32());
            Assert.Equal("name_discovery", broad.GetProperty("graph_evidence_confidence").GetString());
            Assert.False(broad.TryGetProperty("identity_root_available", out _));
            Assert.True(broad.GetProperty("authoritative_count").GetBoolean());

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["MissingLeaf5183", "--db", dbPath, "--json", "--count", "--lang", "csharp"],
                _jsonOptions));
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impact = impactDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            Assert.Equal(0, impact.GetProperty("confirmed_count").GetInt32());
            Assert.True(impact.GetProperty("heuristic").GetBoolean());
            Assert.False(impact.GetProperty("identity_root_available").GetBoolean());
            Assert.Equal("no_identity_backed_root", impact.GetProperty("identity_root_unavailable_reason").GetString());
            Assert.False(impact.GetProperty("authoritative_count").GetBoolean());
            Assert.Contains(
                "no_identity_backed_root",
                impact.GetProperty("impact_failure_chain").EnumerateArray().Select(item => item.GetString()));

            var (strictExitCode, _, strictStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["MissingLeaf5183", "--db", dbPath, "--json", "--strict", "--lang", "csharp"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.FeatureUnavailable, strictExitCode);
            Assert.Equal(string.Empty, strictStderr);

            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["MissingLeaf5183", "--db", dbPath, "--json", "--exact", "--lang", "csharp"],
                _jsonOptions));
            using var inspectDocument = ParseJsonOutput(inspectStdout);
            var inspect = inspectDocument.RootElement;
            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.NotEmpty(inspect.GetProperty("references").EnumerateArray());
            Assert.Empty(inspect.GetProperty("callers").EnumerateArray());
            Assert.Equal(0, inspect.GetProperty("graph_sections").GetProperty("callers").GetProperty("total").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateIssue5183CliDatabase(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/issue5183/Caller.cs",
            "csharp",
            """
            namespace Issue5183;
            public class Caller5183
            {
                public void CallMissing5183() => ExternalApi5183.MissingLeaf5183();
            }
            """);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
        return dbPath;
    }
}
