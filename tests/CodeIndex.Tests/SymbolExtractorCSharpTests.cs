using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_CsharpFileScopedNamespace_DoesNotEnterMemberHeaderMerge()
    {
        const string content = """
            namespace CodeIndex.Indexer;

            internal static class StructuralLineMasker
            {
                internal static string[] MaskLines(string? lang, string[] originalLines)
                    => originalLines;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == "CodeIndex.Indexer");
        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "StructuralLineMasker");
    }

    [Fact]
    public void Extract_CsharpOperatorOverloads_UseOperatorKind()
    {
        const string content = """
            public readonly struct Point
            {
                public static Point operator +(Point left, Point right) => left;
                public static bool operator ==(Point left, Point right) => true;
                public static bool operator !=(Point left, Point right) => false;
                public static explicit operator int(Point point) => 0;
                public static Point operator checked +(Point left, Point right) => left;
            }

            public interface IAddable<TSelf>
            {
                static abstract TSelf operator +(TSelf left, TSelf right);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "operator" && symbol.Name == "operator +");
        Assert.Contains(symbols, symbol => symbol.Kind == "operator" && symbol.Name == "operator ==");
        Assert.Contains(symbols, symbol => symbol.Kind == "operator" && symbol.Name == "operator !=");
        Assert.Contains(symbols, symbol => symbol.Kind == "operator" && symbol.Name == "explicit operator int");
        Assert.Contains(symbols, symbol => symbol.Kind == "operator" && symbol.Name == "operator checked +");
        Assert.Contains(symbols, symbol => symbol.Kind == "operator" && symbol.ContainerKind == "interface");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "function" && symbol.Name.StartsWith("operator", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CsharpManyMethods_DoesNotRescanMethodBodiesAsFieldCandidates()
    {
        var methods = Enumerable.Range(0, 80).Select(i => $$"""
                public void M{{i}}()
                {
                    var value = {{i}};
                    value++;
                }
            """);
        var content = "public class ManyMethods\n{\n" + string.Join('\n', methods) + "\n}";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(80, symbols.Count(symbol => symbol.Kind == "function" && symbol.Name.StartsWith("M", StringComparison.Ordinal)));
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "function" && symbol.Signature?.Contains("value++", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extract_CSharp_DetectsClassesAndMethods()
    {
        var content = "public class UserService\n{\n    public async Task<User> GetUser(int id)\n    {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserService");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetUser");
    }

    [Fact]
    public void Extract_CSharp_DetectsAssignedLambdaAsLambda()
    {
        var content = """
            public class Worker
            {
                public void Run()
                {
                    var transform = value => value + 1;
                    Func<int, int> projector = (value) => value * 2;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "transform");
        Assert.Contains(symbols, s => s.Kind == "lambda" && s.Name == "projector");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name is "transform" or "projector");
    }

    [Fact]
    public void Extract_CSharp_DetectsScopedMethodParametersAsProperties()
    {
        var content = """
            public class RefService
            {
                public void Update<T>(scoped ref T value, scoped Span<int> data)
                {
                }

                public Buffer[] Buffer(scoped ref int value)
                {
                    return [];
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.Name == "value"
            && s.ContainerKind == "function"
            && s.ContainerName == "Update"
            && s.ReturnType == "T"
            && s.Signature == "scoped ref T value");
        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.Name == "data"
            && s.ContainerKind == "function"
            && s.ContainerName == "Update"
            && s.ReturnType == "Span<int>"
            && s.Signature == "scoped Span<int> data");
        Assert.Contains(symbols, s =>
            s.Kind == "property"
            && s.Name == "value"
            && s.ContainerKind == "function"
            && s.ContainerName == "Buffer"
            && s.ReturnType == "int"
            && s.Signature == "scoped ref int value");
    }

    [Fact]
    public void Extract_CSharp_NormalizesVerbatimIdentifiers()
    {
        var content = """
            namespace CsVerbatimIdent;

            public class @class
            {
                public int @int { get; set; }
                public string @return => string.Empty;
                public static @class Make() => new @class();
                public void @if() { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "class");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "return");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Make");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "if");
        Assert.DoesNotContain(symbols, s => s.Name.StartsWith("@", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CSharp_ClassifiesAttributedTestMethods()
    {
        var content = """
            namespace Demo.Tests;

            public class CalculatorTests
            {
                [Fact]
                public void AddsValues() { }

                [Theory(DisplayName = "adds many")]
                [InlineData(1, 2, 3)]
                public void AddsManyValues(int left, int right, int expected) { }

                [TestMethod]
                public void MultipliesValues() { }

                [Test]
                public void DividesValues() { }

                [NUnit.Framework.TestCase(1)]
                public void AcceptsQualifiedNUnitAttributes(int value) { }

                [Obsolete]
                public void HelperMethod() { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "AddsValues");
        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "AddsManyValues");
        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "MultipliesValues");
        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "DividesValues");
        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "AcceptsQualifiedNUnitAttributes");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "HelperMethod");
    }

    [Fact]
    public void Extract_CSharp_NormalizesUnicodeEscapedIdentifierNames()
    {
        const string content = "namespace Demo.\\u004eames;\n\n"
            + "public class \\u0046oo\n"
            + "{\n"
            + "    public int @\\u0063lass { get; set; }\n"
            + "    public void \\u0042ar() { }\n"
            + "}\n\n"
            + "public enum \\u0043olor\n"
            + "{\n"
            + "    \\u0052ed,\n"
            + "}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Demo.Names");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Foo");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "class" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Bar" && s.ContainerName == "Foo");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Color");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Red" && s.ContainerName == "Color");
        Assert.DoesNotContain(symbols, s => s.Name.Contains('\\'));
        Assert.DoesNotContain(symbols, s => s.Name.Contains('@'));
    }

    [Fact]
    public void Extract_CSharp_NormalizesVerbatimQualifiedNamespaceAndImportNames()
    {
        var content = """
            using Outer.@class;

            namespace Outer.@class
            {
                public class Container
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var namespaceSymbol = Assert.Single(symbols.Where(s => s.Kind == "namespace"));
        var importSymbol = Assert.Single(symbols.Where(s => s.Kind == "import"));
        var containerSymbol = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Container"));

        Assert.Equal("Outer.class", namespaceSymbol.Name);
        Assert.Equal("Outer.class", importSymbol.Name);
        Assert.Equal("namespace", containerSymbol.ContainerKind);
        Assert.Equal("Outer.class", containerSymbol.ContainerName);
        Assert.DoesNotContain(symbols, s => s.Name.Contains("@", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CSharp_NormalizesVerbatimConversionOperatorTargetTypes()
    {
        var content = """
            using System.Collections.Generic;

            namespace Outer.@class
            {
                public class Target
                {
                }
            }

            public class @class
            {
            }

            public class Source
            {
                public static implicit operator @class(Source value) => new @class();
                public static explicit operator Outer.@class.Target(Source value) => new Outer.@class.Target();
                public static explicit operator List<@class>(Source value) => new();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "implicit operator class");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator Outer.class.Target");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator List<class>");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name.Contains("@", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CSharp_RawStringFixturesDoNotLeakPhantomSymbols()
    {
        var content = """""
            public class FixtureHost
            {
                public void UsesRawFixture()
                {
                    const string fixture = """
                        public class App
                        {
                            public void Run()
                            {
                            }
                        }

                        function main()
                        end
                        """;

                    const string wider = """"
                        public class Wider
                        """";
                }
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "UsesRawFixture"));
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "FixtureHost");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "App");
        Assert.DoesNotContain(symbols, s => s.Kind == "class" && s.Name == "Wider");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Run");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "main");
        Assert.Equal(20, method.EndLine);
        Assert.Equal(20, method.BodyEndLine);
    }

    [Fact]
    public void Extract_CSharp_NestedRawStringInsideInterpolation_DoesNotLeakPhantomSymbols()
    {
        var content = """""
            public class FixtureHost
            {
                public int Run() => 1;
                public string Id(string value) => value;

                public int UsesRawFixture()
                {
                    return $"""
                        value = {Id("""
                            public class Phantom
                            {
                                public void Go() { }
                            }
                            """) + Run()}
                        """.Length;
                }
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "FixtureHost");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "UsesRawFixture");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Run");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Id");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Phantom");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Go");
    }

    [Fact]
    public void Extract_CSharp_InterpolatedVerbatimStringWithEscapedBraces_DoesNotLeakPhantomSymbols()
    {
        var content = """
            public class FixtureHost
            {
                public string Render()
                {
                    return $@"{{
                        public class Phantom
                    }}";
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "FixtureHost");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Render");
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Phantom");
    }

    [Fact]
    public void Extract_CSharp_Issue363RawInterpolatedAndVerbatimStrings_DoNotLeakPhantomSymbols()
    {
        // Regression for issue #363 exact repro: code-shaped members inside C# raw,
        // interpolated raw, and multi-line verbatim strings must not be indexed as
        // real symbols. The current main branch already handles this correctly; this
        // test locks the user-reported fixture in place so future refactors cannot
        // silently reopen it.
        // issue #363 の exact repro 回帰: C# の raw string / 補間付き raw string /
        // 複数行 verbatim string 内のコード風メンバーを本物の symbol として
        // index してはならない。現行 main では直っているため、このテストで
        // ユーザー報告フィクスチャを固定し、将来の refactor での再発を防ぐ。
        var content = """""
            namespace CsRawStringPhantom;

            public class Svc
            {
                public int RealMethod() => 0;

                public string DocsExample() => """
                    public void FakeMethod() { }
                    public int FakeProp { get; set; }
                    public class FakeClass { }
                    public interface IFakeIface { }
                    public delegate int FakeDel();
                    public event System.EventHandler FakeEvent;
                    public Foo() { }
                    """;

                public string VerbatimExample() => @"
                    public void VerbatimFake() { }
                ";

                public string InterpExample() => $"""
                    public void InterpFake() { }
                    """;

                public int AnotherReal() => 1;
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == "CsRawStringPhantom");
        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Svc");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealMethod");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "DocsExample");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "VerbatimExample");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "InterpExample");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "AnotherReal");
        Assert.Equal(7, symbols.Count);

        Assert.DoesNotContain(symbols, symbol => symbol.Name == "FakeMethod");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "FakeProp");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "FakeClass");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "IFakeIface");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "FakeDel");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "FakeEvent");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Foo");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "VerbatimFake");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "InterpFake");
    }

    [Fact]
    public void Extract_CSharp_RawStringLongerQuoteRunDoesNotLeakPhantomSymbols()
    {
        // Regression for #1453 on the C# symbol scanner path: a raw string opened
        // with four quotes must not close on a longer quote run, including from
        // the middle of that longer run.
        // #1453 の C# symbol scanner 経路の回帰: 4 個の quote で始まった raw
        // string は、より長い quote run では閉じず、その途中からも閉じてはならない。
        var content = """""""
            namespace CsRawStringLongQuoteRun;

            public class Svc
            {
                public string DocsExample() => """"
                    public class HiddenBefore { }
                    """""" public class Phantom { public void Ghost() { } }
                    public class HiddenAfter { }
                    """";

                public int RealMethod() => 0;
            }
            """"""";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "namespace" && symbol.Name == "CsRawStringLongQuoteRun");
        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Svc");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "DocsExample");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "RealMethod");

        Assert.DoesNotContain(symbols, symbol => symbol.Name == "HiddenBefore");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Phantom");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Ghost");
        Assert.DoesNotContain(symbols, symbol => symbol.Name == "HiddenAfter");
    }

    [Fact]
    public void Extract_CSharp_PlainInterpolatedStringHoleWithNestedStringLiteral_DoesNotDropLaterHelpers()
    {
        var content = """
            public class Fixture
            {
                private static string BuildSql(string tableName)
                {
                    var sql = $"PRAGMA index_list('{tableName.Replace("'", "''")}')";
                    return sql;
                }

                private static int ParseFoldVersion()
                {
                    return 1;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "class" && symbol.Name == "Fixture");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "BuildSql");
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "ParseFoldVersion");
    }

    [Fact]
    public void Extract_CSharp_CommentedTripleQuotesDoNotHideFollowingMembers()
    {
        var content = "public class FixtureHost\n{\n    // \"\"\" this is only a comment marker\n    public void Run() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "FixtureHost");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Run");
    }

    [Fact]
    public void Extract_CSharp_CharLiteralBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """
            namespace Demo;

            public class FixtureHost
            {
                public bool IsClosingBrace(char c)
                {
                    return c is not '}';
                }

                public void AfterBraceLiteral()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var host = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "FixtureHost"));
        var after = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "AfterBraceLiteral"));

        Assert.Equal(13, host.EndLine);
        Assert.Equal("class", after.ContainerKind);
        Assert.Equal("FixtureHost", after.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_MultilineRawStringBraceDoesNotBreakFollowingContainerAssignment()
    {
        var content = """"
            namespace Demo;

            public class FixtureHost
            {
                public string UsesRawFixture()
                {
                    return """
                        }
                        """;
                }

                public void AfterRawString()
                {
                }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var host = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "FixtureHost"));
        var uses = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "UsesRawFixture"));
        var after = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "AfterRawString"));

        Assert.Equal(15, host.EndLine);
        Assert.Equal(10, uses.EndLine);
        Assert.Equal("class", after.ContainerKind);
        Assert.Equal("FixtureHost", after.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_MultilineVerbatimStringBraceDoesNotBreakFollowingRangeDetection()
    {
        var content = """"
            namespace Demo;

            public class FixtureHost
            {
                public string UsesVerbatimFixture()
                {
                    return @"
            {
            ";
                }

                public void AfterVerbatimString()
                {
                }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var host = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "FixtureHost"));
        var uses = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "UsesVerbatimFixture"));
        var after = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "AfterVerbatimString"));

        Assert.Equal(15, host.EndLine);
        Assert.Equal(10, uses.EndLine);
        Assert.Equal("class", after.ContainerKind);
        Assert.Equal("FixtureHost", after.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_InterpolatedStringCallSites_DoNotEmitPhantomDescribeStateDefinitions()
    {
        // Regression for issue #790: method-call text inside interpolation holes of an
        // outer multi-line string must not be stitched into fake `function` declarations.
        // The real declaration should remain queryable, but call-site fragments from the
        // log string must not surface as extra `DescribeState` definitions.
        // issue #790 の回帰: 外側の複数行文字列にある interpolation hole 内のメソッド呼び出し
        // テキストを、偽の `function` 宣言として継ぎ合わせてはならない。本物の宣言は
        // 取得できるままにしつつ、ログ文字列由来の call-site 断片は追加の
        // `DescribeState` 定義として現れてはならない。
        var content = """""
            namespace Demo;

            public sealed class ReporterContext
            {
                public string ReportsFolderAbsolutePath { get; set; } = string.Empty;
            }

            public sealed class AuditLogGenerateService
            {
                internal static string DescribeState(string label, string? pathOrCommand)
                    => $"{label}:{pathOrCommand}";

                public void WriteAuditLog(ReporterContext context, string auditLogPath)
                {
                    var message = $"""
                        Failed to write audit log for reports folder {context.ReportsFolderAbsolutePath}
                        to {auditLogPath}
                        current state {
                            DescribeState("ReportsFolder", context.ReportsFolderAbsolutePath)}
                        """;
                }
            }
            """"";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var describeState = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "DescribeState"));
        Assert.Equal("AuditLogGenerateService", describeState.ContainerName);
        Assert.Equal("string", describeState.ReturnType);
        Assert.Equal(
            """internal static string DescribeState(string label, string? pathOrCommand) => $"{label}:{pathOrCommand}";""",
            describeState.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsFileScopedNamespaceAndRecordStruct()
    {
        // C# 10+: file-scoped namespace, global using, record struct
        var content = "global using System.Text;\nnamespace MyApp.Models;\n\npublic record struct Point(int X, int Y);\n\npublic record class User(string Name);";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name.Contains("System.Text"));
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MyApp.Models");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Point");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
    }

    [Fact]
    public void Extract_CSharp_DetectsDelegateAndEvent()
    {
        // C# delegate and event / C# デリゲートとイベント
        var content = "public delegate void EventHandler(object sender, EventArgs e);\n\npublic class Button\n{\n    public event EventHandler Click;\n    public static event Action<string> OnLog;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "EventHandler");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Button");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Click");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnLog");
    }

    [Fact]
    public void Extract_CSharp_DetectsSpacedGenericDelegatesAndEvents()
    {
        var content = """
            namespace App;
            using System;

            public delegate Task<int> GetIdAsync(string user);
            public delegate Task<Dictionary<string, int>> LoadAsync();
            public delegate TResult Func<T1, T2, TResult>(T1 a, T2 b);

            public class Pub
            {
                public event Action<string, int> NamedEvent;
                public event Func<string, int, bool> Filter;
                public event EventHandler<ChangedArgs> Changed;
                public event Action OnReady;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "GetIdAsync" && s.ReturnType == "Task<int>");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "LoadAsync" && s.ReturnType == "Task<Dictionary<string,int>>");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Func" && s.ReturnType == "TResult");

        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "NamedEvent" && s.ReturnType == "Action<string,int>");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Filter" && s.ReturnType == "Func<string,int,bool>");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Changed" && s.ReturnType == "EventHandler<ChangedArgs>");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnReady" && s.ReturnType == "Action");
        Assert.DoesNotContain(symbols, s => s.Kind == "event" && s.Name == "int");
    }

    [Fact]
    public void Extract_CSharp_DetectsProperties()
    {
        // C# property with get/set / C# プロパティ（get/set付き）
        var content = "public class User\n{\n    public string Name { get; set; }\n    public int Age { get; init; }\n    public virtual string? Email { get; set; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Age");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Email");
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialProperties()
    {
        var content = """
            namespace Demo;

            public abstract partial class BaseModel
            {
                public abstract string Description { get; }
            }

            public partial class Model : BaseModel
            {
                public partial string Name
                {
                    get;
                    set;
                }

                public partial int Count
                {
                    get;
                }

                public string NotPartial { get; set; } = string.Empty;
            }

            public partial class Model
            {
                private string _name = string.Empty;

                public partial string Name
                {
                    get => _name;
                    set => _name = value;
                }

                public partial int Count
                    => 42;

                public partial override string Description
                    => "demo";
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.True(symbols.Count(s => s.Kind == "property" && s.Name == "Name") >= 2);
        Assert.True(symbols.Count(s => s.Kind == "property" && s.Name == "Count") >= 2);
        Assert.True(symbols.Count(s => s.Kind == "property" && s.Name == "Description") >= 2);
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "NotPartial");

        var countImplementation = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count" && s.Line == 34));
        Assert.Equal(34, countImplementation.StartLine);
        Assert.Equal(35, countImplementation.EndLine);

        var descriptionImplementation = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Description" && s.Line == 37));
        Assert.Equal(37, descriptionImplementation.StartLine);
        Assert.Equal(38, descriptionImplementation.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsReadonlyProperties()
    {
        // issue #327: `readonly` is a valid property/accessor modifier on C# 8+ struct
        // members. All three shapes — expression-bodied (`readonly int A => _v;`),
        // auto-property (`readonly int B { get; }`), and accessor-body
        // (`readonly int C { get => _v; }`) — must surface as `property` rows. The regex
        // modifier slot must consume `readonly` so that a standalone accessor line
        // (`readonly get => _v;`) inside a block-bodied property does NOT match the
        // expression-bodied property regex and leak a phantom `property get` / `property set`.
        // issue #327: C# 8+ 構造体メンバーの `readonly` は property/accessor 修飾子として有効。
        // 式本体 (`readonly int A => _v;`)、自動プロパティ (`readonly int B { get; }`)、
        // accessor-body (`readonly int C { get => _v; }`) の三形態はいずれも `property`
        // として抽出される必要がある。regex の修飾子スロットが `readonly` を消費することで、
        // ブロック本体プロパティ内の `readonly get => _v;` accessor 行が単独で式本体プロパティ
        // regex にマッチせず phantom `property get` / `property set` を生まない。
        var content = """
            namespace Demo;

            public struct S
            {
                private int _v;

                public readonly int A => _v;
                public readonly int B { get; }
                public readonly int C { get => _v; }

                public int Mixed
                {
                    readonly get => _v;
                    set => _v = value;
                }

                public int D { get; set; }
                public readonly int GetD() => D;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var a = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "A"));
        Assert.Equal("int", a.ReturnType);
        Assert.Equal("public", a.Visibility);

        var b = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "B"));
        Assert.Equal("int", b.ReturnType);
        Assert.Equal("public", b.Visibility);

        var c = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "C"));
        Assert.Equal("int", c.ReturnType);
        Assert.Equal("public", c.Visibility);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Mixed");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "D");

        // Baseline: `readonly` methods continue to extract as `function`.
        // ベースライン: `readonly` メソッドは従来どおり `function` として抽出される。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetD");

        // Phantom suppression: neither the accessor line `readonly get => _v;` nor the
        // accessor line `set => _v = value;` must leak a top-level `property` row named
        // `get` / `set` / `init`.
        // phantom 抑止: accessor 行の `readonly get => _v;` や `set => _v = value;` が
        // top-level の `property get` / `property set` / `property init` を生まないこと。
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "get");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "set");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "init");
    }

    [Fact]
    public void Extract_CSharp_DetectsReadonlyIndexers()
    {
        // issue #352: `readonly` is a valid indexer modifier on C# 8+ struct members.
        // Expression-body (`public readonly int this[int i] => _arr[i];`), block-body
        // (`public readonly string this[string key] { get => key; }`), and generic
        // (`public readonly T this[int i] { get => _items[i]; }`) shapes must all be
        // captured as `function` rows with C#'s metadata name `Item` and `visibility` /
        // `returnType` populated, not dropped because `readonly` is missing from the
        // indexer-regex modifier slot. The non-readonly baseline indexer and the
        // readonly method baseline must continue to extract as before.
        // issue #352: C# 8+ 構造体メンバーの `readonly` はインデクサ修飾子として有効。
        // 式本体 (`public readonly int this[int i] => _arr[i];`)、ブロック本体
        // (`public readonly string this[string key] { get => key; }`)、ジェネリック
        // (`public readonly T this[int i] { get => _items[i]; }`) のいずれも、C# メタ
        // データ名 `Item` の `function` 行として `visibility` / `returnType` を保持
        // したまま抽出される必要がある。インデクサ regex の修飾子スロットに `readonly`
        // が無いことで silent drop されてはならない。非 readonly なインデクサと
        // readonly メソッドのベースラインは従来どおり抽出される。
        var content = """
            namespace Demo;

            public struct S
            {
                private int[] _arr;

                public readonly int this[int i] => _arr[i];

                public readonly string this[string key]
                {
                    get => key;
                }

                public int this[long key] => 0;

                public readonly int ReadMe() => 0;
            }

            public struct Bag<T>
            {
                private T[] _items;

                public readonly T this[int i] { get => _items[i]; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var indexerItems = symbols.Where(s => s.Kind == "function" && s.Name == "Item").ToList();
        Assert.Equal(4, indexerItems.Count);

        // Expression-body readonly indexer (int) — visibility and returnType preserved.
        // 式本体 readonly インデクサ (int) — visibility と returnType を保持。
        var exprReadonlyInt = Assert.Single(indexerItems.Where(s => s.ReturnType == "int" && s.Signature != null && s.Signature.Contains("this[int i]")));
        Assert.Equal("public", exprReadonlyInt.Visibility);
        Assert.Contains("readonly", exprReadonlyInt.Signature);

        // Block-body readonly indexer (string key) — visibility and returnType preserved.
        // ブロック本体 readonly インデクサ (string key) — visibility と returnType を保持。
        var blockReadonlyString = Assert.Single(indexerItems.Where(s => s.ReturnType == "string"));
        Assert.Equal("public", blockReadonlyString.Visibility);
        Assert.Contains("readonly", blockReadonlyString.Signature);

        // Non-readonly baseline indexer (int this[long key]).
        // 非 readonly ベースラインインデクサ (int this[long key])。
        var baselineIntLong = Assert.Single(indexerItems.Where(s => s.ReturnType == "int" && s.Signature != null && s.Signature.Contains("this[long key]")));
        Assert.Equal("public", baselineIntLong.Visibility);
        Assert.DoesNotContain("readonly", baselineIntLong.Signature);

        // Generic readonly indexer (`public readonly T this[int i] { get => _items[i]; }`).
        // ジェネリック readonly インデクサ。
        var genericReadonly = Assert.Single(indexerItems.Where(s => s.ReturnType == "T"));
        Assert.Equal("public", genericReadonly.Visibility);
        Assert.Contains("readonly", genericReadonly.Signature);

        // Baseline: `readonly` methods continue to extract as `function` with the source name.
        // ベースライン: `readonly` メソッドは従来どおり `function` として抽出される。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ReadMe" && s.ReturnType == "int");
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialIndexers()
    {
        // issue #350: `partial` is a valid indexer modifier as of C# 13's extended partial
        // member support. Expression-body (`public partial int this[int i] => _arr[i];`),
        // block-body (`public partial string this[string key] { get => key; }`), and
        // partial-implementation (`public partial int this[long key] => 0;`) shapes must all
        // be captured as `function` rows with C#'s metadata name `Item` and `visibility` /
        // `returnType` populated, not dropped because `partial` is missing from the indexer
        // regex modifier slot. The non-partial baseline indexer must continue to extract as
        // before.
        // issue #350: C# 13 の partial member 拡張で `partial` はインデクサ修飾子として有効。
        // 式本体 (`public partial int this[int i] => _arr[i];`)、ブロック本体
        // (`public partial string this[string key] { get => key; }`)、実装側 partial
        // (`public partial int this[long key] => 0;`) のいずれも、C# メタデータ名 `Item` の
        // `function` 行として `visibility` / `returnType` を保持したまま抽出される必要が
        // ある。インデクサ regex の修飾子スロットに `partial` が無いことで silent drop されて
        // はならない。非 partial なインデクサは従来どおり抽出される。
        var content = """
            namespace Demo;

            public partial class Store
            {
                public partial int this[int i] { get; }

                public partial string this[string key]
                {
                    get;
                }

                public int this[long key] => 0;
            }

            public partial class Store
            {
                private int[] _arr = new int[0];
                private System.Collections.Generic.Dictionary<string, string> _map = new();

                public partial int this[int i] => _arr[i];

                public partial string this[string key]
                {
                    get => _map[key];
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var indexerItems = symbols.Where(s => s.Kind == "function" && s.Name == "Item").ToList();
        Assert.Equal(5, indexerItems.Count);

        // Two partial indexer declarations with `int` return type (declaration + implementation).
        // `int` 戻り値型の partial インデクサ宣言は 2 件 (宣言 + 実装) 検出される。
        var partialIntIndexers = indexerItems.Where(s => s.ReturnType == "int" && s.Signature != null && s.Signature.Contains("this[int i]")).ToList();
        Assert.Equal(2, partialIntIndexers.Count);
        Assert.All(partialIntIndexers, s => Assert.Equal("public", s.Visibility));
        Assert.All(partialIntIndexers, s => Assert.Contains("partial", s.Signature));

        // Two partial indexer declarations with `string` return type (declaration + implementation).
        // `string` 戻り値型の partial インデクサ宣言も 2 件 (宣言 + 実装) 検出される。
        var partialStringIndexers = indexerItems.Where(s => s.ReturnType == "string").ToList();
        Assert.Equal(2, partialStringIndexers.Count);
        Assert.All(partialStringIndexers, s => Assert.Equal("public", s.Visibility));
        Assert.All(partialStringIndexers, s => Assert.Contains("partial", s.Signature));

        // Non-partial baseline indexer (int this[long key]).
        // 非 partial ベースラインインデクサ (int this[long key])。
        var baselineIntLong = Assert.Single(indexerItems.Where(s => s.Signature != null && s.Signature.Contains("this[long key]")));
        Assert.Equal("public", baselineIntLong.Visibility);
        Assert.Equal("int", baselineIntLong.ReturnType);
        Assert.DoesNotContain("partial", baselineIntLong.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialEvents()
    {
        // issue #350: `partial` is a valid event modifier as of C# 14's partial event support.
        // Field-like partial events (`public partial event Action E;`) and accessor-based
        // partial events (`public partial event Action<int> OnLog { add { ... } remove { ... } }`)
        // must be captured as `event` rows with `visibility` / `returnType` populated, not
        // dropped because `partial` is missing from the event-regex modifier slot. Non-partial
        // events must continue to extract as before.
        // issue #350: C# 14 の partial event サポートで `partial` は event 修飾子として有効。
        // field-like partial event (`public partial event Action E;`) と accessor ベースの
        // partial event (`public partial event Action<int> OnLog { add { ... } remove { ... } }`)
        // のいずれも、`event` 行として `visibility` / `returnType` を保持したまま抽出される必要が
        // ある。event regex の修飾子スロットに `partial` が無いことで silent drop されては
        // ならない。非 partial event は従来どおり抽出される。
        var content = """
            namespace Demo;

            public partial class Emitter
            {
                public partial event System.Action Click;
                public partial event System.Action<string> OnLog;
                public event System.Action Plain;
            }

            public partial class Emitter
            {
                public partial event System.Action Click { add { } remove { } }
                public partial event System.Action<string> OnLog { add { } remove { } }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Two partial event declarations with name `Click` (declaration + accessor-body implementation).
        // 名前 `Click` の partial event 宣言が 2 件 (宣言 + アクセサ本体実装) 検出される。
        var clickEvents = symbols.Where(s => s.Kind == "event" && s.Name == "Click").ToList();
        Assert.Equal(2, clickEvents.Count);
        Assert.All(clickEvents, s => Assert.Equal("public", s.Visibility));
        Assert.All(clickEvents, s => Assert.Contains("partial", s.Signature ?? string.Empty));

        // Two partial event declarations with name `OnLog` (generic Action<string>).
        // 名前 `OnLog` の partial event 宣言 (ジェネリック Action<string>) も 2 件検出される。
        var onLogEvents = symbols.Where(s => s.Kind == "event" && s.Name == "OnLog").ToList();
        Assert.Equal(2, onLogEvents.Count);
        Assert.All(onLogEvents, s => Assert.Equal("public", s.Visibility));
        Assert.All(onLogEvents, s => Assert.Contains("partial", s.Signature ?? string.Empty));

        // Non-partial baseline event (`public event System.Action Plain;`) still extracts.
        // 非 partial ベースライン event (`public event System.Action Plain;`) は従来どおり抽出。
        var plain = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "Plain"));
        Assert.Equal("public", plain.Visibility);
        Assert.DoesNotContain("partial", plain.Signature ?? string.Empty);
    }

    [Fact]
    public void Extract_CSharp_MultilinePropertyHeader_DoesNotCreatePhantomFunctionAndKeepsSignature()
    {
        var content = """
            namespace Demo;

            public class Model
            {
                public string
                    SplitName
                    => "x";
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SplitName"));
        Assert.Equal(5, property.StartLine);
        Assert.Equal(7, property.EndLine);
        Assert.Equal("public string SplitName => \"x\";", property.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "SplitName");
    }

    [Fact]
    public void Extract_CSharp_LongGenericMultilinePropertyHeader_KeepsReturnTypeAndSignature()
    {
        var content = """
            namespace Demo;

            public class Model
            {
                public Dictionary<
                    string,
                    List<
                        int
                    >>
                    Count
                    => new();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count"));
        Assert.Equal("Dictionary<string,List<int>>", property.ReturnType);
        Assert.Equal("public Dictionary< string, List< int >> Count => new();", property.Signature);
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Count");
    }

    [Fact]
    public void Extract_CSharp_BraceOnNextLinePropertyHeader_KeepsHeaderSignature()
    {
        var content = """
            namespace Demo;

            public class Model
            {
                public string SplitName
                {
                    get;
                    set;
                }

                public int Count
                { get => 1; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var splitName = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SplitName"));
        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count"));
        Assert.Equal("public string SplitName {", splitName.Signature);
        Assert.Equal("public int Count {", count.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedClassHeader_IncludesBaseListAndWhereClauseInSignature()
    {
        var content = """
            namespace Demo;

            public sealed class Foo<T>
                : BaseFoo<T>, IBar, IBaz
                where T : class, new()
            {
                public Foo(int x) : base(x) { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo<T> : BaseFoo<T>, IBar, IBaz where T : class, new()",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedInterfaceHeader_IncludesBaseListInSignature()
    {
        var content = """
            namespace Demo;

            public interface IFoo<T>
                : IBar<T>,
                  IBaz
                where T : struct
            {
                void Method();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var iface = Assert.Single(symbols.Where(s => s.Kind == "interface" && s.Name == "IFoo"));
        Assert.Equal(
            "public interface IFoo<T> : IBar<T>, IBaz where T : struct",
            iface.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedRecordPrimaryCtorHeader_IncludesCtorParametersAndBaseList()
    {
        var content = """
            namespace Demo;

            public record Point<T>(
                T X,
                T Y)
                : BaseRecord<T>
                where T : INumber<T>;
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var point = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Point"));
        Assert.Equal(
            "public record Point<T>( T X, T Y) : BaseRecord<T> where T : INumber<T>",
            point.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedStructHeader_IncludesBaseListInSignature()
    {
        var content = """
            namespace Demo;

            public readonly struct Value<T>
                : IEquatable<Value<T>>
                where T : IComparable<T>
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var value = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "Value"));
        Assert.Equal(
            "public readonly struct Value<T> : IEquatable<Value<T>> where T : IComparable<T>",
            value.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedTypeHeaderWithSplitGenericConstraints_PreservesNestedConstraintTypes()
    {
        var content = """
            namespace Demo;

            public sealed class Foo<T, U>
                where T : IEnumerable<
                    U>,
                    IComparable<
                    string>
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo<T, U> where T : IEnumerable<U>, IComparable<string>",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedPrimaryCtorHeaderWithWhereInStringDefault_PreservesLiteralWhitespace()
    {
        var content = """
            namespace Demo;

            public sealed class Foo(
                string label = "where X< T >")
                : BaseFoo
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string label = \"where X< T >\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedPrimaryCtorHeaderWithWhereParameterName_PreservesLiteralWhitespace()
    {
        var content = """
            namespace Demo;

            public sealed class Foo(
                string where = "X< T >")
                : BaseFoo
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string where = \"X< T >\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedEnumHeader_IncludesUnderlyingTypeInSignature()
    {
        var content = """
            namespace Demo;

            public enum Kind
                : byte
            {
                A,
                B,
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var kind = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "Kind"));
        Assert.Equal("public enum Kind : byte", kind.Signature);
    }

    [Fact]
    public void Extract_CSharp_NewEnumModifier_ExtractsEnumSymbol()
    {
        // Closes #353: nested `new enum` (member hiding in derived type) must be captured.
        // Modifier order is free, so both `public new enum` and `new public enum` work, and
        // an explicit underlying-type colon must still classify as kind `enum`.
        // Closes #353: 派生型で親のネスト enum を隠蔽する `new enum` は enum としてキャプチャする。
        // 修飾子の順序は自由で、`public new enum` と `new public enum` の両方、
        // 明示的な基底型指定 `: byte` が付いた場合でも kind `enum` として分類する。
        var content = """
            namespace Demo;

            public class Derived : Base
            {
                public new enum Kind { A }
                public new enum KindByte : byte { A }
                new public enum KindReversed { A }
                new enum KindPrivate { A }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Kind" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "KindByte" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "KindReversed" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "KindPrivate");
    }

    [Fact]
    public void Extract_CSharp_NewDelegateModifier_ExtractsDelegateSymbol()
    {
        // Regression for #353 companion: nested `new delegate` (member hiding in derived type)
        // must stay captured. Modifier order is free, so both `public new delegate` and
        // `new public delegate` work.
        // #353 関連の回帰テスト: 派生型で親のネスト delegate を隠蔽する `new delegate` は
        // delegate としてキャプチャし続ける。修飾子の順序は自由で、`public new delegate` と
        // `new public delegate` の両方を受け付ける。
        var content = """
            namespace Demo;

            public class Derived : Base
            {
                public new delegate int Handler(int x);
                new public delegate int Reversed();
                new delegate int PrivateD();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Handler" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Reversed" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "PrivateD");
    }

    [Fact]
    public void Extract_CSharp_SameLineClassHeader_SignatureUnchanged()
    {
        var content = """
            namespace Demo;

            public class Foo : Bar, IBaz
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal("public class Foo : Bar, IBaz", foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedClassHeaderWithLineComment_StripsCommentFromSignature()
    {
        // Wrapped type header with a trailing `// comment` on a base-list or `where` line
        // must not leak comment text into `symbols.signature`. The signature is used by
        // downstream consumers (planned #257 base resolution, #256 type-position
        // references, `impact` / `analyze_symbol` heuristics) that need to parse the base
        // list and `where` clauses; comment bytes in the signature would break them.
        // Closes #382 codex review blocker.
        // 折り返された型ヘッダの base リストや `where` 句の行末に `// comment` が
        // 付いていても、`symbols.signature` にコメント本文が漏れないこと。signature は
        // 下流（#257 の base 解決、#256 の型位置参照、`impact` / `analyze_symbol`
        // ヒューリスティクス）で base リストや `where` 句を解釈するために使われるため、
        // コメントバイトが残ると壊れる。Closes #382 の codex レビュー blocker 対応。
        var content = """
            namespace Demo;

            public sealed class Foo<T>
                : BaseFoo<T>, // primary base
                  IBar,
                  IBaz // diagnostics trait
                where T : class, new() // must be default-constructible
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal("public sealed class Foo<T> : BaseFoo<T>, IBar, IBaz where T : class, new()", foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedClassHeaderWithBlockComment_StripsCommentFromSignature()
    {
        // Same contract as the line-comment variant, for inline `/* ... */` block
        // comments embedded inside a wrapped type header. Closes #382 codex review blocker.
        // 行間や途中に挟まる `/* ... */` ブロックコメントについても同じ契約を固定する。
        // Closes #382 の codex レビュー blocker 対応。
        var content = """
            namespace Demo;

            public class Foo /* annotation */
                : /* base */ Bar,
                  IBaz
                where /* generic */ T : class
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal("public class Foo : Bar, IBaz where T : class", foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedPrimaryCtorHeaderWithStringDefault_PreservesWhitespaceInLiteral()
    {
        // A wrapped primary constructor header that carries a string default with internal
        // double-space must not collapse the literal into a single space. The signature is
        // parsed downstream to recover default values, so collapsing `"a  b"` to `"a b"`
        // would silently rewrite source. Closes #382 codex review iteration 2 blocker.
        // 折り返された primary constructor header に内部 2 連空白を持つ文字列デフォルトが
        // ある場合、リテラル内の空白を潰してはいけない。signature は下流で default 値の
        // 復元に使われるため、`"a  b"` が `"a b"` に潰れると source が書き換わったのと
        // 同じ結果になる。Closes #382 の codex レビュー iteration 2 blocker 対応。
        var content = """
            namespace Demo;

            public sealed class Foo(
                string label = "a  b")
                : BaseFoo
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string label = \"a  b\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedPrimaryCtorHeaderWithVerbatimStringDefault_PreservesWhitespaceInLiteral()
    {
        // Verbatim string (`@"..."`) defaults may contain runs of internal whitespace that
        // must survive signature reconstruction verbatim. Closes #382 codex review iteration
        // 2 blocker.
        // verbatim 文字列（`@"..."`）のデフォルトは内部の空白列をそのまま残す必要がある。
        // Closes #382 の codex レビュー iteration 2 blocker 対応。
        var content = """"
            namespace Demo;

            public sealed class Foo(
                string path = @"C:\tmp\   spaces")
                : BaseFoo
            {
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string path = @\"C:\\tmp\\   spaces\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedPrimaryCtorHeaderWithRawStringDefault_PreservesWhitespaceInLiteral()
    {
        // Raw string literals (`"""..."""`) in a wrapped primary constructor default must
        // preserve internal whitespace verbatim. Closes #382 codex review iteration 2
        // blocker.
        // raw 文字列リテラル（`"""..."""`）を持つ primary constructor デフォルトについて
        // も、内部空白を verbatim に保つこと。Closes #382 の codex レビュー iteration 2
        // blocker 対応。
        var content = """"
            namespace Demo;

            public sealed class Foo(
                string tag = """a   b""")
                : BaseFoo
            {
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string tag = \"\"\"a   b\"\"\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedPrimaryCtorHeaderWithMultilineRawStringDefault_PreservesNewlinesAndIndent()
    {
        // A raw string default that spans multiple physical lines must keep its `\n`
        // characters and the per-line leading indentation verbatim. The previous
        // line-by-line `Trim()` + `' '` join in BuildCSharpTypeHeaderSignature destroyed
        // both, collapsing `"""\n    a  \n    b\n    """` into `""" a b """`. Closes #382
        // codex review iteration 3 blocker.
        // 折り返された primary constructor のデフォルトに multi-line raw string を置くと、
        // 改行と各行先頭のインデントを verbatim に保持しなければならない。以前の line-by-line
        // `Trim()` + ' ' 連結は両方を潰し `"""\n    a  \n    b\n    """` を `""" a b """`
        // に圧縮していた。Closes #382 の codex レビュー iteration 3 blocker 対応。
        var content = """"
            namespace Demo;

            public sealed class Foo(
                string text = """
                a  b
                c
                """)
                : BaseFoo
            {
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string text = \"\"\"\n    a  b\n    c\n    \"\"\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedMultilineRawStringDefault_NormalizesCrlfToLf()
    {
        // Content split on '\n' leaves trailing '\r' on every line for CRLF-terminated
        // sources (Windows CI with autocrlf=true, files saved from VS, etc.). The header
        // slice builder must strip that trailing '\r' so inter-line separators stay '\n'
        // regardless of line endings. Without this normalization the signature for a
        // multi-line raw string default would carry `\r\n` between lines on Windows and
        // `\n` on Linux / macOS, which breaks signature equality across OSes and broke
        // the Windows CI run of #382.
        // `\n` で分割した場合、CRLF 終端のソースでは各行末に '\r' が残る
        // （autocrlf=true の Windows CI、VS で保存したファイルなど）。header スライス組み
        // 立て側で末尾 '\r' を落とさないと、行間セパレータが OS に依存して `\r\n` / `\n`
        // になり、signature の一致判定が崩れる。これは #382 の Windows CI 失敗の原因でも
        // あった。
        var content =
            "namespace Demo;\r\n" +
            "\r\n" +
            "public sealed class Foo(\r\n" +
            "    string text = \"\"\"\r\n" +
            "    a  b\r\n" +
            "    c\r\n" +
            "    \"\"\")\r\n" +
            "    : BaseFoo\r\n" +
            "{\r\n" +
            "}\r\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo( string text = \"\"\"\n    a  b\n    c\n    \"\"\") : BaseFoo",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_MultilineEnumMemberSpan_NormalizesCrlfToLf()
    {
        // Enum members whose value expression or attribute block crosses physical lines go
        // through GetSourceSpanText via TryAddCSharpEnumMemberFromSpan. Content is split on
        // '\n', so CRLF sources leave '\r' on every non-final line and the concatenated
        // signature would contain '\r\n' between physical lines on Windows. Pin this to '\n'
        // so signature equality is OS-independent (#405 follow-up to #382).
        // enum メンバーの値式や属性ブロックが行を跨ぐ場合、TryAddCSharpEnumMemberFromSpan
        // 経由で GetSourceSpanText に入る。content は '\n' で分割しているため、CRLF ソース
        // では各行末に '\r' が残り、Windows では signature に '\r\n' が混入していた。OS
        // 差分で signature が変わらないよう '\n' に揃える（#382 に続く #405 対応）。
        var content =
            "public enum Modes\r\n" +
            "{\r\n" +
            "    Red = 1 |\r\n" +
            "        2,\r\n" +
            "    Blue = 3,\r\n" +
            "}\r\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var red = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "Red"));
        Assert.NotNull(red.Signature);
        Assert.DoesNotContain('\r', red.Signature);
        Assert.Contains("\n", red.Signature);
    }

    [Fact]
    public void Extract_CSharp_MultilineRecordPrimaryComponents_SurviveCrlfInput()
    {
        // Record primary constructors with a wrapped component list feed
        // CollectRecordDeclarationText, which appends each physical line with a '\n' prefix.
        // Without CRLF normalization, the collected declaration text carries '\r' in the
        // middle, which — while parsing still scans only for structural characters — breaks
        // text-equality assumptions downstream. Pin the property extraction to succeed on
        // CRLF input so the fix stays tied to observable behavior (#405 follow-up to #382).
        // record の primary constructor で component リストが行を跨ぐ場合、
        // CollectRecordDeclarationText が各行を '\n' 接頭辞で連結する。CRLF 正規化がないと
        // collected text に '\r' が混じり、parsing 自体は構造文字しか見ないものの、下流の
        // 文字列比較前提が崩れる。CRLF 入力でも property 抽出が壊れないことを固定し、修正
        // を観測可能な挙動に紐づける（#382 に続く #405 対応）。
        var content =
            "namespace App;\r\n" +
            "\r\n" +
            "public record Point(\r\n" +
            "    int X,\r\n" +
            "    int Y);\r\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var x = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "X" && s.ContainerName == "Point"));
        Assert.Equal("int", x.ReturnType);
        var y = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Point"));
        Assert.Equal("int", y.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_ClassPrimaryConstructorParameters_AreIndexedAsContainedProperties()
    {
        var content = """
            namespace App;

            public class Worker(string name, int id)
            {
                public string Describe() => $"{name}#{id}";
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "name" && s.ContainerName == "Worker"));
        Assert.Equal("class", name.ContainerKind);
        Assert.Equal("string", name.ReturnType);
        Assert.Equal("string name", name.Signature);

        var id = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "id" && s.ContainerName == "Worker"));
        Assert.Equal("int", id.ReturnType);
        Assert.Equal("int id", id.Signature);
    }

    [Fact]
    public void Extract_CSharp_StructPrimaryConstructorParameters_AreIndexedAsContainedProperties()
    {
        var content = """
            namespace App;

            internal readonly struct Range(int start, int length)
            {
                public int End => start + length;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var start = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "start" && s.ContainerName == "Range"));
        Assert.Equal("struct", start.ContainerKind);
        Assert.Equal("int", start.ReturnType);

        var length = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "length" && s.ContainerName == "Range"));
        Assert.Equal("struct", length.ContainerKind);
        Assert.Equal("int", length.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_GenericConstraintNewCall_IsNotPrimaryConstructorParameter()
    {
        var content = """
            public class Factory<T> where T : new()
            {
                public T Create() => new T();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.ContainerName == "Factory" && s.Name == "new");
    }

    [Fact]
    public void Extract_CSharp_WrappedHeaderWithInterpolationHoleContainingNestedVerbatim_PreservesInnerLiteral()
    {
        // An interpolation hole in an outer `$"..."` must be classified as Code so the
        // hole contents are lex-aware — in particular, a nested `@"..."` inside the hole
        // must stay in Verbatim mode and preserve any internal double-space, while the
        // outer `$"..."` literal content after the hole is still preserved verbatim.
        // Previously, once we entered String mode we exited on the first unescaped `"`,
        // which meant `$"{@"a  b"}  c"` re-entered Code mode at `@"` and collapsed
        // `a  b` to `a b`. Closes #382 codex review iteration 3 blocker.
        // 外側 `$"..."` の補間ホールは Code として分類し、ホール内は lex-aware に処理する
        // 必要がある。ホール内の `@"..."` は Verbatim モードとして扱い、内部の 2 連空白を
        // 保持することを固定する。以前は String に入った時点で次の `"` で即 Code に戻って
        // いたため、`$"{@"a  b"}  c"` が `a  b` → `a b` に潰れていた。
        // Closes #382 の codex レビュー iteration 3 blocker 対応。
        var content = """
            namespace Demo;

            public sealed class Foo
                : BaseFoo($"{@"a  b"}  c")
            {
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Foo"));
        Assert.Equal(
            "public sealed class Foo : BaseFoo($\"{@\"a  b\"}  c\")",
            foo.Signature);
    }

    [Fact]
    public void Extract_CSharp_NonPartialBlockStyleProperty_AccessorVariants_AreCaptured()
    {
        // Regression coverage for #229: non-partial properties with `{` on the next line
        // and each major accessor body style (auto, expression-bodied arrows, `init`,
        // full method bodies) must all surface as property symbols with header-aligned
        // start lines and end lines spanning the closing brace.
        // #229 の回帰ガード: 非 partial プロパティで `{` が次行に来るすべての代表的な
        // accessor 本体スタイル（auto / `get =>` `set =>` / `init` / フル本体）を、
        // header 行を起点に閉じブレースまでを含む property として抽出し続けることを固定する。
        var content = """
            namespace Demo;

            public class Model
            {
                public string BlockAuto
                {
                    get;
                    set;
                }

                public string BlockFull
                {
                    get => _x;
                    set => _x = value;
                }

                public int BlockInit
                {
                    get;
                    init;
                }

                public string BlockWithLogic
                {
                    get
                    {
                        return _x;
                    }
                    set
                    {
                        _x = value ?? "";
                    }
                }

                private string _x = "";
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var blockAuto = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "BlockAuto"));
        Assert.Equal(5, blockAuto.StartLine);
        Assert.Equal(9, blockAuto.EndLine);

        var blockFull = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "BlockFull"));
        Assert.Equal(11, blockFull.StartLine);
        Assert.Equal(15, blockFull.EndLine);

        var blockInit = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "BlockInit"));
        Assert.Equal(17, blockInit.StartLine);
        Assert.Equal(21, blockInit.EndLine);

        var blockWithLogic = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "BlockWithLogic"));
        Assert.Equal(23, blockWithLogic.StartLine);
        Assert.Equal(33, blockWithLogic.EndLine);

        // None of the block-style variants should leak as phantom functions with the same name.
        // どのブロックスタイルも同名の phantom function として重複抽出されてはいけない。
        Assert.DoesNotContain(symbols, s => s.Kind == "function"
            && (s.Name == "BlockAuto" || s.Name == "BlockFull" || s.Name == "BlockInit" || s.Name == "BlockWithLogic"));
    }

    [Fact]
    public void Extract_CSharp_MultilineExpressionBodiedProperty_KeepsExpressionBodyRange()
    {
        var content = """
            namespace Demo;

            public partial class Model
            {
                public partial int Count
                    => DateTime.Now.Day switch
                    {
                        > 15 => 2,
                        _ => 1
                    };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count"));
        Assert.Equal(5, count.StartLine);
        Assert.Equal(10, count.EndLine);
        Assert.Equal("public partial int Count => DateTime.Now.Day switch { > 15 => 2, _ => 1 };", count.Signature);
    }

    [Fact]
    public void Extract_CSharp_PartialPropertyImplementation_WithAccessorAttribute_IsDetected()
    {
        var content = """
            namespace Demo;

            public partial class Model
            {
                public partial string Name { get; set; }
            }

            public partial class Model
            {
                public partial string Name { [System.Obsolete] get => "x"; set { } }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(2, symbols.Count(s => s.Kind == "property" && s.Name == "Name"));
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name" && s.Signature != null && s.Signature.Contains("[System.Obsolete]", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CSharp_PartialPropertyImplementation_WithMultilineAccessorAttribute_IsDetected()
    {
        var content = """
            namespace Demo;

            public partial class Model
            {
                public partial string Name
                {
                    [System.Obsolete(
                        "x"
                    )]
                    get => "x";
                    set { }
                }

                public int Other => 1;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Other");
    }

    [Fact]
    public void Extract_CSharp_PropertyWithFirstAccessorVisibility_IsDetected()
    {
        // issue #332: `public int X { internal get; set; }` と、`{ private get; public set; }` /
        // `{ protected internal get; set; }` / `{ private protected get; set; }` のように
        // 先頭の accessor に独自の可視性修飾子が付く形も property として抽出されること。
        // accessor の属性プレフィックス (`[Obsolete]` / `[field: NonSerialized]`)、
        // accessor 本体付き (`internal get { ... } set { ... }`)、単独の accessor
        // (`{ private init; }`) も同じパスで拾えることを併せて固定する。
        // issue #332: properties whose FIRST accessor carries its own visibility
        // modifier (`{ internal get; set; }`, `{ private get; public set; }`,
        // `{ protected internal get; set; }`, `{ private protected get; set; }`)
        // must still be captured as properties. Also pins attribute-prefixed
        // accessors (`[Obsolete]` / `[field: NonSerialized]`), body-bearing
        // accessors (`internal get { ... } set { ... }`), and a standalone
        // accessor with visibility (`{ private init; }`).
        var content = """
            using System;
            namespace AccessorVis;

            public class Svc
            {
                public int PubPrivSet { get; private set; }
                public string Name { get; private init; } = "";
                public int Count { get; protected set; }
                public int Internal { internal get; set; }
                public int PrivGetPubSet { private get; public set; }
                public int ProtIntGet { protected internal get; set; }
                public int PrivProtGet { private protected get; set; }
                public int AttrFirstAccessor { [Obsolete] internal get; set; }
                public int FieldAttrFirstAccessor { [field: NonSerialized] private get; set; }
                public int BodyBearing { internal get { return 0; } set { _ = value; } }
                public int PrivInitOnly { private init; }
                public int Prop => 0;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var expected = new[]
        {
            "PubPrivSet",
            "Name",
            "Count",
            "Internal",
            "PrivGetPubSet",
            "ProtIntGet",
            "PrivProtGet",
            "AttrFirstAccessor",
            "FieldAttrFirstAccessor",
            "BodyBearing",
            "PrivInitOnly",
            "Prop",
        };
        foreach (var name in expected)
            Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == name));

        // The first-accessor-visibility rows must not leak as phantom functions either.
        // 先頭 accessor 可視性付きの行が phantom function としても重複抽出されないこと。
        var phantomCandidates = new[]
        {
            "Internal",
            "PrivGetPubSet",
            "ProtIntGet",
            "PrivProtGet",
            "AttrFirstAccessor",
            "FieldAttrFirstAccessor",
            "BodyBearing",
            "PrivInitOnly",
        };
        Assert.DoesNotContain(symbols, s => s.Kind == "function"
            && Array.IndexOf(phantomCandidates, s.Name) >= 0);
    }

    [Fact]
    public void Extract_CSharp_DetectsInlineAttributedProperty()
    {
        var content = """
            using System.Text.Json.Serialization;

            public class UserDto
            {
                [JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserDto");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "FullName");
    }

    [Fact]
    public void Extract_CSharp_DetectsInlinePropertyTargetAttributeWithWhitespace()
    {
        var content = """
            using System.Text.Json.Serialization;

            public class UserDto
            {
                [property : JsonPropertyName("full_name")] public string FullName { get; set; } = string.Empty;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "FullName");
    }

    [Fact]
    public void Extract_CSharp_RequiredProperties_PreserveRequiredAndInitInSignature()
    {
        var content = """
            public class User
            {
                public required string Name { get; init; }
                public required int Age { get; set; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Name"));
        var age = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Age"));

        Assert.Equal("public required string Name { get; init; }", name.Signature);
        Assert.Equal("public required int Age { get; set; }", age.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsNoVisibilityMembers()
    {
        // Classes/methods without explicit visibility (internal by default)
        // 明示的な visibility のないクラス/メソッド（デフォルト internal）
        var content = "namespace MyApp;\n\nclass InternalClass\n{\n    static void Helper() { }\n}\n\nstatic class Utils\n{\n    static int Add(int a, int b) => a + b;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "InternalClass");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Helper");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Utils");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Add");
    }

    [Fact]
    public void Extract_CSharp_DetectsPrimaryConstructors()
    {
        // C# 12 primary constructors on class, struct, record
        // C# 12 のクラス・構造体・record の primary constructor
        var content = "public class Service(ILogger logger, IDb db)\n{\n}\n\npublic struct Point(double x, double y);";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Point");
        // Signature should contain the parameter list / シグネチャにパラメータリストが含まれるべき
        var service = symbols.First(s => s.Name == "Service");
        Assert.Contains("ILogger logger", service.Signature);
    }

    [Fact]
    public void Extract_CSharp_AttributeDoesNotBlockNextLine()
    {
        // [Attribute] on the line before class/method should not prevent extraction
        // [Attribute] がクラス/メソッドの前行にあっても抽出を妨げないこと
        var content = "[Serializable]\npublic class Config\n{\n    [Obsolete(\"Use V2\")]\n    public void OldMethod() { }\n\n    [HttpGet(\"/api\")]\n    public async Task<IActionResult> GetItems() { return null; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OldMethod");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetItems");
    }

    [Fact]
    public void Extract_CSharp_TargetedAttributeDeclarations_DoNotBecomePhantomFunctions()
    {
        var content = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;

            [assembly: CLSCompliant(true)]
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            [module: SkipLocalsInit]

            public class Fixture
            {
                [return: MarshalAs(UnmanagedType.Bool)]
                public bool Method([param: MarshalAs(UnmanagedType.Bool)] bool value) => value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Fixture");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Method");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "CLSCompliant");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "AssemblyVersion");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "SkipLocalsInit");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "MarshalAs");
    }

    [Fact]
    public void Extract_CSharp_MultiSectionAttribute_DoesNotLeakTrailingAttributeNamesAsPhantoms()
    {
        // [A, B(args)] multi-section attributes must not leak B/C as phantom `function` symbols.
        // The attribute-stripper must blank out the whole bracket group even when it consumes the entire line,
        // otherwise the method regex latches onto the content after the comma.
        // xUnit/MSTest/ASP.NET/EF attribute conventions rely heavily on this shape.
        // 複数セクション属性 [A, B(args)] の 2つ目以降の属性名が phantom function として漏れないこと。
        var content = """
            using System;
            using System.Diagnostics;

            namespace MultiSectionAttr;

            public class Svc
            {
                [Obsolete, Conditional("DEBUG")]
                public void A() {}

                [Obsolete][Conditional("DEBUG")]
                public void B() {}

                [Obsolete] [Conditional("DEBUG")]
                public void C() {}

                [Obsolete, Conditional("DEBUG")]
                [System.ComponentModel.Description("x")]
                public void D() {}

                [Obsolete, Conditional("DEBUG"), System.ComponentModel.Description("y")]
                public void E() {}

                [Fact, Trait("cat", "io")]
                public void F() {}
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Svc");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "C");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "D");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "E");
        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "F");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Conditional");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Description");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Trait");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Obsolete");
    }

    [Fact]
    public void Extract_CSharp_MultiSectionAttributeOnClassOrProperty_DoesNotLeakPhantoms()
    {
        // Comma-separated attribute sections on types and properties (EF/ASP.NET/DataAnnotations shape)
        // must stay clean as well — [Required, StringLength(50), Column("name")] etc.
        // 型・プロパティに付く [Required, StringLength(50), Column("name")] 形でも phantom が出ないこと。
        var content = """
            using System.ComponentModel.DataAnnotations;
            using System.ComponentModel.DataAnnotations.Schema;

            namespace Data;

            [Serializable, ApiController]
            public class User
            {
                [Required, StringLength(50), Column("name")]
                public string Name { get; set; } = "";

                [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
                public int Id { get; set; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "User");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Id");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "StringLength");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Column");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "DatabaseGenerated");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "ApiController");
    }

    [Fact]
    public void Extract_CSharp_WrappedConstructorInitializer_DoesNotLeakBaseOrThisAsPhantoms()
    {
        // Wrapped `: base(...)` / `: this(...)` initializers must not surface as phantom
        // `function base` / `function this` symbols. The C# returnType char class includes `:`
        // to support alias-qualified type names like `Alias::Type`, so a wrapped initializer line
        // like `    : base(s, 0)` could otherwise tokenize as returnType=`:` + name=`base` + paren.
        // Both the first-char `(?![?:])` guard and the name-level `(?!(?:base|this)\b)` guard
        // must cooperate to block it. Closes #331.
        // ラップされた `: base(...)` / `: this(...)` 初期化子行が `function base` / `function this`
        // の phantom として漏れないことを担保する。Closes #331.
        var content = """
            namespace CtorChain;

            public class Base
            {
                public Base() { }
                public Base(int x) { }
                public Base(string s, int n) { }
            }

            public class Derived : Base
            {
                public Derived(int x) : base(x) { }

                public Derived(string s)
                    : base(s, 0)
                {
                }

                public Derived() : this(0) { }

                public Derived(int a, int b)
                    : this(a)
                {
                }

                public Derived(double d) : base((int)d, "d") => System.Console.WriteLine(d);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Base");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Derived");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "base");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "this");
        // All five Derived constructors should still be captured / 5 つのコンストラクタは正しく取得できること
        Assert.Equal(5, symbols.Count(s => s.Kind == "function" && s.Name == "Derived"));
    }

    [Fact]
    public void Extract_CSharp_LinqQueryExpressionContinuations_DoNotLeakPhantoms()
    {
        // LINQ query-expression continuation lines with qualified method calls
        // (e.g. `where Validator.Check(x)`, `select Mapper.Convert(x)`, `orderby Math.Abs(x)`)
        // must not fire the explicit-interface-implementation regex as
        // `returnType=<linq-keyword>` + `interface=<qualifier>` + `name=<member>`.
        // Closes #377.
        // LINQ 式の continuation 行（`where Validator.Check(x)` など）が、明示的インターフェース実装
        // regex の returnType+qualifier+name 形として一致し phantom function を生まないこと。Closes #377.
        var content = """
            using System.Linq;
            using System.Collections.Generic;

            namespace LinqPhantom;

            public static class Validator { public static bool Check(int x) => x > 0; }
            public static class Mapper { public static string Convert(int x) => x.ToString(); }

            public class Svc
            {
                public void Query()
                {
                    var list = new List<int> { 1, 2, 3 };

                    var q1 = from x in list
                             where Validator.Check(x)
                             select x;

                    var q2 = from x in list
                             select Mapper.Convert(x);

                    var q3 = from x in list
                             orderby Math.Abs(x)
                             select x;

                    // Exercise line-leading `group`, `by`, and `into` so the guard
                    // covers each keyword individually instead of only the q4 opener.
                    // 行頭 `group` / `by` / `into` を個別に踏ませ、q4 先頭だけで抜けないようにする。
                    var q4 = from x in list
                             group x
                             by Helper.Key(x)
                             into g
                             select g;

                    // Exercise line-leading `join`, `on`, and `equals` so the guard
                    // covers each keyword individually instead of only the q5 opener.
                    // 行頭 `join` / `on` / `equals` を個別に踏ませ、q5 先頭だけで抜けないようにする。
                    var q5 = from x in list
                             join y in list
                             on Helper.Key(x)
                             equals Helper.Key(y)
                             select x;

                    var q6 = from x in list
                             let doubled = Helper.Double(x)
                             select doubled;

                    // Exercise line-leading `ascending` and `descending` so the guard
                    // covers them even when an `orderby` clause wraps onto its own line.
                    // 行頭 `ascending` / `descending` を個別に踏ませ、`orderby` が折り返したときも抜けないようにする。
                    var q7 = from x in list
                             orderby Helper.Key(x)
                             ascending
                             select x;

                    var q8 = from x in list
                             orderby Helper.Key(x)
                             descending
                             select x;
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Real symbols should survive / 実体のシンボルは残る
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Validator");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Mapper");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Svc");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Query");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Check" && s.ContainerName == "Validator");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Convert" && s.ContainerName == "Mapper");

        // No phantom `function` symbols should appear inside the Query body / Query 本体から phantom が出ないこと
        var phantomNames = new[] { "Abs", "Key", "Double" };
        foreach (var name in phantomNames)
        {
            Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == name);
        }

        // Check and Convert must only be declared once each (on their real definition lines), not duplicated from LINQ continuations.
        // Check と Convert は定義行の1個ずつだけで、LINQ continuation からの重複が出ないこと。
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "Check"));
        Assert.Equal(1, symbols.Count(s => s.Kind == "function" && s.Name == "Convert"));
    }

    [Fact]
    public void Extract_CSharp_ContextualKeywordWithTupleSuffixReturn_DoesNotLeakCtorRegexPhantom()
    {
        // Before #349, `public required (int, int) R1 { get; init; }` / `public partial (int, int)? P1();`
        // / `public readonly (int, int)? M() => null;` could be claimed by the ctor regex
        // (`^\s*visibility\s+\w+\s*\(`) with the modifier keyword captured as the ctor name, emitting
        // phantom `function required` / `function partial` / `function readonly` rows and silently
        // dropping the real property/method. The ctor regex now adds a negative lookahead at the
        // opening paren that rejects lines where the matching `)` is followed by an identifier +
        // `{` / `(` / `=>` (with optional `?` / `[]` tuple suffixes in between), so the more specific
        // method/property regexes get a chance to match and no phantom is emitted. Closes #349.
        // 修飾子キーワード + tuple-suffix 戻り値の行を ctor regex が greedy に喰い、
        // modifier キーワード自体を ctor 名として拾ってしまう現象に対するガード。
        // ctor regex の開き括弧の直後に否定先読みを入れ、「対応する `)` のあとに
        // 識別子 + `{` / `(` / `=>`（間に `?` / `[]` の tuple サフィックスを許す）が続く行」を
        // 弾くようにしたので、method / property 側の regex に先を譲り phantom
        // `function required` / `function partial` / `function readonly` が出ない
        // ことを担保する。Closes #349.
        var content = """
            namespace ModifierPhantom;

            public partial class A
            {
                public partial (int, int)? P1();
                public partial (int, int)[] P2();
            }

            public class B
            {
                public required (int, int) R1 { get; init; }
                public required (int, int)? R2 { get; init; }
            }

            public class D
            {
                public readonly struct E
                {
                    public readonly (int, int)? M() => null;
                }
            }

            public class F
            {
                public F() { }
                public F(int x) { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // No phantom rows whose name is a modifier keyword / 修飾子キーワードを name にした phantom は出ない。
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "partial");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "required");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "readonly");

        // Real members are captured / 本物のメンバーが拾えていること。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "P1");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "P2");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "R1");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "R2");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M");

        // Baseline constructors must still be captured / 通常のコンストラクタは引き続き拾えること。
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "F"));
    }

    [Fact]
    public void Extract_CSharp_MultilineGenericMethodOverloadsKeepDistinctSignatures()
    {
        var content = """
            public class Service
            {
                public void M<T>(
                    T value)
                {
                }

                public void M<T,
                    U>(
                    T first,
                    U second)
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var overloads = symbols
            .Where(s => s.Kind == "function" && s.Name == "M")
            .OrderBy(s => s.StartLine)
            .ToArray();

        Assert.Equal(2, overloads.Length);
        Assert.Equal("public void M<T>( T value) {", overloads[0].Signature);
        Assert.Equal("public void M<T, U>( T first, U second) {", overloads[1].Signature);
        Assert.NotEqual(overloads[0].Signature, overloads[1].Signature);
    }

    [Fact]
    public void Extract_CSharp_CtorRegex_StillCapturesAllValidCtorForms()
    {
        // The #349 fix tightens the ctor regex with a negative lookahead that rejects lines where
        // the matching `)` is followed by `IDENT { / IDENT ( / IDENT =>`. Any realistic ctor form
        // must still be captured after the fix — otherwise we would silently drop real ctors to
        // block phantom ones. This test locks in every major ctor form: brace body, expression
        // body, `: base(...)` / `: this(...)` initializers, `extern` declaration ending in `;`,
        // multi-line signature split across lines, and tuple parameter. A regression here means
        // the lookahead is too aggressive.
        // #349 の修正で ctor regex に否定先読み（閉じ括弧の後に `IDENT { / IDENT ( / IDENT =>`
        // が続く行を弾く）を足した。phantom を止めるために本物の ctor を落とすと本末転倒なので、
        // 主要な ctor 記法（brace 本体 / 式本体 / `: base(...)` / `: this(...)` 初期化子 /
        // `;` で終わる extern 宣言 / 複数行に分かれたシグネチャ / tuple パラメータ）が全て
        // 引き続き拾えることをここで担保する。これが壊れたら lookahead が強すぎるサイン。
        var content = """
            namespace CtorForms;

            public class Brace
            {
                public Brace() { }
                public Brace(int x) { }
            }

            public class ExpressionBody
            {
                public ExpressionBody() => System.Console.WriteLine();
            }

            public class WithInitializer
            {
                public WithInitializer() : this(0) { }
                public WithInitializer(int x) : base() { }
            }

            public class Extern
            {
                public extern Extern();
                public extern Extern(int x);
            }

            public class MultiLine
            {
                public MultiLine(
                    int x,
                    int y)
                {
                }
            }

            public class TupleParam
            {
                public TupleParam((int, int) t) { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Brace"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ExpressionBody");
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "WithInitializer"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Extern"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MultiLine");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TupleParam");
    }

    [Fact]
    public void Extract_CSharp_ContextualKeywordWithWhitespacedTupleSuffix_DoesNotLeakCtorRegexPhantom()
    {
        // Follow-up to #349: the initial positional-lookahead fix only rejected tuple suffixes
        // directly abutting `)` (e.g. `(int, int)[]`), so legal C# that puts whitespace between
        // `)` and the suffix token (`(int, int) []`, `(int, int) ?`, `(int, int)  ?`) fell
        // through to the ctor regex and reintroduced phantom `function required` / `function
        // readonly` / `function static` rows while dropping the real property / method. Both the
        // ctor lookahead and CSharpTypePattern now share CSharpTupleSuffixPattern, which allows
        // whitespace between `)` / identifier and each suffix token, so these formatting
        // variants are rejected as ctor shapes via the lookahead and accepted as property /
        // method shapes via the upstream rows.
        // #349 のフォローアップ。初回の位置検査修正では `)` とサフィックストークンが密着した形
        // （`(int, int)[]`）しか弾けず、`)` と `[]` / `?` の間に空白を置いた合法な書式
        // （`(int, int) []` / `(int, int) ?` / `(int, int)  ?`）は ctor regex に落ち、
        // phantom `function required` / `function readonly` / `function static` が再発し
        // 本来の property / method が silent drop していた。CSharpTupleSuffixPattern を
        // ctor 否定先読みと CSharpTypePattern で共有し、`)` や識別子と各サフィックストークンの
        // 間に空白を許容することで、これらの整形バリエーションも ctor 形状として弾きつつ
        // 上流の property / method 行で本物のシンボルとして拾えるようになる。
        var content = """
            namespace ModifierPhantomSpaced;

            public partial class SpacedHost
            {
                public required (int, int) [] R4 { get; init; }
                public readonly (int, int) ? F4 = null;
                public static (int, int) ? M3() => default;
                public partial (int, int)  ? P5 { get; init; }
            }

            public readonly struct SpacedStruct
            {
                public readonly (int, int) ? M4() => null;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // No phantom rows whose name is a modifier keyword even when whitespace sits between
        // `)` and the tuple suffix. / `)` とサフィックスの間に空白があっても、修飾子キーワードを
        // name にした phantom 行は出ない。
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "required");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "readonly");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "static");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "partial");

        // Real members are still captured with the correct kinds. / 本物のメンバーが正しい kind で拾えていること。
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "R4");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "F4");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M3");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P5");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M4");
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordVariants()
    {
        // record, record class, record struct with various modifiers
        var content = "public record UserDto(string Name, int Age);\npublic sealed record class Config(string Key, string Value);\ninternal readonly record struct Point(double X, double Y);";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "UserDto");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Config");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Point");
        // Signature should contain parameters / シグネチャにパラメータが含まれるべき
        var userDto = symbols.First(s => s.Name == "UserDto");
        Assert.Contains("string Name", userDto.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordPrimaryComponentsAsProperties()
    {
        var content = """
            namespace App;

            public record Point(int X, int Y);
            public readonly record struct Vec3(double X, double Y, double Z);
            public record Animal(string Name);
            public record Dog(string Name, string Breed) : Animal(Name);
            public record Options(
                string Host,
                int Port) { public bool UseTls { get; init; } = true; }
            public record Container<T>(T Value, int Count) where T : class;
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var pointX = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "X" && s.ContainerName == "Point"));
        Assert.Equal("int", pointX.ReturnType);
        Assert.Equal("class", pointX.ContainerKind);
        Assert.Equal("App.Point", pointX.ContainerQualifiedName);
        Assert.Equal("public", pointX.Visibility);

        var vec3Z = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Z" && s.ContainerName == "Vec3"));
        Assert.Equal("double", vec3Z.ReturnType);
        Assert.Equal("struct", vec3Z.ContainerKind);

        var dogBreed = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Breed" && s.ContainerName == "Dog"));
        Assert.Equal("string", dogBreed.ReturnType);

        var optionsHost = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Host" && s.ContainerName == "Options"));
        Assert.Equal("string", optionsHost.ReturnType);
        Assert.Contains("string Host", optionsHost.Signature);
        Assert.Equal(8, optionsHost.Line);
        var optionsRecord = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Options"));
        Assert.Equal(9, optionsRecord.EndLine);

        var containerValue = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Value" && s.ContainerName == "Container"));
        Assert.Equal("T", containerValue.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsLongAndCommentedRecordPrimaryComponentsAsProperties()
    {
        var componentLines = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"    int P{i},"));
        var content = $$"""
            namespace App;

            public record Big(
            {{componentLines}}
                int Tail);

            public record Point(
                int X /* separator, comment */,
                // the next component must still parse
                int Y);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P1" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P30" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Tail" && s.ContainerName == "Big");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.ContainerName == "Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Point");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "separator" && s.ContainerName == "Point");

        var tail = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Tail" && s.ContainerName == "Big"));
        Assert.Equal(34, tail.Line);
        var bigRecord = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Big"));
        Assert.Equal(34, bigRecord.EndLine);

        var pointY = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Point"));
        Assert.Equal(39, pointY.Line);
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordPrimaryComponentsWithComparisonDefaults()
    {
        var content = """
            public record Threshold(bool Enabled = 1 < 2, int Count = 0);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var enabled = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Enabled" && s.ContainerName == "Threshold"));
        Assert.Equal("bool", enabled.ReturnType);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count" && s.ContainerName == "Threshold"));
        Assert.Equal("int", count.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsRecordPrimaryComponentsWithTightComparisonDefaults()
    {
        var content = """
            public record Threshold(bool Enabled = left<right, int Count = 0);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var enabled = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Enabled" && s.ContainerName == "Threshold"));
        Assert.Equal("bool", enabled.ReturnType);

        var count = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Count" && s.ContainerName == "Threshold"));
        Assert.Equal("int", count.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DoesNotInjectRecordPrimaryComponentsIntoEarlierSameNamedClass()
    {
        var content = """
            namespace A
            {
                public class Point {}
            }

            namespace B
            {
                public record Point(int X);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "X" && s.ContainerQualifiedName == "A.Point");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.ContainerQualifiedName == "B.Point");
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordStructDeclarationRange()
    {
        var content = """
            namespace App;

            public readonly record struct Bodyless(
                int X,
                int Y);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var bodyless = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "Bodyless"));
        Assert.Equal(5, bodyless.EndLine);

        var y = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Y" && s.ContainerName == "Bodyless"));
        Assert.Equal(5, y.Line);
        Assert.Equal("App.Bodyless", y.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordBaseAndWhereDeclarationRange()
    {
        var content = """
            namespace App;

            public record Animal(string Name);

            public record Dog(
                string Name)
                : Animal(Name);

            public record Box<T>(
                T Value)
                where T : class;
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var dog = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Dog"));
        Assert.Equal(7, dog.EndLine);

        var box = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Box"));
        Assert.Equal(11, box.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordBaseComparisonDeclarationRange()
    {
        var content = """
            namespace App;

            public record Child(int X)
                : Base(1 < 2);

            public record Base(bool Flag);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal(4, child.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsBodylessRecordTightBaseComparisonDeclarationRange()
    {
        var content = """
            namespace App;

            public record Child(int X)
                : Base(left<right);

            public record Base(bool Flag);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var child = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Child"));
        Assert.Equal(4, child.EndLine);
    }

    [Fact]
    public void Extract_CSharp_DetectsEmptyBodylessRecordDeclarationRange()
    {
        var content = """
            namespace Repro;

            public record Empty(
            )
                : Base();

            public record Base();

            public record struct EmptyStruct(
            );
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var empty = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Empty"));
        Assert.Equal(5, empty.EndLine);

        var emptyStruct = Assert.Single(symbols.Where(s => s.Kind == "struct" && s.Name == "EmptyStruct"));
        Assert.Equal(10, emptyStruct.EndLine);
    }

    [Fact]
    public void Extract_CSharp_TracksRecordPrimaryComponentLineAfterAttributes()
    {
        var content = """
            public record Person(
                [property: Obsolete]
                string Name,
                [property: Obsolete]
                int Age);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Name" && s.ContainerName == "Person"));
        Assert.Equal(3, name.Line);

        var age = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Age" && s.ContainerName == "Person"));
        Assert.Equal(5, age.Line);
    }

    [Fact]
    public void Extract_CSharp_RecordComponents_DoNotDisruptBodyMembersOrDuplicateExplicitProperties()
    {
        var content = """
            namespace App;

            public record Person(string Name)
            {
                public string Name { get; init; } = Name;

                public string Upper() => Name.ToUpperInvariant();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var nameProperties = symbols.Where(s => s.Kind == "property" && s.Name == "Name" && s.ContainerName == "Person").ToList();
        var nameProperty = Assert.Single(nameProperties);
        Assert.Equal(5, nameProperty.Line);
        Assert.Equal("App.Person", nameProperty.ContainerQualifiedName);

        var upper = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Upper"));
        Assert.Equal("class", upper.ContainerKind);
        Assert.Equal("Person", upper.ContainerName);
        Assert.Equal("App.Person", upper.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_DetectsCompoundVisibility()
    {
        // protected internal and private protected / 複合アクセス修飾子
        var content = "public class Base\n{\n    protected internal void Foo() { }\n    private protected int Bar { get; set; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var foo = symbols.FirstOrDefault(s => s.Name == "Foo");
        Assert.NotNull(foo);
        Assert.Equal("protected internal", foo.Visibility);

        var bar = symbols.FirstOrDefault(s => s.Name == "Bar");
        Assert.NotNull(bar);
        Assert.Equal("private protected", bar.Visibility);
    }

    [Fact]
    public void Extract_CSharp_DetectsUsingAlias()
    {
        var content = "using System;\nusing Json = System.Text.Json;\nusing static System.Math;\nglobal using Logging = Microsoft.Extensions.Logging;";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Json");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.Math");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Logging");
    }

    [Fact]
    public void Extract_CSharp_DetectsExternAlias()
    {
        // extern alias is a file-prelude declaration used for assembly-alias reconciliation.
        // It must precede using directives per the C# spec.
        // Closes #326.
        var content = "extern alias CoreV1;\nextern alias CoreV2;\n    extern alias Indented;\n\nglobal using System;\nusing static System.Math;\n\nnamespace Demo;\n\npublic class Box\n{\n    public int Calc() => Max(1, 2);\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // All three extern alias lines should be captured as import kind
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "CoreV1");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "CoreV2");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "Indented");

        // Existing using forms must still capture alongside extern alias (no reshuffling)
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System");
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System.Math");

        // The namespace/class/method should still be captured correctly
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Demo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Box");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Calc");
    }

    [Fact]
    public void Extract_CSharp_DetectsConstAndStaticReadonly()
    {
        var content = "public class Config\n{\n    public const string Version = \"1.0\";\n    private const int MaxRetries = 3;\n    internal static readonly Dictionary<string, string> Map = new();\n    public string MutableField;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Version" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MaxRetries" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Map");
        // Regular mutable fields are now extracted as `property` / 通常のフィールドも `property` として抽出される
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "MutableField" && s.ReturnType == "string");
    }

    [Fact]
    public void Extract_CSharp_StaticReadonlyField_FreeModifierOrder()
    {
        // Closes #355: C# allows modifiers to appear in any order, so `readonly static`,
        // `readonly new static`, and `new readonly static` must all be captured as the
        // kind `function` row (static readonly field), not fall through to the plain-field
        // (kind `property`) row.
        // Closes #355: C# の修飾子は任意順で書けるため、`readonly static` /
        // `readonly new static` / `new readonly static` も kind `function`（static readonly
        // フィールド）として取り扱い、通常フィールド（kind `property`）に流れ落ちないこと。
        var content = """
            public class Svc
            {
                public static readonly int A = 1;
                public readonly static int B = 2;
                public new static readonly int C = 3;
                public readonly new static int D = 4;
                public new readonly static int D2 = 5;
                readonly public static int E = 6;
                static readonly new int F = 7;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "A" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "B" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "C" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "D" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "D2" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "E" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "F" && s.ReturnType == "int");
        // Each static readonly declaration must be captured exactly once — no duplicate `property` row
        // from the plain-field regex.
        // それぞれの static readonly 宣言は1回だけ捕捉する — 通常フィールド regex からの重複 `property`
        // 行を生まないこと。
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name is "A" or "B" or "C" or "D" or "D2" or "E" or "F");
    }

    [Fact]
    public void Extract_CSharp_Method_FreeModifierOrder()
    {
        // Closes #355: C# allows visibility to appear anywhere in the modifier sequence, so
        // `static public`, `static internal`, `async public`, `override public` must all be
        // captured as kind `function` with the correct visibility.
        // Closes #355: visibility は修飾子シーケンスの任意位置に置けるため、`static public` /
        // `static internal` / `async public` / `override public` もすべて kind `function` として
        // 正しい visibility で捕捉されること。
        var content = """
            public class Svc
            {
                public static int I() => 0;
                static public int F() => 0;
                static internal void G() { }
                async public System.Threading.Tasks.Task H() { return; }
                override public int J() => 0;
                virtual public int K() => 0;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "I" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "F" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "G" && s.Visibility == "internal");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "H" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "J" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "K" && s.Visibility == "public");
    }

    [Fact]
    public void Extract_CSharp_Property_ModifierBeforeVisibility()
    {
        // Closes #355: property/indexer/event/delegate/operator rows also accept visibility
        // that follows a modifier (e.g. `static public int X { get; set; }`).
        // Closes #355: property / indexer / event / delegate / operator 行も、
        // `static public int X { get; set; }` のように修飾子の後の visibility を受け付ける。
        var content = """
            public class Svc
            {
                static public int P1 { get; set; }
                static public int P2 => 0;
                virtual public int P3 { get; set; }
                override public int P4 => 0;
                static public event System.EventHandler E1;
                static public delegate int D1(int x);
                static public int this[int i] => 0;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P1" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P2" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P3" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P4" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "E1" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "D1" && s.Visibility == "public");
        // Indexer is recorded as `Item` (C# metadata name) after NormalizeCSharpSymbolName.
        // インデクサは NormalizeCSharpSymbolName で C# メタデータ名 `Item` に正規化される。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.Visibility == "public");
    }

    [Fact]
    public void Extract_CSharp_InterfaceEventsUseInterfaceContainer()
    {
        var content = """
            using System;
            namespace EventMods;

            public interface IBus
            {
                event EventHandler Regular;
                static abstract event EventHandler StaticAbs;
                static virtual event EventHandler StaticVirt { add { } remove { } }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        foreach (var name in new[] { "Regular", "StaticAbs", "StaticVirt" })
        {
            var evt = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == name));
            Assert.Equal("interface", evt.ContainerKind);
            Assert.Equal("IBus", evt.ContainerName);
            Assert.Equal("EventMods.IBus", evt.ContainerQualifiedName);
        }
    }

    [Fact]
    public void Extract_CSharp_StructMembersUseStructContainer()
    {
        var content = """
            namespace Demo;

            public struct S
            {
                public int P { get; set; }
                public event System.EventHandler E;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var property = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "P"));
        Assert.Equal("struct", property.ContainerKind);
        Assert.Equal("S", property.ContainerName);
        Assert.Equal("Demo.S", property.ContainerQualifiedName);

        var evt = Assert.Single(symbols.Where(s => s.Kind == "event" && s.Name == "E"));
        Assert.Equal("struct", evt.ContainerKind);
        Assert.Equal("S", evt.ContainerName);
        Assert.Equal("Demo.S", evt.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_TypeDeclarations_FreeModifierOrder()
    {
        // Closes #355: type declarations (class / struct / interface / record) also accept
        // visibility anywhere in the modifier sequence. All fixture forms are compiler-legal
        // (`abstract public class X {}`, `readonly public struct Y {}`, `sealed public class Z {}`,
        // `ref public struct RS {}`, `partial public interface PI {}`) but previously fell
        // through the type rows because visibility had to come first.
        // Closes #355: 型宣言（class / struct / interface / record）も visibility を
        // 修飾子列の任意位置で受け付けること。fixture はすべてコンパイラが通す合法な並びで、
        // 以前は visibility が先頭必須のため型行をすり抜けていた。
        var content = """
            namespace Demo;
            abstract public class AbstractPublicClass {}
            sealed public class SealedPublicClass {}
            readonly public struct ReadonlyPublicStruct {}
            ref public struct RefPublicStruct {}
            partial public interface PartialPublicInterface {}
            abstract public record class AbstractPublicRecordClass {}
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "AbstractPublicClass" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "SealedPublicClass" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "ReadonlyPublicStruct" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "RefPublicStruct" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "PartialPublicInterface" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "AbstractPublicRecordClass" && s.Visibility == "public");
    }

    [Fact]
    public void Extract_CSharp_ConstField_FreeModifierOrder()
    {
        // Closes #355: `const` fields also accept free modifier order. `new public const`
        // is compiler-legal (it hides a same-named base-class const) but was previously
        // dropped because the const row required visibility to come before `new`.
        // Closes #355: `const` フィールドも修飾子順序自由。`new public const` は
        // コンパイラ上合法（同名ベースクラス const の隠蔽）だが、以前は visibility が
        // `new` より前必須のため落ちていた。
        var content = """
            public class Base { public const int BaseConst = 1; }
            public class Derived : Base
            {
                new public const int HiddenConst = 2;
                public new const int HiddenConst2 = 3;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BaseConst" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "HiddenConst" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "HiddenConst2" && s.Visibility == "public" && s.ReturnType == "int");
    }

    [Fact]
    public void Extract_CSharp_ConstField_TupleReturnTypes()
    {
        // Closes #346: const fields with tuple / named-tuple / nullable-tuple /
        // generic-over-tuple / global::-qualified / tuple-array return types were silently
        // dropped because the const row's returnType char class had no `(`, `)`, `\s`, and no
        // tuple alternative. The method row at the next priority was already immunized by
        // the post-#349 CSharpNonTypeKeywordPattern / CSharpTypePattern consolidation, so no
        // phantom `function const` row was emitted — the symbols simply vanished. Switching
        // the const returnType to the shared CSharpTypePattern token restores capture for all
        // of these shapes and preserves baselines (`public const int Plain = 42;`,
        // `new public const int HiddenConst = 2;`).
        // Closes #346: tuple / 名前付き tuple / nullable tuple / generic-over-tuple /
        // `global::` 修飾 / tuple-array を戻り値型とする const フィールドは、const 行の
        // returnType 文字クラスに `(` / `)` / `\s` も tuple 代替もなかったため、サイレントに
        // drop されていた。method 行は #349 以後の CSharpNonTypeKeywordPattern /
        // CSharpTypePattern 統合で既にこの後方参照経路を塞いでいるため、phantom `function const`
        // 行は出ずに単に消えていた。const の returnType を共有トークン CSharpTypePattern に
        // 差し替えることで、以下のすべての形を捕捉し、既存の baseline（`public const int Plain = 42;`
        // / `new public const int HiddenConst = 2;`）も維持する。
        var content = """
            namespace ConstTuple;

            public class Cfg
            {
                public const (int, int) Pair = (1, 2);
                public const (int a, int b) NamedPair = (1, 2);
                public const (int, int)? MaybePair = null;
                public const (int, int)[] PairArray = null;
                public const global::System.Int32 Qualified = 7;
                public const int Plain = 42;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Pair" && s.Visibility == "public" && s.ReturnType == "(int, int)");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NamedPair" && s.Visibility == "public" && s.ReturnType == "(int a, int b)");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "MaybePair" && s.Visibility == "public" && s.ReturnType == "(int, int)?");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "PairArray" && s.Visibility == "public" && s.ReturnType == "(int, int)[]");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Qualified" && s.Visibility == "public" && s.ReturnType == "global::System.Int32");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Plain" && s.Visibility == "public" && s.ReturnType == "int");

        // The method row must not emit a phantom `function const` row for any of the tuple
        // shapes above. `const` itself as a name would only appear via the post-#349 backtrack
        // the issue describes. Assert the negative so any regression of that phantom is caught.
        // tuple 形に対して method 行が phantom `function const` 行を発行していないことを
        // 明示的に確認する。`const` 自体が name として現れるのは #349 以後は起きないはずの
        // 後方参照経路のみなので、将来その regression が起きたらここで検出できる。
        Assert.DoesNotContain(symbols, s => s.Name == "const");
    }

    [Fact]
    public void Extract_CSharp_PlainField_FreeModifierOrder()
    {
        // Closes #355: plain fields (kind `property`) and multi-line field headers must also
        // accept visibility anywhere in the modifier sequence. Previously `static public int X;`
        // captured as a field with empty `visibility` (single-line plain-field regex was
        // visibility-first), and multi-line declarations whose header line starts with a
        // non-visibility modifier were dropped entirely because
        // `CSharpPropertyHeaderPrefixRegex` (the merger trigger) was also visibility-first and
        // did not accept `const`.
        // Closes #355: 通常フィールド（kind `property`）と複数行フィールドヘッダも、修飾子列の
        // 任意位置で visibility を受け付けなければならない。以前は `static public int X;` が
        // visibility 空のまま captured され（単一行 plain-field 正規表現が visibility-first）、
        // 非 visibility 修飾子から始まる複数行宣言は結合トリガの `CSharpPropertyHeaderPrefixRegex`
        // 自体が visibility-first で `const` も受け付けなかったため完全に欠落していた。
        var content = """
            using System.Collections.Generic;
            public class Edge
            {
                static public int X;
                readonly public int Y;
                new public static int Z = 1;
                static public Dictionary<string, int>
                    Map = new();
                new public const int
                    C = 1;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Y" && s.Visibility == "public" && s.ReturnType == "int");
        // `new public static` is promoted to kind `function` via the static readonly / const row set.
        // `new public static` は static readonly / const 系の行で kind `function` に昇格する。
        Assert.Contains(symbols, s => s.Name == "Z" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Map" && s.Visibility == "public" && s.ReturnType != null && s.ReturnType.Contains("Dictionary"));
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "C" && s.Visibility == "public" && s.ReturnType == "int");
    }

    [Fact]
    public void Extract_CSharp_UnsafeExtern_FreeModifierOrder()
    {
        // Closes #355: `unsafe` / `extern` modifiers must not force a specific slot in the
        // modifier sequence. All fixture forms are compiler-legal but previously either dropped
        // entirely (constructor / static constructor / event) or captured the declaration while
        // losing `visibility` and polluting `return_type` with the leading modifiers
        // (property / indexer).
        // Closes #355: `unsafe` / `extern` 修飾子も修飾子列の特定位置に固定されてはならない。
        // fixture はすべてコンパイラ上合法だが、以前は constructor / static constructor / event では
        // そもそも抽出されず、property / indexer では visibility が欠落して return_type に
        // 先頭修飾子が混入していた。
        var content = """
            public unsafe class UnsafeHolder
            {
                unsafe public int P1 { get; set; }
                unsafe public int P2 => 0;
                unsafe public event System.EventHandler E1;
                extern public event System.EventHandler E2;
                unsafe public int this[int* i] => 0;
                unsafe public UnsafeHolder(int* p) { }
                extern public UnsafeHolder(int x);
                unsafe static UnsafeHolder() { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P1" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P2" && s.Visibility == "public" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "E1" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "E2" && s.Visibility == "public");
        // Indexer is recorded as `Item` (C# metadata name) after NormalizeCSharpSymbolName.
        // インデクサは NormalizeCSharpSymbolName で C# メタデータ名 `Item` に正規化される。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.Visibility == "public" && s.ReturnType == "int");
        // Constructors are recorded with visibility and the type name as symbol name.
        // コンストラクタは visibility を保持し、シンボル名は型名になる。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "UnsafeHolder" && s.Visibility == "public");
        // Static constructor has no visibility.
        // 静的コンストラクタは visibility を持たない。
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "UnsafeHolder" && string.IsNullOrEmpty(s.Visibility));
    }

    [Fact]
    public void Extract_CSharp_InheritanceAndFile_FreeModifierOrder()
    {
        // Closes #355: inheritance modifiers on events (`virtual` / `override` / `abstract` /
        // `sealed` / `new`) and the `file` modifier on interface / delegate declarations must
        // be accepted in any position, with visibility still captured when present.
        // Closes #355: event の継承修飾子 (`virtual` / `override` / `abstract` / `sealed` / `new`) と、
        // interface / delegate 宣言の `file` 修飾子は任意位置で受理され、visibility が存在する場合は
        // 併せて拾われる必要がある。
        var content = """
            file interface IWidget
            {
                int ProvideAnswer();
            }
            file delegate int Computer(int x);
            public abstract class Base
            {
                abstract public event System.EventHandler A;
                virtual public event System.EventHandler B;
                sealed public override event System.EventHandler C;
                new public event System.EventHandler D;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // `file interface` should be matched as an interface symbol.
        // `file interface` は interface シンボルとして抽出される。
        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "IWidget");
        // `file delegate` should be matched as a delegate symbol.
        // `file delegate` は delegate シンボルとして抽出される。
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Computer" && s.ReturnType == "int");
        // Events with inheritance modifiers must still record visibility = "public".
        // 継承修飾子付きの event も visibility = "public" を保持する必要がある。
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "A" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "B" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "C" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "D" && s.Visibility == "public");
    }

    [Fact]
    public void Extract_CSharp_FileDelegate_Issue303Repro()
    {
        // Closes #303: the `file` modifier on a top-level delegate must not be dropped by
        // the modifier slot, regardless of whether other variants share the same file.
        // v1.10.0 captured plain / public / unsafe delegates but silently missed
        // `file delegate`. The #355 fix (free modifier order; accept `file` / `new`) covers
        // this, so this test locks in the behavior against the exact reproducer from #303.
        // Closes #303: トップレベル delegate の `file` 修飾子が modifier スロットで落ちないこと。
        // v1.10.0 では plain / public / unsafe delegate は拾えていたが `file delegate` だけが
        // 黙って欠落していた。#355 の修正 (修飾子順序を自由化し `file` / `new` を受理) が
        // 本件も解消するため、#303 の再現 fixture で挙動を固定する。
        var content = """
            namespace Demo;

            public delegate void PublicHandler(object sender);

            file delegate void FileOnlyHandler(object sender);

            delegate void PlainHandler(object sender);

            unsafe delegate void UnsafeHandler(object sender);
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "PublicHandler" && s.Visibility == "public" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "FileOnlyHandler" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "PlainHandler" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "UnsafeHandler" && s.ReturnType == "void");
    }

    [Fact]
    public void Extract_CSharp_EventModifierCombinations_Issue334Repro()
    {
        // Closes #334: the full issue repro must survive extraction, including class events
        // with `abstract` / `virtual` / `override` / `sealed override` / `new`, plus interface
        // events with `static abstract` and accessor-bodied `static virtual`. The current main
        // branch already accepts these modifier sequences; this test locks the exact dogfood
        // fixture in place so the open issue cannot regress silently. The same container
        // walk now also treats `struct` as a real parent, so keep one struct-owned event in
        // the fixture to ensure the broader container fix stays covered too.
        // Closes #334: issue 本文の再現ケース全体を固定する。`abstract` / `virtual` /
        // `override` / `sealed override` / `new` 付き class event と、`static abstract` /
        // accessor 本体付き `static virtual` interface event の両方が抽出され続ける必要がある。
        // 現行 main はこれらを受理できるため、このテストは open issue の dogfood fixture を
        // そのまま回帰防止として固定する。同じ container 走査は `struct` も親として扱うよう
        // になったため、より広い親子付け修正も 1 件の struct event で固定する。
        var content = """
            using System;
            namespace EventMods;

            public abstract class Base
            {
                public abstract event EventHandler Ping;
                public virtual event EventHandler Ring;
                public new event EventHandler Hide;
                protected event EventHandler Peek;
                public event EventHandler Plain;
            }

            public sealed class Derived : Base
            {
                public override event EventHandler Ping;
                public sealed override event EventHandler Ring;
            }

            public struct Box
            {
                public event EventHandler Sent;
            }

            public interface IBus
            {
                event EventHandler Regular;
                static abstract event EventHandler StaticAbs;
                static virtual event EventHandler StaticVirt { add { } remove { } }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var events = symbols.Where(s => s.Kind == "event").ToList();

        Assert.Equal(11, events.Count);
        var basePing = Assert.Single(events.Where(s => s.Name == "Ping" && s.ContainerKind == "class" && s.ContainerName == "Base"));
        Assert.Equal("public", basePing.Visibility);
        Assert.Equal("EventHandler", basePing.ReturnType);

        var derivedPing = Assert.Single(events.Where(s => s.Name == "Ping" && s.ContainerKind == "class" && s.ContainerName == "Derived"));
        Assert.Equal("public", derivedPing.Visibility);
        Assert.Equal("EventHandler", derivedPing.ReturnType);

        var baseRing = Assert.Single(events.Where(s => s.Name == "Ring" && s.ContainerKind == "class" && s.ContainerName == "Base"));
        Assert.Equal("public", baseRing.Visibility);
        Assert.Equal("EventHandler", baseRing.ReturnType);

        var derivedRing = Assert.Single(events.Where(s => s.Name == "Ring" && s.ContainerKind == "class" && s.ContainerName == "Derived"));
        Assert.Equal("public", derivedRing.Visibility);
        Assert.Equal("EventHandler", derivedRing.ReturnType);

        Assert.Contains(events, s => s.Name == "Hide" && s.ContainerKind == "class" && s.ContainerName == "Base" && s.Visibility == "public" && s.ReturnType == "EventHandler");
        Assert.Contains(events, s => s.Name == "Peek" && s.ContainerKind == "class" && s.ContainerName == "Base" && s.Visibility == "protected" && s.ReturnType == "EventHandler");
        Assert.Contains(events, s => s.Name == "Plain" && s.ContainerKind == "class" && s.ContainerName == "Base" && s.Visibility == "public" && s.ReturnType == "EventHandler");
        var sent = Assert.Single(events.Where(s => s.Name == "Sent" && s.ContainerKind == "struct" && s.ContainerName == "Box"));
        Assert.Equal("public", sent.Visibility);
        Assert.Equal("EventHandler", sent.ReturnType);

        var regular = Assert.Single(events.Where(s => s.Name == "Regular" && s.ContainerKind == "interface" && s.ContainerName == "IBus"));
        Assert.True(string.IsNullOrEmpty(regular.Visibility));
        Assert.Equal("EventHandler", regular.ReturnType);

        var staticAbs = Assert.Single(events.Where(s => s.Name == "StaticAbs" && s.ContainerKind == "interface" && s.ContainerName == "IBus"));
        Assert.True(string.IsNullOrEmpty(staticAbs.Visibility));
        Assert.Equal("EventHandler", staticAbs.ReturnType);

        var staticVirt = Assert.Single(events.Where(s => s.Name == "StaticVirt" && s.ContainerKind == "interface" && s.ContainerName == "IBus"));
        Assert.True(string.IsNullOrEmpty(staticVirt.Visibility));
        Assert.Equal("EventHandler", staticVirt.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_NewNestedInterface_MemberHiding()
    {
        // Closes #376: a nested `new interface` that hides a base-class nested interface must
        // still be extracted as its own symbol. Previously the interface regex modifier list
        // did not include `new`, so `public new interface INested` was silently dropped.
        // Closes #376: ベースクラスのネストしたインタフェースを `new interface` で隠蔽する
        // 派生側のネスト interface も独立したシンボルとして抽出されること。以前は
        // interface 正規表現の修飾子リストに `new` が無く、`public new interface INested`
        // が無言で落ちていた。
        var content = """
            namespace NewIfaceTest;
            public class Base
            {
                public interface INested { void M(); }
            }
            public class Derived : Base
            {
                public new interface INested { void M(); }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var nested = symbols.Where(s => s.Kind == "interface" && s.Name == "INested").ToList();
        Assert.Equal(2, nested.Count);
        Assert.Contains(nested, s => s.ContainerName == "Base");
        Assert.Contains(nested, s => s.ContainerName == "Derived");
    }

    [Fact]
    public void Extract_CSharp_Interface_ModifierSlotMatrix_LocksInCommonLegalShapes()
    {
        // Closes #302: the C# interface row's modifier slot must accept the common
        // legal declaration shapes in a single fixture so a future modifier-slot
        // refactor (mirror of the #238 `operator checked`, #244 `static abstract`,
        // #355 `file`, and #376 `new` families) cannot silently drop one variant.
        // The fixture is intentionally hand-verified to be legal C# — `partial`
        // must appear immediately before the `interface` keyword (CS0267 otherwise)
        // so `partial public interface` is NOT a legal ordering and is intentionally
        // absent. Non-canonical modifier order is instead demonstrated by
        // `unsafe public interface` (the `unsafe` type modifier has no required
        // position relative to accessibility). Covers plain `interface`, `public
        // interface`, explicit `internal interface`, `file interface` (C# 11
        // file-scoped, cannot combine with accessibility), bare `partial interface`,
        // `public partial interface`, non-canonical `unsafe public interface`,
        // `unsafe interface`, the nested `public new interface` that hides a
        // same-named base-type member, and the nested `public new partial interface`
        // that exercises the `new + partial` modifier interaction on nested types.
        // Each unique name is pinned with `Assert.Single` so a silent duplicate row
        // or a kind/visibility relabel on a sibling variant cannot make this test
        // pass via a second matching row. The total interface-symbol count for the
        // fixture is also asserted so a phantom extra interface emission anywhere
        // in the file cannot slip past the per-name predicates.
        // Closes #302: C# interface 行の修飾子スロットが、単一 fixture で代表的な
        // 合法宣言形を受理することを固定する。修飾子スロットの将来的な再編（#238
        // の `operator checked`、#244 の `static abstract`、#355 の `file`、#376
        // の `new` と同じファミリの問題）で、いずれか1形を黙って落とす回帰を防ぐ。
        // fixture は手で合法性を検証済みで、`partial` は `interface` キーワード
        // 直前にしか置けず（違反すると CS0267）、`partial public interface` は
        // 合法な順序ではないので意図的に含めない。非正準順序は `unsafe public
        // interface`（`unsafe` 型修飾子は可視性に対して順序の制約がない）で代替
        // する。plain `interface`、`public interface`、明示 `internal interface`、
        // `file interface`（C# 11 file-scoped、accessibility と併用不可）、素の
        // `partial interface`、`public partial interface`、非正準順の `unsafe
        // public interface`、`unsafe interface`、同名ベースメンバを隠蔽する
        // ネストの `public new interface`、`new + partial` 修飾子相互作用を
        // 検証するネストの `public new partial interface` を網羅する。各ユニーク
        // 名は `Assert.Single` で固定し、兄弟変種に silent duplicate や kind /
        // visibility relabel が入っても別行のヒットで silent pass しないように
        // する。fixture 全体の interface シンボル総数もアサートして、ファイル内
        // のどこかで phantom interface が追加された場合でも per-name predicate
        // をすり抜けないようにする。
        var content = """
            namespace ModifierSlotMatrix;

            interface IPlain { void Do(); }
            public interface IPublic { void Do(); }
            internal interface IInternal { void Do(); }
            file interface IFile { void Do(); }
            partial interface IPartial { void Do(); }
            public partial interface IPublicPartial { void Do(); }
            unsafe public interface IUnsafePublic { void Do(); }
            unsafe interface IUnsafe { void Do(); }

            public class Base
            {
                public interface INested { void Do(); }
            }
            public class Derived : Base
            {
                public new interface INested { void Do(); }
                public new partial interface IPartialNested { void Do(); }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IPlain" && string.IsNullOrEmpty(s.Visibility));
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IPublic" && s.Visibility == "public");
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IInternal" && s.Visibility == "internal");
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IFile" && string.IsNullOrEmpty(s.Visibility));
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IPartial" && string.IsNullOrEmpty(s.Visibility));
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IPublicPartial" && s.Visibility == "public");
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IUnsafePublic" && s.Visibility == "public");
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IUnsafe" && string.IsNullOrEmpty(s.Visibility));

        // Nested `public new interface INested` must produce a second symbol attributed
        // to the `Derived` container alongside the base-side `INested` on `Base`.
        // `public new partial interface IPartialNested` covers the `new + partial`
        // modifier interaction on nested types.
        // ネストの `public new interface INested` は、基底側の `Base.INested` に加えて
        // `Derived` コンテナ下の独立シンボルとして抽出される必要がある。
        // `public new partial interface IPartialNested` はネスト型の `new + partial`
        // 修飾子相互作用を検証する。
        var nested = symbols.Where(s => s.Kind == "interface" && s.Name == "INested").ToList();
        Assert.Equal(2, nested.Count);
        Assert.Single(nested, s => s.ContainerName == "Base" && s.Visibility == "public");
        Assert.Single(nested, s => s.ContainerName == "Derived" && s.Visibility == "public");
        Assert.Single(symbols, s => s.Kind == "interface" && s.Name == "IPartialNested" && s.ContainerName == "Derived" && s.Visibility == "public");

        // Fixture contains exactly 11 legal interface declarations (8 top-level +
        // 3 nested: Base.INested, Derived.INested, Derived.IPartialNested). Pinning
        // the aggregate count here prevents a phantom interface emission elsewhere
        // in the file from slipping past the per-name `Assert.Single` predicates.
        // fixture 全体の合法 interface 宣言は正確に 11 件（top-level 8 件 + nested 3
        // 件: Base.INested、Derived.INested、Derived.IPartialNested）。集計数も
        // アサートすることで、ファイル中のどこかで phantom interface が発生しても
        // per-name `Assert.Single` をすり抜けないようにする。
        Assert.Equal(11, symbols.Count(s => s.Kind == "interface"));
    }

    [Fact]
    public void Extract_CSharp_DetectsExpressionBodiedMembers()
    {
        var content = "public class Calc\n{\n    public int X => 42;\n    public string Name => \"calc\";\n    public static double Pi => 3.14;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "X" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Pi" && s.ReturnType == "double");
    }

    [Fact]
    public void Extract_CSharp_ExpressionBodiedMembers_HaveBodyRanges()
    {
        // issue #233: expression-bodied members must report a body range covering the
        // declaration line through the terminating ';' so reference attribution can find
        // them as the innermost enclosing container.
        // issue #233: 式本体メンバーは、宣言行から終端 ';' までを本体範囲として報告する必要がある。
        // そうすることで参照属性解決が内側コンテナとして認識できる。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap1() => Compute();\n    public int Wrap3 => Compute();\n    public int MultiLine()\n        => Compute();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var compute = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Compute"));
        Assert.Equal(3, compute.BodyStartLine);
        Assert.Equal(3, compute.BodyEndLine);

        var wrap1 = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Wrap1"));
        Assert.Equal(4, wrap1.BodyStartLine);
        Assert.Equal(4, wrap1.BodyEndLine);

        var wrap3 = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap3"));
        Assert.Equal(5, wrap3.BodyStartLine);
        Assert.Equal(5, wrap3.BodyEndLine);

        var multi = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "MultiLine"));
        Assert.Equal(6, multi.BodyStartLine);
        Assert.Equal(7, multi.BodyEndLine);
    }

    [Fact]
    public void Extract_CSharp_BlockBodiedProperty_AllmanStyle_IsExtracted()
    {
        // issue #233 review follow-up: Allman-style block-bodied properties (with `{` on
        // the next line) were not matched by the property regex, so `callers` would
        // attribute accessor-internal references to the enclosing class. The widened
        // regex plus `ShouldSkipCSharpHeaderOnlyPropertyCandidate` verification must
        // still recognize them as properties with proper body ranges.
        // issue #233 のレビュー指摘: Allman スタイル（次行に `{`）の block-bodied property が
        // property regex でマッチしておらず、accessor 内の参照がクラスに帰属していた。
        // widened regex と `ShouldSkipCSharpHeaderOnlyPropertyCandidate` の組み合わせで
        // 正しく property として認識され、本体範囲も持つ必要がある。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap\n    {\n        get { return Compute(); }\n    }\n    public string Name\n    {\n        get;\n        set;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(7, wrap.EndLine);
        Assert.Equal(5, wrap.BodyStartLine);
        Assert.Equal(7, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
        Assert.Equal("public", wrap.Visibility);

        var name = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Name"));
        Assert.Equal(8, name.StartLine);
        Assert.Equal(12, name.EndLine);
        Assert.Equal(9, name.BodyStartLine);
        Assert.Equal(12, name.BodyEndLine);
        Assert.Equal("string", name.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_MultiLineExpressionBodiedProperty_IsExtracted()
    {
        // issue #233 second review follow-up: multi-line expression-bodied properties,
        // where the declaration is on one line and `=> expr;` on the continuation line,
        // must still be extracted as properties with a body range spanning the two lines.
        // Without this, accessor-internal calls fall through to the enclosing class.
        // issue #233 の再レビュー指摘: 宣言行の次行に `=> expr;` が来る multi-line 式本体
        // プロパティも property として抽出され、本体範囲が宣言行から `;` 行までを覆う必要がある。
        // これができないと accessor 内呼び出しが外側クラスに誤帰属する。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap\n        => Compute();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(5, wrap.EndLine);
        Assert.Equal(4, wrap.BodyStartLine);
        Assert.Equal(5, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
        Assert.Equal("public", wrap.Visibility);
    }

    [Fact]
    public void Extract_CSharp_WrappedExpressionBodiedProperty_Issue345Repro_ShapesAndControls_AreCaptured()
    {
        // issue #345: explicit regression coverage for expression-bodied properties whose
        // `=>` moves to the next physical line, including attributed/static variants and
        // a multi-line expression body. Indexers and methods with wrapped `=>` remain the
        // expected control cases and must not be reclassified as properties.
        // issue #345: `=>` が次の物理行へ送られた式本体プロパティの明示的な回帰テスト。
        // attribute/static 付きや multi-line 式本体も含めて property として抽出しつつ、
        // wrapped `=>` の indexer / method は従来どおり control case として残す。
        var content = """
            namespace WrappedArrowProp;

            public class Svc
            {
                public int Same => 1;

                public int Wrapped
                    => 2;

                [System.Obsolete]
                public int WrappedAttr
                    => 3;

                public static int WrappedStatic
                    => 4;

                public int WrappedMulti
                    => 1
                     + 2;

                public int this[int i]
                    => i;

                public int WrappedMethod()
                    => 5;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Same");

        var wrapped = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrapped"));
        Assert.Equal(7, wrapped.StartLine);
        Assert.Equal(8, wrapped.EndLine);
        Assert.Equal(7, wrapped.BodyStartLine);
        Assert.Equal(8, wrapped.BodyEndLine);

        var wrappedAttr = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "WrappedAttr"));
        Assert.Equal(11, wrappedAttr.StartLine);
        Assert.Equal(12, wrappedAttr.EndLine);
        Assert.Equal(11, wrappedAttr.BodyStartLine);
        Assert.Equal(12, wrappedAttr.BodyEndLine);

        var wrappedStatic = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "WrappedStatic"));
        Assert.Equal(14, wrappedStatic.StartLine);
        Assert.Equal(15, wrappedStatic.EndLine);
        Assert.Equal(14, wrappedStatic.BodyStartLine);
        Assert.Equal(15, wrappedStatic.BodyEndLine);
        Assert.Equal("public", wrappedStatic.Visibility);

        var wrappedMulti = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "WrappedMulti"));
        Assert.Equal(17, wrappedMulti.StartLine);
        Assert.Equal(19, wrappedMulti.EndLine);
        Assert.Equal(17, wrappedMulti.BodyStartLine);
        Assert.Equal(19, wrappedMulti.BodyEndLine);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "WrappedMethod");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Item");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "WrappedMethod");
    }

    [Fact]
    public void Extract_CSharp_SplitReturnTypeLine_StillCapturesMethodPropertyAndIndexer()
    {
        // issue #361: when a long C# return type wraps onto the previous line, the
        // method/property/indexer must still be emitted instead of being silently
        // dropped by a per-line-only regex pass.
        // issue #361: C# の長い戻り値型が前行へ折り返されても、method/property/indexer は
        // per-line 前提の regex で silent drop されず、引き続き抽出される必要がある。
        var content = """
            using System.Collections.Generic;

            namespace CsMultilineSig;

            public class Svc
            {
                public Dictionary<string, List<int>>
                    GetMapping(
                        string key,
                        int defaultValue) => new();

                public Dictionary<string, int>
                    Cache { get; } = new();

                public T Create<T>(string name)
                    where T : class, new()
                    => default!;

                public int
                    this[string key] { get => 0; set { } }

                public int Simple() => 0;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var getMapping = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "GetMapping"));
        Assert.Equal("public", getMapping.Visibility);
        Assert.Equal("Dictionary<string,List<int>>", getMapping.ReturnType);
        Assert.Equal(7, getMapping.StartLine);
        Assert.Equal(10, getMapping.EndLine);

        var cache = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Cache"));
        Assert.Equal("public", cache.Visibility);
        Assert.Equal("Dictionary<string,int>", cache.ReturnType);
        Assert.Equal(12, cache.StartLine);
        Assert.Equal(13, cache.EndLine);

        var create = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Create"));
        Assert.Equal("public", create.Visibility);
        Assert.Equal("T", create.ReturnType);
        Assert.Equal(15, create.StartLine);
        Assert.Equal(17, create.EndLine);

        var indexer = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Item"));
        Assert.Equal("public", indexer.Visibility);
        Assert.Equal("int", indexer.ReturnType);
        Assert.Equal(19, indexer.StartLine);
        Assert.Equal(20, indexer.EndLine);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Simple" && s.ReturnType == "int");
        Assert.Equal(
            new[]
            {
                ("class", "Svc"),
                ("function", "Create"),
                ("function", "GetMapping"),
                ("function", "Item"),
                ("function", "Simple"),
                ("namespace", "CsMultilineSig"),
                ("property", "Cache"),
            },
            symbols
                .Where(s => s.Kind != "import")
                .Select(s => (s.Kind, s.Name))
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Extract_CSharp_AllmanBlockBodiedProperty_WithIntermediateBlockComment_IsExtracted()
    {
        // issue #233 fourth review follow-up: when an Allman-style block-bodied property
        // has a multi-line `/* ... */` block comment between the header line and the `{`
        // line, the skip guard must traverse the comment via `LexCSharpLine` and still
        // recognize the continuation `{`. A naive prefix-based comment skip only
        // handled `*` / `//` / `/*` line starts and dropped the property entirely.
        // issue #233 第4次レビュー指摘: Allman スタイルの block-bodied property で
        // header 行と `{` の間に multi-line `/* ... */` のブロックコメントがあっても、
        // `LexCSharpLine` でコメントを通り抜けて次の `{` を認識する必要がある。
        // 行頭 prefix だけの素朴なスキップでは `*` / `//` / `/*` の開始行しか飛ばせず、
        // この形の property は落ちていた。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap\n    /* some multi-line\n       block comment */\n    {\n        get { return Compute(); }\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(9, wrap.EndLine);
        Assert.Equal(7, wrap.BodyStartLine);
        Assert.Equal(9, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_MultiLineExpressionBodiedProperty_WithIntermediateBlockComment_IsExtracted()
    {
        // issue #233 fourth review follow-up: same scenario for multi-line expression
        // bodies — `public int Wrap` followed by `/* ... */` and then `=> Compute();`
        // must still be extracted with the property spanning declaration through `;`.
        // issue #233 第4次レビュー指摘: multi-line 式本体プロパティでも同じく、
        // `public int Wrap` の後に `/* ... */`、さらに `=> Compute();` が続く形で
        // 宣言行から `;` 行までを本体範囲とする property が抽出されること。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int WrapExpr\n    /* multi-line\n       comment */\n        => Compute();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "WrapExpr"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(7, wrap.EndLine);
        Assert.Equal(4, wrap.BodyStartLine);
        Assert.Equal(7, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_BraceSameLineAccessorNextLineProperty_IsExtracted()
    {
        // issue #233 fifth review follow-up: the common Microsoft-style block-bodied
        // property — `{` on the same line as the declaration and the accessor on the
        // following line — must be recognized as a property with a body range spanning
        // declaration through closing `}`.
        // issue #233 第5次レビュー指摘: `{` が宣言行末にあり、accessor が次行にある
        // 標準的な block-bodied property が property として抽出され、宣言行から `}` 行
        // までを本体範囲として持つこと。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n    public int Wrap {\n        get { return Compute(); }\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var wrap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Wrap"));
        Assert.Equal(4, wrap.StartLine);
        Assert.Equal(6, wrap.EndLine);
        Assert.Equal(4, wrap.BodyStartLine);
        Assert.Equal(6, wrap.BodyEndLine);
        Assert.Equal("int", wrap.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_BraceSameLineAccessorNextLine_AcceptsAttributeAndVisibility()
    {
        // issue #233 fifth review follow-up: the bare-brace-same-line guard must also
        // accept next lines that begin with accessor attributes (`[JsonIgnore]`) or a
        // visibility modifier (`private set`) before the `get` / `set` / `init` token.
        // issue #233 第5次レビュー指摘: 同一行 bare `{` のガードは、accessor attribute
        // (`[JsonIgnore]`) や visibility 修飾子 (`private set`) で始まる行も受け入れる必要がある。
        var content = "public class Calc\n{\n    public int Compute() => 42;\n"
            + "    public int WithAttr {\n        [System.Obsolete] get => Compute();\n    }\n"
            + "    public int WithVis {\n        private set { }\n        get { }\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "WithAttr");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "WithVis");
    }

    [Fact]
    public void Extract_CSharp_BraceSameLineWithoutAccessor_IsNotMisclassifiedAsProperty()
    {
        // issue #233 fifth review follow-up: the bare-brace-same-line guard must reject
        // non-property shapes that happen to be `Type Name {` followed by a body that
        // does not start an accessor (for example a stray method-like block).
        // issue #233 第5次レビュー指摘: 同一行 bare `{` のガードは、`Type Name {` に
        // 続く行が accessor 宣言でない場合（例: accessor でない任意のブロック）を
        // property として採用してはならない。
        var content = "public class Calc\n{\n    public int Stray {\n        Console.WriteLine(1);\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Stray");
    }

    [Fact]
    public void Extract_CSharp_HeaderOnlyNonProperty_IsNotMisclassified()
    {
        // issue #233 review follow-up: the header-only property alternation must not
        // swallow keyword lines such as `public class X` or `return Foo` even if they
        // happen to look like `Type Name` before a newline.
        // issue #233 のレビュー指摘: header-only の alternation が `public class X` や
        // `return Foo` のようなキーワード行を property と誤分類しないことを担保する。
        var content = "public class Thing\n{\n    public int Method()\n    {\n        return Thing;\n    }\n}\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Kind == "property");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Thing");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Method");
    }

    [Fact]
    public void Extract_CSharp_SwitchExpressionArms_DoNotProducePhantomProperties()
    {
        var content = """
            public class Matcher
            {
                public string Describe(object x) => x switch
                {
                    int n when n > 0 => "pos",
                    int neg => "non-pos",
                    string text => text,
                    double d => "double",
                    List<string> list => "list",
                    _ => "other",
                };

                public int Count(int y) => y switch
                {
                    > 0 => 1,
                    0 => 0,
                    _ => -1,
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Matcher");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Describe");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Count");
        Assert.DoesNotContain(symbols, s => s.Kind == "property");
        Assert.DoesNotContain(symbols, s => s.Name == "neg");
        Assert.DoesNotContain(symbols, s => s.Name == "text");
        Assert.DoesNotContain(symbols, s => s.Name == "d");
        Assert.DoesNotContain(symbols, s => s.Name == "list");
        Assert.DoesNotContain(symbols, s => s.Name == "0");
    }

    [Fact]
    public void Extract_CSharp_MultiLineSwitchExpressionArms_DoNotProducePhantomProperties()
    {
        // issue #233 third review follow-up: switch-expression arms whose `=>` is placed
        // on a continuation line must not be misclassified as multi-line expression-bodied
        // properties. Without switch-expression guard coverage on the continuation `=>`,
        // each pattern variable (e.g. `text`, `neg`) would be emitted as a phantom property
        // and `callers` / `impact` would misattribute calls inside the arm to it.
        // issue #233 第3次レビュー指摘: switch expression arm の `=>` が継続行にある
        // multi-line 形を、multi-line 式本体プロパティと誤認しないこと。continuation `=>`
        // まで switch-expression ガードが及ばないと、`text` や `neg` のようなパターン変数が
        // phantom property として抽出され、arm 内の呼び出しが phantom に誤帰属する。
        var content = """
            public class Matcher
            {
                public string Describe(object x) => x switch
                {
                    string text
                        => text.Trim(),
                    int neg
                        => "non-pos",
                    _
                        => "other",
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Matcher");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Describe");
        Assert.DoesNotContain(symbols, s => s.Kind == "property");
        Assert.DoesNotContain(symbols, s => s.Name == "text");
        Assert.DoesNotContain(symbols, s => s.Name == "neg");
    }

    [Fact]
    public void Extract_CSharp_DetectsExplicitInterfaceImpl()
    {
        // Issue #333: the qualifier-pattern widening that unblocked explicit-interface
        // property extraction also fixes the pre-existing method row for multi-argument
        // generic qualifiers (e.g. `IMap<string, int>.GetCount`) and qualifiers that embed
        // nullable / array type arguments.
        // Issue #333: explicit-interface プロパティ抽出のために広げた qualifier パターンは、
        // 既存のメソッド行にも波及し、`IMap<string, int>.GetCount` のような多引数 generic
        // 修飾子や、nullable / array を含む型引数を正しく拾えるようになる。
        var content = "public class MyClass : IDisposable, IComparable<MyClass>\n{\n    void IDisposable.Dispose()\n    {\n    }\n    int IComparable<MyClass>.CompareTo(MyClass other) => 0;\n    int IMap<string, int>.GetCount() => 0;\n    string IFoo<string?>.NullableArg() => \"n\";\n    string IFoo<int[]>.ArrayArg() => \"a\";\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Dispose" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CompareTo" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetCount" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NullableArg" && s.ReturnType == "string");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ArrayArg" && s.ReturnType == "string");
    }

    [Fact]
    public void Extract_CSharp_DetectsGenericOverTupleReturnTypes()
    {
        // Issue #241 / #344 / #484: the shared C# return-type matcher must allow tuple groups
        // inside generic arguments so ordinary methods, interface declarations, and
        // explicit-interface implementations do not silently disappear, even when tuple
        // elements themselves contain nested tuples.
        // Issue #241 / #344 / #484: 共有の C# 戻り値型 matcher は generic 引数内の tuple を
        // 許容し、通常メソッド・interface 宣言・明示的インターフェース実装が
        // 無言で消えないようにしなければならず、tuple 要素側の入れ子 tuple も扱えなければならない。
        var content = """
            namespace Demo;

            public interface IFoo
            {
                System.Collections.Generic.List<(int, int)> GetList();
                System.Threading.Tasks.Task<((int A, int B), string Name)> Nested();
                System.Threading.Tasks.Task<(((int A, int B), int C), string Name)> TooDeep();
            }

            public class Service : IFoo
            {
                public System.Threading.Tasks.Task<(int, string)> MultiAsync() => System.Threading.Tasks.Task.FromResult((1, "x"));
                public System.Collections.Generic.Dictionary<string, (int x, int y)> Coords() => new();
                public System.Collections.Generic.Dictionary<string, (int x, int y)> CoordsProperty { get; } = new();
                public System.Collections.Generic.IEnumerable<(string Key, int Value)> Items() => [];
                System.Collections.Generic.List<(int, int)> IFoo.GetList() => [];
                public System.Threading.Tasks.Task<((int A, int B), string Name)> NestedAsync() => System.Threading.Tasks.Task.FromResult(((1, 2), "n"));
                public System.Threading.Tasks.Task<(((int A, int B), int C), string Name)> TooDeepAsync() => System.Threading.Tasks.Task.FromResult((((1, 2), 3), "deep"));
                System.Threading.Tasks.Task<((int A, int B), string Name)> IFoo.Nested() => System.Threading.Tasks.Task.FromResult(((1, 2), "n"));
                System.Threading.Tasks.Task<(((int A, int B), int C), string Name)> IFoo.TooDeep() => System.Threading.Tasks.Task.FromResult((((1, 2), 3), "deep"));
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var multiAsync = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "MultiAsync"));
        Assert.Equal("System.Threading.Tasks.Task<(int,string)>", multiAsync.ReturnType);

        var coords = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Coords"));
        Assert.Equal("System.Collections.Generic.Dictionary<string,(int x,int y)>", coords.ReturnType);

        var coordsProperty = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "CoordsProperty"));
        Assert.Equal("System.Collections.Generic.Dictionary<string,(int x,int y)>", coordsProperty.ReturnType);

        var items = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Items"));
        Assert.Equal("System.Collections.Generic.IEnumerable<(string Key,int Value)>", items.ReturnType);

        var getListDeclarations = symbols.Where(s => s.Kind == "function" && s.Name == "GetList").ToList();
        Assert.Equal(2, getListDeclarations.Count);
        Assert.Contains(getListDeclarations, s => s.ContainerKind == "interface" && s.ContainerName == "IFoo" && s.ReturnType == "System.Collections.Generic.List<(int,int)>");
        Assert.Contains(getListDeclarations, s => s.ContainerKind == "class" && s.ContainerName == "Service" && s.ReturnType == "System.Collections.Generic.List<(int,int)>");

        var nestedAsync = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "NestedAsync"));
        Assert.Equal("System.Threading.Tasks.Task<((int A,int B),string Name)>", nestedAsync.ReturnType);
        Assert.Contains("System.Threading.Tasks.Task<((int A, int B), string Name)> NestedAsync()", nestedAsync.Signature);

        var nestedDeclarations = symbols.Where(s => s.Kind == "function" && s.Name == "Nested").ToList();
        Assert.Equal(2, nestedDeclarations.Count);
        Assert.Contains(nestedDeclarations, s => s.ContainerKind == "interface" && s.ContainerName == "IFoo" && s.ReturnType == "System.Threading.Tasks.Task<((int A,int B),string Name)>");
        Assert.Contains(nestedDeclarations, s => s.ContainerKind == "class" && s.ContainerName == "Service" && s.ReturnType == "System.Threading.Tasks.Task<((int A,int B),string Name)>");

        var tooDeepAsync = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "TooDeepAsync"));
        Assert.Equal("System.Threading.Tasks.Task<(((int A,int B),int C),string Name)>", tooDeepAsync.ReturnType);
        Assert.Contains("System.Threading.Tasks.Task<(((int A, int B), int C), string Name)> TooDeepAsync()", tooDeepAsync.Signature);

        var tooDeepDeclarations = symbols.Where(s => s.Kind == "function" && s.Name == "TooDeep").ToList();
        Assert.Equal(2, tooDeepDeclarations.Count);
        Assert.Contains(tooDeepDeclarations, s => s.ContainerKind == "interface" && s.ContainerName == "IFoo" && s.ReturnType == "System.Threading.Tasks.Task<(((int A,int B),int C),string Name)>");
        Assert.Contains(tooDeepDeclarations, s => s.ContainerKind == "class" && s.ContainerName == "Service" && s.ReturnType == "System.Threading.Tasks.Task<(((int A,int B),int C),string Name)>");
    }

    [Fact]
    public void Extract_CSharp_DetectsGenericAttributesOnMethodTypeParameters()
    {
        // Issue #347: method type-parameter lists must survive generic attributes whose bodies
        // contain nested angle brackets; otherwise ordinary methods and explicit-interface
        // implementations disappear from symbols / definition.
        // Issue #347: メソッドの型パラメータ列は、入れ子の angle bracket を含む generic 属性が
        // 付いていても保持されなければならない。そうでないと通常メソッドと explicit-interface
        // 実装が symbols / definition から無言で消える。
        var content = """
            namespace GenericAttr;

            public class GenAttr<T> : System.Attribute { }

            public interface IFoo
            {
                void Run<[GenAttr<System.Collections.Generic.List<int>>] T>(T value);
            }

            public class Tagged : IFoo
            {
                public void M<[GenAttr<int>] U>(U u) { }
                public void N<[GenAttr<int>, GenAttr<string>] U>(U u) { }
                public void P<[GenAttr<(int, int)>] U>(U u) { }
                public void Q<[GenAttr<System.Collections.Generic.List<int>>] U>(U u) { }
                public void R<[GenAttr<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>>] U>(U u) { }
                void IFoo.Run<[GenAttr<System.Collections.Generic.List<int>>] T>(T value) { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var methodNames = symbols.Where(s => s.Kind == "function").Select(s => s.Name).ToList();
        Assert.Contains("M", methodNames);
        Assert.Contains("N", methodNames);
        Assert.Contains("P", methodNames);
        Assert.Contains("Q", methodNames);
        Assert.Contains("R", methodNames);

        var m = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "M"));
        Assert.Equal("class", m.ContainerKind);
        Assert.Equal("Tagged", m.ContainerName);
        Assert.Equal("public void M<[GenAttr<int>] U>(U u) { }", m.Signature);

        var n = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "N"));
        Assert.Equal("public void N<[GenAttr<int>, GenAttr<string>] U>(U u) { }", n.Signature);

        var p = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "P"));
        Assert.Equal("public void P<[GenAttr<(int, int)>] U>(U u) { }", p.Signature);

        var q = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Q"));
        Assert.Equal("public void Q<[GenAttr<System.Collections.Generic.List<int>>] U>(U u) { }", q.Signature);

        var r = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "R"));
        Assert.Equal("public void R<[GenAttr<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>>] U>(U u) { }", r.Signature);

        var runDeclarations = symbols.Where(s => s.Kind == "function" && s.Name == "Run").ToList();
        Assert.Equal(2, runDeclarations.Count);
        Assert.Contains(runDeclarations, s => s.ContainerKind == "interface" && s.ContainerName == "IFoo" && s.Signature == "void Run<[GenAttr<System.Collections.Generic.List<int>>] T>(T value);");
        Assert.Contains(runDeclarations, s => s.ContainerKind == "class" && s.ContainerName == "Tagged" && s.Signature == "void IFoo.Run<[GenAttr<System.Collections.Generic.List<int>>] T>(T value) { }");
    }

    [Fact]
    public void Extract_CSharp_DetectsExplicitInterfacePropertyImpl()
    {
        // Issue #333: explicit-interface property implementations must be indexed just like
        // their method counterparts, in both brace-body and expression-body forms, including
        // generic interface qualifiers and alias-qualified / generic return types.
        // Issue #333: explicit-interface プロパティ実装も、メソッド側と同じく brace body / expression body
        // の両形式、generic interface 修飾子、alias-qualified / generic な戻り値型でインデックスされること。
        var content = """
            using System.Collections.Generic;
            namespace Demo;

            public interface IThing
            {
                int Value { get; set; }
                string Name { get; }
            }

            public interface IBucket<T>
            {
                IReadOnlyList<T> Items { get; }
            }

            public class Svc : IThing, IBucket<int>
            {
                int IThing.Value { get; set; }
                string IThing.Name => "x";
                IReadOnlyList<int> IBucket<int>.Items => new List<int>();
                ref readonly int IThing.Ref => ref _field;
                int IMap<string, int>.PairCount => 2;
                string IFoo<string?>.Nullable => "n";
                string IFoo<int[]>.ArrayArg => "a";
                private int _field;

                public int Ordinary { get; set; }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var svcProps = symbols.Where(s => s.Kind == "property" && s.ContainerName == "Svc").ToList();

        var value = Assert.Single(svcProps, s => s.Name == "Value");
        Assert.Equal("int", value.ReturnType);

        var name = Assert.Single(svcProps, s => s.Name == "Name");
        Assert.Equal("string", name.ReturnType);

        var items = Assert.Single(svcProps, s => s.Name == "Items");
        Assert.Equal("IReadOnlyList<int>", items.ReturnType);

        var refProp = Assert.Single(svcProps, s => s.Name == "Ref");
        Assert.Equal("int", refProp.ReturnType);

        // Multi-argument generic qualifier (`IMap<string, int>.PairCount`) and single-arg
        // generic qualifiers that embed nullable / array types — all three were silently
        // dropped before the qualifier pattern was widened.
        // 複数型引数の generic qualifier (`IMap<string, int>.PairCount`) と、単一型引数でも
        // nullable / array を内包する qualifier は、qualifier パターン拡張前は黙って消えていた。
        var pairCount = Assert.Single(svcProps, s => s.Name == "PairCount");
        Assert.Equal("int", pairCount.ReturnType);

        var nullable = Assert.Single(svcProps, s => s.Name == "Nullable");
        Assert.Equal("string", nullable.ReturnType);

        var arrayArg = Assert.Single(svcProps, s => s.Name == "ArrayArg");
        Assert.Equal("string", arrayArg.ReturnType);

        // Sanity: the ordinary property still lands exactly once, and the interface-side property
        // declarations remain present in the symbol set (two entries each for Value / Items — the
        // interface member and its explicit impl).
        // Sanity: 通常 property も 1 件のまま、interface 側の property 宣言も引き続き抽出される
        // （Value / Items は interface メンバー分と explicit 実装分の 2 件ずつが残る）。
        Assert.Single(svcProps, s => s.Name == "Ordinary");
        Assert.Equal(2, symbols.Count(s => s.Kind == "property" && s.Name == "Value"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "property" && s.Name == "Items"));
    }

    [Fact]
    public void Extract_CSharp_DetectsExplicitInterfaceEventImpl()
    {
        // Issue #351: explicit-interface events must emit the trailing event name (`Evt`)
        // instead of dropping the implementation or inventing the qualifier (`IFoo`) as a
        // phantom event. Cover same-line and next-line accessor blocks plus generic/global
        // qualifiers to keep the dedicated event row aligned with the other explicit-member rows.
        // Issue #351: 明示的インターフェース event は、実装を落としたり qualifier (`IFoo`) を
        // 幻の event 名として emit したりせず、末尾の event 名 (`Evt`) を記録しなければならない。
        // 専用 event 行が他の explicit-member 行と揃うよう、同一行/次行 accessor block と
        // generic/global qualifier をまとめて守る。
        var content = """
            namespace Demo;

            public interface IFoo
            {
                event System.EventHandler Evt;
                event System.EventHandler Evt2;
                event System.EventHandler Evt3;
                event System.EventHandler Evt4;
            }

            public class Svc : IFoo
            {
                event System.EventHandler IFoo.Evt
                {
                    add { }
                    remove { }
                }

                event System.EventHandler IFoo.Evt2 { add { } remove { } }
                event System.EventHandler IMap<string, int>.Evt3 { add { } remove { } }
                event System.EventHandler global::Demo.IFoo.Evt4 { add { } remove { } }
                public event System.EventHandler OnBaseline;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(5, symbols.Count(s => s.Kind == "event" && s.ContainerName == "Svc"));
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Evt" && s.ContainerName == "Svc");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Evt2" && s.ContainerName == "Svc");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Evt3" && s.ContainerName == "Svc");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "Evt4" && s.ContainerName == "Svc");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnBaseline" && s.ContainerName == "Svc");
        Assert.DoesNotContain(symbols, s => s.Kind == "event" && s.Name == "IFoo" && s.ContainerName == "Svc");
        Assert.DoesNotContain(symbols, s => s.Kind == "event" && s.Name == "IMap" && s.ContainerName == "Svc");
    }

    [Fact]
    public void Extract_CSharp_DetectsIndexer()
    {
        var content = "public class Collection\n{\n    public string this[int index]\n    {\n        get => _items[index];\n        set => _items[index] = value;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var indexer = symbols.FirstOrDefault(s => s.Name == "Item");
        Assert.NotNull(indexer);
        Assert.Equal("function", indexer.Kind);
        Assert.Equal("string", indexer.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsMultiLineIndexer()
    {
        // #293 follow-up: `StripMultiLineCSharpAttributeInterior` must only blank
        // attribute-position `[`. `public int this[\n int i\n] => _items[i];` opens `[`
        // right after the `this` keyword, which is an indexer parameter list, not
        // an attribute. If that `[` were blanked, the indexer would silently drop
        // out of symbol extraction.
        // #293 追加対応: `StripMultiLineCSharpAttributeInterior` は属性位置の `[` だけを
        // 空白化する必要がある。`public int this[\n int i\n] => _items[i];` の `[` は
        // インデクサのパラメータリストであり属性ではない。ここを空白化するとインデクサが
        // シンボル抽出から静かに消える。
        var content =
            "public class Collection\n" +
            "{\n" +
            "    private int[] _items = new int[10];\n" +
            "    public int this[\n" +
            "        int i\n" +
            "    ] => _items[i];\n" +
            "}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var indexer = symbols.FirstOrDefault(s => s.Name == "Item");
        Assert.NotNull(indexer);
        Assert.Equal("function", indexer.Kind);
        Assert.Equal("int", indexer.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsOperatorOverloads()
    {
        var content = "using System.Collections.Generic;\npublic unsafe struct Money\n{\n    public static (int whole, int cents) operator +(Money a, Money b) => (0, 0);\n    public static Dictionary<string, int> operator -(Money a, Money b) => new();\n    public static bool operator ==(Money a, Money b) => true;\n    public static checked Money operator checked +(Money a, Money b) => new();\n    public static implicit operator decimal(Money m) => 0m;\n    public static explicit operator Money(decimal d) => new();\n    public static explicit operator checked byte(Money m) => 0;\n    public static explicit operator Dictionary<string,int>(Money m) => new();\n    public static explicit operator (int whole,int cents)(Money m) => (0, 0);\n    public static explicit operator (Dictionary<string, int> map, int count)?(Money m) => null;\n    public static explicit operator (int[] items, int count)(Money m) => ([], 0);\n    public static explicit operator ((int a, int b) pair, int count)(Money m) => ((0, 0), 0);\n    public static unsafe explicit operator int*(Money m) => (int*)0;\n    public static unsafe explicit operator delegate* unmanaged[Cdecl]<int, void>(Money m) => (delegate* unmanaged[Cdecl]<int, void>)0;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Money");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator +");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator -");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator ==");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator checked +");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "implicit operator decimal");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator Money");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator checked byte");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator Dictionary<string,int>");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator (int whole,int cents)");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator (int[] items, int count)");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator int*");
    }

    [Fact]
    public void Extract_CSharp_DetectsCheckedOperators()
    {
        // Issue #238: C# 11 user-defined checked operators (unary, binary, and explicit
        // conversion) must be indexed alongside their unchecked counterparts instead of
        // being silently dropped, and the `checked` keyword must survive into the symbol
        // name so AI clients can disambiguate the two overloads.
        // Issue #238: C# 11 のユーザー定義 `operator checked` (単項 / 二項 / 明示的変換) は
        // unchecked 版と両方インデックスされ、`checked` の有無がシンボル名に残ることで
        // AI クライアントがオーバーロードを区別できるようにする。
        var content = """
            namespace Demo;

            public struct N
            {
                public int V;
                public static N operator +(N a, N b) => new() { V = a.V + b.V };
                public static N operator checked +(N a, N b) => checked(new() { V = a.V + b.V });
                public static N operator -(N a, N b) => new() { V = a.V - b.V };
                public static N operator checked -(N a, N b) => checked(new() { V = a.V - b.V });
                public static N operator -(N a) => new() { V = -a.V };
                public static N operator checked -(N a) => checked(new() { V = -a.V });
                public static explicit operator int(N n) => n.V;
                public static explicit operator checked int(N n) => checked((int)n.V);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator +");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator checked +");
        Assert.Equal(2, symbols.Count(s => s.Kind == "operator" && s.Name == "operator -"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "operator" && s.Name == "operator checked -"));
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator int");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator checked int");
    }

    [Fact]
    public void Extract_CSharp_DetectsStaticAbstractInterfaceOperators()
    {
        // Issue #244: C# 11 `static abstract` / `abstract static` interface operator members
        // (the foundation of `System.Numerics.INumber<TSelf>` generic math) were silently
        // dropped because the conversion and binary/unary operator regexes only accepted
        // `static|unsafe|extern` in their modifier slot. Both modifier orders and both
        // regular operators and implicit/explicit conversion operators must now be captured
        // the same way the struct-level counterparts are.
        // Issue #244: C# 11 の `static abstract` / `abstract static` interface operator
        // （`System.Numerics.INumber<TSelf>` などの generic math 基盤）は、変換演算子と
        // 二項/単項演算子の正規表現が modifier スロットで `static|unsafe|extern` しか
        // 受け付けていなかったため黙って取りこぼされていた。両方の修飾子順序と、
        // 通常演算子・implicit/explicit 変換演算子の両方を struct 側と同様に捕捉する。
        var content = """
            namespace Demo;

            public interface IMath<T> where T : IMath<T>
            {
                static abstract T operator +(T a, T b);
                abstract static T operator -(T a, T b);
                static abstract T operator *(T a, T b);
                abstract static T Zero { get; }
                static abstract implicit operator T(int x);
                abstract static explicit operator int(T t);
                abstract static int Compare(T a, T b);
                static abstract T operator checked +(T a, T b);
                abstract static explicit operator checked int(T t);
            }

            public struct N
            {
                public static N operator +(N a, N b) => a;
                public static N operator -(N a, N b) => a;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "interface" && s.Name == "IMath");
        Assert.Equal(2, symbols.Count(s => s.Kind == "operator" && s.Name == "operator +"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "operator" && s.Name == "operator -"));
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator *");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "implicit operator T");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Zero");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Compare");
        // Widening the modifier slot also incidentally covers C# 11 user-defined checked
        // operator variants on `static abstract` / `abstract static` interface members,
        // because the existing operator-name group already accepts `checked`. Pin both the
        // binary `operator checked +` and the conversion `explicit operator checked int`
        // so a future narrowing of the modifier slot cannot silently drop these shapes.
        // modifier スロットを広げたことで C# 11 の `operator checked` 変換演算子 /
        // 二項演算子の interface 形態も副次的に抽出されるようになる。既存の
        // operator 名キャプチャが `checked` を含む形を受け入れているため、
        // ここでは二項 `operator checked +` と変換 `explicit operator checked int` の
        // 両方を固定し、将来 modifier スロットが狭められても無言で落ちないようにする。
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "operator checked +");
        Assert.Contains(symbols, s => s.Kind == "operator" && s.Name == "explicit operator checked int");
    }

    [Fact]
    public void Extract_CSharp_DetectsPointerReturnTypes()
    {
        // Issue #234: methods with pointer / function-pointer return types must still be indexed.
        // Issue #234: ポインタ / 関数ポインタ戻り値型のメソッドも取りこぼさずインデックスする。
        var content = "namespace Demo;\n\npublic unsafe class FP\n{\n    public int* Get(int[] a) { fixed (int* p = a) { return p; } }\n    public void** Double() => null;\n    public byte* Get1() => null;\n    public delegate*<int, int> Transform() => null;\n    public static unsafe int*[] Arr() => null!;\n    public unsafe void Modify(int* p, int v) { *p = v; }\n    public static unsafe int Deref(int* p) => *p;\n    public int* P { get; set; }\n    public byte* Q => null;\n    public int* this[int i] => null;\n}\n\npublic unsafe delegate int* PointerDelegate(int x);\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get" && s.ReturnType == "int*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Double" && s.ReturnType == "void**");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get1" && s.ReturnType == "byte*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Transform" && s.ReturnType == "delegate*<int, int>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Arr" && s.ReturnType == "int*[]");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Modify");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Deref");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "P" && s.ReturnType == "int*");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Q" && s.ReturnType == "byte*");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.ReturnType == "int*");
        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "PointerDelegate" && s.ReturnType == "int*");
    }

    [Fact]
    public void Extract_CSharp_DetectsExplicitInterfacePointerReturnTypes()
    {
        // Issue #234: explicit-interface implementations with pointer / function-pointer
        // return types must still be indexed, including nested generics inside the
        // function-pointer payload and `delegate* unmanaged[Cdecl]<...>` calling conventions.
        // Issue #234: explicit-interface 実装のポインタ / 関数ポインタ戻り値型も取りこぼさず、
        // function-pointer 内部に入れ子の generic がある場合や `delegate* unmanaged[Cdecl]<...>` でも動くこと。
        var content = """
            namespace Demo;

            public unsafe interface IFoo
            {
                int* Get();
                delegate*<int, int> Transform();
                delegate*<System.Collections.Generic.List<int>, int> TransformNested();
                delegate*<delegate*<int, void>, int> TransformFp();
                delegate* unmanaged[Cdecl]<System.Collections.Generic.List<int>, int> TransformUnmanaged();
                byte** Double();
                int*[] Arr();
            }

            public unsafe class Foo : IFoo
            {
                int* IFoo.Get() => null;
                delegate*<int, int> IFoo.Transform() => null;
                delegate*<System.Collections.Generic.List<int>, int> IFoo.TransformNested() => null;
                delegate*<delegate*<int, void>, int> IFoo.TransformFp() => null;
                delegate* unmanaged[Cdecl]<System.Collections.Generic.List<int>, int> IFoo.TransformUnmanaged() => null;
                byte** IFoo.Double() => null;
                int*[] IFoo.Arr() => null!;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Get"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Transform"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "TransformNested"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "TransformFp"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "TransformUnmanaged"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Double"));
        Assert.Equal(2, symbols.Count(s => s.Kind == "function" && s.Name == "Arr"));

        var impls = symbols.Where(s => s.Kind == "function" && s.ContainerName == "Foo").ToList();
        Assert.Equal("int*", impls.Single(s => s.Name == "Get").ReturnType);
        Assert.Equal("delegate*<int, int>", impls.Single(s => s.Name == "Transform").ReturnType);
        Assert.Equal("delegate*<System.Collections.Generic.List<int>, int>", impls.Single(s => s.Name == "TransformNested").ReturnType);
        Assert.Equal("delegate*<delegate*<int, void>, int>", impls.Single(s => s.Name == "TransformFp").ReturnType);
        // Spaces inside `unmanaged[...]<...>` payload are collapsed by CollapseCSharpGenericTypeWhitespace
        // because the outer `<` has `]` as predecessor (a recognized generic-angle start); non-unmanaged
        // `delegate*<...>` keeps payload spaces because the outer `<` has `*` as predecessor.
        // `unmanaged[...]<...>` の payload 内のスペースは CollapseCSharpGenericTypeWhitespace で除去される
        // （outer `<` の直前が `]` で generic angle start と認識されるため）。通常の `delegate*<...>` は
        // outer `<` の直前が `*` で認識されないため payload 内スペースは保持される。
        Assert.Equal("delegate* unmanaged[Cdecl]<System.Collections.Generic.List<int>,int>", impls.Single(s => s.Name == "TransformUnmanaged").ReturnType);
        Assert.Equal("byte**", impls.Single(s => s.Name == "Double").ReturnType);
        Assert.Equal("int*[]", impls.Single(s => s.Name == "Arr").ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialMethods()
    {
        // C# 9 extended partial methods / C# 9 拡張 partial メソッド
        var content = "public partial class App\n{\n    partial void OnInit();\n    partial OnImplicit();\n    public partial string GetName();\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "App");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnInit" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnImplicit" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetName" && s.ReturnType == "string");
    }

    [Fact]
    public void Extract_CSharp_DetectsPartialConstructors()
    {
        var content = """
            public partial class Widget
            {
                public partial Widget();
                partial Widget();
                public partial Widget() { }
                unsafe public partial Widget(int* ptr) { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Widget");
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "Widget"
            && s.Line == 3
            && s.Visibility == "public"
            && s.ReturnType == null);
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "Widget"
            && s.Line == 4
            && s.ReturnType == null);
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "Widget"
            && s.Line == 5
            && s.Visibility == "public"
            && s.BodyStartLine is not null
            && s.BodyEndLine is not null);
        Assert.Contains(symbols, s =>
            s.Kind == "function"
            && s.Name == "Widget"
            && s.Line == 6
            && s.Visibility == "public");
    }

    [Fact]
    public void Extract_CSharp_DetectsGenericMethodOverloads()
    {
        // Issue #41: generic method overloads should both be extracted as definitions
        // Issue #41: ジェネリックメソッドのオーバーロードは両方とも定義として抽出されるべき
        var content = "public class App\n{\n    private static void TryRaise(Action? handler, string context) { }\n    private static void TryRaise<T>(Action<T>? handler, T argument, string context) { }\n    public Task<List<T>> GetItems<T>(int page) { return null; }\n    public void Process<TKey, TValue>(Dictionary<TKey, TValue> map) { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TryRaise" && s.Line == 3);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TryRaise" && s.Line == 4);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "GetItems");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Process");
    }

    [Fact]
    public void Extract_CSharp_DetectsSpacedAndNestedGenericReturnTypeMethods()
    {
        var content = """
            public class App
            {
                public Task<Result<string, Error>> WithSpace() => null!;
                public Task<Result<string,Error>> NoSpace() => null!;
                public Dictionary<string, List<int>> Map() => new();
                public Tuple<int, int, int, int> Quad() => new(1, 2, 3, 4);
                public Func<int, int, int> Make() => null!;
                public Task<List<Tuple<int, int, int>>> Deep() => null!;
                public (int Left, string Right) Pair() => default;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var withSpace = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "WithSpace"));
        Assert.Equal("Task<Result<string,Error>>", withSpace.ReturnType);

        var noSpace = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "NoSpace"));
        Assert.Equal("Task<Result<string,Error>>", noSpace.ReturnType);

        var map = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Map"));
        Assert.Equal("Dictionary<string,List<int>>", map.ReturnType);

        var quad = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Quad"));
        Assert.Equal("Tuple<int,int,int,int>", quad.ReturnType);

        var make = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Make"));
        Assert.Equal("Func<int,int,int>", make.ReturnType);

        var deep = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Deep"));
        Assert.Equal("Task<List<Tuple<int,int,int>>>", deep.ReturnType);

        var pair = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Pair"));
        Assert.Equal("(int Left, string Right)", pair.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsSpacedGenericTypeMembersBeyondMethods()
    {
        var content = """
            public delegate Dictionary<string, int> Factory();

            public interface IFoo
            {
                Dictionary<string, int> Get();
            }

            public class Holder : IFoo
            {
                public Dictionary<string, int> Lookup { get; set; } = new();
                public event Action<string, int> OnLog;

                Dictionary<string, int> IFoo.Get() => Lookup;
                public Dictionary<string, int> this[int index] => Lookup;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "delegate" && s.Name == "Factory");
        Assert.Contains(symbols, s => s.Kind == "event" && s.Name == "OnLog" && s.ContainerName == "Holder");

        var lookup = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Lookup"));
        Assert.Equal("Dictionary<string,int>", lookup.ReturnType);
        Assert.Equal("Holder", lookup.ContainerName);

        var indexer = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Item"));
        Assert.Equal("Dictionary<string,int>", indexer.ReturnType);
        Assert.Equal("Holder", indexer.ContainerName);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get" && s.Line == 5 && s.ReturnType == "Dictionary<string,int>");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Get" && s.ContainerName == "Holder" && s.ReturnType == "Dictionary<string,int>");
    }

    [Fact]
    public void Extract_CSharp_DetectsTupleReturnTypesWithTrailingSuffix()
    {
        // Issue #328: tuple return types with a trailing suffix (`[]`, `?`, `[,]`, `[][]`)
        // must not be silently dropped. The C# returnType alternation's tuple branch
        // needs to carry a trailing `(?:\?|\[[\],\s]*\])*` loop so tuple-array and
        // nullable-tuple members are captured on methods, properties, indexers, and
        // explicit interface implementations.
        // Issue #328: 末尾サフィックス（`[]` / `?` / `[,]` / `[][]`）付きの tuple 戻り値型が
        // サイレントに落ちてはならない。C# の returnType 分岐の tuple 側に
        // `(?:\?|\[[\],\s]*\])*` のループを持たせ、tuple-array / nullable-tuple を
        // メソッド・プロパティ・インデクサ・明示的インターフェース実装で捕捉する。
        var content = """
            namespace Demo;

            public class Svc
            {
                public (int, int)[]        A()  => new (int, int)[0];
                public (int x, int y)[]    B()  => new (int x, int y)[0];
                public (int, int)?         C()  => null;
                public (int x, int y)?     D()  => null;
                public (int, int)[][]      E()  => new (int, int)[0][];
                public (int, int)[,]       F()  => new (int, int)[0, 0];
                public (int, int)?[]       G()  => null!;
                public (int, int)[]? H()         => null;
                public (int, int)[] Ap { get; set; } = System.Array.Empty<(int, int)>();
                public (int, int)? Np { get; set; }
                public (int, int)[] Fp => new (int, int)[0];
                public (int, int)[] this[int index] => Ap;
                (int, int)? ICoord.MaybeFind(string key) => null;
                (int, int)[] ICoord.FindAll(string key) => System.Array.Empty<(int, int)>();
                public (int, int) Plain() => (0, 0);
                public (int, int) PlainProp { get; set; }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var a = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "A"));
        Assert.Equal("(int, int)[]", a.ReturnType);

        var b = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "B"));
        Assert.Equal("(int x, int y)[]", b.ReturnType);

        var c = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "C"));
        Assert.Equal("(int, int)?", c.ReturnType);

        var d = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "D"));
        Assert.Equal("(int x, int y)?", d.ReturnType);

        var e = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "E"));
        Assert.Equal("(int, int)[][]", e.ReturnType);

        var f = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "F"));
        Assert.Equal("(int, int)[,]", f.ReturnType);

        var g = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "G"));
        Assert.Equal("(int, int)?[]", g.ReturnType);

        var h = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "H"));
        Assert.Equal("(int, int)[]?", h.ReturnType);

        var ap = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Ap"));
        Assert.Equal("(int, int)[]", ap.ReturnType);

        var np = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Np"));
        Assert.Equal("(int, int)?", np.ReturnType);

        var fp = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "Fp"));
        Assert.Equal("(int, int)[]", fp.ReturnType);

        var indexer = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Item" && s.ContainerName == "Svc"));
        Assert.Equal("(int, int)[]", indexer.ReturnType);

        var maybeFindImpl = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "MaybeFind" && s.ContainerName == "Svc"));
        Assert.Equal("(int, int)?", maybeFindImpl.ReturnType);

        var findAllImpl = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "FindAll" && s.ContainerName == "Svc"));
        Assert.Equal("(int, int)[]", findAllImpl.ReturnType);

        // Regression: plain tuple without a suffix still captured.
        // 回帰: サフィックスなしの素の tuple も引き続き捕捉される。
        var plain = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Plain"));
        Assert.Equal("(int, int)", plain.ReturnType);

        var plainProp = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "PlainProp"));
        Assert.Equal("(int, int)", plainProp.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsFileScopedType()
    {
        // C# 11 file-scoped type / C# 11 のファイルスコープ型
        var content = "file class InternalHelper\n{\n    file static void DoWork() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "InternalHelper");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "DoWork");
    }

    [Fact]
    public void Extract_CSharp_DetectsRefStruct()
    {
        var content = "public readonly ref struct Span2D<T> { }\nref struct StackBuffer { }\npublic readonly struct ImmutablePoint { }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "Span2D");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "StackBuffer");
        Assert.Contains(symbols, s => s.Kind == "struct" && s.Name == "ImmutablePoint");
    }

    [Fact]
    public void Extract_CSharp_DetectsRefReturnMethodsPropertiesAndIndexers()
    {
        var content = """
            public interface IRefBox
            {
                ref int GetRef();
                ref readonly int GetRefReadonly();
            }

            public class RefBox : IRefBox
            {
                private static int _value;
                private static readonly int[] _items = [1, 2, 3];

                public static ref int RefReturn(ref int[] arr, int i) => ref arr[i];
                public static ref readonly int RefReadonlyReturn(int[] arr, int i) => ref arr[i];
                public ref int PropRef => ref _value;
                public ref readonly int PropRefRo { get => ref _value; }
                public ref readonly int this[int index] => ref _items[index];
                ref int IRefBox.GetRef() => ref _value;
                ref readonly int IRefBox.GetRefReadonly() => ref _value;
            }

            public struct RefReadonlyMembers
            {
                private static int _value;

                public readonly ref readonly int PropReadonly => ref _value;
                public readonly ref readonly int this[int index] => ref _value;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RefReturn" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RefReadonlyReturn" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PropRef" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PropRefRo" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PropReadonly" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.ReturnType == "int");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Item" && s.Signature != null && s.Signature.Contains("public readonly ref readonly int this[int index]"));

        var explicitGetRef = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "GetRef" && s.ContainerName == "RefBox"));
        Assert.Equal("int", explicitGetRef.ReturnType);
        Assert.Contains("ref int IRefBox.GetRef()", explicitGetRef.Signature);

        var explicitGetRefReadonly = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "GetRefReadonly" && s.ContainerName == "RefBox"));
        Assert.Equal("int", explicitGetRefReadonly.ReturnType);
        Assert.Contains("ref readonly int IRefBox.GetRefReadonly()", explicitGetRefReadonly.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembers()
    {
        var content = "public enum Color\n{\n    Red,\n    Green = 1,\n    Blue = 2,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Color");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Red");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Green");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Blue");
    }

    [Fact]
    public void Extract_CSharp_DetectsCompactAndZeroIndentEnumMembers()
    {
        var content = "namespace Demo;\n\npublic enum Compact { A, B = A }\npublic enum Flat\n{\nC,\nD = C\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var compactA = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "A"));
        var compactB = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "B"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Compact");
        Assert.Equal("enum", compactA.ContainerKind);
        Assert.Equal("Compact", compactA.ContainerName);
        Assert.Equal("Demo.Compact", compactA.ContainerQualifiedName);
        Assert.Equal("enum", compactB.ContainerKind);
        Assert.Equal("Compact", compactB.ContainerName);
        Assert.Equal("Demo.Compact", compactB.ContainerQualifiedName);
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Flat");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "C");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "D");
    }

    [Fact]
    public void Extract_CSharp_DetectsSameLineSiblingEnums()
    {
        var content = "namespace Demo;\n\npublic enum InlineA { A1 } public enum InlineB { B1 }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var a1 = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "A1"));
        var b1 = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "B1"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "InlineA");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "InlineB");
        Assert.Equal("InlineA", a1.ContainerName);
        Assert.Equal("Demo.InlineA", a1.ContainerQualifiedName);
        Assert.Equal("InlineB", b1.ContainerName);
        Assert.Equal("Demo.InlineB", b1.ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_DetectsCompactEnumMembersWithAttributesAndCastValues()
    {
        var content = "public enum Mode { [Obsolete] A = (int)B, [EnumMember(Value = \"b\")] B = (MyFlags)(A | C), C = 1 }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "C");
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembersAcrossDirectiveLines()
    {
        var content = "public enum Mode\n{\n#if DEBUG\n    A,\n#endif\n#region values\n    B,\n#endregion\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var memberA = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "A"));
        var memberB = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "B"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Equal("enum", memberA.ContainerKind);
        Assert.Equal("Mode", memberA.ContainerName);
        Assert.Equal("enum", memberB.ContainerKind);
        Assert.Equal("Mode", memberB.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_TrimsClosingBraceFromFinalEnumMemberSpan()
    {
        var content = "public enum Status\n{\n    Ready,\n    Busy\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var busy = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "Busy"));

        Assert.Equal("Busy", busy.Signature);
        Assert.Equal(4, busy.EndLine);
        Assert.DoesNotContain("}", busy.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsLowercaseAndUnicodeEnumMembers()
    {
        var content = "public enum Status\n{\n    active,\n    inactive,\n    Δelta = active,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "active");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "inactive");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Δelta");
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembersWhenAttributeSharesDeclarationLine()
    {
        var content = "[Flags] public enum Mode\n{\n    A,\n    B = A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
    }

    [Fact]
    public void Extract_CSharp_DetectsEnumMembersWhenMemberAttributesShareLine()
    {
        var content = "public enum Mode\n{\n    [Obsolete] A,\n    [EnumMember(Value = \"a\")] B = A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
    }

    [Fact]
    public void Extract_CSharp_DoesNotTreatMultilineEnumMemberAttributeArgumentsAsMembers()
    {
        var content = "using System.Runtime.Serialization;\n\npublic enum Mode\n{\n    [EnumMember(\n        Value = Alias,\n        Other = 1)]\n    A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Value");
        Assert.DoesNotContain(symbols, s => s.Name == "Other");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
    }

    [Fact]
    public void Extract_CSharp_DetectsTabIndentedEnumMembers()
    {
        var content = "public enum Mode\n{\n\tA,\n\tB = A,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
    }

    [Fact]
    public void Extract_CSharp_RecoversAfterIncompleteEnumDeclarationAttribute()
    {
        var content = "[Attr(\npublic enum Mode\n{\n    A,\n}\n\npublic class After\n{\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "After");
    }

    [Fact]
    public void Extract_CSharp_RecoversAfterIncompleteEnumMemberAttribute()
    {
        var content = "public enum Mode\n{\n    [Attr()\n    A,\n    B\n}\n\npublic class After\n{\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "A");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "B");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "After");
    }

    [Fact]
    public void Extract_CSharp_RecoversAfterIncompleteEnumMemberAttributeMissingParen()
    {
        var content = "public enum BrokenAttr\n{\n    [Attr(\n    X,\n    Y\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var memberY = Assert.Single(symbols.Where(s => s.Kind == "enum" && s.Name == "Y"));

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "BrokenAttr");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "X");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Y");
        Assert.Equal("Y", memberY.Signature);
        Assert.Equal(5, memberY.EndLine);
        Assert.DoesNotContain("}", memberY.Signature);
    }

    [Fact]
    public void Extract_CSharp_RecoversModifierlessMembersAfterIncompleteAttribute()
    {
        var content = "public class C\n{\n    [Attr(\n    void M() {}\n    string Name { get; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name");
    }

    [Fact]
    public void Extract_CSharp_RecoversBracketTypedMembersAfterIncompleteAttribute()
    {
        var content = "public class C\n{\n    [Attr(\n    int[] Values { get; }\n    int[] Build() => [];\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Values");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Build");
    }

    [Fact]
    public void Extract_CSharp_EnumMembersTrackOwningEnum()
    {
        var content = "namespace Demo;\n\npublic enum First\n{\n    None,\n}\n\npublic enum Second\n{\n    None,\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var noneMembers = symbols
            .Where(s => s.Kind == "enum" && s.Name == "None")
            .OrderBy(s => s.Line)
            .ToList();

        Assert.Equal(2, noneMembers.Count);
        Assert.Equal("enum", noneMembers[0].ContainerKind);
        Assert.Equal("First", noneMembers[0].ContainerName);
        Assert.Equal("Demo.First", noneMembers[0].ContainerQualifiedName);
        Assert.Equal("enum", noneMembers[1].ContainerKind);
        Assert.Equal("Second", noneMembers[1].ContainerName);
        Assert.Equal("Demo.Second", noneMembers[1].ContainerQualifiedName);
    }

    [Fact]
    public void Extract_CSharp_EnumMemberDoesNotMatchObjectInitializer()
    {
        // Object initializer lines should not be extracted as enum members
        // オブジェクト初期化子行は enum メンバーとして抽出されないこと
        var content = "var user = new User\n{\n    Name = \"Alice\",\n    Age = 30,\n    Email = GetEmail(),\n};";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.DoesNotContain(symbols, s => s.Name == "Name");
        Assert.DoesNotContain(symbols, s => s.Name == "Age");
        Assert.DoesNotContain(symbols, s => s.Name == "Email");
    }

    [Fact]
    public void Extract_CSharp_Issue374_ObjectInitializerNumericAssignmentsDoNotCreatePhantomSymbols()
    {
        // Closes #374: the full issue repro uses both multiline and inline object initializers
        // with numeric assignments like `Age = 30,` and `Priority = 2`. Those lines must not
        // reappear as phantom enum-member symbols just because they share the old `Name = 1,`
        // surface shape.
        // Closes #374: issue 本文の再現ケースでは `Age = 30,` や `Priority = 2` のような
        // 数値代入を含む multiline / inline object initializer が混在する。旧来の
        // `Name = 1,` 形に見えても phantom enum-member symbol を再発させてはいけない。
        var content = """
            using System.Collections.Generic;

            namespace CsObjInitPhantom;

            public class Person
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
                public int Priority { get; set; }
            }

            public class Creator
            {
                public Person CreatePerson() => new Person
                {
                    Name = "Alice",
                    Age = 30,
                    Priority = 1
                };

                public List<Person> CreateMany() => new()
                {
                    new Person { Name = "Bob", Age = 25, Priority = 2 },
                    new Person
                    {
                        Name = "Carol",
                        Age = 45,
                        Priority = 3
                    }
                };
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ageSymbols = symbols.Where(s => s.Name == "Age").ToList();
        Assert.Single(ageSymbols);
        Assert.Equal("property", ageSymbols[0].Kind);
        Assert.Equal("Person", ageSymbols[0].ContainerName);

        var prioritySymbols = symbols.Where(s => s.Name == "Priority").ToList();
        Assert.Single(prioritySymbols);
        Assert.Equal("property", prioritySymbols[0].Kind);
        Assert.Equal("Person", prioritySymbols[0].ContainerName);

        var nameSymbols = symbols.Where(s => s.Name == "Name").ToList();
        Assert.Single(nameSymbols);
        Assert.Equal("property", nameSymbols[0].Kind);
        Assert.Equal("Person", nameSymbols[0].ContainerName);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CreatePerson" && s.ContainerName == "Creator");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "CreateMany" && s.ContainerName == "Creator");
    }

    [Fact]
    public void Extract_CSharp_Issue357_EnumMembersWithComplexConstantExpressionsStayIndexed()
    {
        // Current main already captures these enum members, but the open issue fixture was not
        // pinned in tests. Keep the exact value-shape mix here so future regex tightening does
        // not silently re-drop member-access, cast, or parenthesized constant expressions.
        // 現在の main はこれらの enum member を抽出できるが、open issue の fixture 自体は
        // テストで固定されていなかった。将来の regex 調整で member access / cast /
        // parenthesized constant expression が黙って再脱落しないよう、この value-shape
        // の混在をここで固定する。
        var content = """
            namespace CsEnumComplexValue;

            public class K
            {
                public const int Foo = 1;
            }

            public enum E1
            {
                Plain = 0,
                Hex = 0xFF,
                Combined = Plain | 0,
                Shifted = 1 << 3,
                Arith = 1 + 2,
                ConstRef = K.Foo,
                Casted = (int)1.5,
                Paren = (1 + 2),
                CharCast = (int)'A',
                MemberAccess = System.Int32.MaxValue,
            }

            [System.Flags]
            public enum Permissions
            {
                None = 0,
                Read = 1,
                Write = 2,
                All = Read | Write,
                Execute = K.Foo,
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        foreach (var name in new[] { "Plain", "Hex", "Combined", "Shifted", "Arith", "ConstRef", "Casted", "Paren", "CharCast", "MemberAccess" })
        {
            var symbol = Assert.Single(symbols.Where(s => s.Name == name));
            Assert.Equal("enum", symbol.Kind);
            Assert.Equal("E1", symbol.ContainerName);
            Assert.Equal("enum", symbol.ContainerKind);
        }

        foreach (var name in new[] { "None", "Read", "Write", "All", "Execute" })
        {
            var symbol = Assert.Single(symbols.Where(s => s.Name == name));
            Assert.Equal("enum", symbol.Kind);
            Assert.Equal("Permissions", symbol.ContainerName);
            Assert.Equal("enum", symbol.ContainerKind);
        }
    }

    [Fact]
    public void Extract_CSharp_Issue339_SameLineAttributedEnumMembersStayIndexed()
    {
        // Same-line C# attributes on enum members must not hide the member name.
        // C# enum member の同行 attribute は member 名を隠してはならない。
        var content = """
            namespace EnumAttr;

            public enum Status
            {
                [System.Obsolete] Legacy = 0,
                [System.ComponentModel.DefaultValue(1)] B = 1,
                [System.Obsolete][System.ComponentModel.Browsable(false)] D = 3,

                [System.Obsolete]
                E = 4,

                F = 5,
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Status");
        foreach (var name in new[] { "Legacy", "B", "D", "E", "F" })
        {
            var symbol = Assert.Single(symbols.Where(s => s.Name == name));
            Assert.Equal("enum", symbol.Kind);
            Assert.Equal("Status", symbol.ContainerName);
            Assert.Equal("enum", symbol.ContainerKind);
        }
    }

    [Fact]
    public void Extract_CSharp_DetectsRegionDirectives()
    {
        var content = "#region Private Methods\nvoid Helper() { }\n#endregion\n\n#region Properties\npublic int X { get; set; }\n#endregion";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Private Methods");
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Properties");
    }

    [Fact]
    public void Extract_CSharp_DetectsStaticConstructorAndFinalizer()
    {
        var content = "public class Cache\n{\n    static Cache()\n    {\n        // init\n    }\n\n    ~Cache()\n    {\n        // cleanup\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Cache");
        // Static constructor — extracted by the static ctor pattern
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Cache" && s.Line == 3);
        // Finalizer
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Cache" && s.Line == 8);
    }

    [Fact]
    public void Extract_CSharp_RangeIgnoresLiteralBracesInsideMethodBodies()
    {
        var content = """
            public class BraceExamples
            {
                public void First()
                {
                    var open = "{";
                    var close = '}';
                    var interpolated = $"{{";
                    var verbatim = @"{";
                }

                public void Second()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var first = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "First"));
        var second = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Second"));
        Assert.Equal(3, first.Line);
        Assert.Equal(9, first.EndLine);
        Assert.Equal(11, second.Line);
        Assert.Equal(13, second.EndLine);
        Assert.Equal(2, symbols.Count(s => s.Kind == "function"));
    }

    [Fact]
    public void Extract_CSharp_DoesNotExtractSymbolsFromRawStringsVerbatimStringsOrComments()
    {
        var content = """"
            public class RealExample
            {
                public const string RawFixture = """
                    class Hidden
                    {
                        public void Fake()
                        {
                        }
                    }
                    """;

                public const string VerbatimFixture = @"class AlsoHidden
            {
                public void FakeVerbatim()
                {
                }
            }";

                /*
                class CommentHidden
                {
                    public void FakeComment()
                    {
                    }
                }
                */

                public void Keep()
                {
                }
            }
            """";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "RealExample");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Keep");
        Assert.DoesNotContain(symbols, s => s.Name == "Hidden");
        Assert.DoesNotContain(symbols, s => s.Name == "Fake");
        Assert.DoesNotContain(symbols, s => s.Name == "AlsoHidden");
        Assert.DoesNotContain(symbols, s => s.Name == "FakeVerbatim");
        Assert.DoesNotContain(symbols, s => s.Name == "CommentHidden");
        Assert.DoesNotContain(symbols, s => s.Name == "FakeComment");
        Assert.Equal(1, symbols.Count(s => s.Kind == "class"));
    }

    [Fact]
    public void Extract_CSharp_DetectsNullableReturnTypeMethods()
    {
        var content = "public static class GitHelper\n{\n    public static string? ResolveGitCommonDir(string projectRoot)\n    {\n        return null;\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var method = Assert.Single(symbols.Where(s => s.Name == "ResolveGitCommonDir"));
        Assert.Equal("function", method.Kind);
        Assert.Equal("string?", method.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_EnumMembers_DoNotUseXmlDocProseAsNames()
    {
        const string content = """
            internal enum ColorMode
            {
                Auto = 0,
                /// <summary>Use ANSI colors even when stdout is redirected.</summary>
                Always = 1,
                /// <summary>Disable ANSI colors even on a TTY.</summary>
                Never = 2,
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Auto" && s.ContainerName == "ColorMode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Always" && s.ContainerName == "ColorMode");
        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Never" && s.ContainerName == "ColorMode");
        Assert.DoesNotContain(symbols, s => s.Kind == "enum" && s.Name == "even" && s.ContainerName == "ColorMode");
    }

    [Fact]
    public void Extract_CSharp_DetectsPlainFieldDeclarations()
    {
        // Plain fields are now captured as kind `property` so definition/symbols/outline/
        // hotspots/unused can see the full member surface of a class. See issue #298.
        // 通常フィールドも kind `property` として抽出される（issue #298）。これにより
        // definition/symbols/outline/hotspots/unused がクラスの全メンバー形を見える。
        var content = "public class Config\n{\n    public string Name;\n    private int _count;\n    public readonly string Id = \"x\";\n    protected List<int> Items = new();\n    internal volatile bool IsReady;\n    public static int GlobalCount;\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Name" && s.ReturnType == "string" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_count" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Id" && s.ReturnType == "string" && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Items" && s.Visibility == "protected");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "IsReady" && s.ReturnType == "bool" && s.Visibility == "internal");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "GlobalCount" && s.ReturnType == "int" && s.Visibility == "public");
        // const / static readonly keep kind `function` / const と static readonly は引き続き kind `function`
        Assert.DoesNotContain(symbols, s => s.Name == "Name" && s.Kind == "function");
        Assert.DoesNotContain(symbols, s => s.Name == "_count" && s.Kind == "function");
        Assert.DoesNotContain(symbols, s => s.Name == "Id" && s.Kind == "function");
    }

    [Fact]
    public void Extract_CSharp_PlainFieldPatternDoesNotLeakLocalVariables()
    {
        // Plain fields are captured as kind `property`, but local variable declarations
        // inside method / property accessor / constructor / lambda bodies share the same
        // shape as fields. Without a scope gate, names like `local`, `numbers`, `tmp`
        // would leak into `symbols`, `definition`, `outline`, `inspect`, and `unused`.
        // Closes #298 follow-up (codex review blocker).
        // 通常フィールドは kind `property` として抽出されるが、メソッド・アクセサ・
        // コンストラクタ・ラムダの内部にあるローカル変数宣言はフィールドと同じ形を持つ。
        // スコープ判定を入れないと `local`、`numbers`、`tmp` などが
        // `symbols` / `definition` / `outline` / `inspect` / `unused` に混入する。
        // Closes #298 の codex レビュー blocker 対応。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class Worker",
            "{",
            "    public string Field;",
            "    public List<int> Items = new();",
            "",
            "    public Worker()",
            "    {",
            "        string ctorLocal = \"ctor\";",
            "        List<int> ctorNumbers = new();",
            "    }",
            "",
            "    public void Run()",
            "    {",
            "        string local = \"x\";",
            "        System.Collections.Generic.List<int> numbers = new();",
            "        if (local.Length > 0)",
            "        {",
            "            string inner = local;",
            "        }",
            "    }",
            "",
            "    public int Value",
            "    {",
            "        get",
            "        {",
            "            int tmp = 1;",
            "            return tmp;",
            "        }",
            "    }",
            "",
            "    public Func<int, int> Lambda = x =>",
            "    {",
            "        int y = x + 1;",
            "        return y;",
            "    };",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Field");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Items");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Lambda");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Worker");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Run");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Value");

        Assert.DoesNotContain(symbols, s => s.Name == "ctorLocal");
        Assert.DoesNotContain(symbols, s => s.Name == "ctorNumbers");
        Assert.DoesNotContain(symbols, s => s.Name == "local");
        Assert.DoesNotContain(symbols, s => s.Name == "numbers");
        Assert.DoesNotContain(symbols, s => s.Name == "inner");
        Assert.DoesNotContain(symbols, s => s.Name == "tmp");
        Assert.DoesNotContain(symbols, s => s.Name == "y");
    }

    [Fact]
    public void Extract_CSharp_ConstLocalsAndQualifiedCallArguments_DoNotLeakPhantomFunctions()
    {
        var content = """
            using System;

            namespace Demo;

            public class Repro
            {
                public void M(TimeSpan elapsed)
                {
                    const string content = "hello";
                    Assert.True(
                        elapsed < TimeSpan.FromSeconds(10),
                        $"x {elapsed.TotalSeconds:F2}");
                }
            }

            public static class Assert
            {
                public static void True(bool condition, string message) { }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "Demo");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Repro");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "M");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Assert");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "True");

        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "content");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "FromSeconds");
    }

    [Fact]
    public void Extract_CSharp_VerbatimReturnTypeIdentifiers_AreNotRejectedBySuffixGuard()
    {
        var content = """
            namespace Demo;

            public class @new {}

            public class UsesVerbatim
            {
                public @new Make() => new @new();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "new");

        var method = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Make"));
        Assert.Equal("@new", method.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_ContextualKeywordReturnTypeIdentifiers_AreNotRejectedBySuffixGuard()
    {
        var content = """
            namespace Demo;

            public class await {}
            public class yield {}

            public class Uses
            {
                public await MakeAwait() => new await();
                public yield MakeYield() => new yield();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "await");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "yield");

        var awaitMethod = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "MakeAwait"));
        Assert.Equal("await", awaitMethod.ReturnType);

        var yieldMethod = Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "MakeYield"));
        Assert.Equal("yield", yieldMethod.ReturnType);
    }

    [Fact]
    public void Extract_CSharp_DetectsMultiLineFieldDeclaration()
    {
        // Plain field whose type occupies one line and whose name / initializer spill
        // onto the next line (`private Dictionary<string, int>\n    _map = new();`) must
        // still be captured as a single `property` symbol. The multi-line property match
        // builder combines the header and continuation lines before handing them to the
        // field regex. Closes #298 follow-up (codex adversarial review).
        // 型が 1 行目、名前と初期化式が次行へ回る通常フィールド
        // （`private Dictionary<string, int>\n    _map = new();`）も、1 件の `property`
        // シンボルとして抽出する。multi-line property match builder がヘッダ行と
        // 継続行を結合してから field regex に渡す。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "using System.Collections.Generic;",
            "namespace Demo;",
            "public class Store",
            "{",
            "    private Dictionary<string, int>",
            "        _map = new();",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_map"
            && s.Visibility == "private"
            && s.ReturnType == "Dictionary<string,int>");
    }

    [Fact]
    public void Extract_CSharp_DetectsMultiLineFieldWithVolatileAndUnsafeModifiers()
    {
        // The multi-line header prefix check must accept field-only modifiers such as
        // `volatile`, `unsafe`, and `extern`, otherwise the combined match line is
        // never built and the declaration silently disappears from the index. Closes
        // #298 follow-up (second codex adversarial review).
        // multi-line ヘッダ判定は `volatile` / `unsafe` / `extern` のような field 固有の
        // 修飾子も受け入れないと、結合済みマッチ行が作られず宣言がインデックスから
        // 黙って消える。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "using System.Collections.Generic;",
            "namespace Demo;",
            "public unsafe class Edge",
            "{",
            "    private volatile Dictionary<string, int>",
            "        _map;",
            "    public unsafe delegate*<int, void>",
            "        Callback;",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_map"
            && s.Visibility == "private"
            && s.ReturnType == "Dictionary<string,int>");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "Callback"
            && s.Visibility == "public");
    }

    [Fact]
    public void Extract_CSharp_DetectsMultiLineFieldWithParenthesizedInitializer()
    {
        // Multi-line fields whose initializer uses a constructor call or parenthesized
        // expression (`= new(\n    …);`) — including bodies that contain a lambda —
        // must still walk through the `(` and merge until the top-level `;`. Without
        // the depth-aware terminator, the earlier `(` break dropped the symbol.
        // Closes #298 follow-up (second codex adversarial review).
        // 複数行フィールドの初期化式がコンストラクタ呼び出しや括弧付き式
        // （`= new(\n    …);`、ラムダを含む場合も）であっても、`(` で打ち切らず
        // トップレベル `;` まで結合する。深さ追跡なしの `(` break ではシンボルが
        // 消えていた。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "using System;",
            "namespace Demo;",
            "public class Lazies",
            "{",
            "    private Lazy<int>",
            "        _value = new(",
            "            () => 42);",
            "    private Lazy<int>",
            "        _plain = new(",
            "            42);",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_value"
            && s.Visibility == "private"
            && s.ReturnType == "Lazy<int>");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_plain"
            && s.Visibility == "private"
            && s.ReturnType == "Lazy<int>");
    }

    [Fact]
    public void Extract_CSharp_DeclaratorListSurvivesComparisonInitializer()
    {
        // Declarator tail scanning must distinguish generic `<`/`>` from the comparison
        // operators inside initializers. Without a token-aware lookahead, expressions
        // like `_a = x < y ? 1 : 2, _b;` inflate the angle depth forever and drop the
        // trailing declarators. Closes #298 follow-up (second codex adversarial review).
        // declarator tail 走査は、初期化式内の比較演算子と generic の `<`/`>` を区別する
        // 必要がある。先読みなしでは `_a = x < y ? 1 : 2, _b;` のような初期化式で
        // angle 深さが 0 に戻らず、後続 declarator が消える。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "public class Compare",
            "{",
            "    private int x = 1, y = 2;",
            "    private int _a = x < y ? 1 : 2, _b;",
            "    private int _c = x > y ? 3 : 4, _d;",
            "    private int _e = new System.Collections.Generic.Dictionary<int, int>() { [x < y ? 1 : 2] = 0 }.Count, _f;",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_a" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_b" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_c" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_d" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_e" && s.ReturnType == "int" && s.Visibility == "private");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "_f" && s.ReturnType == "int" && s.Visibility == "private");
    }

    [Fact]
    public void Extract_CSharp_DetectsMultiLineFieldWithObjectInitializer()
    {
        // Multi-line plain fields whose initializer uses an object or collection
        // initializer (`= new() { ... };`, `= new Dictionary<...> { ... };`) must
        // still complete at the real top-level `;`. The combined match line opens a
        // brace inside the initializer, so the field path cannot assume every `{` is
        // a property body — it must keep merging until the top-level semicolon closes
        // the declaration. Closes #298 follow-up (third codex adversarial review).
        // 複数行の通常フィールドで、`= new() { ... };` や `= new Dictionary<...> { ... };`
        // のようなオブジェクト/コレクション初期化子を使う宣言も、実際のトップレベル `;` で
        // 完了しなければならない。結合済みマッチ行には初期化子の `{` が入るため、field 経路は
        // あらゆる `{` を property 本体とみなしてはならず、宣言終端のトップレベル `;` まで
        // 結合を続ける必要がある。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "using System.Collections.Generic;",
            "namespace Demo;",
            "public class Containers",
            "{",
            "    private Dictionary<string, int>",
            "        _map = new()",
            "        {",
            "            [\"a\"] = 1",
            "        };",
            "    private List<int>",
            "        _list = new() {",
            "            1, 2, 3",
            "        };",
            "    private Dictionary<string, int>",
            "        _typed = new Dictionary<string, int>",
            "        {",
            "            [\"b\"] = 2",
            "        };",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_map"
            && s.Visibility == "private"
            && s.ReturnType == "Dictionary<string,int>");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_list"
            && s.Visibility == "private"
            && s.ReturnType == "List<int>");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_typed"
            && s.Visibility == "private"
            && s.ReturnType == "Dictionary<string,int>");
    }

    [Fact]
    public void Extract_CSharp_DetectsWrappedRawStringFieldBeyondLookaheadBudget()
    {
        // issue #447 follow-up: once the declaration is confirmed at `Script = """`,
        // the extractor must continue linearly to the real `""";` terminator instead of
        // dropping the symbol at the 16-line confirmation cap.
        // issue #447 follow-up: `Script = """` で宣言確定後は、16 行の確認上限で
        // 打ち切らず、実際の `""";` 終端まで線形に継続してシンボルを保持する。
        var content = string.Join(
            "\n",
            [
                "namespace Demo;",
                "public class Fixtures",
                "{",
                "    private static readonly string",
                "        Script = \"\"\"",
                .. Enumerable.Range(1, 18).Select(i => $"line{i:00}"),
                "\"\"\";",
                "}"
            ]);
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var script = Assert.Single(symbols.Where(s => s.Kind == "function"
            && s.Name == "Script"
            && s.Visibility == "private"
            && s.ReturnType == "string"));
        Assert.Contains("Script = \"\"\"", script.Signature);
        Assert.Contains("\"\"\";", script.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsSameLineConstRawStringFieldBeyondLookaheadBudget()
    {
        // Same-line `const string Name = """` must also enter the confirmed continuation
        // path immediately; otherwise the long raw-string body falls past the bounded
        // lookahead window and the stored signature truncates at the opener line.
        // 同一行の `const string Name = """` も確認済み継続へ即時に入らないと、
        // 長い raw string 本体が bounded な先読み窓の外へ落ち、保存 signature が
        // opener 行で途切れてしまう。
        var content = string.Join(
            "\n",
            [
                "namespace Demo;",
                "public class Fixtures",
                "{",
                "    private const string ConstScript = \"\"\"",
                .. Enumerable.Range(1, 18).Select(i => $"line{i:00}"),
                "\"\"\";",
                "}"
            ]);
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var constScript = Assert.Single(symbols.Where(s => s.Kind == "function"
            && s.Name == "ConstScript"
            && s.Visibility == "private"
            && s.ReturnType == "string"));
        Assert.Contains("ConstScript = \"\"\"", constScript.Signature);
        Assert.Contains("\"\"\";", constScript.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsLongObjectInitializerBeyondLookaheadBudget()
    {
        // issue #447 follow-up: long object/collection initializers must keep consuming
        // lines after the declaration is confirmed at `_map = new()`, rather than falling
        // back to the raw header once the bounded confirmation phase expires.
        // issue #447 follow-up: `_map = new()` で宣言確定後は、長い object/collection
        // initializer でも bounded な確認フェーズ満了で raw header に戻らず、そのまま
        // 継続して終端 `;` まで追跡しなければならない。
        var initializerLines = Enumerable.Range(1, 18)
            .Select(i => $"            [\"k{i:00}\"] = {i}")
            .ToArray();
        var content = string.Join(
            "\n",
            [
                "using System.Collections.Generic;",
                "namespace Demo;",
                "public class Containers",
                "{",
                "    private Dictionary<string, int>",
                "        _map = new()",
                "        {",
                .. initializerLines,
                "        };",
                "}"
            ]);
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var map = Assert.Single(symbols.Where(s => s.Kind == "property"
            && s.Name == "_map"
            && s.Visibility == "private"
            && s.ReturnType == "Dictionary<string,int>"));
        Assert.Contains("_map = new()", map.Signature);
        Assert.Contains("};", map.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineNestedClassAttachesFieldToInnerType()
    {
        // `public class Outer { public class Inner { public int X; } }` must capture
        // both Outer and Inner, and the field X must attach to Inner (not Outer).
        // The container resolution uses a same-line `Signature.Contains` check, so the
        // plain-field signature clamp from #400 is required for Inner to correctly
        // "contain" X's signature. Closes #400.
        // `public class Outer { public class Inner { public int X; } }` では Outer と
        // Inner の双方を取得し、X は Outer ではなく Inner に紐づけること。同一行の
        // コンテナ解決は `Signature.Contains` を使うため、#400 で追加した plain-field
        // signature のクランプが無いと Inner が X の signature を「含む」と判定されず
        // X が Outer に吸収されてしまう。Closes #400.
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "",
            "public class Outer { public class Inner { public int X; } }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Outer");
        var inner = Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Inner"));
        Assert.Equal("class", inner.ContainerKind);
        Assert.Equal("Outer", inner.ContainerName);
        var x = Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "X"));
        Assert.Equal("class", x.ContainerKind);
        Assert.Equal("Inner", x.ContainerName);
        Assert.Equal("public int X;", x.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineCompactEnumMembersDoNotLeakAsFields()
    {
        // `public enum Mode { [Obsolete] A = (int)B, ... }` must produce enum-member
        // symbols only and must NOT emit phantom `property` symbols for `[Obsolete] A =`.
        // The column-aware scope gate distinguishes class-like bodies (where fields are
        // legal) from enum bodies (where members are not fields), so the plain-field
        // regex is rejected inside enum bodies. Closes #400.
        // `public enum Mode { [Obsolete] A = (int)B, ... }` は enum member のみを生成し、
        // `[Obsolete] A =` を property として拾ってはならない。列意識スコープゲートが
        // class-like body（field が正当）と enum body（member は field ではない）を
        // 区別するため、enum body 内では plain-field regex がリジェクトされる。
        // Closes #400.
        var content = string.Join(
            "\n",
            "using System;",
            "",
            "public enum Mode { [Obsolete] A = 1, B = 2, C = 3 }");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "enum" && s.Name == "Mode");
        foreach (var name in new[] { "A", "B", "C" })
        {
            Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == name);
        }
    }

    [Fact]
    public void Extract_CSharp_SameLineFieldWithInitializerFollowedByAnotherField()
    {
        // `public class Holder { public int A = 1; public int B; }` must capture
        // both `A` and `B`. The prior fix broke out of the same-line scan as soon
        // as the plain-field pattern matched a `=`-terminated declaration, which
        // dropped any following same-line field statement. Closes #400.
        // `public class Holder { public int A = 1; public int B; }` では `A` と
        // `B` の両方が抽出される必要がある。旧修正は `=` 終端フィールドを拾った
        // 時点で同一行スキャンを break してしまい、直後の同一行フィールドを
        // 取り落としていた。Closes #400.
        var content = "public class Holder { public int A = 1; public int B; }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Holder");
        var a = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "A");
        var b = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "B");
        Assert.Equal("public int A = 1;", a.Signature);
        Assert.Equal("public int B;", b.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineDeclaratorListFollowedByAnotherField()
    {
        // `public class Holder { public int A, B; public int C; }` must capture
        // three property rows (A, B via declarator list, plus C from the second
        // same-line field statement). The prior fix broke out of the same-line
        // scan after declarator expansion and silently dropped `C`. Closes #400.
        // `public class Holder { public int A, B; public int C; }` では declarator
        // list 展開で A と B、続く同一行 field 文から C、合わせて 3 シンボルを
        // 抽出する必要がある。旧修正は declarator 展開後に同一行スキャンを break
        // して C を取り落としていた。Closes #400.
        var content = "public class Holder { public int A, B; public int C; }";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Holder");
        Assert.Single(symbols, s => s.Kind == "property" && s.Name == "A");
        Assert.Single(symbols, s => s.Kind == "property" && s.Name == "B");
        var c = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "C");
        Assert.Equal("public int C;", c.Signature);
    }

    [Fact]
    public void Extract_CSharp_PlainFieldWithInitializerKeepsFullSignature()
    {
        // `private int _x = 42;` must store the full `private int _x = 42;` as
        // signature, not the `=`-truncated `private int _x =`. An earlier version
        // clamped the signature to `match.Length`, which cut off at `=`. The fix
        // clamps to the statement's `;` (or an unbalanced `}` if one is hit
        // first). Closes #400.
        // `private int _x = 42;` の signature は `=` で切り詰めず完全な
        // `private int _x = 42;` を保存する必要がある。旧実装は signature を
        // `match.Length` で clamp して `=` の手前で切れていた。修正では文終端の
        // `;` まで（あるいは先に出現する深さ 0 の `}` の手前まで）で clamp する。
        // Closes #400.
        var content = string.Join(
            "\n",
            "public class Holder",
            "{",
            "    private int _x = 42;",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var field = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "_x");
        Assert.Equal("private int _x = 42;", field.Signature);
    }

    [Fact]
    public void Extract_CSharp_SameLineGenericClassHeaderStillCapturesInnerField()
    {
        // `public class C<T1, T2>{int X;}` must still capture field X even though
        // CollapseCSharpGenericTypeWhitespace removes the space inside `<T1, T2>`
        // when building the match line. Before the fix, the collapsed-space
        // `absoluteStartColumn` was handed directly to the raw-column
        // CSharpTypeBodyScope lookup, so the scope-gate fired too early and
        // the field was dropped. Closes #400.
        // `public class C<T1, T2>{int X;}` では CollapseCSharpGenericTypeWhitespace が
        // `<T1, T2>` の内部空白を詰めるため、collapsed 列 `absoluteStartColumn` を
        // そのまま raw 列ベースの CSharpTypeBodyScope に渡すと scope gate が誤発火し
        // X が落ちていた。Closes #400.
        var content = "public class C<T1, T2>{int X;}\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var field = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "X");
        Assert.Equal("int X;", field.Signature);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C");
    }

    [Fact]
    public void Extract_CSharp_SameLineGenericFieldSignatureKeepsTerminator()
    {
        // `public Dictionary<string, int> Map = new(); public int B;` on one line
        // must produce Map with its trailing `;` and B without a leading `;`.
        // Before the fix, collapsed-space endpoints from
        // FindCSharpPlainFieldStatementEnd were used to slice the raw line, so
        // the generic-whitespace compression shifted the cut: Map lost its `;`
        // and B inherited the `;` as a leading character. Closes #400.
        // `public Dictionary<string, int> Map = new(); public int B;` を 1 行に
        // 書いた場合、Map の signature は末尾 `;` を保ち、B の signature は
        // 先頭 `;` を持たないこと。修正前は FindCSharpPlainFieldStatementEnd が
        // 返す collapsed 列で raw 行を slice していたため、generic 空白の圧縮
        // 分だけ切断位置がずれていた。Closes #400.
        var content = "public class C { public Dictionary<string, int> Map = new(); public int B; }\n";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var map = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "Map");
        Assert.Equal("public Dictionary<string, int> Map = new();", map.Signature);

        var b = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "B");
        Assert.Equal("public int B;", b.Signature);
    }

    [Fact]
    public void Extract_CSharp_MultiLineFieldFollowedBySameLineFieldDoesNotCrash()
    {
        // A multi-line field header whose continuation line also carries a
        // second same-line field — `public Dictionary<string, int>\n    Map = new(); public int B;`
        // — must extract both `Map` and `B` without throwing. In the prior fix,
        // the plain-field same-line scan continued past the first `;` using
        // absoluteStartColumn from the merged multi-line candidate, which sits
        // in the merged-string column domain and is not valid inside lines[i].
        // The follow-up regex hit then reached BuildCSharpMultilineSignature
        // with startColumn > lines[i].Length and crashed indexing with
        // `startIndex cannot be larger than length of string`. Closes #400.
        // 複数行 field ヘッダの continuation 行に 2 個目の同一行 field が続く
        // `public Dictionary<string, int>\n    Map = new(); public int B;` で、
        // 例外なく `Map` と `B` 両方が抽出できる必要がある。旧実装では plain-field の
        // 同一行継続 scan が、マージ済み候補の absoluteStartColumn を使って
        // statementEnd 以降へ進み、2 個目の regex マッチで
        // BuildCSharpMultilineSignature が lines[startLineIndex][startColumn..] で
        // 範囲外アクセスし `startIndex cannot be larger than length of string` で
        // indexing が落ちていた。Closes #400.
        var content = string.Join(
            "\n",
            "public class C",
            "{",
            "    public Dictionary<string, int>",
            "        Map = new(); public int B;",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "C");
        Assert.Single(symbols, s => s.Kind == "property" && s.Name == "Map");
        var b = Assert.Single(symbols, s => s.Kind == "property" && s.Name == "B");
        Assert.Equal("public int B;", b.Signature);
    }

    [Fact]
    public void Extract_CSharp_DetectsFunctionPointerField()
    {
        // Function-pointer field (`delegate*<int, void> Callback;`) must be captured.
        // The plain-field negative lookahead rejects `delegate` to stay away from
        // delegate-type declarations, but `delegate*` is a type form and the lookahead
        // uses `delegate\b(?!\*)` so it does not reject the function-pointer field.
        // Closes #298 follow-up (codex adversarial review).
        // function-pointer field（`delegate*<int, void> Callback;`）も抽出できること。
        // field pattern の negative lookahead は delegate 型宣言を除外するために
        // `delegate` を並べているが、`delegate*` は型なので `delegate\b(?!\*)` で
        // function-pointer field を排除しない。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "public unsafe class Bridge",
            "{",
            "    public delegate*<int, void> Callback;",
            "    private delegate* unmanaged[Cdecl]<int, int> _op;",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "Callback"
            && s.Visibility == "public");
        Assert.Contains(symbols, s => s.Kind == "property"
            && s.Name == "_op"
            && s.Visibility == "private");
    }

    [Fact]
    public void Extract_CSharp_DelegateTypeDeclarationIsNotField()
    {
        // `public delegate int Foo();` still declares a delegate type, not a field, so
        // the plain-field lookahead `delegate\b(?!\*)` must continue to reject it.
        // Closes #298 follow-up (codex adversarial review).
        // `public delegate int Foo();` は相変わらず delegate 型宣言であり field では
        // ないため、plain-field pattern の lookahead `delegate\b(?!\*)` がこれを
        // 引き続き排除することを確認する。Closes #298 follow-up。
        var content = string.Join(
            "\n",
            "namespace Demo;",
            "public class Host",
            "{",
            "    public delegate int Callback(int x);",
            "}");
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Accept either a dedicated delegate / function classification, but never
        // classify the statement as a plain `property` field.
        // delegate / function としての抽出は許容するが、`property` field にだけは
        // 分類しないことを確認する。
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Callback");
    }

    [Fact]
    public void Extract_CSharp_DetectsRegionHeadings()
    {
        var symbols = SymbolExtractor.Extract(1, "csharp", """
            public class Service
            {
                #region Validation
                public void Check() { }
                #endregion
            }
            """);

        Assert.Contains(symbols, s => s.Kind == "heading" && s.Name == "Validation");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchCallSitesAsDefinitions()
    {
        // Issue #40: await/return/throw calls should not be extracted as method definitions
        // Issue #40: await/return/throw の呼び出しがメソッド定義として抽出されないこと
        var content = "public class Service\n{\n    public async Task InitAsync()\n    {\n        await TryNotifyAsync(true);\n        return GetResult();\n        throw CreateException(\"err\");\n        var x = ComputeValue(42);\n        yield return GenerateItem();\n    }\n\n    private async Task TryNotifyAsync(bool force)\n    {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Real definitions should be extracted / 実際の定義は抽出されるべき
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Service");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "InitAsync");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "TryNotifyAsync");
        // Call sites should NOT be extracted as definitions / 呼び出し箇所は定義として抽出されないこと
        Assert.DoesNotContain(symbols, s => s.Name == "TryNotifyAsync" && s.Line == 5);
        Assert.DoesNotContain(symbols, s => s.Name == "GetResult");
        Assert.DoesNotContain(symbols, s => s.Name == "CreateException");
        Assert.DoesNotContain(symbols, s => s.Name == "ComputeValue");
        Assert.DoesNotContain(symbols, s => s.Name == "GenerateItem");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchQualifiedCallSitesAsDefinitions()
    {
        // Qualified call sites (obj.Method()) should not match the explicit interface pattern
        // 修飾付き呼び出し (obj.Method()) は明示的インターフェース実装パターンにマッチしないこと
        var content = "public class Service\n{\n    public async Task Run()\n    {\n        return service.GetResult();\n        await client.SendAsync();\n        throw factory.CreateException(\"err\");\n    }\n\n    void IDisposable.Dispose()\n    {\n    }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Explicit interface impl should be extracted / 明示的インターフェース実装は抽出されるべき
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Dispose" && s.ReturnType == "void");
        // Qualified call sites should NOT be extracted / 修飾付き呼び出しは抽出されないこと
        Assert.DoesNotContain(symbols, s => s.Name == "GetResult");
        Assert.DoesNotContain(symbols, s => s.Name == "SendAsync");
        Assert.DoesNotContain(symbols, s => s.Name == "CreateException");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchNewExpressionStatementsAsExplicitInterfaceDefinitions()
    {
        // Issue #362: `new System.Text.StringBuilder().Append(...)` などの式文が、
        // 正規表現が最初の `(` で止まるために returnType=`new` / interface=手前の修飾チェーン
        // （namespace `System.Text` / 外側型 `Outer` / `MyApp.Outer` のような両者の混在など、
        // ドット連鎖そのもの。この位置では namespace と外側型を区別しない）/
        // name=構築されている型（`StringBuilder` / `HttpClient` / `Inner`）として
        // 明示的インターフェースメソッド定義に化けないこと。ブレース初期化子形
        // (`new Outer.Inner { A = 1 }.Consume();`) と `_ = new ...` 形も同じフィクスチャで
        // 固定し、brace-initializer 側では `Outer.Inner` / `Inner.Consume` のコンテナ関係
        // と `Consume` が全体 1 本だけ（= brace-init から phantom `function Consume` が
        // 増えない、別コンテナ配下や container 未設定の phantom も出ない）ことまで
        // ピン留めする。
        // Issue #362: expression statements like `new System.Text.StringBuilder().Append(...)`
        // must not masquerade as explicit interface method definitions. The phantom name would
        // be the identifier right before the first `(` — the type being constructed
        // (`StringBuilder` / `HttpClient` / `Inner`), because the explicit-interface regex
        // stops at the first `(` and consumes the preceding dot-chain as the would-be
        // interface qualifier. That qualifier may be a namespace prefix (`System.Text` in
        // `new System.Text.StringBuilder()`), an enclosing-type chain (`Outer` in
        // `new Outer.Inner()` where `Outer` is an outer class, not a namespace), or a
        // mix of both (e.g. `new MyApp.Outer.Inner()` where `MyApp` is a namespace and
        // `Outer` is an enclosing type) — the regex does not distinguish which segments are
        // namespaces and which are enclosing types at this position. Brace-initializer forms
        // (`new Outer.Inner { A = 1 }.Consume();`) and discard forms (`_ = new ...`) are
        // also pinned here; the brace-initializer case additionally pins the real
        // `Outer` → `Inner` → `Consume` container chain and that exactly one `Consume`
        // row is emitted (no phantom `function Consume` leaking out of the brace-init site).
        var content = "public class Svc\n{\n    public int Real() => 42;\n\n    public void ChainedNew()\n    {\n        new System.Text.StringBuilder().Append(\"a\").Append(\"b\").ToString();\n    }\n\n    public void DiscardNew()\n    {\n        _ = new System.Text.RegularExpressions.Regex(\"pattern\");\n    }\n\n    public void UseNew()\n    {\n        new System.Net.Http.HttpClient().Dispose();\n    }\n\n    public void BraceInitNew()\n    {\n        new Outer.Inner { A = 1 }.Consume();\n    }\n}\n\npublic class Outer\n{\n    public class Inner { public int A { get; set; } public void Consume() { } }\n}\n\npublic class Consumer : System.IDisposable\n{\n    void System.IDisposable.Dispose() { }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        // Real definitions should be extracted / 実際の定義は抽出されるべき
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Svc");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Real");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ChainedNew");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "DiscardNew");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "UseNew");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "BraceInitNew");
        // Nested Outer.Inner container chain must be preserved, and exactly one Consume symbol
        // must exist in total — emitted under Inner. Two guards here:
        //   (a) the total `Consume` count across ALL kinds / containers must be 1, so a
        //       phantom `function Consume` emitted under `Svc` (or with no container at all)
        //       from `new Outer.Inner { A = 1 }.Consume();` cannot sneak past by living
        //       outside `ContainerName == "Inner"`; and
        //   (b) that single `Consume` must be a `function` under `Inner` under `Outer`, so a
        //       broken container chain also fails.
        // ネストした Outer.Inner のコンテナ関係を固定。`Consume` は全体 1 本のみ（別コンテナ
        // 配下や container 未設定の phantom も弾く）かつ、その 1 本は `Inner` 配下に属する
        // `function` である、という二段のガードで brace-init phantom を検出する。
        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Outer"));
        Assert.Single(symbols.Where(s => s.Name == "Inner"));
        Assert.Single(symbols.Where(s => s.Kind == "class" && s.Name == "Inner" && s.ContainerKind == "class" && s.ContainerName == "Outer"));
        Assert.Single(symbols.Where(s => s.Name == "Consume"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "Consume" && s.ContainerKind == "class" && s.ContainerName == "Inner"));
        // Explicit interface impl on Consumer class must still be captured (regression guard)
        // Consumer クラスの明示的インターフェース実装は引き続き抽出されること（回帰防止）
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Dispose" && s.ReturnType == "void");
        // Phantom function rows from new-expression statements must NOT be produced,
        // whether the chain ends in parentheses (`new T().M(...)`) or a brace-initializer
        // (`new T { ... }.M(...)`). 構築される型名 (`StringBuilder` / `HttpClient` / `Regex`
        // / `Inner`) が function 行として出ないこと。
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "StringBuilder");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Regex");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "HttpClient");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Inner" && s.ReturnType == "new");
        // Kind-agnostic guard specifically against this #362 phantom shape: the `new` keyword
        // itself must not sneak in under ANY kind inside this fixture. This is a targeted
        // guard, not a general "the word `new` can never be a symbol name anywhere" claim.
        // Issue #362 の phantom が将来別 kind に分類し直されても取りこぼさないための
        // kind 非依存ガード。ここでの意味は「このフィクスチャの範囲内で `new` が名前に
        // 出てこない」ことに限定しており、一般命題として主張するものではない。
        Assert.DoesNotContain(symbols, s => s.Name == "new");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchQualifiedNewExpressionsAsExplicitInterfaceDefinitions()
    {
        // Issue #362: qualified constructor expressions (`new Namespace.Type()`) must not be
        // misread as `returnType + interface.member` by the explicit-interface regex.
        // Issue #362: 修飾付きコンストラクタ式 (`new Namespace.Type()`) を、明示的インターフェース
        // 実装 regex の `returnType + interface.member` と誤認しないこと。
        var content = """
            public class Service : IDisposable
            {
                public void Build()
                {
                    new System.Text.StringBuilder().Append("a").Append("b").ToString();
                    _ = new System.Text.RegularExpressions.Regex("pattern");
                    new System.Net.Http.HttpClient().Dispose();
                }

                void IDisposable.Dispose()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Build" && s.ReturnType == "void");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Dispose" && s.ReturnType == "void");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "StringBuilder");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "Regex");
        Assert.DoesNotContain(symbols, s => s.Kind == "function" && s.Name == "HttpClient");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchNamedArgumentFrameworkCallsAsDefinitions()
    {
        // Named-argument labels preceding qualified framework calls must not look like explicit interface impls.
        // 修飾付き framework call の前にある named-argument label を明示的インターフェース実装と誤認しないこと。
        var content = """
            public class PlatformState
            {
                public PlatformState(bool isWindows, bool isMacCatalyst)
                {
                }

                public static PlatformState Detect() =>
                    new(
                        isWindows: OperatingSystem.IsWindows(),
                        isMacCatalyst: OperatingSystem.IsMacCatalyst());
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "PlatformState");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Detect");
        Assert.DoesNotContain(symbols, s => s.Name == "IsWindows");
        Assert.DoesNotContain(symbols, s => s.Name == "IsMacCatalyst");
    }

    [Fact]
    public void Extract_CSharp_DetectsAliasQualifiedExplicitInterfaceImplementations()
    {
        // Alias-qualified return types should still match explicit interface implementations.
        // alias-qualified な戻り値型でも明示的インターフェース実装として抽出されること。
        var content = """
            public interface IFoo
            {
                string Name();
                object Create();
            }

            public class Impl : IFoo
            {
                global::System.String IFoo.Name() => "x";
                Alias::Type IFoo.Create() => default;
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Name" && s.ReturnType == "global::System.String");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Create" && s.ReturnType == "Alias::Type");
    }

    [Fact]
    public void Extract_CSharp_DoesNotMatchTernaryContinuationCallsAsDefinitions()
    {
        var content = """
            public class Dispatcher
            {
                private string Select(bool isUpdate)
                    => isUpdate
                        ? RunUpdateMode()
                        : RunFullScan();

                private string RunUpdateMode() => "update";
                private string RunFullScan() => "full";

                public int? NullableCount() => null;

                public Dispatcher(bool isUpdate)
                    : base()
                {
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Select");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RunUpdateMode" && s.Line == 8);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "RunFullScan" && s.Line == 9);
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "NullableCount" && s.ReturnType == "int?");
        Assert.DoesNotContain(symbols, s => s.Name == "RunUpdateMode" && s.Line == 5);
        Assert.DoesNotContain(symbols, s => s.Name == "RunFullScan" && s.Line == 6);
        Assert.DoesNotContain(symbols, s => s.Name == "base");
    }

    [Fact]
    public void Extract_CSharp_DetectsNewModifierMethods()
    {
        // C# `new` modifier for member hiding should still be extracted as definitions
        // C# のメンバー隠蔽用 `new` 修飾子は定義として抽出されるべき
        var content = "public class Derived : Base\n{\n    new void Reset() { }\n    new int Compare(object obj) { return 0; }\n    public new string ToString() { return \"\"; }\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Reset");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Compare");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "ToString");
    }

    [Fact]
    public void EstimateComplexity_StraightLineFunction_ReturnsOne()
    {
        var body = "var x = 1;\nreturn x + 2;";
        Assert.Equal(1, SymbolExtractor.EstimateComplexity(body));
    }

    [Fact]
    public void EstimateComplexity_BranchingFunction_CountsBranches()
    {
        var body = "if (x > 0)\n    return x;\nelse if (x < 0)\n    return -x;\nfor (int i = 0; i < n; i++)\n    sum += i;\nwhile (retry)\n    attempt();\nvar result = a ?? b;\nvar flag = x && y || z;";
        // 1 (baseline) + if + else if + for + while + ?? + && + || = 8
        Assert.Equal(8, SymbolExtractor.EstimateComplexity(body));
    }

    [Fact]
    public void EstimateComplexity_EmptyBody_ReturnsOne()
    {
        Assert.Equal(1, SymbolExtractor.EstimateComplexity(""));
        Assert.Equal(1, SymbolExtractor.EstimateComplexity("   "));
    }

    [Fact]
    public void Extract_CSharp_WrappedStaticConstructor_EmitsOnceAtNameLine()
    {
        // Regression for issue #348: when `static` sits on its own physical line above
        // the constructor name, the extractor must still emit the ctor exactly once,
        // anchored at the name line, with a signature that reflects the full declaration.
        // issue #348 の回帰: `static` が constructor 名の物理行の一つ上に単独で置かれた
        // 場合でも、名前行を起点に重複なく 1 件だけ emit し、signature には宣言全体が
        // 含まれる必要がある。
        const string content = """
            namespace WrappedCtor;

            public class A
            {
                static
                A() { _x = 1; }

                private static int _x;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "A").ToList();
        Assert.Single(ctors);
        Assert.Equal(6, ctors[0].Line);
        Assert.Contains("static A()", ctors[0].Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedInstanceConstructor_EmitsOnceAtNameLine()
    {
        // issue #348 の回帰: `public` 等の可視性モディファイアが単独行に置かれた
        // non-static constructor も同じく名前行で 1 件だけ emit する。
        const string content = """
            namespace WrappedCtor;

            public class B
            {
                public
                B() { _y = 1; }

                private int _y;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "B").ToList();
        Assert.Single(ctors);
        Assert.Equal(6, ctors[0].Line);
        Assert.Contains("public B()", ctors[0].Signature);
    }

    [Fact]
    public void Extract_CSharp_WrappedAllmanStaticConstructor_CapturesBody()
    {
        // issue #348 の回帰: Allman スタイル（`static` 単独行 → 名前行 → `{` 単独行）の
        // static constructor も 1 件だけ emit し、本体の閉じ brace まで range を追跡する。
        const string content = """
            namespace WrappedCtor;

            public class F
            {
                static
                F()
                {
                    _u = 1;
                }

                private static int _u;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "F").ToList();
        Assert.Single(ctors);
        Assert.Equal(6, ctors[0].Line);
        Assert.Contains("static F()", ctors[0].Signature);
        Assert.True(ctors[0].BodyEndLine >= 9, $"expected body to reach closing brace on line 9 or later, got {ctors[0].BodyEndLine}");
    }

    [Fact]
    public void Extract_CSharp_AttributedWrappedStaticConstructor_EmitsOnceAtNameLine()
    {
        // issue #348 の回帰: 属性行が挟まった wrapped static ctor でも、attribute 行は
        // モディファイア連結に混入せず、名前行 1 件だけに集約される。
        const string content = """
            namespace WrappedCtor;

            public class D
            {
                [System.Obsolete]
                static
                D() { _z = 1; }

                private static int _z;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "D").ToList();
        Assert.Single(ctors);
        Assert.Equal(7, ctors[0].Line);
        Assert.Contains("static D()", ctors[0].Signature);
    }

    [Fact]
    public void Extract_CSharp_MultiModifierWrappedConstructor_EmitsOnceAtNameLine()
    {
        // issue #348 の回帰: 複数のモディファイアが別々の物理行に折り返された wrapped ctor
        // （例: `public\nstatic\nE()`）でも、名前行 1 件だけ emit し、signature には両方の
        // モディファイアを保持する。単純 prefix 合成では constructor regex も static ctor
        // regex も受け付けない合成行になるため、static / visibility の variant を試す候補
        // 列挙ロジックが無いと無言で落ちていた。
        const string content = """
            namespace WrappedCtor;

            public class E
            {
                public
                static
                E() { _w = 1; }

                private static int _w;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "E").ToList();
        Assert.Single(ctors);
        Assert.Equal(7, ctors[0].Line);
        Assert.Contains("public static E()", ctors[0].Signature);
    }

    [Fact]
    public void Extract_CSharp_CompositeVisibilityWrappedConstructor_EmitsOnceAtNameLine()
    {
        // issue #348 の回帰: 複合 visibility (`protected internal` / `private protected`) が
        // 2 行に分割されて折り返された wrapped ctor でも、名前行 1 件だけ emit し、signature
        // には複合 visibility を保持する。candidate 列挙の先頭に full prefix (`protected internal`)
        // を yield するため、constructor regex の `protected\s+internal` 選択肢でそのまま一致する。
        const string content = """
            namespace WrappedCtor;

            public class F
            {
                protected
                internal
                F() { _v = 1; }

                private static int _v;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "F").ToList();
        Assert.Single(ctors);
        Assert.Equal(7, ctors[0].Line);
        Assert.Contains("protected internal F()", ctors[0].Signature);
        Assert.Equal("protected internal", ctors[0].Visibility);
    }

    [Fact]
    public void Extract_CSharp_VisibilityStaticExternWrappedConstructor_EmitsOnceAtNameLine()
    {
        // issue #348 の回帰: visibility + static + extern の 3 modifier が全て別行に折り返された
        // wrapped ctor でも、名前行 1 件だけ emit し、signature には 3 modifier 全てを保持する。
        // full prefix (`public static extern`) は constructor regex の visibility スロット後に
        // static を置けないため単体では通らず、static variant も `()` 要求で引数付きを弾くので
        // 通らないが、visibility-only variant (`public`) が constructor regex にヒットし、signature
        // は full prefix 側から補完される。
        const string content = """
            namespace WrappedCtor;

            public class G
            {
                public
                static
                extern
                G(string s);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ctors = symbols.Where(s => s.Kind == "function" && s.Name == "G").ToList();
        Assert.Single(ctors);
        Assert.Equal(8, ctors[0].Line);
        Assert.Contains("public static extern G(string s)", ctors[0].Signature);
    }

    [Fact]
    public void Extract_Csharp_LeadingBom_IndexesFirstLineImport()
    {
        // BOM-prefixed C# source: `using System;` on line 1 must still be captured.
        // Closes #183.
        // BOM 付き C# ソース: 1 行目の `using System;` も取りこぼさない。Closes #183.
        const string content = "\uFEFFusing System;\n\nnamespace BomTest;\n\npublic class WithBom {\n    public void Run() { }\n}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var bomLess = SymbolExtractor.Extract(2, "csharp", content[1..]);
        Assert.Equal(bomLess.Count, symbols.Count);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "BomTest");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "WithBom");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Run");
    }

    [Fact]
    public void Extract_Csharp_MidFileBom_IndexesAffectedLine()
    {
        // Mid-file BOM (e.g. from file concatenation): the `\uFEFFnamespace MidBom;` line
        // must still yield a namespace symbol, on its real line number. Closes #183.
        // ファイル連結などで挟まった mid-file BOM: `\uFEFFnamespace MidBom;` 行も
        // 実際の行番号で namespace として拾う。Closes #183.
        const string content = "using System;\n\n\uFEFFnamespace MidBom;\n\npublic class X { }\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var ns = Assert.Single(symbols.Where(s => s.Kind == "namespace"));
        Assert.Equal("MidBom", ns.Name);
        Assert.Equal(3, ns.Line);
        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "X");
    }

    [Fact]
    public void Extract_Csharp_CrlfLeadingBom_IndexesFirstLineImport()
    {
        // Direct-call input with CRLF line endings AND a leading BOM: the CRLF → LF
        // normalization must run before StripLineLeadingInvisibles so the line-leading
        // cleanup still recognizes mid-file BOMs (helper treats `\n` as the sole line
        // separator). Closes #183.
        // CRLF 改行 + 先頭 BOM の direct call: StripLineLeadingInvisibles は `\n` を唯一の
        // 行区切りとして扱うので、CRLF → LF 正規化を helper より先に通さないと
        // mid-file 行頭 BOM を剥がし損ねる。Closes #183.
        const string content = "\uFEFFusing System;\r\n\r\n\uFEFFnamespace CrlfBom;\r\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "CrlfBom" && s.Line == 3);
    }

    [Fact]
    public void Extract_Csharp_BareCrLeadingBom_IndexesFirstLineImport()
    {
        // Bare-`\r` direct-call input with a leading BOM: the in-extractor
        // normalization must also rewrite `\r` → `\n`, otherwise a file
        // authored under classic-Mac-style line endings would keep mid-file
        // line-leading BOMs invisible to `StripLineLeadingBom` (which treats
        // `\n` as the sole separator). Closes #183.
        // bare `\r` 改行 + 先頭 BOM の direct call: `\r` → `\n` 正規化も必要で、
        // classic-Mac 改行のファイルに対して mid-file 行頭 BOM を剥がし損ねる
        // のを防ぐ。Closes #183.
        const string content = "\uFEFFusing System;\r\r\uFEFFnamespace BareCrBom;\r";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "BareCrBom" && s.Line == 3);
    }

    [Fact]
    public void Extract_Csharp_MixedLineEndingsLeadingBom_IndexesDeclarationsOnAllLines()
    {
        // Mixed line endings (`\r\n`, bare `\r`, bare `\n`) interleaved with
        // leading + mid-file line-leading BOMs: the in-extractor normalization
        // must reduce the whole content to a `\n`-only stream before the
        // helper runs, otherwise mid-file BOMs following `\r` or `\r\n\r`
        // boundaries would survive and `^\s*`-anchored patterns would miss
        // the next declaration. Closes #183.
        // 混在改行（`\r\n` / bare `\r` / bare `\n`）+ 先頭/中間行頭 BOM の direct call:
        // 正規化を helper より先に通し、`\r\n\r` や `\r` 直後の mid-file 行頭 BOM も
        // 剥がせるようにする。Closes #183.
        const string content = "\uFEFFusing System;\r\n\r\uFEFFnamespace MixedEnds;\n\uFEFFpublic class X { }\r\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System" && s.Line == 1);
        Assert.Contains(symbols, s => s.Kind == "namespace" && s.Name == "MixedEnds" && s.Line == 3);
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "X" && s.Line == 4);
    }

    [Fact]
    public void Extract_CSharp_InstallScriptFixture_CompletesWithinPracticalBudget()
    {
        // issue #447 regression: the real InstallScriptTests fixture previously drove C#
        // symbol extraction into super-linear CPU time. Use the repository's current copy so
        // the regression test keeps exercising the same realistic raw-string + heredoc shape
        // that broke self-indexing. This is a coarse runaway guard, not a tight benchmark, so
        // keep the budget generous enough for slower or noisy CI hosts.
        // issue #447 回帰: 実ファイル InstallScriptTests.cs が C# シンボル抽出を super-linear に
        // 悪化させていた。自己ホストを壊した raw-string + heredoc の実形を継続的に踏むため、
        // リポジトリ内の現行ファイルをそのまま使う。これは厳密な benchmark ではなく runaway
        // guard なので、時間予算は遅い / 混雑した CI でも耐えるよう広めに取る。
        var path = Path.Combine(GetRepositoryRoot(), "tests", "CodeIndex.Tests", "InstallScriptTests.cs");
        var content = File.ReadAllText(path);

        var stopwatch = Stopwatch.StartNew();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        stopwatch.Stop();

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "InstallScriptTests");
        Assert.Contains(symbols, s => s.Kind == "test.method" && s.Name == "Main_WithoutExplicitVersion_DoesNotShortCircuitBrokenZeroVersionInstall");
        var runawayBudget = TimeSpan.FromSeconds(60);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"InstallScriptTests.cs extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

    [Fact]
    public void Extract_CSharp_ReferenceExtractorFixture_CompletesWithinPracticalBudget()
    {
        // issue #2710/#2711/#2717 regression: full self-indexing could spend minutes
        // repeatedly rebuilding the same multi-line C# member candidate while scanning large
        // extractor sources. Keep this as a broad runaway guard for the realistic file that
        // reproduced the stall on origin/main.
        var path = Path.Combine(GetRepositoryRoot(), "src", "CodeIndex", "Indexer", "References", "ReferenceExtractor.cs");
        var content = File.ReadAllText(path);

        var stopwatch = Stopwatch.StartNew();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        stopwatch.Stop();

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "ReferenceExtractor");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "Extract");
        var runawayBudget = TimeSpan.FromSeconds(30);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"ReferenceExtractor.cs extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }
}
