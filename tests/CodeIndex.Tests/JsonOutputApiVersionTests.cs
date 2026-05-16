using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Pins the `api_version` JSON output contract added by issue #1555. The constant lives in
/// <see cref="JsonOutputContract.ApiVersion"/>; every top-level DTO and the
/// `--json-envelope` metadata block must carry it. Bumping the constant should be a deliberate
/// breaking-change signal for downstream consumers.
/// </summary>
[Collection("SQLite pool sensitive")]
public class JsonOutputApiVersionTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ApiVersionConstant_IsExpectedValue()
    {
        Assert.Equal("1", JsonOutputContract.ApiVersion);
    }

    [Theory]
    [InlineData(typeof(StatusResult))]
    [InlineData(typeof(RepoMapResult))]
    [InlineData(typeof(SymbolAnalysisResult))]
    [InlineData(typeof(ImpactAnalysisResult))]
    [InlineData(typeof(OutlineResult))]
    [InlineData(typeof(FileExcerptResult))]
    [InlineData(typeof(SymbolResult))]
    [InlineData(typeof(DefinitionResult))]
    [InlineData(typeof(UnusedSymbolResult))]
    [InlineData(typeof(ReferenceResult))]
    [InlineData(typeof(CallerResult))]
    [InlineData(typeof(CalleeResult))]
    [InlineData(typeof(FileResult))]
    [InlineData(typeof(FileFindResult))]
    [InlineData(typeof(CompactSearchResult))]
    public void TopLevelDto_DefaultsApiVersionToCurrent(Type dtoType)
    {
        var instance = Activator.CreateInstance(dtoType)
            ?? throw new InvalidOperationException($"Could not instantiate {dtoType.FullName}");
        var property = dtoType.GetProperty("ApiVersion")
            ?? throw new InvalidOperationException($"{dtoType.FullName} is missing ApiVersion property");
        var actual = property.GetValue(instance) as string;
        Assert.Equal(JsonOutputContract.ApiVersion, actual);
    }

    [Fact]
    public void StatusResult_JsonOutput_IncludesApiVersion()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_api_version_status");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Lib.cs", "csharp", "namespace Demo;\nclass Lib { }\n");

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var json = ParseFirstObject(stdout);
            Assert.Equal(JsonOutputContract.ApiVersion, json["api_version"]?.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Search_JsonEnvelope_MetadataIncludesApiVersion()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_api_version_envelope");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "namespace Demo;\nclass App { void Authenticate() {} }\n");

            var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Authenticate", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "9.9.9-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var envelope = ParseFirstObject(stdout);
            var metadata = envelope["metadata"] as JsonObject
                ?? throw new InvalidOperationException("envelope missing metadata");
            Assert.Equal(JsonOutputContract.ApiVersion, metadata["api_version"]?.GetValue<string>());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    private static JsonObject ParseFirstObject(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed[0] != '{')
                continue;
            return JsonNode.Parse(trimmed) as JsonObject
                ?? throw new InvalidOperationException("Failed to parse JSON line as object.");
        }
        throw new InvalidOperationException("No JSON object found in stdout.");
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                return (action(), stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
