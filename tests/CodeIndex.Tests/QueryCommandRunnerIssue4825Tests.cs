using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void CSharpTypeReferenceArity_SkipsBlockCommentTrivia_Issue4825()
    {
        Assert.Equal(
            1,
            CSharpTypeReferenceArity.GetReferenceArity(
                "public Commented /* valid trivia */ <string>? Value { get; }",
                "Commented",
                8));
        Assert.Equal(
            1,
            CSharpTypeReferenceArity.GetDefinitionArity(
                "public class Commented /* valid trivia */ <T>",
                "Commented",
                "class"));
    }

    [Fact]
    public void CSharpTypeReferences_ResolveOnlyToTypeLikeSymbolsWithMatchingArity_Issue4825()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_csharp_type_reference_issue4825");
        try
        {
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Types.cs",
                """
                namespace Fixture.Types;

                public class Actual<T>
                {
                }

                public class Actual<TFirst, TSecond>
                {
                }

                public static class NAME
                {
                    public static string Run() => "";
                }

                public class Handler<T>
                {
                }

                public delegate Handler<TOut> Handler<T, TOut>(T input);

                public class Ordinal<T>
                {
                }

                public class ordinal<TFirst, TSecond>
                {
                }

                public class Commented /* valid trivia */ <T>
                {
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Impostors.cs",
                """
                namespace Fixture.Impostors;

                public sealed class CollisionHolder
                {
                    public string Action { get; } = "";
                    public string Stream { get; } = "";
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Consumer.cs",
                """
                using System;
                using System.IO;
                using Fixture.Types;

                namespace Fixture.Consumer;

                public sealed class Consumer
                {
                    public Action<string>? Callback { get; }
                    public Stream? Body { get; }
                    public Actual<string>? One { get; }
                    public Fixture.Types.Actual<string, string>? Two { get; }
                    public Handler<int, string>? Handler { get; }
                    public Commented /* valid trivia */ <string>? Trivia { get; }
                    public Ordinal<string, string>? WrongCaseArity { get; }
                                    public Actual<string>? SameLineOne { get; } public Actual<string, string>? SameLineTwo { get; }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/CaseProperty.cs",
                """
                namespace Fixture.CaseSensitive;

                public partial class CaseConsumer
                {
                    public string Name { get; } = "";
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/CaseUse.cs",
                """
                using Fixture.Types;

                namespace Fixture.CaseSensitive;

                public partial class CaseConsumer
                {
                    public string Invoke() => NAME.Run();
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/PartialState.cs",
                """
                namespace Fixture.Partials;

                public partial class Service
                {
                    public string Name { get; } = "";
                    public string NAME { get; } = "";
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/PartialUse.cs",
                """
                namespace Fixture.Partials;

                public partial class Service
                {
                    public string Normalize() => Name.Trim();
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/BaseState.cs",
                """
                namespace Fixture.Inheritance;

                public class Base
                {
                    protected string Name { get; } = "";
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Middle.cs",
                """
                namespace Fixture.Inheritance;

                public class Middle : Base
                {
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/DerivedUse.cs",
                """
                namespace Fixture.Inheritance;

                public class Derived : Middle
                {
                    public string Normalize() => Name.Trim();
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            foreach (var frameworkTypeName in new[] { "Action", "Stream" })
            {
                var (referenceExitCode, referenceStdout, referenceStderr) = CaptureConsole(
                    () => QueryCommandRunner.RunReferences(
                        [
                            frameworkTypeName,
                            "--db", dbPath,
                            "--json",
                            "--exact-name",
                            "--kind", "type_reference",
                            "--lang", "csharp",
                        ],
                        _jsonOptions));
                using var referenceDocument = ParseJsonOutput(referenceStdout);
                var reference = referenceDocument.RootElement;

                Assert.Equal(CommandExitCodes.Success, referenceExitCode);
                Assert.Equal(string.Empty, referenceStderr);
                Assert.Equal("unresolved", reference.GetProperty("resolution_state").GetString());
                Assert.False(reference.TryGetProperty("resolution_candidate_count", out _));
                Assert.False(reference.TryGetProperty("target_symbol_id", out _));
                Assert.Equal("src/Consumer.cs", reference.GetProperty("path").GetString());
            }

            using (var connection = new SqliteConnection(
                       new SqliteConnectionStringBuilder
                       {
                           DataSource = dbPath,
                           Mode = SqliteOpenMode.ReadOnly,
                       }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COALESCE(r.context, reference_line.context),
                           r.resolution_state,
                           s.kind,
                           s.signature
                    FROM symbol_references AS r
                    JOIN files AS source_file ON source_file.id = r.file_id
                    LEFT JOIN reference_lines AS reference_line ON reference_line.id = r.reference_line_id
                    LEFT JOIN symbols AS s ON s.id = r.target_symbol_id
                    WHERE source_file.path = 'src/Consumer.cs'
                      AND r.reference_kind = 'type_reference'
                      AND r.symbol_name = 'Actual'
                    ORDER BY r.line, r.column_number
                    """;
                using var reader = command.ExecuteReader();
                var actualReferences = new List<(string Context, string? State, string? Kind, string? Signature)>();
                while (reader.Read())
                {
                    actualReferences.Add((
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }

                Assert.Collection(
                    actualReferences,
                    reference =>
                    {
                        Assert.Contains("Actual<string>", reference.Context);
                        Assert.Equal("resolved", reference.State);
                        Assert.Equal("class", reference.Kind);
                        Assert.Contains("Actual<T>", reference.Signature);
                    },
                    reference =>
                    {
                        Assert.Contains("Actual<string, string>", reference.Context);
                        Assert.Equal("resolved", reference.State);
                        Assert.Equal("class", reference.Kind);
                        Assert.Contains("Actual<TFirst, TSecond>", reference.Signature);
                    },
                    reference =>
                    {
                        Assert.Contains("SameLineOne", reference.Context);
                        Assert.Equal("resolved", reference.State);
                        Assert.Equal("class", reference.Kind);
                        Assert.Contains("Actual<T>", reference.Signature);
                    });
                reader.Close();

                command.CommandText = """
                    SELECT r.symbol_name,
                           r.reference_kind,
                           r.resolution_state,
                           s.kind,
                           s.signature
                    FROM symbol_references AS r
                    JOIN files AS source_file ON source_file.id = r.file_id
                    LEFT JOIN symbols AS s ON s.id = r.target_symbol_id
                    WHERE (
                            source_file.path = 'src/CaseUse.cs'
                            AND r.symbol_name = 'NAME'
                          )
                       OR (
                            source_file.path = 'src/Consumer.cs'
                            AND r.symbol_name = 'Handler'
                            AND r.reference_kind = 'type_reference'
                          )
                    ORDER BY source_file.path, r.line, r.column_number
                    """;
                using var compatibilityReader = command.ExecuteReader();
                var compatibilityReferences =
                    new List<(string Name, string Kind, string? State, string? TargetKind, string? Signature)>();
                while (compatibilityReader.Read())
                {
                    compatibilityReferences.Add((
                        compatibilityReader.GetString(0),
                        compatibilityReader.GetString(1),
                        compatibilityReader.IsDBNull(2) ? null : compatibilityReader.GetString(2),
                        compatibilityReader.IsDBNull(3) ? null : compatibilityReader.GetString(3),
                        compatibilityReader.IsDBNull(4) ? null : compatibilityReader.GetString(4)));
                }

                Assert.Collection(
                    compatibilityReferences,
                    reference =>
                    {
                        Assert.Equal("NAME", reference.Name);
                        Assert.Equal("type_reference", reference.Kind);
                        Assert.Equal("resolved", reference.State);
                        Assert.Equal("class", reference.TargetKind);
                        Assert.Contains("class NAME", reference.Signature);
                    },
                    reference =>
                    {
                        Assert.Equal("Handler", reference.Name);
                        Assert.Equal("type_reference", reference.Kind);
                        Assert.Equal("resolved", reference.State);
                        Assert.Equal("delegate", reference.TargetKind);
                        Assert.Contains("Handler<T, TOut>", reference.Signature);
                    });
                compatibilityReader.Close();

                command.CommandText = """
                    SELECT r.resolution_state,
                           s.signature
                    FROM symbol_references AS r
                    JOIN files AS source_file ON source_file.id = r.file_id
                    LEFT JOIN symbols AS s ON s.id = r.target_symbol_id
                    WHERE source_file.path = 'src/Consumer.cs'
                      AND r.reference_kind = 'type_reference'
                      AND r.symbol_name = @name
                    """;
                var nameParameter = command.Parameters.Add("@name", SqliteType.Text);

                nameParameter.Value = "Commented";
                using (var triviaReader = command.ExecuteReader())
                {
                    Assert.True(triviaReader.Read());
                    Assert.Equal("resolved", triviaReader.GetString(0));
                    Assert.Contains(
                        "class Commented <T>",
                        triviaReader.GetString(1));
                }

                nameParameter.Value = "Ordinal";
                using (var ordinalReader = command.ExecuteReader())
                {
                    Assert.True(ordinalReader.Read());
                    Assert.Equal("unresolved", ordinalReader.GetString(0));
                    Assert.True(ordinalReader.IsDBNull(1));
                }

                command.Parameters.Clear();
                command.CommandText = """
                    SELECT source_file.path,
                           r.resolution_state,
                           r.resolution_candidate_count,
                           s.name,
                           target_file.path
                    FROM symbol_references AS r
                    JOIN files AS source_file ON source_file.id = r.file_id
                    LEFT JOIN symbols AS s ON s.id = r.target_symbol_id
                    LEFT JOIN files AS target_file ON target_file.id = s.file_id
                    WHERE r.symbol_name = 'Name'
                      AND source_file.path IN (
                          'src/PartialUse.cs',
                          'src/DerivedUse.cs'
                      )
                    ORDER BY source_file.path
                    """;
                using var propertyReader = command.ExecuteReader();
                Assert.True(propertyReader.Read());
                Assert.Equal("src/DerivedUse.cs", propertyReader.GetString(0));
                Assert.Equal("resolved", propertyReader.GetString(1));
                Assert.Equal(1, propertyReader.GetInt32(2));
                Assert.Equal("Name", propertyReader.GetString(3));
                Assert.Equal("src/BaseState.cs", propertyReader.GetString(4));
                Assert.True(propertyReader.Read());
                Assert.Equal("src/PartialUse.cs", propertyReader.GetString(0));
                Assert.Equal("resolved", propertyReader.GetString(1));
                Assert.Equal(1, propertyReader.GetInt32(2));
                Assert.Equal("Name", propertyReader.GetString(3));
                Assert.Equal("src/PartialState.cs", propertyReader.GetString(4));
                Assert.False(propertyReader.Read());
            }

            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(
                () => QueryCommandRunner.RunInspect(
                    ["Action", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    _jsonOptions));
            using var inspectDocument = ParseJsonOutput(inspectStdout);

            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Empty(inspectDocument.RootElement.GetProperty("references").EnumerateArray());

            var (depsExitCode, depsStdout, depsStderr) = CaptureConsole(
                () => QueryCommandRunner.RunDeps(
                    ["--db", dbPath, "--json", "--lang", "csharp", "--limit", "100"],
                    _jsonOptions));
            using var depsDocument = ParseJsonOutput(depsStdout);
            var dependencyEdges = depsDocument.RootElement.GetProperty("edges").EnumerateArray().ToArray();

            Assert.Equal(CommandExitCodes.Success, depsExitCode);
            Assert.Equal(string.Empty, depsStderr);
            Assert.Contains(
                dependencyEdges,
                edge => edge.GetProperty("source_path").GetString() == "src/Consumer.cs"
                        && edge.GetProperty("target_path").GetString() == "src/Types.cs");
            Assert.DoesNotContain(
                dependencyEdges,
                edge => edge.GetProperty("source_path").GetString() == "src/Consumer.cs"
                        && edge.GetProperty("target_path").GetString() == "src/Impostors.cs");
            Assert.Contains(
                dependencyEdges,
                edge => edge.GetProperty("source_path").GetString() == "src/PartialUse.cs"
                        && edge.GetProperty("target_path").GetString() == "src/PartialState.cs");
            Assert.Contains(
                dependencyEdges,
                edge => edge.GetProperty("source_path").GetString() == "src/DerivedUse.cs"
                        && edge.GetProperty("target_path").GetString() == "src/BaseState.cs");
            Assert.Contains(
                dependencyEdges,
                edge => edge.GetProperty("source_path").GetString() == "src/CaseUse.cs"
                        && edge.GetProperty("target_path").GetString() == "src/Types.cs");
            Assert.DoesNotContain(
                dependencyEdges,
                edge => edge.GetProperty("source_path").GetString() == "src/CaseUse.cs"
                        && edge.GetProperty("target_path").GetString() == "src/CaseProperty.cs");

            using var server = new McpServer(dbPath, ConsoleUi.LoadVersion());
            var referencesRequest = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"references","arguments":{"query":"Action","kind":"type_reference","lang":"csharp","exactName":true}}}""")!;
            var referencesResponse = server.HandleMessage(referencesRequest)!;
            var mcpReference = Assert.Single(
                referencesResponse["result"]!["structuredContent"]!["results"]!.AsArray());

            Assert.Equal("unresolved", mcpReference!["resolutionState"]!.GetValue<string>());
            Assert.Null(mcpReference["targetSymbolId"]);

            var analyzeRequest = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"analyze_symbol","arguments":{"query":"Action","lang":"csharp","exact":true}}}""")!;
            var analyzeResponse = server.HandleMessage(analyzeRequest)!;

            Assert.Empty(
                analyzeResponse["result"]!["structuredContent"]!["references"]!.AsArray());

            var depsRequest = JsonNode.Parse(
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"deps","arguments":{"lang":"csharp","limit":100}}}""")!;
            var depsResponse = server.HandleMessage(depsRequest)!;
            var mcpDependencyEdges =
                depsResponse["result"]!["structuredContent"]!["edges"]!.AsArray();

            Assert.Contains(
                mcpDependencyEdges,
                edge => edge!["sourcePath"]!.GetValue<string>() == "src/Consumer.cs"
                        && edge["targetPath"]!.GetValue<string>() == "src/Types.cs");
            Assert.DoesNotContain(
                mcpDependencyEdges,
                edge => edge!["sourcePath"]!.GetValue<string>() == "src/Consumer.cs"
                        && edge["targetPath"]!.GetValue<string>() == "src/Impostors.cs");
            Assert.Contains(
                mcpDependencyEdges,
                edge => edge!["sourcePath"]!.GetValue<string>() == "src/PartialUse.cs"
                        && edge["targetPath"]!.GetValue<string>() == "src/PartialState.cs");
            Assert.Contains(
                mcpDependencyEdges,
                edge => edge!["sourcePath"]!.GetValue<string>() == "src/DerivedUse.cs"
                        && edge["targetPath"]!.GetValue<string>() == "src/BaseState.cs");
            Assert.Contains(
                mcpDependencyEdges,
                edge => edge!["sourcePath"]!.GetValue<string>() == "src/CaseUse.cs"
                        && edge["targetPath"]!.GetValue<string>() == "src/Types.cs");
            Assert.DoesNotContain(
                mcpDependencyEdges,
                edge => edge!["sourcePath"]!.GetValue<string>() == "src/CaseUse.cs"
                        && edge["targetPath"]!.GetValue<string>() == "src/CaseProperty.cs");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
