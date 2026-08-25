using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSymbols_CSharpExactNameFindsMethodsAfterLiteralDelimiter_Issue5182()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_symbols_csharp_literal_delimiter_5182");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "Tail.cs"),
                """
                internal static class DbDebugExtensions
                {
                    public static void Trigger(string text)
                    {
                        var end = 0;
                        while (end < text.Length && text[end] != '(')
                            end++;
                    }

                    public static void ExecuteTrackedReader(this object command) { }
                    public static bool TrackedRead(this object reader) => true;
                }
                """);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            AssertExactFunction("ExecuteTrackedReader");
            AssertExactFunction("TrackedRead");

            void AssertExactFunction(string name)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                    [name, "--db", dbPath, "--json", "--exact-name", "--lang", "csharp"],
                    _jsonOptions));
                var row = Assert.Single(ParseJsonLines(stdout));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Equal(name, row.RootElement.GetProperty("name").GetString());
                Assert.Equal("function", row.RootElement.GetProperty("kind").GetString());
                Assert.Equal("DbDebugExtensions", row.RootElement.GetProperty("container_name").GetString());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
