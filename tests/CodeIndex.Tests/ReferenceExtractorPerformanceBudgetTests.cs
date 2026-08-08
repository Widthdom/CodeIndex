using System.Collections;
using System.Diagnostics;
using System.Text;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[CollectionDefinition(ReferenceExtractorPerformanceBudgetCollection.Name, DisableParallelization = true)]
public sealed class ReferenceExtractorPerformanceBudgetCollection
{
    public const string Name = "Reference extractor performance budget";
}

[Collection(ReferenceExtractorPerformanceBudgetCollection.Name)]
public sealed class ReferenceExtractorPerformanceBudgetTests
{
    [Fact]
    public void KotlinInfixScan_DoesNotEnumerateLargeKnownNameSet()
    {
        var names = Enumerable.Range(0, 2_000)
            .Select(index => $"customInfix{index}")
            .Append("knownTail");
        var knownNames = new EnumerationCountingReadOnlySet<string>(names, StringComparer.Ordinal);
        var calls = new List<string>();
        var markerlessLine = string.Join(' ', Enumerable.Repeat("ordinaryIdentifier", 1_000));

        KotlinReferenceExtractor.EmitInfixCallReferences(
            markerlessLine,
            markerlessLine,
            knownNames,
            (name, _) => calls.Add(name));
        const string positiveLine = "receiver knownTail operand";
        KotlinReferenceExtractor.EmitInfixCallReferences(
            positiveLine,
            positiveLine,
            knownNames,
            (name, _) => calls.Add(name));

        Assert.Equal(0, knownNames.EnumeratorCreationCount);
        Assert.Equal(["knownTail"], calls);
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void DockerStageScan_LongIrrelevantInstructions_AllocatesNothingWithinPracticalBudget()
    {
        const int iterationCount = 30_000;
        const string shortLine = "ARG class BasicTask this assignment defaultValue newValue";
        var longLine = shortLine + new string('x', 32_000);
        var references = new List<ReferenceRecord>();
        var seen = new ReferenceDedupeSet();
        var stageNames = new HashSet<string>(StringComparer.Ordinal) { "base" };

        for (var index = 0; index < 256; index++)
        {
            DockerfileReferenceExtractor.EmitStageReferences(
                shortLine, shortLine, shortLine, 1, references, seen, 1, stageNames, container: null);
            DockerfileReferenceExtractor.EmitStageReferences(
                longLine, longLine, longLine, 1, references, seen, 1, stageNames, container: null);
        }

        var shortMeasurement = Measure(shortLine);
        var longMeasurement = Measure(longLine);

        Assert.Empty(references);
        Assert.Equal(0, shortMeasurement.AllocatedBytes);
        Assert.Equal(0, longMeasurement.AllocatedBytes);
        var scalingAllowance = shortMeasurement.Elapsed * 4 + TimeSpan.FromMilliseconds(75);
        Assert.True(
            longMeasurement.Elapsed <= scalingAllowance,
            $"Long irrelevant Docker instructions took {longMeasurement.Elapsed.TotalMilliseconds:F1}ms versus "
            + $"{shortMeasurement.Elapsed.TotalMilliseconds:F1}ms for short instructions; expected prefix-only classification scaling.");
        var runawayBudget = TimeSpan.FromSeconds(5);
        Assert.True(
            longMeasurement.Elapsed < runawayBudget,
            $"Docker stage gating took {longMeasurement.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard.");

        (TimeSpan Elapsed, long AllocatedBytes) Measure(string line)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            for (var index = 0; index < iterationCount; index++)
            {
                DockerfileReferenceExtractor.EmitStageReferences(
                    line, line, line, 1, references, seen, 1, stageNames, container: null);
            }

            return (
                Stopwatch.GetElapsedTime(started),
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_CSharpJavaKotlinMarkerlessDecoyFiles_ProduceNoReferencesWithinPracticalBudget()
    {
        ReferenceExtractorWarmup.EnsurePerformanceWarmup();

        var fixtureContents = new[]
        {
            (Language: "csharp", Content: BuildBraceLanguageFixture("class BasicTask", useKotlinProperties: false)),
            (Language: "java", Content: BuildBraceLanguageFixture("class BasicTask", useKotlinProperties: false)),
            (Language: "kotlin", Content: BuildBraceLanguageFixture("class BasicTask", useKotlinProperties: true)),
        };
        var fixtures = fixtureContents
            .Select(fixture => (
                fixture.Language,
                fixture.Content,
                Symbols: SymbolExtractor.Extract(1, fixture.Language, fixture.Content)))
            .ToArray();

        foreach (var fixture in fixtures)
        {
            var warmupReferences = ReferenceExtractor.Extract(
                1, fixture.Language, fixture.Content, fixture.Symbols);
            Assert.Empty(warmupReferences);
        }

        var referencesByFixture = new IReadOnlyList<ReferenceRecord>[fixtures.Length];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < fixtures.Length; index++)
        {
            var fixture = fixtures[index];
            referencesByFixture[index] = ReferenceExtractor.Extract(
                1, fixture.Language, fixture.Content, fixture.Symbols);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        foreach (var references in referencesByFixture)
            Assert.Empty(references);

        // The fixture currently allocates about 15.9 MiB on the primary target. Keep enough
        // headroom for runtime variation while still catching a return to per-pattern work.
        const long allocationBudget = 24L * 1024 * 1024;
        Assert.True(
            allocatedBytes < allocationBudget,
            $"Markerless decoy extraction allocated {allocatedBytes:N0} bytes, expected < {allocationBudget:N0} bytes.");
        var runawayBudget = TimeSpan.FromSeconds(3);
        Assert.True(
            elapsed < runawayBudget,
            $"Markerless decoy extraction took {elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard.");

        static string BuildBraceLanguageFixture(string header, bool useKotlinProperties)
        {
            var builder = new StringBuilder();
            builder.AppendLine(header).AppendLine("{");
            for (var index = 0; index < 400; index++)
            {
                if (useKotlinProperties)
                {
                    builder.Append("    val assignment").Append(index).AppendLine(": Int = 0");
                    builder.Append("    val defaultValue").Append(index).AppendLine(": Int = 0");
                    builder.Append("    val newValue").Append(index).AppendLine(": Int = 0");
                    builder.Append("    val thisValue").Append(index).AppendLine(": Int = 0");
                    builder.Append("    val caseInsensitive").Append(index).AppendLine(": Int = 0");
                }
                else
                {
                    builder.Append("    int assignment").Append(index).AppendLine(";");
                    builder.Append("    int defaultValue").Append(index).AppendLine(";");
                    builder.Append("    int newValue").Append(index).AppendLine(";");
                    builder.Append("    int thisValue").Append(index).AppendLine(";");
                    builder.Append("    int caseInsensitive").Append(index).AppendLine(";");
                }
            }
            return builder.AppendLine("}").ToString();
        }
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_CSharpLargePlainCallFile_CompletesWithinPracticalBudget()
    {
        ReferenceExtractorWarmup.EnsurePerformanceWarmup();

        const int callerCount = 500;
        var builder = new StringBuilder();
        builder.AppendLine("class App {");
        builder.AppendLine("    void Target() { }");
        for (var index = 0; index < callerCount; index++)
            builder.Append("    void Caller").Append(index).AppendLine("() { Target(); }");
        builder.AppendLine("}");
        var content = builder.ToString();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var stopwatch = Stopwatch.StartNew();
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        stopwatch.Stop();

        Assert.Contains(references, reference => reference.SymbolName == "Target" && reference.ContainerName == "Caller0");
        Assert.Contains(references, reference => reference.SymbolName == "Target" && reference.ContainerName == $"Caller{callerCount - 1}");
        var runawayBudget = TimeSpan.FromSeconds(5);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large C# plain call reference extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extract_CSharpLargeMethodWithManyLocals_CompletesWithinPracticalBudget()
    {
        ReferenceExtractorWarmup.EnsurePerformanceWarmup();

        const int localCount = 1_000;
        var builder = new StringBuilder();
        builder.AppendLine("class Demo");
        builder.AppendLine("{");
        builder.AppendLine("    int Run(int input)");
        builder.AppendLine("    {");
        builder.AppendLine("        var result = input;");
        for (var i = 0; i < localCount; i++)
        {
            builder.Append("        var value").Append(i).Append(" = result + ").Append(i).AppendLine(";");
            builder.Append("        result += value").Append(i).AppendLine(";");
        }
        builder.AppendLine("        return Helper(result);");
        builder.AppendLine("    }");
        builder.AppendLine("    int Helper(int value) => value;");
        builder.AppendLine("}");
        var content = builder.ToString();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);

        var stopwatch = Stopwatch.StartNew();
        var references = ReferenceExtractor.Extract(1, "csharp", content, symbols);
        stopwatch.Stop();

        Assert.Contains(references, reference => reference.SymbolName == "Helper" && reference.ReferenceKind == "call");
        var runawayBudget = TimeSpan.FromSeconds(5);
        Assert.True(
            stopwatch.Elapsed < runawayBudget,
            $"Large C# method reference extraction took {stopwatch.Elapsed.TotalSeconds:F2}s, expected < {runawayBudget.TotalSeconds:F0}s runaway guard budget.");
    }

    private sealed class EnumerationCountingReadOnlySet<T> : IReadOnlySet<T>
        where T : notnull
    {
        private readonly HashSet<T> values;

        public EnumerationCountingReadOnlySet(IEnumerable<T> values, IEqualityComparer<T> comparer)
            => this.values = new HashSet<T>(values, comparer);

        public int EnumeratorCreationCount { get; private set; }
        public int Count => values.Count;

        public bool Contains(T item) => values.Contains(item);
        public bool IsProperSubsetOf(IEnumerable<T> other) => values.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<T> other) => values.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<T> other) => values.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<T> other) => values.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<T> other) => values.Overlaps(other);
        public bool SetEquals(IEnumerable<T> other) => values.SetEquals(other);

        public IEnumerator<T> GetEnumerator()
        {
            EnumeratorCreationCount++;
            return values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
