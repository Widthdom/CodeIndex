using System.Reflection;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class SymbolExtractorCSharpRegexProbeTests
{
    private static readonly PropertyInfo[] SymbolProperties = typeof(SymbolRecord)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        .OrderBy(property => property.Name, StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public void Extract_CSharpRegexProbeOptimizations_PreserveAdversarialOutputAndSameLineOffsets()
    {
        const string content = """
            namespace Probe;

            [BrokenAttribute(
            internal class Recovered
            {
                private const string Marker = "= ; }";
                public int Field;
                public int Initialized = 1;
                public int Property { get; set; }
                public int Expression => 42;

                public void WithDefault(int value =
                    42) { }

                static
                Recovered() { }
            }

            internal class Inline { public int SameLine; public void Run() { } }
            """;

        var baseline = Extract(content, applyOptimizations: false, out var baselineMetrics);
        var optimized = Extract(content, applyOptimizations: true, out var optimizedMetrics);

        Assert.Equal(29, SymbolProperties.Length);
        AssertSymbolsEqual(baseline, optimized);
        Assert.Contains(optimized, symbol => symbol.Kind == "class" && symbol.Name == "Recovered");

        var sameLine = Assert.Single(
            optimized.Where(symbol => symbol.Kind == "field" && symbol.Name == "SameLine"));
        Assert.Equal("Inline", sameLine.ContainerName);
        Assert.True(sameLine.StartColumn > 0);
        Assert.Equal(0, baselineMetrics.PhysicalInputNegativePrefixCacheHitCount);
        Assert.True(optimizedMetrics.PhysicalInputNegativePrefixCacheHitCount > 0);
        Assert.True(
            optimizedMetrics.DeclarationPatternRegexAttemptCount
            < baselineMetrics.DeclarationPatternRegexAttemptCount);
    }

    [Fact]
    public void Extract_CSharpRecoverablePatternNegativePrefix_IsolatesMergedInputs()
    {
        const string content = """
            using System.Collections.Generic;

            internal class Merged
            {
                public Dictionary<string,
                    List<int>> Values
                {
                    get;
                    set;
                }

                public void WithDefault(int value =
                    42)
                {
                }
            }
            """;

        var baseline = Extract(content, applyOptimizations: false, out _);
        var optimized = Extract(content, applyOptimizations: true, out _);

        AssertSymbolsEqual(baseline, optimized);
        Assert.Contains(
            optimized,
            symbol => symbol.Kind == "property" && symbol.Name == "Values");
        Assert.Contains(
            optimized,
            symbol => symbol.Kind == "function" && symbol.Name == "WithDefault");
    }

    [Fact]
    public void CSharpPhysicalInputNegativePrefix_RequiresContiguousNonTimeoutFailures()
    {
        var cache = new SymbolExtractor.CSharpPhysicalInputNegativePrefixCache();
        var timeoutRegex = new BoundedRegex("(a+)+$", default, TimeSpan.FromMilliseconds(1));
        var pathologicalInput = new string('a', 10_000) + "!";

        var timeoutResult = timeoutRegex.IsMatchWithTimeoutStatus(pathologicalInput, out var timedOut);
        cache.RecordFailedProbe(patternIndex: 0, timedOut);
        cache.RecordFailedProbe(patternIndex: 1, timedOut: false);

        Assert.False(timeoutResult);
        Assert.True(timedOut);
        Assert.False(cache.IsKnownNegative(0));
        Assert.False(cache.IsKnownNegative(1));

        cache.RecordFailedProbe(patternIndex: 0, timedOut: false);
        cache.RecordFailedProbe(patternIndex: 1, timedOut: true);

        Assert.True(cache.IsKnownNegative(0));
        Assert.False(cache.IsKnownNegative(1));

        cache.RecordFailedProbe(patternIndex: 1, timedOut: false);

        Assert.True(cache.IsKnownNegative(1));
    }

    [Fact]
    public void Extract_CSharpPropertyPrefixSuffixGate_SkipsCompletedLinesButKeepsDefaultArgumentPrefix()
    {
        var noise = string.Join(
            '\n',
            Enumerable.Range(0, 64).Select(index => $"    unrelated_token_{index};"));
        var content = $$"""
            internal class HotPath
            {
            {{noise}}

                public void WithDefault(int value =
                    42)
                {
                }
            }
            """;

        var baseline = Extract(content, applyOptimizations: false, out var baselineMetrics);
        var optimized = Extract(content, applyOptimizations: true, out var optimizedMetrics);

        AssertSymbolsEqual(baseline, optimized);
        Assert.Contains(optimized, symbol => symbol.Kind == "function" && symbol.Name == "WithDefault");
        Assert.Equal(0, baselineMetrics.PropertyPrefixSuffixSkipCount);
        Assert.True(optimizedMetrics.PropertyPrefixSuffixSkipCount >= 64);
        Assert.True(
            optimizedMetrics.PropertyHeaderRegexAttemptCount
            < baselineMetrics.PropertyHeaderRegexAttemptCount);
        Assert.True(optimizedMetrics.MethodHeaderRegexAttemptCount > 0);
    }

    [Fact]
    public void Extract_CSharpPlainFieldTerminatorGate_CoversNormalAndAttributeRecoveryInputs()
    {
        const string content = """
            [BrokenAttribute(
            internal class Recovered
            {
                public int Value;
                public int Initialized = 1;
                CandidateWithoutTerminator
            }
            """;

        var baseline = Extract(content, applyOptimizations: false, out var baselineMetrics);
        var optimized = Extract(content, applyOptimizations: true, out var optimizedMetrics);

        AssertSymbolsEqual(baseline, optimized);
        Assert.Contains(optimized, symbol => symbol.Kind == "class" && symbol.Name == "Recovered");
        Assert.Contains(optimized, symbol => symbol.Kind == "field" && symbol.Name == "Value");
        Assert.Equal(0, baselineMetrics.PlainFieldTerminatorSkipCount);
        Assert.Equal(0, baselineMetrics.RecoverablePlainFieldTerminatorSkipCount);
        Assert.True(optimizedMetrics.PlainFieldTerminatorSkipCount > 0);
        Assert.True(optimizedMetrics.RecoverablePlainFieldTerminatorSkipCount > 0);
        Assert.True(
            optimizedMetrics.PlainFieldRegexAttemptCount
            < baselineMetrics.PlainFieldRegexAttemptCount);
    }

    [Fact]
    public void Extract_CSharpWrappedModifierLookup_CachesSuccessAndNullAndUsesAsciiShapeGate()
    {
        const string content = """
            internal class Cache
            {
                static
                Cache() { }
            }

            internal class NoPrefix
            {
                int sentinel;
                Missing()
            }
            """;

        var baseline = Extract(content, applyOptimizations: false, out var baselineMetrics);
        var optimized = Extract(content, applyOptimizations: true, out var optimizedMetrics);

        AssertSymbolsEqual(baseline, optimized);
        Assert.Contains(optimized, symbol => symbol.Kind == "function" && symbol.Name == "Cache");
        Assert.True(optimizedMetrics.WrappedModifierAsciiShapeSkipCount > 0);
        Assert.True(
            optimizedMetrics.WrappedModifierLookupCount
            < baselineMetrics.WrappedModifierLookupCount);
        Assert.True(
            optimizedMetrics.WrappedModifierLineRegexAttemptCount
            < baselineMetrics.WrappedModifierLineRegexAttemptCount);
        Assert.True(
            optimizedMetrics.WrappedModifierPrefixMaterializationCount
            < baselineMetrics.WrappedModifierPrefixMaterializationCount);
        Assert.True(optimizedMetrics.WrappedModifierMatchInputMaterializationCount > 0);
    }

    [Fact]
    public void Extract_CSharpWrappedModifierLookup_ReducesAllocationsOnRepeatedWrappedConstructors()
    {
        var content = string.Join(
            '\n',
            Enumerable.Range(0, 48).Select(index => $$"""
                internal class Cache{{index}}
                {
                    static
                    Cache{{index}}() { }
                }
                """));

        _ = Extract(content, applyOptimizations: false, out _);
        _ = Extract(content, applyOptimizations: true, out _);

        var baselineAllocatedBytes = MeasureAllocatedBytes(content, applyOptimizations: false);
        var optimizedAllocatedBytes = MeasureAllocatedBytes(content, applyOptimizations: true);

        Assert.True(
            optimizedAllocatedBytes < baselineAllocatedBytes,
            $"Expected wrapped lookup caching to allocate less: "
            + $"optimized={optimizedAllocatedBytes:N0}, baseline={baselineAllocatedBytes:N0} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_CSharpRegexProbeOptimizations_PreserveCancellation(bool applyOptimizations)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => SymbolExtractor.ExtractForCSharpRegexProbeTesting(
                1,
                "internal class C { public int Value; }",
                applyOptimizations,
                out _,
                cancellationToken: cancellation.Token));
    }

    private static List<SymbolRecord> Extract(
        string content,
        bool applyOptimizations,
        out SymbolExtractor.CSharpRegexProbeMetrics metrics) =>
        SymbolExtractor.ExtractForCSharpRegexProbeTesting(
            1,
            content,
            applyOptimizations,
            out metrics);

    private static long MeasureAllocatedBytes(string content, bool applyOptimizations)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 3; iteration++)
            _ = Extract(content, applyOptimizations, out _);
        return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    }

    private static void AssertSymbolsEqual(
        IReadOnlyList<SymbolRecord> expected,
        IReadOnlyList<SymbolRecord> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var symbolIndex = 0; symbolIndex < expected.Count; symbolIndex++)
        {
            foreach (var property in SymbolProperties)
            {
                Assert.True(
                    Equals(property.GetValue(expected[symbolIndex]), property.GetValue(actual[symbolIndex])),
                    $"Symbol {symbolIndex} property {property.Name} differs.");
            }
        }
    }
}
