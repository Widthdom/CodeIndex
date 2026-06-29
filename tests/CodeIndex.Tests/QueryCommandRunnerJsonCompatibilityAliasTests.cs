using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void QueryFindCountJsonResult_DeprecatedAliasHasLifecycleRegistryAndStillSerializes_Issue4182()
    {
        var result = new QueryFindCountJsonResult(3, 1, 1);

        var json = JsonSerializer.Serialize(result, CliJsonSerializerContext.Default.QueryFindCountJsonResult);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(1, root.GetProperty("files").GetInt32());
        Assert.Equal(1, root.GetProperty("file_count").GetInt32());

        var alias = Assert.Single(JsonCompatibilityAliasLifecycles.All, item => item.AliasName == "file_count");
        Assert.Equal("files", alias.ReplacementName);
        Assert.Equal("find --count --json", alias.Contract);
        Assert.Equal(nameof(QueryFindCountJsonResult), alias.JsonContractType);
        Assert.Equal("FileCount", alias.PropertyName);
        Assert.Contains("one minor release", alias.RemovalCriteria, StringComparison.OrdinalIgnoreCase);

        var property = typeof(QueryFindCountJsonResult).GetProperty(alias.PropertyName);
        Assert.NotNull(property);
        var obsolete = Assert.Single(property!.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).Cast<ObsoleteAttribute>());
        Assert.Contains("files", obsolete.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_JsonCompatibilityAliasAuditCanStayProductionScoped_Issue4182()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_json_alias_audit_4182");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/CodeIndex/Cli/JsonCompatibilityAliasLifecycles.cs",
                "csharp",
                """
                using System;
                using System.Text.Json.Serialization;

                namespace CodeIndex.Cli;

                internal sealed record JsonCompatibilityAliasLifecycle(
                    string AliasName,
                    string ReplacementName,
                    string Contract,
                    string JsonContractType,
                    string PropertyName,
                    string RemovalCriteria);

                internal static class JsonCompatibilityAliasLifecycles
                {
                    internal static IReadOnlyList<JsonCompatibilityAliasLifecycle> All { get; } =
                    [
                        new("file_count", "files", "find --count --json", "QueryFindCountJsonResult", "FileCount", "Keep serialized.")
                    ];
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "docs/json-compatibility-alias-lifecycle.md",
                "markdown",
                "Docs mention Obsolete and JsonCompatibilityAliasLifecycle for reviewers.\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/CodeIndex.Tests/JsonOutputContractsTests.cs",
                "csharp",
                "var fixture = \"Obsolete JsonCompatibilityAliasLifecycle\";\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["JsonCompatibilityAliasLifecycle", "--db", dbPath, "--source-only", "--origin", "code", "--json=array", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var result = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal("src/CodeIndex/Cli/JsonCompatibilityAliasLifecycles.cs", result.GetProperty("path").GetString());
            Assert.Contains(result.GetProperty("match_origins").EnumerateArray(), origin => origin.GetString() == "code");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
