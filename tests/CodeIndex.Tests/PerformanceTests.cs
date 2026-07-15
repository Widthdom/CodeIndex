using System.Diagnostics;
using System.Text;
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
        Assert.True(indexElapsed < TimeSpan.FromSeconds(20), $"CI performance smoke indexing took {indexElapsed.TotalSeconds:F1}s");

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

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        TestProjectHelper.DeleteDirectory(_dbDir);
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }
}
