using System.Reflection;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class CSharpPrepassSymbolArtifactCacheTests
{
    [Fact]
    public void TryTake_MatchingChecksumOwnsDeepCloneAndIsTakeOnce()
    {
        var source = CreatePopulatedSymbol();
        var expectedValues = GetSymbolPropertyValues(source);
        var cache = new CSharpPrepassSymbolArtifactCache();

        Assert.True(cache.TryAdmit("src/Cafe\u0301.cs", "checksum-a", [source], hadRegexTimeout: false));
        source.Name = "workspace-mutated";
        source.FileId = 91;
        source.FamilyKey = "workspace-family";

        Assert.True(cache.TryTake("src/Caf\u00E9.cs", "checksum-a", out var artifact));
        var clone = Assert.Single(artifact.Symbols);
        Assert.NotSame(source, clone);
        foreach (var (property, expected) in expectedValues)
            Assert.Equal(expected, property.GetValue(clone));

        clone.Name = "main-mutated";
        clone.FileId = 123;
        clone.FamilyKey = "main-family";
        Assert.Equal("workspace-mutated", source.Name);
        Assert.Equal(91, source.FileId);
        Assert.Equal("workspace-family", source.FamilyKey);
        Assert.False(cache.TryTake("src/Caf\u00E9.cs", "checksum-a", out _));
    }

    [Fact]
    public void TryTake_ChecksumMismatchConsumesArtifactAndReportsFallback()
    {
        var events = new List<CSharpPrepassSymbolArtifactCacheEvent>();
        var previous = CSharpPrepassSymbolArtifactCache.EventForTesting;
        try
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting = events.Add;
            var cache = new CSharpPrepassSymbolArtifactCache();
            Assert.True(cache.TryAdmit(
                "src/Fixture.cs",
                "checksum-a",
                [CreatePopulatedSymbol()],
                hadRegexTimeout: false));

            Assert.False(cache.TryTake("src/Fixture.cs", "checksum-b", out _));
            Assert.False(cache.TryTake("src/Fixture.cs", "checksum-a", out _));
            Assert.Equal(0, cache.Count);
            Assert.Contains(events, item =>
                item.Phase == "checksum_mismatch"
                && item.Path == "src/Fixture.cs");
        }
        finally
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting = previous;
        }
    }

    [Fact]
    public void TryAdmit_EnforcesFileSymbolAndEstimatedByteCapsWithoutPartialPublish()
    {
        var symbol = CreatePopulatedSymbol();
        var fileBound = new CSharpPrepassSymbolArtifactCache(
            maxFiles: 1,
            maxSymbols: 10,
            maxEstimatedBytes: 1_000_000);
        Assert.True(fileBound.TryAdmit("a.cs", "a", [symbol], hadRegexTimeout: false));
        Assert.False(fileBound.TryAdmit("b.cs", "b", [symbol], hadRegexTimeout: false));
        Assert.Equal(1, fileBound.Count);

        var symbolBound = new CSharpPrepassSymbolArtifactCache(
            maxFiles: 10,
            maxSymbols: 1,
            maxEstimatedBytes: 1_000_000);
        Assert.False(symbolBound.TryAdmit(
            "symbols.cs",
            "a",
            [symbol, symbol],
            hadRegexTimeout: false));
        Assert.Equal(0, symbolBound.Count);
        Assert.Equal(0, symbolBound.AdmittedSymbolCount);

        var byteBound = new CSharpPrepassSymbolArtifactCache(
            maxFiles: 10,
            maxSymbols: 10,
            maxEstimatedBytes: 1);
        Assert.False(byteBound.TryAdmit("bytes.cs", "a", [], hadRegexTimeout: false));
        Assert.Equal(0, byteBound.Count);
        Assert.Equal(0, byteBound.AdmittedEstimatedBytes);
    }

    [Fact]
    public void TryAdmit_CancellationDoesNotPublishPartialArtifact()
    {
        var cache = new CSharpPrepassSymbolArtifactCache();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            cache.TryAdmit(
                "src/Fixture.cs",
                "checksum",
                [CreatePopulatedSymbol()],
                hadRegexTimeout: false,
                cancellationToken: cancellation.Token));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.AdmittedFileCount);
    }

    [Fact]
    public void TryAdmit_RegexTimeoutDoesNotPublishPartialSymbols()
    {
        var events = new List<CSharpPrepassSymbolArtifactCacheEvent>();
        var previous = CSharpPrepassSymbolArtifactCache.EventForTesting;
        try
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting = events.Add;
            var cache = new CSharpPrepassSymbolArtifactCache();

            Assert.False(cache.TryAdmit(
                "src/Fixture.cs",
                "checksum",
                [CreatePopulatedSymbol()],
                hadRegexTimeout: true));

            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.AdmittedSymbolCount);
            Assert.Contains(events, item =>
                item.Phase == "regex_timeout_skipped"
                && item.Path == "src/Fixture.cs");
        }
        finally
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting = previous;
        }
    }

    private static Dictionary<PropertyInfo, object?> GetSymbolPropertyValues(
        SymbolRecord symbol)
        => typeof(SymbolRecord)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .ToDictionary(property => property, property => property.GetValue(symbol));

    private static SymbolRecord CreatePopulatedSymbol()
        => new()
        {
            Id = 7,
            FileId = 11,
            Kind = "function",
            SubKind = "method",
            Name = "Fixture",
            IdentityNameFolded = "fixture-identity",
            DisplayNameFolded = "fixture-display",
            Line = 3,
            StartLine = 2,
            StartColumn = 4,
            EndLine = 8,
            BodyStartLine = 4,
            BodyEndLine = 7,
            Signature = "public static int Fixture()",
            ContainerKind = "class",
            ContainerName = "Host",
            ContainerQualifiedName = "Demo.Host",
            FamilyKey = "Demo+Host",
            Visibility = "public",
            ReturnType = "int",
            IsPartialDeclaration = true,
            IsFileLocalDeclaration = true,
            IsExplicitFileLocalDeclaration = true,
            DeclarationStructureMutatedByHook = true,
            DeclarationSemanticScore = 5,
            IdentifierStartColumn = 22,
            IsMetadataTarget = false,
            MetadataTargetSource = SymbolRecord.MetadataTargetSourceExtractor,
            SameLineSignatureOccurrenceIndex = 2,
        };
}
