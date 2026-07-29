using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class JsonEnvelopeWrapperIssue4882Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void GraphBodySnippetProjection_PreservesCountAndPagination_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_body_snippet_4882");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);

            foreach (var (command, query) in new[]
            {
                ("references", "TargetA"),
                ("callers", "TargetA"),
                ("callees", "Caller"),
            })
            {
                var firstArgs = new[]
                {
                    command, query, "--db", dbPath, "--json", "--body", "--snippet-lines", "3",
                    "--fields", "path,line,body_content", "--limit", "1", "--max-json-bytes", "8192",
                    "--exact",
                };
                var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(
                    () => ProgramRunner.Run(firstArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, firstExitCode);
                Assert.Equal(string.Empty, firstStderr);
                using var firstDocument = JsonDocument.Parse(firstStdout);
                var firstMetadata = firstDocument.RootElement.GetProperty("metadata");
                Assert.True(firstMetadata.TryGetProperty("total_count_authoritative", out _));
                Assert.Equal(2, firstMetadata.GetProperty("total_count").GetInt32());
                Assert.True(firstMetadata.GetProperty("has_more").GetBoolean());
                var cursor = Assert.IsType<string>(firstMetadata.GetProperty("next_cursor").GetString());
                Assert.False(string.IsNullOrWhiteSpace(
                    firstDocument.RootElement.GetProperty("results")[0].GetProperty("body_content").GetString()));

                var secondArgs = firstArgs.Concat(["--cursor", cursor]).ToArray();
                var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(
                    () => ProgramRunner.Run(secondArgs, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, secondExitCode);
                Assert.Equal(string.Empty, secondStderr);
                using var secondDocument = JsonDocument.Parse(secondStdout);
                var secondMetadata = secondDocument.RootElement.GetProperty("metadata");
                Assert.Equal(1, secondMetadata.GetProperty("cursor_offset").GetInt32());
                Assert.Equal(2, secondMetadata.GetProperty("total_count").GetInt32());
                Assert.False(secondMetadata.GetProperty("has_more").GetBoolean());
                Assert.Equal(JsonValueKind.Null, secondMetadata.GetProperty("next_cursor").ValueKind);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BoundedGraphCountReplay_PreservesVerbatimSnippetLikeQueries_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_verbatim_snippet_query_4882");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            foreach (var command in new[] { "references", "callers", "callees" })
            {
                var queryForms = new[]
                {
                    new[] { "--query", "--snippet-lines" },
                    new[] { "--", "--snippet-lines" },
                    new[] { "--query=--snippet-lines" },
                };
                foreach (var queryForm in queryForms)
                {
                    var args = new[] { command, "--db", dbPath, "--json", "--fields", "path,line", "--limit", "1", "--max-json-bytes", "8192" }
                        .Concat(queryForm)
                        .ToArray();
                    var (exitCode, stdout, stderr) = CaptureConsole(
                        () => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.Success, exitCode);
                    Assert.Equal(string.Empty, stderr);
                    using var document = JsonDocument.Parse(stdout);
                    var metadata = document.RootElement.GetProperty("metadata");
                    Assert.Equal("--snippet-lines", metadata.GetProperty("query_normalized").GetString());
                    Assert.Equal(2, metadata.GetProperty("total_count").GetInt32());
                    Assert.True(metadata.GetProperty("has_more").GetBoolean());
                    Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("next_cursor").GetString()));
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CompactGraphSnippetValidation_UsesOriginalArgsBeforeDatabase_Issue4882()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("bounded_graph_compact_preflight_4882");
        try
        {
            var missingDbPath = Path.Combine(projectRoot, "missing.db");
            foreach (var command in new[] { "references", "callers", "callees" })
            {
                foreach (var compactArgs in new[]
                {
                    new[] { "--compact" },
                    new[] { "--format", "compact" },
                })
                {
                    var args = new[]
                        {
                            command, "Target", "--db", missingDbPath, "--body", "--snippet-lines", "3",
                        }
                        .Concat(compactArgs)
                        .ToArray();
                    var (exitCode, stdout, stderr) = CaptureConsole(
                        () => ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                    Assert.Equal(CommandExitCodes.UsageError, exitCode);
                    Assert.Equal(string.Empty, stdout);
                    Assert.Contains("--snippet-lines with --body requires text or JSON result output", stderr);
                    Assert.DoesNotContain("DB_NOT_FOUND", stderr, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("database", stderr, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateGraphFixture(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Session.cs",
            "csharp",
            """
            class Session
            {
                int TargetA() => 1;
                int TargetB() => 2;
                int Caller()
                {
                    return TargetA() + TargetB();
                }
                int Other()
                {
                    return TargetA();
                }
                int LiteralCallerOne() => 1;
                int LiteralCallerTwo() => 2;
            }
            """);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        using var select = db.Connection.CreateCommand();
        select.CommandText = "SELECT id FROM files WHERE path = 'src/Session.cs'";
        var fileId = Convert.ToInt32(select.ExecuteScalar());
        var writer = new DbWriter(db.Connection);
        writer.InsertReferences([
            CreateReference(fileId, "TargetA", 7, 16, "Caller"),
            CreateReference(fileId, "TargetB", 7, 28, "Caller"),
            CreateReference(fileId, "TargetA", 11, 16, "Other"),
            CreateReference(fileId, "--snippet-lines", 13, 35, "LiteralCallerOne"),
            CreateReference(fileId, "--snippet-lines", 14, 35, "LiteralCallerTwo"),
            CreateReference(fileId, "LiteralTargetOne", 7, 16, "--snippet-lines"),
            CreateReference(fileId, "LiteralTargetTwo", 7, 28, "--snippet-lines"),
        ]);
        writer.MarkGraphReady();
        writer.MarkFoldReady();
        return dbPath;
    }

    private static ReferenceRecord CreateReference(
        int fileId,
        string symbolName,
        int line,
        int column,
        string containerName)
        => new()
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "call",
            Line = line,
            Column = column,
            Context = $"        return {symbolName}();",
            ContainerKind = "function",
            ContainerName = containerName,
        };

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = action();
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }
}
