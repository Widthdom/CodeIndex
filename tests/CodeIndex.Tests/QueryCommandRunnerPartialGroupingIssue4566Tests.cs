using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void DefinitionSymbolsInspectAndImpact_GroupPartialTypesConsistently_Issue4566()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_grouping_issue4566");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Widget.cs",
                "csharp",
                """
                namespace Demo.One;

                public partial class Widget
                {
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.Widget.cs",
                "csharp",
                """
                namespace Demo.One;

                public partial class Widget
                {
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/C.Widget.cs",
                "csharp",
                """
                namespace Demo.Two;

                public class Widget
                {
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (physicalCountExitCode, physicalCountStdout, physicalCountStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--count"],
                _jsonOptions));
            using var physicalCountDocument = ParseJsonOutput(physicalCountStdout);

            Assert.Equal(CommandExitCodes.Success, physicalCountExitCode);
            Assert.Equal(string.Empty, physicalCountStderr);
            Assert.Equal(3, physicalCountDocument.RootElement.GetProperty("count").GetInt32());

            var (groupedCountExitCode, groupedCountStdout, groupedCountStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--count"],
                _jsonOptions));
            using var groupedCountDocument = ParseJsonOutput(groupedCountStdout);
            var groupedCount = groupedCountDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, groupedCountExitCode);
            Assert.Equal(string.Empty, groupedCountStderr);
            Assert.Equal(2, groupedCount.GetProperty("count").GetInt32());
            Assert.Equal(2, groupedCount.GetProperty("logical_count").GetInt32());
            Assert.Equal(3, groupedCount.GetProperty("physical_count").GetInt32());
            Assert.Equal(3, groupedCount.GetProperty("physical_file_count").GetInt32());
            Assert.Equal("logical_partial_families", groupedCount.GetProperty("count_kind").GetString());
            Assert.True(groupedCount.GetProperty("partials_grouped").GetBoolean());

            var (bareCountExitCode, bareCountStdout, bareCountStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["@", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--count"],
                _jsonOptions));
            using var bareCountDocument = ParseJsonOutput(bareCountStdout);

            Assert.Equal(CommandExitCodes.Success, bareCountExitCode);
            Assert.Equal(string.Empty, bareCountStderr);
            Assert.Equal(0, bareCountDocument.RootElement.GetProperty("logical_count").GetInt32());
            Assert.Equal(0, bareCountDocument.RootElement.GetProperty("physical_count").GetInt32());

            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "2"],
                _jsonOptions));
            var definitionRows = ParseJsonLines(definitionStdout);
            try
            {
                Assert.Equal(CommandExitCodes.Success, definitionExitCode);
                Assert.Equal(string.Empty, definitionStderr);
                Assert.Equal(2, definitionRows.Count);
                var groupedDefinition = Assert.Single(definitionRows, row => row.RootElement.TryGetProperty("definition_sites", out _));
                Assert.Equal("src/A.Widget.cs", groupedDefinition.RootElement.GetProperty("path").GetString());
                Assert.Equal(2, groupedDefinition.RootElement.GetProperty("definition_sites").GetInt32());
            }
            finally
            {
                foreach (var row in definitionRows)
                    row.Dispose();
            }

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Widget", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "2"],
                _jsonOptions));
            using var symbolsDocument = ParseJsonOutput(symbolsStdout);
            var symbolRows = symbolsDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(2, symbolRows.Count);
            var groupedSymbol = Assert.Single(symbolRows, row => row.TryGetProperty("definition_sites", out _));
            Assert.Equal("src/A.Widget.cs", groupedSymbol.GetProperty("path").GetString());
            Assert.Equal(2, groupedSymbol.GetProperty("definition_sites").GetInt32());

            var (symbolCountExitCode, symbolCountStdout, symbolCountStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--count"],
                _jsonOptions));
            using var symbolCountDocument = ParseJsonOutput(symbolCountStdout);

            Assert.Equal(CommandExitCodes.Success, symbolCountExitCode);
            Assert.Equal(string.Empty, symbolCountStderr);
            Assert.Equal(2, symbolCountDocument.RootElement.GetProperty("logical_count").GetInt32());
            Assert.Equal(3, symbolCountDocument.RootElement.GetProperty("physical_count").GetInt32());

            var (inspectExitCode, inspectStdout, inspectStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "2"],
                _jsonOptions));
            using var inspectDocument = ParseJsonOutput(inspectStdout);
            var inspect = inspectDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, inspectExitCode);
            Assert.Equal(string.Empty, inspectStderr);
            Assert.Equal(2, inspect.GetProperty("logical_definition_output_count").GetInt32());
            Assert.Equal(3, inspect.GetProperty("physical_definition_output_count").GetInt32());
            Assert.True(inspect.GetProperty("definitions_collapsed").GetBoolean());
            Assert.Equal(2, inspect.GetProperty("definitions").GetArrayLength());

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Widget", "--db", dbPath, "--json", "--lang", "csharp", "--max-hops", "0", "--limit", "2"],
                _jsonOptions));
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impact = impactDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            Assert.Equal(3, impact.GetProperty("definition_count").GetInt32());
            Assert.Equal(2, impact.GetProperty("definition_output_count").GetInt32());
            Assert.True(impact.GetProperty("definitions_collapsed").GetBoolean());
            Assert.Equal("logical_partial_families", impact.GetProperty("definition_result_scope").GetString());

            var (pathModeExitCode, pathModeStdout, pathModeStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["--path", "src/A.Widget.cs", "--line", "3", "--db", dbPath, "--group-partials"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, pathModeExitCode);
            Assert.Equal(string.Empty, pathModeStdout);
            Assert.Contains("--group-partials is only supported for symbol-mode inspect queries", pathModeStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
