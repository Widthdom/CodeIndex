using System.Text.Json;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class CliNonDatabaseErrorContractTests
{
    [Theory]
    [InlineData("empty", CommandExitCodes.UsageError, CommandErrorCodes.UsageError, "usage", "hooks")]
    [InlineData("not_found", CommandExitCodes.NotFound, CommandErrorCodes.FileNotFound, "not_found", "outline")]
    [InlineData("invalid", CommandExitCodes.InvalidArgument, CommandErrorCodes.UsageError, "usage", "doctor")]
    [InlineData("config", CommandExitCodes.UsageError, CommandErrorCodes.ConfigInvalid, "configuration", "validate-config")]
    [InlineData("platform", CommandExitCodes.InstallError, CommandErrorCodes.HookOperationFailed, "platform", "hooks")]
    public void Failures_UseStableEnvelopeInJsonAndCanonicalHumanOutput_Issue4855(
        string scenario,
        int expectedExitCode,
        string expectedErrorCode,
        string expectedCategory,
        string expectedCommand)
    {
        using var scope = new FailureScenarioScope(scenario);

        var jsonResult = scope.Run(json: true);

        Assert.Equal(expectedExitCode, jsonResult.ExitCode);
        Assert.Equal(string.Empty, jsonResult.Stderr);
        using (var document = JsonDocument.Parse(jsonResult.Stdout))
        {
            var root = document.RootElement;
            Assert.Equal("1", root.GetProperty("api_version").GetString());
            Assert.Equal("error", root.GetProperty("status").GetString());
            Assert.Equal(expectedErrorCode, root.GetProperty("error_code").GetString());
            Assert.Equal(expectedCategory, root.GetProperty("category").GetString());
            Assert.Equal(expectedCommand, root.GetProperty("command").GetString());
            Assert.Equal(expectedExitCode, root.GetProperty("exit_code").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("hint").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("usage").GetString()));
        }
        if (scenario == "platform")
            Assert.DoesNotContain(scope.ProjectRoot, jsonResult.Stdout, StringComparison.Ordinal);

        var humanResult = scope.Run(json: false);

        Assert.Equal(expectedExitCode, humanResult.ExitCode);
        Assert.Equal(string.Empty, humanResult.Stdout);
        Assert.Contains($"Error [{expectedErrorCode}]:", humanResult.Stderr, StringComparison.Ordinal);
        Assert.Contains("Hint:", humanResult.Stderr, StringComparison.Ordinal);
        Assert.Contains("Usage:", humanResult.Stderr, StringComparison.Ordinal);
    }

    private sealed class FailureScenarioScope : IDisposable
    {
        private readonly string _scenario;
        private readonly string _previousDirectory;
        private readonly string? _dbPath;

        internal FailureScenarioScope(string scenario)
        {
            _scenario = scenario;
            _previousDirectory = Environment.CurrentDirectory;
            ProjectRoot = TestProjectHelper.CreateTempProject($"non_db_error_{scenario}");
            switch (scenario)
            {
                case "not_found":
                    _dbPath = TestProjectHelper.CreateProjectDb(ProjectRoot);
                    break;
                case "config":
                    File.WriteAllText(Path.Combine(ProjectRoot, CdidxConfigFile.FileName), "{ invalid json");
                    Environment.CurrentDirectory = ProjectRoot;
                    break;
                case "platform":
                    TestProjectHelper.InitializeGitRepo(ProjectRoot);
                    var hooksDir = Path.Combine(ProjectRoot, ".git", "hooks");
                    Directory.CreateDirectory(hooksDir);
                    File.WriteAllText(Path.Combine(hooksDir, "pre-commit"), "#!/bin/sh\necho existing\n");
                    HookCommandRunner.ReplaceFileForTesting = (_, _, _) => throw new IOException("replace denied");
                    break;
            }
        }

        internal string ProjectRoot { get; }

        internal (int ExitCode, string Stdout, string Stderr) Run(bool json)
            => ConsoleCapture.Capture(() => _scenario switch
            {
                "empty" => ProgramRunner.Run(
                    json ? ["hooks", "--json"] : ["hooks"],
                    appVersion: "test"),
                "not_found" => QueryCommandRunner.RunOutline(
                    json
                        ? ["missing.cs", "--db", _dbPath!, "--json"]
                        : ["missing.cs", "--db", _dbPath!],
                    ProgramRunner.CreateDefaultJsonOptions()),
                "invalid" => ProgramRunner.Run(
                    json ? ["doctor", "--json", "--bogus"] : ["doctor", "--bogus"],
                    appVersion: "test"),
                "config" => ProgramRunner.Run(
                    json ? ["validate-config", "--json"] : ["validate-config"],
                    appVersion: "test"),
                "platform" => HookCommandRunner.Run(
                    json
                        ? ["install", "--project", ProjectRoot, "--json"]
                        : ["install", "--project", ProjectRoot],
                    ProgramRunner.CreateDefaultJsonOptions()),
                _ => throw new ArgumentOutOfRangeException(nameof(_scenario), _scenario, null),
            });

        public void Dispose()
        {
            Environment.CurrentDirectory = _previousDirectory;
            HookCommandRunner.ReplaceFileForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(ProjectRoot);
        }
    }
}
