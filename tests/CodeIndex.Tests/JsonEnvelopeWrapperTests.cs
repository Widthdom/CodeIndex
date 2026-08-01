using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class JsonEnvelopeWrapperTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void Search_WithEnvelope_WrapsResultsAndPopulatesMetadata()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_search");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "namespace Demo;\nclass App { void Authenticate() {} }\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "9.9.9-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.Equal("search", metadata.GetProperty("command").GetString());
            Assert.Equal("9.9.9-test", metadata.GetProperty("cdidx_version").GetString());
            Assert.Equal("Authenticate", metadata.GetProperty("query_normalized").GetString());
            Assert.Equal(dbPath, metadata.GetProperty("db_path").GetString());
            Assert.True(metadata.GetProperty("elapsed_ms").GetDouble() >= 0);
            Assert.Equal(0, metadata.GetProperty("exit_code").GetInt32());

            var results = document.RootElement.GetProperty("results");
            Assert.Equal(JsonValueKind.Array, results.ValueKind);
            Assert.True(results.GetArrayLength() >= 1);
            Assert.Equal(results.GetArrayLength(), metadata.GetProperty("result_count").GetInt32());
            Assert.Equal("src/App.cs", results[0].GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_WithEnvelope_ZeroResultsKeepsEnvelopeAndPropagatesExitCode()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_search_zero");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App {}\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "DoesNotExist_xyz_123", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.Equal(CommandExitCodes.Success, metadata.GetProperty("exit_code").GetInt32());
            Assert.Equal("DoesNotExist_xyz_123", metadata.GetProperty("query_normalized").GetString());

            var results = document.RootElement.GetProperty("results");
            Assert.Equal(JsonValueKind.Array, results.ValueKind);
            Assert.Empty(results.EnumerateArray());
            Assert.Equal(0, metadata.GetProperty("result_count").GetInt32());
            var controls = metadata.GetProperty("stream_control_records");
            Assert.Single(controls.EnumerateArray());
            Assert.Equal(0, controls[0].GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_WithEnvelope_InjectsJsonFlagWhenOmitted()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_implicit_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { void Authenticate() {} }\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.TryGetProperty("metadata", out _));
            Assert.True(document.RootElement.TryGetProperty("results", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Status_WithEnvelope_WrapsSingleObjectIntoResultsArray()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_status");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App {}\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["status", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.Equal("status", metadata.GetProperty("command").GetString());
            var results = document.RootElement.GetProperty("results");
            Assert.Equal(1, results.GetArrayLength());
            Assert.Equal(1, results[0].GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_WithEnvelope_UsesLatestIndexedHeadAndFallsBackForLegacyDatabase_Issue4854()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_indexed_head");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { void Authenticate() {} }\n");
            const string fullScanHead = "1111111111111111111111111111111111111111";
            const string latestHead = "2222222222222222222222222222222222222222";
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues(
                    (DbContext.IndexedHeadCommitMetaKey, fullScanHead),
                    (DbContext.IndexedHeadShaMetaKey, latestHead));
            }

            var (latestExitCode, latestStdout, latestStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, latestExitCode);
            Assert.Equal(string.Empty, latestStderr);
            using (var latestDocument = JsonDocument.Parse(latestStdout))
            {
                Assert.Equal(
                    latestHead,
                    latestDocument.RootElement
                        .GetProperty("metadata")
                        .GetProperty("indexed_at_head_sha")
                        .GetString());
            }

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues((DbContext.IndexedHeadShaMetaKey, null));
            }

            var (nullExitCode, nullStdout, nullStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, nullExitCode);
            Assert.Equal(string.Empty, nullStderr);
            using (var nullDocument = JsonDocument.Parse(nullStdout))
            {
                Assert.False(
                    nullDocument.RootElement
                        .GetProperty("metadata")
                        .TryGetProperty("indexed_at_head_sha", out _));
            }

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                command.Parameters.AddWithValue("@key", DbContext.IndexedHeadShaMetaKey);
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            var (legacyExitCode, legacyStdout, legacyStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, legacyExitCode);
            Assert.Equal(string.Empty, legacyStderr);
            using var legacyDocument = JsonDocument.Parse(legacyStdout);
            Assert.Equal(
                fullScanHead,
                legacyDocument.RootElement
                    .GetProperty("metadata")
                    .GetProperty("indexed_at_head_sha")
                    .GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_UnboundedEnvelope_KeepsIndexedHeadFromValidatedResponseSnapshot_Issue4854()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_unbounded_indexed_head_snapshot");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { void Authenticate() {} }\n");
            const string snapshotHead = "2222222222222222222222222222222222222222";
            const string nextHead = "3333333333333333333333333333333333333333";
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues((DbContext.IndexedHeadShaMetaKey, snapshotHead));
            }

            var hookInvoked = false;
            JsonEnvelopeWrapper.ResponseSnapshotValidatedForTesting = () =>
            {
                hookInvoked = true;
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues((DbContext.IndexedHeadShaMetaKey, nextHead));
            };

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(
                snapshotHead,
                document.RootElement
                    .GetProperty("metadata")
                    .GetProperty("indexed_at_head_sha")
                    .GetString());
            using var verificationDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(nextHead, verificationDb.GetMetaString(DbContext.IndexedHeadShaMetaKey));
        }
        finally
        {
            JsonEnvelopeWrapper.ResponseSnapshotValidatedForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunWrapped_UnboundedEnvelope_RejectsRowsWhenIndexGenerationChanges_Issue4854()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_unbounded_generation_change");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            const string snapshotHead = "2222222222222222222222222222222222222222";
            const string nextHead = "3333333333333333333333333333333333333333";
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues((DbContext.IndexedHeadShaMetaKey, snapshotHead));
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
                "search",
                ["Authenticate", "--db", dbPath, "--json-envelope"],
                "1.0.0",
                _jsonOptions,
                _ =>
                {
                    Console.WriteLine("""{"path":"src/App.cs"}""");
                    using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                    var writer = new DbWriter(db.Connection);
                    writer.SetMetaValues((DbContext.IndexedHeadShaMetaKey, nextHead));
                    return CommandExitCodes.Success;
                }));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("index generation changed", stderr, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Empty(root.GetProperty("results").EnumerateArray());
            Assert.Equal(
                CommandErrorCodes.UsageError,
                root.GetProperty("metadata").GetProperty("error").GetProperty("error_code").GetString());
            Assert.Equal(
                snapshotHead,
                root.GetProperty("metadata").GetProperty("indexed_at_head_sha").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_BoundedEnvelope_KeepsIndexedHeadFromValidatedResponseSnapshot_Issue4854()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_indexed_head_snapshot");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { void Authenticate() {} }\n");
            const string legacyHead = "1111111111111111111111111111111111111111";
            const string snapshotHead = "2222222222222222222222222222222222222222";
            const string nextHead = "3333333333333333333333333333333333333333";
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues(
                    (DbContext.IndexedHeadCommitMetaKey, legacyHead),
                    (DbContext.IndexedHeadShaMetaKey, snapshotHead));
            }

            var hookInvoked = false;
            JsonEnvelopeWrapper.ResponseSnapshotValidatedForTesting = () =>
            {
                hookInvoked = true;
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues((DbContext.IndexedHeadShaMetaKey, nextHead));
            };

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--fields", "path", "--json"],
                _jsonOptions,
                "1.0.0"));

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(
                snapshotHead,
                document.RootElement
                    .GetProperty("metadata")
                    .GetProperty("indexed_at_head_sha")
                    .GetString());
            using var verificationDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(nextHead, verificationDb.GetMetaString(DbContext.IndexedHeadShaMetaKey));
        }
        finally
        {
            JsonEnvelopeWrapper.ResponseSnapshotValidatedForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_WithoutEnvelope_StillEmitsLegacyNdjson()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_legacy_off");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { void Authenticate() {} }\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            // Legacy default: results remain newline-delimited JSON, followed by a done sentinel.
            // 既存 default: 結果は newline-delimited JSON のまま、最後に done sentinel が付く。
            Assert.DoesNotContain("\"metadata\"", stdout);
            Assert.DoesNotContain("\"results\"", stdout);
            var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            using var resultDocument = JsonDocument.Parse(lines[0]);
            Assert.Equal("src/App.cs", resultDocument.RootElement.GetProperty("path").GetString());
            using var doneDocument = JsonDocument.Parse(lines[1]);
            Assert.True(doneDocument.RootElement.GetProperty("done").GetBoolean());
            Assert.Equal(1, doneDocument.RootElement.GetProperty("count").GetInt32());
            Assert.False(doneDocument.RootElement.GetProperty("interrupted").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_WithoutEnvelope_ZeroResultsEmitsDoneSentinel()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_legacy_zero_done");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App {}\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "DoesNotExist_xyz_123", "--db", dbPath, "--json"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            using var zeroDocument = JsonDocument.Parse(lines[0]);
            Assert.Equal(0, zeroDocument.RootElement.GetProperty("count").GetInt32());
            using var doneDocument = JsonDocument.Parse(lines[1]);
            Assert.True(doneDocument.RootElement.GetProperty("done").GetBoolean());
            Assert.Equal(0, doneDocument.RootElement.GetProperty("count").GetInt32());
            Assert.False(doneDocument.RootElement.GetProperty("interrupted").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HasEnvelopeFlag_ClassifiesOptionRoles_Issue4976()
    {
        Assert.True(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--json-envelope"]));
        Assert.True(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--query", "--json-envelope", "--json-envelope"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--json"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--json-envelope=1"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--query", "--json-envelope"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--path", "--json-envelope"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag("search", ["--", "--json-envelope"]));
    }

    [Fact]
    public void PrepareInnerArgs_StripsEnvelopeAndAddsJson()
    {
        var prepared = JsonEnvelopeWrapper.PrepareInnerArgs(
            "search",
            ["foo", "--json-envelope", "--limit", "5"]);

        Assert.Equal(["foo", "--limit", "5", "--json"], prepared);
    }

    [Fact]
    public void PrepareInnerArgs_PreservesExistingJsonFlag()
    {
        var prepared = JsonEnvelopeWrapper.PrepareInnerArgs(
            "search",
            ["foo", "--json", "--json-envelope"]);

        Assert.DoesNotContain("--json-envelope", prepared);
        Assert.Equal(1, prepared.Count(a => a == "--json"));
    }

    [Fact]
    public void PrepareInnerArgs_PreservesOptionValuesAndEndMarker_Issue4976()
    {
        Assert.Equal(
            ["--path", "--json-envelope", "--json"],
            JsonEnvelopeWrapper.PrepareInnerArgs(
                "search",
                ["--path", "--json-envelope", "--json-envelope"]));
        Assert.Equal(
            ["--query", "--json", "--json"],
            JsonEnvelopeWrapper.PrepareInnerArgs(
                "search",
                ["--query", "--json", "--json-envelope"]));
        Assert.Equal(
            ["--json", "--", "--json-envelope"],
            JsonEnvelopeWrapper.PrepareInnerArgs(
                "search",
                ["--json-envelope", "--", "--json-envelope"]));
    }

    [Fact]
    public void Search_JsonEnvelopeLiteralRemainsCommandDataAcrossQueryForms_Issue4976()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("json_envelope_option_value_4976");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.txt",
                "text",
                "The literal --json-envelope remains searchable.\n");

            string[][] arrayCases =
            [
                ["search", "--query", "--json-envelope", "--db", dbPath, "--exact-substring", "--json=array"],
                ["search", "--json=array", "--db", dbPath, "--query", "--json-envelope", "--exact-substring"],
                ["search", "--query=--json-envelope", "--db", dbPath, "--exact-substring", "--json=array"],
                ["search", "--db", dbPath, "--exact-substring", "--json=array", "--", "--json-envelope"],
            ];
            foreach (var args in arrayCases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
                Assert.NotEmpty(document.RootElement.EnumerateArray());
            }

            string[][] envelopeCases =
            [
                ["search", "--query", "--json-envelope", "--db", dbPath, "--exact-substring", "--json-envelope"],
                ["search", "--json-envelope", "--db", dbPath, "--exact-substring", "--", "--json-envelope"],
            ];
            foreach (var args in envelopeCases)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                Assert.Equal("--json-envelope", document.RootElement.GetProperty("metadata").GetProperty("query_normalized").GetString());
                Assert.NotEmpty(document.RootElement.GetProperty("results").EnumerateArray());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunWrapped_CapturedOutputExceedsLimit_ReturnsJsonErrorEnvelope_Issue2901()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                Console.Write(new string('x', JsonEnvelopeWrapper.MaxCapturedOutputChars + 1));
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("--json-envelope captured output exceeded", stderr);
        using var document = JsonDocument.Parse(stdout);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal(CommandExitCodes.InvalidArgument, metadata.GetProperty("exit_code").GetInt32());
        Assert.Equal(0, metadata.GetProperty("result_count").GetInt32());
        Assert.Equal(CommandErrorCodes.UsageError, metadata.GetProperty("error").GetProperty("error_code").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void RunWrapped_TooDeepRawJsonItem_KeepsLineAsString_Issue3016()
    {
        var rawLine = BuildNestedRawJson(JsonEnvelopeWrapper.MaxRawJsonItemDepth + 1);
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                Console.WriteLine(rawLine);
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal(rawLine, result.GetString());
    }

    [Fact]
    public void RunWrapped_MalformedRawJsonItem_KeepsLineAsString_Issue3711()
    {
        const string rawLine = """{"path":"src/App.cs","score":""";
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                Console.WriteLine(rawLine);
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var result = Assert.Single(document.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal(JsonValueKind.String, result.ValueKind);
        Assert.Equal(rawLine, result.GetString());
    }

    [Fact]
    public void RunWrapped_OversizedRawJsonItem_ReturnsStructuredEnvelopeError_Issue3454()
    {
        var rawLine = new string('x', JsonEnvelopeWrapper.MaxRawJsonItemChars + 1);
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                Console.WriteLine(rawLine);
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("--json-envelope raw JSON item line exceeded", stderr);
        using var document = JsonDocument.Parse(stdout);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal(CommandExitCodes.InvalidArgument, metadata.GetProperty("exit_code").GetInt32());
        Assert.Equal(0, metadata.GetProperty("result_count").GetInt32());
        var error = metadata.GetProperty("error");
        Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
        Assert.Equal(JsonEnvelopeWrapper.MaxRawJsonItemChars, error.GetProperty("max_chars").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void RunWrapped_ManyRawJsonItems_ReturnsStructuredEnvelopeError_Issue3779()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                for (var i = 0; i <= JsonEnvelopeWrapper.MaxRawJsonItems; i++)
                    Console.WriteLine("0");
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("--json-envelope raw JSON item count exceeded", stderr);
        using var document = JsonDocument.Parse(stdout);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal(CommandExitCodes.InvalidArgument, metadata.GetProperty("exit_code").GetInt32());
        Assert.Equal(0, metadata.GetProperty("result_count").GetInt32());
        var error = metadata.GetProperty("error");
        Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
        Assert.Equal(JsonEnvelopeWrapper.MaxRawJsonItems, error.GetProperty("max_items").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void RunWrapped_NestedRawJsonNodes_ReturnsStructuredEnvelopeError_Issue3779()
    {
        var rawLine = BuildWideRawJsonArray(JsonEnvelopeWrapper.MaxRawJsonNodes);
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                Console.WriteLine(rawLine);
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("--json-envelope raw JSON node count exceeded", stderr);
        using var document = JsonDocument.Parse(stdout);
        var metadata = document.RootElement.GetProperty("metadata");
        Assert.Equal(CommandExitCodes.InvalidArgument, metadata.GetProperty("exit_code").GetInt32());
        Assert.Equal(0, metadata.GetProperty("result_count").GetInt32());
        var error = metadata.GetProperty("error");
        Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
        Assert.Equal(JsonEnvelopeWrapper.MaxRawJsonNodes, error.GetProperty("max_nodes").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void RunWrapped_MixedRawLines_ParsesWithoutMaterializingSplitArray_Issue3015()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => JsonEnvelopeWrapper.RunWrapped(
            "search",
            ["Needle", "--json-envelope"],
            "1.0.0",
            _jsonOptions,
            _ =>
            {
                Console.Write("{\"path\":\"src/App.cs\"}\r\n");
                Console.Write("not-json\r\n");
                Console.Write("{\"done\":true,\"interrupted\":false,\"count\":2}\r\n");
                return CommandExitCodes.Success;
            }));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("src/App.cs", results[0].GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.String, results[1].ValueKind);
        Assert.Equal("not-json", results[1].GetString());
    }

    [Fact]
    public void Symbols_WithEnvelope_NormalizesQueryFromExtraNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("envelope_symbols");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "class App { void Authenticate() {} }\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["symbols", "App", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "1.0.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("App", document.RootElement.GetProperty("metadata").GetProperty("query_normalized").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);

    private static string BuildNestedRawJson(int nestedObjectCount)
    {
        var builder = new StringBuilder("""{"value":""");
        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append("""{"next":""");

        builder.Append('0');

        for (var i = 0; i < nestedObjectCount; i++)
            builder.Append('}');
        builder.Append('}');
        return builder.ToString();
    }

    private static string BuildWideRawJsonArray(int itemCount)
    {
        var builder = new StringBuilder(itemCount * 2 + 2);
        builder.Append('[');
        for (var i = 0; i < itemCount; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append('0');
        }
        builder.Append(']');
        return builder.ToString();
    }
}
