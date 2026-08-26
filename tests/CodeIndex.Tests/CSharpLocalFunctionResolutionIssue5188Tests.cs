using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Lsp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class CSharpLocalFunctionResolutionIssue5188Tests
{
    private const string FixturePath = "src/LocalFunctions.cs";

    private const string FixtureSource = """
        using System;

        public class FirstHost
        {
            public void Run()
            {
                SameLocal(); // first-before
                void SameLocal() { }
                SameLocal(); // first-after
                Action firstGroup = SameLocal; // first-method-group
            }

            public void Overloads()
            {
                Overloaded(); // overload-zero-before
                Overloaded(1); // overload-one-before
                void Overloaded() { }
                void Overloaded(int value) { }
                {
                    void Overloaded(string value) { }
                    Overloaded("nested"); // nested-overload
                }
            }

            public void ParameterShadow(Action SameLocal)
            {
                SameLocal(); // parameter-shadow
            }

            public void LocalShadow()
            {
                void ValueCall() { }
                {
                    Action ValueCall = () => { };
                    ValueCall(); // local-shadow
                }
                ValueCall(); // local-visible
            }

            public void Siblings()
            {
                {
                    void SiblingOnly() { }
                }
                {
                    SiblingOnly(); // sibling-hidden
                }
            }

            public void MemberFallback() { }

            public void LocalFallbackOwner()
            {
                void MemberFallback() { }
                MemberFallback(); // local-fallback-owner
            }

            public void MemberFallbackCaller()
            {
                MemberFallback(); // nonlocal-member-fallback
            }

            public void Lambda()
            {
                Func<int> lambda = () =>
                {
                    void LambdaOnly() { }
                    LambdaOnly(); // lambda-visible
                    return 0;
                };
            }

            public void ExpressionBodied()
            {
                int ExpressionOnly() => 1;
                _ = ExpressionOnly(); // expression-bodied
            }

            public Action ReturnedGroup()
            {
                void ReturnedLocal() { }
                return ReturnedLocal; // returned-method-group
            }
        }

        public class SecondHost
        {
            public void Run()
            {
                SameLocal(); // second-before
                void SameLocal() { }
                SameLocal(); // second-after
                Action secondGroup = SameLocal; // second-method-group
            }
        }
        """;

    [Fact]
    public void PersistedGraph_UsesNarrowestLexicalLocalFamilyAndValueShadowing_Issue5188()
    {
        var projectRoot = CreateIndexedFixture(out var dbPath, writeSourceFile: false);
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText = """
                SELECT reference.line,
                       reference.symbol_name,
                       reference.resolution_state,
                       reference.resolution_candidate_count,
                       target.line,
                       target.container_name
                FROM symbol_references AS reference
                LEFT JOIN symbols AS target ON target.id = reference.target_symbol_id
                JOIN files AS source_file ON source_file.id = reference.file_id
                WHERE source_file.path = @path
                  AND reference.reference_kind = 'call'
                  AND reference.symbol_name IN (
                      'SameLocal',
                      'Overloaded',
                       'ValueCall',
                       'SiblingOnly',
                       'MemberFallback',
                       'LambdaOnly',
                       'ExpressionOnly',
                       'ReturnedLocal')
                ORDER BY reference.line, reference.column_number;
                """;
            command.Parameters.AddWithValue("@path", FixturePath);

            var rows = new Dictionary<int, GraphRow>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(reader.GetInt32(0), new GraphRow(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }

            var firstTargetLine = LineOf("void SameLocal() { }", occurrence: 1);
            AssertResolved("first-before", firstTargetLine, "Run");
            AssertResolved("first-after", firstTargetLine, "Run");
            AssertResolved("first-method-group", firstTargetLine, "Run");

            var secondTargetLine = LineOf("void SameLocal() { }", occurrence: 2);
            AssertResolved("second-before", secondTargetLine, "Run");
            AssertResolved("second-after", secondTargetLine, "Run");
            AssertResolved("second-method-group", secondTargetLine, "Run");

            AssertResolved("overload-zero-before", LineOf("void Overloaded() { }"), "Overloads");
            AssertResolved("overload-one-before", LineOf("void Overloaded(int value) { }"), "Overloads");
            AssertResolved("nested-overload", LineOf("void Overloaded(string value) { }"), "Overloads");
            AssertResolved("local-visible", LineOf("void ValueCall() { }"), "LocalShadow");
            AssertResolved("local-fallback-owner", LineOf("void MemberFallback() { }", occurrence: 2), "LocalFallbackOwner");
            AssertResolved("nonlocal-member-fallback", LineOf("public void MemberFallback() { }"), "FirstHost");
            AssertResolved("lambda-visible", LineOf("void LambdaOnly() { }"), "Lambda");
            AssertResolved("expression-bodied", LineOf("int ExpressionOnly() => 1;"), "ExpressionBodied");
            AssertResolved("returned-method-group", LineOf("void ReturnedLocal() { }"), "ReturnedGroup");

            AssertUnresolved("parameter-shadow");
            AssertUnresolved("local-shadow");
            AssertUnresolved("sibling-hidden");

            void AssertResolved(string marker, int targetLine, string targetContainer)
            {
                var row = rows[LineOf(marker)];
                Assert.Equal("resolved", row.State);
                Assert.Equal(1, row.CandidateCount);
                Assert.Equal(targetLine, row.TargetLine);
                Assert.Equal(targetContainer, row.TargetContainer);
            }

            void AssertUnresolved(string marker)
            {
                var row = rows[LineOf(marker)];
                Assert.Equal("unresolved", row.State);
                Assert.Equal(0, row.CandidateCount);
                Assert.Null(row.TargetLine);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GenericFallback_NeverSelectsLocalFunctionFromAnotherFile_Issue5188Review()
    {
        const string callerSource = """
            public class Caller
            {
                public void Run()
                {
                    CrossFileOnly();
                }
            }
            """;
        const string localOwnerSource = """
            public class LocalOwner
            {
                public void Run()
                {
                    void CrossFileOnly() { }
                    CrossFileOnly();
                }
            }
            """;
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_local_cross_file_5188");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Caller.cs", "csharp", callerSource);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/LocalOwner.cs", "csharp", localOwnerSource);

        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var command = db.Connection.CreateCommand();
        command.CommandText = """
            SELECT source_file.path,
                   reference.resolution_state,
                   reference.resolution_candidate_count,
                   target_file.path
            FROM symbol_references AS reference
            JOIN files AS source_file ON source_file.id = reference.file_id
            LEFT JOIN symbols AS target ON target.id = reference.target_symbol_id
            LEFT JOIN files AS target_file ON target_file.id = target.file_id
            WHERE reference.symbol_name = 'CrossFileOnly'
              AND reference.reference_kind = 'call'
            ORDER BY source_file.path;
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("src/Caller.cs", reader.GetString(0));
        Assert.Equal("unresolved", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.Read());
        Assert.Equal("src/LocalOwner.cs", reader.GetString(0));
        Assert.Equal("resolved", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal("src/LocalOwner.cs", reader.GetString(3));
        Assert.False(reader.Read());
    }

    [Fact]
    public void IncompleteCallableRanges_StayUnresolvedInsteadOfFallingBackToFileScope_Issue5188()
    {
        const string source = """
            public class IncompleteHost
            {
                public void Run()
                {
                    Maybe();
                    void Maybe() { }
                }
            }
            """;
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_csharp_local_incomplete_5188");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);

        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/Incomplete.cs",
            Lang = "csharp",
            Size = source.Length,
            Lines = FileIndexer.CountPhysicalLines(source),
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "issue5188-incomplete",
        });
        var symbols = SymbolExtractor.Extract(fileId, "csharp", source, "src/Incomplete.cs");
        var outer = Assert.Single(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "Run"
            && symbol.ContainerKind == "class");
        outer.BodyStartLine = null;
        outer.BodyEndLine = null;

        var references = ReferenceExtractor.Extract(fileId, "csharp", source, symbols, "src/Incomplete.cs");
        var maybeCall = Assert.Single(references, reference =>
            reference.ReferenceKind == "call"
            && reference.SymbolName == "Maybe");
        Assert.Equal("\u001fcsharp_local_uncertain", maybeCall.TargetQualifier);

        writer.InsertSymbols(symbols);
        writer.InsertReferences(references);
        using var command = db.Connection.CreateCommand();
        command.CommandText = """
            SELECT resolution_state, resolution_candidate_count, target_symbol_id
            FROM symbol_references
            WHERE file_id = @file_id
              AND symbol_name = 'Maybe'
              AND reference_kind = 'call';
            """;
        command.Parameters.AddWithValue("@file_id", fileId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("unresolved", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
        Assert.False(reader.Read());
    }

    [Fact]
    public void QueriesExposeLexicalResolutionAndInspectSelectorKeepsOneLocalFamily_Issue5188()
    {
        var projectRoot = CreateIndexedFixture(out var dbPath, writeSourceFile: false);
        try
        {
            var firstTargetLine = LineOf("void SameLocal() { }", occurrence: 1);
            var selector = ReadSelector(dbPath, "SameLocal", firstTargetLine);
            var expectedFirstReferenceLines = new[]
            {
                LineOf("first-before"),
                LineOf("first-after"),
                LineOf("first-method-group"),
            };
            var expectedAllReferenceLines = expectedFirstReferenceLines
                .Concat([
                    LineOf("parameter-shadow"),
                    LineOf("second-before"),
                    LineOf("second-after"),
                    LineOf("second-method-group"),
                ])
                .Order()
                .ToArray();

            var (referencesExit, referencesStdout, referencesStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunReferences(
                    ["SameLocal", "--db", dbPath, "--json", "--exact-name"],
                    QueryCommandTestSupport.JsonOptions));
            var referenceRows = ParseJsonRows(referencesStdout)
                .SelectMany(row => row.TryGetProperty("references", out var envelopeReferences)
                    ? envelopeReferences.EnumerateArray().Select(reference => reference.Clone())
                    : [row])
                .ToArray();
            Assert.Equal(CommandExitCodes.Success, referencesExit);
            Assert.Equal(string.Empty, referencesStderr);
            var actualReferenceLines = referenceRows
                .Select(reference => reference.GetProperty("line").GetInt32())
                .Order()
                .ToArray();
            Assert.True(expectedAllReferenceLines.SequenceEqual(actualReferenceLines), referencesStdout);
            var shadowReference = Assert.Single(referenceRows, reference =>
                reference.GetProperty("line").GetInt32() == LineOf("parameter-shadow"));
            Assert.Equal("unresolved", shadowReference.GetProperty("resolution_state").GetString());

            var (callersExit, callersStdout, callersStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallers(
                    ["SameLocal", "--db", dbPath, "--json", "--exact-name"],
                    QueryCommandTestSupport.JsonOptions));
            var callerRows = ParseJsonRows(callersStdout)
                .SelectMany(row => row.TryGetProperty("callers", out var envelopeCallers)
                    ? envelopeCallers.EnumerateArray().Select(caller => caller.Clone())
                    : [row])
                .ToArray();
            Assert.Equal(CommandExitCodes.Success, callersExit);
            Assert.Equal(string.Empty, callersStderr);
            var caller = Assert.Single(callerRows);
            Assert.Equal("Run", caller.GetProperty("caller_name").GetString());
            Assert.Equal(6, caller.GetProperty("reference_count").GetInt32());

            var (inspectExit, inspectStdout, inspectStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["--selector", selector, "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions));
            using var inspect = QueryCommandTestSupport.ParseJsonOutput(inspectStdout);
            Assert.Equal(CommandExitCodes.Success, inspectExit);
            Assert.Equal(string.Empty, inspectStderr);
            var bundle = Assert.Single(inspect.RootElement.GetProperty("candidate_bundles").EnumerateArray());
            Assert.Equal(expectedFirstReferenceLines, bundle.GetProperty("references")
                .EnumerateArray()
                .Select(reference => reference.GetProperty("line").GetInt32())
                .Order()
                .ToArray());

            var (impactExit, impactStdout, impactStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunImpact(
                    ["SameLocal", "--db", dbPath, "--json", "--exact-name"],
                    QueryCommandTestSupport.JsonOptions));
            using var impact = QueryCommandTestSupport.ParseJsonOutput(impactStdout);
            Assert.Equal(CommandExitCodes.Success, impactExit);
            Assert.Equal(string.Empty, impactStderr);
            var impactCallers = impact.RootElement.GetProperty("callers").EnumerateArray().ToArray();
            Assert.Equal(2, impactCallers.Length);
            Assert.All(impactCallers, impactCaller => Assert.Equal("Run", impactCaller.GetProperty("caller_name").GetString()));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void LspPositionLookups_SelectTheMatchingLocalDefinitionAndReferences_Issue5188()
    {
        var projectRoot = CreateIndexedFixture(out var dbPath, writeSourceFile: true);
        try
        {
            var sourcePath = TestProjectHelper.ProjectPath(projectRoot, FixturePath);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var server = new LspServer(
                new DbReader(db),
                "1.2.3",
                ProgramRunner.CreateDefaultJsonOptions(),
                projectRoot);
            var initialize = server.HandleMessage(
                """{"jsonrpc":"2.0","id":51880,"method":"initialize","params":{}}""");
            Assert.NotNull(initialize);
            Assert.Null(initialize!["error"]);

            var firstCallLine = LineOf("first-before");
            var firstDefinition = server.HandleMessage(CreatePositionRequest(
                "textDocument/definition",
                sourcePath,
                51881,
                firstCallLine,
                CharacterOf(firstCallLine, "SameLocal")));
            var firstLocation = Assert.Single(firstDefinition!["result"]!.AsArray());
            Assert.Equal(LineOf("void SameLocal() { }", occurrence: 1) - 1,
                firstLocation!["range"]!["start"]!["line"]!.GetValue<int>());

            var secondCallLine = LineOf("second-before");
            var secondDefinition = server.HandleMessage(CreatePositionRequest(
                "textDocument/definition",
                sourcePath,
                51882,
                secondCallLine,
                CharacterOf(secondCallLine, "SameLocal")));
            var secondLocation = Assert.Single(secondDefinition!["result"]!.AsArray());
            Assert.Equal(LineOf("void SameLocal() { }", occurrence: 2) - 1,
                secondLocation!["range"]!["start"]!["line"]!.GetValue<int>());

            var shadowCallLine = LineOf("parameter-shadow");
            var shadowDefinition = server.HandleMessage(CreatePositionRequest(
                "textDocument/definition",
                sourcePath,
                51884,
                shadowCallLine,
                CharacterOf(shadowCallLine, "SameLocal")));
            Assert.Empty(shadowDefinition!["result"]!.AsArray());

            var shadowReferences = server.HandleMessage(CreatePositionRequest(
                "textDocument/references",
                sourcePath,
                51885,
                shadowCallLine,
                CharacterOf(shadowCallLine, "SameLocal"),
                includeDeclaration: false));
            Assert.Empty(shadowReferences!["result"]!.AsArray());

            var firstDeclarationLine = LineOf("void SameLocal() { }", occurrence: 1);
            var firstReferences = server.HandleMessage(CreatePositionRequest(
                "textDocument/references",
                sourcePath,
                51883,
                firstDeclarationLine,
                CharacterOf(firstDeclarationLine, "SameLocal"),
                includeDeclaration: false));
            Assert.Equal(
                new[] { LineOf("first-before"), LineOf("first-after"), LineOf("first-method-group") },
                firstReferences!["result"]!.AsArray()
                    .Select(location => location!["range"]!["start"]!["line"]!.GetValue<int>() + 1)
                    .Order()
                    .ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateIndexedFixture(out string dbPath, bool writeSourceFile)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_csharp_local_scope_5188");
        dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        if (writeSourceFile)
            TestProjectHelper.WriteTextFile(projectRoot, FixturePath, FixtureSource);
        TestProjectHelper.InsertIndexedFile(dbPath, FixturePath, "csharp", FixtureSource);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        writer.MarkCSharpSymbolNameContractReady();
        return projectRoot;
    }

    private static string ReadSelector(string dbPath, string name, int line)
    {
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
            QueryCommandRunner.RunInspect(
                [name, "--path", FixturePath, "--db", dbPath, "--json"],
                QueryCommandTestSupport.JsonOptions));
        using var inspect = QueryCommandTestSupport.ParseJsonOutput(stdout);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        var bundle = Assert.Single(inspect.RootElement
            .GetProperty("candidate_bundles")
            .EnumerateArray(),
            candidate => candidate
                .GetProperty("definition")
                .GetProperty("line")
                .GetInt32() == line);
        return bundle.GetProperty("selector").GetProperty("selector").GetString()!;
    }

    private static JsonElement[] ParseJsonRows(string output) =>
        QueryCommandTestSupport.ParseJsonLines(output)
            .Select(document =>
            {
                using (document)
                    return document.RootElement.Clone();
            })
            .ToArray();

    private static string CreatePositionRequest(
        string method,
        string sourcePath,
        int id,
        int oneBasedLine,
        int character,
        bool? includeDeclaration = null)
    {
        object parameters = includeDeclaration == null
            ? new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                position = new { line = oneBasedLine - 1, character },
            }
            : new
            {
                textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                position = new { line = oneBasedLine - 1, character },
                context = new { includeDeclaration = includeDeclaration.Value },
            };
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });
    }

    private static int LineOf(string value, int occurrence = 1)
    {
        var lines = FixtureSource.Split('\n');
        var seen = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].Contains(value, StringComparison.Ordinal))
                continue;
            seen++;
            if (seen == occurrence)
                return index + 1;
        }

        throw new InvalidOperationException($"Fixture marker not found: {value} occurrence {occurrence}");
    }

    private static int CharacterOf(int oneBasedLine, string value)
    {
        var line = FixtureSource.Split('\n')[oneBasedLine - 1];
        return line.IndexOf(value, StringComparison.Ordinal);
    }

    private sealed record GraphRow(
        string SymbolName,
        string State,
        int CandidateCount,
        int? TargetLine,
        string? TargetContainer);
}
