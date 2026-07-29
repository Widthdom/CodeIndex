using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_RepositoryMetadata_IndexesSectionsKeysAndRules_Issue4740()
    {
        const string toml = """
            title = "sample"
            [build.output]
            path = "artifacts/app.dll"
            [[targets]]
            name = "linux"
            """;
        var tomlSymbols = SymbolExtractor.Extract(1, "toml", toml);
        AssertSymbolsContain(tomlSymbols, "namespace", "build.output", "targets");
        AssertSymbolsContain(tomlSymbols, "property", "title", "build.output.path", "targets.name");
        Assert.Contains(tomlSymbols, symbol => symbol.Name == "targets" && symbol.SubKind == "array_table");

        const string editorConfig = """
            root = true
            [src/[ab]/*.{cs,vb}]
            indent_style = space
            """;
        var editorConfigSymbols = SymbolExtractor.Extract(1, "editorconfig", editorConfig);
        AssertSymbolsContain(editorConfigSymbols, "namespace", "src/[ab]/*.{cs,vb}");
        AssertSymbolsContain(editorConfigSymbols, "property", "root", "src/[ab]/*.{cs,vb}.indent_style");

        var gitIgnoreSymbols = SymbolExtractor.Extract(1, "gitignore", "# generated\nartifacts/\n!important.log\n");
        AssertSymbolsContain(gitIgnoreSymbols, "rule", "artifacts/", "important.log");
        Assert.Contains(gitIgnoreSymbols, symbol => symbol.Name == "important.log" && symbol.SubKind == "include_rule");

        var dockerIgnoreSymbols = SymbolExtractor.Extract(1, "dockerignore", "bin/\nobj/\n");
        AssertSymbolsContain(dockerIgnoreSymbols, "rule", "bin/", "obj/");

        var attributesSymbols = SymbolExtractor.Extract(1, "gitattributes", "docs/** linguist-documentation diff=markdown\n");
        AssertSymbolsContain(attributesSymbols, "rule", "docs/**");
        AssertSymbolsContain(
            attributesSymbols,
            "property",
            "docs/**.linguist-documentation",
            "docs/**.diff");

        const string rules = """
            prefix_rule(
                include = ["rules/common.rules"],
                decision = "allow",
            )
            """;
        var configSymbols = SymbolExtractor.Extract(1, "config", rules);
        AssertSymbolsContain(configSymbols, "rule", "prefix_rule[0]");
        AssertSymbolsContain(
            configSymbols,
            "property",
            "prefix_rule[0].include",
            "prefix_rule[0].decision");
        Assert.All(
            configSymbols.Where(symbol => symbol.Kind == "property"),
            symbol => Assert.Equal("rule", symbol.ContainerKind));

        const string inlineRules = """
            prefix_rule(pattern = ["rg"], decision = "forbidden")
            """;
        var inlineConfigSymbols = SymbolExtractor.Extract(1, "config", inlineRules);
        AssertSymbolsContain(inlineConfigSymbols, "rule", "prefix_rule[0]");
        AssertSymbolsContain(
            inlineConfigSymbols,
            "property",
            "prefix_rule[0].pattern",
            "prefix_rule[0].decision");

        var quotedAttributesSymbols = SymbolExtractor.Extract(
            1,
            "gitattributes",
            "\"docs/My File.md\" linguist-documentation\n[attr]binary -diff\n");
        AssertSymbolsContain(quotedAttributesSymbols, "rule", "docs/My File.md", "[attr]binary");
    }

    [Fact]
    public void SupportedLanguages_RepositoryMetadataIncludesJsonLines_Issue4740()
    {
        var supported = SymbolExtractor.GetSupportedLanguages();

        foreach (var language in new[] { "toml", "gitignore", "gitattributes", "editorconfig", "dockerignore", "config" })
        {
            Assert.Contains(language, supported);
            Assert.Equal(SymbolExtractor.RepositoryMetadataContractVersion, SymbolExtractor.GetContractVersion(language));
        }

        Assert.Contains("jsonl", supported);
        Assert.Equal(
            SymbolExtractor.JsonLinesContractVersion,
            SymbolExtractor.GetContractVersion("jsonl"));
        Assert.Equal(
            SymbolExtractor.ApplicationManifestContractVersion,
            SymbolExtractor.GetContractVersion("app_manifest"));
    }

    [Fact]
    public void Extract_LargeRepositoryMetadataAndJsonLines_OnlyReturnsPersistableKinds_Issue4740()
    {
        const int itemCount = 4200;
        var cases = new[]
        {
            (
                Language: "toml",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, itemCount).Select(index => $"key{index} = \"value\""))),
            (
                Language: "jsonl",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, itemCount).Select(index => $$"""{"key":{{index}}}"""))),
        };

        foreach (var testCase in cases)
        {
            var symbols = SymbolExtractor.Extract(1, testCase.Language, testCase.Content);

            Assert.Equal(4096, symbols.Count);
            Assert.DoesNotContain(symbols, symbol => symbol.Kind == "extraction_diagnostic");
            Assert.All(
                symbols,
                symbol => Assert.True(
                    SymbolKindCatalog.IsValidSymbolKind(symbol.Kind),
                    $"Unexpected non-persistable symbol kind: {symbol.Kind}"));
        }
    }
}
