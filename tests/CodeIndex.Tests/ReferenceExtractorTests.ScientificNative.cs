using CodeIndex.Indexer;
using CodeIndex.Models;

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
    public void Extract_DTemplateInvocationsAcceptLiteralAndNestedArguments_Issue4738()
    {
        const string content = """
            void run() {
                literal!42();
                nested!(Tuple!(int))();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "literal" && reference.ReferenceKind == "call");
        Assert.Contains(references, reference =>
            reference.SymbolName == "nested" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "Tuple" or "int" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DManyTemplateInvocationsSuppressArgumentCallsInOnePass_Issue4738()
    {
        var content = "void run() { " + string.Join(' ', Enumerable.Repeat("helper!Type();", 512)) + " }";
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.Equal(512, references.Count(reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call"));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Type" && reference.ReferenceKind == "call");
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
        Assert.Equal("Helpers", flushReference.TargetQualifier);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Helpers.Flush");
    }

    [Fact]
    public void Extract_AdaConsecutiveBareCallsDoNotConsumeNextSeparator_Issue4738()
    {
        const string content = """
            procedure Run is
            begin
              First; Second; Third;
            end Run;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        foreach (var name in new[] { "First", "Second", "Third" })
        {
            Assert.Equal("Run", Assert.Single(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call").ContainerName);
        }
    }

    [Fact]
    public void Extract_AdaInlineStatementBoundariesPreserveBareCalls_Issue4738()
    {
        const string content = """
            procedure Run is begin First; end Run;
            procedure Check is begin if Ready then Second; else Third; end if; end Check;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        foreach (var name in new[] { "First", "Second", "Third" })
        {
            Assert.Single(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call");
        }
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
    [InlineData("run(x) = map(x) do item\n    helper(item)\nend\nhelper(x) = x\n", 3)]
    [InlineData("run(x) = (if x\n    helper(x)\nend)\nhelper(x) = x\n", 3)]
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
    public void Extract_JuliaExpressionPositionBlocksKeepOuterFunctionRange_Issue4738()
    {
        const string content = """
            function outer(xs)
                map(function (item)
                    inner(item)
                end, xs)
                push!(xs, begin
                    nested()
                end)
                values = [item for item in xs if item > 0]
                after()
            end
            outside()
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal(10, Assert.Single(symbols, symbol => symbol.Name == "outer").EndLine);
        foreach (var name in new[] { "inner", "nested", "after" })
        {
            Assert.Equal("outer", Assert.Single(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call").ContainerName);
        }
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
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
    public void Extract_MatlabSameIndentNestedFunctionKeepsExplicitOuterRange_Issue4738()
    {
        const string content = """
            function outer()
            helper1();
            function nested()
            inner();
            end
            helper2();
            end
            outside();
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        Assert.Equal(7, Assert.Single(symbols, symbol => symbol.Name == "outer").EndLine);
        Assert.Equal(5, Assert.Single(symbols, symbol => symbol.Name == "nested").EndLine);
        Assert.Equal("outer", Assert.Single(references, reference =>
            reference.SymbolName == "helper2" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
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

    [Theory]
    [InlineData(
        "matlab",
        """
        function run(A)
          value = A(1, ...
            end);
          helper();
        end
        outside();
        """)]
    [InlineData(
        "julia",
        """
        function run(A)
            value = A[
                end]
            helper()
        end
        outside()
        """)]
    public void Extract_ScientificIndexEndOnContinuationLinePreservesFunctionScope_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Equal(5, Assert.Single(symbols, symbol => symbol.Name == "run").EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaMultilineDelimitedShortFunctionKeepsItsCallScope_Issue4738()
    {
        const string content = """
            run(value) = (
                helper(value)
            )
            outside()
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal(3, Assert.Single(symbols, symbol => symbol.Name == "run").EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaEscapedTripleQuoteStaysInsideMultilineString_Issue4738()
    {
        const string content = """"
            function run()
                text = """
                escaped \""" fake()
                still literal
                """
                helper()
            end
            """";
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "fake");
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Theory]
    [InlineData("from .helpers cimport thing\n", "helpers")]
    [InlineData("from ..pkg.helpers cimport thing\n", "pkg.helpers")]
    public void Extract_CythonRelativeCimportsNormalizeTheirModuleName_Issue4738(
        string content,
        string expectedModule)
    {
        var references = ReferenceExtractor.Extract(1, "cython", content, []);

        Assert.Single(references, reference =>
            reference.SymbolName == expectedModule && reference.ReferenceKind == "import");
    }

    [Fact]
    public void Extract_ScientificDependencyNameLimitReportsOnlyAfterTheSharedBoundary_Issue4738()
    {
        var previousLimits = ReferenceExtractor.SafetyLimitsForTesting;
        ReferenceExtractor.SafetyLimitsForTesting = new ReferenceExtractionSafetyLimits
        {
            MaxLookupSymbols = 100,
            MaxLookupLines = 100,
            MaxNamesPerLine = 2,
            MaxContainerCandidates = 100,
        };

        try
        {
            var exact = ReferenceExtractor.ExtractDetailed(
                1,
                "ada",
                "with Alpha, Beta;\n",
                []);
            var exceeded = ReferenceExtractor.ExtractDetailed(
                1,
                "ada",
                "with Alpha, Beta, Gamma;\n",
                []);

            Assert.Equal(2, exact.References.Count(reference => reference.ReferenceKind == "import"));
            Assert.Equal(2, exceeded.References.Count(reference => reference.ReferenceKind == "import"));
            Assert.DoesNotContain(exact.Diagnostics, diagnostic =>
                diagnostic.Kind == "reference_scientific_native_dependency_name_budget_exceeded");
            Assert.Contains(exceeded.Diagnostics, diagnostic =>
                diagnostic.Kind == "reference_scientific_native_dependency_name_budget_exceeded");
        }
        finally
        {
            ReferenceExtractor.SafetyLimitsForTesting = previousLimits;
        }
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
                    /* tokenOnly(); */
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
    public void Extract_JuliaMultilineCommandLiteralDoesNotEmitOrRescopePhantomCode_Issue4738()
    {
        const string content = """
            function real()
                command = `echo
                    phantomCall()
                    function Phantom()
                `
                helper()
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Phantom");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "phantomCall");
        Assert.Equal("real", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaShortFunctionOperatorContinuationKeepsReferenceContainer_Issue4738()
    {
        const string content = """
            f(x) = first(x) +
                second(x)
            """;
        var symbol = Assert.Single(SymbolExtractor.Extract(1, "julia", content));

        var references = ReferenceExtractor.Extract(1, "julia", content, [symbol]);

        Assert.Equal(2, symbol.EndLine);
        Assert.Equal("f", Assert.Single(references, reference =>
            reference.SymbolName == "second" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaShortFunctionTrailingAssignmentKeepsIndentedRhsContainer_Issue4738()
    {
        const string content = """
            f(x) =
                helper(x)
            outside()
            """;
        var symbol = Assert.Single(SymbolExtractor.Extract(1, "julia", content));

        var references = ReferenceExtractor.Extract(1, "julia", content, [symbol]);

        Assert.Equal(2, symbol.EndLine);
        Assert.Equal("f", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_DTokenStringNestedLiteralsAndCommentsDoNotChangeBraceDepth_Issue4738()
    {
        const string content = """
            void run() {
                enum code = q{
                    auto first = "}";
                    auto second = q"[}]";
                    /* } */
                    /+ { } +/
                    phantomCall();
                };
                helper();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "phantomCall");
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_DIdentifierTokenStringRequiresDelimiterLineBoundary_Issue4738()
    {
        const string content = """
            void run() {
                enum text = q"EOS
            inside text contains EOS"; phantomCall()
            still inside
            EOS";
                helper();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.DoesNotContain(references, reference => reference.SymbolName == "phantomCall");
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_DCommentTextCannotOpenTokenStrings_Issue4738()
    {
        const string content = """
            /* documentation mentions q{ without a closing brace */
            /* documentation mentions an unmatched ` delimiter */
            void run() {
                helper();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Theory]
    [InlineData(
        "d",
        """
        void run() {
            auto text = r"fake()\";
            helper();
        }
        """)]
    [InlineData(
        "nim",
        """
        proc run() =
          let text = r"fake()\"
          helper()
        """)]
    public void Extract_DAndNimRawStringsUseLiteralBackslashRules_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "fake");
    }

    [Fact]
    public void Extract_NimGeneralizedRawStringsUseLiteralBackslashRules_Issue4738()
    {
        const string content = """
            proc run() =
              let text = foo"notACall()\"
              helper()
            """;
        var symbols = SymbolExtractor.Extract(1, "nim", content);

        var references = ReferenceExtractor.Extract(1, "nim", content, symbols);

        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "notACall");
    }

    [Fact]
    public void Extract_NimRawStringBeforeBlockCommentDoesNotExposeCommentCode_Issue4738()
    {
        const string content = """
            let text = r"literal\" #[
            proc Phantom() = discard
            phantomCall()
            ]#
            proc real() =
              helper()
            """;
        var symbols = SymbolExtractor.Extract(1, "nim", content);

        var references = ReferenceExtractor.Extract(1, "nim", content, symbols);

        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Phantom");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "phantomCall");
        Assert.Equal("real", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_NimRawTripleStringUsesMultilineMask_Issue4738()
    {
        const string content = """"
            let text = r"""
            proc Phantom() = discard
            phantomCall()
            """
            proc real() =
              helper()
            """";
        var symbols = SymbolExtractor.Extract(1, "nim", content);

        var references = ReferenceExtractor.Extract(1, "nim", content, symbols);

        Assert.DoesNotContain(symbols, symbol => symbol.Name == "Phantom");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "phantomCall");
        Assert.Equal("real", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_CythonStringDependenciesPreserveQuotedNames_Issue4738()
    {
        const string content = """"
            include "helpers.pxi"
            cdef extern from 'native.h':
                void native_call()
            """
            include "phantom.pxi"
            """
            """";
        var symbols = SymbolExtractor.Extract(1, "cython", content);

        var references = ReferenceExtractor.Extract(1, "cython", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "helpers.pxi" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "native.h" && reference.ReferenceKind == "import");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "phantom.pxi");
    }

    [Fact]
    public void Extract_CythonOrdinaryFromImportsEmitModuleDependencies_Issue4738()
    {
        const string content = """
            from helpers import thing
            from .local import other
            """;
        var symbols = SymbolExtractor.Extract(1, "cython", content);

        var references = ReferenceExtractor.Extract(1, "cython", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "helpers" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "local" && reference.ReferenceKind == "import");
    }

    [Fact]
    public void Extract_AdaAttributesPreserveNestedCallsWithoutPhantomAttributeCalls_Issue4738()
    {
        const string content = """
            procedure Run is
            begin
              First(Integer'Image(helper()) & Float'Image(other()));
            end Run;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        foreach (var name in new[] { "First", "helper", "other" })
        {
            Assert.Equal("Run", Assert.Single(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call").ContainerName);
        }
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "Image" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_DMultipleBaseTypesAndCastSyntaxStayGraphAccurate_Issue4738()
    {
        const string content = """
            private import pkg.mod;
            public abstract class Child : Base, IFace {
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
            reference.SymbolName == "pkg.mod" && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "cast" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_CythonRegularClassEmitsBaseTypeReference_Issue4738()
    {
        const string content = """
            class Child(Base):
                def run(self):
                    helper()
            """;
        var symbols = SymbolExtractor.Extract(1, "cython", content);

        var references = ReferenceExtractor.Extract(1, "cython", content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == "Base" && reference.ReferenceKind == "type_reference");
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
            @implementation Widget
            - (void)run {
                int first = left % helper();
                int second = left % -other();
                int third = left % *pointer();
                value %= divisor;
                afterAssignment();
                int fourth = index++ % postIncrement();
                int fifth = index-- % postDecrement();
                int sixth = left % [self computeValue];
                int seventh = 'x' % charRhs();
                int eighth = left % @(boxed());
            }
            @end
            """;

        var symbols = SymbolExtractor.Extract(1, "ambiguous_m", content, "unknown.m");
        var references = ReferenceExtractor.Extract(1, "ambiguous_m", content, symbols, "unknown.m");

        foreach (var name in new[]
        {
            "helper",
            "other",
            "pointer",
            "afterAssignment",
            "postIncrement",
            "postDecrement",
            "computeValue",
            "charRhs",
            "boxed",
        })
        {
            Assert.Single(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call");
        }
    }

    [Fact]
    public void Extract_AmbiguousMMatlabCommentsDoNotMasqueradeAsModuloExpressions_Issue4738()
    {
        const string content = "x = left % helper();";

        var symbols = SymbolExtractor.Extract(1, "ambiguous_m", content, "unknown.m");
        var references = ReferenceExtractor.Extract(1, "ambiguous_m", content, symbols, "unknown.m");

        Assert.DoesNotContain(references, reference => reference.SymbolName == "helper");
    }

    [Theory]
    [InlineData(
        """
        %{
        @interface Fake
        %}
        function result = run(left)
          result = left % helper();
        end
        """)]
    [InlineData(
        """
        const char *text = "\
        @interface Fake";
        function result = run(left)
          result = left % helper();
        end
        """)]
    public void Extract_AmbiguousMCommentedOrQuotedObjectiveCMarkersDoNotEnableModulo_Issue4738(
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, "ambiguous_m", content, "unknown.m");

        var references = ReferenceExtractor.Extract(
            1,
            "ambiguous_m",
            content,
            symbols,
            "unknown.m");

        Assert.DoesNotContain(references, reference => reference.SymbolName == "helper");
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

    [Theory]
    [InlineData("nim", "type Child = object of pkg.Base\n", "pkg")]
    [InlineData("matlab", "classdef Child < pkg.Base\nend\n", "pkg")]
    [InlineData("julia", "struct Child <: Pkg.Base\nend\n", "Pkg")]
    [InlineData("d", "class Child : pkg.Base {}\n", "pkg")]
    [InlineData("cython", "cdef class Child(pkg.Base):\n    pass\n", "pkg")]
    [InlineData("ada", "type Child is new Pkg.Base;\n", "Pkg")]
    public void Extract_QualifiedScientificBaseTypesUseResolvableLeafNames_Issue4738(
        string language,
        string content,
        string expectedQualifier)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var reference = Assert.Single(
            ReferenceExtractor.Extract(1, language, content, symbols),
            candidate => candidate.ReferenceKind == "type_reference");

        Assert.Equal("Base", reference.SymbolName);
        Assert.Equal(expectedQualifier, reference.TargetQualifier);
    }

    [Theory]
    [InlineData("d", "void run() { pkg.tools.flush(); }\n", "flush", "pkg.tools")]
    [InlineData("ada", "procedure Run is begin Pkg.Tools.Flush(); end Run;\n", "Flush", "Pkg.Tools")]
    [InlineData("nim", "proc run() = pkg.tools.flush()\n", "flush", "pkg.tools")]
    [InlineData("julia", "function run()\n  Pkg.Tools.flush()\nend\n", "flush", "Pkg.Tools")]
    [InlineData("cython", "def run():\n    pkg.tools.flush()\n", "flush", "pkg.tools")]
    [InlineData("matlab", "function run()\n  pkg.tools.flush();\nend\n", "flush", "pkg.tools")]
    public void Extract_QualifiedScientificCallsPreserveTargetQualifiers_Issue4738(
        string language,
        string content,
        string expectedName,
        string expectedQualifier)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var reference = Assert.Single(
            ReferenceExtractor.Extract(1, language, content, symbols),
            candidate => candidate.SymbolName == expectedName
                && candidate.ReferenceKind == "call");

        Assert.Equal(expectedQualifier, reference.TargetQualifier);
    }

    [Fact]
    public void Extract_QualifiedJuliaDefinitionsDoNotEmitPhantomCalls_Issue4738()
    {
        const string content = """
            module Base
            function Base.foo(x)
              helper()
            end
            Base.bar(x) = helper2()
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName is "foo" or "bar"
            && reference.ReferenceKind == "call");
        Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Single(references, reference =>
            reference.SymbolName == "helper2" && reference.ReferenceKind == "call");
    }

    [Theory]
    [InlineData("objc")]
    [InlineData("ambiguous_m")]
    public void Extract_ObjectiveCQuotedImportsSurviveStringMasking_Issue4738(string language)
    {
        const string content = """
            /*
            #import "Ignored.h"
            */
            #import "Local/Header.h"
            #include <Framework/Header.h>
            """;
        var symbols = SymbolExtractor.Extract(1, language, content, "sample.m");

        var references = ReferenceExtractor.Extract(1, language, content, symbols, "sample.m");

        Assert.Single(references, reference =>
            reference.SymbolName == "Local/Header.h" && reference.ReferenceKind == "import");
        Assert.Single(references, reference =>
            reference.SymbolName == "Framework/Header.h" && reference.ReferenceKind == "import");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "Ignored.h");
    }

    [Fact]
    public void Extract_AdaBackslashesDoNotEscapeClosingStringQuotes_Issue4738()
    {
        const string content = """
            procedure Run is
            begin
              Put("C:\"); Helper;
            end Run;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        Assert.Single(references, reference =>
            reference.SymbolName == "Helper"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "Run");
    }

    [Theory]
    [InlineData(
        """
        f(x) = helper(
            nested(x)
        )
        outside()
        """)]
    [InlineData(
        """
        f(x) =
            helper(
                nested(x)
            )
        outside()
        """)]
    public void Extract_JuliaShortFunctionMultilineCallsKeepNestedReferencesScoped_Issue4738(
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal("f", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Equal("f", Assert.Single(references, reference =>
            reference.SymbolName == "nested" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_MatlabManyEndlessPeerFunctionsKeepIndependentLinearRanges_Issue4738()
    {
        const int functionCount = 2_048;
        var content = new System.Text.StringBuilder(functionCount * 40);
        for (var index = 0; index < functionCount; index++)
        {
            content.Append("function f")
                .Append(index)
                .Append("()\n  helper")
                .Append(index)
                .Append("();\n");
        }

        var symbols = SymbolExtractor.Extract(1, "matlab", content.ToString());

        Assert.Equal(functionCount, symbols.Count(symbol => symbol.Kind == "function"));
        Assert.Equal(2, Assert.Single(symbols, symbol => symbol.Name == "f0").EndLine);
        Assert.Equal(functionCount * 2 + 1, Assert.Single(
            symbols,
            symbol => symbol.Name == $"f{functionCount - 1}").EndLine);
    }

    [Theory]
    [InlineData(
        "julia",
        """
        function f()
            f()
        end
        """,
        "f")]
    [InlineData(
        "matlab",
        """
        function f()
          f();
        end
        """,
        "f")]
    [InlineData(
        "cython",
        """
        def f():
            f()
        """,
        "f")]
    [InlineData(
        "nim",
        """
        proc p() =
          p()
        """,
        "p")]
    [InlineData(
        "d",
        """
        id id() {
            id();
        }
        """,
        "id")]
    public void Extract_ScientificDeclarationIdentifierSpanDoesNotHideRecursiveCall_Issue4738(
        string language,
        string content,
        string functionName)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Single(references, reference =>
            reference.SymbolName == functionName && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_JuliaMultilineComprehensionDoesNotExtendFunctionRange_Issue4738()
    {
        const string content = """
            function run(xs)
                values = [
                    helper(x)
                    for x in xs
                    if x > 0
                ]
                after()
            end
            outside()
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal(8, Assert.Single(symbols, symbol => symbol.Name == "run").EndLine);
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "after" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_JuliaBangCallablesAndQualifiedMacrosPreserveGraphIdentity_Issue4738()
    {
        const string content = """
            function mutate!(value)
                value
            end
            push!(values, value) = values
            function run(values)
                A.mutate!(values)
                A.push!.(values)
                A.@trace values
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Contains(symbols, symbol => symbol.Name == "mutate!");
        Assert.Contains(symbols, symbol => symbol.Name == "push!");
        foreach (var name in new[] { "mutate!", "push!", "trace" })
        {
            var reference = Assert.Single(references, candidate =>
                candidate.SymbolName == name && candidate.ReferenceKind == "call");
            Assert.Equal("A", reference.TargetQualifier);
            Assert.Equal("run", reference.ContainerName);
        }
    }

    public static TheoryData<string, string> ScientificNativeFollowingStringReferenceCases => new()
    {
        {
            "cython",
            """def run(): consume("a very long string", pkg.helper())"""
        },
        {
            "d",
            """void run() { consume("a very long string", pkg.helper()); }"""
        },
        {
            "nim",
            """proc run() = consume("a very long string", pkg.helper())"""
        },
        {
            "objc",
            """void run() { consume(@"a very long string", pkg.helper()); }"""
        },
    };

    [Theory]
    [MemberData(nameof(ScientificNativeFollowingStringReferenceCases))]
    public void Extract_ScientificNativeStringsPreserveFollowingReferenceColumns_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        var helper = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("pkg", helper.TargetQualifier);
        Assert.Equal(content.IndexOf("helper", StringComparison.Ordinal) + 1, helper.Column);
    }

    [Fact]
    public void Extract_MatlabContinuationTailDoesNotEmitCalls_Issue4738()
    {
        const string content = """
            function run(value)
              first(value, ... ignoredCall()
                value);
              realCall();
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        Assert.Single(references, reference =>
            reference.SymbolName == "first" && reference.ReferenceKind == "call");
        Assert.Single(references, reference =>
            reference.SymbolName == "realCall" && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "ignoredCall");
    }

    [Fact]
    public void Extract_DCompileTimeConstructsDoNotEmitCalls_Issue4738()
    {
        const string content = """
            extern(C) void exported() {
                static if (is(typeof(value) == int)) {
                    pragma(msg, __traits(compiles, helper()));
                }
                realCall();
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        foreach (var name in new[] { "extern", "is", "pragma", "__traits" })
        {
            Assert.DoesNotContain(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call");
        }
        Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Single(references, reference =>
            reference.SymbolName == "realCall" && reference.ReferenceKind == "call");
    }

    [Fact]
    public void Extract_MatlabArgumentsBlockHeaderDoesNotEmitCall_Issue4738()
    {
        const string content = """
            function run(values)
              arguments (Repeating)
                values
              end
              helper();
            end
            """;
        var symbols = SymbolExtractor.Extract(1, "matlab", content);

        var references = ReferenceExtractor.Extract(1, "matlab", content, symbols);

        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "arguments" && reference.ReferenceKind == "call");
        Assert.Equal("run", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
    }

    [Theory]
    [InlineData(
        "cython",
        """
        cdef class Worker:
            def run(self):
                self.helper()
        """)]
    [InlineData(
        "d",
        """
        class Worker {
            void run() {
                this.helper();
            }
        }
        """)]
    public void Extract_InstanceReceiversUseCurrentContainerResolution_Issue4738(
        string language,
        string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        var helper = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("run", helper.ContainerName);
        Assert.Null(helper.TargetQualifier);
    }

    [Fact]
    public void Extract_AdaNestedEndsPreserveOuterProcedureScope_Issue4738()
    {
        const string content = """
            procedure Run is
            begin
              if Ready then
                First;
              end if;
              Later;
            end Run;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        Assert.Equal(7, Assert.Single(symbols, symbol => symbol.Name == "Run").EndLine);
        foreach (var name in new[] { "First", "Later" })
        {
            Assert.Equal("Run", Assert.Single(references, reference =>
                reference.SymbolName == name && reference.ReferenceKind == "call").ContainerName);
        }
    }

    [Fact]
    public void Extract_CythonFStringExpressionsEmitCalls_Issue4738()
    {
        const string content = """
            def run():
                value = f"{pkg.helper()}"
            """;
        var symbols = SymbolExtractor.Extract(1, "cython", content);

        var references = ReferenceExtractor.Extract(1, "cython", content, symbols);

        var helper = Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call");
        Assert.Equal("run", helper.ContainerName);
        Assert.Equal("pkg", helper.TargetQualifier);
        Assert.Equal(20, helper.Column);
    }

    [Fact]
    public void Extract_JuliaWhereShortFunctionOwnsRhsCalls_Issue4738()
    {
        const string content = """
            f(x::T) where {T} = helper(x)
            outside()
            """;
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Equal(1, Assert.Single(symbols, symbol => symbol.Name == "f").EndLine);
        Assert.Equal("f", Assert.Single(references, reference =>
            reference.SymbolName == "helper" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName == "f" && reference.ReferenceKind == "call");
    }

    [Theory]
    [InlineData(
        """
        function f(A)
            value = A[begin]
        end
        outside()
        """)]
    [InlineData(
        """
        f(A) = A[begin]
        outside()
        """)]
    public void Extract_JuliaIndexBeginDoesNotExtendFunctionRange_Issue4738(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "julia", content);

        var references = ReferenceExtractor.Extract(1, "julia", content, symbols);

        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_DSuperReceiverDoesNotBecomeCurrentContainerSelfCall_Issue4738()
    {
        const string content = """
            class Base {
                void run() { }
            }
            class Child : Base {
                override void run() { super.run(); }
            }
            """;
        var symbols = SymbolExtractor.Extract(1, "d", content);

        var references = ReferenceExtractor.Extract(1, "d", content, symbols);

        var call = Assert.Single(references, reference =>
            reference.SymbolName == "run" && reference.ReferenceKind == "call");
        Assert.Equal("super", call.TargetQualifier);
        Assert.False(call.IsSelfReference);
    }

    [Fact]
    public void Extract_AdaUnnamedOuterEndTracksNestedDeclareBlock_Issue4738()
    {
        const string content = """
            procedure Outer is
            begin
              declare
              begin
                Inner;
              end;
              Later;
            end;
            outside;
            """;
        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var references = ReferenceExtractor.Extract(1, "ada", content, symbols);

        Assert.Equal(8, Assert.Single(symbols, symbol => symbol.Name == "Outer").EndLine);
        Assert.Equal("Outer", Assert.Single(references, reference =>
            reference.SymbolName == "Later" && reference.ReferenceKind == "call").ContainerName);
        Assert.Null(Assert.Single(references, reference =>
            reference.SymbolName == "outside" && reference.ReferenceKind == "call").ContainerName);
    }

    [Fact]
    public void Extract_AdaForwardDeclarationDoesNotBorrowLaterBodyRange_Issue4738()
    {
        const string content = """
            procedure Inner;
            procedure Inner is
            begin
              Helper;
            end Inner;
            """;

        var symbols = SymbolExtractor.Extract(1, "ada", content);

        var declarations = symbols.Where(symbol => symbol.Name == "Inner").ToList();
        Assert.Equal(2, declarations.Count);
        Assert.Equal(1, declarations[0].EndLine);
        Assert.Null(declarations[0].BodyStartLine);
        Assert.Equal(5, declarations[1].EndLine);
        Assert.Equal(3, declarations[1].BodyStartLine);
    }
}
