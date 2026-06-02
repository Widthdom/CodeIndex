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
    public void Extract_Assembly_EmitsCallAndBranchTargetsWithContainers()
    {
        const string content = """
            section .text
            _start:
                call printf@PLT
            1:  call helper
                jmp .done
            .loop:
                bl helper
                blx.w helper
                bne .loop
                bne.n .loop
                bne.w .loop
                tbz x0, #3, .done
                bsf ecx, mask
                lea foo(%rip), %rax
                jmp rax
                jr $ra
                b 1f
                jmp 0x401000
                call 1234h
                call qword [rax]
                jmp dword [target]
            .done:
                ret
            helper:
                ret
            ; call ignored
            """;

        var symbols = SymbolExtractor.Extract(1, "assembly", content);
        var references = ReferenceExtractor.Extract(1, "assembly", content, symbols);

        Assert.Contains(ReferenceExtractor.GetSupportedLanguages(), lang => lang == "assembly");
        Assert.Equal(9, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "printf"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "_start");
        Assert.Contains(references, reference =>
            reference.SymbolName == "helper"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "_start");
        Assert.Contains(references, reference =>
            reference.SymbolName == ".done"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "_start");
        Assert.Contains(references, reference =>
            reference.SymbolName == "helper"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == ".loop");
        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "helper"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == ".loop"));
        Assert.Contains(references, reference =>
            reference.SymbolName == ".loop"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == ".loop");
        Assert.Equal(3, references.Count(reference =>
            reference.SymbolName == ".loop"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == ".loop"));
        Assert.Contains(references, reference =>
            reference.SymbolName == ".done"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == ".loop");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "foo");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "mask");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "rax");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "$ra");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "f");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "x401000");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "h");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "qword");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "dword");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "target");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "ignored");
    }

    [Fact]
    public void Extract_Assembly_IgnoresTabSeparatedIndirectTargetsAfterSizeDecorators()
    {
        const string content = "entry:\n    call qword\t[rax]\n    jmp dword\t[target]\n    call helper\nhelper:\n    ret\n";

        var symbols = SymbolExtractor.Extract(1, "assembly", content);
        var references = ReferenceExtractor.Extract(1, "assembly", content, symbols);

        var reference = Assert.Single(references, reference => reference.ReferenceKind == "call");
        Assert.Equal("helper", reference.SymbolName);
        Assert.Equal("entry", reference.ContainerName);
        Assert.DoesNotContain(references, reference => reference.SymbolName == "qword");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "dword");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "rax");
        Assert.DoesNotContain(references, reference => reference.SymbolName == "target");
    }
}
