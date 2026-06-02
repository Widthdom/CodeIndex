using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_EmitsConsumesHookReferences(string language)
    {
        const string content = """
            import { useEffect, useState } from "react";

            const useLocalState = () => {
              const [value] = useState(0);
              useEffect(() => {}, [value]);
              return value;
            };

            export function Widget() {
              const value = useLocalState();
              if (value) {
                useEffect(() => {}, []);
              }
              return value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, language, content);
        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "useState"
            && reference.ReferenceKind == "consumes_hook"
            && reference.ContainerName == "useLocalState");
        Assert.Contains(references, reference =>
            reference.SymbolName == "useLocalState"
            && reference.ReferenceKind == "consumes_hook"
            && reference.ContainerName == "Widget");
        Assert.Contains(references, reference =>
            reference.SymbolName == "useEffect"
            && reference.ReferenceKind == "consumes_hook"
            && reference.ContainerName == "Widget");
    }

    [Fact]
    public void Extract_JavaMethodReferences_TrackMethodReferencesAndConstructors()
    {
        // issue #239: Java / Kotlin / Scala `::` references should index as handoff edges.
        // issue #239: Java / Kotlin / Scala の `::` 参照も handoff edge として索引化すること。
        const string content = """
            package demo;

            import java.util.List;
            import java.util.function.Supplier;
            import java.util.stream.Collectors;

            public class J {
                public int sum(List<Integer> xs) {
                    return xs.stream().mapToInt(Integer::intValue).sum();
                }

                public String names(List<String> xs) {
                    return xs.stream()
                        .map(String::toUpperCase)
                        .collect(Collectors.joining(","));
                }

                public Supplier<J> make() {
                    return J::new;
                }

                public Supplier<java.util.Iterator<Integer>> iterator(List<Integer> xs) {
                    return xs::iterator;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "intValue"
            && r.ReferenceKind == "call"
            && r.ContainerName == "sum");
        Assert.Contains(references, r =>
            r.SymbolName == "Integer"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("Integer::intValue", StringComparison.Ordinal)
            && r.ContainerName == "sum");
        Assert.Contains(references, r =>
            r.SymbolName == "toUpperCase"
            && r.ReferenceKind == "call"
            && r.ContainerName == "names");
        Assert.Contains(references, r =>
            r.SymbolName == "String"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("String::toUpperCase", StringComparison.Ordinal)
            && r.ContainerName == "names");
        Assert.Contains(references, r =>
            r.SymbolName == "J"
            && r.ReferenceKind == "instantiate"
            && r.ContainerName == "make");
        Assert.Contains(references, r =>
            r.SymbolName == "iterator"
            && r.ReferenceKind == "call"
            && r.ContainerName == "iterator");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "xs"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("xs::iterator", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_JavaArrayConstructorMethodReferences_NormalizesOwnerType()
    {
        const string content = """
            package demo;

            public class Widget {}

            public class Factory {
                public Object make() {
                    return Widget[]::new;
                }

                public Object makeNested() {
                    return demo.Widget[][]::new;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Widget"
            && r.ReferenceKind == "instantiate"
            && r.Context.Contains("Widget[]::new", StringComparison.Ordinal)
            && r.ContainerName == "make");
        Assert.Contains(references, r =>
            r.SymbolName == "demo.Widget"
            && r.ReferenceKind == "instantiate"
            && r.Context.Contains("demo.Widget[][]::new", StringComparison.Ordinal)
            && r.ContainerName == "makeNested");
        Assert.DoesNotContain(references, r => r.SymbolName.Contains("[]", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_JavaGenericOwnerMethodReferences_NormalizesOwnerType()
    {
        const string content = """
            package demo;

            import java.util.function.Function;
            import java.util.function.Supplier;

            public class Box<T> {
                public String open() {
                    return "";
                }
            }

            public class Factory {
                public Supplier<Box<String>> make() {
                    return Box<String>::new;
                }

                public Function<Box<String>, String> opener() {
                    return Box<String>::open;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Box"
            && r.ReferenceKind == "instantiate"
            && r.Context.Contains("Box<String>::new", StringComparison.Ordinal)
            && r.ContainerName == "make");
        Assert.Contains(references, r =>
            r.SymbolName == "open"
            && r.ReferenceKind == "call"
            && r.Context.Contains("Box<String>::open", StringComparison.Ordinal)
            && r.ContainerName == "opener");
        Assert.Contains(references, r =>
            r.SymbolName == "Box"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("Box<String>::open", StringComparison.Ordinal)
            && r.ContainerName == "opener");
        Assert.DoesNotContain(references, r => r.SymbolName.Contains("<", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_JavaGenericCallableSignatures_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            package demo;

            interface Comparable<T> {}
            class Payload {}

            class Demo {
                public <T extends Comparable<T>> T pick(T input, Comparable<T> fallback) {
                    return input;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Comparable" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_JavaGenericHeritage_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            package demo;

            interface Comparable<T> {}
            class Base<T> {}
            interface Handler<T> {}
            class Box<T extends Comparable<T>> extends Base<T> implements Handler<T> {}
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Comparable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Base" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_JavaGenericThrows_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            package demo;

            class Failure extends Exception {}
            class Demo {
                public <E extends Failure> void run() throws E {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Failure" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "E" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinCallableReferences_TrackOwnerTypeReferences()
    {
        const string content = """
            class User {
                fun name(): String = "u"
            }

            class Worker {
                fun wire(users: List<User>) {
                    val names = users.map(User::name)
                    val user = User()
                    val bound = user::name
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "name"
            && r.ReferenceKind == "call"
            && r.Context.Contains("User::name", StringComparison.Ordinal)
            && r.ContainerName == "wire");
        Assert.Contains(references, r =>
            r.SymbolName == "User"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("User::name", StringComparison.Ordinal)
            && r.ContainerName == "wire");
        Assert.Contains(references, r =>
            r.SymbolName == "name"
            && r.ReferenceKind == "call"
            && r.Context.Contains("user::name", StringComparison.Ordinal)
            && r.ContainerName == "wire");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "user"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("user::name", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_KotlinInfixCalls_TrackBuiltInAndUserDefinedFunctions()
    {
        const string content = """
            class Bag {
                infix fun add(item: String): Bag = this
                infix fun merge(other: Bag): Bag = this
                infix fun List<String>.combine(other: List<String>): List<String> = this
                infix fun demo.Box.link(other: demo.Box): demo.Box = this
                private infix fun demo.Box.hidden(other: demo.Box): demo.Box = this

                fun build(other: Bag, value: Int) {
                    val xs = listOf("a")
                    val box = demo.Box()
                    val pair = 1 to "one"
                    val named = "name" to value
                    val summed = (1 + 2) to "sum"
                    val shifted = value shl 4
                    val masked = value and 15
                    val ranged = 1 until 10
                    val countdown = 10 downTo 1
                    val evens = 0..10 step 2
                    val combined = value or 2
                    val toggled = value xor 3
                    val shrunk = value shr 1
                    this add "item"
                    this merge other
                    xs combine xs
                    box link box
                    val words = "plain text to ignore"
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        foreach (var name in new[] { "to", "shl", "and", "until", "downTo", "step", "or", "xor", "shr", "add", "merge", "combine", "link" })
        {
            Assert.True(
                references.Any(r => r.SymbolName == name && r.ReferenceKind == "call"),
                $"Expected Kotlin infix call reference for {name}.");
        }

        Assert.True(references.Count(r => r.SymbolName == "to" && r.ReferenceKind == "call") >= 3);
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "to"
            && r.ReferenceKind == "call"
            && r.Context.Contains("plain text", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "hidden"
            && r.ReferenceKind == "call"
            && r.Context.Contains("private infix fun", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_JavaTextBlock_DoesNotEmitPhantomCallReferences()
    {
        const string content = """"
            public class Main {
                void doReal() {
                    legitimateCall();
                    another(1, 2);
                }

                String templateA = """
                    SELECT * FROM users WHERE name = badCall(x);
                    UPDATE phantomMethod(arg) SET y = z;
                    otherFake(1, 2, 3);
                    """;

                String templateB = """
                    someIdentifier();

                    """;

                String templateC = """
                    literal only
                    """ + suffix();

                void legitimateCall() {}
                void another(int a, int b) {}
                String suffix() { return ""; }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "badCall"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "otherFake"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "someIdentifier"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "legitimateCall"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "doReal");
        Assert.Contains(references, reference =>
            reference.SymbolName == "another"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "doReal");
        Assert.Contains(references, reference =>
            reference.SymbolName == "suffix"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_JavaScriptTemplateLiteral_KeepsSingleLineInterpolationCallReferences()
    {
        const string content = """
            function run() {
                return 42;
            }

            function use() {
                const value = `value = ${run()}`;
                return value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        var runReference = Assert.Single(references);
        Assert.Equal("run", runReference.SymbolName);
        Assert.Equal("call", runReference.ReferenceKind);
        Assert.Equal("use", runReference.ContainerName);
    }

    [Fact]
    public void Extract_JavaScriptRequireCall_IsNotDropped()
    {
        const string content = """
            function load() {
                require("fs");
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "require" && reference.ContainerName == "load");
    }

    [Fact]
    public void Extract_JavaScriptLineContinuationString_DoesNotLeakPhantomReferences()
    {
        const string crlf = "\r\n";
        var content = string.Concat(
            "function caller() {", crlf,
            "  const s = \"line1\\", crlf,
            "} externalCall() line2\";", crlf,
            "  runTask();", crlf,
            "}", crlf,
            "function runTask() {}");

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "runTask" && reference.ContainerName == "caller");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "externalCall");
    }

    [Fact]
    public void Extract_JavaScriptContinuedSingleQuotedString_DoesNotPolluteForOfHeaderScan()
    {
        const string content = "function f() {\n" +
            "    const s = 'line1\\\n" +
            "of externalCall';\n" +
            "    for (\n" +
            "        const ch of `abc`\n" +
            "    ) {\n" +
            "        use(ch);\n" +
            "    }\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptSyntaxConstructs_AreIgnored()
    {
        const string content = """
            class Base {
                run() {}
            }

            class Derived extends Base {
                constructor() {
                    import("fs");
                    super();
                    require("path");
                }

                *stream(item) {
                    yield(item);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "import");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "super");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "yield");
        Assert.Contains(references, reference => reference.SymbolName == "require" && reference.ContainerName == "Derived");
    }

    [Fact]
    public void Extract_JavaNestedGenericConstructors_AreInstantiate()
    {
        // Regression (issue #263): Java constructor calls with nested generic type args
        // such as `new HashMap<String, List<Integer>>()` must still emit instantiate rows.
        // リグレッション (issue #263): `new HashMap<String, List<Integer>>()` のような
        // Java の nested generic コンストラクタ呼び出しも `instantiate` を発行する必要がある。
        const string content = """
            import java.util.ArrayList;
            import java.util.HashMap;
            import java.util.List;

            class Worker {
                void run() {
                    var a = new HashMap<String, List<Integer>>();
                    var b = new ArrayList<HashMap<String, Integer>>();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "HashMap" && r.ReferenceKind == "instantiate" && r.Line == 7);
        Assert.Contains(references, r => r.SymbolName == "ArrayList" && r.ReferenceKind == "instantiate" && r.Line == 8);
        Assert.DoesNotContain(references, r => r.SymbolName == "HashMap" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "ArrayList" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_JavaGenericInvocationTypeArguments_AreTypeReferences()
    {
        const string content = """
            import java.util.Collections;
            import java.util.HashMap;
            import java.util.List;

            class Payload {}
            class Result {}

            class Worker {
                <T> T read() { return null; }

                void run() {
                    var a = new HashMap<String, List<Payload>>();
                    var b = Collections.<Result>emptyList();
                    var c = this.<Payload>read();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference" && r.Line == 12);
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "type_reference" && r.Line == 12);
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference" && r.Line == 13);
        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference" && r.Line == 14);
    }

    [Fact]
    public void Extract_KotlinKnownClassConstructorCalls_AreInstantiate()
    {
        // Kotlin has no `new` keyword, so same-file class constructor calls such as
        // `User(1)` should mirror C# / Java constructor search by emitting `instantiate`.
        // PascalCase functions and annotations must remain `call` / `annotation`.
        // Kotlin には `new` がないため、同一ファイル class の `User(1)` のような
        // constructor 呼び出しを C# / Java と同じく `instantiate` として検索可能にする。
        // PascalCase function と annotation は `call` / `annotation` のままにする。
        const string content = """
            class User(val id: Int)
            data class Profile(val name: String)
            annotation class Marker

            fun Build(): User = User(1)

            @Marker()
            fun decorated() {}

            class Service {
                fun run() {
                    val a = User(1)
                    val b = Profile("p")
                    Build()
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "instantiate" && r.Line == 5);
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "instantiate" && r.Line == 12);
        Assert.Contains(references, r => r.SymbolName == "Profile" && r.ReferenceKind == "instantiate" && r.Line == 13);
        Assert.DoesNotContain(references, r => r.SymbolName == "User" && r.ReferenceKind == "instantiate" && r.Line == 1);
        Assert.DoesNotContain(references, r => r.SymbolName == "User" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "Build" && r.ReferenceKind == "call" && r.Line == 14);
        Assert.DoesNotContain(references, r => r.SymbolName == "Build" && r.ReferenceKind == "instantiate");
        Assert.Contains(references, r => r.SymbolName == "Marker" && r.ReferenceKind == "annotation" && r.Line == 7);
        Assert.DoesNotContain(references, r => r.SymbolName == "Marker" && r.ReferenceKind == "instantiate");
    }

    [Fact]
    public void Extract_KotlinGenericInvocationTypeArguments_AreTypeReferences()
    {
        const string content = """
            class Payload
            class Result
            class Box<T>

            fun <T> read(): T = TODO()

            class Worker {
                fun run() {
                    val box = Box<Payload>()
                    val result = read<Result>()
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Box" && r.ReferenceKind == "instantiate" && r.Line == 9);
        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference" && r.Line == 9);
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference" && r.Line == 10);
    }

    [Fact]
    public void Extract_JavaInvalidNestedGenericParenlessInitializer_DoesNotEmitInstantiate()
    {
        // Java does not have collection/object initializer syntax, so the C#-only nested
        // initializer fallback must not manufacture a phantom `instantiate` edge from
        // invalid Java like `new HashMap<String, List<Integer>> { }`.
        // Java には C# のようなコレクション/オブジェクト initializer 構文がないため、
        // `new HashMap<String, List<Integer>> { }` のような不正構文から
        // phantom な `instantiate` edge を作ってはいけない。
        const string content = """
            import java.util.HashMap;
            import java.util.List;

            class Worker {
                void run() {
                    var a = new HashMap<String, List<Integer>>();
                    var b = new HashMap<String, List<Integer>>
                    {
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "HashMap" && r.ReferenceKind == "instantiate" && r.Line == 6);
        Assert.DoesNotContain(references, r => r.SymbolName == "HashMap" && r.ReferenceKind == "instantiate" && r.Line == 7);
    }

    [Fact]
    public void Extract_JavaParenlessArrayInitializer_IsInstantiate()
    {
        // issue #286 (Java side): `new String[] { "a", "b" }` is genuinely an array
        // instantiation but CallRegex misses it because there is no `(`. Primitive
        // types (`int`, `boolean`, ...) must continue to be skipped.
        // issue #286 の Java 側: `new String[] { "a", "b" }` は配列インスタンス化だが
        // `(` がないため CallRegex で取りこぼす。プリミティブ型（`int` 等）は引き続き除外する。
        const string content = """
            public class Demo {
                public void run() {
                    String[] s = new String[] { "a", "b" };
                    int[] n = new int[] { 1, 2, 3 };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "String" && r.ReferenceKind == "instantiate" && r.Line == 3);
        Assert.DoesNotContain(references, r => r.SymbolName == "int");
        Assert.DoesNotContain(references, r => r.SymbolName == "boolean");
    }

    [Fact]
    public void Extract_JavaTypePositions_CaptureTypeReferences()
    {
        // issue #256 Java side: extends/implements, declaration types, throws, and
        // instanceof should surface as `type_reference` edges without regressing annotations.
        // issue #256 の Java 側: extends/implements、宣言型、throws、instanceof を
        // `type_reference` として拾い、annotation 既存経路も壊さないこと。
        const string content = """
            import java.io.IOException;
            import java.util.List;

            @interface Marker {}
            interface ILogger {}
            class Base {}

            class Derived extends Base implements ILogger {
                private ILogger logger;
                List<ILogger> items;
                Base parent;

                Derived(ILogger logger, Base parent) {
                    this.logger = logger;
                    this.parent = parent;
                }

                List<ILogger> getAll() throws IOException {
                    if (logger instanceof ILogger) {
                        return items;
                    }
                    return items;
                }

                @Marker
                void oldMethod() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.True(references.Count(r => r.SymbolName == "Base" && r.ReferenceKind == "type_reference") >= 3);
        Assert.True(references.Count(r => r.SymbolName == "ILogger" && r.ReferenceKind == "type_reference") >= 5);
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IOException" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Marker" && r.ReferenceKind == "annotation");
    }

    [Fact]
    public void Extract_JavaAnnotatedFinalInstanceof_CapturesTypeReference()
    {
        // Java pattern instanceof can put modifiers and type-use annotations before the tested
        // type; those prefixes should not hide the real dependency or become type references.
        // Java の pattern instanceof では型の前に modifier / type-use annotation が置けるため、
        // prefix で実型の依存を落としたり注釈名を型参照化したりしてはならない。
        const string content = """
            @interface NonNull {}
            class Payload {}

            class Demo {
                boolean run(Object value) {
                    return value instanceof final @NonNull Payload payload;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "NonNull" && r.ReferenceKind == "annotation");
        Assert.DoesNotContain(references, r => r.SymbolName == "NonNull" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "final" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinTypeUseAnnotations_DoNotBecomeTypeReferences()
    {
        // Kotlin type-use annotations should mirror Java type expressions: the annotation
        // itself is metadata, while the annotated type remains the dependency edge.
        // Kotlin の type-use annotation も Java と同様に metadata として扱い、
        // 注釈名を type_reference にせず、注釈された型だけを依存として残す。
        const string content = """
            annotation class Fancy
            class Payload

            class Demo {
                val value: @Fancy Payload = Payload()
                fun run(input: @Fancy Payload): @Fancy Payload = input
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.True(references.Count(r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference") >= 3);
        Assert.Contains(references, r => r.SymbolName == "Fancy" && r.ReferenceKind == "annotation");
        Assert.DoesNotContain(references, r => r.SymbolName == "Fancy" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinExtensionFunctionReceivers_CaptureTypeReferences()
    {
        // Kotlin extension receivers are type-position dependencies just like parameters and
        // return types; `fun User.render()` should make `references User` find the extension.
        // Kotlin の extension receiver は parameter / return type と同じ型位置の依存であり、
        // `fun User.render()` でも `references User` から拡張関数へ辿れる必要がある。
        const string content = """
            class User
            class Box<T>

            fun User.render() {}
            fun Box<User>.unwrap() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.True(references.Count(r => r.SymbolName == "User" && r.ReferenceKind == "type_reference") >= 2);
        Assert.Contains(references, r => r.SymbolName == "Box" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinExtensionPropertyReceivers_CaptureTypeReferences()
    {
        // Extension property receivers are declarations too, but the receiver appears before
        // the property name rather than after a parameter colon.
        // extension property の receiver も宣言上の依存だが、property 名より前に現れるため
        // parameter colon 後の型抽出だけでは拾えない。
        const string content = """
            class User
            class Box<T>

            val User.displayName: String get() = ""
            var Box<User>.selected: User
                get() = TODO()
                set(value) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.True(references.Count(r => r.SymbolName == "User" && r.ReferenceKind == "type_reference") >= 2);
        Assert.Contains(references, r => r.SymbolName == "Box" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinBacktickTypeReferences_NormalizesNames()
    {
        // Kotlin declaration names already strip source-only backticks; type-position
        // references need the same canonical name so dependency search joins them.
        // Kotlin の宣言名は source-only な backtick を外しているため、型位置参照も同じ
        // canonical 名で発行し、依存検索で宣言と結合できるようにする。
        const string content = """
            class `Display Name`
            class Holder<T>

            class Demo {
                val first: `Display Name` = TODO()
                val second: Holder<`Display Name`> = TODO()
                val third: `Display Name`? = null
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(symbols, s => s.Name == "Display Name" && s.Kind == "class");
        var displayNameTypeReferenceCount = references.Count(r => r.SymbolName == "Display Name" && r.ReferenceKind == "type_reference");
        Assert.True(
            displayNameTypeReferenceCount >= 3,
            $"Expected at least 3 Display Name type references, got {displayNameTypeReferenceCount}.");
        Assert.Contains(references, r => r.SymbolName == "Holder" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "`Display Name`" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinBacktickClassLiterals_NormalizesTypeReferenceNames()
    {
        // Kotlin class literals can target backticked type names too; keep them aligned with
        // the declaration's canonical name instead of treating the backticks as a string.
        // Kotlin の class literal でも backtick 付き型名を対象にできるため、backtick を
        // 文字列扱いせず、宣言側と同じ canonical 名で参照を発行する。
        const string content = """
            class `Display Name`

            class Demo {
                val token = `Display Name`::class
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(symbols, s => s.Name == "Display Name" && s.Kind == "class");
        Assert.Contains(references, r => r.SymbolName == "Display Name" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "`Display Name`" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinBacktickMethodReferenceOwners_CaptureTypeReference()
    {
        // JVM method references already emit owner type edges for Java/Kotlin; Kotlin backtick
        // owners need the same canonical name handling as declarations and type positions.
        // JVM method reference の owner 型 edge は Java/Kotlin で発行しているため、
        // Kotlin の backtick owner でも宣言・型位置と同じ canonical 名に揃える。
        const string content = """
            class `Display Name` {
                fun render() {}
            }

            class Demo {
                val handler = `Display Name`::render
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(symbols, s => s.Name == "Display Name" && s.Kind == "class");
        Assert.Contains(references, r => r.SymbolName == "Display Name" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "render" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "`Display Name`" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinBacktickMethodReferenceNames_NormalizesCallNames()
    {
        // Backticked Kotlin callable names are source syntax; method-reference call edges should
        // use the same canonical name as the callable declaration.
        // Kotlin callable 名の backtick は source syntax なので、method reference の call edge も
        // 宣言側と同じ canonical 名で発行する。
        const string content = """
            class User {
                fun `render name`() {}
            }

            class Demo {
                val handler = User::`render name`
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(symbols, s => s.Name == "render name" && s.Kind == "function");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "render name" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "`render name`" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinBacktickConstructorCalls_NormalizesInstantiateNames()
    {
        // Backticked Kotlin class names should behave like ordinary constructor calls: the
        // instantiate edge uses the canonical class symbol name.
        // backtick 付き Kotlin class 名の constructor call も通常の constructor call と同様に、
        // canonical class symbol 名で instantiate edge を発行する。
        const string content = """
            class `Display Name`

            class Demo {
                val value = `Display Name`()
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(symbols, s => s.Name == "Display Name" && s.Kind == "class");
        Assert.Contains(references, r => r.SymbolName == "Display Name" && r.ReferenceKind == "instantiate");
        Assert.DoesNotContain(references, r => r.SymbolName == "`Display Name`" && r.ReferenceKind == "instantiate");
    }

    [Fact]
    public void Extract_KotlinBacktickAnnotations_NormalizesNames()
    {
        // Kotlin annotations can be backticked declarations; metadata references should keep
        // the canonical annotation symbol name for both no-arg and argument forms.
        // Kotlin annotation も backtick 付き宣言にできるため、引数なし・引数ありの metadata
        // reference でも canonical annotation symbol 名を使う。
        const string content = """
            annotation class `Fancy Name`(val value: String = "")

            @`Fancy Name`
            class Demo {
                @`Fancy Name`("x")
                fun run(input: @`Fancy Name` Payload) {}
            }

            class Payload
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(symbols, s => s.Name == "Fancy Name" && s.Kind == "class");
        Assert.True(references.Count(r => r.SymbolName == "Fancy Name" && r.ReferenceKind == "annotation") >= 3);
        Assert.DoesNotContain(references, r => r.SymbolName == "`Fancy Name`" && r.ReferenceKind == "annotation");
        Assert.DoesNotContain(references, r => r.SymbolName == "Fancy Name" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinUseSiteTypeAnnotations_DoNotBecomeTypeReferences()
    {
        // Kotlin use-site targets are prefixes on annotations, not part of the annotated type.
        // The annotation should stay metadata and the following type should remain the dependency.
        const string content = """
            annotation class Fancy
            class Payload

            class Demo {
                val value: @field:Fancy Payload = Payload()
                fun run(input: @receiver:Fancy Payload): @param:Fancy Payload = input
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.True(references.Count(r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference") >= 3);
        Assert.True(references.Count(r => r.SymbolName == "Fancy" && r.ReferenceKind == "annotation") >= 3);
        Assert.DoesNotContain(references, r => r.SymbolName == "Fancy" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => (r.SymbolName is "field" or "receiver" or "param") && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinVarianceTypeArguments_DoNotBecomeTypeReferences()
    {
        const string content = """
            interface Producer<T>
            interface Consumer<T>
            class Payload

            class Demo {
                val produced: Producer<out Payload>? = null
                val consumed: Consumer<in Payload>? = null
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Producer" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Consumer" && r.ReferenceKind == "type_reference");
        Assert.True(references.Count(r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference") >= 2);
        Assert.DoesNotContain(references, r => (r.SymbolName is "in" or "out") && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinGenericBounds_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            interface Comparable<T>
            interface Handler<T>
            class Payload
            class Box<out T : Comparable<T>>

            fun <reified T> run(input: T): Handler<T> where T : Payload, T : Handler<T> = TODO()
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Comparable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Handler" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinGenericTypeOperators_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            class User

            inline fun <reified T> accepts(value: Any): Boolean = value is T
            fun parse(value: Any): User = value as User
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinGenericClassLiterals_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            class User

            inline fun <reified T> genericKClass() = T::class
            fun userKClass() = User::class
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_JavaGenericBounds_CaptureRealBoundsAndIgnoreParameterNames()
    {
        // Regression for issue #642: Java generic type-parameter bounds should emit the real
        // bound types, including nested generic bounds, while keeping the parameter names out
        // of the type_reference graph.
        // issue #642 回帰: Java の generic type-parameter bounds は、ネストした generic bound
        // を含めて実際の bound 型を拾いつつ、parameter 名は type_reference graph に出さないこと。
        const string content = """
            class Root {}
            interface Bound {}
            class Wrapper<T> {}

            class Demo<T extends Root & Bound> {
                <U extends Wrapper<Root>> U run(U value) {
                    return value;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Root" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Bound" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Wrapper" && r.ReferenceKind == "type_reference");
        Assert.True(references.Count(r => r.SymbolName == "Root" && r.ReferenceKind == "type_reference") >= 2);
    }

    [Fact]
    public void Extract_JavaCtorChainSuper_RewritesToBaseClass()
    {
        const string content = """
            package demo;

            public class Root {
                public Root(int x) {}
            }

            class Leaf extends Root {
                public Leaf(int x) {
                    super(x);
                }
                public Leaf() {
                    this(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var superRef = Assert.Single(references, r => r.SymbolName == "Root" && r.ReferenceKind == "call");
        Assert.Equal("call", superRef.ReferenceKind);
        Assert.Equal("Leaf", superRef.ContainerName);
        Assert.DoesNotContain(references, r => r.SymbolName == "super");

        var thisRef = Assert.Single(references, r =>
            r.SymbolName == "Leaf" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.Equal("call", thisRef.ReferenceKind);

        // The generic CallRegex loop runs after EmitJavaCtorChainReferences; `this` must be
        // in the Java ignore set so it does not leak as a phantom `call this` edge.
        // chain 書き換え後に generic CallRegex が `call this` を二重に出さないこと。
        Assert.DoesNotContain(references, r => r.SymbolName == "this");
    }

    [Fact]
    public void Extract_JavaCtorChainSuper_PackagePrivateCtor_RewritesToBaseClass()
    {
        // Package-private Java ctors like `Leaf(int x){...}` do not receive body ranges from
        // SymbolExtractor, so they are excluded from the innermost-container lookup. The
        // constructor-chain rewrite must still attribute the super(x) call to the ctor
        // through a name-based fallback against the enclosing class.
        // SymbolExtractor は package-private ctor に body 範囲を付けないため、innermost container
        // 判定からは外れる。chain 書き換えは外側クラス名との一致 fallback で救う。
        const string content = """
            package demo;

            public class Root {
                Root(int x) {}
            }

            class Leaf extends Root {
                Leaf(int x) {
                    super(x);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var superRef = Assert.Single(references, r =>
            r.SymbolName == "Root" && r.ContainerKind == "function");
        Assert.Equal("call", superRef.ReferenceKind);
        Assert.Equal("Leaf", superRef.ContainerName);
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaCtorChain_SameLineBody_RewritesToBaseClass()
    {
        // Same-line ctor bodies like `Leaf(int x){super(x);}` do not match
        // SymbolExtractor's enum-member regex (line ends with `}`, not `{`/`,`/`;`),
        // so no function symbol is emitted. The chain rewrite must synthesize a
        // ctor container from the line text and attribute super(x)/this(0) correctly.
        // 同一行に本体を書くコンストラクタは SymbolExtractor で関数シンボルが作られないため、
        // chain 書き換えは行テキストから ctor コンテナを合成して super/this を拾う必要がある。
        const string content = """
            package demo;

            public class Root {
                public Root(int x) {}
                Root() {}
            }

            class PublicLeaf extends Root {
                public PublicLeaf(int x){super(x);}
                public PublicLeaf(){this(0);}
            }

            class PackageLeaf extends Root {
                PackageLeaf(int x){super(x);}
                PackageLeaf(){this(0);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var publicSuper = Assert.Single(references, r =>
            r.SymbolName == "Root" && r.ContainerName == "PublicLeaf" && r.ContainerKind == "function");
        Assert.Equal("call", publicSuper.ReferenceKind);

        var publicThis = Assert.Single(references, r =>
            r.SymbolName == "PublicLeaf" && r.ContainerName == "PublicLeaf" && r.ContainerKind == "function");
        Assert.Equal("call", publicThis.ReferenceKind);

        var packageSuper = Assert.Single(references, r =>
            r.SymbolName == "Root" && r.ContainerName == "PackageLeaf" && r.ContainerKind == "function");
        Assert.Equal("call", packageSuper.ReferenceKind);

        var packageThis = Assert.Single(references, r =>
            r.SymbolName == "PackageLeaf" && r.ContainerName == "PackageLeaf" && r.ContainerKind == "function");
        Assert.Equal("call", packageThis.ReferenceKind);

        Assert.DoesNotContain(references, r => r.SymbolName == "super");
        Assert.DoesNotContain(references, r => r.SymbolName == "this");
    }

    [Fact]
    public void Extract_JavaCtorChain_SameLineBody_WithLeadingAnnotation_RewritesToBaseClass()
    {
        // Same-line ctor bodies can be preceded by annotations (with or without argument
        // lists), e.g. `@Deprecated Leaf(int x){super(x);}` or
        // `@SuppressWarnings("unused") Leaf(int x){super(x);}`.
        // The synthesis regex must accept the leading annotation so the chain rewrite still
        // finds a ctor container.
        // 同一行 ctor 本体の直前にアノテーションが付く形（引数あり/なし）も、合成コンテナ生成で
        // 取りこぼしてはならない。
        const string content = """
            package demo;

            public class Root {
                public Root(int x) {}
            }

            class Leaf extends Root {
                @Deprecated Leaf(int x){super(x);}
                @SuppressWarnings("unused") Leaf(long x){super((int) x);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var rootRefs = references.Where(r =>
            r.SymbolName == "Root" && r.ContainerName == "Leaf" && r.ContainerKind == "function").ToList();
        Assert.Equal(2, rootRefs.Count);
        Assert.All(rootRefs, r => Assert.Equal("call", r.ReferenceKind));

        Assert.DoesNotContain(references, r => r.SymbolName == "super");
        Assert.DoesNotContain(references, r => r.SymbolName == "this");
    }

    [Fact]
    public void Extract_JavaCtorChain_SameLineBody_WithGenericCtor_RewritesToBaseClass()
    {
        // Generic constructors (`public <T> Leaf(T x){super(0);}`) insert type parameters
        // between the modifiers and the ctor name. The synthesis regex must accept the
        // optional `<...>` token before `<name>`.
        // 型パラメータ付き ctor (`public <T> Leaf(T x){super(0);}`) は修飾子と ctor 名の間に
        // `<...>` が入る。合成コンテナ生成は名前直前の generic 型パラメータを許容すべし。
        const string content = """
            package demo;

            public class Root {
                public Root(int x) {}
            }

            class Leaf extends Root {
                public <T> Leaf(T x){super(0);}
                <T extends Number> Leaf(T x, int y){super(y);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var rootRefs = references.Where(r =>
            r.SymbolName == "Root" && r.ContainerName == "Leaf" && r.ContainerKind == "function").ToList();
        Assert.Equal(2, rootRefs.Count);
        Assert.All(rootRefs, r => Assert.Equal("call", r.ReferenceKind));

        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_OutsideConstructor_IsNotRewritten()
    {
        // super.method(...) is a regular method call, not a ctor chain.
        // Reference extractor must not confuse the two.
        // super.method(...) は通常のメソッド呼び出し。連鎖呼び出しと混同しない。
        const string content = """
            package demo;

            class Base {
                public void run() {}
            }

            class Child extends Base {
                public void run() {
                    super.run();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "Base" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_JavaSuperCall_NestedTypeUnderGenericOuter_AttributesToTerminalSegment()
    {
        // End-to-end Java regression for the `Outer<Integer>.Base` attribution bug.
        // `Outer<Integer>.Base` が `Base` へ張られることの E2E 回帰テスト。
        const string content = """
            package demo;

            public class Outer<T> {
                public static class Base {
                    public Base(int x) {}
                }
            }

            class Leaf extends Outer<Integer>.Base {
                Leaf(){super(0);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaCtorChain_SameLineBody_WithQualifiedAnnotation_RewritesToBaseClass()
    {
        // Annotations on same-line ctors can be fully qualified (`@demo.Ann`) or carry
        // nested-paren argument lists. The synthesis scanner must strip both before locating
        // the ctor name.
        // 同一行 ctor 本体のアノテーションは `@demo.Ann` のような FQCN や、入れ子の括弧付き
        // 引数を持つこともある。合成コンテナ生成は両方を剥がして ctor 名へ辿り着く必要がある。
        const string content = """
            package demo;

            public class Root {
                public Root(int x) {}
            }

            @interface Ann {}

            class Leaf extends Root {
                @demo.Ann Leaf(int x){super(x);}
                @SuppressWarnings({"unused", "unchecked"}) Leaf(long x){super((int) x);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var rootRefs = references.Where(r =>
            r.SymbolName == "Root" && r.ContainerName == "Leaf" && r.ContainerKind == "function").ToList();
        Assert.Equal(2, rootRefs.Count);
        Assert.All(rootRefs, r => Assert.Equal("call", r.ReferenceKind));
    }

    [Fact]
    public void Extract_JavaCtorChain_SameLineBody_WithNestedGenericBound_RewritesToBaseClass()
    {
        // Generic type parameters can carry nested `<...>` bounds such as
        // `<T extends Comparable<Integer>>`. A flat regex cannot balance the nested `>`; the
        // synthesis scanner must handle it.
        // `<T extends Comparable<Integer>>` のような入れ子 `<...>` を伴う generic 境界も
        // 合成コンテナ生成で取りこぼしてはならない。
        const string content = """
            package demo;

            public class Root {
                public Root(int x) {}
            }

            class Leaf extends Root {
                public <T extends Comparable<Integer>> Leaf(T x){super(0);}
                <U extends java.util.List<? extends Number>> Leaf(U xs, int y){super(y);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var rootRefs = references.Where(r =>
            r.SymbolName == "Root" && r.ContainerName == "Leaf" && r.ContainerKind == "function").ToList();
        Assert.Equal(2, rootRefs.Count);
        Assert.All(rootRefs, r => Assert.Equal("call", r.ReferenceKind));
    }

    [Fact]
    public void Extract_JavaSuperCall_SealedWithPermits_AttributesToRealBase()
    {
        // End-to-end regression for Java 17+ sealed class with `permits`. Before the fix the base
        // resolver returned `"Base permits A"` and the super(...) edge was dropped.
        // Java 17+ sealed class with `permits` の E2E 回帰。修正前は基底型解決が
        // `"Base permits A"` となり super(...) エッジが落ちていた。
        const string content = """
            package demo;

            public sealed class Base permits Leaf, Other {
                public Base(int x) {}
            }

            final class Leaf extends Base {
                Leaf(){super(0);}
            }

            final class Other extends Base {
                Other(){super(1);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Other");
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_MultiLineExtends_AttributesToBaseClass()
    {
        // Java multi-line `class Foo\n    extends Base` must still resolve `super(...)`.
        // SymbolRecord.Signature only captures the first declaration line, so the header has to
        // be reconstructed from structural lines before ParseJavaBaseType runs.
        // Java の複数行 `class Foo\n    extends Base` でも `super(...)` の解決先を見失わないこと。
        // SymbolRecord.Signature は 1 行目しか持たないため、ヘッダを structural lines から再構築する。
        const string content = """
            package demo;

            public class Base {
                public Base(int x) {}
            }

            class Leaf
                extends Base {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_MultiLineExtends_NestedGenericOuter_AttributesToTerminalSegment()
    {
        // Multi-line `extends` combined with `Outer<Integer>.Base` nested-generic form.
        // Reconstructed header feeds both multi-line handling and the depth-aware terminal-segment
        // extractor.
        // 複数行 `extends` と `Outer<Integer>.Base` ネスト generic 形の複合ケース。再構築した
        // ヘッダが複数行連結と depth-aware な末尾セグメント抽出の両方を通る。
        const string content = """
            package demo;

            public class Outer<T> {
                public static class Base {
                    public Base(int x) {}
                }
            }

            class Leaf
                extends Outer<Integer>.Base {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_MultiLineExtends_WithPermitsContinuation_AttributesToBaseClass()
    {
        // Java 17+ sealed class with `permits` sitting on a continuation line. The reconstructed
        // multi-line header must still stop at `permits` via the base-type scanner.
        // Java 17+ sealed class で `permits` が継続行にある場合でも、再構築したヘッダから
        // base-type scanner が `permits` を終端として正しく停止すること。
        const string content = """
            package demo;

            public sealed class Base permits Leaf {
                public Base(int x) {}
            }

            sealed class Leaf
                extends Base
                permits None {
                Leaf() {
                    super(0);
                }
            }

            final class None extends Leaf {
                None() { super(); }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_BoundedTypeParameter_AttributesToRealBase()
    {
        // End-to-end regression for bounded type parameter + class-level `extends`. Before the
        // fix the base resolver grabbed the first textual `extends` (inside `<T extends Number>`)
        // and super(0) was recorded against `Number` instead of the real base `Root`.
        // 境界付き型パラメータとクラスレベル `extends` が共存する場合の E2E 回帰。修正前は最初の
        // テキスト一致 `extends`（`<T extends Number>` 側）を拾い、super(0) が実基底 `Root` ではなく
        // 境界型 `Number` に張られていた。
        const string content = """
            package demo;

            class Root {
                Root(int value) {}
            }

            class Leaf<T extends Number> extends Root {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.Contains(references, r =>
            r.SymbolName == "Number" && r.ReferenceKind == "type_reference"
            && r.ContainerKind == "class" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_TypeUseAnnotationWithMultipleArgs_AttributesToRealBase()
    {
        // E2E regression for the multi-arg annotation case. Before the fix the scanner broke at
        // the `,` inside `@Ann(a = 1, b = 2)`, returned a truncated segment, and the super(...)
        // edge either pointed at a phantom annotated type or was dropped entirely.
        // 複数引数 annotation の E2E 回帰。修正前は `@Ann(a = 1, b = 2)` 内の `,` で走査が切れて
        // super(...) が幽霊型名に張られるか完全に落ちていた。
        const string content = """
            package demo;

            @interface Ann {
                int a();
                int b();
            }

            class Root {
                Root(int value) {}
            }

            class Leaf extends @Ann(a = 1, b = 2) Root {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        // The chain edge must attribute to `Root` (the real base), not to a phantom annotated
        // type name. The class-header line still picks up `Ann(...)` as a regular call from the
        // annotation site — that is the existing CallRegex behavior for any parenthesized name
        // and is unrelated to the base-list scanner fix; only the chain-line `super(0)` edge is
        // what we care about here.
        // 連鎖エッジは本当の基底 `Root` に張られ、annotation 由来の幽霊型名にはならないこと。
        // ヘッダ行の `@Ann(...)` は汎用 CallRegex が `Ann(...)` として通常の call を拾う既存挙動で、
        // 本修正のスコープ外。ここでは chain 行 `super(0)` のエッジだけを検証する。
        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf" && r.Line == 14);
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("@"));
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_TypeUseAnnotatedExtends_AttributesToRealBase()
    {
        // End-to-end regression for Java type-use annotated `extends`. Before the fix the base
        // resolver returned `"@Ann Root"` and `super(0)` edges pointed at a phantom symbol.
        // Java の type-use annotation 付き `extends` の E2E 回帰。修正前は基底型解決が
        // `"@Ann Root"` となり super(...) が幽霊シンボルに張られていた。
        const string content = """
            package demo;

            @interface Ann {}

            class Root {
                Root(int value) {}
            }

            class Leaf extends @Ann Root {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "@Ann Root");
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("@"));
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_TypeUseAnnotatedNestedGeneric_AttributesToTerminalSegment()
    {
        // Type-use annotation sitting between nested segments (`Outer<Integer>.@Ann Base`). The
        // resolver must strip the annotation and still return the terminal segment `Base`.
        // ネスト型の区切り位置 (`Outer<Integer>.@Ann Base`) に type-use annotation が入る形。
        // annotation を剥がしたうえで末尾セグメント `Base` を返せることを確認する。
        const string content = """
            package demo;

            @interface Ann {}

            class Outer<T> {
                static class Base {
                    Base(int value) {}
                }
            }

            class Leaf extends Outer<Integer>.@Ann Base {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("@"));
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaThisCall_EnumConstructorChain_AttributesToEnum()
    {
        // Java enums can declare constructors and chain via `this(...)`. Before the fix the
        // enclosing-type lookup filtered to class/struct/interface, so enum bodies were skipped
        // and the chain edge was silently dropped (compounded by `this` being in the ignore set).
        // Java の enum はコンストラクタを宣言でき `this(...)` 連鎖も使える。修正前は外側型の
        // 走査が class/struct/interface に限定され、enum 本体は無視されて連鎖エッジが
        // 黙って落ちていた（`this` が ignore set に含まれることも原因として重なっていた）。
        const string content = """
            package demo;

            enum Shade {
                Red,
                Blue(1);

                Shade() {
                    this(0);
                }

                Shade(int code) {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Shade" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Shade" && r.Line == 8);
        Assert.DoesNotContain(references, r => r.SymbolName == "this");
    }

    [Fact]
    public void Extract_JavaSuperCall_AllmanCtorBody_AttributesToRealBase()
    {
        // Regression for Allman-style ctor bodies where the opening `{` sits on a separate
        // line from the declarator. Java ctors have no return type, so SymbolExtractor's
        // return-type-required method regex does not emit a function symbol for them. The
        // ReferenceExtractor structural fallback must therefore parse the multi-line header
        // and hand the body range off to FindJavaBraceRange so the `super(...)` edge is still
        // attributed to the owning ctor instead of being silently dropped.
        // Allman 形式の ctor（`Leaf()\n{` のように `{` を独立行に置く形）では SymbolExtractor が
        // function シンボルを生成しない。ReferenceExtractor の structural fallback がヘッダを
        // 認識し、body 範囲を FindJavaBraceRange に渡すことで super(...) の連鎖エッジが落ちない
        // ことを固定する。
        const string content = """
            package demo;

            class Root {
                Root(int value) {}
            }

            class Leaf extends Root {
                Leaf()
                {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf" && r.Line == 10);
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_MultiLineParameterList_AttributesToRealBase()
    {
        // Regression for ctor declarations whose parameter list spans multiple lines. Before
        // the fallback rewrite the structural scanner required `)` and `{` to share the
        // declaration line, so the super(...) edge was silently dropped. FindJavaBraceRange
        // already tracks paren depth across lines, so once the header is recognized the body
        // scan handles the rest.
        // parameter list が複数行にまたがる ctor では、以前の structural scanner が `)` と `{` を
        // 同じ物理行で要求していたため super(...) 連鎖が落ちていた。FindJavaBraceRange の多行
        // paren depth 追跡を活用すれば復元できる。
        const string content = """
            package demo;

            class Root {
                Root(int a, int b) {}
            }

            class Leaf extends Root {
                Leaf(
                    int a,
                    int b
                ) {
                    super(a, b);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf" && r.Line == 12);
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_MultiLineThrowsClause_AttributesToRealBase()
    {
        // Regression for ctor declarations whose `throws` clause spans multiple lines. The
        // body `{` lives on a later physical line than the declarator name, so the old
        // same-line-only detector missed it. The structural fallback must recognize the ctor
        // header by name + `(` alone and let FindJavaBraceRange locate the `{` across lines.
        // `throws` 節が複数行にわたる ctor では body `{` が宣言行とは別の物理行に来るため、
        // 旧 same-line detector は検出に失敗していた。Structural fallback は name + `(` で
        // 認識し、`{` の位置は FindJavaBraceRange に任せることで復元する。
        const string content = """
            package demo;

            class IoFailure extends Exception {}

            class Root {
                Root(int value) throws IoFailure {}
            }

            class Leaf extends Root {
                Leaf(int x)
                    throws IoFailure
                {
                    super(x);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf" && r.Line == 13);
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperCall_SameLineCtorWithModifierThenAnnotation_AttributesToRealBase()
    {
        // Regression for same-line ctor bodies where an access modifier precedes an annotation,
        // e.g. `public @Deprecated Leaf(...)`. Before the fix the scanner consumed the modifier
        // first, hit `@` in ConsumeIdentifier, and returned null, dropping the super(...) edge.
        // 修正前は modifier の後に annotation が来ると ctor 名抽出が失敗し、super(...) が落ちた。
        const string content = """
            package demo;

            class Root {
                Root(int value) {}
            }

            class Leaf extends Root {
                public @Deprecated Leaf(int x){super(x);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf" && r.Line == 8);
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_JavaSuperChain_BraceAnnotationArgExtends_AttributesSuperEdgeEndToEnd()
    {
        // End-to-end regression for Java type-use annotated `extends` whose annotation argument
        // itself contains `{...}`, e.g. `extends @Ann({A.class, B.class}) Root`. Before the fix
        // the SymbolExtractor brace range for `class Leaf` stopped at the annotation-arg `}`
        // because it did not skip `{` / `}` inside `()`, so `FindInnermostClassLike` could not
        // recover the enclosing type for the `super(0)` line, and the chain edge was dropped.
        // ParseJavaBaseType alone already resolves the correct base type (locked in
        // CollectCSharpRecordHeader_MultiLineExtendsWithBraceAnnotationArg_CollectsThroughRealBase),
        // so this test locks the extractor-level body range fix so that the full
        // `super(...)` → enclosing class → base type chain makes it into the reference stream.
        // annotation 引数内に `{...}` を含む Java `extends` の E2E 回帰。修正前は SymbolExtractor の
        // brace range が annotation 引数の `}` で閉じてしまい、`super(0)` の外側型が見つからず
        // 連鎖エッジが完全に落ちていた。本テストは body-range fix を E2E で固定する。
        const string content = """
            package demo;

            @interface Ann {
                Class<?>[] value() default {};
            }

            class A {}
            class B {}

            class Root {
                Root(int value) {}
            }

            class Leaf extends @Ann({A.class, B.class}) Root {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        // SymbolExtractor regression: the `class Leaf` symbol must span through the real class
        // body, not collapse onto the declaration line because of the annotation-arg braces.
        // Without this, the downstream `FindInnermostClassLike` lookup at the super(0) line
        // returns null and the chain edge is silently dropped.
        // `class Leaf` の body 範囲が annotation 引数内の `{}` によって 1 行に潰れないことを固定。
        var leafClass = Assert.Single(symbols, s =>
            s.Kind == "class" && s.Name == "Leaf");
        Assert.NotNull(leafClass.BodyStartLine);
        Assert.NotNull(leafClass.BodyEndLine);
        Assert.True(leafClass.BodyEndLine!.Value >= 16,
            $"Leaf class body_end_line must cover the super(0) line, got {leafClass.BodyEndLine}.");

        // The super(0) edge must attribute to `Root` (the real base), with the function-kind
        // container pointing at the Leaf constructor — not a phantom annotated name, and not
        // dropped entirely. This is the edge codex round 8 flagged as silently lost.
        // `super(0)` の連鎖エッジが `Root` に対して Leaf コンストラクタから張られることを E2E で固定。
        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf" && r.Line == 16);
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("@"));
        Assert.DoesNotContain(references, r => r.SymbolName == "super");
    }

    [Fact]
    public void Extract_Java_BraceRangeIgnoresCommentsAndStringsInClassHeader()
    {
        // Regression: `FindBraceRange` previously counted raw parens/brackets/braces, so a
        // declaration header with unbalanced punctuation inside a line comment, block comment,
        // or string literal (for example `class Leaf extends Root /* ( */ { ... }`) left
        // parenDepth / bracketDepth non-zero and silently collapsed the Leaf body range to a
        // single line. The super(...) call site then fell outside the class body, so the
        // constructor-chain edge was dropped.
        // 宣言ヘッダ付近のコメント・文字列内の不均衡な `(` / `[` で body 範囲が潰れないことを固定。
        const string content = """
            package demo;

            class Root {
                Root(int value) {}
            }

            // The stray `(` and `[` below live inside comments / strings and must not leak
            // into the brace-depth counters that FindBraceRange uses.
            class Leaf extends Root /* ( stray [ */ {
                // marker "(" and '[' stay in comment/string space
                String tag = "open ( bracket [";
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var leaf = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "Leaf");
        Assert.NotNull(leaf.BodyStartLine);
        Assert.NotNull(leaf.BodyEndLine);
        Assert.True(leaf.BodyEndLine!.Value - leaf.BodyStartLine!.Value >= 3,
            $"Leaf class body must span several lines, got {leaf.BodyStartLine}..{leaf.BodyEndLine}");

        // Super chain must now attribute to Root, proving the brace range extended far enough
        // to contain the Leaf ctor body through the annotation-/comment-polluted header.
        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
    }

    [Fact]
    public void Extract_Java_AnnotationStringArgWithCloseParen_ResolvesBaseToRoot()
    {
        // Regression: StripJavaTypeAnnotations and SkipBalancedParens previously counted raw
        // parens, so a closing `)` inside a string literal such as `@Ann(text=")")` closed the
        // annotation early. ExtractBareTypeName then received a mangled prefix like `") Root`
        // and the super(...) edge was silently dropped (or misattributed) for otherwise legal
        // annotations with quoted arguments.
        // annotation 引数内の `)` / `(` を含む文字列リテラルを正しくスキップし、基底型解決が
        // 壊れないことを固定する。
        const string content = """
            package demo;

            class Root {
                Root(int value) {}
            }

            @interface Ann {
                String text();
            }

            class Leaf extends @Ann(text=")") Root {
                Leaf() {
                    super(0);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
    }

    [Fact]
    public void Extract_Java_SameLineCtorWithQuotedAnnotationArg_KeepsSuperEdge()
    {
        // Regression: TryExtractJavaCtorNameFromLine walks past annotations using
        // SkipBalancedParens, which previously counted raw `)` characters. A legal
        // `@Ann(text=")")` prefix on a same-line ctor truncated annotation scanning at the
        // string's closing `)` and the ctor name read then started mid-string, so
        // TrySynthesizeSameLineJavaCtor returned null and the synthesized `super(...)` edge
        // added for same-line ctors vanished.
        // 同一行 ctor の annotation 文字列引数内 `)` が ctor 名抽出を壊さないことを固定する。
        const string content = """
            package demo;

            class Root {
                Root(int value) {}
            }

            @interface Ann {
                String text();
            }

            class Leaf extends Root {
                public @Ann(text=")") Leaf(){super(0);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
    }

    [Fact]
    public void Extract_Java_SameLineCtorBodyCall_AttributesToCtorContainer()
    {
        // Regression: non-chain body calls on a same-line Java ctor (for example the
        // `Helper.doWork()` statement after `super(0);` in `Leaf(T x){super(0); Helper.doWork();}`)
        // previously landed on `class:Leaf` because SymbolExtractor does not emit a function
        // symbol for the same-line ctor shape. The main loop now pre-computes a per-line synthetic
        // function-kind container covering the body `{ ... }` span so body calls attribute to
        // `function:Leaf` instead of leaking to the enclosing class.
        // 同一行 ctor 本体の通常 call が外側 class に吸われず、合成 function コンテナに帰属することを固定する。
        const string content = """
            package demo;

            class Helper {
                static void doWork() {}
            }

            class Root {
                Root(int v) {}
            }

            class Leaf extends Root {
                public <T> Leaf(T x){super(0); Helper.doWork();}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "doWork" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "doWork" && r.ContainerKind == "class");

        // Chain edge remains on the synthetic function ctor too.
        // 連鎖エッジも合成 function コンテナに帰属したままであることを確認する。
        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
    }

    [Fact]
    public void Extract_Java_SameLineCtorDeclarator_DoesNotEmitSelfCall()
    {
        // Regression: `CallRegex` matches `CtorName(` on the declarator of a same-line Java ctor
        // and, without suppression, emitted a phantom `Leaf|call|class|Leaf` edge attributing the
        // declarator to the enclosing class. The main loop now skips the `CtorName(` match at the
        // declarator's name column when the current line carries a synthesized same-line ctor.
        // 同一行 ctor の宣言子 `CtorName(` が自己 call として記録されないことを固定する。
        const string content = """
            package demo;

            class Root {
                Root(int v) {}
            }

            class Leaf extends Root {
                Leaf(){super(0);}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.DoesNotContain(references, r =>
            r.SymbolName == "Leaf" && r.ReferenceKind == "call");
        // But the chain rewrite still emits the `Root` edge attributed to `function:Leaf`.
        // 連鎖書き換えによる `Root` エッジは残っていることを確認する。
        Assert.Contains(references, r =>
            r.SymbolName == "Root" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Leaf");
    }

    [Fact]
    public void Extract_Java_ModuleInfoDirectives_EmitModuleDependencyReferences()
    {
        // JPMS directives are dependency edges, not calls. `requires`, `uses`, and
        // `provides ... with ...` should all surface as `type_reference` rows so graph
        // queries can answer module-level dependency questions.
        // JPMS の directive は call ではなく dependency edge であり、`requires` / `uses` /
        // `provides ... with ...` が `type_reference` として見える必要がある。
        const string content = """
            module com.example.app {
                requires java.base;
                requires transitive java.logging;
                uses com.example.spi.MyService;
                provides com.example.spi.MyService with com.example.impl.DefaultService;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Equal(5, references.Count);
        Assert.All(references, reference => Assert.Equal("type_reference", reference.ReferenceKind));
        Assert.All(references, reference => Assert.Equal("com.example.app", reference.ContainerName));
        Assert.Contains(references, reference =>
            reference.SymbolName == "java.base"
            && reference.ContainerKind == "namespace");
        Assert.Contains(references, reference =>
            reference.SymbolName == "java.logging"
            && reference.ContainerKind == "namespace");
        Assert.Equal(2, references.Count(reference => reference.SymbolName == "com.example.spi.MyService"));
        Assert.Single(references, reference =>
            reference.SymbolName == "com.example.impl.DefaultService");
    }

    [Fact]
    public void Extract_Java_SealedPermitsList_EmitsTypeReferences()
    {
        const string content = """
            sealed interface Shape permits Circle, Square {}
            final class Circle implements Shape {}
            non-sealed class Square implements Shape {}
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Circle"
            && reference.ReferenceKind == "type_reference"
            && reference.Context.StartsWith("sealed interface Shape permits", StringComparison.Ordinal));
        Assert.Contains(references, reference =>
            reference.SymbolName == "Square"
            && reference.ReferenceKind == "type_reference"
            && reference.Context.StartsWith("sealed interface Shape permits", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_JavaAnnotation_ClassifiedAsAnnotation()
    {
        // issue #293: `@Deprecated(since="1.0")` must produce an `annotation` reference, not a phantom `call`.
        // issue #293: `@Deprecated(since="1.0")` は `call` ではなく `annotation` として記録されること。
        const string content = """
            public class AnnotatedClass {
                @Deprecated(since="1.0")
                public void doWork() { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var deprecated = Assert.Single(references.Where(r => r.SymbolName == "Deprecated"));
        Assert.Equal("annotation", deprecated.ReferenceKind);
    }

    [Fact]
    public void Extract_JavaQualifiedAnnotation_ClassifiedAsAnnotation()
    {
        // issue #293: `@org.junit.Test(timeout=1000)` — dotted qualifier chain still resolves to `@`.
        // issue #293: `@org.junit.Test(timeout=1000)` のような修飾付き注釈も `annotation` になること。
        const string content = """
            public class T {
                @org.junit.Test(timeout=1000)
                public void testIt() { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var testAnno = Assert.Single(references.Where(r => r.SymbolName == "Test"));
        Assert.Equal("annotation", testAnno.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinAnnotation_ClassifiedAsAnnotation()
    {
        // issue #293: Kotlin `@Deprecated("msg")` also emits `annotation`.
        // issue #293: Kotlin の `@Deprecated("msg")` も `annotation` になること。
        const string content = """
            class K {
                @Deprecated("msg")
                fun old() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var deprecated = Assert.Single(references.Where(r => r.SymbolName == "Deprecated"));
        Assert.Equal("annotation", deprecated.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinTrailingLambdaCallSites_AreReferenced()
    {
        // issue #265: Kotlin trailing-lambda call sites do not end with `(`, so the
        // reference extractor must still index them as `call` edges.
        // issue #265: Kotlin の trailing-lambda 呼び出しは末尾に `(` を持たないため、
        // それでも `call` edge として index されること。
        const string content = """
            interface Box

            fun run() {
                val items = listOf(1, 2, 3)
                items.forEach { }
                items.filter { it > 0 }
                items.map { it * 2 }
                items.fold(0) { acc, x -> acc + x }
                val boxed = object : Box {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "forEach"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "filter"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "map"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "fold"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Box"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaBlockCallSites_AreReferenced()
    {
        // issue #277: Scala block-call sites use `name { ... }` rather than a trailing `(`,
        // so the reference extractor must still index them as `call` edges.
        // issue #277: Scala の block-call は末尾 `(` ではなく `name { ... }` 形なので、
        // それでも `call` edge として index されること。
        const string content = """
            import scala.util.Try

            class Foo {}

            object Main {
                def process(items: List[Int]): Int = {
                    items.foreach { item =>
                        println(s"item=$item")
                    }
                    items.map { x => x * 2 }.sum
                    Try {
                        "result"
                    }
                    items.headOption match {
                        case Some(value) => println(value)
                        case None => ()
                    }
                    synchronized {
                        println("locked")
                    }
                    if (items.nonEmpty) {
                        println("non-empty")
                    }
                    0
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "foreach"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "process");
        Assert.Contains(references, reference =>
            reference.SymbolName == "map"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "process");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Try"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "process");
        Assert.Contains(references, reference =>
            reference.SymbolName == "synchronized"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "process");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "match"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Foo"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "if"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_GradleAnnotation_ClassifiedAsAnnotation()
    {
        // issue #293 follow-up: Gradle/Groovy `@CompileStatic` / `@TaskAction` and similar
        // transform/task annotations are compile-time metadata. Before the fix they were
        // recorded as `call` references (or dropped for the no-arg form), which made
        // `callers TaskAction` / `callees` pick up fake graph edges in build scripts.
        // After the fix they must all classify as `annotation`.
        // issue #293 補足: Gradle/Groovy の `@CompileStatic` / `@TaskAction` なども compile-time
        // metadata。修正前は `call` として記録されるか no-arg 版が脱落し、ビルドスクリプトで
        // `callers TaskAction` / `callees` に偽のグラフエッジが混入していた。修正後はすべて
        // `annotation` として分類される。
        const string content = """
            import groovy.transform.CompileStatic

            @CompileStatic
            class BuildConfig {
                @TaskAction
                void run() {
                    println "built"
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "gradle", content);
        var references = ReferenceExtractor.Extract(1, "gradle", content, symbols);

        var compileStatic = Assert.Single(references.Where(r => r.SymbolName == "CompileStatic"));
        Assert.Equal("annotation", compileStatic.ReferenceKind);

        var taskAction = Assert.Single(references.Where(r => r.SymbolName == "TaskAction"));
        Assert.Equal("annotation", taskAction.ReferenceKind);
    }

    [Fact]
    public void Extract_GradleDslBlockAndCommandForms_AreCapturedAsCalls()
    {
        // issue #280: Gradle/Groovy block DSL (`plugins {}` / `dependencies {}` / `repositories {}`)
        // and command-style calls (`implementation '...'` / `println 'x'` / `apply plugin: 'java'`)
        // must surface as reference rows instead of disappearing behind the shared `foo(...)` regex.
        // issue #280: Gradle/Groovy の block DSL (`plugins {}` / `dependencies {}` / `repositories {}`)
        // と command-style 呼び出し (`implementation '...'` / `println 'x'` / `apply plugin: 'java'`)
        // は、共通の `foo(...)` regex に落ちず reference row として残る必要がある。
        const string content = """
            plugins {
                id 'java'
                id 'application'
            }

            dependencies {
                implementation 'com.google.guava:guava:32.0.0-jre'
                testImplementation 'junit:junit:4.13'
                api project(':shared')
            }

            repositories {
                mavenCentral()
            }

            application {
                mainClass = 'com.example.Main'
            }

            task buildJar(type: Jar) {
                from sourceSets.main.output
                manifest {
                    attributes 'Main-Class': 'com.example.Main'
                }
            }

            subprojects {
                apply plugin: 'java'
            }

            doLast {
                println 'hello'
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "gradle", content);
        var references = ReferenceExtractor.Extract(1, "gradle", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "plugins"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "dependencies"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "repositories"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "application"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "task"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "implementation"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "testImplementation"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "api"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "apply"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "from"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "attributes"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "println"
            && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "doLast"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "mainClass");
    }

    [Fact]
    public void Extract_JavaMethodBodyCall_StaysCall()
    {
        // Regression guard: ordinary Java method call remains `call`, not `annotation`.
        // 回帰防止: Java のメソッド本体内の通常呼び出しは `annotation` に誤判定されず `call` のまま。
        const string content = """
            public class J {
                public int add(int a, int b) { return compute(a, b); }
                public int compute(int a, int b) { return a + b; }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        var compute = Assert.Single(references.Where(r => r.SymbolName == "compute"));
        Assert.Equal("call", compute.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinFieldTargetAnnotation_ClassifiedAsAnnotation()
    {
        // issue #293 follow-up: Kotlin use-site target `@field:Deprecated("msg")` must be
        // classified as `annotation`, not `call`.
        // issue #293 補足: Kotlin の use-site target `@field:Deprecated("msg")` も `annotation`。
        const string content = """
            class Example {
                @field:Deprecated("msg")
                val value: Int = 0
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var deprecated = Assert.Single(references.Where(r => r.SymbolName == "Deprecated"));
        Assert.Equal("annotation", deprecated.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinGetTargetAnnotation_ClassifiedAsAnnotation()
    {
        // Kotlin `@get:JsonName("x")` property getter target annotation.
        // Kotlin の `@get:JsonName("x")` プロパティ getter 向け注釈も `annotation`。
        const string content = """
            class K {
                @get:JsonName("x")
                val x: Int = 0
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var jsonName = Assert.Single(references.Where(r => r.SymbolName == "JsonName"));
        Assert.Equal("annotation", jsonName.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinFileTargetAnnotation_ClassifiedAsAnnotation()
    {
        // Kotlin `@file:JvmName("Foo")` file-level target annotation.
        // Kotlin の `@file:JvmName("Foo")` ファイル単位注釈も `annotation`。
        const string content = """
            @file:JvmName("Foo")

            package example
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var jvmName = Assert.Single(references.Where(r => r.SymbolName == "JvmName"));
        Assert.Equal("annotation", jvmName.ReferenceKind);
    }

    [Fact]
    public void Extract_JavaNoArgAnnotation_ClassifiedAsAnnotation()
    {
        // Regression (issue #293): `@Deprecated`, `@Override`, `@org.junit.Test` — bare no-arg
        // annotations were not indexed because CallRegex requires `(`. A dedicated no-arg
        // regex must emit them with kind `annotation`.
        // リグレッション (issue #293): `@Deprecated` などの引数なし Java annotation も
        // `annotation` として認識されること。
        const string content = """
            public class C {
                @Deprecated
                @Override
                @org.junit.Test
                public void m() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);
        var references = ReferenceExtractor.Extract(1, "java", content, symbols);

        Assert.Single(references.Where(r => r.SymbolName == "Deprecated" && r.ReferenceKind == "annotation"));
        Assert.Single(references.Where(r => r.SymbolName == "Override" && r.ReferenceKind == "annotation"));
        Assert.Single(references.Where(r => r.SymbolName == "Test" && r.ReferenceKind == "annotation"));
    }

    [Fact]
    public void Extract_KotlinNoArgTargetAnnotation_ClassifiedAsAnnotation()
    {
        // Regression (issue #293): `@field:Deprecated` — use-site target without parentheses.
        // リグレッション (issue #293): `@field:Deprecated` のような use-site target 付き
        // 引数なしアノテーションも `annotation` 判定になること。
        const string content = """
            class C {
                @field:Deprecated
                val x: Int = 0
            }
            """;

        var references = ReferenceExtractor.Extract(1, "kotlin", content, []);

        var deprecated = Assert.Single(references.Where(r => r.SymbolName == "Deprecated"));
        Assert.Equal("annotation", deprecated.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinReturnAtLabel_NotClassifiedAsAnnotation()
    {
        // Regression (issue #293): `return@foo` is a Kotlin label reference, not an annotation.
        // The leading lookbehind `(?<![\w)])` in the no-arg annotation regex must prevent a
        // match where `@` is preceded by an identifier character.
        // リグレッション (issue #293): `return@foo` は Kotlin のラベル参照で annotation ではない。
        // 先頭 lookbehind で識別子に続く `@` を除外すること。
        const string content = """
            fun outer() {
                listOf(1).forEach foo@{
                    return@foo
                }
            }
            """;

        var references = ReferenceExtractor.Extract(1, "kotlin", content, []);

        Assert.DoesNotContain(references, r => r.SymbolName == "foo" && r.ReferenceKind == "annotation");
    }

    [Fact]
    public void Extract_JavaScriptDecorator_ClassifiedAsAnnotation()
    {
        // Regression (issue #293 follow-up): JavaScript is a graph-supported language, so
        // its `@Decorator` / `@Decorator()` forms must be reclassified to `annotation` instead
        // of leaking into call-graph edges. Both bare `@sealed` (no-arg, via the dedicated
        // regex) and `@injectable()` (via CallRegex + TryClassifyMetadataReference) must end
        // up as `annotation`.
        // リグレッション (issue #293 補足): JavaScript も graph 対応言語なので、`@Decorator`
        // / `@Decorator()` は `annotation` に再分類され、call graph を汚染しないこと。
        const string content = """
            @sealed
            @injectable()
            class Foo {}
            """;

        var references = ReferenceExtractor.Extract(1, "javascript", content, []);

        var sealedRef = Assert.Single(references.Where(r => r.SymbolName == "sealed"));
        Assert.Equal("annotation", sealedRef.ReferenceKind);
        var injectable = Assert.Single(references.Where(r => r.SymbolName == "injectable"));
        Assert.Equal("annotation", injectable.ReferenceKind);
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinQualifiedFieldTargetAnnotation_ClassifiedAsAnnotation()
    {
        // issue #293 follow-up: Kotlin use-site target with a fully-qualified annotation
        // name, e.g. `@field:com.example.Deprecated("msg")`, must be classified as
        // `annotation` — the dotted qualifier chain plus the `target:` prefix must both
        // resolve back to `@`.
        // issue #293 補足: Kotlin の `@field:com.example.Deprecated("msg")` のように use-site
        // target と修飾付き注釈名が組み合わさった場合も `annotation` 判定になること。
        const string content = """
            class Example {
                @field:com.example.Deprecated("msg")
                val value: Int = 0
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var deprecated = Assert.Single(references.Where(r => r.SymbolName == "Deprecated"));
        Assert.Equal("annotation", deprecated.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinQualifiedGetTargetAnnotation_ClassifiedAsAnnotation()
    {
        // Kotlin `@get:com.fasterxml.jackson.annotation.JsonProperty("x")` — use-site target
        // combined with a long qualifier chain must still be `annotation`.
        // Kotlin の `@get:com.fasterxml.jackson.annotation.JsonProperty("x")` も `annotation`。
        const string content = """
            class K {
                @get:com.fasterxml.jackson.annotation.JsonProperty("x")
                val x: Int = 0
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var jsonProperty = Assert.Single(references.Where(r => r.SymbolName == "JsonProperty"));
        Assert.Equal("annotation", jsonProperty.ReferenceKind);
    }

    [Fact]
    public void Extract_KotlinQualifiedAnnotationWithoutTarget_ClassifiedAsAnnotation()
    {
        // Regression guard: fully-qualified annotation name without a use-site target, e.g.
        // `@org.junit.Test(...)`, must still be `annotation`.
        // 退行防止: use-site target のない `@org.junit.Test(...)` も引き続き `annotation`。
        const string content = """
            class K {
                @org.junit.Test(expected = Exception::class)
                fun run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        var test = Assert.Single(references.Where(r => r.SymbolName == "Test"));
        Assert.Equal("annotation", test.ReferenceKind);
    }

    [Fact]
    public void Extract_JavaScriptTaggedTemplateLiteral_IsCapturedAsCall()
    {
        // issue #268: bare tagged template literals (`gql`, `sql`, etc.) must emit a `call`
        // reference so they surface in references / callers / callees / impact.
        // issue #268: 素のタグ付きテンプレートリテラル (`gql` / `sql` 等) も `call` として記録する。
        const string content = """
            function loadUser(id) {
                return gql`query { user(id: ${id}) { name } }`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        var gql = Assert.Single(references.Where(r => r.SymbolName == "gql"));
        Assert.Equal("call", gql.ReferenceKind);
        Assert.Equal("loadUser", gql.ContainerName);
    }

    [Fact]
    public void Extract_JavaScriptTaggedTemplateInStringLiteral_IsNotMisdetected()
    {
        // Regression guard: a backtick appearing inside a single- or double-quoted string must
        // not be treated as a tagged template opener. The structural masker enters string-skip
        // mode so the backtick is consumed as string content.
        // 退行防止: シングル/ダブルクオート文字列内のバッククォートをタグ付きテンプレートと誤認しない。
        const string content = """
            function note() {
                const s = "see gql`docs` for details";
                return s;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "gql");
        Assert.DoesNotContain(references, r => r.SymbolName == "docs");
    }

    [Fact]
    public void Extract_JavaScriptPlainTemplateLiteral_IsNotCaptured()
    {
        // Regression guard: an untagged template literal (preceded by `=`, operator, or
        // statement-head keyword) must not synthesize a phantom `call` reference.
        // 退行防止: タグのないテンプレート（`=` や演算子、ステートメント先頭キーワードの直後）を
        // 誤って `call` として記録しない。
        const string content = """
            function greet(name) {
                const msg = `Hello, ${name}!`;
                return `Bye, ${name}.`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "return");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "msg");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "Hello");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "Bye");
    }

    [Fact]
    public void Extract_JavaScriptDeleteTaggedTemplate_IsNotCaptured()
    {
        // Regression guard: `delete \`...\`` is a legal (if pointless) expression form
        // where `delete` is an operator — not a call — even though the backward-scan
        // behind the backtick sees the identifier tail `delete`. IsIgnoredCallName must
        // suppress the phantom `call delete` edge.
        // 退行防止: `delete \`...\`` は `delete` が演算子の正当な式形であり、
        // タグ付きテンプレート検出が backward-scan で拾う `delete` を
        // IsIgnoredCallName で握り潰す必要がある。
        const string content = """
            function clean(obj) {
                delete `placeholder-${obj.id}`;
                void `side-effect-${obj.id}`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "delete");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "void");
    }

    [Fact]
    public void Extract_JavaScriptComparisonBeforePlainTemplate_IsNotCaptured()
    {
        // Regression guard: `foo < bar > \`plain\`` is a chained comparison expression, not a
        // generic-tagged template. The backward scan behind the backtick must not strip the
        // `<bar>` range as generics and emit a phantom `call foo`.
        // 退行防止: `foo < bar > \`plain\`` は連鎖比較式であり、ジェネリクス付きタグ付き
        // テンプレートではない。backtick 直前の `<...>` を generic と誤認して
        // `call foo` を幻発行してはならない。
        const string content = """
            function check(foo, bar) {
                return foo < bar > `plain`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "foo");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "bar");
    }

    [Fact]
    public void Extract_JavaScriptComparisonBeforePlainTemplateWithoutSpaces_IsNotCaptured()
    {
        // Regression guard: plain JavaScript has no generics, so `foo<bar>\`plain\`` (no
        // spaces) is a chained comparison `(foo<bar)>\`plain\``, not a generic-tagged
        // template. The backtick backward-scan must not strip the `<bar>` range as TS
        // generics and emit a phantom `call foo`.
        // 退行防止: JavaScript にはジェネリクスがなく、`foo<bar>\`plain\`` は連鎖比較式
        // `(foo<bar)>\`plain\`` である。backtick 直前の `<...>` を TypeScript generic と
        // 誤認して `call foo` を幻発行してはならない。
        const string content = """
            function check(foo, bar) {
                return foo<bar>`plain`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "foo");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "bar");
    }

    [Fact]
    public void Extract_JavaScriptInOperatorBeforePlainTemplate_IsNotCaptured()
    {
        // Regression guard: `foo in \`plain\`` uses `in` as the membership operator, not as
        // a tag identifier. The backtick backward-scan picks up the `in` token, so
        // IsIgnoredCallName must suppress the phantom `call in` edge.
        // 退行防止: `foo in \`plain\`` の `in` はメンバーシップ演算子であり、タグ識別子ではない。
        // backward-scan が拾う `in` は IsIgnoredCallName で握り潰す。
        const string content = """
            function check(foo) {
                return foo in `plain`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "in");
    }

    [Fact]
    public void Extract_JavaScriptForOfLoopOverPlainTemplate_IsNotCaptured()
    {
        // Regression guard: `for (const ch of \`abc\`)` uses `of` as the for-of iterator
        // keyword, not as a tag identifier. The tag scanner must detect the enclosing
        // `for (` header and drop the `of` token instead of emitting a phantom `call of`.
        // 退行防止: `for (const ch of \`abc\`)` の `of` は for-of イテレータキーワードで
        // あり、タグ識別子ではない。タグ検出は外側の `for (` ヘッダを認識して `of` を落とす。
        const string content = """
            function f() {
                for (const ch of `abc`) {
                    use(ch);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptForAwaitOfLoopOverPlainTemplate_IsNotCaptured()
    {
        // Regression guard: `for await (const x of \`...\`)` is the async iterator form. The
        // `for` / `(` scanner must tolerate the `await` contextual keyword between them so
        // the phantom `call of` is still suppressed.
        // 退行防止: `for await (const x of \`...\`)` は非同期イテレータ形。`for` と `(` の間
        // の `await` を読み飛ばして `of` の幻 `call` を抑制する。
        const string content = """
            async function f(iter) {
                for await (const x of `abc`) {
                    use(x);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptOfAsTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268: `of` is an unreserved identifier in ECMAScript. `const of = ...;
        // of\`hello\`` is a legal tagged-template call and must not be silenced by the
        // for-of loop-header suppression — only the header form should be dropped.
        // issue #268: `of` は ECMAScript の予約語ではなく、`const of = ...; of\`hello\``
        // は正当なタグ付きテンプレート呼び出し。for-of ヘッダ抑制が正当なタグ名としての
        // `of` まで握り潰さないことを保証する。
        const string content = """
            const of = (strings) => strings.raw[0];
            function run() {
                return of`hello`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        var hit = Assert.Single(references.Where(r => r.SymbolName == "of" && r.ReferenceKind == "call"));
        Assert.Equal("run", hit.ContainerName);
    }

    [Fact]
    public void Extract_JavaScriptClassicForWithOfAsTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression: classic `for (init; cond; step)` must not silence a
        // legitimate `of` tag used inside its init clause. The for-header probe classifies
        // the loop shape by counting top-level `;` inside the `(...)` group — classic form
        // has `;` and keeps `of` visible.
        // issue #268 退行防止: 古典形 `for (init; cond; step)` 内の `of` タグは消さない。
        // 囲む `(...)` 内のトップレベル `;` を数え、`;` 入りの classic `for` では `of` を
        // タグとして残す。
        const string content = """
            const of = (strings) => strings.raw[0];
            function run() {
                for (of`x`; keepGoing(); step()) {
                    break;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        var hit = Assert.Single(references.Where(r => r.SymbolName == "of" && r.ReferenceKind == "call"));
        Assert.Equal("run", hit.ContainerName);
    }

    [Fact]
    public void Extract_JavaScriptMultiLineForOfLoopOverPlainTemplate_IsNotCaptured()
    {
        // issue #268 regression: the `for (...)` header may span multiple lines. The
        // backward-scan from `of` must cross line boundaries to find the enclosing `(` and
        // then confirm zero top-level `;` to classify this as the for-of form.
        // issue #268 退行防止: `for (...)` ヘッダは複数行に跨ることがある。`of` からの
        // 後方走査は行境界を越えて `(` を見つけ、トップレベル `;` が 0 のとき for-of 形と
        // 判定する必要がある。
        const string content = "function f() {\n" +
            "    for (\n" +
            "        const ch of `abc`\n" +
            "    ) {\n" +
            "        use(ch);\n" +
            "    }\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptMultiLineForAwaitOfLoopOverPlainTemplate_IsNotCaptured()
    {
        // issue #268 regression: multi-line `for await (...)` with the iterator on a later
        // line must still be suppressed. The cross-line scan has to handle the optional
        // `await` contextual keyword between `for` and `(` too.
        // issue #268 退行防止: 複数行 `for await (...)` で iterator 行が離れていても抑制する。
        // `for` と `(` の間の `await` も跨いで判定する。
        const string content = "async function f(iter) {\n" +
            "    for await (\n" +
            "        const x of `abc`\n" +
            "    ) {\n" +
            "        use(x);\n" +
            "    }\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptTagFollowedByNbspBeforeTemplate_IsCapturedAsCall()
    {
        // issue #268 regression: ECMAScript WhiteSpace includes non-ASCII characters such as
        // U+00A0 (NBSP), so `of\u00A0\`hello\`` is still a tagged-template call. The tag-to-
        // backtick backward-scan must tolerate any `char.IsWhiteSpace` codepoint, not just
        // ASCII space and tab.
        // issue #268 退行防止: ECMAScript の WhiteSpace は U+00A0 (NBSP) のような非 ASCII
        // も含むため、`of\u00A0\`hello\`` もタグ付きテンプレート呼び出しとして拾う必要があ
        // る。タグとバッククォート間の後方走査は ASCII スペース/タブだけでなく任意の
        // `char.IsWhiteSpace` を許容する。
        const string content = "const of = (strings) => strings.raw[0];\n" +
            "function run() {\n" +
            "    return of\u00A0`hello`;\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        var hit = Assert.Single(references.Where(r => r.SymbolName == "of" && r.ReferenceKind == "call"));
        Assert.Equal("run", hit.ContainerName);
    }

    [Fact]
    public void Extract_JavaScriptForOfHeaderWithNbspSeparator_IsNotCaptured()
    {
        // issue #268 regression: the for-of header probe must also accept non-ASCII
        // whitespace. `for\u00A0(const ch of \`abc\`)` is a valid for-of loop and must not
        // emit a phantom `call of`.
        // issue #268 退行防止: for-of ヘッダ判定も非 ASCII 空白を許容する必要がある。
        // `for\u00A0(const ch of \`abc\`)` は正当な for-of 形なので phantom `call of` を
        // 出さない。
        const string content = "function f() {\n" +
            "    for\u00A0(const ch of `abc`) {\n" +
            "        use(ch);\n" +
            "    }\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptForAwaitOfHeaderWithNbspSeparator_IsNotCaptured()
    {
        // issue #268 regression: the for-await-of header probe must also tolerate non-ASCII
        // whitespace between `for`, `await`, and `(`.
        // issue #268 退行防止: for-await-of ヘッダ判定は `for`・`await`・`(` の間の非 ASCII
        // 空白も許容する。
        const string content = "async function f(iter) {\n" +
            "    for\u00A0await\u00A0(const x of `abc`) {\n" +
            "        use(x);\n" +
            "    }\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptTagFollowedByBomBeforeTemplate_IsCapturedAsCall()
    {
        // issue #268 regression: BOM `U+FEFF` between a tag and the backtick must be treated
        // as inter-token whitespace. .NET's `char.IsWhiteSpace('\uFEFF')` returns false so the
        // masker has to add BOM explicitly.
        // issue #268 退行防止: タグと backtick の間の BOM `U+FEFF` もトークン間空白として
        // 扱う必要がある。.NET の `char.IsWhiteSpace('\uFEFF')` は false なので明示的に足す。
        const string content = "function f() {\n" +
            "    return of\uFEFF`hello`;\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptForOfHeaderWithBomSeparator_IsNotCaptured()
    {
        // issue #268 regression: for-of header probe must tolerate BOM between `for` and `(`.
        // issue #268 退行防止: for-of ヘッダ判定は `for` と `(` の間の BOM も許容する。
        const string content = "function f(arr) {\n" +
            "    for\uFEFF(const ch of `abc`) {\n" +
            "        use(ch);\n" +
            "    }\n" +
            "}\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptForOfHeaderWithStringParen_IsNotCaptured()
    {
        // issue #268 regression: a string literal `)` inside the for-of header (e.g. type
        // annotation or plain string expression) must not corrupt the paren counter. The
        // post-pass scan buffer blanks string content before counting.
        // issue #268 退行防止: for-of ヘッダ内の文字列リテラル `)` が paren カウンタを壊さ
        // ないよう、post-pass のスキャンバッファで文字列内容を空白化する。
        const string content = """
            function f(arr) {
                for (const x = ")" /* annotation */ && arr[0] of `abc`) {
                    use(x);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptMemberCallNamedInOrInstanceof_IsCapturedAsCall()
    {
        // issue #268 regression: adding `in`, `instanceof`, `void`, `case`, `delete` to the
        // shared ignore list would wrongly drop member calls like `api.in(...)` or
        // `api.instanceof(...)`. The ignore list for those tokens is tagged-template-emit
        // only, so real member calls must still be captured by CallRegex.
        // issue #268 退行防止: `in` / `instanceof` / `void` / `case` / `delete` を共通 ignore
        // に足すと `api.in(...)` のような正当なメンバー呼び出しまで消えてしまう。これらは
        // tagged-template 発行経路のみで弾き、CallRegex 経路では通常どおり捕捉する。
        const string content = """
            function use(api) {
                api.in("x");
                api.instanceof("y");
                api.delete("z");
                api.case(1);
                api.void(2);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "in");
        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "instanceof");
        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "delete");
        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "case");
        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "void");
    }

    [Fact]
    public void Extract_JavaScriptExportDefaultBeforePlainTemplate_IsNotCaptured()
    {
        // issue #268 regression: `export default \`plain\`` is a valid default export of a
        // template-literal expression; `default` is a statement keyword, not a tag identifier.
        // The backward-scan from the backtick picks up `default`, so the tagged-template emit
        // site has to drop it.
        // issue #268 退行防止: `export default \`plain\`` は template リテラル式の default
        // export として正当で、`default` はタグ識別子ではない。backward-scan が `default`
        // を拾うため、タグ付きテンプレート発行側で弾く必要がある。
        const string content = "export default `plain`;\n";

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "default");
    }

    [Fact]
    public void Extract_JavaScriptFinallyBeforePlainTemplate_IsNotCaptured()
    {
        // issue #268 regression: `finally` is a clause keyword of `try ... finally { ... }`,
        // never a tag identifier. Even malformed inputs that place `finally` right before a
        // backtick must not emit a phantom `call finally` row.
        // issue #268 退行防止: `finally` は try-finally 節のキーワードであり、タグ識別子には
        // ならない。backtick の直前に `finally` が来る形（不正入力含む）でも phantom
        // `call finally` を出さない。
        const string content = """
            function f() {
                try {
                    doWork();
                } finally `cleanup`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "finally");
    }

    [Fact]
    public void Extract_JavaScriptForOfBindingPatternTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression guard: in `for (const [x = of\`tag\`] of arr)`, the inner
        // `of` is a real tagged-template call inside the binding pattern LHS, while the
        // outer `of` is the for-of iterator keyword. Only the iterator keyword should be
        // suppressed; the binding-pattern tag must still be captured.
        // issue #268 退行防止: `for (const [x = of\`tag\`] of arr)` の内側 `of` は binding
        // pattern LHS 内の正当なタグ付きテンプレート呼び出しで、外側 `of` だけが for-of
        // iterator keyword。内側のタグ呼び出しは必ず残す。
        const string content = """
            function f(arr) {
                for (const [x = of`tag`] of arr) { use(x); }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "of");
    }

    [Fact]
    public void Extract_JavaScriptMultiLineTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression guard: a tag identifier can sit on a prior line from the
        // opener backtick (`tag\n\`hello\``). Node 25.2.0 evaluates this as a real
        // tagged-template call; the backward-scan must cross the line boundary through
        // inter-token whitespace so `call tag` is emitted.
        // issue #268 退行防止: タグ識別子は opener の backtick より前の行に置ける
        // (`tag\n\`hello\``)。Node 25.2.0 は実際のタグ呼び出しとして評価するため、
        // backward-scan は行境界をまたぐ空白を越えて `call tag` を発行する必要がある。
        const string content = """
            function run(tag) {
                return tag
            `hello`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "tag");
    }

    [Fact]
    public void Extract_JavaScriptObjectDefaultTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression guard: `obj.default\`x\`` is a legal tagged-template call
        // because reserved words are valid property names in JS/TS. The bare-keyword
        // denylist (`default` / `finally` / ...) must NOT suppress member-access tags.
        // issue #268 退行防止: `obj.default\`x\`` は JS/TS で予約語も property 名に
        // なれるため正当なタグ呼び出し。bare-keyword 除外リスト（`default` / `finally` /
        // ...）はメンバーアクセスのタグを握り潰してはならない。
        const string content = """
            function run(obj) {
                return obj.default`x`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "default");
    }

    [Fact]
    public void Extract_JavaScriptMultiLineTaggedTemplateWithLineComment_IsCapturedAsCall()
    {
        // issue #268 regression guard: when the multi-line tag line ends with a `//`
        // comment (`return tag // trailing comment\n\`hello\``), the backward scan
        // must not pick up `comment` as the tag identifier. The masker must blank the
        // `//` comment tail so the cross-line scan sees only real code.
        // issue #268 退行防止: 複数行タグの前行末に `//` コメントがある場合
        // (`return tag // trailing comment\n\`hello\``)、後方スキャンが
        // `comment` をタグ識別子と誤認してはならない。masker 側で `//` コメント以降を
        // 空白化し、行またぎスキャンは実コードのみを見るようにする。
        const string content = """
            function run(tag) {
                return tag // trailing comment
            `hello`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "tag");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "comment");
    }

    [Fact]
    public void Extract_JavaScriptObjectReturnTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression guard: `obj.return\`x\`` is a legal tagged-template call
        // because `return` is a valid property name in JS/TS. The shared ignore list
        // (which holds `return` / `throw` / `await` / `typeof` / `yield` for JS/TS to
        // suppress bare-keyword phantom calls) must NOT suppress member-access tags.
        // issue #268 退行防止: `obj.return\`x\`` は `return` が property 名として合法
        // なので正当なタグ呼び出し。bare-keyword の phantom 呼び出しを抑止する共有
        // ignore list（`return` / `throw` / `await` / `typeof` / `yield`）は
        // メンバーアクセスのタグを握り潰してはならない。
        const string content = """
            function run(obj) {
                return obj.return`x`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "return");
    }

    [Fact]
    public void Extract_JavaScriptObjectAwaitTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression guard: `obj.await\`y\`` is a legal tagged-template call;
        // `await` is a reserved word in async contexts but is still a valid property
        // name. Member-access tags must bypass the `IsIgnoredCallName` filter.
        // issue #268 退行防止: `obj.await\`y\`` は await が async 内で予約語でも
        // property 名としては合法なので正当なタグ呼び出し。メンバーアクセスのタグは
        // `IsIgnoredCallName` を迂回する必要がある。
        const string content = """
            async function run(obj) {
                return obj.await`y`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "await");
    }

    [Fact]
    public void Extract_JavaScriptObjectFinallyTaggedTemplate_IsCapturedAsCall()
    {
        // issue #268 regression guard: `obj.finally\`y\`` is a legal tagged-template call;
        // `finally` is a reserved word but a valid property name. Member-access tags must
        // bypass the bare-keyword denylist that handles `try {} finally \`cleanup\``.
        // issue #268 退行防止: `obj.finally\`y\`` は `finally` が予約語でも property 名と
        // して合法なので正当なタグ呼び出し。メンバーアクセスのタグは
        // `try {} finally \`cleanup\`` 用の bare-keyword 除外リストを迂回する必要がある。
        const string content = """
            function run(obj) {
                return obj.finally`y`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.Contains(references, r => r.ReferenceKind == "call" && r.SymbolName == "finally");
    }

    [Fact]
    public void Extract_JavaScriptInstanceofBeforePlainTemplate_IsNotCaptured()
    {
        // Regression guard: `foo instanceof \`plain\`` uses `instanceof` as the type-check
        // operator, not as a tag identifier. The backtick backward-scan picks it up, so
        // IsIgnoredCallName must suppress the phantom `call instanceof` edge.
        // 退行防止: `foo instanceof \`plain\`` の `instanceof` は型チェック演算子であり、
        // タグ識別子ではない。backward-scan が拾う `instanceof` は IsIgnoredCallName で握り潰す。
        const string content = """
            function check(foo) {
                return foo instanceof `plain`;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);
        var references = ReferenceExtractor.Extract(1, "javascript", content, symbols);

        Assert.DoesNotContain(references, r => r.ReferenceKind == "call" && r.SymbolName == "instanceof");
    }

    [Fact]
    public void Extract_KotlinTripleQuotedString_DoesNotLeakPhantomCallReferences()
    {
        // Regression for issues #385 and #1446: call-looking identifiers inside a Kotlin
        // multi-line raw string (""".. .""") must not be captured as references.
        // issue #385 / #1446 回帰: Kotlin の複数行 raw 文字列（"""..."""）の内側にある
        // 呼び出しらしい識別子は参照として抽出してはならない。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        SELECT * FROM users
                        WHERE id = kotlinPhantomCall(42)
                        AND name = anotherKotlinPhantom("bob")
                    """.trimIndent()

                    realKotlinCall()
                }

                private fun realKotlinCall() {}
                private fun kotlinPhantomCall(x: Int): Int = x
                private fun anotherKotlinPhantom(s: String): String = s
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinPhantomCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherKotlinPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinTripleQuotedStringInterpolationHole_KeepsRealCallReference()
    {
        // Regression for issue #385: `${expr}` interpolation holes inside a Kotlin
        // multi-line raw string must preserve real call edges so the reference
        // graph does not silently drop them. `$ident` is a bare-identifier hole and
        // not a call; masking it with the body is safe.
        // issue #385 回帰: Kotlin の複数行 raw 文字列内の `${expr}` ホールは
        // 本物の call エッジを残すこと。`$ident` は単独識別子で call にならないため
        // 本体とともにマスクしてもよい。
        const string content = """"
            package demo

            class Demo {
                fun m(name: String) {
                    val sql = """
                        phantom: kotlinPhantomCall(42)
                        real: ${runTask()} trailing
                        nested: ${helper(factory(deepReal()))}
                        bare: $name
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun runTask() {}
                private fun helper(x: Int): Int = x
                private fun factory(x: Int): Int = x
                private fun deepReal(): Int = 0
                private fun realKotlinCall() {}
                private fun kotlinPhantomCall(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinPhantomCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "runTask" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "factory" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "deepReal" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaTripleQuotedString_DoesNotLeakPhantomCallReferences()
    {
        // Regression for issues #385 and #1446: call-looking identifiers inside a Scala
        // multi-line raw string (""".. .""", including interpolator-prefixed forms
        // such as `s"""..."""` / `raw"""..."""`) must not be captured as references.
        // issue #385 / #1446 回帰: Scala の複数行 raw 文字列（"""...""" および `s"""..."""` /
        // `raw"""..."""` などの interpolator 形式）の内側にある呼び出しらしい識別子は
        // 参照として抽出してはならない。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val plain =
                  """
                    |SELECT * FROM users
                    |WHERE id = scalaPhantomCall(42)
                    |AND name = anotherScalaPhantom("bob")
                  """.stripMargin
                val rawForm = raw"""rawScalaPhantom(7)"""
                realScalaCall()
              }

              def realScalaCall(): Unit = ()
              def scalaPhantomCall(x: Int): Int = x
              def anotherScalaPhantom(s: String): String = s
              def rawScalaPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "scalaPhantomCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherScalaPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "rawScalaPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaStringInterpolatorHole_KeepsRealCallReference()
    {
        // Regression for issue #385: `${expr}` interpolation holes inside a Scala
        // interpolator-prefixed multi-line string (`s"""..."""` / `f"""..."""`) must
        // preserve real call edges so the reference graph does not silently drop
        // them. Plain `"""..."""` has no interpolation and its ${...}-looking text
        // must stay masked.
        // issue #385 回帰: Scala の interpolator 形式（`s"""..."""` / `f"""..."""`）
        // 内の `${expr}` ホールは本物の call エッジを残すこと。プレーン `"""..."""`
        // は補間なしで、${...} 風のテキストもマスクする。
        const string content = """"
            package demo

            class Demo {
              def m(name: String): Unit = {
                val plain =
                  """
                    |plain: scalaPhantomCall(42)
                  """.stripMargin
                val interp = s"""
                    |phantom: scalaPhantomCall(42)
                    |real: ${runTask()} trailing
                    |nested: ${helper(factory(deepReal()))}
                    |bare: $name
                  """.stripMargin
                realScalaCall()
              }

              def runTask(): Int = 1
              def helper(x: Int): Int = x
              def factory(x: Int): Int = x
              def deepReal(): Int = 0
              def realScalaCall(): Unit = ()
              def scalaPhantomCall(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "scalaPhantomCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "runTask" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "factory" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "deepReal" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinTripleQuotedStringInterpolationHole_WithCommentContainingCloseBrace_KeepsRealCallReference()
    {
        // Regression for issue #385 follow-up: a `${expr}` interpolation hole in a
        // Kotlin raw string may contain a block comment or line comment whose body
        // happens to include `}`. The hole scanner must recognize comments first so
        // the `}` inside the comment does not close the hole prematurely and drop
        // the following real call from the reference graph.
        // issue #385 続編: Kotlin の `${expr}` 補間ホール内のコメント本体に含まれる
        // `}` でホールを早閉じしてはならず、コメント後の本物の call を残すこと。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        real: ${ /* } */ kotlinAfterComment() }
                        line: ${ kotlinLineBefore() // }
                        } tail
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun kotlinAfterComment(): Int = 0
                private fun kotlinLineBefore(): Int = 0
                private fun realKotlinCall() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "kotlinAfterComment" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "kotlinLineBefore" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaStringInterpolatorHole_WithCommentContainingCloseBrace_KeepsRealCallReference()
    {
        // Regression for issue #385 follow-up: an `${expr}` interpolation hole in a
        // Scala interpolator-prefixed multi-line string may contain a block comment
        // or line comment whose body happens to include `}`. The hole scanner must
        // recognize comments first so the `}` inside the comment does not close
        // the hole prematurely.
        // issue #385 続編: Scala の `${expr}` 補間ホール内のコメント本体に含まれる
        // `}` でホールを早閉じせず、コメント後の本物の call を残すこと。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val interp = s"""
                    |real: ${ /* } */ scalaAfterComment() }
                    |line: ${ scalaLineBefore() // }
                    |} tail
                  """.stripMargin
                realScalaCall()
              }

              def scalaAfterComment(): Int = 0
              def scalaLineBefore(): Int = 0
              def realScalaCall(): Unit = ()
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "scalaAfterComment" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "scalaLineBefore" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinTripleQuotedStringNestedTripleInHole_DoesNotLeakPhantomCalls()
    {
        // Regression for issue #992: a nested `"""..."""` literal opened inside a
        // Kotlin `${...}` interpolation hole must not leak its body as phantom
        // calls. The hole scanner has to recognise `"""` as a nested triple opener
        // (not just `"` / `'` single-line strings) and mask through to the matching
        // close before the hole's `{` / `}` tracking resumes.
        // issue #992 回帰: Kotlin の `${...}` ホール内で開いた nested `"""..."""` の本文を
        // phantom call として漏らさないこと。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        outer: ${ wrap("""
                            kotlinNestedPhantom(99)
                        """) }
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun wrap(x: String): String = x
                private fun realKotlinCall() {}
                private fun kotlinNestedPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinNestedPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaStringInterpolatorHoleNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for issue #992: a nested `"""..."""` literal opened inside a
        // Scala interpolator-prefixed `${...}` hole must not leak its body as
        // phantom calls.
        // issue #992 回帰: Scala interpolator 形式の `${...}` ホール内で開いた
        // nested `"""..."""` の本文を phantom call として漏らさないこと。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val interp = s"""
                    |outer: ${ wrap("""
                    |    scalaNestedPhantom(99)
                    |""") }
                  """.stripMargin
                realScalaCall()
              }

              def wrap(x: String): String = x
              def realScalaCall(): Unit = ()
              def scalaNestedPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "scalaNestedPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinTripleQuotedStringNestedTripleHole_PreservesRealCallReferences()
    {
        // Regression for issue #996: a nested `"""..."""` literal opened inside an
        // outer Kotlin `${...}` interpolation hole still has its own `${expr}` holes.
        // The masker must preserve real call edges inside those inner holes while
        // continuing to mask the rest of the nested literal body.
        // issue #996 回帰: Kotlin の outer `${...}` 内に開いた nested `"""..."""` でも
        // それ自身の `${expr}` ホール内の本物の call を保持する。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        outer: ${ wrap("""
                            inner: ${innerCall()}
                            phantom: kotlinNestedPhantom(99)
                        """) }
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun wrap(x: String): String = x
                private fun innerCall(): Int = 0
                private fun realKotlinCall() {}
                private fun kotlinNestedPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "innerCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinNestedPhantom" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaStringInterpolatorHoleNestedInterpolatorTriple_PreservesRealCallReferences()
    {
        // Regression for issue #996: a nested interpolator-prefixed `s"""..."""` (or
        // `f"""...`, `raw"""...`) literal opened inside an outer Scala `${...}` hole
        // still has its own `${expr}` holes. Plain nested `"""..."""` (no prefix)
        // continues to mask everything because plain Scala triples have no interpolation.
        // issue #996 回帰: Scala の outer `${...}` 内に開いた interpolator 付き nested
        // triple は `${expr}` ホール内の call を残し、プレーン nested triple は全マスク。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val interp = s"""
                    |outer: ${ wrap(s"""
                    |    inner: ${innerCall()}
                    |    phantom-prefixed: scalaNestedPhantom(99)
                    |""") }
                    |plain-outer: ${ wrap("""
                    |    plain phantom: scalaPlainNestedPhantom(99)
                    |""") }
                  """.stripMargin
                realScalaCall()
              }

              def wrap(x: String): String = x
              def innerCall(): Int = 0
              def realScalaCall(): Unit = ()
              def scalaNestedPhantom(x: Int): Int = x
              def scalaPlainNestedPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "innerCall" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "scalaNestedPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "scalaPlainNestedPhantom" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinTripleBody_QuotedSubstringWithCallShape_IsMasked()
    {
        // Regression for issue #998 review claim: a quoted substring inside a Kotlin
        // `"""..."""` body that contains call-shaped text must not survive into the
        // reference graph. The triple body's default `masked[pos] = ' '` path masks
        // every char, so the quoted span (including the call shape) is fully blanked.
        // issue #998 のレビュー懸念に対する回帰: Kotlin の `"""..."""` 本文中の
        // 引用符付き部分文字列に call 形のテキストが含まれていても、reference graph に
        // 漏らさないこと。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        WHERE message = "kotlinQuotedPhantom(42)"
                        AND extra = "anotherKotlinQuotedPhantom('inner')"
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun realKotlinCall() {}
                private fun kotlinQuotedPhantom(x: Int): Int = x
                private fun anotherKotlinQuotedPhantom(s: String): String = s
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinQuotedPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherKotlinQuotedPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaTripleBody_QuotedSubstringWithCallShape_IsMasked()
    {
        // Regression for issue #998 review claim: a quoted substring inside a Scala
        // `"""..."""` body that contains call-shaped text must not survive into the
        // reference graph.
        // issue #998 のレビュー懸念に対する回帰: Scala の `"""..."""` 本文中の
        // 引用符付き部分文字列に call 形のテキストが含まれていても、reference graph に
        // 漏らさないこと。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val sql =
                  """
                    |WHERE message = "scalaQuotedPhantom(42)"
                    |AND extra = "anotherScalaQuotedPhantom('inner')"
                  """.stripMargin
                realScalaCall()
              }

              def realScalaCall(): Unit = ()
              def scalaQuotedPhantom(x: Int): Int = x
              def anotherScalaQuotedPhantom(s: String): String = s
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "scalaQuotedPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "anotherScalaQuotedPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinThreeLevelDeepNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for codex review #9 finding: a triple-quoted literal opened
        // 3+ levels deep (i.e. inside the nested triple's own `${...}` hole) must
        // not leak phantom calls. The defensive depth counter masks the deep body
        // through to the matching close so call-shaped text cannot leak as
        // references. Real calls 4+ levels deep are not preserved (would require
        // a full stack), but the masking soundness is preserved.
        // codex review #9 への回帰: Kotlin で 3 段以上深い triple 本文の phantom が
        // 漏れないこと。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        outer: ${ wrap("""
                            inner: ${ helper("""
                                kotlinDeepPhantom(99)
                            """) }
                        """) }
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun wrap(x: String): String = x
                private fun helper(x: String): String = x
                private fun realKotlinCall() {}
                private fun kotlinDeepPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinDeepPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaThreeLevelDeepNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for codex review #9 finding: same shape for Scala interpolator
        // `${...}` holes.
        // codex review #9 への回帰: Scala の interpolator `${...}` ホールの 3 段深い triple。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val interp = s"""
                    |outer: ${ wrap(s"""
                    |    inner: ${ helper(s"""
                    |        scalaDeepPhantom(99)
                    |""") }
                    |""") }
                  """.stripMargin
                realScalaCall()
              }

              def wrap(x: String): String = x
              def helper(x: String): String = x
              def realScalaCall(): Unit = ()
              def scalaDeepPhantom(x: Int): Int = x
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "scalaDeepPhantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinFourLevelDeepNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for issue #1002: a 4th nested triple opened inside the deep
        // Kotlin body must not unwind the 3-deep frame early and expose later
        // source on the same outer hole.
        // issue #1002 回帰: Kotlin で deep body 内に開いた 4 段目の triple が
        // 3 段深い frame を早抜けさせず、後続ソースを露出させないこと。
        const string content = """"
            package demo

            class Demo {
                fun m() {
                    val sql = """
                        outer: ${ wrap("""
                            inner: ${ helper("""
                                deep: ${ deeper("""
                                    kotlinDeepestPhantom(99)
                                """)
                                kotlinAfterDeep4Phantom()
                            """) }
                        """) }
                    """.trimIndent()
                    realKotlinCall()
                }

                private fun wrap(x: String): String = x
                private fun helper(x: String): String = x
                private fun deeper(x: String): String = x
                private fun kotlinAfterDeep4Phantom(): Int = 0
                private fun kotlinDeepestPhantom(x: Int): Int = x
                private fun realKotlinCall() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinDeepestPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "kotlinAfterDeep4Phantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realKotlinCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_ScalaFourLevelDeepNestedTriple_DoesNotLeakPhantomCalls()
    {
        // Regression for issue #1002: a 4th nested triple opened inside the deep
        // Scala body must not unwind the 3-deep frame early.
        // issue #1002 回帰: Scala で deep body 内に開いた 4 段目の triple が
        // 3 段深い frame を早抜けさせないこと。
        const string content = """"
            package demo

            class Demo {
              def m(): Unit = {
                val interp = s"""
                    |outer: ${ wrap(s"""
                    |    inner: ${ helper(s"""
                    |        deep: ${ deeper(s"""
                    |            scalaDeepestPhantom(99)
                    |        """) }
                    |        scalaAfterDeep4Phantom()
                    |    """) }
                  """.stripMargin
                realScalaCall()
              }

              def wrap(x: String): String = x
              def helper(x: String): String = x
              def deeper(x: String): String = x
              def scalaAfterDeep4Phantom(): Int = 0
              def scalaDeepestPhantom(x: Int): Int = x
              def realScalaCall(): Unit = ()
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "scala", content);
        var references = ReferenceExtractor.Extract(1, "scala", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "scalaDeepestPhantom" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "scalaAfterDeep4Phantom" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "wrap" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "helper" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "realScalaCall" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinTypedDeclarations_CaptureStructuralTypeReferences()
    {
        const string content = """
            interface Handler : BaseHandler<Request>, Closeable

            class Service<T : Entity>(private val repo: Repository) : BaseService(repo), Runnable where T : Auditable {
                val current: User = repo.load() as User
                val fallback = repo.load() as User ?: fallbackUser

                fun load(input: User, options: LoadOptions): Result<User> {
                    return input
                }

                fun check(value: Any) = value is User && value.enabledFlag
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "BaseHandler" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Request" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Closeable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Entity" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Repository" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "BaseService" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Runnable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Auditable" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "LoadOptions" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Any" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "enabledFlag" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "fallbackUser" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinSecondaryConstructorDelegation_RewritesThisAndSuperCalls()
    {
        const string content = """
            open class Parent(value: Int) {}

            class Child
                : Parent {
                constructor() : this(0) {
                }

                constructor(value: Int) : super(value) {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Child"
            && r.ReferenceKind == "call"
            && r.Context.Contains(": this(", StringComparison.Ordinal)
            && r.ContainerKind == "function"
            && r.ContainerName == "Child");
        Assert.Contains(references, r =>
            r.SymbolName == "Parent"
            && r.ReferenceKind == "call"
            && r.Context.Contains(": super(", StringComparison.Ordinal)
            && r.ContainerKind == "function"
            && r.ContainerName == "Child");
        Assert.DoesNotContain(references, r =>
            r.ReferenceKind == "call"
            && (r.SymbolName == "constructor" || r.SymbolName == "this" || r.SymbolName == "super"));
    }

    [Fact]
    public void Extract_KotlinClassLiterals_CaptureTypeReferences()
    {
        const string content = """
            class User {}
            class Profile {}

            class Service {
                fun inspect(value: Any) {
                    val kClass = User::class
                    val javaClass = Profile::class.java
                    val runtimeClass = value::class
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "User"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("User::class", StringComparison.Ordinal)
            && r.ContainerName == "inspect");
        Assert.Contains(references, r =>
            r.SymbolName == "Profile"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("Profile::class.java", StringComparison.Ordinal)
            && r.ContainerName == "inspect");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "value"
            && r.ReferenceKind == "type_reference"
            && r.Context.Contains("value::class", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "class"
            && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_KotlinKDocLinks_CaptureTypeReferences()
    {
        const string content = """
            class User
            class Helper
            class Hidden

            class Service {
                /**
                 * Loads [User] and [User.name].
                 * See [docs](https://example.invalid) and [alias][User].
                 * @see Helper
                 */
                fun load() {}

                fun other() {
                    /** [Hidden] */
                    val local = 0
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference" && r.ContainerName == "load");
        Assert.Contains(references, r => r.SymbolName == "name" && r.ReferenceKind == "type_reference" && r.ContainerName == "load");
        Assert.Contains(references, r => r.SymbolName == "Helper" && r.ReferenceKind == "type_reference" && r.ContainerName == "load");
        Assert.DoesNotContain(references, r => r.SymbolName == "docs" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "alias" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Hidden" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_KotlinImportAlias_DoesNotEmitTypeReference()
    {
        const string content = """
            import com.example.Bar as Baz
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);
        var references = ReferenceExtractor.Extract(1, "kotlin", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "Baz" && r.ReferenceKind == "type_reference");
    }
}
