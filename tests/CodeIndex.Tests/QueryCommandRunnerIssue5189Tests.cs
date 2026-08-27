using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Indexer;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerIssue5189Tests
{
    [Fact]
    public void AliasInvocationArity_UsesRecordedSourceSpanAcrossNestedMultilineSyntax_Issue5189()
    {
        const string context = """
                                           _ = new /* before */ Alias(
                                               Factory.Create<List<int>>("a,(b)", 2), // after argument
                                               new[] { 1, 2 });
                                       """;
        var column = context.IndexOf("Alias", StringComparison.Ordinal) + 1;

        Assert.Equal(
            2,
            CSharpTypeReferenceArity.GetInvocationArgumentCount(
                context,
                "Widget",
                column,
                "Alias".Length));
    }

    [Theory]
    [InlineData("_ = new Alias(first: 1, second: 2);")]
    [InlineData("_ = new Alias(\"\"\"raw,(value)\"\"\");")]
    [InlineData("_ = new Alias(left < right, high > low);")]
    [InlineData("_ = new Alias($\"{Make(\"x,y\")}\", 0);")]
    [InlineData("_ = new Alias(1,")]
    public void AliasInvocationArity_KeepsUnsupportedSyntaxAmbiguous_Issue5189(
        string context)
    {
        var column = context.IndexOf("Alias", StringComparison.Ordinal) + 1;

        Assert.Null(CSharpTypeReferenceArity.GetInvocationArgumentCount(
            context,
            "Widget",
            column,
            "Alias".Length));
    }

    [Fact]
    public void AliasInvocationArity_RejectsOverflowingRecordedSpan_Issue5189()
    {
        Assert.Null(CSharpTypeReferenceArity.GetInvocationArgumentCount(
            "_ = new Alias(1);",
            "Widget",
            columnNumber: 1_500_000_001,
            spanLength: 1_500_000_000));
    }

    [Theory]
    [InlineData("_ = new Alias(flag ? 1 : 2);")]
    [InlineData("_ = new Alias(global::System.Math.Abs(1));")]
    public void AliasInvocationArity_CountsNonNamedColonSyntax_Issue5189(string context)
    {
        var column = context.IndexOf("Alias", StringComparison.Ordinal) + 1;

        Assert.Equal(
            1,
            CSharpTypeReferenceArity.GetInvocationArgumentCount(
                context,
                "Widget",
                column,
                "Alias".Length));
    }

    [Fact]
    public void AliasInvocationArity_DoesNotGuessBetweenUnanchoredSameWidthTokens_Issue5189()
    {
        const string context = "new Alpha(1); new Bravo(1, 2);";

        Assert.Null(CSharpTypeReferenceArity.GetInvocationArgumentCount(
            context,
            "Widget",
            columnNumber: 200,
            spanLength: 5));
    }

    [Fact]
    public void AliasInvocationArity_DoesNotUseCanonicalNameInsideArguments_Issue5189()
    {
        foreach (var context in new[]
                 {
                     "_ = new Alias(Widget());",
                     "_ = new Alias(new Widget());",
                     "_ = new Widget(); _ = new Alias(1);",
                 })
        {
            var column = context.IndexOf("Alias", StringComparison.Ordinal) + 1;
            Assert.Equal(
                1,
                CSharpTypeReferenceArity.GetInvocationArgumentCount(
                    context,
                    "Widget",
                    column,
                    "Alias".Length));
        }
    }

    [Fact]
    public void AliasInvocationArity_UsesRecordedSpanWhenContextStartsAtAlias_Issue5189()
    {
        const string context = "        Alias(1, \"two\")";

        Assert.Equal(
            2,
            CSharpTypeReferenceArity.GetInvocationArgumentCount(
                context,
                "Widget",
                columnNumber: 9,
                spanLength: "Alias".Length));
    }

    [Theory]
    [InlineData("public Widget(int value = 0)")]
    [InlineData("public Widget(params int[] values)")]
    [InlineData("public Widget([System.Runtime.InteropServices.Optional] int value)")]
    public void ConstructorParameterCount_KeepsBindingSensitiveDeclarationsAmbiguous_Issue5189(
        string signature)
    {
        Assert.Null(CSharpTypeReferenceArity.GetConstructorParameterCount(
            signature,
            "Widget",
            "function"));
    }

    [Fact]
    public void InspectSelectors_IsolateAliasAndDirectConstructorCalls_Issue5189()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_constructor_alias_issue5189");
        try
        {
            WriteFixture(projectRoot);
            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                JsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            using var firstDocument = InspectConstructors(dbPath, "Widget");
            var firstBundles = GetConstructorBundles(firstDocument, "Widget", "src/First.cs");
            Assert.Equal(3, firstBundles.Length);

            var oneParameter = GetBundle(firstBundles, "Widget(int first)");
            var twoParameter = GetBundle(firstBundles, "Widget(int first, string second)");
            var threeParameter = GetBundle(firstBundles, "Widget(int first, string second, int third)");

            AssertResolvedMarkers(
                oneParameter,
                "first-alias-one",
                "first-direct-one",
                "first-conditional-one",
                "first-global-one",
                "direct-column-collision-one");
            AssertResolvedMarkers(
                twoParameter,
                "first-alias-two",
                "first-direct-two",
                "first-multiline-two",
                "first-nested-two",
                "first-comment-two");
            AssertResolvedMarkers(threeParameter, "first-alias-three", "first-direct-three");
            Assert.DoesNotContain(
                GetReferenceContexts(oneParameter),
                context => context.Contains("first-alias-two", StringComparison.Ordinal));
            Assert.DoesNotContain(
                GetReferenceContexts(twoParameter),
                context => context.Contains("first-alias-one", StringComparison.Ordinal));
            Assert.DoesNotContain(
                GetReferenceContexts(threeParameter),
                context => context.Contains("first-alias-two", StringComparison.Ordinal));
            Assert.DoesNotContain(
                firstBundles.SelectMany(GetReferenceContexts),
                context => context.Contains("second-alias-two", StringComparison.Ordinal));

            Assert.True(oneParameter.GetProperty("identity_scoped").GetBoolean());
            Assert.True(twoParameter.GetProperty("identity_scoped").GetBoolean());
            Assert.True(threeParameter.GetProperty("identity_scoped").GetBoolean());
            AssertSelectorGraph(dbPath, oneParameter, "first-alias-one", "FirstAliasCalls");
            AssertSelectorGraph(dbPath, twoParameter, "first-alias-two", "FirstAliasCalls");
            AssertSelectorGraph(dbPath, threeParameter, "first-alias-three", "FirstAliasCalls");

            using var secondDocument = InspectConstructors(dbPath, "Widget", "src/Second.cs");
            var secondBundles = GetConstructorBundles(secondDocument, "Widget", "src/Second.cs");
            var secondTwoParameter = GetBundle(
                secondBundles,
                "Widget(int first, string second)");
            AssertSecondAliasResolution(dbPath, secondTwoParameter);

            using var escapedDocument = InspectConstructors(dbPath, "EscapedWidget");
            var escapedBundles = GetConstructorBundles(
                escapedDocument,
                "EscapedWidget",
                "src/Conservative.cs");
            Assert.Equal(2, escapedBundles.Length);
            var escapedOneParameter = GetBundle(escapedBundles, "EscapedWidget(int @params)");
            var escapedTwoParameter = GetBundle(
                escapedBundles,
                "EscapedWidget(int value, string text)");
            AssertResolvedMarkers(escapedOneParameter, "escaped-params-one");
            Assert.DoesNotContain(
                GetReferenceContexts(escapedTwoParameter),
                context => context.Contains("escaped-params-one", StringComparison.Ordinal));
            Assert.True(escapedOneParameter.GetProperty("identity_scoped").GetBoolean());

            using var primaryDocument = InspectConstructors(dbPath, "PrimaryOptionalWidget");
            var primaryBundles = GetCandidateBundles(
                primaryDocument,
                "PrimaryOptionalWidget",
                "src/Conservative.cs");
            Assert.Equal(2, primaryBundles.Length);
            Assert.All(
                primaryBundles,
                bundle => Assert.False(bundle.GetProperty("identity_scoped").GetBoolean()));
            Assert.All(
                primaryBundles,
                bundle => Assert.Equal(
                    "ambiguous_reference_candidates",
                    bundle.GetProperty("identity_scope_reason").GetString()));
            Assert.All(
                primaryBundles,
                bundle => AssertResolvedMarkers(
                    bundle,
                    "primary-optional-alias-zero",
                    "primary-optional-direct-zero"));

            foreach (var ambiguousName in new[]
                     {
                         "AmbiguousWidget",
                         "OptionalWidget",
                         "ParamsWidget",
                         "RelationalWidget",
                         "InterpolatedWidget",
                     })
            {
                using var ambiguousDocument = InspectConstructors(
                    dbPath,
                    ambiguousName);
                var ambiguousBundles = GetConstructorBundles(
                    ambiguousDocument,
                    ambiguousName,
                    "src/Conservative.cs");
                Assert.True(ambiguousBundles.Length >= 2);
                Assert.All(
                    ambiguousBundles,
                    bundle => Assert.False(bundle.GetProperty("identity_scoped").GetBoolean()));
                Assert.All(
                    ambiguousBundles,
                    bundle => Assert.Equal(
                        "ambiguous_reference_candidates",
                        bundle.GetProperty("identity_scope_reason").GetString()));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void WriteFixture(string projectRoot)
    {
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "src/First.cs",
            """
            namespace First;

            public sealed class Widget
            {
                public Widget(int first) { }
                public Widget(int first, string second) { }
                public Widget(int first, string second, int third) { }
            }
            """);
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "src/Second.cs",
            """
            namespace Second;

            public sealed class Widget
            {
                public Widget(int first) { }
                public Widget(int first, string second) { }
                public Widget(int first, string second, int third) { }
            }
            """);
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "src/Conservative.cs",
            """
            namespace Conservative;

            public sealed class AmbiguousWidget
            {
                public AmbiguousWidget(string value) { }
                public AmbiguousWidget(string left, string right) { }
                public AmbiguousWidget(string first, string second, string third) { }
            }

            public sealed class OptionalWidget
            {
                public OptionalWidget(int value = 0) { }
                public OptionalWidget(string value = "") { }
            }

            public sealed class ParamsWidget
            {
                public ParamsWidget(params int[] values) { }
                public ParamsWidget(string value) { }
            }

            public sealed class EscapedWidget
            {
                public EscapedWidget(int @params) { }
                public EscapedWidget(int value, string text) { }
            }

            public sealed class PrimaryOptionalWidget(int value)
            {
                public PrimaryOptionalWidget(string value = "") : this(0) { }
            }

            public sealed class RelationalWidget
            {
                public RelationalWidget(bool value) { }
                public RelationalWidget(bool left, bool right) { }
                public RelationalWidget(bool first, bool second, bool third) { }
            }

            public sealed class InterpolatedWidget
            {
                public InterpolatedWidget(string value) { }
                public InterpolatedWidget(string value, int second) { }
                public InterpolatedWidget(string value, int second, int third) { }
            }
            """);
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "src/Calls.cs",
            """"
            using FirstWidget = First.Widget;
            using SecondWidget = Second.Widget;
            using Widget = Second.Widget;
            using AmbiguousAlias = Conservative.AmbiguousWidget;
            using OptionalAlias = Conservative.OptionalWidget;
            using ParamsAlias = Conservative.ParamsWidget;
            using EscapedAlias = Conservative.EscapedWidget;
            using PrimaryOptionalAlias = Conservative.PrimaryOptionalWidget;
            using RelationalAlias = Conservative.RelationalWidget;
            using InterpolatedAlias = Conservative.InterpolatedWidget;

            namespace Calls;

            public sealed class Caller
            {
                private static T Make<T>(T value) => value;

                public void FirstAliasCalls()
                {
                    _ = new FirstWidget(1); // first-alias-one
                    _ = new FirstWidget(1, "two"); // first-alias-two
                    _ = new FirstWidget(1, "two", 3); // first-alias-three
                    _ = new FirstWidget(
                        1,
                        "comma,(parenthesis)"); // first-multiline-two
                    _ = new FirstWidget(Make(new System.Collections.Generic.List<int> { 1, 2 }), "two"); // first-nested-two
                    _ = new /* before */ FirstWidget(1, /* between */ "two"); // first-comment-two
                    _ = new FirstWidget(true ? 1 : 2); // first-conditional-one
                    _ = new FirstWidget(global::System.Math.Abs(1)); // first-global-one
                }

                public void DirectCalls()
                {
                    _ = new First.Widget(1); // first-direct-one
                    _ = new First.Widget(1, "two"); // first-direct-two
                    _ = new First.Widget(1, "two", 3); // first-direct-three
                                   _ = new First.Widget(1); _ = new Gadget(1, "two"); // direct-column-collision-one
                }

                public void SameLeafAliases()
                {
                    _ = new FirstWidget(1); // same-leaf-first-one
                    _ = new SecondWidget(1, "two"); // second-alias-two
                    _ = new Widget(1, "two"); // same-name-alias-two
                }

                public void ConservativeCalls()
                {
                    _ = new AmbiguousAlias(first: "one", second: "two");
                    _ = new AmbiguousAlias("""raw,(value)""");
                    _ = new OptionalAlias();
                    _ = new ParamsAlias(1, 2);
                    _ = new EscapedAlias(1); // escaped-params-one
                    _ = new PrimaryOptionalAlias(); // primary-optional-alias-zero
                    _ = new Conservative.PrimaryOptionalWidget(); // primary-optional-direct-zero
                    _ = new RelationalAlias(1 < 2, 3 > 2); // relational-ambiguous-two
                    _ = new InterpolatedAlias($"{Make("x,y")}", 0); // interpolated-ambiguous-two
                }
            }

            public sealed class Gadget
            {
                public Gadget(int first, string second) { }
            }
            """");
    }

    private static JsonDocument InspectConstructors(
        string dbPath,
        string name,
        string? path = null)
    {
        var args = new List<string>
        {
            name,
            "--db", dbPath,
            "--json",
            "--exact-name",
            "--lang", "csharp",
            "--limit", "20",
        };
        if (path != null)
        {
            args.Add("--path");
            args.Add(path);
        }

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
            args.ToArray(),
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return ParseJsonOutput(stdout);
    }

    private static void AssertSecondAliasResolution(
        string dbPath,
        JsonElement secondTwoParameter)
    {
        var expectedSymbolId = secondTwoParameter
            .GetProperty("selector")
            .GetProperty("symbol_id")
            .GetInt64();
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunReferences(
            [
                "Widget",
                "--db", dbPath,
                "--json",
                "--exact-name",
                "--lang", "csharp",
                "--kind", "instantiate",
                "--path", "src/Calls.cs",
                "--limit", "50",
            ],
            JsonOptions));
        var rows = ParseJsonLines(stdout);
        try
        {
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            foreach (var marker in new[] { "second-alias-two", "same-name-alias-two" })
            {
                var secondAlias = Assert.Single(rows.Where(document =>
                    document.RootElement.GetProperty("context").GetString()!
                        .Contains(marker, StringComparison.Ordinal)));
                Assert.Equal("Widget", secondAlias.RootElement.GetProperty("symbol_name").GetString());
                Assert.Equal("resolved", secondAlias.RootElement.GetProperty("resolution_state").GetString());
                Assert.Equal(1, secondAlias.RootElement.GetProperty("resolution_candidate_count").GetInt32());
                Assert.Equal(expectedSymbolId, secondAlias.RootElement.GetProperty("target_symbol_id").GetInt64());
            }
        }
        finally
        {
            foreach (var row in rows)
                row.Dispose();
        }
    }

    private static JsonElement[] GetConstructorBundles(
        JsonDocument document,
        string name,
        string path)
        => document.RootElement
            .GetProperty("candidate_bundles")
            .EnumerateArray()
            .Where(bundle =>
                bundle.GetProperty("definition").GetProperty("kind").GetString() == "function"
                && bundle.GetProperty("definition").GetProperty("path").GetString() == path
                && bundle.GetProperty("definition").GetProperty("signature").GetString()!
                    .Contains($"{name}(", StringComparison.Ordinal))
            .ToArray();

    private static JsonElement[] GetCandidateBundles(
        JsonDocument document,
        string name,
        string path)
        => document.RootElement
            .GetProperty("candidate_bundles")
            .EnumerateArray()
            .Where(bundle =>
                bundle.GetProperty("definition").GetProperty("path").GetString() == path
                && bundle.GetProperty("definition").GetProperty("signature").GetString()!
                    .Contains($"{name}(", StringComparison.Ordinal))
            .ToArray();

    private static JsonElement GetBundle(IEnumerable<JsonElement> bundles, string signature)
        => Assert.Single(bundles.Where(bundle =>
            bundle.GetProperty("definition").GetProperty("signature").GetString()!
                .Contains(signature, StringComparison.Ordinal)));

    private static string[] GetReferenceContexts(JsonElement bundle)
        => bundle.GetProperty("references")
            .EnumerateArray()
            .Select(reference => reference.GetProperty("context").GetString()!)
            .ToArray();

    private static void AssertResolvedMarkers(JsonElement bundle, params string[] markers)
    {
        var contexts = GetReferenceContexts(bundle);
        foreach (var marker in markers)
        {
            Assert.Contains(
                contexts,
                context => context.Contains(marker, StringComparison.Ordinal));
        }
    }

    private static void AssertSelectorGraph(
        string dbPath,
        JsonElement bundle,
        string referenceMarker,
        string callerName)
    {
        var selector = bundle.GetProperty("selector").GetProperty("selector").GetString()!;
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunInspect(
            ["--selector", selector, "--db", dbPath, "--json", "--limit", "20"],
            JsonOptions));
        using var document = ParseJsonOutput(stdout);
        var selectedBundle = Assert.Single(
            document.RootElement.GetProperty("candidate_bundles").EnumerateArray());

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(
            GetReferenceContexts(selectedBundle),
            context => context.Contains(referenceMarker, StringComparison.Ordinal));
        Assert.Contains(
            selectedBundle.GetProperty("callers").EnumerateArray(),
            caller => string.Equals(
                caller.GetProperty("caller_name").GetString(),
                callerName,
                StringComparison.Ordinal));
    }
}
