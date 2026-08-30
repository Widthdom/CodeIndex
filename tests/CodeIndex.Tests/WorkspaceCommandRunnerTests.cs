using CodeIndex.Cli;
using CodeIndex.Database;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class WorkspaceCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    private void IndexProject(string projectRoot)
    {
        var (exitCode, _, stderr) = ConsoleCapture.Capture(
            () => IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
    }

    [Fact]
    public void WorkspaceList_ReadsManifestMembers()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest");
        var root = project.Root;
        Directory.CreateDirectory(Path.Combine(root, "src", "A"));
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
            {
              "members": ["src/A"],
              "index_strategy": "per_member",
              "default_db_name": "index.db"
            }
            """);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.Combine(root, "src", "A");
            var (exitCode, stdout, _) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["list", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("cdidx.workspace.json", stdout);
            Assert.Contains("index.db", stdout);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceStatusJson_ReportsBoundedMemberIndexHealth_Issue4726AndIssue5224()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_health");
        var root = project.Root;
        var readyRoot = Path.Combine(root, "members", "ready");
        var staleRoot = Path.Combine(root, "members", "stale");
        var futureRoot = Path.Combine(root, "members", "future");
        var missingDbRoot = Path.Combine(root, "members", "missing-db");
        Directory.CreateDirectory(readyRoot);
        Directory.CreateDirectory(staleRoot);
        Directory.CreateDirectory(futureRoot);
        Directory.CreateDirectory(missingDbRoot);

        const string IndexedContent = "class App {}\n";
        File.WriteAllText(Path.Combine(readyRoot, "App.cs"), IndexedContent);
        var readyDb = TestProjectHelper.CreateProjectDb(readyRoot);
        TestProjectHelper.InsertIndexedFile(readyDb, "App.cs", "csharp", IndexedContent);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, readyDb))
        {
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(DbContext.SymbolKindFilterMetaKey, "include=class;exclude=");
            using var command = db.Connection.CreateCommand();
            command.CommandText = $"UPDATE files SET {DbContext.SymbolsDroppedByKindFilterColumn} = 7";
            command.ExecuteNonQuery();
        }

        File.WriteAllText(Path.Combine(staleRoot, "App.cs"), IndexedContent);
        var staleDb = TestProjectHelper.CreateProjectDb(staleRoot);
        TestProjectHelper.InsertIndexedFile(staleDb, "App.cs", "csharp", IndexedContent);
        File.WriteAllText(Path.Combine(staleRoot, "App.cs"), "class App { void Changed() {} }\n");

        var futureDb = TestProjectHelper.CreateProjectDb(futureRoot);
        using (var db = new DbContext(DbOpenIntent.WriteIndex, futureDb))
        {
            var writer = new DbWriter(db.Connection);
            writer.SetMeta(
                DbContext.GetMetadataTargetVersionMetaKey("csharp"),
                (DbContext.MetadataTargetVersion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
            {
              "members": [
                "members/ready",
                "members/stale",
                "members/future",
                "members/missing-db"
              ]
            }
            """);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var runtimeRoot = Environment.CurrentDirectory;
            var runtimeReadyRoot = Path.Combine(runtimeRoot, "members", "ready");
            var runtimeStaleRoot = Path.Combine(runtimeRoot, "members", "stale");
            var runtimeFutureRoot = Path.Combine(runtimeRoot, "members", "future");
            var runtimeMissingDbRoot = Path.Combine(runtimeRoot, "members", "missing-db");
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.GetProperty("check_mode").GetBoolean());
            var healthSummary = payload.GetProperty("member_health_summary");
            Assert.Equal(4, healthSummary.GetProperty("member_count").GetInt32());
            Assert.Equal(3, healthSummary.GetProperty("database_probe_count").GetInt32());
            Assert.Equal(
                WorkspaceCommandRunner.MaxMemberHealthDatabaseProbes,
                healthSummary.GetProperty("database_probe_limit").GetInt32());
            Assert.Equal(0, healthSummary.GetProperty("probe_limit_skipped_member_count").GetInt32());
            Assert.False(healthSummary.GetProperty("truncated").GetBoolean());
            Assert.Equal(0, healthSummary.GetProperty("healthy_member_count").GetInt32());
            Assert.Equal(3, healthSummary.GetProperty("degraded_member_count").GetInt32());
            Assert.Equal(1, healthSummary.GetProperty("missing_member_count").GetInt32());
            Assert.Equal("missing", healthSummary.GetProperty("status").GetString());
            Assert.Equal("required_member_missing", healthSummary.GetProperty("reason").GetString());
            Assert.Equal(CommandExitCodes.NotFound, healthSummary.GetProperty("check_exit_code").GetInt32());

            var members = payload.GetProperty("members").EnumerateArray().ToArray();
            var readyMember = members.Single(member => PathCasing.PathsEqual(member.GetProperty("path").GetString()!, runtimeReadyRoot));
            Assert.True(readyMember.GetProperty("exists").GetBoolean());
            Assert.True(readyMember.GetProperty("project_exists").GetBoolean());
            Assert.True(readyMember.GetProperty("db_exists").GetBoolean());
            var ready = readyMember.GetProperty("index_health");
            Assert.True(ready.GetProperty("db_exists").GetBoolean());
            Assert.True(ready.GetProperty("probed").GetBoolean());
            Assert.True(ready.GetProperty("schema_compatible").GetBoolean());
            Assert.True(ready.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("matched", ready.GetProperty("freshness_reason").GetString());
            Assert.Equal("rebuild_member_index", ready.GetProperty("repair_action").GetProperty("action").GetString());
            var graphTableAvailable = ready.GetProperty("graph_table_available").GetBoolean();
            var graphDataCurrent = ready.GetProperty("graph_data_current").GetBoolean();
            var referenceGraphComplete = ready.GetProperty("reference_graph_complete").GetBoolean();
            var indexComplete = ready.GetProperty("index_complete").GetBoolean();
            Assert.False(indexComplete);
            Assert.Equal("index_incomplete", ready.GetProperty("reason").GetString());
            Assert.Contains(
                ready.GetProperty("index_incomplete_reasons").EnumerateArray(),
                reason => reason.GetString() == DbReader.SymbolKindFilterCoverageLimitedReason);
            Assert.True(ready.GetProperty("symbol_kind_filter_provenance_available").GetBoolean());
            Assert.Equal(
                "class",
                ready.GetProperty("symbol_kind_filter").GetProperty("include")[0].GetString());
            Assert.Equal(7, ready.GetProperty("symbols_dropped_by_kind_filter").GetInt64());
            Assert.Equal(
                graphTableAvailable && graphDataCurrent && referenceGraphComplete && indexComplete,
                ready.GetProperty("graph_ready").GetBoolean());

            var stale = members.Single(member => PathCasing.PathsEqual(member.GetProperty("path").GetString()!, runtimeStaleRoot))
                .GetProperty("index_health");
            Assert.Equal("stale", stale.GetProperty("status").GetString());
            Assert.False(stale.GetProperty("index_matches_workspace").GetBoolean());
            Assert.Equal("changed_files", stale.GetProperty("freshness_reason").GetString());

            var future = members.Single(member => PathCasing.PathsEqual(member.GetProperty("path").GetString()!, runtimeFutureRoot))
                .GetProperty("index_health");
            Assert.Equal("incompatible", future.GetProperty("status").GetString());
            Assert.Equal("index_newer_than_reader", future.GetProperty("reason").GetString());
            Assert.False(future.GetProperty("schema_compatible").GetBoolean());
            Assert.True(future.GetProperty("index_newer_than_reader").GetBoolean());
            Assert.Equal("upgrade_cdidx", future.GetProperty("repair_action").GetProperty("action").GetString());

            var missingDbMember = members.Single(member => PathCasing.PathsEqual(member.GetProperty("path").GetString()!, runtimeMissingDbRoot));
            Assert.True(missingDbMember.GetProperty("project_exists").GetBoolean());
            Assert.False(missingDbMember.GetProperty("db_exists").GetBoolean());
            var missingDb = missingDbMember.GetProperty("index_health");
            Assert.Equal("missing", missingDb.GetProperty("status").GetString());
            Assert.Equal("database_not_found", missingDb.GetProperty("reason").GetString());
            Assert.False(missingDb.GetProperty("db_exists").GetBoolean());
            Assert.False(missingDb.GetProperty("probed").GetBoolean());
            Assert.Equal("index_member", missingDb.GetProperty("repair_action").GetProperty("action").GetString());

            var (checkExitCode, checkStdout, checkStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, checkExitCode);
            Assert.Empty(checkStderr);
            using var checkDocument = JsonDocument.Parse(checkStdout);
            Assert.True(checkDocument.RootElement.GetProperty("check_mode").GetBoolean());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceStatusCheck_UsesStableAggregateExitPolicy_Issue4885()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_check");
        var root = project.Root;
        var healthyRoot = Path.Combine(root, "members", "project with spaces");
        var degradedRoot = Path.Combine(root, "members", "degraded");
        var missingRoot = Path.Combine(root, "members", "missing");
        Directory.CreateDirectory(healthyRoot);
        Directory.CreateDirectory(degradedRoot);
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;

            var (informationalExitCode, _, informationalStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, informationalExitCode);
            Assert.Empty(informationalStderr);
            var (noManifestExitCode, noManifestStdout, noManifestStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, noManifestExitCode);
            Assert.Empty(noManifestStderr);
            using (var noManifestDocument = JsonDocument.Parse(noManifestStdout))
            {
                var summary = noManifestDocument.RootElement.GetProperty("member_health_summary");
                Assert.Equal("missing", summary.GetProperty("status").GetString());
                Assert.Equal("workspace_manifest_not_found", summary.GetProperty("reason").GetString());
            }

            File.WriteAllText(manifestPath, """{ "members": [] }""");
            var (emptyExitCode, emptyStdout, emptyStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, emptyExitCode);
            Assert.Empty(emptyStderr);
            using (var emptyDocument = JsonDocument.Parse(emptyStdout))
            {
                var summary = emptyDocument.RootElement.GetProperty("member_health_summary");
                Assert.Equal("empty", summary.GetProperty("status").GetString());
                Assert.Equal("workspace_has_no_members", summary.GetProperty("reason").GetString());
                Assert.Equal(["add_workspace_members"], summary.GetProperty("recommended_actions").EnumerateArray().Select(item => item.GetString()));
            }

            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new { members = new[] { "members/missing" } }));
            var (missingProjectExitCode, missingProjectStdout, _) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, missingProjectExitCode);
            using (var missingProjectDocument = JsonDocument.Parse(missingProjectStdout))
            {
                var member = missingProjectDocument.RootElement.GetProperty("members")[0];
                Assert.False(member.GetProperty("project_exists").GetBoolean());
                Assert.False(member.GetProperty("db_exists").GetBoolean());
                Assert.Equal(
                    "create_project_directory",
                    member.GetProperty("index_health").GetProperty("repair_action").GetProperty("action").GetString());
            }

            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    members = new[]
                    {
                        "members/project with spaces",
                        "members/degraded",
                    },
                }));
            var (missingDatabaseExitCode, missingDatabaseStdout, _) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, missingDatabaseExitCode);
            using (var missingDatabaseDocument = JsonDocument.Parse(missingDatabaseStdout))
            {
                var members = missingDatabaseDocument.RootElement.GetProperty("members").EnumerateArray().ToArray();
                Assert.All(members, member => Assert.False(member.GetProperty("db_exists").GetBoolean()));
                var repairCommand = members[0]
                    .GetProperty("index_health")
                    .GetProperty("repair_action")
                    .GetProperty("command");
                Assert.Equal("cdidx", repairCommand.GetProperty("name").GetString());
                var memberPath = members[0].GetProperty("path").GetString();
                Assert.Contains(
                    repairCommand.GetProperty("args").EnumerateArray().Select(item => item.GetString()),
                    argument => argument == memberPath);
            }

            const string IndexedContent = "class App {}\n";
            File.WriteAllText(Path.Combine(healthyRoot, "App.cs"), IndexedContent);
            IndexProject(healthyRoot);
            File.WriteAllText(Path.Combine(degradedRoot, "App.cs"), IndexedContent);
            IndexProject(degradedRoot);
            File.WriteAllText(Path.Combine(degradedRoot, "App.cs"), "class App { void Changed() {} }\n");

            var (degradedExitCode, degradedStdout, degradedStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.StaleIndex, degradedExitCode);
            Assert.Empty(degradedStderr);
            using (var degradedDocument = JsonDocument.Parse(degradedStdout))
            {
                var summary = degradedDocument.RootElement.GetProperty("member_health_summary");
                Assert.Equal("degraded", summary.GetProperty("status").GetString());
                Assert.Equal(1, summary.GetProperty("healthy_member_count").GetInt32());
                Assert.Equal(1, summary.GetProperty("degraded_member_count").GetInt32());
                Assert.Equal(0, summary.GetProperty("missing_member_count").GetInt32());
            }

            File.WriteAllText(Path.Combine(degradedRoot, "App.cs"), IndexedContent);
            var (healthyExitCode, healthyStdout, healthyStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, healthyExitCode);
            Assert.Empty(healthyStderr);
            using var healthyDocument = JsonDocument.Parse(healthyStdout);
            var healthySummary = healthyDocument.RootElement.GetProperty("member_health_summary");
            Assert.Equal("healthy", healthySummary.GetProperty("status").GetString());
            Assert.Equal("all_members_ready", healthySummary.GetProperty("reason").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceStatusJson_SingleStrategyReusesSharedDatabaseProbe_Issue4885()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_shared_db");
        var root = project.Root;
        Directory.CreateDirectory(Path.Combine(root, "members", "a"));
        Directory.CreateDirectory(Path.Combine(root, "members", "b"));
        const string ManifestContent = """
            {
              "members": ["members/a", "members/b"],
              "index_strategy": "single"
            }
            """;
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), ManifestContent);
        IndexProject(root);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var summary = document.RootElement.GetProperty("member_health_summary");
            Assert.Equal(1, summary.GetProperty("database_probe_count").GetInt32());
            Assert.Equal(2, summary.GetProperty("healthy_member_count").GetInt32());
            var members = document.RootElement.GetProperty("members").EnumerateArray().ToArray();
            Assert.All(members, member => Assert.True(member.GetProperty("db_exists").GetBoolean()));
            Assert.All(
                members,
                member => Assert.Equal("ready", member.GetProperty("index_health").GetProperty("status").GetString()));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceStatusCheckHuman_ReportsUnambiguousExistenceAndAggregate_Issue4885()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_human");
        var root = project.Root;
        Directory.CreateDirectory(Path.Combine(root, "member with spaces"));
        File.WriteAllText(
            Path.Combine(root, "cdidx.workspace.json"),
            """{ "members": ["member with spaces"] }""");

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--check"], _jsonOptions));

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Empty(stderr);
            Assert.Contains("project present; database missing", stdout);
            Assert.Contains("reason=database_not_found; action=index_member", stdout);
            Assert.Contains("Workspace health: missing", stdout);
            Assert.Contains($"check_exit_code={CommandExitCodes.NotFound}", stdout);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceStatusJson_StopsDatabaseProbesAtMemberHealthLimit_Issue4726()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_health_limit");
        var root = project.Root;
        var memberNames = Enumerable.Range(0, WorkspaceCommandRunner.MaxMemberHealthDatabaseProbes + 1)
            .Select(i => $"members/member-{i:D3}")
            .ToArray();
        foreach (var memberName in memberNames)
        {
            var memberRoot = Path.Combine(root, memberName);
            var dataDirectory = Path.Combine(memberRoot, ".cdidx");
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllBytes(Path.Combine(dataDirectory, "codeindex.db"), []);
        }

        File.WriteAllText(
            Path.Combine(root, "cdidx.workspace.json"),
            JsonSerializer.Serialize(new { members = memberNames }));

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            var healthSummary = payload.GetProperty("member_health_summary");
            Assert.Equal(
                WorkspaceCommandRunner.MaxMemberHealthDatabaseProbes,
                healthSummary.GetProperty("database_probe_count").GetInt32());
            Assert.Equal(1, healthSummary.GetProperty("probe_limit_skipped_member_count").GetInt32());
            Assert.True(healthSummary.GetProperty("truncated").GetBoolean());

            var lastHealth = payload.GetProperty("members").EnumerateArray().Last().GetProperty("index_health");
            Assert.Equal("not_checked", lastHealth.GetProperty("status").GetString());
            Assert.Equal("database_probe_limit_reached", lastHealth.GetProperty("reason").GetString());
            Assert.False(lastHealth.GetProperty("probed").GetBoolean());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceStatus_PropagatesCancellationToMemberHealthScan_Issue4726()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_cancel");
        var root = project.Root;
        Directory.CreateDirectory(Path.Combine(root, "member"));
        File.WriteAllText(
            Path.Combine(root, "cdidx.workspace.json"),
            """{ "members": ["member"] }""");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(
                () => ProgramRunner.Run(
                    ["workspace", "status", "--json"],
                    _jsonOptions,
                    cancellationToken: cancellation.Token));

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Contains("cancelled", stderr);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void CheckedInWorkspaceManifest_ResolvesExistingProjectMembers_Issue4476()
    {
        var repositoryRoot = RepositoryTestPaths.Root;
        var manifest = WorkspaceManifestLoader.Load(RepositoryTestPaths.Combine("cdidx.workspace.json"));

        Assert.Equal(repositoryRoot, manifest.Root);
        Assert.Collection(
            manifest.Members,
            member =>
            {
                Assert.Equal(RepositoryTestPaths.Combine("src", "CodeIndex"), member.Path);
                Assert.True(member.Exists);
            },
            member =>
            {
                Assert.Equal(RepositoryTestPaths.Combine("tests", "CodeIndex.Tests"), member.Path);
                Assert.True(member.Exists);
            });
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("status", false)]
    [InlineData("status", true)]
    public void WorkspaceListJson_InvalidMemberShape_ReturnsStructuredError_Issue4359(
        string command,
        bool check)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_invalid_shape");
        var root = project.Root;
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
            {
              "members": [
                { "name": "member-a", "path": "member-a" },
                { "name": "member-b", "path": "member-b" }
              ]
            }
            """);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var commandArgs = check
                ? new[] { command, "--check", "--json" }
                : [command, "--json"];
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(commandArgs, _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stderr);

            using var document = JsonDocument.Parse(stdout);
            var rootElement = document.RootElement;
            Assert.Equal("error", rootElement.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.WorkspaceManifestInvalid, rootElement.GetProperty("error_code").GetString());
            Assert.Equal("workspace_manifest_invalid", rootElement.GetProperty("category").GetString());
            Assert.Contains("members[0] must be a string", rootElement.GetProperty("message").GetString());
            Assert.Contains("members` must be an array of relative path strings", rootElement.GetProperty("hint").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsOversizedManifest()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_oversized");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, new string('x', WorkspaceManifestLoader.MaxManifestBytes + 1));

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains($"{WorkspaceManifestLoader.MaxManifestBytes} byte limit", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsDeeplyNestedManifest()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_depth");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var nestedPrefix = string.Concat(Enumerable.Repeat("{\"nested\":", WorkspaceManifestLoader.MaxManifestDepth + 1));
        File.WriteAllText(manifestPath, nestedPrefix + "0" + new string('}', WorkspaceManifestLoader.MaxManifestDepth + 1));

        Assert.ThrowsAny<JsonException>(() => WorkspaceManifestLoader.Load(manifestPath));
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsTooManyMembers()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_members");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var members = string.Join(",", Enumerable.Range(0, WorkspaceManifestLoader.MaxManifestMembers + 1).Select(i => $"\"src{i}\""));
        File.WriteAllText(manifestPath, $$"""
            {
              "members": [{{members}}]
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains($"{WorkspaceManifestLoader.MaxManifestMembers} member limit", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsOverlongMemberPath()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_member_length");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var member = new string('a', WorkspaceManifestLoader.MaxManifestMemberPathChars + 1);
        File.WriteAllText(manifestPath, $$"""
            {
              "members": [{{JsonSerializer.Serialize(member)}}]
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains($"{WorkspaceManifestLoader.MaxManifestMemberPathChars} character limit", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsInvalidMemberEntriesWithBoundedDiagnostics_Issue3429()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_invalid_members");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var longMember = new string('a', WorkspaceManifestLoader.MaxManifestMemberPathChars + 1);
        File.WriteAllText(manifestPath, $$"""
            {
              "members": [
                "src/A",
                "",
                42,
                true,
                "   ",
                {{JsonSerializer.Serialize(longMember)}}
              ]
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("members contain invalid entries", ex.Message);
        Assert.Contains("members[1]", ex.Message);
        Assert.Contains("members[2]", ex.Message);
        Assert.Contains("members[3]", ex.Message);
        Assert.Contains("members[4]", ex.Message);
        Assert.Contains("members[5]", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_NormalizesAndDedupesMembers_Issue3429()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_dedupe");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, """
            {
              "members": [
                "src/A",
                "src/./A/",
                "src/B/"
              ]
            }
            """);

        var manifest = WorkspaceManifestLoader.Load(manifestPath);

        Assert.Equal(2, manifest.Members.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "A")), manifest.Members[0].Path);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "B")), manifest.Members[1].Path);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsRootedMemberPath()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_rooted_member");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var absoluteMember = Path.Combine(root, "src", "A");
        File.WriteAllText(manifestPath, $$"""
            {
              "members": [{{JsonSerializer.Serialize(absoluteMember)}}]
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("member path must be relative", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsEscapingMemberPath()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_escaping_member");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, """
            {
              "members": ["../outside"]
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("member path escapes the manifest root", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_UsesPathCasingForContainment_Issue3429()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_case");
        var root = project.Root;
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var alternateRootName = SwapAsciiCase(Path.GetFileName(root));
            Assert.NotEqual(Path.GetFileName(root), alternateRootName);
            var member = Path.Combine("..", alternateRootName, "src");
            File.WriteAllText(manifestPath, $$"""
                {
                  "members": [{{JsonSerializer.Serialize(member)}}]
                }
                """);

            lock (PathCasingTestLock.Gate)
            {
                PathCasing.ResetCacheForTests();
                PathCasing.SeedFromWorkspace(root, ignoreCase: false);

                var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

                Assert.Contains("member path escapes the manifest root", ex.Message);
            }
        }
        finally
        {
            lock (PathCasingTestLock.Gate)
                PathCasing.ResetCacheForTests();
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsOverlongDefaultDbName()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_db_name_length");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var dbName = new string('a', WorkspaceManifestLoader.MaxDefaultDbNameChars + 1);
        File.WriteAllText(manifestPath, $$"""
            {
              "default_db_name": {{JsonSerializer.Serialize(dbName)}}
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains($"{WorkspaceManifestLoader.MaxDefaultDbNameChars} character limit", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsAbsoluteDefaultDbName()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_absolute_db");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var dbName = Path.Combine(root, "outside.db");
        File.WriteAllText(manifestPath, $$"""
            {
              "default_db_name": {{JsonSerializer.Serialize(dbName)}}
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("default_db_name must be a plain file name", ex.Message);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside.db")]
    [InlineData("nested/index.db")]
    public void WorkspaceManifestLoader_Load_RejectsUnsafeDefaultDbName(string dbName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_unsafe_db");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, $$"""
            {
              "default_db_name": {{JsonSerializer.Serialize(dbName)}}
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("default_db_name must be a plain file name", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_AcceptsUtf8BomManifest()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_bom");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        var json = """
            {
              "members": ["src/A"],
              "index_strategy": "per_member",
              "default_db_name": "index.db"
            }
            """;
        File.WriteAllBytes(manifestPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);

        var manifest = WorkspaceManifestLoader.Load(manifestPath);

        Assert.Equal("index.db", manifest.DefaultDbName);
        Assert.Single(manifest.Members);
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsUnknownIndexStrategy()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_index_strategy");
        var root = project.Root;
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, """
            {
              "members": ["src/A"],
              "index_strategy": "singel"
            }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("index_strategy must be 'per_member' or 'single'", ex.Message);
    }

    [Fact]
    public void WorkspaceManifestLoader_Find_RejectsInvalidStartDirectory_Issue3429()
    {
        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Find("\0"));

        Assert.Contains("discovery start directory is invalid", ex.Message);
    }

    [Fact]
    public void WorkspaceErrors_HonorJsonFlag()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["nope", "--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("\"status\":\"error\"", stdout);
        Assert.Contains("Unknown workspace command", stdout);
        Assert.DoesNotContain("Unknown workspace command", stderr);
    }

    [Fact]
    public void ConfigErrors_HonorJsonFlag()
    {
        var configHome = TestProjectHelper.CreateTempProject("cdidx_config_error_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "nope", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("\"status\":\"error\"", stdout);
            Assert.Contains("Unknown config command", stdout);
            Assert.DoesNotContain("Unknown config command", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ConfigShowErrors_HonorJsonFlag()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["extra", "--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("\"status\":\"error\"", stdout);
        Assert.Contains("config show does not accept positional arguments", stdout);
        Assert.DoesNotContain("config show does not accept positional arguments", stderr);
    }

    [Fact]
    public void ConfigShow_PrintsPrecedence()
    {
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_config");
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var (exitCode, stdout, _) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("active_workspace", out _));
        Assert.Contains(
            root.GetProperty("precedence").EnumerateArray(),
            item => item.GetString() == "active_workspace");
    }

    [Fact]
    public void ConfigShowJson_IncludesMissingMetadataAndEffectiveDefaults_Issue3905()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_missing_metadata");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_missing_metadata_home");
        var root = project.Root;
        var configHome = config.Root;
        var previous = Environment.CurrentDirectory;
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                ActiveWorkspace.EnvironmentVariable,
                "XDG_CONFIG_HOME",
                CdidxConfigFile.DisableEnvVar,
                QueryCommandRunner.DefaultLimitEnvironmentVariable,
                QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
                "CDIDX_GITHUB_TOKEN");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", null);
            Environment.CurrentDirectory = root;
            var currentRoot = Environment.CurrentDirectory;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            Assert.DoesNotContain(currentRoot, stdout);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.TryGetProperty("config_path", out _));
            Assert.True(payload.GetProperty("redaction").GetProperty("paths_redacted").GetBoolean());
            Assert.Equal(
                EnvironmentVariableInventory.Items.Count,
                payload.GetProperty("environment_inventory_summary").GetProperty("total").GetInt32());
            Assert.Equal("not_found", payload.GetProperty("config_file").GetProperty("status").GetString());
            Assert.Equal("not_found", payload.GetProperty("config_file").GetProperty("reason").GetString());
            Assert.Contains(
                payload.GetProperty("config_file").GetProperty("supported_files").EnumerateArray(),
                item => item.GetString() == CdidxConfigFile.ProjectConfigRelativePath);
            Assert.Contains(
                payload.GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString()?.Contains("[redacted]", StringComparison.Ordinal) == true);
            Assert.Equal("not_found", payload.GetProperty("active_workspace_status").GetProperty("status").GetString());
            Assert.Equal("not_found", payload.GetProperty("workspace_manifest").GetProperty("status").GetString());
            Assert.Contains(
                payload.GetProperty("workspace_manifest").GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString()?.Contains("[redacted]", StringComparison.Ordinal) == true);

            var defaultLimit = payload.GetProperty("effective_config").GetProperty(QueryCommandRunner.DefaultLimitEnvironmentVariable);
            Assert.Equal("default", defaultLimit.GetProperty("source").GetString());
            Assert.Equal("20", defaultLimit.GetProperty("value").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void ConfigShowJson_IncludesSourcesAndRedactsSecrets_Issue3927()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_sources");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_sources_home");
        var root = project.Root;
        var configHome = config.Root;
        var previous = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(root, CdidxConfigFile.FileName), """
                {
                  "search": {
                    "limit": 37
                  }
                }
                """);
            using var env = EnvironmentVariableScope.Capture(
                ActiveWorkspace.EnvironmentVariable,
                "XDG_CONFIG_HOME",
                CdidxConfigFile.DisableEnvVar,
                QueryCommandRunner.DefaultLimitEnvironmentVariable,
                QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
                "CDIDX_GITHUB_TOKEN",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            const string secret = "ghp_123456789012345678901234567890123456";
            const string visiblePath = "/tmp/cdidx-config-show-visible-4148";
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "5");
            Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", secret);
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", visiblePath);
            Environment.CurrentDirectory = root;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "show", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            Assert.DoesNotContain(secret, stdout);
            Assert.DoesNotContain(visiblePath, stdout);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.Equal("loaded", payload.GetProperty("config_file").GetProperty("status").GetString());
            Assert.Contains("[redacted]", payload.GetProperty("config_file").GetProperty("path").GetString(), StringComparison.Ordinal);
            Assert.True(payload.GetProperty("redaction").GetProperty("paths_redacted").GetBoolean());

            var effective = payload.GetProperty("effective_config");
            var limit = effective.GetProperty(QueryCommandRunner.DefaultLimitEnvironmentVariable);
            Assert.Equal("config_file", limit.GetProperty("source").GetString());
            Assert.Equal("37", limit.GetProperty("value").GetString());
            var snippetLines = effective.GetProperty(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable);
            Assert.Equal("environment", snippetLines.GetProperty("source").GetString());
            Assert.Equal("5", snippetLines.GetProperty("value").GetString());
            var githubToken = effective.GetProperty("CDIDX_GITHUB_TOKEN");
            Assert.Equal("environment", githubToken.GetProperty("source").GetString());
            Assert.True(githubToken.GetProperty("sensitive").GetBoolean());
            Assert.Equal("<redacted>", githubToken.GetProperty("value").GetString());
            var logDir = effective.GetProperty("CDIDX_GLOBAL_TOOL_LOG_DIR");
            Assert.Equal("environment", logDir.GetProperty("source").GetString());
            Assert.Equal("<redacted>", logDir.GetProperty("value").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void ConfigShowJson_ShowPathsKeepsRawPathDiagnostics_Issue4148()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_show_paths");
        var root = project.Root;
        var previous = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(root, CdidxConfigFile.FileName), "{}");
            using var env = EnvironmentVariableScope.Capture(
                ActiveWorkspace.EnvironmentVariable,
                CdidxConfigFile.DisableEnvVar);
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.CurrentDirectory = root;
            var currentRoot = Environment.CurrentDirectory;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "show", "--json", "--show-paths"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.GetProperty("redaction").GetProperty("paths_redacted").GetBoolean());
            Assert.Equal(Path.Combine(currentRoot, CdidxConfigFile.FileName), payload.GetProperty("config_file").GetProperty("path").GetString());
            Assert.Contains(
                payload.GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString() == Path.Combine(currentRoot, CdidxConfigFile.ProjectConfigRelativePath));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void ConfigShowJson_InvalidConfigReportsInvalidStatus_Issue3927()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_invalid");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_config_show_invalid_home");
        var root = project.Root;
        var configHome = config.Root;
        var previous = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(root, CdidxConfigFile.FileName), "{ invalid json");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME", CdidxConfigFile.DisableEnvVar);
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.CurrentDirectory = root;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "show", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var configFile = document.RootElement.GetProperty("config_file");
            Assert.Equal("invalid", configFile.GetProperty("status").GetString());
            Assert.Contains("Invalid JSON", configFile.GetProperty("error").GetString(), StringComparison.Ordinal);

            var (prettyExitCode, prettyStdout, prettyStderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["--pretty", "config", "show", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, prettyExitCode);
            Assert.Empty(prettyStderr);
            using var prettyDocument = JsonDocument.Parse(prettyStdout);
            Assert.Equal("invalid", prettyDocument.RootElement.GetProperty("config_file").GetProperty("status").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void ConfigShowJsonUnsupportedMode_ReturnsStructuredJsonError_Issue3927()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["--json=array"], _jsonOptions));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var payload = document.RootElement;
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.Contains("--json=<format>", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceStatusJsonNoManifest_IncludesDiscoveryMetadata_Issues3905_3956()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_status_no_manifest");
        var root = project.Root;
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var currentRoot = Environment.CurrentDirectory;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.GetProperty("manifest_found").GetBoolean());
            Assert.False(payload.TryGetProperty("manifest", out _));
            Assert.Empty(payload.GetProperty("members").EnumerateArray());
            var manifestStatus = payload.GetProperty("manifest_status");
            Assert.Equal("not_found", manifestStatus.GetProperty("status").GetString());
            Assert.Equal("not_found", manifestStatus.GetProperty("reason").GetString());
            Assert.False(manifestStatus.GetProperty("manifest_found").GetBoolean());
            Assert.Equal("workspace_manifest_not_found", manifestStatus.GetProperty("code").GetString());
            Assert.Contains(
                manifestStatus.GetProperty("supported_files").EnumerateArray(),
                item => item.GetString() == WorkspaceManifestLoader.FileName);
            Assert.Contains(
                manifestStatus.GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString() == Path.Combine(currentRoot, WorkspaceManifestLoader.FileName));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceCurrentJsonNoActiveWorkspace_ReturnsExplicitInactiveState_Issue3939()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_current_no_active");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_current_no_active_config");
        var root = project.Root;
        var configHome = config.Root;
        var previous = Environment.CurrentDirectory;
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.CurrentDirectory = root;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["current", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.GetProperty("active").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("workspace").ValueKind);
            Assert.Equal("inactive", payload.GetProperty("status").GetString());
            Assert.Equal("not_set", payload.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_not_set", payload.GetProperty("code").GetString());
            Assert.False(payload.TryGetProperty("active_workspace", out _));
            Assert.False(payload.TryGetProperty("path", out _));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void ActiveWorkspace_AffectsQueryResolutionButNotIndexResolution()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_project");
        using var active = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_state");
        var projectRoot = project.Root;
        var activeRoot = active.Root;
        var activeDb = Path.Combine(activeRoot, ".cdidx", "codeindex.db");
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, activeDb);

        var query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
        var index = DbPathResolver.ResolveForIndex(projectRoot, explicitDbPath: null, explicitDataDir: null);

        Assert.Equal(Path.GetFullPath(activeDb), query.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceActiveWorkspace, query.DataDirSource);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), index.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, index.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceEnvironment_RelativeDbPath_DoesNotOverrideQueryResolution_Issue3825()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_env_relative_project");
        var projectRoot = project.Root;
        const string RelativeDbPath = "relative_active_workspace_SENTINEL_3825.db";

        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, RelativeDbPath);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains(ActiveWorkspace.EnvironmentVariable, stderr);
        Assert.Contains("value must be an absolute database path", stderr);
        Assert.DoesNotContain(RelativeDbPath, stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void MalformedActiveWorkspaceState_DoesNotOverrideQueryResolution()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_malformed_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_malformed_config");
        var projectRoot = project.Root;
        var configHome = config.Root;

        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, "{");

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.DoesNotContain(configHome, stderr);
        Assert.DoesNotContain(ActiveWorkspace.StatePath, stderr);
        Assert.DoesNotContain("LineNumber", stderr);
        Assert.Contains("invalid JSON", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceState_ControlCharacterName_DoesNotOverrideQueryResolution_Issue3825()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_bad_name_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_bad_name_config");
        var projectRoot = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, $$"""
            {
              "name": "bad\nname",
              "root": {{JsonSerializer.Serialize(projectRoot)}},
              "db_path": {{JsonSerializer.Serialize(Path.Combine(projectRoot, ".cdidx", "codeindex.db"))}}
            }
            """);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.Contains("`name` must not contain control characters", stderr);
        Assert.DoesNotContain("bad", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceState_OversizedName_DoesNotOverrideQueryResolution_Issue3825()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_long_name_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_long_name_config");
        var projectRoot = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, $$"""
            {
              "name": {{JsonSerializer.Serialize(new string('n', ActiveWorkspace.MaxWorkspaceNameChars + 1))}},
              "root": {{JsonSerializer.Serialize(projectRoot)}},
              "db_path": {{JsonSerializer.Serialize(Path.Combine(projectRoot, ".cdidx", "codeindex.db"))}}
            }
            """);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.Contains($"`name` exceeds {ActiveWorkspace.MaxWorkspaceNameChars} characters", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceState_MissingRoot_DoesNotOverrideQueryResolution_Issue3430()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_missing_root_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_missing_root_config");
        var projectRoot = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, $$"""
            {
              "name": "active",
              "db_path": {{JsonSerializer.Serialize(Path.Combine(projectRoot, ".cdidx", "codeindex.db"))}}
            }
            """);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.Contains("`root` is required", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceState_MissingDbPath_DoesNotOverrideQueryResolution_Issue3430()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_missing_db_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_missing_db_config");
        var projectRoot = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, $$"""
            {
              "name": "active",
              "root": {{JsonSerializer.Serialize(projectRoot)}}
            }
            """);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.Contains("`db_path` is required", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceState_DbPathOutsideRoot_DoesNotOverrideQueryResolution_Issue3430()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_outside_db_project");
        using var outside = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_outside_db");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_outside_db_config");
        var projectRoot = project.Root;
        var outsideRoot = outside.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, $$"""
            {
              "name": "active",
              "root": {{JsonSerializer.Serialize(projectRoot)}},
              "db_path": {{JsonSerializer.Serialize(Path.Combine(outsideRoot, ".cdidx", "codeindex.db"))}}
            }
            """);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.Contains("`db_path` must be inside `root`", stderr);
        Assert.DoesNotContain(outsideRoot, stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceState_RelativeConfigHome_DoesNotOverrideQueryResolution_Issue3430()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_relative_config_project");
        var projectRoot = project.Root;
        const string RelativeConfigHome = "relative_config_HOME_SENTINEL_3430";

        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", RelativeConfigHome);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace config home", stderr);
        Assert.Contains("XDG_CONFIG_HOME must be an absolute path", stderr);
        Assert.DoesNotContain(RelativeConfigHome, stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void DeeplyNestedActiveWorkspaceState_DoesNotOverrideQueryResolution_Issue3036()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_depth_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_depth_config");
        var projectRoot = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        var activeDbPath = Path.Combine(configHome, "active.db");
        var nestedPrefix = string.Concat(Enumerable.Repeat("""{"next":""", ActiveWorkspace.MaxStateJsonDepth + 1));
        var nested = nestedPrefix + "0" + new string('}', ActiveWorkspace.MaxStateJsonDepth + 1);
        File.WriteAllText(ActiveWorkspace.StatePath, $$"""
            {
              "name": "active",
              "root": {{JsonSerializer.Serialize(configHome)}},
              "db_path": {{JsonSerializer.Serialize(activeDbPath)}},
              "extra": {{nested}}
            }
            """);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("Ignoring active workspace state", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void OversizedActiveWorkspaceEnvironment_DoesNotOverrideQueryResolution_Issue3164()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_env_large_project");
        var projectRoot = project.Root;
        var raw = new string('a', ActiveWorkspace.MaxEnvironmentPathChars) + "TAIL_ISSUE_3164";
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable);
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, raw);

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains(ActiveWorkspace.EnvironmentVariable, stderr);
        Assert.Contains($"value exceeds {ActiveWorkspace.MaxEnvironmentPathChars} characters", stderr);
        Assert.DoesNotContain("TAIL_ISSUE_3164", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void ActiveWorkspaceSave_OnPosix_WritesPrivateStateFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_private_config");
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_private_project");
        var configHome = config.Root;
        var projectRoot = project.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        ActiveWorkspace.Save(new ActiveWorkspaceState("default", projectRoot, Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

        Assert.Equal(
            DataDirectorySecurity.PrivateDirectoryMode,
            File.GetUnixFileMode(Path.GetDirectoryName(ActiveWorkspace.StatePath)!) & DataDirectorySecurity.PermissionBits);
        Assert.Equal(
            DataDirectorySecurity.PrivateFileMode,
            File.GetUnixFileMode(ActiveWorkspace.StatePath) & DataDirectorySecurity.PermissionBits);
    }

    [Fact]
    public void OversizedActiveWorkspaceState_DoesNotOverrideQueryResolution()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_large_project");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_active_workspace_large_config");
        var projectRoot = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
        File.WriteAllText(ActiveWorkspace.StatePath, new string('x', 65 * 1024));

        DbPathResolution? query = null;
        var (_, _, stderr) = ConsoleCapture.Capture(() =>
        {
            query = TestProjectHelper.ResolveQueryDataDirWithinWorkspace(projectRoot);
            return 0;
        });

        Assert.NotNull(query);
        Assert.Contains("file exceeds", stderr);
        Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
        Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
    }

    [Fact]
    public void WorkspaceUse_RejectsUnknownManifestMember()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_unknown");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_config");
        var root = project.Root;
        var configHome = config.Root;
        Directory.CreateDirectory(Path.Combine(root, "src", "A"));
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/A"] }""");
        using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "typo"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("workspace member was not found", stderr);
            Assert.False(File.Exists(ActiveWorkspace.StatePath));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsMissingManifestMember()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_missing");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_missing_config");
        var root = project.Root;
        var configHome = config.Root;
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/Missing"] }""");
        using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "Missing"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("workspace member is missing on disk", stderr);
            Assert.False(File.Exists(ActiveWorkspace.StatePath));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsAmbiguousSameBasenameMembers()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_ambiguous");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_ambiguous_config");
        var root = project.Root;
        var configHome = config.Root;
        Directory.CreateDirectory(Path.Combine(root, "src", "App"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "App"));
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/App", "tests/App"] }""");
        using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "App"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("workspace member name is ambiguous", stderr);
            Assert.Contains(Path.Combine("src", "App"), stderr);
            Assert.Contains(Path.Combine("tests", "App"), stderr);
            Assert.False(File.Exists(ActiveWorkspace.StatePath));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUse_ManifestRelativePathSelectsRepeatedBasename_Issue4726()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_relative");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_relative_config");
        var root = project.Root;
        var configHome = config.Root;
        var selectedRoot = Path.Combine(root, "apps", "App");
        var longMemberPath = string.Join(
            "/",
            Enumerable.Repeat("segment1234567890", 8).Append("App"));
        var longMemberRoot = Path.Combine(
            root,
            longMemberPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(selectedRoot);
        Directory.CreateDirectory(Path.Combine(root, "tests", "App"));
        Directory.CreateDirectory(longMemberRoot);
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), $$"""
            {
              "members": ["apps/App", "tests/App", {{JsonSerializer.Serialize(longMemberPath)}}]
            }
            """);
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var runtimeSelectedRoot = Path.Combine(Environment.CurrentDirectory, "apps", "App");
            var runtimeLongMemberRoot = Path.Combine(
                Environment.CurrentDirectory,
                longMemberPath.Replace('/', Path.DirectorySeparatorChar));
            var (escapingExitCode, _, escapingStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["use", "apps/../../App"], _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, escapingExitCode);
            Assert.Contains("workspace member was not found", escapingStderr);
            Assert.False(File.Exists(ActiveWorkspace.StatePath));

            foreach (var selector in new[] { "apps/App", "apps/./App", @"apps\App" })
            {
                var (exitCode, _, stderr) = ConsoleCapture.Capture(
                    () => WorkspaceCommandRunner.Run(["use", selector], _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                var state = ActiveWorkspace.Load();
                Assert.NotNull(state);
                Assert.Equal("apps/App", state.Name);
                Assert.True(state.ManifestMember);
                Assert.True(PathCasing.PathsEqual(runtimeSelectedRoot, state.Root));
            }

            var (longExitCode, _, longStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["use", longMemberPath], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, longExitCode);
            Assert.Empty(longStderr);
            var longState = ActiveWorkspace.Load();
            Assert.NotNull(longState);
            Assert.Equal(longMemberPath, longState.Name);
            Assert.True(longState.ManifestMember);
            Assert.True(PathCasing.PathsEqual(runtimeLongMemberRoot, longState.Root));

            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
                {
                  "members": ["apps/App", "tests/App"],
                  "index_strategy": "single"
                }
                """);
            var (singleExitCode, _, singleStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["use", "apps/./App"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, singleExitCode);
            Assert.Empty(singleStderr);
            var singleState = ActiveWorkspace.Load();
            Assert.NotNull(singleState);
            Assert.Equal("apps/App", singleState.Name);
            Assert.True(singleState.ManifestMember);
            Assert.True(PathCasing.PathsEqual(Environment.CurrentDirectory, singleState.Root));

            var (statusExitCode, statusStdout, statusStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Empty(statusStderr);
            using var statusDocument = JsonDocument.Parse(statusStdout);
            Assert.Equal(
                "active",
                statusDocument.RootElement
                    .GetProperty("active_workspace_status")
                    .GetProperty("status")
                    .GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUse_RelativePathPersistsManifestCasing_Issue4726()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_relative_casing");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_relative_casing_config");
        var root = project.Root;
        var configHome = config.Root;
        lock (PathCasingTestLock.Gate)
        {
            PathCasing.ResetCacheForTests();
            Directory.CreateDirectory(Path.Combine(root, "Apps", "App"));
            File.WriteAllText(
                Path.Combine(root, "cdidx.workspace.json"),
                """{ "members": ["Apps/App"] }""");
            using var env = EnvironmentVariableScope.Capture(
                ActiveWorkspace.EnvironmentVariable,
                "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            PathCasing.SeedFromWorkspace(root, ignoreCase: true);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(
                    () => WorkspaceCommandRunner.Run(
                        ["use", "apps/app"],
                        _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                var state = ActiveWorkspace.Load();
                Assert.NotNull(state);
                Assert.Equal("Apps/App", state.Name);
                Assert.True(state.ManifestMember);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                PathCasing.ResetCacheForTests();
            }
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("env")]
    public void WorkspaceUse_RelativeReservedMemberPreservesManifestProvenance_Issue4726(
        string reservedName)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_reserved_relative");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_reserved_relative_config");
        var root = project.Root;
        var configHome = config.Root;
        Directory.CreateDirectory(Path.Combine(root, reservedName));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        File.WriteAllText(
            Path.Combine(root, "cdidx.workspace.json"),
            $$"""{ "members": [{{JsonSerializer.Serialize(reservedName)}}] }""");
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (useExitCode, _, useStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["use", $"./{reservedName}"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, useExitCode);
            Assert.Empty(useStderr);
            var state = ActiveWorkspace.Load();
            Assert.NotNull(state);
            Assert.Equal(reservedName, state.Name);
            Assert.True(state.ManifestMember);

            File.WriteAllText(
                Path.Combine(root, "cdidx.workspace.json"),
                """{ "members": ["other"] }""");
            var (statusExitCode, statusStdout, statusStderr) = ConsoleCapture.Capture(
                () => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Empty(statusStderr);
            using var document = JsonDocument.Parse(statusStdout);
            var activeStatus = document.RootElement.GetProperty("active_workspace_status");
            Assert.Equal("stale", activeStatus.GetProperty("status").GetString());
            Assert.Equal("manifest_member_not_found", activeStatus.GetProperty("reason").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceManifest_EscapingMemberDiagnosticDoesNotLeakMemberPath_Issue3805()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_escape");
        var root = project.Root;
        const string SentinelMember = "../SECRET_MEMBER_SENTINEL_3805";
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, $$"""{ "members": [{{JsonSerializer.Serialize(SentinelMember)}}] }""");

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains("member path escapes the manifest root", ex.Message);
        Assert.DoesNotContain(SentinelMember, ex.Message);
        Assert.DoesNotContain("SECRET_MEMBER_SENTINEL_3805", ex.Message);
    }

    [Fact]
    public void WorkspaceManifest_LongMemberPathDiagnosticDoesNotLeakMemberValue_Issue3805()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_manifest_long_member");
        var root = project.Root;
        var longMember = new string('a', WorkspaceManifestLoader.MaxManifestMemberPathChars + 1) + "TAIL_SENTINEL_3805";
        var manifestPath = Path.Combine(root, "cdidx.workspace.json");
        File.WriteAllText(manifestPath, $$"""{ "members": [{{JsonSerializer.Serialize(longMember)}}] }""");

        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

        Assert.Contains($"exceeds the {WorkspaceManifestLoader.MaxManifestMemberPathChars} character limit", ex.Message);
        Assert.DoesNotContain("TAIL_SENTINEL_3805", ex.Message);
    }

    [Fact]
    public void WorkspaceUse_CaseSensitiveMemberSelectionDistinguishesCaseOnlyBasenames_Issue3805()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_case_sensitive");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_case_sensitive_config");
        var root = project.Root;
        var configHome = config.Root;
        lock (PathCasingTestLock.Gate)
        {
            PathCasing.ResetCacheForTests();
            Directory.CreateDirectory(Path.Combine(root, "src", "App"));
            Directory.CreateDirectory(Path.Combine(root, "src", "app"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/App", "src/app"] }""");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            PathCasing.SeedFromWorkspace(root, ignoreCase: false);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "App"], _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                var state = ActiveWorkspace.Load();
                Assert.NotNull(state);
                Assert.Equal("App", state.Name);
                Assert.Equal(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "App")), state.Root);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
                PathCasing.ResetCacheForTests();
            }
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsNamedWorkspaceWithoutManifest()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_no_manifest");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_no_manifest_config");
        var root = project.Root;
        var configHome = config.Root;
        using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "typo"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("workspace manifest was not found", stderr);
            Assert.False(File.Exists(ActiveWorkspace.StatePath));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUse_RelativeConfigHomeReturnsSafeError_Issue3430()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_relative_config");
        var root = project.Root;
        const string RelativeConfigHome = "relative_config_HOME_SENTINEL_USE_3430";
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", RelativeConfigHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "default"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("XDG_CONFIG_HOME must be an absolute path", stderr);
            Assert.DoesNotContain(RelativeConfigHome, stderr);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUse_SingleStrategyStoresManifestRoot_Issue3430()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_single_strategy");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_single_strategy_config");
        var root = project.Root;
        var configHome = config.Root;
        Directory.CreateDirectory(Path.Combine(root, "src", "A"));
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
            {
              "members": ["src/A"],
              "index_strategy": "single"
            }
            """);
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "A"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var state = ActiveWorkspace.Load();
            Assert.NotNull(state);
            var expectedRoot = Path.GetFullPath(Environment.CurrentDirectory);
            Assert.Equal(expectedRoot, state.Root);
            Assert.Equal(Path.GetFullPath(Path.Combine(expectedRoot, ".cdidx", "codeindex.db")), state.DbPath);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void WorkspaceUseDefault_DoesNotSelectFirstManifestMember()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_default");
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_use_default_config");
        var root = project.Root;
        var configHome = config.Root;
        Directory.CreateDirectory(Path.Combine(root, "src", "A"));
        Directory.CreateDirectory(Path.Combine(root, "src", "B"));
        File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/A", "src/B"] }""");
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var (exitCode, _, _) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "default"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var state = ActiveWorkspace.Load();
            Assert.NotNull(state);
            var expectedRoot = Path.GetFullPath(Environment.CurrentDirectory);
            Assert.Equal(expectedRoot, state.Root);
            Assert.Equal(Path.GetFullPath(Path.Combine(expectedRoot, ".cdidx", "codeindex.db")), state.DbPath);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("deactivate")]
    public void WorkspaceClear_RemovesPersistedActiveWorkspace_Issue4475(string command)
    {
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_clear_config");
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_clear_project");
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config.Root);
        ActiveWorkspace.Save(new ActiveWorkspaceState(
            "default",
            project.Root,
            Path.Combine(project.Root, ".cdidx", "codeindex.db")));

        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
            () => WorkspaceCommandRunner.Run([command, "--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.False(File.Exists(ActiveWorkspace.StatePath));
        Assert.Null(ActiveWorkspace.Load());
        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal("inactive", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("workspace").ValueKind);
    }

    [Fact]
    public void WorkspaceClear_IsIdempotentWhenNoPersistedStateExists_Issue4475()
    {
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_clear_empty_config");
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config.Root);

        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(
            () => WorkspaceCommandRunner.Run(["clear"], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal("Active workspace cleared.", stdout.Trim());
        Assert.Empty(stderr);
    }

    [Fact]
    public void WorkspaceClear_RejectsEnvironmentOverrideWithoutDeletingPersistedState_Issue4475()
    {
        using var config = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_clear_env_config");
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_workspace_clear_env_project");
        using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config.Root);
        ActiveWorkspace.Save(new ActiveWorkspaceState(
            "default",
            project.Root,
            Path.Combine(project.Root, ".cdidx", "codeindex.db")));
        Environment.SetEnvironmentVariable(
            ActiveWorkspace.EnvironmentVariable,
            Path.Combine(project.Root, ".cdidx", "environment.db"));

        var (exitCode, _, stderr) = ConsoleCapture.Capture(
            () => WorkspaceCommandRunner.Run(["clear"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains(ActiveWorkspace.EnvironmentVariable, stderr);
        Assert.Contains("unset", stderr);
        Assert.True(File.Exists(ActiveWorkspace.StatePath));
    }

    private static string SwapAsciiCase(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (ch is >= 'a' and <= 'z')
                chars[i] = (char)(ch - ('a' - 'A'));
            else if (ch is >= 'A' and <= 'Z')
                chars[i] = (char)(ch + ('a' - 'A'));
        }

        return new string(chars);
    }
}
