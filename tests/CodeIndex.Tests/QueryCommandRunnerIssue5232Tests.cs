using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunGoto_AmbiguityCandidatesStopAtCompleteUtf8Rows_Issue5232()
    {
        var longValue = string.Concat(Enumerable.Repeat("ordinarylowercasevalue-", 30));
        var results = Enumerable.Range(0, QueryCommandRunner.GotoAmbiguityCandidateLimit)
            .Select(index => new CodeIndex.Database.DefinitionResult
            {
                Path = index switch
                {
                    0 => "/private/secret/Target.cs",
                    1 => "tests/CodeIndex.Tests/QueryCommandRunnerAuditSarifIssue4903Tests.cs",
                    _ => $"relative/{index}/{longValue}",
                },
                Line = index + 1,
                Lang = longValue,
                Kind = longValue,
                Name = longValue,
                ContainerName = longValue,
                Signature = longValue,
            })
            .ToList();

        var properties = QueryCommandRunner.BuildGotoAmbiguityJsonProperties(
            results,
            totalCount: 100,
            QueryCommandRunner.GotoAmbiguityCandidateLimit,
            _jsonOptions);
        using var document = JsonDocument.Parse(properties.ToJsonString());
        var root = document.RootElement;
        var candidates = root.GetProperty("candidates");

        Assert.True(candidates.GetArrayLength() < QueryCommandRunner.GotoAmbiguityCandidateLimit);
        Assert.True(Encoding.UTF8.GetByteCount(candidates.GetRawText()) <= QueryCommandRunner.GotoAmbiguityCandidateByteLimit);
        Assert.Equal(Encoding.UTF8.GetByteCount(candidates.GetRawText()), root.GetProperty("candidate_bytes").GetInt32());
        Assert.Equal("<redacted>", candidates[0].GetProperty("path").GetString());
        Assert.Equal(
            "tests/CodeIndex.Tests/QueryCommandRunnerAuditSarifIssue4903Tests.cs",
            candidates[1].GetProperty("path").GetString());
        Assert.DoesNotContain(candidates.EnumerateArray(), candidate => candidate.TryGetProperty("signature", out _));
        Assert.Equal(candidates.GetArrayLength(), root.GetProperty("returned_count").GetInt32());
        Assert.Equal(100 - candidates.GetArrayLength(), root.GetProperty("omitted_count").GetInt32());
        Assert.True(root.GetProperty("candidates_truncated").GetBoolean());

        var prettyOptions = new JsonSerializerOptions(_jsonOptions) { WriteIndented = true };
        var prettyProperties = QueryCommandRunner.BuildGotoAmbiguityJsonProperties(
            results,
            totalCount: 100,
            QueryCommandRunner.GotoAmbiguityCandidateLimit,
            prettyOptions);
        using var prettyDocument = JsonDocument.Parse(prettyProperties.ToJsonString(prettyOptions));
        var prettyRoot = prettyDocument.RootElement;
        var prettyCandidates = prettyRoot.GetProperty("candidates");
        var prettyCandidateBytes = Encoding.UTF8.GetByteCount(prettyCandidates.GetRawText());
        Assert.True(prettyCandidateBytes <= QueryCommandRunner.GotoAmbiguityCandidateByteLimit);
        Assert.Equal(prettyCandidateBytes, prettyRoot.GetProperty("candidate_bytes").GetInt32());
    }

    [Fact]
    public void RunGoto_ZeroOneAndManyMatchesKeepJsonAndHumanContracts_Issue5232()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_goto_ambiguity_issue5232");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/OnlyTarget.cs",
                "csharp",
                "public class OnlyTarget { }\n");
            for (var index = 0; index < 25; index++)
            {
                var path = index == 0
                    ? "src/api_key=SUPERSECRETVALUE/SharedTarget0.cs"
                    : index == 1
                        ? "src/ghp_0123456789abcdefghijkl/SharedTarget1.cs"
                    : $"src/SharedTarget{index}.cs";
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    path,
                    "csharp",
                    index == 2
                        ? "public class SharedTarget { public void Run(string password = \"hunter2\") { } }\n"
                        : $"public class SharedTarget {{ public void Run() {{ }} }} // {index}\n");
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/CustomA.css", "css", ":root { --json: red; }\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/CustomB.css", "css", ".x { --json: blue; }\n");

            var (zeroExit, zeroStdout, zeroStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["MissingTarget", "--db", dbPath, "--json", "--exact-name", "--kind", "class", "--lang", "csharp"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, zeroExit);
            Assert.Equal(string.Empty, zeroStderr);
            using (var zeroDocument = ParseJsonOutput(zeroStdout))
            {
                var zero = zeroDocument.RootElement;
                Assert.Equal(CommandErrorCodes.QueryNotFound, zero.GetProperty("error_code").GetString());
                Assert.Equal("goto", zero.GetProperty("command").GetString());
                Assert.Equal(CommandExitCodes.NotFound, zero.GetProperty("exit_code").GetInt32());
            }

            var (oneExit, oneStdout, oneStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["OnlyTarget", "--db", dbPath, "--json", "--exact-name", "--kind", "class", "--lang", "csharp"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, oneExit);
            Assert.Equal(string.Empty, oneStderr);
            using (var oneDocument = ParseJsonOutput(oneStdout))
            {
                Assert.EndsWith("src/OnlyTarget.cs", oneDocument.RootElement.GetProperty("uri").GetString(), StringComparison.Ordinal);
            }

            var ambiguityArgs = new[]
            {
                "Run", "--db", dbPath, "--json", "--exact-name", "--kind", "function", "--lang", "csharp",
            };
            var (manyExit, manyStdout, manyStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ambiguityArgs,
                _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, manyExit);
            Assert.Equal(string.Empty, manyStderr);
            Assert.DoesNotContain("SUPERSECRETVALUE", manyStdout, StringComparison.Ordinal);
            Assert.DoesNotContain("ghp_0123456789abcdefghijkl", manyStdout, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", manyStdout, StringComparison.Ordinal);
            using (var manyDocument = ParseJsonOutput(manyStdout))
            {
                var many = manyDocument.RootElement;
                Assert.Equal("1", many.GetProperty("api_version").GetString());
                Assert.Equal("error", many.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.QueryAmbiguous, many.GetProperty("error_code").GetString());
                Assert.Equal("ambiguous_query", many.GetProperty("category").GetString());
                Assert.Equal("goto", many.GetProperty("command").GetString());
                Assert.Equal(CommandExitCodes.UsageError, many.GetProperty("exit_code").GetInt32());
                Assert.Contains("cdidx goto", many.GetProperty("usage").GetString(), StringComparison.Ordinal);
                Assert.Equal(25, many.GetProperty("match_count").GetInt32());
                Assert.Equal(25, many.GetProperty("total_count").GetInt32());
                Assert.True(many.GetProperty("total_count_authoritative").GetBoolean());
                Assert.Equal(QueryCommandRunner.GotoAmbiguityCandidateLimit, many.GetProperty("candidate_limit").GetInt32());
                Assert.Equal(QueryCommandRunner.GotoAmbiguityCandidateByteLimit, many.GetProperty("candidate_byte_limit").GetInt32());
                Assert.True(many.GetProperty("candidate_bytes").GetInt32() <= QueryCommandRunner.GotoAmbiguityCandidateByteLimit);
                var candidates = many.GetProperty("candidates");
                Assert.True(candidates.GetArrayLength() <= QueryCommandRunner.GotoAmbiguityCandidateLimit);
                Assert.True(Encoding.UTF8.GetByteCount(candidates.GetRawText()) <= QueryCommandRunner.GotoAmbiguityCandidateByteLimit);
                Assert.Equal(candidates.GetArrayLength(), many.GetProperty("returned_count").GetInt32());
                Assert.Equal(25 - candidates.GetArrayLength(), many.GetProperty("omitted_count").GetInt32());
                Assert.True(many.GetProperty("candidates_truncated").GetBoolean());
                var narrowing = many.GetProperty("narrowing");
                Assert.Equal("--all", narrowing.GetProperty("all_option").GetString());
                Assert.Equal(3, narrowing.GetProperty("filter_options").GetArrayLength());
                Assert.All(candidates.EnumerateArray(), candidate =>
                {
                    Assert.Equal(JsonValueKind.Object, candidate.ValueKind);
                    Assert.True(candidate.TryGetProperty("path", out _));
                    Assert.True(candidate.TryGetProperty("line", out _));
                    Assert.True(candidate.TryGetProperty("kind", out _));
                    Assert.True(candidate.TryGetProperty("name", out _));
                });
                Assert.Contains(candidates.EnumerateArray(), candidate =>
                    candidate.GetProperty("path").GetString()?.StartsWith("src/SharedTarget", StringComparison.Ordinal) is true);
                Assert.All(candidates.EnumerateArray(), candidate => Assert.False(candidate.TryGetProperty("signature", out _)));
            }

            var humanArgs = ambiguityArgs.Where(arg => arg != "--json").ToArray();
            var (humanExit, humanStdout, humanStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                humanArgs,
                _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, humanExit);
            Assert.Equal(string.Empty, humanStdout);
            Assert.Contains($"Error [{CommandErrorCodes.QueryAmbiguous}]", humanStderr, StringComparison.Ordinal);
            Assert.Contains("goto found 25 matching definitions", humanStderr, StringComparison.Ordinal);
            Assert.Contains("Hint: narrow the query", humanStderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Usage: cdidx goto", humanStderr, StringComparison.Ordinal);

            var (literalExit, literalStdout, literalStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["--", "--json", "--db", dbPath, "--exact-name", "--kind", "property", "--lang", "css"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, literalExit);
            Assert.Equal(string.Empty, literalStdout);
            Assert.Contains($"Error [{CommandErrorCodes.QueryAmbiguous}]", literalStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunGoto_AmbiguityTotalsCountOnlyMaterializableDefinitions_Issue5232()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_goto_materializable_issue5232");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        for (var index = 0; index < 3; index++)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/Connect{index}.cs",
                "csharp",
                $"public class Connect{index} {{ public void Connect() {{ }} }}\n");
        }

        using (var db = new CodeIndex.Database.DbContext(CodeIndex.Database.DbOpenIntent.WriteIndex, dbPath))
        using (var command = db.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM chunks WHERE file_id = (SELECT id FROM files WHERE path = 'src/Connect2.cs')";
            command.ExecuteNonQuery();
        }

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
            ["Connect", "--db", dbPath, "--json", "--exact-name", "--kind", "function", "--lang", "csharp"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("match_count").GetInt32());
        Assert.Equal(2, root.GetProperty("total_count").GetInt32());
        Assert.Equal(2, root.GetProperty("returned_count").GetInt32());
        Assert.Equal(0, root.GetProperty("omitted_count").GetInt32());
        Assert.False(root.GetProperty("candidates_truncated").GetBoolean());
    }
}
