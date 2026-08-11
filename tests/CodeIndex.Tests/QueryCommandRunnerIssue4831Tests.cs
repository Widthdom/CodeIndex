using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerIssue4831Tests
{
    [Fact]
    public void RunSymbols_CSharpExactNameRejectsDeclarationContinuationPhantoms_Issue4831()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(
            "cdidx_symbols_csharp_declaration_continuation_issue4831");
        try
        {
            TestProjectHelper.WriteTextFile(
                projectRoot,
                "src/Fixture.cs",
                Issue4831CSharpFixture.Source);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                JsonOptions));
            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSymbols(
                    [
                        "GetIndexedLanguageCount",
                        "--db", dbPath,
                        "--json",
                        "--exact-name",
                        "--lang", "csharp",
                    ],
                    JsonOptions));
            var (outExitCode, outStdout, outStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSymbols(
                    ["out", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    JsonOptions));
            var (paramsExitCode, paramsStdout, paramsStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSymbols(
                    ["params", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    JsonOptions));

            var definitionRows = ParseJsonLines(definitionStdout);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(CommandExitCodes.Success, outExitCode);
            Assert.Equal(string.Empty, outStderr);
            Assert.Equal(CommandExitCodes.Success, paramsExitCode);
            Assert.Equal(string.Empty, paramsStderr);

            var definition = Assert.Single(definitionRows);
            Assert.Equal(
                "function",
                definition.RootElement.GetProperty("kind").GetString());
            Assert.Equal(
                70,
                definition.RootElement.GetProperty("start_line").GetInt32());
            Assert.Equal(
                75,
                definition.RootElement.GetProperty("end_line").GetInt32());
            Assert.Equal(
                "Scanner",
                definition.RootElement.GetProperty("container_name").GetString());
            Assert.Equal(string.Empty, outStdout);
            Assert.Equal(string.Empty, paramsStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
