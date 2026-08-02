using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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
            Assert.True(inspect.TryGetProperty("graph_sections", out var groupedGraphSections));
            Assert.True(groupedGraphSections.TryGetProperty("references", out _));
            Assert.True(groupedGraphSections.TryGetProperty("callers", out _));
            Assert.True(groupedGraphSections.TryGetProperty("callees", out _));

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
                    i == 50
                        ? """
                          namespace Demo.Wide;
                          public partial class Wide : BaseWide
                          {
                          }
                          """
                        : """
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
            var impactDefinition = impact.GetProperty("definitions")[0];
            Assert.Equal(51, impactDefinition.GetProperty("definition_sites").GetInt32());
            Assert.StartsWith("partial:", impactDefinition.GetProperty("partial_family_id").GetString());
            Assert.Equal("semantic_declaration", impactDefinition.GetProperty("representative_reason").GetString());
            Assert.Equal(50, impactDefinition.GetProperty("family_members").GetArrayLength());
            Assert.True(impactDefinition.GetProperty("family_members_truncated").GetBoolean());
            Assert.Contains(
                impactDefinition.GetProperty("family_members").EnumerateArray(),
                member => member.GetProperty("path").GetString() == "src/Wide.50.cs"
                    && member.GetProperty("representative").GetBoolean());
            Assert.False(impact.TryGetProperty("definitions_truncated", out _));

            var (wideSymbolsExitCode, wideSymbolsStdout, wideSymbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Wide", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "1"],
                _jsonOptions));
            using var wideSymbolsDocument = ParseJsonOutput(wideSymbolsStdout);
            var wideSymbol = Assert.Single(wideSymbolsDocument.RootElement.EnumerateArray().ToList());

            Assert.Equal(CommandExitCodes.Success, wideSymbolsExitCode);
            Assert.Equal(string.Empty, wideSymbolsStderr);
            Assert.Equal(50, wideSymbol.GetProperty("family_members").GetArrayLength());
            Assert.True(wideSymbol.GetProperty("family_members_truncated").GetBoolean());
            Assert.Equal("src/Wide.50.cs", wideSymbol.GetProperty("path").GetString());
            Assert.Contains(
                wideSymbol.GetProperty("family_members").EnumerateArray(),
                member => member.GetProperty("path").GetString() == "src/Wide.50.cs"
                    && member.GetProperty("representative").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_UsesSemanticRulesAndExposesFamilyNavigation_Issue4914()
    {
        Assert.Equal(13, DbContext.HotspotFamilyVersion);
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_canonical_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Widget.Split.cs",
                "csharp",
                """
                namespace Demo;

                [System.Obsolete]
                public partial class Widget : BaseWidget
                {
                }
                """,
                isGenerated: true);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Widget.cs",
                "csharp",
                """
                namespace Demo;

                public partial class Widget
                {
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Customer.g.cs",
                "csharp",
                """
                namespace Demo;
                public partial record Customer : System.IComparable<Customer>
                {
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Customer.cs",
                "csharp",
                """
                namespace Demo;
                public partial record Customer : System.IComparable<Customer>
                {
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Controller.cs",
                "csharp",
                """
                using @global = Demo;
                namespace Demo;
                public class Item { }
                public class item { }
                public class Result<T> { }

                public partial class Controller
                {
                    partial void OnReady([P] int declarationValue = 0);
                    partial void Alias(int declarationValue);
                    partial void Defaults(bool flag = 1 < 2, int count = 0);
                    partial void Quoted(string text = ")", int count = 0);
                    partial void Escaped(@Item declarationValue);
                    partial void @event(int declarationValue);
                    partial void Dynamic(dynamic declarationValue);
                    partial /* CommentName( */ void CommentName(int declarationValue);
                    partial void CommentGap /* identity trivia */ (int value);
                    partial void GenericGap<T> /* identity trivia */ (T value);
                    partial void AttrString([Marker("/*")] int declarationValue);
                    partial void Run(Item declarationValue);
                    partial void Run(item declarationValue);
                    partial void Rooted(Item declarationValue);
                    partial void Rooted(global::Item declarationValue);
                    partial void Shadowed(System.Int32 declarationValue);
                    partial void Shadowed(int declarationValue);
                    partial void ReferenceNullable(string? declarationValue);
                    partial void ValueNullable(int? declarationValue);
                    partial void QualifiedGeneric<T>(N.T declarationValue);
                    partial void QualifiedGeneric<U>(N.U declarationValue);
                    partial void VerbatimGlobal(@global::System.Int32 declarationValue);
                    partial void VerbatimGlobal(global::System.Int32 declarationValue);
                    private partial Result<int> Result();
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Root.Item.cs",
                "csharp",
                "public class Item { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/QualifiedGenericTypes.cs",
                "csharp",
                "namespace N { public class T { } public class U { } }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Controller.cs",
                "csharp",
                """
                using @global = Demo;
                namespace Demo;
                public partial class Controller
                {
                    partial void OnReady(int implementationValue)
                    {
                    }

                    partial void Alias(global::@System.@Int32 implementationValue) { }
                    partial void Defaults(bool flag, int count) { }
                    partial void Quoted(string text, int count) { }
                    partial void Escaped(Item implementationValue) { }
                    partial void @event(int implementationValue) { }
                    partial void Dynamic(object implementationValue) { }
                    partial void CommentName(int implementationValue) { }
                    partial void CommentGap(int value) { }
                    partial void GenericGap<T>(T value) { }
                    partial void AttrString(int implementationValue) { }
                    partial void Run(Item implementationValue) { }
                    partial void Run(item implementationValue) { }
                    partial void Rooted(Item implementationValue) { }
                    partial void Rooted(global::Item implementationValue) { }
                    partial void Shadowed(System.Int32 implementationValue) { }
                    partial void Shadowed(int implementationValue) { }
                    partial void ReferenceNullable(string implementationValue) { }
                    partial void ValueNullable(global::System.Nullable<int> implementationValue) { }
                    partial void QualifiedGeneric<T>(N.T implementationValue) { }
                    partial void QualifiedGeneric<U>(N.U implementationValue) { }
                    partial void VerbatimGlobal(@global::System.Int32 implementationValue) { }
                    partial void VerbatimGlobal(global::System.Int32 implementationValue) { }
                    private partial Result<global::@System.@Int32> Result() => new();
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Demo.System.Int32.cs",
                "csharp",
                """
                namespace Demo.System;
                public class Int32 { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "scripts/functions.sh",
                "shell",
                """
                function hello {
                  echo hi
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.GenericContainers.cs",
                "csharp",
                """
                namespace Demo;
                public
                partial // one-arity host modifier
                class GenericHost<T>
                {
                    partial // declaration modifier
                    void ContainerMethod();
                }

                public
                partial /* two-arity host modifier */
                class GenericHost<T, U>
                {
                    partial /* declaration modifier */
                    void ContainerMethod();
                }

                public partial class Outer<T>
                {
                    public partial class Nested { }
                }

                public partial class Outer<T, U>
                {
                    public partial class Nested { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.GenericContainers.cs",
                "csharp",
                """
                namespace Demo;
                public
                partial // one-arity host modifier
                class GenericHost<T>
                {
                    partial // implementation modifier
                    void ContainerMethod() { }
                }

                public
                partial /* two-arity host modifier */
                class GenericHost<T, U>
                {
                    partial /* implementation modifier */
                    void ContainerMethod() { }
                }

                public partial class Outer<T>
                {
                    public partial class Nested { }
                }

                public partial class Outer<T, U>
                {
                    public partial class Nested { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.Equal.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Equal { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Equal.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Equal { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Profile.Split.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Profile { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Profile.Primary.cs",
                "csharp",
                """
                namespace Demo;
                [System.Serializable]
                public partial class Profile : BaseProfile { }
                """);
            MarkGraphAndFoldReady(dbPath);

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var rename = connection.CreateCommand();
                rename.CommandText = "UPDATE files SET path = 'src/ZZZ.Profile.Primary.cs' WHERE path = 'src/A.Profile.Primary.cs'";
                Assert.Equal(1, rename.ExecuteNonQuery());
            }

            var widget = RunGroupedSymbol(dbPath, "Widget", "class");
            Assert.Equal("src/Z.Widget.cs", widget.GetProperty("path").GetString());
            Assert.Equal("non_generated_source", widget.GetProperty("representative_reason").GetString());
            Assert.StartsWith("partial:", widget.GetProperty("partial_family_id").GetString());
            Assert.Equal(2, widget.GetProperty("definition_sites").GetInt32());
            var widgetMembers = widget.GetProperty("family_members").EnumerateArray().ToList();
            Assert.Equal(2, widgetMembers.Count);
            Assert.All(widgetMembers, member => Assert.Equal(21, member.GetProperty("start_column").GetInt32()));
            Assert.Contains(widgetMembers, member => member.GetProperty("path").GetString() == "src/A.Widget.Split.cs" && member.GetProperty("generated").GetBoolean());
            Assert.Single(widgetMembers, member => member.TryGetProperty("representative", out var representative) && representative.GetBoolean());

            var customer = RunGroupedSymbol(dbPath, "Customer", "class");
            Assert.Equal("src/Z.Customer.cs", customer.GetProperty("path").GetString());
            Assert.Equal("non_generated_source", customer.GetProperty("representative_reason").GetString());
            Assert.Equal(22, customer.GetProperty("start_column").GetInt32());
            Assert.All(
                customer.GetProperty("family_members").EnumerateArray(),
                member => Assert.Equal(22, member.GetProperty("start_column").GetInt32()));

            var onReady = RunGroupedSymbol(dbPath, "OnReady", "function");
            Assert.Equal("src/Z.Controller.cs", onReady.GetProperty("path").GetString());
            Assert.Equal("implementation_body", onReady.GetProperty("representative_reason").GetString());
            Assert.Equal(2, onReady.GetProperty("definition_sites").GetInt32());
            Assert.All(
                onReady.GetProperty("family_members").EnumerateArray(),
                member => Assert.Equal(17, member.GetProperty("start_column").GetInt32()));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void OnReady([P] int declarationValue = 0);",
                    "OnReady",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void OnReady(int implementationValue) { }",
                    "OnReady",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void OnReady(int value);",
                    "OnReady",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void OnReady(string value);",
                    "OnReady",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial T Transform<T>(T declarationValue);",
                    "Transform",
                    "T"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial TResult Transform<TResult>(TResult implementationValue) { }",
                    "Transform",
                    "TResult"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void QualifiedGeneric<T>(N.T declarationValue);",
                    "QualifiedGeneric",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void QualifiedGeneric<U>(N.U implementationValue) { }",
                    "QualifiedGeneric",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Collect(params int[] declarationValues);",
                    "Collect",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Collect(int[] implementationValues) { }",
                    "Collect",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Mutate(int value);",
                    "Mutate",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Mutate(ref int value);",
                    "Mutate",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Alias(int value);",
                    "Alias",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Alias(global::@System.@Int32 value) { }",
                    "Alias",
                    "global::@System.@Void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Alias(int value);",
                    "Alias",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Alias(System.Int32 value) { }",
                    "Alias",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void ReferenceNullable(string? value);",
                    "ReferenceNullable",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void ReferenceNullable(string value) { }",
                    "ReferenceNullable",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void ValueNullable(int? value);",
                    "ValueNullable",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void ValueNullable(global::System.Nullable<int> value) { }",
                    "ValueNullable",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void ValueNullable(int value);",
                    "ValueNullable",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void ValueNullable(int? value) { }",
                    "ValueNullable",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Global(global::System.Uri declarationValue);",
                    "Global",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Global(System.Uri implementationValue) { }",
                    "Global",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void VerbatimGlobal(@global::System.Int32 declarationValue);",
                    "VerbatimGlobal",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void VerbatimGlobal(global::System.Int32 implementationValue) { }",
                    "VerbatimGlobal",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Rooted(Item declarationValue);",
                    "Rooted",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Rooted(global::Item implementationValue) { }",
                    "Rooted",
                    "void"));
            var (shadowedExitCode, shadowedStdout, shadowedStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Shadowed", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var shadowedDocument = ParseJsonOutput(shadowedStdout);
            var shadowedFamilies = shadowedDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, shadowedExitCode);
            Assert.Equal(string.Empty, shadowedStderr);
            Assert.Equal(2, shadowedFamilies.Count);
            Assert.All(shadowedFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Equal(
                2,
                shadowedFamilies
                    .Select(family => family.GetProperty("partial_family_id").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Defaults(bool flag = 1 < 2, int count = 0);",
                    "Defaults",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Defaults(bool flag, int count) { }",
                    "Defaults",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Decorated([P(1 < 2)] int declarationValue, string secondValue);",
                    "Decorated",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Decorated(int implementationValue, string secondValue) { }",
                    "Decorated",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    """partial void Quoted(string text = ")", int count = 0);""",
                    "Quoted",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Quoted(string text, int count) { }",
                    "Quoted",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Commented(int value = (/* ) */ 0), string text = \"x\");",
                    "Commented",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Commented(int value, string text) { }",
                    "Commented",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Escaped(@Item declarationValue);",
                    "Escaped",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Escaped(Item implementationValue) { }",
                    "Escaped",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void @event(int declarationValue);",
                    "event",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void @event(int implementationValue) { }",
                    "event",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Dynamic(dynamic declarationValue);",
                    "Dynamic",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Dynamic(object implementationValue) { }",
                    "Dynamic",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void CommentTrivia(int /* declaration */ value);",
                    "CommentTrivia",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void CommentTrivia(int implementationValue /* implementation */) { }",
                    "CommentTrivia",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial /* CommentName( */ void CommentName(int declarationValue);",
                    "CommentName",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void CommentName(int implementationValue) { }",
                    "CommentName",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void CommentGap /* identity trivia */ (int value);",
                    "CommentGap",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void CommentGap(int value) { }",
                    "CommentGap",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void GenericGap<T> /* identity trivia */ (T value);",
                    "GenericGap",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void GenericGap<T>(T value) { }",
                    "GenericGap",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void AttrString([Marker(\"/*\")] int declarationValue);",
                    "AttrString",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void AttrString(int implementationValue) { }",
                    "AttrString",
                    "void"));
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void RawDefault(string declarationValue = \"\"\"\"a,b\"\"\"\", int count = 0);",
                    "RawDefault",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void RawDefault(string implementationValue, int count) { }",
                    "RawDefault",
                    "void"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Run(Item value);",
                    "Run",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Run(item value) { }",
                    "Run",
                    "void"));
            Assert.NotNull(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "private partial Result<int> Result();",
                    "Result",
                    "Result<int>"));

            var alias = RunGroupedSymbol(dbPath, "Alias", "function");
            Assert.Equal(2, alias.GetProperty("definition_sites").GetInt32());

            var defaults = RunGroupedSymbol(dbPath, "Defaults", "function");
            Assert.Equal(2, defaults.GetProperty("definition_sites").GetInt32());

            var quoted = RunGroupedSymbol(dbPath, "Quoted", "function");
            Assert.Equal(2, quoted.GetProperty("definition_sites").GetInt32());

            var escaped = RunGroupedSymbol(dbPath, "Escaped", "function");
            Assert.Equal(2, escaped.GetProperty("definition_sites").GetInt32());

            var verbatim = RunGroupedSymbol(dbPath, "event", "function");
            Assert.Equal(2, verbatim.GetProperty("definition_sites").GetInt32());

            var (verbatimGotoExitCode, verbatimGotoStdout, verbatimGotoStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["event", "--db", dbPath, "--exact-name", "--lang", "csharp", "--kind", "function", "--include-generated"],
                _jsonOptions));
            using var verbatimGotoDocument = ParseJsonOutput(verbatimGotoStdout);
            var verbatimGoto = verbatimGotoDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, verbatimGotoExitCode);
            Assert.Equal(string.Empty, verbatimGotoStderr);
            Assert.Equal(18, verbatimGoto.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
            Assert.Equal(23, verbatimGoto.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
            Assert.All(
                verbatimGoto.GetProperty("family_members").EnumerateArray(),
                member =>
                {
                    Assert.Equal(18, member.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
                    Assert.Equal(23, member.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
                });

            var dynamicAlias = RunGroupedSymbol(dbPath, "Dynamic", "function");
            Assert.Equal(2, dynamicAlias.GetProperty("definition_sites").GetInt32());

            var commentName = RunGroupedSymbol(dbPath, "CommentName", "function");
            Assert.Equal(2, commentName.GetProperty("definition_sites").GetInt32());

            var commentGap = RunGroupedSymbol(dbPath, "CommentGap", "function");
            Assert.Equal(2, commentGap.GetProperty("definition_sites").GetInt32());

            var genericGap = RunGroupedSymbol(dbPath, "GenericGap", "function");
            Assert.Equal(2, genericGap.GetProperty("definition_sites").GetInt32());

            var attrString = RunGroupedSymbol(dbPath, "AttrString", "function");
            Assert.Equal(2, attrString.GetProperty("definition_sites").GetInt32());

            var referenceNullable = RunGroupedSymbol(dbPath, "ReferenceNullable", "function");
            Assert.Equal(2, referenceNullable.GetProperty("definition_sites").GetInt32());

            var valueNullable = RunGroupedSymbol(dbPath, "ValueNullable", "function");
            Assert.Equal(2, valueNullable.GetProperty("definition_sites").GetInt32());

            var (qualifiedGenericExitCode, qualifiedGenericStdout, qualifiedGenericStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["QualifiedGeneric", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var qualifiedGenericDocument = ParseJsonOutput(qualifiedGenericStdout);
            var qualifiedGenericFamilies = qualifiedGenericDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, qualifiedGenericExitCode);
            Assert.Equal(string.Empty, qualifiedGenericStderr);
            Assert.Equal(2, qualifiedGenericFamilies.Count);
            Assert.All(qualifiedGenericFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Contains(qualifiedGenericFamilies, family => family.GetProperty("signature").GetString()!.Contains("N.T", StringComparison.Ordinal));
            Assert.Contains(qualifiedGenericFamilies, family => family.GetProperty("signature").GetString()!.Contains("N.U", StringComparison.Ordinal));

            var (rootedExitCode, rootedStdout, rootedStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Rooted", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var rootedDocument = ParseJsonOutput(rootedStdout);
            var rootedFamilies = rootedDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, rootedExitCode);
            Assert.Equal(string.Empty, rootedStderr);
            Assert.Equal(2, rootedFamilies.Count);
            Assert.All(rootedFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));

            var (verbatimGlobalExitCode, verbatimGlobalStdout, verbatimGlobalStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["VerbatimGlobal", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var verbatimGlobalDocument = ParseJsonOutput(verbatimGlobalStdout);
            var verbatimGlobalFamilies = verbatimGlobalDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, verbatimGlobalExitCode);
            Assert.Equal(string.Empty, verbatimGlobalStderr);
            Assert.Equal(2, verbatimGlobalFamilies.Count);
            Assert.All(verbatimGlobalFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Equal(
                2,
                verbatimGlobalFamilies
                    .Select(family => family.GetProperty("partial_family_id").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var (shellExitCode, shellStdout, shellStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["hello", "--db", dbPath, "--json=array", "--exact-name", "--lang", "shell", "--kind", "function", "--limit", "1"],
                _jsonOptions));
            using var shellDocument = ParseJsonOutput(shellStdout);
            var shellFunction = Assert.Single(shellDocument.RootElement.EnumerateArray().ToList());

            Assert.Equal(CommandExitCodes.Success, shellExitCode);
            Assert.Equal(string.Empty, shellStderr);
            Assert.Equal(9, shellFunction.GetProperty("start_column").GetInt32());

            var (containerExitCode, containerStdout, containerStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["ContainerMethod", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var containerDocument = ParseJsonOutput(containerStdout);
            var containerFamilies = containerDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, containerExitCode);
            Assert.Equal(string.Empty, containerStderr);
            Assert.Equal(2, containerFamilies.Count);
            Assert.All(containerFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));

            var (genericTypeExitCode, genericTypeStdout, genericTypeStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["GenericHost", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var genericTypeDocument = ParseJsonOutput(genericTypeStdout);
            var genericTypeFamilies = genericTypeDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, genericTypeExitCode);
            Assert.Equal(string.Empty, genericTypeStderr);
            Assert.Equal(2, genericTypeFamilies.Count);
            Assert.All(genericTypeFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Equal(
                2,
                genericTypeFamilies
                    .Select(family => family.GetProperty("partial_family_id").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var (nestedTypeExitCode, nestedTypeStdout, nestedTypeStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Nested", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var nestedTypeDocument = ParseJsonOutput(nestedTypeStdout);
            var nestedTypeFamilies = nestedTypeDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, nestedTypeExitCode);
            Assert.Equal(string.Empty, nestedTypeStderr);
            Assert.Equal(2, nestedTypeFamilies.Count);
            Assert.All(nestedTypeFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));

            var resultFactory = RunGroupedSymbol(dbPath, "Result", "function");
            Assert.Equal(2, resultFactory.GetProperty("definition_sites").GetInt32());
            var resultFactoryMembers = resultFactory.GetProperty("family_members").EnumerateArray().ToList();
            Assert.Contains(
                resultFactoryMembers,
                member => member.GetProperty("path").GetString() == "src/A.Controller.cs"
                    && member.GetProperty("start_column").GetInt32() == 32);
            Assert.Contains(
                resultFactoryMembers,
                member => member.GetProperty("path").GetString() == "src/Z.Controller.cs"
                    && member.GetProperty("start_column").GetInt32() == 51);

            var (caseExitCode, caseStdout, caseStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Run", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var caseDocument = ParseJsonOutput(caseStdout);
            var caseFamilies = caseDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, caseExitCode);
            Assert.Equal(string.Empty, caseStderr);
            Assert.Equal(2, caseFamilies.Count);
            Assert.All(caseFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Contains(caseFamilies, family => family.GetProperty("signature").GetString()!.Contains("Item implementationValue", StringComparison.Ordinal));
            Assert.Contains(caseFamilies, family => family.GetProperty("signature").GetString()!.Contains("item implementationValue", StringComparison.Ordinal));

            var equal = RunGroupedSymbol(dbPath, "Equal", "class");
            Assert.Equal("src/A.Equal.cs", equal.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", equal.GetProperty("representative_reason").GetString());

            var rebuildRoot = TestProjectHelper.CreateDirectory(projectRoot, "rebuild-order");
            var rebuildDbPath = TestProjectHelper.CreateProjectDb(rebuildRoot);
            TestProjectHelper.InsertIndexedFile(
                rebuildDbPath,
                "src/A.Equal.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Equal { }
                """);
            TestProjectHelper.InsertIndexedFile(
                rebuildDbPath,
                "src/B.Equal.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Equal { }
                """);
            MarkGraphAndFoldReady(rebuildDbPath);
            var rebuiltEqual = RunGroupedSymbol(rebuildDbPath, "Equal", "class");
            Assert.Equal("src/A.Equal.cs", rebuiltEqual.GetProperty("path").GetString());
            Assert.Equal(equal.GetProperty("partial_family_id").GetString(), rebuiltEqual.GetProperty("partial_family_id").GetString());

            var profile = RunGroupedSymbol(dbPath, "Profile", "class");
            Assert.Equal("src/ZZZ.Profile.Primary.cs", profile.GetProperty("path").GetString());
            Assert.Equal("semantic_declaration", profile.GetProperty("representative_reason").GetString());

            var (definitionExitCode, definitionStdout, definitionStderr) = CaptureConsole(() => QueryCommandRunner.RunDefinition(
                ["Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "1"],
                _jsonOptions));
            using var definitionDocument = ParseJsonOutput(definitionStdout);

            Assert.Equal(CommandExitCodes.Success, definitionExitCode);
            Assert.Equal(string.Empty, definitionStderr);
            Assert.Equal(widget.GetProperty("partial_family_id").GetString(), definitionDocument.RootElement.GetProperty("partial_family_id").GetString());
            Assert.Equal(2, definitionDocument.RootElement.GetProperty("family_members").GetArrayLength());

            var (projectedExitCode, projectedStdout, projectedStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definition", "Widget", "--db", dbPath, "--json", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--fields", "family_members"],
                _jsonOptions,
                "1.0.0-test"));
            using var projectedDocument = ParseJsonOutput(projectedStdout);
            var projectedDefinition = Assert.Single(projectedDocument.RootElement.GetProperty("results").EnumerateArray());

            Assert.Equal(CommandExitCodes.Success, projectedExitCode);
            Assert.Equal(string.Empty, projectedStderr);
            Assert.Single(projectedDefinition.EnumerateObject());
            Assert.Equal(2, projectedDefinition.GetProperty("family_members").GetArrayLength());

            var (nestedFieldExitCode, nestedFieldStdout, nestedFieldStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definition", "Widget", "--db", dbPath, "--json", "--fields", "family_members.path"],
                _jsonOptions,
                "1.0.0-test"));
            using var nestedFieldDocument = ParseJsonOutput(nestedFieldStdout);

            Assert.Equal(CommandExitCodes.UsageError, nestedFieldExitCode);
            Assert.Equal(string.Empty, nestedFieldStderr);
            Assert.Contains("Unknown --fields value 'family_members.path'", nestedFieldDocument.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

            var (gotoExitCode, gotoStdout, gotoStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["Widget", "--db", dbPath, "--exact-name", "--lang", "csharp", "--kind", "class", "--include-generated"],
                _jsonOptions));
            using var gotoDocument = ParseJsonOutput(gotoStdout);
            var gotoLocation = gotoDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, gotoExitCode);
            Assert.Equal(string.Empty, gotoStderr);
            Assert.EndsWith("/src/Z.Widget.cs", new Uri(gotoLocation.GetProperty("uri").GetString()!).AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("non_generated_source", gotoLocation.GetProperty("representative_reason").GetString());
            Assert.Equal(2, gotoLocation.GetProperty("family_members").GetArrayLength());
            Assert.All(
                gotoLocation.GetProperty("family_members").EnumerateArray(),
                member => Assert.Equal(21, member.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()));

            var (gotoAllExitCode, gotoAllStdout, gotoAllStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["Widget", "--db", dbPath, "--exact-name", "--lang", "csharp", "--kind", "class", "--include-generated", "--all"],
                _jsonOptions));
            using var gotoAllDocument = ParseJsonOutput(gotoAllStdout);
            var allLocations = gotoAllDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, gotoAllExitCode);
            Assert.Equal(string.Empty, gotoAllStderr);
            Assert.Equal(2, allLocations.Count);
            Assert.Contains(allLocations, location => new Uri(location.GetProperty("uri").GetString()!).AbsolutePath.EndsWith("/src/A.Widget.Split.cs", StringComparison.Ordinal));
            Assert.Contains(allLocations, location => new Uri(location.GetProperty("uri").GetString()!).AbsolutePath.EndsWith("/src/Z.Widget.cs", StringComparison.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_PersistsSplitModifierAndLeadingSemanticEvidence_Issue4914()
    {
        const string source =
            """
            namespace Demo;
            public partial class Container
            {
                /// <summary>Primary declaration.</summary>
                [System.Obsolete]
                partial // declaration modifier
                void OnReady(
                    int value)
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(
            1,
            "csharp",
            source);

        var method = Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "OnReady"));
        Assert.True(method.IsPartialDeclaration);
        Assert.Equal(3, method.DeclarationSemanticScore);
        Assert.Equal(9, method.IdentifierStartColumn);
        Assert.DoesNotContain("partial", method.Signature, StringComparison.Ordinal);

        var persistedFamily = new SymbolResult
        {
            Lang = "csharp",
            Kind = "function",
            Name = "OnReady",
            Signature = method.Signature,
            LogicalPartialKey = "family:csharp\u001ffunction\u001fDemo.Container\u001fOnReady/0(System.Int32):System.Void",
        };
        Assert.True(LogicalPartialSymbolGrouper.TryBuildKey(persistedFamily, out var persistedFamilyKey));
        Assert.Equal(persistedFamily.LogicalPartialKey, persistedFamilyKey);

        var persistedPhysical = new SymbolResult
        {
            Lang = "csharp",
            Kind = "function",
            Name = "OnReady",
            Signature = "partial void OnReady(int value);",
            ReturnType = "void",
            ContainerName = "Container",
            LogicalPartialKey = "symbol:42",
        };
        Assert.False(LogicalPartialSymbolGrouper.TryBuildKey(persistedPhysical, out _));

        var persistedPhysicalType = new SymbolResult
        {
            Lang = "csharp",
            Kind = "class",
            Name = "Container",
            Signature = "public partial class Container",
            ContainerName = "Demo",
            LogicalPartialKey = "symbol:43",
        };
        Assert.False(LogicalPartialSymbolGrouper.TryBuildKey(persistedPhysicalType, out _));
        Assert.True(
            LogicalPartialSymbolGrouper.TryBuildTypeFamilyKeyForReferenceResolution(
                persistedPhysicalType,
                out var degradedTypeFamilyKey));
        Assert.Contains("Container", degradedTypeFamilyKey, StringComparison.Ordinal);

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_worker_metadata_issue4914");
        try
        {
            var request = new SymbolExtractionWorker.WorkerRequest(
                1,
                "csharp",
                source,
                Path.Combine(projectRoot, "Container.cs"),
                projectRoot);
            using var input = new StringReader(
                JsonSerializer.Serialize(request, SymbolExtractionWorker.JsonOptions) + "\n");
            using var output = new StringWriter();
            using var error = new StringWriter();

            var handled = SymbolExtractionWorker.TryRunCommand(
                [SymbolExtractionWorker.CommandName],
                input,
                output,
                error,
                out var exitCode);
            var response = JsonSerializer.Deserialize<SymbolExtractionWorker.WorkerResponse>(
                output.ToString(),
                SymbolExtractionWorker.JsonOptions);
            var transportedMethod = Assert.Single(
                response!.Symbols!.Where(symbol => symbol.Kind == "function" && symbol.Name == "OnReady"));

            Assert.True(handled);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, error.ToString());
            Assert.True(transportedMethod.IsPartialDeclaration);
            Assert.Equal(3, transportedMethod.DeclarationSemanticScore);
            Assert.Equal(9, transportedMethod.IdentifierStartColumn);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_IgnoresSameLineBodiesAndCommentedOrdinaryMethods_Issue4914()
    {
        const string extractionSource =
            """
            namespace Demo;
            public partial class FileBodyHost { private int file; }
            public partial class BracketBodyHost { private int[] values = []; }
            public partial class PrimaryConstructorHost(int file) { }
            public class ParameterNamedPartial(int partial) { }
            """;
        var extractedTypes = SymbolExtractor.Extract(1, "csharp", extractionSource);
        var fileBodyHost = Assert.Single(
            extractedTypes.Where(symbol => symbol.Kind == "class" && symbol.Name == "FileBodyHost"));
        var bracketBodyHost = Assert.Single(
            extractedTypes.Where(symbol => symbol.Kind == "class" && symbol.Name == "BracketBodyHost"));
        var primaryConstructorHost = Assert.Single(
            extractedTypes.Where(symbol => symbol.Kind == "class" && symbol.Name == "PrimaryConstructorHost"));
        var parameterNamedPartial = Assert.Single(
            extractedTypes.Where(symbol => symbol.Kind == "class" && symbol.Name == "ParameterNamedPartial"));

        Assert.True(fileBodyHost.IsPartialDeclaration);
        Assert.False(fileBodyHost.IsFileLocalDeclaration);
        Assert.Equal(0, fileBodyHost.DeclarationSemanticScore);
        Assert.True(bracketBodyHost.IsPartialDeclaration);
        Assert.False(bracketBodyHost.IsFileLocalDeclaration);
        Assert.Equal(0, bracketBodyHost.DeclarationSemanticScore);
        Assert.True(primaryConstructorHost.IsPartialDeclaration);
        Assert.False(primaryConstructorHost.IsFileLocalDeclaration);
        Assert.False(parameterNamedPartial.IsPartialDeclaration);

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_header_evidence_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Host.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Host { private int file; }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Host.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Host { private int[] values = []; }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/ConditionalMethods.cs",
                "csharp",
                """
                namespace Demo;
                public partial class MethodHost
                {
                #if A
                    void M(/* partial */ int value) { }
                    void N(int partial) { }
                #else
                    void M(/* partial */ int value) { }
                    void N(int partial) { }
                #endif
                }
                #if A
                public class ConstructorHost(int partial) { }
                #else
                public class ConstructorHost(int partial) { }
                #endif
                """);
            MarkGraphAndFoldReady(dbPath);

            var groupedHost = RunGroupedSymbol(dbPath, "Host", "class");
            Assert.Equal(2, groupedHost.GetProperty("definition_sites").GetInt32());
            Assert.Equal("src/A.Host.cs", groupedHost.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", groupedHost.GetProperty("representative_reason").GetString());

            var (methodsExitCode, methodsStdout, methodsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["M", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var methodsDocument = ParseJsonOutput(methodsStdout);
            var methods = methodsDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, methodsExitCode);
            Assert.Equal(string.Empty, methodsStderr);
            Assert.Equal(2, methods.Count);
            Assert.All(methods, method => Assert.False(method.TryGetProperty("definition_sites", out _)));
            Assert.All(methods, method => Assert.False(method.TryGetProperty("partial_family_id", out _)));

            var (parameterMethodsExitCode, parameterMethodsStdout, parameterMethodsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["N", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var parameterMethodsDocument = ParseJsonOutput(parameterMethodsStdout);
            var parameterMethods = parameterMethodsDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, parameterMethodsExitCode);
            Assert.Equal(string.Empty, parameterMethodsStderr);
            Assert.Equal(2, parameterMethods.Count);
            Assert.All(parameterMethods, method => Assert.False(method.TryGetProperty("definition_sites", out _)));
            Assert.All(parameterMethods, method => Assert.False(method.TryGetProperty("partial_family_id", out _)));

            var (constructorsExitCode, constructorsStdout, constructorsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["ConstructorHost", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var constructorsDocument = ParseJsonOutput(constructorsStdout);
            var constructors = constructorsDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, constructorsExitCode);
            Assert.Equal(string.Empty, constructorsStderr);
            Assert.Equal(2, constructors.Count);
            Assert.All(constructors, constructor => Assert.False(constructor.TryGetProperty("definition_sites", out _)));
            Assert.All(constructors, constructor => Assert.False(constructor.TryGetProperty("partial_family_id", out _)));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_GroupsTestClassifiedImplementation_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_test_method_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.AttributedPartial.cs",
                "csharp",
                """
                namespace Demo;
                public partial class AttributedPartial
                {
                    public partial void Execute();
                    public partial void InlineExecute();
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.AttributedPartial.cs",
                "csharp",
                """
                namespace Demo;
                public partial class AttributedPartial
                {
                    [Fact]
                    public partial void Execute() { }
                    [Fact] public partial void InlineExecute() { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Execute", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var symbolsDocument = ParseJsonOutput(symbolsStdout);
            var grouped = Assert.Single(symbolsDocument.RootElement.EnumerateArray().ToList());

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal("test.method", grouped.GetProperty("kind").GetString());
            Assert.Equal("src/Z.AttributedPartial.cs", grouped.GetProperty("path").GetString());
            Assert.Equal(2, grouped.GetProperty("definition_sites").GetInt32());
            Assert.Equal("implementation_body", grouped.GetProperty("representative_reason").GetString());

            var (gotoExitCode, gotoStdout, gotoStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["Execute", "--db", dbPath, "--exact-name", "--lang", "csharp"],
                _jsonOptions));
            using var gotoDocument = ParseJsonOutput(gotoStdout);
            var location = gotoDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, gotoExitCode);
            Assert.Equal(string.Empty, gotoStderr);
            Assert.Contains("Z.AttributedPartial.cs", location.GetProperty("uri").GetString(), StringComparison.Ordinal);
            Assert.Equal(2, location.GetProperty("family_members").GetArrayLength());

            var (inlineExitCode, inlineStdout, inlineStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["InlineExecute", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var inlineDocument = ParseJsonOutput(inlineStdout);
            var inlineExecute = Assert.Single(inlineDocument.RootElement.EnumerateArray().ToList());
            Assert.Equal(CommandExitCodes.Success, inlineExitCode);
            Assert.Equal(string.Empty, inlineStderr);
            Assert.True(inlineExecute.TryGetProperty("definition_sites", out var inlineDefinitionSites));
            Assert.Equal(2, inlineDefinitionSites.GetInt32());
            Assert.Equal(
                ["src/A.AttributedPartial.cs", "src/Z.AttributedPartial.cs"],
                inlineExecute.GetProperty("family_members")
                    .EnumerateArray()
                    .Select(member => member.GetProperty("path").GetString())
                    .Order(StringComparer.Ordinal)
                    .ToArray());

            var (inlineGotoExitCode, inlineGotoStdout, inlineGotoStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunGoto(
                    ["InlineExecute", "--db", dbPath, "--exact-name", "--lang", "csharp"],
                    _jsonOptions));
            using var inlineGotoDocument = ParseJsonOutput(inlineGotoStdout);
            Assert.Equal(CommandExitCodes.Success, inlineGotoExitCode);
            Assert.Equal(string.Empty, inlineGotoStderr);
            Assert.Equal(2, inlineGotoDocument.RootElement.GetProperty("family_members").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_GroupsSplitModifierAndRanksLeadingEvidence_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_leading_evidence_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Split.cs",
                "csharp",
                """
                namespace Demo;
                public partial class SplitHost
                {
                    partial
                    void OnSplit(int first, string second);
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Split.cs",
                "csharp",
                """
                namespace Demo;
                public partial class SplitHost
                {
                    partial
                    void OnSplit(int value, string text) { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Documented.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Documented { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Documented.cs",
                "csharp",
                """
                namespace Demo;
                /**
                 * <summary>Primary declaration.</summary>

                 */
                public partial class Documented { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.CommentDecoy.cs",
                "csharp",
                """
                namespace Demo;
                public partial class CommentDecoy { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.CommentDecoy.cs",
                "csharp",
                """
                namespace Demo;
                /*
                /// <summary>Not documentation.</summary>
                */
                public partial class CommentDecoy { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Attributed.cs",
                "csharp",
                """
                namespace Demo;
                public partial class Attributed { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Attributed.cs",
                "csharp",
                """
                namespace Demo;
                [System.Obsolete]
                public partial class Attributed { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.AttributeModifier.cs",
                "csharp",
                """
                namespace Demo;
                public partial class AttributeModifier { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.AttributeModifier.cs",
                "csharp",
                """
                namespace Demo;
                [System.Obsolete] public partial
                class AttributeModifier { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.BlankModifier.cs",
                "csharp",
                """
                namespace Demo;
                public partial class BlankModifier { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.BlankModifier.cs",
                "csharp",
                """
                namespace Demo;
                public
                partial

                class BlankModifier { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.InlineAttributed.cs",
                "csharp",
                """
                namespace Demo;
                public partial class InlineAttributed { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.InlineAttributed.cs",
                "csharp",
                """
                namespace Demo;
                [System.Obsolete] public partial class InlineAttributed { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.BlankDocumentation.cs",
                "csharp",
                """
                namespace Demo;
                public partial class BlankDocumentation { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.InlineDocumented.cs",
                "csharp",
                "public partial class InlineDocumented { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.InlineDocumented.cs",
                "csharp",
                "/** <summary>Primary declaration.</summary> */ public partial class InlineDocumented { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.InlineDocumentationDecoy.cs",
                "csharp",
                "public partial class InlineDocumentationDecoy { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.InlineDocumentationDecoy.cs",
                "csharp",
                "/* /** Not documentation. */ public partial class InlineDocumentationDecoy { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.AssemblyTarget.cs",
                "csharp",
                "public partial class AssemblyTarget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.AssemblyTarget.cs",
                "csharp",
                """
                [assembly: System.CLSCompliant(true)]
                public partial class AssemblyTarget { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.ModuleTarget.cs",
                "csharp",
                "public partial class ModuleTarget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.ModuleTarget.cs",
                "csharp",
                """
                [module: System.CLSCompliant(true)]
                public partial class ModuleTarget { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.InlineAssemblyTarget.cs",
                "csharp",
                "public partial class InlineAssemblyTarget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.InlineAssemblyTarget.cs",
                "csharp",
                "[assembly: System.CLSCompliant(true)] public partial class InlineAssemblyTarget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.InlineModuleTarget.cs",
                "csharp",
                "public partial class InlineModuleTarget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.InlineModuleTarget.cs",
                "csharp",
                "[module: System.CLSCompliant(true)] public partial class InlineModuleTarget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.BlankDocumentation.cs",
                "csharp",
                """
                namespace Demo;
                /// <summary>Detached documentation.</summary>

                public partial class BlankDocumentation { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.UnattributedAfterSibling.cs",
                "csharp",
                "public partial class UnattributedAfterSibling { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.UnattributedAfterSibling.cs",
                "csharp",
                "[System.Obsolete] public class AttributeOwner { } public partial class UnattributedAfterSibling { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/SameLineDuplicate.cs",
                "csharp",
                "public partial class SameLineDuplicate { } public partial class SameLineDuplicate { }");
            MarkGraphAndFoldReady(dbPath);

            var split = RunGroupedSymbol(dbPath, "OnSplit", "function");
            Assert.Equal(2, split.GetProperty("definition_sites").GetInt32());
            Assert.Contains(
                split.GetProperty("family_members").EnumerateArray(),
                member => member.GetProperty("path").GetString() == "src/A.Split.cs"
                    && member.GetProperty("start_column").GetInt32() == 9);
            Assert.Contains(
                split.GetProperty("family_members").EnumerateArray(),
                member => member.GetProperty("path").GetString() == "src/Z.Split.cs"
                    && member.GetProperty("start_column").GetInt32() == 9);
            Assert.Equal(9, split.GetProperty("start_column").GetInt32());

            var documented = RunGroupedSymbol(dbPath, "Documented", "class");
            Assert.Equal("src/Z.Documented.cs", documented.GetProperty("path").GetString());
            Assert.Equal("semantic_declaration", documented.GetProperty("representative_reason").GetString());

            var commentDecoy = RunGroupedSymbol(dbPath, "CommentDecoy", "class");
            Assert.Equal("src/A.CommentDecoy.cs", commentDecoy.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", commentDecoy.GetProperty("representative_reason").GetString());

            var attributed = RunGroupedSymbol(dbPath, "Attributed", "class");
            Assert.Equal("src/Z.Attributed.cs", attributed.GetProperty("path").GetString());
            Assert.Equal("semantic_declaration", attributed.GetProperty("representative_reason").GetString());

            var attributeModifier = RunGroupedSymbol(dbPath, "AttributeModifier", "class");
            Assert.Equal(2, attributeModifier.GetProperty("definition_sites").GetInt32());
            Assert.Equal("src/Z.AttributeModifier.cs", attributeModifier.GetProperty("path").GetString());
            Assert.Equal("semantic_declaration", attributeModifier.GetProperty("representative_reason").GetString());

            var blankModifier = RunGroupedSymbol(dbPath, "BlankModifier", "class");
            Assert.Equal(2, blankModifier.GetProperty("definition_sites").GetInt32());

            var inlineAttributed = RunGroupedSymbol(dbPath, "InlineAttributed", "class");
            Assert.Equal("src/Z.InlineAttributed.cs", inlineAttributed.GetProperty("path").GetString());
            Assert.Equal("semantic_declaration", inlineAttributed.GetProperty("representative_reason").GetString());

            var blankDocumentation = RunGroupedSymbol(dbPath, "BlankDocumentation", "class");
            Assert.Equal("src/A.BlankDocumentation.cs", blankDocumentation.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", blankDocumentation.GetProperty("representative_reason").GetString());

            var inlineDocumented = RunGroupedSymbol(dbPath, "InlineDocumented", "class");
            Assert.Equal("src/Z.InlineDocumented.cs", inlineDocumented.GetProperty("path").GetString());
            Assert.Equal("semantic_declaration", inlineDocumented.GetProperty("representative_reason").GetString());

            var inlineDocumentationDecoy = RunGroupedSymbol(dbPath, "InlineDocumentationDecoy", "class");
            Assert.Equal("src/A.InlineDocumentationDecoy.cs", inlineDocumentationDecoy.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", inlineDocumentationDecoy.GetProperty("representative_reason").GetString());

            var assemblyTarget = RunGroupedSymbol(dbPath, "AssemblyTarget", "class");
            Assert.Equal("src/A.AssemblyTarget.cs", assemblyTarget.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", assemblyTarget.GetProperty("representative_reason").GetString());

            var moduleTarget = RunGroupedSymbol(dbPath, "ModuleTarget", "class");
            Assert.Equal("src/A.ModuleTarget.cs", moduleTarget.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", moduleTarget.GetProperty("representative_reason").GetString());

            foreach (var inlineGlobalTarget in new[] { "InlineAssemblyTarget", "InlineModuleTarget" })
            {
                var grouped = RunGroupedSymbol(dbPath, inlineGlobalTarget, "class");
                Assert.Equal($"src/A.{inlineGlobalTarget}.cs", grouped.GetProperty("path").GetString());
                Assert.Equal("stable_path_and_position", grouped.GetProperty("representative_reason").GetString());
            }

            var unattributedAfterSibling = RunGroupedSymbol(dbPath, "UnattributedAfterSibling", "class");
            Assert.Equal("src/A.UnattributedAfterSibling.cs", unattributedAfterSibling.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", unattributedAfterSibling.GetProperty("representative_reason").GetString());

            var sameLineDuplicate = RunGroupedSymbol(dbPath, "SameLineDuplicate", "class");
            Assert.Equal(2, sameLineDuplicate.GetProperty("definition_sites").GetInt32());
            Assert.Equal(
                [21, 64],
                sameLineDuplicate.GetProperty("family_members")
                    .EnumerateArray()
                    .Select(member => member.GetProperty("start_column").GetInt32())
                    .Order()
                    .ToArray());

            var (gotoAllExitCode, gotoAllStdout, gotoAllStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["SameLineDuplicate", "--db", dbPath, "--exact-name", "--lang", "csharp", "--kind", "class", "--all"],
                _jsonOptions));
            using var gotoAllDocument = ParseJsonOutput(gotoAllStdout);
            var allLocations = gotoAllDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, gotoAllExitCode);
            Assert.Equal(string.Empty, gotoAllStderr);
            Assert.Equal(
                [21, 64],
                allLocations
                    .Select(location => location.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32())
                    .Order()
                    .ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_RespectsFileLocalAndLexedEvidence_Issue4914()
    {
        const string definingLine = "    [M()] partial /* M( */ void M();";
        var publicDefiningLine = definingLine.Replace("partial", "public partial", StringComparison.Ordinal);
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_file_local_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            foreach (var path in new[] { "src/A.Local.cs", "src/B.Local.cs" })
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    path,
                    "csharp",
                    $$"""
                    using System;
                    file sealed class MAttribute : Attribute { }
                    file partial class Host
                    {
                    {{publicDefiningLine}}
                    }
                    partial class Host
                    {
                        public partial void M() { }
                    }
                    file sealed class Use { public void Call() { new Host().M(); } }
                    """);
            }
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Widget.cs",
                "csharp",
                "public partial class Widget { }");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Widget.cs",
                "csharp",
                "public partial /* [ : where */ class Widget { }");
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["M", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);
            var families = document.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, families.Count);
            Assert.All(families, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Equal(
                2,
                families
                    .Select(family => family.GetProperty("partial_family_id").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.All(
                families,
                family =>
                {
                    var path = family.GetProperty("path").GetString();
                    var members = family.GetProperty("family_members").EnumerateArray().ToList();
                    Assert.All(members, member => Assert.Equal(path, member.GetProperty("path").GetString()));
                    Assert.Contains(
                        members,
                        member => member.GetProperty("line").GetInt32() == 5
                            && member.GetProperty("start_column").GetInt32()
                                == publicDefiningLine.LastIndexOf("M", StringComparison.Ordinal));
                });

            var (hostExitCode, hostStdout, hostStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Host", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var hostDocument = ParseJsonOutput(hostStdout);
            var hostFamilies = hostDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, hostExitCode);
            Assert.Equal(string.Empty, hostStderr);
            Assert.Equal(2, hostFamilies.Count);
            Assert.All(hostFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.All(
                hostFamilies,
                family => Assert.Single(
                    family.GetProperty("family_members")
                        .EnumerateArray()
                        .Select(member => member.GetProperty("path").GetString())
                        .Distinct(StringComparer.Ordinal)));

            var (hotspotsExitCode, hotspotsStdout, hotspotsStderr) = CaptureConsole(() => QueryCommandRunner.RunHotspots(
                ["--db", dbPath, "--json", "--lang", "csharp", "--kind", "function", "--limit", "10"],
                _jsonOptions));
            using var hotspotsDocument = ParseJsonOutput(hotspotsStdout);
            var methodHotspots = hotspotsDocument.RootElement.GetProperty("hotspots")
                .EnumerateArray()
                .Where(hotspot => hotspot.GetProperty("name").GetString() == "M")
                .ToList();

            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.Equal(string.Empty, hotspotsStderr);
            Assert.Equal(2, methodHotspots.Count);
            Assert.All(methodHotspots, hotspot => Assert.Equal(2, hotspot.GetProperty("reference_count").GetInt32()));
            Assert.Equal(
                2,
                methodHotspots
                    .Select(hotspot => hotspot.GetProperty("path").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var candidatePaths = connection.CreateCommand();
                candidatePaths.CommandText =
                    """
                    SELECT source_file.path, target_file.path
                    FROM symbol_references AS reference
                    JOIN files AS source_file ON source_file.id = reference.file_id
                    JOIN symbol_reference_candidates AS candidate ON candidate.reference_id = reference.id
                    JOIN symbols AS target ON target.id = candidate.symbol_id
                    JOIN files AS target_file ON target_file.id = target.file_id
                    WHERE reference.symbol_name IN ('Host', 'M')
                      AND reference.reference_kind IN ('instantiate', 'call')
                    ORDER BY source_file.path, target_file.path
                    """;
                using var reader = candidatePaths.ExecuteReader();
                var resolvedPaths = new List<(string Source, string Target)>();
                while (reader.Read())
                    resolvedPaths.Add((reader.GetString(0), reader.GetString(1)));

                Assert.NotEmpty(resolvedPaths);
                Assert.All(resolvedPaths, paths => Assert.Equal(paths.Source, paths.Target));
                Assert.Equal(
                    ["src/A.Local.cs", "src/B.Local.cs"],
                    resolvedPaths.Select(paths => paths.Source).Distinct(StringComparer.Ordinal).ToArray());
            }

            var widget = RunGroupedSymbol(dbPath, "Widget", "class");
            Assert.Equal("src/A.Widget.cs", widget.GetProperty("path").GetString());
            Assert.Equal("stable_path_and_position", widget.GetProperty("representative_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GroupPartials_DegradesSafelyWhenCSharpFamilyContractIsStale_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_stale_family_contract_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            foreach (var path in new[] { "src/A.Hosts.cs", "src/Z.Hosts.cs" })
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    path,
                    "csharp",
                    """
                    namespace Demo;
                    public partial class Host<T>
                    {
                        partial void OnReady();
                    }
                    public partial class Host<T, U>
                    {
                        partial void OnReady();
                    }
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using (var staleFamilyKeys = connection.CreateCommand())
                {
                    staleFamilyKeys.CommandText =
                        """
                        UPDATE symbols
                        SET family_key = REPLACE(REPLACE(family_key, 'Host`1', 'Host'), 'Host`2', 'Host')
                        WHERE name = 'OnReady'
                        """;
                    Assert.Equal(4, staleFamilyKeys.ExecuteNonQuery());
                }

                using var staleContract = connection.CreateCommand();
                staleContract.CommandText =
                    """
                    INSERT INTO codeindex_meta(key, value)
                    VALUES ($key, $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """;
                staleContract.Parameters.AddWithValue(
                    "$key",
                    DbContext.GetHotspotFamilyVersionMetaKey("csharp"));
                staleContract.Parameters.AddWithValue(
                    "$value",
                    (DbContext.HotspotFamilyVersion - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(1, staleContract.ExecuteNonQuery());
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["OnReady", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);
            var rows = document.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, row => Assert.False(row.TryGetProperty("definition_sites", out _)));
            Assert.All(rows, row => Assert.False(row.TryGetProperty("partial_family_id", out _)));

            var (impactExitCode, impactStdout, impactStderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Host", "--db", dbPath, "--json", "--lang", "csharp", "--max-hops", "0", "--limit", "10"],
                _jsonOptions));
            using var impactDocument = ParseJsonOutput(impactStdout);
            var impact = impactDocument.RootElement;

            Assert.Equal(CommandExitCodes.Success, impactExitCode);
            Assert.Equal(string.Empty, impactStderr);
            Assert.Equal(4, impact.GetProperty("definition_count").GetInt32());
            Assert.Equal(4, impact.GetProperty("logical_definition_count").GetInt32());
            Assert.Equal(4, impact.GetProperty("definition_output_count").GetInt32());
            Assert.False(impact.GetProperty("definitions_collapsed").GetBoolean());
            Assert.All(
                impact.GetProperty("definitions").EnumerateArray(),
                definition => Assert.False(definition.TryGetProperty("partial_family_id", out _)));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_FallsBackToDesignerPathForOldDatabaseMetadata_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_canonical_old_db_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.LegacyWidget.Designer.cs",
                "csharp",
                """
                namespace Demo;
                [System.Obsolete]
                public partial class LegacyWidget : LegacyBase { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.LegacyWidget.cs",
                "csharp",
                """
                namespace Demo;
                public partial class LegacyWidget { }
                """);
            MarkGraphAndFoldReady(dbPath);

            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using (var dropGeneratedIndex = connection.CreateCommand())
                {
                    dropGeneratedIndex.CommandText = "DROP INDEX IF EXISTS idx_files_generated";
                    dropGeneratedIndex.ExecuteNonQuery();
                }
                using var dropGeneratedColumn = connection.CreateCommand();
                dropGeneratedColumn.CommandText = "ALTER TABLE files DROP COLUMN generated";
                dropGeneratedColumn.ExecuteNonQuery();
            }

            var legacyWidget = RunGroupedSymbol(dbPath, "LegacyWidget", "class");
            Assert.Equal("src/Z.LegacyWidget.cs", legacyWidget.GetProperty("path").GetString());
            Assert.Equal("non_generated_source", legacyWidget.GetProperty("representative_reason").GetString());
            Assert.Contains(
                legacyWidget.GetProperty("family_members").EnumerateArray(),
                member => member.GetProperty("path").GetString() == "src/A.LegacyWidget.Designer.cs"
                    && member.GetProperty("generated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_UsesIndexedTypeFactsForCustomNullability_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_custom_nullability_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Declarations.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public class Node { }
                public struct Token { }
                public class Box<T> { }
                public struct Box { }
                public class Outer<T> { public class Nested<TNested> { } }
                public class Outer { public struct Nested<TNested> { } }
                public partial class Container
                {
                    partial void Reference(Node? value);
                    partial void Generic(Box<int>? value);
                    partial void NestedGeneric(Outer<int>.Nested<string>? value);
                    partial void Value(Token value);
                    partial void Value(Token? value);
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Z.Implementations.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public partial class Container
                {
                    partial void Reference(Node value) { }
                    partial void Generic(Box<int> value) { }
                    partial void NestedGeneric(Outer<int>.Nested<string> value) { }
                    partial void Value(Token value) { }
                    partial void Value(Token? value) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var reference = RunGroupedSymbol(dbPath, "Reference", "function");
            Assert.Equal(2, reference.GetProperty("definition_sites").GetInt32());
            Assert.Equal("src/Z.Implementations.cs", reference.GetProperty("path").GetString());
            Assert.Equal("implementation_body", reference.GetProperty("representative_reason").GetString());

            var generic = RunGroupedSymbol(dbPath, "Generic", "function");
            Assert.True(generic.TryGetProperty("definition_sites", out var genericDefinitionSites), generic.GetRawText());
            Assert.Equal(2, genericDefinitionSites.GetInt32());
            var nestedGeneric = RunGroupedSymbol(dbPath, "NestedGeneric", "function");
            Assert.Equal(2, nestedGeneric.GetProperty("definition_sites").GetInt32());

            var (valueExitCode, valueStdout, valueStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Value", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--limit", "10"],
                _jsonOptions));
            using var valueDocument = ParseJsonOutput(valueStdout);
            var valueFamilies = valueDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, valueExitCode);
            Assert.Equal(string.Empty, valueStderr);
            Assert.Equal(2, valueFamilies.Count);
            Assert.All(valueFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Contains(valueFamilies, family => family.GetProperty("signature").GetString()!.Contains("Token value", StringComparison.Ordinal));
            Assert.Contains(valueFamilies, family => family.GetProperty("signature").GetString()!.Contains("Token? value", StringComparison.Ordinal));

            var (gotoExitCode, gotoStdout, gotoStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["Reference", "--db", dbPath, "--exact-name", "--lang", "csharp", "--kind", "function"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, gotoExitCode);
            Assert.Equal(string.Empty, gotoStderr);
            using var gotoDocument = JsonDocument.Parse(gotoStdout);
            Assert.EndsWith(
                "/src/Z.Implementations.cs",
                new Uri(gotoDocument.RootElement.GetProperty("uri").GetString()!).AbsolutePath,
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_PreservesProjectScopeForNullableTypeFacts_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_project_nullable_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "A/A.csproj",
                "msbuild",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "A/Types.cs",
                "csharp",
                """
                namespace Demo;
                public class Node { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "A/Partials.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public partial class Container
                {
                    partial void Scoped(Node? value);
                    partial void Scoped(Node value) { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "B/B.csproj",
                "msbuild",
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "B/Types.cs",
                "csharp",
                """
                namespace Demo;
                public struct Node { }
                """);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText =
                    """
                    UPDATE symbols
                    SET family_key = 'A|' || family_key
                    WHERE family_key IS NOT NULL
                      AND file_id IN (SELECT id FROM files WHERE path LIKE 'A/%');
                    UPDATE symbols
                    SET family_key = 'B|' || family_key
                    WHERE family_key IS NOT NULL
                      AND file_id IN (SELECT id FROM files WHERE path LIKE 'B/%');
                    """;
                command.ExecuteNonQuery();
            }
            MarkGraphAndFoldReady(dbPath);

            var grouped = RunGroupedSymbol(dbPath, "Scoped", "function");

            Assert.Equal(2, grouped.GetProperty("definition_sites").GetInt32());
            Assert.Contains("Node value", grouped.GetProperty("signature").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_CanonicalizesExplicitNullableValueType_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_explicit_nullable_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Container.cs",
                "csharp",
                """
                #nullable enable
                public struct Token { }
                public partial class Container
                {
                    partial void NullableValue(Token? value);
                    partial void NullableValue(global::System.Nullable<Token> value) { }
                    partial void NullableGeneric<T>(T? value) where T : struct;
                    partial void NullableGeneric<T>(global::System.Nullable<T> value) where T : struct { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var grouped = RunGroupedSymbol(dbPath, "NullableValue", "function");
            var groupedGeneric = RunGroupedSymbol(dbPath, "NullableGeneric", "function");

            Assert.Equal(2, grouped.GetProperty("definition_sites").GetInt32());
            Assert.Contains(
                "global::System.Nullable<Token>",
                grouped.GetProperty("signature").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(2, groupedGeneric.GetProperty("definition_sites").GetInt32());
            Assert.Contains(
                "global::System.Nullable<T>",
                groupedGeneric.GetProperty("signature").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_ResolvesScopedAndGenericNullableIdentities_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_scoped_nullable_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Scoped.cs",
                "csharp",
                """
                #nullable enable
                using External;
                namespace Demo;
                public struct Node { }
                public partial class Outer<T>
                {
                    public class Node { }
                    partial void Nested(Node? value);
                    partial void Nested(Node value) { }
                }
                public partial class ReferenceContainer<T> where T : class
                {
                    partial void ContainingReference(T? value);
                }
                public partial class ValueContainer<T> where T : struct
                {
                    partial void ContainingValue(T? value);
                }
                public partial class ShadowOuter<T> where T : class
                {
                    public partial class ShadowInner<T> where T : struct
                    {
                        partial void Shadowed(T? value);
                    }
                }
                public class QualifiedOuter<T>
                {
                    public class QualifiedNode { }
                }
                public partial class Container
                {
                    partial void Qualified(QualifiedOuter<int>.QualifiedNode? value);
                    partial void Qualified(QualifiedOuter<int>.QualifiedNode value) { }
                    partial void Generic<T>(T? value) where T : class;
                    partial void Generic<T>(T value) where T : class { }
                    partial void Combining(int á);
                    partial void Combining(int b́) { }
                    partial void Imported(ImportedNode value);
                    partial void Imported(ImportedNode? value);
                    partial void Imported(ImportedNode value) { }
                    partial void Imported(ImportedNode? value) { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/ScopedImplementations.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public partial class ReferenceContainer<T> where T : class
                {
                    partial void ContainingReference(T value) { }
                }
                public partial class ValueContainer<T> where T : struct
                {
                    partial void ContainingValue(global::System.Nullable<T> value) { }
                }
                public partial class ShadowOuter<T> where T : class
                {
                    public partial class ShadowInner<T> where T : struct
                    {
                        partial void Shadowed(global::System.Nullable<T> value) { }
                    }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/UnrelatedLeaf.cs",
                "csharp",
                """
                namespace Other;
                public class ImportedNode { }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.FileLocal.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                file class LocalNode { }
                file partial class LocalContainer
                {
                    partial void Local(LocalNode? value);
                    partial void Local(LocalNode value) { }
                }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.FileLocal.cs",
                "csharp",
                """
                namespace Demo;
                file struct LocalNode { }
                """);
            MarkGraphAndFoldReady(dbPath);

            foreach (var name in new[]
                     {
                         "Nested",
                         "ContainingReference",
                         "ContainingValue",
                         "Shadowed",
                         "Qualified",
                         "Generic",
                         "Combining",
                         "Local",
                     })
            {
                var grouped = RunGroupedSymbol(dbPath, name, "function");
                Assert.True(grouped.TryGetProperty("definition_sites", out var definitionSites), grouped.GetRawText());
                Assert.Equal(2, definitionSites.GetInt32());
            }

            var (importedExitCode, importedStdout, importedStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunSymbols(
                    ["Imported", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "function", "--group-partials", "--limit", "10"],
                    _jsonOptions));
            using var importedDocument = ParseJsonOutput(importedStdout);
            var importedFamilies = importedDocument.RootElement.EnumerateArray().ToList();
            Assert.Equal(CommandExitCodes.Success, importedExitCode);
            Assert.Equal(string.Empty, importedStderr);
            Assert.Equal(2, importedFamilies.Count);
            Assert.All(
                importedFamilies,
                family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));

            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial T? Generic<T>(T? value) where T : class;",
                    "Generic",
                    "T?"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial T Generic<TResult>(TResult value) where TResult : class { }",
                    "Generic",
                    "TResult"));
            Assert.NotEqual(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Value<T>(T? value) where T : struct;",
                    "Value",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void Value<T>(T value) where T : struct { }",
                    "Value",
                    "void"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_LazilyRefreshesExternalTypeFacts_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_external_type_facts_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Initial.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public class InitialNode { }
                public partial class Container
                {
                    partial void Initial(InitialNode? value);
                    partial void Initial(InitialNode value) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var scans = 0;
            var candidateScans = new List<CSharpCallableTypeKindLookup.CandidateScanStats>();
            CSharpCallableTypeKindLookup.ScanForTesting = () => scans++;
            CSharpCallableTypeKindLookup.CandidateScanForTesting = candidateScans.Add;
            try
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                using var reader = new DbReader(db);
                Assert.Equal(0, scans);

                var physical = reader.SearchSymbols(
                    ["Initial"],
                    limit: 10,
                    kind: "function",
                    lang: "csharp",
                    exact: true,
                    groupPartials: false);
                Assert.Equal(2, physical.Count);
                Assert.Equal(0, scans);

                var initial = Assert.Single(reader.SearchSymbols(
                    ["Initial"],
                    limit: 10,
                    kind: "function",
                    lang: "csharp",
                    exact: true,
                    groupPartials: true));
                Assert.Equal(2, initial.DefinitionSites);
                Assert.Equal(1, scans);
                var initialScan = Assert.Single(candidateScans);
                Assert.False(initialScan.UsedFullScan);
                Assert.Equal(2, initialScan.CallableCount);
                Assert.InRange(initialScan.TypeFactCount, 1, 4);

                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    "src/Late.cs",
                    "csharp",
                    """
                    #nullable enable
                    namespace Demo;
                    public class LateNode { }
                    public partial class Container
                    {
                        partial void Late(LateNode? value);
                        partial void Late(LateNode value) { }
                    }
                    """);

                var late = Assert.Single(reader.SearchSymbols(
                    ["Late"],
                    limit: 10,
                    kind: "function",
                    lang: "csharp",
                    exact: true,
                    groupPartials: true));
                Assert.Equal(2, late.DefinitionSites);
                Assert.Equal(2, scans);
                Assert.Equal(2, candidateScans.Count);
                var lateScan = candidateScans[^1];
                Assert.False(lateScan.UsedFullScan);
                Assert.Equal(2, lateScan.CallableCount);
                Assert.InRange(lateScan.TypeFactCount, 1, 5);
            }
            finally
            {
                CSharpCallableTypeKindLookup.ScanForTesting = null;
                CSharpCallableTypeKindLookup.CandidateScanForTesting = null;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Impact_UsesCallableIdentifierColumnForTestMethod_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_test_method_column_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/ResultTests.cs",
                "csharp",
                """
                public class Result { }
                public class ResultTests
                {
                    [Fact]
                    public Result Result() => new();
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunImpact(
                ["Result", "--db", dbPath, "--json", "--lang", "csharp", "--max-hops", "0", "--limit", "10"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);
            var testMethod = Assert.Single(
                document.RootElement
                    .GetProperty("definitions")
                    .EnumerateArray()
                    .Where(definition => definition.GetProperty("kind").GetString() == "test.method"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(18, testMethod.GetProperty("start_column").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_DistinguishesNestedTypeNamesAndArities_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_nested_identity_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            foreach (var path in new[] { "src/A.Host.cs", "src/B.Host.cs" })
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    path,
                    "csharp",
                    """
                    namespace Demo;
                    public partial class Host
                    {
                        public partial class Child { }
                        public partial class Sibling { }
                        public partial class Nested<T> { }
                        public partial class Nested<T, U> { }
                    }
                    """);
            }
            MarkGraphAndFoldReady(dbPath);

            var child = RunGroupedSymbol(dbPath, "Child", "class");
            var sibling = RunGroupedSymbol(dbPath, "Sibling", "class");
            Assert.Equal(2, child.GetProperty("definition_sites").GetInt32());
            Assert.Equal(2, sibling.GetProperty("definition_sites").GetInt32());
            Assert.NotEqual(
                child.GetProperty("partial_family_id").GetString(),
                sibling.GetProperty("partial_family_id").GetString());

            var (symbolsExitCode, symbolsStdout, symbolsStderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Nested", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var symbolsDocument = ParseJsonOutput(symbolsStdout);
            var nestedFamilies = symbolsDocument.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, symbolsExitCode);
            Assert.Equal(string.Empty, symbolsStderr);
            Assert.Equal(2, nestedFamilies.Count);
            Assert.All(nestedFamilies, family => Assert.Equal(2, family.GetProperty("definition_sites").GetInt32()));
            Assert.Equal(
                2,
                nestedFamilies
                    .Select(family => family.GetProperty("partial_family_id").GetString())
                    .Distinct(StringComparer.Ordinal)
                    .Count());

            var (gotoExitCode, gotoStdout, gotoStderr) = CaptureConsole(() => QueryCommandRunner.RunGoto(
                ["Nested", "--db", dbPath, "--exact-name", "--lang", "csharp", "--kind", "class"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, gotoExitCode);
            Assert.Equal(string.Empty, gotoStdout);
            Assert.Contains("goto found 2 matching definitions", gotoStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_UsesPlainRecordDeclarationArityAfterAttributes_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_record_attribute_arity_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Item.cs",
                "csharp",
                """
                using System;
                namespace Demo;
                public sealed class MarkerAttribute : Attribute
                {
                    public MarkerAttribute(Type type) { }
                }
                public class Item { }
                [Marker(typeof(Item))] public partial record Item<T>;
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.Item.cs",
                "csharp",
                """
                namespace Demo;
                public partial record Item<T>;
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Item", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);
            var symbols = document.RootElement.EnumerateArray().ToList();
            var recordFamily = Assert.Single(
                symbols.Where(symbol => symbol.TryGetProperty("definition_sites", out _)));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, symbols.Count);
            Assert.Equal(2, recordFamily.GetProperty("definition_sites").GetInt32());
            Assert.Equal(2, recordFamily.GetProperty("family_members").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_PropagatesFileLocalScopeToNestedFamilies_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_nested_file_local_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/A.Host.cs",
                "csharp",
                """
                namespace Demo;
                file partial class Host { }
                partial class Host { public partial class Child { } }
                """);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/B.Host.cs",
                "csharp",
                """
                namespace Demo;
                partial class Host { public partial class Child { } }
                """);
            MarkGraphAndFoldReady(dbPath);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
                ["Child", "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", "class", "--group-partials", "--include-generated", "--limit", "10"],
                _jsonOptions));
            using var document = ParseJsonOutput(stdout);
            var families = document.RootElement.EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(2, families.Count);
            Assert.All(families, family => Assert.False(family.TryGetProperty("definition_sites", out _)));
            Assert.Equal(
                ["src/A.Host.cs", "src/B.Host.cs"],
                families.Select(family => family.GetProperty("path").GetString()).Order().ToArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCanonicalRepresentative_IgnoresConstraintTextInsideImplementationBody_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_constraint_body_text_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Container.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public partial class Container
                {
                    partial void M<T>(T? value);
                    partial void M<T>(T? value) { var text = "where T : struct"; }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var grouped = RunGroupedSymbol(dbPath, "M", "function");

            Assert.Equal(2, grouped.GetProperty("definition_sites").GetInt32());
            Assert.Equal("implementation_body", grouped.GetProperty("representative_reason").GetString());
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void M<T>(T? value);",
                    "M",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void M<T>(T? value) { var text = \"where T : struct\"; }",
                    "M",
                    "void"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCallableGrouping_UsesFoldedCandidateNames_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_folded_candidate_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Container.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public class Node { }
                public partial class Container
                {
                    partial void MÉTHODE(Node? value);
                    partial void MÉTHODE(Node value) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var grouped = RunGroupedSymbol(dbPath, "méthode", "function");

            Assert.Equal(2, grouped.GetProperty("definition_sites").GetInt32());
            Assert.Equal("implementation_body", grouped.GetProperty("representative_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PartialCallableGrouping_NormalizesNullableTupleShorthand_Issue4914()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_partial_nullable_tuple_issue4914");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Container.cs",
                "csharp",
                """
                #nullable enable
                namespace Demo;
                public partial class Container
                {
                    partial void M((int, int)? value);
                    partial void M(global::System.Nullable<(int, int)> value) { }
                }
                """);
            MarkGraphAndFoldReady(dbPath);

            var grouped = RunGroupedSymbol(dbPath, "M", "function");

            Assert.Equal(2, grouped.GetProperty("definition_sites").GetInt32());
            Assert.Equal("implementation_body", grouped.GetProperty("representative_reason").GetString());
            Assert.Equal(
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void M((int, int)? value);",
                    "M",
                    "void"),
                LogicalPartialSymbolGrouper.BuildCallableIdentity(
                    "partial void M(global::System.Nullable<(int, int)> value) { }",
                    "M",
                    "void"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private JsonElement RunGroupedSymbol(string dbPath, string name, string kind)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
            [name, "--db", dbPath, "--json=array", "--exact-name", "--lang", "csharp", "--kind", kind, "--group-partials", "--include-generated", "--limit", "1"],
            _jsonOptions));
        Assert.False(string.IsNullOrWhiteSpace(stdout), stderr);
        using var document = ParseJsonOutput(stdout);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return Assert.Single(document.RootElement.EnumerateArray().ToList()).Clone();
    }
}
