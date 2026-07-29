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
                }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var select = db.Connection.CreateCommand();
                select.CommandText = "SELECT id FROM files WHERE path = 'src/Session.cs'";
                var fileId = Convert.ToInt32(select.ExecuteScalar());
                var writer = new DbWriter(db.Connection);
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "TargetA",
                        ReferenceKind = "call",
                        Line = 7,
                        Column = 16,
                        Context = "        return TargetA() + TargetB();",
                        ContainerKind = "function",
                        ContainerName = "Caller",
                    },
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "TargetB",
                        ReferenceKind = "call",
                        Line = 7,
                        Column = 28,
                        Context = "        return TargetA() + TargetB();",
                        ContainerKind = "function",
                        ContainerName = "Caller",
                    },
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "TargetA",
                        ReferenceKind = "call",
                        Line = 11,
                        Column = 16,
                        Context = "        return TargetA();",
                        ContainerKind = "function",
                        ContainerName = "Other",
                    },
                ]);
                writer.MarkGraphReady();
                writer.MarkFoldReady();
            }

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

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = action();
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }
}
