using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
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
            Assert.Equal(1, results.GetArrayLength());
            Assert.Equal(0, results[0].GetProperty("count").GetInt32());
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
    public void HasEnvelopeFlag_DetectsExactFlagOnly()
    {
        Assert.True(JsonEnvelopeWrapper.HasEnvelopeFlag(["--json-envelope"]));
        Assert.True(JsonEnvelopeWrapper.HasEnvelopeFlag(["foo", "--json", "--json-envelope"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag(["--json"]));
        Assert.False(JsonEnvelopeWrapper.HasEnvelopeFlag(["--json-envelope=1"]));
    }

    [Fact]
    public void PrepareInnerArgs_StripsEnvelopeAndAddsJson()
    {
        var prepared = JsonEnvelopeWrapper.PrepareInnerArgs(["foo", "--json-envelope", "--limit", "5"]);
        Assert.DoesNotContain("--json-envelope", prepared);
        Assert.Contains("--json", prepared);
        Assert.Contains("foo", prepared);
        Assert.Contains("--limit", prepared);
        Assert.Contains("5", prepared);
    }

    [Fact]
    public void PrepareInnerArgs_PreservesExistingJsonFlag()
    {
        var prepared = JsonEnvelopeWrapper.PrepareInnerArgs(["foo", "--json", "--json-envelope"]);
        Assert.DoesNotContain("--json-envelope", prepared);
        Assert.Equal(1, prepared.Count(a => a == "--json"));
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
}
