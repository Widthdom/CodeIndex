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
    public void Extract_Shell_CommandSubstitutions_AcrossSupportedForms_PreserveCalls()
    {
        // Dollar, backtick, nested, and single-quoted lookalike forms share one function.
        // Unique callable names plus exact counts make every scanner branch observable.
        // dollar / backtick / nested / single-quote lookalike を1 function にまとめ、
        // 固有 callable 名と厳密件数で各 scanner 分岐を観測する。
        const string content = """
            dollar_helper() {
              echo dollar
            }

            dollar_other() {
              echo dollar-other
            }

            backtick_helper() {
              echo backtick
            }

            nested_outer() {
              echo outer
            }

            nested_inner() {
              echo inner
            }

            quoted_only() {
              echo quoted
            }

            run() {
              result=$(dollar_helper)
              count=$(dollar_helper arg)
              if [ -n "$(dollar_other)" ]; then
                :
              fi
              first=`backtick_helper arg`
              echo "wrapped `backtick_helper`"
              nested=$(nested_outer $(nested_inner))
              literal='$(quoted_only)'
              also='`quoted_only`'
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences("shell", content);

        Assert.Equal(7, references.Count(reference => reference.ReferenceKind == "call"));
        AssertCallCount("dollar_helper", 2);
        AssertCallCount("dollar_other", 1);
        AssertCallCount("backtick_helper", 2);
        AssertCallCount("nested_outer", 1);
        AssertCallCount("nested_inner", 1);
        AssertReferencesDoNotContain(references, "call", "quoted_only");

        void AssertCallCount(string symbolName, int expectedCount) =>
            Assert.Equal(expectedCount, references.Count(reference =>
                reference.SymbolName == symbolName
                && reference.ReferenceKind == "call"
                && reference.ContainerName == "run"));
    }

    [Fact]
    public void Extract_Shell_Aliases_AcrossDefinitionForms_PreserveCalls()
    {
        // Separate and grouped alias declarations share one extraction. Unique names distinguish
        // declaration layouts, while assignment lookalikes remain covered by exact call counts.
        // 個別 / grouped alias 宣言を1回の抽出にまとめ、固有名と厳密件数で assignment
        // lookalike が call を増やさないことも維持する。
        const string content = """
            alias single-list='ls -la'
            alias single-grep='grep -n'
            alias -g SINGLE_G='| grep'
            alias grouped-list='ls -la' grouped-status='git status'
            alias -g GROUP_G='| grep' GROUP_H='| head'

            run() {
              single-list /tmp
              single-grep needle
              echo foo SINGLE_G bar
              single_value=SINGLE_G
              grouped-list /tmp
              grouped-status
              echo foo GROUP_G bar
              GROUP_H pattern
              grouped_value=GROUP_G
            }
            """;

        var (_, references) = ExtractSymbolsAndReferences("shell", content);

        var expectedAliases = new[]
        {
            "single-list",
            "single-grep",
            "SINGLE_G",
            "grouped-list",
            "grouped-status",
            "GROUP_G",
            "GROUP_H",
        };
        var aliasCalls = references
            .Where(reference =>
                reference.ReferenceKind == "call"
                && reference.ContainerName == "run")
            .Select(reference => reference.SymbolName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(7, references.Count(reference => reference.ReferenceKind == "call"));
        Assert.Equal(expectedAliases.OrderBy(name => name, StringComparer.Ordinal), aliasCalls);
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
