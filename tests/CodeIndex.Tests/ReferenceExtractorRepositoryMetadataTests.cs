using CodeIndex.Indexer;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Theory]
    [InlineData("toml", "[build]\ninclude = \"config/shared.toml\"\n", "config/shared.toml", 2)]
    [InlineData("toml", "path = \"config\\\\shared.toml\"\n", "config/shared.toml", 1)]
    [InlineData("gitignore", "# generated\nartifacts/\n", "artifacts", 2)]
    [InlineData("gitattributes", "docs/** linguist-documentation\n", "docs/**", 1)]
    [InlineData("editorconfig", "[src/[ab]/*.cs]\nindent_style = space\n", "src/[ab]/*.cs", 1)]
    [InlineData("dockerignore", "bin/\n", "bin", 1)]
    [InlineData("config", "rule(\ninclude = [\"rules/common.rules\"]\n)\n", "rules/common.rules", 2)]
    public void Extract_RepositoryMetadata_IndexesLocalPathReferences_Issue4740(
        string language,
        string content,
        string expectedPath,
        int expectedLine)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);
        var references = ReferenceExtractor.Extract(1, language, content, symbols);

        Assert.Contains(
            references,
            reference => reference.ReferenceKind == "project_reference"
                && reference.SymbolName == expectedPath
                && reference.Line == expectedLine);
    }

    [Fact]
    public void Extract_JsonLines_IndexesEachValidRecordWithoutFlatteningMalformedRecords_Issue4740()
    {
        const string content = """
            {"input":"src/first.cs"}
            not json
            {"output":"artifacts/result.json"}
            """;
        var symbols = SymbolExtractor.Extract(1, "jsonl", content);
        var references = ReferenceExtractor.Extract(1, "jsonl", content, symbols);

        Assert.Contains(
            references,
            reference => reference.SymbolName == "src/first.cs"
                && reference.ReferenceKind == "project_reference"
                && reference.ContainerName == "[0]"
                && reference.Line == 1);
        Assert.Contains(
            references,
            reference => reference.SymbolName == "artifacts/result.json"
                && reference.ReferenceKind == "project_reference"
                && reference.ContainerName == "[2]"
                && reference.Line == 3);
        Assert.DoesNotContain(references, reference => reference.Line == 2);
    }

    [Fact]
    public void Extract_TomlMultilineArrays_IndexesPathsWithoutCollectionLiteralEdges_Issue4740()
    {
        const string content = """
            includes = [
              "config/first.toml",
              "config/second.toml",
            ]
            empty = []
            numeric = [1, 2]
            """;
        var symbols = SymbolExtractor.Extract(1, "toml", content);
        var references = ReferenceExtractor.Extract(1, "toml", content, symbols);

        AssertReferencesContain(
            references,
            "project_reference",
            containerName: "includes",
            "config/first.toml",
            "config/second.toml");
        Assert.DoesNotContain(
            references,
            reference => reference.SymbolName is "[" or "[]" or "[1, 2]");
    }

    [Fact]
    public void Extract_InlineConfigRule_UsesTheRuleAsTheReferenceContainer_Issue4740()
    {
        const string content = """
            prefix_rule(include = ["rules/common.rules"], decision = "allow")
            """;
        var symbols = SymbolExtractor.Extract(1, "config", content);
        var references = ReferenceExtractor.Extract(1, "config", content, symbols);

        var reference = Assert.Single(references);
        Assert.Equal("rules/common.rules", reference.SymbolName);
        Assert.Equal("prefix_rule[0]", reference.ContainerName);
        Assert.Equal("rule", reference.ContainerKind);
    }

    [Fact]
    public void Extract_GitAttributes_HandlesQuotedPatternsAndSuppressesAttributeMacros_Issue4740()
    {
        const string content = """
            "docs/My File.md" linguist-documentation
            "docs/My\040Other.md" linguist-documentation
            [attr]binary -diff
            """;
        var symbols = SymbolExtractor.Extract(1, "gitattributes", content);
        var references = ReferenceExtractor.Extract(1, "gitattributes", content, symbols);

        Assert.Equal(2, references.Count);
        Assert.Contains(
            references,
            reference => reference.SymbolName == "docs/My File.md"
                && reference.ContainerName == "docs/My File.md");
        Assert.Contains(
            references,
            reference => reference.SymbolName == "docs/My Other.md"
                && reference.ContainerName == "docs/My Other.md");
        Assert.DoesNotContain(references, item => item.SymbolName.StartsWith("[attr]", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ApplicationManifest_IndexesDependenciesAndLocalPaths_Issue4740()
    {
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly xmlns="urn:schemas-microsoft-com:asm.v1">
              <assemblyIdentity name="Contoso.App" version="1.0.0.0" />
              <dependency>
                <dependentAssembly>
                  <assemblyIdentity name="Contoso.Core" version="2.0.0.0" />
                  <codeBase href="lib/Contoso.Core.dll" />
                </dependentAssembly>
              </dependency>
              <dependency>
                <dependentAssembly>
                  <assemblyIdentity name="https://example.com/unsafe.dll" />
                </dependentAssembly>
              </dependency>
              <file name="plugins/helper.dll" />
              <file name="/etc/unsafe.dll" />
              <codeBase href="https://example.com/remote.dll" />
              <probing privatePath="lib;plugins" />
            </assembly>
            """;
        var symbols = SymbolExtractor.Extract(1, "app_manifest", content);
        var references = ReferenceExtractor.Extract(1, "app_manifest", content, symbols);

        Assert.Contains(
            references,
            reference => reference.ReferenceKind == "dependency"
                && reference.SymbolName == "Contoso.Core"
                && reference.ContainerName == "Contoso.App"
                && reference.Line == 6);
        Assert.DoesNotContain(
            references,
            reference => reference.ReferenceKind == "dependency"
                && reference.SymbolName == "Contoso.App");
        AssertReferencesContain(
            references,
            "project_reference",
            containerName: "Contoso.App",
            "lib/Contoso.Core.dll",
            "plugins/helper.dll",
            "lib",
            "plugins");
        Assert.DoesNotContain(references, reference => reference.SymbolName.Contains("unsafe", StringComparison.Ordinal));
        Assert.DoesNotContain(references, reference => reference.SymbolName.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportedLanguages_RepositoryMetadataAndManifestAdvertiseReferences_Issue4740()
    {
        var supported = ReferenceExtractor.GetSupportedLanguages();

        Assert.All(
            new[]
            {
                "toml",
                "jsonl",
                "gitignore",
                "gitattributes",
                "editorconfig",
                "dockerignore",
                "config",
                "app_manifest",
            },
            language => Assert.Contains(language, supported));
    }

    [Fact]
    public void Extract_RepositoryMetadata_RejectsRemoteAbsoluteAndParentTraversalReferences_Issue4740()
    {
        const string toml = """
            version = "1.2.3"
            remote = "https://example.com/config.toml"
            absolute = "/etc/config.toml"
            parent = "../shared/config.toml"
            environment = "${ROOT}/config.toml"
            comment = "value" # see "docs/comment-only.md"
            local = "config/local.toml"
            """;
        var tomlSymbols = SymbolExtractor.Extract(1, "toml", toml);
        var tomlReferences = ReferenceExtractor.Extract(1, "toml", toml, tomlSymbols);

        Assert.Single(tomlReferences);
        Assert.Equal("config/local.toml", tomlReferences[0].SymbolName);

        const string attributes = "../outside/** export-ignore\n";
        var attributeSymbols = SymbolExtractor.Extract(1, "gitattributes", attributes);
        var attributeReferences = ReferenceExtractor.Extract(1, "gitattributes", attributes, attributeSymbols);
        Assert.Empty(attributeReferences);
    }

    [Fact]
    public void Extract_LargeRepositoryMetadataAndJsonLines_RemainsBoundedAndComplete_Issue4740()
    {
        const int recordCount = 4200;
        var cases = new[]
        {
            (
                Language: "toml",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, recordCount).Select(index => $"path{index} = \"src/file{index}.cs\""))),
            (
                Language: "jsonl",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, recordCount).Select(index => $$"""{"path":"src/file{{index}}.cs"}"""))),
        };

        foreach (var testCase in cases)
        {
            var symbols = SymbolExtractor.Extract(1, testCase.Language, testCase.Content);
            var references = ReferenceExtractor.Extract(1, testCase.Language, testCase.Content, symbols);

            Assert.Equal(recordCount, references.Count);
            Assert.Equal("src/file4199.cs", references[^1].SymbolName);
        }
    }
}
