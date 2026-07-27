using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class ProjectionFieldRegistryIssue4836Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Theory]
    [InlineData("search", "path")]
    [InlineData("references", "resolution_state")]
    [InlineData("map", "languages.lang")]
    public void FieldsList_DiscoversCommandSpecificSchemaWithoutRunningQuery_Issue4836(
        string command,
        string expectedField)
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run([command, "--fields", "list"], _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("api_version").GetString());
        Assert.Equal(command, root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("case_sensitive").GetBoolean());
        Assert.Equal("list", root.GetProperty("discovery_value").GetString());
        Assert.Contains(
            root.GetProperty("valid_fields").EnumerateArray(),
            field => field.GetString() == expectedField);
        Assert.All(
            root.GetProperty("fields").EnumerateArray(),
            field => Assert.False(field.GetProperty("deprecated").GetBoolean()));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("references")]
    [InlineData("map")]
    public void UnknownFields_ReturnTypedJsonUsageErrorBeforeQueryExecution_Issue4836(string command)
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run([command, "--fields", "bogus"], _jsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(CommandErrorCodes.UsageError, root.GetProperty("error_code").GetString());
        Assert.Equal("usage", root.GetProperty("category").GetString());
        Assert.Contains("Unknown --fields value 'bogus'", root.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains($"{command} --fields list", root.GetProperty("hint").GetString(), StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("results", out _));
    }

    [Fact]
    public void ProjectionFields_AreCaseSensitiveAndDiscoveryValueMustBeUsedAlone_Issue4836()
    {
        var (caseExitCode, caseStdout, caseStderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                ["references", "--fields", "Path", "--json"],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, caseExitCode);
        Assert.Equal(string.Empty, caseStderr);
        using (var caseDocument = JsonDocument.Parse(caseStdout))
        {
            Assert.Contains(
                "Unknown --fields value 'Path'",
                caseDocument.RootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }

        var (typoExitCode, typoStdout, typoStderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                ["search", "--fields", "paht"],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, typoExitCode);
        Assert.Equal(string.Empty, typoStderr);
        using (var typoDocument = JsonDocument.Parse(typoStdout))
        {
            Assert.Contains(
                "Nearby valid fields: path",
                typoDocument.RootElement.GetProperty("hint").GetString(),
                StringComparison.Ordinal);
        }

        var (listExitCode, listStdout, listStderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                ["search", "--fields", "list,path", "--json"],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, listExitCode);
        Assert.Equal(string.Empty, listStderr);
        using var listDocument = JsonDocument.Parse(listStdout);
        Assert.Contains(
            "must be used by itself",
            listDocument.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidMultipleFieldsAndPathAlias_PreserveProjectionBehavior_Issue4836()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("projection_field_registry_4836");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Alpha.cs",
                "csharp",
                "public sealed class Alpha { public void Run() { } }");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    ["search", "Alpha", "--db", dbPath, "--fields", "file,line", "--json"],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var row = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("src/Alpha.cs", row.GetProperty("file").GetString());
            Assert.True(row.GetProperty("line").GetInt32() > 0);
            Assert.Equal(2, row.EnumerateObject().Count());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RegistryAliasesAndNestedCollections_AreMachineDiscoverable_Issue4836()
    {
        var search = ProjectionFieldRegistry.CreateDiscoveryDocument("search");
        var fileAlias = Assert.Single(
            search["fields"]!.AsArray(),
            item => item!["name"]!.GetValue<string>() == "file");
        Assert.Equal("alias", fileAlias!["kind"]!.GetValue<string>());
        Assert.Equal("path", fileAlias["alias_for"]!.GetValue<string>());

        var map = ProjectionFieldRegistry.CreateDiscoveryDocument("map");
        var nestedAlias = Assert.Single(
            map["fields"]!.AsArray(),
            item => item!["name"]!.GetValue<string>() == "top_files.file");
        Assert.Equal("top_files.path", nestedAlias!["alias_for"]!.GetValue<string>());
        Assert.Equal("top_files", nestedAlias["collection"]!.GetValue<string>());
    }

    [Fact]
    public void EveryDiscoveredProjectionField_ValidatesFromTheSameRegistry_Issue4836()
    {
        foreach (var command in ProjectionFieldRegistry.SupportedCommands)
        {
            var document = ProjectionFieldRegistry.CreateDiscoveryDocument(command);
            var validFields = document["valid_fields"]!.AsArray()
                .Select(field => field!.GetValue<string>())
                .ToArray();

            Assert.NotEmpty(validFields);
            Assert.Equal(validFields.Length, validFields.Distinct(StringComparer.Ordinal).Count());
            foreach (var field in validFields)
                Assert.True(ProjectionFieldRegistry.TryValidate(command, [field], out var error), error?.Message);
        }
    }

    [Theory]
    [InlineData("search", "guard_evidence")]
    [InlineData("search", "next_steps")]
    [InlineData("definition", "body_content_recovery")]
    [InlineData("symbols", "reference_count")]
    [InlineData("symbols", "signature_truncated")]
    [InlineData("hotspots", "symbol_count")]
    [InlineData("hotspots", "definition_site_details")]
    [InlineData("status", "index_matches_workspace")]
    [InlineData("status", "effective_config")]
    [InlineData("status", "update_check")]
    [InlineData("references", "body_content")]
    [InlineData("callers", "aggregate_truncated")]
    [InlineData("callees", "body_content_recovery")]
    [InlineData("impact", "path_details")]
    [InlineData("map", "next_commands")]
    public void ExistingConditionalAndModeSpecificFields_RemainValid_Issue4836(
        string command,
        string field)
    {
        Assert.True(
            ProjectionFieldRegistry.TryValidate(command, [field], out var error),
            error?.Message);

        var discovery = ProjectionFieldRegistry.CreateDiscoveryDocument(command);
        Assert.Contains(
            discovery["valid_fields"]!.AsArray(),
            item => item!.GetValue<string>() == field);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("bogus")]
    public void EarlyProjectionRegistryResponses_HonorMaxJsonBytes_Issue4836(string fields)
    {
        const int maxJsonBytes = 100;
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                ["status", "--fields", fields, "--max-json-bytes", maxJsonBytes.ToString()],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.True(Encoding.UTF8.GetByteCount(stdout) <= maxJsonBytes);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(
            $"--max-json-bytes {maxJsonBytes} is too small",
            stderr,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("references")]
    [InlineData("map")]
    public void CommandHelp_DirectsFieldsUsersToRegistryDiscovery_Issue4836(string command)
    {
        var (printed, stdout, stderr) = ConsoleCapture.Capture(() =>
            ConsoleUi.PrintCommandUsage(command) ? 1 : 0);

        Assert.Equal(1, printed);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("--fields <csv|list>", stdout, StringComparison.Ordinal);
        Assert.Contains("use --fields list for the machine-readable catalog", stdout, StringComparison.Ordinal);
    }
}
