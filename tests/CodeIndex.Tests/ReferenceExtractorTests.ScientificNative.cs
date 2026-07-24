using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    public static TheoryData<string, string, string, string, string> ScientificNativeReferenceCases => new()
    {
        {
            "nim",
            """
            import std/math, strutils
            type Child = object of Base
            proc run() =
              # ignoredCall()
              #[
              ignoredBlockCall()
              ]#
              helper()
            """,
            "std/math",
            "strutils",
            "Base"
        },
        {
            "matlab",
            """
            classdef Child < Base
              methods
                function run(obj)
                  import pkg.Tools pkg.Other
                  % ignoredCall()
                  %{
                  ignoredBlockCall()
                  %}
                  helper();
                end
              end
            end
            """,
            "pkg.Tools",
            "pkg.Other",
            "Base"
        },
        {
            "julia",
            """
            module Sample
            using LinearAlgebra, Statistics
            struct Child <: Base
            end
            function run()
                # ignoredCall()
                value' #=
                ignoredBlockCall()
                =#
                helper()
            end
            end
            """,
            "LinearAlgebra",
            "Statistics",
            "Base"
        },
        {
            "d",
            """
            module sample;
            import std.stdio, std.algorithm;
            class Child : Base, IFace {
                void run() {
                    /* ignoredCall(); */
                    /+
                    ignoredBlockCall();
                    +/
                    helper();
                }
            }
            """,
            "std.stdio",
            "std.algorithm",
            "Base"
        },
        {
            "cython",
            """"
            cimport numpy, cython
            cdef class Child(Base):
                def run(self):
                    # ignoredCall()
                    """
                    ignoredBlockCall()
                    """
                    helper()
            """",
            "numpy",
            "cython",
            "Base"
        },
        {
            "ada",
            """
            with Ada.Text_IO, Ada.Command_Line;
            package body Demo is
              type Child is new Base;
              procedure Run is
              begin
                -- IgnoredCall;
                Helper;
              end Run;
            end Demo;
            """,
            "Ada.Text_IO",
            "Ada.Command_Line",
            "Base"
        },
    };

    [Theory]
    [MemberData(nameof(ScientificNativeReferenceCases))]
    public void Extract_ScientificNativeLanguagesEmitBoundedGraphReferences_Issue4738(
        string language,
        string content,
        string importedName,
        string secondImportedName,
        string baseTypeName)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == importedName && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == secondImportedName && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == baseTypeName && reference.ReferenceKind == "type_reference");
        var helperReference = Assert.Single(references, reference =>
            reference.SymbolName.Equals("helper", StringComparison.OrdinalIgnoreCase)
            && reference.ReferenceKind == "call");
        Assert.Equal("run", helperReference.ContainerName, ignoreCase: true);
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName.Equals("ignoredCall", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName.Equals("ignoredBlockCall", StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string, string> ScientificNativePhantomSymbolCases => new()
    {
        {
            "d",
            """
            /+
            void Phantom() {}
            +/
            void real() { Phantom(); }
            """
        },
        {
            "nim",
            """
            #[
            proc Phantom() = discard
            ]#
            proc real() = Phantom()
            """
        },
        {
            "julia",
            """
            #=
            function Phantom()
            end
            =#
            function real()
                Phantom()
            end
            """
        },
        {
            "matlab",
            """
            %{
            function Phantom()
            end
            %}
            function real()
              Phantom();
            end
            """
        },
        {
            "cython",
            """"
            """
            def Phantom():
                pass
            """
            def real():
                Phantom()
            """"
        },
    };

    [Theory]
    [MemberData(nameof(ScientificNativePhantomSymbolCases))]
    public void Extract_ScientificNativeNonCodeDeclarationsDoNotBecomeResolutionTargets_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Phantom");
        Assert.Single(references, reference =>
            reference.SymbolName == "Phantom" && reference.ReferenceKind == "call");
    }

    [Theory]
    [InlineData(
        "matlab",
        """
        function result = run(a, b)
          text = 'stringCall()';
          result = a' * helper() * b';
        end
        """)]
    [InlineData(
        "julia",
        """
        function run(a, b)
            text = "stringCall()"
            a' * helper() * b'
        end
        """)]
    public void Extract_MatlabAndJuliaPreservePostfixTransposeCalls_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        var helperReference = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("run", helperReference.ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "stringCall");
    }

    [Fact]
    public void Extract_MatlabAppliesCharacterVectorQuoteRules_Issue4738()
    {
        const string content = """
            function run()
              path = 'C:\'; helper();
              items = [prefix 'fake()'];
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        var helperReference = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("run", helperReference.ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "fake");
    }

    [Fact]
    public void Extract_MatlabPreservesCallsAfterDotTranspose_Issue4738()
    {
        const string content = """
            function run(A)
              value = A.'; helper();
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        var helperReference = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("run", helperReference.ContainerName);
    }

    [Fact]
    public void Extract_NimExpandsGroupedImports_Issue4738()
    {
        const string content = """
            import std/[strutils, sequtils], os
            proc run() =
              helper()
            """;
        var symbols = SymbolExtractor.Extract(1, "nim", content);

        var references = ReferenceExtractor.Extract(1, "nim", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "std/strutils" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "std/sequtils" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "os" && reference.ReferenceKind == "import");
        Assert.DoesNotContain(references, reference => reference.SymbolName is "std" or "sequtils");
    }

    [Fact]
    public void Extract_JuliaNormalizesRelativeImportsAndBroadcastCalls_Issue4738()
    {
        const string content = """
            module Main
            using .Utils, ..Parent
            function run(xs)
                helper.(xs)
            end
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Utils" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Parent" && reference.ReferenceKind == "import");
        Assert.Equal(8, Assert.Single(references, reference =>
            reference.SymbolName == "Utils" && reference.ReferenceKind == "import").Column);
        Assert.Equal(17, Assert.Single(references, reference =>
            reference.SymbolName == "Parent" && reference.ReferenceKind == "import").Column);
        var helperReference = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("run", helperReference.ContainerName);
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is ".Utils" or "..Parent");
    }

    [Fact]
    public void Extract_DTemplateInvocationsEmitCalleeCalls_Issue4738()
    {
        const string content = """
            void run() {
                helper!int();
                other!(string, int)(42);
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "other" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "int" or "string" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_AdaQualifiedBareCallUsesResolvableLeafName_Issue4738()
    {
        const string content = """
            package body Demo is
              procedure Run is
              begin
                Helpers.Flush;
              end Run;
            end Demo;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        var flushReference = Assert.Single(references, reference =>
            reference.SymbolName == "Flush" && reference.ReferenceKind == "call");
        Assert.Equal("Run", flushReference.ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Helpers.Flush");
    }

    [Fact]
    public void Extract_MatlabCommaSeparatedEndKeepsFollowingCallTopLevel_Issue4738()
    {
        const string content = """
            function first()
              if true, helper(), end
            end
            toplevel();
            function second()
              other();
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        var first = Assert.Single(symbols, symbol => symbol.Name == "first");
        Assert.Equal(3, first.EndLine);
        var topLevelReference = Assert.Single(references, reference =>
            reference.SymbolName == "toplevel" && reference.ReferenceKind == "call");
        Assert.Null(topLevelReference.ContainerName);
    }

    [Theory]
    [InlineData("matlab", "function run(), helper(), end\noutside();\n")]
    [InlineData("julia", "function run(); helper(); end\noutside()\n")]
    public void Extract_CompactScientificFunctionEndsOnDeclarationLine_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        var function = Assert.Single(symbols, symbol => symbol.Name == "run");
        Assert.Equal(1, function.EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_MatlabIndexEndDoesNotCloseFunction_Issue4738()
    {
        const string content = """
            function run(A)
              value = A(:, end);
              helper();
            end
            outside();
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        Assert.Equal(4, Assert.Single(symbols, symbol => symbol.Name == "run").EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaMacroBlockDoesNotCloseFunctionEarly_Issue4738()
    {
        const string content = """
            function run()
                @async begin
                    helper()
                end
                after()
            end
            outside()
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal(6, Assert.Single(symbols, symbol => symbol.Name == "run").EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "after" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Theory]
    [InlineData("run(x) = helper(x)\nhelper(x) = x\n", 1)]
    [InlineData("run(x) = begin\n    helper(x)\nend\nhelper(x) = x\n", 3)]
    [InlineData("run(x) =\nbegin\n    helper(x)\nend\nhelper(x) = x\n", 4)]
    public void Extract_JuliaShortFunctionsOwnTheirCallReferences_Issue4738(
        string content,
        int expectedEndLine)
    {
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        var run = Assert.Single(symbols, symbol => symbol.Name == "run");
        Assert.Equal(expectedEndLine, run.EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_MatlabPeerFunctionsWithoutClosingEndHaveSeparateRanges_Issue4738()
    {
        const string content = """
            function first()
              helper1();
            function second()
              helper2();
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        var first = Assert.Single(symbols, symbol => symbol.Name == "first");
        var second = Assert.Single(symbols, symbol => symbol.Name == "second");
        Assert.Equal(2, first.EndLine);
        Assert.Equal(4, second.EndLine);
        Assert.Equal("first", Assert.Single(references, reference =>
            reference.SymbolName == "helper1" && reference.ReferenceKind == "call").ContainerName);
        Assert.Equal("second", Assert.Single(references, reference =>
            reference.SymbolName == "helper2" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaTransposeBeforeBlockCommentDoesNotMaskFollowingCalls_Issue4738()
    {
        const string content = """"
            function run(value)
                value' #=
                """
                =#
                helper()
            end
            """";
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    public static TheoryData<string, string> ScientificNativeMultilineLiteralCases => new()
    {
        {
            "julia",
            """"
            function run()
                text = """
                #= tokenOnly()
                """
                helper()
            end
            """"
        },
        {
            "nim",
            """"
            proc run() =
              let text = """
              #[ tokenOnly()
              """
              helper()
            """"
        },
        {
            "d",
            """
            void run() {
                auto text = q{
                    /* tokenOnly();
                };
                helper();
            }
            """
        },
        {
            "d",
            """
            void run() {
                auto text = `literal
                    tokenOnly()
                literal`;
                helper();
            }
            """
        },
        {
            "d",
            """
            void run() {
                auto text = q"EOS
                    tokenOnly()
                EOS";
                helper();
            }
            """
        },
    };

    [Theory]
    [MemberData(nameof(ScientificNativeMultilineLiteralCases))]
    public void Extract_ScientificNativeLiteralTokensDoNotSuppressFollowingCalls_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "tokenOnly");
    }

    [Fact]
    public void Extract_DMultipleBaseTypesAndCastSyntaxStayGraphAccurate_Issue4738()
    {
        const string content = """
            class Child : Base, IFace {
                void run() {
                    auto value = cast(int)(helper());
                }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Base" && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "IFace" && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "cast" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_AmbiguousMCombinesMatlabAndObjectiveCSymbolsAndReferences_Issue4738()
    {
        const string content = """
            #import <Foundation/Foundation.h>
            @interface Widget : NSObject
            @end

            function result = run()
              import pkg.Tools
              % ignoredMatlabCall()
              %{
              ignoredMatlabBlockCall()
              %}
              helper();
            end
            """;

        var symbols = SymbolExtractor.Extract(1, "ambiguous_m", content, "mixed.m");
        var references = ReferenceExtractor.Extract(1, "ambiguous_m", content, symbols, "mixed.m");

        Assert.Contains(symbols, symbol => symbol.Name == "Widget" && symbol.Kind == "class");
        Assert.Contains(symbols, symbol => symbol.Name == "run" && symbol.Kind == "function");
        Assert.Contains(references, reference =>
            reference.SymbolName == "Foundation/Foundation.h" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "pkg.Tools" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "ignoredMatlabCall");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "ignoredMatlabBlockCall");
    }

    [Fact]
    public void Extract_AmbiguousMRespectsObjectiveCCommentsDuringMatlabFallback_Issue4738()
    {
        const string content = """
            // ignoredObjectiveCCall()
            void run(void) {
                helper();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "ambiguous_m", content, "unknown.m");
        var references = ReferenceExtractor.Extract(1, "ambiguous_m", content, symbols, "unknown.m");

        Assert.Contains(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "ignoredObjectiveCCall");
    }

    [Fact]
    public void Extract_AmbiguousMPreservesObjectiveCModuloExpressions_Issue4738()
    {
        const string content = """
            void run(void) {
                int value = left % helper();
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "ambiguous_m", content, "unknown.m");
        var references = ReferenceExtractor.Extract(1, "ambiguous_m", content, symbols, "unknown.m");

        Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_AmbiguousMRetainsSharedSafetyGuards_Issue4738()
    {
        var oversizeContent = new string('x', ChunkSplitter.MaxLineLength + 1) + " helper();";
        const string conflictContent = """
            function result = run()
            <<<<<<< ours
              helper();
            =======
              alternate();
            >>>>>>> theirs
            end
            """;

        var oversizeReferences = ReferenceExtractor.Extract(
            1,
            "ambiguous_m",
            oversizeContent,
            [],
            "oversize.m");
        var conflictReferences = ReferenceExtractor.Extract(
            1,
            "ambiguous_m",
            conflictContent,
            [],
            "conflict.m");

        Assert.Empty(oversizeReferences);
        Assert.Empty(conflictReferences);
    }
}
