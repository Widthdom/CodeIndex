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
    public void Extract_Shell_DetectsCommandStyleFunctionCalls()
    {
        const string content = """
            function setup() {
              :
            }

            cleanup() {
              :
            }

            run() {
              # setup should stay ignored here
              setup && cleanup
              setup=1
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Contains(ReferenceExtractor.GetSupportedLanguages(), lang => lang == "shell");
        Assert.Equal(2, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "setup"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "cleanup"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
    }

    [Fact]
    public void Extract_Shell_DetectsCallsInsideCommandSubstitution()
    {
        const string content = """
            helper() {
              echo helper
            }

            other() {
              echo other
            }

            run() {
              result=$(helper)
              count=$(helper arg)
              if [ -n "$(other)" ]; then
                :
              fi
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Equal(3, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "helper"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "other"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
    }

    [Fact]
    public void Extract_Shell_DetectsCallsInsideBackticks()
    {
        const string content = """
            helper() {
              echo helper
            }

            run() {
              output=`helper arg`
              echo "wrapped `helper`"
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Equal(2, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "helper"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run"));
    }

    [Fact]
    public void Extract_Shell_DetectsCallsInsideNestedCommandSubstitution()
    {
        const string content = """
            outer() {
              echo outer
            }

            inner() {
              echo inner
            }

            run() {
              result=$(outer $(inner))
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Equal(2, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "outer"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "inner"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
    }

    [Fact]
    public void Extract_Shell_IgnoresSingleQuotedCommandSubstitutionLookAlikes()
    {
        const string content = """
            helper() {
              echo helper
            }

            run() {
              literal='$(helper)'
              also='`helper`'
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Empty(references.Where(reference => reference.ReferenceKind == "call"));
    }

    [Fact]
    public void Extract_Shell_DetectsAliasCalls()
    {
        const string content = """
            alias ll='ls -la'
            alias my-grep='grep -n'
            alias -g G='| grep'

            run() {
              ll /tmp
              my-grep needle
              echo foo G bar
              foo=G
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Equal(3, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "ll"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "my-grep"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "G"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
    }

    [Fact]
    public void Extract_Shell_DetectsMultipleAliasDefinitionsAndCalls()
    {
        const string content = """
            alias ll='ls -la' gs='git status'
            alias -g G='| grep' H='| head'

            run() {
              ll /tmp
              gs
              echo foo G bar
              H pattern
              foo=G
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Equal(4, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "ll"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "gs"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "G"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "H"
            && reference.ReferenceKind == "call"
            && reference.ContainerName == "run");
    }

    [Fact]
    public void Extract_Shell_DetectsSourcedFileReferences()
    {
        const string content = """
            run() {
              source ./env.sh
              source "./quoted env.sh"
              . ./lib/common.sh
              echo done
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "shell", content);
        var references = ReferenceExtractor.Extract(1, "shell", content, symbols);

        Assert.Equal(3, references.Count(reference => reference.ReferenceKind == "reference"));
        Assert.Contains(references, reference =>
            reference.SymbolName == "./env.sh"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "./quoted env.sh"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "run");
        Assert.Contains(references, reference =>
            reference.SymbolName == "./lib/common.sh"
            && reference.ReferenceKind == "reference"
            && reference.ContainerName == "run");
    }
}
