using System.Diagnostics;
using System.Text;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
#if NET8_0
    [Fact]
#else
    [Fact(Skip = PracticalBudgetTestTarget.SecondaryTargetSkipReason)]
#endif
    public void Run_InitialFullIndex_MixedLanguageFixture_PreservesColdPathContractsWithinBroadBudget()
    {
        var budget = OperatingSystem.IsWindows()
            ? TimeSpan.FromSeconds(120)
            : TimeSpan.FromSeconds(60);

        RunInitialFullIndexMixedLanguageContract(copiesPerLanguage: 2, budget);
    }

    [ManualPerformanceFact]
    public void Run_InitialFullIndex_MixedLanguageFixture_ManualRepositoryScaleSmoke()
    {
        var budget = OperatingSystem.IsWindows()
            ? TimeSpan.FromMinutes(4)
            : TimeSpan.FromMinutes(2);

        RunInitialFullIndexMixedLanguageContract(copiesPerLanguage: 20, budget);
    }

    private void RunInitialFullIndexMixedLanguageContract(int copiesPerLanguage, TimeSpan budget)
    {
        var projectRoot = CreateTempProject();
        var dbRoot = TestProjectHelper.CreateTempProject("cdidx_initial_full_db");
        var dbPath = Path.Combine(dbRoot, "codeindex.db");
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousRawScopeHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        var previousFamilyScopeHook =
            IndexCommandRunner.FullScanFamilyScopeResolvedForTesting;
        var previousCSharpSourceHook =
            IndexCommandRunner.FullScanCSharpSourceObservedForTesting;
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        var rawScopeSnapshots = new List<DbWriter.AuthoritativeFreshRawInsertScopeStats>();
        var familyScopeResolutionCount = 0;
        var csharpSourceObservationCount = 0;
        try
        {
            var fixture = WriteInitialFullIndexFixture(projectRoot, copiesPerLanguage);
            Assert.False(File.Exists(dbPath));

            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                rawScopeSnapshots.Add(stats);
                previousRawScopeHook?.Invoke(stats);
            };
            IndexCommandRunner.FullScanFamilyScopeResolvedForTesting = path =>
            {
                Interlocked.Increment(ref familyScopeResolutionCount);
                previousFamilyScopeHook?.Invoke(path);
            };
            IndexCommandRunner.FullScanCSharpSourceObservedForTesting = path =>
            {
                Interlocked.Increment(ref csharpSourceObservationCount);
                previousCSharpSourceHook?.Invoke(path);
            };

            var stopwatch = Stopwatch.StartNew();
            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--db",
                dbPath,
                "--json",
                "--quiet",
                "--memory-trace",
                "--parallelism",
                "2",
            ]);
            stopwatch.Stop();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("incremental", json.GetProperty("mode").GetString());
            var summary = json.GetProperty("summary");
            Assert.Equal(fixture.FileCount, summary.GetProperty("files_scanned").GetInt32());
            Assert.Equal(fixture.FileCount, summary.GetProperty("files_extracted").GetInt32());
            Assert.Equal(fixture.FileCount, summary.GetProperty("files_persisted").GetInt32());
            Assert.Equal(0, summary.GetProperty("files_skipped").GetInt32());
            Assert.Equal(0, summary.GetProperty("errors").GetInt32());
            Assert.Equal(fixture.FileCount, familyScopeResolutionCount);
            Assert.Equal(
                fixture.FilesPerLanguage["csharp"],
                csharpSourceObservationCount);
            Assert.True(summary.GetProperty("chunks_persisted").GetInt32() >= fixture.FileCount);
            Assert.True(summary.GetProperty("symbols_persisted").GetInt32() >= fixture.FileCount);
            Assert.True(summary.GetProperty("references_persisted").GetInt32() > 0);
            Assert.True(json.GetProperty("index_complete").GetBoolean());
            Assert.True(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.True(json.GetProperty("graph_data_current").GetBoolean());
            var rawOperations = rawWork.Select(work => work.Operation).ToArray();
            Assert.Contains("insert_files", rawOperations);
            Assert.Contains("insert_chunks", rawOperations);
            Assert.Contains("insert_symbols", rawOperations);
            Assert.Contains("insert_reference_lines", rawOperations);
            Assert.Contains("insert_references", rawOperations);
            var rawFileWork = rawWork
                .Where(work => work.Operation == "insert_files")
                .ToArray();
            Assert.Equal(fixture.FileCount, rawFileWork.Length);
            Assert.All(rawFileWork, work =>
            {
                Assert.Equal(1, work.StatementRows);
                Assert.Equal(7, work.BoundParameterCount);
            });
            Assert.All(
                rawWork.Where(work => work.Operation == "insert_reference_lines"),
                work => Assert.Equal(work.StatementRows * 3, work.BoundParameterCount));
            var rawScope = Assert.Single(rawScopeSnapshots);
            Assert.True(rawScope.Completed);
            Assert.Equal(32, rawScope.Capacity);
            Assert.Equal(rawWork.Count, rawScope.StatementExecutionCount);
            Assert.Equal(0, rawScope.EvictionCount);
            Assert.Equal(rawScope.PrepareCount, rawScope.FinalizeCount);

            var samples = json
                .GetProperty("memory_timeline")
                .GetProperty("samples")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(9, samples.Length);
            Assert.Equal(
                ["start", "scan", "csharp_prepass", "purge", "extraction"],
                samples[..5].Select(sample => sample.GetProperty("phase").GetString()));
            Assert.Equal(
                ["reference_graph", "text_index"],
                samples[5..7]
                    .Select(sample => sample.GetProperty("phase").GetString())
                    .OrderBy(phase => phase, StringComparer.Ordinal));
            Assert.Equal(
                ["finalize", "commit"],
                samples[^2..].Select(sample => sample.GetProperty("phase").GetString()));
            AssertPhaseSamplesAreMonotonic(samples);
            Assert.True(
                stopwatch.Elapsed < budget,
                $"Initial full index took {stopwatch.Elapsed.TotalSeconds:F1}s "
                + $"(broad runaway budget {budget.TotalSeconds:F0}s)");

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson([
                "--db",
                dbPath,
                "--check",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(fixture.FileCount, statusJson.GetProperty("files").GetInt32());
            Assert.True(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(statusJson.GetProperty("reference_graph_complete").GetBoolean());
            Assert.True(statusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
            Assert.Empty(statusJson.GetProperty("failed_checks").EnumerateArray());
            var lastRun = statusJson.GetProperty("last_index_run");
            Assert.Equal("incremental", lastRun.GetProperty("mode").GetString());
            Assert.Equal(fixture.FileCount, lastRun.GetProperty("files_scanned").GetInt32());
            Assert.Equal(0, lastRun.GetProperty("files_skipped").GetInt32());
            Assert.False(lastRun.GetProperty("bytes_read_incomplete").GetBoolean());
            Assert.True(lastRun.GetProperty("bytes_read").GetInt64() >= fixture.Utf8Bytes);
            Assert.InRange(
                Math.Abs(lastRun.GetProperty("duration_ms").GetInt64() - json.GetProperty("elapsed_ms").GetInt64()),
                0,
                5_000);

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using (var integrity = db.Connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", integrity.ExecuteScalar());
            }

            using (var generation = db.Connection.CreateCommand())
            {
                generation.CommandText = """
                    SELECT
                        CAST((SELECT value
                              FROM codeindex_meta
                              WHERE key = 'resource_list_generation') AS INTEGER),
                        (SELECT COUNT(*)
                         FROM sqlite_master
                         WHERE type = 'trigger'
                           AND name IN (
                               'files_resource_generation_ai',
                               'files_resource_generation_ad',
                               'files_resource_generation_au'))
                    """;
                using var reader = generation.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(3L, reader.GetInt64(1));
            }

            using (var languageCounts = db.Connection.CreateCommand())
            {
                languageCounts.CommandText = """
                    SELECT lang, COUNT(*)
                    FROM files
                    GROUP BY lang
                    ORDER BY lang
                    """;
                using var reader = languageCounts.ExecuteReader();
                var actual = new Dictionary<string, int>(StringComparer.Ordinal);
                while (reader.Read())
                    actual[reader.GetString(0)] = reader.GetInt32(1);

                Assert.Equal(fixture.FilesPerLanguage.Count, actual.Count);
                foreach (var (language, expectedCount) in fixture.FilesPerLanguage)
                    Assert.Equal(expectedCount, actual[language]);
            }

            using (var symbolLanguages = db.Connection.CreateCommand())
            {
                symbolLanguages.CommandText = """
                    SELECT DISTINCT f.lang
                    FROM symbols AS s
                    JOIN files AS f ON f.id = s.file_id
                    ORDER BY f.lang
                    """;
                using var reader = symbolLanguages.ExecuteReader();
                var actual = new List<string>();
                while (reader.Read())
                    actual.Add(reader.GetString(0));

                Assert.Equal(fixture.FilesPerLanguage.Keys.OrderBy(lang => lang, StringComparer.Ordinal), actual);
            }

            using (var searchable = db.Connection.CreateCommand())
            {
                searchable.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'ColdStartMarker0'";
                Assert.Equal(fixture.FilesPerLanguage.Count, Convert.ToInt32(searchable.ExecuteScalar()));
            }
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousRawScopeHook;
            IndexCommandRunner.FullScanFamilyScopeResolvedForTesting =
                previousFamilyScopeHook;
            IndexCommandRunner.FullScanCSharpSourceObservedForTesting =
                previousCSharpSourceHook;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
            DeleteDirectory(dbRoot);
        }
    }

    private static InitialFullIndexFixture WriteInitialFullIndexFixture(string projectRoot, int copiesPerLanguage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(copiesPerLanguage, 1);
        var filesPerLanguage = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["cpp"] = copiesPerLanguage,
            ["csharp"] = copiesPerLanguage,
            ["go"] = copiesPerLanguage,
            ["java"] = copiesPerLanguage,
            ["kotlin"] = copiesPerLanguage,
            ["python"] = copiesPerLanguage,
            ["rust"] = copiesPerLanguage,
            ["typescript"] = copiesPerLanguage,
        };
        long utf8Bytes = 0;

        foreach (var (language, count) in filesPerLanguage)
        {
            var languageRoot = Path.Combine(projectRoot, language);
            Directory.CreateDirectory(languageRoot);
            for (var index = 0; index < count; index++)
            {
                var (fileName, content) = BuildInitialFullIndexSource(language, index);
                var path = Path.Combine(languageRoot, fileName);
                File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                utf8Bytes += Encoding.UTF8.GetByteCount(content);
            }
        }

        return new InitialFullIndexFixture(
            FileCount: filesPerLanguage.Values.Sum(),
            Utf8Bytes: utf8Bytes,
            FilesPerLanguage: filesPerLanguage);
    }

    private static (string FileName, string Content) BuildInitialFullIndexSource(string language, int index) =>
        language switch
        {
            "csharp" => ($"ColdStart{index}.cs", $$"""
                namespace InitialFullIndex;
                public static class CSharpColdStart{{index}}
                {
                    public const string Marker = "ColdStartMarker{{index}} 初回索引";
                    public static int Target() => 1;
                    public static int Caller() => Target();
                }
                """),
            "typescript" => ($"cold-start-{index}.ts", $$"""
                export const marker{{index}} = "ColdStartMarker{{index}} 初回索引";
                export function tsTarget{{index}}(): number { return 1; }
                export function tsCaller{{index}}(): number { return tsTarget{{index}}(); }
                """),
            "python" => ($"cold_start_{index}.py", $$"""
                MARKER_{{index}} = "ColdStartMarker{{index}} 初回索引"
                def py_target_{{index}}():
                    return 1
                def py_caller_{{index}}():
                    return py_target_{{index}}()
                """),
            "java" => ($"JavaColdStart{index}.java", $$"""
                public final class JavaColdStart{{index}} {
                    static final String MARKER = "ColdStartMarker{{index}} 初回索引";
                    static int target() { return 1; }
                    static int caller() { return target(); }
                }
                """),
            "go" => ($"cold_start_{index}.go", $$"""
                package initialfullindex
                const Marker{{index}} = "ColdStartMarker{{index}} 初回索引"
                func GoTarget{{index}}() int { return 1 }
                func GoCaller{{index}}() int { return GoTarget{{index}}() }
                """),
            "rust" => ($"cold_start_{index}.rs", $$"""
                pub const MARKER_{{index}}: &str = "ColdStartMarker{{index}} 初回索引";
                pub fn rust_target_{{index}}() -> i32 { 1 }
                pub fn rust_caller_{{index}}() -> i32 { rust_target_{{index}}() }
                """),
            "cpp" => ($"cold_start_{index}.cpp", $$"""
                static const char* marker_{{index}} = "ColdStartMarker{{index}} 初回索引";
                int cpp_target_{{index}}() { return 1; }
                int cpp_caller_{{index}}() { return cpp_target_{{index}}(); }
                """),
            "kotlin" => ($"KotlinColdStart{index}.kt", $$"""
                class KotlinColdStart{{index}} {
                    val marker = "ColdStartMarker{{index}} 初回索引"
                    fun target(): Int = 1
                    fun caller(): Int = target()
                }
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
        };

    private sealed record InitialFullIndexFixture(
        int FileCount,
        long Utf8Bytes,
        IReadOnlyDictionary<string, int> FilesPerLanguage);
}
