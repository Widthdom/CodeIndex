using System.Reflection;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public sealed class SymbolExtractorCSharpRegexProbeTests
{
    private const int AllocationSampleCount = 5;
    private const long AllocationNoiseAllowanceBytes = 64 * 1024;

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
        Assert.True(
            optimizedMetrics.PropertyCandidateBuildCount
            < baselineMetrics.PropertyCandidateBuildCount);
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
    public void CSharpPhysicalInputNegativePrefix_RequiresContiguousNonTimeoutFailures_Issue5182()
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

        AssertNoMaterialAllocationRegression(
            "wrapped lookup caching",
            MeasureRepresentativeAllocatedBytes(content));
    }

    [Fact]
    public void Extract_CSharpLineStartStates_ReuseInitialLexerPassAndReduceAllocations()
    {
        var content = string.Join(
            '\n',
            Enumerable.Range(0, 32).Select(index => $$""""
                internal class Lexed{{index}}
                {
                    /* A multiline comment with declaration-shaped noise:
                       public int Hidden { get; set; }
                    */
                    private const string Verbatim = @"first
                { still literal }";
                    private const string Raw = """
                public int AlsoHidden { get; set; }
                """;
                    public int Value { get; set; }
                }
                """"));

        var baseline = Extract(content, applyOptimizations: false, out var baselineMetrics);
        var optimized = Extract(content, applyOptimizations: true, out var optimizedMetrics);

        AssertSymbolsEqual(baseline, optimized);
        Assert.Equal(32, optimized.Count(symbol => symbol.Kind == "class"));
        Assert.Equal(32, optimized.Count(symbol => symbol.Kind == "property" && symbol.Name == "Value"));
        Assert.DoesNotContain(optimized, symbol => symbol.Name is "Hidden" or "AlsoHidden");
        Assert.Equal(0, baselineMetrics.LineStartStateReuseCount);
        Assert.Equal(content.Split('\n').Length, optimizedMetrics.LineStartStateReuseCount);

        AssertNoMaterialAllocationRegression(
            "initial lexer-state reuse",
            MeasureRepresentativeAllocatedBytes(content));
    }

    [Fact]
    public void Extract_CSharpPropertyStructuralGate_PreservesPropertyFormsAndReducesAllocations()
    {
        var content = string.Join(
            '\n',
            Enumerable.Range(0, 48).Select(index => $$"""
                #region Type{{index}}
                internal class Shape{{index}}
                {
                    public (int Left, int Right) Pair
                    {
                        get;
                    }

                    public int Wrap {
                        get;
                    }

                    public void Run(int value) {
                    }
                }
                #endregion
                """));

        var baseline = Extract(content, applyOptimizations: false, out var baselineMetrics);
        var optimized = Extract(content, applyOptimizations: true, out var optimizedMetrics);

        AssertSymbolsEqual(baseline, optimized);
        Assert.Equal(48, optimized.Count(symbol => symbol.Kind == "property" && symbol.Name == "Pair"));
        Assert.Equal(48, optimized.Count(symbol => symbol.Kind == "property" && symbol.Name == "Wrap"));
        Assert.Equal(48, optimized.Count(symbol => symbol.Kind == "function" && symbol.Name == "Run"));
        Assert.Equal(0, baselineMetrics.PropertyStructuralShapeSkipCount);
        Assert.True(optimizedMetrics.PropertyStructuralShapeSkipCount > 0);
        Assert.True(
            optimizedMetrics.PropertyHeaderRegexAttemptCount
            < baselineMetrics.PropertyHeaderRegexAttemptCount);

        AssertNoMaterialAllocationRegression(
            "property structural gating",
            MeasureRepresentativeAllocatedBytes(content));
    }

    [Fact]
    public void AllocationRegressionComparison_UsesMedianNoiseAllowanceAndRejectsMaterialIncrease_Issue5244()
    {
        Assert.Equal(300, SelectMedian([500, 100, 300, 200, 400]));
        Assert.False(IsMaterialAllocationRegression(
            baselineAllocatedBytes: 1_000_000,
            optimizedAllocatedBytes: 1_000_000 + AllocationNoiseAllowanceBytes));
        Assert.True(IsMaterialAllocationRegression(
            baselineAllocatedBytes: 1_000_000,
            optimizedAllocatedBytes: 1_000_001 + AllocationNoiseAllowanceBytes));
        Assert.False(IsMaterialAllocationRegression(
            baselineAllocatedBytes: 1_000_000,
            optimizedAllocatedBytes: 900_000));
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

    private static (long Baseline, long Optimized) MeasureRepresentativeAllocatedBytes(string content)
    {
        _ = Extract(content, applyOptimizations: false, out _);
        _ = Extract(content, applyOptimizations: true, out _);

        var baselineSamples = new long[AllocationSampleCount];
        var optimizedSamples = new long[AllocationSampleCount];
        for (var sampleIndex = 0; sampleIndex < AllocationSampleCount; sampleIndex++)
        {
            if ((sampleIndex & 1) == 0)
            {
                baselineSamples[sampleIndex] = MeasureAllocatedBytes(content, applyOptimizations: false);
                optimizedSamples[sampleIndex] = MeasureAllocatedBytes(content, applyOptimizations: true);
            }
            else
            {
                optimizedSamples[sampleIndex] = MeasureAllocatedBytes(content, applyOptimizations: true);
                baselineSamples[sampleIndex] = MeasureAllocatedBytes(content, applyOptimizations: false);
            }
        }

        return (SelectMedian(baselineSamples), SelectMedian(optimizedSamples));
    }

    private static long SelectMedian(long[] samples)
    {
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static bool IsMaterialAllocationRegression(
        long baselineAllocatedBytes,
        long optimizedAllocatedBytes) =>
        optimizedAllocatedBytes > baselineAllocatedBytes
        && optimizedAllocatedBytes - baselineAllocatedBytes > AllocationNoiseAllowanceBytes;

    private static void AssertNoMaterialAllocationRegression(
        string optimization,
        (long Baseline, long Optimized) allocatedBytes)
    {
        var increase = Math.Max(0, allocatedBytes.Optimized - allocatedBytes.Baseline);
        Assert.False(
            IsMaterialAllocationRegression(allocatedBytes.Baseline, allocatedBytes.Optimized),
            $"Expected {optimization} not to materially increase allocations: "
            + $"optimized median={allocatedBytes.Optimized:N0}, "
            + $"baseline median={allocatedBytes.Baseline:N0}, "
            + $"increase={increase:N0}, "
            + $"noise allowance={AllocationNoiseAllowanceBytes:N0} bytes.");
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
