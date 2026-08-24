using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_FullIndexReextractsUnstampedCSharpBeforeCertifyingTopLevelContract_Issue5164Review()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_top_level_unstamped_upgrade_5164");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Program.cs"),
                "using System;\nConsole.WriteLine(\"upgrade\");\n");
            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM symbols WHERE sub_kind = @sub_kind;
                    DELETE FROM codeindex_meta WHERE key = @extractor_key;
                    """;
                command.Parameters.AddWithValue(
                    "@sub_kind",
                    SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind);
                command.Parameters.AddWithValue(
                    "@extractor_key",
                    DbContext.GetSymbolExtractorVersionMetaKey("csharp"));
                Assert.Equal(2, command.ExecuteNonQuery());
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_extracted").GetInt32());
            Assert.Equal((2, 2), ReadTopLevelFactIssue5164(dbPath, "Program.cs").Span);

            using var verifiedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(verifiedDb.Connection);
            Assert.True(writer.SymbolExtractorVersionsMatchCurrent(["csharp"]));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullAndIncrementalTopLevelAddChangeDeleteRenameAreDeterministic_Issue5164()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_top_level_incremental_5164");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var programPath = Path.Combine(projectRoot, "Program.cs");
        var addedPath = Path.Combine(projectRoot, "Added.cs");
        var renamedPath = Path.Combine(projectRoot, "Renamed.cs");
        try
        {
            File.WriteAllText(programPath, "using System;\nConsole.WriteLine(\"initial\");\n");

            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            var initial = ReadTopLevelFactIssue5164(dbPath, "Program.cs");
            Assert.Equal((2, 2), (initial.StartLine, initial.EndLine));

            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            Assert.Equal(initial, ReadTopLevelFactIssue5164(dbPath, "Program.cs"));

            File.WriteAllText(
                programPath,
                "using System;\nConsole.WriteLine(\"changed and longer\");\nConsole.WriteLine(\"again\");\n");
            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            var changed = ReadTopLevelFactIssue5164(dbPath, "Program.cs");
            Assert.Equal((2, 3), (changed.StartLine, changed.EndLine));
            Assert.NotEqual(initial, changed);

            File.WriteAllText(addedPath, "System.Console.WriteLine(\"added\");\n");
            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            Assert.Equal((1, 1), ReadTopLevelFactIssue5164(dbPath, "Added.cs").Span);
            Assert.Equal(2, CountTopLevelFactsIssue5164(dbPath));

            File.Move(addedPath, renamedPath);
            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            Assert.Null(TryReadTopLevelFactIssue5164(dbPath, "Added.cs"));
            Assert.Equal((1, 1), ReadTopLevelFactIssue5164(dbPath, "Renamed.cs").Span);
            Assert.Equal(2, CountTopLevelFactsIssue5164(dbPath));

            File.Delete(programPath);
            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            Assert.Null(TryReadTopLevelFactIssue5164(dbPath, "Program.cs"));
            var renamed = ReadTopLevelFactIssue5164(dbPath, "Renamed.cs");
            Assert.Equal((1, 1), renamed.Span);
            Assert.Equal(1, CountTopLevelFactsIssue5164(dbPath));

            AssertSuccessfulTopLevelIndexIssue5164(projectRoot);
            Assert.Equal(renamed, ReadTopLevelFactIssue5164(dbPath, "Renamed.cs"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private void AssertSuccessfulTopLevelIndexIssue5164(string projectRoot)
    {
        var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal("success", json.GetProperty("status").GetString());
    }

    private static TopLevelFact ReadTopLevelFactIssue5164(string dbPath, string path)
        => TryReadTopLevelFactIssue5164(dbPath, path)
            ?? throw new Xunit.Sdk.XunitException($"Expected one top-level symbol for '{path}'.");

    private static TopLevelFact? TryReadTopLevelFactIssue5164(string dbPath, string path)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var command = db.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.start_line, s.end_line
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = @path AND s.sub_kind = @sub_kind
            ORDER BY s.id;
            """;
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@sub_kind", SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var result = new TopLevelFact(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2));
        Assert.False(reader.Read(), $"Expected at most one top-level symbol for '{path}'.");
        return result;
    }

    private static int CountTopLevelFactsIssue5164(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols WHERE sub_kind = @sub_kind";
        command.Parameters.AddWithValue("@sub_kind", SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed record TopLevelFact(long SymbolId, int StartLine, int EndLine)
    {
        public (int StartLine, int EndLine) Span => (StartLine, EndLine);
    }
}
