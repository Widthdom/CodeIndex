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
    [InlineData("status", "workspace_check.unindexed_files_omitted_count")]
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
    public void StatusExplain_StructuredFieldsSupportBoundedProjection_Issue4891()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                [
                    "status",
                    "--explain",
                    "files",
                    "--json",
                    "--fields",
                    "meaning,source,redaction,known_fields_truncated",
                    "--max-json-bytes",
                    "8192"
                ],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var row = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Contains("files", row.GetProperty("meaning").GetString(), StringComparison.Ordinal);
        Assert.Contains("StatusResult", row.GetProperty("source").GetString(), StringComparison.Ordinal);
        var redaction = row.GetProperty("redaction");
        Assert.False(redaction.GetProperty("runtime_values_included").GetBoolean());
        Assert.False(redaction.GetProperty("paths_included").GetBoolean());
        Assert.True(row.GetProperty("known_fields_truncated").GetBoolean());
        Assert.Equal(4, row.EnumerateObject().Count());
        var metadata = root.GetProperty("metadata");
        Assert.Equal(8192, metadata.GetProperty("max_json_bytes").GetInt32());
        foreach (var forbiddenField in new[]
                 {
                     "cdidx_version",
                     "elapsed_ms",
                     "db_path",
                     "indexed_at_head_sha",
                     "result_stable_at",
                 })
        {
            Assert.False(
                metadata.TryGetProperty(forbiddenField, out _),
                $"Bounded status explain metadata must omit runtime field '{forbiddenField}'.");
        }
        Assert.DoesNotContain(Directory.GetCurrentDirectory(), stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusExplain_PrimaryPayloadSurvivesCompactAndBoundedModes_Issue5093()
    {
        var cases = new[]
        {
            new StatusExplainOutputCase(
                "index_complete",
                "index_complete",
                ["--json"],
                Wrapped: false),
            new StatusExplainOutputCase(
                "files",
                "files",
                ["--json", "--compact"],
                Wrapped: true),
            new StatusExplainOutputCase(
                "db_pragma_settings.busy_timeout_ms",
                "db_pragma_settings.busy_timeout_ms",
                ["--format", "compact"],
                Wrapped: true),
            new StatusExplainOutputCase(
                "Index generation completeness",
                "index_complete",
                ["--json", "--max-json-bytes", "50000"],
                Wrapped: true),
        };

        foreach (var testCase in cases)
        {
            var args = new[] { "status", "--explain", testCase.RequestedField }
                .Concat(testCase.OutputArgs)
                .ToArray();
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            var row = testCase.Wrapped
                ? Assert.Single(root.GetProperty("results").EnumerateArray())
                : root;
            Assert.Equal("1", row.GetProperty("api_version").GetString());
            Assert.Equal(testCase.CanonicalField, row.GetProperty("field").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("meaning").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("interpretation").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("remediation").GetString()));

            if (!testCase.Wrapped)
            {
                Assert.True(row.TryGetProperty("source", out _));
                Assert.True(row.TryGetProperty("known_fields", out _));
                continue;
            }

            var metadata = root.GetProperty("metadata");
            Assert.Equal("compact", metadata.GetProperty("explanation_schema").GetString());
            Assert.Equal(
                ["api_version", "field", "meaning", "interpretation", "remediation"],
                metadata.GetProperty("explanation_required_fields")
                    .EnumerateArray()
                    .Select(field => field.GetString())
                    .ToArray());
            var omittedFields = metadata.GetProperty("explanation_omitted_optional_fields")
                .EnumerateArray()
                .Select(field => field.GetString())
                .ToArray();
            Assert.Equal(
                [
                    "label", "scope", "source", "dependencies", "dependencies_truncated",
                    "repair_guidance", "ready", "degraded", "redaction", "known_fields",
                    "known_field_limit", "known_fields_truncated",
                ],
                omittedFields);
            Assert.Equal(
                omittedFields.Length,
                metadata.GetProperty("explanation_omitted_optional_field_count").GetInt32());
        }
    }

    [Fact]
    public void StatusExplain_TerminalContractsRemainExplicitForTextUnknownAndTinyBounds_Issue5093()
    {
        var (textExitCode, textStdout, textStderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                ["status", "--explain", "index_complete"],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, textExitCode);
        Assert.Equal(string.Empty, textStderr);
        Assert.Contains("Index generation completeness (index_complete)", textStdout, StringComparison.Ordinal);
        Assert.Contains("Meaning:", textStdout, StringComparison.Ordinal);
        Assert.Contains("Interpretation:", textStdout, StringComparison.Ordinal);
        Assert.Contains("Remediation:", textStdout, StringComparison.Ordinal);

        var (unknownExitCode, unknownStdout, unknownStderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                ["status", "--explain", "invalid", "--json", "--compact"],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, unknownExitCode);
        Assert.Equal(string.Empty, unknownStderr);
        using (var unknownDocument = JsonDocument.Parse(unknownStdout))
        {
            var root = unknownDocument.RootElement;
            Assert.Empty(root.GetProperty("results").EnumerateArray());
            var error = root.GetProperty("metadata").GetProperty("error");
            Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
            Assert.Contains("unknown status field", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        const int maxJsonBytes = 100;
        var (boundedExitCode, boundedStdout, boundedStderr) = ConsoleCapture.Capture(() =>
            ProgramRunner.Run(
                [
                    "status", "--explain", "index_complete", "--json",
                    "--max-json-bytes", maxJsonBytes.ToString(),
                ],
                _jsonOptions,
                "1.0.0-test"));

        Assert.Equal(CommandExitCodes.UsageError, boundedExitCode);
        Assert.Equal(string.Empty, boundedStderr);
        using var boundedDocument = JsonDocument.Parse(boundedStdout);
        var boundedError = boundedDocument.RootElement;
        Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, boundedError.GetProperty("error_code").GetString());
        Assert.Equal(maxJsonBytes, boundedError.GetProperty("requested_bytes").GetInt32());
        Assert.True(boundedError.GetProperty("minimum_required_bytes_known").GetBoolean());
        Assert.True(boundedError.GetProperty("minimum_required_bytes").GetInt64() > maxJsonBytes);
        Assert.Equal(
            "increase_max_json_bytes",
            boundedError.GetProperty("retry").GetProperty("action").GetString());
    }

    private sealed record StatusExplainOutputCase(
        string RequestedField,
        string CanonicalField,
        string[] OutputArgs,
        bool Wrapped);

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
    [InlineData("status", "scope")]
    [InlineData("status", "meaning")]
    [InlineData("status", "source")]
    [InlineData("status", "dependencies")]
    [InlineData("status", "dependencies_truncated")]
    [InlineData("status", "interpretation")]
    [InlineData("status", "repair_guidance")]
    [InlineData("status", "redaction")]
    [InlineData("status", "known_field_limit")]
    [InlineData("status", "known_fields_truncated")]
    [InlineData("references", "body_content")]
    [InlineData("callers", "aggregate_truncated")]
    [InlineData("callers", "first_column")]
    [InlineData("callees", "body_content_recovery")]
    [InlineData("callees", "first_column")]
    [InlineData("callees", "first_length")]
    [InlineData("impact", "path_details")]
    [InlineData("map", "language_count")]
    [InlineData("map", "module_count")]
    [InlineData("map", "entrypoint_count")]
    [InlineData("map", "summary_only")]
    [InlineData("map", "sections")]
    [InlineData("map", "output_byte_limit")]
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
    [InlineData("definition", "container_qualified_name")]
    [InlineData("definition", "family_key")]
    [InlineData("definition", "is_metadata_target")]
    [InlineData("definition", "metadata_target_source")]
    [InlineData("definition", "same_line_signature_occurrence_index")]
    [InlineData("definition", "reference_count")]
    [InlineData("symbols", "container_qualified_name")]
    [InlineData("symbols", "family_key")]
    [InlineData("symbols", "is_metadata_target")]
    [InlineData("symbols", "metadata_target_source")]
    [InlineData("symbols", "same_line_signature_occurrence_index")]
    [InlineData("callees", "has_self_reference")]
    [InlineData("callees", "has_mutual_recursion")]
    public void NonOutputFields_AreNotAdvertisedOrAccepted_Issue4836(
        string command,
        string field)
    {
        Assert.False(ProjectionFieldRegistry.TryValidate(command, [field], out var error));
        Assert.NotNull(error);

        var discovery = ProjectionFieldRegistry.CreateDiscoveryDocument(command);
        Assert.DoesNotContain(
            discovery["valid_fields"]!.AsArray(),
            item => item!.GetValue<string>() == field);
    }

    [Fact]
    public void CallGraphCompactDefaults_IncludeCallSiteColumns_Issue4836_Issue4841()
    {
        Assert.Contains("column", ProjectionFieldRegistry.GetCompactFields("callers")!);
        Assert.Contains("column", ProjectionFieldRegistry.GetCompactFields("callees")!);
    }

    [Fact]
    public void MapCollectionCounts_ArePopulatedWhenProjected_Issue4836()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("projection_map_counts_4836");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Alpha.cs",
                "csharp",
                "public sealed class Alpha { public static void Main() { } }");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    [
                        "map", "--db", dbPath, "--fields",
                        "language_count,module_count,entrypoint_count", "--json",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var row = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
            Assert.True(row.TryGetProperty("language_count", out _));
            Assert.True(row.TryGetProperty("module_count", out _));
            Assert.True(row.TryGetProperty("entrypoint_count", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement;
        Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
        Assert.Equal("response_budget", error.GetProperty("category").GetString());
        Assert.Equal("status", error.GetProperty("command").GetString());
        Assert.Equal(maxJsonBytes, error.GetProperty("requested_bytes").GetInt64());
        Assert.Equal(maxJsonBytes, error.GetProperty("effective_bytes").GetInt64());
        Assert.True(error.GetProperty("minimum_required_bytes_known").GetBoolean());
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
