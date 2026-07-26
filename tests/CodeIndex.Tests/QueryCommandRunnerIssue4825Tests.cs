using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Mcp;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
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
                    });
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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
