using System.Reflection;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class CSharpPrepassSymbolArtifactCacheTests
{
    [Fact]
    public void CSharpWorkspaceAssembly_PreservesOrderIdentityAndEvidence()
    {
        var existing = CreateMinimalSymbols(1);
        existing[0].Name = "Existing";
        var contract = new SymbolRecord
        {
            Kind = "function",
            Name = "Create",
            Signature = "static abstract T Create();",
            ContainerKind = "interface",
            ContainerName = "IContract",
        };
        var enumMember = new SymbolRecord
        {
            Kind = "enum",
            Name = "Red",
            ContainerKind = "enum",
            ContainerName = "Shade",
        };
        var ordinary = CreateMinimalSymbols(1)[0];
        ordinary.Name = "Ordinary";
        IReadOnlyList<SymbolRecord>?[] segments =
        [
            null,
            new List<SymbolRecord> { contract },
            [],
            new List<SymbolRecord> { enumMember, ordinary },
        ];

        var appended = new List<SymbolRecord>(existing);
        var evidence = CSharpStaticInterfacePrepass.AppendExtractedWorkspaceSymbols(
            segments,
            appended);
        Assert.Equal(3, evidence.SymbolCount);
        Assert.True(evidence.HasStaticInterfaceContracts);
        Assert.True(evidence.HasMemberReadTargets);
        Assert.Equal(
            new[] { existing[0], contract, enumMember, ordinary },
            appended,
            ReferenceEqualityComparer.Instance);

        var ordinaryOnly = new List<SymbolRecord>();
        var ordinaryEvidence = CSharpStaticInterfacePrepass.AppendExtractedWorkspaceSymbols(
            [new List<SymbolRecord> { ordinary }],
            ordinaryOnly);
        Assert.False(ordinaryEvidence.HasStaticInterfaceContracts);
        Assert.False(ordinaryEvidence.HasMemberReadTargets);
        Assert.Same(ordinary, Assert.Single(ordinaryOnly));
    }

    [Fact]
    public void CSharpWorkspaceSymbolSegments_PreservesPrefixCandidateOrderCountAndIdentity()
    {
        var prefix = CreateMinimalSymbols(2);
        var first = CreateMinimalSymbols(2);
        var last = CreateMinimalSymbols(1);
        IReadOnlyList<SymbolRecord>?[] segments = [null, first, [], last];
        var view = new CSharpStaticInterfacePrepass.CSharpWorkspaceSymbolSegments(
            prefix,
            segments,
            candidateSymbolCount: 3);

        Assert.Equal(5, view.Count);
        Assert.Equal(
            prefix.Concat(first).Concat(last),
            view,
            ReferenceEqualityComparer.Instance);
        Assert.Same(prefix[0], view[0]);
        Assert.Same(last[0], view[^1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => view[view.Count]);
    }

    [Fact]
    public void QualifiedPatternLookups_PreserveRawNonEnumTypeShadowing()
    {
        var lookups = ReferenceExtractor.BuildCSharpQualifiedPatternLookups(
        [
            new SymbolRecord { Kind = "enum", Name = "Shade" },
            new SymbolRecord
            {
                Kind = "enum",
                Name = "Red",
                ContainerKind = "enum",
                ContainerName = "Shade",
            },
            new SymbolRecord { Kind = "class", Name = "Shade" },
            new SymbolRecord { Kind = "enum", Name = "Tone" },
            new SymbolRecord
            {
                Kind = "enum",
                Name = "Warm",
                ContainerKind = "enum",
                ContainerName = "Tone",
            },
            new SymbolRecord { Kind = "field", Name = "Tone" },
        ]);

        Assert.False(Assert.Single(lookups.EnumMemberLookup["Red"]).AllowShortNameFallback);
        Assert.True(Assert.Single(lookups.EnumMemberLookup["Warm"]).AllowShortNameFallback);
    }

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
    public void TryAdmitOwned_TransfersIdentityOnlyAfterSuccessfulAtomicAdmission()
    {
        var source = CreatePopulatedSymbol();
        var ownedSymbols = new List<SymbolRecord> { source };
        var cache = new CSharpPrepassSymbolArtifactCache();

        Assert.True(cache.TryAdmitOwned(
            "src/Fixture.cs",
            "checksum",
            ownedSymbols,
            hadRegexTimeout: false));
        Assert.True(cache.TryTake("src/Fixture.cs", "checksum", out var artifact));
        Assert.Same(ownedSymbols, artifact.Symbols);
        Assert.Same(source, Assert.Single(artifact.Symbols));
        Assert.False(cache.TryTake("src/Fixture.cs", "checksum", out _));

        var rejectedSymbols = new List<SymbolRecord> { CreatePopulatedSymbol() };
        var capped = new CSharpPrepassSymbolArtifactCache(
            maxFiles: 1,
            maxSymbols: 1,
            maxEstimatedBytes: 1);
        Assert.False(capped.TryAdmitOwned(
            "src/Rejected.cs",
            "checksum",
            rejectedSymbols,
            hadRegexTimeout: false));
        Assert.Equal(0, capped.Count);
        Assert.False(capped.TryTake("src/Rejected.cs", "checksum", out _));
        Assert.Same(rejectedSymbols[0], Assert.Single(rejectedSymbols));

        var timeoutSymbols = new List<SymbolRecord> { CreatePopulatedSymbol() };
        Assert.False(cache.TryAdmitOwned(
            "src/Timeout.cs",
            "checksum",
            timeoutSymbols,
            hadRegexTimeout: true));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryTake("src/Timeout.cs", "checksum", out _));
        Assert.Same(timeoutSymbols[0], Assert.Single(timeoutSymbols));

        var cancelledSymbols = new List<SymbolRecord> { CreatePopulatedSymbol() };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            cache.TryAdmitOwned(
                "src/Cancelled.cs",
                "checksum",
                cancelledSymbols,
                hadRegexTimeout: false,
                cancellationToken: cancellation.Token));
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryTake("src/Cancelled.cs", "checksum", out _));
        Assert.Same(cancelledSymbols[0], Assert.Single(cancelledSymbols));
    }

    [Fact]
    public void TryAdmitOwned_AvoidsGenericCloneAllocationForLargeArtifact()
    {
        const int symbolCount = 4_096;
        const long minimumAllocationSavings = 500L * 1024;
        WarmUpArtifactAdmissionsForAllocationMeasurement();
        var genericSymbols = CreateMinimalSymbols(symbolCount);
        var ownedSymbols = CreateMinimalSymbols(symbolCount);
        var genericCache = new CSharpPrepassSymbolArtifactCache();
        var ownedCache = new CSharpPrepassSymbolArtifactCache();

        var allocatedBeforeGenericAdmission =
            GC.GetAllocatedBytesForCurrentThread();
        var genericAdmitted = genericCache.TryAdmit(
            "src/Fixture.cs",
            "checksum",
            genericSymbols,
            hadRegexTimeout: false);
        var genericAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread()
            - allocatedBeforeGenericAdmission;

        var allocatedBeforeOwnedAdmission =
            GC.GetAllocatedBytesForCurrentThread();
        var ownedAdmitted = ownedCache.TryAdmitOwned(
            "src/Fixture.cs",
            "checksum",
            ownedSymbols,
            hadRegexTimeout: false);
        var ownedAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread()
            - allocatedBeforeOwnedAdmission;

        Assert.True(genericAdmitted);
        Assert.True(ownedAdmitted);
        Assert.True(
            genericAllocatedBytes - ownedAllocatedBytes
            >= minimumAllocationSavings,
            $"Expected owned admission to save at least {minimumAllocationSavings} bytes; "
            + $"generic={genericAllocatedBytes}, owned={ownedAllocatedBytes}.");

        Assert.True(genericCache.TryTake(
            "src/Fixture.cs",
            "checksum",
            out var genericArtifact));
        Assert.NotSame(genericSymbols, genericArtifact.Symbols);
        Assert.NotSame(genericSymbols[0], genericArtifact.Symbols[0]);
        var cachedGenericName = genericArtifact.Symbols[0].Name;
        genericSymbols[0].Name = "mutated-after-admission";
        Assert.Equal(cachedGenericName, genericArtifact.Symbols[0].Name);

        Assert.True(ownedCache.TryTake(
            "src/Fixture.cs",
            "checksum",
            out var ownedArtifact));
        Assert.Same(ownedSymbols, ownedArtifact.Symbols);
        Assert.Same(ownedSymbols[0], ownedArtifact.Symbols[0]);
    }

    [Fact]
    public void BuildWorkspaceSymbols_OwnedArtifactsKeepMaterializedLookupsIsolated()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(
            "csharp_prepass_owned_artifact");
        var sourcePath = TestProjectHelper.WriteTextFile(
            project.Root,
            "src/Contracts.cs",
            """
            namespace Demo;
            public interface IShape<T>
            {
                static abstract T Create(T value);
            }
            public enum Shade
            {
                Red,
            }
            public static class Tokens
            {
                public const int Answer = 42;
            }
            """);
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        var indexer = new FileIndexer(project.Root, ignoreCase: false);
        var target = CSharpStaticInterfacePrepass.FileTarget.Create(
            project.Root,
            sourcePath,
            "csharp");

        var baseline = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
            writer,
            indexer,
            [target],
            includeExistingSymbols: false);
        var cache = new CSharpPrepassSymbolArtifactCache();
        var compact = CSharpStaticInterfacePrepass.BuildWorkspaceSymbols(
            writer,
            indexer,
            [target],
            includeExistingSymbols: false,
            symbolArtifactCache: cache);

        Assert.NotEmpty(baseline.Symbols);
        Assert.Empty(compact.Symbols);
        Assert.Equal(
            FlattenStaticInterfaceLookups(baseline.StaticInterfaceMemberLookups!),
            FlattenStaticInterfaceLookups(compact.StaticInterfaceMemberLookups!));
        var compactQualifiedLookups =
            FlattenQualifiedPatternLookups(compact.QualifiedPatternLookups!);
        Assert.Equal(
            FlattenQualifiedPatternLookups(baseline.QualifiedPatternLookups!),
            compactQualifiedLookups);
        var compactStaticInterfaceLookups =
            FlattenStaticInterfaceLookups(compact.StaticInterfaceMemberLookups!);
        Assert.Contains(
            compactStaticInterfaceLookups,
            item => item.Contains("IShape:Create:", StringComparison.Ordinal));
        Assert.Contains(
            compactQualifiedLookups,
            item => item.StartsWith("enum:Red:Shade:", StringComparison.Ordinal));
        Assert.Contains(
            compactQualifiedLookups,
            item => item.StartsWith("constant:Answer:Tokens:", StringComparison.Ordinal));

        var checksum = indexer.BuildRecord(sourcePath).record.Checksum;
        Assert.NotNull(checksum);
        Assert.True(cache.TryTake(target.IndexPath, checksum, out var artifact));
        var contract = Assert.Single(
            artifact.Symbols,
            symbol => symbol.Name == "Create" && symbol.ContainerName == "IShape");
        contract.Name = "MutatedCreate";
        contract.ContainerName = "MutatedShape";
        contract.Signature = "mutated";
        var enumMember = Assert.Single(
            artifact.Symbols,
            symbol => symbol.Name == "Red" && symbol.ContainerName == "Shade");
        enumMember.Name = "MutatedRed";
        enumMember.ContainerName = "MutatedShade";
        var constant = Assert.Single(
            artifact.Symbols,
            symbol => symbol.Name == "Answer" && symbol.ContainerName == "Tokens");
        constant.Name = "MutatedAnswer";
        constant.ContainerName = "MutatedTokens";

        Assert.Equal(
            compactStaticInterfaceLookups,
            FlattenStaticInterfaceLookups(compact.StaticInterfaceMemberLookups!));
        Assert.Equal(
            compactQualifiedLookups,
            FlattenQualifiedPatternLookups(compact.QualifiedPatternLookups!));
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

    private static void WarmUpArtifactAdmissionsForAllocationMeasurement()
    {
        var genericCache = new CSharpPrepassSymbolArtifactCache();
        Assert.True(genericCache.TryAdmit(
            "warmup-generic.cs",
            "checksum",
            CreateMinimalSymbols(1),
            hadRegexTimeout: false));
        Assert.True(genericCache.TryTake(
            "warmup-generic.cs",
            "checksum",
            out _));

        var ownedCache = new CSharpPrepassSymbolArtifactCache();
        var ownedSymbols = CreateMinimalSymbols(1);
        Assert.True(ownedCache.TryAdmitOwned(
            "warmup-owned.cs",
            "checksum",
            ownedSymbols,
            hadRegexTimeout: false));
        Assert.True(ownedCache.TryTake(
            "warmup-owned.cs",
            "checksum",
            out _));
    }

    private static List<SymbolRecord> CreateMinimalSymbols(int count)
    {
        var symbols = new List<SymbolRecord>(count);
        for (var index = 0; index < count; index++)
        {
            symbols.Add(new SymbolRecord
            {
                Kind = "function",
                Name = "Fixture",
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
            });
        }

        return symbols;
    }

    private static string[] FlattenStaticInterfaceLookups(
        ReferenceExtractor.CSharpStaticInterfaceMemberLookups lookups)
        => lookups.ContractsByType
            .SelectMany(pair => pair.Value.Select(contract =>
                $"{pair.Key}:{contract.Name}:{contract.Kind}:{contract.ParameterShape}:{contract.ReturnTypeShape}"))
            .Concat(lookups.InterfaceGenericParameters.SelectMany(pair =>
                pair.Value.Select(parameter => $"{pair.Key}:generic:{parameter}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] FlattenQualifiedPatternLookups(
        ReferenceExtractor.CSharpQualifiedPatternLookups lookups)
        => lookups.EnumMemberLookup
            .SelectMany(pair => pair.Value.Select(target =>
                $"enum:{pair.Key}:{target.EnumName}:{target.QualifiedEnumName}:{target.AllowShortNameFallback}"))
            .Concat(lookups.ConstantPatternMemberLookup.SelectMany(pair =>
                pair.Value.Select(target =>
                    $"constant:{pair.Key}:{target.ContainerName}:{target.QualifiedContainerName}:{target.AllowShortNameFallback}")))
            .Concat(lookups.TypePatternLookup.SelectMany(pair =>
                pair.Value.Select(target =>
                    $"type:{pair.Key}:{target.ContainerName}:{target.QualifiedContainerName}:{target.AllowShortNameFallback}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

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
