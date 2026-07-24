using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    public static TheoryData<string, string, string, string> ScientificNativeReferenceCases => new()
    {
        {
            "nim",
            """
            import std/math
            type Child = object of Base
            proc run() =
              # ignoredCall()
              #[
              ignoredBlockCall()
              ]#
              helper()
            """,
            "std/math",
            "Base"
        },
        {
            "matlab",
            """
            classdef Child < Base
              methods
                function run(obj)
                  import pkg.Tools
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
            "Base"
        },
        {
            "julia",
            """
            module Sample
            using LinearAlgebra
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
            "Base"
        },
        {
            "d",
            """
            module sample;
            import std.stdio;
            class Child : Base {
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
            "Base"
        },
        {
            "cython",
            """"
            from libc.stdlib cimport malloc
            cdef class Child(Base):
                def run(self):
                    # ignoredCall()
                    """
                    ignoredBlockCall()
                    """
                    helper()
            """",
            "libc.stdlib",
            "Base"
        },
        {
            "ada",
            """
            with Ada.Text_IO;
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
            "Base"
        },
    };

    [Theory]
    [MemberData(nameof(ScientificNativeReferenceCases))]
    public void Extract_ScientificNativeLanguagesEmitBoundedGraphReferences_Issue4738(
        string language,
        string content,
        string importedName,
        string baseTypeName)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);

        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Contains(references, reference =>
            reference.SymbolName == importedName && reference.ReferenceKind == "import");
        Assert.Contains(references, reference =>
            reference.SymbolName == baseTypeName && reference.ReferenceKind == "type_reference");
        Assert.Contains(references, reference =>
            reference.SymbolName.Equals("helper", StringComparison.OrdinalIgnoreCase)
            && reference.ReferenceKind == "call");
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName.Equals("ignoredCall", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, reference =>
            reference.SymbolName.Equals("ignoredBlockCall", StringComparison.OrdinalIgnoreCase));
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
