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

            var (definitionSummaryExitCode, definitionSummaryStdout, definitionSummaryStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Widget", "--db", dbPath, "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, definitionSummaryExitCode);
            Assert.Contains("src/A.Widget.cs", definitionSummaryStdout);
            Assert.Contains("1 of 2 logical definitions shown; 3 total physical declaration sites", definitionSummaryStderr);

            var (symbolSummaryExitCode, symbolSummaryStdout, symbolSummaryStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Widget", "--db", dbPath, "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, symbolSummaryExitCode);
            Assert.Contains("src/A.Widget.cs", symbolSummaryStdout);
            Assert.Contains("1 of 2 logical symbols shown; 3 total physical declaration sites", symbolSummaryStderr);

            var (bareCountExitCode, bareCountStdout, bareCountStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["@", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--count"],
                _jsonOptions));
            using var bareCountDocument = ParseJsonOutput(bareCountStdout);

            Assert.Equal(CommandExitCodes.Success, bareCountExitCode);
            Assert.Equal(string.Empty, bareCountStderr);
            Assert.Equal(0, bareCountDocument.RootElement.GetProperty("logical_count").GetInt32());
            Assert.Equal(0, bareCountDocument.RootElement.GetProperty("physical_count").GetInt32());

            var (bareSymbolCountExitCode, bareSymbolCountStdout, bareSymbolCountStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["@", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--group-partials", "--count"],
                _jsonOptions));
            using var bareSymbolCountDocument = ParseJsonOutput(bareSymbolCountStdout);

            Assert.Equal(CommandExitCodes.Success, bareSymbolCountExitCode);
            Assert.Equal(string.Empty, bareSymbolCountStderr);
            Assert.True(bareSymbolCountDocument.RootElement.GetProperty("group_partials").GetBoolean());
            Assert.Equal(0, bareSymbolCountDocument.RootElement.GetProperty("logical_count").GetInt32());
            Assert.Equal(0, bareSymbolCountDocument.RootElement.GetProperty("physical_count").GetInt32());

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
                ["Widget", "--db", dbPath, "--json", "--lang", "csharp", "--max-hops", "0", "--limit", "1"],
                _jsonOptions));
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impact = impactDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            Assert.Equal(3, impact.GetProperty("definition_count").GetInt32());
            Assert.Equal(2, impact.GetProperty("logical_definition_count").GetInt32());
            Assert.Equal(1, impact.GetProperty("definition_output_count").GetInt32());
            Assert.True(impact.GetProperty("definitions_collapsed").GetBoolean());
            Assert.Equal("logical_partial_families", impact.GetProperty("definition_result_scope").GetString());
            Assert.True(impact.GetProperty("definitions_truncated").GetBoolean());
            Assert.Equal(1, impact.GetProperty("definitions_omitted").GetInt32());
            Assert.Equal(2, impact.GetProperty("definitions")[0].GetProperty("definition_sites").GetInt32());

            var (pathModeExitCode, pathModeStdout, pathModeStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["--path", "src/A.Widget.cs", "--line", "3", "--db", dbPath, "--group-partials"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, pathModeExitCode);
            Assert.Equal(string.Empty, pathModeStdout);
            Assert.Contains("--group-partials is only supported for symbol-mode inspect queries", pathModeStderr);

            var (positionalPathExitCode, positionalPathStdout, positionalPathStderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
                ["src/A.Widget.cs", "--db", dbPath, "--json", "--group-partials"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, positionalPathExitCode);
            Assert.Equal(string.Empty, positionalPathStdout);
            Assert.Contains("--group-partials is only supported for symbol-mode inspect queries", positionalPathStderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GroupPartials_UsesQualifiedPartialIdentityAndPreservesTotalsAndSorts_Issue4566()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_grouping_adversarial_issue4566");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/One.NativeMethods.cs",
                "csharp",
                """
                namespace Demo.One;
                public class Host
                {
                    private static class NativeMethods { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Two.NativeMethods.cs",
                "csharp",
                """
                namespace Demo.Two;
                public class Host
                {
                    private static class NativeMethods { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Ranked.cs",
                "csharp",
                """
                namespace Demo.Ranking;
                public partial class Ranked
                {
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.PartialHost.cs",
                "csharp",
                """
                namespace Demo.Nested;
                public partial class PartialHost
                {
                    private class FirstNested { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.PartialHost.cs",
                "csharp",
                """
                namespace Demo.Nested;
                public partial class PartialHost
                {
                    private class SecondNested { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.Ranked.cs",
                "csharp",
                """
                namespace Demo.Ranking;
                public partial class Ranked
                {
                    public void One() { }
                    public void Two() { }
                    public void Three() { }
                    public void Four() { }
                    public void Five() { }
                }
                """);
            for (var i = 0; i < 51; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Wide.{i:D2}.cs",
                    "csharp",
                    """
                    namespace Demo.Wide;
                    public partial class Wide
                    {
                    }
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            var (nestedExitCode, nestedStdout, nestedStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["NativeMethods", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var nestedDocument = ParseJsonOutput(nestedStdout);
            var nestedRows = nestedDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, nestedExitCode);
            Assert.Equal(string.Empty, nestedStderr);
            Assert.Equal(2, nestedRows.Count);
            Assert.All(nestedRows, row => Assert.False(row.TryGetProperty("definition_sites", out _)));

            var (partialHostExitCode, partialHostStdout, partialHostStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json=array", "--lang", "csharp", "--kind", "class", "--path", "src/*.PartialHost.cs", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var partialHostDocument = ParseJsonOutput(partialHostStdout);
            var partialHostRows = partialHostDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, partialHostExitCode);
            Assert.Equal(string.Empty, partialHostStderr);
            Assert.Equal(3, partialHostRows.Count);
            Assert.Contains(partialHostRows, row => row.GetProperty("name").GetString() == "FirstNested" && !row.TryGetProperty("definition_sites", out _));
            Assert.Contains(partialHostRows, row => row.GetProperty("name").GetString() == "SecondNested" && !row.TryGetProperty("definition_sites", out _));
            Assert.Contains(partialHostRows, row => row.GetProperty("name").GetString() == "PartialHost" && row.GetProperty("definition_sites").GetInt32() == 2);

            foreach (var (sort, field) in new[]
            {
                ("hotspot", "hotspot_score"),
                ("references", "reference_count"),
                ("size", "size_lines"),
                ("complexity", "complexity_score"),
            })
            {
                var (sortExitCode, sortStdout, sortStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                    ["--db", dbPath, "--json=array", "--lang", "csharp", "--kind", "class", "--group-partials", "--sort", sort, "--limit", "200"],
                    _jsonOptions));
                using var sortDocument = ParseJsonOutput(sortStdout);
                var values = sortDocument.RootElement.EnumerateArray()
                    .Select(row => row.GetProperty(field).GetDouble())
                    .ToList();

                Assert.Equal(CommandExitCodes.Success, sortExitCode);
                Assert.Equal(string.Empty, sortStderr);
                Assert.Equal(values.OrderByDescending(value => value), values);
            }

            var (pathSortExitCode, pathSortStdout, pathSortStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["--db", dbPath, "--json=array", "--lang", "csharp", "--kind", "class", "--group-partials", "--sort", "path", "--limit", "200"],
                _jsonOptions));
            using var pathSortDocument = ParseJsonOutput(pathSortStdout);
            var paths = pathSortDocument.RootElement.EnumerateArray()
                .Select(row => row.GetProperty("path").GetString()!)
                .ToList();

            Assert.Equal(CommandExitCodes.Success, pathSortExitCode);
            Assert.Equal(string.Empty, pathSortStderr);
            Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);

            var (rankedExitCode, rankedStdout, rankedStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Ranked", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--group-partials", "--sort", "size", "--limit", "1"],
                _jsonOptions));
            using var rankedDocument = ParseJsonOutput(rankedStdout);
            var ranked = Assert.Single(rankedDocument.RootElement.EnumerateArray().ToList());

            Assert.Equal(CommandExitCodes.Success, rankedExitCode);
            Assert.Equal(string.Empty, rankedStderr);
            Assert.Equal("src/A.Ranked.cs", ranked.GetProperty("path").GetString());
            Assert.Equal(2, ranked.GetProperty("definition_sites").GetInt32());
            Assert.True(ranked.GetProperty("size_lines").GetInt32() > ranked.GetProperty("end_line").GetInt32() - ranked.GetProperty("start_line").GetInt32() + 1);

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Wide", "--db", dbPath, "--json", "--lang", "csharp", "--max-hops", "0", "--limit", "1"],
                _jsonOptions));
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impact = impactDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            Assert.Equal(51, impact.GetProperty("definition_count").GetInt32());
            Assert.Equal(1, impact.GetProperty("logical_definition_count").GetInt32());
            Assert.Equal(1, impact.GetProperty("definition_output_count").GetInt32());
            Assert.Equal(51, impact.GetProperty("definitions")[0].GetProperty("definition_sites").GetInt32());
            Assert.False(impact.TryGetProperty("definitions_truncated", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
