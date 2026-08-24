using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Performance smoke tests for large datasets (10K+ files).
/// 大規模データ（10K+ファイル）のパフォーマンススモークテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class PerformanceTests : IDisposable
{
    private readonly string _dbDir;
    private readonly string _dbPath;
    private readonly string _projectRoot;
    private readonly DbContext _db;

    public PerformanceTests()
    {
        _dbDir = TestProjectHelper.CreateTempProject("codeindex_perf");
        _dbPath = Path.Combine(_dbDir, "codeindex.db");
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_perf_smoke");
        _db = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        _db.InitializeSchema();
    }

    [Fact]
    public void ReferenceBatchTransactions_RepositoryScaleAtomicFileScopeEliminatesControlledSqlScopes()
    {
        // Model the repository snapshot's 321,352 references across 856 files without
        // allocating the rows themselves. The 5/6-batch distribution keeps large-file
        // batching in the contract instead of reducing every file to one SQL scope.
        // 参照row自体を確保せず、自己snapshotの321,352 refs / 856 filesを5/6 batch分布で固定する。
        var referenceCountsByFile = Enumerable.Repeat(386, 729)
            .Concat(Enumerable.Repeat(315, 80))
            .Concat(Enumerable.Repeat(314, 47))
            .ToArray();

        Assert.Equal(856, referenceCountsByFile.Length);
        Assert.Equal(321_352, referenceCountsByFile.Sum());
        Assert.Equal(
            5_009L,
            DbWriter.CountReferenceBatchTransactionScopesForTesting(
                referenceCountsByFile,
                atomicFileScope: false));
        Assert.Equal(
            0L,
            DbWriter.CountReferenceBatchTransactionScopesForTesting(
                referenceCountsByFile,
                atomicFileScope: true));
    }

    [ManualPerformanceFact]
    public void Insert10KFiles_CompletesInReasonableTime()
    {
        var writer = new DbWriter(_db.Connection);
        var sw = Stopwatch.StartNew();

        // Insert 10,000 files with minimal content / 10,000ファイルを最小限の内容で挿入
        using var tx = writer.BeginTransaction();
        for (int i = 0; i < 10_000; i++)
        {
            writer.UpsertFile(new FileRecord
            {
                Path = $"src/module{i / 100}/file{i}.cs",
                Lang = "csharp",
                Size = 100 + i,
                Lines = 10,
                Modified = DateTime.UtcNow,
                Checksum = $"hash{i:X8}",
            });
        }
        tx.Commit();
        sw.Stop();

        // Should complete in under 10 seconds (typically < 2s on modern hardware)
        // 10秒以内に完了すべき（通常は現代のハードウェアで2秒未満）
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"Insert 10K files took {sw.Elapsed.TotalSeconds:F1}s");

        var (files, _, _, _) = writer.GetCounts();
        Assert.Equal(10_000, files);
    }

    [ManualPerformanceFact]
    public void AuthoritativeFreshRawBulkInsert_ReportsProviderParityElapsedAndAllocations()
    {
        const int RowCount = 10_000;
        var chunks = Enumerable.Range(0, RowCount)
            .Select(index => new ChunkRecord
            {
                FileId = 1,
                ChunkIndex = index,
                StartLine = index + 1,
                EndLine = index + 1,
                Content = index == 0 ? "雪😀a\0β" : $"chunk_{index}",
            })
            .ToArray();
        var symbols = Enumerable.Range(0, RowCount)
            .Select(index => new SymbolRecord
            {
                FileId = 1,
                Kind = "function",
                Name = $"target_{index}",
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
                Signature = index % 2 == 0 ? null : $"void target_{index}()",
            })
            .ToArray();
        var issues = Enumerable.Range(0, RowCount)
            .Select(index => new FileIssue
            {
                Path = "src/benchmark.cs",
                Kind = "benchmark",
                Line = index + 1,
                Message = $"issue {index}",
                Origin = index % 2 == 0 ? null : "benchmark",
                Severity = index % 2 == 0 ? null : "warning",
            })
            .ToArray();
        var references = Enumerable.Range(0, RowCount)
            .Select(index => new ReferenceRecord
            {
                FileId = 1,
                SymbolName = $"target_{index}",
                ReferenceKind = "call",
                Line = index + 1,
                Column = 1,
                SpanLength = index % 2 == 0 ? 0 : 8,
                Context = $"target_{index}();",
                ContainerKind = index % 2 == 0 ? null : "function",
                ContainerName = index % 2 == 0 ? null : "caller",
            })
            .ToArray();
        var providerSamples = new List<RawBulkInsertBenchmarkSample>();
        var rawSamples = new List<RawBulkInsertBenchmarkSample>();

        for (var iteration = 0; iteration < 4; iteration++)
        {
            var rawFirst = iteration % 2 != 0;
            var first = RunRawBulkInsertBenchmark(
                useRawBindings: rawFirst,
                chunks,
                symbols,
                issues,
                references);
            var second = RunRawBulkInsertBenchmark(
                useRawBindings: !rawFirst,
                chunks,
                symbols,
                issues,
                references);
            Assert.Equal(first.Snapshot, second.Snapshot);
            if (iteration == 0)
                continue;

            (rawFirst ? rawSamples : providerSamples).Add(first);
            (rawFirst ? providerSamples : rawSamples).Add(second);
        }

        Assert.Equal(3, providerSamples.Count);
        Assert.Equal(3, rawSamples.Count);
        Assert.All(
            providerSamples.Concat(rawSamples),
            sample => Assert.Equal(providerSamples[0].Snapshot, sample.Snapshot));
        foreach (var stage in new[] { "files", "chunks", "symbols", "issues", "references", "finalize", "total" })
        {
            var providerElapsed = Median(providerSamples.Select(sample => sample.Stages[stage].ElapsedTicks));
            var rawElapsed = Median(rawSamples.Select(sample => sample.Stages[stage].ElapsedTicks));
            var providerAllocated = Median(providerSamples.Select(sample => sample.Stages[stage].AllocatedBytes));
            var rawAllocated = Median(rawSamples.Select(sample => sample.Stages[stage].AllocatedBytes));
            Console.WriteLine(
                $"authoritative-fresh bulk stage={stage} "
                + $"provider_ms={TicksToMilliseconds(providerElapsed):F3} raw_ms={TicksToMilliseconds(rawElapsed):F3} "
                + $"provider_allocated={providerAllocated:N0} raw_allocated={rawAllocated:N0}");
        }
    }

    private static RawBulkInsertBenchmarkSample RunRawBulkInsertBenchmark(
        bool useRawBindings,
        IReadOnlyList<ChunkRecord> chunks,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<FileIssue> issues,
        IReadOnlyList<ReferenceRecord> references)
    {
        var root = TestProjectHelper.CreateTempProject(
            useRawBindings ? "cdidx_raw_binding_benchmark" : "cdidx_provider_binding_benchmark");
        try
        {
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(root, "codeindex.db"));
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            using var graph = writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = writer.BeginTransaction();
            using var rawScope = useRawBindings
                ? writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None)
                : null;
            var stages = new Dictionary<string, RawBulkInsertBenchmarkStage>(StringComparer.Ordinal);
            var totalStart = Stopwatch.GetTimestamp();
            var totalAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
            long fileId = 0;
            stages["files"] = Measure(() => fileId = writer.InsertNewFile(new FileRecord
            {
                Path = "src/benchmark.cs",
                Lang = "csharp",
                Size = 1_000_000,
                Lines = chunks.Count,
                Checksum = "benchmark",
                Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc)
                    .AddTicks(1_234_567),
            }));
            Assert.Equal(1L, fileId);
            stages["chunks"] = Measure(() => writer.InsertChunks(chunks));
            stages["symbols"] = Measure(() => writer.InsertSymbols(symbols));
            stages["issues"] = Measure(() => writer.InsertIssuesForNewFile(fileId, issues));
            stages["references"] = Measure(() =>
                writer.InsertReferencesForNewFilesInAtomicFileScope(
                    references,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None));
            stages["finalize"] = Measure(() => rawScope?.Complete());
            stages["total"] = new RawBulkInsertBenchmarkStage(
                Stopwatch.GetTimestamp() - totalStart,
                GC.GetAllocatedBytesForCurrentThread() - totalAllocatedStart);
            transaction.Commit();

            using var snapshotCommand = db.Connection.CreateCommand();
            snapshotCommand.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM files),
                    (SELECT COUNT(*) FROM chunks),
                    (SELECT COUNT(*) FROM symbols),
                    (SELECT COUNT(*) FROM file_issues),
                    (SELECT COUNT(*) FROM reference_lines),
                    (SELECT COUNT(*) FROM symbol_references),
                    (SELECT hex(CAST(path AS BLOB)) FROM files WHERE id = 1),
                    (SELECT hex(CAST(modified AS BLOB)) FROM files WHERE id = 1),
                    (SELECT generated FROM files WHERE id = 1),
                    (SELECT hex(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0),
                    (SELECT COUNT(*) FROM symbols WHERE signature IS NULL),
                    (SELECT COUNT(*) FROM symbol_references WHERE context IS NULL)
                """;
            using var reader = snapshotCommand.ExecuteReader();
            Assert.True(reader.Read());
            var snapshot = string.Join(
                '|',
                Enumerable.Range(0, reader.FieldCount)
                    .Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)));
            return new RawBulkInsertBenchmarkSample(stages, snapshot);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(root);
        }

        static RawBulkInsertBenchmarkStage Measure(Action action)
        {
            var allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            var elapsedStart = Stopwatch.GetTimestamp();
            action();
            return new RawBulkInsertBenchmarkStage(
                Stopwatch.GetTimestamp() - elapsedStart,
                GC.GetAllocatedBytesForCurrentThread() - allocatedStart);
        }
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.Order().ToArray();
        Assert.NotEmpty(ordered);
        return ordered[ordered.Length / 2];
    }

    private static double TicksToMilliseconds(long ticks)
        => ticks * 1_000d / Stopwatch.Frequency;

    private sealed record RawBulkInsertBenchmarkSample(
        IReadOnlyDictionary<string, RawBulkInsertBenchmarkStage> Stages,
        string Snapshot);

    private readonly record struct RawBulkInsertBenchmarkStage(
        long ElapsedTicks,
        long AllocatedBytes);

    [ManualPerformanceFact]
    public void Search1KFileIndex_ReturnsInReasonableTime()
    {
        var writer = new DbWriter(_db.Connection);

        // Seed 1000 files with searchable content / 1000ファイルに検索可能な内容を投入
        using var tx = writer.BeginTransaction();
        for (int i = 0; i < 1_000; i++)
        {
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = $"src/mod{i / 50}/service{i}.cs",
                Lang = "csharp",
                Size = 500,
                Lines = 20,
                Modified = DateTime.UtcNow,
                Checksum = $"hash{i:X8}",
            });
            writer.InsertChunks([new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 20,
                Content = $"public class Service{i} {{ public void Execute() {{ }} }}",
            }]);
        }
        tx.Commit();

        var reader = new DbReader(_db.Connection);
        var sw = Stopwatch.StartNew();
        var results = reader.Search("Execute", limit: 20);
        sw.Stop();

        // FTS5 search should be fast even with many files / FTS5検索は多数ファイルでも高速であるべき
        Assert.True(sw.Elapsed.TotalMilliseconds < 500, $"Search took {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.True(results.Count > 0);
    }

    [ManualPerformanceFact]
    public void ExtractLargeSameLineSymbolFixture_CompletesInReasonableTime()
    {
        var content = string.Join(
            "\n",
            Enumerable.Range(0, 2_000).Select(i => $"public partial class C{i} {{ public partial class N{i} {{ }} }}"));

        var sw = Stopwatch.StartNew();
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        sw.Stop();

        // The hot path should stay close to linear even when a file contains a large
        // number of symbols. This is a manual smoke test rather than a CI gate.
        // hot path は、大量の symbol を含むファイルでもほぼ線形であるべき。
        // CI の強制ゲートではなく、手動 smoke test として残す。
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"Extraction took {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(4_000, symbols.Count);
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void CiPerformanceSmoke_IndexAndSearchSmallFixture_StaysWithinBudget()
    {
        WritePerformanceSmokeFixture(_projectRoot, fileCount: 120);
        var writer = new DbWriter(_db.Connection);

        var indexElapsed = MeasureElapsed(() => IndexScannedFiles(_projectRoot, writer));
        var (files, chunks, symbols, references) = writer.GetCounts();

        Assert.Equal(120, files);
        Assert.True(chunks >= 120, $"Expected at least one chunk per file, got {chunks}");
        Assert.True(symbols >= 240, $"Expected class and method symbols from the smoke fixture, got {symbols}");
        Assert.True(references > 0, "Expected reference rows from the smoke fixture.");
        // Hosted Windows runners have wider filesystem and process-scheduling variance.
        // Keep a bounded platform budget without weakening other lanes.
        // hosted Windows runner は filesystem / process scheduling の変動幅が大きいため、
        // 他 lane の基準は維持したまま platform 別の上限を設定する。
        var indexBudget = OperatingSystem.IsWindows()
            ? TimeSpan.FromSeconds(45)
            : TimeSpan.FromSeconds(20);
        Assert.True(
            indexElapsed < indexBudget,
            $"CI performance smoke indexing took {indexElapsed.TotalSeconds:F1}s (budget {indexBudget.TotalSeconds:F0}s)");

        var reader = new DbReader(_db.Connection);
        List<SearchResult> results = [];
        var searchElapsed = MeasureElapsed(() => results = reader.Search("Execute42", limit: 5));

        Assert.Contains(results, result => result.Path.EndsWith("service42.cs", StringComparison.Ordinal));
        Assert.True(searchElapsed < TimeSpan.FromSeconds(2), $"CI performance smoke search took {searchElapsed.TotalMilliseconds:F0}ms");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReusableStatSnapshot_OnePassMaterialization_StaysWithinAllocationBudget()
    {
        const int fileCount = 1_024;
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var writer = new DbWriter(_db.Connection);
        using (var transaction = writer.BeginTransaction())
        {
            for (var index = 0; index < fileCount; index++)
            {
                writer.UpsertFile(new FileRecord
                {
                    Path = $"src/reusable/file{index:D4}.cs",
                    Lang = "csharp",
                    Size = index + 1,
                    Lines = 1,
                    Modified = modified,
                    Checksum = $"checksum-{index:D4}",
                });
            }
            transaction.Commit();
        }
        writer.StampSymbolExtractorVersions(["csharp"]);

        _ = writer.LoadReusableIndexedFileStats(
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10,
            initialCapacity: fileCount);

        IReadOnlyDictionary<string, ReusableIndexedFileStat>? snapshot = null;
        var allocatedBytes = MeasureAllocatedBytes(() =>
            snapshot = writer.LoadReusableIndexedFileStats(
                maxSymbolsPerFile: 10,
                maxReferencesPerFile: 10,
                initialCapacity: fileCount));

        Assert.Equal(fileCount, snapshot!.Count);
        Assert.True(allocatedBytes < 380_000, $"Reusable stat snapshot allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SymbolExtraction_CsharpHotPath_StaysWithinAllocationBudget()
    {
        var content = BuildCSharpHotPathFixture(typeCount: 120);
        _ = SymbolExtractor.Extract(1, "csharp", content);

        var allocatedBytes = MeasureAllocatedBytes(() => SymbolExtractor.Extract(1, "csharp", content));

        Assert.True(allocatedBytes < 18_000_000, $"Symbol extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SymbolExtraction_PatternContext_StaysWithinAllocationBudget()
    {
        const string content = "package sample\n\nfunc run() {}\n";
        const int extractionCount = 4_096;
        var symbols = SymbolExtractor.Extract(1, "go", content);
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "run");
        for (var iteration = 0; iteration < 32; iteration++)
            _ = SymbolExtractor.Extract(1, "go", content);

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var iteration = 0; iteration < extractionCount; iteration++)
                _ = SymbolExtractor.Extract(1, "go", content);
        });

        Assert.True(
            allocatedBytes < 16_730_000,
            $"Pattern extraction allocated {allocatedBytes:N0} bytes for {extractionCount:N0} files");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void StructuralLineMasking_JvmTripleStrings_StaysWithinAllocationBudget()
    {
        string[] lines = ["val value = \"\"\"literal\"\"\""];
        const int maskingCount = 4_096;
        var masked = StructuralLineMasker.MaskLines("kotlin", lines);
        Assert.DoesNotContain("literal", masked[0], StringComparison.Ordinal);
        for (var iteration = 0; iteration < 32; iteration++)
            _ = StructuralLineMasker.MaskLines("kotlin", lines);

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var iteration = 0; iteration < maskingCount; iteration++)
                _ = StructuralLineMasker.MaskLines("kotlin", lines);
        });

        Assert.True(
            allocatedBytes < 1_000_000,
            $"JVM triple-string masking allocated {allocatedBytes:N0} bytes for {maskingCount:N0} calls");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SymbolExtraction_CsharpSameLineRecoveryDecisions_StayWithinAllocationBudget()
    {
        const int propertyCount = 1_000;
        var content = "public sealed class Fixture\n{\n"
            + string.Join('\n', Enumerable.Range(0, propertyCount).Select(index =>
                $"    public static Dictionary<string, List<(int Left, int Right)>> Property{index} {{ get; }} = new();"))
            + "\n}";
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        Assert.Equal(propertyCount, symbols.Count(symbol => symbol.Kind == "property"));

        var allocatedBytes = MeasureAllocatedBytes(() => SymbolExtractor.Extract(1, "csharp", content));

        Assert.True(
            allocatedBytes < 6_800_000,
            $"C# same-line recovery extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void CSharpStaticInterfacePrepass_LargeSemanticProbeStaysWithinAllocationBudget()
    {
        const int repeats = 12;
        var filler = new string('x', 576 * 1024);
        var semanticNegative = $"class C {{ const string S = \"interface I {{ static abstract int M(); }}\"; {filler} }}";
        var contract = $"interface I {{ {filler} static abstract int M(); }}";
        Assert.False(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(semanticNegative));
        Assert.True(CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(contract));

        var negativeResult = false;
        var contractResult = false;
        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var iteration = 0; iteration < repeats; iteration++)
            {
                negativeResult |= CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(semanticNegative);
                contractResult |= CSharpStaticInterfacePrepass.MayContainCSharpStaticInterfaceContract(contract);
            }
        });

        Assert.False(negativeResult);
        Assert.True(contractResult);
        Assert.True(allocatedBytes < 4_096, $"C# static-interface semantic probes allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void CSharpPrepassWorkspaceSegments_AvoidFlattenedReferenceBuffers()
    {
        const int segmentCount = 32;
        const int symbolsPerSegment = 4_096;
        IReadOnlyList<SymbolRecord>?[] segments = Enumerable.Range(0, segmentCount)
            .Select(_ => (IReadOnlyList<SymbolRecord>)Enumerable.Repeat(
                new SymbolRecord { Kind = "function", Name = "Ordinary" },
                symbolsPerSegment).ToArray())
            .ToArray();
        var prefix = Array.Empty<SymbolRecord>();

        var warmup = new CSharpStaticInterfacePrepass.CSharpWorkspaceSymbolSegments(
            prefix,
            segments,
            segmentCount * symbolsPerSegment);
        Assert.Equal(segmentCount * symbolsPerSegment, warmup.Count);
        Assert.Equal(warmup.Count, warmup.Count(static _ => true));

        CSharpStaticInterfacePrepass.CSharpWorkspaceSymbolSegments? view = null;
        var observed = 0;
        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            view = new CSharpStaticInterfacePrepass.CSharpWorkspaceSymbolSegments(
                prefix,
                segments,
                segmentCount * symbolsPerSegment);
            foreach (var symbol in view)
            {
                if (symbol.Kind == "function")
                    observed++;
            }
        });

        Assert.NotNull(view);
        Assert.Equal(segmentCount * symbolsPerSegment, observed);
        Assert.True(
            allocatedBytes < 16_384,
            $"Segmented C# prepass workspace allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void CSharpStaticInterfaceLookup_UnrelatedInterfacesStayWithinAllocationBudget()
    {
        const int unrelatedInterfaceCount = 20_000;
        var workspaceSymbols = new List<SymbolRecord>(unrelatedInterfaceCount + 2);
        for (var index = 0; index < unrelatedInterfaceCount; index++)
        {
            workspaceSymbols.Add(new SymbolRecord
            {
                Kind = "interface",
                Name = $"IUnrelated{index}",
                ContainerKind = "namespace",
                ContainerName = "Demo",
                ContainerQualifiedName = "Demo",
                Signature = $"public interface IUnrelated{index}<T>",
            });
        }

        workspaceSymbols.Add(new SymbolRecord
        {
            Kind = "interface",
            Name = "IContract",
            ContainerKind = "namespace",
            ContainerName = "Demo",
            ContainerQualifiedName = "Demo",
            Signature = "public interface IContract<T>",
        });
        workspaceSymbols.Add(new SymbolRecord
        {
            Kind = "function",
            Name = "Create",
            Signature = "static abstract T Create();",
            ReturnType = "T",
            ContainerKind = "interface",
            ContainerName = "IContract",
        });

        _ = ReferenceExtractor.BuildCSharpStaticInterfaceMemberLookups(workspaceSymbols);
        _ = ReferenceExtractor.BuildCSharpQualifiedPatternLookups(workspaceSymbols);
        ReferenceExtractor.CSharpStaticInterfaceMemberLookups? lookups = null;
        ReferenceExtractor.CSharpQualifiedPatternLookups? qualifiedLookups = null;
        var allocatedBytes = MeasureAllocatedBytes(
            () => lookups = ReferenceExtractor.BuildCSharpStaticInterfaceMemberLookups(workspaceSymbols));
        var qualifiedAllocatedBytes = MeasureAllocatedBytes(
            () => qualifiedLookups = ReferenceExtractor.BuildCSharpQualifiedPatternLookups(workspaceSymbols));

        Assert.True(
            allocatedBytes < 64_000,
            $"C# static-interface lookup allocated {allocatedBytes:N0} bytes for unrelated interfaces");
        Assert.Single(lookups!.ContractsByType);
        Assert.Single(lookups.InterfaceGenericParameters);
        Assert.Equal(unrelatedInterfaceCount + 1, qualifiedLookups!.TypePatternLookup.Count);
        Assert.True(
            qualifiedAllocatedBytes < 7_000_000,
            $"C# qualified workspace lookup allocated {qualifiedAllocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SymbolExtraction_JavaScriptTypeScriptDenseIdentifiers_StaysWithinAllocationBudget()
    {
        var content = BuildJavaScriptTypeScriptDenseIdentifierFixture(functionCount: 180);
        _ = SymbolExtractor.Extract(1, "javascript", content);
        _ = SymbolExtractor.Extract(1, "typescript", content);

        var javaScriptAllocatedBytes = MeasureAllocatedBytes(
            () => SymbolExtractor.Extract(1, "javascript", content));
        var typeScriptAllocatedBytes = MeasureAllocatedBytes(
            () => SymbolExtractor.Extract(1, "typescript", content));

        Assert.True(
            javaScriptAllocatedBytes < 3_300_000
                && typeScriptAllocatedBytes < 4_000_000
                && javaScriptAllocatedBytes + typeScriptAllocatedBytes < 7_100_000,
            $"Dense JS/TS identifier extraction allocated JavaScript={javaScriptAllocatedBytes:N0}, TypeScript={typeScriptAllocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SymbolExtraction_JavaScriptTypeScriptScopeLexing_ReusesSanitizedSnapshot()
    {
        var content = BuildJavaScriptTypeScriptScopeLexingFixture(statementCount: 1_200);
        var javaScriptSymbol = Assert.Single(
            SymbolExtractor.Extract(1, "javascript", content));
        var typeScriptSymbol = Assert.Single(
            SymbolExtractor.Extract(1, "typescript", content));
        Assert.All([javaScriptSymbol, typeScriptSymbol], symbol =>
        {
            Assert.Equal("function", symbol.Kind);
            Assert.Equal("inspect", symbol.Name);
        });

        var javaScriptAllocatedBytes = MeasureAllocatedBytes(
            () => SymbolExtractor.Extract(1, "javascript", content));
        var typeScriptAllocatedBytes = MeasureAllocatedBytes(
            () => SymbolExtractor.Extract(1, "typescript", content));

        Assert.True(
            javaScriptAllocatedBytes < 6_000_000
                && typeScriptAllocatedBytes < 8_200_000
                && javaScriptAllocatedBytes + typeScriptAllocatedBytes < 14_100_000,
            $"Scope-heavy JS/TS extraction allocated JavaScript={javaScriptAllocatedBytes:N0}, TypeScript={typeScriptAllocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_CsharpHotPath_StaysWithinAllocationBudget()
    {
        var content = BuildCSharpHotPathFixture(typeCount: 80);
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        _ = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var allocatedBytes = MeasureAllocatedBytes(() => ReferenceExtractor.Extract(1, "csharp", content, symbols));

        Assert.True(allocatedBytes < 6_000_000, $"Reference extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_MaskedMultilinePayloads_StayWithinAllocationBudget()
    {
        var payloadLine = $"    {new string('x', 4_096)} TargetInsidePayload();    ";
        var fixtures = new[]
        {
            (
                Language: "csharp",
                Content: $$""""
                    public sealed class C
                    {
                        private const string Payload = """
                    {{string.Join('\n', Enumerable.Repeat(payloadLine, 256))}}
                        """;
                        public void Run() => CSharpTarget();
                    }
                    """",
                ExpectedTarget: "CSharpTarget"),
            (
                Language: "java",
                Content: $$""""
                    public final class C {
                        private static final String PAYLOAD = """
                    {{string.Join('\n', Enumerable.Repeat(payloadLine, 256))}}
                        """;
                        public void run() { JavaTarget(); }
                    }
                    """",
                ExpectedTarget: "JavaTarget"),
            (
                Language: "typescript",
                Content: $$"""
                    const payload = `
                    {{string.Join('\n', Enumerable.Repeat(payloadLine, 256))}}
                    `;
                    export function run() { TypeScriptTarget(); }
                    """,
                ExpectedTarget: "TypeScriptTarget"),
        };

        long allocatedBytes = 0;
        foreach (var fixture in fixtures)
        {
            var symbols = SymbolExtractor.Extract(1, fixture.Language, fixture.Content);
            _ = ReferenceExtractor.Extract(1, fixture.Language, fixture.Content, symbols);

            List<ReferenceRecord>? references = null;
            allocatedBytes += MeasureAllocatedBytes(
                () => references = ReferenceExtractor.Extract(
                    1,
                    fixture.Language,
                    fixture.Content,
                    symbols));

            Assert.Contains(
                references!,
                reference => reference.ReferenceKind == "call"
                    && reference.SymbolName == fixture.ExpectedTarget);
        }

        Assert.True(
            allocatedBytes < 23_000_000,
            $"Masked multiline payload extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void CppHeaderDetection_LargeSample_DoesNotMaterializeLineArrays()
    {
        var content = string.Join(
            '\n',
            Enumerable.Range(0, 8_192)
                .Select(index => $"struct record_{index} {{ int value; }};"));
        _ = FileIndexer.ContainsCppHeaderMarkerForTesting(content);

        var allocatedBytes = MeasureAllocatedBytes(
            () => FileIndexer.ContainsCppHeaderMarkerForTesting(content));

        Assert.True(
            allocatedBytes < 1_024,
            $"C/C++ header detection allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void DelimitedSpanWalking_DenseExtractorLists_DoesNotAllocate()
    {
        var content = string.Join(
            ',',
            Enumerable.Range(0, 8_192)
                .Select(index => $"  value_{index}  "));
        var expectedLength = Enumerable.Range(0, 8_192)
            .Sum(index => $"value_{index}".Length);
        _ = MeasureDelimitedEntries(content);

        (int Count, int TotalLength) result = default;
        var allocatedBytes = MeasureAllocatedBytes(
            () => result = MeasureDelimitedEntries(content));

        Assert.Equal((8_192, expectedLength), result);
        Assert.True(
            allocatedBytes < 1_024,
            $"Delimited span walking allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void FunctionalSpanMembership_RepeatedCallFiltering_DoesNotAllocate()
    {
        var spans = Enumerable.Range(0, 128)
            .Select(index => (Start: index * 8, End: index * 8 + 4))
            .ToArray();
        var foundCount = 0;
        _ = ReferenceExtractor.ContainsFunctionalSpan(spans, 16);

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < 10_000; index++)
            {
                if (ReferenceExtractor.ContainsFunctionalSpan(
                        spans,
                        index % (spans[^1].End + 1)))
                {
                    foundCount++;
                }
            }
        });

        Assert.True(foundCount > 0);
        Assert.True(
            allocatedBytes < 1_024,
            $"Functional span membership allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void HardwareScopeMembership_RepeatedIdentifierFiltering_DoesNotAllocate()
    {
        var bindings = Enumerable.Range(0, 128)
            .Select(index => new ShaderReferenceExtractor.BindingSite(
                $"resource_{index}",
                index * 4))
            .ToArray();
        var scopes = Enumerable.Range(0, 128)
            .Select(index => new ShaderReferenceExtractor.ScopedResource(
                $"kernel_{index}",
                HeaderEndLine: index,
                BodyEndLine: index + 16,
                FirstBodyColumn: 8))
            .ToArray();
        var missingNames = Enumerable.Range(0, 8)
            .Select(index => $"missing_{index}")
            .ToArray();
        var matchCount = 0;
        _ = ShaderReferenceExtractor.ContainsBindingSite(bindings, "resource_64", 256);
        _ = ShaderReferenceExtractor.ContainsActiveScopedResource(
            scopes,
            "kernel_64",
            70,
            12);

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < 10_000; index++)
            {
                if (ShaderReferenceExtractor.ContainsBindingSite(
                        bindings,
                        missingNames[index % missingNames.Length],
                        index))
                {
                    matchCount++;
                }

                if (ShaderReferenceExtractor.ContainsActiveScopedResource(
                        scopes,
                        "kernel_64",
                        70,
                        12))
                {
                    matchCount++;
                }
            }
        });

        Assert.Equal(10_000, matchCount);
        Assert.True(
            allocatedBytes < 1_024,
            $"Hardware scope membership allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SpanCharacterSearch_RepeatedLongMetadataCandidates_DoesNotAllocate()
    {
        var candidate = new string('x', 4_096);
        var matches = 0;
        _ = SpanCharacterSearch.ContainsControl(candidate);
        _ = SpanCharacterSearch.ContainsWhitespace(candidate);

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < 2_048; index++)
            {
                if (SpanCharacterSearch.ContainsControl(candidate)
                    || SpanCharacterSearch.ContainsWhitespace(candidate))
                {
                    matches++;
                }
            }
        });

        Assert.Equal(0, matches);
        Assert.True(
            allocatedBytes < 1_024,
            $"Span character classification allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void SourceLineSplitting_LargeFiles_AvoidsSeparatorIndexArrays()
    {
        Assert.Equal([""], SourceLineSplitter.Split(string.Empty));
        Assert.Equal(["line", ""], SourceLineSplitter.Split("line\n"));
        Assert.Equal(["first", "", "third"], SourceLineSplitter.Split("first\n\nthird"));

        var content = string.Join(
            '\n',
            Enumerable.Range(0, 8_192)
                .Select(index => $"line_{index:D5}_payload"));
        _ = SourceLineSplitter.Split(content);

        string[]? lines = null;
        var allocatedBytes = MeasureAllocatedBytes(
            () => lines = SourceLineSplitter.Split(content));
        var genericSplitAllocatedBytes = MeasureAllocatedBytes(
            () => lines = content.Split('\n'));

        Assert.Equal(8_192, lines!.Length);
        Assert.Equal("line_00000_payload", lines[0]);
        Assert.Equal("line_08191_payload", lines[^1]);
        Assert.True(
            allocatedBytes < 610_000,
            $"Source line splitting allocated {allocatedBytes:N0} bytes");
        Assert.True(
            allocatedBytes + 50_000 < genericSplitAllocatedBytes,
            $"Source line splitting allocated {allocatedBytes:N0} bytes versus {genericSplitAllocatedBytes:N0} bytes for generic splitting");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void FunctionalTerminatorChecks_LongPaddedLines_DoNotAllocate()
    {
        var periodLine = $"value.{new string(' ', 4_096)}";
        var heredocLine = $"END{new string(' ', 4_096)}";
        var matchCount = 0;
        _ = ReferenceExtractor.TrimmedFunctionalLineEndsWith(periodLine, '.');
        _ = ReferenceExtractor.TrimmedFunctionalLineEquals(heredocLine, "END");

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < 4_096; index++)
            {
                if (ReferenceExtractor.TrimmedFunctionalLineEndsWith(
                        periodLine,
                        '.'))
                {
                    matchCount++;
                }
                if (ReferenceExtractor.TrimmedFunctionalLineEquals(
                        heredocLine,
                        "END"))
                {
                    matchCount++;
                }
            }
        });

        Assert.Equal(8_192, matchCount);
        Assert.True(
            allocatedBytes < 1_024,
            $"Functional terminator checks allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void TrimmedSuffixChecks_LongDeclarationLines_DoNotAllocate()
    {
        var semicolonLine = $"declaration;{new string(' ', 4_096)}";
        var commaLine = $"selector,{new string(' ', 4_096)}";
        var matchCount = 0;
        _ = SpanCharacterSearch.EndsWithAfterTrim(semicolonLine, ';');

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < 4_096; index++)
            {
                if (SpanCharacterSearch.EndsWithAfterTrim(
                        semicolonLine,
                        ';'))
                {
                    matchCount++;
                }
                if (SpanCharacterSearch.EndsWithAfterTrim(commaLine, ','))
                    matchCount++;
            }
        });

        Assert.Equal(8_192, matchCount);
        Assert.True(
            allocatedBytes < 1_024,
            $"Trimmed suffix checks allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void FunctionalReferenceExtraction_CallFreeLines_AvoidsEmptySpanLists()
    {
        const int lineCount = 4_096;
        var fixtures = new[]
        {
            (
                Language: "erlang",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, lineCount)
                        .Select(index => $"value_{index} = {index}."))),
            (
                Language: "ocaml",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, lineCount)
                        .Select(index => $"let value_{index} = {index}"))),
            (
                Language: "raku",
                Content: string.Join(
                    '\n',
                    Enumerable.Range(0, lineCount)
                        .Select(index => $"my $value_{index} = {index};"))),
        }
        .Select(fixture => (
            fixture.Language,
            fixture.Content,
            Symbols: SymbolExtractor.Extract(
                1,
                fixture.Language,
                fixture.Content)))
        .ToArray();
        foreach (var fixture in fixtures)
        {
            _ = ReferenceExtractor.Extract(
                1,
                fixture.Language,
                fixture.Content,
                fixture.Symbols);
        }

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            foreach (var fixture in fixtures)
            {
                _ = ReferenceExtractor.Extract(
                    1,
                    fixture.Language,
                    fixture.Content,
                    fixture.Symbols);
            }
        });

        Assert.True(
            allocatedBytes < 14_000_000,
            $"Call-free functional extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_CSharpNoAliasDenseReferences_StaysWithinAllocationBudget()
    {
        var content = BuildCSharpNoAliasReferenceFixture(referenceCount: 12_000);
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        _ = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        List<ReferenceRecord>? references = null;
        var allocatedBytes = MeasureAllocatedBytes(
            () => references = ReferenceExtractor.Extract(1, "csharp", content, symbols));

        Assert.Equal(12_000, references!.Count(reference =>
            reference.SymbolName == "Target"
            && reference.ReferenceKind == "call"));
        Assert.True(
            allocatedBytes < 62_000_000,
            $"No-alias dense reference extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void MutualRecursion_DenseRepeatedQualifiedNames_StaysWithinAllocationBudget()
    {
        ReferenceExtractor.MarkMutualRecursionReferences(
            BuildDenseMutualReferenceFixture("Warmup.Alpha", "Warmup.Beta", pairCount: 1));
        var csharpReferences = BuildDenseMutualReferenceFixture(
            "Example.Namespace.Alpha",
            "Example.Namespace.Beta",
            pairCount: 6_000);
        var pythonReferences = BuildDenseMutualReferenceFixture(
            "example.module.alpha",
            "example.module.beta",
            pairCount: 6_000);

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            ReferenceExtractor.MarkMutualRecursionReferences(csharpReferences);
            ReferenceExtractor.MarkMutualRecursionReferences(pythonReferences);
        });

        Assert.All(csharpReferences, reference => Assert.True(reference.IsMutualRecursion));
        Assert.All(pythonReferences, reference => Assert.True(reference.IsMutualRecursion));
        Assert.True(
            allocatedBytes < 10_000,
            $"Dense mutual-recursion marking allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void CSharpAliasCompaction_DenseDuplicates_StaysWithinAllocationBudget()
    {
        ReferenceExtractor.CompactCSharpUsingAliasReferences(
            BuildCSharpAliasDuplicateReferenceFixture(uniqueReferenceCount: 1),
            "csharp");
        var references = BuildCSharpAliasDuplicateReferenceFixture(uniqueReferenceCount: 6_000);

        var allocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.CompactCSharpUsingAliasReferences(references, "csharp"));

        Assert.Equal(6_000, references.Count);
        Assert.All(references, reference => Assert.StartsWith("alias-", reference.Context, StringComparison.Ordinal));
        Assert.True(
            allocatedBytes < 1_000_000,
            $"Dense C# alias compaction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_RepeatedSymbolMembership_StaysWithinAllocationBudget()
    {
        var pythonContent = BuildPythonImportedTypeCallFixture(importCount: 120);
        var pythonSymbols = SymbolExtractor.Extract(1, "python", pythonContent);
        _ = ReferenceExtractor.Extract(1, "python", pythonContent, pythonSymbols);

        var pythonAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "python", pythonContent, pythonSymbols));

        var csharpContent = BuildCSharpPrivatePropertyReceiverFixture(typeCount: 120);
        var csharpSymbols = SymbolExtractor.Extract(1, "csharp", csharpContent);
        _ = ReferenceExtractor.Extract(1, "csharp", csharpContent, csharpSymbols);

        var csharpAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "csharp", csharpContent, csharpSymbols));

        Assert.True(pythonAllocatedBytes < 2_000_000, $"Python reference extraction allocated {pythonAllocatedBytes:N0} bytes");
        Assert.True(csharpAllocatedBytes < 6_000_000, $"C# reference extraction allocated {csharpAllocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_RepeatedContainerLookup_StaysWithinAllocationBudget()
    {
        var csharpContent = BuildCSharpPrivatePropertyReceiverFixture(typeCount: 240);
        var csharpSymbols = SymbolExtractor.Extract(1, "csharp", csharpContent);
        _ = ReferenceExtractor.Extract(1, "csharp", csharpContent, csharpSymbols);
        var csharpAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "csharp", csharpContent, csharpSymbols));

        var yamlContent = BuildGitHubActionsJobFixture(jobCount: 240);
        var yamlSymbols = SymbolExtractor.Extract(1, "yaml", yamlContent);
        _ = ReferenceExtractor.Extract(1, "yaml", yamlContent, yamlSymbols);
        var yamlAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "yaml", yamlContent, yamlSymbols));

        Assert.True(
            csharpAllocatedBytes < 8_000_000 && yamlAllocatedBytes < 2_500_000,
            $"Container lookup allocated {csharpAllocatedBytes:N0} bytes for C# and {yamlAllocatedBytes:N0} bytes for YAML");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Extraction_DenseDelimitedLists_StayWithinAllocationBudget()
    {
        var pythonContent = BuildPythonCommaImportFixture(itemCount: 400);
        var pythonSymbols = SymbolExtractor.Extract(1, "python", pythonContent);
        _ = ReferenceExtractor.Extract(1, "python", pythonContent, pythonSymbols);
        var pythonAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "python", pythonContent, pythonSymbols));

        var yamlContent = BuildGitHubActionsNeedsListFixture(dependencyCount: 400);
        var yamlSymbols = SymbolExtractor.Extract(1, "yaml", yamlContent);
        _ = ReferenceExtractor.Extract(1, "yaml", yamlContent, yamlSymbols);
        var yamlAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "yaml", yamlContent, yamlSymbols));

        var jsonContent = BuildJsonRepositoryPathFixture(pathCount: 400);
        var jsonSymbols = SymbolExtractor.Extract(1, "json", jsonContent);
        _ = ReferenceExtractor.Extract(1, "json", jsonContent, jsonSymbols);
        var jsonAllocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(1, "json", jsonContent, jsonSymbols));

        var fortranContent = BuildFortranProcedureListFixture(procedureCount: 400);
        _ = SymbolExtractor.Extract(1, "fortran", fortranContent);
        var fortranAllocatedBytes = MeasureAllocatedBytes(
            () => SymbolExtractor.Extract(1, "fortran", fortranContent));

        var totalAllocatedBytes = pythonAllocatedBytes + yamlAllocatedBytes + jsonAllocatedBytes + fortranAllocatedBytes;
        Assert.True(
            pythonAllocatedBytes < 1_150_000
                && yamlAllocatedBytes < 1_400_000
                && jsonAllocatedBytes < 470_000
                && fortranAllocatedBytes < 340_000
                && totalAllocatedBytes < 3_180_000,
            $"Dense list extraction allocated Python={pythonAllocatedBytes:N0}, YAML={yamlAllocatedBytes:N0}, JSON={jsonAllocatedBytes:N0}, Fortran={fortranAllocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Utf8LineStarts_DenseInput_AllocatesOnlyFinalOffsetArray()
    {
        const int lineCount = 100_000;
        var utf8 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("value\n", lineCount)));
        int[]? lineStarts = null;

        var allocatedBytes = MeasureAllocatedBytes(
            () => lineStarts = Utf8LineStarts.Build(utf8));

        Assert.NotNull(lineStarts);
        Assert.Equal(lineCount + 1, lineStarts.Length);
        Assert.Equal(0, lineStarts[0]);
        Assert.Equal(utf8.Length, lineStarts[^1]);
        Assert.True(
            allocatedBytes < 450_000,
            $"Dense UTF-8 line offsets allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_PrologCallFreeRules_AvoidsPerLineLists()
    {
        const int ruleCount = 8_000;
        var contentBuilder = new StringBuilder(ruleCount * 40);
        for (var index = 0; index < ruleCount; index++)
        {
            contentBuilder
                .Append("rule_")
                .Append(index)
                .Append("(Value) :- Value = Value.\n");
        }
        var content = contentBuilder.ToString();
        var symbols = SymbolExtractor.Extract(1, "prolog", content);
        _ = ReferenceExtractor.Extract(1, "prolog", content, symbols);

        List<ReferenceRecord>? references = null;
        var allocatedBytes = MeasureAllocatedBytes(
            () => references = ReferenceExtractor.Extract(1, "prolog", content, symbols));

        Assert.NotNull(references);
        Assert.DoesNotContain(references, reference => reference.ReferenceKind == "call");
        Assert.True(
            allocatedBytes < 24_000_000,
            $"Call-free Prolog reference extraction allocated {allocatedBytes:N0} bytes");
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceExtraction_BoundedDenseFSharpPipeline_StopsAtCapacity()
    {
        var contentBuilder = new StringBuilder("seed");
        for (var i = 0; i < 4_000; i++)
            contentBuilder.Append(" |> Func").Append(i);
        var content = contentBuilder.ToString();
        var symbols = SymbolExtractor.Extract(1, "fsharp", content);

        var references = ReferenceExtractor.Extract(
            1,
            "fsharp",
            content,
            symbols,
            maxReferenceCount: 1);
        var allocatedBytes = MeasureAllocatedBytes(
            () => ReferenceExtractor.Extract(
                1,
                "fsharp",
                content,
                symbols,
                maxReferenceCount: 1));

        Assert.Single(references);
        Assert.True(
            allocatedBytes < 1_000_000,
            $"Bounded dense F# reference extraction allocated {allocatedBytes:N0} bytes");
    }

    [Fact]
    public void ReferenceMatchEnumeration_BoundedListDoesNotRequestMatchAfterCapacity()
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount: 1);
        var sourceMoveNextCount = 0;
        using var matches = ReferenceExtractor
            .EnumerateReferenceMatches(EnumerateMatches(), references)
            .GetEnumerator();

        Assert.True(matches.MoveNext());
        Assert.Equal(1, sourceMoveNextCount);

        references.Add(new ReferenceRecord());

        Assert.False(matches.MoveNext());
        Assert.Equal(1, sourceMoveNextCount);

        IEnumerable<Match> EnumerateMatches()
        {
            sourceMoveNextCount++;
            yield return Match.Empty;

            sourceMoveNextCount++;
            throw new InvalidOperationException("The bounded enumerator requested an unused match.");
        }
    }

    [Fact]
    public void ReferenceMatchEnumeration_ConcreteRegexDoesNotRequestSuffixAfterCapacity()
    {
        var regex = new BoundedRegex(
            @"token|(?:a+)+$",
            default,
            TimeSpan.FromMilliseconds(25));
        var input = "token " + new string('a', 100_000) + "!";
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount: 1);
        using var capture = BoundedRegex.CaptureTimeouts("csharp", "bounded_regex_test");
        var matches = ReferenceExtractor
            .EnumerateReferenceMatches(regex, input, references)
            .GetEnumerator();

        try
        {
            Assert.True(matches.MoveNext());
            Assert.Equal("token", matches.Current.Value);
            references.Add(new ReferenceRecord());

            Assert.False(matches.MoveNext());
            Assert.False(capture.HasTimeouts);
        }
        finally
        {
            matches.Dispose();
        }
    }

    [Fact]
    public void ReferenceMatchEnumeration_BelowCapacity_DoesNotAllocateWrapperEnumerators()
    {
        const int scanCount = 10_000;
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount: scanCount + 1);
        var source = new ReusableMatchEnumerable();
        var observedMatches = 0;

        Scan();
        observedMatches = 0;
        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < scanCount; index++)
                Scan();
        });

        Assert.Equal(scanCount, observedMatches);
        Assert.True(
            allocatedBytes < 1_024,
            $"Below-cap reference match wrappers allocated {allocatedBytes:N0} bytes");

        void Scan()
        {
            foreach (var _ in ReferenceExtractor.EnumerateReferenceMatches(source, references))
                observedMatches++;
        }
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void BoundedRegexEnumeration_DirectNoMatchScansDoNotAllocateEnumerators()
    {
        const int scanCount = 10_000;
        const string input = "alpha beta gamma";
        var regex = new BoundedRegex(
            @"\bmissing\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var observedMatches = 0;

        for (var index = 0; index < 128; index++)
            Scan();
        observedMatches = 0;

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < scanCount; index++)
                Scan();
        });

        Assert.Equal(0, observedMatches);
        Assert.True(
            allocatedBytes < 1_024,
            $"Direct bounded-regex enumeration allocated {allocatedBytes:N0} bytes");

        void Scan()
        {
            foreach (var _ in BoundedRegex.EnumerateMatches(regex, input))
                observedMatches++;
        }
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceMatchEnumeration_PrefilledCapacityDoesNotAllocateOrLookAhead()
    {
        const int scanCount = 10_000;
        const string input = "token";
        var regex = new BoundedRegex(
            @"token",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount: 1);
        references.Add(new ReferenceRecord());
        var observedMatches = 0;

        for (var index = 0; index < 128; index++)
            Scan();
        observedMatches = 0;

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            for (var index = 0; index < scanCount; index++)
                Scan();
        });

        Assert.Equal(0, observedMatches);
        Assert.True(
            allocatedBytes < 1_024,
            $"Prefilled-cap reference match enumeration allocated {allocatedBytes:N0} bytes");

        void Scan()
        {
            foreach (var _ in ReferenceExtractor.EnumerateReferenceMatches(regex, input, references))
                observedMatches++;
        }
    }

#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void ReferenceDedupe_DenseLongIdentities_StayWithinAllocationBudget()
    {
        const int keyCount = 10_000;
        var name = new string('N', 128);
        var container = new SymbolRecord
        {
            Kind = "function",
            Name = new string('C', 128),
        };

        var allocatedBytes = MeasureAllocatedBytes(() =>
        {
            var seen = new ReferenceDedupeSet(keyCount);
            for (var index = 0; index < keyCount; index++)
            {
                seen.Add(ReferenceExtractor.CreateReferenceDedupeKey(
                    1,
                    "csharp",
                    index + 1,
                    17,
                    "type_reference",
                    name,
                    container));
            }
        });

        Assert.True(allocatedBytes < 1_000_000, $"Reference dedupe allocated {allocatedBytes:N0} bytes");
    }

    private static long MeasureAllocatedBytes(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static (int Count, int TotalLength) MeasureDelimitedEntries(string content)
    {
        var count = 0;
        var totalLength = 0;
        foreach (var entry in new DelimitedSpanEnumerable(
                     content.AsSpan(),
                     ',',
                     trimEntries: true,
                     removeEmptyEntries: true))
        {
            count++;
            totalLength += entry.Length;
        }

        return (count, totalLength);
    }

    private static TimeSpan MeasureElapsed(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed;
    }

    private static void WritePerformanceSmokeFixture(string projectRoot, int fileCount)
    {
        for (var i = 0; i < fileCount; i++)
        {
            var directory = Path.Combine(projectRoot, "src", $"module{i / 20}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"service{i}.cs"), BuildPerformanceSmokeSource(i));
        }
    }

    private static void IndexScannedFiles(string projectRoot, DbWriter writer)
    {
        var indexer = new FileIndexer(projectRoot);
        foreach (var filePath in indexer.ScanFiles())
        {
            var (record, content, rawBytes, _) = indexer.BuildRecordWithRawBytes(filePath);
            var fileId = writer.UpsertFile(record);
            writer.DeleteFileData(fileId);
            writer.InsertChunks(ChunkSplitter.Split(fileId, content));
            var symbols = SymbolExtractor.Extract(fileId, record.Lang, content, record.Path);
            writer.InsertSymbols(symbols);
            writer.InsertReferences(ReferenceExtractor.Extract(fileId, record.Lang, content, symbols, record.Path));
            writer.InsertIssues(fileId, FileIndexer.ValidateContent(record.Path, rawBytes, content));
        }
    }

    private static string BuildPerformanceSmokeSource(int index) => $$"""
        namespace PerfSmoke.Module{{index / 20}};

        public sealed class Service{{index}}
        {
            private readonly Dependency{{index}} dependency = new();

            public int Execute{{index}}(int value)
            {
                var transformed = dependency.Transform(value);
                return transformed + {{index}};
            }
        }

        public sealed class Dependency{{index}}
        {
            public int Transform(int value) => value * 2;
        }
        """;

    private static string BuildCSharpHotPathFixture(int typeCount)
    {
        return string.Join(
            "\n",
            Enumerable.Range(0, typeCount).Select(i => $$"""
                public sealed class Service{{i}}
                {
                    private readonly Dependency{{i}} dependency;
                    public Service{{i}}(Dependency{{i}} dependency) => this.dependency = dependency;
                    public Result{{i}} Execute(Request{{i}} request)
                    {
                        var value = dependency.Transform(request.Value);
                        return new Result{{i}}(value);
                    }
                }
                """));
    }

    private static string BuildCSharpNoAliasReferenceFixture(int referenceCount)
    {
        var content = new StringBuilder(referenceCount * 20)
            .AppendLine("public sealed class DenseReferences")
            .AppendLine("{")
            .AppendLine("    public void Run()")
            .AppendLine("    {");
        for (var index = 0; index < referenceCount; index++)
            content.AppendLine("        Target();");
        return content.AppendLine("    }").AppendLine("}").ToString();
    }

    private static List<ReferenceRecord> BuildDenseMutualReferenceFixture(
        string callerName,
        string calleeName,
        int pairCount)
    {
        var references = new List<ReferenceRecord>(pairCount * 2);
        for (var index = 0; index < pairCount; index++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = 1,
                SymbolName = calleeName,
                ReferenceKind = "call",
                Line = (index * 2) + 1,
                Column = 1,
                ContainerKind = "function",
                ContainerName = callerName,
            });
            references.Add(new ReferenceRecord
            {
                FileId = 1,
                SymbolName = callerName,
                ReferenceKind = "call",
                Line = (index * 2) + 2,
                Column = 1,
                ContainerKind = "function",
                ContainerName = calleeName,
            });
        }

        return references;
    }

    private static List<ReferenceRecord> BuildCSharpAliasDuplicateReferenceFixture(int uniqueReferenceCount)
    {
        var references = new List<ReferenceRecord>(uniqueReferenceCount * 2);
        for (var index = 0; index < uniqueReferenceCount; index++)
        {
            references.Add(new ReferenceRecord
            {
                FileId = 1,
                SymbolName = "TargetType",
                ReferenceKind = "instantiate",
                Line = index + 1,
                Column = 17,
                Context = $"alias-{index}",
                ContainerKind = "function",
                ContainerName = "Build",
            });
            references.Add(new ReferenceRecord
            {
                FileId = 1,
                SymbolName = "TargetType",
                ReferenceKind = "instantiate",
                Line = index + 1,
                Column = 17,
                Context = $"duplicate-{index}",
                ContainerKind = "function",
                ContainerName = "Build",
            });
        }

        return references;
    }

    private static string BuildJavaScriptTypeScriptDenseIdentifierFixture(int functionCount)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < functionCount; index++)
        {
            builder.Append("export function handler").Append(index)
                .Append("(inputValue").Append(index).Append(", divisorValue").Append(index).AppendLine(") {")
                .Append("  const normalizedValue").Append(index).Append(" = inputValue").Append(index).AppendLine(";")
                .Append("  if (normalizedValue").Append(index).Append(") return /[{}]/.test(normalizedValue").Append(index).AppendLine(");")
                .Append("  const quotientValue").Append(index).Append(" = normalizedValue").Append(index)
                .Append(" / divisorValue").Append(index).AppendLine(";")
                .Append("  return quotientValue").Append(index).AppendLine(";")
                .AppendLine("}");
        }

        return builder.ToString();
    }

    private static string BuildJavaScriptTypeScriptScopeLexingFixture(int statementCount)
    {
        var builder = new StringBuilder("export function inspect(input) {\n  let total = 0;\n");
        for (var index = 0; index < statementCount; index++)
        {
            builder.Append("  if (input) { total += /[{}]/.test(input) ? ")
                .Append(index)
                .Append(" : 0; }")
                .Append('\n');
        }

        return builder.Append("  return total;\n}")
            .Append('\n')
            .ToString();
    }

    private static string BuildPythonImportedTypeCallFixture(int importCount)
    {
        var content = new StringBuilder(importCount * 120);
        for (var index = 0; index < importCount; index++)
        {
            content.Append("from models").Append(index).Append(" import Model").Append(index)
                .Append(" as Alias").Append(index).Append('\n');
            content.Append("import services").Append(index).Append(" as svc").Append(index).Append('\n');
        }
        content.Append("def build_all():\n");
        for (var index = 0; index < importCount; index++)
        {
            content.Append("    Alias").Append(index).Append("()\n");
            content.Append("    svc").Append(index).Append(".Service").Append(index).Append("()\n");
        }
        return content.ToString();
    }

    private static string BuildCSharpPrivatePropertyReceiverFixture(int typeCount)
    {
        var content = new StringBuilder(typeCount * 180);
        for (var index = 0; index < typeCount; index++)
        {
            content.Append("class Service").Append(index).Append(" {\n")
                .Append("    private Worker").Append(index).Append(" Worker").Append(index).Append(" { get; }\n")
                .Append("    void Run() { Worker").Append(index).Append(".Execute(); }\n")
                .Append("}\n")
                .Append("class Worker").Append(index).Append(" { public void Execute() { } }\n");
        }
        return content.ToString();
    }

    private static string BuildGitHubActionsJobFixture(int jobCount)
    {
        var content = new StringBuilder(jobCount * 120);
        content.Append("name: Dense workflow\njobs:\n");
        for (var index = 0; index < jobCount; index++)
        {
            content.Append("  job").Append(index).Append(":\n");
            if (index > 0)
                content.Append("    needs: [job").Append(index - 1).Append("]\n");
            content.Append("    steps:\n")
                .Append("      - run: ./scripts/job").Append(index).Append(".sh\n");
        }
        return content.ToString();
    }

    private static string BuildPythonCommaImportFixture(int itemCount)
    {
        var content = new StringBuilder(itemCount * 60);
        content.Append("from models import ");
        for (var index = 0; index < itemCount; index++)
        {
            if (index > 0)
                content.Append(", ");
            content.Append("Model").Append(index).Append(" as Alias").Append(index);
        }
        content.Append("\ndef build_all():\n");
        for (var index = 0; index < itemCount; index++)
            content.Append("    Alias").Append(index).Append("()\n");
        return content.ToString();
    }

    private static string BuildGitHubActionsNeedsListFixture(int dependencyCount)
    {
        var content = new StringBuilder(dependencyCount * 70);
        content.Append("name: Dense needs\njobs:\n");
        for (var index = 0; index < dependencyCount; index++)
            content.Append("  job").Append(index).Append(":\n    steps:\n      - run: echo ready\n");
        content.Append("  aggregate:\n    needs: [");
        for (var index = 0; index < dependencyCount; index++)
        {
            if (index > 0)
                content.Append(", ");
            content.Append("job").Append(index);
        }
        content.Append("]\n    steps:\n      - run: echo done\n");
        return content.ToString();
    }

    private static string BuildJsonRepositoryPathFixture(int pathCount)
    {
        var content = new StringBuilder(pathCount * 50).Append("{\n");
        for (var index = 0; index < pathCount; index++)
        {
            content.Append("  \"path").Append(index).Append("\": \"src/module")
                .Append(index).Append("/file").Append(index).Append(".cs\"");
            content.Append(index + 1 == pathCount ? '\n' : ",\n");
        }
        return content.Append("}\n").ToString();
    }

    private static string BuildFortranProcedureListFixture(int procedureCount)
    {
        var content = new StringBuilder(procedureCount * 20)
            .Append("module dense_mod\n  interface\n    module procedure ");
        for (var index = 0; index < procedureCount; index++)
        {
            if (index > 0)
                content.Append(", ");
            content.Append("proc_").Append(index);
        }
        return content.Append("\n  end interface\nend module dense_mod\n").ToString();
    }

    private sealed class ReusableMatchEnumerable : IEnumerable<Match>, IEnumerator<Match>
    {
        private bool _moved;

        public Match Current => Match.Empty;

        object System.Collections.IEnumerator.Current => Current;

        public IEnumerator<Match> GetEnumerator()
        {
            _moved = false;
            return this;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public bool MoveNext()
        {
            if (_moved)
                return false;

            _moved = true;
            return true;
        }

        public void Reset() => _moved = false;

        public void Dispose()
        {
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        TestProjectHelper.DeleteDirectory(_dbDir);
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }
}
