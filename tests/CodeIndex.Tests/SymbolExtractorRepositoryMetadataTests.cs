using CodeIndex.Indexer;

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
            [src/**/*.{cs,vb}]
            indent_style = space
            """;
        var editorConfigSymbols = SymbolExtractor.Extract(1, "editorconfig", editorConfig);
        AssertSymbolsContain(editorConfigSymbols, "namespace", "src/**/*.{cs,vb}");
        AssertSymbolsContain(editorConfigSymbols, "property", "root", "src/**/*.{cs,vb}.indent_style");

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
    }

    [Fact]
    public void SupportedLanguages_RepositoryMetadataIncludesJsonLines_Issue4740()
    {
        var supported = SymbolExtractor.GetSupportedLanguages();

        foreach (var language in new[] { "toml", "jsonl", "gitignore", "gitattributes", "editorconfig", "dockerignore", "config" })
        {
            Assert.Contains(language, supported);
            Assert.Equal(SymbolExtractor.RepositoryMetadataContractVersion, SymbolExtractor.GetContractVersion(language));
        }

        Assert.Equal(
            SymbolExtractor.ApplicationManifestContractVersion,
            SymbolExtractor.GetContractVersion("app_manifest"));
    }
}
