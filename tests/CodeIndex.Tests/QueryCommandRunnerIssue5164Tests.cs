using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerIssue5164Tests
{
    [Fact]
    public void CandidateCallees_LegacySchemaWithoutSourceIdentityFailsClosed_Issue5164Review()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_selector_legacy_source_identity_5164");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFiles(
                dbPath,
                [
                    new TestProjectHelper.IndexedFileFixture("a.sh", "shell", "first_cmd\n"),
                    new TestProjectHelper.IndexedFileFixture("b.sh", "shell", "second_cmd\n"),
                ]);

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using (var legacySchema = db.Connection.CreateCommand())
            {
                legacySchema.CommandText = """
                    DROP INDEX idx_symbol_refs_unresolved_mutual_folded;
                    DROP INDEX idx_symbol_refs_resolved_source_target_kind;
                    DROP INDEX idx_symbol_refs_source_symbol;
                    ALTER TABLE symbol_references DROP COLUMN source_symbol_id;
                    INSERT INTO symbol_references(
                        file_id, symbol_name, reference_kind, line, column_number,
                        container_kind, container_name, symbol_name_folded, container_name_folded)
                    SELECT id, 'first_cmd', 'call', 1, 1,
                           'function', '<script>', 'first_cmd', '<script>'
                    FROM files WHERE path = 'a.sh';
                    INSERT INTO symbol_references(
                        file_id, symbol_name, reference_kind, line, column_number,
                        container_kind, container_name, symbol_name_folded, container_name_folded)
                    SELECT id, 'second_cmd', 'call', 1, 1,
                           'function', '<script>', 'second_cmd', '<script>'
                    FROM files WHERE path = 'b.sh';
                    """;
                legacySchema.ExecuteNonQuery();
            }

            var reader = new DbReader(db.Connection);
            var candidate = new DefinitionResult
            {
                SymbolId = 1,
                Name = "<script>",
                Lang = "shell",
            };

            Assert.Empty(reader.GetCalleesForCandidate(
                candidate,
                limit: 20,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false));
            Assert.Equal(0, reader.CountCalleesForCandidate(
                candidate,
                pathPatterns: null,
                excludePathPatterns: null,
                excludeTests: false).Count);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ScriptScope_OutlineAndInspectExposeSameFileQualifiedIdentity_Issue5164Review()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_script_scope_identity_5164");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "scripts/run.sh",
                "shell",
                "echo ready\n");
            MarkCurrentContracts(dbPath);

            using var outline = RunOutline(dbPath, "scripts/run.sh");
            var scriptScope = Assert.Single(outline.RootElement.GetProperty("symbols").EnumerateArray(), symbol =>
                symbol.GetProperty("name").GetString() == "<script>");
            var selector = scriptScope.GetProperty("selector").GetString();
            Assert.Equal("scripts/run.sh::<script>", scriptScope.GetProperty("qualified_name").GetString());

            var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["--path", "scripts/run.sh", "--line", "1", "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions));
            using var inspect = QueryCommandTestSupport.ParseJsonOutput(stdout);
            var candidateSelector = Assert.Single(inspect.RootElement
                .GetProperty("candidate_bundles")
                .EnumerateArray())
                .GetProperty("selector");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(selector, candidateSelector.GetProperty("selector").GetString());
            Assert.Equal("scripts/run.sh::<script>", candidateSelector.GetProperty("qualified_name").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void TopLevelSelectors_RoundTripCoordinateAndCalleeIdentityWithoutCrossFileCollisions_Issue5164()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_top_level_selector_5164");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFiles(
                dbPath,
                [
                    new TestProjectHelper.IndexedFileFixture(
                        "src/First.cs",
                        "csharp",
                        """
                        using System.Text;
                        using CodeIndex.Cli;

                        // On Windows the console defaults to the OEM code page.
                        // Keep Unicode output deterministic.
                        // 日本語の出力も UTF-8 に揃える。
                        Console.OutputEncoding = Encoding.UTF8;
                        ConsoleUi.EnsureConsoleWritersSynchronized();
                        RuntimeSafety.Configure();
                        return ProgramRunner.Run(args);
                        """),
                    new TestProjectHelper.IndexedFileFixture(
                        "src/Second.cs",
                        "csharp",
                        "using System;\nConsole.WriteLine(\"second\");\nTarget.Run();\n"),
                    new TestProjectHelper.IndexedFileFixture(
                        "src/Target.cs",
                        "csharp",
                        "public static class Target { public static void Run() { } }\n"),
                    new TestProjectHelper.IndexedFileFixture(
                        "src/ProgramRunner.cs",
                        "csharp",
                        "public static class ProgramRunner { public static int Run(string[] args) => 0; }\n"),
                ]);
            MarkCurrentContracts(dbPath);

            var firstOutline = RunOutline(dbPath, "src/First.cs");
            var secondOutline = RunOutline(dbPath, "src/Second.cs");
            var firstTopLevel = GetTopLevelSymbol(firstOutline.RootElement);
            var secondTopLevel = GetTopLevelSymbol(secondOutline.RootElement);
            var firstSelector = firstTopLevel.GetProperty("selector").GetString()!;
            var secondSelector = secondTopLevel.GetProperty("selector").GetString()!;

            Assert.NotEqual(firstSelector, secondSelector);
            Assert.Equal("src/First.cs::<top-level>", firstTopLevel.GetProperty("qualified_name").GetString());
            Assert.Equal(7, firstTopLevel.GetProperty("start_line").GetInt32());
            Assert.Equal(10, firstTopLevel.GetProperty("end_line").GetInt32());

            foreach (var line in new[] { 7, 8, 9, 10 })
            {
                var (inspectExitCode, inspectStdout, inspectStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                    QueryCommandRunner.RunInspect(
                        ["--path", "src/First.cs", "--line", line.ToString(), "--db", dbPath, "--json"],
                        QueryCommandTestSupport.JsonOptions));
                using var inspect = QueryCommandTestSupport.ParseJsonOutput(inspectStdout);
                var definition = Assert.Single(inspect.RootElement.GetProperty("definitions").EnumerateArray());

                Assert.Equal(CommandExitCodes.Success, inspectExitCode);
                Assert.Equal(string.Empty, inspectStderr);
                var bundle = inspect.RootElement.GetProperty("candidate_bundles")[0];
                Assert.Equal(firstSelector, bundle
                    .GetProperty("selector")
                    .GetProperty("selector")
                    .GetString());
                Assert.Equal(SyntheticSymbolIdentity.CSharpTopLevelScopeName, definition.GetProperty("name").GetString());
                Assert.True(definition.GetProperty("is_synthetic").GetBoolean());
                var calleeNames = bundle.GetProperty("callees")
                    .EnumerateArray()
                    .Select(callee => callee.GetProperty("callee_name").GetString())
                    .ToArray();
                Assert.Contains("EnsureConsoleWritersSynchronized", calleeNames);
                Assert.Contains("Configure", calleeNames);
                Assert.Contains("Run", calleeNames);
            }

            var (calleesExitCode, calleesStdout, calleesStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallees(
                    [firstSelector, "--path", "src/First.cs", "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions));
            using var calleeRows = new JsonDocumentCollection(QueryCommandTestSupport.ParseJsonLines(calleesStdout));
            Assert.Equal(CommandExitCodes.Success, calleesExitCode);
            Assert.Equal(string.Empty, calleesStderr);
            Assert.All(calleeRows.Documents, row =>
                Assert.Equal("src/First.cs", row.RootElement.GetProperty("path").GetString()));
            Assert.Equal(
                new[] { "Configure", "EnsureConsoleWritersSynchronized", "Run" },
                calleeRows.Documents
                    .Select(row => row.RootElement.GetProperty("callee_name").GetString())
                    .Order(StringComparer.Ordinal)
                    .ToArray());

            var (countExitCode, countStdout, countStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallees(
                    [firstSelector, "--path", "src/First.cs", "--db", dbPath, "--count", "--json"],
                    QueryCommandTestSupport.JsonOptions));
            using var count = QueryCommandTestSupport.ParseJsonOutput(countStdout);
            Assert.Equal(CommandExitCodes.Success, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            Assert.Equal(3, count.RootElement.GetProperty("count").GetInt32());

            var (callersExitCode, callersStdout, callersStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallers(
                    ["Run", "--db", dbPath, "--json", "--exact-name"],
                    QueryCommandTestSupport.JsonOptions));
            using var callerRows = new JsonDocumentCollection(QueryCommandTestSupport.ParseJsonLines(callersStdout));
            Assert.Equal(CommandExitCodes.Success, callersExitCode);
            Assert.Equal(string.Empty, callersStderr);
            var topLevelCallers = callerRows.Documents
                .Where(row => row.RootElement.GetProperty("caller_name").GetString() == SyntheticSymbolIdentity.CSharpTopLevelScopeName)
                .Select(row => row.RootElement.GetProperty("path").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { "src/First.cs", "src/Second.cs" }, topLevelCallers);

            var (compactExitCode, compactStdout, compactStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunOutline(
                    ["src/First.cs", "--db", dbPath, "--compact"],
                    QueryCommandTestSupport.JsonOptions));
            using var compact = QueryCommandTestSupport.ParseJsonOutput(compactStdout);
            var compactTopLevel = GetTopLevelSymbol(compact.RootElement);
            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            Assert.Equal("src/First.cs", compact.RootElement.GetProperty("path").GetString());
            Assert.Equal("function", compactTopLevel.GetProperty("kind").GetString());
            Assert.Equal(7, compactTopLevel.GetProperty("start_line").GetInt32());
            Assert.Equal(10, compactTopLevel.GetProperty("end_line").GetInt32());
            Assert.True(compactTopLevel.GetProperty("is_synthetic").GetBoolean());
            Assert.Equal(firstSelector, compactTopLevel.GetProperty("selector").GetString());
            Assert.Equal("src/First.cs::<top-level>", compactTopLevel.GetProperty("qualified_name").GetString());

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GenerationChange.cs",
                "csharp",
                "public sealed class GenerationChange { }\n");
            var (staleExitCode, staleStdout, staleStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallees(
                    [firstSelector, "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions));
            using var stale = QueryCommandTestSupport.ParseJsonOutput(staleStdout);
            Assert.Equal(CommandExitCodes.NotFound, staleExitCode);
            Assert.Equal(string.Empty, staleStderr);
            Assert.Equal(CommandErrorCodes.QueryNotFound, stale.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("stale", stale.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Outline_StaleOrMissingCSharpExtractorContractReportsTypedReindexLimitation_Issue5164(
        bool hasLegacyExtractorVersion)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_top_level_stale_5164");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Legacy.cs",
                "csharp",
                "using System;\nConsole.WriteLine(\"legacy\");\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var extractorVersionKey = DbContext.GetSymbolExtractorVersionMetaKey("csharp");
                if (hasLegacyExtractorVersion)
                {
                    writer.SetMeta(
                        extractorVersionKey,
                        (SymbolExtractor.CSharpContractVersion - 1).ToString());
                }
                else
                {
                    using var deleteMeta = db.Connection.CreateCommand();
                    deleteMeta.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                    deleteMeta.Parameters.AddWithValue("@key", extractorVersionKey);
                    Assert.Equal(1, deleteMeta.ExecuteNonQuery());
                }
                using var command = db.Connection.CreateCommand();
                command.CommandText = "DELETE FROM symbols WHERE sub_kind = @sub_kind";
                command.Parameters.AddWithValue("@sub_kind", SyntheticSymbolIdentity.CSharpTopLevelScopeSubKind);
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunOutline(
                    ["src/Legacy.cs", "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions));
            using var document = QueryCommandTestSupport.ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("reindex_required", document.RootElement.GetProperty("top_level_symbol_support").GetString());
            Assert.Contains("reindex", document.RootElement.GetProperty("top_level_symbol_limitation").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static JsonDocument RunOutline(string dbPath, string path)
    {
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
            QueryCommandRunner.RunOutline(
                [path, "--db", dbPath, "--json"],
                QueryCommandTestSupport.JsonOptions));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return QueryCommandTestSupport.ParseJsonOutput(stdout);
    }

    private static JsonElement GetTopLevelSymbol(JsonElement outline)
        => Assert.Single(outline.GetProperty("symbols").EnumerateArray().Where(symbol =>
            symbol.GetProperty("name").GetString() == SyntheticSymbolIdentity.CSharpTopLevelScopeName));

    private static void MarkCurrentContracts(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkReferenceIdentityContractReady();
        writer.StampSymbolExtractorVersions(["csharp"]);
    }

    private sealed class JsonDocumentCollection(List<JsonDocument> documents) : IDisposable
    {
        public List<JsonDocument> Documents { get; } = documents;

        public void Dispose()
        {
            foreach (var document in Documents)
                document.Dispose();
        }
    }
}
