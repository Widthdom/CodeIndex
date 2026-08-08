using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    private const string CSharpMarkerFixture = """
        using System;
        interface Contract {}
        class Parent {}
        class Widget {}
        class Demo : Parent
        {
            event Action Changed;
            Demo() : this(0) {}
            Demo(int value) {}
            void Target() {}
            void Handle() {}
            void Run(object value)
            {
                Changed += Handle;
                _ = typeof(Widget);
                _ = typeof(Demo).GetMethod("Target");
                var local = 1;
                Func<int> capture = () => local;
                Console.WriteLine(local);
                if (value is Widget) {}
                switch (value) { case Widget item: break; }
                _ = new Widget { };
            }
        }
        class Box<T> where T : Contract {}
        """;

    private const string JavaMarkerFixture = """
        module demo.module {
            requires app.core;
            uses api.ModuleService;
            provides api.ModuleService with impl.ModuleImpl;
        }
        class Parent {}
        @Deprecated
        class Widget<T> extends Parent {
            Widget() { this(1); }
            Widget(int value) {
                Runnable action = this::work;
                Class<?> literal = Widget.class;
                if (this instanceof Parent) {}
            }
            <U extends BoundService> U load() throws LoadFailure { return null; }
            void work() {}
        }
        """;

    private const string KotlinMarkerFixture = """
        annotation class Audit
        open class Parent
        interface Contract
        class Bound
        class Input
        class Output
        class Source
        class Result
        class Receiver
        class `Fancy Widget`
        infix fun Receiver.combine(other: Receiver): Receiver = this
        @Audit
        class Widget<T : Bound>(val item: T) : Parent() where T : Contract {
            constructor() : this(defaultValue)
            fun load(input: Input): Output = TODO()
            val Source.extension: Result get() = Result()
            fun run(receiver: Receiver, operand: Receiver) {
                val literal = Widget::class
                val made = `Fancy Widget`()
                receiver combine operand
            }
        }
        """;

    private const string JavaScriptMarkerFixture = """
        class Widget {}
        const service = {};
        const node = {};
        const chain = service?.loader;
        const ready = node.kind === "ready";
        const made = new Widget;
        """;

    private const string TypeScriptMarkerFixture = """
        import * as api from "./api";
        declare const source: unknown;
        declare const value: unknown;
        const qualified = api.Widget;
        const narrowed = value as CastType;
        const frozen = { kind: "ready" } as const;
        function load<T extends BoundType>(input: InputType): OutputType { throw 0; }
        class Demo {
            @Inject service: ServiceType;
        }
        type Handler = { run: (input: HandlerInput) => HandlerOutput };
        type SourceType = typeof source;
        const view = <Widget />;
        """;

    public static TheoryData<string, string, string, string> MarkerGatedPositiveCases => new()
    {
        { "csharp", CSharpMarkerFixture, "Changed", "subscribe" },
        { "csharp", CSharpMarkerFixture, "WriteLine", "call" },
        { "csharp", CSharpMarkerFixture, "Widget", "instantiate" },
        { "csharp", CSharpMarkerFixture, "Target", "type_reference" },
        { "csharp", CSharpMarkerFixture, "local", "capture" },
        { "csharp", CSharpMarkerFixture, "Demo", "call" },
        { "csharp", CSharpMarkerFixture, "Console", "type_reference" },
        { "csharp", CSharpMarkerFixture, "Parent", "type_reference" },
        { "csharp", CSharpMarkerFixture, "Contract", "type_reference" },
        { "java", JavaMarkerFixture, "work", "call" },
        { "java", JavaMarkerFixture, "Deprecated", "annotation" },
        { "java", JavaMarkerFixture, "Widget", "call" },
        { "java", JavaMarkerFixture, "Widget", "type_reference" },
        { "java", JavaMarkerFixture, "Parent", "type_reference" },
        { "java", JavaMarkerFixture, "BoundService", "type_reference" },
        { "java", JavaMarkerFixture, "LoadFailure", "type_reference" },
        { "java", JavaMarkerFixture, "api.ModuleService", "type_reference" },
        { "java", JavaMarkerFixture, "impl.ModuleImpl", "type_reference" },
        { "kotlin", KotlinMarkerFixture, "Audit", "annotation" },
        { "kotlin", KotlinMarkerFixture, "Widget", "type_reference" },
        { "kotlin", "class Widget { constructor(value: Int) {}\nconstructor() : this(0) {} }", "Widget", "call" },
        { "kotlin", KotlinMarkerFixture, "Fancy Widget", "instantiate" },
        { "kotlin", KotlinMarkerFixture, "combine", "call" },
        { "kotlin", KotlinMarkerFixture, "Parent", "type_reference" },
        { "kotlin", KotlinMarkerFixture, "Contract", "type_reference" },
        { "kotlin", KotlinMarkerFixture, "Input", "type_reference" },
        { "kotlin", KotlinMarkerFixture, "Output", "type_reference" },
        { "kotlin", KotlinMarkerFixture, "Source", "type_reference" },
        { "javascript", JavaScriptMarkerFixture, "service.loader", "reference" },
        { "javascript", JavaScriptMarkerFixture, "node.kind=ready", "type_tag" },
        { "javascript", JavaScriptMarkerFixture, "Widget", "instantiate" },
        { "typescript", TypeScriptMarkerFixture, "./api", "reference" },
        { "typescript", TypeScriptMarkerFixture, "CastType", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "const", "const_assertion" },
        { "typescript", TypeScriptMarkerFixture, "BoundType", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "InputType", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "OutputType", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "ServiceType", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "HandlerInput", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "HandlerOutput", "type_reference" },
        { "typescript", TypeScriptMarkerFixture, "source", "type_reference" },
        { "swift", "func render(_ action: () -> Void) {}\nfunc run() { render { } }", "render", "call" },
        { "terraform", "output \"region\" { value = var.targetRegion }", "targetRegion", "reference" },
        { "json", "{ \"entry\": \"./src/app.ts\" }", "src/app.ts", "project_reference" },
        { "yaml", "jobs:\n  build:\n    steps:\n      - run: |\n          ./scripts/build.sh", "scripts/build.sh", "project_reference" },
    };

    [Theory]
    [MemberData(nameof(MarkerGatedPositiveCases))]
    public void Extract_MarkerGatedPositiveSyntax_PreservesReferences(
        string language,
        string content,
        string expectedName,
        string expectedKind)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);
        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == expectedName
            && reference.ReferenceKind == expectedKind);
    }

    [Fact]
    public void Extract_MarkupMarkerGates_UpdateStateBeforeSkippingPlainLines()
    {
        const string graphql = """
            type Query {
              plainIdentifier
              item: Widget
            }
            """;
        var graphqlSymbols = SymbolExtractor.Extract(1, "graphql", graphql);
        var graphqlReferences = ReferenceExtractor.Extract(1, "graphql", graphql, graphqlSymbols);
        Assert.Contains(graphqlReferences, reference =>
            reference.SymbolName == "Widget"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Query");

        const string html = """
            <!--
            markerless comment body
            -->
            <status-card></status-card>
            """;
        var htmlSymbols = SymbolExtractor.Extract(2, "html", html);
        var htmlReferences = ReferenceExtractor.Extract(2, "html", html, htmlSymbols);
        Assert.Contains(htmlReferences, reference =>
            reference.SymbolName == "status-card"
            && reference.ReferenceKind == "call");

        const string markdown = """
            ```text
            [ignored](inside-fence.md)
            markerless fence body
            ```
            [kept](outside-fence.md)
            """;
        var markdownSymbols = SymbolExtractor.Extract(3, "markdown", markdown);
        var markdownReferences = ReferenceExtractor.Extract(3, "markdown", markdown, markdownSymbols);
        Assert.DoesNotContain(markdownReferences, reference => reference.SymbolName == "inside-fence.md");
        Assert.Contains(markdownReferences, reference =>
            reference.SymbolName == "outside-fence.md"
            && reference.ReferenceKind == "import");
    }

    [Fact]
    public void Extract_XmlMarkerGates_ContinueMultilineBindingStates()
    {
        const string content = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding
                    Path=Profile.DisplayName}" />
                <Binding.Path>
                    Selection.CurrentName
                </Binding.Path>
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var references = ReferenceExtractor.Extract(1, "xml", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "DisplayName"
            && reference.ReferenceKind == "reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "CurrentName"
            && reference.ReferenceKind == "reference");
    }

    [Fact]
    public void Extract_DockerfileMarkerGates_PreserveMixedCaseStageReferences()
    {
        const string content = """
            FROM alpine AS base
            FrOm base As next
            CoPy --FrOm=base /src /dst
            RuN --MoUnT=type=bind,from=base,target=/src true
            """;

        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);
        var references = ReferenceExtractor.Extract(1, "dockerfile", content, symbols);
        var baseCalls = references
            .Where(reference => reference.SymbolName == "base" && reference.ReferenceKind == "call")
            .ToList();

        Assert.Equal(3, baseCalls.Count);
        Assert.Equal([2, 3, 4], baseCalls.Select(reference => reference.Line).OrderBy(line => line).ToArray());
    }

    [Fact]
    public void Extract_GoMarkerGates_PreserveDeclarationsAndInterfaceMethods()
    {
        const string content = """
            package demo

            type Source struct{}
            type Alias = Source
            var current Source
            const fallback Source = Source{}

            type Holder struct {
                Value Source
            }

            type Loader interface {
                Load(Source) Source
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "go", content);
        var references = ReferenceExtractor.Extract(1, "go", content, symbols);
        var sourceTypes = references
            .Where(reference => reference.SymbolName == "Source" && reference.ReferenceKind == "type_reference")
            .ToList();

        Assert.Contains(sourceTypes, reference => reference.Line == 4);
        Assert.Contains(sourceTypes, reference => reference.Line == 5);
        Assert.Contains(sourceTypes, reference => reference.Line == 6);
        Assert.Contains(sourceTypes, reference => reference.Line == 9);
        Assert.Contains(sourceTypes, reference => reference.Line == 13);
    }
}
