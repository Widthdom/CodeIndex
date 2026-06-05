using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for SymbolExtractor.
/// SymbolExtractorのテスト。
/// </summary>
public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CancelledToken_ThrowsBeforeWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SymbolExtractor.Extract(1, "csharp", "public class App { }", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void BuiltInSymbolRegexes_HaveBoundedMatchTimeouts()
    {
        var regexes = EnumerateStaticRegexValues(
            typeof(SymbolExtractor).Assembly.GetTypes().Where(IsSymbolRegexOwnerType))
            .ToList();

        Assert.NotEmpty(regexes);

        var infiniteTimeouts = regexes
            .Where(item => item.Regex.MatchTimeout == Regex.InfiniteMatchTimeout)
            .Select(item => item.Path)
            .ToList();

        Assert.True(
            infiniteTimeouts.Count == 0,
            "Built-in symbol regexes must have explicit match timeouts: " + string.Join(", ", infiniteTimeouts));
    }

    [Fact]
    public void Extract_CSharp_BraceBodiedFunctionSignatureStopsAtDeclarationHeader()
    {
        const string content = """
            internal static class Example
            {
                public static void First(string value)
                {
                    if (value == null)
                        return;
                }

                private static bool Second()
                {
                    return true;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var first = Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "First"));

        Assert.Equal("public static void First(string value) {", first.Signature);
        Assert.DoesNotContain("Second", first.Signature, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_BuiltInSymbolRegexes_AdversarialLongLinesDoNotThrow()
    {
        var typeScriptLine = "export const Component = React.memo<" + new string('A', 20000) + new string('<', 2000) + new string('>', 2000) + ">(value);";
        var cssLine = ":root { --" + new string('a', 50000) + ": " + new string('(', 2000) + "; }";

        var stopwatch = Stopwatch.StartNew();
        var exception = Record.Exception(() =>
        {
            SymbolExtractor.Extract(1, "typescript", typeScriptLine);
            SymbolExtractor.Extract(2, "css", cssLine);
        });
        stopwatch.Stop();

        Assert.Null(exception);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Adversarial symbol extraction took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Extract_CustomSymbolPlugin_HandlesUnsupportedLanguage()
    {
        lock (TestConsoleLock.Gate)
        {
            ExtractorPluginRegistry.ResetForTests();
            ExtractorPluginRegistry.Register(new ToyDslSymbolExtractor());

            var symbols = SymbolExtractor.Extract(7, "toydsl", "entity Widget", "demo.toy");

            var symbol = Assert.Single(symbols);
            Assert.Equal(7, symbol.FileId);
            Assert.Equal("class", symbol.Kind);
            Assert.Equal("Widget", symbol.Name);
            Assert.Equal("toydsl", FileIndexer.DetectLanguage("demo.toy"));
            Assert.Contains("toydsl", SymbolExtractor.GetSupportedLanguages());
            ExtractorPluginRegistry.ResetForTests();
        }
    }

    [Fact]
    public void Extract_ConfiguredPatternYaml_HandlesOutOfTreeLanguage()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_patterns_{Guid.NewGuid():N}");
            var originalDirectory = Environment.CurrentDirectory;
            try
            {
                Directory.CreateDirectory(Path.Combine(tempDir, ".cdidx", "patterns"));
                File.WriteAllText(
                    Path.Combine(tempDir, ".cdidx", "patterns", "toydsl.yaml"),
                    "language: \"toydsl\"\nextensions:\n  - extension: \".toy\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>\\\\w+)\"\n");
                var outsideDir = Path.Combine(tempDir, "outside");
                Directory.CreateDirectory(outsideDir);
                Environment.CurrentDirectory = outsideDir;
                ExtractorPluginRegistry.ReloadForTests();

                var symbols = SymbolExtractor.Extract(2, "toydsl", "entity Widget", "demo.toy", tempDir);

                var symbol = Assert.Single(symbols);
                Assert.Equal("class", symbol.Kind);
                Assert.Equal("Widget", symbol.Name);
                Assert.Equal("toydsl", FileIndexer.DetectLanguage("demo.toy"));
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                Environment.CurrentDirectory = originalDirectory;
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }





    [Theory]
    [InlineData("csharp", "Pages/Product.razor")]
    [InlineData("csharp", "Views/Product.cshtml")]
    [InlineData("razor", "Pages/Product.razor")]
    [InlineData("cshtml", "Views/Product.cshtml")]
    [InlineData("razor", null)]
    public void Extract_RazorFile_IndexesDirectiveSymbols(string language, string? filePath)
    {
        const string content = """
            @page "/products/{id:int}"
            @implements IDisposable
            @attribute [Authorize]
            @layout MainLayout

            @code {
                private void Load() { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, language, content, filePath);

        Assert.Contains(symbols, symbol => symbol.Kind == "route" && symbol.Name == "/products/{id:int}" && symbol.Line == 1);
        Assert.Contains(symbols, symbol => symbol.Kind == "implements" && symbol.Name == "IDisposable" && symbol.Line == 2);
        Assert.Contains(symbols, symbol => symbol.Kind == "attribute" && symbol.Name == "Authorize" && symbol.Line == 3);
        Assert.Contains(symbols, symbol => symbol.Kind == "layout" && symbol.Name == "MainLayout" && symbol.Line == 4);
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Load");
    }

    [Fact]
    public void Extract_HtmlClassAttributes_IndexesIndividualClassReferences()
    {
        const string content = """
            <div class="btn  btn-primary mx-2 md:flex hover:bg-red-500 [&>*]:mt-2"></div>
            <span className='inline-flex  items-center'></span>
            <section class="   "></section>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);
        var classReferences = symbols
            .Where(symbol => symbol.Kind == "reference")
            .Select(symbol => (symbol.Name, symbol.Line))
            .ToArray();

        Assert.Contains(("btn", 1), classReferences);
        Assert.Contains(("btn-primary", 1), classReferences);
        Assert.Contains(("mx-2", 1), classReferences);
        Assert.Contains(("md:flex", 1), classReferences);
        Assert.Contains(("hover:bg-red-500", 1), classReferences);
        Assert.Contains(("[&>*]:mt-2", 1), classReferences);
        Assert.Contains(("inline-flex", 2), classReferences);
        Assert.Contains(("items-center", 2), classReferences);
        Assert.DoesNotContain(classReferences, symbol => string.IsNullOrWhiteSpace(symbol.Name));
        Assert.DoesNotContain(("btn  btn-primary mx-2 md:flex hover:bg-red-500 [&>*]:mt-2", 1), classReferences);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_ClassifiesReactCustomHookFunctions(string language)
    {
        var content = """
            import { useEffect, useState } from "react";

            const useLocalState = () => {
              const [value, setValue] = useState(0);
              useEffect(() => setValue(value + 1), [value]);
              return value;
            };

            export const useComposedHook = () => {
              return useLocalState();
            };

            const ordinaryFunction = () => useLocalState();
            """;

        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, symbol => symbol.Kind == "hook" && symbol.Name == "useLocalState");
        Assert.Contains(symbols, symbol => symbol.Kind == "hook" && symbol.Name == "useComposedHook");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "hook" && symbol.Name == "ordinaryFunction");
    }

    [Fact]
    public void Extract_AllPatternLanguages_IsDeterministicUnderParallelCalls()
    {
        var samples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["assembly"] = "Start:\n    call Target\nTarget:\n    ret\n",
            ["batch"] = ":run\necho ok\n",
            ["c"] = "int answer(void) { return 42; }\n",
            ["cobol"] = "       IDENTIFICATION DIVISION.\n       PROGRAM-ID. HELLO.\n",
            ["cpp"] = "namespace demo { int answer() { return 42; } }\n",
            ["csharp"] = "namespace Demo; public class Service { public int Run() => 1; }\n",
            ["css"] = ".card { color: red; }\n@keyframes fade { from { opacity: 0; } }\n",
            ["dart"] = "class Service { int run() => 1; }\n",
            ["dockerfile"] = "FROM alpine AS build\nARG VERSION=1\n",
            ["elixir"] = "defmodule Demo do\n  def run do\n    :ok\n  end\nend\n",
            ["fortran"] = "module demo\ncontains\nsubroutine run()\nend subroutine\nend module\n",
            ["fsharp"] = "module Demo\nlet run x = x + 1\n",
            ["go"] = "package demo\nfunc Run() int { return 1 }\n",
            ["gradle"] = "task buildDocs {\n}\n",
            ["graphql"] = "type Query { answer: Int }\nquery GetAnswer { answer }\n",
            ["haskell"] = "module Demo where\nrun :: Int -> Int\nrun x = x + 1\n",
            ["html"] = "<div id=\"app\"></div>\n<script>function run() { return 1; }</script>\n",
            ["java"] = "package demo; public class Service { int run() { return 1; } }\n",
            ["javascript"] = "export function run() { return 1; }\n",
            ["kotlin"] = "package demo\nclass Service { fun run(): Int = 1 }\n",
            ["lua"] = "local function run()\n  return 1\nend\n",
            ["makefile"] = "build:\n\t@echo build\n",
            ["objc"] = "@interface Service\n- (void)run;\n@end\n",
            ["pascal"] = "unit Demo;\ninterface\nprocedure Run;\nimplementation\nprocedure Run; begin end;\nend.\n",
            ["perl"] = "package Demo;\nsub run { return 1; }\n",
            ["php"] = "<?php\nclass Service { function run() { return 1; } }\n",
            ["powershell"] = "function Invoke-Demo { return 1 }\n",
            ["protobuf"] = "message User { string name = 1; }\nservice Users { rpc Get(User) returns (User); }\n",
            ["python"] = "class Service:\n    def run(self):\n        return 1\n",
            ["r"] = "run <- function(x) {\n  x + 1\n}\n",
            ["racket"] = "(define (run x) x)\n",
            ["ruby"] = "class Service\n  def run\n    1\n  end\nend\n",
            ["rust"] = "pub struct Service;\npub fn run() -> i32 { 1 }\n",
            ["scala"] = "class Service {\n  def run(): Int = 1\n}\n",
            ["shell"] = "run() {\n  echo ok\n}\n",
            ["smalltalk"] = "Object subclass: #Service\nService >> run\n  ^1\n",
            ["sql"] = "CREATE TABLE users (id int);\nCREATE PROCEDURE run AS SELECT 1;\n",
            ["svelte"] = "<script>\n  export let name;\n  function run() { return name; }\n</script>\n",
            ["swift"] = "class Service { func run() -> Int { 1 } }\n",
            ["terraform"] = "resource \"local_file\" \"demo\" {\n  filename = \"demo.txt\"\n}\n",
            ["typescript"] = "export class Service { run(): number { return 1; } }\n",
            ["vb"] = "Public Class Service\n  Public Sub Run()\n  End Sub\nEnd Class\n",
            ["vue"] = "<script setup lang=\"ts\">\nfunction run() { return 1 }\n</script>\n",
            ["zig"] = "pub fn run() i32 { return 1; }\n",
        };

        var patternLanguages = GetSymbolExtractorPatternLanguages();
        Assert.Empty(patternLanguages.Except(samples.Keys, StringComparer.Ordinal));

        foreach (var (language, content) in samples)
        {
            var expectedJson = JsonSerializer.Serialize(SymbolExtractor.Extract(1, language, content));

            Parallel.For(0, 100, _ =>
            {
                var actualJson = JsonSerializer.Serialize(SymbolExtractor.Extract(1, language, content));
                Assert.Equal(expectedJson, actualJson);
            });
        }
    }

    private static IReadOnlyCollection<string> GetSymbolExtractorPatternLanguages()
    {
        var field = typeof(SymbolExtractor).GetField("PatternCache", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var patternCache = Assert.IsAssignableFrom<IDictionary>(field.GetValue(null));
        return patternCache.Keys.Cast<string>().Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class ToyDslSymbolExtractor : ISymbolExtractor
    {
        public string Language => "toydsl";

        public IReadOnlyCollection<string> FileExtensions => [".toy"];

        public IReadOnlyList<SymbolRecord> Extract(long fileId, string source, ExtractionContext context)
        {
            var name = source.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last();
            return
            [
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "class",
                    Name = name,
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                    Signature = source.Trim(),
                },
            ];
        }
    }




































    [Fact]
    public void Extract_Assembly_DetectsLabelsSectionsDirectivesAndConstants()
    {
        const string content = """
            ; fake_label: should stay a comment
            section .text
            global _start
            extern printf
            %include "runtime.inc"
            #include "config.inc"
            %define BUFFER_SIZE 64
            #define PAGE_SIZE 4096
            TABLE_SIZE = 128

            print_line MACRO msg
            ENDM

            _start:
                call printf
                jmp .done
            .loop:
                bl helper
                bne .loop
            .done:
                ret

            helper PROC
                ret
            helper ENDP

            section .data
            message: db "hello;not-comment", 0
            .section .note.GNU-stack,"",@progbits
            """;

        var symbols = SymbolExtractor.Extract(1, "assembly", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == ".text");
        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == ".data");
        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == ".note.GNU-stack");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "_start" && symbol.ContainerName == ".text");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == ".loop");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == ".done");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "helper");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "print_line");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "message" && symbol.ContainerName == ".data");
        Assert.Contains(symbols, symbol => symbol.Kind == "import" && symbol.Name == "printf");
        Assert.Contains(symbols, symbol => symbol.Kind == "import" && symbol.Name == "runtime.inc");
        Assert.Contains(symbols, symbol => symbol.Kind == "import" && symbol.Name == "config.inc");
        Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "BUFFER_SIZE");
        Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "PAGE_SIZE");
        Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "TABLE_SIZE");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "fake_label");

        var helper = Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "helper"));
        var dataSection = Assert.Single(symbols.Where(symbol => symbol.Kind == "namespace" && symbol.Name == ".data"));
        Assert.Equal(dataSection.StartLine - 1, helper.BodyEndLine);
    }

    [Fact]
    public void Extract_Protobuf_DetectsEnumPackageOneofExtendAndService()
    {
        var content = """
            syntax = "proto3";

            package google.api;

            message IssueDetails {
              enum Severity {
                SEVERITY_UNSPECIFIED = 0;
                DEPRECATION = 1;
              }

              oneof kind {
                string name = 1;
                int32 code = 2;
              }
            }

            extend google.protobuf.FieldOptions {
              string custom = 50001;
            }

            service Greeter {
              rpc SayHello (HelloRequest) returns (HelloReply);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "protobuf", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "google.api");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "IssueDetails");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Severity" && s.ContainerName == "IssueDetails");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "kind" && s.ContainerName == "IssueDetails");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "google.protobuf.FieldOptions");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Greeter");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "SayHello");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Severity" && s.ContainerName == "IssueDetails");
    }

    [Fact]
    public void Extract_JavaScript_DetectsFunctionsAndClasses()
    {
        // Should detect exported and non-exported functions and classes
        // export有無にかかわらず関数とクラスを検出する
        var content = "export function login() {}\nclass AuthService {}\nimport React from 'react'";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "login");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "AuthService");
        Assert.Contains(symbols, s => s.Kind == "import");
    }

    [Fact]
    public void Extract_SqlProcedure_WithEscapedBracketIdentifier_IsDetected()
    {
        var content = """
            CREATE PROCEDURE [dbo].[proc]]name]
            AS
            SELECT 1;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name.Contains("proc", StringComparison.OrdinalIgnoreCase)
            && s.Name.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_SqlGeneratedColumns_DetectsColumnSymbols()
    {
        var content = """
            CREATE TABLE dbo.Orders (
                subtotal int,
                tax int,
                total int GENERATED ALWAYS AS (subtotal + tax) STORED,
                invoice_no int DEFAULT NEXT VALUE FOR billing.invoice_seq,
                created_at timestamp DEFAULT CURRENT_TIMESTAMP
            );
            ALTER TABLE dbo.Orders ADD COLUMN net_total int GENERATED ALWAYS AS (total - tax) STORED;
            ALTER TABLE dbo.Orders ADD computed_total AS (subtotal + tax) PERSISTED;
            ALTER TABLE dbo.Orders ADD CONSTRAINT df_orders_created DEFAULT 0 FOR created_at;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.SubKind == "generated_column"
            && s.Name == "total"
            && s.ContainerName == "Orders");
        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.SubKind == "generated_column"
            && s.Name == "invoice_no"
            && s.ContainerName == "Orders");
        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.SubKind == "generated_column"
            && s.Name == "net_total"
            && s.ContainerName == "Orders");
        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.SubKind == "generated_column"
            && s.Name == "computed_total"
            && s.ContainerName == "Orders");
        Assert.DoesNotContain(symbols, s =>
            s.Kind == "property"
            && s.SubKind == "generated_column"
            && s.Name == "created_at");
        Assert.DoesNotContain(symbols, s =>
            s.Kind == "property"
            && s.SubKind == "generated_column"
            && s.Name == "CONSTRAINT");
    }

    [Fact]
    public void Extract_CobolProgramId_DetectsProgramSymbol()
    {
        const string content = """
            IDENTIFICATION DIVISION.
            PROGRAM-ID. hello-world.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                ENTRY "ALT-ENTRY".
                PERFORM HELPER-SECTION
                PERFORM HELPER-PARA THRU EXIT-PARA
                STOP RUN.
            HELPER-SECTION SECTION.
            HELPER-PARA.
                DISPLAY "A".
            MIDDLE-PARA.
                DISPLAY "B".
            EXIT-PARA.
                DISPLAY "C".
            END PROGRAM hello-world.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "HELLO-WORLD");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "ALT-ENTRY");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "MAIN-SECTION");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "HELPER-SECTION");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "HELPER-PARA");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "MIDDLE-PARA");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "EXIT-PARA");
        Assert.Contains(SymbolExtractor.GetSupportedLanguages(), lang => lang == "cobol");
    }

    [Fact]
    public void Extract_CobolClassId_DetectsClassSymbolAndContainers()
    {
        const string content = """
            IDENTIFICATION DIVISION.
            CLASS-ID. customer-service.
            PROCEDURE DIVISION.
            MAIN-SECTION SECTION.
                DISPLAY "A".
            END CLASS customer-service.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "CUSTOMER-SERVICE");
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "MAIN-SECTION"
            && symbol.ContainerName == "CUSTOMER-SERVICE");
    }

    [Fact]
    public void Extract_CobolMethodId_DetectsFunctionSymbol()
    {
        const string content = """
            IDENTIFICATION DIVISION.
            CLASS-ID. customer-service.
            METHOD-ID. "load-customer".
            PROCEDURE DIVISION.
                DISPLAY "A".
            END METHOD load-customer.
            END CLASS customer-service.
            """;

        var symbols = SymbolExtractor.Extract(1, "cobol", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "LOAD-CUSTOMER");
    }

    [Fact]
    public void Extract_VueScriptSetup_DetectsTypeScriptSymbols()
    {
        const string content = """
            <script setup lang="ts">
            import { computed, ref } from "vue";

            const count = ref(0);
            const doubled = computed(() => count.value * 2);

            function increment() {
                count.value++;
            }
            </script>

            <template>
                <button @click="increment">{{ count }} / {{ doubled }}</button>
            </template>
            """;

        var symbols = SymbolExtractor.Extract(1, "vue", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "increment");
        Assert.Contains(symbols, symbol => symbol.Kind == "import" && symbol.Name.Contains("computed", StringComparison.Ordinal));
        Assert.Contains(SymbolExtractor.GetSupportedLanguages(), lang => lang == "vue");
    }

    [Fact]
    public void Extract_SvelteScript_DetectsTypeScriptSymbolsAndReactiveProperty()
    {
        const string content = """
            <script lang="ts">
                let count = 0;
                $: doubled = count * 2;

                function increment() {
                    count++;
                }
            </script>

            <button on:click={increment}>{count} / {doubled}</button>
            """;

        var symbols = SymbolExtractor.Extract(1, "svelte", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "increment");
        Assert.Contains(symbols, symbol => symbol.Kind == "property" && symbol.Name == "doubled");
        Assert.Contains(SymbolExtractor.GetSupportedLanguages(), lang => lang == "svelte");
    }




    [Fact]
    public void Extract_Cpp_DetectsQualifiedDefinitionsConceptsAndModules()
    {
        var content = """
            export module my_module;

            inline namespace v2 {}
            namespace outer::inner {
                class Nested {};
            }

            template <typename T>
            concept Addable = requires(T a, T b) { a + b; };

            class Foo {
            public:
                Foo();
                ~Foo();
                void bar();
                Foo& operator=(const Foo&);
                operator int() const;
                static int counter;
            };

            Foo::Foo() {}
            Foo::~Foo() {}
            void Foo::bar() {}
            Foo& Foo::operator=(const Foo&) { return *this; }
            Foo::operator int() const { return 0; }
            int Foo::counter = 0;

            template <typename T>
            T add(T a, T b) { return a + b; }

            template <>
            int add<int>(int a, int b) { return a + b + 1; }

            class Outer {
            public:
                class Inner {
                public:
                    void method();
                };
            };

            void Outer::Inner::method() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "my_module");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "v2");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "outer::inner");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Addable");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Nested" && s.ContainerName == "outer::inner");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "~Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator=");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "counter");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "int");
    }

    [Fact]
    public void Extract_Cpp_DetectsUsingTypeAliases()
    {
        // C++: using 型エイリアス / C++: using type aliases
        var content = """
            using StringList = std::vector<std::string>;
            template <typename T> using Ptr = std::unique_ptr<T>;
            using enum colors::Mode;

            namespace demo {
                using namespace std; // comment to ignore
                using typename Base::value_type;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "StringList");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Ptr");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "colors::Mode");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Base::value_type" && s.ContainerName == "demo");
    }

    [Fact]
    public void Extract_Cpp_DetectsUsingDeclarations()
    {
        var content = """
            namespace demo {
                using ns::Type;
                using std::size_t; // comment to ignore
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ns::Type" && s.ContainerName == "demo");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::size_t" && s.ContainerName == "demo");
    }

    [Fact]
    public void Extract_Cpp_DetectsModuleImports()
    {
        var content = """
            export import std;
            import std.compat;
            import :partition;
            import <vector>;
            import "detail/config.hpp";
            #import <UIKit/UIKit.h>
            # import "objc/Bridge.h"
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std.compat");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == ":partition");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "vector");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "detail/config.hpp");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "UIKit/UIKit.h");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "objc/Bridge.h");
    }

    [Fact]
    public void Extract_Cpp_DetectsModulePartitionDeclarations()
    {
        var content = """
            export module app.core:api;
            module app.impl:detail;
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "app.core:api");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "app.impl:detail");
    }

    [Fact]
    public void Extract_Cpp_DetectsTemplateTypeDeclarationsOnOneLine()
    {
        var content = """
            template <typename T> class Box {};
            template <class T> struct Slot {};
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Box");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Slot");
    }

    [Fact]
    public void Extract_Cpp_DetectsExportedTypeDeclarations()
    {
        var content = """
            export class Api {};
            export template <typename T> struct ApiBox {};
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Api");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "ApiBox");
    }

    [Fact]
    public void Extract_Cpp_DetectsFriendDeclarations()
    {
        var content = """
            class Widget {
              friend class Inspector;
              friend struct ns::Peer;
              friend void freeFn(Widget&);
              friend bool operator==(const Widget&, const Widget&);
              template <typename U> friend class Container;
              // friend class CommentOnly;
              const char* text = "friend class StringOnly;";
            };

            template <typename T>
            class Box {
              friend class BoxInspector;
            };

            class Outer {
              class Inner {
                friend class Outer::Probe;
              };
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inspector" && s.Signature == "friend class Inspector;");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Peer" && s.Signature == "friend struct ns::Peer;");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "freeFn" && s.Signature == "friend void freeFn(Widget&);");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator==" && s.Signature == "friend bool operator==(const Widget&, const Widget&);");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Container" && s.Signature == "template <typename U> friend class Container;");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "BoxInspector");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Probe" && s.Signature == "friend class Outer::Probe;");
        Assert.DoesNotContain(symbols, s => s.Name == "CommentOnly");
        Assert.DoesNotContain(symbols, s => s.Name == "StringOnly");
    }

    [Fact]
    public void Extract_Cpp_DetectsUnionDeclarations()
    {
        var content = """
            union Value { int number; float real; };
            export template <typename T> union Tagged { T value; };
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "union" && s.Name == "Value");
        Assert.Contains(symbols, s => s.Kind == "union" && s.Name == "Tagged");
    }

    [Fact]
    public void Extract_Cpp_DetectsExportedEnumDeclarations()
    {
        var content = """
            export enum class Mode { Read, Write };
            export enum Status { Ok, Failed };
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
    }

    [Fact]
    public void Extract_Cpp_DetectsExportedNamespaces()
    {
        var content = """
            export namespace api {
                class Service {};
            }
            export namespace outer::inner {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "api");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "outer::inner");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.ContainerName == "api");
    }

    [Fact]
    public void Extract_Cpp_DetectsConstexprConstants()
    {
        var content = """
            inline constexpr int kMaxConnections = 8;
            constexpr std::size_t BUFFER_SIZE = 4096;
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "kMaxConnections" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "BUFFER_SIZE" && s.ReturnType == "std::size_t");
    }

    [Fact]
    public void Extract_Cpp_DetectsNamespaceLocalUsingAlias()
    {
        // C++: namespace-local using aliases / C++: 名前空間ローカル using エイリアス
        var content = """
            namespace demo {
                using DemoValue = long;
                using namespace std; /* comment to ignore */
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "DemoValue");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std");
    }

    [Fact]
    public void Extract_Cpp_DetectsTypedefAliases()
    {
        // C++: typedef aliases / C++: typedef エイリアス
        var content = """
            namespace demo {
                typedef long DemoLength;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "DemoLength");
    }

    [Fact]
    public void Extract_JavaScript_DetectsQualifiedAssignmentsAndObjectLiteralFunctionValues()
    {
        var content = """
            function Vehicle(make) { this.make = make; }
            Vehicle.prototype.start = function start() { return this.make; };
            Vehicle.factory = function factory() { return new Vehicle("default"); };
            Vehicle.VERSION = "1.0";

            var Foo = {};
            Foo.Bar = class {
                method() { return 1; }
            };

            var MyNS = { utils: {} };
            MyNS.utils.parse = function parse(s) { return s; };

            var add = function add(a, b) { return a + b; };

            const handlers = {
                onClick() { return true; },
                onSubmit: function submitHandler() { return true; },
                onClose: () => { return true; }
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Vehicle.prototype.start");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Vehicle.factory");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Vehicle.VERSION");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Foo.Bar");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Foo.Bar");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MyNS.utils.parse");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "onClick" && s.ContainerName == "handlers");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "onSubmit" && s.ContainerName == "handlers");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "onClose" && s.ContainerName == "handlers");
    }

    [Fact]
    public void Extract_JavaScript_DetectsHocWrappedComponentBindings()
    {
        // HOC-wrapped / call-result / tagged-template component bindings must not be
        // silently dropped — every PascalCase `const Name = <non-arrow RHS>` shape
        // should produce a `function` symbol so that `definition`, `callers`,
        // `inspect`, and default exports can resolve the name. Closes #240.
        // React.memo / React.forwardRef / React.lazy / connect(...)(...) /
        // styled.div`...` / withAuth(Home) のような HOC ラップと呼び出し結果・
        // タグ付きテンプレート代入の束縛が漏れないことを確認する。どの PascalCase
        // 名 `const Name = <非 arrow の RHS>` も `function` シンボルになることで、
        // `definition` / `callers` / `inspect` と default export が名前解決できる。
        // Closes #240.
        var content = """
            import React from 'react';

            const Box = React.forwardRef(function Box(props, ref) {
                return React.createElement('div', { ref }, props.children);
            });

            const Expensive = React.memo(function Expensive({ data }) {
                return React.createElement('div', null, data);
            });

            const Wrapped = React.memo(() => React.createElement('div', null, 'arrow'));

            const Lazy = React.lazy(() => import('./X'));

            const Connected = connect(mapState)(MyComponent);

            const Styled = styled.div`color: red`;

            const WithAuth = withAuthentication(Home);
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Box");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Expensive");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Wrapped");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Lazy");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Connected");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Styled");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "WithAuth");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternSkipsLowercaseBindings()
    {
        // Lowercase names are NOT captured by the HOC/call-result binding row so
        // ordinary non-component constants (`const count = 5;`, `const total = sum(a, b);`)
        // do not gain phantom `function` rows. The capitalization gate also keeps
        // every non-arrow ordinary constant out of the symbol table; only the
        // PascalCase shape — which matches the React / component naming
        // convention — is surfaced. Closes #240.
        // 小文字始まり名は HOC / 呼び出し結果束縛パターンで取り込まれない。通常の
        // 非コンポーネント定数（`const count = 5;`、`const total = sum(a, b);`）に
        // 架空の `function` 行が生えないことを確認する。大文字始まりのゲートにより、
        // React / コンポーネント命名規則に沿う PascalCase 形だけがシンボルテーブル
        // に出る。Closes #240.
        var content = """
            const count = 5;
            const total = sum(a, b);
            const config = loadConfig();
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "count");
        Assert.DoesNotContain(symbols, s => s.Name == "total");
        Assert.DoesNotContain(symbols, s => s.Name == "config");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternDoesNotShadowCapitalizedArrow()
    {
        // A capitalized arrow-function binding (`const Foo = () => <div/>`) still
        // matches the pre-existing arrow pattern, which runs FIRST and sets the
        // same-line stop flag. The new HOC row must not produce a second,
        // BodyStyle.None duplicate for the same symbol — the arrow row already
        // captures the function with a brace body range. Closes #240.
        // 大文字始まりの arrow 関数束縛（`const Foo = () => <div/>`）は既存 arrow
        // パターンに先行一致し、同一行の stop flag が立つ。今回の HOC 行で重複
        // シンボル（BodyStyle.None の `function Foo`）を追加で生やしてはいけない —
        // arrow 行がすでに本体範囲つきで捕捉している。Closes #240.
        var content = """
            const Foo = () => {
                return 1;
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        // Assert.Single already guarantees no duplicate was emitted by the new HOC
        // row on the same line — the arrow row still wins via stopAfterFirstPatternMatch.
        // Assert.Single で、新 HOC 行が同一行に重複シンボルを生やしていないことを保証する。
        // arrow 行が stopAfterFirstPatternMatch で先勝ちしている。
        var foo = Assert.Single(symbols.Where(s => s.Name == "Foo"));
        Assert.Equal("lambda", foo.Kind);
        // Arrow pattern uses BodyStyle.Brace, so EndLine is advanced past StartLine
        // when the body spans multiple lines. The HOC row (BodyStyle.None) would
        // leave EndLine equal to StartLine; a strictly greater end line proves the
        // arrow row won.
        // arrow パターンは BodyStyle.Brace なので、本体が複数行の場合 EndLine は
        // StartLine より後まで伸びる。HOC 行（BodyStyle.None）は EndLine を
        // StartLine のまま残すため、StartLine より大きい EndLine は arrow 行が
        // 勝ったことの証拠になる。
        Assert.True(foo.EndLine > foo.StartLine);
    }

    [Fact]
    public void Extract_TypeScript_DetectsHocWrappedComponentBindingsWithTypeAnnotation()
    {
        // TypeScript HOC bindings frequently carry an explicit type annotation
        // between the name and `=`; the TypeScript row's optional `:` branch
        // must consume the annotation so the name is still captured. Closes #240.
        // TypeScript の HOC 束縛では名前と `=` の間に型注釈が入ることが多い。
        // TypeScript 行のオプションの `:` 分岐が型注釈を消費し、名前が正しく
        // 取得できることを確認する。Closes #240.
        var content = """
            import React from 'react';

            const Connected: React.ComponentType<Props> = connect(mapState)(MyComponent);

            const Styled: StyledComponent<'div', Theme> = styled.div`color: red`;

            const Callback: (x: number) => number = (x) => x + 1;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Connected");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Styled");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "Callback");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternAcceptsGenericTypeArgumentsOnReactHoc()
    {
        // TypeScript HOCs very frequently carry type arguments directly on the
        // HOC call itself — `React.forwardRef<HTMLDivElement, Props>(...)`,
        // `React.memo<Props>(...)`, `React.lazy<typeof X>(...)`, and the same
        // shape on bare `forwardRef<T>(...)` / `memo<T>(...)` / `lazy<T>(...)` /
        // `connect<State, Dispatch>(...)` / `observer<Props>(...)` / any
        // `with<Pascal><T>(...)` call. The narrow HOC allowlist must still
        // accept them; the earlier revision dropped every generic shape because
        // the `<...>` tokens pushed the `(` away from the HOC name. Closes #240.
        // TypeScript の HOC には HOC 呼び出し自身に型引数が付く形が非常に多い
        // — `React.forwardRef<HTMLDivElement, Props>(...)`、`React.memo<Props>(...)`、
        // `React.lazy<typeof X>(...)`、素の `forwardRef<T>(...)` /
        // `memo<T>(...)` / `lazy<T>(...)` / `connect<State, Dispatch>(...)` /
        // `observer<Props>(...)` / `with<Pascal><T>(...)` のいずれも同じ形。
        // narrow な HOC allowlist でこの形を落としてはいけない。以前のリビジョンは
        // `<...>` トークンが `(` を HOC 名から離してしまい、generic 形が全部
        // 落ちていた。Closes #240.
        var content = """
            import React from 'react';

            const Box = React.forwardRef<HTMLDivElement, Props>((props, ref) => <div ref={ref} />);
            const MemoBox = React.memo<Props>(Box);
            const LazyBox = React.lazy<typeof Box>(() => import('./Box'));

            const BareBox = forwardRef<HTMLDivElement, Props>((props, ref) => <div ref={ref} />);
            const BareMemo = memo<Props>(BareBox);
            const BareLazy = lazy<typeof BareBox>(() => import('./BareBox'));

            const ConnectedGeneric = connect<State, Dispatch>(mapState, mapDispatch)(MyComponent);
            const Observed = observer<Props>(MyComponent);
            const WithAuthGeneric = withAuthentication<Props>(Home);

            const NestedMemo = React.memo<Map<string, Props>>(Box);
            const FunctionTypeMemo = React.memo<(props: Props) => JSX.Element>(Box);
            const DeepNestedMemo = React.memo<Record<string, Map<string, Props>>>(Box);
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Box");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MemoBox");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "LazyBox");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BareBox");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BareMemo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BareLazy");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ConnectedGeneric");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Observed");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "WithAuthGeneric");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NestedMemo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "FunctionTypeMemo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "DeepNestedMemo");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAmbientDeclarationsTypeAliasesAndDecoratedClassMembers()
    {
        var content = """
            type Handler = (e: Event) => void;
            type Coord = { x: number; y: number };

            declare function globalHelper(x: number): number;
            declare const VERSION: string;

            declare module "ext" {
                export function loadExt(): void;
            }

            class Api {
                @log fetch() {}
                @readonly name = "x";
            }

            function log(_: any, _k: string) {}
            function readonly(_: any, _k: string) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Coord");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "globalHelper");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "loadExt" && s.ContainerName == "ext");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Api");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch" && s.ContainerName == "Api");
    }

    [Fact]
    public void Extract_TypeScript_DetectsImportEqualsAliasesAndTrailingComments()
    {
        var content = """
            import nodeFs = require('fs'); // keep trailing comments
            import aliasPath = require("./lib/util") /* keep block comments */
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var imports = symbols.Where(symbol => symbol.Kind == "import").ToList();

        Assert.Contains(imports, symbol => symbol.Name == "nodeFs");
        Assert.Contains(imports, symbol => symbol.Name == "fs");
        Assert.Contains(imports, symbol => symbol.Name == "aliasPath");
        Assert.Contains(imports, symbol => symbol.Name == "./lib/util");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInterfaceAbstractMembersAndTypedArrowAssignments()
    {
        var content = """
            interface User {
              id: string;
              name: string;
              readonly age: number;
              login(password: string): Promise<boolean>;
              logout(): void;
            }

            interface Callback<T> {
              (data: T): void;
              timeout?: number;
            }

            abstract class Base {
              abstract compute(): number;
              abstract readonly count: number;
              protected ready(): boolean { return true; }
            }

            interface Props { title: string; }
            const Header: React.FC<Props> = ({ title }) => <h1>{title}</h1>;
            const add: (a: number, b: number) => number = (a, b) => a + b;
            const handler: MouseEventHandler = (e) => { e.preventDefault(); };
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Base");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "id" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "age" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "login" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "logout" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "compute" && s.ContainerName == "Base");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "count" && s.ContainerName == "Base");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "Header");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "handler");
    }

    [Fact]
    public void Extract_TypeScript_CollapsesFunctionOverloadsIntoImplementationRow()
    {
        var content = """
            function format(x: number): string;
            function format(x: string): string;
            function format(x: number | string): string {
                return String(x);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);
        var overloads = symbols.Where(s => s.Kind == "function" && s.Name == "format").ToList();

        var implementation = Assert.Single(overloads);
        Assert.NotNull(implementation.BodyStartLine);
        Assert.NotNull(implementation.BodyEndLine);
        Assert.True(implementation.EndLine > implementation.StartLine);
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternSkipsPascalCaseNonHocConstants()
    {
        // PascalCase bindings whose RHS is NOT a known HOC prefix must not be
        // silently promoted to `function` symbols — that would create phantom
        // symbol rows and pollute `definition`, `symbols`, and `inspect` output.
        // The narrow HOC-prefix gate (React.memo/React.forwardRef/React.lazy
        // only — bare `React.` is NOT accepted — styled/connect/memo/forwardRef/
        // lazy/observer/with<Pascal>) intentionally rejects ordinary constants,
        // ALL_CAPS config values, and arbitrary call results. Closes #240.
        // RHS が既知の HOC プレフィックスでない PascalCase / ALL_CAPS 束縛は
        // `function` シンボルに昇格させてはいけない — 架空のシンボル行が出ると
        // `definition` / `symbols` / `inspect` が汚染される。狭い HOC プレフィックス
        // ゲート（`React.memo` / `React.forwardRef` / `React.lazy` のみで、素の
        // `React.` は受け付けない。`styled` / `connect` / `memo` / `forwardRef` /
        // `lazy` / `observer` / `with<Pascal>`）で通常定数、ALL_CAPS 設定値、任意の
        // 呼び出し結果を意図的に弾く。Closes #240.
        var content = """
            const Config = loadConfig();
            const ENV = process.env.NODE_ENV;
            const API = 'https://example.com';
            const Total = sum(a, b);
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Config");
        Assert.DoesNotContain(symbols, s => s.Name == "ENV");
        Assert.DoesNotContain(symbols, s => s.Name == "API");
        Assert.DoesNotContain(symbols, s => s.Name == "Total");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternDoesNotCollideWithClassExpressionBinding()
    {
        // `const Widget = class extends React.Component {}` is a class-expression
        // binding. It must produce a single `class Widget` symbol (from the
        // synthetic class-expression pass), NOT both `function Widget` (from the
        // HOC row) and `class Widget`. The narrow HOC-prefix regex excludes
        // `= class` so the two passes do not collide. Closes #240.
        // `const Widget = class extends React.Component {}` はクラス式束縛。
        // `class Widget`（class expression 合成パス由来）だけが出るべきで、
        // `function Widget`（HOC 行由来）と `class Widget` が二重に出てはいけない。
        // 狭い HOC プレフィックス正規表現は `= class` を受け付けないため、2 つの
        // パスが衝突しない。Closes #240.
        var content = """
            const Widget = class extends React.Component {
                render() { return null; }
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var widgetSymbols = symbols.Where(s => s.Name == "Widget").ToList();
        Assert.Single(widgetSymbols);
        Assert.Equal("class", widgetSymbols[0].Kind);
    }

    [Fact]
    public void Extract_TypeScript_TypedArrowBindingPreservesBraceBody()
    {
        // A TypeScript arrow-function binding with an explicit function-type
        // annotation (`const Callback: (x: number) => number = (x) => {...}`)
        // must match the arrow row (BodyStyle.Brace) and keep its multi-line
        // body range, even though the type annotation itself contains `=>`.
        // The HOC row with BodyStyle.None must NOT shadow it. Closes #240.
        // 関数型注釈付き TypeScript arrow 束縛（`const Callback: (x: number) =>
        // number = (x) => {...}`）は arrow 行（BodyStyle.Brace）に一致し、型注釈に
        // `=>` が含まれていても複数行の本体範囲が維持される必要がある。
        // BodyStyle.None の HOC 行で上書きされてはいけない。Closes #240.
        var content = """
            const Callback: (x: number) => number = (x) => {
                return x + 1;
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var callback = Assert.Single(symbols.Where(s => s.Name == "Callback"));
        Assert.Equal("lambda", callback.Kind);
        // Arrow row (BodyStyle.Brace) pushes EndLine past StartLine for a multi-line body.
        // HOC row (BodyStyle.None) would leave EndLine==StartLine.
        // arrow 行は複数行本体で EndLine を StartLine より後ろへ伸ばす。HOC 行
        // （BodyStyle.None）なら EndLine は StartLine のまま残るため、これで
        // arrow 行が勝ったことを確認できる。
        Assert.True(callback.EndLine > callback.StartLine);
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternSkipsNonHocReactApiCalls()
    {
        // `React.` on the RHS is not a HOC marker on its own — only
        // `React.memo(` / `React.forwardRef(` / `React.lazy(` are real HOCs.
        // Other React APIs (`React.createContext(...)`, hooks like
        // `React.useCallback(...)` / `React.useMemo(...)`, `React.createRef(...)`)
        // return plain values and must NOT produce phantom `function` symbols.
        // Pins the strict allowlist on both JS and TS sides. Closes #240.
        // RHS の `React.` だけでは HOC ではない — 真の HOC は
        // `React.memo(` / `React.forwardRef(` / `React.lazy(` のみ。それ以外の
        // React API（`React.createContext(...)`、`React.useCallback(...)` / `React.useMemo(...)`
        // などの hooks、`React.createRef(...)`）は素の値を返すだけで、phantom
        // `function` シンボルを生やしてはいけない。JS / TS の両行で厳格な allowlist を
        // pin する。Closes #240.
        var jsContent = """
            const Theme = React.createContext(null);
            const Stable = React.useCallback(() => 1, []);
            const Derived = React.useMemo(() => compute(), [dep]);
            const Ref = React.createRef();
            """;

        var jsSymbols = SymbolExtractor.Extract(1, "javascript", jsContent);

        Assert.DoesNotContain(jsSymbols, s => s.Name == "Theme");
        Assert.DoesNotContain(jsSymbols, s => s.Name == "Stable");
        Assert.DoesNotContain(jsSymbols, s => s.Name == "Derived");
        Assert.DoesNotContain(jsSymbols, s => s.Name == "Ref");

        var tsContent = """
            const Theme = React.createContext<string | null>(null);
            const Stable = React.useCallback(() => 1, []);
            const Derived: number = React.useMemo(() => compute(), [dep]);
            const Ref = React.createRef<HTMLDivElement>();
            """;

        var tsSymbols = SymbolExtractor.Extract(2, "typescript", tsContent);

        Assert.DoesNotContain(tsSymbols, s => s.Name == "Theme");
        Assert.DoesNotContain(tsSymbols, s => s.Name == "Stable");
        Assert.DoesNotContain(tsSymbols, s => s.Name == "Derived");
        Assert.DoesNotContain(tsSymbols, s => s.Name == "Ref");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternDoesNotMatchComparisonShape()
    {
        // In JavaScript, `const Result = memo < Props > (Component);` is a chained
        // comparison / call expression — NOT a HOC binding with generic type
        // arguments. The TypeScript HOC row intentionally accepts an optional
        // `<TypeArgs>` token between the HOC call name and `(`, but the
        // JavaScript row must not, because JS has no generic syntax. A regex
        // that shares the generic token between the two rows would produce
        // phantom `function Result` on pure-JS comparison shapes. Pins the
        // asymmetry so `memo < Props >` / `forwardRef < Props >` /
        // `lazy < typeof X >` / `connect < State, Dispatch >` / `observer < Props >` /
        // `withAuth < Props >` in a JS source stay 0-symbol. Closes #240.
        // JavaScript では `const Result = memo < Props > (Component);` は generic 付きの
        // HOC 束縛ではなく、比較・呼び出しの連鎖式である。TypeScript 行は HOC 呼び出し名と
        // `(` の間に `<TypeArgs>` を意図的に受け入れるが、JavaScript 行は受け入れては
        // いけない。JS に generic 構文は無いため、両行で同じ regex を共有すると純粋な
        // JS の比較式から phantom な `function Result` が生えてしまう。非対称性を pin し、
        // JS ソース上の `memo < Props >` / `forwardRef < Props >` /
        // `lazy < typeof X >` / `connect < State, Dispatch >` / `observer < Props >` /
        // `withAuth < Props >` が 0 シンボルのままであることを保証する。Closes #240.
        var content = """
            const Result = memo < Props > (Component);
            const Forwarded = forwardRef < Props > (Component);
            const Lazied = lazy < typeof Component > (Component);
            const Connected = connect < State, Dispatch > (Component);
            const Observed = observer < Props > (Component);
            const WithAuth = withAuthentication < Props > (Home);
            const ReactMemoed = React.memo < Props > (Component);
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Result");
        Assert.DoesNotContain(symbols, s => s.Name == "Forwarded");
        Assert.DoesNotContain(symbols, s => s.Name == "Lazied");
        Assert.DoesNotContain(symbols, s => s.Name == "Connected");
        Assert.DoesNotContain(symbols, s => s.Name == "Observed");
        Assert.DoesNotContain(symbols, s => s.Name == "WithAuth");
        Assert.DoesNotContain(symbols, s => s.Name == "ReactMemoed");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRequiresTaggedTemplateForStyled()
    {
        // The `styled` HOC branch must require a tagged-template backtick on the same
        // line. `const StyledFactory = styled.div;` (factory capture) and
        // `const StyledFactoryCall = styled(Component);` (plain function call) are
        // NOT component bindings — no component is produced on that line — and must
        // stay 0-symbol. Only the tagged-template forms
        // (`styled.div\`...\``, `styled(Component)\`...\``) create a real styled
        // component binding and must still match. Closes #240.
        // `styled` HOC 分岐はタグ付きテンプレートのバッククォートを同一行で必須にする。
        // `const StyledFactory = styled.div;`（factory 捕捉）や
        // `const StyledFactoryCall = styled(Component);`（素の関数呼び出し）はその行で
        // コンポーネントを生成しないため、0 シンボルのままであるべき。
        // タグ付きテンプレート形（`styled.div\`...\``、`styled(Component)\`...\``）のみが
        // 実体のある styled コンポーネント束縛となり、引き続きマッチする。Closes #240.
        var content = """
            const StyledFactory = styled.div;
            const StyledFactoryCall = styled(Component);
            const RealStyled = styled.div`color: red;`;
            const WrappedStyled = styled(Component)`color: blue;`;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactoryCall");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RealStyled");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "WrappedStyled");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRequiresTaggedTemplateForStyled()
    {
        // Same tagged-template requirement on the TypeScript side — the factory
        // capture and plain call shapes must not produce phantom bindings even in
        // TS sources, while real tagged templates (including ones with generic
        // type arguments on `styled.div<Props>\`...\``) still match. Closes #240.
        // TypeScript 側でも同じタグ付きテンプレート要件を適用する — factory 捕捉や素の
        // 関数呼び出し形は TS ソース上でも phantom 束縛を生やさず、タグ付きテンプレート
        // （generic 型引数を伴う `styled.div<Props>\`...\`` も含む）のみが引き続き
        // マッチする。Closes #240.
        var content = """
            const StyledFactory = styled.div;
            const StyledFactoryCall = styled(Component);
            const RealStyled = styled.div`color: red;`;
            const TypedStyled = styled.div<Props>`color: blue;`;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactoryCall");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RealStyled");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TypedStyled");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRejectsSameLineMultiStatementStyledFactory()
    {
        // A factory-capture or plain-call `styled` binding placed on the same
        // source line as an UNRELATED tagged template (a separate statement after
        // `;`) must still be rejected. The gate scans only between the match end
        // and the next `;`, so the backtick in `const note = \`...\`;` does not
        // re-open the gate for `const StyledFactory = styled.div;` earlier on the
        // same line. Closes #240 follow-up (codex review #7 blocker).
        // factory 捕捉 / 素の呼び出し形の `styled` 束縛と、無関係なタグ付きテンプレート
        // （`;` で区切られた別の文）が同じ行に置かれたケースでも除外は働かなければ
        // ならない。ゲートは match 終端から次の `;` までしか見ないので、行後半の
        // `const note = \`...\`;` が行前半の `const StyledFactory = styled.div;` の
        // ゲートを誤って解除しない。Closes #240 follow-up（codex レビュー #7 の
        // blocker 対応）。
        var content = """
            const StyledFactory = styled.div; const note = `not a component`;
            const StyledFactoryCall = styled(Component); const later = `still not a component`;
            const RealStyled = styled.div`color: red;`;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactoryCall");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RealStyled");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRejectsSameLineMultiStatementStyledFactory()
    {
        // Same statement-local gate applies on TypeScript input: a backtick from
        // an unrelated later statement on the same line must not keep a
        // factory-capture / plain-call styled binding alive. Closes #240
        // follow-up (codex review #7 blocker).
        // TypeScript 側でも同じ statement-local ゲートが必要。同じ行上の無関係な
        // タグ付きテンプレートによるバッククォートが、前方の factory 捕捉 / 素の
        // 呼び出し形 styled 束縛を生かしてはいけない。Closes #240 follow-up
        // （codex レビュー #7 の blocker 対応）。
        var content = """
            const StyledFactory = styled.div; const note = `not a component`;
            const StyledFactoryCall = styled(Component); const later = `still not a component`;
            const TypedStyled = styled.div<Props>`color: blue;`;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledFactoryCall");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TypedStyled");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRejectsPostTemplateComparisonAndDivisionOperators()
    {
        // After a real tagged template, the styled-factory gate must also
        // reject post-template comparison and division operators. Without this
        // guard, `styled.div` bindings can leak through as phantom symbols for
        // `<`, `>`, and `/` continuations. Closes #997.
        // 本物のタグ付きテンプレートの後でも、styled-factory ゲートは比較演算子と
        // 除算演算子を拒否しなければならない。これがないと `styled.div` 束縛が
        // `<`、`>`、`/` の継続として phantom シンボル化する。Closes #997.
        var content = """
            const StyledLess = styled.div`color: red` < theme;
            const StyledGreater = styled.div`color: red` > theme;
            const StyledDivide = styled.div`color: red` / theme;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "StyledLess");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledGreater");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledDivide");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRejectsPostTemplateComparisonAndDivisionOperators()
    {
        // Same post-template operator rejection applies to TypeScript inputs.
        // Comparison and division operators after the closing backtick must not
        // keep a styled binding alive. Closes #997.
        // TypeScript 入力でも同じ post-template 演算子拒否が必要。閉じバッククォート
        // の後に比較演算子・除算演算子があっても styled 束縛を生かしてはならない。
        // Closes #997.
        var content = """
            const StyledLess = styled.div`color: red` < theme;
            const StyledGreater = styled.div`color: red` > theme;
            const StyledDivide = styled.div`color: red` / theme;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "StyledLess");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledGreater");
        Assert.DoesNotContain(symbols, s => s.Name == "StyledDivide");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternIgnoresStyledBacktickInCommentsAndStrings()
    {
        // The statement-local styled-factory gate must understand line comments,
        // block comments, and plain string literals when looking for a backtick
        // or `;`. A backtick or `;` that lives only inside a comment or string
        // must not steer the gate's accept/reject decision. Four failure shapes
        // are pinned:
        //   (a) `// \`...\`` — backtick inside a line comment must not accept
        //       a factory-capture binding.
        //   (b) `/* \`...\` */` — backtick inside a block comment must not
        //       accept a factory-capture binding.
        //   (c) `+ "\`"` — backtick inside a plain string literal must not
        //       accept a non-template binding.
        //   (d) `/* ; */ \`color:red\`;` — `;` inside a block comment must not
        //       fence a real subsequent backtick off from a real tagged
        //       template on the same statement.
        // Closes #240 follow-up (codex review #8 blocker).
        // 文ローカルの styled factory ゲートは、バッククォートや `;` を探索する際に行コメント /
        // ブロックコメント / 通常文字列リテラルを構文として認識する必要がある。コメントや文字列
        // 内のバッククォート・`;` がゲートの判定を誤らせてはならない。(a) 行コメント内の
        // バッククォートで factory 捕捉が維持されない、(b) ブロックコメント内のバッククォートで
        // 維持されない、(c) 文字列リテラル内のバッククォートで維持されない、(d) ブロックコメント
        // 内の `;` によって同一文の本物のバッククォートが文終端で遮られない、の 4 形を pin する。
        // Closes #240 follow-up（codex レビュー #8 の blocker 対応）。
        var content = """
            const LineCommentFactory = styled.div // `not a template`
            const BlockCommentFactory = styled.div /* `not a template` */;
            const StringLiteralFactory = styled.div + "`";
            const RealStyledAfterBlock = styled.div /* ; */ `color:red`;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "LineCommentFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "BlockCommentFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "StringLiteralFactory");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RealStyledAfterBlock");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternIgnoresStyledBacktickInCommentsAndStrings()
    {
        // Same comment / string awareness on the TypeScript side. Closes #240
        // follow-up (codex review #8 blocker).
        // TypeScript 側でも同じコメント / 文字列対応が必要。Closes #240 follow-up
        // （codex レビュー #8 の blocker 対応）。
        var content = """
            const LineCommentFactory = styled.div // `not a template`
            const BlockCommentFactory = styled.div /* `not a template` */;
            const StringLiteralFactory = styled.div + "`";
            const RealStyledAfterBlock = styled.div<Props> /* ; */ `color:red`;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "LineCommentFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "BlockCommentFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "StringLiteralFactory");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RealStyledAfterBlock");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternAcceptsMultiLineTaggedTemplateContinuation()
    {
        // Prettier / dprint style often places the tagged-template backtick on the
        // line after `styled.div` / `styled(Component)`. The statement-local gate
        // must walk across raw lines within a bounded lookahead window so these
        // bindings still register as function symbols. Implicit ASI must still
        // terminate the scan: `const X = styled.div\nconst Y = 5;` stays rejected
        // because the continuation line begins with a `const` statement starter.
        // Closes #240 follow-up (codex review #9 blocker, upstream issue #901).
        // Prettier / dprint の整形では `styled.div` / `styled(Component)` の次行に
        // タグ付きテンプレートのバッククォートを置くことが多い。文ローカルのゲートは
        // 所定の行数まで改行をまたいで走査し、これらの束縛を function シンボルとして
        // 維持しなければならない。同時に暗黙 ASI による終端は守る必要があり、
        // `const X = styled.div\nconst Y = 5;` は継続行が `const` の文頭キーワードで
        // 始まるため引き続き除外される。Closes #240 follow-up（codex レビュー #9 の
        // blocker 対応、上流 issue #901）。
        var content = """
            const MultiLineMember = styled.div
              `color: red;`;
            const MultiLineCall = styled(Component)
              `color: blue;`;
            const AsiRejectedMember = styled.div
            const AsiFollower = 5;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MultiLineMember");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MultiLineCall");
        Assert.DoesNotContain(symbols, s => s.Name == "AsiRejectedMember");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternAcceptsMultiLineTaggedTemplateContinuation()
    {
        // Same multi-line continuation support on the TypeScript side. The
        // TS HOC row uses the same `styled[.(\`]` branch as JavaScript (no
        // generic suffix on `styled` itself), so the shapes that need to be
        // pinned mirror the JavaScript test. TypeScript-specific binding
        // forms such as an optional `: ComponentType<Props>` type annotation
        // between the name and `=` are also exercised so the gate does not
        // get confused by an identifier that carries a type annotation.
        // Closes #240 follow-up (codex review #9 blocker, upstream issue #901).
        // TypeScript 側でも同じ複数行継続対応が必要。TS の HOC 行は `styled`
        // 自体に generic 接尾辞を載せない `styled[.(\`]` 分岐を JS と共有するため、
        // pin すべき形も JS テストとミラーする。加えて TS 特有の型注釈付き束縛
        // （`const Foo: ComponentType<Props> = styled.div\n\`...\``）も通過
        // させ、ゲートが型注釈付き識別子で混乱しないことを確認する。
        // Closes #240 follow-up（codex レビュー #9 の blocker 対応、上流 issue #901）。
        var content = """
            const MultiLineMember = styled.div
              `color: red;`;
            const MultiLineCall = styled(Component)
              `color: blue;`;
            const MultiLineAnnotated: ComponentType<Props> = styled.div
              `color: green;`;
            const AsiRejectedMember = styled.div
            const AsiFollower: number = 5;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MultiLineMember");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MultiLineCall");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MultiLineAnnotated");
        Assert.DoesNotContain(symbols, s => s.Name == "AsiRejectedMember");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRejectsExpressionStatementWithBacktickOnNextStatement()
    {
        // ASI inserts a `;` between `styled.div` and the next line's expression
        // statement when that statement begins with an identifier / `await` /
        // other non-continuation token. The gate must inspect the first
        // meaningful character of each continuation line: only a backtick
        // (template itself) or `.` (member chain) may continue the expression.
        // `<` is intentionally not whitelisted because `<Foo>...` at statement
        // start is a JSX element (or TS cast), not a tagged-template generic
        // continuation. Closes #240 follow-up (codex review #10 and #11
        // blockers).
        // ASI は `styled.div` の次行が識別子始まり・`await` 始まり等の非継続トークンで
        // 始まる式文のとき、暗黙の `;` を挿入する。ゲートは継続行の最初の実トークンを
        // 見て判定する必要がある — バッククォート（テンプレート自体）か `.`（メンバー
        // チェーン）のみが式を継続可能で、それ以外は新しい文として走査を打ち切る。
        // `<` は JSX 要素 / TS キャストの開始にもなるため意図的に許可しない。
        // Closes #240 follow-up（codex レビュー #10 と #11 の blocker 対応）。
        var content = """
            const IdentifierStartFactory = styled.div
            foo(`not a template`)
            const AwaitStartFactory = styled.div
            await foo(`not a template`)
            const CallExprFactory = styled(Component)
            foo(`not a template`)
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "IdentifierStartFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "AwaitStartFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "CallExprFactory");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRejectsExpressionStatementWithBacktickOnNextStatement()
    {
        // Same ASI-aware continuation rule on the TypeScript side. Closes #240
        // follow-up (codex review #10 blocker, upstream issue #910).
        // TypeScript 側でも同じ ASI 対応の継続ルールが必要。Closes #240 follow-up
        // （codex レビュー #10 の blocker 対応、上流 issue #910）。
        var content = """
            const IdentifierStartFactory = styled.div
            foo(`not a template`)
            const AwaitStartFactory = styled.div
            await foo(`not a template`)
            const CallExprFactory = styled(Component)
            foo(`not a template`)
            const AnnotatedIdentifierStart: StyledComponent<'div'> = styled.div
            foo(`not a template`)
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "IdentifierStartFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "AwaitStartFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "CallExprFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "AnnotatedIdentifierStart");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRejectsJsxElementOnNextStatement()
    {
        // A `<Foo>...` continuation is a JSX element (standalone expression
        // statement) — NOT a tagged-template generic — so the styled-factory
        // candidate on the previous line must be rejected even though the
        // JSX element contains a backtick-delimited child. Closes #240
        // follow-up (codex review #11 blocker).
        // 継続行の先頭が `<Foo>...` の場合は JSX 要素（独立した式文）であり
        // tagged-template の generic 継続ではない。JSX 要素の子がバッククォートを
        // 含んでも、前行の styled factory 候補は不採用にする必要がある。
        // Closes #240 follow-up（codex レビュー #11 の blocker 対応）。
        var content = """
            const JsxElementFactory = styled.div
            <Foo>{`not a template`}</Foo>
            const JsxFragmentFactory = styled(Component)
            <><span>{`also not`}</span></>
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "JsxElementFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "JsxFragmentFactory");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRejectsJsxElementOnNextStatement()
    {
        // Same JSX-on-next-statement rejection on the TypeScript/TSX side —
        // `<Foo>...` can also be a TS type cast, and in either reading it is
        // still a new statement inserted by ASI, not a continuation of the
        // preceding styled expression. Closes #240 follow-up (codex review
        // #11 blocker).
        // TypeScript/TSX 側でも同様に `<Foo>...` は JSX 要素か TS キャストであり、
        // どちらの解釈でも ASI で挿入された新しい文であって先行式の継続ではない。
        // Closes #240 follow-up（codex レビュー #11 の blocker 対応）。
        var content = """
            const JsxElementFactory = styled.div
            <Foo>{`not a template`}</Foo>
            const AnnotatedJsxFactory: StyledComponent<'div'> = styled.div
            <Bar>{`also not`}</Bar>
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "JsxElementFactory");
        Assert.DoesNotContain(symbols, s => s.Name == "AnnotatedJsxFactory");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRejectsOperatorBetweenStyledAndTemplate()
    {
        // `styled.div + \`...\`` and `styled(Component) + \`...\`` are NOT
        // tagged-template bindings — the `+` operator at depth 0 between the
        // styled expression and the backtick breaks the tag-head continuation
        // chain. Closes #240 follow-up (codex review #12 blocker). Without the
        // depth-0 operator reject, the gate would happily walk past `+` to
        // the first backtick and accept the candidate as a phantom
        // `function NotStyled` symbol.
        // `styled.div + \`...\`` や `styled(Component) + \`...\`` は tagged-template
        // 束縛ではない — depth 0 の `+` 演算子が styled 式とバッククォートの間に
        // 入ることで tag-head 継続チェーンが切れる。Closes #240 follow-up
        // （codex レビュー #12 の blocker 対応）。depth-0 演算子除外がないと、ゲートが
        // `+` を跨いで最初のバッククォートに到達し phantom `function NotStyled` を
        // 出してしまう。
        var content = """
            const NotStyledMember = styled.div + `not a styled template`;
            const NotStyledCall = styled(Component) + `also not`;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledMember");
        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledCall");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRejectsOperatorBetweenStyledAndTemplate()
    {
        // TypeScript counterpart for the depth-0 operator reject — including
        // a typed-annotation variant — must still drop these phantom bindings.
        // Closes #240 follow-up (codex review #12 blocker).
        // TypeScript 側の depth-0 演算子除外（型注釈付きの変種を含む）。
        // Closes #240 follow-up（codex レビュー #12 の blocker 対応）。
        var content = """
            const NotStyledMember = styled.div + `not a styled template`;
            const NotStyledCall = styled(Component) + `also not`;
            const AnnotatedNotStyled: StyledComponent<'div'> = styled.div + `still not`;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledMember");
        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledCall");
        Assert.DoesNotContain(symbols, s => s.Name == "AnnotatedNotStyled");
    }

    [Fact]
    public void Extract_JavaScript_HocBindingPatternRejectsOperatorAfterStyledTemplate()
    {
        // `styled.div\`color: red\` + theme` is theme composition, not a styled
        // binding — even though the tag-head backtick is present, the depth-0
        // `+` operator after the closing backtick indicates the right-hand side
        // is a binary expression. The gate must walk past the template body and
        // still reject on the post-template operator. Closes #240 follow-up
        // (codex review #13 High blocker).
        // `styled.div\`color: red\` + theme` はテーマ合成式であって styled 束縛では
        // ない — tag head の backtick が存在しても、closing backtick 後の depth 0
        // `+` 演算子により右辺が二項式になっている。ゲートはテンプレート本体を
        // 読み飛ばした後でも post-template operator を検出して除外する必要がある。
        // Closes #240 follow-up（codex レビュー #13 High blocker）。
        var content = """
            const NotStyledPlusTheme = styled.div`color: red` + theme;
            const NotStyledCallPlusTheme = styled(Component)`color: blue` + theme;
            """;

        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledPlusTheme");
        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledCallPlusTheme");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternRejectsOperatorAfterStyledTemplate()
    {
        // TypeScript counterpart for the post-template operator reject —
        // including a typed-annotation variant. Closes #240 follow-up
        // (codex review #13 High blocker).
        // TypeScript 側の post-template 演算子除外（型注釈付き変種を含む）。
        // Closes #240 follow-up（codex レビュー #13 High blocker）。
        var content = """
            const NotStyledPlusTheme = styled.div`color: red` + theme;
            const NotStyledCallPlusTheme = styled(Component)`color: blue` + theme;
            const AnnotatedNotStyledPlusTheme: StyledComponent<'div'> = styled.div`color: red` + theme;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledPlusTheme");
        Assert.DoesNotContain(symbols, s => s.Name == "NotStyledCallPlusTheme");
        Assert.DoesNotContain(symbols, s => s.Name == "AnnotatedNotStyledPlusTheme");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternAcceptsLongAttrsChainBeforeTemplate()
    {
        // Prettier-formatted `.attrs((props) => ({ ... }))` argument objects can
        // span more than ten lines before the backtick is reached. The gate's
        // lookahead window must be large enough so the trailing tagged template
        // is still recognized and the styled binding is kept. Closes #240
        // follow-up (codex review #13 Medium blocker).
        // Prettier 整形の `.attrs((props) => ({ ... }))` 引数オブジェクトは
        // バッククォート到達まで 10 行を超えることがある。lookahead window が
        // 十分広くないと末尾の tagged template を見落とし styled 束縛が落ちる。
        // Closes #240 follow-up（codex レビュー #13 Medium blocker）。
        var content = """
            const Tall = styled.div.attrs((props) => ({
              field1: props.value1,
              field2: props.value2,
              field3: props.value3,
              field4: props.value4,
              field5: props.value5,
              field6: props.value6,
              field7: props.value7,
              field8: props.value8,
              field9: props.value9,
              field10: props.value10,
            }))`
              color: red;
              padding: 8px;
            `;
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Tall");
    }

    [Fact]
    public void Extract_TypeScript_HocBindingPatternAcceptsCallbackPropInsideFunctionTypeGeneric()
    {
        // Inline function-type generic arguments whose parameter object literal
        // carries a callback-prop with ITS OWN paren group
        // (`React.memo<(props: { onClick: (x: number) => void }) => JSX.Element>(Box)`)
        // must still be accepted. The shared TypeScriptOptionalHocTypeArgsPattern
        // now balances one level of nested parens inside each generic-argument paren
        // segment so real React callback-prop shapes do not get dropped. Closes #240.
        // 関数型 generic 引数の中にある引数オブジェクトに、さらに自前の paren を持つ
        // callback-prop が入る形
        // （`React.memo<(props: { onClick: (x: number) => void }) => JSX.Element>(Box)`）
        // もマッチさせる必要がある。共有定数 TypeScriptOptionalHocTypeArgsPattern は、
        // 各 generic 引数内の paren セグメントで 1 段のネスト paren を balance するように
        // なったため、実在する React の callback-prop 形を取りこぼさない。Closes #240.
        var content = """
            import React from 'react';

            const NestedCallbackPropMemo = React.memo<(props: { onClick: (x: number) => void }) => JSX.Element>(Box);
            const BareCallbackPropMemo = memo<(props: { onChange: (value: string) => void }) => JSX.Element>(Box);
            """;

        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NestedCallbackPropMemo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BareCallbackPropMemo");
    }

    [Fact]
    public void Extract_JavaScript_DetectsReExportSurfaceSymbols()
    {
        var content = """
            export * from './util';
            export { foo, bar } from './other'; // trailing comment
            export { default as Helper } from './helper';
            export * /* from './bogus-star' */ as ns from './ns';
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./helper");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Helper");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineStarReExportSurfaceSymbols()
    {
        var content = """
            export *
            from './util';
            export * /* from './bogus-star' */ as ns
            from './ns';
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMinifiedReExportSurfaceSymbols()
    {
        var content = """
            export{foo as bar}from './other';
            export*as ns from './ns';
            export*from './util';
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./ns");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
    }

    [Fact]
    public void Extract_JavaScript_DetectsNamedReExportWhenExportAndSpecifierListAreSplitAcrossLines()
    {
        var content = """
            export
            { foo, bar } from './other';
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsLocalNamedExportSurfaceSymbols(string language)
    {
        var content = """
            const foo = 1;
            const local = 2;
            export { foo, local as renamed };
            export
            {
              foo as multilineFoo,
              local as multilineLocal,
            };
            export { forwarded } from './other';
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "renamed" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "multilineFoo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "multilineLocal" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "forwarded" && s.Visibility == "export");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsExportedVariableSurfaceSymbols(string language)
    {
        var content = """
            export const foo = 1, bar = compute({ value: "," });
            export let baz;
            export var qux = call(1, 2);
            export const
                multilineFoo = 1,
                multilineBar = compute(["x", "y"]);
            export const noSemi = 1
            export const afterNoSemi = 2
            export const fn = () => {};
            export const [skipped] = values;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "multilineFoo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "multilineBar" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "noSemi" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "afterNoSemi" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "fn" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "fn" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "skipped" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_TypeScript_DetectsDeclareExportedVariableSurfaceSymbols()
    {
        var content = """
            export declare const externalThing: string;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "externalThing" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_TypeScript_DetectsLocalTypeOnlyNamedExportSurfaceSymbols()
    {
        var content = """
            type User = { id: string };
            type Admin = User & { role: string };
            export type { User, Admin as RootAdmin };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "User" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "RootAdmin" && s.Visibility == "export");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsStringLiteralExportNames(string language)
    {
        var content = """
            const handler = () => {};
            const other = 1;
            export { handler as "x-api", other as otherName /* keep */ };
            export { remote as "remote-key", another as anotherName } from "./remote";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x-api" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "otherName" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "remote-key" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "anotherName" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "\"x-api\"");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./remote");
    }

    [Fact]
    public void Extract_TypeScript_DetectsNamedAndTypeReExportSurfaceSymbols()
    {
        var content = """
            export {
              foo, // from './bogus'
              bar,
            } from './other';
            export { default as Helper } from './helper'; // trailing comment
            export type {
              User,
              Admin,
            } from './types';
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./helper");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Helper");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Admin");
    }

    [Fact]
    public void Extract_TypeScript_DetectsTypeOnlyStarReExportSurfaceSymbols()
    {
        var content = """
            export type * from './types';
            export type * as ns from './types-ns';
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types-ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
    }

    [Fact]
    public void Extract_TypeScript_DetectsImportEqualsRequireSurfaceSymbols()
    {
        var content = """
            import fs = require('node:fs');
            import path = require('./path-utils');
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "node:fs"));
        Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./path-utils"));
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsRequireModuleSymbols(string language)
    {
        var content = """
            const fs = require("node:fs");
            const helper = require(
              "./helper"
            );
            const method = loader.require("./method");
            const resolved = require.resolve("./resolved");
            const resolvedWithPaths = require.resolve("./with-paths", { paths: [__dirname] });
            const text = "require('./string')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var fsImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "node:fs"));
        Assert.Equal(1, fsImport.Line);
        var helperImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./helper"));
        Assert.Equal(3, helperImport.Line);
        Assert.Contains("require(", helperImport.Signature);
        var resolvedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./resolved"));
        Assert.Equal(6, resolvedImport.Line);
        Assert.Contains("require.resolve", resolvedImport.Signature);
        var resolvedWithPathsImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./with-paths"));
        Assert.Equal(7, resolvedWithPathsImport.Line);
        Assert.Contains("paths", resolvedWithPathsImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsImportMetaResolveModuleSymbols(string language)
    {
        var content = """
            const resolved = import.meta.resolve("./feature.js");
            const scoped = import.meta.resolve(
              "./scoped.js",
              import.meta.url
            );
            client.import.meta.resolve("./method.js");
            const dynamic = import.meta.resolve(path);
            const text = "import.meta.resolve('./string.js')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var resolvedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./feature.js"));
        Assert.Equal(1, resolvedImport.Line);
        Assert.Contains("import.meta.resolve", resolvedImport.Signature);
        var scopedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./scoped.js"));
        Assert.Equal(3, scopedImport.Line);
        Assert.Contains("import.meta.url", scopedImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "path");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsNewUrlImportMetaModuleSymbols(string language)
    {
        var content = """
            const workerUrl = new URL("./worker.js", import.meta.url);
            const imageUrl = new URL(
              "./image.png",
              import.meta.url
            );
            const templated = new URL(`./view.js`, import.meta.url);
            const computed = new URL(`./${name}.js`, import.meta.url);
            const plain = URL("./plain.js", import.meta.url);
            const otherBase = new URL("./other.js", baseUrl);
            const hrefBase = new URL("./href.js", import.meta.url.href);
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var workerImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./worker.js"));
        Assert.Equal(1, workerImport.Line);
        Assert.Contains("new URL", workerImport.Signature);
        var imageImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./image.png"));
        Assert.Equal(3, imageImport.Line);
        Assert.Contains("import.meta.url", imageImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./view.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./plain.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./other.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./href.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsImportScriptsModuleSymbols(string language)
    {
        var content = """
            importScripts("./worker-a.js", "/worker-b.js");
            importScripts(
              "./legacy.js",
              `./template-worker.js`,
              `./${name}.js`
            );
            loader.importScripts("./method.js");
            const text = "importScripts('./string.js')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./worker-a.js" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/worker-b.js" && s.Line == 1);
        var legacyImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./legacy.js"));
        Assert.Equal(3, legacyImport.Line);
        Assert.Contains("importScripts", legacyImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./template-worker.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsServiceWorkerRegisterModuleSymbols(string language)
    {
        var content = """
            navigator.serviceWorker.register("./sw.js");
            navigator.serviceWorker.register(
              "./scoped-sw.js",
              { scope: "./" }
            );
            window.navigator.serviceWorker.register("./window-sw.js");
            globalThis.navigator.serviceWorker.register("./global-sw.js");
            navigator.serviceWorker.register(dynamicPath);
            const text = "navigator.serviceWorker.register('./string-sw.js')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var serviceWorkerImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./sw.js"));
        Assert.Equal(1, serviceWorkerImport.Line);
        Assert.Contains("navigator.serviceWorker.register", serviceWorkerImport.Signature);
        var scopedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./scoped-sw.js"));
        Assert.Equal(3, scopedImport.Line);
        Assert.Contains("scope", scopedImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./window-sw.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./global-sw.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "dynamicPath");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string-sw.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsWorkletAddModuleSymbols(string language)
    {
        var content = """
            audioWorklet.addModule("./audio-processor.js");
            CSS.paintWorklet.addModule(
              "./paint-worklet.js",
              { credentials: "same-origin" }
            );
            layoutWorklet.addModule(`./layout-worklet.js`);
            this.audioWorklet.addModule("./method-audio.js");
            worklet.addModule("./generic-worklet.js");
            audioWorklet.addModule(dynamicPath);
            const text = "audioWorklet.addModule('./string-audio.js')";
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var audioImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./audio-processor.js"));
        Assert.Equal(1, audioImport.Line);
        Assert.Contains("audioWorklet.addModule", audioImport.Signature);
        var paintImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./paint-worklet.js"));
        Assert.Equal(3, paintImport.Line);
        Assert.Contains("credentials", paintImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./layout-worklet.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method-audio.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./generic-worklet.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "dynamicPath");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string-audio.js");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsWorkerConstructorModuleSymbols(string language)
    {
        var content = """
            const worker = new Worker("./worker.js");
            const shared = new SharedWorker(
              "./shared-worker.js",
              { type: "module" }
            );
            const templated = new Worker(`./template-worker.js`, { type: "module" });
            const computed = new Worker(`./${name}.js`);
            const windowWorker = new window.Worker("./window-worker.js");
            const globalShared = new globalThis.SharedWorker("./global-shared-worker.js");
            const plain = Worker("./plain-worker.js");
            const service = new ServiceWorker("./service-worker.js");
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var workerImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./worker.js"));
        Assert.Equal(1, workerImport.Line);
        Assert.Contains("new Worker", workerImport.Signature);
        var sharedImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./shared-worker.js"));
        Assert.Equal(3, sharedImport.Line);
        Assert.Contains("type", sharedImport.Signature);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./template-worker.js");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./window-worker.js");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./global-shared-worker.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./plain-worker.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./service-worker.js");
    }

    [Fact]
    public void Extract_TypeScript_DetectsReExportSurfaceSymbolsWithImportAttributes()
    {
        var content = """
            export * from './util' with { type: 'json' };
            export { foo as bar } from './other' with { type: 'json' };
            export * from './legacy' assert { type: 'json' };
            export { baz as qux } from './older' assert { type: 'json' };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./legacy");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./older");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineStarReExportSurfaceSymbolsWithImportAttributes()
    {
        var content = """
            export * from './util' with {
              type: 'json'
            };
            export * as ns from './other' assert {
              type: 'json'
            };
            export type * from './types' with {
              type: 'json'
            };
            export type * as typeNs from './types-ns' assert {
              type: 'json'
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./util");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types-ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ns");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "typeNs");
    }

    [Fact]
    public void Extract_TypeScript_DetectsNamedReExportSurfaceSymbolsWhenImportAttributeBraceStartsOnNextLine()
    {
        var content = """
            export { foo as bar } from './other' with
            {
              type: 'json'
            };
            export type { User } from './types' assert
            {
              type: 'json'
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./other");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "User");
    }

    [Fact]
    public void Extract_JavaScript_StringBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """"
            export class Example {
              foo() {
                const value = "}";
                return value;
              }

              bar() {
                return 1;
              }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var example = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Example"));
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo"));
        var bar = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "bar"));

        Assert.Equal(10, example.EndLine);
        Assert.Equal(5, foo.EndLine);
        Assert.Equal("class", bar.ContainerKind);
        Assert.Equal("Example", bar.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_TemplateLiteralBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """"
            export class Example {
              foo() {
                const value = `}`;
                return value;
              }

              bar() {
                return 1;
              }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var example = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Example"));
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo"));
        var bar = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "bar"));

        Assert.Equal(10, example.EndLine);
        Assert.Equal(5, foo.EndLine);
        Assert.Equal("class", bar.ContainerKind);
        Assert.Equal("Example", bar.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_TemplateInterpolationBracesStillCountTowardMethodRange()
    {
        var content = """"
            export class Example {
              foo() {
                const value = `${format({ answer: 42 })}`;
                return value;
              }

              bar() {
                return 1;
              }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var example = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Example"));
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo"));
        var bar = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "bar"));

        Assert.Equal(10, example.EndLine);
        Assert.Equal(5, foo.EndLine);
        Assert.Equal("class", bar.ContainerKind);
        Assert.Equal("Example", bar.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsExportDefaultClassMembers()
    {
        var content = """""
            export default class DefaultJs {
                run() {}
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DefaultJs");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotInventExtendsAsAnonymousDefaultClassName()
    {
        var content = """
            export default class extends Base {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotInventExtendsAsAnonymousDefaultDerivedClassName()
    {
        var content = """
            export default class extends mixin(Base) {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassExpressionMethods()
    {
        var content = """
            const Service = class NamedService {
                run() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineExportedClassExpressionMethods()
    {
        var content = """
            export const Service =
                class {
                    run() {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "export const Service = class {");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedClassExpressionMethods()
    {
        var content = "const Service = (class { run() {} });";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineClassMethods()
    {
        var content = "export class Inline { run() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineMultipleMethods()
    {
        var content = "class Inline { first() {} second() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Inline", first.ContainerName);
        Assert.Equal("first() {}", first.Signature);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("Inline", second.ContainerName);
        Assert.Equal("second() {}", second.Signature);
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLineSiblingClassesWithDistinctMethodNames()
    {
        var content = "class A { first() {} } class B { second() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLineSiblingClassesWithIdenticalMethodNames()
    {
        var content = "class A { run() {} } class B { run() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLinePublicClassAfterStatementPrefix()
    {
        var content = "foo(); class Visible { keep() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLinePublicClassAfterFunctionLocalHiddenClass()
    {
        var content = "function outer(){ class Hidden { run() {} } } class Visible { keep() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_JavaScript_DetectsSameLineClassExpressionAfterStatementPrefix()
    {
        var content = "foo(); const Service = class Visible { keep() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "const Service = class Visible { keep() {} }");
    }

    [Fact]
    public void Extract_JavaScript_DetectsStatementPrefixedDefaultExportClassSignatureFromExport()
    {
        var content = "const before = 1; export default (class { run() {} })";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export default (class { run() {} }");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineDefaultExportClassMethods()
    {
        var content = "export default class Inline { run() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineDefaultExportMultipleMethods()
    {
        var content = "export default class { first() {} second() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedDefaultExportClassMembers()
    {
        var content = """
            export default (class {
                run() {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineParenthesizedDefaultExportClassSignature()
    {
        var content = """
            export default
            (
                class {
                    run() {}
                }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export default ( class {");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineModifierNamedMethods()
    {
        var content = "export default class { async() {} static() {} keep() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.Signature == "async() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.Signature == "static() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.Signature == "keep() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineMethodsWithDefaultArguments()
    {
        var content = "class Example { method(x = 1) {} visible() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.Signature == "method(x = 1) {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlinePrivateAndGeneratorMethods()
    {
        var content = "class Example { #hidden() {} *iterator() {} async *stream() {} visible() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.Signature == "#hidden() {}");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.Signature == "*iterator() {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.Signature == "async *stream() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineComputedMethods()
    {
        var content = "class Example { ['computed']() {} [Symbol.iterator]() {} visible() {} }";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.Signature == "['computed']() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "[Symbol.iterator]() {}");
    }

    [Fact]
    public void Extract_JavaScript_PreservesModifierMetadataForComputedMethods()
    {
        var content = """
            class Example {
                async [Symbol.asyncIterator]() {}
                get [Symbol.toStringTag]() {}
                set [key](value) {}
                async *[Symbol.iterator]() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "[Symbol.asyncIterator]" && s.Signature == "async [Symbol.asyncIterator]() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.toStringTag]" && s.Signature == "get [Symbol.toStringTag]() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[key]" && s.Signature == "set [key](value) {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "[Symbol.iterator]" && s.Signature == "async *[Symbol.iterator]() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsQuotedAndNumericLiteralMethodNames()
    {
        var content = """
            class Example {
                "run"() {}
                'stop'() {}
                1() {}
                1.5() {}
                0x10() {}
                1_000() {}
                next() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"run\"" && s.Signature == "\"run\"() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'stop'" && s.Signature == "'stop'() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1" && s.Signature == "1() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1.5" && s.Signature == "1.5() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "0x10" && s.Signature == "0x10() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1_000" && s.Signature == "1_000() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next() {}");
    }

    [Fact]
    public void Extract_JavaScript_DetectsEscapedQuotedLiteralMethodNames()
    {
        var content = """
            class Example {
                "a\"b"() {}
                'c\'d'() {}
                next() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"a\\\"b\"" && s.Signature == "\"a\\\"b\"() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'c\\'d'" && s.Signature == "'c\\'d'() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next() {}");
    }

    [Fact]
    public void Extract_JavaScript_ClassifiesAsyncAndGeneratorFunctionKinds()
    {
        var content = """
            function plain() {}
            async function asyncPlain() {}
            function* generated() {}
            async function* asyncGenerated() {}
            export default async function* () {}
            class Example {
                async method() {}
                *items() {}
                async *stream() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plain");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "asyncPlain");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "asyncGenerated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "default" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "method" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "items" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example");
    }

    [Theory]
    [InlineData(
        """
        class Example {
            handler = function namedHandler() {};
            keep() {}
        }
        """,
        "namedHandler")]
    [InlineData(
        """
        class Example {
            handler = function () {};
            keep() {}
        }
        """,
        "function")]
    [InlineData(
        """
        class Example {
            field = { inner() {} };
            keep() {}
        }
        """,
        "inner")]
    [InlineData(
        """
        class Example {
            field = class Inner { run() {} };
            keep() {}
        }
        """,
        "run")]
    public void Extract_JavaScript_DoesNotTreatClassFieldInitializerMembersAsClassMethods(string content, string unexpectedMethod)
    {
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Example");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == unexpectedMethod && s.ContainerName == "Example");
    }

    [Fact]
    public void Extract_JavaScript_SemicolonlessFieldInitializerDoesNotHideComputedOrGeneratorMethods()
    {
        var content = """
            class Example {
                field = foo
                [Symbol.iterator]() {}
                *generate() {}
                next() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generate" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.ContainerName == "Example");
    }

    [Fact]
    public void Extract_JavaScript_DetectsInlineClassExpressionMethods()
    {
        var content = "const Service = class NamedService { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsExportsClassExpressionMethods()
    {
        var content = "exports.Service = class NamedService { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_JavaScript_DetectsDollarPrefixedClassExpressionBindings()
    {
        var content = """
            export const $Service = class {
                run() {}
            };

            exports.$Handler = class {
                keep() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "$Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "$Handler");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsModuleExportsClassExpressionMethods()
    {
        var content = "module.exports = class { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineCommonJsModuleExportsClassExpressionMethods()
    {
        var content = """
            module.exports =
                class {
                    run() {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedCommonJsModuleExportsClassExpressionMethods()
    {
        var content = """
            module.exports = (class {
                run() {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsModuleExportsPropertyClassExpressionMethods()
    {
        var content = "module.exports.Service = class { run() {} };";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsClassExpressionInsideTopLevelConditionalBlock()
    {
        var content = """
            if (typeof module !== "undefined") {
                module.exports = class {
                    run() {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakBlockScopedClassesInsideConditionalBlocks()
    {
        var content = """
            if (flag) {
                const Hidden = class {
                    run() {}
                };

                class LocalDecl {
                    keep() {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "LocalDecl");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakClassMethodLocalClassExpressionMethods()
    {
        var content = """
            export class Outer {
                method() {
                    const Inner = class {
                        run() {}
                    };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakClassMethodDirectLocalClasses()
    {
        var content = """
            class Outer {
                method() {
                    class Hidden {
                        run() {}
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakFunctionLocalClassExpressionMethods()
    {
        var content = """
            function outer() {
                const Service = class {
                    run() {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakDirectFunctionLocalClasses()
    {
        var content = """
            function outer() {
                class Hidden {
                    run() {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakCommonJsFunctionExpressionLocalClassMethods()
    {
        var content = """
            exports.handler = function () {
                const Local = class {
                    inside() {}
                };
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DetectsCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = function foo() { return 1; };
            module.exports.bar = () => 2;
            exports.baz = 42;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsNumericBracketNamedExportAssignments(string language)
    {
        var content = """
            exports[404] = notFound;
            module.exports[500] = function serverError() { return 500; };
            exports[dynamicKey] = hidden;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "404" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "500" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "dynamicKey" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatCommonJsNamedExportComparisonsAsAssignments()
    {
        var content = """
            module.exports.foo === undefined;
            exports.bar == null;
            module.exports.baz !== 1;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo =
              async () => {};
            module.exports.bar =
              () => 2;
            exports.baz =
              42;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedMultilineCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo =
              (
                async () => 1
              );
            module.exports.bar =
              (
                function () {
                  return 2;
                }
              );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
    }

    [Fact]
    public void Extract_JavaScript_DetectsParenthesizedSameLineCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = (function () { return 1; });
            module.exports.bar = (async function () { return 2; });
            module.exports.baz = (() => 3);
            exports.qux = (42);
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "baz");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux");
    }

    [Fact]
    public void Extract_JavaScript_CommonJsNamedExportFunctionsPreserveMultilineBraceBodyRanges()
    {
        var content = """
            module.exports.foo = function ()
            {
              return 1;
            };
            module.exports.bar = () =>
            {
              return 2;
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "foo"));
        Assert.Equal(1, foo.StartLine);
        Assert.Equal(4, foo.EndLine);
        Assert.Equal(2, foo.BodyStartLine);
        Assert.Equal(4, foo.BodyEndLine);

        var bar = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "bar"));
        Assert.Equal(5, bar.StartLine);
        Assert.Equal(8, bar.EndLine);
        Assert.Equal(6, bar.BodyStartLine);
        Assert.Equal(8, bar.BodyEndLine);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsDefaultFunctionAssignments(string language)
    {
        var content = """
            module.exports =
              (
                function createServer(req) {
                  return req;
                }
              );
            module.exports = async (value) => {
              return value;
            };
            module.exports = class Service { run() {} };
            module.exports = { named() {} };
            module.exports = 42;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var defaults = symbols
            .Where(s => s.Kind == "function" && s.Name == "default" && s.Visibility == "export")
            .ToList();
        Assert.Single(defaults);
        Assert.Contains(defaults, s => s.StartLine == 1 && s.BodyStartLine == 3 && s.BodyEndLine == 5);
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "default" && s.Visibility == "export" && s.StartLine == 7 && s.BodyStartLine == 7 && s.BodyEndLine == 9);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "named" && s.ContainerName == "module.exports");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsDefinePropertyExports(string language)
    {
        var content = """
            Object.defineProperty(exports, "__esModule", { value: true });
            Object.defineProperty(exports, "foo", { enumerable: true, get: function () { return api.foo; } });
            Object.defineProperty(exports, 404, { value: notFound });
            Object.defineProperty(
              module.exports,
              "bar-baz",
              { value: bar }
            );
            Object.defineProperty(
              module.exports,
              500,
              { value: serverError }
            );
            Object.defineProperty(local, "hidden", { value: hidden });
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar-baz" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "404" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "500" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "__esModule");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "hidden" && s.Visibility == "export");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsDefinePropertiesExports(string language)
    {
        var content = """
            Object.defineProperties(exports, {
              __esModule: { value: true },
              foo: { enumerable: true, get: function () { return api.foo; } },
              "bar-baz": { value: bar },
              ["computed-key"]: { value: computed },
              [dynamicKey]: { value: hidden },
              descriptorRef,
            });
            Object.defineProperties(
              module.exports,
              {
                default: { value: api },
                500: { value: serverError },
              }
            );
            Object.defineProperties(exports, { sameLine: { value: sameLine } });
            Object.defineProperties(local, { hidden: { value: hidden } });
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar-baz" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "computed-key" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "descriptorRef" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "default" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "500" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "sameLine" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "__esModule");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "dynamicKey" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "hidden" && s.Visibility == "export");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsCommonJsObjectAssignExports(string language)
    {
        var content = """
            Object.assign(exports, {
              foo,
              alias: value,
              "bar-baz": bar,
              ["computed-key"]: computed,
              [dynamicKey]: hidden,
            });
            Object.assign(
              module.exports,
              {
                default: api,
                500: serverError,
              }
            );
            Object.assign(exports, { sameLine: sameLine });
            Object.assign(local, { hidden });
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alias" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar-baz" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "computed-key" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "default" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "500" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "sameLine" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "dynamicKey" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "hidden" && s.Visibility == "export");
    }

    [Fact]
    public void Extract_JavaScript_DetectsConditionalCommonJsNamedExportAssignmentsInTopLevelBlocks()
    {
        var content = """
            if (process.env.FEATURE) {
              module.exports.enabled = function () {
                return true;
              };
            }
            if (process.env.FLAG) {
              exports.flag = 1;
            }
            function setup() {
              module.exports.hidden = function () {
                return false;
              };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "enabled");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "flag");
        Assert.DoesNotContain(symbols, s => s.Name == "hidden");
    }

    [Fact]
    public void Extract_TypeScript_GenericArrowCommonJsNamedExportFunctionsPreserveMultilineBraceBodyRanges()
    {
        var content = """
            module.exports.foo = <T>(value: T) =>
            {
              return value;
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "foo"));
        Assert.Equal(1, foo.StartLine);
        Assert.Equal(4, foo.EndLine);
        Assert.Equal(2, foo.BodyStartLine);
        Assert.Equal(4, foo.BodyEndLine);
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericArrowCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = <T>(value: T) => value;
            module.exports.bar =
              <T>(value: T) => value;
            module.exports.baz = (<T>(value: T) => value);
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "baz");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineAndConstrainedGenericArrowCommonJsNamedExportAssignments()
    {
        var content = """
            module.exports.foo = <T>(
              value: T
            ) => value;
            module.exports.bar = <T extends (...args: any[]) => number>(value: T) => value;
            module.exports.baz = async <T>(
              value: T
            ) => value;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "baz");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotTreatCommonJsNamedExportComparisonsAsAssignments()
    {
        var content = """
            module.exports.foo === undefined;
            exports.bar == null;
            module.exports.baz !== 1;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatCommonJsNamedExportIdentifierPrefixesAsFunctionsOrClasses()
    {
        var content = """
            module.exports.foo = functionCall();
            module.exports.bar = classyThing;
            module.exports.baz = (functionCall());
            exports.qux = (classyThing);
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "qux");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && (s.Name == "foo" || s.Name == "baz"));
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && (s.Name == "bar" || s.Name == "qux"));
    }

    [Fact]
    public void Extract_JavaScript_DetectsExportedObjectLiteralAliasProperties()
    {
        var content = """
            const foo = 1;
            function inner() { return 3; }
            function named() { return 4; }
            const answer = 42;
            module.exports = { foo, alias: inner, named, method() {} };
            export default { answer };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alias" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "named" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "inner" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_JavaScript_DetectsDestructuredNamedExports()
    {
        var content = """
            const source = {};
            export const { foo, renamed: localName, nested: { leaf }, items: [first], ...rest } = source;
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "localName" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "leaf" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "first" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "rest" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && (s.Name == "renamed" || s.Name == "nested" || s.Name == "items"));
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultiLineObjectLiteralBinding()
    {
        // The `{` may sit on a line after the `=` binding; collector must thread
        // the lex state across lines to find the open brace.
        // `{` が `=` バインディングと別行にあっても、collector は lex 状態を
        // 跨いで open brace を検出できること。
        var content = """
            const obj =
            {
                foo() { return 1; },
                *bar() { yield 1; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "obj");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "obj");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatQuotedOrComputedExportedObjectLiteralKeysAsValueSideShorthandProperties()
    {
        var content = """
            module.exports = { 'foo': bar, [baz]: qux, answer: 42 };
            export default { [name]: value, visible };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "visible" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "qux" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "value" && s.ContainerKind == "object" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatExportedObjectLiteralSpreadsAsProperties()
    {
        var content = """
            const rest = source;
            const defaults = source;
            const answer = 42;
            module.exports = { ...rest, actual: 1, config: { ...rest } };
            export default { ...defaults, answer };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "actual" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "config" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "rest" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "defaults" && s.ContainerKind == "object" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportedObjectLiteralShorthandProperties()
    {
        var content = """
            const foo = 1;
            const bar = 2;
            module.exports = {
              foo,
              bar,
              baz: foo,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "baz" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakBlocklessArrowReturnedClasses()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "factory");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakWrappedBlocklessArrowReturnedClassesAndKeepsRange()
    {
        var content = """
            const factory = () =>
                wrap(
                    class Hidden {
                        method() {}
                    }
                );
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(6, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(6, factory.BodyEndLine);
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingTopLevelClass()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                }
            class Visible {
                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingExpressionStatement()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                }
            foo();
            class Visible {
                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_JavaScript_BlocklessArrowWithoutSemicolonDoesNotHideFollowingCommonJsClassExport()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method() {}
                }
            exports.Service = class Visible {
                keep() {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakIifeLocalClassMethods()
    {
        var content = """
            (function () {
                const Local = class {
                    inside() {}
                };
            })();
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakStaticBlockLocalClassMethods()
    {
        var content = """
            class Outer {
                static {
                    const Local = class {
                        inside() {}
                    };
                }

                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakDirectStaticBlockLocalClasses()
    {
        var content = """
            class Outer {
                static {
                    class Local {
                        inside() {}
                    }
                }

                keep() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakObjectLiteralConciseMethodLocalClasses()
    {
        var content = """
            const obj = {
                method() {
                    class Inner {
                        run() {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakGetterSetterLocalClasses()
    {
        var content = """
            const obj = {
                get value() {
                    class HiddenGetter {
                        run() {}
                    }
                    return 1;
                },
                set value(input) {
                    class HiddenSetter {
                        run() {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenGetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenSetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_PreservesGetterSetterSignaturesAndMethodNamedGetSet()
    {
        var content = """
            class Example {
                get value() {}
                set value(input) {}
                get() {}
                set() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "get value() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "set value(input) {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get" && s.Signature == "get() {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "set" && s.Signature == "set() {}");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotLeakNamedClassExpressionsAfterColon()
    {
        var content = """
            const pick = cond ? value : class Hidden { method() {} };
            const obj = { field: class Inner { run() {} } };
            class Visible { ok() {} }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ok" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatControlFlowBlocksAsFunctions()
    {
        var content = """
            class Parser {
                parse(value) {
                    if (value) {
                    }

                    for (const item of value.items) {
                    }

                    while (value.ready) {
                    }

                    switch (value.mode) {
                        case "fast":
                            break;
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Parser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "for");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "while");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "switch");
    }

    [Fact]
    public void Extract_JavaScript_AllowsKeywordNamedMethodsAtClassBodyDepthZero()
    {
        var content = """
            class KeywordMethods {
                if() {}
                catch() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "KeywordMethods");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "catch");
    }

    [Fact]
    public void Extract_JavaScript_IgnoresRegexLiteralBracesAndBlockCommentMethodShapes()
    {
        var content = """
            class Example {
                /*
                    fake() {
                    }
                */
                first() {
                    const open = /{/;
                    const close = /}/;
                }

                second() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "fake");
    }

    [Fact]
    public void Extract_JavaScript_KeepsSiblingMethodsAfterWrappedControlFlowRegexLiterals()
    {
        var content = """
            class Example {
                first(value) {
                    if (
                        ready
                    ) /{/.test(value);
                }

                second(value) {
                    if (first) {
                    }
                    else if (
                        secondReady
                    ) /{/.test(value);
                }

                third() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

    [Fact]
    public void Extract_JavaScript_IgnoresHeaderObjectLiteralBracesBeforeClassBody()
    {
        var content = """
            class Derived extends mixin({ value: true }) {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotTreatHeaderComparisonAsGenericAngleDepth()
    {
        var content = """
            class Derived extends mixin(a < b ? Base : Fallback) {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_JavaScript_KeepsSiblingMethodsAfterElseRegexLiteral()
    {
        var content = """
            class Example {
                first(value) {
                    if (cond) {
                    }
                    else /{/.test(value);
                }

                second() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_JavaScript_KeepsSiblingMethodsAfterDoAndFinallyRegexLiterals()
    {
        var content = """
            class Example {
                first(value) {
                    do /{/.test(value); while (cond);
                }

                second(value) {
                    try {
                    }
                    finally /{/.test(value);
                }

                third() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

    [Fact]
    public void Extract_JavaScript_FunctionRangeIgnoresComparisonAngleBracketsInParameters()
    {
        var content = """
            function choose(value = a < b ? one : two) {
                return value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var choose = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "choose"));
        Assert.Equal(1, choose.StartLine);
        Assert.Equal(3, choose.EndLine);
        Assert.Equal(1, choose.BodyStartLine);
        Assert.Equal(3, choose.BodyEndLine);
    }

    [Fact]
    public void Extract_TypeScript_DetectsAbstractClassAndNamespace()
    {
        var content = "export abstract class BaseService {\n    abstract getName(): string;\n}\ndeclare module 'express' {\n    interface Request { }\n}\nnamespace App.Models {\n    export type ID = string;\n}";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "BaseService");
        // Quoted ambient module declaration / 引用符付きアンビエントモジュール宣言
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "express");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "App.Models");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ID");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportAsNamespace()
    {
        var content = """
            export as namespace LegacyWidgets;
            export as namespace $Widgets;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "LegacyWidgets");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "$Widgets");
    }

    [Fact]
    public void Extract_TypeScript_DetectsDeclarationOnlyMembersInDeclareClassAndInterface()
    {
        var content = """
            declare class Service {
                run(): void;
                fetch<T>(id: string): Promise<T>;
            }

            interface Api {
                ping(): void;
                fetch<T>(id: string): Promise<T>;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerKind == "class" && s.ContainerName == "Service" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch" && s.ContainerKind == "class" && s.ContainerName == "Service" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Api");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ping" && s.ContainerKind == "interface" && s.ContainerName == "Api" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch" && s.ContainerKind == "interface" && s.ContainerName == "Api" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_Shell_DetectsMultipleAliasDefinitions()
    {
        var content = """
            alias ll='ls -la' gs='git status'
            alias -g G='| grep' H='| head'
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);

        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "ll");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "gs");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "G");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "H");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAccessorClassFields()
    {
        var content = """
            class Settings {
                accessor theme: string;
                accessor count: number;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Settings");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "theme" && s.ContainerKind == "class" && s.ContainerName == "Settings" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "count" && s.ContainerKind == "class" && s.ContainerName == "Settings" && s.ReturnType == "number");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotMergeAbstractMemberIntoFollowingConcreteMethod()
    {
        var content = """
            export default abstract class Example {
                abstract run(): void;

                keep(): { value: string } {
                    return { value: "x" };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run" && s.Signature != null && s.Signature.Contains("keep()", StringComparison.Ordinal));
        var keep = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "keep"));
        Assert.Equal("{ value: string }", keep.ReturnType);
        Assert.Equal("keep(): { value: string } {", keep.Signature);
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportDefaultClassMembers()
    {
        var content = """
            export default class DefaultTs {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DefaultTs");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Theory]
    [InlineData("javascript", "export default function load() { return 1; }", "load")]
    [InlineData("javascript", "export default function* () { yield 1; }", "default")]
    [InlineData("typescript", "export default function load<T>(value: T): T { return value; }", "load")]
    [InlineData("typescript", "export default function <T>(value: T): T { return value; }", "default")]
    public void Extract_JavaScriptTypeScript_DetectsExportDefaultFunctionSymbols(
        string language,
        string content,
        string expectedName)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var function = Assert.Single(symbols.Where(s => (s.Kind == "function" || s.Kind == "generator") && s.Name == expectedName));
        Assert.Equal("export", function.Visibility);
        Assert.Equal(content, function.Signature);
    }

    [Theory]
    [InlineData("javascript", "export default\n  (value) => value;", null, null)]
    [InlineData("typescript", "export default\n  (value) => value;", null, null)]
    [InlineData("javascript", "export default async (value) => {\n  return value;\n};", 1, 3)]
    [InlineData("typescript", "export default async (value) => {\n  return value;\n};", 1, 3)]
    public void Extract_JavaScriptTypeScript_DetectsExportDefaultArrowFunctionSymbols(
        string language,
        string content,
        int? expectedBodyStartLine,
        int? expectedBodyEndLine)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var function = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "default"));
        Assert.Equal("export", function.Visibility);
        Assert.Equal(1, function.StartLine);
        Assert.Equal(expectedBodyStartLine, function.BodyStartLine);
        Assert.Equal(expectedBodyEndLine, function.BodyEndLine);
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportDefaultGenericArrowFunctionSymbol()
    {
        var content = """
            export default <T>(
              value: T
            ) => value;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var function = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "default"));
        Assert.Equal("export", function.Visibility);
        Assert.Equal(1, function.StartLine);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsMultilineDynamicImportSymbols(string language)
    {
        var content = """
            const loader = () => import(
                "./feature"
            );
            const method = client.import(
                "./method"
            );
            const optional = client?.import("./optional");
            class Loader { #import(path) {} load() { return this.#import("./private"); } }
            const text = "import('./string')";
            // import('./comment')
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var importSymbol = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./feature"));
        Assert.Equal(2, importSymbol.Line);
        Assert.Contains("const loader", importSymbol.Signature);
        Assert.Contains("import(", importSymbol.Signature);
        Assert.Contains("./feature", importSymbol.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./method");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./optional");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./private");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./string");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./comment");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsDynamicImportSymbolsWithImportOptions(string language)
    {
        var content = """
            const data = await import("./data.json", {
                with: { type: "json" }
            });
            const legacy = await import(
                "./legacy.json",
                { assert: { type: "json" } }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var dataImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./data.json"));
        Assert.Equal(1, dataImport.Line);
        Assert.Contains("with", dataImport.Signature);
        Assert.Contains("type", dataImport.Signature);

        var legacyImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./legacy.json"));
        Assert.Equal(5, legacyImport.Line);
        Assert.Contains("assert", legacyImport.Signature);
        Assert.Contains("./legacy.json", legacyImport.Signature);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsTemplateLiteralDynamicImportSymbols(string language)
    {
        var content = """
            const view = import(`./view.js`);
            const computed = import(`./${name}.js`);
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        var viewImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./view.js"));
        Assert.Equal(1, viewImport.Line);
        Assert.Contains("`./view.js`", viewImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("${", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsStaticImportModuleSymbols(string language)
    {
        var content = """
            import React from "react";
            import {
                computed,
                ref,
            } from
                "vue";
            import "./setup";
            import data from "./data.json" with { type: "json" };
            import legacy from "./legacy.json" assert {
                type: "json"
            };
            import { with as withAlias, assert as assertAlias } from "./keywords"
            const meta = import.meta.url;
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "react");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "vue");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./setup");
        var dataImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./data.json"));
        Assert.Contains("with", dataImport.Signature);
        var legacyImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./legacy.json"));
        Assert.Contains("assert", legacyImport.Signature);
        var keywordsImport = Assert.Single(symbols.Where(s => s.Kind == "import" && s.Name == "./keywords"));
        Assert.DoesNotContain("import.meta", keywordsImport.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "meta");
    }

    [Fact]
    public void Extract_TypeScript_MultilineImportTypeQuery_DoesNotEmitRuntimeImportSymbol()
    {
        var content = """
            type Module = typeof import(
                "./types"
            );
            const runtime = import(
                "./runtime"
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "./types");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "./runtime");
    }

    [Fact]
    public void Extract_JavaScript_DetectsMultilineAnonymousDefaultExportClassMembers()
    {
        var content = """
            export default class
            extends Base
            {
                run() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotInventExtendsAsAnonymousDefaultClassName()
    {
        var content = """
            export default class extends Base {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotInventExtendsAsAnonymousDefaultDerivedClassName()
    {
        var content = """
            export default class extends mixin(Base) {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "extends");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotInventImplementsAsAnonymousDefaultClassName()
    {
        var content = """
            export default class implements Runnable {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "implements");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("default", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassExpressionMethods()
    {
        var content = """
            const Service = class NamedService {
                run(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "NamedService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineClassMethods()
    {
        var content = "export class Inline { run(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethods()
    {
        var content = "export class Inline { first(): void {} second(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Inline", first.ContainerName);
        Assert.Equal("first(): void {}", first.Signature);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("Inline", second.ContainerName);
        Assert.Equal("second(): void {}", second.Signature);
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineSiblingClassesWithDistinctMethodNames()
    {
        var content = "export class A { first(): void {} } export class B { second(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineClassExpressionAfterStatementPrefixWithCleanSignature()
    {
        var content = "foo(); export const Service = class Visible { keep(): void {} };";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "export const Service = class Visible { keep(): void {} }");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineSiblingClassesWithIdenticalMethodNames()
    {
        var content = "export class A { run(): void {} } export class B { run(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLinePublicClassAfterStatementPrefix()
    {
        var content = "foo(); class Visible { keep(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLinePublicClassAfterFunctionLocalHiddenClass()
    {
        var content = "function outer(): void { class Hidden { run(): void {} } } class Visible { keep(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DetectsSameLineClassExpressionAfterStatementPrefix()
    {
        var content = "foo(); const Service = class Visible { keep(): void {} };";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineDefaultExportClassMethods()
    {
        var content = "export default class Inline { run(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Inline", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsTypeAliasesAsImports()
    {
        var content = """
            export type Pair<T> = [T, T];
            type Callback = (x: number) => number;
            declare type User = { name: string; age: number };
            interface Admin { perms: string[]; }
            class Person { name: string = ""; }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Pair");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Admin");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericTypeAliasesWithDefaultTypeParameters()
    {
        var content = """
            export type Result<T = string> = { value: T };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Result");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineDefaultExportMultipleMethods()
    {
        var content = "export default class { first(): void {} second(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedDefaultExportClassMembers()
    {
        var content = """
            export default (class {
                run(): void {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineParenthesizedDefaultExportClassSignature()
    {
        var content = """
            export default
            (
                class {
                    run(): void {}
                }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export default ( class {");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAnonymousAbstractDefaultExportClassMembers()
    {
        var content = """
            export default abstract class {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineAnonymousAbstractDefaultExportClassMembers()
    {
        var content = """
            export default abstract class
            extends Base
            {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAnonymousGenericDefaultExportClassMembers()
    {
        var content = """
            export default class<T> extends Base<{ value: string }> {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAnonymousAbstractGenericDefaultExportClassMembers()
    {
        var content = """
            export default abstract class<T> extends Base<{ value: string }> {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineModifierNamedMethods()
    {
        var content = "export class Example { async(): void {} static(): void {} keep(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "async" && s.Signature == "async(): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "static" && s.Signature == "static(): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.Signature == "keep(): void {}");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMethodsWithDefaultArguments()
    {
        var content = "class Example { method(x: number = 1): void {} visible(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.Signature == "method(x: number = 1): void {}");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlinePrivateAndGeneratorMethods()
    {
        var content = "class Example { #hidden(): void {} *iterator(): Iterable<number> {} async *stream(): AsyncIterable<number> {} visible(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example" && s.ReturnType == "AsyncIterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "#hidden" && s.Signature == "#hidden(): void {}");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "iterator" && s.Signature == "*iterator(): Iterable<number> {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.Signature == "async *stream(): AsyncIterable<number> {}");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineComputedMethods()
    {
        var content = "class Example { ['computed'](): void {} [Symbol.iterator](): Iterable<number> {} visible(): void {} }";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "visible" && s.ContainerName == "Example" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "['computed']" && s.Signature == "['computed'](): void {}");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "[Symbol.iterator](): Iterable<number> {}");
    }

    [Fact]
    public void Extract_TypeScript_PreservesModifierMetadataForComputedMethods()
    {
        var content = """
            class Example {
                async [Symbol.asyncIterator](): AsyncGenerator<number> {}
                public static [Symbol.iterator](): IterableIterator<number> {}
                get [Symbol.toStringTag](): string {}
                set [key](value: string) {}
                async *[Symbol.dispose](): AsyncGenerator<string> {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "[Symbol.asyncIterator]" && s.Signature == "async [Symbol.asyncIterator](): AsyncGenerator<number> {}" && s.ReturnType == "AsyncGenerator<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.Signature == "public static [Symbol.iterator](): IterableIterator<number> {}" && s.Visibility == "public" && s.ReturnType == "IterableIterator<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.toStringTag]" && s.Signature == "get [Symbol.toStringTag](): string {}" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[key]" && s.Signature == "set [key](value: string) {}");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "[Symbol.dispose]" && s.Signature == "async *[Symbol.dispose](): AsyncGenerator<string> {}" && s.ReturnType == "AsyncGenerator<string>");
    }

    [Fact]
    public void Extract_TypeScript_DetectsStringAndTemplateLiteralReturnTypes()
    {
        var content = """
            export class Example {
                literal(): 'a' {}
                union(): 'a' | 'b' {}
                message(): `a${string}` {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "literal" && s.Signature == "literal(): 'a' {}" && s.ReturnType == "'a'");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "union" && s.Signature == "union(): 'a' | 'b' {}" && s.ReturnType == "'a' | 'b'");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "message" && s.Signature == "message(): `a${string}` {}" && s.ReturnType == "`a${string}`");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsEscapedStringLiteralReturnTypes()
    {
        var content = """
            export class Example {
                method(): "a\"b" {}
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.Signature == "method(): \"a\\\"b\" {}" && s.ReturnType == "\"a\\\"b\"");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.Signature == "keep(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsQuotedAndNumericLiteralMethodNames()
    {
        var content = """
            export class Example {
                "run"(): void {}
                'stop'(): void {}
                1(): void {}
                1.5(): void {}
                0x10(): void {}
                1_000(): void {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"run\"" && s.Signature == "\"run\"(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'stop'" && s.Signature == "'stop'(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1" && s.Signature == "1(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1.5" && s.Signature == "1.5(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "0x10" && s.Signature == "0x10(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "1_000" && s.Signature == "1_000(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsEscapedQuotedLiteralMethodNames()
    {
        var content = """
            export class Example {
                "a\"b"(): void {}
                'c\'d'(): void {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "\"a\\\"b\"" && s.Signature == "\"a\\\"b\"(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "'c\\'d'" && s.Signature == "'c\\'d'(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.Signature == "next(): void {}" && s.ReturnType == "void");
    }

    [Theory]
    [InlineData(
        """
        export class Example {
            handler = function namedHandler(): void {};
            keep(): void {}
        }
        """,
        "namedHandler")]
    [InlineData(
        """
        export class Example {
            handler = function (): void {};
            keep(): void {}
        }
        """,
        "function")]
    [InlineData(
        """
        export class Example {
            field = { inner(): void {} };
            keep(): void {}
        }
        """,
        "inner")]
    [InlineData(
        """
        export class Example {
            field = class Inner { run(): void {} };
            keep(): void {}
        }
        """,
        "run")]
    public void Extract_TypeScript_DoesNotTreatClassFieldInitializerMembersAsClassMethods(string content, string unexpectedMethod)
    {
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Example");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == unexpectedMethod && s.ContainerName == "Example");
    }

    [Fact]
    public void Extract_TypeScript_ClassifiesAsyncAndGeneratorFunctionKinds()
    {
        var content = """
            function plain(): void {}
            async function asyncPlain(): Promise<void> {}
            function* generated(): Iterable<number> {}
            async function* asyncGenerated(): AsyncIterable<number> {}
            export default async function* (): AsyncIterable<number> {}
            export class Example {
                async method(): Promise<void> {}
                *items(): Iterable<number> {}
                async *stream(): AsyncIterable<number> {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plain");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "asyncPlain");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "asyncGenerated");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "default" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "method" && s.ContainerName == "Example" && s.ReturnType == "Promise<void>");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "items" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "stream" && s.ContainerName == "Example" && s.ReturnType == "AsyncIterable<number>");
    }

    [Fact]
    public void Extract_TypeScript_SemicolonlessFieldInitializerDoesNotHideComputedOrGeneratorMethods()
    {
        var content = """
            export class Example {
                field = foo
                [Symbol.iterator](): Iterator<number> {}
                *generate(): Iterable<number> {}
                next(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[Symbol.iterator]" && s.ContainerName == "Example" && s.ReturnType == "Iterator<number>");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "generate" && s.ContainerName == "Example" && s.ReturnType == "Iterable<number>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "next" && s.ContainerName == "Example" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethodsWithObjectReturnType()
    {
        var content = """export class Example { first(): { value: string } { return { value: "x" }; } second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("first(): { value: string } { return { value: \"x\" }; }", first.Signature);
        Assert.Equal("second(): void {}", second.Signature);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethodsWithConditionalObjectReturnType()
    {
        var content = """export class Example { first(): T extends U ? { a: string } : { b: string } {} second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("first(): T extends U ? { a: string } : { b: string } {}", first.Signature);
        Assert.Equal("T extends U ? { a: string } : { b: string }", first.ReturnType);
        Assert.Equal("second(): void {}", second.Signature);
        Assert.Equal("void", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineMultipleMethodsWithFunctionReturningObjectType()
    {
        var content = """export class Example { first(): (() => { value: string }) {} second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second"));
        Assert.Equal("first(): (() => { value: string }) {}", first.Signature);
        Assert.Equal("(() => { value: string })", first.ReturnType);
        Assert.Equal("second(): void {}", second.Signature);
        Assert.Equal("void", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineClassMethodHeaders()
    {
        var content = """
            export class MultiLineMethod {
                run(
                    value: string,
                ): void {}

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MultiLineMethod");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        var keep = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "keep"));
        Assert.Equal("void", run.ReturnType);
        Assert.Contains("run(", run.Signature);
        Assert.Contains("): void {", run.Signature);
        Assert.Equal("void", keep.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineGenericClassMethodHeaders()
    {
        var content = """
            export class MultiLineGenericMethod {
                run<T>(
                    value: T,
                ): Promise<T> {
                    return Promise.resolve(value);
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MultiLineGenericMethod");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("Promise<T>", run.ReturnType);
        Assert.Contains("run<T>(", run.Signature);
        Assert.Contains("): Promise<T> {", run.Signature);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_PreservesReturnTypeMetadataForMethodsWithMultilineBodies()
    {
        var content = """
            export class Example {
                run(): void {
                    return;
                }

                build(): (() => { value: string }) {
                    return () => ({ value: "x" });
                }

                async *stream<T>(): AsyncGenerator<T> {
                    yield default(T)!;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        var build = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "build"));
        var stream = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "stream"));
        Assert.Equal("void", run.ReturnType);
        Assert.Equal("(() => { value: string })", build.ReturnType);
        Assert.Equal("AsyncGenerator<T>", stream.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineClassExpressionMethods()
    {
        var content = "const Service = class NamedService { run(): void {} };";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        var run = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "run"));
        Assert.Equal("class", run.ContainerKind);
        Assert.Equal("Service", run.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedClassExpressionMethods()
    {
        var content = "const Service = (class { run(): void {} });";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportEqualsClassExpressionMethods()
    {
        var content = """
            export = class {
                run(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportEqualsNamedClassExpressionMethods()
    {
        var content = """
            export = class Named {
                run(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Named");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedExportEqualsClassExpressionMethods()
    {
        var content = """
            export = (class {
                run(): void {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineParenthesizedExportEqualsClassSignature()
    {
        var content = """
            export =
            (
                class {
                    run(): void {}
                }
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default" && s.Signature == "export = ( class {");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassExpressionInsideNamespaceBlock()
    {
        var content = """
            namespace Foo {
                export const Service = class {
                    run(): void {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_TypeScript_DetectsDollarPrefixedClassExpressionBindings()
    {
        var content = """
            export const $Service = class {
                run(): void {}
            };

            module.exports.$Handler = class {
                keep(): void {}
            };

            export namespace PublicNs {
                export const $Worker = class {
                    job(): void {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "$Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "$Handler");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "PublicNs");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "$Worker");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "job" && s.ContainerName == "$Worker");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineClassExpressionInsideNamespaceBlock()
    {
        var content = """
            namespace Foo {
                export const Service =
                    class {
                        run(): void {}
                    };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "Service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.Signature == "export const Service = class {");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakNonExportedNamespaceClasses()
    {
        var content = """
            namespace Foo {
                class Hidden {
                    run(): void {}
                }

                const HiddenExpr = class {
                    keep(): void {}
                };

                export class Visible {
                    stay(): void {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Foo");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenExpr");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "stay" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DetectsParenthesizedCommonJsModuleExportsClassExpressionMethods()
    {
        var content = """
            module.exports = (class {
                run(): void {}
            });
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakBlockScopedClassesInsideConditionalBlocks()
    {
        var content = """
            if (flag) {
                const Hidden = class {
                    run(): void {}
                };

                class LocalDecl {
                    keep(): void {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "LocalDecl");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethods()
    {
        var content = """
            export class Example {
                first<T extends Foo<Bar>>(): void {}
                second(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethodsWithFunctionTypeDefault()
    {
        var content = """
            export class Example {
                method<T = () => void>(): number {}
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethodsWithFunctionTypeConstraint()
    {
        var content = """
            export class Example {
                method<T extends () => void>(): number {}
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotMergeOverloadSignaturesIntoImplementationMethod()
    {
        var content = """
            class Overloaded {
                foo(x: string): string;
                foo(x: number): number;
                foo(x: string | number): string | number {
                    return x;
                }

                bar(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Overloaded");
        var fooDeclarations = symbols.Where(s => s.Kind == "function" && s.Name == "foo" && s.BodyStartLine == null).ToList();
        Assert.Equal(2, fooDeclarations.Count);
        var foo = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "foo" && s.BodyStartLine != null));
        Assert.Equal(4, foo.Line);
        Assert.Equal("string | number", foo.ReturnType);
        Assert.Equal("foo(x: string | number): string | number {", foo.Signature);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bar" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsGenericClassMethodsWithMultilineFunctionTypeParameters()
    {
        var content = """
            export class Example {
                method<T = () => void>(
                    value: T,
                ): number {
                    return 1;
                }

                constrained<T extends () => void>(
                    value: T,
                ): number {
                    return 2;
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "constrained" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInlineGenericClassMethods()
    {
        var content = """export class Example { first<T extends Foo<Bar>>(): void {} second(): void {} }""";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakClassMethodLocalClassExpressionMethods()
    {
        var content = """
            export class Outer {
                method(): void {
                    const Inner = class {
                        run(): void {}
                    };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakClassMethodLocalSyntheticClassExpressionsWithObjectReturnType()
    {
        var content = """
            export class Outer {
                method(): { value: string } {
                    var Service = class Hidden {
                        run(): void {}
                    };
                    module.exports = class ModuleHidden {
                        keep(): void {}
                    };
                    return { value: "x" };
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakClassMethodDirectLocalClasses()
    {
        var content = """
            export class Outer {
                method(): void {
                    class Hidden {
                        run(): void {}
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerName == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakFunctionLocalClassExpressionMethods()
    {
        var content = """
            function outer(): void {
                const Service = class {
                    run(): void {}
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakDirectFunctionLocalClasses()
    {
        var content = """
            function outer(): void {
                class Hidden {
                    run(): void {}
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakCommonJsFunctionExpressionLocalClassMethods()
    {
        var content = """
            exports.handler = function (): void {
                const Local = class {
                    inside(): void {}
                };
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakIifeLocalClassMethods()
    {
        var content = """
            (() => {
                const Local = class {
                    inside(): void {}
                };
            })();
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakBlocklessArrowReturnedClasses()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "factory");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakWrappedBlocklessArrowReturnedClassesAndKeepsRange()
    {
        var content = """
            const factory = () =>
                wrap(
                    class Hidden {
                        method(): void {}
                    }
                );
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(6, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(6, factory.BodyEndLine);
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_TypeScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingTopLevelClass()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                }
            export class Visible {
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(1, factory.StartLine);
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(2, factory.BodyStartLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
    }

    [Fact]
    public void Extract_TypeScript_BlocklessArrowWithoutSemicolonDoesNotConsumeFollowingExpressionStatement()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                }
            foo();
            export class Visible {
                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Visible");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_TypeScript_BlocklessArrowWithoutSemicolonDoesNotHideFollowingCommonJsClassExport()
    {
        var content = """
            const factory = () =>
                class Hidden {
                    method(): void {}
                }
            exports.Service = class Visible {
                keep(): void {}
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "lambda" && s.Name == "factory"));
        Assert.Equal(4, factory.EndLine);
        Assert.Equal(4, factory.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep" && s.ContainerName == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultilineAnonymousDefaultExportClassMembers()
    {
        var content = """
            export default class
            implements Runnable
            {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerName == "default");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakStaticBlockLocalClassMethods()
    {
        var content = """
            export class Outer {
                static {
                    const Local = class {
                        inside(): void {}
                    };
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakDirectStaticBlockLocalClasses()
    {
        var content = """
            export class Outer {
                static {
                    class Local {
                        inside(): void {}
                    }
                }

                keep(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "keep");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "inside");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakObjectLiteralConciseMethodLocalClasses()
    {
        var content = """
            const obj = {
                method(): void {
                    class Inner {
                        run(): void {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakObjectLiteralConciseMethodSyntheticClassExpressionsWithObjectReturnType()
    {
        var content = """
            const obj = {
                method(): { value: string } {
                    var Service = class Hidden {
                        run(): void {}
                    };
                    module.exports = class ModuleHidden {
                        keep(): void {}
                    };
                    return { value: "x" };
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "keep");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakGetterSetterLocalClasses()
    {
        var content = """
            const obj = {
                get value(): number {
                    class HiddenGetter {
                        run(): void {}
                    }
                    return 1;
                },
                set value(input: number) {
                    class HiddenSetter {
                        run(): void {}
                    }
                }
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenGetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "HiddenSetter");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_PreservesGetterSetterSignaturesVisibilityAndMethodNamedGetSet()
    {
        var content = """
            class Example {
                public get value(): number {}
                private set value(input: number) {}
                get(): void {}
                set(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "public get value(): number {}" && s.Visibility == "public" && s.ReturnType == "number");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value" && s.Signature == "private set value(input: number) {}" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get" && s.Signature == "get(): void {}" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "set" && s.Signature == "set(): void {}" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotLeakNamedClassExpressionsAfterColon()
    {
        var content = """
            const pick = flag ? value : class Hidden { method(): void {} };
            const obj = { field: class Inner { run(): void {} } };
            class Visible { ok(): void {} }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Inner");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Visible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ok" && s.ContainerName == "Visible");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotTreatControlFlowBlocksAsFunctions()
    {
        var content = """
            export class Parser {
                override parse(value: Payload): Result {
                    if (value.ready) {
                    }

                    for (const item of value.items) {
                    }

                    while (value.more) {
                    }

                    switch (value.mode) {
                        case "fast":
                            return value.result;
                    }
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Parser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "for");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "while");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "switch");
    }

    [Fact]
    public void Extract_TypeScript_AllowsKeywordNamedMethodsAtClassBodyDepthZero()
    {
        var content = """
            export class KeywordMethods {
                if(): void {}
                catch(): string {
                    return "ok";
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "KeywordMethods");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "catch");
    }

    [Fact]
    public void Extract_TypeScript_IgnoresRegexLiteralBracesAndBlockCommentMethodShapes()
    {
        var content = """
            export class Example {
                /*
                    fake(): void {
                    }
                */
                first(): void {
                    const open = /{/;
                    const close = /}/;
                }

                second(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Example");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "fake");
    }

    [Fact]
    public void Extract_TypeScript_IgnoresHeaderGenericBracesBeforeClassBody()
    {
        var content = """
            export class Derived extends Base<{ value: string }> {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotTreatHeaderComparisonAsGenericAngleDepth()
    {
        var content = """
            export class Derived extends mixin(a < b ? Base : Fallback) {
                run(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_FunctionRangeIgnoresComparisonAngleBracketsInParameters()
    {
        var content = """
            function choose(value = a < b ? one : two): Result {
                return value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var choose = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "choose"));
        Assert.Equal(1, choose.StartLine);
        Assert.Equal(3, choose.EndLine);
        Assert.Equal(1, choose.BodyStartLine);
        Assert.Equal(3, choose.BodyEndLine);
    }

    [Fact]
    public void Extract_TypeScript_FunctionRangeIgnoresObjectReturnTypeBraces()
    {
        var content = """
            function outer(): { a: number } {
                return { a: 1 };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var outer = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "outer"));
        Assert.Equal(1, outer.StartLine);
        Assert.Equal(3, outer.EndLine);
        Assert.Equal(1, outer.BodyStartLine);
        Assert.Equal(3, outer.BodyEndLine);
        Assert.Equal("function outer(): { a: number } {", outer.Signature);
    }

    [Theory]
    [InlineData(
        "typescript",
        """
        function outer({ value }) {
            var Service = class Hidden {
                run() {}
            };
        }
        """)]
    [InlineData(
        "typescript",
        """
        function outer(value: { a: number }) {
            var Service = class Hidden {
                run() {}
            };
        }
        """)]
    [InlineData(
        "typescript",
        """
        const outer = function(value = { a: 1 }) {
            var Service = class Hidden {
                run() {}
            };
        };
        """)]
    public void Extract_TypeScript_DoesNotLeakClassExpressionsFromClassicFunctionHeaders(string lang, string content)
    {
        var symbols = SymbolExtractor.Extract(1, lang, content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
    }

    [Fact]
    public void Extract_TypeScript_KeepsSiblingMethodsAfterWrappedControlFlowRegexLiterals()
    {
        var content = """
            export class Example {
                first(value: string): void {
                    if (
                        ready
                    ) /{/.test(value);
                }

                second(value: string): void {
                    if (first) {
                    }
                    else if (
                        secondReady
                    ) /{/.test(value);
                }

                third(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }

    [Fact]
    public void Extract_TypeScript_KeepsSiblingMethodsAfterElseRegexLiteral()
    {
        var content = """
            export class Example {
                first(value: string): void {
                    if (cond) {
                    }
                    else /{/.test(value);
                }

                second(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
    }

    [Fact]
    public void Extract_TypeScript_KeepsSiblingMethodsAfterDoAndFinallyRegexLiterals()
    {
        var content = """
            export class Example {
                first(value: string): void {
                    do /{/.test(value); while (cond);
                }

                second(value: string): void {
                    try {
                    }
                    finally /{/.test(value);
                }

                third(): void {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third");
    }



















































    [Fact]
    public void Extract_JavaScript_WrappedBareMethodHeader_NormalizesCrlfToLf()
    {
        // JS/TS class-body methods whose header wraps across physical lines go through
        // TryCaptureJavaScriptTypeScriptMethodHeader, which appends each line with a '\n'
        // prefix. Without CRLF normalization, Windows sources (autocrlf=true, VS saves)
        // produce a Signature carrying '\r\n' between lines. Pin to '\n' for OS-independent
        // signature equality (#405 follow-up to #382).
        // JS/TS の class body method で header が行を跨ぐ場合、
        // TryCaptureJavaScriptTypeScriptMethodHeader が各行を '\n' 接頭辞で連結する。
        // CRLF 正規化がないと Windows ソース（autocrlf=true、VS 保存など）で Signature に
        // '\r\n' が混入していた。OS 差分で一致判定が崩れないよう '\n' に揃える
        // （#382 に続く #405 対応）。
        var content =
            "class Foo {\r\n" +
            "    myMethod(\r\n" +
            "        a,\r\n" +
            "        b,\r\n" +
            "    ) {\r\n" +
            "    }\r\n" +
            "}\r\n";
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "myMethod"));
        Assert.NotNull(method.Signature);
        Assert.DoesNotContain('\r', method.Signature);
        Assert.Contains("\n", method.Signature);
    }























































































































    [Fact]
    public void Extract_AssignsContainerAndRanges_ForNestedMembers()
    {
        var content = "public class UserService\n{\n    public async Task<User> GetUser(int id)\n    {\n        return default!;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var classSymbol = Assert.Single(symbols.Where(s => s.Kind == "class"));
        var method = Assert.Single(symbols.Where(s => s.Kind == "function"));

        Assert.Equal(1, classSymbol.StartLine);
        Assert.Equal(7, classSymbol.EndLine);
        Assert.Equal(2, classSymbol.BodyStartLine);
        Assert.Equal(7, classSymbol.BodyEndLine);
        Assert.Equal("class", method.ContainerKind);
        Assert.Equal("UserService", method.ContainerName);
        Assert.Equal(3, method.StartLine);
        Assert.Equal(6, method.EndLine);
    }









    [Fact]
    public void Extract_Fortran_DetectsModulesProgramsSubroutinesAndFunctions()
    {
        var content = """
            module math_utils
              interface math_iface
                module procedure &
                  normalize_iface, &
                  normalize_alt
                procedure(normalize_iface) :: &
                  normalize_forward
              procedure, pointer :: &
                  normalize_pointer
              end interface math_iface
              abstract &
              interface
                subroutine abstract_callback( &
                    value)
                end subroutine abstract_callback
              end interface
              implicit none
              type :: point_t
                integer :: x
                real :: origin_x, origin_y
              end type point_t
              integer, parameter :: max_rank = 8, default_rank = 2
              parameter (legacy_rank = 4, legacy_limit = selected_int_kind(9))
              integer legacy_count, legacy_total
              allocatable :: workspace, scratch
              pointer :: shared_point
              common /work_area/ common_status, common_flag
              namelist /config_area/ config_status, config_flag
              type, extends(point_t) :: colored_point
                integer :: color
              end type colored_point
              enum, bind(c)
                enumerator :: color_red = 1, color_blue = 2
              end enum
            contains
              integer(kind=4) &
              function split_value(value)
              end function split_value

              pure &
              recursive &
              subroutine split_subroutine(value)
              end subroutine split_subroutine

              recursive subroutine normalize(v)
                call normalize2(v) ! function phantom()
                print *, "subroutine phantom"
                entry normalize_restart(v)
              contains
                subroutine normalize_inner()
                end subroutine normalize_inner
              end
              end module procedure normalize_iface
              recursive subroutine normalize2(v)
              end subroutine normalize2
            end module math_utils

            submodule (math_utils) math_utils_impl
            contains
              module procedure normalize_impl
                print *, "normalize"
              end procedure normalize_impl

              module subroutine expand(v)
              end subroutine expand
            end submodule math_utils_impl

            program demo
              print *, "hello"
            end program demo

            block data constants_block
              common /constants/ pi
              data pi /3.14159/
            end block data constants_block

            integer function add(a, b)
            end function add

            real(kind=8) function typed_value(x)
            end function typed_value
            """;
        var symbols = SymbolExtractor.Extract(1, "fortran", content);

        var mathUtils = Assert.Single(symbols, s => s.Kind == "namespace" && s.Name == "math_utils");
        Assert.NotNull(mathUtils.BodyStartLine);
        Assert.NotNull(mathUtils.BodyEndLine);

        var mathIface = Assert.Single(symbols, s => s.Kind == "namespace" && s.Name == "math_iface");
        Assert.NotNull(mathIface.BodyStartLine);
        Assert.NotNull(mathIface.BodyEndLine);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize_iface");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize_alt");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize_forward");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize_pointer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "abstract_callback");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "split_value");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "split_subroutine");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "color_red");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "color_blue");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "max_rank");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "default_rank");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "legacy_rank");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "legacy_limit");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "legacy_count" && s.ReturnType == "integer");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "legacy_total" && s.ReturnType == "integer");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "workspace");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "scratch");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "shared_point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "common_status");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "common_flag");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "config_status");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "config_flag");
        Assert.Equal("namespace", Assert.Single(symbols, s => s.Kind == "function" && s.Name == "normalize_iface").ContainerKind);
        Assert.Equal("math_iface", Assert.Single(symbols, s => s.Kind == "function" && s.Name == "normalize_iface").ContainerName);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize_restart");

        var mathUtilsImpl = Assert.Single(symbols, s => s.Kind == "namespace" && s.Name == "math_utils_impl");
        Assert.NotNull(mathUtilsImpl.BodyStartLine);
        Assert.NotNull(mathUtilsImpl.BodyEndLine);

        var demo = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "demo");
        Assert.NotNull(demo.BodyStartLine);
        Assert.NotNull(demo.BodyEndLine);

        var constantsBlock = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "constants_block");
        Assert.NotNull(constantsBlock.BodyStartLine);
        Assert.NotNull(constantsBlock.BodyEndLine);

        var point = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "point_t");
        Assert.Equal("namespace", point.ContainerKind);
        Assert.Equal("math_utils", point.ContainerName);
        Assert.NotNull(point.BodyStartLine);
        Assert.NotNull(point.BodyEndLine);

        var pointX = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "x");
        Assert.Equal("class", pointX.ContainerKind);
        Assert.Equal("point_t", pointX.ContainerName);
        Assert.Equal("integer", pointX.ReturnType);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "origin_x" && s.ReturnType == "real");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "origin_y" && s.ReturnType == "real");

        var coloredPoint = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "colored_point");
        Assert.Equal("namespace", coloredPoint.ContainerKind);
        Assert.Equal("math_utils", coloredPoint.ContainerName);
        Assert.NotNull(coloredPoint.BodyStartLine);
        Assert.NotNull(coloredPoint.BodyEndLine);

        var color = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "color");
        Assert.Equal("class", color.ContainerKind);
        Assert.Equal("colored_point", color.ContainerName);
        Assert.Equal("integer", color.ReturnType);

        var normalize = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "normalize");
        Assert.Equal("namespace", normalize.ContainerKind);
        Assert.Equal("math_utils", normalize.ContainerName);
        Assert.NotNull(normalize.BodyStartLine);
        Assert.NotNull(normalize.BodyEndLine);

        var normalizeInner = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "normalize_inner");
        Assert.Equal("namespace", normalizeInner.ContainerKind);
        Assert.Equal("math_utils", normalizeInner.ContainerName);
        Assert.True(normalize.BodyEndLine > normalizeInner.EndLine);

        var expand = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "expand");
        Assert.Equal("namespace", expand.ContainerKind);
        Assert.Equal("math_utils_impl", expand.ContainerName);

        var normalizeImpl = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "normalize_impl");
        Assert.Equal("namespace", normalizeImpl.ContainerKind);
        Assert.Equal("math_utils_impl", normalizeImpl.ContainerName);
        Assert.NotNull(normalizeImpl.BodyStartLine);
        Assert.NotNull(normalizeImpl.BodyEndLine);

        var normalize2 = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "normalize2");
        Assert.Equal("namespace", normalize2.ContainerKind);
        Assert.Equal("math_utils", normalize2.ContainerName);
        Assert.True(normalize.BodyEndLine < normalize2.StartLine);

        Assert.DoesNotContain(symbols, s => s.Kind == "namespace" && s.Name == "subroutine");

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "typed_value");
    }




    [Fact]
    public void Extract_Shell_DetectsFunctionsAndAliases()
    {
        var content = """
            function setup() {
              echo 'setup'
            }

            cleanup() {
              echo 'cleanup'
            }

            alias ll='ls -la'
            alias my-grep='grep -n'
            alias -g G='| grep'
            """;
        var symbols = SymbolExtractor.Extract(1, "shell", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "setup");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "cleanup");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "ll");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "my-grep");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "G");
    }

    [Fact]
    public void Extract_SQL_DetectsCreateStatements()
    {
        var content = "CREATE TEMP TABLE users (\n  id INT PRIMARY KEY\n);\n\nCREATE OR REPLACE FUNCTION get_user(id INT) RETURNS void;\n\nCREATE MATERIALIZED VIEW active_users AS SELECT * FROM users;\n\nCREATE TYPE color AS ENUM ('red', 'green');\nCREATE TYPE inventory_item AS (name text);\nCREATE SCHEMA analytics;\nCREATE SCHEMA AUTHORIZATION analytics_owner;\nCREATE SEQUENCE order_seq;\nCREATE EXTENSION IF NOT EXISTS pgcrypto;\nCREATE DOMAIN positive_int AS integer CHECK (VALUE > 0);\nCREATE UNIQUE INDEX users_email_idx ON users (id);\n\nALTER TABLE users ADD COLUMN email TEXT;";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get_user");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "active_users");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "color");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "inventory_item");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "analytics");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "analytics_owner");
        Assert.DoesNotContain(symbols, s => s.Kind == "namespace" && s.Name == "AUTHORIZATION");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "order_seq");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "pgcrypto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "positive_int");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users_email_idx");
    }

    [Fact]
    public void Extract_SQL_DoesNotCaptureOnAsAnonymousIndexName()
    {
        var content = "CREATE INDEX ON users (email);\nCREATE INDEX IF NOT EXISTS users_name_idx ON users (name);";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "ON");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "users_name_idx");
    }

    [Fact]
    public void Extract_SQL_MySqlDefinerCreatesDefinerSymbols()
    {
        const string content = """
            CREATE DEFINER='admin'@'%' PROCEDURE schema.proc()
            BEGIN
              SELECT 1;
            END;
            CREATE DEFINER=`app_user`@`localhost` VIEW `schema`.`v_orders` AS SELECT 1;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "definer" && s.Name == "admin@%");
        Assert.Contains(symbols, s => s.Kind == "definer" && s.Name == "app_user@localhost");
    }

    [Fact]
    public void Extract_SQL_PostgresReturnsTableAndOutParametersCreateFieldSymbols()
    {
        const string content = """
            CREATE FUNCTION public.search_orders()
            RETURNS TABLE(id bigint, customer_name text, total numeric(12, 2))
            AS $$
            BEGIN
              RETURN QUERY SELECT id, customer_name, total FROM orders;
            END;
            $$ LANGUAGE plpgsql;

            CREATE FUNCTION public.load_order(OUT order_id int, OUT order_name text) RETURNS RECORD
            AS $$ SELECT 1, 'a' $$ LANGUAGE sql;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "id" && s.ContainerName == "public.search_orders" && s.ReturnType == "bigint");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "customer_name" && s.ContainerName == "public.search_orders" && s.ReturnType == "text");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "total" && s.ContainerName == "public.search_orders" && s.ReturnType == "numeric(12, 2)");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "order_id" && s.ContainerName == "public.load_order");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "order_name" && s.ContainerName == "public.load_order");
    }

    [Fact]
    public void Extract_SQL_DoesNotEmitDefinerOrReturnFieldsFromCommentsAndStrings()
    {
        const string content = """
            -- CREATE DEFINER='ghost'@'%' PROCEDURE hidden()
            SELECT 'RETURNS TABLE(fake int)';
            CREATE FUNCTION public.real()
            RETURNS TABLE(real_id int)
            AS $$ SELECT 1 $$ LANGUAGE sql;
            """;

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "definer" && s.Name == "ghost@%");
        Assert.DoesNotContain(symbols, s => s.Kind == "field" && s.Name == "fake");
        Assert.Contains(symbols, s => s.Kind == "field" && s.Name == "real_id");
    }

    [Fact]
    public void Extract_SQL_DetectsTSqlDdlKinds()
    {
        var content =
            "CREATE SCHEMA sales AUTHORIZATION dbo;\n" +
            "CREATE TYPE sales.Money FROM DECIMAL(18, 4) NOT NULL;\n" +
            "CREATE SEQUENCE sales.OrderSeq START WITH 1 INCREMENT BY 1;\n" +
            "CREATE AGGREGATE dbo.SumInt (@value INT) RETURNS INT EXTERNAL NAME dbo.SumInt;\n" +
            "CREATE ASSEMBLY dbo.MyAssembly FROM 0x010203;\n" +
            "CREATE XML SCHEMA COLLECTION dbo.XmlCollection AS N'<schema />';\n" +
            "CREATE RULE sales.discount_rule AS @price > 0;\n" +
            "CREATE DEFAULT dbo.zero_default AS 0;\n" +
            "CREATE SYNONYM dbo.Customers FOR external.Customers;\n" +
            "CREATE LOGIN [app_service] WITH PASSWORD = 'x';\n" +
            "CREATE USER app_service FOR LOGIN app_service;\n" +
            "CREATE ROLE sales_writer AUTHORIZATION dbo;\n" +
            "CREATE DATABASE sales_db;\n" +
            "CREATE CERTIFICATE svc_cert WITH SUBJECT = 'svc';\n" +
            "CREATE SECURITY POLICY SalesFilter ADD FILTER PREDICATE Security.fn_salesPredicate(SalesRep) ON dbo.Orders WITH (STATE = ON);\n" +
            "CREATE PARTITION FUNCTION pf_OrdersByYear (datetime2) AS RANGE RIGHT FOR VALUES ('2024-01-01');\n" +
            "CREATE PARTITION SCHEME ps_OrdersByYear AS PARTITION pf_OrdersByYear TO ([primary]);\n" +
            "CREATE FULLTEXT CATALOG ftc_sales WITH ACCENT_SENSITIVITY=OFF;\n" +
            "CREATE PROC dbo.sp_DailyReport @Date DATE AS BEGIN SELECT 1; END\n" +
            "CREATE PROCEDURE [dbo].[sp_GetOrder] @Id INT AS SELECT 1;\n" +
            "CREATE OR ALTER PROCEDURE dbo.sp_UpsertUser AS SELECT 1;\n" +
            "CREATE OR ALTER VIEW dbo.v_ActiveOrders AS SELECT 1;\n" +
            "ALTER PROCEDURE dbo.sp_DailyReport AS SELECT 2;\n" +
            "ALTER FUNCTION dbo.fn_Total() RETURNS INT AS BEGIN RETURN 1 END;\n" +
            "ALTER PARTITION FUNCTION pf_OrdersByYear() SPLIT RANGE ('2025-01-01');\n" +
            "ALTER AGGREGATE dbo.SumInt (@value INT) RETURNS INT EXTERNAL NAME dbo.SumIntV2;\n" +
            "ALTER ASSEMBLY dbo.MyAssembly FROM 0x040506;\n" +
            "ALTER XML SCHEMA COLLECTION dbo.XmlCollection ADD N'<schema />';\n" +
            "ALTER SCHEMA sales TRANSFER dbo.Customers;\n" +
            "ALTER EXTENSION hstore UPDATE TO '1.5';\n" +
            "ALTER CERTIFICATE svc_cert WITH PRIVATE KEY (DECRYPTION BY PASSWORD = 'x');\n" +
            "ALTER SECURITY POLICY SalesFilter WITH (STATE = OFF);\n" +
            "ALTER DOMAIN us_postal_code DROP NOT NULL;\n";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.Money");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.OrderSeq");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.SumInt");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.MyAssembly");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.XmlCollection");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.discount_rule");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.zero_default");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.Customers");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[app_service]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_service");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales_writer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales_db");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "svc_cert");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SalesFilter");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "pf_OrdersByYear");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ps_OrdersByYear");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ftc_sales");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_DailyReport");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[dbo].[sp_GetOrder]");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_UpsertUser");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.v_ActiveOrders");
        // ALTER on an object kind other than TABLE must now be captured with the same kind
        // contract as the matching CREATE row (function for procedure-like, namespace for schema,
        // import for extension, class for everything else).
        // TABLE 以外のオブジェクトに対する ALTER も、対応する CREATE 行と同じ kind 契約で
        // 捕捉されること（プロシージャ類は function、SCHEMA は namespace、EXTENSION は import、
        // その他は class）。
        Assert.Equal(2, symbols.Count(s => s.Name == "dbo.sp_DailyReport"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_DailyReport" && s.Signature != null && s.Signature.StartsWith("ALTER PROCEDURE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.fn_Total");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "pf_OrdersByYear" && s.Signature != null && s.Signature.StartsWith("ALTER PARTITION FUNCTION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.SumInt" && s.Signature != null && s.Signature.StartsWith("CREATE AGGREGATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dbo.SumInt" && s.Signature != null && s.Signature.StartsWith("ALTER AGGREGATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.MyAssembly" && s.Signature != null && s.Signature.StartsWith("CREATE ASSEMBLY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.MyAssembly" && s.Signature != null && s.Signature.StartsWith("ALTER ASSEMBLY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.XmlCollection" && s.Signature != null && s.Signature.StartsWith("CREATE XML SCHEMA COLLECTION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "dbo.XmlCollection" && s.Signature != null && s.Signature.StartsWith("ALTER XML SCHEMA COLLECTION", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales" && s.Signature != null && s.Signature.StartsWith("ALTER SCHEMA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "hstore");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "svc_cert" && s.Signature != null && s.Signature.StartsWith("ALTER CERTIFICATE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SalesFilter" && s.Signature != null && s.Signature.StartsWith("ALTER SECURITY POLICY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "us_postal_code");

        // SQL `CREATE SCHEMA sales;` and `ALTER SCHEMA sales TRANSFER ...;` are body-less
        // namespace-kind symbols. They must NOT be treated as C# file-scoped namespaces and
        // must not wrap every subsequent top-level SQL symbol as their container — SQL
        // schema is expressed through qualified names (`sales.Money`) rather than containment.
        // SQL の `CREATE SCHEMA sales;` と `ALTER SCHEMA sales TRANSFER ...;` は body 無しの
        // namespace kind だが、C# の file-scoped namespace 扱いにしてはならない。SQL の schema
        // は包含ではなく `sales.Money` のような修飾名で表すので、後続の top-level シンボルを
        // schema 配下に吸い込んではいけない。
        var containerPollutionVictims = new[]
        {
            "dbo.fn_Total",
            "pf_OrdersByYear",
            "hstore",
            "us_postal_code",
            "[app_service]",
            "app_service",
            "sales_writer",
            "sales_db",
            "svc_cert",
            "ps_OrdersByYear",
            "ftc_sales",
            "dbo.sp_DailyReport",
            "[dbo].[sp_GetOrder]",
            "dbo.sp_UpsertUser",
            "dbo.v_ActiveOrders",
        };
        foreach (var name in containerPollutionVictims)
        {
            Assert.All(
                symbols.Where(s => s.Name == name),
                s => Assert.True(
                    s.ContainerKind != "namespace" || s.ContainerName != "sales",
                    $"{s.Kind} {s.Name} was wrapped under namespace=sales — ALTER/CREATE SCHEMA must not act as a C# file-scoped namespace container."));
        }
    }

    [Fact]
    public void Extract_SQL_QualifiedNamesAllowWhitespaceAroundDots()
    {
        var content =
            "CREATE PROCEDURE [sales] . [sp_Report] AS SELECT 1;\n" +
            "CREATE VIEW dbo . v_Orders AS SELECT 1;\n" +
            "CREATE TYPE sales . Money AS ENUM ('usd');\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name.Contains("sp_Report", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name.Contains("v_Orders", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name.Contains("Money", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_TSqlBeginEnd()
    {
        // Multi-line T-SQL CREATE PROCEDURE with explicit BEGIN/END body terminated by GO.
        // ReferenceExtractor.ResolveContainerForCall depends on BodyStartLine/BodyEndLine
        // covering the lines that hold EXEC / CALL calls inside the procedure (issue #429).
        // GO で終わる複数行 T-SQL CREATE PROCEDURE（BEGIN/END 本体）。
        // ReferenceExtractor.ResolveContainerForCall は本体内の EXEC / CALL を含む行を
        // カバーする BodyStartLine / BodyEndLine に依存する（issue #429）。
        var content =
            "CREATE PROCEDURE dbo.sp_Outer\n" +  // line 1
            "AS\n" +                              // line 2
            "BEGIN\n" +                           // line 3
            "  EXEC dbo.sp_Inner;\n" +            // line 4
            "  SELECT 1;\n" +                     // line 5
            "END\n" +                             // line 6
            "GO\n" +                              // line 7
            "CREATE PROCEDURE dbo.sp_Inner\n" +   // line 8
            "AS\n" +                              // line 9
            "BEGIN\n" +                           // line 10
            "  SELECT 2;\n" +                     // line 11
            "END\n" +                             // line 12
            "GO\n";                               // line 13

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var outer = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Outer");
        Assert.Equal(1, outer.StartLine);
        Assert.NotNull(outer.BodyStartLine);
        Assert.NotNull(outer.BodyEndLine);
        // Body must cover the EXEC call on line 4 so callers/impact can attribute it.
        // EXEC 行（4 行目）を本体が覆う必要がある（callers / impact が帰属させられるように）。
        Assert.True(outer.BodyStartLine!.Value <= 4, $"BodyStartLine={outer.BodyStartLine} must be <= 4");
        Assert.True(outer.BodyEndLine!.Value >= 6, $"BodyEndLine={outer.BodyEndLine} must be >= 6 (body END)");
        // Body must not leak into the next procedure on line 8.
        // 8 行目の次のプロシージャまで本体が伸びてはいけない。
        Assert.True(outer.BodyEndLine!.Value < 8, $"BodyEndLine={outer.BodyEndLine} must not leak into the next CREATE at line 8");

        var inner = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Inner");
        Assert.Equal(8, inner.StartLine);
        Assert.NotNull(inner.BodyStartLine);
        Assert.NotNull(inner.BodyEndLine);
        Assert.True(inner.BodyStartLine!.Value <= 11, $"BodyStartLine={inner.BodyStartLine} must cover SELECT on line 11");
        Assert.True(inner.BodyEndLine!.Value >= 12, $"BodyEndLine={inner.BodyEndLine} must cover END on line 12");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_PostgresDollarQuoted()
    {
        // PostgreSQL CREATE FUNCTION ... AS $$ ... $$ must resolve BodyEndLine to the line
        // containing the closing `$$`, regardless of BEGIN / END / GO / ; inside the body.
        // PostgreSQL の `CREATE FUNCTION ... AS $$ ... $$` は、本体内の BEGIN / END / GO / ;
        // に関係なく、閉じ `$$` の行で BodyEndLine を解決できる必要がある。
        var content =
            "CREATE OR REPLACE FUNCTION public.notify_user(uid INT) RETURNS void AS $$\n" +  // line 1
            "DECLARE msg TEXT;\n" +                                                            // line 2
            "BEGIN\n" +                                                                        // line 3
            "  msg := 'hi; GO -- fake terminator';\n" +                                        // line 4
            "  PERFORM public.enqueue(uid, msg);\n" +                                          // line 5
            "END;\n" +                                                                         // line 6
            "$$ LANGUAGE plpgsql;\n" +                                                         // line 7
            "\n" +                                                                             // line 8
            "CREATE FUNCTION public.enqueue(uid INT, msg TEXT) RETURNS void AS $$\n" +         // line 9
            "BEGIN\n" +                                                                        // line 10
            "  INSERT INTO outbox VALUES (uid, msg);\n" +                                      // line 11
            "END;\n" +                                                                         // line 12
            "$$ LANGUAGE plpgsql;\n";                                                          // line 13

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var notify = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "public.notify_user");
        Assert.Equal(1, notify.StartLine);
        Assert.NotNull(notify.BodyStartLine);
        Assert.NotNull(notify.BodyEndLine);
        Assert.True(notify.BodyStartLine!.Value <= 5, $"BodyStartLine={notify.BodyStartLine} must cover PERFORM on line 5");
        Assert.Equal(7, notify.BodyEndLine);

        var enqueue = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "public.enqueue");
        Assert.Equal(9, enqueue.StartLine);
        Assert.NotNull(enqueue.BodyStartLine);
        Assert.NotNull(enqueue.BodyEndLine);
        Assert.True(enqueue.BodyStartLine!.Value <= 11, $"BodyStartLine={enqueue.BodyStartLine} must cover INSERT on line 11");
        Assert.Equal(13, enqueue.BodyEndLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_ClosesAtNextCreateWithoutGo()
    {
        // No `GO` between two procedures — the new-DDL-start guard must still close the
        // previous body so the second CREATE's header is not swallowed into sp_First's body.
        // プロシージャ間に `GO` が無い場合でも、次の DDL 行で前のボディを閉じる必要がある
        // （そうしないと次の CREATE 行が sp_First のボディに吸い込まれる）。
        var content =
            "CREATE PROCEDURE dbo.sp_First AS\n" +  // line 1
            "BEGIN\n" +                              // line 2
            "  EXEC dbo.sp_Helper;\n" +              // line 3
            "END\n" +                                // line 4
            "CREATE PROCEDURE dbo.sp_Second AS\n" +  // line 5
            "BEGIN\n" +                              // line 6
            "  SELECT 2;\n" +                        // line 7
            "END\n";                                 // line 8

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var first = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_First");
        Assert.NotNull(first.BodyEndLine);
        Assert.True(first.BodyEndLine!.Value <= 4, $"BodyEndLine={first.BodyEndLine} must close before the next CREATE on line 5");

        var second = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Second");
        Assert.Equal(5, second.StartLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_SingleLineAsBeginEnd()
    {
        // Single-line `CREATE PROC ... AS BEGIN ... END` must still expose a body range so
        // that any call on that same line (e.g. an EXEC inside a single-line body) can be
        // attributed back to the procedure.
        // 1 行で書かれた `CREATE PROC ... AS BEGIN ... END` でも、同一行の呼び出しを
        // プロシージャに帰属させるため、必ず body range を返す必要がある。
        var content =
            "CREATE PROC dbo.sp_A AS BEGIN EXEC dbo.sp_B; END\n" +
            "CREATE PROC dbo.sp_B AS BEGIN SELECT 1; END\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var a = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_A");
        Assert.NotNull(a.BodyStartLine);
        Assert.NotNull(a.BodyEndLine);
        Assert.True(a.BodyStartLine!.Value <= 1 && a.BodyEndLine!.Value >= 1,
            $"single-line sp_A body range must cover line 1 (got [{a.BodyStartLine}, {a.BodyEndLine}])");
        Assert.True(a.BodyEndLine!.Value < 2, $"sp_A body must not leak into sp_B on line 2 (got {a.BodyEndLine})");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_IgnoresTerminatorsInStringsAndComments()
    {
        // A `GO` inside a string literal or a block comment must not close the body
        // prematurely — MaskSqlLineForBodyScan strips strings/comments before the scan.
        // 文字列や `/* ... */` 内の `GO` で本体を閉じないこと
        // （MaskSqlLineForBodyScan が文字列・コメントを除去するため）。
        var content =
            "CREATE PROCEDURE dbo.sp_NoisyBody AS\n" +       // line 1
            "BEGIN\n" +                                       // line 2
            "  DECLARE @msg NVARCHAR(100) = 'GO ahead';\n" +  // line 3 — 'GO' in string
            "  /* GO */\n" +                                  // line 4 — GO in block comment
            "  -- GO line-comment\n" +                        // line 5 — GO in line comment
            "  EXEC dbo.sp_Target;\n" +                       // line 6
            "END\n" +                                         // line 7
            "GO\n" +                                          // line 8 — real terminator
            "CREATE PROCEDURE dbo.sp_Target AS\n" +           // line 9
            "BEGIN SELECT 1; END\n" +                         // line 10
            "GO\n";                                           // line 11

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var noisy = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_NoisyBody");
        Assert.NotNull(noisy.BodyStartLine);
        Assert.NotNull(noisy.BodyEndLine);
        // Body must cover the real EXEC on line 6 and must not stop at the fake GO on line 3/4/5.
        // 本体は line 6 の本物の EXEC を覆い、line 3/4/5 の偽 GO で止まってはいけない。
        Assert.True(noisy.BodyEndLine!.Value >= 6, $"BodyEndLine={noisy.BodyEndLine} must cover the real EXEC on line 6");
        Assert.True(noisy.BodyEndLine!.Value < 9, $"BodyEndLine={noisy.BodyEndLine} must not leak into sp_Target on line 9");

        var target = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Target");
        Assert.Equal(9, target.StartLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_AlterProcedureHasBody()
    {
        // ALTER PROCEDURE / ALTER FUNCTION / ALTER TRIGGER share the body shape with CREATE
        // and must get a body range so replacement implementations' inner calls resolve too.
        // ALTER PROCEDURE / ALTER FUNCTION / ALTER TRIGGER は CREATE と本体形状を共有するので、
        // 置換実装内の呼び出しも解決できるよう body range を持つ必要がある。
        var content =
            "ALTER PROCEDURE dbo.sp_Reset AS\n" +   // line 1
            "BEGIN\n" +                              // line 2
            "  EXEC dbo.sp_Clear;\n" +               // line 3
            "END\n" +                                // line 4
            "GO\n";                                  // line 5

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var reset = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Reset");
        Assert.NotNull(reset.BodyStartLine);
        Assert.NotNull(reset.BodyEndLine);
        Assert.True(reset.BodyEndLine!.Value >= 3, $"BodyEndLine={reset.BodyEndLine} must cover the EXEC on line 3");
        Assert.True(reset.BodyEndLine!.Value < 5, $"BodyEndLine={reset.BodyEndLine} must not include the GO batch terminator on line 5");
    }

    [Fact]
    public void Extract_SQL_AlterPartitionFunctionHasNoBody()
    {
        // ALTER PARTITION FUNCTION only changes partition boundaries (no code body), so it
        // must keep BodyStartLine / BodyEndLine unset even though ALTER PROCEDURE / FUNCTION
        // / TRIGGER now resolve a body via SqlProcBody.
        // ALTER PARTITION FUNCTION は境界変更のみ（コード本体なし）のため、
        // ALTER PROCEDURE / FUNCTION / TRIGGER が SqlProcBody で本体を取るようになっても、
        // BodyStartLine / BodyEndLine は null のまま維持する必要がある。
        var content =
            "ALTER PARTITION FUNCTION pf_OrdersByYear() SPLIT RANGE ('2025-01-01');\n" +
            "CREATE PROCEDURE dbo.sp_After AS BEGIN SELECT 1; END\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var partition = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "pf_OrdersByYear");
        Assert.Null(partition.BodyStartLine);
        Assert.Null(partition.BodyEndLine);
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_DoesNotPolluteContainer()
    {
        // Regression guard for the schema-pollution invariant (existing
        // Extract_SQL_DetectsTSqlDdlKinds) extended to proc-body ranges: a CREATE PROCEDURE
        // with a real body must not wrap the *next* proc as its container.
        // スキーマ汚染防止の不変量（既存の Extract_SQL_DetectsTSqlDdlKinds）を、今回追加した
        // プロシージャ本体にも拡張する。本体を持つ CREATE PROCEDURE が「次の」プロシージャを
        // コンテナとして囲ってはならない。
        var content =
            "CREATE PROCEDURE dbo.sp_First AS\n" +
            "BEGIN\n" +
            "  SELECT 1;\n" +
            "END\n" +
            "GO\n" +
            "CREATE PROCEDURE dbo.sp_Second AS\n" +
            "BEGIN\n" +
            "  SELECT 2;\n" +
            "END\n" +
            "GO\n";

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var second = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Second");
        Assert.True(
            second.ContainerKind != "function" || second.ContainerName != "dbo.sp_First",
            $"dbo.sp_Second was wrapped under container=dbo.sp_First — CREATE PROCEDURE body must not wrap sibling procedures.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_BodyInternalCreateTableDoesNotCloseBody()
    {
        // Regression for codex review iteration 1 finding #1: the body terminator heuristic must
        // only react to *another* proc-like DDL header (`CREATE|ALTER|DROP PROCEDURE|PROC|FUNCTION|
        // TRIGGER`), not to ordinary body-internal DDL like `CREATE TABLE #tmp` / `ALTER TABLE`.
        // Otherwise issue #429 re-appears whenever a T-SQL procedure stages a temp table before its
        // real work.
        // codex レビュー iteration 1 指摘 #1 の回帰テスト: 本体終端の判定は別の proc 系ヘッダ
        // （`CREATE|ALTER|DROP PROCEDURE|PROC|FUNCTION|TRIGGER`）のときだけ発火し、本体内の普通の
        // DDL（`CREATE TABLE #tmp` / `ALTER TABLE` など）では閉じてはならない。そうしないと、T-SQL
        // プロシージャが一時テーブルを用意してから実処理する典型パターンで issue #429 が再発する。
        var content =
            "CREATE PROCEDURE dbo.sp_Stage AS\n" +  // line 1
            "BEGIN\n" +                              // line 2
            "  CREATE TABLE #tmp(id INT);\n" +      // line 3 — body-internal DDL, must NOT close
            "  ALTER TABLE #tmp ADD name NVARCHAR(100);\n" + // line 4 — same
            "  EXEC dbo.sp_Inner;\n" +               // line 5 — real EXEC must be inside body
            "END\n" +                                // line 6
            "GO\n" +                                 // line 7
            "CREATE PROCEDURE dbo.sp_Inner AS BEGIN SELECT 1; END\n" + // line 8
            "GO\n";                                  // line 9

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var stage = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Stage");
        Assert.NotNull(stage.BodyStartLine);
        Assert.NotNull(stage.BodyEndLine);
        Assert.True(stage.BodyEndLine!.Value >= 5,
            $"BodyEndLine={stage.BodyEndLine} must cover the EXEC on line 5; body-internal CREATE TABLE / ALTER TABLE must not close the body.");
        Assert.True(stage.BodyEndLine!.Value < 8,
            $"BodyEndLine={stage.BodyEndLine} must not leak into sp_Inner starting on line 8.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_MultiLineBlockCommentDoesNotCloseBody()
    {
        // Regression for codex review iteration 1 finding #2: MaskSqlLineForBodyScan threads an
        // `inBlockComment` state across lines, so a bare `GO` or `CREATE` appearing inside a
        // multi-line `/* ... */` block must not close the enclosing procedure body.
        // codex レビュー iteration 1 指摘 #2 の回帰テスト: MaskSqlLineForBodyScan は `inBlockComment`
        // を行間に持ち越し、複数行の `/* ... */` ブロック内に現れる単独 `GO` / `CREATE` では
        // 外側プロシージャ本体を閉じてはならない。
        var content =
            "CREATE PROCEDURE dbo.sp_CommentHeavy AS\n" + // line 1
            "BEGIN\n" +                                     // line 2
            "  /*\n" +                                      // line 3 — block comment opens
            "   GO\n" +                                     // line 4 — bare GO inside comment
            "   CREATE PROCEDURE dbo.sp_FakeInner AS SELECT 0;\n" + // line 5 — fake header inside comment
            "  */\n" +                                      // line 6 — block comment closes
            "  EXEC dbo.sp_Real;\n" +                       // line 7 — real EXEC must be inside body
            "END\n" +                                       // line 8
            "GO\n" +                                        // line 9 — real terminator
            "CREATE PROCEDURE dbo.sp_Real AS BEGIN SELECT 1; END\n" + // line 10
            "GO\n";                                         // line 11

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var heavy = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_CommentHeavy");
        Assert.NotNull(heavy.BodyStartLine);
        Assert.NotNull(heavy.BodyEndLine);
        Assert.True(heavy.BodyEndLine!.Value >= 7,
            $"BodyEndLine={heavy.BodyEndLine} must cover the real EXEC on line 7; multi-line block comment must not close the body.");
        Assert.True(heavy.BodyEndLine!.Value < 10,
            $"BodyEndLine={heavy.BodyEndLine} must not leak into sp_Real starting on line 10.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_CreateOrAlterClosesPriorBody()
    {
        // Regression for codex review iteration 2 finding #1: SqlTopLevelDdlStartRegex must accept
        // both PostgreSQL `CREATE OR REPLACE PROCEDURE` and T-SQL `CREATE OR ALTER PROCEDURE`
        // (SQL Server 2016+) so a sibling `CREATE OR ALTER PROCEDURE` declaration without an
        // intervening `GO` actually terminates the previous procedure's body range.
        // codex レビュー iteration 2 指摘 #1 の回帰テスト: SqlTopLevelDdlStartRegex は PostgreSQL の
        // `CREATE OR REPLACE PROCEDURE` と T-SQL（SQL Server 2016+）の `CREATE OR ALTER PROCEDURE`
        // の両方を受理し、`GO` 区切りなしの隣接 `CREATE OR ALTER PROCEDURE` 宣言で前 proc の
        // body 範囲を確実に閉じなければならない。
        var content =
            "CREATE OR ALTER PROCEDURE dbo.sp_First AS\n" + // line 1
            "BEGIN\n" +                                     // line 2
            "  EXEC dbo.sp_Inner;\n" +                      // line 3 — must be inside sp_First body
            "END\n" +                                       // line 4
            "CREATE OR ALTER PROCEDURE dbo.sp_Second AS\n" +// line 5 — sibling must close sp_First
            "BEGIN\n" +                                     // line 6
            "  EXEC dbo.sp_Other;\n" +                      // line 7 — must be inside sp_Second body
            "END\n";                                        // line 8

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var first = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_First");
        Assert.NotNull(first.BodyStartLine);
        Assert.NotNull(first.BodyEndLine);
        Assert.True(first.BodyEndLine!.Value >= 3,
            $"BodyEndLine={first.BodyEndLine} must cover sp_First's EXEC on line 3.");
        Assert.True(first.BodyEndLine!.Value < 5,
            $"BodyEndLine={first.BodyEndLine} must close before sp_Second begins on line 5; CREATE OR ALTER PROCEDURE sibling must terminate the prior body.");

        var second = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_Second");
        Assert.NotNull(second.BodyStartLine);
        Assert.NotNull(second.BodyEndLine);
        Assert.True(second.BodyStartLine!.Value >= 5,
            $"BodyStartLine={second.BodyStartLine} for sp_Second must start at or after line 5.");
        Assert.True(second.BodyEndLine!.Value >= 7,
            $"BodyEndLine={second.BodyEndLine} must cover sp_Second's EXEC on line 7.");
    }

    [Fact]
    public void Extract_SQL_ProcedureBodyRange_NestedBlockCommentDoesNotCloseBody()
    {
        // Regression for codex review iteration 2 finding #2: MaskSqlLineForBodyScan must track
        // block-comment depth instead of a plain bool so PostgreSQL-style nested
        // `/* /* ... */ ... */` block comments do not exit on the inner `*/` and re-expose comment-
        // interior `GO` / `CREATE PROCEDURE` tokens that would prematurely close the body.
        // codex レビュー iteration 2 指摘 #2 の回帰テスト: MaskSqlLineForBodyScan は plain bool ではなく
        // ブロックコメント depth を追う必要があり、PostgreSQL 風のネスト `/* /* ... */ ... */` を
        // 内側の `*/` で誤って抜けてはならない。抜けるとコメント内部の `GO` / `CREATE PROCEDURE` が
        // 露出し、本体を早期に閉じてしまう。
        var content =
            "CREATE PROCEDURE dbo.sp_NestedComment AS\n" +    // line 1
            "BEGIN\n" +                                         // line 2
            "  /*\n" +                                          // line 3 — outer block opens
            "   /*\n" +                                         // line 4 — inner block opens
            "     GO\n" +                                       // line 5 — bare GO in nested comment
            "     CREATE PROCEDURE dbo.sp_FakeNested AS SELECT 0;\n" + // line 6 — fake header
            "   */\n" +                                         // line 7 — inner closes; outer still open
            "   GO\n" +                                         // line 8 — still inside outer comment
            "   CREATE PROCEDURE dbo.sp_FakeOuter AS SELECT 0;\n" + // line 9 — still inside outer
            "  */\n" +                                          // line 10 — outer closes
            "  EXEC dbo.sp_RealCall;\n" +                       // line 11 — real EXEC must be inside body
            "END\n" +                                           // line 12
            "GO\n" +                                            // line 13 — real terminator
            "CREATE PROCEDURE dbo.sp_AfterNested AS BEGIN SELECT 1; END\n" + // line 14
            "GO\n";                                             // line 15

        var symbols = SymbolExtractor.Extract(1, "sql", content);

        var nested = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "dbo.sp_NestedComment");
        Assert.NotNull(nested.BodyStartLine);
        Assert.NotNull(nested.BodyEndLine);
        Assert.True(nested.BodyEndLine!.Value >= 11,
            $"BodyEndLine={nested.BodyEndLine} must cover the real EXEC on line 11; nested block comment must not close the body via the inner `*/`.");
        Assert.True(nested.BodyEndLine!.Value < 14,
            $"BodyEndLine={nested.BodyEndLine} must not leak into sp_AfterNested starting on line 14.");
    }

    [Fact]
    public void Extract_SQL_DetectsOraclePlSqlDdlKinds()
    {
        // Oracle PL/SQL — PACKAGE / PACKAGE BODY / TYPE / TYPE BODY / DATABASE LINK / DIRECTORY /
        // CONTEXT / PROFILE must all be captured, and object names may contain `$` / `#`.
        // Oracle PL/SQL — PACKAGE / PACKAGE BODY / TYPE / TYPE BODY / DATABASE LINK / DIRECTORY /
        // CONTEXT / PROFILE を全て捕捉し、オブジェクト名に `$` / `#` を含められる。
        var content =
            "CREATE OR REPLACE PACKAGE orders_pkg IS\n" +
            "  PROCEDURE insert_order(p_id IN NUMBER);\n" +
            "END orders_pkg;\n" +
            "/\n" +
            "CREATE OR REPLACE PACKAGE BODY orders_pkg IS\n" +
            "  PROCEDURE insert_order(p_id IN NUMBER) IS BEGIN NULL; END;\n" +
            "END orders_pkg;\n" +
            "/\n" +
            "CREATE OR REPLACE TYPE address_t AS OBJECT (street VARCHAR2(100));\n" +
            "/\n" +
            "CREATE OR REPLACE TYPE BODY address_t AS\n" +
            "END;\n" +
            "/\n" +
            "CREATE SEQUENCE hr.order_seq START WITH 1 INCREMENT BY 1;\n" +
            "CREATE PUBLIC SYNONYM customer_v FOR schema1.customers;\n" +
            "CREATE PUBLIC DATABASE LINK remote_db CONNECT TO app IDENTIFIED BY \"x\" USING 'REMOTE';\n" +
            "CREATE SHARED PUBLIC DATABASE LINK remote_shared_pub_db CONNECT TO app IDENTIFIED BY \"x\" USING 'REMOTE';\n" +
            "CREATE SHARED DATABASE LINK remote_shared_db CONNECT TO app IDENTIFIED BY \"x\" USING 'REMOTE';\n" +
            "CREATE DIRECTORY data_dir AS '/var/oracle/data';\n" +
            "CREATE CONTEXT app_ctx USING app_pkg;\n" +
            "CREATE PROFILE app_profile LIMIT SESSIONS_PER_USER 5;\n" +
            "CREATE TABLE SYS$ITEMS#1 (id NUMBER);\n" +
            "ALTER PACKAGE orders_pkg COMPILE;\n" +
            "ALTER PACKAGE orders_pkg COMPILE BODY;\n" +
            "ALTER TYPE address_t COMPILE BODY;\n" +
            "ALTER DATABASE LINK remote_db;\n" +
            "ALTER DIRECTORY data_dir AS '/var/oracle/data2';\n" +
            "ALTER PROFILE app_profile LIMIT SESSIONS_PER_USER 10;\n";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        // PACKAGE spec/body both captured (BODY is not absorbed as the package name)
        // PACKAGE spec / body の両方が取れ、`BODY` が package name に吸い込まれない
        Assert.Equal(2, symbols.Count(s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.Contains("PACKAGE BODY", StringComparison.OrdinalIgnoreCase));

        // TYPE + TYPE BODY
        Assert.Equal(2, symbols.Count(s => s.Kind == "class" && s.Name == "address_t" && s.Signature != null && s.Signature.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "address_t" && s.Signature != null && s.Signature.Contains("TYPE BODY", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "hr.order_seq");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "customer_v");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_db" && s.Signature != null && s.Signature.Contains("DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        // Oracle allows `CREATE [SHARED] [PUBLIC] DATABASE LINK` — both modifiers may appear together.
        // Oracle は `CREATE [SHARED] [PUBLIC] DATABASE LINK` で両修飾子の 2 語並びも取る。
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_shared_pub_db" && s.Signature != null && s.Signature.Contains("SHARED PUBLIC DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_shared_db" && s.Signature != null && s.Signature.Contains("SHARED DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "data_dir" && s.Signature != null && s.Signature.StartsWith("CREATE DIRECTORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_ctx" && s.Signature != null && s.Signature.StartsWith("CREATE CONTEXT", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_profile" && s.Signature != null && s.Signature.StartsWith("CREATE PROFILE", StringComparison.OrdinalIgnoreCase));

        // Oracle identifiers may contain `$` / `#`
        // Oracle 識別子は `$` / `#` を含められる
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SYS$ITEMS#1");

        // `BODY` keyword is NOT treated as the object name — these assertions would fail if the
        // generic PACKAGE / TYPE rows absorbed the `BODY` token.
        // `BODY` キーワードは name として取られない — generic な PACKAGE / TYPE 行が `BODY` を
        // 飲み込んでしまうと以下の Assert が失敗する
        Assert.DoesNotContain(symbols, s => s.Name == "BODY");

        // `LINK` keyword must not be eaten by the generic CREATE DATABASE row.
        // `LINK` が generic な CREATE DATABASE 行に食われないこと
        Assert.DoesNotContain(symbols, s => s.Name == "LINK");

        // ALTER counterparts — Oracle body compilation uses `ALTER PACKAGE <name> COMPILE BODY` /
        // `ALTER TYPE <name> COMPILE BODY`, not a `BODY <name>` keyword position.
        // ALTER 側 — Oracle の body コンパイルは `ALTER PACKAGE <name> COMPILE BODY` /
        // `ALTER TYPE <name> COMPILE BODY` であり、`BODY <name>` という位置取りではない。
        Assert.Equal(2, symbols.Count(s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.StartsWith("ALTER PACKAGE ", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "orders_pkg" && s.Signature != null && s.Signature.Contains("COMPILE BODY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "address_t" && s.Signature != null && s.Signature.StartsWith("ALTER TYPE ", StringComparison.OrdinalIgnoreCase) && s.Signature.Contains("COMPILE BODY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "remote_db" && s.Signature != null && s.Signature.StartsWith("ALTER DATABASE LINK", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "data_dir" && s.Signature != null && s.Signature.StartsWith("ALTER DIRECTORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app_profile" && s.Signature != null && s.Signature.StartsWith("ALTER PROFILE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_SQL_KeepsQualifiedNamesWhenDotsHaveSurroundingWhitespace()
    {
        var content =
            "CREATE SCHEMA sales . reporting;\n" +
            "CREATE SCHEMA AUTHORIZATION sales . auth_owner;\n" +
            "CREATE SEQUENCE sales . seq_orders START WITH 1;\n" +
            "CREATE EXTENSION \"sales\" . \"ext_demo\";\n" +
            "CREATE SYNONYM [sales] . [syn_demo] FOR dbo.target;\n" +
            "CREATE DATABASE LINK sales . remote_db CONNECT TO app IDENTIFIED BY 'x' USING 'REMOTE';\n" +
            "CREATE LOGIN sales . app_login WITH PASSWORD = 'x';\n" +
            "CREATE PARTITION FUNCTION sales . pf_orders (int) AS RANGE LEFT FOR VALUES (1);\n" +
            "CREATE PARTITION SCHEME sales . ps_orders AS PARTITION sales . pf_orders ALL TO ([PRIMARY]);\n" +
            "CREATE FULLTEXT CATALOG sales . ft_catalog;\n" +
            "CREATE INDEX sales . idx_users_email ON dbo.Users (Email);\n" +
            "ALTER PARTITION FUNCTION sales . pf_orders() SPLIT RANGE (2);\n" +
            "ALTER SCHEMA sales . reporting TRANSFER dbo.Users;\n" +
            "ALTER EXTENSION \"sales\" . \"ext_demo\" UPDATE TO '2.0';\n" +
            "ALTER DATABASE LINK sales . remote_db;\n" +
            "ALTER SEQUENCE sales . seq_orders RESTART WITH 10;\n" +
            "ALTER SYNONYM [sales] . [syn_demo] FOR dbo.target;\n" +
            "ALTER LOGIN sales . app_login WITH DEFAULT_DATABASE = master;\n" +
            "ALTER INDEX sales . idx_users_email REBUILD;\n" +
            "ALTER PARTITION SCHEME sales . ps_orders NEXT USED [PRIMARY];\n" +
            "ALTER FULLTEXT CATALOG sales . ft_catalog REORGANIZE;\n";
        var symbols = SymbolExtractor.Extract(1, "sql", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales.reporting");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "sales.auth_owner");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.seq_orders");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "\"sales\".\"ext_demo\"");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[sales].[syn_demo]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.remote_db");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.app_login");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "sales.pf_orders");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.ps_orders");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.ft_catalog");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "sales.idx_users_email");
    }

    [Fact]
    public void Extract_Terraform_DetectsResources()
    {
        var content =
            "resource \"aws_s3_bucket\" \"my_bucket\" {\n  bucket = \"my-bucket\"\n}\n\n" +
            "provider \"aws\" {\n  region = \"us-east-1\"\n}\n\n" +
            "terraform {\n  required_version = \">= 1.0\"\n}\n\n" +
            "import {\n  id = \"bucket-123\"\n}\n\n" +
            "moved {\n  from = aws_s3_bucket.old_bucket\n  to = aws_s3_bucket.my_bucket\n}\n\n" +
            "removed {\n  from = aws_s3_bucket.deprecated_bucket\n}\n\n" +
            "check \"health\" {\n  assert {\n    condition = true\n  }\n}\n\n" +
            "locals {\n  region = \"us-east-1\"\n}\n\n" +
            "variable \"region\" {\n  default = \"us-east-1\"\n}\n\n" +
            "output \"bucket_arn\" {\n  value = aws_s3_bucket.my_bucket.arn\n}\n\n" +
            "module \"vpc\" {\n  source = \"./modules/vpc\"\n}";
        var symbols = SymbolExtractor.Extract(1, "terraform", content);

        // resource captures logical name (second quoted token), not provider type
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "my_bucket");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "aws");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "terraform");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "import");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "moved");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "removed");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "health");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "locals");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "region");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bucket_arn");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vpc");
    }

    [Fact]
    public void Extract_PHP_DetectsExpandedFeatures()
    {
        var content = """
            <?php
            namespace App\Models;

            use Illuminate\Database\Eloquent\Model;

            readonly class Config {
                public const VERSION = '1.0';
                public function getName(): string { return ''; }
            }

            enum Status: string {
                case Active = 'active';
                case Pending = 'pending';
            }

            enum OrderStatus {
                case Draft;
                case Published;
            }

            enum Priority: int {
                case Low = 1;

                public function label(): string {
                    return match ($this) {
                        Priority::Low => 'low',
                    };
                }
            }

            function inspectState(int $state): void {
                switch ($state) {
                    case 1:
                        break;
                    case 'x':
                        break;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name.Contains("App"));
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("Model"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "getName");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "OrderStatus");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Priority" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Active" && s.ContainerName == "Status" && s.ReturnType == "'active'");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Pending" && s.ContainerName == "Status" && s.ReturnType == "'pending'");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Draft" && s.ContainerName == "OrderStatus" && s.ReturnType == null);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Published" && s.ContainerName == "OrderStatus" && s.ReturnType == null);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Low" && s.ContainerName == "Priority" && s.ReturnType == "1");
        Assert.Equal(5, symbols.Count(s => s.Kind == "property"));
    }

    [Fact]
    public void Extract_PHP_DetectsImportAliasesGroupUseAndLiteralRequires()
    {
        var content = """
            <?php

            use Closure;
            use Illuminate\Auth\Middleware\Authenticate;
            use Illuminate\Support\Arr as A;
            use function Laravel\Prompts\text;
            use const Foo\Bar\BAZ;
            use X\Y\{A, B as C, D};

            require 'static/path.php';
            require __DIR__ . '/bootstrap.php';
            require_once dirname(__FILE__) . '/legacy.php';
            require $variable;
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);
        var imports = symbols.Where(s => s.Kind == "import").ToList();

        Assert.Equal(11, imports.Count);
        Assert.Contains(imports, s => s.Name == "Closure");
        Assert.Contains(imports, s => s.Name == "Illuminate\\Auth\\Middleware\\Authenticate");
        Assert.Contains(imports, s => s.Name == "A");
        Assert.Contains(imports, s => s.Name == "Laravel\\Prompts\\text");
        Assert.Contains(imports, s => s.Name == "Foo\\Bar\\BAZ");
        Assert.Contains(imports, s => s.Name == "X\\Y\\A");
        Assert.Contains(imports, s => s.Name == "C");
        Assert.Contains(imports, s => s.Name == "X\\Y\\D");
        Assert.Contains(imports, s => s.Name == "static/path.php");
        Assert.Contains(imports, s => s.Name == "bootstrap.php");
        Assert.Contains(imports, s => s.Name == "legacy.php");
        Assert.DoesNotContain(imports, s => s.Name.Contains("__DIR__", StringComparison.Ordinal));
        Assert.DoesNotContain(imports, s => s.Name.Contains("$variable", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_PHP_GroupUseAliasHandlesTokenBoundariesAndPathsContainingAs()
    {
        var content = "<?php\nuse Foo\\{Bar\\As_thing as alias, Other\\Item\tas\tAliasedItem, Plain};\n";

        var symbols = SymbolExtractor.Extract(1, "php", content);
        var imports = symbols.Where(s => s.Kind == "import").Select(s => s.Name).ToList();

        Assert.Contains("alias", imports);
        Assert.Contains("AliasedItem", imports);
        Assert.Contains("Foo\\Plain", imports);
        Assert.DoesNotContain(imports, name => name.Contains(" as ", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(imports, name => name.Contains('\t'));
    }

    [Fact]
    public void Extract_PHP_DetectsClassProperties()
    {
        var content = """
            <?php
            class User {
                public string $name;
                protected static ?Profile $profile = null;
                var $legacy;

                public function rename(string $name): void {
                    $local = $name;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ReturnType == "string" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "profile" && s.ReturnType == "?Profile" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "legacy" && s.ContainerName == "User");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local");
    }

    [Fact]
    public void Extract_PHP_DetectsAdditionalSameLineClassProperties()
    {
        var content = """
            <?php
            class User {
                public string $firstName, $lastName;
                protected $flags = ['a', 'b'], $state;
                private $literal = ", $notAProperty", $real;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "firstName" && s.ReturnType == "string" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "lastName" && s.ReturnType == "string" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "flags" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "state" && s.ContainerName == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real" && s.ContainerName == "User");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "notAProperty");
    }

    [Fact]
    public void Extract_PHP_DetectsPropertyHookAccessors()
    {
        var content = """
            <?php
            class User {
                public string $displayName {
                    get => $this->firstName . ' ' . $this->lastName;
                    set {
                        $this->_displayName = strtoupper($value);
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        var property = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "displayName");
        Assert.Equal("php_property_hook", property.SubKind);
        Assert.Equal(3, property.StartLine);
        Assert.Equal(8, property.EndLine);
        Assert.Equal(3, property.BodyStartLine);
        Assert.Equal(8, property.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "accessor" && s.Name == "displayName.get" && s.ContainerKind == "property" && s.ContainerName == "displayName");
        Assert.Contains(symbols, s => s.Kind == "accessor" && s.Name == "displayName.set" && s.ContainerKind == "property" && s.ContainerName == "displayName" && s.BodyEndLine == 7);
    }

    [Fact]
    public void Extract_PHP_DetectsSameLinePromotedConstructorProperties()
    {
        var content = """
            <?php
            class User {
                public function __construct(public string $id, private readonly ?Profile $profile, string $local = 'x,y') {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "id" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "profile" && s.ReturnType == "?Profile");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local");

        var profile = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "profile");
        var profileLine = content.Split('\n')[profile.Line - 1];
        Assert.NotNull(profile.StartColumn);
        Assert.Equal('p', profileLine[profile.StartColumn.Value]);
    }

    [Fact]
    public void Extract_PHP_DetectsMultilinePromotedConstructorProperties()
    {
        var content = """
            <?php
            class User {
                public function __construct(
                    // public string $commented,
                    /* private string $blockCommented, */
                    public string $id,
                    private readonly ?Profile $profile,
                    string $local = 'x,y',
                ) {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "id" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "profile" && s.ReturnType == "?Profile");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "commented");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "blockCommented");
    }

    [Fact]
    public void Extract_PHP_DetectsMethodsWithModifiersBeforeVisibility()
    {
        var content = """
            <?php
            abstract class BaseService {
                abstract protected function normalize();
                final public static function make(): self {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize" && s.Visibility == "protected");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "make" && s.Visibility == "public");
    }

    [Fact]
    public void Extract_PHP_DetectsVariableBoundClosures()
    {
        var content = """
            <?php
            $handler = function (Request $request) {
                return $request;
            };
            $mapper = fn (User $user) => $user->id;
            $staticFactory = static function () {
                return new User();
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "mapper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "staticFactory");
    }

    [Fact]
    public void Extract_PHP_DetectsDocblockMethods()
    {
        var content = """
            <?php
            /**
             * @method static Builder<User> whereEmail(string $email)
             * @method ?User findByEmail(string $email)
             * @method User|Guest resolveActor(int $id)
             * @method refresh()
             */
            class UserQuery {}
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "whereEmail" && s.ReturnType == "Builder<User>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "findByEmail" && s.ReturnType == "?User");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "resolveActor" && s.ReturnType == "User|Guest");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "refresh" && s.ReturnType == null);
    }

    [Fact]
    public void Extract_PHP_DetectsDocblockProperties()
    {
        var content = """
            <?php
            /**
             * @property \App\Models\User $owner
             * @property-read Collection<User> $items
             * @property-write ?string $status
             * @phpstan-property-read Money $balance
             */
            class UserPresenter {}
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "owner" && s.ReturnType == "\\App\\Models\\User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "items" && s.ReturnType == "Collection<User>");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "status" && s.ReturnType == "?string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "balance" && s.ReturnType == "Money");
    }

    [Fact]
    public void Extract_PHP_DocblockProperties_DuplicateForSameName_EmitsOnce()
    {
        var content = """
            <?php
            /**
             * @property OwnerA $name
             * @property OwnerB $name
             * @property-read OwnerC $title
             * @property-write OwnerD $title
             */
            class UserPresenter {}

            /**
             * @property OwnerE $name
             */
            class Other {}
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);
        var docblockProperties = symbols.Where(s => s.Kind == "property").ToList();

        Assert.Equal(3, docblockProperties.Count(s => s.Name == "name" || s.Name == "title"));
        Assert.Single(docblockProperties, s => s.Name == "name" && s.ReturnType == "OwnerA");
        Assert.DoesNotContain(docblockProperties, s => s.ReturnType == "OwnerB");
        Assert.Single(docblockProperties, s => s.Name == "title" && s.ReturnType == "OwnerC");
        Assert.DoesNotContain(docblockProperties, s => s.ReturnType == "OwnerD");
        Assert.Single(docblockProperties, s => s.Name == "name" && s.ReturnType == "OwnerE");
    }

    [Fact]
    public void Extract_PHP_DetectsTraitAliasMethods()
    {
        var content = """
            <?php
            class User {
                use Timestampable {
                    Timestampable::touch as touchTimestamp;
                    touch as private;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "touchTimestamp");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "private");
    }

    [Fact]
    public void Extract_PHP_DetectsDocblockTypeAliases()
    {
        var content = """
            <?php
            /**
             * @phpstan-type UserShape array{id:int,name:string}
             * @psalm-type EmailAddress non-empty-string
             * @phpstan-import-type RemoteShape from \App\Types\RemoteSource as LocalShape
             * @psalm-import-type ExternalShape from \App\Types\ExternalSource
             */
            class UserTypes {}
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "type" && s.Name == "UserShape" && s.ReturnType == "array{id:int,name:string}");
        Assert.Contains(symbols, s => s.Kind == "type" && s.Name == "EmailAddress" && s.ReturnType == "non-empty-string");
        Assert.Contains(symbols, s => s.Kind == "type" && s.Name == "LocalShape" && s.ReturnType == "\\App\\Types\\RemoteSource");
        Assert.Contains(symbols, s => s.Kind == "type" && s.Name == "ExternalShape" && s.ReturnType == "\\App\\Types\\ExternalSource");
    }

    [Fact]
    public void Extract_PHP_DetectsDefineConstants()
    {
        var content = """
            <?php
            define('APP_ENV', 'testing');
            define("FEATURE_FLAG", true);
            DEFINE('UPPER_DEFINE', true);
            define($dynamic, true);
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "APP_ENV");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "FEATURE_FLAG");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "UPPER_DEFINE");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "dynamic");
    }

    [Fact]
    public void Extract_PHP_DetectsTypedClassConstants()
    {
        var content = """
            <?php
            class Config {
                public const string VERSION = '1.0';
                protected const int|float LIMIT = 10;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION" && s.ReturnType == "string" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "LIMIT" && s.ReturnType == "int|float" && s.Visibility == "protected");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "string");
    }

    [Fact]
    public void Extract_Swift_DetectsActorAndTypealias()
    {
        var content = "public actor NetworkManager {\n    func fetch() { }\n}\n\npublic typealias Handler = (Data) -> Void\n\ntypealias UserID = Int\npublic typealias Callback = (Int) -> Int\n\ndistributed actor RemoteWorker { }";
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "NetworkManager");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "UserID");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RemoteWorker");
    }

    [Fact]
    public void Extract_Swift_DetectsBacktickEscapedTypealiasAndWhereClause()
    {
        var content = """
            public typealias `Callback`<T> = (T) -> Void where T: Sendable
            typealias ResultHandler<T> where T: Hashable = (T) -> Void

            public protocol Store {
                associatedtype Item
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "`Callback`");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "ResultHandler");
        Assert.Contains(symbols, s => s.Kind == "associatedtype" && s.Name == "Item");
    }

    [Fact]
    public void Extract_Swift_NormalizesGranularImportNames()
    {
        var content = """
            import Foundation
            import struct Foundation.URL
            import enum Dispatch.DispatchQoS
            import func Darwin.C.printf
            public import Logging
            package import struct PackageKit.Token
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Foundation");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Foundation.URL");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Dispatch.DispatchQoS");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Darwin.C.printf");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Logging");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "PackageKit.Token");
        Assert.DoesNotContain(symbols, s =>
            s.Kind == "import"
            && (s.Name.StartsWith("struct ", StringComparison.Ordinal)
                || s.Name.StartsWith("enum ", StringComparison.Ordinal)
                || s.Name.StartsWith("func ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Extract_Swift_DetectsOperatorFunctionDeclarations()
    {
        var content = """
            struct Vector {
                static func + (lhs: Vector, rhs: Vector) -> Vector { lhs }
                static func == (lhs: Vector, rhs: Vector) -> Bool { true }
            }

            prefix func ! (value: Vector) -> Vector { value }
            """;

        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "+");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "==");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "!");
    }

    [Fact]
    public void Extract_Swift_DetectsPrivateSetProperties()
    {
        var content = """
            public struct Counter {
                private(set) var value = 0
                fileprivate(set) var label = "shared"
                public(set) var capacity = 10
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Counter");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "label");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "capacity");
    }

    [Fact]
    public void Extract_Swift_DetectsEnumCasesAndIndirectEnums()
    {
        var content = """
            public enum NetworkError: Error {
                case timeout
                case server(code: Int, message: String)
                case client(Int)
                case unknown
                case badRequest, unauthorized, serverError(Int)
            }

            indirect enum Tree<T> {
                case leaf(T)
                indirect case node(Tree<T>, Tree<T>)
            }

            enum Status: String, Codable {
                case active
                case inactive = "off"
                case pending
            }

            enum HTTPStatus: Int {
                case accepted = 202, gone = 410
            }

            enum Phrase: String {
                case greeting = "hello, world", farewell = "bye, now"
            }

            func handle(_ error: NetworkError) {
                switch error {
                case .overheated:
                    break
                case let .recoverable(code, message):
                    break
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "swift", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "NetworkError");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Tree");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "HTTPStatus");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Phrase");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "timeout");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "server");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "client");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "unknown");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "badRequest");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "unauthorized");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "serverError");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "leaf");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "node");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "active");

        var inactive = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "inactive"));
        Assert.Equal("\"off\"", inactive.ReturnType);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "pending");

        var accepted = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "accepted"));
        Assert.Equal("202", accepted.ReturnType);
        var gone = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "gone"));
        Assert.Equal("410", gone.ReturnType);

        var greeting = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "greeting"));
        Assert.Equal("\"hello, world\"", greeting.ReturnType);
        var farewell = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "farewell"));
        Assert.Equal("\"bye, now\"", farewell.ReturnType);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "overheated");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "recoverable");
    }

    [Fact]
    public void Extract_ObjC_DetectsInterfacesPropertiesMethodsAndImports()
    {
        var content = """
            #import <Foundation/Foundation.h>

            @interface Dog : NSObject
            @property (nonatomic, strong) NSString *name;
            @property (nonatomic, assign) NSInteger age;
            - (void)bark;
            - (NSString *)greet:(NSString *)greeting withName:(NSString *)name;
            + (instancetype)dogWithName:(NSString *)name;
            @end

            @protocol Animal <NSObject>
            - (void)move;
            @optional
            - (NSString *)describe;
            @end

            @interface Dog (Testing)
            - (BOOL)isReady;
            @end

            @implementation Dog
            - (void)bark {
                NSLog(@"Woof!");
            }
            - (BOOL)isReady {
                return YES;
            }
            @end

            typedef NS_ENUM(NSInteger, FruitType) {
                FruitTypeApple,
                FruitTypeOrange,
            };

            typedef NS_EXTENSIBLE_ENUM(NSInteger, FruitMood) {
                FruitMoodRipe,
            };

            typedef NS_OPTIONS(NSUInteger, FruitOptions) {
                FruitOptionJuicy = 1 << 0,
                FruitOptionCitrus = 1 << 1,
            };

            typedef NS_ERROR_ENUM(NSInteger, FruitError) {
                FruitErrorUnknown,
            };

            typedef CF_ENUM(NSInteger, FruitKind) {
                FruitKindFresh,
            };

            typedef CF_OPTIONS(NSUInteger, FruitFlags) {
                FruitFlagTasty = 1 << 0,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "objc", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Dog");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Dog(Testing)");
        Assert.True(symbols.Count(s => s.Kind == "class" && s.Name == "Dog") >= 2);
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Animal");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "FruitType");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "FruitMood");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "FruitOptions");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "FruitError");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "FruitKind");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "FruitFlags");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "age");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bark");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "isReady");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "greet");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dogWithName");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "move");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "describe");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Foundation/Foundation.h");
    }

    [Fact]
    public void Extract_Pascal_DetectsUnitsTypesMembersAndUses()
    {
        var content = """
            unit Demo;

            interface

            uses SysUtils, AppTypes;

            type
              TColor = (Red, Green, Blue);
              TPoint = record
                X: Integer;
              end;
              IService = interface
              end;
              TService = class
              public
                constructor Create;
                procedure Run(input: TUser);
                property Name: string read FName;
              end;

            implementation

            function BuildService: TService;
            begin
            end;

            end.
            """;

        var symbols = SymbolExtractor.Extract(1, "pascal", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Demo");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "TColor");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "TPoint");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "IService");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "TService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Create");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Run");
        var buildService = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "BuildService");
        Assert.NotNull(buildService.BodyStartLine);
        Assert.NotNull(buildService.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "SysUtils, AppTypes");
    }

    [Fact]
    public void Extract_Smalltalk_DetectsClassesAndMethods()
    {
        var content = """
            Object subclass: #UserService

            UserService >> run
                self prepare.

            UserService class >> save:
                self flush.

            UserService >> save: user with: options
                self persist.
            """;

        var symbols = SymbolExtractor.Extract(1, "smalltalk", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        var run = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "run");
        var save = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "save:");
        var saveWith = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "save:with:");
        Assert.NotNull(run.BodyStartLine);
        Assert.NotNull(run.BodyEndLine);
        Assert.NotNull(save.BodyStartLine);
        Assert.NotNull(save.BodyEndLine);
        Assert.NotNull(saveWith.BodyStartLine);
        Assert.NotNull(saveWith.BodyEndLine);
    }

    [Fact]
    public void Extract_Ruby_DetectsAttrAndRailsDSL()
    {
        var content = "class User < ActiveRecord::Base\n  attr_accessor :name\n  attr_reader :email\n  has_many :posts\n  belongs_to :company\n  scope :active\n  enum :status\n  attribute :timezone, :string\n\n  def initialize(name)\n    @name = name\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "email");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "status");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "timezone");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "posts");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "company");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "active");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "initialize");
    }

    [Fact]
    public void Extract_Ruby_DetectsAttrListsAndAliases()
    {
        var content = """
            class Person
              attr_accessor :name, :age, :email
              attr_reader :id, :created_at
              attr_writer :internal_flag, :debug_count
              store_accessor :settings, :theme, :locale
              attr_accessor :nickname

              def greet(greeting = "Hello")
                "#{greeting}, #{name}"
              end

              alias_method :full_name, :name
              alias_method :profile!, :name
              alias greet_alias greet
              alias shout! greet
              alias :display_name :name
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "name"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "age"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "email"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "id"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "created_at"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "internal_flag"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "debug_count"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "theme"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "locale"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "property" && s.Name == "nickname"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "greet");
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "full_name"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "profile!"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "greet_alias"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "shout!"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "display_name"));
    }

    [Fact]
    public void Extract_Ruby_DetectsQualifiedClassAndModuleNames()
    {
        var content = """
            module Admin::Billing
              class Admin::Billing::Invoice
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Admin::Billing");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Admin::Billing::Invoice");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Admin");
    }

    [Fact]
    public void Extract_Ruby_DetectsReceiverQualifiedSingletonMethods()
    {
        var content = """
            class Admin::User
              def self.find
              end

              def Admin::User.export!
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "find");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "export!");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Admin");
    }

    [Fact]
    public void Extract_Ruby_NormalizesRequireImportNames()
    {
        var content = """
            require "active_support/core_ext/string"
            require_relative 'models/user'
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "active_support/core_ext/string");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "models/user");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "\"active_support/core_ext/string\"");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "'models/user'");
    }

    [Fact]
    public void Extract_Ruby_DetectsConstantAssignments()
    {
        var content = """
            class Client
              MAX_RETRIES = 3
              DefaultTimeout = 30
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "MAX_RETRIES");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DefaultTimeout");
    }

    [Fact]
    public void Extract_Ruby_DetectsClassNewBlockAssignments()
    {
        var content = """
            User = Class.new(ApplicationRecord) do
              def active?
                true
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "active?"
            && s.ContainerKind == "class"
            && s.ContainerName == "User");
    }

    [Fact]
    public void Extract_Ruby_DetectsStructNewBlockAssignments()
    {
        var content = """
            Result = Struct.new(:ok, :value) do
              def success?
                ok
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Result");
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "success?"
            && s.ContainerKind == "class"
            && s.ContainerName == "Result");
    }

    [Fact]
    public void Extract_Ruby_DetectsOperatorMethodDefinitions()
    {
        var content = """
            class Collection
              def [](index)
              end

              def self.[]=(key, value)
              end

              def <=>(other)
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[]");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "[]=");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "<=>");
    }

    [Fact]
    public void Extract_Ruby_DetectsVisibilityModifiedMethodDefinitions()
    {
        var content = """
            class Client
              private def secret
              end

              protected def token?
              end

              public def self.build!
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "secret");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "token?");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build!");
    }

    [Fact]
    public void Extract_Ruby_DetectsRakeTaskDefinitions()
    {
        var content = """
            task :build do
            end

            task test: :environment do
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "test");
    }

    [Fact]
    public void Extract_Ruby_DetectsRakeNamespaces()
    {
        var content = """
            namespace :db do
              task :migrate do
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "db");
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "migrate"
            && s.ContainerKind == "namespace"
            && s.ContainerName == "db");
    }

    [Fact]
    public void Extract_Ruby_DetectsFactoryBotFactoryDefinitions()
    {
        var content = """
            FactoryBot.define do
              factory :user do
                name { "Ada" }
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        var factory = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "user"));
        Assert.NotNull(factory.BodyStartLine);
        Assert.NotNull(factory.BodyEndLine);
    }

    [Fact]
    public void Extract_Ruby_DetectsRSpecLetDefinitions()
    {
        var content = """
            shared_examples "auditable" do
              it "tracks changes" do
              end
            end

            RSpec.describe User do
              subject(:profile) do
                build(:profile)
              end

              let(:user) do
                build(:user)
              end

              let!(:account) do
                create(:account)
              end
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        var sharedExamples = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "auditable"));
        var profile = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "profile"));
        var user = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "user"));
        var account = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "account"));
        Assert.NotNull(sharedExamples.BodyStartLine);
        Assert.NotNull(profile.BodyStartLine);
        Assert.NotNull(user.BodyStartLine);
        Assert.NotNull(account.BodyEndLine);
    }

    [Fact]
    public void Extract_Rust_DetectsExpandedFeatures()
    {
        var content = "macro_rules! my_macro {\n    () => {};\n}\n\npub mod utils {\n}\n\nconst MAX_SIZE: usize = 1024;\nstatic COUNTER: AtomicU32 = AtomicU32::new(0);\npub const fn default_value() -> i32 { 42 }\npub unsafe fn raw_ptr() { }\npub extern fn no_abi() { }\npub unsafe extern \"C-unwind\" fn ffi_entry() { }\ndefault async fn trait_default() { }\ntype Result<T> = std::result::Result<T, Error>;\ntrait Iter {\n    type Item;\n    fn next(&mut self) -> Option<Self::Item>;\n}\ntype Callback = fn(i32) -> i32;\npub union MyUnion { f: f32 }";
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_macro");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "utils");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_SIZE");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "COUNTER");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "default_value");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "raw_ptr");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "no_abi");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ffi_entry");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "trait_default");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Result");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Item");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "MyUnion");
    }

    [Fact]
    public void Extract_Rust_DetectsUnsafeBlockContainer()
    {
        const string content = """
            fn demo() {
                unsafe {
                    let p = Box::leak(Box::new(42));
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);

        var unsafeBlock = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "unsafe");
        Assert.Equal(2, unsafeBlock.Line);
        Assert.Equal(2, unsafeBlock.BodyStartLine);
        Assert.Equal(4, unsafeBlock.BodyEndLine);
    }

    [Fact]
    public void Extract_Rust_DetectsUnsafeBlockInExpression()
    {
        const string content = """
            fn demo() {
                let value = unsafe {
                    read_raw()
                };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);

        var unsafeBlock = Assert.Single(symbols, s => s.Kind == "class" && s.Name == "unsafe");
        Assert.Equal(2, unsafeBlock.Line);
        Assert.Equal(2, unsafeBlock.BodyStartLine);
        Assert.Equal(4, unsafeBlock.BodyEndLine);
    }

    [Fact]
    public void Extract_Rust_DetectsPubUseStatements()
    {
        var content = """
            pub use std::fmt::Display;
            pub(crate) use std::io::Result as IoResult;
            use std::collections::HashMap;
            pub use std::collections::*;
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::fmt::Display");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::io::Result as IoResult");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::collections::HashMap");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Display");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "IoResult");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "HashMap");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "*");
    }

    [Fact]
    public void Extract_RustTraitAssociatedTypeDefaults_RecordsPropertySymbols()
    {
        const string content = """
            trait Builder {
                fn before(&self) {
                    println!("{");
                }
                type Output = ();
                type Error: std::error::Error = String;
                type Pending;
                fn build(&self) {
                    type Local = String;
                }
                fn helper<'a>(&self) {
                    type Borrowed = &'a str;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.Name == "Output"
            && s.ContainerKind == "protocol"
            && s.ContainerName == "Builder"
            && s.ReturnType == "()");
        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.Name == "Error"
            && s.ContainerKind == "protocol"
            && s.ContainerName == "Builder"
            && s.ReturnType == "String");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Pending");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Local");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Borrowed");
    }

    [Fact]
    public void Extract_Rust_DetectsGroupedUseTreePrefixes()
    {
        var content = """
            use std::collections::{HashMap, HashSet as Set};
            pub use crate::io::{self, Result as IoResult, Write};
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::collections::HashMap");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "HashMap");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::collections::HashSet as Set");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "HashSet");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Set");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "crate::io::Result as IoResult");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "crate::io::Write");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "crate::io");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Result");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "IoResult");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Write");
    }

    [Fact]
    public void Extract_Rust_DetectsMultilineUseTrees()
    {
        var content = """
            use std::{
                fmt::Display,
                io::{Result as IoResult, Write},
            };
            pub(crate) use crate::{
                net::Client,
                net::server::Server as NetServer,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::fmt::Display");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Display" && s.Line == 2 && s.StartColumn == 10);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::io::Result as IoResult");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "IoResult" && s.Line == 3 && s.StartColumn == 20);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::io::Write");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Write" && s.Line == 3 && s.StartColumn == 30);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "crate::net::Client");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Client" && s.Line == 6 && s.StartColumn == 10);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "crate::net::server::Server as NetServer");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "NetServer" && s.Line == 7 && s.StartColumn == 28);
    }

    [Fact]
    public void Extract_Rust_UseAliasHandlesTokenBoundariesAndPathsContainingAs()
    {
        var content = "use crate::as_helpers::Inner\tas\tAliased;\nuse std::io::Result\n    as\n    IoResult;\n";

        var symbols = SymbolExtractor.Extract(1, "rust", content);
        var imports = symbols.Where(s => s.Kind == "import").Select(s => s.Name).ToList();

        Assert.Contains("Aliased", imports);
        Assert.Contains("Inner", imports);
        Assert.Contains("IoResult", imports);
        Assert.Contains("Result", imports);
    }

    [Fact]
    public void Extract_Rust_MapsImplBlocksToImplementingType()
    {
        var content = """
            struct Widget;
            struct Task<T> { value: T }
            struct Empty<T> { value: T }

            impl Widget {}
            impl Debug for Widget {}
            impl<T> Future for Task<T> {}
            unsafe impl<T> Send for Empty<T> {}
            impl crate::models::Widget {}
            impl<T> crate::tasks::Task<T> {}
            impl Debug for crate::models::Widget {}
            unsafe impl<T> Send for crate::tasks::Task<T> {}
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Task");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Empty");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Task");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Empty");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Debug");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Future");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Send");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "crate");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "models");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "tasks");
    }

    [Fact]
    public void Extract_Rust_DetectsMultilineImplBlocks()
    {
        var content = """
            struct Wrapped<T>(T);
            trait Trait {}

            unsafe impl<T>
                Trait
                for
                Wrapped<T>
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Wrapped" && s.Line == 7 && s.StartColumn == 5);
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Trait" && s.Line == 4);
    }

    [Fact]
    public void Extract_Rust_DetectsMultilineFnHeaders()
    {
        var content = """
            pub unsafe extern "C"
            fn exported_api<T>(
                value: T,
            ) -> T
            where
                T: Copy,
            {
                value
            }
        """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        var symbol = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "exported_api");
        Assert.Equal(2, symbol.Line);
        Assert.Equal(7, symbol.StartColumn);
    }

    [Fact]
    public void Extract_Rust_DistinguishesFileModulesAndScopesInlineModules()
    {
        var content = """
            pub mod file_backed;

            pub(crate) mod outer {
                pub mod inner {
                    pub fn build() {}
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "rust", content);

        var fileModule = Assert.Single(symbols, s => s.Kind == "file_module" && s.Name == "file_backed");
        Assert.Equal("pub", fileModule.Visibility);

        var outer = Assert.Single(symbols, s => s.Kind == "namespace" && s.Name == "outer");
        Assert.Equal("pub(crate)", outer.Visibility);

        var inner = Assert.Single(symbols, s => s.Kind == "namespace" && s.Name == "inner");
        Assert.Equal("outer", inner.ContainerName);
        Assert.Equal("pub", inner.Visibility);

        var build = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Equal("inner", build.ContainerName);
        Assert.Contains("outer", build.ContainerQualifiedName, StringComparison.Ordinal);
        Assert.Contains("inner", build.ContainerQualifiedName, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_Go_DetectsTypeAliasAndConst()
    {
        var content = "type Handler struct {\n}\ntype ID = string\ntype Callback func(int) int\ntype Logger interface {\n}\n\nconst (\n    MaxRetries = 3\n    DefaultTimeout = 30\n)\n\nvar GlobalConfig Config";
        var symbols = SymbolExtractor.Extract(1, "go", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ID");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "protocol" && s.Name == "Logger");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "MaxRetries");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DefaultTimeout");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "GlobalConfig");
    }

    [Fact]
    public void Extract_Rust_DetectsFunctionsAndStructs()
    {
        var content = "pub fn handle_request() {}\npub struct Config {}\nimpl Config {";
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "handle_request");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config");
    }

    [Fact]
    public void Extract_Rust_DetectsEnumVariants()
    {
        var content = """
            pub enum Shape {
                Circle { radius: f64 },
                Rectangle(f64, f64),
                Point,
            }

            pub enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            pub enum Color {
                Red,
                Green,
                Blue,
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Circle");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Rectangle");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Ok");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Err");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Red");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Green");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Blue");
        Assert.Equal(8, symbols.Count(s => s.Kind == "property"));
    }

    [Fact]
    public void Extract_Rust_LifetimeAnnotationsDoNotBreakBraceRanges()
    {
        var content = """
            pub struct Holder<'a> {
                value: &'a str,
            }

            impl<'a> Holder<'a> {
                pub fn get(&self) -> &'a str {
                    self.value
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "rust", content);

        var holder = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "Holder"));
        var get = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "get"));

        Assert.Equal(3, holder.EndLine);
        Assert.Equal(8, get.EndLine);
        Assert.Equal("class", get.ContainerKind);
        Assert.Equal("Holder", get.ContainerName);
    }

    [Fact]
    public void Extract_Markdown_DetectsHeadingsOutsideCodeFences()
    {
        const string content = """
            # Guide

            Intro text.

            ## Details

            ```markdown
            # Not a heading
            ## Also ignored
            ```

            ### Deep Dive

            # Appendix
            """;

        var symbols = SymbolExtractor.Extract(1, "markdown", content);
        var headings = symbols.Where(s => s.Kind == "heading").ToList();

        Assert.Equal(4, headings.Count);
        Assert.Contains(headings, s => s.Name == "Guide" && s.ContainerName == null);
        Assert.Contains(headings, s => s.Name == "Details" && s.ContainerName == "Guide");
        Assert.Contains(headings, s => s.Name == "Deep Dive" && s.ContainerName == "Details");
        Assert.Contains(headings, s => s.Name == "Appendix" && s.ContainerName == null);
        Assert.DoesNotContain(headings, s => s.Name == "Not a heading");
        Assert.DoesNotContain(headings, s => s.Name == "Also ignored");
    }

    [Fact]
    public void Extract_Markdown_DetectsFencedCodeBlockSymbols()
    {
        const string content = """
            # Guide

            ```python
            print("hello")
            ```

            ## Examples

            ~~~bash title="setup"
            echo ok
            ~~~

            ```
            no language
            ```
            """;

        var symbols = SymbolExtractor.Extract(1, "markdown", content);
        var codeBlocks = symbols.Where(s => s.Kind == "code").ToList();

        Assert.Equal(3, codeBlocks.Count);
        Assert.Contains(codeBlocks, s =>
            s.Name == "python"
            && s.Line == 3
            && s.StartLine == 3
            && s.EndLine == 5
            && s.BodyStartLine == 4
            && s.BodyEndLine == 4
            && s.ContainerName == "Guide"
            && s.Signature == "```python");
        Assert.Contains(codeBlocks, s =>
            s.Name == "bash"
            && s.Line == 9
            && s.EndLine == 11
            && s.BodyStartLine == 10
            && s.BodyEndLine == 10
            && s.ContainerName == "Examples"
            && s.Signature == "~~~bash title=\"setup\"");
        Assert.Contains(codeBlocks, s =>
            s.Name == "code"
            && s.Line == 13
            && s.EndLine == 15
            && s.BodyStartLine == 14
            && s.BodyEndLine == 14
            && s.ContainerName == "Examples");
    }

    [Fact]
    public void Extract_Markdown_DetectsEmptyAndUnclosedFencedCodeBlockSymbols()
    {
        const string emptyFenceContent = """
            # Guide

            ```json
            ```
            """;
        const string unclosedFenceContent = """
            # Guide

            ```dockerfile
            FROM alpine
            RUN true
            """;

        var emptyFenceSymbols = SymbolExtractor.Extract(1, "markdown", emptyFenceContent);
        var emptyCodeBlock = Assert.Single(emptyFenceSymbols.Where(s => s.Kind == "code"));

        Assert.Equal("json", emptyCodeBlock.Name);
        Assert.Equal(3, emptyCodeBlock.StartLine);
        Assert.Equal(4, emptyCodeBlock.EndLine);
        Assert.Equal(4, emptyCodeBlock.BodyStartLine);
        Assert.Equal(4, emptyCodeBlock.BodyEndLine);
        Assert.Equal("Guide", emptyCodeBlock.ContainerName);

        var unclosedFenceSymbols = SymbolExtractor.Extract(1, "markdown", unclosedFenceContent);
        var unclosedCodeBlock = Assert.Single(unclosedFenceSymbols.Where(s => s.Kind == "code"));

        Assert.Equal("dockerfile", unclosedCodeBlock.Name);
        Assert.Equal(3, unclosedCodeBlock.StartLine);
        Assert.Equal(5, unclosedCodeBlock.EndLine);
        Assert.Equal(4, unclosedCodeBlock.BodyStartLine);
        Assert.Equal(5, unclosedCodeBlock.BodyEndLine);
        Assert.Equal("Guide", unclosedCodeBlock.ContainerName);
    }

    [Fact]
    public void Extract_Markdown_DetectsSetextHeadingsAndLocalAnchorReferences()
    {
        const string content = """
            Guide
            =====

            Intro text with a [jump](#details) reference.

            Details
            -------

            A reference-style link also points at [guide][guide-anchor].

            [guide-anchor]: #guide
            """;

        var symbols = SymbolExtractor.Extract(1, "markdown", content);
        var headings = symbols.Where(s => s.Kind == "heading").ToList();
        var references = symbols.Where(s => s.Kind == "reference").ToList();

        Assert.Contains(headings, s => s.Name == "Guide" && s.Line == 1);
        Assert.Contains(headings, s => s.Name == "Details" && s.Line == 6);
        Assert.Equal(3, references.Count);
        Assert.Contains(references, s => s.Name == "details");
        Assert.Contains(references, s => s.Name == "guide");
        Assert.Equal(2, references.Count(s => s.Name == "guide"));
    }

    [Fact]
    public void Extract_NullLang_ReturnsEmpty()
    {
        var symbols = SymbolExtractor.Extract(1, null, "some content");
        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_LineNumbers_AreOneBased()
    {
        // Line numbers should be 1-based
        // 行番号は1始まりであること
        var content = "x = 1\ndef foo():\n    pass";
        var symbols = SymbolExtractor.Extract(1, "python", content);

        Assert.Single(symbols);
        Assert.Equal(2, symbols[0].Line);
    }

    [Fact]
    public void Extract_Java_DetectsClassesAndMethods()
    {
        // Java: class, interface, methods / Java: クラス、インターフェース、メソッド
        var content = "public class UserService {\n    public User getUser(int id) {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "getUser");
    }

    [Fact]
    public void Extract_Java_NormalizesUnicodeEscapedIdentifierNames()
    {
        const string content = "package demo.\\u0061pp;\n\n"
            + "public class \\u0046oo {\n"
            + "    public void \\u0062ar() { }\n"
            + "}\n\n"
            + "public interface \\uuuu0041pi {\n"
            + "}\n";

        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "demo.app");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bar" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Api");
        Assert.DoesNotContain(symbols, s => s.Name.Contains('\\'));
    }

    [Fact]
    public void Extract_Java_DetectsGenericMethodsWithTypeParameterPrefix()
    {
        var content = """
            package com.example;

            public class GenericService {
                public <T> T first(java.util.List<T> items) { return items.get(0); }
                public <K, V> V get(java.util.Map<K, V> map, K key) { return map.get(key); }
                public <T extends Comparable<T>> T max(T a, T b) { return a.compareTo(b) >= 0 ? a : b; }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "GenericService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "GenericService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "get" && s.ContainerName == "GenericService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "max" && s.ContainerName == "GenericService");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "return");
    }

    [Fact]
    public void Extract_Java_DetectsPackageQualifiedReturnTypesAndAnnotatedMembers()
    {
        var content = """
            package com.example.service;

            public class UserService {
                @Deprecated
                public UserService() {}

                public java.util.List<String[]> loadAll() { return java.util.List.of(); }

                @Deprecated
                public static final java.util.Map<String, Integer> MAX_RETRIES = java.util.Map.of();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "com.example.service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "loadAll" && s.ReturnType == "java.util.List<String[]>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_RETRIES" && s.ReturnType == "java.util.Map<String, Integer>");
    }

    [Fact]
    public void Extract_Java_DetectsRecordAndSealedClass()
    {
        // Java 16+ record, Java 17+ sealed class / Java 16 の record、Java 17 の sealed class
        var content = "public record Point(int x, int y) { }\npublic sealed class Shape permits Circle, Rect { }";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Shape");
    }

    [Fact]
    public void Extract_Java_DetectsRecordPrimaryComponentsAsProperties()
    {
        var content = """
            package com.example;

            public record Point(int x, int y) {}
            public record Range(
                int low,
                int high
            ) {
                public int span() { return high - low; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var pointX = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "x" && s.ContainerName == "Point"));
        Assert.Equal("int", pointX.ReturnType);
        Assert.Equal("class", pointX.ContainerKind);
        Assert.Equal("Point", pointX.ContainerQualifiedName);
        Assert.Equal("public", pointX.Visibility);

        var rangeLow = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "low" && s.ContainerName == "Range"));
        Assert.Equal("int", rangeLow.ReturnType);
        Assert.Equal(5, rangeLow.Line);

        var rangeHigh = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "high" && s.ContainerName == "Range"));
        Assert.Equal("int", rangeHigh.ReturnType);
        Assert.Equal(6, rangeHigh.Line);

        var span = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "span"));
        Assert.Equal("class", span.ContainerKind);
        Assert.Equal("Range", span.ContainerName);
        Assert.Equal("Range", span.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_Java_DetectsLongAndCommentedRecordPrimaryComponentsAsProperties()
    {
        var componentLines = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"    int p{i},"));
        var content = $$"""
            package com.example;

            public record Big(
            {{componentLines}}
                int tail
            ) {}

            public record Point(
                int x /* separator, comment */,
                // the next component must still parse
                int y
            ) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "p1" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "p30" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "tail" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x" && s.ContainerName == "Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "y" && s.ContainerName == "Point");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "separator" && s.ContainerName == "Point");

        var tail = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "tail" && s.ContainerName == "Big"));
        Assert.Equal(34, tail.Line);

        var pointY = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "y" && s.ContainerName == "Point"));
        Assert.Equal(40, pointY.Line);
    }

    [Fact]
    public void Extract_Java_DetectsRecordPrimaryComponentsWithSpacedGenericTypes()
    {
        var content = """
            import java.util.Map;

            public record Sample(Map <String, Integer> values, int count) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var values = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "values" && s.ContainerName == "Sample"));
        Assert.Equal("Map <String, Integer>", values.ReturnType);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "count" && s.ContainerName == "Sample"));
        Assert.Equal("int", count.ReturnType);
    }

    [Fact]
    public void Extract_Java_TracksRecordPrimaryComponentLineAfterAnnotations()
    {
        var content = """
            public record Person(
                @Deprecated
                String name,
                @Deprecated
                int age
            ) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "Person"));
        Assert.Equal(3, name.Line);

        var age = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "age" && s.ContainerName == "Person"));
        Assert.Equal(5, age.Line);
    }

    [Fact]
    public void Extract_Java_RecordComponents_DoNotDisruptBodyMembers()
    {
        var content = """
            package repro;

            public record Point(int x) {
                public int doubled() { return x * 2; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var doubled = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "doubled"));
        Assert.Equal("class", doubled.ContainerKind);
        Assert.Equal("Point", doubled.ContainerName);
        Assert.Equal("Point", doubled.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_Java_DetectsSameLineAnnotatedMethodsCompactConstructorsAndEnumConstantOverrides()
    {
        var content = """
            package com.example;

            public class Same {
                @Override public String toString() { return "x"; }
                @Deprecated public int legacy() { return 0; }

                @Override
                public int hashCode() { return 42; }
            }

            public enum Op {
                ADD {
                    @Override public int apply(int a, int b) { return a + b; }
                },
                SUB {
                    @Override public int apply(int a, int b) { return a - b; }
                };
                public abstract int apply(int a, int b);
            }

            public record Range(int low, int high) {
                public Range {
                    if (low > high) throw new IllegalArgumentException();
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var toString = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "toString"));
        Assert.Equal("class", toString.ContainerKind);
        Assert.Equal("Same", toString.ContainerName);

        var legacy = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "legacy"));
        Assert.Equal("class", legacy.ContainerKind);
        Assert.Equal("Same", legacy.ContainerName);

        var addApply = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "apply" && s.ContainerName == "ADD"));
        Assert.Equal("function", addApply.ContainerKind);
        Assert.Equal("Op.ADD", addApply.ContainerQualifiedName);

        var subApply = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "apply" && s.ContainerName == "SUB"));
        Assert.Equal("function", subApply.ContainerKind);
        Assert.Equal("Op.SUB", subApply.ContainerQualifiedName);

        var abstractApply = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "apply" && s.ContainerName == "Op"));
        Assert.Equal("enum", abstractApply.ContainerKind);

        var compactCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Range"));
        Assert.Equal("class", compactCtor.ContainerKind);
        Assert.Equal("Range", compactCtor.ContainerName);
        Assert.NotNull(compactCtor.BodyStartLine);
        Assert.NotNull(compactCtor.BodyEndLine);
    }

    [Fact]
    public void Extract_Java_DetectsAllmanStyleCompactConstructors()
    {
        var content = """
            public record Range(int low, int high) {
                public Range
                {
                    if (low > high) throw new IllegalArgumentException();
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var compactCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Range"));
        Assert.Equal("class", compactCtor.ContainerKind);
        Assert.Equal("Range", compactCtor.ContainerName);
        Assert.Equal(2, compactCtor.StartLine);
        Assert.NotNull(compactCtor.BodyStartLine);
        Assert.NotNull(compactCtor.BodyEndLine);
        Assert.True(compactCtor.BodyStartLine >= compactCtor.StartLine);
    }

    [Fact]
    public void Extract_Java_SwitchExpressionsDoNotEmitPhantomMethods()
    {
        var content = """
            package com.example;

            sealed interface Shape permits Circle, Square {}
            record Circle(double r) implements Shape {}
            record Square(double side) implements Shape {}

            public class Matcher {
                public double area(Shape shape) {
                    return switch (shape) {
                        case Circle(double r) -> Math.PI * r * r;
                        case Square(double side) -> side * side;
                        default -> 0.0;
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Circle");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Square");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "area" && s.ContainerName == "Matcher");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "switch");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Circle");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Square");
    }

    [Fact]
    public void Extract_Java_DetectsSameLineAnnotatedDeclarationsWhenAnnotationArgumentsContainParen()
    {
        var content = """
            public class Demo {
                @Label(")") public int broken() { return 1; }
            }

            @Ann(value = helper(")"))
            public record Wrapped(int value) {}
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "broken" && s.ContainerKind == "class" && s.ContainerName == "Demo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Wrapped");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value" && s.ContainerKind == "class" && s.ContainerName == "Wrapped");
    }

    [Fact]
    public void Extract_Java_DetectsSameLineAnnotatedMethodsWhenAnnotationArgumentsContainBraceLiterals()
    {
        var content = """
            public class Demo {
                @SuppressWarnings({"unchecked"}) public int first() { return 1; } int second() { return 2; }
            }

            public class Solo {
                @SuppressWarnings({"rawtypes"}) public int only() { return 3; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerKind == "class" && s.ContainerName == "Demo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerKind == "class" && s.ContainerName == "Demo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "only" && s.ContainerKind == "class" && s.ContainerName == "Solo");
    }

    [Fact]
    public void Extract_Java_DetectsBraceLiteralAnnotationsOnSameLineTypesAndEnumBodies()
    {
        var content = """
            @Target({ElementType.TYPE}) public record Wrapped(int value) { public int twice() { return value * 2; } }

            public enum Op {
                ADD { @SuppressWarnings({"unchecked"}) public int apply(int a, int b) { return a + b; } };
                public abstract int apply(int a, int b);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Wrapped");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value" && s.ContainerKind == "class" && s.ContainerName == "Wrapped");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "twice" && s.ContainerKind == "class" && s.ContainerName == "Wrapped");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "apply" && s.ContainerKind == "function" && s.ContainerName == "ADD");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "apply" && s.ContainerKind == "enum" && s.ContainerName == "Op");
    }

    [Fact]
    public void Extract_Java_SameLineRecordsDoNotEmitPhantomHeaderFunctions()
    {
        var content = """
            public record Empty(int x) {}
            public record Inline(int x) { public int twice() { return x * 2; } }
            public record Compact(int x) { public Compact { if (x < 0) throw new IllegalArgumentException(); } }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Empty");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Inline");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Compact");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x" && s.ContainerName == "Empty");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "twice" && s.ContainerKind == "class" && s.ContainerName == "Inline");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Compact" && s.ContainerKind == "class" && s.ContainerName == "Compact");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name is "Empty" or "Inline" && s.ReturnType == "record");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Compact" && s.ReturnType == "record");
    }

    [Fact]
    public void Extract_Java_SameLineCompactConstructorAfterSiblingStillIndexesConstructor()
    {
        var content = """
            public record R(int x) { int first() { return x; } public R { if (x < 0) throw new IllegalArgumentException(); } }
            public record Annotated(int x) { @Deprecated int first() { return x; } @Deprecated public Annotated { if (x < 0) throw new IllegalArgumentException(); } }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "R");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x" && s.ContainerKind == "class" && s.ContainerName == "R");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerKind == "class" && s.ContainerName == "R");
        var compactCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "R"));
        Assert.Equal("class", compactCtor.ContainerKind);
        Assert.Equal("R", compactCtor.ContainerName);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerKind == "class" && s.ContainerName == "Annotated");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Annotated" && s.ContainerKind == "class" && s.ContainerName == "Annotated");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name is "R" or "Annotated" && s.ReturnType == "record");
    }

    [Fact]
    public void Extract_Java_SameLineMembersAfterCompactConstructorStillIndex()
    {
        var content = """
            public record R(int x) { public R { } int later() { return x; } }
            public record Annotated(int x) { @Deprecated public Annotated { } @Deprecated int later() { return x; } }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "R" && s.ContainerKind == "class" && s.ContainerName == "R");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "later" && s.ContainerKind == "class" && s.ContainerName == "R");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Annotated" && s.ContainerKind == "class" && s.ContainerName == "Annotated");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "later" && s.ContainerKind == "class" && s.ContainerName == "Annotated");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.ReturnType == "record");
    }

    [Fact]
    public void Extract_Java_HandlesSameLineSiblingMethodsInsideEnumBody()
    {
        var content = """
            public enum Demo {
                A;
                int first() { return 1; } int second() { return 2; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "first" && s.ContainerKind == "enum" && s.ContainerName == "Demo"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "second" && s.ContainerKind == "enum" && s.ContainerName == "Demo"));
        Assert.Equal("int first() { return 1; }", first.Signature);
        Assert.Equal("int second() { return 2; }", second.Signature);
    }

    [Fact]
    public void Extract_Java_DetectsStaticFinalAndEnumMembers()
    {
        var content = "public class Config {\n    public static final String VERSION = \"1.0\";\n    private static final int MAX_RETRIES = 3;\n}\n\npublic enum Status {\n    ACTIVE,\n    INACTIVE,\n    PENDING;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_RETRIES");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ACTIVE");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "INACTIVE");
    }

    [Fact]
    public void Extract_Java_DetectsAnnotationType()
    {
        var content = "public @interface MyAnnotation {\n    String value();\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyAnnotation");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "value");
    }

    [Fact]
    public void Extract_Java_DetectsModuleInfoDeclarationAndDirectives()
    {
        const string content = """
            open module com.example.app {
                requires static transitive java.logging;
                requires java.base;
                exports com.example.api;
                exports com.example.internal to com.example.plugin, com.example.tools;
                opens com.example.model;
                uses com.example.spi.MyService;
                provides com.example.spi.MyService with com.example.impl.DefaultService, com.example.impl.BackupService;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var module = Assert.Single(symbols.Where(s => s.Kind == "namespace" && s.Name == "com.example.app"));
        Assert.Equal(1, module.Line);
        Assert.Equal(1, module.StartLine);
        Assert.Equal(9, module.EndLine);

        var imports = symbols.Where(s => s.Kind == "import").ToList();
        Assert.Equal(7, imports.Count);
        Assert.Contains(imports, s => s.Name == "java.logging" && s.ContainerName == "com.example.app");
        Assert.Contains(imports, s => s.Name == "java.base" && s.ContainerName == "com.example.app");
        Assert.Contains(imports, s => s.Name == "com.example.api" && s.ContainerName == "com.example.app");
        Assert.Contains(imports, s => s.Name == "com.example.internal" && s.ContainerName == "com.example.app");
        Assert.Contains(imports, s => s.Name == "com.example.model" && s.ContainerName == "com.example.app");
        Assert.Equal(2, imports.Count(s => s.Name == "com.example.spi.MyService" && s.ContainerName == "com.example.app"));
    }

    [Fact]
    public void Extract_Java_DetectsModuleInfoDeclarationWithAllmanBrace()
    {
        const string content = """
            module com.example.app
            {
                requires java.base;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var module = Assert.Single(symbols.Where(s => s.Kind == "namespace" && s.Name == "com.example.app"));
        Assert.Equal(1, module.Line);
        Assert.Equal(1, module.StartLine);
        Assert.Equal(4, module.EndLine);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "java.base" && s.ContainerName == "com.example.app");
    }

    [Fact]
    public void Extract_Java_DetectsModuleInfoDirectivesOnAllmanBraceLine()
    {
        const string content = """
            module com.example.app
            { requires java.base;
              exports com.example.api;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var imports = symbols.Where(s => s.Kind == "import").ToList();
        Assert.Equal(2, imports.Count);
        Assert.Contains(imports, s => s.Name == "java.base" && s.ContainerName == "com.example.app");
        Assert.Contains(imports, s => s.Name == "com.example.api" && s.ContainerName == "com.example.app");
    }

    [Fact]
    public void Extract_Java_DetectsModuleInfoDirectivesWithMultilineListsAndComments()
    {
        const string content = """
            module com.example.app {
                requires /*comment*/ java.base;
                exports com.example.internal
                    to com.example.plugin,
                       com.example.tools;
                opens com.example.model
                    to com.example.viewer,
                       com.example.editor;
                uses com.example.spi.MyService;
                provides com.example.spi.MyService
                    with com.example.impl.DefaultService,
                         com.example.impl.BackupService;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var imports = symbols.Where(s => s.Kind == "import").ToList();
        Assert.Equal(5, imports.Count);
        Assert.Contains(imports, s => s.Name == "java.base");
        Assert.Contains(imports, s => s.Name == "com.example.internal");
        Assert.Contains(imports, s => s.Name == "com.example.model");
        Assert.Equal(2, imports.Count(s => s.Name == "com.example.spi.MyService"));
        Assert.Contains(imports, s => s.Signature == "requires /*comment*/ java.base;");
        Assert.Contains(imports, s => s.Signature == "exports com.example.internal to com.example.plugin, com.example.tools;");
        Assert.Contains(imports, s => s.Signature == "opens com.example.model to com.example.viewer, com.example.editor;");
        Assert.Contains(imports, s => s.Signature == "provides com.example.spi.MyService with com.example.impl.DefaultService, com.example.impl.BackupService;");
    }

    [Fact]
    public void Extract_Java_DetectsFlexibleConstantOrder()
    {
        // final static order (reversed) and generic types with spaces
        var content = "public class Config {\n    private final static int MAX = 100;\n    public static final Map<String, Integer> COUNTS = Map.of();\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "COUNTS");
    }

    [Fact]
    public void Extract_Java_DetectsEnumMembersWithTwoSpaceIndent()
    {
        // 2-space indent enum / 2スペースインデントの enum
        var content = "public enum Color {\n  RED,\n  GREEN,\n  BLUE;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RED");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GREEN");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BLUE");
    }

    [Fact]
    public void Extract_Java_DetectsTabIndentedEnumMembers()
    {
        // Single-tab indent enum (EditorConfig indent_style=tab) — regression for #364 Java side.
        // タブ1文字でインデントされた enum（#364 の Java 側クロス言語修正のリグレッション）。
        var content = "public enum Color {\n\tRED,\n\tGREEN,\n\tBLUE;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RED");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GREEN");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BLUE");
    }

    [Fact]
    public void Extract_Java_DoesNotExtractMethodCallsAsEnumMembers()
    {
        // A class body method call like `\tRED();` must not be misread as an enum member — regression for #292.
        // クラス本体内のメソッド呼び出し `\tRED();` を enum メンバーとして誤検出しないこと（#292 のリグレッション）。
        var content = "public class Test {\n\tvoid run() {\n\t\tRED();\n\t\tGREEN();\n\t}\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "RED");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "GREEN");
    }

    [Fact]
    public void Extract_Java_HandlesAnnotationWithQuotedParen()
    {
        // `@Label(")")` must not fool the paren-balance counter — the `)` is inside a string literal.
        // `@Label(")")` の `)` は文字列内なので括弧バランスで閉じてはいけない。
        var content = "public enum E {\n    @Label(\")\") A,\n    B;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerName == "E");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "E");
    }

    [Fact]
    public void Extract_Java_HandlesAnnotationMemberDefaultArrayValueAsBodyLess()
    {
        // `default { ... }` on an annotation member is part of the default value, not a real
        // member body. The scanner must keep the declaration body-less so later same-line
        // siblings still survive.
        // annotation member の `default { ... }` は本体ではなく default 値の一部。
        // body-less のまま保持し、同一行の後続 sibling を落とさないこと。
        var content = "@interface Tags { String[] value() default {\"a\", \"b\"}; int age(); } class C {}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var value = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "value" && s.ContainerName == "Tags"));
        Assert.Equal("String[] value() default {\"a\", \"b\"};", value.Signature);
        Assert.Null(value.BodyStartLine);
        Assert.Null(value.BodyEndLine);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "age" && s.ContainerName == "Tags" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C");
    }

    [Fact]
    public void Extract_Java_HandlesBlockCommentBetweenAnnotationAndMember()
    {
        // Block comments between `@Annotation` and the member name must be skipped.
        // アノテーションとメンバー名の間の block comment を読み飛ばすこと。
        var content = "public enum E {\n    @A /*note*/ B,\n    C;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "E");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "C" && s.ContainerName == "E");
    }

    [Fact]
    public void Extract_Java_HandlesEmptyEnumBody()
    {
        // `enum X {}` has no members; the enum itself should still be extracted.
        // 空本体の enum でも enum 自身は抽出されること。
        var content = "public enum Empty {}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Empty");
        Assert.DoesNotContain(symbols, s => s.ContainerKind == "enum" && s.ContainerName == "Empty");
    }

    [Fact]
    public void Extract_Java_HandlesEnumWithOnlySemicolon()
    {
        // `enum X { ; }` declares no members but may still hold methods/fields.
        // 本体が `;` のみの enum はメンバーを持たない。
        var content = "public enum NoMembers {\n    ;\n    private int count;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "NoMembers");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.ContainerKind == "enum" && s.ContainerName == "NoMembers" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_Java_HandlesTextBlockContainingBrace()
    {
        // A `}` inside a Java text block (""") must not close the enum body prematurely, which
        // would otherwise drop every member after the text block. Regression for FindJavaBraceRange.
        // Java text block 内の `}` で enum 本体範囲を誤って閉じず、後続メンバーが落ちないこと。
        var content = "public enum TxtBlock {\n  FIRST(\"\"\"\n    end }\n    more ;\n    \"\"\"),\n  SECOND;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "FIRST" && s.ContainerName == "TxtBlock");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "SECOND" && s.ContainerName == "TxtBlock");
    }

    [Fact]
    public void Extract_Java_HandlesStringContainingBrace()
    {
        // A `}` inside a regular string literal must not close the enum body prematurely either.
        // 文字列リテラル内の `}` でも enum 本体範囲を閉じないこと。
        var content = "public enum QuotedBrace {\n    A(\"text with } inside\"),\n    B;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerName == "QuotedBrace");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "QuotedBrace");
    }

    [Fact]
    public void Extract_Java_DetectsUnicodeEnumMembers()
    {
        var content = "public enum Localized {\n    RÉSUMÉ,\n    NAÏVE;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RÉSUMÉ" && s.ContainerName == "Localized");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NAÏVE" && s.ContainerName == "Localized");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "R" && s.ContainerName == "Localized");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "NA" && s.ContainerName == "Localized");
    }

    [Fact]
    public void Extract_Java_HandlesTrailingComma()
    {
        // `enum X { A, B, }` — trailing comma before closing brace must not emit an empty member.
        // `,` の直後が body end でも空メンバーを出さないこと。
        var content = "public enum Trailing {\n    A,\n    B,\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerName == "Trailing");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "Trailing");
        Assert.Equal(2, symbols.Count(s => s.ContainerKind == "enum" && s.ContainerName == "Trailing" && s.BodyStartLine == null));
    }

    [Fact]
    public void Extract_Java_HandlesAnonymousMemberBody()
    {
        // Anonymous member bodies (`A { void f() {} }`) must not suppress the following member.
        // 匿名メンバー本体があっても直後のメンバーが消えないこと。
        var content = "public enum WithBody {\n    A {\n        void f() {}\n    },\n    B;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerName == "WithBody" && s.BodyStartLine != null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "f" && s.ContainerKind == "function" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "WithBody" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_Java_HandlesSameLineAnonymousMemberBodyMethods()
    {
        var content = """
            public enum Mix {
                A { @Override public int f() { return 1; } int g() { return 2; } },
                B { @Override public int f() { return 3; } int h() { return 4; } };
                public abstract int f();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerKind == "enum" && s.ContainerName == "Mix" && s.BodyStartLine != null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerKind == "enum" && s.ContainerName == "Mix" && s.BodyStartLine != null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "f" && s.ContainerKind == "function" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "g" && s.ContainerKind == "function" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "f" && s.ContainerKind == "function" && s.ContainerName == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "h" && s.ContainerKind == "function" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_Java_RecoversMembersWhenAnnotationIsMalformed()
    {
        // An unclosed `@Ann(` would otherwise make the primary scanner swallow subsequent member lines.
        // The per-line fallback rescues obvious uppercase-identifier members.
        // 未閉鎖の `@Ann(` で primary scanner が後続行を飲み込んでしまう状況を、line fallback で救済する。
        var content = "public enum E {\n    @Ann(\n    A,\n    B;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerName == "E");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "E");
    }

    [Fact]
    public void Extract_Java_RecoveryIgnoresLinesInsideAnonymousMemberBody()
    {
        // Malformed input forces the recovery pass; recovery must track brace depth so uppercase
        // call statements inside an anonymous member body (e.g. `ACTIVATE_HELPER();`) are not
        // emitted as phantom enum members, and the subsequent real member is still captured.
        // 不整形入力で recovery が走るとき、匿名メンバー本体内の大文字呼び出しを誤って member にせず、
        // その後の実メンバーを救済できること。
        var content = "public enum E {\n    @Bad(\n    RED {\n        void f() { ACTIVATE_HELPER(); }\n    },\n    GREEN;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GREEN" && s.ContainerName == "E" && s.BodyStartLine == null);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "ACTIVATE_HELPER" && s.ContainerName == "E" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_Java_RecoveryDedupsByNameAcrossAnnotationStartLines()
    {
        // Primary scanner stamps StartLine at the annotation line; recovery stamps the member-name
        // line. StartLine-based dedup would double-emit. Name-based dedup must suppress duplicates.
        // primary scanner と recovery で StartLine 基準が異なるため、名前基準で重複排除されること。
        var content = "public enum E {\n    @Marker\n    A,\n    @Bad(\n    B;\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        var aMembers = symbols.Where(s => s.Kind == "function" && s.Name == "A" && s.ContainerName == "E" && s.BodyStartLine == null).ToList();
        Assert.Single(aMembers);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerName == "E" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_Java_StopsEnumMembersAtSemicolon()
    {
        // After the first top-level `;` inside the enum body, non-member declarations must not be captured as members.
        // Enum members have no body range (BodyStartLine == null); the method extractor populates a body range.
        // enum 本体内の最初の top-level `;` より後の宣言をメンバーとして誤検出しないこと。
        // メンバーには body range が無い（BodyStartLine == null）点をメソッド抽出と区別する。
        var content = "public enum Status {\n    ACTIVE,\n    INACTIVE;\n    public void activate() { ACTIVATE_HELPER(); }\n    private static void ACTIVATE_HELPER() {}\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ACTIVE" && s.ContainerName == "Status" && s.BodyStartLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "INACTIVE" && s.ContainerName == "Status" && s.BodyStartLine == null);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "ACTIVATE_HELPER" && s.BodyStartLine == null);
    }

    [Fact]
    public void Extract_Java_DetectsDefaultAndSynchronizedMethods()
    {
        var content = "public interface Service {\n    default void init() { }\n    static Service create() { return null; }\n}\npublic class Worker {\n    synchronized void process() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "init");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "create");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "process");
    }

    [Fact]
    public void Extract_Kotlin_DetectsFunctionsAndClasses()
    {
        // Kotlin: class, fun / Kotlin: クラス、関数
        var content = """
            data class Config(val name: String)
            fun one() = 1
            fun process(input: String): String {
                return input.trim()
            }
            fun three() = 3
            """;
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerKind == "class" && s.ContainerName == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "one" && s.StartLine == 2 && s.EndLine == 2 && s.BodyStartLine == null && s.BodyEndLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "process" && s.StartLine == 3 && s.EndLine == 5 && s.BodyStartLine == 3 && s.BodyEndLine == 5);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "three" && s.StartLine == 6 && s.EndLine == 6 && s.BodyStartLine == null && s.BodyEndLine == null);
    }

    [Fact]
    public void Extract_Kotlin_DistinguishesValueClassesAndInlineReifiedFunctions()
    {
        var content = """
            @JvmInline
            value class UserId(val id: Long)
            inline class LegacyId(val value: String)
            inline fun <reified T> parse(): T = TODO()
            inline fun render(block: () -> Unit) = block()
            inline suspend fun <reified T> load(): T = TODO()
            fun value(inline: String): String = inline
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserId" && s.SubKind == "kotlin_value_class");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "LegacyId" && s.SubKind == "kotlin_inline_class");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse" && s.SubKind == "kotlin_inline_reified_function");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render" && s.SubKind == "kotlin_inline_function");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "load" && s.SubKind == "kotlin_inline_reified_function");
        Assert.DoesNotContain(symbols, s => s.Name == "value" && s.SubKind != null);
    }

    [Fact]
    public void Extract_Kotlin_NormalizesBacktickedSymbolNames()
    {
        var content = """
            class `when` {
                fun `is`(): Int = 1
                val `value-name`: Int = 2
            }

            typealias `Alias Name` = `when`

            enum class `enum` {
                `mixed-case`
            }

            class Holder {
                companion object `Factory Name` {
                    fun `top level`(): String = "ok"
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "when");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "is" && s.ContainerName == "when");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value-name" && s.ContainerName == "when");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Alias Name");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "enum");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "mixed-case" && s.ContainerName == "enum");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Holder");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Factory Name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "top level" && s.ContainerName == "Factory Name");
        Assert.DoesNotContain(symbols, s => s.Name.Contains('`'));
    }

    [Fact]
    public void Extract_Kotlin_DetectsTypealiasDeclarations()
    {
        var content = """
            typealias Handler = (String) -> Unit
            internal typealias UserMap = Map<String, User>
            public typealias Nested<T> = List<Pair<String, T>>

            val type = 1
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Handler" && s.Visibility == null);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "UserMap" && s.Visibility == "internal");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Nested" && s.Visibility == "public");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "type");
    }

    [Fact]
    public void Extract_Kotlin_DetectsSecondaryConstructors()
    {
        var content = """
            class Person(private val name: String) {
                var age: Int = 0

                constructor(name: String, age: Int) : this(name) { this.age = age }

                constructor() : this("anonymous", 0)

                fun greet(): String = "Hi $name"
            }

            class Box<T>(
                val value: T
            ) {
                private constructor(list: List<T>) : this(list.first())
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Box");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerKind == "class" && s.ContainerName == "Person" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value" && s.ContainerKind == "class" && s.ContainerName == "Box" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Person" && s.ContainerName == "Person" && s.Signature == "constructor(name: String, age: Int) : this(name) { this.age = age }");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Person" && s.ContainerName == "Person" && s.Signature == "constructor() : this(\"anonymous\", 0)");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Box" && s.ContainerName == "Box" && s.Visibility == "private" && s.Signature == "private constructor(list: List<T>) : this(list.first())");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "constructor");
    }

    [Fact]
    public void Extract_Kotlin_AnnotatedDeclarations_AreStillIndexed()
    {
        var content = """
            @Serializable
            class Envelope { }

            @Deprecated("use markedV2")
            fun marked(): Int = 1
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Envelope");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "marked");
    }

    [Fact]
    public void Extract_Kotlin_UseSiteTargetsAndAnnotatedSecondaryConstructors_AreIndexed()
    {
        var content = """
            class Envelope(@field:Deprecated val id: String, @param:Deprecated val count: Int)

            @get:JvmName("displayName")
            val name: String = "x"

            @receiver:JvmName("decorate")
            fun String.marked(): String = this

            class Service {
                @Inject
                constructor() { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Envelope");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "id" && s.ContainerKind == "class" && s.ContainerName == "Envelope");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "count" && s.ContainerKind == "class" && s.ContainerName == "Envelope");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "marked");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Service" && s.ContainerKind == "class" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_Kotlin_DetectsExpandedFeatures()
    {
        var content = "sealed interface Shape\nfun interface Transformer\nvalue class Email(val value: String)\ninner class Handler\n\ncompanion object {\n    const val MAX = 100\n}\n\nfun String.truncate(max: Int): String = take(max)\nsuspend fun fetchData(): List<Int> = emptyList()\ninline fun <reified T> parse(json: String): T = TODO()";
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Shape");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Transformer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Email");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler");
        // Companion object (unnamed) / コンパニオンオブジェクト（無名）
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Companion" && s.Signature != null && s.Signature.Contains("companion object"));
        // Extension function / 拡張関数
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "truncate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetchData");
        // const val / 定数プロパティ
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "MAX");
    }

    [Fact]
    public void Extract_Kotlin_DetectsInheritanceModifierFunctions()
    {
        var content = """
            package demo

            abstract class Base {
                abstract fun required(): Int
                open fun extensible(): String = "base"
                protected open fun scoped(): Int = 1
                internal abstract fun hidden(): Boolean
            }

            class Derived : Base() {
                override fun required(): Int = 42
                override fun extensible(): String = "child"
                final override fun scoped(): Int = 99
                override fun hidden(): Boolean = false
                public override fun toString(): String = "Derived"
                override suspend fun maybeAsync(): Int = 0

                final fun cannotOverride() {}
                private final fun noOverride() {}
                fun normal() {}
                suspend fun suspended() {}
                inline fun inlined() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "required");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "extensible");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "scoped");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "hidden");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "toString");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "maybeAsync");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "cannotOverride");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "noOverride");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normal");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "suspended");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "inlined");
    }

    [Fact]
    public void Extract_Kotlin_DetectsEnumEntries()
    {
        var content = """
            enum class Direction {
                NORTH,
                SOUTH,
                EAST,
                WEST
            }

            enum class HttpStatus(val code: Int) {
                OK(200),
                NOT_FOUND(404),
                SERVER_ERROR(500);

                fun isError(): Boolean = code >= 400
            }

            enum class Color(val rgb: Int) {
                RED(0xFF0000) {
                    override fun display() = "red"
                },
                GREEN(0x00FF00) {
                    override fun display() = "green"
                };

                abstract fun display(): String
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NORTH");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SOUTH");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "EAST");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "WEST");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "OK");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NOT_FOUND");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SERVER_ERROR");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "RED");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "GREEN");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "rgb" && s.ContainerKind == "enum" && s.ContainerName == "Color");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "isError" && s.ContainerKind == "enum" && s.ContainerName == "HttpStatus");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "display" && s.Line == 18);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "display" && s.Line == 21);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "display" && s.Line == 24 && s.ReturnType == "String");
    }

    [Fact]
    public void Extract_Kotlin_AnonymousCompanionDefaultsToCompanionName()
    {
        var content = """
            class Widget {
                companion object {
                    const val MAX = 100
                    fun create(): Widget = Widget()
                }
            }

            class Named {
                companion object Factory {
                    fun build(): Named = Named()
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "kotlin", content);

        Assert.DoesNotContain(symbols, s => string.IsNullOrWhiteSpace(s.Name));

        var anonymousCompanion = Assert.Single(symbols.Where(s =>
            s.Kind == "class"
            && s.Name == "Companion"
            && s.ContainerKind == "class"
            && s.ContainerName == "Widget"));
        Assert.Equal("companion object {", anonymousCompanion.Signature);

        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.Name == "MAX"
            && s.ContainerKind == "class"
            && s.ContainerName == "Companion");
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "create"
            && s.ContainerKind == "class"
            && s.ContainerName == "Companion");
        Assert.Contains(symbols, s =>
            s.Kind == "class"
            && s.Name == "Factory"
            && s.ContainerKind == "class"
            && s.ContainerName == "Named");
    }

    [Fact]
    public void Extract_Ruby_DetectsDefAndClass()
    {
        // Ruby: def, class, module / Ruby: メソッド、クラス、モジュール
        var content = "class UserService\n  def find_user(id)\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "find_user");
    }

    [Fact]
    public void Extract_Perl_DetectsPackageAndSub()
    {
        var content = """
            package Example::Widget;

            sub build {
                return 1;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Example::Widget");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
    }

    [Fact]
    public void Extract_Perl_DetectsImportsAndIgnoresPod()
    {
        var content = """
            =pod
            package Fake::Doc;
            use Fake::InPod;
            =cut

            use strict;
            use Foo::Bar;
            require Baz::Qux;

            package Example::Widget;

            sub build {
                return 1;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Fake::Doc");
        Assert.DoesNotContain(symbols, s => s.Name == "Fake::InPod");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "strict");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Foo::Bar");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Baz::Qux");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Example::Widget");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
    }

    [Fact]
    public void Extract_Ruby_IgnoresEndInsideStringsAndComments()
    {
        var content = "class UserService\n  def find_user(id)\n    puts \"the word end should not close this block\"\n    # end should not close this block either\n    id\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s =>
            s.Kind == "class"
            && s.Name == "UserService"
            && s.StartLine == 1
            && s.EndLine == 7
            && s.BodyStartLine == 2
            && s.BodyEndLine == 7);

        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "find_user"
            && s.StartLine == 2
            && s.EndLine == 6
            && s.BodyStartLine == 3
            && s.BodyEndLine == 6);
    }

    [Fact]
    public void Extract_Ruby_IgnoresEndInsidePercentLiteral()
    {
        var content = "class UserService\n  def quote\n    text = %Q{the word end should stay inside the literal}\n    text\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s =>
            s.Kind == "class"
            && s.Name == "UserService"
            && s.StartLine == 1
            && s.EndLine == 6
            && s.BodyStartLine == 2
            && s.BodyEndLine == 6);

        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "quote"
            && s.StartLine == 2
            && s.EndLine == 5
            && s.BodyStartLine == 3
            && s.BodyEndLine == 5);
    }

    [Fact]
    public void Extract_Ruby_IgnoresEndInsideHeredoc()
    {
        var content = "class UserService\n  def query\n    sql = <<~SQL\n      select 'end' as word\n    SQL\n    sql\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "ruby", content);

        Assert.Contains(symbols, s =>
            s.Kind == "class"
            && s.Name == "UserService"
            && s.StartLine == 1
            && s.EndLine == 8
            && s.BodyStartLine == 2
            && s.BodyEndLine == 8);

        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "query"
            && s.StartLine == 2
            && s.EndLine == 7
            && s.BodyStartLine == 3
            && s.BodyEndLine == 7);
    }

    [Fact]
    public void Extract_PHP_DetectsFunctionsAndClasses()
    {
        // PHP: function, class, trait / PHP: 関数、クラス、トレイト
        var content = "class AuthService {\n    public function login($user) {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "AuthService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "login");
    }

    [Fact]
    public void Extract_PHP_DetectsTraits()
    {
        var content = """
            <?php

            trait InteractsWithCache {
                public function remember(): void {}
            }

            class Example {
                use InteractsWithCache;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "php", content);

        Assert.Contains(symbols, s => s.Kind == "trait" && s.Name == "InteractsWithCache");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "remember");
    }











    [Fact]
    public void Extract_C_DetectsFunctionsAndStructs()
    {
        // C: functions, struct / C: 関数、構造体
        var content = "typedef struct Config {\n    int value;\n};\nunion Packet {\n    int tag;\n};\nint main(int argc) {\n}";
        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "union" && s.Name == "Packet");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main");
    }

    [Fact]
    public void Extract_C_DetectsTypedefStructAndEnumAliases()
    {
        var content = """
            typedef struct Node Node_t;
            typedef struct Payload* PayloadRef;
            typedef union Value Value_t;
            typedef enum Mode Mode_t;

            struct Node {
                int value;
            };

            union Value {
                int number;
            };

            enum Mode {
                ModeIdle,
                ModeActive,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Node_t");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "PayloadRef");
        Assert.Contains(symbols, s => s.Kind == "union" && s.Name == "Value_t");
        Assert.Contains(symbols, s => s.Kind == "union" && s.Name == "Value");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode_t");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Node");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
    }

    [Fact]
    public void Extract_C_DetectsAttributedFunctions()
    {
        var content = """
            __attribute__((noreturn)) void die(void) {
            }

            static inline __attribute__((always_inline)) int add(int a, int b) {
                return a + b;
            }

            __declspec(dllexport) int exported(void) {
                return 0;
            }

            [[nodiscard]] int compute(void) {
                return 1;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "die");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "exported");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "compute");
    }

    [Fact]
    public void Extract_C_DetectsKrStyleAndNestedAttributedFunctions()
    {
        var content = """
            int legacy(a, b)
                int a;
                int b;
            {
                return a + b;
            }

            __attribute__((format(printf, 1, 2))) int log_message(const char *fmt, ...)
            {
                return 0;
            }

            __declspec(align(16)) int aligned_value(void)
            {
                return 1;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "legacy");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "log_message");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "aligned_value");
    }

    [Fact]
    public void Extract_C_DoesNotCapturePrimitiveTypesForFunctionPointerTypedefs()
    {
        // C: function-pointer typedefs and ordinary functions / C: 関数ポインタ typedef と通常関数
        var content = """
            typedef int t_func_int_of_float_double(float, double);
            typedef int (*t_ptr_func_int_of_float_double)(float, double);
            typedef int (*t_ptr_func_int_of_float_complex)(float complex);
            typedef int (*t_ptr_func_int_of_double_complex)(double complex);
            static int add(int a, int b) {
                return a + b;
            }
            void my_callback(int x) {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_callback");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && (s.Name == "int" || s.Name == "void"));
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "t_func_int_of_float_double");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "t_ptr_func_int_of_float_double");
    }

    [Fact]
    public void Extract_C_DetectsFunctionLikeAndObjectLikeMacros()
    {
        // C: function-like and object-like #define macros / C: 関数風・オブジェクト風 #define マクロ
        var content = """
            #include <stdio.h>

            #define MAX(a, b) ((a) > (b) ? (a) : (b))
            #define VERSION "1.0"
            #define MAX_BUFFER 4096

            void work(void) {
                int a = MAX(1, 2);
                printf("v=%s buf=%d\n", VERSION, MAX_BUFFER);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_BUFFER");
    }

    [Fact]
    public void Extract_C_NormalizesIncludeTargets()
    {
        // C: include targets should be searchable by header name / C: include 先はヘッダー名で検索できるべき
        var content = """
            #include <stdio.h>
            # include <stdlib.h>
            #include_next <limits.h>
            #import "legacy.h"
            #include "project/foo.h"
            #include HEADER_NAME
            """;

        var symbols = SymbolExtractor.Extract(1, "c", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "stdio.h");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "stdlib.h");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "limits.h");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "legacy.h");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "project/foo.h");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "HEADER_NAME");
    }

    [Fact]
    public void Extract_Cpp_DetectsClassAndNamespace()
    {
        // C++: class, namespace, functions / C++: クラス、名前空間、関数
        var content = "namespace MyApp {\nconstexpr decltype(foo(42)) value() { return foo(42); }\nclass Handler {\n    void process(int data) {\n    }\n};\n}";
        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler" && s.ContainerName == "MyApp");
        var value = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "value");
        Assert.Equal("MyApp", value.ContainerName);
        Assert.Contains("decltype(foo(42))", value.ReturnType);
    }

    [Fact]
    public void Extract_CppTemplateSpecializations_DistinguishesDeclarationSites()
    {
        var content = """
            template <typename T>
            class Box {};

            template<>
            class Box<int> {};

            template <typename U>
            class Box<U*> {};

            template<>
            void Save<int>(int value) {}
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Box");
        var specializations = symbols.Where(s => s.Kind == "specialization" && s.Name == "Box").ToList();
        Assert.Equal(2, specializations.Count);
        Assert.All(specializations, s => Assert.Equal("Box", s.FamilyKey));
        Assert.Contains(symbols, s => s.Kind == "specialization" && s.Name == "Save" && s.ReturnType == "void");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Signature?.Contains("Box<int>", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extract_Cpp_DetectsClassBodyMembers()
    {
        // C++: constructors, destructors, operator overloads / C++: コンストラクタ、デストラクタ、演算子オーバーロード
        var content = "class Handler { Handler(); ~Handler(); Handler operator+(const Handler& other) const; };";

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Handler" && s.ContainerName == "Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "~Handler" && s.ContainerName == "Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator+" && s.ContainerName == "Handler");
    }

    [Fact]
    public void Extract_Cpp_DetectsExternLinkageFunctions()
    {
        var content = """
            extern "C" int plugin_entry() { return 0; }
            extern "C++" Handler* create_handler() { return nullptr; }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        var entry = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "plugin_entry");
        Assert.Equal("int", entry.ReturnType);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "create_handler");
    }

    [Fact]
    public void Extract_Cpp_DetectsAttributePrefixedFunctions()
    {
        var content = """
            [[nodiscard]] int compute_score() { return 1; }
            [[gnu::always_inline]] inline int fast_path() { return 2; }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "compute_score");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fast_path");
    }

    [Fact]
    public void Extract_Cpp_DetectsNamespaceAliasesAndNamespaceDirectives()
    {
        // C++: namespace aliases and using namespace directives / C++: 名前空間エイリアスと using namespace
        var content = """
            namespace MyApp {
                namespace fs = std::filesystem;
                using namespace std::chrono_literals;

                class Handler {
                };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "fs" && s.ContainerName == "MyApp");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std::chrono_literals" && s.ContainerName == "MyApp");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Handler" && s.ContainerName == "MyApp");
    }

    [Fact]
    public void Extract_Cpp_DetectsFunctionLikeAndObjectLikeMacros()
    {
        // C++: function-like and object-like #define macros / C++: 関数風・オブジェクト風 #define マクロ
        var content = """
            #include <cstdio>

            #define MAX(a, b) ((a) > (b) ? (a) : (b))
            #define VERSION "1.0"
            #define MAX_BUFFER 4096

            void work() {
                auto a = MAX(1, 2);
                std::printf("v=%s buf=%d\n", VERSION, MAX_BUFFER);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MAX_BUFFER");
    }

    [Fact]
    public void Extract_Cpp_DoesNotCapturePrimitiveTypesForFunctionReturningPointerDeclarations()
    {
        // C++: function-returning-pointer declarations / C++: 関数が関数ポインタを返す宣言
        var content = """
            typedef int t_func_int_of_float_double(float, double);
            typedef int (*t_ptr_func_int_of_float_double)(float, double);
            extern int (*XSynchronize(
                Display* display,
                Bool onoff
            ))(
                Display* display
            );

            void my_callback(int x) {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "cpp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_callback");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "int");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "void");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "t_func_int_of_float_double");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "t_ptr_func_int_of_float_double");
    }

    [Fact]
    public void Extract_TypeScript_DetectsInterfaceAndEnum()
    {
        // TypeScript: interface, type, enum / TypeScript: インターフェース、型、列挙型
        var content = "export interface IUser {\n    name: string;\n}\nexport enum Status {\n    Active,\n}";
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "IUser");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
    }

    [Fact]
    public void Extract_Haskell_DetectsTypeSignaturesAndDataTypes()
    {
        // Haskell: type signature, data, import / Haskell: 型シグネチャ、data型、import
        var content = "import Data.List\nimport qualified Data.Map as Map\n\ndata Tree a = Leaf | Node a (Tree a) (Tree a)\n\ninsert :: Ord a => a -> Tree a -> Tree a\ninsert x Leaf = Node x Leaf Leaf";
        var symbols = SymbolExtractor.Extract(1, "haskell", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Data.List");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Data.Map");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Tree");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "insert");
    }

    [Fact]
    public void Extract_Haskell_DoesNotTreatInstancesAsClasses()
    {
        // Haskell instance declarations should not be indexed as phantom type definitions.
        // Haskell の instance 宣言は phantom な型定義としてはインデックスしない。
        var content = """
            class Greeter a where
                greet :: a -> String

            data Person = Person

            instance Greeter Person where
                greet _ = "Hello"

            instance Greeter Int where
                greet _ = "Hi"
            """;

        var symbols = SymbolExtractor.Extract(1, "haskell", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Greeter");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Greeter");
    }

    [Fact]
    public void Extract_FSharp_DetectsLetTypeModuleOpen()
    {
        // F#: let, type, module, open / F#: let束縛、型、モジュール、open
        var content = """
            module MyApp.Domain

            open System
            open type System.Math

            type UserId = int
            type User = { Name: string; Age: int }
            type Color = Red | Green | Blue
            exception ``domain error`` of string
            type Person(name: string) =
                member _.Name = name

            let x = 1
            let mutable counter = 0
            let inline add x y = x + y
            let private secret = 42
            let internal hidden = "x"
            let ``spaced name`` = 1

            let validate user =
                user.Age > 0

            let rec factorial n =
                if n <= 1 then 1 else n * factorial (n - 1)
            """;
        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp.Domain");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.Math");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "UserId");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Color");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
        Assert.Contains(symbols, s => s.Kind == "exception" && s.Name == "domain error");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "x");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "counter");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "add");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "secret");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "hidden");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "spaced name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "validate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "factorial");
    }

    [Fact]
    public void Extract_FSharp_DetectsTypeAbbreviations()
    {
        var content = """
            module MyApp.Types

            type UserId = int
            type OrderId = string
            type Names = string list
            type public PublicId = int
            type Result<'T> = Choice<'T, string>
            type Pair<'T, 'U> = 'T * 'U
            type rec Tree<'T> = Leaf | Node of 'T * Tree<'T>
            type rec Workflow = Started | Finished
            type public Visibility = Public | Internal
            """;
        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "UserId");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "OrderId");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "Names");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "PublicId");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "Result");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "Pair");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Tree");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Workflow");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Visibility");
        Assert.DoesNotContain(symbols, s => s.Kind == "enum" && s.Name == "Result");
        Assert.DoesNotContain(symbols, s => s.Kind == "enum" && s.Name == "Pair");
        Assert.DoesNotContain(symbols, s => s.Kind == "typealias" && s.Name == "Tree");
        Assert.DoesNotContain(symbols, s => s.Kind == "typealias" && s.Name == "Workflow");
    }

    [Fact]
    public void Extract_FSharp_DetectsNamespacesModulesAndMembers()
    {
        // F#: namespace rec, module private, member forms / F#: namespace rec、module private、member形
        var content = """
            namespace rec MyApp.Domain
            module Json = System.Text.Json
            module Helpers = ``Legacy Helpers``
            module ``Domain Helpers``
            open ``Domain Helpers``

            type UserId = int
            type OrderId = string
            type ``User Record`` = { Name: string }
            type ``Color Choice`` = Red | Blue
            type ``Worker Type``() = class end
            type Box<'T> = class end
            type ConstrainedBox<'T when 'T : not struct> = class end
            type Factory<'T>(value: 'T) = class end
            type ConstrainedFactory<'T when 'T : not struct>(value: 'T) = class end

            type Person(name: string) =
                member this.Name = name
                member this.``display name`` = name
                member val DisplayName = name with get, set
                member _.Age = 0
                static member Create(name: string) = Person(name)
                override this.ToString() = this.Name

            type IVisitor =
                abstract member Visit : unit -> unit
                abstract Reset : unit -> unit
                val Id : string
                val mutable Count : int

            type ILogger = interface end

            type Coordinates = struct end

            type Handler = delegate of string -> unit

            let validate user =
                user.Age > 0

            let rec isEven n = n = 0 || isOdd (n - 1)
            and isOdd n = n <> 0 && isEven (n - 1)

            let workflow user =
                task {
                    use client = createClient user
                    let! loadedUser = loadUser user
                    use! lease = acquireLease loadedUser
                    return loadedUser
                }
            """;

        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp.Domain");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.Text.Json");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Legacy Helpers");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Domain Helpers");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Domain Helpers");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Worker Type");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Box");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ConstrainedBox");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Factory");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ConstrainedFactory");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "ILogger");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "User Record");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Color Choice");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Coordinates");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "UserId");
        Assert.Contains(symbols, s => s.Kind == "typealias" && s.Name == "OrderId");
        Assert.DoesNotContain(symbols, s => s.Kind == "typealias" && s.Name == "ILogger");
        Assert.DoesNotContain(symbols, s => s.Kind == "typealias" && s.Name == "Coordinates");
        Assert.DoesNotContain(symbols, s => s.Kind == "typealias" && s.Name == "Handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "display name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DisplayName");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Age");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Create");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ToString");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Visit");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Reset");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Id");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Count");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "validate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "isEven");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "isOdd");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "client");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "loadedUser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "lease");
    }

    [Fact]
    public void Extract_FSharp_DetectsActivePatternDefinitions()
    {
        var content = """
            module MyApp.Patterns

            let (|Even|Odd|) n =
                if n % 2 = 0 then Even else Odd

            let private (|ParseInt|_|) (value: string) =
                match System.Int32.TryParse(value) with
                | true, parsed -> Some parsed
                | false, _ -> None
            """;

        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Even");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Odd");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ParseInt");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "_");
    }

    [Fact]
    public void Extract_FSharp_DetectsOperatorDefinitions()
    {
        var content = """
            module MyApp.Operators

            let (++) left right = left + right
            let inline (>>=) value binder = binder value
            """;

        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator ++");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "operator >>=");
    }

    [Fact]
    public void Extract_FSharp_DetectsUnionCasesAndRecordFields()
    {
        var content = """
            module MyApp.Domain

            type Color =
                | Red
                | [<Obsolete>] Amber
                | Green
                | Blue

            type Person =
                { Name: string
                  Age: int }
        """;

        var symbols = SymbolExtractor.Extract(1, "fsharp", content);
        Assert.Contains(symbols, s => s.Name == "Red");
        Assert.Contains(symbols, s => s.Name == "Amber");
        Assert.Contains(symbols, s => s.Name == "Green");
        Assert.Contains(symbols, s => s.Name == "Blue");
        Assert.Contains(symbols, s => s.Name == "Name");
        Assert.Contains(symbols, s => s.Name == "Age");
    }

    [Fact]
    public void Extract_FSharp_DetectsValueBindings()
    {
        // F# value bindings should be indexed by their binding names / F# の値束縛は
        // 束縛名で索引される。
        var content = "let x = 5\nlet name = \"hello\"\nlet list = [1; 2; 3]";
        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "x");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "list");
    }

    [Fact]
    public void Extract_VB_DetectsCompoundVisibility()
    {
        // VB.NET compound visibility: Protected Friend / VB.NET 複合可視性
        var content = "Protected Friend Sub OnInit()\nEnd Sub\n\nPrivate Protected Function GetData() As String\n    Return \"\"\nEnd Function";
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnInit");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetData");
    }

    [Fact]
    public void Extract_VB_DetectsShadowsMembers()
    {
        // VB.NET Shadows members should still be searchable / VB.NET の Shadows 付き member も
        // 変わらず検索できること。
        var content = """
            Public Class DerivedWidget
                Public Shadows Sub Render()
                End Sub

                Public Shadows Function Compute() As Integer
                    Return 42
                End Function

                Public Shadows ReadOnly Property Count As Integer
                    Get
                        Return 1
                    End Get
                End Property
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Render");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Compute");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Count");
    }

    [Fact]
    public void Extract_VB_DetectsNotOverridableMembers()
    {
        var content = """
            Public Class DerivedWidget
                Public NotOverridable Overrides Sub Render()
                End Sub

                Public NotOverridable Overrides Property Count As Integer
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Render");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Count");
    }

    [Fact]
    public void Extract_VB_DetectsNestedEnumMembersAndMembersAfterEnum()
    {
        // VB.NET enum bodies should stay searchable, and nested enums must not cut off later
        // declarations in the containing class / VB.NET の enum 本体は検索対象のままであり、
        // ネストした enum が外側クラスの後続宣言を切り捨てないこと。
        var content = """
            Namespace MyApp
                Public Class Widget
                    Public Enum Color
                        Red
                        Green = 1
                    End Enum

                    Public Function AfterEnum() As Integer
                        Return 1
                    End Function
                End Class
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Color");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Red");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Green");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "AfterEnum");
    }

    [Fact]
    public void Extract_VB_DetectsEnumMembersWithAttributesAndMultilineInitializers()
    {
        // VB.NET enum members can carry attributes and split initializers across lines.
        // The fallback needs to keep both shapes searchable / VB.NET の enum member は属性と
        // 複数行 initializer を持てるため、fallback が両方を検索可能に保つこと。
        var content = """
            Public Enum Status
                <Obsolete,
                 System.ComponentModel.Description("legacy")>
                Legacy

                Complex = If(
                    True,
                    1,
                    2)

                Ready
            End Enum
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Legacy");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Complex");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Ready");
    }















    [Fact]
    public void Extract_CSharp_MultiLineFieldIgnoresBraceInsideStringLiteral()
    {
        // Brace detection must use the sanitized match line, not the raw source, so a
        // `{` that lives inside a string literal or comment doesn't flip the field path
        // into property-body handling and then silently drop the declaration. Closes
        // #298 follow-up (third codex adversarial review).
        // brace 検出はサニタイズ済みのマッチ行で行わなければならない。raw 行を見ると
        // 文字列リテラルやコメント内の `{` で field 経路が property 本体扱いに切り替わり、
        // 宣言が黙って消える恐れがあるため。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "public class Templates",
            "{",
            "    private string",
            "        _open = \"{\";",
            "    private string",
            "        _pair = \"{\" + \"}\";",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_open"
            && s.Visibility == "private"
            && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_pair"
            && s.Visibility == "private"
            && s.ReturnType == "string");
    }

    [Fact]
    public void Extract_CSharp_DetectsDeclaratorListFields()
    {
        // `private int _x, _y;` must emit one `property` symbol per declarator. The
        // field regex greedily swallows earlier declarators into `returnType`, so the
        // post-match expander walks the top-level commas in `returnType` and the tail
        // after the match to recover every declarator name. Closes #298 follow-up
        // (codex adversarial review).
        // `private int _x, _y;` のような declarator list は declarator ごとに 1 件の
        // `property` シンボルを発行する。field regex は前段の declarator を
        // returnType に飲み込むため、post-match 展開で returnType のトップレベル `,`
        // とマッチ後テールを走査し、すべての declarator 名を復元する。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "public class Holder",
            "{",
            "    private int _x, _y;",
            "    public string First, Second, Third;",
            "    private int _a = 1, _b, _c = 3;",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_x" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_y" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "First" && s.ReturnType == "string" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Second" && s.ReturnType == "string" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Third" && s.ReturnType == "string" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_a" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_b" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_c" && s.ReturnType == "int" && s.Visibility == "private");
        // The bogus `int _x,` or `int _a = 1,` returnType from a single-symbol emit must
        // not leak into the index. 単一シンボル発行で紛れ込む `int _x,` 等の returnType は
        // インデックスに漏らさない。
        Assert.DoesNotContain(symbols, s => s.ReturnType != null && s.ReturnType.Contains(','));
    }

    [Fact]
    public void Extract_CSharp_SameLineDeclaratorListsStillExpandAfterEarlierSiblings()
    {
        // Same-line field declarator lists must still expand trailing declarators even
        // when the field statement is only reached after an earlier sibling on the same
        // physical line restarts matching. The current branch already handles the #582
        // repro shapes; this test locks that behavior so a future column-domain mismatch
        // does not silently drop `B`. Closes #582.
        // 同一物理行で先行 sibling の後ろから field 文に再入した場合でも、same-line の
        // declarator list は末尾 declarator を展開し続けなければならない。現行ブランチは
        // #582 の再現形を既に正しく処理できるため、このテストで将来の列座標ずれ回帰から
        // `B` が無言で欠落するのを防ぐ。Closes #582.
        var cases = new[]
        {
            new
            {
                Content = "public class C { public void M() { } public int A = 1, B; }",
                SiblingKind = "function",
                SiblingName = "M",
                Signature = "public int A = 1, B;"
            },
            new
            {
                Content = "public class C { public int P { get; set; } public int A, B; }",
                SiblingKind = "property",
                SiblingName = "P",
                Signature = "public int A, B;"
            },
            new
            {
                Content = "public class C { public class N { } public int A = 1, B; }",
                SiblingKind = "class",
                SiblingName = "N",
                Signature = "public int A = 1, B;"
            }
        };

        foreach (var @case in cases)
        {
            var symbols = SymbolExtractor.Extract(1, "csharp", @case.Content);

            Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C");
            Assert.Contains(symbols, s => s.Kind == @case.SiblingKind && s.Name == @case.SiblingName
                && s.ContainerKind == "class" && s.ContainerName == "C");

            var a = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "A"));
            Assert.Equal("class", a.ContainerKind);
            Assert.Equal("C", a.ContainerName);
            Assert.Equal(@case.Signature, a.Signature);

            var b = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "B"));
            Assert.Equal("class", b.ContainerKind);
            Assert.Equal("C", b.ContainerName);
            Assert.Equal(@case.Signature, b.Signature);
        }
    }

    [Fact]
    public void Extract_CSharp_EmptySameLineNestedTypeStillExposesLaterSiblingType()
    {
        // Stepping into a same-line nested type body must only happen when there is an
        // actual member after the opening `{`. For an empty nested interface body, the
        // next statement start is the closing `}`, and restarting there would skip the
        // later same-line sibling type entirely. Closes #585.
        // same-line の nested type 本体へ潜る再開は、開き `{` の後に実際の member がある
        // ときだけ行う必要がある。空の nested interface 本体では次の文頭が closing `}`
        // になり、そこへ再開すると後続の same-line sibling type が丸ごと落ちる。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "[A]",
            "public class Outer { public interface I<T1,           T2> { } public class Sibling { } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "interface"
            && s.Name == "I"
            && s.ContainerName == "Outer");
        Assert.Contains(symbols, s => s.Kind == "class"
            && s.Name == "Sibling"
            && s.ContainerName == "Outer"
            && s.Signature == "public class Sibling { }");
    }

    [Fact]
    public void Extract_CSharp_EmptySameLineNestedTypeStillExposesLaterOuterProperty()
    {
        // When a real nested same-line type ends before a later outer sibling property on
        // the same physical line, extraction must skip the nested type's closing `}` and
        // resume at the later property instead of treating the empty body as a restart
        // target. This is the closing-line outer-sibling variant found during review.
        // 実在する same-line nested type の後ろに outer 側の sibling property が同じ物理行
        // で続く場合、抽出は nested type の closing `}` を飛ばして後続 property から
        // 再開しなければならない。空本体そのものを再開先にしてしまうと outer sibling が
        // 欠落する。review で見つかった closing-line outer-sibling 変種を固定する。
        var content = """
            namespace Demo;

            public class Host
            {
                public class Wrapped<T>
                    where T : class
                {
                    public class Child { } } public int P { get; set; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class"
            && s.Name == "Child"
            && s.ContainerName == "Wrapped");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "P"
            && s.ContainerName == "Host"
            && s.Signature == "public int P { get; set; }");
    }

    [Fact]
    public void Extract_CSharp_SameLineClassBodyFieldIsCapturedAndLocalIsRejected()
    {
        // Column-aware scope tracking: `public class C { public int X; }` must capture
        // both the outer class C and the inner field X. Before #400, the type-body gate
        // only looked at the scope at line start, so a same-line class body looked like
        // "not inside a type body" and X was silently dropped. Conversely,
        // `public void M() { int local = 1; }` inside a class must NOT emit a phantom
        // `property local`: column-wise, col where `int local` starts sits inside a
        // method body (not a class body), and the column-aware gate correctly rejects it.
        // Closes #400.
        // 列単位のスコープ追跡: `public class C { public int X; }` では外側の class C と
        // 同一行のフィールド X をどちらも拾う必要がある。#400 以前は line-start のみを
        // 見ていたため、同一行の class body は「型本体の中ではない」と誤判定され X が
        // 取りこぼされた。逆に class 内の `public void M() { int local = 1; }` では、
        // `int local` の列が method body の中（class body ではない）であることを列意識
        // ゲートが認識するため、擬似的な `property local` を生成してはならない。
        // Closes #400.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C { public int X; }",
            "",
            "public class D",
            "{",
            "    public void M() { int local = 1; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X"
            && s.ContainerKind == "class" && s.ContainerName == "C"
            && s.Signature == "public int X;");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "D");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M"
            && s.ContainerKind == "class" && s.ContainerName == "D");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "local");
    }

    [Fact]
    public void Extract_CSharp_SameLineAutoPropertiesInsideTypeBodiesAreCaptured()
    {
        // Same-line C# type bodies already recover methods, events, and plain fields, so
        // brace-body auto-properties must also survive when nested inside the same
        // `class/struct/interface { ... }` physical line. Before #470, the brace-property
        // skip guard inspected the line's first `{`, which belongs to the enclosing type
        // body, and silently discarded `P { get; set; }` / `R { get; }` as "not a
        // property". Closes #470.
        // 同一行 C# 型本体では method / event / plain field は既に復元できるため、
        // `class/struct/interface { ... }` と同じ物理行にある brace-body auto-property も
        // 抽出されなければならない。#470 前は brace-property の skip guard が行頭側の
        // 最初の `{`（外側型本体）を見てしまい、`P { get; set; }` / `R { get; }` を
        // 「property ではない」と誤判定して無言で捨てていた。Closes #470.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C { public int P { get; set; } }",
            "public struct S { public int Q { get; set; } }",
            "public interface I { int R { get; } }",
            "public class MethodsOk { public void M() { } }",
            "public class EventsOk { public event System.EventHandler E; }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var p = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", p.ContainerKind);
        Assert.Equal("C", p.ContainerName);
        Assert.Equal("public int P { get; set; }", p.Signature);

        var q = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("struct", q.ContainerKind);
        Assert.Equal("S", q.ContainerName);
        Assert.Equal("public int Q { get; set; }", q.Signature);

        var r = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "R"));
        Assert.Equal("interface", r.ContainerKind);
        Assert.Equal("I", r.ContainerName);
        Assert.Equal("int R { get; }", r.Signature);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M"
            && s.ContainerKind == "class" && s.ContainerName == "MethodsOk");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "E"
            && s.ContainerKind == "class" && s.ContainerName == "EventsOk");
    }

    [Fact]
    public void Extract_CSharp_SameLineAutoPropertyAfterExpressionBodiedMethodIsCaptured()
    {
        // Outer-type false positives must still be skipped even when a later same-line
        // member introduces `=>`. Before the follow-up fix for #470, the brace-property
        // guard looked for `=>` anywhere in the remaining line, so
        // `public class C { public int M() => 1; public int P { get; set; } }`
        // treated the outer class header as an "expression-bodied property" and broke
        // before reaching `P`. Closes #470.
        // 同一行後半の member が `=>` を含んでいても、outer type 由来の偽陽性は
        // 引き続き弾かれなければならない。#470 の追修正前は brace-property guard が
        // 行末までのどこかに `=>` があるだけで式本体 property 扱いしてしまい、
        // `public class C { public int M() => 1; public int P { get; set; } }`
        // で outer class header を誤許可し、`P` まで到達できなかった。Closes #470.
        var content = "public class C { public int M() => 1; public int P { get; set; } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("class", method.ContainerKind);
        Assert.Equal("C", method.ContainerName);
        Assert.Equal("public int M() => 1;", method.Signature);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", property.ContainerKind);
        Assert.Equal("C", property.ContainerName);
        Assert.Equal("public int P { get; set; }", property.Signature);
    }

    [Fact]
    public void Extract_CSharp_HeaderLineAutoPropertyInsideMultilineTypeBodyIsCaptured()
    {
        // A C# type can open its body on the header line while still closing on a later
        // line (`public class C { public int P { get; }` + next-line `}`). Before #580,
        // the outer class/struct/interface match stopped the same-line scan because the
        // type body was not fully compact on one line, so the first member that shared
        // the header line silently disappeared. Closes #580.
        // C# の型は、本体開始 `{` をヘッダ行に置いたまま閉じ `}` を後続行へ送れる
        // (`public class C { public int P { get; }` + 次行 `}`)。#580 前は outer
        // class/struct/interface のマッチ時点で same-line scan が止まり、ヘッダ行を
        // 共有する最初の member が無言で脱落していた。Closes #580.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class Outer { public int P { get; }",
            "}",
            "public struct Holder { public int Q { get; }",
            "}",
            "public interface IOuter { int R { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var p = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", p.ContainerKind);
        Assert.Equal("Outer", p.ContainerName);
        Assert.Equal("public int P { get; }", p.Signature);

        var q = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("struct", q.ContainerKind);
        Assert.Equal("Holder", q.ContainerName);
        Assert.Equal("public int Q { get; }", q.Signature);

        var r = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "R"));
        Assert.Equal("interface", r.ContainerKind);
        Assert.Equal("IOuter", r.ContainerName);
        Assert.Equal("int R { get; }", r.Signature);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Outer");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Holder");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "IOuter");
    }

    [Fact]
    public void Extract_CSharp_SameLineAutoPropertyAfterExpressionBodiedPropertyIsCaptured()
    {
        // Same-line C# type bodies must not skip the first real member just because an
        // outer-type false-positive property candidate overran into a later sibling while
        // scanning for `{` / `=>`. In
        // `public class C { public int A => 1; public int P { get; set; } }`,
        // both `A` and `P` must survive and there must be no phantom `property C`.
        // Closes #472.
        // 同一行 C# 型本体では、outer-type 由来の偽 property 候補が後続 sibling まで
        // 食い込んだとしても、最初の本物 member を飛ばしてはならない。
        // `public class C { public int A => 1; public int P { get; set; } }`
        // では `A` と `P` の両方が抽出され、phantom `property C` が出てはいけない。
        // Closes #472.
        var content = "public class C { public int A => 1; public int P { get; set; } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var expressionProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "A"));
        Assert.Equal("class", expressionProperty.ContainerKind);
        Assert.Equal("C", expressionProperty.ContainerName);
        Assert.Equal("public int A => 1;", expressionProperty.Signature);

        var autoProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", autoProperty.ContainerKind);
        Assert.Equal("C", autoProperty.ContainerName);
        Assert.Equal("public int P { get; set; }", autoProperty.Signature);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "C");
    }

    [Fact]
    public void Extract_CSharp_MultiLineConstantPatterns_DoNotBecomePhantomProperties()
    {
        // issue #779: multi-line expression-bodied constant patterns (`value is` + later-line
        // `Red` / `or Red`) must stay inside the enclosing method body. Before the fix, the
        // continuation lines re-entered the plain-field regex, emitted phantom `property Red`
        // rows, and downstream reference extraction suppressed the real pattern heads.
        // issue #779: 複数行の式本体 constant pattern（`value is` の次行に `Red` / `or Red`）
        // は enclosing method body の内部として扱われなければならない。修正前は継続行が
        // plain-field regex に再突入して phantom `property Red` を出し、後段の reference
        // 抽出が本物の pattern head を抑止していた。
        const string content = """
            using static Demo.Color;

            namespace Demo;

            public enum Color
            {
                Red
            }

            public sealed class Uses
            {
                public bool Match(object value) => value is
                    Red
                    or
                    Red;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var match = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Match"));
        Assert.Equal(12, match.StartLine);
        Assert.Equal(15, match.EndLine);
        Assert.Equal(12, match.BodyStartLine);
        Assert.Equal(15, match.BodyEndLine);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Red" && s.ContainerName == "Uses");
    }

    [Fact]
    public void Extract_CSharp_MultiLineExpressionBodiedMethod_TerminatorLineKeepsFullSignatureAndSiblingField()
    {
        // issue #835 / #836: when a multi-line expression-bodied member ends on the next line,
        // a real same-line sibling after that terminating `;` must still be extracted, and the
        // stored signature must include the continuation line through the terminator.
        // issue #835 / #836: 複数行の式本体メンバーが次行の `;` で終わる場合でも、その
        // `;` の後ろにある同一行 sibling は抽出されなければならず、保存される signature も
        // 継続行を含めて終端 `;` までを保持していなければならない。
        const string content = """
            namespace Demo;

            public enum Color
            {
                Red
            }

            public sealed class Uses
            {
                public bool Match(object value) => value is
                    Red; public int X;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var match = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Match"));
        Assert.Equal(10, match.StartLine);
        Assert.Equal(11, match.EndLine);
        Assert.Equal(10, match.BodyStartLine);
        Assert.Equal(11, match.BodyEndLine);
        Assert.Equal("public bool Match(object value) => value is Red;", match.Signature);

        var x = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "X"));
        Assert.Equal(11, x.StartLine);
        Assert.Equal(11, x.EndLine);
        Assert.Equal("Uses", x.ContainerName);
        Assert.Equal("class", x.ContainerKind);
        Assert.Equal("public int X;", x.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineAutoPropertyBeforeMethodIsCaptured()
    {
        // Mixed-kind same-line siblings must not lose the earlier brace-body property when a
        // later method on the same physical line needs a different regex family. This locks the
        // property side of `property -> method` so the outer-type false-positive skip and the
        // same-line continuation machinery do not silently leave only the method behind.
        // Closes #472 / #473 follow-up.
        // 同一行の mixed-kind sibling では、後続 method が別 regex 群を必要とするからと
        // いって、手前の brace-body property を落としてはならない。outer type 偽陽性の
        // skip と same-line 継続処理が組み合わさって method だけ残る退行を防ぐため、
        // `property -> method` の property 側を固定する。Closes #472 / #473 follow-up.
        var content = "public class C { public int P { get; set; } public void M() { } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", property.ContainerKind);
        Assert.Equal("C", property.ContainerName);
        Assert.Equal("public int P { get; set; }", property.Signature);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("class", method.ContainerKind);
        Assert.Equal("C", method.ContainerName);
        Assert.Equal("public void M() { }", method.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineAutoPropertyBeforeEventIsCaptured()
    {
        // The same continuation path must also preserve brace-body properties when the next
        // same-line sibling is an event declaration instead of a method. This guards the
        // `property -> event` shape covered by the mixed-kind same-line follow-up issue.
        // Closes #472 / #473 follow-up.
        // 同じ継続経路は、次の same-line sibling が method ではなく event の場合でも
        // brace-body property を保持しなければならない。mixed-kind same-line の追件で
        // 問題になった `property -> event` 形をここで固定する。Closes #472 / #473 follow-up.
        var content = "public class C { public int P { get; set; } public event System.EventHandler E; }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", property.ContainerKind);
        Assert.Equal("C", property.ContainerName);
        Assert.Equal("public int P { get; set; }", property.Signature);

        var eventSymbol = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", eventSymbol.ContainerKind);
        Assert.Equal("C", eventSymbol.ContainerName);
        Assert.Equal("public event System.EventHandler E;", eventSymbol.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineEventBeforeMethodIsCaptured()
    {
        // Mixed-kind same-line siblings must also preserve an earlier event when a later
        // method shares the same physical line. Without a C#-specific defer/restart path,
        // the method regex can claim the later sibling before the event row ever runs and
        // silently drop `event E`.
        // Closes #473 follow-up.
        // 同一行の mixed-kind sibling では、後続 method が同じ物理行にある場合でも
        // 手前の event を落としてはならない。C# 専用の defer/restart 経路が無いと、
        // method regex が先に後続 sibling を取って `event E` が無言で欠落する。
        // Closes #473 follow-up.
        var content = "public class C { public event System.EventHandler E; public void M() { } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var eventSymbol = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", eventSymbol.ContainerKind);
        Assert.Equal("C", eventSymbol.ContainerName);
        Assert.Equal("public event System.EventHandler E;", eventSymbol.Signature);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("class", method.ContainerKind);
        Assert.Equal("C", method.ContainerName);
        Assert.Equal("public void M() { }", method.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineAccessorEventBeforeMethodIsCaptured()
    {
        // Accessor-bodied events use `{ add/remove }` instead of `;`, so same-line mixed-kind
        // recovery must treat the accessor body as the event's boundary and still restart at
        // the following sibling method. Otherwise the event signature absorbs `public void M`
        // and the method disappears.
        // Closes #473 follow-up.
        // アクセサ本体付き event は `;` ではなく `{ add/remove }` を持つため、same-line の
        // mixed-kind 回復でも event 本体終端を境界として扱い、後続 method 位置から再開
        // しなければならない。そうしないと event の signature が `public void M` を
        // 飲み込み、method 自体が消える。Closes #473 follow-up.
        var content = "public class C { public event System.Action E { add { } remove { } } public void M() { } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var eventSymbol = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", eventSymbol.ContainerKind);
        Assert.Equal("C", eventSymbol.ContainerName);
        Assert.Equal("public event System.Action E { add { } remove { } }", eventSymbol.Signature);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("class", method.ContainerKind);
        Assert.Equal("C", method.ContainerName);
        Assert.Equal("public void M() { }", method.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineAccessorEventBeforePropertyIsCaptured()
    {
        // Accessor-bodied events must stop at their own closing `}` even when the next same-line
        // sibling is a property. Otherwise the semicolon fallback keeps scanning until the later
        // property terminator, the event signature absorbs `public int P`, and the property never
        // gets a restart chance. Closes #519.
        // accessor body を持つ event は、次の same-line sibling が property の場合でも
        // 自身の閉じ `}` で終端しなければならない。そうしないと semicolon fallback が
        // 後続 property の終端まで進み、event signature に `public int P` が混入し、
        // property 側の再開機会も失われる。Closes #519.
        var content = "public class C { public event System.Action E { add { } remove { } } public int P { get; set; } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var eventSymbol = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", eventSymbol.ContainerKind);
        Assert.Equal("C", eventSymbol.ContainerName);
        Assert.Equal("public event System.Action E { add { } remove { } }", eventSymbol.Signature);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", property.ContainerKind);
        Assert.Equal("C", property.ContainerName);
        Assert.Equal("public int P { get; set; }", property.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineEventBeforeDelegateIsCaptured()
    {
        // Delegate rows sit ahead of event rows in the C# pattern list, so a failed delegate
        // match at `event E;` must not keep scanning forward and claim the later delegate.
        // Otherwise the earlier event never reaches its own regex family and silently drops.
        // Closes #522.
        // C# の pattern 順では delegate 行が event 行より前にあるため、`event E;` で失敗した
        // delegate row が後続 delegate まで進んではならない。進んでしまうと手前の event が
        // 自分の regex family に到達できず、無言で欠落する。Closes #522.
        var content = "public class C { public event System.Action E; public delegate void D(); }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var eventSymbol = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", eventSymbol.ContainerKind);
        Assert.Equal("C", eventSymbol.ContainerName);
        Assert.Equal("public event System.Action E;", eventSymbol.Signature);

        var delegateSymbol = Assert.Single(symbols.Where(s => s.Kind == "delegate" && s.Name == "D"));
        Assert.Equal("class", delegateSymbol.ContainerKind);
        Assert.Equal("C", delegateSymbol.ContainerName);
        Assert.Equal("public delegate void D();", delegateSymbol.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineAccessorEventBeforeDelegateIsCaptured()
    {
        // The same cross-family starvation also applies when the earlier declaration is an
        // accessor-bodied event: the event must clamp at its own accessor block, and the later
        // delegate must only be reached via the explicit restart path rather than by skipping
        // past the event statement. Closes #519 / #522.
        // 同じ cross-family の starvation は、手前が accessor-bodied event の場合にも起こる。
        // event 自身は accessor block で正しく切れ、後続 delegate には event 文を飛び越える
        // のではなく明示的な restart 経路で到達しなければならない。Closes #519 / #522.
        var content = "public class C { public event System.Action E { add { } remove { } } public delegate void D(); }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "C"));

        var eventSymbol = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", eventSymbol.ContainerKind);
        Assert.Equal("C", eventSymbol.ContainerName);
        Assert.Equal("public event System.Action E { add { } remove { } }", eventSymbol.Signature);

        var delegateSymbol = Assert.Single(symbols.Where(s => s.Kind == "delegate" && s.Name == "D"));
        Assert.Equal("class", delegateSymbol.ContainerKind);
        Assert.Equal("C", delegateSymbol.ContainerName);
        Assert.Equal("public delegate void D();", delegateSymbol.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineDelegateBeforeAccessorEventWithLaterSiblingIsCaptured()
    {
        // After a leading delegate restarts the same-line scan at a later accessor-bodied event,
        // the defer checks for function/property/event/delegate rows must still see the raw event
        // statement start. If they inspect the property/function merged candidate instead, they
        // can skip directly to the trailing sibling and silently drop the middle custom event.
        // Lock the issue #603 repro plus the sibling-family matrix (`property` / `method` /
        // `delegate` / `event`) in one fixture. Closes #603.
        // 先頭 delegate から later accessor-bodied event へ same-line restart した後でも、
        // function/property/event/delegate 各 row の defer 判定は raw の event 文開始位置を
        // 見続けなければならない。property/function 用の merged candidate を見てしまうと、
        // 後続 sibling へ直接飛んで中間 custom event が無言で欠落する。#603 の最小再現と、
        // 後続 sibling family (`property` / `method` / `delegate` / `event`) を 1 fixture で
        // 固定する。Closes #603.
        var content = string.Join(
            "\n",
            "public class PropertyCase { public delegate void D(); public event System.Action E { add { } remove { } } public int P { get; set; } }",
            "public class MethodCase { public delegate void D(); public event System.Action E { add { } remove { } } public void M() { } }",
            "public class DelegateCase { public delegate void D1(); public event System.Action E { add { } remove { } } public delegate void D2(); }",
            "public class EventCase { public delegate void D1(); public event System.Action E { add { } remove { } } public event System.Action E2; }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var propertyEvent = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E" && s.ContainerName == "PropertyCase"));
        Assert.Equal("public event System.Action E { add { } remove { } }", propertyEvent.Signature);
        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P" && s.ContainerName == "PropertyCase"));
        Assert.Equal("public int P { get; set; }", property.Signature);

        var methodEvent = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E" && s.ContainerName == "MethodCase"));
        Assert.Equal("public event System.Action E { add { } remove { } }", methodEvent.Signature);
        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M" && s.ContainerName == "MethodCase"));
        Assert.Equal("public void M() { }", method.Signature);

        var delegateEvent = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E" && s.ContainerName == "DelegateCase"));
        Assert.Equal("public event System.Action E { add { } remove { } }", delegateEvent.Signature);
        var delegateTail = Assert.Single(symbols.Where(s => s.Kind == "delegate" && s.Name == "D2" && s.ContainerName == "DelegateCase"));
        Assert.Equal("public delegate void D2();", delegateTail.Signature);

        var eventEvent = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E" && s.ContainerName == "EventCase"));
        Assert.Equal("public event System.Action E { add { } remove { } }", eventEvent.Signature);
        var trailingEvent = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E2" && s.ContainerName == "EventCase"));
        Assert.Equal("public event System.Action E2;", trailingEvent.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineAutoPropertyAfterConstructorsIsCaptured()
    {
        // Same-line C# constructors must not stop later sibling declarations from
        // reaching their own patterns. The #470 follow-up initially only resumed
        // after method-like patterns with a return type, so
        // `public class C { public C() { } public int P { get; set; } }`
        // still dropped `P` while the same shape after a normal method worked.
        // Issue #478 showed an additional starvation path: the dedicated static-ctor
        // regex sat after property rows, so
        // `public class D { static D() { } public int Q { get; set; } }`
        // indexed `Q` but silently lost the static ctor itself. Lock both ctor kinds
        // and the later properties in one same-line fixture. Closes #470 / #478.
        // 同一行の C# constructor は、その後ろに続く sibling 宣言の pattern 到達を
        // 止めてはならない。#470 の追修正当初は戻り値型を持つ method 系だけを再開
        // していたため、`public class C { public C() { } public int P { get; set; } }`
        // では通常 method 後と違って `P` がまだ落ちていた。さらに #478 では、
        // static ctor 専用 regex が property 行より後ろにあったため
        // `public class D { static D() { } public int Q { get; set; } }`
        // で `Q` は出ても static ctor 自体が欠落していた。instance / static ctor と
        // 後続 property の両方をこの same-line fixture で固定する。Closes #470 / #478.
        var content = string.Join(
            "\n",
            "public class C { public C() { } public int P { get; set; } }",
            "public class D { static D() { } public int Q { get; set; } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "C"));
        Assert.Equal("class", ctor.ContainerKind);
        Assert.Equal("C", ctor.ContainerName);

        var p = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", p.ContainerKind);
        Assert.Equal("C", p.ContainerName);
        Assert.Equal("public int P { get; set; }", p.Signature);

        var staticCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "D"));
        Assert.Equal("class", staticCtor.ContainerKind);
        Assert.Equal("D", staticCtor.ContainerName);
        Assert.Equal("static D() { }", staticCtor.Signature);

        var q = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("class", q.ContainerKind);
        Assert.Equal("D", q.ContainerName);
        Assert.Equal("public int Q { get; set; }", q.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineMixedMemberKindsAreAllCaptured()
    {
        // Compact same-line C# type bodies must behave like a sibling stream even when
        // adjacent declarations are different member kinds. Before #473, `event E;`
        // short-circuited later auto-properties, and interface-style `void M();`
        // swallowed the following property into the method signature. Guard both
        // issue repros plus the reverse `property + event` order in one fixture.
        // 同一行のコンパクトな C# 型本体は、隣接宣言が異なる member kind でも
        // sibling ストリームとして扱われなければならない。#473 前は `event E;` が
        // 後続 auto-property を止め、interface 形の `void M();` は後続 property を
        // method signature に飲み込んでいた。issue の最小再現に加え、逆順の
        // `property + event` も 1 つの fixture で固定する。Closes #473.
        var content = string.Join(
            "\n",
            "public class C { public event System.EventHandler E; public int P { get; set; } }",
            "public struct S { public int Q { get; set; } public event System.EventHandler F; }",
            "public interface I { void M(); int R { get; } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var e = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", e.ContainerKind);
        Assert.Equal("C", e.ContainerName);
        Assert.Equal("public event System.EventHandler E;", e.Signature);

        var p = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", p.ContainerKind);
        Assert.Equal("C", p.ContainerName);
        Assert.Equal("public int P { get; set; }", p.Signature);

        var q = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("struct", q.ContainerKind);
        Assert.Equal("S", q.ContainerName);
        Assert.Equal("public int Q { get; set; }", q.Signature);

        var f = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "F"));
        Assert.Equal("struct", f.ContainerKind);
        Assert.Equal("S", f.ContainerName);
        Assert.Equal("public event System.EventHandler F;", f.Signature);

        var m = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("interface", m.ContainerKind);
        Assert.Equal("I", m.ContainerName);
        Assert.Equal("void M();", m.Signature);

        var r = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "R"));
        Assert.Equal("interface", r.ContainerKind);
        Assert.Equal("I", r.ContainerName);
        Assert.Equal("int R { get; }", r.Signature);
    }

    [Fact]
    public void Extract_CSharp_BodylessMembersWithTrailingCommentsStopAtSemicolon()
    {
        // Semicolon-terminated C# members must stop at the semicolon even when a trailing
        // line or block comment follows it. Otherwise the range scan walks into the next
        // brace body and corrupts `end_line` / `body_end_line` for the last body-less member
        // in a block. Closes #245.
        // `;` で終わる C# の body-less member は、その後ろに行コメント / ブロックコメントが
        // 続いても semicolon で範囲を止める必要がある。そうしないと range scan が次の
        // brace body まで進み、block 内の最後の body-less member の `end_line` / `body_end_line`
        // が壊れる。Closes #245.
        var content = """
            namespace Demo;

            public interface IFoo
            {
                int A();                     // trailing line comment
                int B(int x); // another trailing line comment
                int C();                     // no real body here either
                int D(int y); /* trailing block comment */
            }

            public class X
            {
                public int DoIt() => 42;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var d = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "D"));
        Assert.Equal(8, d.StartLine);
        Assert.Equal(8, d.EndLine);
        Assert.Null(d.BodyStartLine);
        Assert.Null(d.BodyEndLine);

        var x = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "X"));
        Assert.Equal(11, x.StartLine);
        Assert.Equal(14, x.EndLine);
    }

    [Fact]
    public void Extract_CSharp_SameLineAccessorEventsStillExposeSiblingMembers()
    {
        // Accessor-based same-line events must clamp their signature at the accessor body
        // and still reopen earlier patterns for later siblings. Without that brace-end
        // clamp, `event E { add {} remove {} } public int P { get; set; }` stores the
        // event signature through the property and silently drops `P`. Also pin the
        // reverse `property + accessor event` order, including a generic event type with
        // internal whitespace, so both sibling directions remain visible. Closes #520.
        // 同一行の accessor event は accessor 本体の閉じ `}` で signature を切り、
        // 後続 sibling のために earlier pattern を再び開く必要がある。そうしないと
        // `event E { add {} remove {} } public int P { get; set; }` で event signature が
        // property まで飲み込み、`P` が無言で欠落する。逆順の `property + accessor event`
        // も、空白入り generic event 型を含めて固定し、両方向の sibling が可視なまま
        // であることを保証する。Closes #520.
        var content = string.Join(
            "\n",
            "public class C { public event System.EventHandler E { add {} remove {} } public int P { get; set; } }",
            "public struct S { public int Q { get; set; } public event System.Action<int, string> F { add {} remove {} } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var e = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("class", e.ContainerKind);
        Assert.Equal("C", e.ContainerName);
        Assert.Equal("public event System.EventHandler E { add {} remove {} }", e.Signature);

        var p = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", p.ContainerKind);
        Assert.Equal("C", p.ContainerName);
        Assert.Equal("public int P { get; set; }", p.Signature);

        var q = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("struct", q.ContainerKind);
        Assert.Equal("S", q.ContainerName);
        Assert.Equal("public int Q { get; set; }", q.Signature);

        var f = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "F"));
        Assert.Equal("struct", f.ContainerKind);
        Assert.Equal("S", f.ContainerName);
        Assert.Equal("public event System.Action<int, string> F { add {} remove {} }", f.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineGenericBraceBodiedMembersStillExposeLaterCompactSiblings()
    {
        // After the #525 raw-column brace clamp, same-line generic brace-bodied
        // members must translate their sibling-restart offset back into the
        // collapsed match-line column domain before reopening the pattern scan.
        // Otherwise `M<T1,           T2>() { }int P { get; }event ... E;`
        // restarts too far to the right, drops `P` / `E`, or lets `M` absorb the
        // following sibling text. Closes #533.
        // #525 の raw-column brace clamp 後は、same-line の generic brace-body member が
        // sibling scan を再開する位置を、pattern scan 再開前に collapsed match-line 側の
        // 列空間へ戻す必要がある。そうしないと
        // `M<T1,           T2>() { }int P { get; }event ... E;` で再開位置が右にずれ、
        // `P` / `E` が欠落するか、`M` の signature が後続 sibling を飲み込む。Closes #533.
        var content = "public interface I { void M<T1,           T2>() { }int P { get; }event System.Action<int,           string> E; }\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var m = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("interface", m.ContainerKind);
        Assert.Equal("I", m.ContainerName);
        Assert.Equal("void M<T1,           T2>() { }", m.Signature);

        var p = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("interface", p.ContainerKind);
        Assert.Equal("I", p.ContainerName);
        Assert.Equal("int P { get; }", p.Signature);

        var e = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("interface", e.ContainerKind);
        Assert.Equal("I", e.ContainerName);
        Assert.Equal("event System.Action<int,           string> E;", e.Signature);
    }

    [Fact]
    public void Extract_CSharp_GenericSameLineSemicolonMembersKeepTerminator()
    {
        // The same-line semicolon-boundary fix for #473 must translate collapsed generic
        // columns back to raw columns before slicing signatures, or spaces inside generic
        // arguments make the extracted signature stop one character early and silently drop
        // the terminating `;`. Lock event / interface-method / delegate shapes that all
        // route through the semicolon-body path. Closes #473 review follow-up.
        // #473 の same-line semicolon 境界 fix は、signature を切り出す前に collapsed
        // generic 列を raw 列へ戻す必要がある。そうしないと generic 引数内の空白のぶんだけ
        // signature が 1 文字短くなり、終端 `;` が無言で脱落する。semicolor-body 経路を
        // 通る event / interface method / delegate の 3 形を固定する。Closes #473 review
        // follow-up.
        var content = string.Join(
            "\n",
            "public class C { public event System.Action<int, string> E; public int P { get; set; } }",
            "public interface I { void M<T1, T2>(); int R { get; } }",
            "public class Holder { public delegate void Inner<T1, T2>(); public int Q { get; set; } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var e = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("public event System.Action<int, string> E;", e.Signature);
        Assert.Equal("class", e.ContainerKind);
        Assert.Equal("C", e.ContainerName);

        var m = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("void M<T1, T2>();", m.Signature);
        Assert.Equal("interface", m.ContainerKind);
        Assert.Equal("I", m.ContainerName);

        var inner = Assert.Single(symbols.Where(s => s.Kind == "delegate" && s.Name == "Inner"));
        Assert.Equal("public delegate void Inner<T1, T2>();", inner.Signature);
        Assert.Equal("class", inner.ContainerKind);
        Assert.Equal("Holder", inner.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_SameLineSemicolonMembersClampRangeAtTopLevelSemicolon()
    {
        // Body-less C# members (`void M();`, `event E;`, `delegate D();`) on the same
        // physical line as the enclosing type's closing `}` must clamp their range at
        // the in-line `;`. Before this fix, FindCSharpBraceRange only short-circuited
        // when the entire scan line ended with `;`, so a single-member interface
        // `interface I { void M(); }` and the property->method order
        // `interface J { int P { get; } void M(); }` both bled past the `}` into the
        // next file line, attributing the next type's brace range to M and emitting
        // wrong end_line / body_start_line / body_end_line. Closes #515.
        // 同じ物理行に外側型の閉じ `}` がある body-less な C# member
        // (`void M();`, `event E;`, `delegate D();`) は、行内 `;` の時点で範囲を確定
        // させなければならない。修正前の FindCSharpBraceRange は scan 行末が `;` で
        // 終わる場合だけ早期 return していたため、単一メンバー interface
        // `interface I { void M(); }` や property->method 並びの
        // `interface J { int P { get; } void M(); }` がいずれも `}` を越えてファイル
        // 次行に食い込み、次の型の brace 範囲を M に帰属させて end_line /
        // body_start_line / body_end_line を誤らせていた。Closes #515.
        var content = string.Join(
            "\n",
            "public interface I { void M(); }",
            "public interface J { int P { get; } void M(); }",
            "public interface K { void M(); int P { get; } }",
            "public class L { public int P { get; set; } public event System.EventHandler E; }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var solitaryMethod = Assert.Single(symbols.Where(s =>
            s.Kind == "function"
            && s.Name == "M"
            && s.ContainerKind == "interface"
            && s.ContainerName == "I"));
        Assert.Equal(1, solitaryMethod.Line);
        Assert.Equal(1, solitaryMethod.StartLine);
        Assert.Equal(1, solitaryMethod.EndLine);
        Assert.Null(solitaryMethod.BodyStartLine);
        Assert.Null(solitaryMethod.BodyEndLine);
        Assert.Equal("void M();", solitaryMethod.Signature);

        var afterPropertyMethod = Assert.Single(symbols.Where(s =>
            s.Kind == "function"
            && s.Name == "M"
            && s.ContainerKind == "interface"
            && s.ContainerName == "J"));
        Assert.Equal(2, afterPropertyMethod.Line);
        Assert.Equal(2, afterPropertyMethod.StartLine);
        Assert.Equal(2, afterPropertyMethod.EndLine);
        Assert.Null(afterPropertyMethod.BodyStartLine);
        Assert.Null(afterPropertyMethod.BodyEndLine);
        Assert.Equal("void M();", afterPropertyMethod.Signature);

        // Method-then-property order keeps existing behavior: M still has no body
        // metadata leak from the trailing property's brace range.
        // method-then-property 並びでも、後続 property の brace 範囲が M の body
        // メタデータに混入しないことを確認する。
        var beforePropertyMethod = Assert.Single(symbols.Where(s =>
            s.Kind == "function"
            && s.Name == "M"
            && s.ContainerKind == "interface"
            && s.ContainerName == "K"));
        Assert.Equal(3, beforePropertyMethod.Line);
        Assert.Equal(3, beforePropertyMethod.StartLine);
        Assert.Equal(3, beforePropertyMethod.EndLine);
        Assert.Null(beforePropertyMethod.BodyStartLine);
        Assert.Null(beforePropertyMethod.BodyEndLine);
        Assert.Equal("void M();", beforePropertyMethod.Signature);

        // Same fix also locks event range when a property accessor block precedes it
        // on the same line (the original #473 case is regressed via the same path).
        // 同じ修正は、property accessor block を先行させた event 並びでも range を
        // 固定する (#473 元ケースも同じ経路で reg している)。
        var trailingEvent = Assert.Single(symbols.Where(s =>
            s.Kind == "event"
            && s.Name == "E"
            && s.ContainerKind == "class"
            && s.ContainerName == "L"));
        Assert.Equal(4, trailingEvent.Line);
        Assert.Equal(4, trailingEvent.StartLine);
        Assert.Equal(4, trailingEvent.EndLine);
        Assert.Null(trailingEvent.BodyStartLine);
        Assert.Null(trailingEvent.BodyEndLine);
        Assert.Equal("public event System.EventHandler E;", trailingEvent.Signature);
    }

    [Fact]
    public void Extract_CSharp_GenericSameLineMembersKeepLaterBraceSiblingStartColumns()
    {
        // After earlier generic same-line members collapse whitespace in the per-line
        // C# match buffer, later brace-bodied siblings must still slice their signature
        // from the raw start column. Otherwise `int P { get; }` and `interface J { ... }`
        // keep the preceding `;` / `{` from the raw line even though the symbols
        // themselves are found. Pin both the direct property case and the nested
        // interface case reported in #525. Closes #525.
        // 先行する generic な same-line member によって C# の per-line match buffer 側で
        // 空白が潰れても、後続 brace-bodied sibling の signature は raw start 列から
        // 切り出されなければならない。そうでないと `int P { get; }` や
        // `interface J { ... }` の先頭に、raw 行上の直前 delimiter (`;` / `{`) が残る。
        // symbol 自体は見つかっていても signature が壊れるので、#525 の direct
        // property ケースと nested interface ケースの両方を固定する。Closes #525.
        var content = string.Join(
            "\n",
            "public interface I { void M<T1, T2>(); event System.Action<int, string> E; int P { get; } }",
            "public interface I2 { void M<T1, T2>(); event System.Action<int, string> E; interface J { int P { get; } } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var directProperty = Assert.Single(symbols.Where(s =>
            s.Kind == "property"
            && s.Name == "P"
            && s.ContainerKind == "interface"
            && s.ContainerName == "I"));
        Assert.Equal("int P { get; }", directProperty.Signature);

        var nestedInterface = Assert.Single(symbols.Where(s =>
            s.Kind == "interface"
            && s.Name == "J"
            && s.ContainerKind == "interface"
            && s.ContainerName == "I2"));
        Assert.Equal("interface J { int P { get; } }", nestedInterface.Signature);

        var nestedProperty = Assert.Single(symbols.Where(s =>
            s.Kind == "property"
            && s.Name == "P"
            && s.Line == 2
            && s.Signature == "int P { get; }"));
        Assert.Equal("int P { get; }", nestedProperty.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineNestedInterfacePropertyUsesInnermostContainer()
    {
        // Same-line nested interface members must stay attached to the nested interface,
        // even when earlier same-line siblings are longer and would otherwise reorder the
        // container walk by signature length. Before #529, `P` attached to outer `I2`
        // because `AssignContainers` processed same-line symbols out of source order and
        // popped `J` before reaching the later property. Closes #529.
        // 同一行の nested interface member は、先行 sibling の signature 長によって
        // same-line の処理順が崩れても、外側 `I2` ではなく内側 `J` に属し続ける必要が
        // ある。#529 前は `AssignContainers` が source order を失い、後続 property に
        // 到達する前に `J` を stack から外してしまうため `P` が `I2` に誤帰属していた。
        const string content = "public interface I2 { void M<T1, T2>(); event System.Action<int, string> E; interface J { int P { get; } } }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var nestedInterface = Assert.Single(symbols.Where(s => s.Kind == "interface" && s.Name == "J"));
        Assert.Equal("interface", nestedInterface.ContainerKind);
        Assert.Equal("I2", nestedInterface.ContainerName);

        var nestedProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("int P { get; }", nestedProperty.Signature);
        Assert.Equal("interface", nestedProperty.ContainerKind);
        Assert.Equal("J", nestedProperty.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_GenericBraceMembersRestartCollapsedSameLineSiblingsFromCollapsedColumns()
    {
        // Raw-column brace fixes for same-line generic members must not leak into the
        // sibling restart offset. The restart scan still runs on the collapsed C#
        // match line, so using the raw closing-brace column directly can jump into/past
        // a later compact sibling when generic whitespace was removed earlier in the
        // line. Pin the no-space compact chain where `M<T1,           T2>() { }int P`
        // used to lose both `P` and `E` and absorb `P` into `M`'s signature. Closes #533.
        // same-line generic member の raw-column brace fix は、sibling 再開位置まで
        // raw 列のまま漏れてはいけない。再開スキャン自体は collapsed な C# match 行
        // 上で動くため、閉じ brace の raw 列をそのまま使うと、generic 内で先に消えた
        // 空白ぶんだけ次の compact sibling の途中/後ろへ飛んでしまう。`M<T1, T2>() { }int P`
        // 形で `P` / `E` が落ち、`M` の signature が `P` を飲み込んでいた回帰を固定する。
        // Closes #533.
        const string content = "public interface I { void M<T1,           T2>() { }int P { get; }event System.Action<int,           string> E; }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("void M<T1,           T2>() { }", method.Signature);
        Assert.Equal("interface", method.ContainerKind);
        Assert.Equal("I", method.ContainerName);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("int P { get; }", property.Signature);
        Assert.Equal("interface", property.ContainerKind);
        Assert.Equal("I", property.ContainerName);

        var evt = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("event System.Action<int,           string> E;", evt.Signature);
        Assert.Equal("interface", evt.ContainerKind);
        Assert.Equal("I", evt.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedClassMembersStayAttachedToNestedContainer()
    {
        // The #525 wrapped-header signature fix must not evict a same-line nested type
        // from later container assignment. In the regression from #535, `Wrapped` kept
        // its corrected multi-line header signature, but the later property `P` attached
        // to the outer `Host` class after same-line siblings popped `Wrapped` out of the
        // active container stack too early. Pin the exact issue fixture so both the
        // signature fix and the nested-container attachment stay true together. Closes #535.
        // #525 の wrapped-header signature fix は、同一行にいる nested type を後続 member の
        // container 判定から追い出してはならない。#535 の回帰では `Wrapped` の multi-line
        // header 自体は正しくなった一方で、同一行 sibling を処理した時点で active container
        // stack から `Wrapped` が早く落ち、後続 property `P` が outer `Host` に付いていた。
        // issue の fixture そのものを固定し、signature 修正と nested-container 付与が同時に
        // 崩れないようにする。Closes #535.
        var content = string.Join(
            "\n",
            "namespace ReviewFixtures;",
            "",
            "public class Host",
            "{",
            "    public void M<T1, T2>() { } public event System.Action<int, string>? E; public class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public int P { get; }",
            "    }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrapped = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Wrapped"));
        Assert.Equal("public class Wrapped<T> where T : class", wrapped.Signature);
        Assert.Equal("class", wrapped.ContainerKind);
        Assert.Equal("Host", wrapped.ContainerName);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("public int P { get; }", property.Signature);
        Assert.Equal("class", property.ContainerKind);
        Assert.Equal("Wrapped", property.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedPartialTypesKeepRootToLeafFamilyKeys()
    {
        // The #535 container-path fix changes `BuildSelfFamilyKey` to receive a root-to-leaf
        // effective container path. If the old `Reverse()` is left in place, nested partial
        // types flip their family key order (`Host.ReviewFixtures.Wrapped`) and no longer
        // match the container-qualified-name contract used by hotspot-family grouping.
        // Pin a wrapped nested partial-type fixture so both the container path and family key
        // stay in canonical root-to-leaf order. Closes #541.
        // #535 の container-path fix 以降、`BuildSelfFamilyKey` は root-to-leaf 順の
        // effective container path を受け取る。ここで旧 `Reverse()` が残ると、nested partial
        // type の family key が `Host.ReviewFixtures.Wrapped` のように逆順化し、
        // hotspot-family grouping が依存する container-qualified-name 契約と食い違う。
        // wrapped な nested partial-type の fixture を固定し、container path と family key の
        // 両方が canonical な root-to-leaf 順を保つことを検証する。Closes #541.
        var content = string.Join(
            "\n",
            "namespace ReviewFixtures;",
            "",
            "public class Host",
            "{",
            "    public void M<T1, T2>() { } public event System.Action<int, string>? E; public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public partial class Child",
            "        {",
            "        }",
            "    }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrapped = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Wrapped"));
        Assert.Equal("ReviewFixtures.Host", wrapped.ContainerQualifiedName);
        Assert.Equal("ReviewFixtures.Host.Wrapped", wrapped.FamilyKey);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("ReviewFixtures.Host.Wrapped", child.ContainerQualifiedName);
        Assert.Equal("ReviewFixtures.Host.Wrapped", child.FamilyKey);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeClosingBraceLineStillFindsOuterSibling()
    {
        // Wrapped multi-line nested types can end on a line that also starts a later outer
        // sibling (`} public int Q { get; }`). The line-level C# scan must restart from the
        // post-brace statement boundary instead of treating the leading `}` as a dead line,
        // or the outer sibling silently disappears from symbols/definition/outline. Closes #545.
        // 折り返された multi-line nested type は、閉じ brace 行に outer sibling が続く
        // (`} public int Q { get; }`) 形を取りうる。C# の行単位 scan は先頭 `}` の後ろにある
        // statement 境界から再開しなければならず、そうしないと outer sibling が
        // symbols/definition/outline から無言で脱落する。Closes #545.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public int P { get; }",
            "    } public int Q { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrapped = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Wrapped"));
        Assert.Equal("class", wrapped.ContainerKind);
        Assert.Equal("Host", wrapped.ContainerName);

        var innerProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", innerProperty.ContainerKind);
        Assert.Equal("Wrapped", innerProperty.ContainerName);

        var outerProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("public int Q { get; }", outerProperty.Signature);
        Assert.Equal("class", outerProperty.ContainerKind);
        Assert.Equal("Host", outerProperty.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeClosingBraceLineKeepsLastInnerMemberAndOuterSibling()
    {
        // A wrapped nested type can end on the same line as both its last inner member and
        // a later outer sibling (`public int P { get; } } public int Q { get; }`). The
        // closing-line body clamp from #545 must still keep `P` inside `Wrapped`, while the
        // same-line restart skips the intervening `}` and still reaches outer sibling `Q`.
        // Closes #549.
        // wrapped な nested type は、最後の inner member と後続 outer sibling が同じ閉じ
        // brace 行 (`public int P { get; } } public int Q { get; }`) に載ることがある。
        // #545 の closing-line body clamp は `P` を `Wrapped` の内側に残しつつ、same-line
        // restart は間の `}` を飛ばして outer sibling `Q` に到達しなければならない。
        // Closes #549.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public int P { get; } } public int Q { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var innerProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", innerProperty.ContainerKind);
        Assert.Equal("Wrapped", innerProperty.ContainerName);

        var outerProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("public int Q { get; }", outerProperty.Signature);
        Assert.Equal("class", outerProperty.ContainerKind);
        Assert.Equal("Host", outerProperty.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeBraceBodiedLastInnerMemberKeepsOuterPropertySibling()
    {
        // A compact brace-bodied inner type can sit on the wrapped type's closing-brace line
        // before an outer property sibling (`Child { } } public int Q { get; }`). The
        // type-header false-positive recovery must skip the intermediate `}` and still
        // reach `Q`, while keeping `Child` attached to `Wrapped`. Closes #554.
        // compact な brace-bodied inner type は、wrapped type の closing-brace 行で outer
        // property sibling (`Child { } } public int Q { get; }`) の直前に現れうる。type
        // header 偽陽性からの same-line 再開は中間の `}` を飛ばして `Q` まで届きつつ、
        // `Child` を `Wrapped` 配下に残さなければならない。Closes #554.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public partial class Child { } } public int Q { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("class", child.ContainerKind);
        Assert.Equal("Wrapped", child.ContainerName);

        var outerProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("public int Q { get; }", outerProperty.Signature);
        Assert.Equal("class", outerProperty.ContainerKind);
        Assert.Equal("Host", outerProperty.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeBraceBodiedLastInnerMemberKeepsOuterTypeSibling()
    {
        // The same compact `Child { } }` shape must also keep a later outer class sibling
        // visible. Otherwise the type-header recovery restarts on the closing `}` and drops
        // `Sibling` from symbol-oriented queries. Closes #554.
        // 同じ compact な `Child { } }` 形では、後続の outer class sibling も見え続けなければ
        // ならない。そうでないと type-header 回復が closing `}` から再開して `Sibling` を
        // symbol 系クエリから落としてしまう。Closes #554.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public partial class Child { } } public partial class Sibling",
            "        {",
            "        }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("class", child.ContainerKind);
        Assert.Equal("Wrapped", child.ContainerName);

        var sibling = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Sibling"));
        Assert.Equal("class", sibling.ContainerKind);
        Assert.Equal("Host", sibling.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_EmptySameLineNestedInterfaceBodyKeepsLaterOuterClassSibling()
    {
        // An empty same-line nested interface body must not consume a later outer class
        // sibling of a different kind. The #585 repro previously emitted `Outer` + `I`
        // but dropped `Sibling` after the interface's compact `{ }` body. Closes #585.
        // 空の same-line nested interface body は、kind が異なる後続 outer class sibling を
        // 飲み込んではならない。#585 の repro では、以前は interface の compact な `{ }`
        // 本体の後で `Outer` と `I` だけが出力され、`Sibling` が落ちていた。Closes #585.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "[A]",
            "public class Outer { public interface I<T1,           T2> { } public class Sibling { } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var outer = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Outer"));
        Assert.Equal("namespace", outer.ContainerKind);
        Assert.Equal("Demo", outer.ContainerName);

        var nestedInterface = Assert.Single(symbols.Where(s => s.Kind == "interface" && s.Name == "I"));
        Assert.Equal("class", nestedInterface.ContainerKind);
        Assert.Equal("Outer", nestedInterface.ContainerName);

        var sibling = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Sibling"));
        Assert.Equal("class", sibling.ContainerKind);
        Assert.Equal("Outer", sibling.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_WrappedEmptySameLineNestedTypeBodyKeepsLaterOuterPropertySibling()
    {
        // A wrapped nested type whose last inner member is `Child { } }` must still leave a
        // later outer property visible on that same closing-brace line. The #585 repro
        // previously kept `Child` but dropped `P` from the outer `Host`. Closes #585.
        // wrapped nested type の最後の inner member が `Child { } }` でも、同じ closing-brace
        // 行に続く outer property は見え続けなければならない。#585 の repro では、以前は
        // `Child` は残る一方で outer `Host` の `P` が落ちていた。Closes #585.
        var content = string.Join(
            "\n",
            "public class Host",
            "{",
            "    public class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public class Child { } } public int P { get; set; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("class", child.ContainerKind);
        Assert.Equal("Wrapped", child.ContainerName);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", property.ContainerKind);
        Assert.Equal("Host", property.ContainerName);
        Assert.Equal("public int P { get; set; }", property.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeKeepsSameLineDuplicateSignatureSiblings()
    {
        // Duplicate suppression must not collapse distinct same-line siblings just because
        // they share the same short signature before container assignment runs. In the
        // compact wrapped case below, both `Child` declarations are real: one inside
        // `Wrapped`, one later under `Host`. Closes #552.
        // duplicate suppression は、container 判定前に短い signature が一致するだけで
        // 別物の same-line sibling を潰してはならない。下の compact な wrapped case では
        // `Child` 宣言が 2 つとも実在し、片方は `Wrapped` 配下、もう片方は後続の `Host`
        // 配下である。Closes #552.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public partial class Child { } } public partial class Child { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var children = symbols
            .Where(s => s.Kind == "class" && s.Name == "Child")
            .OrderBy(s => s.ContainerName)
            .ToList();
        Assert.Equal(2, children.Count);

        Assert.Contains(children, child => child.ContainerKind == "class" && child.ContainerName == "Wrapped");
        Assert.Contains(children, child => child.ContainerKind == "class" && child.ContainerName == "Host");
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeIgnoresStringLiteralLookalikesWhenTrackingSameLineOccurrences()
    {
        // Same-line occurrence tracking must ignore string-literal lookalikes of the same
        // declaration signature. Otherwise the later real declaration is mapped onto the
        // quoted copy and the outer sibling misattaches under the inner `Child`. Closes #558.
        // same-line occurrence tracking は、同じ宣言 signature を含む文字列リテラルを
        // 数えてはいけない。数えてしまうと後続の本物の宣言が quoted copy に対応付けられ、
        // outer sibling が inner `Child` 配下へ誤帰属する。Closes #558.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public const string Marker = \"public partial class Child { }\"; public partial class Child { } } public partial class Child { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var children = symbols
            .Where(s => s.Kind == "class" && s.Name == "Child")
            .OrderBy(s => s.ContainerName)
            .ToList();
        Assert.Equal(2, children.Count);

        Assert.Contains(children, child => child.ContainerKind == "class" && child.ContainerName == "Wrapped");
        Assert.Contains(children, child => child.ContainerKind == "class" && child.ContainerName == "Host");
    }

    [Fact]
    public void Extract_CSharp_WrappedNestedTypeIgnoresMultilineCommentLookalikesWhenTrackingSameLineOccurrences()
    {
        // Same-line occurrence recovery must also respect block-comment state carried from
        // earlier lines. Otherwise a declaration-shaped lookalike inside a multiline comment
        // consumes the first occurrence slot and the later outer sibling is misattached under
        // the inner wrapped type. Closes #567.
        // same-line occurrence 復元は、前行から継続する block comment state も尊重しなければ
        // ならない。そうしないと複数行コメント中の宣言風 lookalike が最初の occurrence slot を
        // 奪い、後続 outer sibling が inner wrapped type 配下へ誤帰属する。Closes #567.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        /*",
            "        public partial class Child { } */ public partial class Child { } } public partial class Child { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var children = symbols
            .Where(s => s.Kind == "class" && s.Name == "Child")
            .OrderBy(s => s.ContainerName)
            .ToList();
        Assert.Equal(2, children.Count);

        Assert.Contains(children, child => child.ContainerKind == "class" && child.ContainerName == "Wrapped");
        Assert.Contains(children, child => child.ContainerKind == "class" && child.ContainerName == "Host");
    }

    [Fact]
    public void Extract_CSharp_CarriedVerbatimStringContinuationStillFindsLaterSameLineNestedAndOuterTypes()
    {
        // When a physical line begins inside a carried verbatim string, the closing `";`
        // leaves a top-level semicolon before the real declaration stream. The same-line
        // C# restart must skip that empty statement and still reach the later real types.
        // Closes #630 / #633.
        // 継続中の verbatim string から始まる物理行では、閉じ `";` の直後に top-level の
        // 空文 `;` が残る。same-line の C# 再開はその空文を飛ばし、後続の実型宣言まで
        // 到達しなければならない。Closes #630 / #633.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        private string _s = @\"",
            "        public partial class Fake { }\"; public partial class Child { } } public partial class OuterChild { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Fake");
        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("class", child.ContainerKind);
        Assert.Equal("Wrapped", child.ContainerName);
        Assert.Equal("public partial class Child { }", child.Signature);

        var outerChild = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "OuterChild"));
        Assert.Equal("class", outerChild.ContainerKind);
        Assert.Equal("Host", outerChild.ContainerName);
        Assert.Equal("public partial class OuterChild { }", outerChild.Signature);
    }

    [Fact]
    public void Extract_CSharp_CarriedRawStringContinuationStillFindsLaterSameLineNestedAndOuterTypes()
    {
        // Raw-string continuation lines have the same top-level `;` restart hazard as
        // verbatim strings. The fake declaration inside the string must stay suppressed
        // while the later real nested and outer siblings still extract. Closes #630 / #633.
        // raw string の継続行も、verbatim string と同じく top-level の `;` 再開ハザードを持つ。
        // 文字列内の fake 宣言は抑止したまま、後続の実 nested / outer sibling を抽出する必要がある。
        // Closes #630 / #633.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        private string _s = \"\"\"",
            "        public partial class Fake { }\"\"\"; public partial class Child { } } public partial class OuterChild { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Fake");
        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("class", child.ContainerKind);
        Assert.Equal("Wrapped", child.ContainerName);
        Assert.Equal("public partial class Child { }", child.Signature);

        var outerChild = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "OuterChild"));
        Assert.Equal("class", outerChild.ContainerKind);
        Assert.Equal("Host", outerChild.ContainerName);
        Assert.Equal("public partial class OuterChild { }", outerChild.Signature);
    }

    [Fact]
    public void Extract_CSharp_CarriedVerbatimStringContinuationPreservesSameLinePropertySiblings()
    {
        // A carried verbatim-string close line must clamp the first same-line brace-bodied
        // property at its real `}` so later property siblings on the same physical line
        // still restart and extract independently. Closes #636.
        // 継続中の verbatim string の close line では、同一物理行上の最初の brace-body
        // property を本物の `}` で切り、後続 property sibling が再開して独立抽出される
        // 必要がある。Closes #636.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C",
            "{",
            "    private string _s = @\"",
            "    fake\"; public int P { get; } public int Q { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var propertyP = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("public int P { get; }", propertyP.Signature);
        Assert.Equal("C", propertyP.ContainerName);

        var propertyQ = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("public int Q { get; }", propertyQ.Signature);
        Assert.Equal("C", propertyQ.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_CarriedRawStringContinuationPreservesSameLinePropertySiblings()
    {
        // Raw-string continuation lines share the same brace-end clamp requirement as
        // verbatim strings: the first same-line property must stop at its own accessor
        // block so later property siblings remain visible. Closes #636.
        // raw string の継続行も、verbatim string と同じ brace-end clamp を必要とする。
        // 先頭 property は自分の accessor block で止まり、後続 property sibling が
        // 見えるままでなければならない。Closes #636.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C",
            "{",
            "    private string _s = \"\"\"",
            "    fake\"\"\"; public int P { get; } public int Q { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var propertyP = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("public int P { get; }", propertyP.Signature);
        Assert.Equal("C", propertyP.ContainerName);

        var propertyQ = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("public int Q { get; }", propertyQ.Signature);
        Assert.Equal("C", propertyQ.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_CarriedVerbatimStringContinuationPreservesSameLineMethodSiblings()
    {
        // Carried verbatim-string close lines must also clamp same-line brace-bodied
        // methods. Otherwise the first method absorbs the rest of the line and later
        // siblings disappear. Closes #636.
        // 継続中の verbatim string の close line では、同一行 brace-body method も
        // 正しく切り出す必要がある。そうしないと先頭 method が残り全体を飲み込み、
        // 後続 sibling が消える。Closes #636.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C",
            "{",
            "    private string _s = @\"",
            "    fake\"; public void M() { } public void N() { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var methodM = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("public void M() { }", methodM.Signature);
        Assert.Equal("C", methodM.ContainerName);

        var methodN = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "N"));
        Assert.Equal("public void N() { }", methodN.Signature);
        Assert.Equal("C", methodN.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_CarriedRawStringContinuationPreservesSameLineMethodSiblings()
    {
        // Raw-string continuation lines need the same method-body clamp: both methods on
        // the close line must survive with their own signatures instead of collapsing into
        // one oversized match. Closes #636.
        // raw string の継続行でも method-body clamp は同じく必要であり、close line 上の
        // 2 つの method は 1 つの過大 signature に潰れず、それぞれ独立して残る
        // 必要がある。Closes #636.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C",
            "{",
            "    private string _s = \"\"\"",
            "    fake\"\"\"; public void M() { } public void N() { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var methodM = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("public void M() { }", methodM.Signature);
        Assert.Equal("C", methodM.ContainerName);

        var methodN = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "N"));
        Assert.Equal("public void N() { }", methodN.Signature);
        Assert.Equal("C", methodN.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_CarriedVerbatimStringContinuationWithSameLineAccessorEventStillFindsLaterTypes()
    {
        // Carried verbatim close-lines can now restart across the top-level `";`, but
        // same-line accessor-event sibling recovery must also use the carried lexical
        // state or the later nested/outer class declarations still disappear.
        // Closes #630 / #633 follow-up.
        // 継続 verbatim string の close-line では top-level の `";` を跨いで再開できても、
        // same-line accessor event の sibling 回復も carried lexical state を使わないと
        // 後続の nested / outer class 宣言がまだ消えてしまう。Closes #630 / #633 follow-up.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        private string _s = @\"",
            "        public partial class Fake { }\"; public event System.Action E { add { } remove { } } public partial class Child { } } public partial class OuterChild { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Fake");

        var evt = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("Wrapped", evt.ContainerName);
        Assert.Equal("public event System.Action E { add { } remove { } }", evt.Signature);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("Wrapped", child.ContainerName);
        Assert.Equal("public partial class Child { }", child.Signature);

        var outerChild = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "OuterChild"));
        Assert.Equal("Host", outerChild.ContainerName);
        Assert.Equal("public partial class OuterChild { }", outerChild.Signature);
    }

    [Fact]
    public void Extract_CSharp_CarriedRawStringContinuationWithSameLineAccessorEventStillFindsLaterTypes()
    {
        // Raw-string close-lines should keep the same event-sibling restart contract as the
        // verbatim path: same-line accessor events must not block later nested/outer types.
        // Closes #630 / #633 follow-up.
        // raw string の close-line でも verbatim と同じ event-sibling 再開契約を保ち、
        // same-line accessor event が後続の nested / outer type を塞がないことを確認する。
        // Closes #630 / #633 follow-up.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        private string _s = \"\"\"",
            "        public partial class Fake { }\"\"\"; public event System.Action E { add { } remove { } } public partial class Child { } } public partial class OuterChild { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Fake");

        var evt = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("Wrapped", evt.ContainerName);
        Assert.Equal("public event System.Action E { add { } remove { } }", evt.Signature);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("Wrapped", child.ContainerName);
        Assert.Equal("public partial class Child { }", child.Signature);

        var outerChild = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "OuterChild"));
        Assert.Equal("Host", outerChild.ContainerName);
        Assert.Equal("public partial class OuterChild { }", outerChild.Signature);
    }

    [Fact]
    public void Extract_CSharp_InlineBlockCommentBeforeSameLineClassDoesNotPolluteSignature()
    {
        // Inline block comments that end immediately before a real same-line declaration must
        // not leak into the emitted signature text. The real symbol should keep only the
        // canonical declaration slice. Closes #578.
        // same-line の実宣言直前で閉じる inline block comment は、出力 signature に
        // 混入してはならない。実在 symbol には正規の宣言部分だけを残す。Closes #578.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        /* public partial class Child { } */ public partial class Child { }",
            "    }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal("Wrapped", child.ContainerName);
        Assert.Equal("public partial class Child { }", child.Signature);
    }

    [Fact]
    public void Extract_CSharp_InlineBlockCommentBeforeSameLinePropertyDoesNotPolluteSignature()
    {
        // Property signatures must also clamp from the declaration token after an inline
        // block comment, not from the comment prefix. Closes #578.
        // property の signature も inline block comment の先頭ではなく、その後ろの
        // 実宣言トークンから切り出さなければならない。Closes #578.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C",
            "{",
            "    /* public int P { get; } */ public int P { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("C", property.ContainerName);
        Assert.Equal("public int P { get; }", property.Signature);
    }

    [Fact]
    public void Extract_CSharp_InlineBlockCommentBeforeSameLineMethodDoesNotPolluteSignature()
    {
        // Method signatures must ignore inline block-comment lookalikes that appear on the
        // same physical line before the real declaration. Closes #578.
        // method の signature も、同一物理行で実宣言より前にある inline block comment
        // 由来の見かけ上の宣言を無視しなければならない。Closes #578.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class C",
            "{",
            "    /* public void M() { } */ public void M() { }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("C", method.ContainerName);
        Assert.Equal("public void M() { }", method.Signature);
    }

    [Fact]
    public void Extract_CSharp_MultilineCommentCloseLineDoesNotEmitPhantomSameLineClass()
    {
        // A multiline block comment that closes immediately before a real same-line class
        // declaration must not leak comment-body lookalikes or `*/`-prefixed phantom rows
        // into symbol extraction. The current branch regression around wrapped same-line
        // occurrence recovery started emitting an extra `Child` row here. Closes #571.
        // 複数行 block comment の閉じ `*/` 直後に実在する same-line class 宣言が続く場合でも、
        // コメント本文の lookalike や `*/` 付き phantom 行を symbol 抽出へ漏らしてはならない。
        // wrapped same-line occurrence 修正の枝では、この fixture で余計な `Child` 行が
        // 追加で出る回帰が入っていた。Closes #571.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        /*",
            "        public partial class Fake { } */ public partial class Child { } }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var children = symbols
            .Where(s => s.Kind == "class" && s.Name == "Child")
            .ToList();
        var child = Assert.Single(children);
        Assert.Equal("public partial class Child { }", child.Signature);
        Assert.Equal("class", child.ContainerKind);
        Assert.Equal("Wrapped", child.ContainerName);
        Assert.Equal(0, child.SameLineSignatureOccurrenceIndex);

        Assert.DoesNotContain(symbols, s =>
            s.Kind == "class"
            && s.Signature != null
            && s.Signature.Contains("Fake", StringComparison.Ordinal));
        Assert.DoesNotContain(symbols, s =>
            s.Kind == "class"
            && s.Signature != null
            && s.Signature.StartsWith("*/", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CSharp_ClosingBraceLineKeepsInnerMemberAfterInnerMethodClosesSameLine()
    {
        // Wrapped closing-brace-line recovery must carry forward the unmatched brace depth
        // that is already open at the start of the end line. Otherwise the first `}` on the
        // line is mistaken for the wrapped type's close and later inner members fall out to
        // the outer type. Closes #575.
        // wrapped closing-brace-line の復元では、end line 開始時点ですでに開いている
        // unmatched brace depth を引き継がなければならない。そうしないと行頭側の
        // 最初の `}` を wrapped type 自身の閉じ括弧と誤認し、後続 inner member が
        // outer type 側へこぼれる。Closes #575.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public partial class Host",
            "{",
            "    public partial class Wrapped<T>",
            "        where T : class",
            "    {",
            "        public void M()",
            "        {",
            "        } public int P { get; } } public int Q { get; }",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrappedProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("class", wrappedProperty.ContainerKind);
        Assert.Equal("Wrapped", wrappedProperty.ContainerName);

        var hostProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Q"));
        Assert.Equal("class", hostProperty.ContainerKind);
        Assert.Equal("Host", hostProperty.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_SameLineMultipleFieldsAreAllCaptured()
    {
        // `public class Multi { public int A; public int B; public int C; }` must
        // produce three `property` symbols (A, B, C) plus the outer `Multi` class, with
        // clean signatures that stop at the field terminator rather than trailing into
        // the enclosing `} }`. Closes #400.
        // `public class Multi { public int A; public int B; public int C; }` は外側 class
        // Multi と A / B / C の 3 つの property を生成し、signature は末尾の `} }` を
        // 含まずにフィールド終端（`;`）で切り詰められていなければならない。Closes #400.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class Multi { public int A; public int B; public int C; }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Multi");
        foreach (var name in new[] { "A", "B", "C" })
        {
            var field = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == name));
            Assert.Equal("class", field.ContainerKind);
            Assert.Equal("Multi", field.ContainerName);
            Assert.Equal($"public int {name};", field.Signature);
            Assert.DoesNotContain("}", field.Signature);
        }
    }











    [Fact]
    public void Extract_VB_DetectsSubFunctionClassModule()
    {
        // VB.NET: Sub, Function, Class, Module, Imports / VB.NET: サブ、関数、クラス、モジュール、Imports
        var content = "Imports System.IO\n\nPublic Class UserService\n    Public Sub Save(user As User)\n    End Sub\n\n    Private Function Validate(user As User) As Boolean\n        Return True\n    End Function\nEnd Class";
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("System.IO"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Save");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Validate");
    }

    [Fact]
    public void Extract_VB_DetectsDelegateDeclarations()
    {
        // VB.NET Delegate declarations should be searchable as delegate symbols / VB.NET の
        // Delegate 宣言は delegate シンボルとして検索できる必要がある。
        var content = """
            Public Delegate Sub ProgressHandler(value As Integer)
            Friend Delegate Function Formatter(input As String) As String
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "ProgressHandler");
        var formatter = Assert.Single(symbols, s => s.Kind == "delegate" && s.Name == "Formatter");
        Assert.Equal("Friend", formatter.Visibility);
    }

    [Fact]
    public void Extract_VB_DetectsOperatorDeclarations()
    {
        var content = """
            Namespace MyApp

            Public Class Money
                Public Shared Operator +(left As Money, right As Money) As Money
                    Return New Money()
                End Operator

                Public Shared Widening Operator CType(value As Money) As Decimal
                    Return 0D
                End Operator
            End Class
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var add = Assert.Single(symbols, s => s.Kind == "operator" && s.Name == "Operator +");
        Assert.Equal(4, add.StartLine);
        Assert.Equal(5, add.BodyStartLine);
        Assert.Equal(6, add.BodyEndLine);
        Assert.Equal(6, add.EndLine);
        Assert.Equal("class", add.ContainerKind);
        Assert.Equal("Money", add.ContainerName);

        var conversion = Assert.Single(symbols, s => s.Kind == "operator" && s.Name == "Operator CType");
        Assert.Equal(8, conversion.StartLine);
        Assert.Equal(9, conversion.BodyStartLine);
        Assert.Equal(10, conversion.BodyEndLine);
        Assert.Equal(10, conversion.EndLine);
        Assert.Equal("class", conversion.ContainerKind);
        Assert.Equal("Money", conversion.ContainerName);
    }

    [Fact]
    public void Extract_VB_DetectsDeclareFunctionDeclarations()
    {
        var content = """
            Public Class NativeMethods
                Private Declare Unicode Function GetWindowText Lib "user32" Alias "GetWindowTextW" () As Integer
                Public Declare PtrSafe Function GetTickCount Lib "kernel32" () As Long
                Friend Declare Auto Sub SendMessage Lib "user32" ()
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var function = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "GetWindowText");
        Assert.Equal("Private", function.Visibility);

        var ptrSafe = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "GetTickCount");
        Assert.Equal("Public", ptrSafe.Visibility);

        var sub = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "SendMessage");
        Assert.Equal("Friend", sub.Visibility);
    }

    [Fact]
    public void Extract_VB_DetectsConstMembersAsProperties()
    {
        var content = """
            Public Class Limits
                Public Const MaxItems As Integer = 10
                Private Shared Const CacheKey As String = "items"
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var maxItems = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "MaxItems");
        Assert.Equal("Public", maxItems.Visibility);

        var cacheKey = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "CacheKey");
        Assert.Equal("Private", cacheKey.Visibility);
    }

    [Fact]
    public void Extract_VB_DetectsVisibleFieldsAsProperties()
    {
        var content = """
            Public Class State
                Private ReadOnly repo As Repository
                Public Shared Count As Integer

                Public Sub New()
                    Dim localValue As Integer
                End Sub
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var repo = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "repo");
        Assert.Equal("Private", repo.Visibility);

        var count = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "Count");
        Assert.Equal("Public", count.Visibility);

        Assert.DoesNotContain(symbols, s => s.Name == "localValue");
    }

    [Fact]
    public void Extract_VB_DetectsNamespaceAndImplicitVisibilityDeclarations()
    {
        var content = """
            Imports System

            Namespace MyApp
                Public Class Foo
                    Sub Bar()
                    End Sub

                    Function Quux() As Integer
                        Return 1
                    End Function
                End Class

                Class Helper
                End Class

                Public Module Utils
                    Sub Log()
                    End Sub
                End Module
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var ns = Assert.Single(symbols.Where(s => s.Kind == "namespace" && s.Name == "MyApp"));
        Assert.Equal(3, ns.StartLine);
        Assert.Equal(20, ns.EndLine);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal("namespace", foo.ContainerKind);
        Assert.Equal("MyApp", foo.ContainerName);

        var helper = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Helper"));
        Assert.Equal("namespace", helper.ContainerKind);
        Assert.Equal("MyApp", helper.ContainerName);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Bar");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Quux");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Log");
    }

    [Fact]
    public void Extract_VB_DetectsEscapedNamespaceSegments()
    {
        var content = """
            Namespace [My].App
                Public Class Widget
                End Class
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        var ns = Assert.Single(symbols.Where(s => s.Kind == "namespace" && s.Name == "My.App"));
        Assert.Equal(1, ns.StartLine);
        Assert.DoesNotContain(symbols, s => s.Kind == "namespace" && s.Name == "[My].App");
    }

    [Fact]
    public void Extract_VB_DetectsImportAliasSymbols()
    {
        var content = """
            Imports CustomerAlias = App.Domain.Customer
            Imports [Select] = App.Domain.Selector

            Public Class Controller
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "CustomerAlias");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Select");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "CustomerAlias = App.Domain.Customer");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "[Select]");
    }

    [Fact]
    public void Extract_VB_DetectsXmlNamespaceImportPrefixes()
    {
        var content = """
            Imports <xmlns:ui="urn:ui">
            Imports <xmlns:ui-kit="urn:ui-kit">

            Public Class ViewModel
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ui");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ui-kit");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.Contains("xmlns", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_VB_DetectsEscapedTypeDeclarationNames()
    {
        var content = """
            Public Class [Class]
            End Class

            Public Interface [Interface]
            End Interface

            Public Structure [Structure]
            End Structure

            Public Enum [Enum]
                One
            End Enum
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Class");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Interface");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Structure");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Enum");
        Assert.DoesNotContain(symbols, s => s.Name == "[Class]");
    }

    [Fact]
    public void Extract_VB_DetectsEscapedMemberDeclarationNames()
    {
        var content = """
            Public Class EscapedMembers
                Public Delegate Sub [Delegate]()
                Public Sub [Select]()
                End Sub

                Public Property [Property] As Integer
                Public Event [Event] As EventHandler
                Public Const [Const] As Integer = 1
                Private [Dim] As Integer
            End Class
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Delegate");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Select");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Property");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Event");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Const");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Dim");
        Assert.DoesNotContain(symbols, s => s.Name == "[Select]");
    }

    [Fact]
    public void Extract_VB_DetectsLeadingModifiersAndMemberKindsWithoutVisibility()
    {
        var content = """
            Namespace MyApp
                Partial Class Form1
                End Class

                Public Class Widget
                    Partial Private Sub OnReady()
                    End Sub

                    Shared Sub Log()
                    End Sub

                    Overrides Function ToString() As String
                        Return ""
                    End Function

                    Public Iterator Function Values() As IEnumerable(Of Integer)
                        Yield 1
                    End Function

                    Property Count As Integer

                    Event Changed As EventHandler
                End Class
            End Namespace
            """;
        var symbols = SymbolExtractor.Extract(1, "vb", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Form1");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnReady");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Log");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ToString");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Values");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Count");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Changed");
    }

    [Fact]
    public void Extract_Haskell_DetectsIndentedAndLiterateSignatures()
    {
        // Indented where-clause signature and literate Haskell '>' prefix
        // インデントされたwhere節のシグネチャとliterate Haskellの'>'接頭辞
        var content = "  helper :: Int -> Int\n  helper x = x + 1\n> main :: IO ()\n> main = putStrLn \"hello\"";
        var symbols = SymbolExtractor.Extract(1, "haskell", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main");
    }

    [Fact]
    public void Extract_R_DoesNotMatchOrdinaryAssignment()
    {
        // Ordinary assignment should not be detected as a function
        // 通常の代入は関数として検出されないこと
        var content = "x <- 42\ny <- some_func(x)\nz <- list(1, 2, 3)\nglobal <<- 42";
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function");
    }

    [Fact]
    public void Extract_R_DetectsQuotedLibraryAndRequireNamespaceImports()
    {
        // R: quoted package loads and requireNamespace should both index as imports.
        // R: 引用付き package load と requireNamespace も import として索引する。
        var content = """
            library("ggplot2")
            requireNamespace("jsonlite", quietly = TRUE)
            require(dplyr)
            library(package = "stringr")
            require(package = tidyr)
            base::library("readr")
            base::requireNamespace(package = "rlang")
            library(help = "stats")
            base::require(help = utils)
            pacman::p_load(lubridate, "data.table", janitor, character.only = FALSE) # , fakepkg
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ggplot2");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "jsonlite");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "dplyr");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "stringr");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "tidyr");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "readr");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "rlang");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "stats");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "utils");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "lubridate");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "data.table");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "janitor");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "fakepkg");
    }

    [Fact]
    public void Extract_R_DetectsSourceFileImports()
    {
        // R: source() loads another R file and should be searchable as an import.
        // R: source() は別の R ファイルを読み込むため import として検索可能にする。
        const string content = """
            source("R/helpers.R")
            source(file = "R/models/fit.R", local = TRUE)
            sys.source("R/bootstrap.R", envir = environment())
            base::source("R/base_helpers.R")
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "R/helpers.R");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "R/models/fit.R");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "R/bootstrap.R");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "R/base_helpers.R");
    }

    [Fact]
    public void Extract_R_DetectsTestthatTestCases()
    {
        // R testthat cases are useful search units in package test suites.
        // R の testthat case は package test suite で有用な検索単位。
        const string content = """
            test_that("filters missing rows", {
              expect_equal(drop_missing(data), expected)
            })

            testthat::test_that("plots model output", {
              expect_s3_class(plot_model(model), "ggplot")
            })
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "filters missing rows");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plots model output");
    }

    [Fact]
    public void Extract_R_DetectsTestthatBddBlocks()
    {
        // testthat also exposes describe()/it() BDD-style test blocks.
        // testthat には describe()/it() の BDD 形式 test block もある。
        const string content = """
            describe("model plotting", {
              it("renders residuals", {
                expect_s3_class(plot_residuals(model), "ggplot")
              })
            })

            testthat::it("handles missing values", {
              expect_true(all(is.na(clean(data))))
            })
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "model plotting");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "renders residuals");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "handles missing values");
    }

    [Fact]
    public void Extract_R_DetectsShinyOutputRenderSymbols()
    {
        // Shiny output renderers define user-visible server endpoints.
        // Shiny output renderer はユーザーに見える server endpoint を定義する。
        const string content = """
            output$summary_plot <- renderPlot({
              plot(data)
            })

            output$table <- renderTable({
              data
            })

            output[["detail-plot"]] <- renderPlot({
              plot(detail)
            })
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "summary_plot");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "table");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "detail-plot");
    }

    [Fact]
    public void Extract_R_DetectsShinyReactiveSymbols()
    {
        // Shiny reactive assignments are named server endpoints even though they are not function().
        // Shiny reactive 代入は function() ではないが名前付き server endpoint。
        const string content = """
            filtered <- reactive({
              data
            })

            selected = eventReactive(input$go, {
              input$id
            })

            `refresh-cache` <- observeEvent(input$refresh, {
              update_cache()
            })
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "filtered");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "selected");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "refresh-cache");
    }

    [Fact]
    public void Extract_R_DetectsBacktickEscapedFunctionNames()
    {
        // Backtick-escaped names are valid R identifiers / バッククォート付きの名前は R の有効な識別子
        const string content = """
            `plot-model` <- function(data) {
              data
            }

            my_plot <- function(x) {
              x
            }

            `print-model` = function(value) {
              value
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plot-model");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_plot");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "print-model");
    }

    [Fact]
    public void Extract_R_DetectsSuperassignmentFunctionDefinitions()
    {
        // R: superassignment can define functions in an enclosing environment.
        // R: superassignment は外側の環境へ関数を定義できる。
        const string content = """
            register_handler <<- function(event) {
              event
            }

            `plot-model` <<- function(data) {
              data
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "register_handler");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plot-model");
    }

    [Fact]
    public void Extract_R_DetectsShorthandFunctionAssignments()
    {
        // R 4.1 shorthand functions use \(...) instead of function(...).
        // R 4.1 の shorthand function は function(...) の代わりに \(...) を使う。
        const string content = """
            compact <- \(x) x + 1
            `plot-model` = \(data) data
            global <<- \(value) value
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "compact");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plot-model");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "global");
    }

    [Fact]
    public void Extract_R_DetectsRightwardFunctionAssignments()
    {
        // R supports rightward assignment forms for function values.
        // R は function 値の右代入形式もサポートする。
        const string content = """
            function(x) x + 1 -> increment
            function(data) data ->> global_transform
            \(value) value -> `plot-model`
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "increment");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "global_transform");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plot-model");
    }

    [Fact]
    public void Extract_R_DetectsAssignFunctionDefinitions()
    {
        // R: assign() can install a function under a string name.
        // R: assign() は文字列名で function を配置できる。
        const string content = """
            assign("build_plot", function(data) {
              data
            })

            assign(x = "format_model", value = function(model) {
              model
            })

            assign("compact_plot", \(data) data)
            assign(x = "label_model", value = \(model) model)

            assign("not_a_function", 42)
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build_plot");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "format_model");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "compact_plot");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "label_model");
        Assert.DoesNotContain(symbols, s => s.Name == "not_a_function");
    }

    [Fact]
    public void Extract_R_DetectsS4AndReferenceClassDefinitions()
    {
        // R: setClass, setClassUnion, setIs, setRefClass, R6Class, setGeneric, setMethod, inherit metadata / R: setClass、setClassUnion、setIs、setRefClass、R6Class、setGeneric、setMethod、inherit メタデータ
        var content = """
            methods::setClass(Class = "Person", slots = c(name = "character"))

            methods::setClassUnion(name = Renderable, c(Person, Widget))

            methods::setIs(class1 = SourceClass, class2 = TargetClass)

            methods::setRefClass(classname = Widget, fields = list(value = "numeric"))

            methods::setOldClass(classes = "LegacyThing")

            methods::setValidity(Class = "LegacyThing", function(object) TRUE)

            R6::R6Class(classname = Thing,
              inherit = c(BaseThing, AnotherThing),
              public = list(print = function() self),
              private = list(secret = function() self),
              active = list(state = function(value) self))

            methods::setGeneric(f = "normalize", function(x) standardGeneric("normalize"))

            methods::setGroupGeneric(name = "transformGroup")

            methods::setMethod(f = show, signature(object = "Person"), function(object) {
              object
            })
            """;
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Person");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Renderable");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "TargetClass");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "LegacyThing");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Thing");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "BaseThing");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "LegacyThing");
        var print = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "print");
        Assert.Equal("public", print.Visibility);
        var secret = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "secret");
        Assert.Equal("private", secret.Visibility);
        var state = Assert.Single(symbols, s => s.Kind == "function" && s.Name == "state");
        Assert.Equal("active", state.Visibility);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "transformGroup");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "show");
    }

    [Fact]
    public void Extract_R_DetectsFunctionAssignmentAndLibrary()
    {
        // R: function assignment, library / R: 関数代入、library
        var content = "library(ggplot2)\n\nmy_plot <- function(data, x, y) {\n  ggplot(data, aes(x, y))\n}";
        var symbols = SymbolExtractor.Extract(1, "r", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ggplot2");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "my_plot");
    }

    [Fact]
    public void Extract_Lua_DetectsFunctionsAndRequire()
    {
        // Lua: function, local function, assignment forms, require / Lua: 関数、ローカル関数、代入形式、require
        var content = """
            local http = require('socket.http')

            local helper = function(x)
              return x
            end

            M.named = function(name)
              return "hello " .. name
            end

            function M:method_form(arg)
              return arg
            end

            function M.dot_form(arg)
              return arg
            end

            function M.deep.table_key(arg, ...)
              return arg
            end

            local function top_local(x)
              return x
            end

            function plain_function(a)
              return a
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "lua", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "socket.http");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M.named");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M:method_form");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M.dot_form");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M.deep.table_key");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "top_local");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "plain_function");
    }

    [Fact]
    public void Extract_Elixir_DetectsModuleAndFunctions()
    {
        // Elixir: defmodule, def, defp / Elixir: モジュール、関数、プライベート関数
        var content = "defmodule MyApp.Router do\n  def call(conn, _opts) do\n    conn\n  end\n\n  defp parse(data) do\n    data\n  end\nend";
        var symbols = SymbolExtractor.Extract(1, "elixir", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyApp.Router");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "call");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse");
    }

    [Fact]
    public void Extract_Elixir_DetectsProtocolImplementations()
    {
        const string content = """
            defmodule MyApp.Stream do
              defstruct [:items]
            end

            defimpl Enumerable, for: MyApp.Stream do
              def count(stream), do: {:ok, length(stream.items)}
            end

            defimpl Inspect, for: [MyApp.Stream, Other.Stream] do
              def inspect(stream, _opts), do: "#Stream<#{length(stream.items)}>"
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "elixir", content);

        Assert.Contains(symbols, s => s.Kind == "protocol_impl" && s.Name.StartsWith("Enumerable", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "protocol_impl" && s.Name.StartsWith("Inspect", StringComparison.Ordinal));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "count" && s.ContainerKind == "protocol_impl");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "inspect" && s.ContainerKind == "protocol_impl");
    }

    [Fact]
    public void Extract_Elixir_NestedBlocks_AndDoShorthand_HaveMatchingBodyRanges()
    {
        // Elixir: nested fn/case/if/with bodies and `, do:` shorthand / ネストした fn/case/if/with と `, do:` 短縮形
        var content = "defmodule MyApp do\n  def process(items) do\n    Enum.each(items, fn item ->\n      IO.puts(\"item=#{item}\")\n    end)\n\n    Enum.map items, fn x -> x * 2 end\n    sigil = ~s(end)\n\n    case items do\n      [] -> :empty\n      [h | _] -> h\n    end\n\n    with {:ok, data} <- fetch(),\n         {:ok, parsed} <- parse(data) do\n      IO.puts(parsed)\n    end\n  end\n\n  def fetch do\n    if true do\n      {:ok, \"data\"}\n    else\n      :error\n    end\n  end\n\n  def parse(data), do: {:ok, data}\n\n  def quick_check, do: helper()\n\n  def helper, do: :ok\nend";
        var symbols = SymbolExtractor.Extract(1, "elixir", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyApp" && s.StartLine == 1 && s.EndLine == 34);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "process" && s.StartLine == 2 && s.EndLine == 19 && s.BodyStartLine == 3 && s.BodyEndLine == 19);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fetch" && s.StartLine == 21 && s.EndLine == 27 && s.BodyStartLine == 22 && s.BodyEndLine == 27);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "parse" && s.StartLine == 29 && s.EndLine == 29 && s.BodyStartLine == 29 && s.BodyEndLine == 29);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "quick_check" && s.StartLine == 31 && s.EndLine == 31 && s.BodyStartLine == 31 && s.BodyEndLine == 31);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper" && s.StartLine == 33 && s.EndLine == 33 && s.BodyStartLine == 33 && s.BodyEndLine == 33);
    }

    [Fact]
    public void Extract_Scala_DetectsObjectTraitAndDef()
    {
        // Scala: object, trait, def, case class / Scala: オブジェクト、トレイト、def、ケースクラス
        var content = """
            object Main {
              def one() = 1
              def run(): Unit = {
                2
              }
              def three() = 3
              type Callback = Int => Int
              type UserID = Int
            }
            sealed trait Message
            case class Ping(id: Int) extends Message
            """;
        var symbols = SymbolExtractor.Extract(1, "scala", content);

        Assert.Contains(symbols, s => s.Kind == "object" && s.Name == "Main");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "one" && s.StartLine == 2 && s.EndLine == 2 && s.BodyStartLine == null && s.BodyEndLine == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.StartLine == 3 && s.EndLine == 5 && s.BodyStartLine == 3 && s.BodyEndLine == 5);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "three" && s.StartLine == 6 && s.EndLine == 6 && s.BodyStartLine == null && s.BodyEndLine == null);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Callback");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "UserID");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Message");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Ping");
    }

    [Fact]
    public void Extract_Scala_DetectsCompanionAndSealedObjects()
    {
        var content = """
            package example

            class Config(value: String)
            object Config extends App {
              def load(): Config = Config("default")
            }

            object PackageRegistry {
              def register(): Unit = ()
            }

            sealed trait Status
            case object Ready extends Status
            sealed object Closed extends Status
            """;
        var symbols = SymbolExtractor.Extract(1, "scala", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config" && s.SubKind == "has_companion_object" && s.StartLine == 3 && s.EndLine == 3 && s.BodyStartLine == null && s.BodyEndLine == null);
        Assert.Contains(symbols, s => s.Kind == "object" && s.Name == "Config" && s.SubKind == "companion_object" && s.ContainerKind == null && s.ContainerName == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "load" && s.ContainerKind == "object" && s.ContainerName == "Config");
        Assert.Contains(symbols, s => s.Kind == "object" && s.Name == "PackageRegistry");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "object" && s.Name == "Ready");
        Assert.Contains(symbols, s => s.Kind == "object" && s.Name == "Closed");
    }

    [Fact]
    public void Extract_Scala_WrappedClassHeaderKeepsBodyRange()
    {
        var content = """
            class Service
              extends Base {
              def run(): Int = 1
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "scala", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service" && s.StartLine == 1 && s.EndLine == 4 && s.BodyStartLine == 2 && s.BodyEndLine == 4);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerKind == "class" && s.ContainerName == "Service");
    }

    [Fact]
    public void Extract_Scala_WrappedBracelessConstructorDoesNotContainCompanion()
    {
        var content = """
            class Config(
              value: String
            )
            object Config {
              def load(): Config = Config("default")
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "scala", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config" && s.SubKind == "has_companion_object" && s.StartLine == 1 && s.EndLine == 3 && s.BodyStartLine == null && s.BodyEndLine == null);
        Assert.Contains(symbols, s => s.Kind == "object" && s.Name == "Config" && s.SubKind == "companion_object" && s.ContainerKind == null && s.ContainerName == null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "load" && s.ContainerKind == "object" && s.ContainerName == "Config");
    }

    [Fact]
    public void Extract_Dart_DetectsClassFunctionAndMixin()
    {
        // Dart: class, mixin, function / Dart: クラス、mixin、関数
        var content = "abstract class Widget {\n  void build(BuildContext ctx) {\n  }\n}\nmixin Logging on Widget {\n}";
        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Logging");
    }

    [Fact]
    public void Extract_Dart_DetectsConstructorsAndTypedefAliases()
    {
        // Dart: constructors, typedef aliases / Dart: コンストラクタ、typedef エイリアス
        var content = """
            abstract class Animal {
              String name;
              int age;

              Animal(this.name, this.age);
              Animal.named({required this.name, this.age = 0});
              factory Animal.empty() => _EmptyAnimal();
              factory Animal.fromJson(Map<String, dynamic> json) =>
                  Animal(json['name'] as String, json['age'] as int);
              const Animal.constant(this.name, this.age);
            }

            class Point {
              const Point(this.x, this.y);

              final int x;
              final int y;
            }

            class Token {
              const Token();
              const Token.named();

              void demo() {
                const Widget(key: k);
              }
            }

            class _EmptyAnimal extends Animal {
              _EmptyAnimal() : super('', 0);
            }

            typedef IntCallback = int Function(int value);
            typedef StringMap = Map<String, String>;
            typedef int LegacyCallback(int value);
            """;

        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Animal");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Animal.named");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Animal.empty");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Animal.fromJson");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Animal.constant");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Token");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Token.named");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "_EmptyAnimal");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "IntCallback");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "StringMap");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "LegacyCallback");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Widget");
    }

    [Fact]
    public void Extract_Dart_DoesNotTreatKeywordLookalikesAsFunctions()
    {
        var content = """
            abstract class Widget {}

            String bar() => 'bar';

            void sample(bool first, bool secondReady) {
              if (first) {
              }
              else if (secondReady) {
              }
            }

            void handle(Object value) {
              switch (value) {
                case const Class():
                  break;
              }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "bar");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "sample");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "handle");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Class");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "class");
    }

    [Fact]
    public void Extract_Dart_DetectsImportAndEnum()
    {
        // Dart: import, enum / Dart: インポート、列挙型
        var content = "import 'package:flutter/material.dart';\n\nenum Status {\n  active,\n  inactive,\n}";
        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("flutter"));
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
    }

    [Fact]
    public void Extract_Dart_DoesNotMatchExpressionLines()
    {
        // Expressions that look like "type name(" but are not function definitions
        // 関数定義に見えるが実際は式の行を誤検出しないことを検証
        var content = "void main() {\n  return foo(bar);\n  await task(x);\n  const Widget(key: k);\n  throw Error('oops');\n}";
        var symbols = SymbolExtractor.Extract(1, "dart", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "foo");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "task");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Widget");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Error");
        // main() should still be detected / main()は検出されるべき
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main");
    }

    [Fact]
    public void Extract_GraphQL_DetectsSymbols()
    {
        var content = """
            type User {
              id: ID!
              name: String!
            }

            input CreateUserInput {
              name: String!
              email: String!
              roles: [Role!]!
            }

            union SearchResult @deprecated(reason: "legacy") = User | Organization | Team @deprecated # returned by search

            enum Role {
              ADMIN
              USER
            }

            query GetUser($id: ID!) {
              user(id: $id) { name }
            }

            mutation CreateUser($input: CreateUserInput!) {
              createUser(input: $input) { id }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "graphql", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "CreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "CreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "email" && s.ContainerName == "CreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "roles" && s.ContainerName == "CreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "User" && s.ContainerName == "SearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "Organization" && s.ContainerName == "SearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "Team" && s.ContainerName == "SearchResult");
        Assert.DoesNotContain(symbols, s => s.Kind == "reference" && s.Name is "deprecated" or "reason" or "legacy" or "returned" or "search");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Role");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CreateUser");
    }

    [Fact]
    public void Extract_GraphQL_DetectsFragmentsDirectivesAndExtends()
    {
        var content = """
            type User {
              id: ID!
            }

            input CreateUserInput {
              name: String!
            }

            interface Node {
              id: ID!
            }

            enum Role {
              ADMIN
              USER
            }

            fragment ProcessingTimeoutError on ProcessingTimeoutError {
              __typename
              message
            }

            directive @auth(role: String!) on FIELD_DEFINITION

            extend type ExtendedUser {
              profile: Profile
            }

            extend interface ExtendedNode {
              archived: Boolean
            }

            extend input ExtendedCreateUserInput {
              email: String
              phone: String
            }

            extend enum ExtendedRole {
              GUEST
            }

            extend union SearchResult =
              | User
              # organization result
              | Organization
              | Team @deprecated(reason: "legacy")

            union ExternalSearchResult @deprecated(reason: "legacy")
              = User
              | Organization

            extend scalar DateTime

            query GetUser($id: ID!) {
              user(id: $id) { name }
            }

            mutation CreateUser($input: CreateUserInput!) {
              createUser(input: $input) { id }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "graphql", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "CreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "Node");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Role");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ProcessingTimeoutError");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "auth");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ExtendedUser");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ExtendedNode");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ExtendedCreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "email" && s.ContainerName == "ExtendedCreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "phone" && s.ContainerName == "ExtendedCreateUserInput");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ExtendedRole");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "User" && s.ContainerName == "SearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "Organization" && s.ContainerName == "SearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "Team" && s.ContainerName == "SearchResult");
        Assert.DoesNotContain(symbols, s => s.Kind == "reference" && s.Name is "organization" or "result" or "deprecated" or "reason" or "legacy");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "User" && s.ContainerName == "ExternalSearchResult");
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "Organization" && s.ContainerName == "ExternalSearchResult");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "DateTime");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CreateUser");
    }



    [Fact]
    public void Extract_JavaScript_DetectsModuleDocHeading()
    {
        var symbols = SymbolExtractor.Extract(1, "javascript", """
            /**
             * @module payments/service
             */
            export function charge() {}
            """);

        Assert.Contains(symbols, s => s.Kind == "heading" && s.Name == "payments/service");
    }

    [Fact]
    public void Extract_Gradle_DetectsSymbols()
    {
        // Both legacy apply plugin: and modern plugins { id '...' } forms
        // レガシーの apply plugin: と新しい plugins { id '...' } の両形式
        var content = "apply plugin: 'java'\n\nplugins {\n  id 'org.springframework.boot'\n}\n\ntask build {\n  doLast { println 'Building' }\n}\n\ndef customTask {\n  println 'custom'\n}\n";
        var symbols = SymbolExtractor.Extract(1, "gradle", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "java");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "org.springframework.boot");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "customTask");
    }










    [Fact]
    public void Extract_CommonLisp_DetectsTopLevelDefinitions()
    {
        // Common Lisp: package, package transitions, classes, structs, variables, functions / Common Lisp: パッケージ、パッケージ遷移、クラス、構造体、変数、関数
        var content = """
            (defpackage :my-app
              (:use :cl))

            (in-package :my-app)
            (use-package :cl)
            (import 'widget)
            (shadowing-import 'render)

            (defclass widget ()
              ())

            (defstruct point
              x
              y)

            (defparameter *default-size* 42)

            (defun render (widget)
              widget)

            (defmacro with-widget ((widget) &body body)
              `(let ((,widget ,widget))
                 ,@body))
            """;
        var symbols = SymbolExtractor.Extract(1, "commonlisp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == ":my-app");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == ":my-app");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == ":cl");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "widget");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "render");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "widget");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "*default-size*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "with-widget");
    }

    [Fact]
    public void Extract_CommonLisp_DoesNotTreatQuotedReaderMacroFormsAsDefinitions()
    {
        var content = """
            '(defun quoted-function () nil)
            `(let ((x 1)) ,x)
            ,(expand-macro)
            (quote (defun nested-quoted () nil))
            (defun real-function () nil)
            """;

        var symbols = SymbolExtractor.Extract(1, "commonlisp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "real-function");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "quoted-function");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "nested-quoted");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "let");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "expand-macro");
    }

    [Fact]
    public void Extract_CommonLisp_PreservesDefinitionsInsideEvalWhen()
    {
        var content = """
            (eval-when (:compile-toplevel :load-toplevel :execute)
              (defun real-wrapper-function () nil))
            """;

        var symbols = SymbolExtractor.Extract(1, "commonlisp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "real-wrapper-function");
    }

    [Fact]
    public void Extract_Racket_DetectsModuleAndDefinitions()
    {
        // Racket: module, define, define-syntaxes, struct, require / Racket: module、define、define-syntaxes、struct、require
        var content = """
            #lang racket

            (module app racket
              (require racket/list)
              (provide render)
              (provide point)

              (define answer 42)

              (define (render value)
                value)

              (define-syntax-rule (make-point x)
                x)

              (define-syntaxes (make-point-2)
                (lambda (stx)
                  stx))

              (struct point (x y)))
            """;
        var symbols = SymbolExtractor.Extract(1, "racket", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "app");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "racket/list");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "render");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "make-point");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "make-point-2");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "point");
    }

    [Fact]
    public void Extract_CommonLisp_MasksCommentsAndAssignsFunctionRanges()
    {
        var content = """
            (in-package :my-app)

            (defun render (widget)
              "(defun hidden-string () nil)"
              ; (defun hidden-line () nil)
              #| (defun hidden-block () nil) |#
              (helper widget))

            (defun helper (value)
              value)
            """;
        var symbols = SymbolExtractor.Extract(1, "commonlisp", content);

        var render = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "render"));
        Assert.Equal(3, render.StartLine);
        Assert.Equal(7, render.EndLine);
        Assert.Equal(3, render.BodyStartLine);
        Assert.Equal(7, render.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.DoesNotContain(symbols, s => s.Name == "hidden-string");
        Assert.DoesNotContain(symbols, s => s.Name == "hidden-line");
        Assert.DoesNotContain(symbols, s => s.Name == "hidden-block");
    }

    [Theory]
    [InlineData("csharp", "public interface IFoo { }", "interface")]
    [InlineData("csharp", "public enum Color { }", "enum")]
    [InlineData("csharp", "public struct Point { }", "struct")]
    [InlineData("csharp", "public delegate void Handler();", "delegate")]
    [InlineData("csharp", "public event EventHandler Click;", "event")]
    [InlineData("csharp", "public string Name { get; set; }", "property")]
    [InlineData("java", "interface Foo { }", "interface")]
    [InlineData("java", "enum Color { RED }", "enum")]
    [InlineData("kotlin", "interface Foo", "interface")]
    [InlineData("kotlin", "enum class Color { RED }", "enum")]
    [InlineData("kotlin", "val name: String = \"\"", "property")]
    [InlineData("typescript", "export interface IFoo { }", "interface")]
    [InlineData("typescript", "export enum Status { A }", "enum")]
    [InlineData("go", "type Foo struct { }", "struct")]
    [InlineData("go", "type Foo interface { }", "protocol")]
    [InlineData("rust", "pub struct Config { }", "struct")]
    [InlineData("rust", "pub enum Color { }", "enum")]
    [InlineData("rust", "pub trait Foo { }", "protocol")]
    [InlineData("swift", "struct Config { }", "struct")]
    [InlineData("swift", "enum Color { }", "enum")]
    [InlineData("swift", "protocol Foo { }", "protocol")]
    [InlineData("c", "struct Config { };", "struct")]
    [InlineData("c", "enum Color { RED };", "enum")]
    [InlineData("cpp", "struct Config { };", "struct")]
    [InlineData("cpp", "enum Color { RED };", "enum")]
    [InlineData("php", "interface Foo { }", "interface")]
    [InlineData("php", "trait Foo { }", "trait")]
    [InlineData("php", "enum Color { }", "enum")]
    [InlineData("scala", "trait Foo", "interface")]
    [InlineData("scala", "enum Color", "enum")]
    [InlineData("dart", "enum Status { active }", "enum")]
    [InlineData("graphql", "interface Node { }", "interface")]
    [InlineData("graphql", "enum Role { ADMIN }", "enum")]
    [InlineData("haskell", "class Functor f where", "interface")]
    [InlineData("elixir", "defprotocol Enumerable do\nend", "interface")]
    [InlineData("ruby", "  attr_accessor :name", "property")]
    [InlineData("python", "@property\ndef name(self):", "property")]
    public void Extract_CrossLanguage_GranularKindsAreConsistent(string lang, string content, string expectedKind)
    {
        var symbols = SymbolExtractor.Extract(1, lang, content);
        Assert.Contains(symbols, s => s.Kind == expectedKind);
    }




    [Fact]
    public void Extract_CSS_DetectsSymbols()
    {
        var content = """
            @import 'reset.css';
            @forward 'theme';

            $primary-color: #333;

            @function shade-color($color, $weight) {
              @return $color;
            }

            @mixin flex-center {
              display: flex;
              align-items: center;
            }

            @keyframes fade-in {
              from { opacity: 0; }
              to { opacity: 1; }
            }

            @property --accent-color {
              syntax: "<color>";
              inherits: true;
              initial-value: #09f;
            }

            .container {
              max-width: 1200px;
            }

            #header {
              background: $primary-color;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("reset.css"));
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("theme"));
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "primary-color");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "shade-color");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "flex-center");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fade-in");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "--accent-color");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".container");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "#header");
    }

    [Fact]
    public void Extract_CSS_DetectsCustomPropertiesFontFacesAndSelectorVariants()
    {
        var content = """
            :root {
              --accent-color: #09f;
            }

            @font-face {
              font-family: "Block Font";
              src: url("block.woff2");
            }

            @FONT-FACE {
              font-family:
                "Split Font";
              src: url("split.woff2");
            }

            a:hover {
              color: red;
            }

            .btn.primary {
              color: green;
            }

            .alert .link {
              color: orange;
            }

            input[type="text"] {
              color: blue;
            }

            [hidden] {
              display: none;
            }

            .btn::before {
              content: "";
            }

            %button-base {
              padding: 4px;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ":root");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "--accent-color");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Block Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Split Font");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "a:hover");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".btn");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".alert");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "input[type=\"text\"]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[hidden]");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".btn::before");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "%button-base");
    }

    [Fact]
    public void Extract_CSS_DetectsInlineFontFaceFamilyNames()
    {
        var content = """
            @font-face { font-family: "Inline Font"; src: url("inline.woff2"); }
            @font-face { src: url("same-line.woff2"); font-family: "Trailing Font"; unicode-range: U+0-5FF; }
            @font-face { src: url("valid-last.woff2"); font-family: "Last No Semicolon" }
            @font-face { font-family: /* keep */ "Comment Gap"; src: url("comment-gap.woff2"); }
            @font-face {
              src: url("commented.woff2");
              /* font-family: bogus; */
              font-family: "Commented Font";
            }
            @font-face {
              src: url("data:application/font-woff2;charset=utf-8;base64,font-family:bogus");
              font-family: "Real Font";
            }
            @font-face { src: url(data:text/plain;charset=utf-8;foo=1;font-family:bogus); font-family: Real Data Font; }
            @font-face { src: url(data:image/svg+xml,<svg>{}</svg>); font-family: Svg Data Font; }
            @font-face { src: url("no-family.woff2"); }
            @font-face {
              font-family:
                "Split Font";
              src: url("split.woff2");
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Inline Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Trailing Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Last No Semicolon");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Comment Gap");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Commented Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Real Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Real Data Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Svg Data Font");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Split Font");
        Assert.DoesNotContain(symbols, s => s.Name == "@font-face");
        Assert.DoesNotContain(symbols, s => s.Name == "bogus)");
    }

    [Fact]
    public void Extract_CSS_CapturesCommaSeparatedSelectorListsAndNamedAtRules()
    {
        var content = """
            .btn, .link { color: red; }
            #nav, #header { display: flex; }

            @counter-style circled {
              system: fixed;
              symbols: \2460 \2461;
            }

            @layer reset, base, theme;
            @namespace svg url("http://www.w3.org/2000/svg");

            @page :first {
              margin: 2cm;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".btn");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".link");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "#nav");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "#header");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "circled");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "reset");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "base");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "theme");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "svg");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == ":first");
    }

    [Fact]
    public void Extract_CSS_CapturesGroupingAtRulesAndNativeNestingSelectors()
    {
        var content = """
            @layer components {
              .layer-class {
                &:hover {
                  color: blue;
                }

                & .icon {
                  color: white;
                }

                &.modifier {
                  color: green;
                }

                & > .child {
                  color: yellow;
                }
              }
            }

            @container (min-width: 500px) {
              .container-class {
                display: flex;
              }
            }

            @supports (display: grid) {
              .supports-class {
                display: grid;
              }
            }

            @media screen {
              .media-class {
                color: red;
              }
            }

            .parent {
              .nested-child {
                color: blue;
              }
            }

            @media screen { .inline-media { color: red; } }
        """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "layer");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "container");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "supports");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "media");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".layer-class");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".container-class");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".supports-class");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".media-class");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".inline-media");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "hover");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "icon");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "modifier");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "child");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == ".nested-child");
    }

    [Fact]
    public void Extract_CSS_CapturesMediaFeatureNamesButNotValuesOrOperators()
    {
        var content = """
            @media (min-width: 768px) and (prefers-color-scheme: dark), not screen and (orientation: landscape) {
              .responsive {
                color: red;
              }
            }

            @supports (display: grid) {
              @media (width >= 40rem) and (--narrow) {
                .nested-media {
                  display: grid;
                }
              }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "min-width");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "prefers-color-scheme");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "orientation");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "width");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "--narrow");
        Assert.DoesNotContain(symbols, s =>
            s.Kind == "property"
            && s.Name is "768px" or "dark" or "landscape" or "and" or "not" or "or");
    }

    [Fact]
    public void Extract_CSS_DoesNotLeakNestedSelectorsAfterSameLineGroupingAndQualifiedRule()
    {
        var content = """
            @media screen { .outer {
              .inner { color: red; }
            } }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == ".inner");
    }

    [Fact]
    public void Extract_CSS_TopLevelDetection_IgnoresScssLineCommentsAndEscapedQuotes()
    {
        var content = """
            // {
            .top-level { color: red; }

            .foo::before { content: "\""; }
            .bar { color: blue; }

            .parent { background-image: url(http://example.com/a.png); }
            .top-level:hover { color: green; }

            .parent2 { background-image: url(//cdn.example.com/app.css); }
            [data-theme="dark"] { color: white; }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".top-level");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".foo::before");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".bar");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".top-level:hover");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "[data-theme=\"dark\"]");
    }

    [Fact]
    public void Extract_CSS_PreservesLiteralSelectorNames()
    {
        var content = """
            :root { --accent: #09f; }
            .root { color: red; }
            #root { color: blue; }
            """;
        var symbols = SymbolExtractor.Extract(1, "css", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ":root");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "--accent");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == ".root");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "#root");
    }

    [Fact]
    public void Extract_PowerShell_DetectsSymbols()
    {
        var content = """
            Import-Module ActiveDirectory
            using module PSDesiredStateConfiguration
            using namespace System.IO
            using assembly System.Xml.Linq

            configuration MyConfig {
                Node 'localhost' { }
            }

            workflow TestFlow {
                Get-Process
            }

            class ServerConfig {
                [string]$Name
            }

            enum Environment {
                Dev
                Staging
                Prod
            }

            function Get-UserInfo {
                param($UserId)
                Get-ADUser -Identity $UserId
            }

            function script:Private-Helper { return 42 }
            function global:Setup-Env {
                $env:APP_MODE = 'dev'
            }
            function local:Inner-Helper { return 'inner' }
            function private:InternalUtil { return 'util' }

            filter script:Where-Active {
                if ($_.Enabled) { $_ }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "powershell", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "ActiveDirectory");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "PSDesiredStateConfiguration");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.IO");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.Xml.Linq");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MyConfig");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TestFlow");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ServerConfig");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Environment");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get-UserInfo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Private-Helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Setup-Env");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Inner-Helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "InternalUtil");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Where-Active");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "script");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "global");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "local");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "private");
    }

    [Fact]
    public void Extract_PowerShell_DetectsAliasDefinitions()
    {
        const string content = """
            Set-Alias gci Get-ChildItem
            New-Alias -Name ls -Value Get-ChildItem

            function Show-Items {
                gci
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "powershell", content);

        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "gci");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "ls");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Show-Items");
    }

    [Fact]
    public void Extract_PowerShell_DetectsClassMembersAndEnumValues()
    {
        var content = """
            class Vehicle {
                [DscProperty(Key)] [string]$Name
                hidden [string]$Secret

                Vehicle([string]$make) {
                    $this.Name = $make
                }

                [string] ToString() {
                    return "$($this.Name)"
                }

                static [Vehicle] CreateDefault() {
                    return [Vehicle]::new("Unknown")
                }

                hidden [void] InternalMethod() {
                }
            }

            enum LogLevel {
                Debug
                Info = 1
                Warning
                Error
            }

            class MyDscResource {
                [DscProperty(Key)] [string]$Name

                [MyDscResource] Get() {
                    return $this
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "powershell", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Vehicle");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Secret");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Vehicle");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ToString");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CreateDefault");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "InternalMethod");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "LogLevel");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Debug");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Info");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Warning");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Error");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "MyDscResource");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get");
    }

    [Fact]
    public void Extract_Batch_DetectsLabelsAndSetAssignments()
    {
        // Covers issue #217: batch (.bat / .cmd) labels are the only navigation anchors
        // in a batch script (goto :X / call :X targets). Without label symbols every batch
        // file indexed with zero symbol rows. Also pins:
        //   - `:EOF` is the reserved `goto :EOF` / `call :EOF` target and must NOT surface.
        //   - `::` / `:::` comment lines must not produce bogus symbols.
        //   - `SET` / `Set` / `SET /A` / `SET /P` variations are all picked up.
        //   - CRLF line endings behave the same as LF.
        //   - Echo-suppression `@set VAR=...` (with or without whitespace after `@`).
        //   - `set /a VAR+=1` and other compound arithmetic operators (`-=`, `*=`, `/=`,
        //     `%=`, `&=`, `|=`, `^=`, `<<=`, `>>=`).
        //   - `if <cond> set VAR=...` style one-line conditional assignments.
        //   - Same-line multi-statement forms emit one symbol per `set`: `&`-chained
        //     (`set A=1 & set B=2`), `if ... & set` (`if exist x set C=3 & set D=4`),
        //     parenthesized + `else` (`if exist x ( set E=5 ) else set F=6`), and
        //     `for ... do set` (`for %%I in (1) do set LOOPVAR=%%I`).
        //   - `rem` / `@rem` / `::` comment lines do NOT emit phantom `set` symbols even when
        //     the comment body contains the new boundary tokens (`&`, `(`, `else`, `do`).
        //   - Dotted labels such as `:build.release` are captured in full, not truncated.
        // issue #217 対応: batch (.bat / .cmd) のラベルは batch スクリプトにおける唯一の
        // ナビゲーションアンカー (goto :X / call :X の着地点)。ラベルシンボルが無いと
        // 全ての batch ファイルがシンボル 0 件のまま索引されてしまっていた。あわせて以下を固定:
        //   - `:EOF` は `goto :EOF` / `call :EOF` 用の予約ターゲットなのでシンボル化しない。
        //   - `::` / `:::` コメント行は偽シンボルを生成しない。
        //   - `SET` / `Set` / `SET /A` / `SET /P` の大小文字混在・オプション違いも拾う。
        //   - CRLF 行末でも LF と同じ結果になる。
        //   - echo 抑止プレフィクス付きの `@set VAR=...` (`@` 直後の空白有無を含む) を拾う。
        //   - `set /a VAR+=1` および `-=` / `*=` / `/=` / `%=` / `&=` / `|=` / `^=` / `<<=` / `>>=`
        //     の複合演算子も拾う。
        //   - `if <cond> set VAR=...` 形式の 1 行条件付き代入も拾う。
        //   - 同一行複数ステートメント形を 1 `set` ごとに 1 シンボルとして拾う:
        //     `&` 連結 (`set A=1 & set B=2`) 、`if ... & set` (`if exist x set C=3 & set D=4`) 、
        //     括弧 + `else` (`if exist x ( set E=5 ) else set F=6`) 、
        //     `for ... do set` (`for %%I in (1) do set LOOPVAR=%%I`)。
        //   - `rem` / `@rem` / `::` コメント行は、本文に新しい境界トークン (`&` / `(` / `else` / `do`) を
        //     含んでいても偽の `set` シンボルを出さない。
        //   - `:build.release` のようなドット付きラベルは切り詰めずフル名で取得する。
        // Fixture also includes `:eof2` / `:eofish` / `:end-of-file` so the `(?!eof(?![\w.-]))`
        // boundary is explicitly pinned — only the reserved `:EOF` token is rejected, not
        // labels that merely start with `eof`.
        // `:eof2` / `:eofish` / `:end-of-file` も fixture に含め、`(?!eof(?![\w.-]))` の境界条件を固定する
        // (予約トークン `:EOF` だけが除外され、`eof` で始まる別名は通る)。
        var content = "@echo off\r\nREM Build script\r\nsetlocal\r\n\r\nset VERSION=1.0.0\r\nSET OUTPUT_DIR=%~dp0out\r\nSet /A COUNT=1\r\nSET /P INPUT=Enter: \r\nset \"QUOTED=value with spaces\"\r\n@set AT_PREFIX=1\r\n@ SET AT_SPACED=2\r\nset /a COMPOUND+=1\r\nset /A SHIFTED<<=2\r\nif not defined INLINE_DEF set INLINE_DEF=inline_default\r\nif \"%1\"==\"\" set INLINE_EQ=empty\r\nset CHAIN_A=1 & set CHAIN_B=2\r\nif exist foo.txt set IF_CHAIN_X=3 & set IF_CHAIN_Y=4\r\nif exist foo.txt ( set PAREN_P=5 ) else set ELSE_Q=6\r\nfor %%I in (1) do set LOOPVAR=%%I\r\nREM set FROM_REM=ignored\r\nREM & set FROM_REM_AMP=ignored\r\nREM ( set FROM_REM_PAREN=ignored )\r\nREM else set FROM_REM_ELSE=ignored\r\nREM do set FROM_REM_DO=ignored\r\n@REM & set FROM_AT_REM_AMP=ignored\r\n:: set FROM_DOUBLE_COLON=ignored\r\n:: & set FROM_DC_AMP=ignored\r\n:: ( set FROM_DC_PAREN=ignored )\r\n:: else set FROM_DC_ELSE=ignored\r\n:: do set FROM_DC_DO=ignored\r\n\r\n:main\r\ncall :compile\r\nif errorlevel 1 goto :error\r\ncall :test\r\ngoto :end\r\n\r\n:compile\r\necho Compiling...\r\ndotnet build\r\nexit /b %ERRORLEVEL%\r\n\r\n:test\r\necho Testing...\r\nexit /b %ERRORLEVEL%\r\n\r\n:error\r\necho Build failed\r\ngoto :EOF\r\n\r\n:end\r\ncall :eOf\r\ncall :eof2\r\ncall :eofish\r\ncall :end-of-file\r\ncall :build.release\r\nendlocal\r\n\r\n:eof2\r\nexit /b 0\r\n\r\n:eofish\r\nexit /b 0\r\n\r\n:end-of-file\r\nexit /b 0\r\n\r\n:build.release\r\nexit /b 0\r\n\r\n:: This is a batch comment and must not produce a symbol\r\n::: triple-colon comment must not produce a symbol either\r\n";
        var symbols = SymbolExtractor.Extract(1, "batch", content);

        // Exact function label set — nothing extra (no `:EOF`, no comment-derived names),
        // but the `eof`-prefixed user labels (`eof2`, `eofish`, `end-of-file`) pass, and
        // dotted labels (`build.release`) are captured in full rather than truncated.
        // function ラベル集合は厳密一致 — `:EOF` / コメント由来の偽名は混ざらないが、
        // `eof` で始まるユーザーラベル (`eof2` / `eofish` / `end-of-file`) は通り、
        // ドット付きラベル (`build.release`) も切り詰めず全体が取得される。
        var functionNames = symbols.Where(s => s.Kind == "function").Select(s => s.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "main", "compile", "test", "error", "end", "eof2", "eofish", "end-of-file", "build.release" }, functionNames);

        // Exact property name set — nothing extra from comments / echo lines, and the new
        // `@set`, compound-operator, and inline-`if` variants all produce a symbol.
        // property 名集合は厳密一致 — コメントや echo 行由来の偽名は混ざらず、新しく対応した
        // `@set` / 複合演算子 / インライン `if` の各形もすべてシンボル化される。
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToHashSet();
        Assert.Equal(
            new HashSet<string>
            {
                "VERSION", "OUTPUT_DIR", "COUNT", "INPUT", "QUOTED",
                "AT_PREFIX", "AT_SPACED",
                "COMPOUND", "SHIFTED",
                "INLINE_DEF", "INLINE_EQ",
                "CHAIN_A", "CHAIN_B",
                "IF_CHAIN_X", "IF_CHAIN_Y",
                "PAREN_P", "ELSE_Q",
                "LOOPVAR",
            },
            propertyNames);
    }

    [Fact]
    public void Extract_Zig_DetectsSymbols()
    {
        var content = "const std = @import(\"std\");\n\npub fn main() !void {\n    std.debug.print(\"hello\", .{});\n}\n\nfn helper(x: u32) u32 {\n    return x + 1;\n}\n\npub const Config = struct {\n    name: []const u8,\n};\n\nconst Direction = enum {\n    north,\n    south,\n};\n\ntest \"basic test\" {\n    try std.testing.expect(true);\n}";
        var symbols = SymbolExtractor.Extract(1, "zig", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "std");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "main" && s.Visibility == "pub");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "helper");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Config" && s.Visibility == "pub");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Direction");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "basic test");
    }

    [Fact]
    public void Extract_Makefile_DetectsTargetsAndAssignments()
    {
        var content = """
            CC := gcc
            CFLAGS ::= -O2 -Wall
            OBJ = foo.o bar.o
            DEBUG ?= 1
            EXTRA += -pipe

            all: program
            program: $(OBJ)
            	$(CC) -o $@ $^

            foo.o: foo.c foo.h
            	$(CC) $(CFLAGS) -c foo.c

            .PHONY: clean install

            clean:
            	rm -f *.o program

            install: all
            	cp program /usr/local/bin

            %.o: %.c
            	$(CC) $(CFLAGS) -c $< -o $@

            $(OBJ): %.o: %.c
            	$(CC) -c $< -o $@
            """;
        var symbols = SymbolExtractor.Extract(1, "makefile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "all");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "program");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo.o");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "clean");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "install");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "%.o");

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "CC");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "CFLAGS");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "OBJ");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "DEBUG");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "EXTRA");

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "CC");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "CFLAGS");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "OBJ");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DEBUG");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "EXTRA");
    }

    [Fact]
    public void Extract_Dockerfile_DetectsStages()
    {
        var content = "FROM node:18 AS builder\nWORKDIR /app\nCOPY . .\nRUN npm build\n\nFROM alpine:3.18\nCOPY --from=builder /app/dist /app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "builder");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "node:18");
        // Unnamed FROM lines produce base image class / 名前なしFROM行はベースイメージclassを生成
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "alpine:3.18");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/app");
        Assert.Equal(5, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsLowercaseInstructionsAndEnvSymbols()
    {
        var content = """
            from --platform=$BUILDPLATFORM golang:1.22 as builder
            env APP_HOME=/app
            env PATH=/usr/local/bin:$PATH
            from --platform=linux/amd64 alpine:3.20
            """;
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "builder");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "golang:1.22");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "alpine:3.20");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "APP_HOME");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PATH");
        Assert.Equal(5, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsMultipleEnvKeyValueSymbols()
    {
        var content = "ENV APP_HOME=/app NODE_ENV=production PATH=/usr/local/bin:$PATH\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "APP_HOME");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NODE_ENV");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PATH");
        Assert.Equal(3, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_MultipleEnvKeyValueSymbolsIgnoreQuotedValueAssignments()
    {
        var content = "ENV APP_HOME=\"/opt BAR=not-a-key\" NODE_ENV=production\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "APP_HOME");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NODE_ENV");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "BAR");
        Assert.Equal(2, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsLabelKeySymbols()
    {
        var content = "LABEL org.opencontainers.image.title=\"demo\"\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "org.opencontainers.image.title");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsMultipleLabelKeySymbols()
    {
        var content = "LABEL org.opencontainers.image.title=\"demo\" org.opencontainers.image.version=\"1.0\"\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "org.opencontainers.image.title");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "org.opencontainers.image.version");
        Assert.Equal(2, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsLegacyLabelKeySymbols()
    {
        var content = "LABEL com.example.channel stable\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "com.example.channel");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsExposePortSymbols()
    {
        var content = "EXPOSE 8080/tcp\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "8080/tcp");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsMultipleExposePortSymbols()
    {
        var content = "EXPOSE 80 443/tcp 53/udp\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "80");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "443/tcp");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "53/udp");
        Assert.Equal(3, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsUserSymbols()
    {
        var content = "USER appuser\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "appuser");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsUserGroupSymbols()
    {
        var content = "USER appuser:appgroup\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "appuser:appgroup");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "appuser");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsWorkdirSymbols()
    {
        var content = "WORKDIR /app/service\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/app/service");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsVolumePathSymbols()
    {
        var content = "VOLUME /var/lib/app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/var/lib/app");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsMultipleVolumePathSymbols()
    {
        var content = "VOLUME /var/lib/app /var/cache/app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/var/lib/app");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/var/cache/app");
        Assert.Equal(2, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_VolumePathSymbolsIgnoreInlineComments()
    {
        var content = "VOLUME /var/lib/app # /not-a-volume\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/var/lib/app");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "/not-a-volume");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsJsonVolumePathSymbols()
    {
        var content = "VOLUME [\"/var/lib/app\", \"/var/cache/app\"]\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/var/lib/app");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/var/cache/app");
        Assert.Equal(2, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsStopSignalSymbols()
    {
        var content = "STOPSIGNAL SIGTERM\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SIGTERM");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsShellExecutableSymbols()
    {
        var content = "SHELL [\"/bin/bash\", \"-o\", \"pipefail\", \"-c\"]\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/bin/bash");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "-o");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsCopyDestinationPathSymbols()
    {
        var content = "COPY --from=builder /src/app /usr/local/bin/app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/usr/local/bin/app");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "/src/app");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsAddDestinationPathSymbols()
    {
        var content = "ADD archive.tar.gz /opt/app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/opt/app");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "archive.tar.gz");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsJsonCopyDestinationPathSymbols()
    {
        var content = "COPY --chmod=0644 [\"package.json\", \"/app/package.json\"]\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/app/package.json");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "package.json");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsJsonAddDestinationPathSymbols()
    {
        var content = "ADD [\"archive.tar.gz\", \"/opt/app\"]\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/opt/app");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "archive.tar.gz");
        Assert.Single(symbols);
    }

    [Theory]
    [InlineData("VOLUME ")]
    [InlineData("SHELL ")]
    [InlineData("COPY ")]
    [InlineData("ADD ")]
    public void Extract_Dockerfile_JsonFormsIgnorePayloadsBeyondParserDepthLimit(string prefix)
    {
        var depth = SymbolExtractor.DockerfileJsonFormMaxDepth + 1;
        var content = prefix + new string('[', depth) + "\"/too-deep\"" + new string(']', depth) + "\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsOnbuildCopyDestinationPathSymbols()
    {
        var content = "ONBUILD COPY /src/app /usr/local/bin/app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/usr/local/bin/app");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "/src/app");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsOnbuildAddDestinationPathSymbols()
    {
        var content = "ONBUILD ADD archive.tar.gz /opt/app\n";
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "/opt/app");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "archive.tar.gz");
        Assert.Single(symbols);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsPlatformFlaggedStages()
    {
        var content = """
            FROM --platform=$BUILDPLATFORM golang:1.22 AS builder
            FROM --platform=linux/amd64 alpine:3.20
            """;
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "builder");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "golang:1.22");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "alpine:3.20");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "--platform=$BUILDPLATFORM");
        Assert.Equal(3, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_NamedStageBaseImagesSkipPriorStages()
    {
        var content = """
            FROM alpine AS builder
            FROM builder AS runtime
            """;
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "alpine");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "builder");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "runtime");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "builder");
        Assert.Equal(3, symbols.Count);
    }

    [Fact]
    public void Extract_Dockerfile_DetectsBuildArgs()
    {
        var content = """
            ARG NODE_VERSION=20
            FROM node:${NODE_VERSION} AS builder

            ARG APP_HOME
            WORKDIR /app
            """;
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NODE_VERSION");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "APP_HOME");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "builder");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name.Contains("node:${NODE_VERSION}", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_Dockerfile_DetectsHyphenatedStageNames()
    {
        var content = """
            FROM node:20 AS build-env
            FROM build-env AS runtime
            """;
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build-env");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "runtime");
    }

    [Fact]
    public void Extract_Dockerfile_DetectsDottedStageNames()
    {
        var content = """
            FROM node:20 AS build.env
            FROM build.env AS runtime
            """;
        var symbols = SymbolExtractor.Extract(1, "dockerfile", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "build.env");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "runtime");
    }

    [Fact]
    public void Extract_Protobuf_DetectsSymbols()
    {
        var content = """
            syntax = "proto3";
            import "google/protobuf/timestamp.proto";

            message User {
              string name = 1;
              int32 age = 2;
            }

            enum Status {
              UNKNOWN = 0;
              ACTIVE = 1;
            }

            service UserService {
              rpc GetUser (GetUserRequest) returns (User);
              rpc ListUsers (ListUsersRequest) returns (ListUsersResponse);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "protobuf", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "google/protobuf/timestamp.proto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ListUsers");
    }

    [Fact]
    public void Extract_Html_CapturesIdAttributesAsProperties()
    {
        var content = """
            <!DOCTYPE html>
            <html>
              <body>
                <header id="main-header" class="site-header"><h1>Welcome</h1></header>
                <main id='content'><article></article></main>
                <section id="side-panel">legacy</section>
              </body>
            </html>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "main-header");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "content");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "side-panel");
    }

    [Fact]
    public void Extract_Html_IgnoresDataIdAndAriaIdAndXmlIdAttributes()
    {
        // `data-id`, `aria-*id`, and `xml:id` must not be captured as plain id attributes.
        // data-id / aria-*id / xml:id を通常の id として拾わない。
        var content = """
            <article data-id="1"></article>
            <div aria-labelledby="x" aria-hiddenid="bogus"></div>
            <span xml:id="ns"></span>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "1");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "bogus");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "ns");
    }

    [Fact]
    public void Extract_Html_CapturesDataAndAriaAttributeNamesAsProperties()
    {
        var content = """
            <button DATA-TestId="save-button" data-user-id=42 aria-label="Save" aria-expanded></button>
            <section
              data-panel-state="open"
              aria-labelledby='panel-title'></section>
            <div title="data-fake=&quot;nope&quot; aria-hidden=&quot;true&quot;" id="real"></div>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "data-testid" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "data-user-id" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "aria-label" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "aria-expanded" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "data-panel-state" && s.Line == 3);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "aria-labelledby" && s.Line == 4);
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "data-fake");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "aria-hidden");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_CapturesExternalScriptAndLinkAsImports()
    {
        var content = """
            <link rel="stylesheet" href="style.css">
            <link rel="icon" href='/favicon.ico'>
            <script src="main.js"></script>
            <script type="module" src='/static/app.mjs'></script>
            <script>inline(); // no src — no import</script>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "style.css");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/favicon.ico");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "main.js");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/static/app.mjs");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "inline()");
    }

    [Fact]
    public void Extract_Html_CapturesCommonResourceTagsAsImports()
    {
        // HTML pages often embed navigable assets outside `<script>` / `<link>` too:
        // images, iframes, media, and embedded documents are all useful search targets.
        // Keep those resource-bearing attributes queryable so `definition` / `references`
        // can jump to the referenced path instead of only seeing the raw text chunk.
        // HTML ページは `<script>` / `<link>` 以外にも探索対象の資産を埋め込む。
        // 画像・iframe・メディア・埋め込み文書も検索できるようにして、
        // `definition` / `references` が raw text chunk ではなく参照先へ飛べるようにする。
        var content = """
            <img src="/images/logo.png" alt="logo">
            <img srcset="/images/logo-1x.png 1x, /images/logo-2x.png 2x" src="/images/logo.png" alt="logo">
            <iframe src="/docs/frame.html"></iframe>
            <video poster="/media/thumb.jpg">
              <source src="/media/movie.mp4" type="video/mp4">
              <source srcset="/media/movie-480.jpg 480w, /media/movie-960.jpg 960w">
            </video>
            <object data="/files/manual.pdf"></object>
            <svg xmlns:xlink="http://www.w3.org/1999/xlink"><use xlink:href="/icons.svg#check"></use></svg>
            <a href="/docs/readme.html">Read more</a>
            <area href="/docs/map.html">
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/images/logo.png");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/images/logo-1x.png");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/images/logo-2x.png");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/docs/frame.html");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/media/thumb.jpg");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/media/movie.mp4");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/media/movie-480.jpg");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/media/movie-960.jpg");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/files/manual.pdf");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/icons.svg#check");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/docs/readme.html");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/docs/map.html");
    }

    [Fact]
    public void Extract_Html_CapturesCustomWebComponentTagsAsClasses()
    {
        // Custom element tag names always contain a hyphen per the HTML spec.
        // HTML 仕様上、カスタム要素名には必ずハイフンが含まれる。
        var content = """
            <my-button>ok</my-button>
            <app-sidebar></app-sidebar>
            <div>plain</div>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "my-button");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app-sidebar");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "div");
    }

    [Fact]
    public void Extract_Html_CapturesSlotDeclarationsAndProjectionReferences()
    {
        var content = """
            <template id="card-template">
              <slot name="header">Untitled</slot>
              <slot></slot>
              <slot name='footer'><slot name="nested"></slot></slot>
            </template>
            <article>
              <h2 slot="header">Title</h2>
              <p>Default content</p>
              <span slot='footer'>Actions</span>
              <slot slot="footer" name="forwarded"></slot>
            </article>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "header");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "(default)");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "footer");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "nested");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "forwarded");
        Assert.Equal(2, symbols.Count(s => s.Kind == "reference" && s.Name == "footer"));
        Assert.Contains(symbols, s => s.Kind == "reference" && s.Name == "header");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "slot");
    }

    [Fact]
    public void Extract_Html_CapturesAllSymbolsOnSameLine()
    {
        // Minified HTML or a single line with multiple landmark-bearing tags must
        // produce one symbol per match, not only the winning pattern's first hit.
        // Closes #215 codex review blocker.
        // ミニファイされた HTML や 1 行に複数の landmark タグが入るケースでも、
        // 勝ちパターンの 1 件ではなく各マッチごとにシンボルが出る必要がある。
        var content = "<alpha-card id=\"first\"></alpha-card><beta-card id=\"second\"></beta-card>" +
            "<script src=\"a.js\"></script><link rel=\"stylesheet\" href=\"b.css\">";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "alpha-card");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "beta-card");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "first");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "second");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "a.js");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "b.css");
    }

    [Fact]
    public void Extract_Html_IgnoresSymbolsInsideComments()
    {
        // HTML comments must not produce phantom imports / classes / properties,
        // including multi-line comments. Closes #215 codex review blocker.
        // HTML コメント内のタグ類を phantom シンボルとして拾わないこと。複数行に
        // またがるコメントでも同様。
        var content = """
            <!-- <script src="commented.js"></script> -->
            <article id="real"></article>
            <!--
              <my-widget id="fake"></my-widget>
              <link rel="stylesheet" href="also-commented.css">
            -->
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "commented.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "also-commented.css");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "fake");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_IgnoresSymbolsInsideScriptAndStyleBodies()
    {
        // Inline <script> body content is raw text per the HTML spec and must not
        // leak symbols from template strings. <style> body text follows the same
        // raw-text rule. Closes #215 codex review blocker.
        // HTML 仕様上、<script> 本体は raw text であり、テンプレート文字列から
        // 疑似シンボルを漏らしてはいけない。<style> 本体も同じ raw text 規則。
        var content = """
            <script>
              const tpl = '<inline-card id="inline-id"></inline-card>';
            </script>
            <style>
              .rule { background: url('<bg-tag id="bg">'); }
            </style>
            <section id="visible"></section>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "inline-card");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "inline-id");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "bg-tag");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "bg");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "visible");
    }

    [Fact]
    public void Extract_Html_StillCapturesExternalScriptSrcEvenWhenBodyHasRawText()
    {
        // The masker must preserve the <script src="..."> opening tag so external
        // scripts are still indexed, while raw-text children stay masked.
        // raw-text の子要素はマスクしつつ、<script src="..."> 開始タグは保ち、
        // 外部 script が引き続き import として索引されることを固定する。
        var content = "<script src=\"app.js\">const x = '<evil-tag id=\"evil\"></evil-tag>';</script>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "app.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "evil-tag");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "evil");
    }

    [Fact]
    public void Extract_Html_CapturesMultiLineScriptAndLinkOpeningTags()
    {
        // Formatter-split opening tags must still match across lines so imports
        // are not silently dropped. Closes #215 codex review finding.
        // フォーマッタによって開始タグが改行されても、import を黙って落とさない
        // ようクロス行で一致する必要がある。#215 codex review 指摘への対応。
        var content = """
            <script
              type="module"
              src="/app.js"></script>
            <link
              rel="stylesheet"
              href="/app.css">
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.js");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.css");
    }

    [Fact]
    public void Extract_Html_IgnoresSymbolsInsideTextareaAndTitleBodies()
    {
        // <textarea> / <title> bodies are RCDATA per the HTML spec and their
        // contents must not leak phantom symbols. Closes #215 codex review finding.
        // <textarea> / <title> の本体は HTML 仕様上 RCDATA であり、疑似シンボルを
        // 漏らしてはならない。#215 codex review 指摘への対応。
        var content = """
            <textarea><my-widget id="fake"></my-widget></textarea>
            <title><bogus-tag id="phantom"></bogus-tag></title>
            <section id="real"></section>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "fake");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "bogus-tag");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_MasksUnclosedRawTextAndRcdataBodies()
    {
        // cdidx indexes the working tree, so unclosed <script> / <style> /
        // <textarea> / <title> are common mid-edit. Phantom symbols from those
        // unclosed bodies must not leak. Closes #215 codex review finding.
        // cdidx は working tree を対象にするため、編集途中で <script> / <style> /
        // <textarea> / <title> が未閉鎖な状態は普通に起きる。未閉鎖でも本体から
        // phantom シンボルを漏らしてはならない。#215 codex review 指摘への対応。
        var unclosedScript = "<script>const tpl = '<evil-card id=\"phantom\"></evil-card>';";
        var unclosedSymbols = SymbolExtractor.Extract(1, "html", unclosedScript);
        Assert.DoesNotContain(unclosedSymbols, s => s.Kind == "class" && s.Name == "evil-card");
        Assert.DoesNotContain(unclosedSymbols, s => s.Kind == "property" && s.Name == "phantom");

        var unclosedStyle = "<style>\n  .r { content: '<rogue-tag id=\"styleid\"></rogue-tag>'; }";
        var unclosedStyleSymbols = SymbolExtractor.Extract(1, "html", unclosedStyle);
        Assert.DoesNotContain(unclosedStyleSymbols, s => s.Kind == "class" && s.Name == "rogue-tag");
        Assert.DoesNotContain(unclosedStyleSymbols, s => s.Kind == "property" && s.Name == "styleid");

        var unclosedTextarea = "<textarea><my-widget id=\"taid\"></my-widget>";
        var unclosedTextareaSymbols = SymbolExtractor.Extract(1, "html", unclosedTextarea);
        Assert.DoesNotContain(unclosedTextareaSymbols, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.DoesNotContain(unclosedTextareaSymbols, s => s.Kind == "property" && s.Name == "taid");

        var unclosedTitle = "<title><bogus-tag id=\"titleid\"></bogus-tag>";
        var unclosedTitleSymbols = SymbolExtractor.Extract(1, "html", unclosedTitle);
        Assert.DoesNotContain(unclosedTitleSymbols, s => s.Kind == "class" && s.Name == "bogus-tag");
        Assert.DoesNotContain(unclosedTitleSymbols, s => s.Kind == "property" && s.Name == "titleid");
    }

    [Fact]
    public void Extract_Html_MultiLineOpeningTagReportsAttributeValueLine()
    {
        // When a `<script>` / `<link>` opening tag wraps across lines, the symbol's
        // line must point at the line that actually carries the attribute value,
        // not the opening `<`, so `definition` / `excerpt` jump to the right place.
        // Closes #215 codex review finding.
        // 開始タグが折り返された場合、symbol の行は開始 `<` の行ではなく属性値が
        // 実在する行を指す必要がある。こうしないと `definition` / `excerpt` の
        // ジャンプ先が先頭行にずれる。#215 codex review 指摘への対応。
        var content = """
            <script
              type="module"
              src="/app.js"></script>
            <link
              rel="stylesheet"
              href="/app.css">
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        var scriptImport = Assert.Single(symbols, s => s.Kind == "import" && s.Name == "/app.js");
        Assert.Equal(3, scriptImport.Line);
        Assert.Equal(3, scriptImport.StartLine);

        var linkImport = Assert.Single(symbols, s => s.Kind == "import" && s.Name == "/app.css");
        Assert.Equal(6, linkImport.Line);
        Assert.Equal(6, linkImport.StartLine);
    }

    [Fact]
    public void Extract_Html_IgnoresIdLiteralsInDocumentText()
    {
        // Prose like `<p>documentation says id="fake" here</p>` must not be
        // harvested as a DOM id — the `id=` pattern only matters inside an
        // opening tag. Real `<section id="real">` still captures.
        // Closes #215 codex review finding.
        // 本文中の `id="..."` を DOM id として誤抽出してはならない。id 属性は開始タグ
        // の内部でのみ意味を持つ。実在する `<section id="real">` は引き続き抽出する。
        // #215 codex review 指摘への対応。
        var content = """
            <p>documentation says id="fake" here</p>
            <article>inline prose id='phantom' mentioned</article>
            <section id="real"></section>
            """;

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "fake");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_MasksUnclosedComments()
    {
        // Unclosed `<!--` must also mask its suffix to EOF, matching the
        // raw-text / RCDATA `|\z` policy. Without this, editing a comment
        // live leaked every tag inside the unclosed comment as phantom
        // symbols. Closes #215 codex review finding.
        // `<!--` 未閉鎖でも EOF まで本体をマスクする必要がある。raw-text / RCDATA と
        // 同じく編集途中の未閉鎖コメントから phantom シンボルを漏らさないためのもの。
        // #215 codex review 指摘への対応。
        var content = "<!--\n<script src=\"commented.js\"></script>\n<custom-tag id=\"phantom\"></custom-tag>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "commented.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "custom-tag");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
    }

    [Fact]
    public void Extract_Html_CommentLiteralInsideScriptDoesNotSwallowFollowingTags()
    {
        // `<script>` bodies that literally contain `<!--` must not be treated
        // as unclosed comments. The raw-text masker has to run before the
        // comment masker or everything after the literal gets blanked out,
        // dropping every real subsequent symbol. Closes #215 codex review finding.
        // `<script>` 本文に `<!--` リテラルが入っているだけで未閉鎖コメント扱いに
        // してはならない。body マスクをコメントマスクより先に動かさないと、リテラル
        // 以降の本物のタグまで全滅する。#215 codex review 指摘への対応。
        var content = "<script>const s = \"<!--\";</script>\n<section id=\"real\"></section>\n<my-widget></my-widget>\n<link href=\"/app.css\">";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.css");
    }

    [Fact]
    public void Extract_Html_CapturesUnquotedAttributeValues()
    {
        // HTML5 allows unquoted attribute values. Dropping these silently meant
        // `<section id=real>`, `<script src=/app.js>`, `<link href=/app.css>`
        // all produced zero symbols. Closes #215 codex review finding.
        // HTML5 では引用符なし属性値も許される。黙って無視していた結果
        // `<section id=real>` / `<script src=/app.js>` / `<link href=/app.css>` が
        // いずれも 0 シンボルを返していた。#215 codex review 指摘への対応。
        var content = "<section id=real></section>\n<script src=/app.js></script>\n<link rel=stylesheet href=/app.css>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.js");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.css");
    }

    [Fact]
    public void Extract_Html_IgnoresAttributeLookAlikesInsideSameTagQuotedValues()
    {
        // The `<script src=...>` / `<link href=...>` / `id=...` regexes anchor at the real
        // outer tag so their `[^>]*?` prefix can reach forward into the same opening tag's
        // other attribute values. Without checking the name capture position, literals like
        // `data-note="src=evil.js"` on a real `<script>` leaked phantom imports. Pin that
        // the name-capture mask catches these same-tag leaks. Closes #215 codex review finding.
        // `<script src=...>` / `<link href=...>` / `id=...` の regex は実在する外側タグ
        // 起点で走り、`[^>]*?` が同じ開始タグ内の別属性値へ進めてしまう。開始 `<` だけで
        // 判定すると `<script data-note="src=evil.js">` のような同一タグ内の属性リテラル
        // から phantom import が漏れるので、name capture 位置で mask をかけ直し、それが
        // 同一タグ内の src=/href=/id= 文字列にも効くことを固定する。#215 codex review
        // 指摘への対応。
        var content = "<script data-note=\"src=evil.js\"></script>\n<link title=\"href=evil.css\" rel=stylesheet href=\"/real.css\">\n<div title=\"docs id=phantom\"></div>\n<section id=\"real\"></section>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "evil.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "evil.css");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/real.css");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_CapturesIdValuesWithPunctuation()
    {
        // Quoted id values can legally contain any non-whitespace character per the HTML5
        // id attribute spec. The previous `[\w:.\-]+` class silently dropped real DOM
        // anchors like `id="user@top"` and `id="group/main"`, so `definition` / `outline`
        // couldn't jump to them. Pin the broadened quoted class while keeping
        // unquoted values conservative (they still collide with CSS selector syntax).
        // Closes #215 codex review finding.
        // HTML5 では引用符付き id 値に任意の non-whitespace 文字が使える。従来の
        // `[\w:.\-]+` クラスだと `id="user@top"` / `id="group/main"` のような実在の
        // DOM アンカーを黙って落としていた。引用符付きは受け入れを広げつつ、
        // 引用符なしは CSS セレクタ構文との衝突を避けて保守的なままにする。
        // #215 codex review 指摘への対応。
        var content = "<section id=\"user@top\"></section>\n<section id=\"group/main\"></section>\n<section id=\"plain.id\"></section>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "user@top");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "group/main");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "plain.id");
    }

    [Fact]
    public void Extract_Html_QuotedGtInsideScriptOpenTagPreservesSiblingSymbols()
    {
        // A `>` character is legal inside a quoted attribute value, so the raw-text
        // body masker must parse the `<script>` opening tag with quote awareness.
        // The earlier `[^>]*>` class terminated at the first quoted `>` and blanked
        // every following real attribute and sibling tag as masked body content,
        // which dropped both the intended `src="/app.js"` import and the sibling
        // `<section id="real">`. Closes #215 codex review blocker.
        // 引用符付き属性値内の `>` でも raw-text 本体マスクの開始タグ解析が終端しない
        // ことを固定する。以前の `[^>]*>` は先頭の引用符内 `>` でタグを切ってしまい、
        // 後続の実属性と兄弟タグを body としてマスクしていたため、本来 emit すべき
        // `src="/app.js"` の import と `<section id="real">` の両方を落としていた。
        // #215 codex review blocker 対応。
        var content = "<script data-note=\"a > b\" src=\"/app.js\"></script>\n<section id=\"real\"></section>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.js");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_UnterminatedQuotedAttributeDoesNotSwallowRestOfFile()
    {
        // Mid-edit working-tree content commonly leaves a quoted attribute unterminated
        // (e.g. user is still typing `title="...`). When no valid-looking close exists
        // to EOF, the parser must bound damage by bailing at the current line rather
        // than scanning through every subsequent sibling tag looking for a matching
        // quote. Otherwise every `<my-widget>` / `<link href=...>` after the broken
        // tag drops out of `symbols` / `definition` / `outline` until the user types
        // the matching quote. Closes #215 codex review finding.
        // 編集中の working tree では `title="...` のような未閉鎖引用符が頻発する。
        // EOF まで妥当な閉じ候補が無い真の未終端では、行末で止めて以降の
        // `<my-widget>` / `<link href=...>` を symbols / definition / outline から
        // 消さないこと。#215 codex review 指摘対応。
        var content = "<div title=\"oops\n<my-widget></my-widget>\n<link href=/app.css>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.css");
    }

    [Fact]
    public void Extract_Html_MultiLineQuotedAttributeValueWithEmbeddedTagsPreservesSiblingAttributes()
    {
        // HTML5 allows newlines AND tag-like content inside quoted attribute values
        // (`<div title="line1\n<section></section>\nline3" id="real">`). The earlier
        // `\n<tagstart>` bail heuristic treated any `\n<` inside a quoted value as an
        // unterminated-quote signal, which silently prematurely terminated valid
        // multi-line title / data-note / alt values and either (1) leaked embedded
        // `<section id=phantom>` as a phantom `property phantom` symbol, or (2)
        // dropped the genuine `id="real"` attribute that followed the value. Pin
        // that valid multi-line quoted values containing tag-like content are
        // treated as single attribute values, and the following `id="real"` is
        // emitted. Closes #215 codex review #9 blocker 2.
        // HTML5 は引用符付き属性値の中に改行もタグ様テキストも許容する
        // (`<div title="line1\n<section></section>\nline3" id="real">`)。以前の
        // `\n<tagstart>` 早期中断ヒューリスティクスは、これを未終端と誤認して
        // (1) 埋め込まれた `<section id=phantom>` を phantom な property として拾ったり、
        // (2) 後続する本物の `id="real"` を落としたりしていた。妥当な複数行引用属性値を
        // 正しく 1 つの値として扱い、後続の `id="real"` を emit することを固定する。
        // #215 codex review #9 blocker 2 対応。
        var content = "<div title=\"line1\n<section id=phantom></section>\nline3\" id=\"real\"></div>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
    }

    [Fact]
    public void Extract_Html_RawTextOpenerInsideQuotedAttributeValueDoesNotMaskFollowingContent()
    {
        // `<script>` / `<style>` / `<textarea>` / `<title>` embedded inside another
        // tag's quoted attribute value is NOT a real raw-text opener. The mask pass
        // must walk past the outer tag's quoted value rather than re-encountering
        // `<script>` / `<!--` inside the value and masking through EOF. Pin that
        // quoted `<script>` / `<!--` does not swallow sibling tags on following lines.
        // Closes #215 codex review #9 blocker 1.
        // 引用符付き属性値内に出てくる `<script>` / `<style>` / `<textarea>` / `<title>`
        // や `<!--` は raw-text 開始でもコメント開始でもない。マスク処理は外側タグの
        // 引用符付き値を飛ばして進まなければならず、さもないと値内の `<script>` を
        // raw-text 開始と誤認して EOF までマスクし、後続の兄弟タグを全部落とす。
        // #215 codex review #9 blocker 1 対応。
        var scriptInAttr = "<div title=\"<script>\">ok</div>\n<section id=\"real\"></section>";
        var symbols1 = SymbolExtractor.Extract(1, "html", scriptInAttr);
        Assert.Contains(symbols1, s => s.Kind == "property" && s.Name == "real");

        var commentInAttr = "<div title=\"<!--\">ok</div>\n<section id=\"realc\"></section>";
        var symbols2 = SymbolExtractor.Extract(1, "html", commentInAttr);
        Assert.Contains(symbols2, s => s.Kind == "property" && s.Name == "realc");
    }

    [Fact]
    public void Extract_Html_SelfClosingVoidElementDoesNotDropSiblingAttributes()
    {
        // XHTML / formatter-wrapped HTML often writes void elements as
        // `<link href="/app.css"/>`. Previously the closing `"` of the path-
        // like attribute had post-context `/` which was NOT accepted as a
        // strong close, and the nested-attribute fallback then mis-identified
        // `id="real"` on the following sibling tag as a nested opener, causing
        // FindHtmlQuoteClose to return -1 and the attribute parser to bail,
        // dropping BOTH the `href` import and the sibling `id`. The self-
        // closing shape `"/>` is now accepted as a strong post-context.
        // Closes #215 codex review #11 Blocker 1.
        // XHTML / フォーマッタ折り返しの HTML では `<link href="/app.css"/>` の
        // 形を採ることがある。以前は閉じ `"` 直後が `/` のため strong でなく、
        // 後続の `id="real"` を nested 属性と誤認して -1 → 行末 bail となり、
        // `href` import も兄弟 `id` も落としていた。`"/>` を strong として
        // 受理することで self-closing void 要素も後続も正しく拾う。
        // #215 codex review #11 Blocker 1 対応。
        var content = "<link href=\"/app.css\"/><section id=\"real\"></section>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.css");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_CdataAndProcessingInstructionContentsAreNotLeakedAsSymbols()
    {
        // XHTML / SVG / MathML content can contain `<![CDATA[...]]>` sections
        // whose body is text, not markup. The old `<!`-branch in the extractor
        // stopped at the first `>` which is often inside an inner element of
        // the CDATA body, so the remainder was parsed as real HTML and leaked
        // phantom tags / properties. Processing instructions `<?...?>` and
        // DOCTYPE declarations have the same shape. Pin that CDATA / PI /
        // DOCTYPE bodies do NOT emit symbols and that siblings after them
        // still do. Closes #215 codex review #11 Blocker 2.
        // XHTML / SVG / MathML の `<![CDATA[...]]>` 本体はマークアップではなく
        // テキスト。旧実装は最初の `>` で終端扱いしたため、内部要素の `>` で
        // 早期終了して残り本体が real HTML として抽出され phantom を漏らして
        // いた。`<?...?>` や `<!DOCTYPE...>` も同様。CDATA / PI / DOCTYPE 本体
        // は emit せず、それらの後続兄弟が emit されることを固定する。
        // #215 codex review #11 Blocker 2 対応。
        var cdata = "<![CDATA[ <two-widget id=\"phantom\"></two-widget><three-widget id=\"ghost\"></three-widget> ]]><section id=\"real\"></section>";
        var symbolsCdata = SymbolExtractor.Extract(1, "html", cdata);
        Assert.DoesNotContain(symbolsCdata, s => s.Name == "two-widget");
        Assert.DoesNotContain(symbolsCdata, s => s.Name == "three-widget");
        Assert.DoesNotContain(symbolsCdata, s => s.Name == "phantom");
        Assert.DoesNotContain(symbolsCdata, s => s.Name == "ghost");
        Assert.Contains(symbolsCdata, s => s.Kind == "property" && s.Name == "real");

        var pi = "<?xml version=\"1.0\"?><?xml-stylesheet href=\"/evil.css\"?><section id=\"realpi\"></section>";
        var symbolsPi = SymbolExtractor.Extract(1, "html", pi);
        Assert.DoesNotContain(symbolsPi, s => s.Kind == "import" && s.Name == "/evil.css");
        Assert.Contains(symbolsPi, s => s.Kind == "property" && s.Name == "realpi");

        var doctype = "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\"><section id=\"realdt\"></section>";
        var symbolsDt = SymbolExtractor.Extract(1, "html", doctype);
        Assert.Contains(symbolsDt, s => s.Kind == "property" && s.Name == "realdt");
    }

    [Fact]
    public void Extract_Html_UnterminatedOuterTagWithEmbeddedRawTextOpenerDoesNotMaskToEof()
    {
        // codex review #10 Blocker B: when the outer non-raw-text tag is itself
        // mid-edit and never closes (no matching `"` anywhere), the mask pass
        // must NOT re-enter the raw-text / comment branch at the `<!--` /
        // `<script>` sitting inside the broken quoted value, because doing so
        // masks through EOF and drops every sibling tag on the following lines.
        // Advancing past the current line when a non-raw-text opener cannot be
        // closed lets the later sibling tags still be walked. Closes #215 codex
        // review #10 Blocker B.
        // 外側の non-raw-text タグ自体が編集途中で EOF まで `"` が現れない場合、
        // 破損した引用値の中の `<!--` / `<script>` を comment / raw-text 開始と
        // 再解釈して EOF までマスクしてはならない。未終端 opener は現在行で
        // 止めて次行以降の兄弟タグを拾う。#215 codex review #10 Blocker B 対応。
        var commentOpenerInBrokenTag = "<div title=\"<!--\n<my-widget></my-widget>\n<link href=/app.css>";
        var symbols1 = SymbolExtractor.Extract(1, "html", commentOpenerInBrokenTag);
        Assert.Contains(symbols1, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.Contains(symbols1, s => s.Kind == "import" && s.Name == "/app.css");

        var scriptOpenerInBrokenTag = "<div title=\"<script>\n<my-widget></my-widget>\n<link href=/app.css>";
        var symbols2 = SymbolExtractor.Extract(1, "html", scriptOpenerInBrokenTag);
        Assert.Contains(symbols2, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.Contains(symbols2, s => s.Kind == "import" && s.Name == "/app.css");
    }

    [Fact]
    public void Extract_Html_IgnoresNativeHyphenatedSvgAndMathmlTags()
    {
        // HTML/SVG/MathML have a small set of native hyphenated tag names
        // (`<font-face>`, `<color-profile>`, `<missing-glyph>`, `<annotation-xml>`).
        // Per the HTML spec these are reserved and must NOT be treated as custom
        // elements; otherwise any project with inline SVG / MathML gets phantom
        // `class` symbols. Pin that genuine custom elements next to reserved tags
        // are still captured. Closes #215 codex review finding.
        // HTML / SVG / MathML にはハイフン付きだが仕様で予約された標準タグ（`<font-face>`
        // / `<color-profile>` / `<missing-glyph>` / `<annotation-xml>`）が存在する。
        // これらを custom element 扱いしないこと、および同居する本物のカスタム要素は
        // 引き続き class として拾うことを固定する。#215 codex review 指摘対応。
        var content = "<svg><font-face></font-face><color-profile></color-profile><missing-glyph></missing-glyph><my-widget></my-widget></svg>\n<math><annotation-xml></annotation-xml></math>\n<app-sidebar></app-sidebar>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "font-face");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "color-profile");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "missing-glyph");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "annotation-xml");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "my-widget");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "app-sidebar");
    }

    [Fact]
    public void Extract_Html_MultiLineQuotedAttributeValuePreservesFollowingAttributes()
    {
        // HTML5 allows newlines inside quoted attribute values. Formatter-wrapped
        // tags and verbose `title` / `alt` / `data-note` copy often span multiple
        // lines. The state machine must NOT abort tag parsing at the first
        // newline inside a quoted value — otherwise it silently drops sibling
        // `src=` / `href=` / `id=` attributes on the same tag. Closes #215
        // codex review #8 blocker.
        // HTML5 は引用符付き属性値の中に改行を許容する。フォーマッタによる折り返し
        // タグや長文の `title` / `alt` / `data-note` 等は複数行に跨ることがある。
        // state machine は改行で属性解析を中断してはならない — さもないと同一タグ内の
        // `src=` / `href=` / `id=` が兄弟属性として silent に落ちる。#215 codex
        // review #8 blocker 対応。
        var content = "<div title=\"line1\nline2\" id=\"real\">text</div>\n<link data-note=\"line1\nline2\" href=\"/app.css\">\n<script data-note=\"line1\nline2\" src=\"/app.js\"></script>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "real");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.css");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "/app.js");
    }

    [Fact]
    public void Extract_Html_UnterminatedQuoteInRawTextOpenerDoesNotLeakScriptBodySymbols()
    {
        // Mid-edit `<script>` / `<style>` / `<textarea>` / `<title>` openers with
        // an unterminated quoted attribute MUST still have their body masked,
        // otherwise the state machine walks into what should be raw-text /
        // RCDATA content and emits phantom `class` / `property` / `import`
        // symbols from embedded template-string markup. The mask falls back to
        // EOF when the opener cannot be closed, matching HTML's raw-text spec
        // behavior. Closes #215 codex review #8 blocker.
        // 編集中の `<script>` 等の開始タグで引用符が未終端の場合も、本体は必ず
        // マスクされなければならない。さもないと state machine が raw-text / RCDATA
        // 本体に入り込み、埋め込まれたテンプレート文字列のタグ風テキストから phantom
        // シンボルを漏らす。未終端時は仕様どおり EOF までマスクする。#215 codex
        // review #8 blocker 対応。
        var content = "<script data-note=\"oops\nconst tpl = '<evil-card id=\"phantom\"></evil-card>';\n<section id=\"real\"></section>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "evil-card");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
        // The `<section id="real">` after the unterminated `<script>` is inside
        // the unclosed raw-text body per spec, so it is intentionally NOT
        // emitted — this matches how a browser would treat the content.
        // 仕様上 `<section id="real">` も未閉鎖 raw-text の中なので、ブラウザと同じく
        // emit しないのが正しい。
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "real");
    }

    [Fact]
    public void Extract_Html_IgnoresSymbolsNestedInsideQuotedAttributeValues()
    {
        // Tag-looking text embedded inside a quoted attribute value (commonly in
        // doc generators, Markdown-to-HTML output, or `title="..."` blurbs) must
        // not produce phantom custom-element, id, src, or href symbols. Closes
        // #215 codex review finding.
        // 引用符付き属性値の中に入ったタグ風テキスト（ドキュメント生成器や
        // `title="..."` の注釈によくある）から、phantom な custom element / id /
        // src / href を拾ってはならない。#215 codex review 指摘への対応。
        var content = "<div title=\"<fake-widget>\" data-doc=\"<section id=phantom></section>\" aria-label=\"<script src=/evil.js></script><link href=/evil.css>\"></div>";

        var symbols = SymbolExtractor.Extract(1, "html", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "fake-widget");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "phantom");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "/evil.js");
        Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "/evil.css");
    }


    [Fact]
    public void Extract_RustRawString_DoesNotLeakPhantomSymbols()
    {
        // Regression for issue #291: code-shaped fixture text inside r#"..."# /
        // r##"..."## raw strings must not produce phantom fn/struct rows.
        // issue #291 回帰: r#"..."# / r##"..."## raw string 内のコード風フィクスチャ
        // テキストは phantom の fn/struct を生成してはならない。
        const string content = "const BASIC: &str = r#\"\n"
            + "fn fake_basic() {}\n"
            + "struct FakeStructBasic;\n"
            + "\"#;\n"
            + "const NESTED: &str = r##\"\n"
            + "contains \"# marker\n"
            + "fn fake_nested() {}\n"
            + "\"##;\n"
            + "fn real_fn() {}\n"
            + "struct RealStruct;\n";

        var symbols = SymbolExtractor.Extract(1, "rust", content);

        Assert.DoesNotContain(symbols, s => s.Name == "fake_basic");
        Assert.DoesNotContain(symbols, s => s.Name == "FakeStructBasic");
        Assert.DoesNotContain(symbols, s => s.Name == "fake_nested");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "real_fn");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "RealStruct");
    }

    [Fact]
    public void Extract_JsTsTemplateLiteral_DoesNotLeakPhantomSymbols()
    {
        // Regression for issue #291: code-shaped fixture text inside multi-line
        // JavaScript/TypeScript `...` template literal bodies must not produce
        // phantom function/class rows. Interpolation hole contents remain visible
        // to downstream reference extraction (covered in ReferenceExtractor tests).
        // issue #291 回帰: 複数行 JavaScript/TypeScript `...` テンプレートリテラル本体の
        // コード風テキストは phantom の function/class を生成してはならない。
        const string content = """
            const src = `
            function fakeFromTemplate() {
              return 1;
            }
            class FakeClassInTemplate {}
            `;

            function realFunction() {}
            class RealClass {}
            """;

        var jsSymbols = SymbolExtractor.Extract(1, "javascript", content);
        Assert.DoesNotContain(jsSymbols, s => s.Name == "fakeFromTemplate");
        Assert.DoesNotContain(jsSymbols, s => s.Name == "FakeClassInTemplate");
        Assert.Contains(jsSymbols, s => s.Kind == "function" && s.Name == "realFunction");
        Assert.Contains(jsSymbols, s => s.Kind == "class" && s.Name == "RealClass");

        var tsSymbols = SymbolExtractor.Extract(1, "typescript", content);
        Assert.DoesNotContain(tsSymbols, s => s.Name == "fakeFromTemplate");
        Assert.DoesNotContain(tsSymbols, s => s.Name == "FakeClassInTemplate");
        Assert.Contains(tsSymbols, s => s.Kind == "function" && s.Name == "realFunction");
        Assert.Contains(tsSymbols, s => s.Kind == "class" && s.Name == "RealClass");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_CrlfTemplateLiteralKeepsColumnsAligned(string language)
    {
        // Regression for issue #1465: CRLF input is normalized before JS/TS lexing, so a
        // multi-line template literal must not shift columns for later real symbols.
        // issue #1465 の回帰: JS/TS lexing 前に CRLF 入力を正規化するため、複数行
        // template literal が後続の本物のシンボル列をずらしてはならない。
        const string content = """
            const src = `
            class FakeClassInTemplate {}
            function fakeFromTemplate() {}
            `;

            export function realFunction() {
              return src;
            }
            """;
        var lfContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlfContent = lfContent.Replace("\n", "\r\n", StringComparison.Ordinal);

        var lfSymbols = SymbolExtractor.Extract(1, language, lfContent);
        var crlfSymbols = SymbolExtractor.Extract(1, language, crlfContent);

        var lfRealFunction = Assert.Single(lfSymbols, s => s.Kind == "function" && s.Name == "realFunction");
        var crlfRealFunction = Assert.Single(crlfSymbols, s => s.Kind == "function" && s.Name == "realFunction");
        Assert.Equal(lfRealFunction.Line, crlfRealFunction.Line);
        Assert.Equal(lfRealFunction.StartColumn, crlfRealFunction.StartColumn);
        Assert.Equal(lfRealFunction.Signature, crlfRealFunction.Signature);

        Assert.DoesNotContain(crlfSymbols, s => s.Name == "fakeFromTemplate");
        Assert.DoesNotContain(crlfSymbols, s => s.Name == "FakeClassInTemplate");
    }








    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowFunction()
    {
        var content = """
            class Foo {
                handleClick = () => { };
                handleHover = (e) => { return e; };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var handleClick = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "handleClick");
        Assert.NotNull(handleClick);
        Assert.Equal("class", handleClick.ContainerKind);
        Assert.Equal("Foo", handleClick.ContainerName);

        var handleHover = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "handleHover");
        Assert.NotNull(handleHover);
        Assert.Equal("class", handleHover.ContainerKind);
        Assert.Equal("Foo", handleHover.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowFunctionWithTypes()
    {
        var content = """
            class Foo {
                handleClick = (): void => { };
                transform = <T>(x: T): T => { return x; };
                count: number = 0;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var handleClick = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "handleClick");
        Assert.NotNull(handleClick);
        Assert.Equal("class", handleClick.ContainerKind);
        Assert.Equal("Foo", handleClick.ContainerName);
        Assert.Equal("void", handleClick.ReturnType);

        var transform = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "transform");
        Assert.NotNull(transform);
        Assert.Equal("T", transform.ReturnType);

        // Plain value field (no arrow) must not be mis-classified as a function.
        // 素の値フィールド（アロー関数ではない）は function として検出してはならない。
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "count");
    }

    [Fact]
    public void Extract_TypeScript_DetectsAutoAccessorWithInitializer()
    {
        var content = """
            class Foo {
                accessor theme: string = "dark";
                accessor count = 1;
                handleClick = () => { return this.count; };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var theme = symbols.FirstOrDefault(s => s.Kind == "property" && s.Name == "theme");
        Assert.NotNull(theme);
        Assert.Equal("class", theme.ContainerKind);
        Assert.Equal("Foo", theme.ContainerName);
        Assert.Equal("string", theme.ReturnType);

        var count = symbols.FirstOrDefault(s => s.Kind == "property" && s.Name == "count");
        Assert.NotNull(count);
        Assert.Equal("class", count.ContainerKind);
        Assert.Equal("Foo", count.ContainerName);
        Assert.Null(count.ReturnType);

        var handleClick = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "handleClick");
        Assert.NotNull(handleClick);
        Assert.Equal("class", handleClick.ContainerKind);
        Assert.Equal("Foo", handleClick.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowWithExpressionBody()
    {
        var content = """
            class Foo {
                handleExpr = () => 42;
                compute = (x) => x + 1;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var handleExpr = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "handleExpr");
        Assert.NotNull(handleExpr);
        Assert.Equal("class", handleExpr.ContainerKind);
        Assert.Equal("Foo", handleExpr.ContainerName);

        var compute = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "compute");
        Assert.NotNull(compute);
        Assert.Equal("class", compute.ContainerKind);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowWithMultiLineExpressionBody()
    {
        // Regression for multi-line expression body causing scanner to skip the next field.
        // 複数行の式本体が次の field をスキップさせる回帰の回帰テスト。
        var content = """
            class Foo {
                handleExpr = (): number => 42;
                transform = <T>(x: T): T =>
                    x;
                runInline = (a: number, b: number): number => a + b;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var handleExpr = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "handleExpr");
        Assert.NotNull(handleExpr);
        Assert.Equal("class", handleExpr.ContainerKind);
        Assert.Equal("number", handleExpr.ReturnType);

        var transform = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "transform");
        Assert.NotNull(transform);
        Assert.Equal("T", transform.ReturnType);

        // runInline must still be captured after transform's multi-line expression body.
        // transform の複数行式本体の後でも runInline は取りこぼされてはならない。
        var runInline = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "runInline");
        Assert.NotNull(runInline);
        Assert.Equal("class", runInline.ContainerKind);
        Assert.Equal("number", runInline.ReturnType);
    }

    [Fact]
    public void Extract_JavaScript_DoesNotMisclassifyPlainClassFieldAsFunction()
    {
        var content = """
            class Foo {
                value = 42;
                items = [1, 2, 3];
                config = { key: "value" };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "value");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "items");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "config");
    }

    [Fact]
    public void Extract_JavaScript_DetectsTopLevelGeneratorFunctions()
    {
        var content = """
            function* regularGen() { yield 1; }
            async function* asyncGen() { yield 1; }
            function* spacedGen () { yield 1; }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "regularGen");
        Assert.Contains(symbols, s => s.Kind == "async_generator" && s.Name == "asyncGen");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "spacedGen");
    }

    [Fact]
    public void Extract_TypeScript_DetectsTopLevelGeneratorFunctions()
    {
        var content = """
            function* regularGen(): Generator<number> { yield 1; }
            async function* asyncGen(): AsyncGenerator<number> { yield 1; }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var regular = symbols.FirstOrDefault(s => s.Kind == "generator" && s.Name == "regularGen");
        Assert.NotNull(regular);

        var asyncGen = symbols.FirstOrDefault(s => s.Kind == "async_generator" && s.Name == "asyncGen");
        Assert.NotNull(asyncGen);
    }

    [Fact]
    public void Extract_JavaScript_DetectsObjectLiteralMethodShorthand()
    {
        var content = """
            const obj = {
                get foo() { return 1; },
                set foo(v) { },
                *bar() { yield 1; },
                async baz() { return 1; },
                qux() { return 1; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var getFoo = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "foo" && s.Line == 2);
        Assert.NotNull(getFoo);
        Assert.Equal("object", getFoo.ContainerKind);
        Assert.Equal("obj", getFoo.ContainerName);

        var setFoo = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "foo" && s.Line == 3);
        Assert.NotNull(setFoo);
        Assert.Equal("object", setFoo.ContainerKind);

        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "bar" && s.ContainerKind == "object");
        Assert.Contains(symbols, s => s.Kind == "async_function" && s.Name == "baz" && s.ContainerKind == "object");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "qux" && s.ContainerKind == "object");
    }

    [Fact]
    public void Extract_TypeScript_DetectsObjectLiteralMethodShorthand()
    {
        var content = """
            const obj = {
                get foo(): number { return 1; },
                set foo(v: number) { },
                *bar(): Generator<number> { yield 1; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "obj");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "obj");
    }

    [Fact]
    public void Extract_JavaScript_DetectsModuleExportsObjectLiteralMembers()
    {
        var content = """
            module.exports = {
                run() { return 1; },
                *gen() { yield 1; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "run" && s.ContainerKind == "object");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "gen" && s.ContainerKind == "object");
    }

    [Fact]
    public void Extract_TypeScript_DoesNotEmitObjectLiteralMembersInBlockScopeOrNonExportedNamespace()
    {
        // Non-exported bindings in block scope or namespace scope should be filtered out,
        // matching the scope-filter parity already applied to other JS/TS capture paths.
        // block scope や namespace 内の非 export バインディングは、他の JS/TS 抽出経路と
        // 同じスコープフィルタに合わせて除外されること。
        var content = """
            if (Math.random() > 0.5) {
              const blockScoped = {
                run() { return 1; },
              };
            }

            namespace N {
              const hidden = {
                run() { return 1; },
              };
              export const shown = {
                ok() { return 2; },
              };
            }

            export const topLevel = {
              fn() { return 3; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "run");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ok" && s.ContainerKind == "object");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "fn" && s.ContainerKind == "object");
    }

    [Fact]
    public void Extract_JavaScript_DoesNotEmitObjectLiteralMembersForPlainValues()
    {
        var content = """
            const obj = {
                key: "value",
                count: 42,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "key");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "count");
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowWithAsiBetweenFields()
    {
        // ASI (Automatic Semicolon Insertion) between class fields must not swallow
        // the next arrow-property header into the previous expression body.
        // クラスフィールド間の ASI により式本体の後続フィールドを取りこぼさないこと。
        var content = """
            class Foo {
                first = () => 42
                second = () => 43
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Foo", first.ContainerName);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("Foo", second.ContainerName);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowWithAsiBetweenFields()
    {
        var content = """
            class Foo {
                first = (): number => 42
                second = (x: number): number => x + 1
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("number", first.ReturnType);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("class", second.ContainerKind);
        Assert.Equal("number", second.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowAsiBeforeClosingBrace()
    {
        // Single field without trailing `;` followed by the class-closing `}` must
        // still be captured; ASI at `}` terminates the expression body.
        // セミコロンなしの単一 field が直後の class 終了 `}` で終端されるケース。
        var content = """
            class Foo {
                only = (): number => 7
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var only = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "only");
        Assert.NotNull(only);
        Assert.Equal("class", only.ContainerKind);
        Assert.Equal("Foo", only.ContainerName);
        Assert.Equal("number", only.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultiLineObjectLiteralBinding()
    {
        // The `{` may sit on a line after the `=` binding; collector must thread
        // the lex state across lines to find the open brace.
        // `{` が `=` バインディングと別行にあっても、collector は lex 状態を
        // 跨いで open brace を検出できること。
        var content = """
            const obj =
            {
                foo() { return 1; },
                *bar() { yield 1; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "obj");
        Assert.Contains(symbols, s => s.Kind == "generator" && s.Name == "bar" && s.ContainerKind == "object" && s.ContainerName == "obj");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportedObjectLiteralAliasProperties()
    {
        var content = """
            const foo = 1;
            function inner() { return 3; }
            function named() { return 4; }
            const answer = 42;
            module.exports = { foo, alias: inner, named, method() {} };
            export default { answer };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "foo" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alias" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "named" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "method" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "answer" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "inner" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("typescript")]
    public void Extract_JavaScriptTypeScript_DetectsExportedObjectLiteralLiteralKeys(string language)
    {
        var content = """
            const handler = () => 1;
            const notFound = () => 2;
            const dynamicKey = "runtime";
            module.exports = {
                "x-api": handler,
                'content-type': handler,
                404: notFound,
                ["computed-api"]: handler,
                [500]: notFound,
                [dynamicKey]: handler,
            };
            export default {
                "dash-key": handler,
                ["computed-dash"]: handler,
            };
            """;
        var symbols = SymbolExtractor.Extract(1, language, content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "x-api" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "content-type" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "404" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "computed-api" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "500" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "dash-key" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "computed-dash" && s.ContainerKind == "object" && s.ContainerName == "default");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "dynamicKey" && s.ContainerKind == "object" && s.ContainerName == "module.exports");
    }

    [Fact]
    public void Extract_TypeScript_DetectsMultiLineDestructuredNamedExports()
    {
        var content = """
            const cfg = {} as Config;
            export const {
                alpha,
                renamed: beta,
            }: Pick<Config, "alpha" | "renamed"> = cfg;
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "alpha" && s.Visibility == "export");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "beta" && s.Visibility == "export");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "renamed");
    }

    [Fact]
    public void Extract_TypeScript_DetectsExportDefaultObjectLiteralMembers()
    {
        // `export default { ... }` is a common module-shape; its shorthand members
        // should be captured with container_name == "default".
        // `export default { ... }` のショートハンドメンバは container_name == "default"
        // として抽出されること。
        var content = """
            export default {
                foo() { return 1; },
                async bar() { return 2; },
            };
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var foo = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "foo");
        Assert.NotNull(foo);
        Assert.Equal("object", foo.ContainerKind);
        Assert.Equal("default", foo.ContainerName);

        var bar = symbols.FirstOrDefault(s => s.Kind == "async_function" && s.Name == "bar");
        Assert.NotNull(bar);
        Assert.Equal("object", bar.ContainerKind);
        Assert.Equal("default", bar.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowComputedMemberContinuation()
    {
        // A bare `[` on the next line is `foo[bar]` member-access continuation per JS ASI rules,
        // NOT a new computed class method. The scanner must not cut the expression body at `foo`.
        // JS の ASI 規則では、次行頭の `[` は `foo[bar]` メンバアクセスの継続であり、
        // computed method 名の開始ではない。式本体を `foo` で打ち切ってはならない。
        var content = """
            class Foo {
              first = () => foo
                [bar];
              second = () => 43;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Equal("Foo", first.ContainerName);
        Assert.Contains("[bar]", first.Signature);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("class", second.ContainerKind);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowComputedMemberContinuation()
    {
        var content = """
            class Foo {
              first = (): unknown => foo
                [bar];
              second = (): number => 43;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("class", first.ContainerKind);
        Assert.Contains("[bar]", first.Signature);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("number", second.ReturnType);
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowStringLiteralBeforeClosingBrace()
    {
        // A string-returning arrow without a trailing `;` must be terminated by the class-body `}`.
        // The lexer preserves opening/closing quote characters in the sanitized header, so the
        // ASI terminator check must treat `"` / `'` / `` ` `` as valid expression ends.
        // セミコロンなしで文字列を返す矢印フィールドは、直後のクラス終了 `}` で終端されなければならない。
        // lexer は開閉クォートを sanitized header 上に残すため、ASI 終端チェックは
        // `"` / `'` / `` ` `` を有効な式終端として扱わなければならない。
        var content = """
            class Foo {
              only = () => "x"
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        var only = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "only");
        Assert.NotNull(only);
        Assert.Equal("class", only.ContainerKind);
        Assert.Equal("Foo", only.ContainerName);
    }

    [Fact]
    public void Extract_JavaScript_DetectsClassFieldArrowStringLiteralWithAsiBetweenFields()
    {
        var content = """
            class Foo {
              first = () => "x"
              second = () => 43
              third = () => `template`
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "javascript", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "first" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "second" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "third" && s.ContainerName == "Foo");
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowStringLiteralBeforeClosingBrace()
    {
        var content = """
            class Foo {
              only = (): string => "x"
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var only = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "only");
        Assert.NotNull(only);
        Assert.Equal("class", only.ContainerKind);
        Assert.Equal("string", only.ReturnType);
    }

    [Fact]
    public void Extract_TypeScript_DetectsClassFieldArrowStringLiteralWithAsiBetweenFields()
    {
        var content = """
            class Foo {
              first = (): string => "x"
              second = (): number => 43
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "typescript", content);

        var first = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "first");
        Assert.NotNull(first);
        Assert.Equal("string", first.ReturnType);

        var second = symbols.FirstOrDefault(s => s.Kind == "function" && s.Name == "second");
        Assert.NotNull(second);
        Assert.Equal("number", second.ReturnType);

    }




    [Fact]
    public void Extract_NullContent_ReturnsEmpty()
    {
        // Direct callers that pass `null` must not throw. The #183 CRLF-normalization
        // step added ahead of StripLineLeadingInvisibles would otherwise dereference `null`
        // before the helper's IsNullOrEmpty guard could run. Closes #183.
        // direct call で `null` を渡してもスローしない。#183 で StripLineLeadingInvisibles
        // の前段に CRLF 正規化を入れたため、helper 側 IsNullOrEmpty まで届かず
        // `null` を逆参照してしまう回帰を防ぐ。Closes #183.
        Assert.Empty(SymbolExtractor.Extract(1, "csharp", null!));
    }

    [Fact]
    public void Extract_EmptyContent_ReturnsEmpty()
    {
        // Empty content returns no symbols and does not throw. Closes #183.
        // 空入力はシンボル 0 個で、例外にならない。Closes #183.
        Assert.Empty(SymbolExtractor.Extract(1, "csharp", string.Empty));
    }






    [Fact]
    public void Extract_Java_SameLineAnnotationsCompactConstructorsAndEnumOverrides_StayIndexed()
    {
        const string content = """
            package com.example;

            public class Same {
                @Override public String toString() { return "x"; }
                @Deprecated public int legacy() { return 0; }
            }

            public enum Op {
                ADD {
                    @Override public int apply(int a, int b) { return a + b; }
                },
                SUB {
                    @Override public int apply(int a, int b) { return a - b; }
                };
                public abstract int apply(int a, int b);
            }

            public record Range(int low, int high) {
                public Range {
                    if (low > high) throw new IllegalArgumentException();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        var toString = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "toString"));
        Assert.Equal("class", toString.ContainerKind);
        Assert.Equal("Same", toString.ContainerName);

        var legacy = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "legacy"));
        Assert.Equal("class", legacy.ContainerKind);
        Assert.Equal("Same", legacy.ContainerName);

        var addApply = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "apply" && s.ContainerName == "ADD"));
        Assert.Equal("function", addApply.ContainerKind);

        var subApply = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "apply" && s.ContainerName == "SUB"));
        Assert.Equal("function", subApply.ContainerKind);

        var abstractApply = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "apply" && s.ContainerName == "Op"));
        Assert.Equal("enum", abstractApply.ContainerKind);

        var compactCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Range"));
        Assert.Equal("class", compactCtor.ContainerKind);
        Assert.Equal("Range", compactCtor.ContainerName);
        Assert.NotNull(compactCtor.BodyStartLine);
        Assert.NotNull(compactCtor.BodyEndLine);
    }

    [Fact]
    public void Extract_Java_SameLineCompactConstructors_StayIndexedWithoutRecordHeaderLeak()
    {
        const string content = """
            public record R(int x) {
                @Deprecated public R { if (x < 0) throw new IllegalArgumentException(); }
            }

            public record Inline(int x) { public Inline { if (x < 0) throw new IllegalArgumentException(); } }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        var annotatedCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "R"));
        Assert.Equal("class", annotatedCtor.ContainerKind);
        Assert.Equal("R", annotatedCtor.ContainerName);
        Assert.Equal("public R { if (x < 0) throw new IllegalArgumentException(); }", annotatedCtor.Signature);
        Assert.True(string.IsNullOrEmpty(annotatedCtor.ReturnType));
        Assert.NotNull(annotatedCtor.BodyStartLine);
        Assert.NotNull(annotatedCtor.BodyEndLine);

        var inlineCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Inline"));
        Assert.Equal("class", inlineCtor.ContainerKind);
        Assert.Equal("Inline", inlineCtor.ContainerName);
        Assert.Equal("public Inline { if (x < 0) throw new IllegalArgumentException(); }", inlineCtor.Signature);
        Assert.True(string.IsNullOrEmpty(inlineCtor.ReturnType));
        Assert.NotNull(inlineCtor.BodyStartLine);
        Assert.NotNull(inlineCtor.BodyEndLine);
    }

    [Fact]
    public void Extract_Java_AllmanCompactConstructors_StayIndexed()
    {
        const string content = """
            public record Sample(int value)
            {
                public Sample
                {
                    if (value < 0) throw new IllegalArgumentException();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        var compactCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Sample"));
        Assert.Equal("class", compactCtor.ContainerKind);
        Assert.Equal("Sample", compactCtor.ContainerName);
        Assert.Equal("public Sample", compactCtor.Signature);
        Assert.Null(compactCtor.ReturnType);
        Assert.NotNull(compactCtor.BodyStartLine);
        Assert.NotNull(compactCtor.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "value" && s.ContainerName == "Sample");
    }

    [Fact]
    public void Extract_Java_EnumAnonymousMemberBodyMethods_StayNestedUnderMemberBodies()
    {
        const string content = """
            public enum Mix {
                A { @Override public int f() { return 1; } int g() { return 2; } },
                B { @Override public int f() { return 3; } int h() { return 4; } };
                public abstract int f();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ContainerKind == "enum" && s.ContainerName == "Mix" && s.BodyStartLine != null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ContainerKind == "enum" && s.ContainerName == "Mix" && s.BodyStartLine != null);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "f" && s.ContainerKind == "function" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "g" && s.ContainerKind == "function" && s.ContainerName == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "f" && s.ContainerKind == "function" && s.ContainerName == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "h" && s.ContainerKind == "function" && s.ContainerName == "B");
    }

    [Fact]
    public void Extract_Java_SameLineAnnotationInterfaceMembers_DoNotLeakIntoNextDeclaration()
    {
        const string content = """
            @interface Tags { String[] value(); }
            class C {}
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        var valueMember = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "value"));
        Assert.Equal("class", valueMember.ContainerKind);
        Assert.Equal("Tags", valueMember.ContainerName);
        Assert.Equal(1, valueMember.EndLine);
        Assert.Null(valueMember.BodyStartLine);
        Assert.Null(valueMember.BodyEndLine);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C" && s.StartLine == 2 && s.EndLine == 2);
    }

    [Fact]
    public void Extract_Java_SameLineAnnotationInterfaceMembers_KeepLaterMembers()
    {
        const string content = """
            @interface Tags { String[] value(); int age(); } class C {}
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        var valueMember = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "value"));
        var ageMember = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "age"));
        Assert.Equal("Tags", valueMember.ContainerName);
        Assert.Equal("Tags", ageMember.ContainerName);
        Assert.Equal(1, valueMember.EndLine);
        Assert.Equal(1, ageMember.EndLine);
        Assert.Null(valueMember.BodyStartLine);
        Assert.Null(ageMember.BodyStartLine);
        Assert.Equal("String[] value();", valueMember.Signature);
        Assert.Equal("int age();", ageMember.Signature);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C" && s.StartLine == 1 && s.EndLine == 1);
    }

    [Fact]
    public void Extract_Java_RecordHeaderAnnotationArray_KeepsCompactConstructor()
    {
        const string content = """
            @interface Tags { String[] value(); }

            public record Sample(@Tags({"a", "b"}) int value) {
                public Sample {
                    if (value < 0) throw new IllegalArgumentException();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "java", content);

        var compactCtor = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Sample"));
        Assert.Equal("class", compactCtor.ContainerKind);
        Assert.Equal("Sample", compactCtor.ContainerName);
        Assert.NotNull(compactCtor.BodyStartLine);
        Assert.NotNull(compactCtor.BodyEndLine);
        Assert.Contains("public Sample {", compactCtor.Signature);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesXClassAndXName()
    {
        var content = """
            <Window x:Class="Sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid>
                    <Button x:Name="SaveButton" Content="Save" />
                    <TextBlock x:Name="StatusText" />
                </Grid>
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Sample.MainWindow");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SaveButton");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "StatusText");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesXKey()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <SolidColorBrush x:Key="{x:Static Member={x:Type local:Keys}.AccentBrush}" Color="Tomato" />
                <Style x:Key="PrimaryButtonStyle" TargetType="Button">
                    <Setter Property="Background" Value="{StaticResource AccentBrush}" />
                </Style>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "local:Keys.AccentBrush");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PrimaryButtonStyle");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTargetTypeAndDataType()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels">
                <Style TargetType="Button">
                    <Setter Property="Background" Value="Tomato" />
                </Style>
                <ControlTemplate TargetType="{x:Type vm:CustomButton}">
                    <Grid />
                </ControlTemplate>
                <DataTemplate x:DataType="vm:PersonViewModel">
                    <TextBlock Text="{Binding FullName}" />
                </DataTemplate>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Button");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypeArgumentsAsClassSymbols()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels"
                                xmlns:local="clr-namespace:Sample.Controls">
                <local:Pair x:TypeArguments="x:String, vm:PersonViewModel" />
                <local:Factory x:TypeArguments="{x:Type vm:CustomButton}" />
                <local:Nested x:TypeArguments="vm:Outer(x:String, vm:InnerModel)" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:String");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Outer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:InnerModel");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesWrappedTypeArgumentsAcrossLines()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels"
                                xmlns:local="clr-namespace:Sample.Controls">
                <local:Pair
                    x:TypeArguments="x:String,
                                     vm:Outer(
                                         vm:InnerModel,
                                         x:Int32)" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:String");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Outer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:InnerModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:Int32");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesWrappedTypeBearingAttributesAcrossLines()
    {
        var content = """
            <Window
                x:Class=
                    "Sample.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Sample.ViewModels">
                <DataTemplate
                    x:DataType=
                        "vm:PersonViewModel">
                    <Style
                        TargetType=
                            "{x:Type vm:CustomButton}" />
                </DataTemplate>
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Sample.MainWindow");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypeObjectElementsAcrossLines()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels">
                <DataTemplate.DataType>
                    <x:Type TypeName=
                        "vm:PersonViewModel" />
                </DataTemplate.DataType>
                <Style.TargetType>
                    <x:TypeExtension TypeName=
                        "{x:Type vm:CustomButton}" />
                </Style.TargetType>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypePropertyElementsAcrossLines()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels">
                <x:Type.TypeName>
                    vm:PersonViewModel
                </x:Type.TypeName>
                <x:TypeExtension.TypeName>
                    {x:Type vm:CustomButton}
                </x:TypeExtension.TypeName>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypeMarkupExtensions()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <ContentPage.Resources>
                    <ControlTemplate TargetType="{x:Type vm:PersonViewModel}" />
                    <TextBlock ToolTip="{x:TypeExtension TypeName=vm:CustomButton}" />
                </ContentPage.Resources>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesXStaticMemberTypeReferences()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Sample.ViewModels">
                <SolidColorBrush x:Key="{x:Static local:Keys.AccentBrush}" Color="Tomato" />
                <TextBlock Text="{x:Static local:App.DisplayName}" />
                <Style x:Key="{x:Static Member={x:Type local:Keys}.PrimaryStyleKey}">
                    <Setter Property="Background" Value="Tomato" />
                </Style>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "local:Keys");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "local:App");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesCommonEventHandlers()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <VerticalStackLayout>
                    <Button Text="Save" Clicked="OnSaveClicked" />
                    <Entry TextChanged="OnFilterTextChanged" />
                    <CollectionView SelectionChanged="OnSelectionChanged" />
                </VerticalStackLayout>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnSaveClicked");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnFilterTextChanged");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnSelectionChanged");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesWrappedSearchAttributesAcrossLines()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:Sample.ViewModels">
                <ContentPage.Resources>
                    <SolidColorBrush
                        x:Key=
                            "{x:Static Member={x:Type local:Keys}.AccentBrush}"
                        Color="Tomato" />
                </ContentPage.Resources>
                <VerticalStackLayout>
                    <Button
                        x:Name=
                            "SaveButton"
                        Clicked=
                            "OnSaveClicked" />
                    <Entry
                        TextChanged=
                            "OnFilterTextChanged" />
                </VerticalStackLayout>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "local:Keys.AccentBrush"));
        Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SaveButton"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnSaveClicked"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnFilterTextChanged"));
    }

    [Fact]
    public void Extract_Xml_XamlCapturesBindingPaths()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <StackPanel DataContext="{Binding Source=Root, Path=ViewModel}">
                    <Label Text="{Binding
                        Title}" />
                    <Button Command="{x:Bind
                        ViewModel.SaveCommand}" />
                </StackPanel>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ViewModel");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Title");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SaveCommand");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Root");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesCompiledAndReflectionBindingPaths()
    {
        var content = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel>
                    <TextBlock Text="{CompiledBinding ViewModel.Title}" />
                    <TextBox Text="{ReflectionBinding Path=Search.FilterText}" />
                    <Button Command="{CompiledBinding
                        Commands.Save}" />
                    <TextBlock Tag="{CompiledBinding Path=Profile.DisplayName, ConverterParameter='Path=Ignored'}" />
                </StackPanel>
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("Title", propertyNames);
        Assert.Contains("FilterText", propertyNames);
        Assert.Contains("Save", propertyNames);
        Assert.Contains("DisplayName", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
        Assert.DoesNotContain("ViewModel", propertyNames);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesBindingElementNameReferences()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <Grid>
                    <TextBlock Text="{Binding Text, ElementName=SearchBox}" />
                    <Slider Value="{Binding ElementName=VolumeSlider, Path=Value}" />
                    <TextBlock Tag="{Binding Path=Title, ConverterParameter='prefix, ElementName=Ignored'}" />
                    <Binding
                        ElementName="RootPanel"
                        Path="DataContext.CurrentUser.Name" />
                    <Binding.ElementName>
                        DetailsList
                    </Binding.ElementName>
                </Grid>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("SearchBox", propertyNames);
        Assert.Contains("VolumeSlider", propertyNames);
        Assert.Contains("RootPanel", propertyNames);
        Assert.Contains("DetailsList", propertyNames);
        Assert.Contains("Name", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTemplateBindingProperties()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Sample.Controls">
                <ControlTemplate TargetType="{x:Type local:ButtonChrome}">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding Property=local:ButtonChrome.BorderBrush}" />
                </ControlTemplate>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Background");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "BorderBrush");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesObjectElementBindingPaths()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <TextBlock>
                    <TextBlock.Text>
                        <MultiBinding StringFormat="{}{0} {1}">
                            <Binding
                                Source="Root"
                                ConverterParameter="Path='Ignored'"
                                Path="ViewModel.FirstName" />
                            <Binding Path="vm:PersonViewModel.LastName" />
                        </MultiBinding>
                    </TextBlock.Text>
                </TextBlock>
                <Binding.Path>
                    Profile.DisplayName
                </Binding.Path>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "FirstName");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "LastName");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DisplayName");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Root");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Ignored");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesXReferenceTargets()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:Sample.ViewModels">
                <Grid>
                    <TextBlock Text="{Binding Source={x:Reference RootPanel}, Path=Title}" />
                    <TextBlock Text="{Binding Source={x:Reference Name=NamedTarget}, Path=Title}" />
                    <TextBlock Text="{Binding Source={x:ReferenceExtension Name=ExtensionTarget}, Path=Title}" />
                    <x:Reference ToolTip="Name='Ignored'" Name="ObjectTarget" />
                    <x:Reference.Name>
                        PropertyTarget
                    </x:Reference.Name>
                </Grid>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("RootPanel", propertyNames);
        Assert.Contains("NamedTarget", propertyNames);
        Assert.Contains("ExtensionTarget", propertyNames);
        Assert.Contains("ObjectTarget", propertyNames);
        Assert.Contains("PropertyTarget", propertyNames);
        Assert.Contains("Title", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
        Assert.DoesNotContain("x:Reference", propertyNames);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesStaticAndDynamicResourceKeys()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Sample.ViewModels">
                <SolidColorBrush x:Key="PrimaryBrush" Color="Tomato" />
                <SolidColorBrush x:Key="{x:Static local:Keys.WarningBrush}" Color="Orange" />
                <TextBlock Foreground="{StaticResource PrimaryBrush}" />
                <Border BorderBrush="{DynamicResource ResourceKey={x:Static Member={x:Type local:Keys}.AccentBrush}}" />
                <TextBlock DataContext="{Binding Source={StaticResource ViewModelLocator}, Path=CurrentUser.DisplayName}" />
                <TextBlock ToolTip="{StaticResource}" />
                <Border Background="{DynamicResource ResourceKey=}" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("PrimaryBrush", propertyNames);
        Assert.Contains("local:Keys.WarningBrush", propertyNames);
        Assert.Contains("local:Keys.AccentBrush", propertyNames);
        Assert.Contains("ViewModelLocator", propertyNames);
        Assert.Contains("DisplayName", propertyNames);
        Assert.DoesNotContain("StaticResource", propertyNames);
        Assert.DoesNotContain("DynamicResource", propertyNames);
        Assert.DoesNotContain("ResourceKey", propertyNames);
    }

    [Fact]
    public void Extract_Xml_NonXamlXmlDoesNotEmitXamlSymbols()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Foo x:Name="ShouldNotBeCaptured" />
              </ItemGroup>
            </Project>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        Assert.DoesNotContain(symbols, s => s.Name == "ShouldNotBeCaptured");
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }
    [Fact]
    public void Extract_Shell_DetectsMultipleAliases()
    {
        var content = """
            alias ll='ls -la' gs='git status'
            alias -g G='| grep' H='| head'
            """;
        var symbols = SymbolExtractor.Extract(1, "shell", content);

        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "ll");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "gs");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "G");
        Assert.Contains(symbols, s => s.Kind == "alias" && s.Name == "H");
    }

    [Fact]
    public void Extract_Perl_CapturesPackagesImportsConstantsAndAttributedSubs()
    {
        var content = """
            package My::App v1.2.3;

            use parent 'My::Base';
            use constant DEFAULT_LIMIT => 10;
            use constant { SECOND_LIMIT => 20, 'THIRD_LIMIT' => 30 };
            use constant {
                FOURTH_LIMIT => 40,
                "FIFTH_LIMIT" => 50,
            };
            our $VERSION = '1.0';
            our @EXPORT_OK = qw(render);
            has name => (is => 'ro');
            has '+id' => (default => 1);

            sub render : prototype($) {
                return DEFAULT_LIMIT;
            }

            sub My::App::qualified_render {
                return DEFAULT_LIMIT;
            }

            my sub local_helper {
                return SECOND_LIMIT;
            }

            state sub cached_helper {
                return THIRD_LIMIT;
            }

            method dispatch ($request) {
                return render($request);
            }

            fun normalize ($value) {
                return $value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "My::App");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "parent");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "DEFAULT_LIMIT");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "SECOND_LIMIT");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "THIRD_LIMIT");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "FOURTH_LIMIT");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "FIFTH_LIMIT");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "VERSION");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "EXPORT_OK");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "id");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "My::App::qualified_render");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "local_helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "cached_helper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "dispatch");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "normalize");
    }

    [Fact]
    public void Extract_PerlHashConstants_NormalizesQuotedKeysAndDeduplicates()
    {
        var content = """
            use constant {
                "foo " => 1,
                foo => 2,
                "naïve" => 3,
                "naïve" => 4,
                "hex\xEF" => 5,
                "hexï" => 6,
                "braced\x{00EF}" => 7,
                "bracedï" => 8,
                "   " => 9,
            };
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.Equal(4, symbols.Count(symbol => symbol.Kind == "function"));
        Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "foo"));
        Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "naïve"));
        Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "hexï"));
        Assert.Single(symbols.Where(symbol => symbol.Kind == "function" && symbol.Name == "bracedï"));
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "foo ");
    }

    [Fact]
    public void Extract_Perl_CapturesPackageBlockNamespaces()
    {
        var content = """
            package My::Block {
                sub render {
                    return 1;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "My::Block");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render");
    }

    [Fact]
    public void Extract_Perl_CapturesClassFeatureDeclarations()
    {
        var content = """
            class My::Widget {
                field $name;
                method render {
                    return 1;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "My::Widget");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "name");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render");
    }

    [Fact]
    public void Extract_Perl_CapturesRoleFeatureDeclarations()
    {
        var content = """
            role My::Renderable {
                method render {
                    return 1;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "perl", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "My::Renderable");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "render");
    }

    [Fact]
    public void Extract_TypeScript_ResolvesTsconfigPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = () => null;\n");
            var sourcePath = WriteFile(projectRoot, "src/app/page.tsx", "import { Button } from \"@/components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_BomPrefixedTsconfigResolvesPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_bom_symbols");
        try
        {
            var tsconfigPath = Path.Combine(projectRoot, "tsconfig.json");
            File.WriteAllText(
                tsconfigPath,
                """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = () => null;\n");
            var sourcePath = WriteFile(projectRoot, "src/app/page.tsx", "import { Button } from \"@/components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesBaseUrlOnlyImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_baseurl_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": "src"
                  }
                }
                """);
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/app/page.tsx", "import { Button } from \"components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "components/Button");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesImportEqualsRequirePathAlias()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_import_equals_alias_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "src/services/api.ts", "export = {};\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import Api = require(\"@/services/api\");\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Api");
            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/services/api.ts");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "@/services/api");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesInheritedTsconfigPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_extends_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.base.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~lib/*": ["lib/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "extends": "./tsconfig.base.json"
                }
                """);
            WriteFile(projectRoot, "lib/math/index.ts", "export const sum = () => 0;\n");
            var sourcePath = WriteFile(projectRoot, "src/app.ts", "import { sum } from \"~lib/math\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "lib/math/index.ts");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesInheritedPathAliasesFromDeclaringBaseUrl()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_extends_baseurl_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.base.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~shared/*": ["shared/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "apps/web/tsconfig.json", """
                {
                  "extends": "../../tsconfig.base.json",
                  "compilerOptions": {
                    "baseUrl": "."
                  }
                }
                """);
            WriteFile(projectRoot, "shared/api.ts", "export const api = 1;\n");
            WriteFile(projectRoot, "apps/web/shared/api.ts", "export const wrong = 1;\n");
            var sourcePath = WriteFile(projectRoot, "apps/web/src/app.ts", "import { api } from \"~shared/api\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath, projectRoot);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "shared/api.ts");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "apps/web/shared/api.ts");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_PrefersMoreSpecificTsconfigPathAliasImports()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_specificity_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "*": ["fallback/*"],
                      "@app/*": ["src/app/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "fallback/@app/Button.ts", "export const Wrong = 1;\n");
            WriteFile(projectRoot, "src/app/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@app/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/app/Button.ts");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "fallback/@app/Button.ts");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ResolvesNestedTsconfigAliasesRelativeToProjectRoot()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_nested_symbols");
        try
        {
            WriteFile(projectRoot, "packages/app/tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "packages/app/src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "packages/app/src/page.tsx", "import { Button } from \"@/components/Button\";\n");

            var symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath, projectRoot);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "packages/app/src/components/Button.tsx");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OversizedTsconfigSkipsPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_oversized_symbols");
        try
        {
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}},\"pad\":\"" + new string('a', 260 * 1024) + "\"}");
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("exceeds", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_MalformedTsconfigSkipsPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_malformed_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                """);
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("could not be parsed as JSON", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_DeepTsconfigJsonSkipsPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_deep_json_symbols");
        try
        {
            var deepJson = string.Concat(Enumerable.Repeat("{\"nested\":", 40)) + "0" + new string('}', 40);
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}},\"deep\":" + deepJson + "}");
            WriteFile(projectRoot, "src/components/Button.tsx", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@/components/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@/components/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/components/Button.tsx");
            Assert.Contains("Skipped TypeScript path alias config", stderr, StringComparison.Ordinal);
            Assert.Contains("32-level depth limit", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigExtendsDepthSkipsInheritedPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_deep_extends_symbols");
        try
        {
            WriteFile(projectRoot, "tsconfig.json", "{\"extends\":\"./tsconfig.1.json\"}");
            for (var i = 1; i <= 8; i++)
                WriteFile(projectRoot, $"tsconfig.{i}.json", "{\"extends\":\"./tsconfig." + (i + 1) + ".json\"}");
            WriteFile(projectRoot, "tsconfig.9.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~lib/*": ["lib/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "lib/math.ts", "export const sum = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/app.ts", "import { sum } from \"~lib/math\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "~lib/math");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "lib/math.ts");
            Assert.Contains("extends depth", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_TsconfigExtendsTotalBytesCapSkipsInheritedPathAliasesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_total_bytes_symbols");
        try
        {
            var pad = new string('a', 210 * 1024);
            WriteFile(projectRoot, "tsconfig.json", "{\"extends\":\"./tsconfig.1.json\",\"pad\":\"" + pad + "\"}");
            WriteFile(projectRoot, "tsconfig.1.json", "{\"extends\":\"./tsconfig.2.json\",\"pad\":\"" + pad + "\"}");
            WriteFile(projectRoot, "tsconfig.2.json", "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"~lib/*\":[\"lib/*\"]}},\"pad\":\"" + pad + "\"}");
            WriteFile(projectRoot, "lib/math.ts", "export const sum = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/app.ts", "import { sum } from \"~lib/math\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "~lib/math");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "lib/math.ts");
            Assert.Contains("extends chain exceeds", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigPathAliasRulesTruncatesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_many_rules_symbols");
        try
        {
            var maxRules = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasRules");
            var paths = new StringBuilder();
            for (var i = 0; i < maxRules; i++)
            {
                if (i > 0)
                    paths.Append(',');
                paths.Append('"').Append("@skip").Append(i).Append("/*").Append("\":[\"missing").Append(i).Append("/*\"]");
            }

            paths.Append(",\"@hit/*\":[\"src/*\"]");

            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{" + paths + "}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@hit/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@hit/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Truncated TypeScript path alias rules", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_ExcessiveTsconfigPathAliasTargetsTruncatesWithWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_many_targets_symbols");
        try
        {
            var maxTargets = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetsPerRule");
            var targets = new StringBuilder();
            for (var i = 0; i < maxTargets; i++)
            {
                if (i > 0)
                    targets.Append(',');
                targets.Append('"').Append("missing").Append(i).Append("/*").Append('"');
            }

            targets.Append(",\"src/*\"");

            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@hit/*\":[" + targets + "]}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { Button } from \"@hit/Button\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "@hit/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Truncated TypeScript path alias targets", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OverlongTsconfigPathAliasStringsAreIgnoredWithBoundedWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_long_strings_symbols");
        try
        {
            var maxPatternLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasPatternLength");
            var maxTargetLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetLength");
            var longPatternPrefix = "@" + new string('a', maxPatternLength);
            var longPattern = longPatternPrefix + "/*";
            var longTarget = "src/" + new string('b', maxTargetLength + 1) + "/*";
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\""
                + longPattern
                + "\":[\"src/*\"],\"@longtarget/*\":[\""
                + longTarget
                + "\"]}}}");
            WriteFile(projectRoot, "src/Button.ts", "export const Button = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", """
                import { Button } from "__LONG_PATTERN__/Button";
                import { Other } from "@longtarget/Other";
                """.Replace("__LONG_PATTERN__", longPatternPrefix, StringComparison.Ordinal));

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == longPatternPrefix + "/Button");
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name == "src/Button.ts");
            Assert.Contains("Ignored TypeScript path alias rules", stderr, StringComparison.Ordinal);
            Assert.Contains("Ignored TypeScript path alias targets", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longPattern, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longTarget, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OverlongPathAliasModuleSpecifierSkipsResolutionWithBoundedWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_long_module_symbols");
        try
        {
            var maxModuleSpecifierLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasModuleSpecifierLength");
            var longModuleName = "@/" + new string('a', maxModuleSpecifierLength + 1);
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\"src/*\"]}}}");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import value from \"" + longModuleName + "\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == longModuleName);
            Assert.DoesNotContain(symbols, s => s.Kind == "import" && s.Name.StartsWith("src/", StringComparison.Ordinal));
            Assert.Contains("Skipped TypeScript path alias resolution", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longModuleName, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_TypeScript_OverlongPathAliasSubstitutionSkipsCandidateWithBoundedWarning()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("tsconfig_alias_long_substitution_symbols");
        try
        {
            var maxTargetLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasTargetLength");
            var maxSubstitutedTargetLength = GetSymbolExtractorIntConstant("MaxTypeScriptPathAliasSubstitutedTargetLength");
            var wildcard = new string('a', (maxSubstitutedTargetLength / maxTargetLength) + 2);
            var longSubstitutingTarget = new string('*', maxTargetLength);
            WriteFile(
                projectRoot,
                "tsconfig.json",
                "{\"compilerOptions\":{\"baseUrl\":\".\",\"paths\":{\"@/*\":[\""
                + longSubstitutingTarget
                + "\",\"src/*\"]}}}");
            WriteFile(projectRoot, "src/" + wildcard + ".ts", "export const value = 1;\n");
            var sourcePath = WriteFile(projectRoot, "src/main.ts", "import { value } from \"@/" + wildcard + "\";\n");

            List<SymbolRecord> symbols = [];
            var stderr = ConsoleCapture.CaptureError(() =>
                symbols = SymbolExtractor.Extract(1, "typescript", File.ReadAllText(sourcePath), sourcePath));

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "src/" + wildcard + ".ts");
            Assert.Contains("Skipped TypeScript path alias target substitution", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(longSubstitutingTarget, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(wildcard, stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Extract_JavaScript_ResolvesJsconfigPathAliasImportsAndKeepsMissesLiteral()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("jsconfig_alias_symbols");
        try
        {
            WriteFile(projectRoot, "jsconfig.json", """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~components/*": ["components/*"]
                    }
                  }
                }
                """);
            WriteFile(projectRoot, "components/Card.jsx", "export const Card = () => null;\n");
            var sourcePath = WriteFile(projectRoot, "src/view.js", """
                import Card from "~components/Card";
                import Missing from "~components/Missing";
                """);

            var symbols = SymbolExtractor.Extract(1, "javascript", File.ReadAllText(sourcePath), sourcePath);

            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "components/Card.jsx");
            Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "~components/Missing");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static int GetSymbolExtractorIntConstant(string name)
    {
        var field = typeof(SymbolExtractor).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<int>(field.GetRawConstantValue());
    }

    private static bool IsSymbolRegexOwnerType(Type type) =>
        type.Namespace == "CodeIndex.Indexer"
        && (type.Name == "SymbolExtractor" || type.Name.EndsWith("SymbolNameNormalizer", StringComparison.Ordinal));

    private static IEnumerable<(string Path, Regex Regex)> EnumerateStaticRegexValues(IEnumerable<Type> types)
    {
        foreach (var type in types)
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? value;
                try
                {
                    value = field.GetValue(null);
                }
                catch (TargetInvocationException)
                {
                    throw;
                }

                foreach (var item in EnumerateRegexValues(value, $"{type.Name}.{field.Name}", new HashSet<object>(ReferenceEqualityComparer.Instance)))
                    yield return item;
            }
        }
    }

    private static IEnumerable<(string Path, Regex Regex)> EnumerateRegexValues(object? value, string path, HashSet<object> seen)
    {
        if (value is null)
            yield break;

        if (value is Regex regex)
        {
            yield return (path, regex);
            yield break;
        }

        if (value is string or Type or MemberInfo or Delegate)
            yield break;

        var valueType = value.GetType();
        if (valueType.IsPrimitive || valueType.IsEnum)
            yield break;

        if (!valueType.IsValueType && !seen.Add(value))
            yield break;

        if (value is IEnumerable enumerable)
        {
            var index = 0;
            foreach (var item in enumerable)
            {
                foreach (var nested in EnumerateRegexValues(item, $"{path}[{index}]", seen))
                    yield return nested;
                index++;
            }

            yield break;
        }

        foreach (var field in valueType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? child;
            try
            {
                child = field.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            foreach (var nested in EnumerateRegexValues(child, $"{path}.{field.Name}", seen))
                yield return nested;
        }

        foreach (var property in valueType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;

            object? child;
            try
            {
                child = property.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            foreach (var nested in EnumerateRegexValues(child, $"{path}.{property.Name}", seen))
                yield return nested;
        }
    }

    private static string WriteFile(string projectRoot, string relativePath, string content)
    {
        var path = Path.Combine(projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
