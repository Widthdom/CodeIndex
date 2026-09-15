using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public async Task Run_PythonStringColumnsMatchPersistedCliAndFramedLspRanges_Issue5362()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "lib.py");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var lines = new List<string> { "def 終了():", "    pass", "def 開始():" };
            var expected = new List<(int Line, int Column)>();
            string[] prefixes =
            [
                "    s = '😀'; ", "    ", "    s = ''; ", "    s = 'ascii'; ",
                "    s = '日本語'; ", "    s = '終了() 😀'; ", "    s = \"escaped \\\" # 😀\"; ",
                "    s = r'終了() \\ 😀'; ", "    s = b'bytes'; ", "    s = 'a' '😀'; ",
                "    s = '''終了() 😀'''; ", "    s = f'終了() 😀'; ",
            ];
            foreach (var prefix in prefixes)
            {
                const string between = "終了(); s = '😀'; ";
                lines.Add(prefix + between + "終了() # 終了()");
                expected.Add((lines.Count, prefix.Length + 1));
                expected.Add((lines.Count, prefix.Length + between.Length + 1));
            }
            File.WriteAllText(sourcePath, string.Join('\n', lines) + '\n', new UTF8Encoding(false));

            var (indexExitCode, _) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal((4, 15), expected[0]);

            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT line, column_number, resolution_state, resolution_candidate_count
                    FROM symbol_references WHERE symbol_name = '終了' AND reference_kind = 'call'
                    ORDER BY line, column_number
                    """;
                using var rows = command.ExecuteReader();
                var persisted = new List<(int, int)>();
                while (rows.Read())
                {
                    persisted.Add((rows.GetInt32(0), rows.GetInt32(1)));
                    Assert.Equal("resolved", rows.GetString(2));
                    Assert.Equal(1, rows.GetInt32(3));
                }
                Assert.Equal(expected, persisted);
            }

            var cli = ConsoleCapture.Capture(() => QueryCommandRunner.RunReferences(
                ["終了", "--db", dbPath, "--json", "--kind", "call", "--limit", "100"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, cli.ExitCode);
            var cliPositions = new List<(int, int)>();
            foreach (var row in cli.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var json = JsonDocument.Parse(row);
                if (json.RootElement.TryGetProperty("column", out var column))
                    cliPositions.Add((json.RootElement.GetProperty("line").GetInt32(), column.GetInt32()));
            }
            Assert.Equal(expected, cliPositions.OrderBy(position => position.Item1).ThenBy(position => position.Item2));

            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using var server = new LspServer(new DbReader(db), "1.2.3", ProgramRunner.CreateDefaultJsonOptions(), projectRoot);
            static string Frame(string payload) => $"Content-Length: {Encoding.UTF8.GetByteCount(payload)}\r\n\r\n{payload}";
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/references",
                @params = new
                {
                    textDocument = new { uri = new Uri(sourcePath).AbsoluteUri },
                    position = new { line = 0, character = 4 },
                    context = new { includeDeclaration = false },
                },
            });
            using var output = new MemoryStream();
            using var initializeInput = new MemoryStream(Encoding.UTF8.GetBytes(
                Frame("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""")));
            Assert.Equal(CommandExitCodes.Success,
                await server.RunAsync(initializeInput, output).WaitAsync(TestDeterminism.DefaultTimeout));
            // Wait for initialize to finish before sending requests, as an LSP client must.
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(
                Frame("""{"jsonrpc":"2.0","method":"initialized","params":{}}""") + Frame(request)));
            Assert.Equal(CommandExitCodes.Success,
                await server.RunAsync(input, output).WaitAsync(TestDeterminism.DefaultTimeout));
            output.Position = 0;
            JsonObject? response = null;
            while (LspServer.TryReadMessage(output, out var payload))
            {
                var message = JsonNode.Parse(payload)!.AsObject();
                Assert.True(message["error"] is null, message.ToJsonString());
                if (message["id"]?.GetValue<int>() == 2)
                    response = message;
            }
            Assert.NotNull(response);
            var lspPositions = new List<(int, int)>();
            foreach (var location in response!["result"]!.AsArray())
            {
                Assert.Equal(new Uri(sourcePath).AbsoluteUri, location!["uri"]!.GetValue<string>());
                var range = location["range"]!;
                var line = range["start"]!["line"]!.GetValue<int>();
                var character = range["start"]!["character"]!.GetValue<int>();
                Assert.Equal(line, range["end"]!["line"]!.GetValue<int>());
                Assert.Equal(character + 2, range["end"]!["character"]!.GetValue<int>());
                Assert.Equal("終了", lines[line].Substring(character, 2));
                lspPositions.Add((line + 1, character + 1));
            }
            Assert.Equal(expected, lspPositions.OrderBy(position => position.Item1).ThenBy(position => position.Item2));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }
}
