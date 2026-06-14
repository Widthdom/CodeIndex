using System.Diagnostics;
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
    private readonly string _dbPath;
    private readonly string _projectRoot;
    private readonly DbContext _db;

    public PerformanceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"codeindex_perf_{Guid.NewGuid():N}.db");
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_perf_smoke");
        _db = new DbContext(_dbPath);
        _db.InitializeSchema();
    }

    [Fact(Skip = "Performance test — run manually with: dotnet test --filter Insert10KFiles")]
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

    [Fact(Skip = "Performance test — run manually with: dotnet test --filter Search10KFileIndex")]
    public void Search10KFileIndex_ReturnsInReasonableTime()
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

    [Fact(Skip = "Performance test — run manually with: dotnet test --filter ExtractLargeSameLineSymbolFixture_CompletesInReasonableTime")]
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

    [Fact]
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
        Assert.True(indexElapsed < TimeSpan.FromSeconds(20), $"CI performance smoke indexing took {indexElapsed.TotalSeconds:F1}s");

        var reader = new DbReader(_db.Connection);
        List<SearchResult> results = [];
        var searchElapsed = MeasureElapsed(() => results = reader.Search("Execute42", limit: 5));

        Assert.Contains(results, result => result.Path.EndsWith("service42.cs", StringComparison.Ordinal));
        Assert.True(searchElapsed < TimeSpan.FromSeconds(2), $"CI performance smoke search took {searchElapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public void SymbolExtraction_CsharpHotPath_StaysWithinAllocationBudget()
    {
        var content = BuildCSharpHotPathFixture(typeCount: 120);
        _ = SymbolExtractor.Extract(1, "csharp", content);

        var allocatedBytes = MeasureAllocatedBytes(() => SymbolExtractor.Extract(1, "csharp", content));

        Assert.True(allocatedBytes < 18_000_000, $"Symbol extraction allocated {allocatedBytes:N0} bytes");
    }

    [Fact]
    public void ReferenceExtraction_CsharpHotPath_StaysWithinAllocationBudget()
    {
        var content = BuildCSharpHotPathFixture(typeCount: 80);
        var symbols = SymbolExtractor.Extract(1, "csharp", content);
        _ = ReferenceExtractor.Extract(1, "csharp", content, symbols);

        var allocatedBytes = MeasureAllocatedBytes(() => ReferenceExtractor.Extract(1, "csharp", content, symbols));

        Assert.True(allocatedBytes < 18_000_000, $"Reference extraction allocated {allocatedBytes:N0} bytes");
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

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }
}
