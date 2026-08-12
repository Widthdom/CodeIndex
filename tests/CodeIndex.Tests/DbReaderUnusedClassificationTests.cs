using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void GetUnusedSymbols_OverlappingSurfaceSignalsPreserveDomainPrecedenceAndTags()
    {
        const string generatedContractPath = "tests/mcp/config/contracts/GeneratedDto.g.cs";
        const string attributedGeneratedContractPath = "src/contracts/AttributedDto.g.cs";
        const string markdownPath = "tests/mcp/guide.md";
        var generatedContractFileId = CreateUnusedClassificationFile(
            generatedContractPath,
            "csharp");
        var attributedGeneratedContractFileId = CreateUnusedClassificationFile(
            attributedGeneratedContractPath,
            "csharp");
        var markdownFileId = CreateUnusedClassificationFile(markdownPath, "markdown");
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = attributedGeneratedContractFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 7,
                Content = "using System.Text.Json.Serialization;\n\npublic sealed class AttributedDto\n{\n    [JsonPropertyName(\"title\")]\n    public string Title { get; init; } = \"\";\n}\n",
            },
        ]);
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = generatedContractFileId,
                Kind = "property",
                Name = "Limit",
                Line = 4,
                StartLine = 4,
                EndLine = 4,
                Signature = "public int Limit { get; set; }",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "ParseException",
                ContainerQualifiedName = "Fixtures.ParseException",
            },
            new SymbolRecord
            {
                FileId = attributedGeneratedContractFileId,
                Kind = "property",
                Name = "Title",
                Line = 6,
                StartLine = 5,
                EndLine = 6,
                Signature = "public string Title { get; init; } = \"\";",
                Visibility = "public",
                ContainerKind = "class",
                ContainerName = "AttributedDto",
                ContainerQualifiedName = "Fixtures.AttributedDto",
            },
            new SymbolRecord
            {
                FileId = markdownFileId,
                Kind = "heading",
                Name = "Configuration Guide",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = "# Configuration Guide",
            },
        ]);

        var unused = _reader.GetUnusedSymbols(
            limit: 10,
            kind: null,
            lang: null,
            pathPatterns: ["tests/mcp/**", "src/contracts/**"],
            excludePathPatterns: null,
            excludeTests: false);
        var detailedCount = _reader.CountUnusedSymbolsDetailed(
            kind: null,
            lang: null,
            pathPatterns: ["tests/mcp/**", "src/contracts/**"],
            excludePathPatterns: null,
            excludeTests: false);

        var generatedContract = Assert.Single(unused, result => result.Name == "Limit");
        Assert.Equal("reflection_or_config_suspect", generatedContract.UnusedBucket);
        Assert.Equal("test_contract", generatedContract.UnusedContractDomain);
        Assert.Equal(
        [
            "no_indexed_references",
            "intentional_surface_suspect",
            "reflection_or_config_suspect",
            "generated_surface",
            "contract_member",
            "config_or_metadata_surface",
            "exception_metadata",
            "config_or_metadata_member",
            "public_or_exported",
        ], generatedContract.UnusedReasonTags);
        Assert.Equal(
        [
            "intentional_surface_suspect",
            "reflection_or_config_suspect",
            "generated_surface",
            "contract_member",
            "config_or_metadata_surface",
            "exception_metadata",
            "config_or_metadata_member",
            "public_or_exported",
            "test_contract",
            "test_surface",
        ], generatedContract.UnusedContractDomainTags);

        var attributedGeneratedContract = Assert.Single(unused, result => result.Name == "Title");
        Assert.Equal("reflection_or_config_suspect", attributedGeneratedContract.UnusedBucket);
        Assert.Equal("generated_code", attributedGeneratedContract.UnusedContractDomain);
        Assert.Equal(
        [
            "intentional_surface_suspect",
            "generated_surface",
            "contract_member",
            "reflection_or_config_suspect",
            "public_or_exported",
            "generated_code",
        ], attributedGeneratedContract.UnusedContractDomainTags);

        var heading = Assert.Single(unused, result => result.Name == "Configuration Guide");
        Assert.Equal("reflection_or_config_suspect", heading.UnusedBucket);
        Assert.Equal("documentation_surface", heading.UnusedContractDomain);
        Assert.Equal(
        [
            "intentional_surface_suspect",
            "documentation_heading",
            "nonpublic_or_protected",
            "documentation_surface",
        ], heading.UnusedContractDomainTags);

        Assert.Equal(3, detailedCount.Count);
        Assert.Equal(3, detailedCount.FileCount);
        Assert.Equal(3, detailedCount.BucketCounts["reflection_or_config_suspect"]);
        Assert.Equal(1, detailedCount.ContractDomainCounts["generated_code"]);
        Assert.Equal(1, detailedCount.ContractDomainCounts["documentation_surface"]);
        Assert.Equal(1, detailedCount.ContractDomainCounts["test_contract"]);
    }

    private long CreateUnusedClassificationFile(string path, string lang) =>
        _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = lang,
            Size = 200,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
}
