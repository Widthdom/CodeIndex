using CodeIndex.Cli;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerIssue4833Tests
{
    [Fact]
    public void CSharpNamedArgumentLabels_DoNotCreateExactReferencesOrDependencies_Issue4833()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_csharp_named_arguments_issue4833");
        try
        {
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Types.cs",
                """
                namespace Fixture;

                public sealed class ExpressionPayload
                {
                }

                public sealed class PatternHolder
                {
                    public object? Value { get; init; }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Impostor.cs",
                """
                namespace Fixture;

                public sealed class CollisionHolder
                {
                    public bool overwrite { get; } = true;
                    public object? Value { get; }
                }
                """);
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Consumer.cs",
                """
                using System;
                using System.Collections.Generic;
                using System.Linq;

                namespace Fixture;

                public sealed class Consumer
                {
                    public void Run(bool condition, IEnumerable<ExpressionPayload> source)
                    {
                        Sink(1, overwrite: true, payload: typeof(ExpressionPayload));
                        Sink(
                            positional: 2,
                            payload: typeof(ExpressionPayload),
                            overwrite: condition ? true : false);
                        SinkOut(payload: out ExpressionPayload declaredPayload);
                        _ = source.Select(selector: (ExpressionPayload item) => item);
                        _ = source.Select(selector: delegate(ExpressionPayload item) { return item; });
                        SinkQuery(query: from ExpressionPayload item in source select item, other: condition);
                        SinkQuery(
                            query: from ExpressionPayload item in source select item,
                            other: condition);
                        if (declaredPayload is PatternHolder
                            {
                                Value: ExpressionPayload propertyPayload,
                            })
                        {
                            _ = propertyPayload;
                        }
                    }

                    private static void Sink(int positional, bool overwrite, Type payload)
                    {
                    }

                    private static void SinkOut(out ExpressionPayload payload)
                    {
                        payload = new();
                    }

                    private static void SinkQuery(object query, object other)
                    {
                    }
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                JsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (labelExitCode, labelStdout, labelStderr) = CaptureConsole(
                () => QueryCommandRunner.RunReferences(
                    [
                        "overwrite",
                        "--db", dbPath,
                        "--json",
                        "--exact-name",
                        "--kind", "type_reference",
                        "--lang", "csharp",
                    ],
                    JsonOptions));
            using var labelDocument = ParseJsonOutput(labelStdout);

            Assert.Equal(CommandExitCodes.Success, labelExitCode);
            Assert.Equal(string.Empty, labelStderr);
            Assert.Equal(0, labelDocument.RootElement.GetProperty("count").GetInt32());
            Assert.Empty(labelDocument.RootElement.GetProperty("references").EnumerateArray());

            var (payloadExitCode, payloadStdout, payloadStderr) = CaptureConsole(
                () => QueryCommandRunner.RunReferences(
                    [
                        "ExpressionPayload",
                        "--db", dbPath,
                        "--json",
                        "--exact-name",
                        "--kind", "type_reference",
                        "--lang", "csharp",
                    ],
                    JsonOptions));
            using var payloadDocument = ParseJsonOutput(payloadStdout);
            var payloadReference = payloadDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, payloadExitCode);
            Assert.Equal(string.Empty, payloadStderr);
            Assert.Equal("resolved", payloadReference.GetProperty("resolution_state").GetString());
            Assert.Equal("src/Consumer.cs", payloadReference.GetProperty("path").GetString());

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
                    SELECT COUNT(*)
                    FROM symbols
                    WHERE name = 'overwrite'
                      AND kind = 'property'
                    """;
                Assert.Equal(1L, (long)command.ExecuteScalar()!);

                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbol_references AS reference
                    JOIN files AS source_file ON source_file.id = reference.file_id
                    WHERE source_file.path = 'src/Consumer.cs'
                      AND reference.symbol_name = 'overwrite'
                      AND reference.reference_kind = 'type_reference'
                    """;
                Assert.Equal(0L, (long)command.ExecuteScalar()!);

                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbol_references AS reference
                    JOIN files AS source_file ON source_file.id = reference.file_id
                    WHERE source_file.path = 'src/Consumer.cs'
                      AND reference.symbol_name = 'ExpressionPayload'
                      AND reference.reference_kind = 'type_reference'
                    """;
                Assert.Equal(10L, (long)command.ExecuteScalar()!);

                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbol_references AS reference
                    JOIN files AS source_file ON source_file.id = reference.file_id
                    WHERE source_file.path = 'src/Consumer.cs'
                      AND reference.symbol_name IN ('payload', 'selector', 'query', 'other', 'Value')
                      AND reference.reference_kind = 'type_reference'
                    """;
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
            }

            var (depsExitCode, depsStdout, depsStderr) = CaptureConsole(
                () => QueryCommandRunner.RunDeps(
                    ["--db", dbPath, "--json", "--lang", "csharp", "--limit", "100"],
                    JsonOptions));
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
                        && edge.GetProperty("target_path").GetString() == "src/Impostor.cs");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
