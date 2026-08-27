using CodeIndex.Cli;
using CodeIndex.Database;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void CSharpMultilineTestAttributes_PersistAcrossSymbolCommandsAndTestFilters_Issue5192()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_csharp_multiline_test_attributes_5192");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "tests/CalculatorTests.cs",
                "csharp",
                """
                namespace Demo;

                public class CalculatorTests
                {
                    [Theory]
                    [InlineData(
                        1,
                        "closing ] and fake [Fact]")]
                    private void MultilineTheory(int value, string text) { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Calculator.cs",
                "csharp",
                """
                namespace Demo;

                public sealed class Fact { }
                public sealed class MetadataAttribute<TLeft, TRight> : System.Attribute { }

                public class Calculator
                {
                    private void HelperMethod() { }

                    [Metadata<
                        string,
                        Fact>]
                    private void GenericAttributeHelper() { }

                    private static int FactValue => 1;
                    private int[][] NestedCollectionInitializer =
                    {
                        [
                            FactValue
                        ]
                    }; private void InitializerFollowingHelper() { }
                }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                new DbWriter(db.Connection).MarkGraphReady();
            }

            var (outlineExitCode, outlineStdout, outlineStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunOutline(
                    ["tests/CalculatorTests.cs", "--db", dbPath, "--json"],
                    _jsonOptions));
            using var outlineDocument = ParseJsonOutput(outlineStdout);
            var outlineTestMethod = Assert.Single(
                outlineDocument.RootElement
                    .GetProperty("symbols")
                    .EnumerateArray()
                    .Where(symbol => symbol.GetProperty("name").GetString() == "MultilineTheory"));

            Assert.Equal(CommandExitCodes.Success, outlineExitCode);
            Assert.Equal(string.Empty, outlineStderr);
            Assert.Equal("test.method", outlineTestMethod.GetProperty("kind").GetString());

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunSymbols(
                    ["MultilineTheory", "--db", dbPath, "--json", "--exact-name", "--kind", "test.method"],
                    _jsonOptions));
            var symbolRows = ParseJsonLines(symbolsStdout)
                .Select(document => document.RootElement)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal("test.method", Assert.Single(symbolRows).GetProperty("kind").GetString());

            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunInspect(
                    ["MultilineTheory", "--db", dbPath, "--json", "--exact"],
                    _jsonOptions));
            using var inspectDocument = ParseJsonOutput(inspectStdout);
            var inspectedDefinition = Assert.Single(
                inspectDocument.RootElement.GetProperty("definitions").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Equal("test.method", inspectedDefinition.GetProperty("kind").GetString());

            var (unusedExitCode, unusedStdout, unusedStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunUnused(
                    ["--db", dbPath, "--json", "--all", "--lang", "csharp", "--limit", "50"],
                    _jsonOptions));
            using var unusedDocument = ParseJsonOutput(unusedStdout);
            var unusedNames = unusedDocument.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, unusedExitCode);
            Assert.Equal(string.Empty, unusedStderr);
            Assert.Contains("MultilineTheory", unusedNames);
            Assert.Contains("HelperMethod", unusedNames);

            var (excludedExitCode, excludedStdout, excludedStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunUnused(
                    ["--db", dbPath, "--json", "--all", "--lang", "csharp", "--exclude-tests", "--limit", "50"],
                    _jsonOptions));
            using var excludedDocument = ParseJsonOutput(excludedStdout);
            var excludedNames = excludedDocument.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(symbol => symbol.GetProperty("name").GetString())
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, excludedExitCode);
            Assert.Equal(string.Empty, excludedStderr);
            Assert.DoesNotContain("MultilineTheory", excludedNames);
            Assert.Contains("HelperMethod", excludedNames);
            Assert.Contains("GenericAttributeHelper", excludedNames);
            Assert.Contains("InitializerFollowingHelper", excludedNames);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
