using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_SqlSystemVariablesPersistAcrossFreshFullAndScopedUpdates()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        const string content = """
            CREATE PROCEDURE dbo.SaveOrder
            AS
            BEGIN
                SELECT @@IDENTITY;
                IF @@ROWCOUNT = 0 SELECT @@ERROR;
                SELECT @@session.sql_mode, @@global.max_connections;
            END;
            """;
        try
        {
            var path = Path.Combine(projectRoot, "SampleData.sql");
            for (var pass = 0; pass < 3; pass++)
            {
                File.WriteAllText(path, content + new string('\n', pass));
                string[] arguments = pass == 2
                    ? [projectRoot, "--files", "SampleData.sql", "--db", dbPath, "--json", "--quiet"]
                    : [projectRoot, "--db", dbPath, "--json", "--quiet"];
                var (exitCode, json) = RunAndCaptureJson(arguments);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("success", json.GetProperty("status").GetString());
                Assert.True(json.GetProperty("index_complete").GetBoolean());
                Assert.True(json.GetProperty("reference_graph_complete").GetBoolean());

                using var connection = OpenNonPoolingConnection(dbPath);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT symbol_name, line, column_number
                    FROM symbol_references
                    WHERE reference_kind = 'system_variable'
                    ORDER BY line, column_number
                    """;
                using var reader = command.ExecuteReader();
                var actual = new List<(string Name, int Line, int Column)>();
                while (reader.Read())
                    actual.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
                Assert.Equal(
                    [("@@IDENTITY", 4, 12), ("@@ROWCOUNT", 5, 8), ("@@ERROR", 5, 30),
                     ("@@session.sql_mode", 6, 12), ("@@global.max_connections", 6, 32)],
                    actual);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }
}
