using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerIssue5159Tests
{
    [Theory]
    [InlineData("service.Ping()", "Ping", 9, 0)]
    [InlineData("service.Ping(1, Create(2, 3))", "Ping", 9, 2)]
    public void UnambiguousInvocationArity_CountsSimplePositionalCalls_Issue5159(
        string context,
        string name,
        long column,
        int expected)
    {
        Assert.Equal(
            expected,
            CSharpTypeReferenceArity.GetUnambiguousInvocationArgumentCount(context, name, column));
    }

    [Theory]
    [InlineData("service.Ping(value: 1)")]
    [InlineData("service.Ping<int>(1)")]
    [InlineData("service.Ping(condition ? 1 : 2)")]
    [InlineData("service.Ping(a < b, c > d)")]
    public void UnambiguousInvocationArity_RejectsBindingSensitiveCalls_Issue5159(string context)
    {
        Assert.Null(CSharpTypeReferenceArity.GetUnambiguousInvocationArgumentCount(
            context,
            "Ping",
            9));
    }

    [Theory]
    [InlineData("public void Ping()", 0)]
    [InlineData("public static void Ping(int value, string text)", 2)]
    public void UnambiguousCallableArity_CountsRequiredNonGenericParameters_Issue5159(
        string signature,
        int expected)
    {
        Assert.Equal(
            expected,
            CSharpTypeReferenceArity.GetUnambiguousCallableParameterCount(
                signature,
                "Ping",
                "function"));
    }

    [Theory]
    [InlineData("public void Ping(int value = 0)")]
    [InlineData("public void Ping(params int[] values)")]
    [InlineData("public void Ping<T>(T value)")]
    [InlineData("public static void Ping(this Service service)")]
    public void UnambiguousCallableArity_RejectsBindingSensitiveDeclarations_Issue5159(
        string signature)
    {
        Assert.Null(CSharpTypeReferenceArity.GetUnambiguousCallableParameterCount(
            signature,
            "Ping",
            "function"));
    }

    [Fact]
    public void InspectSelector_RoundTripsOverloadIdentityAndKeepsAmbiguityTruthful_Issue5159()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_selector_issue5159");
        try
        {
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Fixture.cs",
                """
                namespace SelectorFixture;

                public sealed class Service
                {
                    public void Ping() { }
                    public void Ping(int value) { }
                    public void Optional(int value = 0) { }
                    public void Optional(string value = "") { }
                }

                public sealed class Caller
                {
                    public void Run(Service service)
                    {
                        service.Ping();
                        service.Ping();
                        service.Ping(1);
                        service.Ping(2);
                        service.Optional();
                    }
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                JsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (nameExitCode, nameStdout, nameStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["Ping", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--limit", "10"],
                    JsonOptions));
            using var nameDocument = ParseJsonOutput(nameStdout);
            var bundles = nameDocument.RootElement.GetProperty("candidate_bundles").EnumerateArray().ToArray();
            var zeroParameterBundle = Assert.Single(bundles.Where(bundle =>
                bundle.GetProperty("definition").GetProperty("signature").GetString()!.Contains("Ping()", StringComparison.Ordinal)));
            var oneParameterBundle = Assert.Single(bundles.Where(bundle =>
                bundle.GetProperty("definition").GetProperty("signature").GetString()!.Contains("Ping(int value)", StringComparison.Ordinal)));
            var zeroSelector = zeroParameterBundle.GetProperty("selector").GetProperty("selector").GetString()!;
            var oneSelector = oneParameterBundle.GetProperty("selector").GetProperty("selector").GetString()!;
            var zeroSymbolId = zeroParameterBundle.GetProperty("selector").GetProperty("symbol_id").GetInt64();
            var zeroGeneration = zeroParameterBundle.GetProperty("selector").GetProperty("generation_fingerprint").GetString()!;

            Assert.Equal(CommandExitCodes.Success, nameExitCode);
            Assert.Equal(string.Empty, nameStderr);
            Assert.NotEqual(zeroSelector, oneSelector);
            Assert.Equal($"id:{zeroSymbolId}@g:{zeroGeneration}", zeroSelector);
            Assert.Matches("^[0-9a-f]{16}$", zeroGeneration);
            Assert.True(zeroParameterBundle.GetProperty("identity_scoped").GetBoolean());
            Assert.True(oneParameterBundle.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal("exact_identity", zeroParameterBundle.GetProperty("identity_scope_reason").GetString());
            Assert.All(
                zeroParameterBundle.GetProperty("references").EnumerateArray(),
                reference => Assert.Contains("Ping()", reference.GetProperty("context").GetString(), StringComparison.Ordinal));
            Assert.All(
                oneParameterBundle.GetProperty("references").EnumerateArray(),
                reference => Assert.Matches(@"Ping\([12]\)", reference.GetProperty("context").GetString()));

            var selectorArgs = new[]
            {
                "--selector", zeroSelector,
                "--db", dbPath,
                "--json",
                "--lang", "csharp",
                "--limit", "1",
            };
            var (selectorExitCode, selectorStdout, selectorStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(selectorArgs, JsonOptions));
            using var selectorDocument = ParseJsonOutput(selectorStdout);
            var selected = selectorDocument.RootElement;
            var selectedBundle = Assert.Single(selected.GetProperty("candidate_bundles").EnumerateArray());
            var nextCursor = selectedBundle
                .GetProperty("graph_sections")
                .GetProperty("references")
                .GetProperty("next_cursor")
                .GetString();

            Assert.Equal(CommandExitCodes.Success, selectorExitCode);
            Assert.Equal(string.Empty, selectorStderr);
            Assert.Equal(zeroSelector, selected.GetProperty("query").GetString());
            Assert.Equal("single_candidate", selected.GetProperty("graph_scope").GetString());
            Assert.Equal(zeroSelector, selectedBundle.GetProperty("selector").GetProperty("selector").GetString());
            Assert.Single(selected.GetProperty("definitions").EnumerateArray());
            Assert.Contains("Ping()", selected.GetProperty("definitions")[0].GetProperty("signature").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(nextCursor));

            var (legacyExitCode, legacyStdout, legacyStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["--selector", $"id:{zeroSymbolId}", "--db", dbPath, "--json"],
                    JsonOptions));
            using var legacyDocument = ParseJsonOutput(legacyStdout);
            Assert.Equal(CommandExitCodes.Success, legacyExitCode);
            Assert.Equal(string.Empty, legacyStderr);
            Assert.Contains(
                "Ping()",
                legacyDocument.RootElement.GetProperty("definitions")[0].GetProperty("signature").GetString(),
                StringComparison.Ordinal);

            var (continuationExitCode, continuationStdout, continuationStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(selectorArgs.Concat(["--cursor", nextCursor!]).ToArray(), JsonOptions));
            using var continuationDocument = ParseJsonOutput(continuationStdout);
            var continuedReference = Assert.Single(
                continuationDocument.RootElement.GetProperty("references").EnumerateArray());
            Assert.Equal(CommandExitCodes.Success, continuationExitCode);
            Assert.Equal(string.Empty, continuationStderr);
            Assert.Contains("Ping()", continuedReference.GetProperty("context").GetString(), StringComparison.Ordinal);

            var (ambiguousExitCode, ambiguousStdout, ambiguousStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["Optional", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--limit", "10"],
                    JsonOptions));
            using var ambiguousDocument = ParseJsonOutput(ambiguousStdout);
            var ambiguousBundles = ambiguousDocument.RootElement.GetProperty("candidate_bundles").EnumerateArray().ToArray();
            Assert.Equal(CommandExitCodes.Success, ambiguousExitCode);
            Assert.Equal(string.Empty, ambiguousStderr);
            Assert.All(ambiguousBundles, bundle => Assert.False(bundle.GetProperty("identity_scoped").GetBoolean()));
            Assert.All(ambiguousBundles, bundle => Assert.Equal(
                "ambiguous_reference_candidates",
                bundle.GetProperty("identity_scope_reason").GetString()));

            Assert.Contains("--selector", CliFlagSchema.GetAcceptedFlagNamesForCommand("inspect"));

            var (missingExitCode, missingStdout, missingStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["--selector", "id:9223372036854775807", "--db", dbPath, "--json"],
                    JsonOptions));
            using var missingDocument = ParseJsonOutput(missingStdout);
            Assert.Equal(CommandExitCodes.NotFound, missingExitCode);
            Assert.Equal(string.Empty, missingStderr);
            Assert.Equal("E018_QUERY_NOT_FOUND", missingDocument.RootElement.GetProperty("error_code").GetString());

            var (invalidExitCode, _, invalidStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["--selector", "id:-1", "--db", dbPath],
                    JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, invalidExitCode);
            Assert.Contains("invalid symbol selector", invalidStderr, StringComparison.Ordinal);

            var otherProjectRoot = TestProjectHelper.CreateTempProject("cdidx_inspect_selector_cross_db_issue5159");
            try
            {
                TestProjectHelper.WriteTextFile(
                    otherProjectRoot,
                    "src/Other.cs",
                    "namespace OtherFixture; public sealed class Other { public void Ping() { } }");
                var (otherIndexExitCode, _, otherIndexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                    [otherProjectRoot, "--json", "--quiet"],
                    JsonOptions));
                var otherDbPath = Path.Combine(otherProjectRoot, ".cdidx", "codeindex.db");
                Assert.Equal(CommandExitCodes.Success, otherIndexExitCode);
                Assert.Equal(string.Empty, otherIndexStderr);

                var (crossDbExitCode, crossDbStdout, crossDbStderr) = CaptureConsole(() =>
                    QueryCommandRunner.RunInspect(
                        ["--selector", zeroSelector, "--db", otherDbPath, "--json"],
                        JsonOptions));
                using var crossDbDocument = ParseJsonOutput(crossDbStdout);
                Assert.Equal(CommandExitCodes.NotFound, crossDbExitCode);
                Assert.Equal(string.Empty, crossDbStderr);
                Assert.Equal("E018_QUERY_NOT_FOUND", crossDbDocument.RootElement.GetProperty("error_code").GetString());
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(otherProjectRoot);
            }

            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/GenerationChange.cs",
                "namespace SelectorFixture; public sealed class GenerationChange { }");
            var (updateExitCode, _, updateStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                JsonOptions));
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(string.Empty, updateStderr);

            var (staleExitCode, staleStdout, staleStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["--selector", zeroSelector, "--db", dbPath, "--json"],
                    JsonOptions));
            using var staleDocument = ParseJsonOutput(staleStdout);
            Assert.Equal(CommandExitCodes.NotFound, staleExitCode);
            Assert.Equal(string.Empty, staleStderr);
            Assert.Equal("E018_QUERY_NOT_FOUND", staleDocument.RootElement.GetProperty("error_code").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
