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
    [Fact]
    public void Extract_CSharpSelfCall_StampsSelfReference()
    {
        const string content = """
            public static class CycleFixture
            {
                public static void Recurse() { Recurse(); }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var selfCall = Assert.Single(references, reference =>
            reference.SymbolName == "Recurse"
            && reference.ReferenceKind == "call");
        Assert.True(selfCall.IsSelfReference);
        Assert.False(selfCall.IsMutualRecursion);
    }

    [Fact]
    public void Extract_CSharpGenericInvocation_EmitsGraphTypeArgumentReference()
    {
        const string content = """
            interface IFoo {}
            class Runner
            {
                void Process<T>(T item) {}
                void Run(IFoo value) { Process<IFoo>(value); }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "IFoo"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "IFoo"
            && reference.ReferenceKind == "generic_type_argument"
            && reference.ContainerName == "Run");
    }

    [Fact]
    public void Extract_CsharpRawStringLongerQuoteRun_DoesNotLeakCallReferences()
    {
        // Regression for #1453: a raw string opened with four quotes must only
        // close on exactly four quotes. A longer quote run inside the content
        // stays masked so call-shaped text does not become a phantom reference.
        // #1453 の回帰: 4 個の quote で始まった raw string は、ちょうど 4 個の
        // quote でのみ閉じる。本文中のより長い quote run はマスクされたままになり、
        // 呼び出し風テキストが疑似参照になってはならない。
        const string content = """""""
            class Service
            {
                void Real()
                {
                    var s = """"hello """""" PhantomCall() world"""";
                    ActualCall();
                }
            }
            """"""";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "ActualCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "PhantomCall");
    }

    [Fact]
    public void Extract_CsharpLineCommentAttributeCandidate_DoesNotScanToEndOfFile()
    {
        var builder = new StringBuilder();
        builder.AppendLine("class Service");
        builder.AppendLine("{");
        builder.AppendLine("    void Run()");
        builder.AppendLine("    {");
        for (var i = 0; i < 1000; i++)
            builder.AppendLine($"        Call{i}(); // argument, [not an attribute candidate");
        builder.AppendLine("        ActualCall();");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        var content = builder.ToString();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "ActualCall");
    }

    [Fact]
    public void Extract_CsharpAsyncIterator_EmitsTypeAndImplicitImplementationReferences()
    {
        const string content = """
            using System.Collections.Generic;

            class Item {}

            class Service
            {
                public async IAsyncEnumerable<Item> StreamAsync()
                {
                    await Task.Yield();
                    yield return new Item();
                }

                public void UseLocal()
                {
                    async IAsyncEnumerable<Item> LocalStream()
                    {
                        yield return new Item();
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "StreamAsync"
            && symbol.ReturnType?.Contains("IAsyncEnumerable<Item>", StringComparison.Ordinal) == true);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "LocalStream"
            && symbol.ReturnType?.Contains("IAsyncEnumerable<Item>", StringComparison.Ordinal) == true);

        Assert.Contains(references, reference =>
            reference.SymbolName == "IAsyncEnumerable"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "StreamAsync");
        Assert.Contains(references, reference =>
            reference.SymbolName == "IAsyncEnumerator"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "StreamAsync");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Item"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "LocalStream");
        Assert.Contains(references, reference =>
            reference.SymbolName == "GetAsyncEnumerator"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "StreamAsync");
        Assert.Contains(references, reference =>
            reference.SymbolName == "MoveNextAsync"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "LocalStream");
    }

    [Fact]
    public void Extract_CsharpStaticInterfaceMembers_EmitImplicitImplementationReferences()
    {
        const string content = """
            public interface IParseable<T>
            {
                static abstract T Parse(string s);
                static virtual T Create() => default!;
                static abstract int Scale { get; }
            }

            public interface ICodeParseable<T>
            {
                static abstract T Parse(int code);
            }

            public interface IAdditive<TSelf>
            {
                static abstract TSelf Add(TSelf left, TSelf right);
                static abstract bool TryParse(string s, out TSelf value);
            }

            public interface IWrongReturn<T>
            {
                static abstract int Parse(string s);
            }

            public interface IWrongProperty
            {
                static abstract string Scale { get; }
            }

            public readonly struct Money : IParseable<Money>, IAdditive<Money>
            {
                public static Money Parse(string s) => new();
                public static Money Create() => new();
                public static int Scale => 1;
                public static Money Add(Money left, Money right) => new();
                public static bool TryParse(string s, out Money value)
                {
                    value = new();
                    return true;
                }
            }

            public readonly struct TextOnly : ICodeParseable<TextOnly>
            {
                public static TextOnly Parse(string s) => new();
            }

            public readonly struct WrongRef : IAdditive<WrongRef>
            {
                public static WrongRef Add(WrongRef left, WrongRef right) => new();
                public static bool TryParse(string s, ref WrongRef value) => true;
            }

            public readonly struct WrongReturn : IWrongReturn<WrongReturn>
            {
                public static WrongReturn Parse(string s) => new();
            }

            public readonly struct WrongProperty : IWrongProperty
            {
                public static int Scale => 2;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "Parse"
            && symbol.ContainerKind == "interface"
            && symbol.ContainerName == "IParseable"
            && symbol.Signature?.Contains("static abstract", StringComparison.Ordinal) == true);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "function"
            && symbol.Name == "Create"
            && symbol.ContainerKind == "interface"
            && symbol.ContainerName == "IParseable"
            && symbol.Signature?.Contains("static virtual", StringComparison.Ordinal) == true);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "property"
            && symbol.Name == "Scale"
            && symbol.ContainerKind == "interface"
            && symbol.ContainerName == "IParseable"
            && symbol.Signature?.Contains("static abstract", StringComparison.Ordinal) == true);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Parse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "Parse"
            && reference.Context == "public static Money Parse(string s) => new();");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Create"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "Create"
            && reference.Context == "public static Money Create() => new();");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Scale"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "Scale"
            && reference.Context == "public static int Scale => 1;");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Add"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "Add"
            && reference.Context == "public static Money Add(Money left, Money right) => new();");
        Assert.Contains(references, reference =>
            reference.SymbolName == "TryParse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "TryParse"
            && reference.Context == "public static bool TryParse(string s, out Money value)");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Parse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.Context == "public static TextOnly Parse(string s) => new();");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "TryParse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.Context == "public static bool TryParse(string s, ref WrongRef value) => true;");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Parse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.Context == "public static WrongReturn Parse(string s) => new();");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Scale"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.Context == "public static int Scale => 2;");
    }

    [Fact]
    public void Extract_CsharpStaticInterfaceMembers_UsesWorkspaceContractsAcrossFiles()
    {
        const string interfaceContent = """
            public interface IParseable<T>
            {
                static abstract T Parse(string s);
            }
            """;
        const string implementationContent = """
            public readonly struct Money : IParseable<Money>
            {
                public static Money Parse(string s) => new();
            }
            """;

        var interfaceSymbols = SymbolExtractor.Extract(1, "csharp", interfaceContent, "IParseable.cs");
        var implementationSymbols = SymbolExtractor.Extract(2, "csharp", implementationContent, "Money.cs");
        var sameFileOnlyReferences = ReferenceExtractor.Extract(2, "csharp", implementationContent, implementationSymbols, "Money.cs");
        var workspaceReferences = ReferenceExtractor.Extract(
            2,
            "csharp",
            implementationContent,
            implementationSymbols,
            "Money.cs",
            interfaceSymbols.Concat(implementationSymbols).ToList());

        Assert.DoesNotContain(sameFileOnlyReferences, reference =>
            reference.SymbolName == "Parse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.Context == "public static Money Parse(string s) => new();");
        Assert.Contains(workspaceReferences, reference =>
            reference.SymbolName == "Parse"
            && reference.ReferenceKind == "implicit_implementation"
            && reference.ContainerName == "Parse"
            && reference.Context == "public static Money Parse(string s) => new();");
    }

    [Fact]
    public void Extract_CsharpExpressionBodiedMembers_AttributeToIndividualMember()
    {
        // issue #233: expression-bodied methods and properties must attribute their
        // calls to the individual member, not collapse to the enclosing class.
        // issue #233: 式本体メソッドと式本体プロパティは、外側クラスにまとめられず
        // 個別メンバーに呼び出しが帰属する必要がある。
        const string content = """
            namespace App;

            public class Calc
            {
                public int Compute() => 42;
                public int Wrap1() => Compute();
                public int Wrap2() => this.Compute();
                public int Wrap3 => Compute();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRefs = references.Where(r => r.SymbolName == "Compute").ToList();
        Assert.Equal(3, computeRefs.Count);
        Assert.Contains(computeRefs, r => r.ContainerKind == "function" && r.ContainerName == "Wrap1");
        Assert.Contains(computeRefs, r => r.ContainerKind == "function" && r.ContainerName == "Wrap2");
        Assert.Contains(computeRefs, r => r.ContainerKind == "property" && r.ContainerName == "Wrap3");
        Assert.DoesNotContain(computeRefs, r => r.ContainerKind == "class");
    }

    [Fact]
    public void Extract_CsharpSameLineDefinitionCalls_KeepRecursiveAndDelegatedReferences()
    {
        // issue #252: same-line definition suppression must drop only the declarator token,
        // not later recursive calls or delegated calls that happen to share the same name.
        // issue #252: 同一行の定義抑制は宣言子トークンだけを落とし、同じ名前を共有する
        // 後続の再帰呼び出しや委譲呼び出しまで消してはいけない。
        const string content = """
            namespace Demo;

            public class R
            {
                public int Fib(int n) => Fib(n - 1);
                public void DoLog(ILog log) => log.DoLog();

                public int FibBlock(int n)
                {
                    if (n < 2) return n;
                    return FibBlock(n - 1) + FibBlock(n - 2);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var fibRefs = references.Where(r => r.SymbolName == "Fib").ToList();
        Assert.Single(fibRefs);
        Assert.All(fibRefs, reference =>
        {
            Assert.Equal("function", reference.ContainerKind);
            Assert.Equal("Fib", reference.ContainerName);
        });

        var doLogRef = Assert.Single(references.Where(r => r.SymbolName == "DoLog"));
        Assert.Equal("function", doLogRef.ContainerKind);
        Assert.Equal("DoLog", doLogRef.ContainerName);

        var fibBlockRefs = references.Where(r => r.SymbolName == "FibBlock").ToList();
        Assert.Equal(2, fibBlockRefs.Count);
        Assert.All(fibBlockRefs, reference =>
        {
            Assert.Equal("function", reference.ContainerKind);
            Assert.Equal("FibBlock", reference.ContainerName);
        });
    }

    [Fact]
    public void Extract_CsharpMethodGroups_TrackDelegateHandoffs()
    {
        // issue #239: method groups and callback handoffs should survive without a trailing `(`.
        // issue #239: method group / callback の handoff も末尾 `(` がなくても拾うこと。
        const string content = """
            namespace Demo;
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Worker
            {
                public void Fire() { }
                public int Compute(int x) => x;
                public bool IsValid(int x) => x > 0;

                public void Wire(IEnumerable<int> xs)
                {
                    Action a = Fire;
                    Func<int, int> f = Compute;
                    var g = this.Compute;
                    var h = new Func<int, int>(Compute);
                    var filtered = xs.Where(IsValid);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Equal(1, references.Count(r => r.SymbolName == "Fire" && r.ReferenceKind == "call"));
        Assert.Equal(3, references.Count(r => r.SymbolName == "Compute" && r.ReferenceKind == "call"));
        Assert.Equal(1, references.Count(r => r.SymbolName == "IsValid" && r.ReferenceKind == "call"));
        Assert.All(references.Where(r => r.SymbolName is "Fire" or "Compute" or "IsValid"), reference =>
        {
            Assert.Equal("function", reference.ContainerKind);
            Assert.Equal("Wire", reference.ContainerName);
        });
    }

    [Fact]
    public void Extract_CsharpExpressionBodiedMultiLine_AttributesToMember()
    {
        // Multi-line expression body (declaration on one line, `=> expr;` on the next)
        // must still attribute calls on the expression line to the enclosing member.
        // 宣言行の次の行に `=> expr;` が来る multi-line 式本体でも、式行の呼び出しが
        // 外側メンバーに帰属すること。
        const string content = """
            public class Calc
            {
                public int Compute() => 42;
                public int MultiLine()
                    => Compute();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRef = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("function", computeRef.ContainerKind);
        Assert.Equal("MultiLine", computeRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpMultiLineExpressionBodiedProperty_AttributesToProperty()
    {
        // issue #233 second review follow-up: multi-line expression-bodied property
        // (`public int Wrap` + newline + `    => Compute();`) must attribute the
        // Compute() call to the property, not the enclosing class.
        // issue #233 の再レビュー指摘: 宣言行の次行に `=> expr;` が来る multi-line 式本体
        // プロパティも、Compute() 呼び出しが外側クラスではなく property に帰属すること。
        const string content = """
            public class Calc
            {
                public int Compute() => 42;
                public int Wrap
                    => Compute();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRef = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("property", computeRef.ContainerKind);
        Assert.Equal("Wrap", computeRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpBlockBodiedPropertyAccessor_AttributesToProperty()
    {
        // issue #233 review follow-up: Allman-style block-bodied properties (with `{`
        // on the next line) must have accessor-internal calls attributed to the property,
        // not the enclosing class.
        // issue #233 のレビュー指摘: Allman スタイル（次行に `{`）の block-bodied property は、
        // accessor 内部の呼び出しが外側クラスではなく property に帰属する必要がある。
        const string content = """
            public class Calc
            {
                public int Compute() => 42;

                public int Wrap
                {
                    get { return Compute(); }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRef = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("property", computeRef.ContainerKind);
        Assert.Equal("Wrap", computeRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpBraceSameLineAccessorNextLineProperty_AttributesToProperty()
    {
        // issue #233 fifth review follow-up: the common Microsoft-style block-bodied
        // property — `{` on the same line as the declaration and the accessor on the
        // following line — must have accessor-internal calls attributed to the property,
        // not the enclosing class.
        // issue #233 第5次レビュー指摘: `{` が宣言行末にあり、accessor が次行にある
        // 標準的な block-bodied property でも、accessor 内部の呼び出しが外側クラスでは
        // なく property に帰属する必要がある。
        const string content = """
            public class Calc
            {
                public int Compute() => 42;

                public int Wrap {
                    get { return Compute(); }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRef = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("property", computeRef.ContainerKind);
        Assert.Equal("Wrap", computeRef.ContainerName);
    }

    [Fact]
    public void Extract_CSharp_VerboseVerbatimIdentifiers_NormalizeCallsAndInstantiation()
    {
        const string content = """
            public class @class
            {
                public void @if() { }

                public void Run()
                {
                    @if();
                    var direct = new @class();
                    var initializer = new @class { };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "if"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Run");
        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "class"
            && reference.ReferenceKind == "instantiate"));
        Assert.DoesNotContain(references, reference => reference.SymbolName.StartsWith("@", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CsharpAllmanBlockBodiedProperty_WithBlockComment_AttributesToProperty()
    {
        // issue #233 fourth review follow-up: a multi-line /* ... */ block comment
        // between the property header line and its `{` must not prevent the property
        // from being recognized, so accessor-internal calls still attribute to the
        // property rather than the enclosing class.
        // issue #233 の 4 回目レビュー指摘: property のヘッダ行と `{` の間に複数行の
        // /* ... */ ブロックコメントが入っていても、property として認識され、
        // accessor 内部の呼び出しは外側クラスではなく property に帰属する必要がある。
        const string content = """
            public class Calc
            {
                public int Compute() => 42;

                public int Wrap
                /* some multi-line
                   block comment */
                {
                    get { return Compute(); }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRef = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("property", computeRef.ContainerKind);
        Assert.Equal("Wrap", computeRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpMultiLineExpressionBodiedProperty_WithBlockComment_AttributesToProperty()
    {
        // issue #233 fourth review follow-up: a multi-line /* ... */ block comment
        // between the property header line and its `=>` continuation must not prevent
        // the property from being recognized, so Compute() still attributes to the
        // property rather than the enclosing class.
        // issue #233 の 4 回目レビュー指摘: property のヘッダ行と `=>` 継続行の間に
        // 複数行の /* ... */ ブロックコメントが入っていても property として認識され、
        // Compute() 呼び出しは外側クラスではなく property に帰属する必要がある。
        const string content = """
            public class Calc
            {
                public int Compute() => 42;

                public int Wrap
                /* multi-line
                   comment */
                    => Compute();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRef = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("property", computeRef.ContainerKind);
        Assert.Equal("Wrap", computeRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpCompactSameLineTypeBody_AttributesEnumMemberReferenceToInnermostMethod()
    {
        // issue #546: when a compact same-line C# type body contains multiple nested
        // members, enum-member references inside the inline method must attach to the
        // innermost function rather than collapsing to the outer class.
        // issue #546: compact な same-line C# 型本体で複数メンバーが同一行に並んでも、
        // inline method 内の enum member 参照は外側 class ではなく最内側 function に帰属すること。
        const string content = """
            namespace N;
            enum Color { Red }
            class C { int N => 0; void M() { var x = global::N.Color.Red; } }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRef = Assert.Single(references.Where(r => r.SymbolName == "Red"));
        Assert.Equal("function", redRef.ContainerKind);
        Assert.Equal("M", redRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpCompactSameLineTypeBody_AttributesPropertyCallToInnermostProperty()
    {
        // Compact same-line type bodies must also keep earlier inline property references on
        // the property itself instead of letting a later same-line method steal them.
        // compact な same-line 型本体でも、先行する inline property 内の参照は、
        // 後続 method ではなく property 自身に帰属し続ける必要がある。
        const string content = """
            namespace N;
            enum Color { Red }
            class C { Color Wrap => global::N.Color.Red; void M() { var x = global::N.Color.Red; } }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var wrapRef = Assert.Single(references.Where(r => r.SymbolName == "Red" && r.ContainerName == "Wrap"));
        Assert.Equal("property", wrapRef.ContainerKind);
        Assert.DoesNotContain(references, r => r.SymbolName == "Red" && r.ContainerName == "Wrap" && r.ContainerKind == "class");
    }

    [Fact]
    public void Extract_CsharpMultiLineTypeBody_KeepsEnumMemberReferenceOnMethod()
    {
        // The fix for compact same-line type bodies must not regress the existing multi-line
        // control case, which already resolves enum-member references to the method.
        // compact same-line 向け修正で、既存の multi-line 制御ケースが class 側へ戻らないこと。
        const string content = """
            namespace N;
            enum Color { Red }
            class C
            {
                int N => 0;
                void M() { var x = global::N.Color.Red; }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRef = Assert.Single(references.Where(r => r.SymbolName == "Red"));
        Assert.Equal("function", redRef.ContainerKind);
        Assert.Equal("M", redRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpDefinitionLine_DoesNotBecomeReference()
    {
        const string content = """
            public class App
            {
                public void Run()
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Empty(references);
    }

    [Fact]
    public void Extract_CsharpRawStringFixture_DoesNotBecomeReference()
    {
        const string content = """"
            public class FixtureHost
            {
                public void UsesRawFixture()
                {
                    const string fixture = """
                        Execute();
                        new Widget();
                        main();
                        """;

                    Run();
                }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var reference = Assert.Single(references);
        Assert.Equal("Run", reference.SymbolName);
        Assert.Equal("call", reference.ReferenceKind);
        Assert.Equal("UsesRawFixture", reference.ContainerName);
    }

    [Fact]
    public void Extract_CsharpInterpolatedRawString_KeepsInterpolationCallReferences()
    {
        const string content = """"
            public class FixtureHost
            {
                public string Run() => "ok";

                public string UsesRawFixture()
                {
                    return $"""
                        value = {Run()}
                        literal = function main()
                        """;
                }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var reference = Assert.Single(references);
        Assert.Equal("Run", reference.SymbolName);
        Assert.Equal("call", reference.ReferenceKind);
        Assert.Equal("UsesRawFixture", reference.ContainerName);
    }

    [Fact]
    public void Extract_CsharpInterpolatedRawString_WithNestedInterpolatedString_KeepsInterpolationCallReferences()
    {
        const string content = """"
            public class FixtureHost
            {
                public string Run() => "ok";

                public string UsesRawFixture()
                {
                    return $"""
                        value = {$"{Run()}"}
                        literal = function main()
                        """;
                }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var runReference = Assert.Single(references.Where(reference => reference.SymbolName == "Run"));
        Assert.Equal("call", runReference.ReferenceKind);
        Assert.Equal("UsesRawFixture", runReference.ContainerName);
    }

    [Fact]
    public void Extract_CsharpInterpolatedString_KeepsSingleLineInterpolationCallReferences()
    {
        const string content = """
            public class FixtureHost
            {
                public int Run() => 42;

                public string Render()
                {
                    return $"value = {Run()}";
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var runReference = Assert.Single(references);
        Assert.Equal("Run", runReference.SymbolName);
        Assert.Equal("call", runReference.ReferenceKind);
        Assert.Equal("Render", runReference.ContainerName);
    }

    [Fact]
    public void Extract_CsharpInterpolatedRawString_WithNestedRawString_DoesNotLeakPhantomReferences()
    {
        const string content = """"
            public class FixtureHost
            {
                public int Run() => 1;
                public string Id(string value) => value;

                public int UsesRawFixture()
                {
                    return $"""
                        value = {Id("""
                            Execute();
                            public class Phantom
                            {
                                public void Go() { }
                            }
                            """) + Run()}
                        """.Length;
                }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "Id" && reference.ContainerName == "UsesRawFixture");
        Assert.Contains(references, reference => reference.SymbolName == "Run" && reference.ContainerName == "UsesRawFixture");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Execute");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Go");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Phantom");
    }

    [Fact]
    public void Extract_CsharpInterpolatedString_WithEscapedBraces_DoesNotLeakPhantomReference()
    {
        const string content = """
            public class FixtureHost
            {
                public string Render()
                {
                    return $"{{Run()}}";
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Run");
    }

    [Fact]
    public void Extract_CsharpInterpolatedVerbatimString_WithEscapedBraces_DoesNotLeakPhantomReference()
    {
        const string content = """
            public class FixtureHost
            {
                public string Render()
                {
                    return $@"{{Run()}}";
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Run");
    }

    [Fact]
    public void Extract_CsharpMultiLineVerbatimString_DoesNotLeakPhantomCallReferences()
    {
        // Regression for issue #288: call-looking identifiers inside a multi-line
        // @"..." verbatim string body must not be captured as references, including
        // constructor-style initializer text that would otherwise look like instantiate.
        // issue #288 回帰: 複数行 @"..." 逐語文字列の本体にある呼び出しらしい識別子や
        // instantiate っぽい構文は参照として抽出してはならない。
        const string content = """
            public class FixtureHost
            {
                public void M()
                {
                    var legacy = @"
                        SELECT * FROM t
                        new Widget { X = 1 };
                        WHERE x = BadCall()
                    ";
                    RealCall();
                }

                private void RealCall() { }
                private void BadCall() { }
                private sealed class Widget { public int X { get; set; } }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "BadCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Widget");
        Assert.Contains(references, reference => reference.SymbolName == "RealCall" && reference.ContainerName == "M");
    }

    [Fact]
    public void Extract_CsharpInterpolatedAndVerbatimStrings_Issue264_Repro_CapturesHoleCallsAndSuppressesPhantoms()
    {
        // Regression for issue #264 exact repro: single-line $"..." interpolated
        // strings, multi-line $@"..." AND the alternate @$"..." verbatim-interpolated
        // ordering, and non-interpolated @"..." verbatim strings must all be handled
        // correctly: interpolation-hole call sites are captured (for both $@" and @$"
        // orderings, which StructuralLineMasker handles via dedicated branches) while
        // pure verbatim bodies never leak phantom references.
        // issue #264 repro 回帰: 単行 $"..."、複数行 $@"..." と代替順序 @$"..." の
        // 双方の補間ホール内の呼び出しが捕捉され、非補間 @"..." 本体からは phantom
        // 参照を漏らさないこと。
        const string content = """"
            namespace Demo;
            public class Helper
            {
                public static string GetName() => "bob";
                public static int    GetAge()  => 42;
                public static string Format(string s) => s;
            }
            public class Caller
            {
                public void Work()
                {
                    var s1 = $"Hello {Helper.GetName()}";
                    var s2 = $"Age: {Helper.GetAge()} years";
                    var s3 = $"Nested {Helper.Format(Helper.GetName())}";
                    var s4 = $@"Multi
            line {Helper.GetName()} text";
                    var s6 = @$"Alt
            order {Helper.GetName()} {Helper.GetAge()} end";
                    var s5 = Helper.GetName();
                    var sql = @"SELECT PhantomCall() FROM PhantomTable()
            MoreFake() lines";
                }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var getNameCalls = references
            .Where(r => r.SymbolName == "GetName" && r.ReferenceKind == "call")
            .ToList();
        Assert.Equal(5, getNameCalls.Count);
        Assert.All(getNameCalls, r => Assert.Equal("Work", r.ContainerName));

        var getAgeCalls = references
            .Where(r => r.SymbolName == "GetAge" && r.ReferenceKind == "call")
            .ToList();
        Assert.Equal(2, getAgeCalls.Count);
        Assert.All(getAgeCalls, r => Assert.Equal("Work", r.ContainerName));

        Assert.Contains(references, r =>
            r.SymbolName == "Format" && r.ReferenceKind == "call" && r.ContainerName == "Work");

        Assert.DoesNotContain(references, r => r.SymbolName == "PhantomCall");
        Assert.DoesNotContain(references, r => r.SymbolName == "PhantomTable");
        Assert.DoesNotContain(references, r => r.SymbolName == "MoreFake");
    }

    [Fact]
    public void Extract_CsharpMultiLineRawString_IssueRepro_DoesNotLeakPhantomCallReferences()
    {
        // Regression for issue #288 exact repro: C# 11 raw strings, interpolated raw
        // strings, and multi-line verbatim strings must not leak call-looking
        // identifiers from their content into the reference graph.
        // issue #288 の repro に対する回帰: C# 11 の raw string、補間付き raw string、
        // 複数行 verbatim string の本体から呼び出しらしい識別子を参照グラフへ漏らさない。
        const string content = """"
            namespace App;

            public class Demo
            {
                public void M()
                {
                    var sql = """
                        SELECT * FROM users
                        WHERE id = EvilCall(42)
                        AND name = AnotherCall('bob')
                        """;

                    var id = 42;
                    var msg = $"""
                        Query result for id={id}
                        Hidden: PhantomCall({id})
                        """;

                    var legacy = @"
                        SELECT * FROM t
                        new Widget { X = 1 }
                        WHERE x = BadCall()
                    ";

                    RealCall();
                }

                private void RealCall() { }
                private int EvilCall(int x) => x;
                private string AnotherCall(string s) => s;
                private int PhantomCall(int x) => x;
                private void BadCall() { }
                private sealed class Widget { public int X { get; set; } }
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "EvilCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "AnotherCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "PhantomCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "BadCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Widget");
        Assert.Contains(references, reference => reference.SymbolName == "RealCall" && reference.ContainerName == "M");
    }

    [Fact]
    public void Extract_CsharpIndentedRawStringBeforeBlockComment_DoesNotLeakXmlDocReferences()
    {
        // Regression: BuildCSharpBlockCommentLines must recognize the closing delimiter of an
        // indented raw string. Otherwise the scanner stays in raw-string mode, misses the
        // following ordinary block comment, and treats its `/**` opener as XML doc.
        // 回帰: BuildCSharpBlockCommentLines はインデント付き raw string の閉じ記号を認識する必要がある。
        // さもないと raw-string mode に居座って後続の通常 block comment を見失い、その `/**` を XML doc と
        // 誤認してしまう。
        var content =
            "namespace App;\n"
            + "\n"
            + "public class Demo\n"
            + "{\n"
            + "    public void M()\n"
            + "    {\n"
            + "        var raw = \"\"\"\n"
            + "            ignored content\n"
            + "            \"\"\";\n"
            + "\n"
            + "        /*\n"
            + "         * /**\n"
            + "         * <see cref=\"PhantomCall\"/>\n"
            + "         */\n"
            + "\n"
            + "        RealCall();\n"
            + "    }\n"
            + "\n"
            + "    private void RealCall() { }\n"
            + "    private void PhantomCall() { }\n"
            + "}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "PhantomCall");
        Assert.Contains(references, reference => reference.SymbolName == "RealCall" && reference.ContainerName == "M");
    }

    [Fact]
    public void Extract_CsharpRawStringCloseWithSemicolon_DoesNotMaskFollowingComment()
    {
        // Regression for issue #988: a raw string close line with a trailing semicolon
        // must still end raw-string tracking so the following comment line is not
        // treated as part of the raw-string body.
        // issue #988 回帰: 末尾にセミコロンが付く raw string の閉じ行でも raw-string tracking を
        // 終了し、その次のコメント行が raw-string 本体として扱われないこと。
        var lines = new[]
        {
            "public class Demo",
            "{",
            "    public string Raw() => \"\"\"",
            "        ignored content",
            "        \"\"\";   ",
            "",
            "    /// <summary><see cref=\"Helper\"/></summary>",
            "    public void Run() { }",
            "}",
        };

        var buildMethod = typeof(ReferenceExtractor).GetMethod(
            "BuildCSharpMultilineStringContentLines",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(buildMethod);

        var insideStringContent = (bool[])buildMethod!.Invoke(null, new object[] { lines })!;

        Assert.True(insideStringContent[3]);
        Assert.True(insideStringContent[4]);
        Assert.False(insideStringContent[6]);
    }

    [Fact]
    public void Extract_CsharpKeywords_NotExtractedAsReferences()
    {
        // LINQ and C# contextual keywords should be ignored
        // LINQ や C# の文脈キーワードは参照として抽出されないこと
        const string content = """
            public class Query
            {
                public void Run()
                {
                    var x = from(items);
                    if (x is string s) { }
                    var y = default(int);
                    base.ToString();
                    value.GetType();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "from");
        Assert.DoesNotContain(references, r => r.SymbolName == "is");
        Assert.DoesNotContain(references, r => r.SymbolName == "default");
        // ToString and GetType are real calls, should be extracted
        Assert.Contains(references, r => r.SymbolName == "ToString");
        Assert.Contains(references, r => r.SymbolName == "GetType");
    }

    [Fact]
    public void Extract_CsharpRunCall_IsNotDroppedByMakefileKeywordList()
    {
        const string content = """
            public class Worker
            {
                public void Execute(Task task)
                {
                    task.Run();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference => reference.SymbolName == "Run" && reference.ContainerName == "Execute");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_UsesNarrowestOwnerContainer()
    {
        const string content = """
            namespace Demo;

            public class Outer
            {
                public enum First { None }
            }

            public enum Nested { A = 1, B = A }

            public class UsesEnum
            {
                public Nested Value => Nested.A;

                public Nested GetValue()
                {
                    return Nested.A;
                }

                public void Use()
                {
                    return Outer.First.None;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var aRefs = references.Where(reference => reference.SymbolName == "A").OrderBy(reference => reference.Line).ToList();
        Assert.Equal(2, aRefs.Count);
        Assert.All(aRefs, reference => Assert.Equal("call", reference.ReferenceKind));
        Assert.Contains(aRefs, reference => reference.ContainerKind == "property" && reference.ContainerName == "Value");
        Assert.Contains(aRefs, reference => reference.ContainerKind == "function" && reference.ContainerName == "GetValue");

        var noneRef = Assert.Single(references.Where(reference => reference.SymbolName == "None"));
        Assert.Equal("call", noneRef.ReferenceKind);
        Assert.Equal("function", noneRef.ContainerKind);
        Assert.Equal("Use", noneRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpReturnTargetAttribute_ReferenceIsRecordedAsAttribute()
    {
        // issue #293: C# attributes (including targeted `[return: ...]` form) are recorded as
        // `attribute` kind — the reference must not be dropped, but also must not pollute the
        // call-graph with a phantom `call` row.
        // issue #293: C# 属性（`[return: ...]` の target 付きも含む）は `attribute` として
        // 記録される。参照自体は失われないが、call-graph を `call` 行で汚染してはならない。
        const string content = """
            using System.Runtime.InteropServices;

            public class Foo
            {
                [MarshalAs(UnmanagedType.Bool)]
                public bool Field1;

                [return: MarshalAs(UnmanagedType.Bool)]
                public bool Method1() => true;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var marshalAsRefs = references
            .Where(reference => reference.SymbolName == "MarshalAs")
            .OrderBy(reference => reference.Line)
            .ToList();

        Assert.Equal(2, marshalAsRefs.Count);
        Assert.Equal([5, 8], marshalAsRefs.Select(reference => reference.Line).ToArray());
        Assert.All(marshalAsRefs, reference => Assert.Equal("attribute", reference.ReferenceKind));
        Assert.All(marshalAsRefs, reference => Assert.Equal("Foo", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpNonEnumQualifiedMemberAccess_DoesNotBecomeEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum EnumHolder
            {
                A = 1
            }

            public static class Values
            {
                public static int A = 1;
            }

            public class UsesValues
            {
                public int Read()
                {
                    return Values.A;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "A" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithConflictingNonEnumType_DoesNotLeakAcrossNamespaces()
    {
        const string content = """
            namespace A;

            public enum Status
            {
                Ready
            }

            namespace B;

            public static class Status
            {
                public static int Ready = 1;
            }

            public class Uses
            {
                public int Read()
                {
                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithUsingAliasToNonEnumType_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace A;

            public enum Status
            {
                Ready
            }

            public static class Values
            {
                public static int Ready = 1;
            }

            namespace B;

            using Status = A.Values;

            public class UsesAlias
            {
                public int Read()
                {
                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithUsingAliasToEnumType_PreservesEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            using Alias = Demo.Status;

            public class UsesAlias
            {
                public Status Read()
                {
                    return Alias.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var ready = Assert.Single(references.Where(reference => reference.SymbolName == "Ready"));
        Assert.Equal("call", ready.ReferenceKind);
        Assert.Equal("Read", ready.ContainerName);
    }

    [Fact]
    public void Extract_CsharpUsingStaticConstantPattern_WithTypeAliasKeepsTypeReference()
    {
        const string content = """
            using Red = RealTypes.Red;
            using static Probe.Color;

            namespace Probe;

            enum Color { Red, Blue }
            class Demo
            {
                bool Match(object value) => value is Red;
                void ProbeType() { _ = typeof(Red); }
            }

            namespace RealTypes;
            class Red {}
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRefs = references.Where(reference =>
            reference.SymbolName == "Red"
            && reference.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, redRefs.Count);
        Assert.Contains(redRefs, reference => reference.ContainerName == "Match" && reference.Line == 9);
        Assert.Contains(redRefs, reference => reference.ContainerName == "ProbeType" && reference.Line == 10);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithRepeatedAliasNames_UsesNearestAliasScope()
    {
        const string content = """
            namespace Demo
            {
                public enum Status
                {
                    Ready
                }

                public static class Values
                {
                    public static int Ready = 1;
                }
            }

            namespace B
            {
                using Alias = Demo.Values;

                public class UsesValues
                {
                    public int Read()
                    {
                        return Alias.Ready;
                    }
                }
            }

            namespace C
            {
                using Alias = Demo.Status;

                public class UsesEnum
                {
                    public Demo.Status Read()
                    {
                        return Alias.Ready;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(35, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLaterSiblingAliasRebinding_DoesNotStealEarlierEnumScope()
    {
        const string content = """
            namespace Demo
            {
                public enum Status
                {
                    Ready
                }

                public static class Values
                {
                    public static int Ready = 1;
                }
            }

            namespace B
            {
                using Alias = Demo.Status;

                public class UsesEnum
                {
                    public Demo.Status Read()
                    {
                        return Alias.Ready;
                    }
                }
            }

            namespace C
            {
                using Alias = Demo.Values;

                public class UsesValues
                {
                    public int Read()
                    {
                        return Alias.Ready;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(22, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithValueReceiverNamedLikeEnum_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Holder Status { get; } = new();

                public int Read()
                {
                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithInstancePropertyShadowInStaticMethod_PreservesEnumReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Holder Status { get; } = new();

                public static Demo.Status Read()
                {
                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(19, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithStaticPropertyShadowInStaticMethod_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public static Holder Status { get; } = new();

                public static int Read()
                {
                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithIndentedLocalShadowing_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(bool flag)
                {
                    if (flag)
                    {
                        Holder Status = new();
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(23, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithIndentedUsingVarShadowing_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder : IDisposable
            {
                public int Ready { get; set; }

                public void Dispose()
                {
                }
            }

            public sealed class Uses
            {
                public Demo.Status Read(bool flag)
                {
                    if (flag)
                    {
                        using var Status = new Holder();
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(27, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithPropertyAccessorLocalShadowing_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Value
                {
                    get
                    {
                        Holder Status = new();
                        _ = Status.Ready;
                        return Demo.Status.Ready;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Value", readyRefs[0].ContainerName);
        Assert.Equal("property", readyRefs[0].ContainerKind);
    }

    [Fact]
    public void Extract_CsharpGlobalQualifiedEnumMemberAccess_WithPropertyShadowing_PreservesReference()
    {
        const string content = """
            enum Color
            {
                Red
            }

            class C
            {
                int Color => 0;

                void M()
                {
                    var x = global::Color.Red;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRef = Assert.Single(references.Where(reference => reference.SymbolName == "Red" && reference.ReferenceKind == "call"));
        Assert.Equal(12, redRef.Line);
        Assert.Equal("M", redRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpGlobalQualifiedEnumMemberAccess_WithConflictingUsingAlias_PreservesReference()
    {
        const string content = """
            namespace Demo
            {
                public enum Color
                {
                    Red
                }
            }

            namespace Shadow
            {
                public static class Demo
                {
                    public static int Red => 0;
                }
            }

            using Demo = Shadow;

            class C
            {
                Demo.Color M()
                {
                    return global::Demo.Color.Red;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRef = Assert.Single(references.Where(reference => reference.SymbolName == "Red" && reference.ReferenceKind == "call"));
        Assert.Equal(23, redRef.Line);
        Assert.Equal("M", redRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpGlobalQualifiedEnumMemberAccess_WithUsingAliasName_DoesNotCreateReference()
    {
        const string content = """
            namespace Demo;

            public enum Color
            {
                Red
            }

            using Color = Demo.Color;

            class C
            {
                void M()
                {
                    _ = global::Color.Red;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Red" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithGetterLocalShadowing_DoesNotLeakIntoSetter()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Status Value
                {
                    get
                    {
                        Holder Status = new();
                        _ = Status.Ready;
                        return Demo.Status.Ready;
                    }
                    set
                    {
                        _ = Status.Ready;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ToList();
        Assert.Equal([21, 25], readyRefs.Select(reference => reference.Line).ToArray());
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithOutDeclarationShadowing_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                private static bool TryGet(out Holder holder)
                {
                    holder = new Holder();
                    return true;
                }

                public Demo.Status Read()
                {
                    if (TryGet(out Holder Status))
                    {
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithCatchShadowing_DoesNotLeakAfterCatchBlock()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Status Read()
                {
                    try
                    {
                        throw new Exception();
                    }
                    catch (Exception Status)
                    {
                        _ = Status.Message;
                    }

                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithUsingStatementShadowing_DoesNotLeakAfterUsingBlock()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder : IDisposable
            {
                public int Ready { get; set; }

                public void Dispose()
                {
                }
            }

            public sealed class Uses
            {
                public Status Read(bool flag)
                {
                    if (flag)
                    {
                        using (Holder Status = new())
                        {
                            _ = Status.Ready;
                        }
                    }

                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithOutVarShadowing_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                private static bool TryGet(out Holder holder)
                {
                    holder = new Holder();
                    return true;
                }

                public Demo.Status Read()
                {
                    if (TryGet(out var Status))
                    {
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLambdaParameterNamedLikeEnum_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Func<Holder, int> Build()
                {
                    return Status => Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultiLineLambdaParameterNamedLikeEnum_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Func<Holder, int> Build()
                {
                    return
                        (Status) =>
                            Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLambdaParameterNamedLikeEnum_DoesNotLeakAfterLambda()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read()
                {
                    Func<Holder, int> f = Status => Status.Ready;
                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLambdaParameterNamedLikeEnum_DoesNotLeakAfterSameLineLambda()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read()
                {
                    Func<Holder, int> f = Status => Status.Ready; return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedLambdaParameterNamedLikeEnum_DoesNotSuppressEarlierSameLineReference()
    {
        const string content = """
            using System;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(Demo.Status left, Func<Holder, int> right) => left;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read()
                {
                    return Sink.Pick(Demo.Status.Ready, (Holder Status) => Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableNamedLikeEnum_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableNamedLikeEnum_DoesNotLeakAfterQuery()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    _ = from Status in items
                        select Status.Ready;

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableNamedLikeEnum_DoesNotLeakPastQueryArgument()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(IEnumerable<int> left, Demo.Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(from Status in items select Status.Ready, Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableNamedLikeEnum_OrderByComma_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby Status, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableNamedLikeEnum_OrderByDirectionalComma_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby Status descending, items.Count() ascending
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedTerminalSelectInArgument_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(from Status in items select(Status.Ready), Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedGroupByQueryInArgument_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(from Status in items group(Status.Ready) by items.Count(), Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableMemberNamedSelect_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
                public int select { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby Status.select, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableMemberNamedEscapedSelect_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
                public int @select { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby Status.@select, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableMemberNamedSelectSeparatedBySpaces_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
                public int select { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby Status . select, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableOrderByAnonymousTypeComma_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby new { X = Status.Ready, Y = items.Count() }, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryKeywordNamedLocalFunctionInParenthesizedOrderByExpression_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static int select(IEnumerable<Holder> xs) => xs.Count();
                    return from Status in items
                           orderby select(items), items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryKeywordNamedLocalFunctionAfterGreaterThanInOrderByTernary_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static int select(IEnumerable<Holder> xs) => xs.Count();
                    return from Status in items
                           orderby items.Count() > select(items) ? 1 : 0, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryKeywordNamedLocalFunctionAfterLessThanInOrderByTernary_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static int select(IEnumerable<Holder> xs) => xs.Count();
                    return from Status in items
                           orderby items.Count() < select(items) ? 1 : 0, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryKeywordNamedLocalFunctionAfterBangInOrderByTernary_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static bool select(IEnumerable<Holder> xs) => xs.Any();
                    return from Status in items
                           orderby ! select(items) ? 1 : 0, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithAwaitBeforeQueryKeywordNamedLocalFunctionInOrderBy_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading.Tasks;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public async Task<IEnumerable<int>> Read(IEnumerable<Holder> items)
                {
                    static async Task<int> select(IEnumerable<Holder> xs) => await Task.FromResult(xs.Count());
                    return from Status in items
                           orderby await select(items), items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithCommentSeparatedAwaitBeforeQueryKeywordNamedLocalFunctionInOrderBy_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading.Tasks;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public async Task<IEnumerable<int>> Read(IEnumerable<Holder> items)
                {
                    static async Task<int> select(IEnumerable<Holder> xs) => await Task.FromResult(xs.Count());
                    return from Status in items
                           orderby await select /*comment*/ (items), items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithPostfixNullForgivingBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Status Pick(object left, Status right) => right;
                public static Holder? Maybe(Holder value) => value;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Status Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(from Status in items
                                     let alias = Sink.Maybe(Status)!
                                     select(Status.Ready),
                                     Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithThrowBeforeQueryKeywordNamedLocalFunctionInOrderBy_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static System.Exception select(IEnumerable<Holder> xs) => new System.Exception(xs.Count().ToString());
                    return from Status in items
                           orderby items.Count() > 0 ? throw select(items) : 0, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithThrowBeforeGroupNamedLocalFunctionInOrderBy_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static System.Exception group(IEnumerable<Holder> xs) => new System.Exception(xs.Count().ToString());
                    return from Status in items
                           orderby items.Count() > 0 ? throw group(items) : 0, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultilineSelectAfterGreaterThan_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static int select(IEnumerable<Holder> xs) => xs.Count();
                    return from Status in items
                           orderby items.Count() >
                                   select
                                   (items) ? 1 : 0, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultilineThrowBeforeGroup_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static System.Exception group(IEnumerable<Holder> xs) => new System.Exception(xs.Count().ToString());
                    return from Status in items
                           orderby items.Count() > 0 ? throw
                                   group
                                   (items) : null, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithNullableTypeSuffixBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, object value)
                {
                    return Sink.Pick(from Status in items
                                     let cast = value as Status?
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithNullableTupleTypeSuffixBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, object value)
                {
                    return Sink.Pick(from Status in items
                                     let cast = value as (int Left, int Right)?
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithPostfixIncrementBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, int counter)
                {
                    return Sink.Pick(from Status in items
                                     let n = counter++
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithNullableArrayRankSuffixBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, object value)
                {
                    return Sink.Pick(from Status in items
                                     let cast = value as Status[,]?
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithCastedLocalSelectCallInOrderBy_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    static object select(IEnumerable<Holder> xs) => xs.Count();
                    return from Status in items
                           orderby (object)select(items), items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSimpleIdentifierCastedLocalSelectCallInOrderBy_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class CustomType
            {
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    static CustomType select(IEnumerable<Holder> xs) => new();
                    return Sink.Pick(from Status in items
                                     orderby (CustomType)select(items), items.Count()
                                     select Status.Ready,
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultilineSimpleIdentifierCastedLocalSelectCallInOrderBy_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class CustomType
            {
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    static CustomType select(IEnumerable<Holder> xs) => new();
                    return Sink.Pick(from Status in items
                                     orderby (CustomType)
                                             select(items), items.Count()
                                     select Status.Ready,
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedTernaryOrderByBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, bool flag, int left, int right)
                {
                    return Sink.Pick(from Status in items
                                     orderby (flag ? left : right)
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedCoalesceOrderByBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, int? left, int right)
                {
                    return Sink.Pick(from Status in items
                                     orderby (left ?? right)
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedQualifiedMemberAccessBeforeParenthesizedTerminalSelect_PreservesOnlyRealReferences()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items)
                {
                    return Sink.Pick(from Status in items
                                     orderby (Demo.Status.Ready)
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Equal(2, readyRefs.Count);
        Assert.All(readyRefs, readyRef => Assert.Equal("Read", readyRef.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLowercaseAliasCastedLocalSelectCallInOrderBy_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;
            using customType = Demo.CustomType;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class CustomType
            {
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items)
                {
                    static customType select(IEnumerable<object> xs) => new();
                    return Sink.Pick(from Status in items
                                     orderby (customType)select(items)
                                     select Status.Ready,
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedKeywordNamedParameterBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items, int Select)
                {
                    return Sink.Pick(from Status in items
                                     orderby (Select)
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedKeywordNamedLocalBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items)
                {
                    const int Select = 1;
                    return Sink.Pick(from Status in items
                                     orderby (Select)
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedUppercaseConstantBeforeParenthesizedTerminalSelect_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items)
                {
                    const int READY = 1;
                    return Sink.Pick(from Status in items
                                     orderby (READY)
                                     select(Status.Ready),
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParenthesizedTerminalSelectAfterGenericClose_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<object> items)
                {
                    return Sink.Pick(from Status in items where Status is List<int> select(Status.Ready), Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryRangeVariableOrderByObjectInitializerComma_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Key
            {
                public int A { get; set; }
                public int B { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           orderby new Key { A = Status.Ready, B = items.Count() }, items.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithNestedQueryBeforeOrderByComma_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items, IEnumerable<int> others)
                {
                    return from Status in items
                           let nested = from x in others select x
                           orderby items.Count(), nested.Count()
                           select Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithNestedQueryBeforeParenthesizedOrderByComma_PreservesOnlyTrailingReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Demo.Status Pick(object left, Demo.Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items, IEnumerable<int> others)
                {
                    return Sink.Pick(from Status in items
                                     let nested = from x in others select x
                                     orderby(items.Count()), nested.Count()
                                     select Status.Ready,
                                     Demo.Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectGenericTypeArgumentComma_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public static class Sink
            {
                public static int Wrap<TLeft, TRight>(int value) => value;
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           select Sink.Wrap<int, int>(Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectSingleGenericArgument_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public static class Sink
            {
                public static int Wrap<T>(int value) => value;
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           select Sink.Wrap<int>(Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectShiftExpression_PreservesOnlyTrailingEnumReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public static class Sink
            {
                public static IEnumerable<int> Pick(IEnumerable<int> left, Status right) => left;
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(
                        from Status in items
                        select (Status.Ready << 1) >> (1 + Status.Ready),
                        Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ToList();

        Assert.Equal([28], readyRefs.Select(reference => reference.Line).ToArray());
        Assert.All(readyRefs, reference => Assert.Equal("Read", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectGenericTypePatternDesignation_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public int Read(IEnumerable<Holder> items)
                {
                    return (from Status in items
                            select Status is Dictionary<int, int> dict ? Status.Ready : 0).First();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectGenericTypePatternWithoutDesignation_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public int Read(IEnumerable<Holder> items)
                {
                    return (from Status in items
                            select Status is Dictionary<int, int> ? Status.Ready : 0).First();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Theory]
    [InlineData("!=")]
    [InlineData("==")]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectGenericAsNullComparison_DoesNotLeakReference(string comparisonOperator)
    {
        var content = $$"""
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public int Read(IEnumerable<Holder> items)
                {
                    return (from Status in items
                            select Status as Dictionary<int, int> {{comparisonOperator}} null ? Status.Ready : 0).First();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectGenericAsNullComparison_PreservesLaterEnumReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Status Pick(int left, Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Status Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(
                        (from Status in items
                         select Status as Dictionary<int, int> != null ? Status.Ready : 0).First(),
                        Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Ready"
            && reference.ReferenceKind == "call"
            && reference.Line == 28
            && reference.Context.Contains("Status.Ready", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectTupleGenericTypeArgument_DoesNotLeakReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public static class Sink
            {
                public static int Wrap<T>(int value) => value;
            }

            public sealed class Uses
            {
                public IEnumerable<int> Read(IEnumerable<Holder> items)
                {
                    return from Status in items
                           select Sink.Wrap<(int, List<int>)>(Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithQueryKeywordNamedLocalFunctionInSelectExpression_PreservesLaterEnumReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static Status Pick(IEnumerable<int> left, Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Status Read(IEnumerable<Holder> items)
                {
                    static int from(IEnumerable<Holder> xs) => xs.Count();
                    return Sink.Pick(from Status in items select from(items), Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Ready"
            && reference.ReferenceKind == "call"
            && reference.Line == 26
            && reference.Context.Contains("Status.Ready", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithGroupByQueryInArgument_PreservesLaterEnumReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public static class Sink
            {
                public static object Pick(object left, Status right) => right;
            }

            public sealed class Uses
            {
                public object Read(IEnumerable<Holder> items)
                {
                    return Sink.Pick(from Status in items group Status.Ready by items.Count(), Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .ToList();

        Assert.Single(readyRefs);
        Assert.Equal(25, readyRefs[0].Line);
        Assert.Contains("Status.Ready", readyRefs[0].Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithTerminalSelectIdentifierNamedDescending_PreservesLaterEnumReference()
    {
        const string content = """
            using System.Collections.Generic;
            using System.Linq;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public static class Sink
            {
                public static object Pick(object left, Status right) => right;
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public object Read(IEnumerable<Holder> items)
                {
                    var descending = 1;
                    return Sink.Pick(from Status in items select descending, Status.Ready);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .ToList();

        Assert.Single(readyRefs);
        Assert.Equal(26, readyRefs[0].Line);
        Assert.Contains("Status.Ready", readyRefs[0].Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithForeachValueNamedLikeEnum_DoesNotLeakAfterEmbeddedStatement()
    {
        const string content = """
            using System.Collections.Generic;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    foreach (var Status in items)
                        _ = Status.Ready;

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithForeachValueNamedLikeEnum_DoesNotLeakAfterSameLineEmbeddedStatement()
    {
        const string content = """
            using System.Collections.Generic;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items)
                {
                    foreach (var Status in items) _ = Status.Ready; return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithForeachValueNamedLikeEnum_DoesNotLeakInsideElseBranch()
    {
        const string content = """
            using System.Collections.Generic;

            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(IEnumerable<Holder> items, bool flag)
                {
                    foreach (var Status in items)
                        if (flag)
                            _ = 0;
                        else
                            _ = Status.Ready;

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        Assert.Single(readyRefs);
        Assert.Equal("Read", readyRefs[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLaterLocalShadowing_DoesNotSuppressEarlierReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Before()
                {
                    _ = Status.Ready;
                    Holder Status = new();
                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ToList();
        Assert.Equal([17, 19], readyRefs.Select(reference => reference.Line).ToArray());
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithParameterNamedLikeEnum_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public int Read(Holder Status)
                {
                    return Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithDeclarationPatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    if (value is Holder Status)
                    {
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(22, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultiLineIfDeclarationPatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    if (
                        value is Holder Status)
                    {
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(23, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultiLineWhileDeclarationPatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    while (
                        value is Holder Status)
                    {
                        _ = Status.Ready;
                        break;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(24, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithLambdaScopedDeclarationPatternVariable_DoesNotLeakIntoOuterIfBody()
    {
        const string content = """
            namespace RealNs;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public RealNs.Status Read(object[] values)
                {
                    if (values.Any(value => value is Holder RealNs))
                    {
                        return RealNs.Status.Ready;
                    }

                    return RealNs.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ToList();

        Assert.Equal([19, 22], readyRefs.Select(reference => reference.Line).ToArray());
        Assert.All(readyRefs, reference => Assert.Equal("Read", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithNestedLambdaScopedDeclarationPatternVariable_DoesNotLeakIntoOuterIfBody()
    {
        const string content = """
            namespace RealNs;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public RealNs.Status Read(object[] values)
                {
                    if (values.Any(value => value is Holder RealNs && values.Any(other => other is Holder Other)))
                    {
                        return RealNs.Status.Ready;
                    }

                    return RealNs.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ToList();

        Assert.Equal([19, 22], readyRefs.Select(reference => reference.Line).ToArray());
        Assert.All(readyRefs, reference => Assert.Equal("Read", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSwitchCaseDeclarationPatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    switch (value)
                    {
                        case Holder Status:
                            _ = Status.Ready;
                            break;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(24, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithConditionalExpressionDeclarationPatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    return value is Holder Status
                        ? (Demo.Status)Status.Ready
                        : Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(19, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithRecursivePatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    if (value is Holder { Ready: > 0 } Status)
                    {
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(22, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithMultiLineRecursivePatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    if (value is Holder
                        {
                            Ready: > 0
                        } Status)
                    {
                        _ = Status.Ready;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(25, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithRecursivePatternCaseVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    switch (value)
                    {
                        case Holder { Ready: > 0 } Status:
                            _ = Status.Ready;
                            break;
                    }

                    return Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(24, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSwitchExpressionRecursivePatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    return value switch
                    {
                        Holder { Ready: > 0 } Status => (Demo.Status)Status.Ready,
                        _ => Demo.Status.Ready
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(20, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSwitchExpressionDeclarationPatternVariable_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    return value switch
                    {
                        Holder Status => (Demo.Status)Status.Ready,
                        _ => Demo.Status.Ready
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(20, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSwitchExpressionDeclarationPatternWhenGuard_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    return value switch
                    {
                        Holder Status when Status.Ready > 0 => Demo.Status.Ready,
                        _ => Demo.Status.Ready
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ThenBy(reference => reference.Column)
            .ToList();

        Assert.Equal([19, 20], readyRefs.Select(reference => reference.Line).ToArray());
        Assert.Equal([64, 30], readyRefs.Select(reference => reference.Column).ToArray());
        Assert.All(readyRefs, reference => Assert.Equal("Read", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSwitchExpressionDeclarationPatternWhenInComment_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    return value switch
                    {
                        Holder Status /* when comment */ => (Demo.Status)Status.Ready,
                        _ => Demo.Status.Ready
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(20, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithSwitchExpressionDeclarationPatternWhenInMultiLineComment_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public Demo.Status Read(object value)
                {
                    return value switch
                    {
                        Holder /* trivia
                                  when comment */ Status when Status.Ready > 0 => Demo.Status.Ready,
                        _ => Demo.Status.Ready
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ThenBy(reference => reference.Column)
            .ToList();

        Assert.Equal([20, 21], readyRefs.Select(reference => reference.Line).ToArray());
        Assert.Equal([83, 30], readyRefs.Select(reference => reference.Column).ToArray());
        Assert.All(readyRefs, reference => Assert.Equal("Read", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithStaticLambdaScopedDeclarationPatternVariable_DoesNotLeakIntoOuterIfBody()
    {
        const string content = """
            namespace RealNs;

            public enum Status
            {
                Ready
            }

            public sealed class Holder
            {
                public int Ready { get; set; }
            }

            public sealed class Uses
            {
                public RealNs.Status Read(object[] values)
                {
                    if (values.Any(static value => value is Holder RealNs))
                    {
                        return RealNs.Status.Ready;
                    }

                    return RealNs.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references
            .Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call")
            .OrderBy(reference => reference.Line)
            .ToList();

        Assert.Equal([19, 22], readyRefs.Select(reference => reference.Line).ToArray());
        Assert.All(readyRefs, reference => Assert.Equal("Read", reference.ContainerName));
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithDottedValueReceiverChain_DoesNotLeakAsEnumMemberReference()
    {
        const string content = """
            namespace RealNs;

            public enum Status
            {
                Ready
            }

            namespace Test;

            public sealed class ReadyHolder
            {
                public int Ready { get; set; }
            }

            public sealed class NamespaceLike
            {
                public ReadyHolder Status { get; } = new();
            }

            public sealed class Uses
            {
                public global::RealNs.Status Read(NamespaceLike RealNs)
                {
                    _ = RealNs.Status.Ready;
                    return global::RealNs.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var readyRefs = references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call").ToList();
        var readyRef = Assert.Single(readyRefs);
        Assert.Equal(25, readyRef.Line);
        Assert.Equal("Read", readyRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpQualifiedEnumMemberAccess_WithGlobalQualifierAndConflictingType_PreservesReference()
    {
        const string content = """
            namespace Demo;

            public enum Status
            {
                Ready
            }

            namespace Other;

            public static class Status
            {
                public static int Value = 1;
            }

            public class Uses
            {
                public Demo.Status Read()
                {
                    return global::Demo.Status.Ready;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var ready = Assert.Single(references.Where(reference => reference.SymbolName == "Ready" && reference.ReferenceKind == "call"));
        Assert.Equal(19, ready.Line);
        Assert.Equal("Read", ready.ContainerName);
    }

    [Fact]
    public void Extract_CsharpNestedGenericConstructorAndMethodCalls_AreIndexed()
    {
        // Regression (issue #263): nested generic tails such as `>>(` previously broke the
        // flat CallRegex generic segment, so constructor calls like
        // `new Dictionary<string, List<int>>()` and generic method calls like
        // `Helper.DoWork<List<int>>()` were silently dropped from the reference index.
        // リグレッション (issue #263): `>>(` を含む nested generic 呼び出しは平坦な
        // CallRegex の generic segment が壊れ、`new Dictionary<string, List<int>>()` や
        // `Helper.DoWork<List<int>>()` が reference index から黙って脱落していた。
        const string content = """
            using System.Collections.Generic;

            namespace Demo;

            public static class Helper
            {
                public static void DoWork<T>() { }
                public static void Process<T>() { }
            }

            public class Builder
            {
                public void Build()
                {
                    var a = new Dictionary<string, List<int>>();
                    var b = new List<Dictionary<string, int>>();
                    var c = new Dictionary<int, Dictionary<string, List<int>>>();
                    Helper.DoWork<List<int>>();
                    Helper.Process<Dictionary<string, int>>();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "instantiate" && r.Line == 15);
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "instantiate" && r.Line == 16);
        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "instantiate" && r.Line == 17);
        Assert.Contains(references, r => r.SymbolName == "DoWork" && r.ReferenceKind == "call" && r.Line == 18);
        Assert.Contains(references, r => r.SymbolName == "Process" && r.ReferenceKind == "call" && r.Line == 19);
        Assert.DoesNotContain(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "List" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpGenericInvocationTypeArguments_AreTypeReferences()
    {
        const string content = """
            using System.Collections.Generic;

            namespace Demo;

            public sealed class Payload { }
            public sealed class Result { }

            public static class Helper
            {
                public static void DoWork<T>() { }
            }

            public class Builder
            {
                public void Build()
                {
                    var a = new List<Payload>();
                    Helper.DoWork<List<Result>>();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference" && r.Line == 17);
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "type_reference" && r.Line == 18);
        Assert.Contains(references, r => r.SymbolName == "Result" && r.ReferenceKind == "type_reference" && r.Line == 18);
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference" && r.Line == 10);
    }

    [Fact]
    public void Extract_CsharpNestedGenericParenlessInitializers_AreInstantiate()
    {
        // Regression follow-up for issue #263: nested generic parenless initializers such as
        // `new Dictionary<string, List<int>> { ... }` and Allman-style `new Dictionary<...>\n{`
        // must keep the outer instantiate edge instead of indexing only the inner flat ctor calls.
        // issue #263 の追補: `new Dictionary<string, List<int>> { ... }` や
        // Allman 形式の `new Dictionary<...>\n{` でも、内側の平坦な ctor 呼び出しだけでなく
        // 外側型の instantiate edge を維持しなければならない。
        const string content = """
            using System.Collections.Generic;

            namespace Demo;

            public class Builder
            {
                public void Build()
                {
                    var a = new Dictionary<string, List<int>> { ["k"] = new List<int>() };
                    var b = new List<Dictionary<string, int>> { new Dictionary<string, int>() };
                    var c = new Dictionary<int, List<int>>
                    {
                        [1] = new List<int>()
                    };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "instantiate" && r.Line == 9);
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "instantiate" && r.Line == 10);
        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "instantiate" && r.Line == 11);
        Assert.DoesNotContain(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "List" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpParenlessInitializers_AreInstantiate()
    {
        // issue #286: object/collection/dictionary/array initializer syntax without `()`
        // (`new Foo { ... }`, `new List<int> { ... }`, `new Dictionary<K,V> { [k] = v }`,
        // `new Bar[] { ... }`) must be recorded as `instantiate` references.
        // issue #286: 括弧省略のオブジェクト/コレクション/ディクショナリ/配列イニシャライザも
        // `instantiate` として参照テーブルに記録される必要がある。
        const string content = """
            namespace App;

            using System.Collections.Generic;

            public class Bar { public int X { get; set; } }

            public class Worker
            {
                public void M()
                {
                    var a = new Bar { X = 1 };
                    var list = new List<int> { 1, 2, 3 };
                    var dict = new Dictionary<string, int> { ["k"] = 1 };
                    var arr = new int[] { 1, 2, 3 };
                    var arr2 = new Bar[] { new Bar() { X = 9 } };
                    if (true) { }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Bar" && r.ReferenceKind == "instantiate" && r.Line == 11);
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "instantiate" && r.Line == 12);
        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "instantiate" && r.Line == 13);
        Assert.Contains(references, r => r.SymbolName == "Bar" && r.ReferenceKind == "instantiate" && r.Line == 15 && r.Column == 24);
        Assert.Contains(references, r => r.SymbolName == "Bar" && r.ReferenceKind == "instantiate" && r.Line == 15 && r.Column == 36);
        // Built-in `int` must not produce an `instantiate int` row from `new int[] { ... }`.
        // 組み込み型 `int` は `instantiate int` を発行しない。
        Assert.DoesNotContain(references, r => r.SymbolName == "int");
        // The negative `if (true) { }` line must not match the initializer regex.
        // `if (true) { }` のような `new` を含まない `{` 開始は initializer regex にマッチしない。
        Assert.DoesNotContain(references, r => r.SymbolName == "if" && r.ReferenceKind == "instantiate");
        // Same target on the same line/column must not double-emit instantiate + call.
        // 同一行・同一列で `instantiate` と `call` を二重に出さない。
        Assert.DoesNotContain(references, r => r.SymbolName == "Bar" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "List" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedParenlessInitializer_IsInstantiate()
    {
        // Qualified type names (`new N.Foo { ... }`, `new global::N.Foo { ... }`) must
        // capture the trailing identifier as `instantiate`, mirroring the behavior of
        // `new N.Foo()` which the existing CallRegex+IsConstructorCallName path already covers.
        // 修飾された型名（`new N.Foo { ... }` / `new global::N.Foo { ... }`）でも、
        // `new N.Foo()` と同様に末尾の識別子が `instantiate` として捕捉されること。
        const string content = """
            namespace N
            {
                public class Foo { public int X { get; set; } }
                public class Bar { public int X { get; set; } }
            }

            public class Worker
            {
                public void M()
                {
                    var a = new N.Foo { X = 1 };
                    var b = new global::N.Bar { X = 2 };
                    var c = new global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<int>> { ["k"] = new global::System.Collections.Generic.List<int>() };
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Foo" && r.ReferenceKind == "instantiate" && r.Line == 11);
        Assert.Contains(references, r => r.SymbolName == "Bar" && r.ReferenceKind == "instantiate" && r.Line == 12);
        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "instantiate" && r.Line == 13);
    }

    [Fact]
    public void Extract_CsharpAllmanParenlessInitializers_AreInstantiate()
    {
        // issue #286 (multi-line): the common Allman-style form places `{` on the next
        // physical line. The same-line regex cannot see that `{`, so add a trailing-shape
        // path that matches `new T` at end of line and peeks forward to a `{`-starting line.
        // issue #286 の多行形式: Allman スタイルでは `{` が次行にあるため、行末の `new T` を
        // 末尾マッチ regex で拾い、次の非空 prepared line が `{` で始まる時だけ `instantiate` を発行する。
        const string content = """
            namespace App;

            using System.Collections.Generic;

            public class Foo { public int X { get; set; } }
            public class Bag { public List<Foo> Items { get; set; } = new(); }

            public static class Helper
            {
                public static Bag BuildBagAllman()
                {
                    return new Bag
                    {
                        Items = new List<Foo>
                        {
                            new Foo
                            {
                                X = 6
                            }
                        }
                    };
                }

                public static Foo[] BuildArrayAllman()
                {
                    return new Foo[]
                    {
                        new Foo { X = 4 }
                    };
                }

                public static Foo NotAnInstantiate()
                {
                    // `new Foo` here is not followed by `{` — it is a compile error in
                    // real code, but the extractor must not emit a phantom instantiate.
                    // This exercises the peek-ahead negative path.
                    var f = new Foo
                    ;
                    return f;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        // new Bag (line 12) with `{` on line 13
        Assert.Contains(references, r => r.SymbolName == "Bag" && r.ReferenceKind == "instantiate" && r.Line == 12);
        // new List<Foo> (line 14) with `{` on line 15
        Assert.Contains(references, r => r.SymbolName == "List" && r.ReferenceKind == "instantiate" && r.Line == 14);
        // new Foo (line 16) with `{` on line 17
        Assert.Contains(references, r => r.SymbolName == "Foo" && r.ReferenceKind == "instantiate" && r.Line == 16);
        // new Foo[] (line 26) with `{` on line 27
        Assert.Contains(references, r => r.SymbolName == "Foo" && r.ReferenceKind == "instantiate" && r.Line == 26);
        // The negative `var f = new Foo` (line 37) followed by `;` (line 38) must NOT
        // emit an instantiate at that line via the trailing peek path.
        // ネガティブ: `var f = new Foo` (行 37) の次行は `;` なので trailing peek は発行しない。
        Assert.DoesNotContain(references, r => r.SymbolName == "Foo" && r.ReferenceKind == "instantiate" && r.Line == 37);
    }

    [Fact]
    public void Extract_RazorBlazor_CapturesComponentsDirectivesInjectionAndEventHandlers()
    {
        const string content = """
            @inherits App.Pages.BasePage
            @implements App.Pages.IUserActions
            @attribute [Authorize]
            @inject Services.UserService UserService

            <UserCard User="CurrentUser" />
            <Shared.DetailPanel />
            <MyApp.Components.Forms.LoginButton OnClick="HandleClick" />
            <button @onclick="HandleClick">Save</button>
            <button @onclick="@HandleClick">Save explicit</button>
            <button @onclick="InheritedClick">Inherited</button>
            <input @ref="inputRef" @key="person" @bind="Value" />
            @* <AdminPanel /> *@
            <!-- <AuditPanel /> -->

            @code {
                var sample = "<CodeStringPanel />";
                List<CodeGenericPanel> panels = new();
                // <CodeCommentPanel />
                void HandleClick() { UserService.Save(); }
            }

            @if (ShowInline)
            {
                var inline = "<InlineCodePanel />";
            }
            else
            {
                var fallback = "<ElseCodePanel />";
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content, "Pages/User.razor");
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols, "Pages/User.razor");
        var aliasReferences = ReferenceExtractor.Extract(1, "razor", content, symbols);
        var qualifiedComponentColumn = content
            .Split('\n')
            .Single(line => line.Contains("Shared.DetailPanel", StringComparison.Ordinal))
            .IndexOf("Shared.DetailPanel", StringComparison.Ordinal) + 1;
        var nestedComponentColumn = content
            .Split('\n')
            .Single(line => line.Contains("MyApp.Components.Forms.LoginButton", StringComparison.Ordinal))
            .IndexOf("MyApp.Components.Forms.LoginButton", StringComparison.Ordinal) + 1;

        Assert.Contains(references, r => r.SymbolName == "BasePage" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IUserActions" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Authorize" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "UserService" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "UserCard" && r.ReferenceKind == "call");
        Assert.Contains(references, r => r.SymbolName == "Shared.DetailPanel" && r.ReferenceKind == "call" && r.Column == qualifiedComponentColumn);
        Assert.Contains(references, r => r.SymbolName == "MyApp.Components.Forms.LoginButton" && r.ReferenceKind == "call" && r.Column == nestedComponentColumn);
        Assert.Contains(references, r => r.SymbolName == "HandleClick" && r.ReferenceKind == "razor_event_binding");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "HandleClick"
            && r.ReferenceKind == "implicit_implementation");
        Assert.Contains(references, r =>
            r.SymbolName == "InheritedClick"
            && r.ReferenceKind == "razor_event_binding");
        Assert.Contains(references, r =>
            r.SymbolName == "InheritedClick"
            && r.ReferenceKind == "implicit_implementation"
            && r.ContainerKind == "interface"
            && r.ContainerName == "IUserActions");
        Assert.Contains(references, r => r.SymbolName == "Save" && r.ReferenceKind == "call");
        Assert.Contains(aliasReferences, r => r.SymbolName == "UserCard" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "AdminPanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "AuditPanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "CodeStringPanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "CodeGenericPanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "CodeCommentPanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "InlineCodePanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "ElseCodePanel" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "inputRef" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "person" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "Value" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpSwitchExpressionLaterGenericArmAfterRelationalPattern_StillEmitsTypeHead()
    {
        // issue #852: relational `<` in a previous recursive pattern must not hide a later generic arm.
        // issue #852: 先行 recursive pattern の relational `<` で後続 generic arm を隠してはならない。
        const string content = """
            namespace Probe;

            class Point { public int X { get; init; } }
            class Shape {}
            class Wrapper<TLeft, TRight> {}

            class Demo
            {
                int Match(object value) => value switch
                {
                    Point { X: < 0 } => 1,
                    Wrapper<Point, Shape> => 2,
                    _ => 0,
                };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Wrapper" && r.ReferenceKind == "type_reference" && r.ContainerName == "Match");
        Assert.Equal(2, references.Count(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference" && r.ContainerName == "Match"));
        Assert.Contains(references, r => r.SymbolName == "Shape" && r.ReferenceKind == "type_reference" && r.ContainerName == "Match");
    }

    [Fact]
    public void Extract_CsharpVerbatimPatternTypeNames_DoNotCollapseIntoBarePatternTokens()
    {
        // issue #677: `@not` / `@default` are legal type names, so the non-type pattern
        // filter must not erase them just because their normalized spellings match keyword-like tokens.
        // issue #677: `@not` / `@default` は合法な型名なので、normalized 後に keyword 風 token
        // と一致しても non-type pattern filter で消してはいけない。
        const string content = """
            namespace Probe;

            class @not {}
            class @default {}

            class Demo
            {
                bool MatchNot(object value) => value is @not;
                bool MatchDefault(object value) => value is @default;
                bool Guard(object value) => value is not null;
                bool TypeOfNot() => typeof(@not) == typeof(@not);
                bool TypeOfDefault() => typeof(@default) == typeof(@default);

                void Run(object value)
                {
                    switch (value)
                    {
                        case @not:
                            break;
                        case @default:
                            break;
                        case default:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var notRefs = references.Where(r => r.SymbolName == "not" && r.ReferenceKind == "type_reference").ToList();
        var defaultRefs = references.Where(r => r.SymbolName == "default" && r.ReferenceKind == "type_reference").ToList();

        Assert.Equal(4, notRefs.Count);
        Assert.Equal(4, defaultRefs.Count);
        Assert.Contains(notRefs, r => r.ContainerName == "MatchNot");
        Assert.Contains(notRefs, r => r.ContainerName == "Run");
        Assert.Equal(2, notRefs.Count(r => r.ContainerName == "TypeOfNot"));
        Assert.Contains(defaultRefs, r => r.ContainerName == "MatchDefault");
        Assert.Contains(defaultRefs, r => r.ContainerName == "Run");
        Assert.Equal(2, defaultRefs.Count(r => r.ContainerName == "TypeOfDefault"));
        Assert.DoesNotContain(references, r => r.ReferenceKind == "type_reference" && r.ContainerName == "Guard");
    }

    [Fact]
    public void Extract_CsharpQualifiedConstantPatterns_DoNotEmitTypeReferences()
    {
        const string content = """
            namespace Probe;

            enum Color { Red, Blue }
            class Point {}

            class Demo
            {
                bool Match(object value) => value is Color.Red or Color.Blue or Point;

                void Run(object value)
                {
                    switch (value)
                    {
                        case Color.Red:
                            break;
                        case Color.Red or Color.Blue:
                            break;
                        case Color.Red or Point:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Blue" && r.ReferenceKind == "type_reference");

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, pointRefs.Count);
        Assert.Contains(pointRefs, r => r.ContainerName == "Match");
        Assert.Contains(pointRefs, r => r.ContainerName == "Run");
    }

    [Fact]
    public void Extract_CsharpUsingStaticLogicalConstantPatterns_KeepAmbiguousHeadsForReadTimeFiltering()
    {
        const string content = """
            using static Probe.Color;

            namespace Probe;

            enum Color { Red, Blue }
            class Point {}

            class Demo
            {
                bool Match(object value) => value is Red or Blue or Point;

                void Run(object value)
                {
                    switch (value)
                    {
                        case Red:
                            break;
                        case Red or Blue:
                            break;
                        case Red or Point:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRefs = references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference").ToList();
        var blueRefs = references.Where(r => r.SymbolName == "Blue" && r.ReferenceKind == "type_reference").ToList();
        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();

        Assert.Equal(4, redRefs.Count);
        Assert.Equal(2, blueRefs.Count);
        Assert.Equal(2, pointRefs.Count);
        Assert.Contains(redRefs, r => r.ContainerName == "Match");
        Assert.Equal(3, redRefs.Count(r => r.ContainerName == "Run"));
        Assert.Contains(blueRefs, r => r.ContainerName == "Match");
        Assert.Contains(blueRefs, r => r.ContainerName == "Run");
        Assert.Contains(pointRefs, r => r.ContainerName == "Match");
        Assert.Contains(pointRefs, r => r.ContainerName == "Run");
    }

    [Fact]
    public void Extract_CsharpUsingStaticMultiLineLogicalConstantPatterns_KeepTypeReferences()
    {
        // issue #779: the multi-line form `value is` + later-line `Red` / `or Red` should keep
        // the same ambiguous constant-pattern references as the single-line form. Phantom
        // `property Red` symbols from SymbolExtractor previously suppressed these rows entirely.
        // issue #779: `value is` の後続行に `Red` / `or Red` が来る複数行形でも、
        // 単一行版と同じあいまい constant-pattern 参照を保持しなければならない。以前は
        // SymbolExtractor 側の phantom `property Red` がこの参照行を丸ごと抑止していた。
        const string content = """
            using static Probe.Color;

            namespace Probe;

            enum Color { Red, Blue }

            class Demo
            {
                bool Match(object value) => value is
                    Red
                    or
                    Red;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Red" && s.ContainerName == "Demo");

        var redRefs = references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, redRefs.Count);
        Assert.All(redRefs, reference => Assert.Equal("Match", reference.ContainerName));
        Assert.Equal([10, 12], redRefs.Select(reference => reference.Line).ToArray());
    }

    [Fact]
    public void Extract_CsharpMultiLineCasePatterns_KeepFirstAndLaterTypeHeads()
    {
        // issues #843 / #747: `case` labels must keep both a first head that moves to the next
        // line and later logical heads that continue on following lines.
        // issues #843 / #747: `case` ラベルは、次行へ移る first head と、後続行へ続く logical head
        // の両方を維持しなければならない。
        const string content = """
            namespace Probe;

            class Point {}
            class Shape {}

            class Demo
            {
                void Run(object value)
                {
                    switch (value)
                    {
                        case
                            Point:
                            break;
                        case Point or
                            Shape:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        var shapeRefs = references.Where(r => r.SymbolName == "Shape" && r.ReferenceKind == "type_reference").ToList();

        Assert.Equal(2, pointRefs.Count);
        Assert.Single(shapeRefs);
        Assert.All(pointRefs, reference => Assert.Equal("Run", reference.ContainerName));
        Assert.Equal("Run", shapeRefs[0].ContainerName);
        Assert.Equal([13, 15], pointRefs.Select(reference => reference.Line).OrderBy(line => line).ToArray());
        Assert.Equal(16, shapeRefs[0].Line);
    }

    [Fact]
    public void Extract_CsharpCommentSeparatedMultiLineTypePatterns_KeepPendingHeads()
    {
        // issue #850: comment-only lines are structurally masked to trivia, so the pending
        // multiline type-pattern state must not flush before the real type head arrives.
        // issue #850: comment-only 行は構造マスク後に trivia 扱いになるため、複数行
        // type-pattern の pending state を実際の型 head より先に flush してはならない。
        const string content = """
            namespace Probe;

            class Point {}

            class Demo
            {
                bool Match(object value) => value is
                    // formatting-only comment
                    Point;

                void Run(object value)
                {
                    switch (value)
                    {
                        case
                            // formatting-only comment
                            Point:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();

        Assert.Equal(2, pointRefs.Count);
        Assert.Equal(["Match", "Run"], pointRefs.Select(reference => reference.ContainerName).OrderBy(name => name).ToArray());
        Assert.Equal([9, 17], pointRefs.Select(reference => reference.Line).OrderBy(line => line).ToArray());
    }

    [Fact]
    public void Extract_CsharpStandaloneNotLineMultiLineTypePatterns_KeepPendingHeads()
    {
        // issue #891: a standalone `not` continuation line is still valid C# trivia-separated
        // formatting, so the pending multiline type-pattern state must survive until the head.
        // issue #891: 単独行の `not` 継続も有効な C# フォーマットであるため、複数行
        // type-pattern の pending state は実際の型 head まで維持しなければならない。
        const string content = """
            namespace Probe;

            class Point {}

            class Demo
            {
                bool Match(object value) => value is
                    not
                    Point;

                void Run(object value)
                {
                    switch (value)
                    {
                        case
                            not
                            Point:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();

        Assert.Equal(2, pointRefs.Count);
        Assert.Equal(["Match", "Run"], pointRefs.Select(reference => reference.ContainerName).OrderBy(name => name).ToArray());
        Assert.Equal([9, 17], pointRefs.Select(reference => reference.Line).OrderBy(line => line).ToArray());
    }

    [Fact]
    public void Extract_CsharpNonTypeCaseLabels_DoNotArmMultiLineTypeCarry()
    {
        // issue #857: relational/non-type `case` labels like `case > 0:` must not arm the
        // multiline type-pattern carry or the next-line call token becomes a phantom type reference.
        // issue #857: `case > 0:` のような非型 `case` ラベルで複数行 type-pattern carry を
        // armed にしてしまうと、次行の call token が phantom type_reference になってしまう。
        const string content = """
            namespace Probe;

            class Demo
            {
                void Run(int value)
                {
                    switch (value)
                    {
                        case > 0:
                            Target();
                            break;
                    }
                }

                void Target() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Target"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Run");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Target"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpUsingStaticMultiLineCaseLogicalConstantPatterns_KeepAmbiguousHeads()
    {
        // issue #843: multi-line `case` labels should keep the same ambiguous using-static
        // constant heads that the single-line form leaves for read-time filtering.
        // issue #843: 複数行 `case` ラベルでも、単一行版と同じ using-static の曖昧な constant head
        // を read path の判定用に残す必要がある。
        const string content = """
            using static Probe.Color;

            namespace Probe;

            enum Color { Red, Blue }

            class Demo
            {
                void Run(object value)
                {
                    switch (value)
                    {
                        case
                            Red
                            or
                            Red:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRefs = references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, redRefs.Count);
        Assert.All(redRefs, reference => Assert.Equal("Run", reference.ContainerName));
        Assert.Equal([14, 16], redRefs.Select(reference => reference.Line).OrderBy(line => line).ToArray());
    }

    [Fact]
    public void Extract_CsharpCommentSeparatedMultiLineUsingStaticCaseConstantPatterns_KeepAmbiguousHeads()
    {
        // issue #850: comment-only lines between `case` and an imported constant head must not
        // drop the ambiguous row that the read path later suppresses or keeps.
        // issue #850: `case` と import 済み constant head の間に comment-only 行があっても、
        // read path が後で抑止/維持する曖昧 row を落としてはならない。
        const string content = """
            using static Probe.Color;

            namespace Probe;

            enum Color { Red }

            class Demo
            {
                void Run(object value)
                {
                    switch (value)
                    {
                        case
                            // formatting-only comment
                            Red
                            or
                            Red:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRefs = references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference").ToList();

        Assert.Equal(2, redRefs.Count);
        Assert.All(redRefs, reference => Assert.Equal("Run", reference.ContainerName));
        Assert.Equal([15, 17], redRefs.Select(reference => reference.Line).OrderBy(line => line).ToArray());
    }

    [Fact]
    public void Extract_CsharpQualifiedMultiLineCaseLogicalConstantPatterns_StaySuppressed()
    {
        // issue #747 follow-up control: extending `case` logical-pattern carry across lines must
        // not reintroduce phantom qualified constant/member type references.
        // issue #747 の対照ケース: `case` の logical-pattern carry を複数行へ広げても、
        // 修飾済み constant/member の phantom type_reference を復活させてはいけない。
        const string content = """
            namespace Probe;

            enum Color { Red, Blue }

            class Demo
            {
                void Run(object value)
                {
                    switch (value)
                    {
                        case Color.Red or
                            Color.Blue:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Blue" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpUsingStaticTypeAliasPattern_KeepsTypeReference()
    {
        const string content = """
            using static Probe.Color;
            using Red = Probe.Real.Red;

            namespace Probe
            {
                enum Color { Red }

                namespace Real
                {
                    class Red {}
                }

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        var redRef = Assert.Single(references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference"));

        Assert.Equal("Match", redRef.ContainerName);
        Assert.Contains("value is Red", redRef.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CsharpUsingStaticNestedSameNameTypePattern_KeepsTypeReference()
    {
        const string content = """
            using static Probe.Color;

            namespace Probe;

            enum Color
            {
                Red
            }

            class Outer
            {
                class Red {}

                bool Match(object value) => value is Red;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        var redRef = Assert.Single(references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference"));

        Assert.Equal("Match", redRef.ContainerName);
        Assert.Contains("value is Red", redRef.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CsharpUsingStaticTopLevelSameNameTypePattern_KeepsTypeReferences()
    {
        const string content = """
            using static Probe.Color;

            namespace Probe;

            enum Color
            {
                Red
            }

            public class Red {}

            class Demo
            {
                bool Match(object value) => value is Red;

                bool Switch(object value) => value switch
                {
                    Red => true,
                    _ => false,
                };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var redRefs = references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, redRefs.Count);
        Assert.Contains(redRefs, reference => reference.ContainerName == "Match");
        Assert.Contains(redRefs, reference => reference.ContainerName == "Switch");
    }

    [Fact]
    public void Extract_CsharpUsingStaticNamespaceImportPattern_KeepsTypeReference()
    {
        const string content = """
            using static Probe.Color;
            using RealTypes;

            namespace Probe
            {
                enum Color { Red }

                class Demo
                {
                    bool Match(object value) => value is Red;
                }
            }

            namespace RealTypes
            {
                class Red {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        var redRef = Assert.Single(references.Where(r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference"));

        Assert.Equal("Match", redRef.ContainerName);
        Assert.Contains("value is Red", redRef.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_CsharpCaseRecursiveAndPositionalPatterns_CaptureTypeReferences()
    {
        // issue #661: recursive/property and positional `case Type ...` patterns without
        // a designation are still real type-pattern sites and must keep `type_reference`.
        // issue #661: designation を持たない recursive/property / positional の
        // `case Type ...` パターンも本物の型パターンなので `type_reference` を残す。
        const string content = """
            namespace Probe;

            class Point
            {
                public int X { get; }
                public int Y { get; }
            }

            class Demo
            {
                void Run(object value)
                {
                    if (value is Point(var x, var y))
                    {
                    }

                    switch (value)
                    {
                        case Point { X: 0, Y: 0 }:
                            break;
                        case Point(var x, var y):
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(3, pointRefs.Count);
        Assert.All(pointRefs, r => Assert.Equal("Run", r.ContainerName));
        Assert.DoesNotContain(references, r => r.SymbolName == "Point" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpMultilinePositionalPatterns_CaptureTypeReferences()
    {
        // issue #969: multiline positional `case` / `is` heads must behave the same as
        // the same-line forms and keep the real `type_reference` without phantom calls.
        // issue #969: 改行をまたぐ positional `case` / `is` head も同一行版と同様に
        // 本物の `type_reference` を残し、phantom な call を出してはならない。
        const string content = """
            namespace Probe;

            class Point
            {
                public int X { get; }
                public int Y { get; }
            }

            class Demo
            {
                void Run(object value)
                {
                    if (value is
                        Point(var x, var y))
                    {
                    }

                    switch (value)
                    {
                        case
                            Point(var x, var y):
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, pointRefs.Count);
        Assert.All(pointRefs, r => Assert.Equal("Run", r.ContainerName));
        Assert.DoesNotContain(references, r => r.SymbolName == "Point" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpMultilinePositionalPatternWhenGuard_PreservesGuardCall()
    {
        // issue #986: a real `when`-guard call after a split positional pattern must stay
        // visible as `call` instead of being suppressed as part of the pattern head.
        // issue #986: 分割された positional pattern の後ろにある実際の `when` guard 呼び出しは、
        // pattern head の一部として抑止されず `call` として残る必要がある。
        const string content = """
            namespace Probe;

            class Point
            {
                public int X { get; }
                public int Y { get; }
            }

            class Demo
            {
                void Check() { }

                void Run(object value)
                {
                    switch (value)
                    {
                        case
                            Point(var x, var y) when Check():
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        Assert.Single(pointRefs);
        Assert.Equal("Run", pointRefs[0].ContainerName);
        Assert.Contains(references, r => r.SymbolName == "Check" && r.ReferenceKind == "call" && r.ContainerName == "Run");
    }

    [Fact]
    public void Extract_CsharpSwitchExpressionPositionalPatterns_DoNotEmitPhantomCalls()
    {
        // issue #968: switch-expression arm heads such as `Point(var x, var y) => 1`
        // are pattern heads, not calls, so they must keep the real type reference only.
        // issue #968: `Point(var x, var y) => 1` のような switch 式 arm head は
        // pattern head であり call ではないため、本物の type reference だけを残す。
        const string content = """
            namespace Probe;

            class Point
            {
                public int X { get; }
                public int Y { get; }
            }

            class Demo
            {
                int Match(object value) => value switch
                {
                    Point(var x, var y) => 1,
                    Point(var c, var d)
                        => 2,
                    _ => 0,
                };
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(2, pointRefs.Count);
        Assert.All(pointRefs, r => Assert.Equal("Match", r.ContainerName));
        Assert.DoesNotContain(references, r => r.SymbolName == "Point" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpCaseLogicalAndNegatedTypePatterns_CaptureTypeReferences()
    {
        // issues #668/#670: logical/negated type patterns must keep the left-hand type
        // dependency for both unqualified and qualified heads without reclassifying enum
        // member labels such as `Color.Red or Probe.Color.Blue` as type dependencies.
        // issues #668/#670: logical/negated な型パターンは unqualified / qualified の両方で
        // 左端の型依存を残しつつ、`Color.Red or Probe.Color.Blue` のような enum member label を
        // 型依存へ再分類してはならない。
        const string content = """
            namespace Probe;

            class Point {}
            enum Color { Red, Blue }

            class Demo
            {
                void Run(object value)
                {
                    switch (value)
                    {
                        case Point or null:
                            break;
                        case not Point:
                            break;
                        case Probe.Point or null:
                            break;
                        case not Probe.Point:
                            break;
                        case global::Probe.Point or null:
                            break;
                        case Color.Red or Probe.Color.Blue:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var pointRefs = references.Where(r => r.SymbolName == "Point" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(5, pointRefs.Count);
        Assert.All(pointRefs, r => Assert.Equal("Run", r.ContainerName));
        Assert.DoesNotContain(references, r => r.SymbolName == "null" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Color" && r.ReferenceKind == "type_reference" && r.ContainerName == "Run");
        Assert.DoesNotContain(references, r => r.SymbolName == "Red" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "Blue" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpCaseVerbatimKeywordLikeDesignations_KeepTypeReferences()
    {
        // issue #669: verbatim designators such as `@or` / `@when` / `@and` are identifiers,
        // not control keywords, so the enclosing type pattern must remain visible.
        // issue #669: `@or` / `@when` / `@and` のような verbatim designator は識別子であり、
        // control keyword ではないため enclosing type pattern を落としてはならない。
        const string content = """
            namespace Probe;

            class Foo {}

            class Demo
            {
                void Run(object value)
                {
                    switch (value)
                    {
                        case Foo @or:
                            break;
                        case Foo @when:
                            break;
                        case Foo @and:
                            break;
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var fooRefs = references.Where(r => r.SymbolName == "Foo" && r.ReferenceKind == "type_reference").ToList();
        Assert.Equal(3, fooRefs.Count);
        Assert.All(fooRefs, r => Assert.Equal("Run", r.ContainerName));
    }

    [Fact]
    public void Extract_CsharpWhereConstraintKeywords_DoNotBecomeTypeReferences()
    {
        const string content = """
            interface IContract {}
            class Demo<TValue, TKey, TBuffer, TDefault>
                where TValue : unmanaged, IContract
                where TKey : notnull
                where TBuffer : IContract, allows ref struct
                where TDefault : default
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "IContract" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => (r.SymbolName is "allows" or "default" or "ref" or "unmanaged" or "notnull") && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpMultiLineWhereConstraints_CapturesConstraintTypeReferences()
    {
        const string content = """
            using System.Collections.Generic;

            interface IContract<T> {}
            interface IAuditable {}
            namespace Domain.Models { class Entity {} }

            class Demo<
                TValue,
                TKey>
                where TValue :
                    global::Domain.Models.Entity,
                    IContract<TKey>,
                    IAuditable,
                    new()
                where TKey : notnull
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Entity" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IContract" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IAuditable" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => (r.SymbolName is "TValue" or "TKey" or "notnull" or "new") && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpGenericSignatures_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            interface IContract<T> {}
            class Base<T> {}

            class Demo<TValue> : Base<TValue>, IContract<TValue>
            {
                public TItem Pick<TItem>(TItem value, IContract<TItem> fallback) => value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "Base" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "IContract" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => (r.SymbolName is "TValue" or "TItem") && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpGenericTypeOperators_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            class User {}

            class Demo {
                public static bool Is<T>(object value) => value is T;
                public static bool IsEither<T>(object value) => value is T or User;
                public static User Cast(object value) => value as User;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpGenericTypeKeywords_DoNotEmitTypeParameterReferences()
    {
        const string content = """
            class User {}

            class Demo {
                public static object TypeOf<T>() => typeof(T);
                public static string NameOf<T>() => nameof(T);
                public static object UserType() => typeof(User);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r => r.SymbolName == "User" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "T" && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpShortAndTStyleTypeNames_CaptureTypeReferences()
    {
        // Regression for issue #644: real type names like `X` and `TResult` must not be
        // dropped just because they resemble generic parameter spellings.
        // issue #644 回帰: `X` や `TResult` のような実在型名を、generic parameter に似ている
        // という理由だけで落としてはならない。
        const string content = """
            class X {}
            class TResult {}

            class Demo
            {
                X field;
                X Make(X value)
                {
                    X local = new X();
                    return local;
                }

                TResult Use(TResult value) => value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.True(references.Count(r => r.SymbolName == "X" && r.ReferenceKind == "type_reference") >= 3);
        Assert.True(references.Count(r => r.SymbolName == "TResult" && r.ReferenceKind == "type_reference") >= 2);
    }

    [Fact]
    public void Extract_CsharpDocCref_UsesDocumentedMemberAsContainer()
    {
        const string content = """
            class Base { public void Do() {} }
            interface ILogger { void Log(); }
            class Derived {
                /// <summary>
                /// References <see cref="Base.Do"/> and <seealso cref="ILogger.Log"/>.
                /// </summary>
                public void WithDocs() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols)
            .Where(r => r.Line == 5 && r.ReferenceKind == "type_reference")
            .ToList();

        Assert.Equal(4, references.Count);
        Assert.All(references, r => Assert.Equal("WithDocs", r.ContainerName));
        Assert.Contains(references, r => r.SymbolName == "Base");
        Assert.Contains(references, r => r.SymbolName == "Do");
        Assert.Contains(references, r => r.SymbolName == "ILogger");
        Assert.Contains(references, r => r.SymbolName == "Log");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatPlainCommentsAsDocComments()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                // <see cref="Foo"/>
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 4);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatFourSlashCommentsAsDocComments()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                //// <see cref="Foo"/>
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 4);
    }

    [Fact]
    public void Extract_CsharpDocCref_TreatsDelimitedDocCommentsAsDocComments()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /**
                 * <summary><see cref="Foo"/></summary>
                 */
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols)
            .Where(r => r.Line == 5 && r.ReferenceKind == "type_reference")
            .ToList();

        Assert.Single(references);
        Assert.Equal("Foo", references[0].SymbolName);
        Assert.Equal("Run", references[0].ContainerName);
    }

    [Fact]
    public void Extract_CsharpDocCref_TreatsSameLineDelimitedDocCommentsAsDocComments()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /** <summary><see cref="Foo"/></summary> */ void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols)
            .Where(r => r.Line == 4 && r.ReferenceKind == "type_reference")
            .ToList();

        var fooReference = Assert.Single(references);
        Assert.Equal("Foo", fooReference.SymbolName);
        Assert.Equal("Run", fooReference.ContainerName);
    }

    [Fact]
    public void Extract_CsharpDocCref_TripleSlashKeepsPhysicalLineColumn()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /// <see cref="Foo"/>
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var fooReference = Assert.Single(references.Where(r => r.SymbolName == "Foo" && r.ReferenceKind == "type_reference"));
        var expectedColumn = content.Split('\n')[3].IndexOf("Foo", StringComparison.Ordinal) + 1;
        Assert.Equal(expectedColumn, fooReference.Column);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashInsideMethodBodyAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                void Run()
                {
                    /// <see cref="Foo"/>
                    var x = 1;
                }

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 6);
    }

    [Fact]
    public void Extract_CsharpDocCref_DelimitedDocCommentsKeepPhysicalLineColumn()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /**
                 * <summary><see cref="Foo"/></summary>
                 */
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var fooReference = Assert.Single(references.Where(r =>
            r.SymbolName == "Foo"
            && r.ReferenceKind == "type_reference"
            && r.Line == 5));
        var expectedColumn = content.Split('\n')[4].IndexOf("Foo", StringComparison.Ordinal) + 1;
        Assert.Equal(expectedColumn, fooReference.Column);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedBlockCommentsInsideMethodBodyAsDocComments()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                void Run()
                {
                    /** <see cref="Foo"/> */
                    var x = 1;
                }

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 6);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedDocCommentInsideOrdinaryBlockCommentAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /*
                /** <summary><see cref="Foo"/></summary>
                */
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashInsideOrdinaryBlockCommentAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /*
                /// <summary><see cref="Foo"/></summary>
                */
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashInsideFieldInitializerLambdaAsDocComment()
    {
        const string content = """
            using System;

            class Foo {}
            class Demo
            {
                Action callback = () =>
                {
                    /// <see cref="Foo"/>
                    var x = 1;
                };

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 8);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedBlockCommentsInsideFieldInitializerLambdaAsDocComment()
    {
        const string content = """
            using System;

            class Foo {}
            class Demo
            {
                Action callback = () =>
                {
                    /** <see cref="Foo"/> */
                    var x = 1;
                };

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 8);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashInsideBraceFreeFieldInitializerAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                string text = string.Concat(
                    /// <summary><see cref="Foo"/></summary>
                    "a",
                    "b");

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 5);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedBlockCommentsInsideBraceFreeFieldInitializerAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                string text = string.Concat(
                    /** <summary><see cref="Foo"/></summary> */
                    "a",
                    "b");

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 5);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashInsideBraceFreeExpressionLambdaAsDocComment()
    {
        const string content = """
            using System;
            class Foo {}
            class Demo
            {
                Action callback = () =>
                    /// <summary><see cref="Foo"/></summary>
                    Console.WriteLine(1);

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 6);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedBlockCommentsInsideBraceFreeExpressionLambdaAsDocComment()
    {
        const string content = """
            using System;
            class Foo {}
            class Demo
            {
                Action callback = () =>
                    /** <summary><see cref="Foo"/></summary> */
                    Console.WriteLine(1);

                void Later() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 6);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashInsideMultilineGenericFieldHeaderAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                Dictionary<
                    /// <summary><see cref="Foo"/></summary>
                    string,
                    int> map = new();

                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedDocCommentInsideMultilineGenericFieldHeaderAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                Dictionary<
                    /** <summary><see cref="Foo"/></summary> */
                    string,
                    int> map = new();

                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashBeforeTopLevelStatementAsLaterLocalFunctionDocComment()
    {
        const string content = """
            class Foo {}
            /// <summary><see cref="Foo"/></summary>
            System.Console.WriteLine(1);
            void Later() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatDelimitedBlockCommentBeforeTopLevelStatementAsLaterLocalFunctionDocComment()
    {
        const string content = """
            class Foo {}
            /** <summary><see cref="Foo"/></summary> */
            System.Console.WriteLine(1);
            void Later() {}
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatTripleSlashBeforeTopLevelStatementAsLaterTypeDocComment()
    {
        const string content = """
            class Foo {}
            /// <summary><see cref="Foo"/></summary>
            System.Console.WriteLine(1);
            class Later {}
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatSameLineDelimitedDocCommentBeforeFieldAsLaterMethodDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /** <summary><see cref="Foo"/></summary> */ string text = "";
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 4);
    }

    [Fact]
    public void Extract_CsharpDocCref_TreatsSameLineDelimitedDocCommentBeforeAttributeAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /** <summary><see cref="Foo"/></summary> */ [System.Obsolete]
                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols)
            .Where(r => r.SymbolName == "Foo" && r.ReferenceKind == "type_reference")
            .ToList();

        var fooReference = Assert.Single(references);
        Assert.Equal("Run", fooReference.ContainerName);
        Assert.Equal(4, fooReference.Line);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatCodeAfterDelimitedDocCloseAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                /**
                 * no cref here
                 */ string text = "<see cref=\"Foo\"/>";
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 6);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatRawStringAfterDelimitedDocCloseAsDocComment()
    {
        const string content = """"
            class Foo {}
            class Bar {}
            class Demo
            {
                /**
                 * <summary><see cref="Foo"/></summary> */ string text = """<see cref="Bar"/>""";
                void Run() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols)
            .Where(r => r.Line == 6 && r.ReferenceKind == "type_reference")
            .ToList();

        Assert.DoesNotContain(references, r => r.SymbolName == "Foo");
        Assert.DoesNotContain(references, r => r.SymbolName == "Bar");
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatRawStringContentStartingWithDelimitedDocMarkerAsDocComment()
    {
        const string content = """"
            class Foo {}
            class Demo
            {
                string text = """
                /** <summary><see cref="Foo"/></summary> */
                """;

                void Run() {}
            }
            """";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 5);
    }

    [Fact]
    public void Extract_CsharpDocCref_DoesNotTreatVerbatimStringContentStartingWithDelimitedDocMarkerAsDocComment()
    {
        const string content = """
            class Foo {}
            class Demo
            {
                string text = @"line1
                /** <summary><see cref="Foo"/></summary> */
                line3";

                void Run() {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(
            references,
            r => r.SymbolName == "Foo"
                && r.ReferenceKind == "type_reference"
                && r.Line == 5);
    }

    [Fact]
    public void Extract_CsharpCtorChainThis_RewritesToEnclosingClass()
    {
        const string content = """
            namespace Demo;

            public class A
            {
                public A(int x) { }
                public A() : this(0) { }
                public A(string s) : this(s.Length) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var chainRefs = references.Where(r => r.SymbolName == "A").ToList();
        Assert.Equal(2, chainRefs.Count);
        Assert.All(chainRefs, r => Assert.Equal("call", r.ReferenceKind));
        Assert.All(chainRefs, r => Assert.Equal("function", r.ContainerKind));
        Assert.All(chainRefs, r => Assert.Equal("A", r.ContainerName));
        Assert.DoesNotContain(references, r => r.SymbolName == "this");
        Assert.DoesNotContain(references, r => r.SymbolName == "base");
    }

    [Fact]
    public void Extract_CsharpCtorChainBase_RewritesToBaseType()
    {
        const string content = """
            namespace Demo;

            public class A
            {
                public A(int x) { }
            }

            public class B : A
            {
                public B() : base(42) { }
                public B(int x, int y) : base(x + y) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var chainRefs = references
            .Where(r => r.SymbolName == "A" && r.ContainerName == "B")
            .ToList();
        Assert.Equal(2, chainRefs.Count);
        Assert.All(chainRefs, r => Assert.Equal("call", r.ReferenceKind));
        Assert.DoesNotContain(references, r => r.SymbolName == "base");
    }

    [Fact]
    public void Extract_CsharpCtorChainCrossLine_AttributesToConstructor()
    {
        const string content = """
            namespace Demo;

            public class A
            {
                public A(int x) { }
                public A(int x, int y)
                    : this(x + y)
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var chainRef = Assert.Single(references, r => r.SymbolName == "A");
        Assert.Equal("call", chainRef.ReferenceKind);
        Assert.Equal("function", chainRef.ContainerKind);
        Assert.Equal("A", chainRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpRecordChain_RewritesToBaseType()
    {
        const string content = """
            namespace Demo;

            public record Parent(int Value);

            public record Child(int Value, int Extra) : Parent(Value);
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "base");
        Assert.DoesNotContain(references, r => r.SymbolName == "this");

        // Record primary-ctor base call is attributed to a synthetic function named after
        // the record, so `callers` / `callees` / `impact` can traverse the chain edge.
        // record のプライマリーコンストラクタの base 呼び出しが、record 名の合成 function に紐付く。
        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("Child", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpRecordChain_MultilineBaseCall_RewritesToBaseType()
    {
        // Record declaration wraps the base primary-ctor call onto a continuation line.
        // The synthetic function container must span the whole header range so the Parent
        // reference is attributed to the record rather than landing without a container.
        // record 宣言の base primary-ctor 呼び出しが改行で別行に来るケース。
        // 合成 function コンテナは宣言ヘッダー全体を覆い、`Parent(...)` が record に紐付くこと。
        const string content = """
            namespace Demo;

            public record Parent(int Value);

            public record Child(int Value, int Extra)
                : Parent(Value);
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("Child", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpRecordChain_BracedBodyAfterBaseCall_AttributesBaseCallOnly()
    {
        // Base primary-ctor call sits at the end of the record header on its own line,
        // followed by a braced body. The synthetic container must cover the base-call line
        // but not leak into body method lines (those keep their real innermost containers).
        // base 呼び出し後に `{}` 本体が続くケース。合成コンテナはヘッダーのみを覆い、
        // body 内のメソッド行は通常の container を維持すること。
        const string content = """
            namespace Demo;

            public record Parent(int Value);

            public record Child(int Value, int Extra)
                : Parent(Value)
            {
                public int DoubleValue() => Value * 2;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("Child", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpRecordChain_SameLineBracedBody_DoesNotStealBodyCallsForSyntheticCtor()
    {
        // Legal same-line braced record where the header, base call, and a body method all live
        // on one line: `record Child(int V) : Parent(V) { public int Sum() => Add(V, 1); }`.
        // The synthetic function-kind container must cover only the header portion of that line
        // (before `{`), not the body. Overriding the whole line would steal `Add(...)` from its
        // real inner container and misattribute it to the synthetic record ctor in callers/impact.
        // 同一行 record 内の `{` より後ろの本体呼び出しが合成 ctor に奪われないことを固定する。
        const string content = """
            namespace Demo;

            public record Parent(int Value);

            public record Child(int Value) : Parent(Value) { public int Sum() => Add(Value, 1); }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("Child", parentRef.ContainerName);

        // Add(...) sits past the header-terminating `{`, so the synthetic record ctor must NOT
        // claim it. The exact inner container depends on whether SymbolExtractor produces a Sum
        // symbol for this layout, but in any case the call must not be attributed to a
        // `function` kind container named `Child` (that would be the synthetic record ctor).
        // `Add(...)` が synthetic record ctor に奪われていないことだけを固定（inner 側の
        // 具体的な container は extractor の挙動に依存するため深追いしない）。
        var addRef = Assert.Single(references, r => r.SymbolName == "Add");
        Assert.False(
            addRef.ContainerKind == "function" && addRef.ContainerName == "Child",
            $"Add(...) was incorrectly attributed to the synthetic record ctor (container_kind={addRef.ContainerKind}, container_name={addRef.ContainerName}).");
    }

    [Fact]
    public void Extract_CsharpRecord_TupleGenericInterfaceBase_DoesNotSynthesizeRecordCtor()
    {
        // Interface implementations that use tuple generic type args (e.g. `IBox<(int, int)>`)
        // contain `(` at generic depth 1, not at depth 0 as a primary-ctor call would. The record
        // primary-ctor detector must treat this as a non-chain base list and avoid synthesizing a
        // phantom record-ctor container; otherwise unrelated header calls would be attributed to
        // a fake function-kind `Pair` caller. The body call `Multiply(A, B)` inside `Combine`
        // must therefore stay attributed to `Combine`, not to a synthetic record ctor.
        // tuple を含む interface 実装が primary-ctor 呼び出しと誤判定されて合成 ctor が立たないよう固定。
        const string content = """
            namespace Demo;

            public interface IBox<T> { }

            public record Pair(int A, int B) : IBox<(int Left, int Right)>
            {
                public int Combine() => Multiply(A, B);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        // Multiply must stay attributed to `Combine`, not to a synthetic record ctor.
        var multiplyRef = Assert.Single(references, r => r.SymbolName == "Multiply");
        Assert.Equal("call", multiplyRef.ReferenceKind);
        Assert.False(
            multiplyRef.ContainerKind == "function" && multiplyRef.ContainerName == "Pair",
            $"Multiply(...) was incorrectly attributed to a synthetic record ctor (container_kind={multiplyRef.ContainerKind}, container_name={multiplyRef.ContainerName}).");
    }

    [Fact]
    public void Extract_CsharpRecordChain_SplitLinePrimaryCtorAndBaseCall_AttributesToSyntheticRecordCtor()
    {
        // Legal C# formatter style where the primary-ctor parens and `: Parent(...)` are split
        // across multiple lines: `record Child` / `(` / `  int Value` / `)` / `  : Parent(Value);`.
        // The joined-header record detector must still pick this up so the base call is attributed
        // to the synthetic function-kind container named after the record, not to class/null.
        // record 名と `(` を別行に分け、base 呼び出しもさらに別行に置く合法な改行スタイル。
        // 連結済みヘッダーベースの record 判定で拾い、`Parent(...)` を record の合成 function
        // コンテナに紐付ける必要がある。
        const string content = """
            namespace Demo;

            public record Parent(int Value);

            public record Child
            (
                int Value
            )
                : Parent(Value);
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("Child", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpClassPrimaryCtor_BaseCall_AttributesToSyntheticCtor()
    {
        // C# 12 introduced primary constructors on plain `class` / `struct` (not just records).
        // `public class Child(int value) : Parent(value)` must be treated the same as the record
        // form: the `Parent(...)` base call is attributed to a synthetic function-kind container
        // named after the child class, so callers/impact can follow the ctor chain.
        // C# 12 の class primary constructor でも record と同じ合成 function コンテナで扱うことを固定。
        const string content = """
            namespace Demo;

            public class Parent(int value)
            {
                public int Value { get; } = value;
            }

            public class Child(int value) : Parent(value)
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("Child", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpClassPrimaryCtor_SplitLineBaseCall_AttributesToSyntheticCtor()
    {
        // Split-line variant of C# 12 class primary ctor. The joined-header detector must still
        // pick it up so the base call is attributed to the synthetic function-kind container.
        // C# 12 class primary ctor の split-line 形式でも同じ扱いになることを固定。
        const string content = """
            namespace Demo;

            public class Parent(int value)
            {
            }

            public class ChildSplit
            (
                int value
            ) : Parent(
                value
            )
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("ChildSplit", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpGenericClassPrimaryCtor_BaseCall_AttributesToSyntheticCtor()
    {
        // Generic class with C# 12 primary ctor: `public class GenericChild<T>(T value) : Parent(42)`.
        // The generic arity in the type header must not break primary-ctor detection, and the base
        // call still resolves to the synthetic function-kind container named after the class.
        // generic class の C# 12 primary ctor でも同じ扱いになることを固定。
        const string content = """
            namespace Demo;

            public class Parent(int value)
            {
            }

            public class GenericChild<T>(T value) : Parent(42)
            {
                public T Item { get; } = value;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("call", parentRef.ReferenceKind);
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("GenericChild", parentRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpClassPrimaryCtor_SameLineBracedBody_DoesNotStealBodyCalls()
    {
        // Same-line braced form of C# 12 class primary ctor: header, base call, and a body
        // expression all on one line. The synthetic function-kind container must cover only the
        // header portion (before `{`), so body calls stay attributed to their real container.
        // C# 12 class primary ctor の同一行 braced 形式でも、本体呼び出しが合成 ctor に奪われないことを固定。
        const string content = """
            namespace Demo;

            public static class Helper
            {
                public static int Compute(int x) => x + 1;
            }

            public class Parent(int value)
            {
            }

            public class ChildWithBody(int value) : Parent(value) { public int Doubled { get; } = Helper.Compute(value); }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var parentRef = Assert.Single(references, r => r.SymbolName == "Parent");
        Assert.Equal("function", parentRef.ContainerKind);
        Assert.Equal("ChildWithBody", parentRef.ContainerName);

        // Helper.Compute(value) sits past the header-terminating `{`, so the synthetic ctor
        // must NOT claim it. The exact inner container depends on SymbolExtractor behavior,
        // but the call must not be attributed to a `function` kind container named `ChildWithBody`.
        // 本体側 `Helper.Compute(value)` が合成 ctor に奪われていないことだけを固定する。
        var computeRef = Assert.Single(references, r => r.SymbolName == "Compute");
        Assert.False(
            computeRef.ContainerKind == "function" && computeRef.ContainerName == "ChildWithBody",
            $"Compute(...) was incorrectly attributed to the synthetic class primary ctor (container_kind={computeRef.ContainerKind}, container_name={computeRef.ContainerName}).");
    }

    [Fact]
    public void Extract_CsharpStructPrimaryCtor_BaseCall_AttributesToSyntheticCtor()
    {
        // C# 12 struct primary ctor with an interface-like base list carrying a call. structs
        // cannot inherit another class, but the synthetic-ctor code path is still reached when
        // the base list entry uses the primary-ctor call shape. This exercises the `struct` kind
        // branch added alongside class generalization.
        // C# 12 struct primary ctor でも合成 function コンテナに切り替わることを固定（struct 分岐カバー）。
        const string content = """
            namespace Demo;

            public class BaseImpl(int value)
            {
            }

            public struct BoxedValue(int value) : BaseImpl(value)
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var baseRef = Assert.Single(references, r => r.SymbolName == "BaseImpl");
        Assert.Equal("call", baseRef.ReferenceKind);
        Assert.Equal("function", baseRef.ContainerKind);
        Assert.Equal("BoxedValue", baseRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpGenericBase_StripsGenericArgs()
    {
        const string content = """
            namespace Demo;

            public class Holder<T>
            {
                public Holder(T value) { }
            }

            public class IntHolder : Holder<int>
            {
                public IntHolder() : base(0) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var chainRef = Assert.Single(references, r => r.SymbolName == "Holder" && r.ReferenceKind == "call");
        Assert.Equal("call", chainRef.ReferenceKind);
        Assert.Equal("IntHolder", chainRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpBaseAfterInterfaces_UsesFirstBaseListEntry()
    {
        // The C# compiler requires the base class to come first in the base list,
        // so the first entry is the authoritative target for `base(...)`.
        // C# の base list では基底クラスが先頭に来る必要があり、そこが base(...) の呼び先となる。
        const string content = """
            namespace Demo;

            public interface IMarker {}

            public class Root
            {
                public Root(int x) { }
            }

            public class Leaf : Root, IMarker
            {
                public Leaf() : base(42) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var chainRef = Assert.Single(references, r => r.SymbolName == "Root" && r.ReferenceKind == "call");
        Assert.Equal("call", chainRef.ReferenceKind);
        Assert.Equal("Leaf", chainRef.ContainerName);
    }

    [Fact]
    public void Extract_CsharpBaseCall_NestedTypeUnderGenericOuter_AttributesToTerminalSegment()
    {
        // End-to-end C# regression for the `Outer<int>.Base` attribution bug.
        // `Outer<int>.Base` が `Base` へ張られることの E2E 回帰テスト。
        const string content = """
            namespace Demo;

            public class Outer<T>
            {
                public class Base
                {
                    public Base(int x) {}
                }
            }

            public class Leaf : Outer<int>.Base
            {
                public Leaf() : base(0) {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Leaf");
        Assert.DoesNotContain(references, r => r.SymbolName == "base");
    }

    [Fact]
    public void Extract_CsharpBaseCall_MultiLineBaseList_AttributesToParent()
    {
        // C# multi-line base-list (e.g. `class Child\n    : Parent`) must still resolve the
        // `: base(...)` target. SymbolRecord.Signature only stores the first declaration line, so
        // the header must be reconstructed from structural lines to find `: Parent`.
        // C# の複数行 base-list (`class Child\n    : Parent` の形) でも `: base(...)` の
        // 解決先を見失わないこと。SymbolRecord.Signature は 1 行目しか持たないため、ヘッダを
        // structural lines から再構築する必要がある。
        const string content = """
            namespace Demo;

            public class Parent
            {
                public Parent(int x) {}
            }

            public class Child
                : Parent
            {
                public Child() : base(0) {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Parent" && r.ContainerKind == "function" && r.ContainerName == "Child");
        Assert.DoesNotContain(references, r => r.SymbolName == "base");
    }

    [Fact]
    public void Extract_CsharpBaseCall_MultiLineBaseList_NestedGenericOuter_AttributesToTerminalSegment()
    {
        // Multi-line base-list combined with the `Outer<T>.Base` nested-generic form. Reconstructed
        // header feeds both multi-line handling and the depth-aware terminal-segment extractor.
        // 複数行 base-list と `Outer<T>.Base` ネスト generic 形の複合ケース。再構築したヘッダが
        // 両方の分岐（multi-line 連結と depth-aware な末尾セグメント抽出）を通る。
        const string content = """
            namespace Demo;

            public class Outer<T>
            {
                public class Base
                {
                    public Base(int x) {}
                }
            }

            public class Child<T>
                : Outer<T>.Base
            {
                public Child() : base(0) {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Base" && r.ContainerKind == "function" && r.ContainerName == "Child");
        Assert.DoesNotContain(references, r => r.SymbolName == "base");
    }

    [Fact]
    public void Extract_CsharpBaseCall_MultiLineBaseList_WithWhereClause_AttributesToParent()
    {
        // Multi-line base-list continuation followed by a `where` constraint must still resolve
        // the base type correctly. The `where` clause regex is trimmed before extracting the
        // first base entry, and the multi-line header path must not regress that behavior.
        // 複数行 base-list の継続行の末尾に `where` 制約が続く形でも基底型が正しく解決されること。
        const string content = """
            namespace Demo;

            public class Parent<U>
            {
                public Parent(U x) {}
            }

            public class Child<T>
                : Parent<T>
                where T : class, new()
            {
                public Child() : base(null!) {}
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Parent" && r.ContainerKind == "function" && r.ContainerName == "Child");
        Assert.DoesNotContain(references, r => r.SymbolName == "base");
    }

    [Fact]
    public void Extract_CsharpDefaultInterfaceMethod_GenericWhereConstraints_EmitsTypeReferences()
    {
        const string content = """
            using System;
            using System.Collections.Generic;

            namespace Demo;

            public interface IWorker<T>
                where T : IDisposable
            {
                void Do<U>(U item) where U : T
                {
                }

                IAsyncEnumerable<U> Stream<U>()
                    where U : IAsyncEnumerable<T>
                    => default!;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "IDisposable" && r.ReferenceKind == "type_reference" && r.Line == 7);
        Assert.Contains(references, r =>
            r.SymbolName == "T" && r.ReferenceKind == "type_reference" && r.Line == 9 && r.ContainerName == "Do");
        Assert.Contains(references, r =>
            r.SymbolName == "IAsyncEnumerable" && r.ReferenceKind == "type_reference" && r.Line == 14 && r.ContainerName == "Stream");
        Assert.Contains(references, r =>
            r.SymbolName == "T" && r.ReferenceKind == "type_reference" && r.Line == 14 && r.ContainerName == "Stream");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "U" && r.ReferenceKind == "type_reference" && r.Line is 9 or 13 or 14);
    }

    [Fact]
    public void Extract_CsharpDefaultInterfaceMethod_SameLineOuterWhere_DoesNotCaptureTypeConstraint()
    {
        const string content = """
            using System;

            namespace Demo;

            public interface IWorker<T> where T : IDisposable { void Do<U>() where U : T { } }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "IDisposable" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "IDisposable" && r.ReferenceKind == "type_reference" && r.ContainerName == "Do");
    }

    [Fact]
    public void CollectCSharpRecordHeader_MultiLineExtendsWithBraceAnnotationArg_CollectsThroughRealBase()
    {
        // Depth-aware header collector regression. Before the fix the collector terminated at
        // the first raw `{` without tracking `()` / `[]` / `<>` depth, so the `{` inside
        // `@Ann({A.class, B.class})` truncated the header before `Root`. ParseJavaBaseType and
        // the C# record primary-ctor container synthesis both feed off this helper, so any
        // multi-line Java `extends` or C# record base-list that carries a brace-containing
        // annotation argument must still reach the real base type. An end-to-end regression
        // covering the same brace-annotation shape now lives in
        // Extract_JavaSuperChain_BraceAnnotationArgExtends_AttributesSuperEdgeEndToEnd.
        // annotation 引数内の `{}` をヘッダ終端と誤認しない depth-aware 収集を直接検証する。
        // ParseJavaBaseType と record primary-ctor container synthesis の両方が依存する。
        var structuralLines = new[]
        {
            "class Leaf",
            "    extends @Ann({A.class, B.class}) Root {",
            "    Leaf() {",
            "        super(0);",
            "    }",
            "}",
        };

        var (endLine, endColumn, text) = ReferenceExtractor.CollectCSharpRecordHeader(structuralLines, startLine: 1);

        // Header must reach `Root` (the real base), which means the collector treated the inner
        // `{` as nested rather than the body opener. The terminator `{` is the one right after
        // `Root`, so endLine points at the second structural line, endColumn points at that `{`,
        // and text ends with `Root `.
        Assert.Equal(2, endLine);
        Assert.Contains("Root", text);
        Assert.EndsWith("Root ", text);
        // The terminator `{` sits at the very end of `    extends @Ann({A.class, B.class}) Root {`
        // (the outer `{` after `Root `). Use the structural line length minus 1 to stay agnostic
        // to exact column math while still asserting the column points at that `{`.
        Assert.Equal(structuralLines[1].Length - 1, endColumn);
        Assert.Equal('{', structuralLines[1][endColumn]);

        // ParseJavaBaseType on the collected header must also resolve to `Root`, not a phantom
        // annotated segment. This is the downstream path that super(...) attribution uses.
        Assert.Equal("Root", ReferenceExtractor.ParseJavaBaseType(text));
    }

    [Fact]
    public void CollectCSharpRecordHeader_AttributeArgsWithComparisonOperator_TerminatesAtRealBrace()
    {
        // Regression: previously the collector tracked `<` / `>` as generic depth, so a
        // comparison operator inside an attribute expression (e.g. `[Attr(Flag = 1 < 2)]`)
        // left angleDepth pinned at 1 with no matching `>`, which silently skipped the real
        // top-level `{` terminator and let the synthetic primary-ctor container stretch to
        // EOF. That polluted `callers` / `impact` with phantom record-ctor callers for every
        // call in the file. Now only `()` / `[]` are tracked for terminator detection.
        // 属性引数内の比較演算子 `<` で angleDepth が残り、ヘッダ終端の `{` を取り逃す回帰を固定。
        var structuralLines = new[]
        {
            "[Attr(Flag = 1 < 2)]",
            "public class Child(int x) : Parent(x)",
            "{",
            "    public int Doubled => x * 2;",
            "}",
        };

        var (endLine, endColumn, text) = ReferenceExtractor.CollectCSharpRecordHeader(structuralLines, startLine: 1);

        Assert.Equal(3, endLine);
        Assert.Equal(0, endColumn);
        Assert.Equal('{', structuralLines[2][endColumn]);
        Assert.Contains("Parent(x)", text);
        Assert.DoesNotContain("Doubled", text);
    }

    [Fact]
    public void Extract_CSharpPrimaryCtor_AttributeArgsWithComparison_DoesNotLeakContainerToFollowingCalls()
    {
        // End-to-end regression: with `[Attr(Flag = 1 < 2)]` on a C# 12 class primary
        // constructor, the synthetic record-ctor container must stop at the real body `{`.
        // Previously the unbalanced `<` kept the synthetic container active through the rest
        // of the file, so body-level calls (including calls in later unrelated methods) were
        // misattributed to the primary-ctor function container.
        // 属性引数内の比較演算子で合成 primary-ctor コンテナが EOF まで伸び、後続の call を
        // 誤って primary ctor 経由と見せる回帰を E2E で固定する。
        const string content = """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            class AttrAttribute : Attribute
            {
                public int Flag { get; set; }
            }

            public class Parent(int v)
            {
                public int Value { get; } = v;
            }

            public class Helper
            {
                public static int Compute(int x) => x + 1;
            }

            [Attr(Flag = 1 < 2)]
            public class Child(int x) : Parent(x)
            {
                public int Doubled() => Helper.Compute(x);
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        // The `Parent(x)` base call must attribute to the synthetic `Child` ctor container.
        Assert.Contains(references, r =>
            r.SymbolName == "Parent" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Child");

        // The `Helper.Compute(x)` body call must NOT leak into the synthetic Child ctor;
        // it must belong to the `Doubled` function container instead.
        Assert.Contains(references, r =>
            r.SymbolName == "Compute" && r.ReferenceKind == "call"
            && r.ContainerKind == "function" && r.ContainerName == "Doubled");
        Assert.DoesNotContain(references, r =>
            r.SymbolName == "Compute" && r.ContainerKind == "function" && r.ContainerName == "Child");
    }

    [Fact]
    public void Extract_CSharpPrimaryCtor_SameLineAttributeCallsStayOnClassContainer()
    {
        // Regression: the synthetic C# primary-ctor container previously applied to every
        // call on the declaration's start line, including attribute argument calls that
        // textually precede the `class` keyword. `[Attr(Helper.Get())] public class Child(int x) : Parent(x) {}`
        // then attributed `Attr` and `Get` to the synthetic `function:Child` container, so
        // `callers Attr` / `callers Helper.Get` looked like the child ctor called them.
        // The synthetic range now tracks a start column at the `class` keyword, so tokens
        // before it stay on the real `class:Child` container while `Parent(x)` remains on
        // the synthetic ctor.
        // 宣言行より前の属性呼び出しが合成 primary-ctor コンテナに奪われないことを固定する。
        const string content =
            "[Attr(Helper.Get())] public class Child(int x) : Parent(x) {}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, r =>
            r.SymbolName == "Attr" && r.ContainerKind == "class" && r.ContainerName == "Child");
        Assert.Contains(references, r =>
            r.SymbolName == "Get" && r.ContainerKind == "class" && r.ContainerName == "Child");
        Assert.Contains(references, r =>
            r.SymbolName == "Parent" && r.ContainerKind == "function" && r.ContainerName == "Child");

        // No `Attr` / `Get` references should be attributed to the synthetic function ctor.
        Assert.DoesNotContain(references, r =>
            (r.SymbolName == "Attr" || r.SymbolName == "Get")
            && r.ContainerKind == "function" && r.ContainerName == "Child");
    }

    [Fact]
    public void Extract_CsharpAttribute_ClassifiedAsAttribute()
    {
        // issue #293: `[Obsolete("msg")]` must produce an `attribute` reference, not a phantom `call`.
        // issue #293: `[Obsolete("msg")]` は `call` ではなく `attribute` として記録されること。
        const string content = """
            using System;
            [Obsolete("old")]
            public class Old
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var obsolete = Assert.Single(references.Where(r => r.SymbolName == "Obsolete"));
        Assert.Equal("attribute", obsolete.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpTargetedAttribute_ClassifiedAsAttribute()
    {
        // issue #293: `[return: NotNull("x")]` targeted attribute is classified as `attribute`.
        // issue #293: `[return: NotNull("x")]` のターゲット付き属性も `attribute` になること。
        const string content = """
            public class C
            {
                [return: NotNull("x")]
                public string M() => string.Empty;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var notNull = Assert.Single(references.Where(r => r.SymbolName == "NotNull"));
        Assert.Equal("attribute", notNull.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpMultipleAttributes_ClassifiedAsAttribute()
    {
        // issue #293: `[Foo("a"), Bar("b")]` — both entries in a comma-separated attribute list
        // must be classified as `attribute`.
        // issue #293: `[Foo("a"), Bar("b")]` のカンマ区切り属性リストは全て `attribute` になること。
        const string content = """
            [Foo("a"), Bar("b")]
            public class C { }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var foo = Assert.Single(references.Where(r => r.SymbolName == "Foo"));
        var bar = Assert.Single(references.Where(r => r.SymbolName == "Bar"));
        Assert.Equal("attribute", foo.ReferenceKind);
        Assert.Equal("attribute", bar.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpAttributeWithNewArgument_InstantiateStaysInstantiate()
    {
        // Inside attribute arguments, `new Foo()` still counts as `instantiate` — only the
        // attribute identifier itself is reclassified.
        // 属性引数内の `new Foo()` は従来通り `instantiate`。属性名本体のみが再分類される。
        const string content = """
            [AttributeUsage(AttributeTargets.Class)]
            public class C { }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var au = Assert.Single(references.Where(r => r.SymbolName == "AttributeUsage"));
        Assert.Equal("attribute", au.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpMethodBodyCall_StaysCall()
    {
        // Regression guard: ordinary method calls inside method bodies must still produce `call`,
        // not be mistaken for attribute references due to unrelated `[` tokens on nearby lines.
        // 回帰防止: メソッド本体内の通常呼び出しは、近くの `[` トークンの影響で `attribute` と
        // 誤判定されず `call` のまま残ること。
        const string content = """
            public class C
            {
                public int Run() => Compute(42);
                public int Compute(int x) => x;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var compute = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("call", compute.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpCollectionExpression_StaysCall()
    {
        // issue #293 regression: C# 12 collection expressions `var xs = [Make(), Make()]`
        // share the `[...]` syntax with attributes but must NOT be classified as `attribute`.
        // issue #293 回帰防止: C# 12 collection expression `var xs = [Make(), Make()]` は
        // 属性と同じ `[...]` 構文を共有するが `attribute` に誤分類してはならない。
        const string content = """
            public class C
            {
                public int Make() => 42;
                public void Run()
                {
                    var xs = [Make(), Make()];
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var makeRefs = references.Where(r => r.SymbolName == "Make").ToList();
        Assert.Equal(2, makeRefs.Count);
        Assert.All(makeRefs, r => Assert.Equal("call", r.ReferenceKind));
        Assert.All(makeRefs, r => Assert.Equal("Run", r.ContainerName));
    }

    [Fact]
    public void Extract_CsharpCollectionExpressionInArgument_StaysCall()
    {
        // Collection expressions appearing as arguments, nested in other expressions, or
        // after `return` must still classify inner calls as `call`, not `attribute`.
        // 引数やネストされた式、`return` 後の collection expression 内の呼び出しは `call` のまま。
        const string content = """
            public class C
            {
                public int Make() => 42;
                public int[] Wrap() => [Make(), Make()];
                public void Consume(int[] xs) { }
                public void Run()
                {
                    Consume([Make(), Make()]);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var makeRefs = references.Where(r => r.SymbolName == "Make").ToList();
        Assert.Equal(4, makeRefs.Count);
        Assert.All(makeRefs, r => Assert.Equal("call", r.ReferenceKind));
    }

    [Fact]
    public void Extract_CsharpIndexerAccess_StaysCall()
    {
        // `arr[Compute()]` — `[` is preceded by an identifier, so it is an indexer, not an
        // attribute, and the inner call must stay `call`.
        // `arr[Compute()]` は indexer で、`[` の直前が識別子のため attribute 扱いにはしない。
        const string content = """
            public class C
            {
                public int Compute() => 0;
                public int Read(int[] arr) => arr[Compute()];
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var compute = Assert.Single(references.Where(r => r.SymbolName == "Compute"));
        Assert.Equal("call", compute.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpChainedIndexerCalls_StayCall()
    {
        // issue #293 follow-up: `arr[Compute()][Compute()]` — the second `[` is preceded by
        // an indexer-closing `]`, not an attribute-section `]`. Walking back to the matching
        // `[` must find an expression-position bracket so both inner calls remain `call`.
        // issue #293 補足: `arr[Compute()][Compute()]` の 2 個目の `[` は indexer の `]` に
        // 続くだけで attribute section の終端ではないため、対応する `[` まで戻って宣言位置で
        // ないことを確認し、両方の呼び出しを `call` のまま残す。
        const string content = """
            public class C
            {
                public int Compute() => 0;
                public int Read(int[][] arr) => arr[Compute()][Compute()];
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var computeRefs = references.Where(r => r.SymbolName == "Compute").ToList();
        Assert.Equal(2, computeRefs.Count);
        Assert.All(computeRefs, r => Assert.Equal("call", r.ReferenceKind));
    }

    [Fact]
    public void Extract_CsharpMatrixIndexerCalls_StayCall()
    {
        // Two consecutive indexer accesses on a matrix — `matrix[Row()][Col()]` — must keep
        // both inner calls as `call`.
        // 連続 indexer `matrix[Row()][Col()]` でも、両方の呼び出しが `call` のまま残ること。
        const string content = """
            public class M
            {
                public int Row() => 0;
                public int Col() => 0;
                public int Read(int[,] matrix) => matrix[Row(), Col()];
                public int Read2(int[][] grid) => grid[Row()][Col()];
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var row = references.Where(r => r.SymbolName == "Row").ToList();
        var col = references.Where(r => r.SymbolName == "Col").ToList();
        Assert.Equal(2, row.Count);
        Assert.Equal(2, col.Count);
        Assert.All(row, r => Assert.Equal("call", r.ReferenceKind));
        Assert.All(col, r => Assert.Equal("call", r.ReferenceKind));
    }

    [Fact]
    public void Extract_CsharpChainedAttributeLists_StayAttribute()
    {
        // `[A(...)][B(...)]` on a declaration — the chained attribute-list form must still
        // classify both entries as `attribute` after the indexer-safety walk-back.
        // `[A(...)][B(...)]` の連続 attribute list は、indexer との区別が入ったあとも両方 `attribute`。
        const string content = """
            [A("x")][B("y")]
            public class C { }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var a = Assert.Single(references.Where(r => r.SymbolName == "A"));
        var b = Assert.Single(references.Where(r => r.SymbolName == "B"));
        Assert.Equal("attribute", a.ReferenceKind);
        Assert.Equal("attribute", b.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpParameterAttributes_ClassifiedAsAttribute()
    {
        // Parameter attributes are introduced by `(` or `,` rather than a scope boundary, so
        // the classifier must use forward lookahead from `[` to disambiguate against C# 12
        // collection expressions in argument position like `Consume([Make()])`.
        // パラメータ属性は `(` や `,` に続くため、collection expression と区別するには `[` から
        // 対応する `]` まで前方を走査して、直後が識別子かを確認する必要がある。
        const string content = """
            public class C
            {
                public void M([Attr("x")] int a, [Other("y")] int b) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var attr = Assert.Single(references.Where(r => r.SymbolName == "Attr"));
        var other = Assert.Single(references.Where(r => r.SymbolName == "Other"));
        Assert.Equal("attribute", attr.ReferenceKind);
        Assert.Equal("attribute", other.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpMultiLineAttribute_ClassifiedAsAttribute()
    {
        // A multi-line attribute list `[\n Foo("x")\n ]` must still classify `Foo` as
        // attribute even though the opening `[` is not on the same line as the identifier.
        // `[` と `Foo("x")` が別行にある場合でも `Foo` を属性として判定すること。
        const string content = """
            [
                Foo("x")
            ]
            public class C { }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var foo = Assert.Single(references.Where(r => r.SymbolName == "Foo"));
        Assert.Equal("attribute", foo.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpMultiLineParameterAttribute_ClassifiedAsAttribute()
    {
        // Parameter attribute split across lines — `(` ends one line, `[Attr]` sits on the
        // next, and the declaration continues after. Cross-line lookahead must still find
        // the identifier after the matching `]`.
        // 改行を挟んだパラメータ属性でも、跨行 lookahead で `]` の直後に続く識別子まで到達し、
        // 属性として判定できること。
        const string content = """
            public class C
            {
                public void M(
                    [Attr("x")]
                    int a)
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var attr = Assert.Single(references.Where(r => r.SymbolName == "Attr"));
        Assert.Equal("attribute", attr.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpTargetedAttribute_StaysAttribute()
    {
        // Regression: `[return: NotNullWhen(true)]` is recognised as an attribute section by
        // the pre-pass (bracket position, not `target:` heuristics). Keep the case covered.
        // リグレッション: `[return: NotNullWhen(true)]` も属性セクションとして判定されること。
        const string content = """
            public class C
            {
                [return: NotNullWhen(true)]
                public bool Try() => true;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var notNullWhen = Assert.Single(references.Where(r => r.SymbolName == "NotNullWhen"));
        Assert.Equal("attribute", notNullWhen.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpCollectionExpressionInArgument_StaysCallAfterParen()
    {
        // Defense-in-depth: `Consume([Make()])` has `[` immediately after `(`, matching the
        // parameter-attribute entry point, but forward lookahead sees `)` after the matching
        // `]` and correctly keeps `Make` as `call`.
        // `Consume([Make()])` のように `(` 直後に `[` が続くケースでも、`]` の直後が `)` であれば
        // collection expression として `call` のままであること。
        const string content = """
            public class C
            {
                public void M()
                {
                    Consume([Make(), Make()]);
                }
                private static int Make() => 0;
                private void Consume(int[] xs) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var makeRefs = references.Where(r => r.SymbolName == "Make").ToList();
        Assert.Equal(2, makeRefs.Count);
        Assert.All(makeRefs, r => Assert.Equal("call", r.ReferenceKind));
    }

    [Fact]
    public void Extract_CsharpCollectionExpressionPatternMatch_StaysCall()
    {
        // Regression: `[Make()] is int[] xs` is a pattern expression, not an attribute. The
        // next token after `]` is the contextual keyword `is`, so `Make` must stay `call`.
        // リグレッション: `[Make()] is int[] xs` はパターン式なので、`]` の次の `is` を属性の続きと誤認せず `Make` は `call`。
        const string content = """
            public class C
            {
                public bool M()
                {
                    return Consume([Make()] is int[] xs && xs.Length > 0);
                }
                private static int Make() => 0;
                private bool Consume(bool b) => b;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var make = Assert.Single(references.Where(r => r.SymbolName == "Make"));
        Assert.Equal("call", make.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpCollectionExpressionAsCast_StaysCall()
    {
        // Regression: `[Make()] as int[]` is an `as` cast, not an attribute. The classifier
        // must treat `as` as expression continuation and keep `Make` as `call`.
        // リグレッション: `[Make()] as int[]` は `as` キャストなので `Make` は `call` のまま。
        const string content = """
            public class C
            {
                public void M()
                {
                    var arr = ([Make()] as int[]);
                }
                private static int Make() => 0;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var make = Assert.Single(references.Where(r => r.SymbolName == "Make"));
        Assert.Equal("call", make.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpCollectionExpressionSwitchExpression_StaysCall()
    {
        // Regression: `[Make()] switch { ... }` is a switch expression over a collection,
        // not an attribute. The classifier must treat `switch` as expression continuation.
        // リグレッション: `[Make()] switch { ... }` は collection に対する switch 式のため `Make` は `call`。
        const string content = """
            public class C
            {
                public bool M() => Consume([Make()] switch { _ => true });
                private static int Make() => 0;
                private bool Consume(bool b) => b;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var make = Assert.Single(references.Where(r => r.SymbolName == "Make"));
        Assert.Equal("call", make.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpTupleTypedParameterAttribute_ClassifiedAsAttribute()
    {
        // Regression: `void M([Attr("x")] (int, int) value)` — the token after `]` is `(`
        // (tuple type syntax), which must still be treated as a declaration start so the
        // preceding `[...]` is classified as an attribute.
        // リグレッション: `void M([Attr("x")] (int, int) value)` のように `]` の直後が tuple 型の `(` でも属性扱い。
        const string content = """
            public class C
            {
                public void M([Attr("x")] (int a, int b) value) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var attr = Assert.Single(references.Where(r => r.SymbolName == "Attr"));
        Assert.Equal("attribute", attr.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpCallerInfoAttributes_EmitCompilerServicesTypeReferences()
    {
        // Regression (issue #2086): caller-info attributes are compile-time metadata.
        // Keep the ordinary `attribute` row, and add a `type_reference` to the framework
        // attribute type so impact/reference queries can see the dependency without treating
        // the attribute as a runtime call.
        // リグレッション (issue #2086): caller-info 属性はコンパイル時 metadata なので、
        // 通常の `attribute` 行を維持しつつ framework attribute 型への `type_reference` も出す。
        const string content = """
            using System.Runtime.CompilerServices;

            public static class Log
            {
                public static void Warning(
                    string message,
                    [CallerMemberName] string member = "",
                    [CallerFilePath] string file = "",
                    [CallerLineNumber] int line = 0,
                    [CallerArgumentExpression("message")] string expression = "",
                    [CallerArgumentExpressionAttribute("message")] string expressionWithSuffix = "",
                    [System.Runtime.CompilerServices.CallerArgumentExpression("message")] string qualifiedExpression = "")
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Single(references.Where(r => r.SymbolName == "CallerMemberName" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "CallerFilePath" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "CallerLineNumber" && r.ReferenceKind == "attribute"));
        Assert.Equal(2, references.Count(r => r.SymbolName == "CallerArgumentExpression" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "CallerArgumentExpressionAttribute" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "System.Runtime.CompilerServices.CallerMemberNameAttribute" && r.ReferenceKind == "type_reference"));
        Assert.Single(references.Where(r => r.SymbolName == "System.Runtime.CompilerServices.CallerFilePathAttribute" && r.ReferenceKind == "type_reference"));
        Assert.Single(references.Where(r => r.SymbolName == "System.Runtime.CompilerServices.CallerLineNumberAttribute" && r.ReferenceKind == "type_reference"));
        Assert.Equal(3, references.Count(r => r.SymbolName == "System.Runtime.CompilerServices.CallerArgumentExpressionAttribute" && r.ReferenceKind == "type_reference"));
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("Caller", StringComparison.Ordinal) && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpCallerInfoNoArgAttributes_EmitTypeReferencesForAttributeSuffixAndQualifiedNames()
    {
        // Regression (issue #2086): no-arg caller-info attributes bypass CallRegex, and callers
        // may spell them with the `Attribute` suffix or a namespace qualifier.
        // リグレッション (issue #2086): 引数なし caller-info 属性は CallRegex を通らず、
        // `Attribute` suffix や namespace 修飾付きでも書けるため同じ型参照を出す。
        const string content = """
            public static class Log
            {
                public static void Warning(
                    [System.Runtime.CompilerServices.CallerMemberNameAttribute] string member = "",
                    [global::System.Runtime.CompilerServices.CallerFilePath] string file = "",
                    [CallerLineNumberAttribute] int line = 0)
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Single(references.Where(r => r.SymbolName == "CallerMemberNameAttribute" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "CallerFilePath" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "CallerLineNumberAttribute" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "System.Runtime.CompilerServices.CallerMemberNameAttribute" && r.ReferenceKind == "type_reference"));
        Assert.Single(references.Where(r => r.SymbolName == "System.Runtime.CompilerServices.CallerFilePathAttribute" && r.ReferenceKind == "type_reference"));
        Assert.Single(references.Where(r => r.SymbolName == "System.Runtime.CompilerServices.CallerLineNumberAttribute" && r.ReferenceKind == "type_reference"));
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("Caller", StringComparison.Ordinal) && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpCallerInfoLookalikeAttributes_DoNotEmitCompilerServicesTypeReferences()
    {
        // Regression (issue #2086 review): explicit non-System qualifiers can legally end in
        // caller-info-like names, but they are not the BCL caller-info attributes.
        // リグレッション (issue #2086 review): 明示的な非 System 修飾子で caller-info 風の名前を
        // 使う属性は BCL caller-info 属性ではないため、compiler-services 型参照を出さない。
        const string content = """
            public static class Log
            {
                public static void Warning(
                    [MyCompany.CallerMemberName] string member = "",
                    [MyCompany.CallerArgumentExpression("message")] string expression = "")
                {
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Single(references.Where(r => r.SymbolName == "CallerMemberName" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "CallerArgumentExpression" && r.ReferenceKind == "attribute"));
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("System.Runtime.CompilerServices.Caller", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.SymbolName.StartsWith("Caller", StringComparison.Ordinal) && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpTypeParameterAttribute_ClassifiedAsAttribute()
    {
        // Regression: `class C<[Attr("x")] T>` — the `[` is preceded by `<`, which is a valid
        // attribute position for type parameters. The classifier must accept `<` alongside
        // `(` and `,` as parameter-list entry points.
        // リグレッション: `class C<[Attr("x")] T>` のように `<` の直後にある型パラメータ属性も検出できること。
        const string content = """
            public class C<[Attr("x")] T>
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var attr = Assert.Single(references.Where(r => r.SymbolName == "Attr"));
        Assert.Equal("attribute", attr.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpLambdaAttribute_ClassifiedAsAttribute()
    {
        // Regression: `var f = [Attr("x")] () => 0;` — the `[` is preceded by `=`, and the token
        // after `]` is `(` (lambda parameter list). The classifier must accept `=` as a valid
        // attribute-entry context alongside `(`, `,`, `<`.
        // リグレッション: `var f = [Attr("x")] () => 0;` のように `=` の直後にあるラムダ属性も検出できること。
        const string content = """
            public class C
            {
                public void M()
                {
                    var f = [Attr("x")] () => 0;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var attr = Assert.Single(references.Where(r => r.SymbolName == "Attr"));
        Assert.Equal("attribute", attr.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpNoArgAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293): `[Serializable]`, `[Obsolete]`, `[System.Obsolete]`,
        // `[assembly: CLSCompliant]`, `[Required, Key]` — bare no-arg attributes were not
        // indexed at all because CallRegex requires `(`. A dedicated no-arg entry path
        // must emit them with kind `attribute`.
        // リグレッション (issue #293): `[Serializable]` などの引数なし属性も `attribute` として
        // インデックスされること。CallRegex は `(` を要求するため専用の取り込み経路が必要。
        const string content = """
            [assembly: CLSCompliant]
            [Serializable]
            [Obsolete]
            [System.Obsolete]
            [Required, Key]
            public class C
            {
            }
            """;

        var references = ReferenceExtractor.Extract(1, "csharp", content, []);

        Assert.Single(references.Where(r => r.SymbolName == "CLSCompliant" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "Serializable" && r.ReferenceKind == "attribute"));
        // `[System.Obsolete]` — the qualifier chain is part of the attribute, and the emitted
        // reference should carry the final segment (`Obsolete`). There are two `Obsolete` rows
        // (the plain `[Obsolete]` above and the qualified `[System.Obsolete]`), both attribute.
        Assert.Equal(2, references.Count(r => r.SymbolName == "Obsolete" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "Required" && r.ReferenceKind == "attribute"));
        Assert.Single(references.Where(r => r.SymbolName == "Key" && r.ReferenceKind == "attribute"));
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpIndexerAccess_NotClassifiedAsAttribute()
    {
        // Regression (issue #293): `arr[i]` looks like a bare `[name]` token, but it is an
        // indexer expression, not an attribute. The no-arg attribute path must defer to the
        // attribute-range pre-pass so indexer access is not misclassified as `attribute`.
        // リグレッション (issue #293): `arr[i]` のような indexer アクセスは `[name]` 形だが
        // 属性ではない。属性レンジ pre-pass を経由することで attribute への誤分類を防ぐ。
        const string content = """
            public class C
            {
                public int M(int[] arr, int i) => arr[i];
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r => r.SymbolName == "i" && r.ReferenceKind == "attribute");
    }

    [Fact]
    public void Extract_CsharpGlobalQualifiedNoArgAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): `[global::System.Obsolete]` — fully qualified
        // attribute using the `global::` alias. The no-arg attribute regex must accept both
        // `.` and `::` as qualifier separators so these references are not silently dropped.
        // リグレッション (issue #293 補足): `[global::System.Obsolete]` のように `::` で修飾した
        // 引数なし属性も `attribute` として取り込まれること。
        const string content = """
            [global::System.Obsolete]
            public class C
            {
            }
            """;

        var references = ReferenceExtractor.Extract(1, "csharp", content, []);

        var obsolete = Assert.Single(references.Where(r => r.SymbolName == "Obsolete"));
        Assert.Equal("attribute", obsolete.ReferenceKind);
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpMultiLineNoArgAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): multi-line no-arg attribute forms such as
        // `[\n Serializable\n]`, `[\n global::System.Obsolete\n]`, and `[Serializable,\n Obsolete]`
        // must still classify as `attribute`. The attribute range pre-pass already tracks the
        // section across line breaks; the no-arg regex must not reject identifiers just because
        // the opening `[` or `,` is on a previous line.
        // リグレッション (issue #293 補足): `[\n Serializable\n]` のように `[` と識別子が別行に
        // ある複数行形、`[\n global::System.Obsolete\n]` のような `::` 修飾複数行形、そして
        // `[Serializable,\n Obsolete]` のような行を跨ぐカンマ区切りも `attribute` として取り込まれること。
        const string content = """
            [
                Serializable
            ]
            [
                global::System.Obsolete
            ]
            [Required,
                Key]
            public class C
            {
            }
            """;

        // Use SymbolExtractor to mirror end-to-end indexing: if SymbolExtractor misclassifies
        // a bare identifier inside a multi-line attribute section as a top-level symbol, the
        // reference would be filtered out via the `definitionNames` guard and this test would
        // catch that regression instead of silently passing with `[]` symbols.
        // SymbolExtractor を通すことで end-to-end と同じ流れを再現する。複数行属性セクション内の
        // 裸識別子を誤ってトップレベルのシンボルとして抽出してしまうと `definitionNames` ガードで
        // 参照が脱落してしまうため、本テストがその退行も検出する。
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var serializable = Assert.Single(references.Where(r => r.SymbolName == "Serializable"));
        Assert.Equal("attribute", serializable.ReferenceKind);
        var obsolete = Assert.Single(references.Where(r => r.SymbolName == "Obsolete"));
        Assert.Equal("attribute", obsolete.ReferenceKind);
        var required = Assert.Single(references.Where(r => r.SymbolName == "Required"));
        Assert.Equal("attribute", required.ReferenceKind);
        var key = Assert.Single(references.Where(r => r.SymbolName == "Key"));
        Assert.Equal("attribute", key.ReferenceKind);
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpGenericNoArgAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): generic no-arg C# attributes such as
        // `[MyAudit<int>]`, `[assembly: MyAttr<string>]`, and multi-line `[\n MyAttr<int>\n]`
        // must still classify as `attribute`. The no-arg attribute regex must accept an
        // optional generic argument list after the name so these references are indexed.
        // リグレッション (issue #293 補足): `[MyAudit<int>]` などのジェネリック引数なし属性、
        // `[assembly: MyAttr<string>]` のような assembly targeted 形、そして複数行の
        // `[\n MyAttr<int>\n]` も `attribute` として取り込まれること。
        const string content = """
            [assembly: MyAttr<string>]
            [MyAudit<int>]
            [
                MyAttr<int>
            ]
            public class C
            {
            }
            """;

        var references = ReferenceExtractor.Extract(1, "csharp", content, []);

        var myAudit = Assert.Single(references.Where(r => r.SymbolName == "MyAudit"));
        Assert.Equal("attribute", myAudit.ReferenceKind);
        Assert.Equal(2, references.Count(r => r.SymbolName == "MyAttr" && r.ReferenceKind == "attribute"));
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpGenericNoArgAttribute_RecordsTypeArgumentReferences()
    {
        // Regression (issue #1455): C# 11 generic attributes must still emit references for
        // custom type arguments inside the attribute's `<...>` list.
        // リグレッション (issue #1455): C# 11 の generic attribute では、属性の `<...>` 内に
        // 現れるユーザー定義型引数も参照として記録すること。
        const string content = """
            public class Payload
            {
            }

            public class Converter
            {
            }

            [Serializable<Payload>]
            [MyAttr<Dictionary<string, Converter>>]
            public class Data
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var serializable = Assert.Single(references.Where(r => r.SymbolName == "Serializable"));
        Assert.Equal("attribute", serializable.ReferenceKind);
        var myAttr = Assert.Single(references.Where(r => r.SymbolName == "MyAttr"));
        Assert.Equal("attribute", myAttr.ReferenceKind);
        Assert.Contains(references, r => r.SymbolName == "Payload" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Dictionary" && r.ReferenceKind == "type_reference");
        Assert.Contains(references, r => r.SymbolName == "Converter" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "string" && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpNestedGenericNoArgAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293 round-16 follow-up): nested generic no-arg C#
        // attributes such as `[MyAttr<Dictionary<string, int>>]` and
        // `[MyAttr<ValueTuple<int, List<string>>>]` must still classify as
        // `attribute`. The previous `<[^>\n]+>` generic segment stopped at the
        // first `>` and left the outer `>` dangling, so nested-generic
        // attributes were silently dropped from the index.
        // リグレッション (issue #293 round-16 補足): `[MyAttr<Dictionary<string, int>>]`
        // のような入れ子ジェネリック引数を持つ引数なし属性も `attribute` として
        // 取り込まれること。`<...>` 内部で `>` を除外する以前の実装では最初の `>`
        // で止まってしまい、nested generic 属性が黙って脱落していた。
        const string content = """
            [MyAttr<Dictionary<string, int>>]
            [MyOther<ValueTuple<int, List<string>>>]
            [
                MyMulti<Dictionary<string, List<int>>>
            ]
            public class C
            {
            }
            """;

        var references = ReferenceExtractor.Extract(1, "csharp", content, []);

        var a = Assert.Single(references.Where(r => r.SymbolName == "MyAttr"));
        Assert.Equal("attribute", a.ReferenceKind);
        var b = Assert.Single(references.Where(r => r.SymbolName == "MyOther"));
        Assert.Equal("attribute", b.ReferenceKind);
        var c = Assert.Single(references.Where(r => r.SymbolName == "MyMulti"));
        Assert.Equal("attribute", c.ReferenceKind);
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpNoArgParameterAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): no-arg parameter attributes such as
        // `void M([FromServices] IService s)` must still classify as `attribute`.
        // A previous iteration of the top-level-zone gate tracked paren depth globally
        // so the attribute section — which opens at global paren depth 1 inside the
        // method parameter list — never entered top-level. The fix is to track paren
        // depth section-locally so the section's own `[` / `]` define its zero point.
        // リグレッション (issue #293 補足): `void M([FromServices] IService s)` のような
        // 引数なしパラメータ属性も引き続き `attribute` として取り込まれること。
        const string content = """
            public class S
            {
                public void M([FromServices] IService s) { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var fromServices = Assert.Single(references.Where(r => r.SymbolName == "FromServices"));
        Assert.Equal("attribute", fromServices.ReferenceKind);
        Assert.DoesNotContain(references, r => r.SymbolName == "FromServices" && r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpNoArgDelegateAndLambdaParameterAttributes_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): no-arg attributes on delegate parameters and
        // lambda parameters also open their `[` inside outer parens, so they require
        // section-local paren-depth tracking for top-level zone detection.
        // リグレッション (issue #293 補足): デリゲート・ラムダの仮引数に付く no-arg 属性も
        // `(` の中で `[` が開くため、section-local の paren 深さ追跡が必要。
        const string content = """
            public delegate void D([Attr] int x);
            public class C
            {
                public void M()
                {
                    System.Func<int, int> f = ([Attr] int x) => x;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        // Both occurrences of `Attr` should be classified as `attribute`, not `call`.
        // 2 箇所の `Attr` が `attribute` として分類され、`call` にはならないこと。
        var attrs = references.Where(r => r.SymbolName == "Attr").ToList();
        Assert.Equal(2, attrs.Count);
        Assert.All(attrs, r => Assert.Equal("attribute", r.ReferenceKind));
    }

    [Fact]
    public void Extract_CsharpMultiLineNoArgAttribute_NonLeadingOpenBracket_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): multi-line `[...]` sections that open with `[`
        // appearing AFTER other text on the opening line (e.g. `void M([`, `class C<[`,
        // `delegate void D([`) must also blank out the interior in SymbolExtractor. Otherwise
        // the bare identifier on the interior line is extracted as a phantom `function`
        // declaration, and the downstream `definitionNames` guard suppresses the real
        // `attribute` reference, silently dropping it from `references --kind attribute`.
        // リグレッション (issue #293 補足): 開口行の途中で `[` が開く複数行属性
        // (`void M([`, `class C<[`, `delegate void D([` 等) も SymbolExtractor 側で
        // 内部を空白化しなければならない。そうしないと、内部行の裸識別子が phantom な
        // `function` 宣言として抽出され、下流の `definitionNames` ガードに食われて
        // 本来の `attribute` 参照が `references --kind attribute` から消える。
        const string content = """
            public class Foo
            {
                public void M([
                    FromServices
                ] IService s) { }
            }

            public class Bar<[
                TypeParamAttr
            ] T>
            {
            }

            public delegate void D([
                DelegateParamAttr
            ] int x);
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        // None of the attribute names should be misclassified as phantom function symbols.
        // 属性名が phantom な function シンボルとして抽出されていないこと。
        Assert.DoesNotContain(symbols, s => s.Name == "FromServices" && s.Kind == "function");
        Assert.DoesNotContain(symbols, s => s.Name == "TypeParamAttr" && s.Kind == "function");
        Assert.DoesNotContain(symbols, s => s.Name == "DelegateParamAttr" && s.Kind == "function");

        var fromServices = Assert.Single(references.Where(r => r.SymbolName == "FromServices"));
        Assert.Equal("attribute", fromServices.ReferenceKind);

        var typeParamAttr = Assert.Single(references.Where(r => r.SymbolName == "TypeParamAttr"));
        Assert.Equal("attribute", typeParamAttr.ReferenceKind);

        var delegateParamAttr = Assert.Single(references.Where(r => r.SymbolName == "DelegateParamAttr"));
        Assert.Equal("attribute", delegateParamAttr.ReferenceKind);
    }

    [Fact]
    public void Extract_CsharpMultiLineAttributeArgumentEnum_UsesAttributeKindInsteadOfCall()
    {
        // Regression (issue #492): enum-member accesses inside C# attribute arguments must reuse
        // metadata classification so they do not leak into the runtime call-graph as `call`.
        // The no-arg attribute detector still only applies to the attribute-list top level.
        // リグレッション (issue #492): C# 属性引数内の enum メンバーアクセスは metadata kind に
        // 落とし、runtime call-graph に `call` として混入させない。no-arg 属性検出は引き続き
        // 属性リストの top-level にのみ適用される。
        const string content = """
            using System;

            public enum ConverterStrategy
            {
                AllowNumbers,
                Strict
            }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class JsonConverterAttribute : Attribute
            {
                public JsonConverterAttribute(ConverterStrategy strategy) { }
            }

            [
                JsonConverter(
                    ConverterStrategy.AllowNumbers
                )
            ]
            public class A
            {
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        // JsonConverter is the only attribute here (with-args, classified by the metadata path).
        var jsonConverter = Assert.Single(references.Where(r => r.SymbolName == "JsonConverter"));
        Assert.Equal("attribute", jsonConverter.ReferenceKind);

        // AllowNumbers sits inside the attribute argument list, so it should inherit the metadata
        // context (`attribute`) without being emitted as a runtime `call`.
        // AllowNumbers は属性引数内なので、runtime `call` ではなく metadata 文脈 (`attribute`) を継承する。
        var allowNumbers = Assert.Single(references.Where(r => r.SymbolName == "AllowNumbers"));
        Assert.Equal("attribute", allowNumbers.ReferenceKind);
        Assert.DoesNotContain(references, r => r.SymbolName == "AllowNumbers" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "ConverterStrategy" && r.ReferenceKind == "attribute");
    }

    [Fact]
    public void Extract_CsharpAliasQualifiedNoArgAttribute_ClassifiedAsAttribute()
    {
        // Regression (issue #293 follow-up): `[Alias::MyAttr]` — alias-qualified attribute.
        // The qualifier separator may be `::` (extern alias) as well as `.`; the name segment
        // must still be emitted with kind `attribute`.
        // リグレッション (issue #293 補足): `[Alias::MyAttr]` のように extern alias 修飾された
        // 引数なし属性も `attribute` として取り込まれること。
        const string content = """
            [Alias::MyAttr]
            public class C
            {
            }
            """;

        var references = ReferenceExtractor.Extract(1, "csharp", content, []);

        var attr = Assert.Single(references.Where(r => r.SymbolName == "MyAttr"));
        Assert.Equal("attribute", attr.ReferenceKind);
        Assert.DoesNotContain(references, r => r.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_Csharp_LeadingBom_ExtractsReferencesOnFirstLine()
    {
        // BOM-prefixed C# source: reference extraction on line 1 must still work.
        // Closes #183.
        // BOM 付き C# ソース: 1 行目の参照抽出も機能する。Closes #183.
        const string content = "\uFEFFusing System;\n\nnamespace BomRef;\n\npublic class C\n{\n    public void Run() { Helper(); }\n    public void Helper() { }\n}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(symbols, s => s.Kind == "import" && s.Name == "System" && s.Line == 1);
        Assert.Contains(references, r => r.SymbolName == "Helper");
    }

    [Fact]
    public void Extract_CsharpReflectionNameLiteral_CapturesMemberReference()
    {
        const string content = """
            using System;
            using System.Reflection;

            public class Target
            {
                public void Foo() { }

                public MethodInfo? Resolve()
                {
                    return typeof(Target).GetMethod("Foo");
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Foo"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Resolve");
    }

    [Fact]
    public void Extract_CsharpReflectionNameLiteralConcat_CapturesMemberReference()
    {
        const string content = """
            using System;

            public class Target
            {
                public string DisplayName { get; set; } = "";

                public void Resolve()
                {
                    _ = typeof(Target).GetProperty("Display" + "Name");
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "DisplayName"
            && reference.ReferenceKind == "type_reference"
            && reference.ContainerName == "Resolve");
    }

    [Fact]
    public void Extract_CsharpReflectionNameDynamicConcat_DoesNotCaptureMemberReference()
    {
        const string content = """
            using System;

            public class Target
            {
                public void Foo() { }

                public void Resolve(string suffix)
                {
                    _ = typeof(Target).GetMethod("Fo" + suffix);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Foo"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpReflectionNameLiteralInComment_DoesNotCaptureMemberReference()
    {
        const string content = """
            using System;

            public class Target
            {
                public void Foo() { }

                public void Resolve(string name)
                {
                    _ = typeof(Target).GetMethod(name); // GetMethod("Foo")
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Foo"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpReflectionNameLiteralInBlockComment_DoesNotCaptureMemberReference()
    {
        const string content = """
            using System;

            public class Target
            {
                public void Foo() { }

                public void Resolve(string name)
                {
                    _ = typeof(Target).GetMethod(name); /* GetMethod("Foo") */
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Foo"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpReflectionNameLiteralInString_DoesNotCaptureMemberReference()
    {
        const string content = """
            using System;

            public class Target
            {
                public void Foo() { }

                public void Resolve()
                {
                    _ = "GetMethod(\"Foo\")";
                    _ = typeof(Target).GetMethod("Real");
                }

                public void Real() { }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Foo"
            && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Real"
            && reference.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CsharpStaticMemberAccess_CapturesClassQualifierReference()
    {
        const string content = """
            public static class Program
            {
                public static int Main(string[] args)
                {
                    return ProgramRunner.Run(args);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "ProgramRunner"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Main");
    }

    [Fact]
    public void Extract_CsharpGlobalQualifiedStaticMemberAccess_CapturesClassQualifierReference()
    {
        const string content = """
            public static class Program
            {
                public static int Main(string[] args)
                {
                    return global::ProgramRunner.Run(args);
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "ProgramRunner"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Main");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "global::ProgramRunner"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpUsingStatementStaticMemberAccess_CapturesClassQualifierReference()
    {
        const string content = """
            public class Consumer
            {
                public void Run()
                {
                    using var stream = FileFactory.OpenRead();
                    using (ResourceFactory.Acquire())
                    {
                    }
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "FileFactory"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "ResourceFactory"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Run");
    }

    [Fact]
    public void Extract_CsharpStaticFieldAccess_CapturesClassQualifierReference()
    {
        const string content = """
            public class Consumer
            {
                public int Read() => Options.DefaultTimeout;
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Options"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Read");
    }

    [Fact]
    public void Extract_CsharpNamespaceAndInstanceMemberAccess_DoesNotCaptureQualifierReference()
    {
        const string content = """
            namespace Demo.Tools;

            public class Consumer
            {
                public void Run(Service service)
                {
                    service.Start();
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Demo"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "service"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedStaticMemberAccess_CapturesRightmostTypeQualifier()
    {
        const string content = """
            public class Consumer
            {
                public void Run()
                {
                    System.Console.WriteLine("ok");
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Console"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Run");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "System"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpPascalCaseInstanceMemberChain_DoesNotCaptureMiddleQualifierReference()
    {
        const string content = """
            public class Consumer
            {
                public int Read(Config config, Request request)
                {
                    _ = config.Options.DefaultTimeout;
                    return request.User.Name.Length;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Options"
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "User"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedTypeDeclarations_DoNotCaptureNamespaceSegmentsAsCallReferences()
    {
        const string content = """
            public class Consumer
            {
                private System.Text.StringBuilder _builder;

                public System.Text.StringBuilder Builder => _builder;

                public System.Text.StringBuilder Create(System.Text.StringBuilder input)
                {
                    return input;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Text"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CsharpQualifiedTypeExpressions_DoNotCaptureNamespaceSegmentsAsCallReferences()
    {
        const string content = """
            public class Consumer
            {
                public object Convert(object value, List<System.Text.StringBuilder> builders)
                {
                    var builder = (System.Text.StringBuilder)value;
                    return builders.Count > 0 ? builder : value;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Text"
            && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_Csharp_MidFileBom_ExtractsReferencesOnAffectedLine()
    {
        // Mid-file BOM right before a call site: the reference must still be captured
        // on its real line number. Closes #183.
        // mid-file BOM が呼び出し行直前に挟まっても、実際の行番号で参照を拾う。Closes #183.
        const string content = "namespace BomRef;\npublic class C\n{\n    public void Run()\n    {\n\uFEFF        Helper();\n    }\n    public void Helper() { }\n}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var helperRef = Assert.Single(references.Where(r => r.SymbolName == "Helper"));
        Assert.Equal(6, helperRef.Line);
    }

    [Fact]
    public void Extract_Csharp_CrlfLeadingBom_ExtractsReferencesOnFirstLine()
    {
        // Direct-call input with CRLF line endings AND a leading BOM: the CRLF → LF
        // normalization must run before StripLineLeadingInvisibles so call sites on
        // mid-file BOM lines are still captured. Closes #183.
        // CRLF 改行 + 先頭 BOM の direct call: CRLF → LF 正規化を helper より先に通す
        // ことで、mid-file 行頭 BOM 直後の呼び出しも参照として拾える。Closes #183.
        const string content = "\uFEFFnamespace BomRefCrlf;\r\npublic class C\r\n{\r\n    public void Run()\r\n    {\r\n\uFEFF        Helper();\r\n    }\r\n    public void Helper() { }\r\n}\r\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var helperRef = Assert.Single(references.Where(r => r.SymbolName == "Helper"));
        Assert.Equal(6, helperRef.Line);
    }

    [Fact]
    public void Extract_Csharp_BareCrLeadingBom_ExtractsReferenceOnBomLine()
    {
        // Bare-`\r` direct-call input with a leading BOM + mid-file line-leading
        // BOM in front of the call site: the in-extractor `\r` → `\n`
        // normalization must run so `StripLineLeadingBom` (which treats `\n` as
        // the sole line separator) still sees the mid-file BOM as line-leading
        // and strips it, letting the regex capture the call site on the
        // BOM-prefixed line. Closes #183.
        // bare `\r` 改行 + 先頭 BOM + 呼び出し行頭 BOM の direct call: `\r` → `\n`
        // 正規化を helper より先に通し、classic-Mac 改行でも BOM 行の呼び出し
        // 参照が拾えることを固定。Closes #183.
        const string content = "\uFEFFnamespace BomRefBareCr;\rpublic class C\r{\r    public void Run()\r    {\r\uFEFF        Helper();\r    }\r    public void Helper() { }\r}\r";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var helperRef = Assert.Single(references.Where(r => r.SymbolName == "Helper"));
        Assert.Equal(6, helperRef.Line);
    }

    [Fact]
    public void Extract_Csharp_MixedLineEndingsLeadingBom_ExtractsReferenceOnBomLine()
    {
        // Mixed line endings (`\r\n`, bare `\r`, bare `\n`) interleaved with a
        // leading BOM and a mid-file line-leading BOM positioned immediately
        // after a real `\r\n\r` boundary (the blank line uses bare `\r`, so the
        // BOM follows `\r\n` + `\r`). The call site on the BOM-prefixed line is
        // only captured when the normalization collapses `\r\n` AND bare `\r`
        // to `\n` before `StripLineLeadingBom` runs — otherwise the `\r`
        // immediately preceding the mid-file BOM would keep the BOM
        // non-line-leading (helper treats `\n` as the sole line separator).
        // Line 7 assertion accounts for the blank line inserted by that extra
        // `\r`. Closes #183.
        // 混在改行（`\r\n` / bare `\r` / bare `\n`）+ 先頭 BOM + `\r\n\r` 境界直後の
        // mid-file 行頭 BOM の direct call: `\r\n` と bare `\r` の双方を `\n` に
        // 正規化してからでないと、BOM 直前の `\r` のせいで helper からは BOM が
        // 行頭扱いされず呼び出し参照が拾えない。bare `\r` による空行が挟まる分、
        // Helper は行 7。Closes #183.
        const string content = "\uFEFFnamespace BomRefMixed;\r\npublic class C\r{\n    public void Run()\r\n    {\r\n\r\uFEFF        Helper();\n    }\r    public void Helper() { }\r\n}\n";

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var helperRef = Assert.Single(references.Where(r => r.SymbolName == "Helper"));
        Assert.Equal(7, helperRef.Line);
    }

    [Fact]
    public void Extract_CSharpJavaKotlinCatchClauses_CaptureExceptionTypeReferences()
    {
        const string csharp = """
            class Service {
                void Run() {
                    try { Work(); }
                    catch (System.IO.IOException ex) when (ex != null) { }
                    catch (CustomException) { }
                    catch (System.Exception @caught) { }
                }
            }
            """;

        var csharpSymbols = SymbolExtractor.Extract(1, "csharp", csharp);
        var csharpReferences = ReferenceExtractor.Extract(1, "csharp", csharp, csharpSymbols);

        Assert.Contains(csharpReferences, r =>
            r.SymbolName == "IOException"
            && r.ReferenceKind == "type_reference"
            && r.ContainerName == "Run");
        Assert.Contains(csharpReferences, r =>
            r.SymbolName == "CustomException"
            && r.ReferenceKind == "type_reference"
            && r.ContainerName == "Run");
        Assert.DoesNotContain(csharpReferences, r =>
            r.SymbolName == "ex"
            && r.ReferenceKind == "type_reference");
        Assert.DoesNotContain(csharpReferences, r =>
            r.SymbolName == "caught"
            && r.ReferenceKind == "type_reference");

        const string java = """
            class Service {
                void run() {
                    try { work(); }
                    catch (final java.io.IOException | CustomException ex) { }
                }
            }
            """;

        var javaSymbols = SymbolExtractor.Extract(1, "java", java);
        var javaReferences = ReferenceExtractor.Extract(1, "java", java, javaSymbols);

        Assert.Contains(javaReferences, r =>
            r.SymbolName == "IOException"
            && r.ReferenceKind == "type_reference"
            && r.ContainerName == "run");
        Assert.Contains(javaReferences, r =>
            r.SymbolName == "CustomException"
            && r.ReferenceKind == "type_reference"
            && r.ContainerName == "run");
        Assert.DoesNotContain(javaReferences, r =>
            r.SymbolName == "ex"
            && r.ReferenceKind == "type_reference");

        const string kotlin = """
            class Service {
                fun run() {
                    try { work() }
                    catch (ex: java.io.IOException) { }
                    catch (_: CustomException) { }
                }
            }
            """;

        var kotlinSymbols = SymbolExtractor.Extract(1, "kotlin", kotlin);
        var kotlinReferences = ReferenceExtractor.Extract(1, "kotlin", kotlin, kotlinSymbols);

        Assert.Contains(kotlinReferences, r =>
            r.SymbolName == "IOException"
            && r.ReferenceKind == "type_reference"
            && r.ContainerName == "run");
        Assert.Contains(kotlinReferences, r =>
            r.SymbolName == "CustomException"
            && r.ReferenceKind == "type_reference"
            && r.ContainerName == "run");
        Assert.DoesNotContain(kotlinReferences, r =>
            r.SymbolName == "ex"
            && r.ReferenceKind == "type_reference");
    }

    [Fact]
    public void Extract_CSharpLambdaCapture_EmitsCaptureReferenceForEnclosingLocal()
    {
        const string content = """
            class Demo
            {
                void Run()
                {
                    var seed = 1;
                    System.Func<int> next = () => seed + 1;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var capture = Assert.Single(references.Where(r =>
            r.SymbolName == "seed"
            && r.ReferenceKind == "capture"));
        Assert.Equal(6, capture.Line);
        Assert.Equal("function", capture.ContainerKind);
        Assert.Equal("Run", capture.ContainerName);
    }

    [Fact]
    public void Extract_CSharpLambdaCapture_DoesNotCaptureLambdaParameterShadow()
    {
        const string content = """
            class Demo
            {
                void Run()
                {
                    var seed = 1;
                    System.Func<int, int> next = seed => seed + 1;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r =>
            r.SymbolName == "seed"
            && r.ReferenceKind == "capture");
    }

    [Fact]
    public void Extract_CSharpLambdaCapture_DoesNotShareLocalsAcrossSameNamedMethods()
    {
        const string content = """
            class First
            {
                void Run()
                {
                    var seed = 1;
                }
            }

            class Second
            {
                void Run()
                {
                    System.Func<int> next = () => seed + 1;
                }
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        Assert.DoesNotContain(references, r =>
            r.SymbolName == "seed"
            && r.ReferenceKind == "capture");
    }
}
