using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class IndexCommandRunnerTests
{
    [Fact]
    public void Run_RebuildReclaimsHighFreelistWithConcurrentReaderAndPersistsTelemetry_Issue5057()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { public string Run() => \"ready\"; }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE rebuild_payload (id INTEGER PRIMARY KEY, payload BLOB);
                    WITH RECURSIVE n(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1 FROM n WHERE value < 256
                    )
                    INSERT INTO rebuild_payload (payload)
                    SELECT randomblob(4096) FROM n;
                    DELETE FROM rebuild_payload;";
                command.ExecuteNonQuery();
            }

            using var readerConnection = OpenNonPoolingConnection(dbPath);
            readerConnection.Open();
            using var readerCommand = readerConnection.CreateCommand();
            readerCommand.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(1L, (long)readerCommand.ExecuteScalar()!);

            var (rebuildExitCode, rebuildJson) = RunAndCaptureJson(
                [projectRoot, "--rebuild", "--yes", "--json", "--quiet", "--memory-trace"]);

            Assert.Equal(CommandExitCodes.Success, rebuildExitCode);
            Assert.Equal(1L, (long)readerCommand.ExecuteScalar()!);
            var memoryPhases = rebuildJson
                .GetProperty("memory_timeline")
                .GetProperty("samples")
                .EnumerateArray()
                .Select(sample => sample.GetProperty("phase").GetString())
                .ToArray();
            Assert.Equal(["commit", "rebuild_reclaim"], memoryPhases[^2..]);
            var reclaim = rebuildJson.GetProperty("rebuild_reclaim");
            Assert.Equal("completed", reclaim.GetProperty("state").GetString());
            Assert.Equal("threshold_exceeded", reclaim.GetProperty("reason").GetString());
            Assert.True(reclaim.GetProperty("freelist_ratio_before").GetDouble()
                >= reclaim.GetProperty("freelist_threshold_ratio").GetDouble());
            Assert.True(reclaim.GetProperty("freelist_ratio_after").GetDouble()
                < reclaim.GetProperty("freelist_threshold_ratio").GetDouble());
            Assert.True(reclaim.GetProperty("pages_reclaimed").GetInt64() > 0);
            Assert.True(reclaim.GetProperty("bytes_reclaimed").GetInt64() > 0);
            Assert.True(
                reclaim.GetProperty("logical_database_bytes_before").GetInt64()
                > reclaim.GetProperty("logical_database_bytes_after").GetInt64());

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var statusReclaim = statusJson.GetProperty("last_index_run").GetProperty("rebuild_reclaim");
            Assert.Equal("completed", statusReclaim.GetProperty("state").GetString());
            Assert.Equal(
                reclaim.GetProperty("pages_reclaimed").GetInt64(),
                statusReclaim.GetProperty("pages_reclaimed").GetInt64());
            Assert.Equal(
                reclaim.GetProperty("logical_database_bytes_after").GetInt64(),
                statusReclaim.GetProperty("logical_database_bytes_after").GetInt64());
            Assert.NotEqual(
                "vacuum_recommended",
                statusJson.GetProperty("maintenance_guidance").GetProperty("freelist_state").GetString());

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var integrityCommand = db.Connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", integrityCommand.ExecuteScalar());
            var explicitDryRun = db.RunIncrementalVacuum(dryRun: true);
            Assert.Equal("dry_run", explicitDryRun.Status);
            Assert.Equal(
                statusReclaim.GetProperty("freelist_count_after").GetInt64(),
                explicitDryRun.FreelistCountBefore);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_RebuildReclaimFailureKeepsCommittedDatabaseUsable_Issue5057()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE rebuild_failure_payload (id INTEGER PRIMARY KEY, payload BLOB);
                    WITH RECURSIVE n(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1 FROM n WHERE value < 256
                    )
                    INSERT INTO rebuild_failure_payload (payload)
                    SELECT randomblob(4096) FROM n;
                    DELETE FROM rebuild_failure_payload;";
                command.ExecuteNonQuery();
            }
            DbContext.MaintenanceProgressForTesting = (operation, phase) =>
            {
                if (operation == "rebuild_reclaim" && phase == "incremental_vacuum")
                    throw new InvalidOperationException("injected rebuild reclaim failure");
            };

            var (rebuildExitCode, rebuildJson) = RunAndCaptureJson(
                [projectRoot, "--rebuild", "--yes", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, rebuildExitCode);
            var reclaim = rebuildJson.GetProperty("rebuild_reclaim");
            Assert.Equal("failed", reclaim.GetProperty("state").GetString());
            Assert.Equal("unexpected_error", reclaim.GetProperty("reason").GetString());
            using var connectionAfter = OpenNonPoolingConnection(dbPath);
            connectionAfter.Open();
            using var integrityCommand = connectionAfter.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", integrityCommand.ExecuteScalar());
            using var countCommand = connectionAfter.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(1L, (long)countCommand.ExecuteScalar()!);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(
                "failed",
                statusJson.GetProperty("last_index_run")
                    .GetProperty("rebuild_reclaim")
                    .GetProperty("state")
                    .GetString());
            Assert.Equal(
                "vacuum_recommended",
                statusJson.GetProperty("maintenance_guidance").GetProperty("freelist_state").GetString());
        }
        finally
        {
            DbContext.MaintenanceProgressForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_InterruptedRebuildPreservesPreviouslyCommittedDatabase_Issue5057()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public int Value => 1; }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var checksumBefore = ReadIndexedChecksum(dbPath, "app.cs");
            Assert.NotNull(checksumBefore);

            File.WriteAllText(sourcePath, "public class App { public int Value => 2; }\n");
            IndexCommandRunner.FullScanFtsOptimizeForTesting = cancellation.Cancel;

            var (rebuildExitCode, rebuildJson) = RunAndCaptureJson(
                [projectRoot, "--rebuild", "--yes", "--json", "--quiet"],
                cancellation);

            Assert.Equal(CommandExitCodes.Interrupted, rebuildExitCode);
            Assert.Equal(CommandErrorCodes.Interrupted, rebuildJson.GetProperty("error_code").GetString());
            Assert.Equal(checksumBefore, ReadIndexedChecksum(dbPath, "app.cs"));
            using var connection = OpenNonPoolingConnection(dbPath);
            connection.Open();
            using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", integrityCommand.ExecuteScalar());
        }
        finally
        {
            IndexCommandRunner.FullScanFtsOptimizeForTesting = null;
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAndScopedUpdateUseGuardedAtomicFileReferenceScope()
    {
        var projectRoot = CreateTempProject();
        var previousAtomicHook = DbWriter.AtomicFileReferenceInsertForTesting;
        var previousAggregateRefreshHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var atomicCalls = new List<bool>();
        var aggregateRefreshStatements = 0;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(sourcePath, "def run():\n    return helper()\n");
            DbWriter.AtomicFileReferenceInsertForTesting = newFiles =>
            {
                atomicCalls.Add(newFiles);
                previousAtomicHook?.Invoke(newFiles);
            };
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = () =>
            {
                aggregateRefreshStatements++;
                previousAggregateRefreshHook?.Invoke();
            };

            var (fullExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, fullExitCode);
            Assert.Contains(true, atomicCalls);
            Assert.Equal(1, aggregateRefreshStatements);

            atomicCalls.Clear();
            aggregateRefreshStatements = 0;
            File.WriteAllText(sourcePath, "def run():\n    return updated_helper()\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));
            var (updateExitCode, _) = RunAndCaptureJson(
                [projectRoot, "--files", sourcePath, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Contains(false, atomicCalls);
            Assert.Equal(1, aggregateRefreshStatements);
        }
        finally
        {
            DbWriter.AtomicFileReferenceInsertForTesting = previousAtomicHook;
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousAggregateRefreshHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_MultiLanguagePersistenceUsesNewAndUpdateReferenceLinePaths()
    {
        var projectRoot = CreateTempProject();
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        var statements = new List<DbWriter.DbWriterBatchStatement>();
        try
        {
            var pythonPath = Path.Combine(projectRoot, "app.py");
            var typeScriptPath = Path.Combine(projectRoot, "app.ts");
            File.WriteAllText(
                pythonPath,
                "def py_target():\n    return 1\n\ndef py_caller():\n    return py_target()\n");
            File.WriteAllText(
                typeScriptPath,
                "function tsTarget() { return 1; }\nfunction tsCaller() { return tsTarget(); }\n");
            DbWriter.BatchStatementExecutingForTesting = statement =>
            {
                statements.Add(statement);
                previousStatementHook?.Invoke(statement);
            };

            var (fullExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, fullExitCode);
            Assert.Contains(statements, statement => statement.Operation == "insert_reference_lines");
            Assert.Contains(statements, statement => statement.Operation == "insert_references");

            statements.Clear();
            File.WriteAllText(
                pythonPath,
                "def py_target():\n    return 2\n\ndef py_caller():\n    return py_target() + py_target()\n");
            File.WriteAllText(
                typeScriptPath,
                "function tsTarget() { return 2; }\nfunction tsCaller() { return tsTarget() + tsTarget(); }\n");
            File.SetLastWriteTimeUtc(pythonPath, DateTime.UtcNow.AddSeconds(2));
            File.SetLastWriteTimeUtc(typeScriptPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Contains(statements, statement => statement.Operation == "upsert_reference_lines");
            Assert.Contains(statements, statement => statement.Operation == "lookup_reference_lines");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText = """
                SELECT f.lang, COUNT(sr.id), COUNT(DISTINCT sr.reference_line_id)
                FROM files AS f
                JOIN symbol_references AS sr ON sr.file_id = f.id
                WHERE f.path IN ('app.py', 'app.ts')
                GROUP BY f.lang
                """;
            var languageCounts = new Dictionary<string, (long References, long Lines)>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                languageCounts[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));

            Assert.True(languageCounts["python"].References >= 2);
            Assert.True(languageCounts["python"].Lines >= 1);
            Assert.True(languageCounts["typescript"].References >= 2);
            Assert.True(languageCounts["typescript"].Lines >= 1);
        }
        finally
        {
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_MemoryTrace_ReportsFullScanAndUpdatePhaseBoundaries()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var csharpPath = Path.Combine(projectRoot, "App.cs");
            var pythonPath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(csharpPath, "public class App { public void Run() { } }\n");
            File.WriteAllText(pythonPath, "def run():\n    return 1\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "types.ts"),
                "interface MemoryTraceContract { value: number }\n");

            var (fullExitCode, fullJson) = RunAndCaptureJson([
                projectRoot,
                "--memory-trace",
                "--json",
                "--quiet",
            ]);

            Assert.Equal(CommandExitCodes.Success, fullExitCode);
            var fullSamples = fullJson.GetProperty("memory_timeline").GetProperty("samples").EnumerateArray().ToArray();
            Assert.Equal(
                ["start", "scan", "csharp_prepass", "purge", "extraction", "text_index", "reference_graph", "finalize", "commit"],
                fullSamples.Select(sample => sample.GetProperty("phase").GetString()));
            AssertPhaseSamplesAreMonotonic(fullSamples);

            File.WriteAllText(pythonPath, "def run():\n    return 2\n");
            var (updateExitCode, updateJson) = RunAndCaptureJson([
                projectRoot,
                "--files",
                pythonPath,
                "--memory-trace",
                "--json",
                "--quiet",
            ]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            var updateSamples = updateJson.GetProperty("memory_timeline").GetProperty("samples").EnumerateArray().ToArray();
            Assert.Equal(
                ["start", "extraction", "reference_graph", "text_index", "finalize"],
                updateSamples.Select(sample => sample.GetProperty("phase").GetString()));
            AssertPhaseSamplesAreMonotonic(updateSamples);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private static void AssertPhaseSamplesAreMonotonic(JsonElement[] samples)
    {
        long priorElapsedMs = -1;
        foreach (var sample in samples)
        {
            var elapsedMs = sample.GetProperty("elapsed_ms").GetInt64();
            Assert.True(elapsedMs >= priorElapsedMs);
            Assert.True(sample.GetProperty("heap_bytes").GetInt64() >= 0);
            Assert.True(sample.GetProperty("working_set_bytes").GetInt64() > 0);
            priorElapsedMs = elapsedMs;
        }
    }

    [Fact]
    public void Run_FullScanJson_ProjectMarkerBudgetWarningIncludesTruncatedWarning()
    {
        var projectRoot = CreateTempProject();
        var previousEnumerator = FileIndexer.EnumerateProjectMarkerDirectoriesForTesting;
        var previousDirectoryBudget = FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting;
        var markerDirectoryEnumerationCount = 0;
        try
        {
            var childDir = Path.Combine(projectRoot, "nested");
            Directory.CreateDirectory(childDir);
            File.WriteAllText(Path.Combine(childDir, "App.cs"), "public class App { }\n");
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting =
                directory =>
                {
                    markerDirectoryEnumerationCount++;
                    return Directory.EnumerateDirectories(directory);
                };
            FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting = 1;

            var (exitCode, json, _) = RunAndCaptureJsonWithStderr([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, markerDirectoryEnumerationCount);
            Assert.Contains(
                json.GetProperty("warnings").EnumerateArray(),
                warning =>
                    warning.GetProperty("message").GetString()!.Contains("Project marker discovery truncated", StringComparison.Ordinal)
                    && warning.GetProperty("message").GetString()!.Contains("directory budget", StringComparison.Ordinal));
        }
        finally
        {
            FileIndexer.EnumerateProjectMarkerDirectoriesForTesting = previousEnumerator;
            FileIndexer.ProjectMarkerFingerprintDirectoryBudgetForTesting = previousDirectoryBudget;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ExcludeSymbolKindDropsMatchingSymbols()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), """
                class App:
                    pass

                def helper():
                    return App()
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "function", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("symbols_dropped_by_kind_filter").GetInt32());
            Assert.Equal(["function"], json.GetProperty("symbol_kind_filter").GetProperty("exclude").EnumerateArray().Select(value => value.GetString()).ToArray());

            var counts = ReadSymbolKindCounts(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.True(counts.GetValueOrDefault("class") > 0);
            Assert.False(counts.ContainsKey("function"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("full", true, true, true, null, null)]
    [InlineData("symbols-only", false, false, false, "symbols_only_references_omitted", "symbols_only_graph_omitted")]
    [InlineData("max-file-bytes", true, false, false, "file_too_large", "file_too_large")]
    [InlineData("max-symbols", true, false, false, "symbol_count_exceeded", "symbol_count_exceeded")]
    [InlineData("max-references", true, false, false, "reference_count_exceeded", "reference_count_exceeded")]
    public void Run_FullScan_CompletenessMatrixMatchesImmediateStatus_Issue4826(
        string scenario,
        bool expectedGraphTableAvailable,
        bool expectedIndexComplete,
        bool expectedReferenceGraphComplete,
        string? expectedIndexReason,
        string? expectedReferenceReason)
    {
        var projectRoot = CreateTempProject();
        try
        {
            var args = new List<string> { projectRoot, "--json", "--quiet" };
            switch (scenario)
            {
                case "full":
                case "symbols-only":
                    File.WriteAllText(
                        Path.Combine(projectRoot, "App.cs"),
                        "public class App { public void Run() { Helper(); } private void Helper() { } }\n");
                    if (scenario == "symbols-only")
                        args.Insert(1, "--symbols-only");
                    break;
                case "max-file-bytes":
                    File.WriteAllText(
                        Path.Combine(projectRoot, "large.py"),
                        "print('start')\n" + new string('a', 256));
                    args.InsertRange(1, ["--max-file-bytes", "128"]);
                    break;
                case "max-symbols":
                    File.WriteAllText(
                        Path.Combine(projectRoot, "generated.py"),
                        string.Join('\n', Enumerable.Range(0, 4).Select(i => $"def f{i}(): pass")));
                    args.InsertRange(1, ["--max-symbols-per-file", "2"]);
                    break;
                case "max-references":
                    File.WriteAllText(
                        Path.Combine(projectRoot, "DenseReferences.cs"),
                        BuildDenseReferenceCSharpSource(3));
                    args.InsertRange(1, ["--max-references-per-file", "2"]);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown completeness scenario: {scenario}");
            }

            var (indexExitCode, indexJson) = RunAndCaptureJson([.. args]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(expectedGraphTableAvailable, indexJson.GetProperty("graph_table_available").GetBoolean());
            Assert.Equal(expectedIndexComplete, indexJson.GetProperty("index_complete").GetBoolean());
            Assert.Equal(expectedReferenceGraphComplete, indexJson.GetProperty("reference_graph_complete").GetBoolean());
            Assert.Equal(
                expectedGraphTableAvailable && expectedIndexComplete && expectedReferenceGraphComplete,
                indexJson.GetProperty("graph_data_current").GetBoolean());
            AssertCompletenessReason(indexJson, "index_incomplete_reasons", expectedIndexReason);
            AssertCompletenessReason(indexJson, "reference_graph_incomplete_reasons", expectedReferenceReason);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            AssertCompletenessSignalsEqual(indexJson, statusJson);
            if (scenario == "symbols-only")
            {
                var referenceDegradation = statusJson
                    .GetProperty("readiness_degradations")
                    .EnumerateArray()
                    .Single(degradation =>
                        degradation.GetProperty("field").GetString()
                        == "reference_graph_complete");
                Assert.Equal(
                    DegradationReasonCodes.SymbolsOnlyGraphOmitted,
                    referenceDegradation.GetProperty("root_cause").GetString());
                Assert.Contains(
                    "symbols-only",
                    referenceDegradation.GetProperty("degraded_reason").GetString(),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "safety cap",
                    referenceDegradation.GetProperty("degraded_reason").GetString(),
                    StringComparison.OrdinalIgnoreCase);
            }

            using (var db = new DbContext(DbOpenIntent.QueryOnly, dbPath))
            {
                var reader = new DbReader(db.Connection, db.IsReadOnly);
                var workspaceHealth = reader.GetWorkspaceIndexHealth();
                Assert.Equal(
                    statusJson.GetProperty("graph_table_available").GetBoolean(),
                    workspaceHealth.GraphTableAvailable);
                Assert.Equal(
                    statusJson.GetProperty("graph_data_current").GetBoolean(),
                    workspaceHealth.GraphDataCurrent);
                Assert.Equal(
                    statusJson.GetProperty("index_complete").GetBoolean(),
                    workspaceHealth.IndexComplete);
                Assert.Equal(
                    statusJson.GetProperty("reference_graph_complete").GetBoolean(),
                    workspaceHealth.ReferenceGraphComplete);
            }

            if (scenario == "max-file-bytes")
            {
                var humanArgs = args
                    .Where(arg => arg is not "--json" and not "--quiet")
                    .ToArray();
                var (humanExitCode, stdout, stderr) = RunAndCaptureStreams(humanArgs);
                Assert.Equal(CommandExitCodes.Success, humanExitCode);
                Assert.Contains("Index", stdout, StringComparison.Ordinal);
                Assert.Contains("incomplete", stdout, StringComparison.Ordinal);
                Assert.Contains(
                    "Index generation is incomplete: file_too_large.",
                    stderr,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "Reference graph is incomplete: file_too_large.",
                    stderr,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public void Run_FullScan_FileSizePolicyTransitionReprocessesUnchangedFile_Issue4826(
        bool initialCap,
        bool nextCap,
        bool expectedComplete)
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "large.py"),
                "print('start')\n" + new string('a', 256));
            var initialArgs = new List<string> { projectRoot, "--json", "--quiet" };
            if (initialCap)
                initialArgs.InsertRange(1, ["--max-file-bytes", "128"]);
            var (initialExitCode, _) = RunAndCaptureJson([.. initialArgs]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var nextArgs = new List<string> { projectRoot, "--json", "--quiet" };
            if (nextCap)
                nextArgs.InsertRange(1, ["--max-file-bytes", "128"]);
            var (nextExitCode, nextJson) = RunAndCaptureJson([.. nextArgs]);

            Assert.Equal(CommandExitCodes.Success, nextExitCode);
            Assert.Equal(expectedComplete, nextJson.GetProperty("index_complete").GetBoolean());
            Assert.Equal(
                expectedComplete,
                nextJson.GetProperty("reference_graph_complete").GetBoolean());
            Assert.Equal(
                1,
                nextJson.GetProperty("summary").GetProperty("files_extracted").GetInt64());
            Assert.Equal(
                1,
                nextJson.GetProperty("summary").GetProperty("files_persisted").GetInt64());
            AssertCompletenessReason(
                nextJson,
                "index_incomplete_reasons",
                expectedComplete ? null : "file_too_large");
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_IncompleteGenerationDoesNotExposeFoldOnlyRemediation_Issue4826()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "large.py"), "def ready(): return True\n");
            File.WriteAllText(Path.Combine(projectRoot, "other.py"), "def other(): return True\n");
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM codeindex_meta
                    WHERE key IN (@version, @fingerprint)
                    """;
                command.Parameters.AddWithValue("@version", "fold_key_version");
                command.Parameters.AddWithValue("@fingerprint", "fold_key_fingerprint");
                command.ExecuteNonQuery();
            }
            File.WriteAllText(
                Path.Combine(projectRoot, "large.py"),
                "print('start')\n" + new string('a', 256));

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--max-file-bytes", "128", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("degraded_reason").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("recommended_action").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("alternative_action").ValueKind);

            var humanArgs = new[] { projectRoot, "--max-file-bytes", "128" };
            var (humanExitCode, _, stderr) = RunAndCaptureStreams(humanArgs);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.DoesNotContain("fold-only", stderr, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, false, "file_too_large")]
    public void PersistedReadiness_OlderDatabaseWithoutCompletenessMetadataUsesSafeFallback_Issue4826(
        bool capFileBytes,
        bool expectedComplete,
        string? expectedReason)
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "sample.py"),
                capFileBytes
                    ? "print('start')\n" + new string('a', 256)
                    : "def ready(): return True\n");
            var args = capFileBytes
                ? new[] { projectRoot, "--max-file-bytes", "128", "--json", "--quiet" }
                : new[] { projectRoot, "--json", "--quiet" };
            var (indexExitCode, _) = RunAndCaptureJson(args);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM codeindex_meta
                    WHERE key IN (@completeness, @reasons);
                    """;
                command.Parameters.AddWithValue(
                    "@completeness",
                    DbContext.IndexCompletenessMetaKey);
                command.Parameters.AddWithValue(
                    "@reasons",
                    DbContext.IndexIncompleteReasonsMetaKey);
                command.ExecuteNonQuery();
            }

            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var status = reader.GetStatus();
            var workspaceHealth = reader.GetWorkspaceIndexHealth();

            Assert.Equal(expectedComplete, status.IndexComplete);
            Assert.Equal(expectedComplete, status.ReferenceGraphComplete);
            Assert.Equal(expectedComplete, status.GraphDataCurrent);
            Assert.Equal(expectedComplete, workspaceHealth.IndexComplete);
            Assert.Equal(expectedComplete, workspaceHealth.ReferenceGraphComplete);
            Assert.Equal(expectedComplete, workspaceHealth.GraphDataCurrent);
            if (expectedReason == null)
            {
                Assert.Null(status.IndexIncompleteReasons);
            }
            else
            {
                Assert.Contains(expectedReason, status.IndexIncompleteReasons ?? []);
                Assert.Contains(expectedReason, status.ReferenceGraphIncompleteReasons ?? []);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ExtractorFailurePersistsSuccessfulGraphAndTruthfulPartialState_Issue4609()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Service.cs"), "public class Service { public void Target() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "Caller.cs"), "public class Caller { public void Run(Service service) { service.Target(); } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "Broken.cs"), "public class Broken { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "schema.sql"), "CREATE TABLE audit_events (id bigint);\nSELECT id FROM audit_events;\n");

            lock (FullScanContentLoadHookGate)
            {
                var brokenSymbolPhaseCalls = 0;
                try
                {
                    IndexCommandRunner.FullScanFilePhaseForTesting = (path, phase) =>
                    {
                        if (path == "Broken.cs"
                            && phase == "symbols"
                            && Interlocked.Increment(ref brokenSymbolPhaseCalls) == 2)
                            throw new JsonException("synthetic extractor failure", "Broken.cs", 5, 7);
                    };

                    var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.PartialResult, exitCode);
                    Assert.Equal("partial", json.GetProperty("status").GetString());
                    Assert.Equal(CommandErrorCodes.IndexPartial, json.GetProperty("error_code").GetString());
                    Assert.False(json.GetProperty("index_complete").GetBoolean());
                    Assert.True(json.GetProperty("graph_table_available").GetBoolean());
                    Assert.False(json.GetProperty("graph_data_current").GetBoolean());
                    Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());
                    AssertCompletenessReason(json, "index_incomplete_reasons", "file_index_error");
                    AssertCompletenessReason(json, "reference_graph_incomplete_reasons", "file_index_error");
                    Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());

                    var summary = json.GetProperty("summary");
                    Assert.True(summary.GetProperty("references_total").GetInt64() > 0);
                    Assert.Equal(summary.GetProperty("references_extracted").GetInt64(), summary.GetProperty("references_persisted").GetInt64());
                    Assert.True(summary.GetProperty("files_extracted").GetInt64() > summary.GetProperty("files_persisted").GetInt64());

                    var fileError = Assert.Single(json.GetProperty("file_errors").EnumerateArray());
                    Assert.Equal("Broken.cs", fileError.GetProperty("file").GetString());
                    Assert.Equal("extraction_error", fileError.GetProperty("category").GetString());
                    Assert.Equal("symbols", fileError.GetProperty("phase").GetString());
                    Assert.Contains("synthetic extractor failure", fileError.GetProperty("detail").GetString(), StringComparison.Ordinal);
                    Assert.Equal(6, fileError.GetProperty("line").GetInt64());
                    Assert.Equal(8, fileError.GetProperty("column").GetInt64());

                    var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
                    using (var connection = OpenNonPoolingConnection(dbPath))
                    {
                        connection.Open();
                        using var countCommand = connection.CreateCommand();
                        countCommand.CommandText = "SELECT COUNT(*) FROM symbol_references";
                        var persistedReferenceCount = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                        Assert.Equal(summary.GetProperty("references_total").GetInt64(), persistedReferenceCount);
                        Assert.True(persistedReferenceCount > 0);
                    }

                    var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
                    Assert.NotEqual(CommandExitCodes.Success, statusExitCode);
                    Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
                    Assert.False(statusJson.GetProperty("graph_data_current").GetBoolean());
                    Assert.False(statusJson.GetProperty("index_complete").GetBoolean());
                    Assert.False(statusJson.GetProperty("reference_graph_complete").GetBoolean());
                    AssertCompletenessSignalsEqual(json, statusJson);
                    Assert.Equal(summary.GetProperty("references_total").GetInt64(), statusJson.GetProperty("references").GetInt64());
                    Assert.Contains("INCOMPLETE", statusJson.GetProperty("summary").GetString(), StringComparison.Ordinal);
                    var lastFailure = statusJson.GetProperty("last_failed_or_partial_index_run");
                    Assert.Equal(CommandErrorCodes.IndexPartial, lastFailure.GetProperty("error_code").GetString());
                    Assert.True(lastFailure.GetProperty("progress_persisted").GetBoolean());
                    Assert.Contains("rebuild is not required", lastFailure.GetProperty("recovery_hint").GetString(), StringComparison.Ordinal);
                    Assert.Equal("Broken.cs", lastFailure.GetProperty("file_errors")[0].GetProperty("file").GetString());

                    brokenSymbolPhaseCalls = 0;
                    var (allowedExitCode, allowedJson) = RunAndCaptureJson([projectRoot, "--json", "--allow-partial"]);
                    Assert.Equal(CommandExitCodes.Success, allowedExitCode);
                    Assert.Equal("partial", allowedJson.GetProperty("status").GetString());
                }
                finally
                {
                    IndexCommandRunner.FullScanFilePhaseForTesting = null;
                }
            }
        }
        finally
        {
            IndexCommandRunner.FullScanFilePhaseForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    private static void AssertCompletenessSignalsEqual(
        JsonElement indexJson,
        JsonElement statusJson)
    {
        foreach (var propertyName in new[]
        {
            "graph_table_available",
            "graph_data_current",
            "index_complete",
            "reference_graph_complete",
        })
        {
            Assert.Equal(
                indexJson.GetProperty(propertyName).GetBoolean(),
                statusJson.GetProperty(propertyName).GetBoolean());
        }

        Assert.Equal(
            ReadCompletenessReasons(indexJson, "index_incomplete_reasons"),
            ReadCompletenessReasons(statusJson, "index_incomplete_reasons"));
        Assert.Equal(
            ReadCompletenessReasons(indexJson, "reference_graph_incomplete_reasons"),
            ReadCompletenessReasons(statusJson, "reference_graph_incomplete_reasons"));
    }

    private static void AssertCompletenessReason(
        JsonElement json,
        string propertyName,
        string? expectedReason)
    {
        var reasons = ReadCompletenessReasons(json, propertyName);
        if (expectedReason == null)
            Assert.Empty(reasons);
        else
            Assert.Contains(expectedReason, reasons);
    }

    private static string[] ReadCompletenessReasons(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var reasons)
            && reasons.ValueKind == JsonValueKind.Array
            ? reasons.EnumerateArray().Select(reason => reason.GetString()!).ToArray()
            : [];

    [PublishedTrimmedCliFact]
    public void Run_FullScan_PublishedTrimmedBinary_IndexesJsonWorkerInputs_Issue4709()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_trimmed_json_worker");
        try
        {
            TestProjectHelper.WriteTextFiles(
                projectRoot,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["version.json"] = """
                        {
                          "$schema": "https://example.test/version.schema.json",
                          "version": "1.0.0"
                        }
                        """,
                    ["stryker-config.json"] = """
                        {
                          "$schema": "https://example.test/stryker.schema.json",
                          "stryker-config": { "mutate": ["src/**/*.cs"] }
                        }
                        """,
                    ["package-lock.json"] = """
                        {
                          "lockfileVersion": 3,
                          "packages": {
                            "node_modules/@scope/package": { "version": "1.2.3" }
                          }
                        }
                        """,
                    ["doc/config.schema.json"] = """
                        {
                          "$schema": "https://json-schema.org/draft/2020-12/schema",
                          "$id": "https://example.test/config.schema.json",
                          "properties": { "enabled": { "type": "boolean" } }
                        }
                        """,
                });

            var publishedCli = TrimmedCliTestHelper.SharedTrimmedCli;
            var (indexExitCode, indexStdOut, indexStdErr) = TrimmedCliTestHelper.RunPublishedCli(
                publishedCli,
                projectRoot,
                "index",
                projectRoot,
                "--rebuild",
                "--yes",
                "--parallelism",
                "1",
                "--json",
                "--quiet");

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStdErr);
            using (var indexDocument = JsonDocument.Parse(indexStdOut))
            {
                Assert.Equal("success", indexDocument.RootElement.GetProperty("status").GetString());
                Assert.True(indexDocument.RootElement.GetProperty("index_complete").GetBoolean());
                Assert.Equal(
                    0,
                    indexDocument.RootElement.GetProperty("summary").GetProperty("errors").GetInt32());
            }

            var dbPath = TestProjectHelper.ProjectPath(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusStdOut, statusStdErr) = TrimmedCliTestHelper.RunPublishedCli(
                publishedCli,
                projectRoot,
                "status",
                "--db",
                dbPath,
                "--check",
                "--json");

            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.Equal(string.Empty, statusStdErr);
            using var statusDocument = JsonDocument.Parse(statusStdOut);
            var status = statusDocument.RootElement;
            Assert.True(status.GetProperty("index_matches_workspace").GetBoolean());
            Assert.True(status.GetProperty("index_complete").GetBoolean());
            Assert.Equal(status.GetProperty("version").GetString(), status.GetProperty("index_writer_version").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReferenceCapHitPersistsPerFileAndRunCompleteness_Issue4620()
    {
        var projectRoot = CreateTempProject();
        var previousLimits = ReferenceExtractor.SafetyLimitsForTesting;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "def first():\n    pass\n\ndef second():\n    pass\n");
            ReferenceExtractor.SafetyLimitsForTesting = new ReferenceExtractionSafetyLimits
            {
                MaxLookupSymbols = 1,
                MaxLookupLines = 100,
                MaxNamesPerLine = 100,
                MaxContainerCandidates = 100,
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.False(json.GetProperty("graph_data_current").GetBoolean());
            Assert.Equal(1, json.GetProperty("reference_extraction_limits").GetProperty("max_lookup_symbols").GetInt32());
            AssertReferenceCapHitSummary(json.GetProperty("reference_extraction_cap_hits"), "app.py");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.False(statusJson.GetProperty("reference_graph_complete").GetBoolean());
            Assert.False(statusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.Contains(
                "reference_definition_lookup_symbol_budget_exceeded",
                statusJson.GetProperty("reference_graph_incomplete_reasons").EnumerateArray().Select(value => value.GetString()));
            AssertReferenceCapHitSummary(statusJson.GetProperty("reference_extraction_cap_hits"), "app.py");
            AssertReferenceCapHitSummary(
                statusJson.GetProperty("last_index_run").GetProperty("reference_extraction_cap_hits"),
                "app.py");

            var (checkExitCode, checkJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check=graph", "--json"]);
            Assert.NotEqual(CommandExitCodes.Success, checkExitCode);
            var repairCommand = Assert.Single(checkJson.GetProperty("repair_commands").EnumerateArray());
            Assert.Equal("reference_graph_complete", repairCommand.GetProperty("reason").GetString());
            Assert.Contains(
                "cap-hitting",
                repairCommand.GetProperty("safety_notes")[0].GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            ReferenceExtractor.SafetyLimitsForTesting = previousLimits;
            DeleteDirectory(projectRoot);
        }
    }

    private static void AssertReferenceCapHitSummary(JsonElement summary, string expectedFile)
    {
        Assert.True(summary.GetProperty("state_available").GetBoolean());
        Assert.True(summary.GetProperty("hit_count").GetInt64() > 0);
        Assert.Equal(1, summary.GetProperty("affected_file_count").GetInt64());
        Assert.Contains(
            "reference_definition_lookup_symbol_budget_exceeded",
            summary.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));
        var file = Assert.Single(summary.GetProperty("files").EnumerateArray());
        Assert.Equal(expectedFile, file.GetProperty("file").GetString());
        Assert.True(file.GetProperty("hit_count").GetInt64() > 0);
    }

    [Fact]
    public void Run_FullScan_RechecksIndexabilityBeforeContentRead()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_read_probe_race");
        var outsideRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_read_probe_outside");
        var sourcePath = Path.Combine(projectRoot, "app.py");
        var outsidePath = Path.Combine(outsideRoot, "outside.py");
        var swapped = false;
        try
        {
            File.WriteAllText(sourcePath, "def project_value():\n    return 1\n");
            File.WriteAllText(outsidePath, "def escaped_secret():\n    return 2\n");

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
                    {
                        if (!string.Equals(path, "app.py", StringComparison.Ordinal) || swapped)
                            return;

                        File.Delete(sourcePath);
                        File.CreateSymbolicLink(sourcePath, outsidePath);
                        swapped = true;
                    };

                    var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.True(swapped);
                    Assert.Equal(CommandExitCodes.PartialResult, exitCode);
                    Assert.Equal("partial", json.GetProperty("status").GetString());
                    // The guarded content read reports the unsafe target, and the final
                    // snapshot barrier independently reports the directory-entry drift.
                    Assert.Equal(2, json.GetProperty("summary").GetProperty("errors").GetInt32());
                    Assert.Contains(
                        json.GetProperty("errors").EnumerateArray(),
                        error => string.Equals(error.GetProperty("file").GetString(), sourcePath, StringComparison.Ordinal));
                    Assert.Contains(
                        json.GetProperty("errors").EnumerateArray(),
                        error => error.GetProperty("file").GetString() == "."
                            && error.GetProperty("message").GetString()!.StartsWith(
                                nameof(IOException),
                                StringComparison.Ordinal));
                    Assert.DoesNotContain("app.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteDirectory(outsideRoot);
        }
    }

    [Fact]
    public void Run_FullScan_IncludeSymbolKindKeepsOnlyMatchingSymbols()
    {
        var projectRoot = CreateTempProject();
        var previousArtifactHook =
            CSharpPrepassSymbolArtifactCache.EventForTesting;
        var artifactEvents =
            new ConcurrentQueue<CSharpPrepassSymbolArtifactCacheEvent>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), """
                class App:
                    pass

                def helper():
                    return App()
                """);
            const string csharpRelativePath = "Cafe\u0301.cs";
            var csharpIndexPath = FileIndexer.NormalizeIndexPath(csharpRelativePath);
            var csharpPath = Path.Combine(projectRoot, csharpRelativePath);
            const string csharpSource = """
                namespace Demo;
                file partial class Fixture
                {
                    public static int RemovedByFilter() => 1;
                }
                """;
            File.WriteAllText(csharpPath, csharpSource);
            var indexer = new FileIndexer(projectRoot, ignoreCase: false);
            var expectedCSharpSymbols = SymbolExtractor.Extract(
                0,
                "csharp",
                csharpSource,
                csharpPath,
                projectRoot);
            SymbolExtractor.ApplyFamilyScope(
                expectedCSharpSymbols,
                indexer.GetFamilyScopeKey(csharpPath, "csharp"),
                "csharp");
            var expectedFamilyKey = Assert.Single(
                expectedCSharpSymbols,
                symbol => symbol.Kind == "class"
                          && symbol.Name == "Fixture").FamilyKey;
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                artifactEvents.Enqueue;

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--include-symbol-kind", "class", "--parallelism", "1", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("symbols_dropped_by_kind_filter").GetInt32() > 0);
            Assert.Contains(
                artifactEvents,
                item => item.Phase == "taken"
                        && item.Path == csharpIndexPath);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var counts = ReadSymbolKindCounts(dbPath);
            Assert.True(counts.GetValueOrDefault("class") > 0);
            Assert.DoesNotContain(counts.Keys, kind => !string.Equals(kind, "class", StringComparison.OrdinalIgnoreCase));
            using var connection = OpenNonPoolingConnection(dbPath);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT family_key
                FROM symbols
                WHERE file_id = (SELECT id FROM files WHERE path = $path)
                  AND kind = 'class'
                  AND name = 'Fixture'
                """;
            command.Parameters.AddWithValue("$path", csharpIndexPath);
            Assert.Equal(expectedFamilyKey, command.ExecuteScalar());
        }
        finally
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                previousArtifactHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_FinalizesMutualRecursionAfterBulkReferenceInsert()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "cycle_a.cs"), """
                public static class FullScanCycleA
                {
                    public static void CrossCycleA() { CrossCycleB(); }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "cycle_b.cs"), """
                public static class FullScanCycleB
                {
                    public static void CrossCycleB() { CrossCycleA(); }
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(CountMutualRecursionReferences(Path.Combine(projectRoot, ".cdidx", "codeindex.db")) >= 2);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SkipsOversizedGitExclude()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var excludePath = Path.Combine(projectRoot, ".git", "info", "exclude");
            File.WriteAllText(excludePath, new string('x', IndexCommandRunner.MaxGitExcludeBytes + 1));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(IndexCommandRunner.MaxGitExcludeBytes + 1, File.ReadAllText(excludePath).Length);
            Assert.DoesNotContain("cdidx (CodeIndex)", File.ReadAllText(excludePath));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterHeadChange_ParallelizesOnlyChangedFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_head_changed_skip_before_extract");
        bool? parallelized = null;
        string? reason = null;
        int? queueCapacity = null;
        var loadedPaths = new ConcurrentBag<string>();
        var statLookups = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var statSnapshotReads = 0;
        var foldBackfillVerifications = 0;
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            RunGit(projectRoot, "add", "app.cs", "app.ts");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { public void Run() { } }\n");
            RunGit(projectRoot, "add", "feature.cs");
            RunGit(projectRoot, "commit", "-m", "next");

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, why) =>
                    {
                        parallelized = enabled;
                        reason = why;
                    };
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);
                    IndexCommandRunner.FullScanExtractionQueueCapacityForTesting = capacity => queueCapacity = capacity;
                    IndexedFileStatReuse.LookupForTesting = path => statLookups.AddOrUpdate(path, 1, static (_, count) => count + 1);
                    DbWriter.ReusableStatSnapshotReadForTesting = () => statSnapshotReads++;
                    DbWriter.FoldBackfillVerificationForTesting = () => foldBackfillVerifications++;

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.False(refreshJson.GetProperty("head_changed").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, refreshJson.GetProperty("head_change_notice").ValueKind);
                    Assert.True(parallelized);
                    Assert.Equal("incremental_changes", reason);
                    Assert.Equal(2, queueCapacity);
                    Assert.Equal(2, refreshJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
                    Assert.DoesNotContain("app.cs", loadedPaths);
                    Assert.DoesNotContain("app.ts", loadedPaths);
                    Assert.Contains("feature.cs", loadedPaths);
                    Assert.Equal(2, statLookups["app.cs"]);
                    Assert.Equal(2, statLookups["app.ts"]);
                    Assert.Equal(1, statLookups["feature.cs"]);
                    Assert.Equal(1, statSnapshotReads);
                    Assert.Equal(1, foldBackfillVerifications);
                }
                finally
                {
                    IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                    IndexCommandRunner.FullScanExtractionQueueCapacityForTesting = null;
                    IndexedFileStatReuse.LookupForTesting = null;
                    DbWriter.ReusableStatSnapshotReadForTesting = null;
                    DbWriter.FoldBackfillVerificationForTesting = null;
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BuildFullScanExtractionTailSchedule_PrioritizesLargestKnownFilesWithinBoundedSuffix()
    {
        long?[] lengths = [null, null, null, 4, 100, 100, 2_000, 10, 900, 2];
        var probedOrdinals = new List<int>();

        var schedule = IndexCommandRunner.BuildFullScanExtractionTailSchedule(
            workItemCount: lengths.Length,
            workerCount: 2,
            maxFileSizeBytes: 1_000,
            workOrdinal =>
            {
                probedOrdinals.Add(workOrdinal);
                return lengths[workOrdinal];
            },
            CancellationToken.None);

        Assert.Equal(Enumerable.Range(2, 8), probedOrdinals);
        Assert.Equal([8, 4, 5, 7, 3, 9, 2, 6], schedule);
    }

    [Theory]
    [InlineData(6, 1)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    public void BuildFullScanExtractionTailSchedule_NonParallelOrFirstWorkerWave_DoesNotProbe(
        int workItemCount,
        int workerCount)
    {
        var probeCount = 0;

        var schedule = IndexCommandRunner.BuildFullScanExtractionTailSchedule(
            workItemCount,
            workerCount,
            maxFileSizeBytes: 1_000,
            _ =>
            {
                probeCount++;
                return 1;
            },
            CancellationToken.None);

        Assert.Empty(schedule);
        Assert.Equal(0, probeCount);
    }

    [Fact]
    public void BuildFullScanExtractionTailSchedule_OneItemBeyondFirstWorkerWave_StillProbes()
    {
        var probedOrdinals = new List<int>();

        var schedule = IndexCommandRunner.BuildFullScanExtractionTailSchedule(
            workItemCount: 5,
            workerCount: 4,
            maxFileSizeBytes: 1_000,
            workOrdinal =>
            {
                probedOrdinals.Add(workOrdinal);
                return workOrdinal;
            },
            CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 5), probedOrdinals);
        Assert.Equal([4, 3, 2, 1, 0], schedule);
    }

    [Fact]
    public void ResolveFullScanExtractionFileIndex_UsesSparseMappingOrWorkOrdinalFallback()
    {
        int[] extractionFileIndexes = [11, 3, 17, 5];

        Assert.Equal(
            [11, 3, 17, 5],
            Enumerable.Range(0, extractionFileIndexes.Length)
                .Select(workOrdinal =>
                    IndexCommandRunner.ResolveFullScanExtractionFileIndex(
                        extractionFileIndexes,
                        workOrdinal)));
        Assert.Equal(
            7,
            IndexCommandRunner.ResolveFullScanExtractionFileIndex(
                extractionFileIndexes: null,
                workOrdinal: 7));
    }

    [Fact]
    public void BuildFullScanExtractionTailSchedule_BoundsProbeAndScheduleState()
    {
        const int workItemCount = 1_000;
        var probedOrdinals = new List<int>();

        var schedule = IndexCommandRunner.BuildFullScanExtractionTailSchedule(
            workItemCount,
            workerCount: 16,
            maxFileSizeBytes: long.MaxValue,
            workOrdinal =>
            {
                probedOrdinals.Add(workOrdinal);
                return workOrdinal;
            },
            CancellationToken.None);

        Assert.Equal(IndexCommandRunner.MaxFullScanExtractionTailProbeCount, schedule.Length);
        Assert.Equal(
            Enumerable.Range(
                workItemCount - IndexCommandRunner.MaxFullScanExtractionTailProbeCount,
                IndexCommandRunner.MaxFullScanExtractionTailProbeCount),
            probedOrdinals);
        Assert.Equal(probedOrdinals.AsEnumerable().Reverse(), schedule);
    }

    [Fact]
    public void BuildFullScanExtractionTailSchedule_TreatsExpectedProbeFailuresAsStableUnknowns()
    {
        var schedule = IndexCommandRunner.BuildFullScanExtractionTailSchedule(
            workItemCount: 10,
            workerCount: 2,
            maxFileSizeBytes: 1_000,
            workOrdinal => workOrdinal switch
            {
                2 => throw new IOException("simulated size probe failure"),
                3 => 10,
                4 => throw new UnauthorizedAccessException("simulated size probe denial"),
                5 => 20,
                >= 6 => null,
                _ => throw new InvalidOperationException("prefix must not be probed"),
            },
            CancellationToken.None);

        Assert.Equal([5, 3, 2, 4, 6, 7, 8, 9], schedule);
    }

    [Fact]
    public void BuildFullScanExtractionTailSchedule_PreCancelled_DoesNotProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probeCount = 0;

        Assert.Throws<OperationCanceledException>(() =>
            IndexCommandRunner.BuildFullScanExtractionTailSchedule(
                workItemCount: 16,
                workerCount: 4,
                maxFileSizeBytes: 1_000,
                _ =>
                {
                    probeCount++;
                    return 1;
                },
                cancellation.Token));
        Assert.Equal(0, probeCount);
    }

    [Fact]
    public void BuildFullScanExtractionTailSchedule_CancelledAfterProbe_StopsBeforeNextProbe()
    {
        using var cancellation = new CancellationTokenSource();
        var probedOrdinals = new List<int>();

        Assert.Throws<OperationCanceledException>(() =>
            IndexCommandRunner.BuildFullScanExtractionTailSchedule(
                workItemCount: 10,
                workerCount: 2,
                maxFileSizeBytes: 1_000,
                workOrdinal =>
                {
                    probedOrdinals.Add(workOrdinal);
                    cancellation.Cancel();
                    return 1;
                },
                cancellation.Token));
        Assert.Equal([2], probedOrdinals);
    }

    [Fact]
    public void Run_FullScan_DenseDeletionCapsReusableStatSnapshotCapacityToRetainedRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_reuse_capacity");
        var previousCapacityHook = DbWriter.ReusableStatSnapshotInitialCapacityForTesting;
        var previousFilterModeHook = DbWriter.ReusableStatSnapshotFilterModeForTesting;
        var previousCandidateRowHook = DbWriter.ReusableStatSnapshotCandidateRowForTesting;
        int? reusableSnapshotInitialCapacity = null;
        var filterModes = new List<string>();
        var candidateRows = new List<string>();
        try
        {
            var retainedPath = Path.Combine(projectRoot, "retained.py");
            File.WriteAllText(retainedPath, "def retained():\n    return 1\n");
            var stalePaths = Enumerable.Range(0, 32)
                .Select(index => Path.Combine(projectRoot, $"stale-{index:D2}.py"))
                .ToArray();
            foreach (var stalePath in stalePaths)
                File.WriteAllText(stalePath, "def stale():\n    return 1\n");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(stalePaths.Length + 1, initialJson.GetProperty("summary").GetProperty("files_scanned").GetInt32());

            foreach (var stalePath in stalePaths)
                File.Delete(stalePath);
            DbWriter.ReusableStatSnapshotInitialCapacityForTesting = capacity =>
            {
                reusableSnapshotInitialCapacity = capacity;
                previousCapacityHook?.Invoke(capacity);
            };
            DbWriter.ReusableStatSnapshotFilterModeForTesting = mode =>
            {
                filterModes.Add(mode);
                previousFilterModeHook?.Invoke(mode);
            };
            DbWriter.ReusableStatSnapshotCandidateRowForTesting = path =>
            {
                candidateRows.Add(path);
                previousCandidateRowHook?.Invoke(path);
            };

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal(1, reusableSnapshotInitialCapacity);
            Assert.Equal(new[] { "excluded_ids" }, filterModes);
            Assert.Equal(new[] { "retained.py" }, candidateRows);
            Assert.Equal(stalePaths.Length, refreshJson.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal(1, refreshJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
        }
        finally
        {
            DbWriter.ReusableStatSnapshotInitialCapacityForTesting = previousCapacityHook;
            DbWriter.ReusableStatSnapshotFilterModeForTesting = previousFilterModeHook;
            DbWriter.ReusableStatSnapshotCandidateRowForTesting = previousCandidateRowHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_IncompleteLegacyStatReindexesFile()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_incomplete_legacy_stat");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = OpenNonPoolingConnection(dbPath))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = "UPDATE files SET size = NULL WHERE path = 'app.cs'";
                Assert.Equal(1, command.ExecuteNonQuery());
            }

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.cs", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "SELECT size FROM files WHERE path = 'app.cs'";
            Assert.IsType<long>(verifyCommand.ExecuteScalar());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_StatSnapshotObservesCancellationToken()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_stat_snapshot_cancel");
        using var cancellation = new CancellationTokenSource();
        var hookInvoked = false;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    DbWriter.ReusableStatSnapshotReadForTesting = () =>
                    {
                        hookInvoked = true;
                        cancellation.Cancel();
                    };

                    var (exitCode, json) = RunAndCaptureJson(
                        [projectRoot, "--json"],
                        cancellation);

                    Assert.True(hookInvoked);
                    Assert.Equal(CommandExitCodes.Interrupted, exitCode);
                    Assert.Equal(CommandErrorCodes.Interrupted, json.GetProperty("error_code").GetString());
                }
                finally
                {
                    DbWriter.ReusableStatSnapshotReadForTesting = null;
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_GraphNeutralChangeSkipsMutualRecursionRefresh()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_graph_neutral");
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var refreshCount = 0;
        try
        {
            var graphNeutralPath = Path.Combine(projectRoot, "graph-neutral.py");
            File.WriteAllText(graphNeutralPath, "# text-only source\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };
            File.WriteAllText(graphNeutralPath, "# changed text-only source\n");
            File.SetLastWriteTimeUtc(graphNeutralPath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_scanned").GetInt32());
            Assert.Equal(0, refreshCount);
        }
        finally
        {
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ChangedReferenceUsesDirtyGraphScope()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_dirty_graph_scope");
        var previousScopeHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        DbWriter.ReferenceGraphRefreshScopeStats? observed = null;
        try
        {
            var firstPath = Path.Combine(projectRoot, "MutualRecursionA.cs");
            File.WriteAllText(
                firstPath,
                "public static class MutualRecursionA { public static void CrossCycleA() { CrossCycleB(); } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "MutualRecursionB.cs"),
                "public static class MutualRecursionB { public static void CrossCycleB() { CrossCycleA(); } }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DbWriter.ReferenceGraphRefreshScopeForTesting = stats =>
            {
                observed = stats;
                previousScopeHook?.Invoke(stats);
            };
            File.AppendAllText(firstPath, "// changed A\n");
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.InRange(json.GetProperty("summary").GetProperty("files_scanned").GetInt32(), 1, 2);
            Assert.NotNull(observed);
            Assert.False(observed!.UsedFullRefresh);
            Assert.Equal(2, observed.DirtyReferenceCount);
            Assert.Equal(2, observed.TotalReferenceCount);
        }
        finally
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousScopeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_CancelledDuringMutualRecursionRefresh_LeavesReadinessDegraded()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_mutual_refresh_cancel");
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        using var cancellation = new CancellationTokenSource();
        var hookInvoked = false;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "MutualRecursionA.cs"),
                "public static class MutualRecursionA { public static void CrossCycleA() { CrossCycleB(); } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "MutualRecursionB.cs"),
                "public static class MutualRecursionB { public static void CrossCycleB() { CrossCycleA(); } }\n");
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                hookInvoked = true;
                cancellation.Cancel();
                previousRefreshHook?.Invoke();
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"], cancellation);

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, exitCode);
            Assert.Equal(CommandErrorCodes.Interrupted, json.GetProperty("error_code").GetString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(DbContext.HotspotReferenceAggregateFlags, db.GetUserVersion());
        }
        finally
        {
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterTypeScriptConfigChange_ReprocessesUnchangedTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_tsconfig_refresh");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), "{ \"compilerOptions\": { \"baseUrl\": \".\" } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), "{ \"compilerOptions\": { \"baseUrl\": \".\", \"paths\": {} } }\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "tsconfig.json"), DateTime.UtcNow.AddSeconds(2));

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.ts", loadedPaths);
                    Assert.Contains("tsconfig.json", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterTypeScriptConfigContentChangeWithStableStat_ReprocessesUnchangedTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_tsconfig_stable_stat");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            var configPath = Path.Combine(projectRoot, "tsconfig.json");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            File.WriteAllText(configPath, "{ \"compilerOptions\": { \"baseUrl\": \".\" } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var originalStat = new FileInfo(configPath);
            var replacement = "{ \"compilerOptions\": { \"baseUrl\": \"x\" } }\n";
            Assert.Equal(originalStat.Length, Encoding.UTF8.GetByteCount(replacement));
            File.WriteAllText(configPath, replacement);
            File.SetLastWriteTimeUtc(configPath, originalStat.LastWriteTimeUtc);

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.ts", loadedPaths);
                    Assert.Contains("tsconfig.json", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanAfterDerivedTypeScriptConfigDelete_ReprocessesUnchangedTypeScriptFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_tsconfig_base_delete");
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "export interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.base.json"), "{ \"compilerOptions\": { \"baseUrl\": \".\" } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(Path.Combine(projectRoot, "tsconfig.base.json"));

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

                    var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

                    Assert.Equal(CommandExitCodes.Success, refreshExitCode);
                    Assert.Equal("success", refreshJson.GetProperty("status").GetString());
                    Assert.Contains("app.ts", loadedPaths);
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanWithSequentialExtraction_PersistsValidationIssues()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_sequential_validation");
        bool? parallelized = null;
        int? queueCapacity = null;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App\rpublic class Other\n");

            IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, _) => parallelized = enabled;
            IndexCommandRunner.FullScanExtractionQueueCapacityForTesting = capacity => queueCapacity = capacity;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "test.method", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(parallelized);
            Assert.Equal(1, queueCapacity);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var issue = Assert.Single(ReadFileIssues(dbPath, "mixed_line_endings"));
            Assert.Equal("app.cs", issue.Path);
        }
        finally
        {
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            IndexCommandRunner.FullScanExtractionQueueCapacityForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanWithoutCSharp_DoesNotRunCSharpPrepass()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fullscan_no_csharp_prepass");
        var ranCSharpPrepass = false;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 1\n");

            IndexCommandRunner.FullScanCSharpPrepassForTesting = () => ranCSharpPrepass = true;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(ranCSharpPrepass);
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpPrepassForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FreshBuiltInSpecialFoldIdentitiesMatchFullValidator()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_fresh_special_fold_identities");
        var foldValueVerifications = 0;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "guide.md"),
                "# Café Guide\n\n## Shared Heading\n\n## Shared Heading\n\n[again](#shared-heading-1)\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "service.cs"),
                "public interface IFoo { void Run(); }\n"
                + "public class Service : IFoo { void IFoo.Run() { } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "style.nim"),
                "proc My_Proc*() = discard\nproc call*() = myProc()\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "worker.ts"),
                "interface Worker { runTask(): void; }\n"
                + "class Impl implements Worker { runTask(): void {} }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "unicode.py"),
                "def Straße():\n    return 1\n");
            DbWriter.FoldValueVerificationForTesting = () => foldValueVerifications++;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, foldValueVerifications);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(db.Connection);
            Assert.True(writer.AllFoldedColumnValuesMatchCurrentFold());
            Assert.Equal(1, foldValueVerifications);
            Assert.Equal(
                DbContext.FoldReadyFlag,
                db.GetUserVersion() & DbContext.FoldReadyFlag);
        }
        finally
        {
            DbWriter.FoldValueVerificationForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FreshFoldClaim_TransientCustomProducerHistoryFailsClosed()
    {
        lock (TestConsoleLock.Gate)
        {
            var projectRoot = TestProjectHelper.CreateTempProject(
                "cdidx_fresh_fold_transient_custom_producer");
            var foldValueVerifications = 0;
            var reloaded = 0;
            ExtractorPluginRegistry.FoldProducerReadinessSnapshot? customSnapshot = null;
            ExtractorPluginRegistry.FoldProducerReadinessSnapshot? restoredSnapshot = null;
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                File.WriteAllText(
                    Path.Combine(projectRoot, "app.py"),
                    "def Straße():\n    return 1\n");
                var initialSnapshot =
                    ExtractorPluginRegistry.CaptureFoldProducerReadinessSnapshot(projectRoot);
                Assert.True(initialSnapshot.UsesOnlyBuiltInProducers);

                DbWriter.FoldValueVerificationForTesting = () => foldValueVerifications++;
                IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = phase =>
                {
                    if (!string.Equals(phase, "before_readiness", StringComparison.Ordinal)
                        || Interlocked.Exchange(ref reloaded, 1) != 0)
                    {
                        return;
                    }

                    var patternsDirectory = Path.Combine(projectRoot, ".cdidx", "patterns");
                    Directory.CreateDirectory(patternsDirectory);
                    var patternPath = Path.Combine(patternsDirectory, "transient.yaml");
                    File.WriteAllText(
                        patternPath,
                        "language: \"transientdsl\"\n"
                        + "extensions:\n  - extension: \".transient\"\n"
                        + "patterns:\n  - kind: \"class\"\n"
                        + "    regex: \"^entity (?<name>\\\\w+)\"\n");
                    ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(projectRoot);
                    customSnapshot =
                        ExtractorPluginRegistry.CaptureFoldProducerReadinessSnapshot(projectRoot);

                    File.Delete(patternPath);
                    Directory.Delete(patternsDirectory);
                    ExtractorPluginRegistry.ReloadPatternConfigsForProjectRoot(projectRoot);
                    restoredSnapshot =
                        ExtractorPluginRegistry.CaptureFoldProducerReadinessSnapshot(projectRoot);
                };

                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("success", json.GetProperty("status").GetString());
                Assert.Equal(1, reloaded);
                Assert.False(customSnapshot!.Value.UsesOnlyBuiltInProducers);
                Assert.True(restoredSnapshot!.Value.UsesOnlyBuiltInProducers);
                Assert.NotEqual(
                    initialSnapshot.MutationGeneration,
                    restoredSnapshot.Value.MutationGeneration);
                Assert.Equal(1, foldValueVerifications);
            }
            finally
            {
                IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = null;
                DbWriter.FoldValueVerificationForTesting = null;
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Run_FullScanAfterHeadChange_WithPostExtractionHooksKeepsSequentialReferences()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_head_changed_hooks_sequential");
        using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
            "cdidx_head_changed_hook_extensions_sequential");
        bool? parallelized = null;
        var originalHooksDir = Environment.GetEnvironmentVariable("CDIDX_HOOKS_DIR");
        var previousFoldValueVerification = DbWriter.FoldValueVerificationForTesting;
        var foldValueVerifications = 0;
        try
        {
            var hooksDir = Path.Combine(extensionProject.Root, "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookAssemblyPath = typeof(
                CodeIndex.HookIsolationFixture.PathSelectivePostExtractionHook).Assembly.Location;
            File.Copy(
                hookAssemblyPath,
                Path.Combine(hooksDir, Path.GetFileName(hookAssemblyPath)));
            Environment.SetEnvironmentVariable("CDIDX_HOOKS_DIR", hooksDir);
            DbWriter.FoldValueVerificationForTesting = () =>
            {
                foldValueVerifications++;
                previousFoldValueVerification?.Invoke();
            };

            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            var initialStatus = initialJson.GetProperty("status").GetString();
            Assert.Contains(initialStatus, ["success", "partial"]);
            Assert.Equal(
                initialStatus == "partial" ? CommandExitCodes.PartialResult : CommandExitCodes.Success,
                initialExitCode);
            Assert.Equal(initialStatus == "success" ? 1 : 0, foldValueVerifications);
            DbWriter.FoldValueVerificationForTesting = previousFoldValueVerification;

            File.AppendAllText(Path.Combine(projectRoot, "app.cs"), "public class Next { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "next");

            IndexCommandRunner.FullScanExtractionSchedulingForTesting = (enabled, _) => parallelized = enabled;

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

            var refreshStatus = refreshJson.GetProperty("status").GetString();
            Assert.Contains(refreshStatus, ["success", "partial"]);
            Assert.Equal(
                refreshStatus == "partial" ? CommandExitCodes.PartialResult : CommandExitCodes.Success,
                refreshExitCode);
            Assert.False(parallelized);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_HOOKS_DIR", originalHooksDir);
            DbWriter.FoldValueVerificationForTesting = previousFoldValueVerification;
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanExplicitDb_FailedFirstMutation_DoesNotRewriteIndexedProjectRootMetadata()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_fullscan_explicit_rollback_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRootA, "init");
            var sourcePathA = Path.Combine(projectRootA, "app.cs");
            File.WriteAllText(sourcePathA, "public class AppA { public void Run() { } }\n");
            RunGit(projectRootA, "add", ".");
            RunGit(projectRootA, "commit", "-m", "init-a");
            var headA = RunGitCaptureStdOut(projectRootA, "rev-parse", "HEAD").Trim();

            RunGit(projectRootB, "init");
            var sourcePathB = Path.Combine(projectRootB, "app.cs");
            File.WriteAllText(sourcePathB, "public class AppB { public void Run() { } public void Extra() { } }\n");
            RunGit(projectRootB, "add", ".");
            RunGit(projectRootB, "commit", "-m", "init-b");
            File.SetLastWriteTimeUtc(sourcePathB, DateTime.UtcNow.AddSeconds(2));

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TRIGGER fail_update
                    BEFORE UPDATE ON files
                    BEGIN
                        SELECT RAISE(FAIL, 'boom');
                    END;
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRootA), statusJson.GetProperty("project_root").GetString());
            Assert.Equal(headA, statusJson.GetProperty("git_head").GetString());
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_FullScanExplicitDb_SuccessfulNoOpBackfillsMissingIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_fullscan_explicit_noop_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var head = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRoot), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRoot), statusJson.GetProperty("project_root").GetString());
            Assert.Equal(head, statusJson.GetProperty("git_head").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_FullScanExplicitDb_KnownRootSwitchRejectsPositiveCsharpStatReuse()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_fullscan_csharp_root_switch_{Guid.NewGuid():N}.db");
        var contractSource =
            "public interface IParseable<T> { static abstract T Parse(string s); }\n";
        var plainPrefix = "public interface IParseable<T> { }\n";
        var plainSource = plainPrefix.TrimEnd('\n').PadRight(contractSource.Length - 1) + "\n";
        var implementationSource =
            "public readonly struct Money : IParseable<Money>\n"
            + "{\n"
            + "    public static Money Parse(string s) => new();\n"
            + "}\n";
        var sharedModified = DateTime.UtcNow.AddMinutes(-5);
        try
        {
            foreach (var projectRoot in new[] { projectRootA, projectRootB })
                File.WriteAllText(Path.Combine(projectRoot, "Money.cs"), implementationSource);
            File.WriteAllText(Path.Combine(projectRootA, "IParseable.cs"), contractSource);
            File.WriteAllText(Path.Combine(projectRootB, "IParseable.cs"), plainSource);
            foreach (var projectRoot in new[] { projectRootA, projectRootB })
            {
                File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "IParseable.cs"), sharedModified);
                File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "Money.cs"), sharedModified);
            }

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json", "--quiet"], _jsonOptions));

            var (exitCode, json) = RunAndCaptureJson(
                [projectRootB, "--db", dbPath, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var command = conn.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbol_references r
                    JOIN files f ON f.id = r.file_id
                    WHERE f.path = 'Money.cs'
                      AND r.symbol_name = 'Parse'
                      AND r.reference_kind = 'implicit_implementation'
                    """;
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
            }
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(Path.GetFullPath(projectRootB), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            Assert.Equal(false, new DbWriter(db).GetCSharpStaticInterfaceSourceEvidence());
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_FullScan_FatalParallelResultKeepsWorkerResourcesAliveUntilPeersStop()
    {
        var projectRoot = CreateTempProject();
        lock (FullScanContentLoadHookGate)
        {
            var previousPhaseHook = IndexCommandRunner.FullScanFilePhaseForTesting;
            var previousWorkStartedHook =
                IndexCommandRunner.FullScanExtractionWorkStartedForTesting;
            var previousWorkersStoppedHook =
                IndexCommandRunner.FullScanExtractionWorkersStoppedForTesting;
            var previousArtifactHook =
                CSharpPrepassSymbolArtifactCache.EventForTesting;
            using var blockedWorkerStarted = new ManualResetEventSlim();
            using var releaseBlockedWorker = new ManualResetEventSlim();
            using var workersStopped = new ManualResetEventSlim();
            using var artifactCleared = new ManualResetEventSlim();
            var artifactEvents =
                new ConcurrentQueue<CSharpPrepassSymbolArtifactCacheEvent>();
            var pipelineUsed = 0;
            try
            {
                File.WriteAllText(
                    Path.Combine(projectRoot, "A.cs"),
                    "public class A { public static int Value => 1; }\n");
                File.WriteAllText(
                    Path.Combine(projectRoot, "B.cs"),
                    "public class B { public static int Value => 2; }\n");
                CSharpPrepassSymbolArtifactCache.EventForTesting = item =>
                {
                    artifactEvents.Enqueue(item);
                    if (item.Phase == "cleared")
                        artifactCleared.Set();
                };
                IndexCommandRunner.FullScanExtractionWorkStartedForTesting = () =>
                    Interlocked.Exchange(ref pipelineUsed, 1);
                IndexCommandRunner.FullScanExtractionWorkersStoppedForTesting =
                    workersStopped.Set;
                IndexCommandRunner.FullScanFilePhaseForTesting = (path, phase) =>
                {
                    if (phase != "symbols")
                        return;
                    if (path == "A.cs")
                    {
                        blockedWorkerStarted.Set();
                        releaseBlockedWorker.Wait(TimeSpan.FromSeconds(30));
                        return;
                    }
                    if (path == "B.cs")
                    {
                        if (!blockedWorkerStarted.Wait(TimeSpan.FromSeconds(30)))
                        {
                            throw new TimeoutException(
                                "The blocked full-scan peer did not reach symbol extraction.");
                        }
                        throw new IndexCommandRunner.IndexExtractionStalledException(
                            0,
                            null,
                            TimeSpan.FromMilliseconds(10),
                            "B.cs [symbols]",
                            "injected fatal full-scan result");
                    }
                };

                var stopwatch = Stopwatch.StartNew();
                var (exitCode, json) = RunAndCaptureJson(
                    [projectRoot, "--parallelism", "2", "--json", "--quiet"]);
                stopwatch.Stop();

                Assert.True(blockedWorkerStarted.IsSet);
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"The fatal full-scan result waited for its blocked peer for {stopwatch.Elapsed}.");
                Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
                Assert.Equal(
                    CommandErrorCodes.IndexExtractionStalled,
                    json.GetProperty("error_code").GetString());
                Assert.Contains(
                    "B.cs [symbols]",
                    json.GetProperty("message").GetString(),
                    StringComparison.Ordinal);
                Assert.Contains(
                    "injected fatal full-scan result",
                    json.GetProperty("message").GetString(),
                    StringComparison.Ordinal);
                Assert.False(workersStopped.IsSet);
                Assert.Contains(
                    artifactEvents,
                    item => item.Phase == "admitted" && item.Path == "A.cs");
                Assert.Contains(
                    artifactEvents,
                    item => item.Phase == "admitted" && item.Path == "B.cs");
                Assert.DoesNotContain(artifactEvents, item => item.Phase == "cleared");

                releaseBlockedWorker.Set();
                Assert.True(
                    workersStopped.Wait(TimeSpan.FromSeconds(30)),
                    "The fatal full-scan extraction workers did not stop.");
                Assert.True(
                    artifactCleared.Wait(TimeSpan.FromSeconds(30)),
                    "The full-scan worker resources were not cleared after the workers stopped.");
                var completedEvents = artifactEvents.ToArray();
                var takenIndex = Array.FindIndex(
                    completedEvents,
                    item => item.Phase == "taken" && item.Path == "A.cs");
                var clearedIndex = Array.FindIndex(
                    completedEvents,
                    item => item.Phase == "cleared");
                Assert.True(takenIndex >= 0);
                Assert.True(clearedIndex > takenIndex);
                Assert.Equal(1, completedEvents.Count(item => item.Phase == "cleared"));
            }
            finally
            {
                releaseBlockedWorker.Set();
                var cleanupSafe = Volatile.Read(ref pipelineUsed) == 0
                    || workersStopped.Wait(TimeSpan.FromSeconds(30));
                IndexCommandRunner.FullScanFilePhaseForTesting = previousPhaseHook;
                IndexCommandRunner.FullScanExtractionWorkStartedForTesting =
                    previousWorkStartedHook;
                IndexCommandRunner.FullScanExtractionWorkersStoppedForTesting =
                    previousWorkersStoppedHook;
                CSharpPrepassSymbolArtifactCache.EventForTesting =
                    previousArtifactHook;
                if (cleanupSafe)
                    DeleteDirectory(projectRoot);
                SqliteConnection.ClearAllPools();
            }
        }
    }

    [Fact]
    public void Run_FullScan_ParallelCsharpStaticInterfacePrepass_IndexesImplicitImplementationReference()
    {
        const int implementationFileCount = 64;
        var projectRoot = CreateTempProject();
        var previousLookupHook = ReferenceExtractor.CSharpStaticInterfaceMemberLookupsBuiltForTesting;
        var previousPrepassHook = IndexCommandRunner.FullScanCSharpPrepassForTesting;
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        var previousArtifactHook =
            CSharpPrepassSymbolArtifactCache.EventForTesting;
        var artifactEvents =
            new ConcurrentQueue<CSharpPrepassSymbolArtifactCacheEvent>();
        var matchingLookupBuilds = 0;
        var noOpPrepassCount = 0;
        var noOpContentLoadCount = 0;
        try
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                artifactEvents.Enqueue;
            ReferenceExtractor.CSharpStaticInterfaceMemberLookupsBuiltForTesting = symbols =>
            {
                if (symbols.Any(symbol =>
                    symbol.Kind == "interface"
                    && symbol.Name == "IPrecomputedLookupFixture"))
                {
                    matchingLookupBuilds++;
                }
                previousLookupHook?.Invoke(symbols);
            };
            File.WriteAllText(
                Path.Combine(projectRoot, "IParseable.cs"),
                """
                public interface IPrecomputedLookupFixture<T>
                {
                    static abstract T Parse(string s);
                }
                """);
            for (var fileIndex = 0; fileIndex < implementationFileCount; fileIndex++)
            {
                var typeName = $"Money{fileIndex:D3}";
                File.WriteAllText(
                    Path.Combine(projectRoot, typeName + ".cs"),
                    $$"""
                    public readonly struct {{typeName}} : IPrecomputedLookupFixture<{{typeName}}>
                    {
                        public static {{typeName}} Parse(string s) => new();
                    }
                    """);
            }

            var exitCode = IndexCommandRunner.Run(
                [projectRoot, "--parallelism", "4", "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(
                implementationFileCount + 1,
                artifactEvents.Count(item => item.Phase == "admitted"));
            Assert.Equal(
                implementationFileCount + 1,
                artifactEvents.Count(item => item.Phase == "taken"));
            Assert.Contains(
                artifactEvents,
                item => item.Phase == "cleared");
            artifactEvents.Clear();
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            InstallCSharpEvidenceWriteAudit(dbPath);

            IndexCommandRunner.FullScanCSharpPrepassForTesting = () => noOpPrepassCount++;
            IndexCommandRunner.FullScanFileContentLoadForTesting = _ => noOpContentLoadCount++;
            var noOpExitCode = IndexCommandRunner.Run(
                [projectRoot, "--parallelism", "4", "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, noOpExitCode);
            Assert.Equal(0, noOpPrepassCount);
            Assert.Equal(0, noOpContentLoadCount);
            Assert.Empty(artifactEvents);
            Assert.Equal(0L, CountCSharpEvidenceWrites(dbPath));

            using var conn = OpenNonPoolingConnection(dbPath);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM symbol_references r
                JOIN files f ON f.id = r.file_id
                JOIN reference_lines rl ON rl.id = r.reference_line_id
                WHERE f.path = 'Money000.cs'
                  AND r.symbol_name = 'Parse'
                  AND r.reference_kind = 'implicit_implementation'
                  AND rl.context = 'public static Money000 Parse(string s) => new();'";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(1, count);
            Assert.Equal(1, matchingLookupBuilds);

            static void InstallCSharpEvidenceWriteAudit(string path)
            {
                using var connection = OpenNonPoolingConnection(path);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE csharp_evidence_write_audit(operation TEXT NOT NULL);
                    CREATE TRIGGER csharp_evidence_write_audit_insert
                    AFTER INSERT ON codeindex_meta
                    WHEN NEW.key = '{DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey}'
                    BEGIN
                        INSERT INTO csharp_evidence_write_audit(operation) VALUES ('insert');
                    END;
                    CREATE TRIGGER csharp_evidence_write_audit_update
                    AFTER UPDATE ON codeindex_meta
                    WHEN NEW.key = '{DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey}'
                    BEGIN
                        INSERT INTO csharp_evidence_write_audit(operation) VALUES ('update');
                    END;
                    CREATE TRIGGER csharp_evidence_write_audit_delete
                    AFTER DELETE ON codeindex_meta
                    WHEN OLD.key = '{DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey}'
                    BEGIN
                        INSERT INTO csharp_evidence_write_audit(operation) VALUES ('delete');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            static long CountCSharpEvidenceWrites(string path)
            {
                using var connection = OpenNonPoolingConnection(path);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM csharp_evidence_write_audit";
                return (long)command.ExecuteScalar()!;
            }
        }
        finally
        {
            ReferenceExtractor.CSharpStaticInterfaceMemberLookupsBuiltForTesting = previousLookupHook;
            IndexCommandRunner.FullScanCSharpPrepassForTesting = previousPrepassHook;
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                previousArtifactHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FreshFullScan_CSharpPrepassArtifactChecksumMismatchFallsBackToAuthoritativeMainRead()
    {
        var projectRoot = CreateTempProject();
        var sourcePath = Path.Combine(projectRoot, "Fixture.cs");
        const string originalSource =
            "public class Fixture { public static int Alpha() => 1; }\n";
        const string mutatedSource =
            "public class Fixture { public static int Bravo() => 1; }\n";
        Assert.Equal(
            Encoding.UTF8.GetByteCount(originalSource),
            Encoding.UTF8.GetByteCount(mutatedSource));
        File.WriteAllText(sourcePath, originalSource);
        var originalModified = File.GetLastWriteTimeUtc(sourcePath);
        var previousContentLoadHook =
            IndexCommandRunner.FullScanFileContentLoadForTesting;
        var previousArtifactHook =
            CSharpPrepassSymbolArtifactCache.EventForTesting;
        var artifactEvents =
            new ConcurrentQueue<CSharpPrepassSymbolArtifactCacheEvent>();
        var mutated = 0;
        try
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                artifactEvents.Enqueue;
            IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
            {
                if (path != "Fixture.cs"
                    || Interlocked.Exchange(ref mutated, 1) != 0)
                {
                    return;
                }

                File.WriteAllText(sourcePath, mutatedSource);
                File.SetLastWriteTimeUtc(sourcePath, originalModified);
            };

            var exitCode = IndexCommandRunner.Run(
                [projectRoot, "--parallelism", "2", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, Volatile.Read(ref mutated));
            Assert.Contains(
                artifactEvents,
                item => item.Phase == "admitted"
                        && item.Path == "Fixture.cs");
            Assert.Contains(
                artifactEvents,
                item => item.Phase == "checksum_mismatch"
                        && item.Path == "Fixture.cs");
            Assert.DoesNotContain(
                artifactEvents,
                item => item.Phase == "taken"
                        && item.Path == "Fixture.cs");

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var connection = OpenNonPoolingConnection(dbPath);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM symbols
                WHERE file_id = (SELECT id FROM files WHERE path = 'Fixture.cs')
                """;
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            Assert.Contains("Bravo", names);
            Assert.DoesNotContain("Alpha", names);
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting =
                previousContentLoadHook;
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                previousArtifactHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("rebuild")]
    [InlineData("symbols-only")]
    [InlineData("stall-seam")]
    public void Run_CSharpPrepassArtifactReuse_IsDisabledOutsideFreshFullIndex(
        string mode)
    {
        var projectRoot = CreateTempProject();
        var sourcePath = Path.Combine(projectRoot, "Fixture.cs");
        File.WriteAllText(
            sourcePath,
            "public class Fixture { public static int Run() => 1; }\n");
        var previousArtifactHook =
            CSharpPrepassSymbolArtifactCache.EventForTesting;
        var previousStallTimeout =
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        var artifactEvents =
            new ConcurrentQueue<CSharpPrepassSymbolArtifactCacheEvent>();
        try
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                artifactEvents.Enqueue;
            if (mode == "stall-seam")
            {
                IndexCommandRunner.IndexExtractionStallTimeoutForTesting =
                    () => TimeSpan.FromMinutes(1);
            }
            var args = new List<string>
            {
                projectRoot,
                "--json",
                "--quiet",
            };
            if (mode == "rebuild")
            {
                args.Add("--rebuild");
                args.Add("--yes");
            }
            else if (mode == "symbols-only")
                args.Add("--symbols-only");

            var exitCode = IndexCommandRunner.Run(args.ToArray(), _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain(
                artifactEvents,
                item => item.Phase is "admitted" or "taken");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var connection = OpenNonPoolingConnection(dbPath);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM symbols
                WHERE name = 'Fixture'
                """;
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally
        {
            CSharpPrepassSymbolArtifactCache.EventForTesting =
                previousArtifactHook;
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting =
                previousStallTimeout;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_ParallelSymbolCapLeavesLateCsharpStaticInterfaceSourceEvidenceUnknown()
    {
        var projectRoot = CreateTempProject();
        var sourcePath = Path.Combine(projectRoot, "IParseable.cs");
        var sourceRewritten = false;
        var parallelized = false;
        try
        {
            File.WriteAllText(sourcePath, "public interface IParseable<T> { }\n");

            lock (FullScanContentLoadHookGate)
            {
                try
                {
                    IndexCommandRunner.FullScanExtractionSchedulingForTesting =
                        (enabled, _) => parallelized = enabled;
                    IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
                    {
                        if (sourceRewritten
                            || !string.Equals(path, "IParseable.cs", StringComparison.Ordinal))
                        {
                            return;
                        }

                        File.WriteAllText(
                            sourcePath,
                            """
                            public interface IParseable<T>
                            {
                                static abstract T Parse(string s);
                            }
                            public sealed class ExtraSymbol
                            {
                                public void First() { }
                                public void Second() { }
                            }
                            """);
                        sourceRewritten = true;
                    };

                    var (exitCode, json) = RunAndCaptureJson(
                        [projectRoot, "--parallelism", "4", "--max-symbols-per-file", "1", "--json", "--quiet"]);

                    Assert.Equal(CommandExitCodes.PartialResult, exitCode);
                    Assert.Equal("partial", json.GetProperty("status").GetString());
                    Assert.False(json.GetProperty("index_complete").GetBoolean());
                    Assert.False(json.GetProperty("graph_data_current").GetBoolean());
                    Assert.Contains(
                        json.GetProperty("file_errors").EnumerateArray(),
                        error => error.GetProperty("phase").GetString() == "csharp_workspace_validation");
                }
                finally
                {
                    IndexCommandRunner.FullScanFileContentLoadForTesting = null;
                    IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
                }
            }

            Assert.True(sourceRewritten);
            Assert.True(parallelized);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var writer = new DbWriter(db);
            Assert.Null(writer.GetCSharpStaticInterfaceSourceEvidence());

            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson(
                [projectRoot, "--parallelism", "4", "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            Assert.True(recoveryJson.GetProperty("index_complete").GetBoolean());
            Assert.True(recoveryJson.GetProperty("graph_data_current").GetBoolean());
            Assert.Equal(true, writer.GetCSharpStaticInterfaceSourceEvidence());
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = null;
            IndexCommandRunner.FullScanExtractionSchedulingForTesting = null;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Run_FullScan_PostPrepassCsharpContractLeavesReadinessPartialUntilCleanRetry(
        bool rebuildExisting,
        bool incrementalExisting)
    {
        var projectRoot = CreateTempProject();
        var csharpRoot = Path.Combine(projectRoot, "csharp");
        var interfacePath = Path.Combine(csharpRoot, "IParseable.cs");
        var moneyPath = Path.Combine(csharpRoot, "Money.cs");
        var ftsSourcePath = Path.Combine(projectRoot, "00_FtsBeforeDrift.py");
        const string moneySource =
            "public readonly struct Money : IParseable<Money>\n"
            + "{\n"
            + "    public static Money Parse(string s) => new();\n"
            + "}\n";
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        var previousOptimizeHook = IndexCommandRunner.FullScanFtsOptimizeForTesting;
        var interfaceRewritten = false;
        var optimizeCount = 0;
        try
        {
            Directory.CreateDirectory(csharpRoot);
            if (incrementalExisting)
            {
                File.WriteAllText(ftsSourcePath, "# fullscanftsbaseline\n");
                File.WriteAllText(
                    interfacePath,
                    "public interface IParseable<T> { } // baseline\n");
                File.WriteAllText(moneyPath, moneySource);
                Assert.Equal(
                    CommandExitCodes.Success,
                    IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            }
            else if (rebuildExisting)
            {
                File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('ready')\n");
                Assert.Equal(
                    CommandExitCodes.Success,
                    IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            }

            File.WriteAllText(ftsSourcePath, "# fullscanftsdrifttoken\n");
            File.WriteAllText(interfacePath, "public interface IParseable<T> { }\n");
            File.WriteAllText(
                moneyPath,
                incrementalExisting
                    ? moneySource + "// dirty before extraction\n"
                    : moneySource);
            if (incrementalExisting)
            {
                var changedAt = DateTime.UtcNow.AddSeconds(2);
                File.SetLastWriteTimeUtc(ftsSourcePath, changedAt);
                File.SetLastWriteTimeUtc(interfacePath, changedAt);
                File.SetLastWriteTimeUtc(moneyPath, changedAt);
            }
            IndexCommandRunner.FullScanFtsOptimizeForTesting = () =>
            {
                optimizeCount++;
                previousOptimizeHook?.Invoke();
            };
            IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
            {
                if (interfaceRewritten || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return;

                File.WriteAllText(
                    interfacePath,
                    "public interface IParseable<T> { static abstract T Parse(string s); }\n");
                if (path != "IParseable.cs")
                    File.WriteAllText(moneyPath, moneySource + "// changed after workspace preflight\n");
                interfaceRewritten = true;
            };
            var arguments = rebuildExisting
                ? new[] { projectRoot, "--rebuild", "--yes", "--parallelism", "1", "--json", "--quiet" }
                : new[] { projectRoot, "--parallelism", "1", "--json", "--quiet" };

            var (partialExitCode, partialJson) = RunAndCaptureJson(arguments);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.False(partialJson.GetProperty("csharp_symbol_name_ready").GetBoolean());
            Assert.False(partialJson.GetProperty("csharp_metadata_target_ready").GetBoolean());
            Assert.Contains(
                partialJson.GetProperty("file_errors").EnumerateArray(),
                error => error.GetProperty("phase").GetString() == "csharp_workspace_validation");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            (long chunks, long fts, long trigram) ftsCounts;
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT
                        (SELECT COUNT(*) FROM chunks WHERE content LIKE '%fullscanftsdrifttoken%'),
                        (SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'fullscanftsdrifttoken'),
                        (SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'fullscanftsdrifttoken')
                    """;
                using var reader = command.ExecuteReader();
                Assert.True(reader.Read());
                ftsCounts = (
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2));
            }
            Assert.Equal(1L, ftsCounts.chunks);
            Assert.Equal(
                (fts: 1L, trigram: 1L, optimize: 1),
                (fts: ftsCounts.fts, trigram: ftsCounts.trigram, optimize: optimizeCount));

            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson(arguments);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            Assert.True(recoveryJson.GetProperty("csharp_symbol_name_ready").GetBoolean());
            Assert.True(recoveryJson.GetProperty("csharp_metadata_target_ready").GetBoolean());
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            IndexCommandRunner.FullScanFtsOptimizeForTesting = previousOptimizeHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_InitialSqlReadFailureKeepsSqlReadinessPartialUntilCleanRetry()
    {
        var projectRoot = CreateTempProject();
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "schema.sql"),
                "CREATE TABLE dbo.Widget (Id int PRIMARY KEY);\n");
            IndexCommandRunner.FullScanFileContentLoadForTesting = _ =>
                throw new IOException("simulated initial SQL read failure");

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--parallelism", "1", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.Equal(1, partialJson.GetProperty("file_errors").GetArrayLength());
            Assert.False(partialJson.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Equal(
                DegradationReasonCodes.BuildSqlGraphContractDegradedReason(),
                partialJson.GetProperty("sql_graph_contract_degraded_reason").GetString());

            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson(
                [projectRoot, "--parallelism", "1", "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            Assert.True(recoveryJson.GetProperty("sql_graph_contract_ready").GetBoolean());
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_RetargetedExternalFileLinkFailsWorkspaceValidation_Issue4829()
    {
        var projectRoot = CreateTempProject();
        var outsideRoot = CreateTempProject();
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        var retargeted = false;
        try
        {
            var firstTarget = Path.Combine(outsideRoot, "First.cs");
            var secondTarget = Path.Combine(outsideRoot, "Second.cs");
            var linkPath = Path.Combine(projectRoot, "ExternalLink.cs");
            const string source = "public class External4829 { }\n";
            File.WriteAllText(firstTarget, source);
            File.WriteAllText(secondTarget, source);
            var sharedModifiedUtc = DateTime.UtcNow.AddMinutes(-5);
            File.SetLastWriteTimeUtc(firstTarget, sharedModifiedUtc);
            File.SetLastWriteTimeUtc(secondTarget, sharedModifiedUtc);
            try
            {
                File.CreateSymbolicLink(linkPath, firstTarget);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
            {
                if (retargeted || path != "ExternalLink.cs")
                    return;

                File.Delete(linkPath);
                File.CreateSymbolicLink(linkPath, secondTarget);
                retargeted = true;
            };

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--follow-symlinks",
                "all",
                "--parallelism",
                "1",
                "--json",
                "--quiet",
            ]);

            Assert.True(retargeted);
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Contains(
                json.GetProperty("file_errors").EnumerateArray(),
                error => error.GetProperty("file").GetString() == "ExternalLink.cs"
                    && error.GetProperty("phase").GetString() == "csharp_workspace_validation");
            Assert.DoesNotContain(
                "ExternalLink.cs",
                ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            DeleteDirectory(outsideRoot);
        }
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("sql")]
    public void Run_FullScan_RebuildFailureRetainsPersistedReadinessForReclassifiedLanguage(
        string initialLanguage)
    {
        var projectRoot = CreateTempProject();
        var configPath = Path.Combine(projectRoot, LanguageMapOverrides.WorkspaceFileName);
        var sourcePath = Path.Combine(projectRoot, "sample.custom");
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
        try
        {
            File.WriteAllText(
                configPath,
                $"entries:\n- extension: .custom\n  language: {initialLanguage}\n");
            File.WriteAllText(
                sourcePath,
                initialLanguage == "csharp"
                    ? "public interface IContract { }\n"
                    : "CREATE TABLE dbo.Widget (Id int PRIMARY KEY);\n");

            var (initialExitCode, initialJson) = RunAndCaptureJson(
                [projectRoot, "--parallelism", "1", "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(initialLanguage == "csharp"
                ? initialJson.GetProperty("csharp_symbol_name_ready").GetBoolean()
                : initialJson.GetProperty("sql_graph_contract_ready").GetBoolean());

            File.WriteAllText(
                configPath,
                "entries:\n- extension: .custom\n  language: python\n");
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
            {
                if (path == "sample.custom")
                    throw new IOException("simulated rebuild read failure after language reclassification");
            };

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--rebuild", "--yes", "--parallelism", "1", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            if (initialLanguage == "csharp")
            {
                Assert.False(partialJson.GetProperty("csharp_symbol_name_ready").GetBoolean());
                Assert.False(partialJson.GetProperty("csharp_metadata_target_ready").GetBoolean());
            }
            else
            {
                Assert.False(partialJson.GetProperty("sql_graph_contract_ready").GetBoolean());
                Assert.Equal(
                    DegradationReasonCodes.BuildSqlGraphContractDegradedReason(),
                    partialJson.GetProperty("sql_graph_contract_degraded_reason").GetString());
            }

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.True(new DbWriter(db).HasAnyFilesWithLanguage(initialLanguage));
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_PartialDiscoveryRetainsKnownLanguageReadinessFailures()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var unreadableDirectory = Path.Combine(projectRoot, "unreadable");
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "schema.sql"),
                "CREATE TABLE dbo.Widget (Id int PRIMARY KEY);\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "Contract.cs"),
                "public interface IContract { }\n");
            Directory.CreateDirectory(unreadableDirectory);
            File.WriteAllText(Path.Combine(unreadableDirectory, "blocked.py"), "print('blocked')\n");
            originalMode = File.GetUnixFileMode(unreadableDirectory);
            File.SetUnixFileMode(unreadableDirectory, UnixFileMode.None);
            IndexCommandRunner.FullScanFileContentLoadForTesting = path =>
            {
                if (path == "schema.sql")
                    throw new IOException("simulated SQL read failure after partial discovery");
            };

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--parallelism", "1", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.True(json.GetProperty("summary").GetProperty("errors").GetInt32() >= 2);
            Assert.False(
                json.GetProperty("sql_graph_contract_ready").GetBoolean(),
                json.GetRawText());
            Assert.Equal(
                DegradationReasonCodes.BuildSqlGraphContractDegradedReason(),
                json.GetProperty("sql_graph_contract_degraded_reason").GetString());
            Assert.False(json.GetProperty("csharp_symbol_name_ready").GetBoolean());
            Assert.False(json.GetProperty("csharp_metadata_target_ready").GetBoolean());
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            if (originalMode.HasValue && Directory.Exists(unreadableDirectory))
                File.SetUnixFileMode(unreadableDirectory, originalMode.Value);
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_PositiveCsharpNoOpLateRemovalRefreshesEveryCsharpReference()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var stalePath = Path.Combine(projectRoot, "obsolete.py");
        var loadedPaths = new ConcurrentBag<string>();
        var previousRevalidationHook = IndexCommandRunner.FullScanCSharpFinalStatRevalidationForTesting;
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            File.WriteAllText(stalePath, "print('obsolete')\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            File.Delete(stalePath);

            IndexCommandRunner.FullScanCSharpFinalStatRevalidationForTesting = () =>
            {
                WriteParseableInterface(interfacePath, hasStaticContract: false);
                File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(2));
            };
            IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

            var exitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(["IParseable.cs", "Money.cs"], loadedPaths.Order(StringComparer.Ordinal));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(false, new DbWriter(db).GetCSharpStaticInterfaceSourceEvidence());
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpFinalStatRevalidationForTesting = previousRevalidationHook;
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_KnownNegativeNoOpSkipsWorkspaceUntilCompletenessIsDegraded()
    {
        var projectRoot = CreateTempProject();
        var previousPrepassHook = IndexCommandRunner.FullScanCSharpPrepassForTesting;
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        var prepassCalls = 0;
        var loadedPaths = new List<string>();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "IPlain.cs"),
                "public interface IPlain { void Run(); }\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            using (var db = new DbContext(
                       DbOpenIntent.WriteIndex,
                       Path.Combine(projectRoot, ".cdidx", "codeindex.db")))
            {
                Assert.Equal(false, new DbWriter(db).GetCSharpStaticInterfaceSourceEvidence());
            }

            IndexCommandRunner.FullScanCSharpPrepassForTesting = () => prepassCalls++;
            IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(0, prepassCalls);
            Assert.Empty(loadedPaths);

            using (var db = new DbContext(
                       DbOpenIntent.WriteIndex,
                       Path.Combine(projectRoot, ".cdidx", "codeindex.db")))
            {
                new DbWriter(db).MarkIndexIncomplete(["test_degraded"]);
            }

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, prepassCalls);
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpPrepassForTesting = previousPrepassHook;
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_ReadinessBoundaryNewCsharpFileLeavesPartialUntilCleanRetry()
    {
        var projectRoot = CreateTempProject();
        var previousValidationHook = IndexCommandRunner.FullScanCSharpReadinessValidationForTesting;
        var previousAugmentationHook = IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting;
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var addedPath = Path.Combine(projectRoot, "INewContract.cs");
        var deletedTypeScriptPath = Path.Combine(projectRoot, "first.ts");
        var augmentationRebuildCount = 0;
        var refreshCount = 0;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Plain.cs"), "public sealed class Plain { }\n");
            File.WriteAllText(
                deletedTypeScriptPath,
                "interface SharedBoundary { first: number }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "second.ts"),
                "interface SharedBoundary { second: number }\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            File.Delete(deletedTypeScriptPath);
            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = () =>
            {
                File.WriteAllText(
                    addedPath,
                    "public interface INewContract<T> { static abstract T Create(); }\n");
                File.SetLastWriteTimeUtc(addedPath, DateTime.UtcNow.AddSeconds(2));
            };
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = () =>
            {
                augmentationRebuildCount++;
                previousAugmentationHook?.Invoke();
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.Contains(
                partialJson.GetProperty("file_errors").EnumerateArray(),
                error => error.GetProperty("phase").GetString() == "csharp_workspace_validation");
            Assert.Equal(0, augmentationRebuildCount);
            Assert.Equal(1, refreshCount);
            using (var partialDb = new DbContext(
                       DbOpenIntent.WriteIndex,
                       Path.Combine(projectRoot, ".cdidx", "codeindex.db")))
            {
                Assert.Null(new DbWriter(partialDb).GetCSharpStaticInterfaceSourceEvidence());
                Assert.Null(partialDb.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
            }

            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = previousValidationHook;
            augmentationRebuildCount = 0;
            refreshCount = 0;
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            Assert.Equal(1, augmentationRebuildCount);
            Assert.Equal(1, refreshCount);
            using var recoveryDb = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(true, new DbWriter(recoveryDb).GetCSharpStaticInterfaceSourceEvidence());
            Assert.Equal(
                DbContext.TypeScriptAugmentationVersion.ToString(CultureInfo.InvariantCulture),
                recoveryDb.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = previousValidationHook;
            IndexCommandRunner.FullScanTypeScriptAugmentationRebuildForTesting = previousAugmentationHook;
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Run_FullScan_ValidatesScanInputExactlyBeforeWriteAndReadiness(
        bool includeCSharp,
        bool symbolsOnly)
    {
        var projectRoot = CreateTempProject();
        var previousBarrierHook = IndexCommandRunner.FullScanInputSnapshotBarrierForTesting;
        var previousWritePhaseHook = IndexCommandRunner.FullScanWritePhaseStartedForTesting;
        var phases = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "def run():\n    return 1\n");
            if (includeCSharp)
                File.WriteAllText(Path.Combine(projectRoot, "Plain.cs"), "public sealed class Plain { }\n");
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = phases.Add;
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () =>
                phases.Add("write_started");

            var args = symbolsOnly
                ? new[] { projectRoot, "--symbols-only", "--json", "--quiet" }
                : [projectRoot, "--json", "--quiet"];
            var exitCode = IndexCommandRunner.Run(args, _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(
                ["before_write", "write_started", "before_readiness"],
                phases);
        }
        finally
        {
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = previousBarrierHook;
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = previousWritePhaseHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FreshFullScan_ExternalPreTransactionWriteFallsBackToFullResolution()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousBarrierHook = IndexCommandRunner.FullScanInputSnapshotBarrierForTesting;
        var injected = 0;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "def run():\n    return 1\n");
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = phase =>
            {
                previousBarrierHook?.Invoke(phase);
                if (!string.Equals(phase, "before_write", StringComparison.Ordinal)
                    || Interlocked.Exchange(ref injected, 1) != 0)
                {
                    return;
                }

                using var externalDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                externalDb.InitializeSchema();
                var externalWriter = new DbWriter(externalDb.Connection);
                var fileId = externalWriter.UpsertFile(new FileRecord
                {
                    Path = "external/concurrent.py",
                    Lang = "python",
                    Size = 20,
                    Lines = 1,
                    Checksum = "external-concurrent",
                    Modified = new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc),
                });
                externalWriter.InsertReferences(
                    [
                        new ReferenceRecord
                        {
                            FileId = fileId,
                            SymbolName = "NoCandidate",
                            ReferenceKind = "call",
                            Line = 1,
                            Column = 1,
                            Context = "NoCandidate()",
                            ContainerKind = "function",
                            ContainerName = "external",
                            IsSelfReference = true,
                            IsMutualRecursion = true,
                        },
                    ],
                    refreshMutualRecursionFlags: false);
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, injected);
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT r.resolution_state,
                       r.resolution_candidate_count,
                       r.target_symbol_id,
                       r.target_symbol_key,
                       r.is_self_reference,
                       r.is_mutual_recursion
                FROM symbol_references AS r
                JOIN files AS f ON f.id = r.file_id
                WHERE f.path = 'external/concurrent.py'
                  AND r.symbol_name = 'NoCandidate'
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("unresolved", reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.Equal(0, reader.GetInt32(4));
            Assert.Equal(0, reader.GetInt32(5));
            Assert.False(reader.Read());
        }
        finally
        {
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FreshFullScan_PersistsNestedReferenceSourcesAcrossLanguages()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                """
                public sealed class Caller
                {
                    public void Outer()
                    {
                        Target();
                        void Inner()
                        {
                            Target();
                        }
                    }
                    private static void Target() { }
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "caller.py"),
                """
                def outer():
                    python_target()
                    def inner():
                        python_target()
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText =
                """
                SELECT source_file.path,
                       reference.line,
                       source_symbol.name
                FROM symbol_references AS reference
                JOIN files AS source_file ON source_file.id = reference.file_id
                LEFT JOIN symbols AS source_symbol ON source_symbol.id = reference.source_symbol_id
                WHERE (source_file.path = 'Caller.cs'
                       AND reference.symbol_name = 'Target'
                       AND reference.reference_kind = 'call')
                   OR (source_file.path = 'caller.py'
                       AND reference.symbol_name = 'python_target'
                       AND reference.reference_kind = 'call')
                ORDER BY source_file.path COLLATE BINARY,
                         reference.line
                """;
            using var reader = command.ExecuteReader();
            var sources = new List<(string Path, long Line, string? Source)>();
            while (reader.Read())
            {
                sources.Add((
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            Assert.Equal(
                [
                    ("Caller.cs", 5L, "Outer"),
                    ("Caller.cs", 8L, "Inner"),
                    ("caller.py", 2L, "outer"),
                    ("caller.py", 4L, "inner"),
                ],
                sources);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_Rebuild_CSharpSwitchExpressionReturnedLambdasRemainInGraph_Issue5085()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Routes.cs"),
                """
                using System;
                using System.Threading.Tasks;

                public enum ResultKind { None }
                public readonly record struct Point(int X, int Y);

                public static class Routes
                {
                    public static Func<int, int> Resolve(string key) =>
                        key switch
                        {
                            "expression" => value => Targets.Expression(value),
                            "block" => value =>
                            {
                                return Targets.Block(value);
                            },
                            "parenthesized" => (value) => Targets.Parenthesized(value),
                            "unqualified" => value => Unqualified(value),
                            "multiline" => value => Targets.Multiline(
                                value),
                            "nested" => value => value switch
                            {
                                > 0 => Targets.NestedPositive(value),
                                _ => Targets.NestedOther(value),
                            },
                            _ => value => value,
                        };

                    public static Func<int, Task<int>> ResolveAsync(string key) =>
                        key switch
                        {
                            "async" => async value => await Targets.Async(value),
                            _ => async value => value,
                        };

                    public static object ResolveValue(string key) =>
                        key switch
                        {
                            "plain" => Targets.Value,
                            _ => ResultKind.None,
                        };

                    public static int Match(Point point) => point switch
                    {
                        Point(0, 0) => 0,
                        Point(var x, var y) when x > 0
                            => x + y,
                        Point(var x, var y) => x + y,
                        Point(Point(var nested, _), var y)
                            => nested + y,
                        Point(var withValue, _) with
                            => withValue,
                    };

                    private static int Unqualified(int value) => value;
                }

                public static class Targets
                {
                    public static int Value { get; } = 1;
                    public static int Expression(int value) => value;
                    public static int Block(int value) => value;
                    public static int Parenthesized(int value) => value;
                    public static int Multiline(int value) => value;
                    public static int NestedPositive(int value) => value;
                    public static int NestedOther(int value) => value;
                    public static Task<int> Async(int value) => Task.FromResult(value);
                }
                """);

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--rebuild", "--yes", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("index_complete").GetBoolean());
            Assert.True(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.True(json.GetProperty("graph_data_current").GetBoolean());

            using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT reference.symbol_name,
                           reference.line,
                           reference.column_number,
                           reference.container_name,
                           COALESCE(reference.target_qualifier, '')
                    FROM symbol_references AS reference
                    JOIN files AS source_file ON source_file.id = reference.file_id
                    WHERE source_file.path = 'Routes.cs'
                      AND reference.reference_kind = 'call'
                      AND reference.container_name IN ('Resolve', 'ResolveAsync', 'ResolveValue', 'Match')
                    ORDER BY reference.line,
                             reference.column_number
                    """;
                using var rowReader = command.ExecuteReader();
                var calls = new List<(string Name, long Line, long Column, string Container, string Qualifier)>();
                while (rowReader.Read())
                {
                    calls.Add((
                        rowReader.GetString(0),
                        rowReader.GetInt64(1),
                        rowReader.GetInt64(2),
                        rowReader.GetString(3),
                        rowReader.GetString(4)));
                }

                Assert.Equal(
                    [
                        ("Expression", 12L, 46L, "Resolve", "Targets"),
                        ("Block", 15L, 32L, "Resolve", "Targets"),
                        ("Parenthesized", 17L, 51L, "Resolve", "Targets"),
                        ("Unqualified", 18L, 39L, "Resolve", ""),
                        ("Multiline", 19L, 45L, "Resolve", "Targets"),
                        ("NestedPositive", 23L, 32L, "Resolve", "Targets"),
                        ("NestedOther", 24L, 30L, "Resolve", "Targets"),
                        ("Async", 32L, 53L, "ResolveAsync", "Targets"),
                    ],
                    calls);
            }

            using var graphReader = new DbReader(db.Connection);
            var resolveCallees = graphReader.GetCallees(
                "Resolve",
                limit: 50,
                lang: "csharp",
                exact: true,
                includeQualifiedCommonCalls: true);
            Assert.Equal(
                [
                    "Block",
                    "Expression",
                    "Multiline",
                    "NestedOther",
                    "NestedPositive",
                    "Parenthesized",
                    "Unqualified",
                ],
                resolveCallees.Select(result => result.CalleeName).Order(StringComparer.Ordinal));

            foreach (var calleeName in resolveCallees.Select(result => result.CalleeName))
            {
                var callers = graphReader.GetCallers(
                    calleeName,
                    limit: 20,
                    lang: "csharp",
                    exact: true,
                    includeQualifiedCommonCalls: true);
                Assert.Contains(callers, result => result.CallerName == "Resolve");
            }

            var expressionCaller = Assert.Single(graphReader.GetCallers(
                "Expression",
                limit: 20,
                lang: "csharp",
                exact: true,
                includeQualifiedCommonCalls: true));
            Assert.Equal("Resolve", expressionCaller.CallerName);
            Assert.Equal(12, expressionCaller.FirstLine);
            Assert.Equal(46, expressionCaller.FirstColumn);

            var asyncCaller = Assert.Single(graphReader.GetCallers(
                "Async",
                limit: 20,
                lang: "csharp",
                exact: true,
                includeQualifiedCommonCalls: true));
            Assert.Equal("ResolveAsync", asyncCaller.CallerName);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_FreshSnapshotAbortRetainsDiscoveredLanguageFailuresWithoutRows()
    {
        var projectRoot = CreateTempProject();
        var previousBarrierHook = IndexCommandRunner.FullScanInputSnapshotBarrierForTesting;
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var phases = new List<string>();
        try
        {
            File.WriteAllText(ignorePath, "# snapshot-a\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "Contract.cs"),
                "public interface IContract { }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "schema.sql"),
                "CREATE TABLE dbo.Widget (Id int PRIMARY KEY);\n");
            var ignoreModifiedUtc = File.GetLastWriteTimeUtc(ignorePath);
            var rootModifiedUtc = Directory.GetLastWriteTimeUtc(projectRoot);
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = phase =>
            {
                phases.Add(phase);
                if (phase != "before_write")
                    return;
                File.WriteAllText(ignorePath, "# snapshot-b\n");
                File.SetLastWriteTimeUtc(ignorePath, ignoreModifiedUtc);
                Directory.SetLastWriteTimeUtc(projectRoot, rootModifiedUtc);
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(["before_write"], phases);
            Assert.False(json.GetProperty("sql_graph_contract_ready").GetBoolean());
            Assert.Equal(
                DegradationReasonCodes.BuildSqlGraphContractDegradedReason(),
                json.GetProperty("sql_graph_contract_degraded_reason").GetString());
            Assert.False(json.GetProperty("csharp_symbol_name_ready").GetBoolean());
            Assert.False(json.GetProperty("csharp_metadata_target_ready").GetBoolean());
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            var reader = new DbReader(db.Connection);
            Assert.Null(reader.GetFileByPath("Contract.cs"));
            Assert.Null(reader.GetFileByPath("schema.sql"));
        }
        finally
        {
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_FullScan_FirstSnapshotBarrierDriftPreservesRowsAndTrustAfterSchemaInitialization(
        bool rebuild)
    {
        var projectRoot = CreateTempProject();
        var previousBarrierHook = IndexCommandRunner.FullScanInputSnapshotBarrierForTesting;
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var appPath = Path.Combine(projectRoot, "app.py");
        var obsoletePath = Path.Combine(projectRoot, "obsolete.md");
        var phases = new List<string>();
        try
        {
            File.WriteAllText(ignorePath, "never-match-a\n");
            File.WriteAllText(appPath, "def run():\n    return 1\n");
            File.WriteAllText(obsoletePath, "# Obsolete\n");
            File.WriteAllText(Path.Combine(projectRoot, "Plain.cs"), "public sealed class Plain { }\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            int priorReadiness;
            string? priorIndexComplete;
            string? priorAppChecksum;
            bool? priorSourceEvidence;
            string? priorFtsRecoveryMarker;
            string? priorBatchMarker;
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                priorReadiness = db.GetUserVersion();
                priorIndexComplete = db.GetMetaString(DbContext.IndexCompletenessMetaKey);
                priorAppChecksum = new DbReader(db.Connection).GetFileByPath("app.py")?.Checksum;
                var priorWriter = new DbWriter(db);
                priorSourceEvidence = priorWriter.GetCSharpStaticInterfaceSourceEvidence();
                priorWriter.MarkFtsBulkLoadRecoveryNeeded();
                priorWriter.MarkBatchInProgress();
                priorFtsRecoveryMarker = db.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey);
                priorBatchMarker = db.GetMetaString(DbContext.BatchInProgressMetaKey);
            }

            File.WriteAllText(appPath, "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(appPath, DateTime.UtcNow.AddSeconds(3));
            File.Delete(obsoletePath);
            var ignoreModifiedUtc = File.GetLastWriteTimeUtc(ignorePath);
            var rootModifiedUtc = Directory.GetLastWriteTimeUtc(projectRoot);
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = phase =>
            {
                phases.Add(phase);
                if (phase != "before_write")
                    return;
                File.WriteAllText(ignorePath, "never-match-b\n");
                File.SetLastWriteTimeUtc(ignorePath, ignoreModifiedUtc);
                Directory.SetLastWriteTimeUtc(projectRoot, rootModifiedUtc);
            };

            var args = rebuild
                ? new[] { projectRoot, "--rebuild", "--yes", "--json", "--quiet" }
                : [projectRoot, "--json", "--quiet"];
            var (exitCode, json) = RunAndCaptureJson(args);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(["before_write"], phases);
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Contains(
                json.GetProperty("file_errors").EnumerateArray(),
                error => error.GetProperty("phase").GetString() == "csharp_workspace_validation");
            using var preservedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(priorReadiness, preservedDb.GetUserVersion());
            Assert.Equal(priorIndexComplete, preservedDb.GetMetaString(DbContext.IndexCompletenessMetaKey));
            Assert.Equal(
                priorFtsRecoveryMarker,
                preservedDb.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.Equal(
                priorBatchMarker,
                preservedDb.GetMetaString(DbContext.BatchInProgressMetaKey));
            Assert.Equal(priorAppChecksum, new DbReader(preservedDb.Connection).GetFileByPath("app.py")?.Checksum);
            Assert.NotNull(new DbReader(preservedDb.Connection).GetFileByPath("obsolete.md"));
            Assert.Equal(
                priorSourceEvidence,
                new DbWriter(preservedDb).GetCSharpStaticInterfaceSourceEvidence());
        }
        finally
        {
            IndexCommandRunner.FullScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_ReadinessBoundaryInPlaceGitignoreChangeLeavesPartialUntilCleanRetry()
    {
        var projectRoot = CreateTempProject();
        var previousValidationHook = IndexCommandRunner.FullScanCSharpReadinessValidationForTesting;
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Plain.cs"), "public sealed class Plain { }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "IHidden.cs"),
                "public interface IHidden<T> { static abstract T Create(); }\n");
            File.WriteAllText(ignorePath, "IHidden.cs\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var rootModifiedUtc = Directory.GetLastWriteTimeUtc(projectRoot);
            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = () =>
            {
                File.WriteAllText(ignorePath, "XHidden.cs\n");
                Directory.SetLastWriteTimeUtc(projectRoot, rootModifiedUtc);
            };

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);

            Assert.Equal(rootModifiedUtc, Directory.GetLastWriteTimeUtc(projectRoot));
            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.False(partialJson.GetProperty("index_complete").GetBoolean());
            Assert.False(partialJson.GetProperty("graph_data_current").GetBoolean());
            Assert.Contains(
                partialJson.GetProperty("file_errors").EnumerateArray(),
                error => error.GetProperty("phase").GetString() == "csharp_workspace_validation");
            using (var partialDb = new DbContext(
                       DbOpenIntent.WriteIndex,
                       Path.Combine(projectRoot, ".cdidx", "codeindex.db")))
            {
                Assert.Null(new DbWriter(partialDb).GetCSharpStaticInterfaceSourceEvidence());
            }

            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = previousValidationHook;
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            using var recoveryDb = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(true, new DbWriter(recoveryDb).GetCSharpStaticInterfaceSourceEvidence());
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = previousValidationHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_ReadinessBoundaryIgnoredDirectoryChurnRemainsClean()
    {
        var projectRoot = CreateTempProject();
        var previousValidationHook = IndexCommandRunner.FullScanCSharpReadinessValidationForTesting;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Plain.cs"), "public sealed class Plain { }\n");
            var ignoredDirectory = Path.Combine(projectRoot, "node_modules", "pkg");
            Directory.CreateDirectory(ignoredDirectory);
            File.WriteAllText(Path.Combine(ignoredDirectory, "existing.js"), "module.exports = 1;\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = () =>
                File.WriteAllText(
                    Path.Combine(ignoredDirectory, "generated.cs"),
                    "public interface IIgnored<T> { static abstract T Create(); }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpReadinessValidationForTesting = previousValidationHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_RawCsharpPrepassMemberRemovalPreservesRowsUntilCleanRetry()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var implementationPath = Path.Combine(projectRoot, "Money.cs");
        var previousPrepassHook = IndexCommandRunner.FullScanCSharpPrepassForTesting;
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        var loadedPaths = new ConcurrentBag<string>();
        var contractRemoved = false;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                implementationPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            var interfaceChecksumBefore = ReadIndexedChecksum("IParseable.cs");
            var implementationChecksumBefore = ReadIndexedChecksum("Money.cs");

            File.AppendAllText(implementationPath, "// force raw workspace prepass\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(2));
            IndexCommandRunner.FullScanCSharpPrepassForTesting = () =>
            {
                Assert.False(contractRemoved);
                WriteParseableInterface(interfacePath, hasStaticContract: false);
                File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
                contractRemoved = true;
            };
            IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.True(contractRemoved);
            Assert.Empty(loadedPaths);
            Assert.Equal(2, partialJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            var failure = Assert.Single(partialJson.GetProperty("file_errors").EnumerateArray());
            Assert.Equal("IParseable.cs", failure.GetProperty("file").GetString());
            Assert.Equal("csharp_prepass", failure.GetProperty("phase").GetString());
            Assert.Equal(interfaceChecksumBefore, ReadIndexedChecksum("IParseable.cs"));
            Assert.Equal(implementationChecksumBefore, ReadIndexedChecksum("Money.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using (var partialDb = new DbContext(
                       DbOpenIntent.WriteIndex,
                       Path.Combine(projectRoot, ".cdidx", "codeindex.db")))
            {
                Assert.Null(new DbWriter(partialDb).GetCSharpStaticInterfaceSourceEvidence());
            }

            IndexCommandRunner.FullScanCSharpPrepassForTesting = previousPrepassHook;
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using var recoveryDb = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(false, new DbWriter(recoveryDb).GetCSharpStaticInterfaceSourceEvidence());

            string ReadIndexedChecksum(string path)
            {
                using var db = new DbContext(
                    DbOpenIntent.WriteIndex,
                    Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
                using var command = db.Connection.CreateCommand();
                command.CommandText = "SELECT checksum FROM files WHERE path = $path";
                command.Parameters.AddWithValue("$path", path);
                return Assert.IsType<string>(command.ExecuteScalar());
            }
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpPrepassForTesting = previousPrepassHook;
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_AfterSymbolsOnlyRebuildsCsharpGraphBeforeReady()
    {
        var projectRoot = CreateTempProject();
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        var loadedPaths = new ConcurrentBag<string>();
        try
        {
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--symbols-only", "--json", "--quiet"],
                    _jsonOptions));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));

            IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);
            var (refreshExitCode, refreshJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal("success", refreshJson.GetProperty("status").GetString());
            Assert.Equal(["IParseable.cs", "Money.cs"], loadedPaths.Order(StringComparer.Ordinal));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Null(db.GetMetaString(DbContext.SymbolsOnlyGraphOmittedMetaKey));
            var status = new DbReader(db.Connection).GetStatus();
            Assert.True(status.GraphDataCurrent);
            Assert.True(status.IndexComplete);
        }
        finally
        {
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_PositiveCsharpNoOpLateOversizeContractRefreshesEveryCsharpReference()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var stalePath = Path.Combine(projectRoot, "obsolete.py");
        var loadedPaths = new ConcurrentBag<string>();
        var previousRevalidationHook = IndexCommandRunner.FullScanCSharpFinalStatRevalidationForTesting;
        var previousContentLoadHook = IndexCommandRunner.FullScanFileContentLoadForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            File.WriteAllText(stalePath, "print('obsolete')\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            File.Delete(stalePath);
            IndexCommandRunner.FullScanCSharpFinalStatRevalidationForTesting = () =>
            {
                File.WriteAllText(interfacePath, new string('x', 2048));
                File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(2));
            };
            IndexCommandRunner.FullScanFileContentLoadForTesting = path => loadedPaths.Add(path);

            var (refreshExitCode, refreshJson) = RunAndCaptureJson(
                [projectRoot, "--max-file-bytes", "1024", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal("success", refreshJson.GetProperty("status").GetString());
            Assert.False(refreshJson.GetProperty("index_complete").GetBoolean());
            AssertCompletenessReason(
                refreshJson,
                "index_incomplete_reasons",
                "file_too_large");
            Assert.False(refreshJson.GetProperty("reference_graph_complete").GetBoolean());
            AssertCompletenessReason(
                refreshJson,
                "reference_graph_incomplete_reasons",
                "file_too_large");
            Assert.False(refreshJson.GetProperty("graph_data_current").GetBoolean());
            Assert.Equal(["IParseable.cs", "Money.cs"], loadedPaths.Order(StringComparer.Ordinal));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using var refreshDb = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(false, new DbWriter(refreshDb).GetCSharpStaticInterfaceSourceEvidence());
        }
        finally
        {
            IndexCommandRunner.FullScanCSharpFinalStatRevalidationForTesting = previousRevalidationHook;
            IndexCommandRunner.FullScanFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_DeletedCsharpStaticInterfaceContractDoesNotRegenerateImplicitReference()
    {
        var projectRoot = CreateTempProject();
        var previousPreflightHook = DbWriter.CSharpContractPreflightForTesting;
        var preflightCount = 0;
        try
        {
            DbWriter.CSharpContractPreflightForTesting = () =>
            {
                preflightCount++;
                previousPreflightHook?.Invoke();
            };
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            File.WriteAllText(
                interfacePath,
                """
                public interface IParseable<T>
                {
                    static abstract T Parse(string s);
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                """
                public readonly struct Money : IParseable<Money>
                {
                    public static Money Parse(string s) => new();
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0, preflightCount);

            File.Delete(interfacePath);

            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0, preflightCount);

            var noStaleExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, noStaleExitCode);
            Assert.Equal(0, preflightCount);

            using var conn = OpenNonPoolingConnection(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'IParseable'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.CSharpContractPreflightForTesting = previousPreflightHook;
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_DeletedPlainCsharpInterfaceKeepsUnchangedFilesReusable()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var plainInterfacePath = Path.Combine(projectRoot, "IPlain.cs");
            File.WriteAllText(plainInterfacePath, "public interface IPlain { void Run(); }\n");
            File.WriteAllText(Path.Combine(projectRoot, "Stable.cs"), "public sealed class Stable { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(plainInterfacePath);

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal(1, refreshJson.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal(1, refreshJson.GetProperty("summary").GetProperty("files_skipped").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_DeletedNonCsharpFileKeepsPositiveCsharpSnapshotReusable()
    {
        var projectRoot = CreateTempProject();
        try
        {
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            var stalePythonPath = Path.Combine(projectRoot, "stale.py");
            File.WriteAllText(stalePythonPath, "print('stale')\n");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.Delete(stalePythonPath);

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            var summary = refreshJson.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("files_purged").GetInt32());
            Assert.Equal(2, summary.GetProperty("files_skipped").GetInt32());
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_FatalDiscoveryErrorDefersCsharpGraphMutationUntilCleanRetry()
    {
        var projectRoot = CreateTempProject();
        var moneyPath = Path.Combine(projectRoot, "Money.cs");
        var oversizedIgnorePath = Path.Combine(projectRoot, ".gitignore");
        try
        {
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            File.WriteAllText(
                moneyPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.WriteAllText(
                moneyPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string text) => new();\n"
                + "}\n");
            File.SetLastWriteTimeUtc(moneyPath, DateTime.UtcNow.AddSeconds(2));
            File.WriteAllText(oversizedIgnorePath, new string('a', 256 * 1024 + 1));

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using (var partialDb = new DbContext(
                       DbOpenIntent.WriteIndex,
                       Path.Combine(projectRoot, ".cdidx", "codeindex.db")))
            {
                Assert.Null(new DbWriter(partialDb).GetCSharpStaticInterfaceSourceEvidence());
            }

            File.Delete(oversizedIgnorePath);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_FullScan_ConfiguredGeneratedCodePatternsKeepChunksButSkipExtraction()
    {
        using var env = EnvironmentVariableScope.Capture(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        var projectRoot = CreateTempProject();
        try
        {
            env.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, "src/generated/**");
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "generated"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "generated", "GeneratedClient.cs"),
                """
                public class GeneratedClient
                {
                    public string Lookup() => "generated";
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "NormalClient.cs"),
                """
                public class NormalClient
                {
                    public string Lookup() => "normal";
                }
                """);

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var generatedCmd = conn.CreateCommand();
                generatedCmd.CommandText = """
                    SELECT f.generated,
                           (SELECT COUNT(*) FROM chunks c WHERE c.file_id = f.id AND c.content LIKE '%GeneratedClient%'),
                           (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id),
                           (SELECT COUNT(*) FROM symbol_references r WHERE r.file_id = f.id),
                           (SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id AND i.kind = @issueKind)
                    FROM files f
                    WHERE f.path = @path
                    """;
                generatedCmd.Parameters.AddWithValue("@issueKind", FileIndexer.GeneratedCodeExtractionSkippedIssueKind);
                generatedCmd.Parameters.AddWithValue("@path", "src/generated/GeneratedClient.cs");
                using (var reader = generatedCmd.ExecuteReader())
                {
                    Assert.True(reader.Read());
                    Assert.Equal(0, reader.GetInt32(0));
                    Assert.True(reader.GetInt32(1) > 0);
                    Assert.Equal(0, reader.GetInt32(2));
                    Assert.Equal(0, reader.GetInt32(3));
                    Assert.Equal(1, reader.GetInt32(4));
                }

                using var normalCmd = conn.CreateCommand();
                normalCmd.CommandText = """
                    SELECT COUNT(*)
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE f.path = 'NormalClient.cs'
                      AND s.name = 'NormalClient'
                    """;
                Assert.Equal(1L, (long)normalCmd.ExecuteScalar()!);
            }

            env.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, null);
            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "src/generated/GeneratedClient.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);

            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var updatedCmd = conn.CreateCommand();
                updatedCmd.CommandText = """
                    SELECT f.generated,
                           (SELECT COUNT(*) FROM symbols s WHERE s.file_id = f.id AND s.name = 'GeneratedClient'),
                           (SELECT COUNT(*) FROM file_issues i WHERE i.file_id = f.id AND i.kind = @issueKind)
                    FROM files f
                    WHERE f.path = @path
                    """;
                updatedCmd.Parameters.AddWithValue("@issueKind", FileIndexer.GeneratedCodeExtractionSkippedIssueKind);
                updatedCmd.Parameters.AddWithValue("@path", "src/generated/GeneratedClient.cs");
                using var reader = updatedCmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(0, reader.GetInt32(0));
                Assert.Equal(1, reader.GetInt32(1));
                Assert.Equal(0, reader.GetInt32(2));
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [ProductionRuntimeFact]
    public void Run_FullScan_WithOversizedFile_PrintsSkipWarningWithoutRecoveryWarning()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            TestProjectHelper.WriteSparseFile(projectRoot, "huge.py", 10 * 1024 * 1024 + 1L);

            var (exitCode, _, stderr) = RunCliInSubprocess([projectRoot], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("[WARN] File too large", stderr);
            Assert.Contains("Index generation is incomplete: file_too_large.", stderr);
            Assert.Contains("Reference graph is incomplete: file_too_large.", stderr);
            Assert.DoesNotContain("Some files failed to index", stderr);
            Assert.DoesNotContain("rerun `cdidx index", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RedirectedOutput_PrintsIndexingBannerOnce()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "util.py"), "def helper():\n    return 1\n");

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, CountOccurrences(stdout, "Indexing..."));
            Assert.Contains("0.0%", stdout);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_CancelledAfterReadinessDemotion_RollsBackExistingIndex()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            int initialReadiness;
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                initialReadiness = db.GetUserVersion();
            Assert.Equal(DbContext.CurrentSchemaVersion, initialReadiness);
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "later.cs"), "public class Later { }\n");
            using var cancellation = new CancellationTokenSource();
            var hookInvoked = false;
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () =>
            {
                hookInvoked = true;
                cancellation.Cancel();
            };

            int interruptedExitCode;
            JsonElement interruptedJson;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions, cancellation);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    interruptedJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            Assert.Equal("error", interruptedJson.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, interruptedJson.GetProperty("error_code").GetString());
            Assert.Contains("full-scan progress was rolled back", interruptedJson.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("rolled back", interruptedJson.GetProperty("hint").GetString(), StringComparison.Ordinal);
            var reopenWarning = ConsoleCapture.CaptureError(() =>
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                Assert.Equal(initialReadiness, db.GetUserVersion());
            });
            Assert.DoesNotContain("Last batch did not complete", reopenWarning);
            Assert.DoesNotContain("later.cs", ReadIndexedPaths(dbPath));
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRun = statusJson.GetProperty("last_failed_or_partial_index_run");
            Assert.Equal("partial", lastRun.GetProperty("status").GetString());
            Assert.Equal("incremental", lastRun.GetProperty("mode").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, lastRun.GetProperty("error_code").GetString());
            Assert.False(lastRun.GetProperty("progress_persisted").GetBoolean());
            Assert.Contains("rolled back", lastRun.GetProperty("recovery_hint").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_WithMalformedIgnoreRule_ReturnsSuccessWithWarningInsteadOfCrashing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[z-a].py\nignored.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('ignored')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore:1", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.Contains("Invalid ignore rule skipped", json.GetProperty("warnings")[0].GetProperty("message").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("ignored.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_UsesRepositoryIgnoreCaseConfigWhenTrue()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            RunGit(repoRoot, "config", "core.ignorecase", "true");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "FOO.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "foo.py"), "print('ignored')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("foo.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_UsesRepositoryIgnoreCaseConfigWhenFalse()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            RunGit(repoRoot, "config", "core.ignorecase", "false");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "FOO.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "foo.py"), "print('kept')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("foo.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_RespectsAncestorGitignore()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/ignored.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('ignored')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('kept')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("ignored.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_SubdirectoryProjectRoot_RespectsAncestorDirectoryGitignoreRule()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('ignored root dir')\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("app.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ProjectRootNamedNodeModules_IndexesExplicitProjectRoot()
    {
        var tempRoot = CreateTempProject();
        var projectRoot = Path.Combine(tempRoot, "node_modules");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.js"), "console.log('ignored root dir');\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("app.js", indexedPaths);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RemovesIndexedScriptThatLosesShebang()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(toolPath, "plain text now\n");
            File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_scanned").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_FullScan_IncompleteSnapshotDoesNotWriteCheckpointAndCleanRetryRescans(
        bool symbolsOnly)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var srcDir = Path.Combine(projectRoot, "src");
        var secretDir = Path.Combine(projectRoot, "secret");
        var srcFile = Path.Combine(srcDir, "b.cs");
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(srcFile, "public class B { }\n");
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var jsonArgs = symbolsOnly
                ? new[] { projectRoot, "--symbols-only", "--json" }
                : [projectRoot, "--json"];
            var humanArgs = symbolsOnly
                ? new[] { projectRoot, "--symbols-only" }
                : [projectRoot];

            var initialExitCode = IndexCommandRunner.Run(jsonArgs, _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(secretDir, UnixFileMode.None);
            var (partialExitCode, partialJson) = RunAndCaptureJson(jsonArgs);

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.Equal(0, partialJson.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.True(partialJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Contains(
                partialJson.GetProperty("errors").EnumerateArray(),
                error =>
                    error.GetProperty("file").GetString() == "secret"
                    && error.GetProperty("message").GetString() == "Could not scan directory due to permissions.");

            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            Assert.False(File.Exists(checkpointPath));

            var (humanExitCode, _, stderr) = RunAndCaptureStreams(humanArgs);
            Assert.Equal(CommandExitCodes.PartialResult, humanExitCode);
            Assert.Contains("secret", stderr);
            Assert.Contains("Could not scan directory due to permissions.", stderr);

            SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var head = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            File.WriteAllText(
                checkpointPath,
                $$"""
                {
                  "Version": 1,
                  "GitHead": "{{head}}",
                  "Directories": [
                    "src"
                  ]
                }
                """);
            Assert.True(File.Exists(checkpointPath));
            File.Delete(srcFile);
            var (retryExitCode, retryJson) = RunAndCaptureJson(jsonArgs);

            Assert.Equal(CommandExitCodes.Success, retryExitCode);
            Assert.Equal("success", retryJson.GetProperty("status").GetString());
            Assert.False(File.Exists(checkpointPath));

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/b.cs", indexedPaths);
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_IncompleteSnapshotSkipsCheckpointWriteAndDeleteFailureIsWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(secretDir, UnixFileMode.None);
            var checkpointSaveAttempts = 0;
            IndexCommandRunner.WriteScanCheckpointForTesting = _ =>
            {
                checkpointSaveAttempts++;
                throw new IOException("checkpoint save denied");
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(0, checkpointSaveAttempts);

            IndexCommandRunner.WriteScanCheckpointForTesting = null;
            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            Assert.False(File.Exists(checkpointPath));

            SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var head = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            File.WriteAllText(
                checkpointPath,
                $$"""
                {
                  "Version": 1,
                  "GitHead": "{{head}}",
                  "Directories": [
                    "secret"
                  ]
                }
                """);
            IndexCommandRunner.DeleteScanCheckpointForTesting = _ => throw new IOException("checkpoint delete denied");

            var (deleteExitCode, deleteJson) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.Equal("success", deleteJson.GetProperty("status").GetString());
            Assert.Contains(
                deleteJson.GetProperty("warnings").EnumerateArray(),
                warning =>
                    warning.GetProperty("file").GetString() == "<scan_checkpoint>"
                    && warning.GetProperty("message").GetString()!.Contains("scan checkpoint delete failed", StringComparison.Ordinal)
                    && warning.GetProperty("message").GetString()!.Contains("IOException", StringComparison.Ordinal));
            Assert.True(File.Exists(checkpointPath));
        }
        finally
        {
            IndexCommandRunner.WriteScanCheckpointForTesting = null;
            IndexCommandRunner.DeleteScanCheckpointForTesting = null;
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_StableFileReadFailureDeletesLegacyCheckpointWithoutWritingOne()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var targetPath = Path.Combine(projectRoot, "stable.cs");
        try
        {
            File.WriteAllText(targetPath, "public class Stable { }\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            File.WriteAllText(
                checkpointPath,
                "{\"Version\":1,\"GitHead\":\"legacy\",\"Directories\":[\"\"]}");
            File.WriteAllText(targetPath, "public class Stable { public int Changed => 1; }\n");
            SetUnixPermissions(targetPath, UnixFileMode.None);
            var checkpointSaveAttempts = 0;
            var checkpointDeleteAttempts = 0;
            IndexCommandRunner.WriteScanCheckpointForTesting = _ =>
            {
                checkpointSaveAttempts++;
                throw new IOException("legacy checkpoint must not be written");
            };
            IndexCommandRunner.DeleteScanCheckpointForTesting = path =>
            {
                checkpointDeleteAttempts++;
                File.Delete(path);
            };

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(0, checkpointSaveAttempts);
            Assert.Equal(1, checkpointDeleteAttempts);
            Assert.False(File.Exists(checkpointPath));
        }
        finally
        {
            IndexCommandRunner.WriteScanCheckpointForTesting = null;
            IndexCommandRunner.DeleteScanCheckpointForTesting = null;
            if (File.Exists(targetPath))
                SetUnixPermissions(targetPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DeletesLegacyOversizedCheckpointWithoutParsing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "a.cs"), "public class A { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            var head = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var checkpointPath = Path.Combine(projectRoot, ".cdidx", "scan-checkpoint.json");
            Directory.CreateDirectory(Path.GetDirectoryName(checkpointPath)!);
            var checkpoint = $$"""
                {
                  "Version": 1,
                  "GitHead": "{{head}}",
                  "Directories": [
                    "src"
                  ]
                }
                """;
            var padding = new System.Text.StringBuilder(IndexCommandRunner.MaxScanCheckpointBytes + 2048);
            while (checkpoint.Length + padding.Length <= IndexCommandRunner.MaxScanCheckpointBytes)
                padding.Append(' ', 1024).Append('\n');
            File.WriteAllText(checkpointPath, checkpoint + padding);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var warnings = json.GetProperty("warnings");
            if (warnings.ValueKind == JsonValueKind.Array)
            {
                Assert.DoesNotContain(
                    warnings.EnumerateArray(),
                    warning => warning.GetProperty("file").GetString() == "<scan_checkpoint>");
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, warnings.ValueKind);
            }
            Assert.False(File.Exists(checkpointPath));

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("src/a.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private static (int Readiness, string? IndexCompleteness) ReadFullScanTrustSnapshot(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        return (db.GetUserVersion(), db.GetMetaString(DbContext.IndexCompletenessMetaKey));
    }

    [Fact]
    public void Run_FullScan_PreservesStaleRowsWhenAnyDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var priorTrust = ReadFullScanTrustSnapshot(dbPath);

            File.WriteAllText(toolPath, "plain text now\n");
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Contains(
                json.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("file").GetString() == "secret");

            Assert.Equal(priorTrust, ReadFullScanTrustSnapshot(dbPath));
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("tool", indexedPaths);
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_HumanOutput_ExplainsWriteBarrierWhenDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var priorTrust = ReadFullScanTrustSnapshot(dbPath);

            File.WriteAllText(toolPath, "plain text now\n");
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (humanExitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot]);

            Assert.Equal(CommandExitCodes.PartialResult, humanExitCode);
            Assert.DoesNotContain("Purged", stdout, StringComparison.Ordinal);
            Assert.Contains("stopped before index-data mutation", stderr, StringComparison.Ordinal);
            Assert.Equal(priorTrust, ReadFullScanTrustSnapshot(dbPath));
            Assert.Contains("tool", ReadIndexedPaths(dbPath));
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PreservesDeletedFilesWhenAnotherDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            var deletedPath = Path.Combine(projectRoot, "src", "a.cs");
            File.WriteAllText(deletedPath, "public class Deleted { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var priorTrust = ReadFullScanTrustSnapshot(dbPath);

            File.Delete(deletedPath);
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Contains(
                json.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("file").GetString() == "secret");

            Assert.Equal(priorTrust, ReadFullScanTrustSnapshot(dbPath));
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("src/a.cs", indexedPaths);
            Assert.Contains("secret/a.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PreservesDeletedRootFileWhenSiblingDirectoryIsUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            var deletedPath = Path.Combine(projectRoot, "direct.cs");
            File.WriteAllText(deletedPath, "public class Direct { }\n");
            File.WriteAllText(Path.Combine(secretDir, "hidden.cs"), "public class Hidden { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var priorTrust = ReadFullScanTrustSnapshot(dbPath);

            File.Delete(deletedPath);
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Contains(
                json.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("file").GetString() == "secret");

            Assert.Equal(priorTrust, ReadFullScanTrustSnapshot(dbPath));
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("direct.cs", indexedPaths);
            Assert.Contains("secret/hidden.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PreservesDeletedFilesWhenUnreadableDescendantExistsUnderSameParentDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var srcDir = Path.Combine(projectRoot, "src");
        var secretDir = Path.Combine(srcDir, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            var deletedPath = Path.Combine(srcDir, "direct.cs");
            File.WriteAllText(deletedPath, "public class Direct { }\n");
            File.WriteAllText(Path.Combine(secretDir, "hidden.cs"), "public class Hidden { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var priorTrust = ReadFullScanTrustSnapshot(dbPath);

            File.Delete(deletedPath);
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Contains(
                json.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("file").GetString() == "src/secret");

            Assert.Equal(priorTrust, ReadFullScanTrustSnapshot(dbPath));
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("src/direct.cs", indexedPaths);
            Assert.Contains("src/secret/hidden.cs", indexedPaths);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_PurgesDeletedFilesWithinDirectoryWhenExtensionlessSiblingProbeFails()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var deletedPath = Path.Combine(srcDir, "old.cs");
            var toolPath = Path.Combine(srcDir, "tool");
            File.WriteAllText(deletedPath, "public class Old { }\n");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            SetUnixPermissions(toolPath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_purged").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal("src/tool", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not probe file for indexability/language.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/old.cs", indexedPaths);
            Assert.Contains("src/tool", indexedPaths);
        }
        finally
        {
            var toolPath = Path.Combine(projectRoot, "src", "tool");
            if (File.Exists(toolPath))
                SetUnixPermissions(toolPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_OutputReportsReadinessInJsonAndHumanModes()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var (jsonExitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var (humanExitCode, output) = RunAndCaptureOutput([projectRoot]);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("Graph    : ready", output);
            Assert.Contains("Issues   : ready", output);
            Assert.Contains("SQL graph: ready", output);
            Assert.Contains("Hotspots : ready", output);
            Assert.Contains("Fold     : ready", output);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesUnchangedCSharpFilesWhenCanonicalNameContractChanged()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "money.cs"),
                """
                public struct Money
                {
                    public static explicit operator Money(decimal d) => new();
                }

                public class Bag
                {
                    public string this[int index] => "";
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name = 'explicit' WHERE name = 'explicit operator Money';
                    UPDATE symbols SET name = 'this' WHERE name = 'Item';
                    DELETE FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.True(json.GetProperty("csharp_symbol_name_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var exactNameCmd = verify.CreateCommand();
            exactNameCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'explicit operator Money'";
            Assert.Equal(1L, (long)exactNameCmd.ExecuteScalar()!);

            using var itemCmd = verify.CreateCommand();
            itemCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name = 'Item'";
            Assert.Equal(1L, (long)itemCmd.ExecuteScalar()!);

            using var legacyNameCmd = verify.CreateCommand();
            legacyNameCmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE name IN ('explicit', 'this')";
            Assert.Equal(0L, (long)legacyNameCmd.ExecuteScalar()!);

            using var contractCmd = verify.CreateCommand();
            contractCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'csharp_symbol_name_contract_version'";
            Assert.Equal(
                DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contractCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesPythonSemanticTypeKindsWhenExtractorContractChanges_Issue4615()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }");
            File.WriteAllText(
                Path.Combine(projectRoot, "lib.py"),
                """
                def target():
                    return 1

                type UserId = int
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET signature = 'def stale():' WHERE name = 'target';
                    UPDATE symbols SET kind = 'import' WHERE name = 'UserId';
                    UPDATE codeindex_meta SET value = '1' WHERE key = 'symbol_extractor_version_python';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("files_skipped").GetInt32() > 0);
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var signatureCmd = verify.CreateCommand();
            signatureCmd.CommandText = "SELECT signature FROM symbols WHERE name = 'target'";
            Assert.Equal("def target():", signatureCmd.ExecuteScalar() as string);

            using var typeKindCmd = verify.CreateCommand();
            typeKindCmd.CommandText = "SELECT kind FROM symbols WHERE name = 'UserId'";
            Assert.Equal("typealias", typeKindCmd.ExecuteScalar() as string);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'symbol_extractor_version_python'";
            Assert.Equal(
                SymbolExtractor.PythonContractVersion.ToString(CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesYamlHierarchyWhenExtractorContractChanges_Issue4873()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }");
            File.WriteAllText(
                Path.Combine(projectRoot, "workflow.yml"),
                """
                jobs:
                  build:
                    steps:
                      - name: Upload
                        with:
                          path: artifacts
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols
                    SET container_name = 'jobs.build.steps[0]',
                        container_qualified_name = 'jobs.build.steps[0]'
                    WHERE name = 'jobs.build.steps[0].with';
                    UPDATE codeindex_meta
                    SET value = '2'
                    WHERE key = 'symbol_extractor_version_yaml';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("files_skipped").GetInt32() > 0);

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var parentCmd = verify.CreateCommand();
            parentCmd.CommandText = """
                SELECT container_name || '|' || container_qualified_name
                FROM symbols
                WHERE name = 'jobs.build.steps[0].with'
                """;
            Assert.Equal("jobs.build.steps|jobs.build.steps[0]", parentCmd.ExecuteScalar() as string);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'symbol_extractor_version_yaml'";
            Assert.Equal(
                SymbolExtractor.YamlContractVersion.ToString(CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesNormalizedCSharpFieldsWhenExtractorContractChanges_Issue4865()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var entries = string.Join(
                ",\n",
                Enumerable.Range(0, 160).Select(index => $"        new Person({index})"));
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                $$"""
                public sealed record Person(int Id);

                public sealed class App
                {
                    private static readonly List<Person> BuiltInRecipes =
                    [
                {{entries}}
                    ];
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "lib.py"), "def untouched():\n    return 1\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols
                    SET kind = 'property',
                        signature = 'private static readonly List<Person> BuiltInRecipes = stale;'
                    WHERE name = 'BuiltInRecipes';
                    UPDATE codeindex_meta
                    SET value = '7'
                    WHERE key = 'symbol_extractor_version_csharp';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("summary").GetProperty("files_skipped").GetInt32() > 0);

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var fieldCmd = verify.CreateCommand();
            fieldCmd.CommandText = "SELECT kind, signature FROM symbols WHERE name = 'BuiltInRecipes'";
            using (var fieldReader = fieldCmd.ExecuteReader())
            {
                Assert.True(fieldReader.Read());
                Assert.Equal("field", fieldReader.GetString(0));
                Assert.Equal(
                    "private static readonly List<Person> BuiltInRecipes = …;",
                    fieldReader.GetString(1));
            }

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'symbol_extractor_version_csharp'";
            Assert.Equal(
                SymbolExtractor.CSharpContractVersion.ToString(CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ResolvesInheritedCSharpFieldReceiversAsFields_Issue4865()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "base.cs"),
                """
                namespace Demo;

                public enum Status { Ready }
                public sealed class Holder { public int Ready; }

                public abstract class Base
                {
                    protected Holder Status = new();
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "derived.cs"),
                """
                namespace Demo;

                public sealed class Derived : Base
                {
                    public int Read() => Status.Ready;
                }
                """);

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT r.reference_kind,
                       r.resolution_state,
                       target.kind,
                       target.name,
                       target.container_qualified_name
                FROM symbol_references AS r
                JOIN files AS source_file ON source_file.id = r.file_id
                LEFT JOIN symbols AS target ON target.id = r.target_symbol_id
                WHERE source_file.path = 'derived.cs'
                  AND r.symbol_name = 'Status'
                """;
            using var referenceReader = referenceCmd.ExecuteReader();
            Assert.True(referenceReader.Read());
            Assert.Equal("reference", referenceReader.GetString(0));
            Assert.Equal("resolved", referenceReader.GetString(1));
            Assert.Equal("field", referenceReader.GetString(2));
            Assert.Equal("Status", referenceReader.GetString(3));
            Assert.Equal("Demo.Base", referenceReader.GetString(4));
            Assert.False(referenceReader.Read());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RebuildRestampsExtractorVersionForZeroSymbolLanguage()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "empty.py"), "# intentionally no declarations\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES(@key, '0')";
                cmd.Parameters.AddWithValue("@key", DbContext.GetSymbolExtractorVersionMetaKey("python"));
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var symbolCountCmd = verify.CreateCommand();
            symbolCountCmd.CommandText = """
                SELECT COUNT(*)
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'empty.py'
                """;
            Assert.Equal(0L, (long)symbolCountCmd.ExecuteScalar()!);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            versionCmd.Parameters.AddWithValue("@key", DbContext.GetSymbolExtractorVersionMetaKey("python"));
            Assert.Equal(
                SymbolExtractor.GetContractVersion("python").ToString(CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesUnchangedSqlFilesWhenSqlGraphContractChanged()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "sql"));
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "target.sql"),
                """
                CREATE FUNCTION dbo.fn_Target()
                RETURNS INT
                AS
                BEGIN
                    RETURN 1;
                END;
                GO
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "sql", "caller.sql"),
                """
                CREATE PROCEDURE dbo.usp_Caller
                AS
                BEGIN
                    SELECT dbo.fn_Target();
                END;
                GO
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbol_references
                    SET symbol_name = 'fn_Target',
                        symbol_name_folded = 'fn_target',
                        column_number = 1
                    WHERE symbol_name = 'dbo.fn_Target';
                    DELETE FROM codeindex_meta WHERE key = 'sql_graph_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.True(json.GetProperty("sql_graph_contract_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();

            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT symbol_name, column_number
                FROM symbol_references
                WHERE container_name = 'dbo.usp_Caller'
                LIMIT 1
                """;
            using var reader = referenceCmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("fn_Target", reader.GetString(0));
            Assert.NotEqual(1L, reader.GetInt64(1));

            using var contractCmd = verify.CreateCommand();
            contractCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'sql_graph_contract_version'";
            Assert.Equal(
                DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contractCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesUnchangedHdlFilesWhenGraphContractIsMissing_Issue4742()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "top.v"),
                """
                module child;
                endmodule
                module top;
                    child u_child();
                endmodule
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DELETE FROM symbol_references WHERE file_id IN (
                        SELECT id FROM files WHERE lang IN ('verilog', 'systemverilog', 'vhdl')
                    );
                    DELETE FROM codeindex_meta WHERE key = 'hdl_graph_contract_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var legacyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var legacyReader = new DbReader(legacyDb.Connection);
                Assert.False(legacyReader.GetStatus().GraphDataCurrent);
                var exactSignal = legacyReader.GetReferencesExactQuerySignal(lang: "verilog");
                Assert.False(exactSignal.ExactIndexAvailable);
                Assert.Contains("hdl_graph_contract_ready=false", exactSignal.DegradedReason, StringComparison.Ordinal);
            }

            var (scopedExitCode, scopedJson) = RunAndCaptureJson([
                projectRoot,
                "--files",
                Path.Combine(projectRoot, "top.v"),
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, scopedExitCode);
            Assert.Equal(1, scopedJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.False(scopedJson.GetProperty("graph_data_current").GetBoolean());
            Assert.False(scopedJson.GetProperty("hdl_graph_contract_ready").GetBoolean());
            Assert.Contains(
                "hdl_graph_contract_ready=false",
                scopedJson.GetProperty("hdl_graph_contract_degraded_reason").GetString(),
                StringComparison.Ordinal);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());
            Assert.True(json.GetProperty("graph_data_current").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT COUNT(*)
                FROM symbol_references r
                JOIN files f ON f.id = r.file_id
                WHERE f.lang = 'verilog'
                  AND r.reference_kind = 'instantiate'
                  AND r.symbol_name = 'child'
                """;
            Assert.Equal(1L, (long)referenceCmd.ExecuteScalar()!);

            using var contractCmd = verify.CreateCommand();
            contractCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'hdl_graph_contract_version'";
            Assert.Equal(
                DbContext.HdlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                contractCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RewritesStaleCSharpExtractorContractForRazorDirectives()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var pagesDir = Path.Combine(projectRoot, "Pages");
            Directory.CreateDirectory(pagesDir);
            var sourcePath = Path.Combine(pagesDir, "Product.razor");
            File.WriteAllText(
                sourcePath,
                """
                @page "/products/{id:int}"
                @implements IDisposable
                @attribute [Authorize]
                @layout MainLayout

                @code {
                    public void Dispose() { }
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM symbols WHERE kind IN ('route', 'implements', 'attribute', 'layout');
                    UPDATE codeindex_meta
                    SET value = '0'
                    WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var symbolsCmd = verify.CreateCommand();
            symbolsCmd.CommandText = """
                SELECT kind, name
                FROM symbols
                WHERE kind IN ('route', 'implements', 'attribute', 'layout')
                ORDER BY kind, name
                """;
            var symbols = new List<(string Kind, string Name)>();
            using (var reader = symbolsCmd.ExecuteReader())
            {
                while (reader.Read())
                    symbols.Add((reader.GetString(0), reader.GetString(1)));
            }

            Assert.Contains(("attribute", "Authorize"), symbols);
            Assert.Contains(("implements", "IDisposable"), symbols);
            Assert.Contains(("layout", "MainLayout"), symbols);
            Assert.Contains(("route", "/products/{id:int}"), symbols);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            Assert.Equal(
                SymbolExtractor.CSharpContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReextractsQualifiedCommonCallsFromVersion7CSharpIndex_Issue4867()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                """
                using System.Text.Json;

                public class Caller
                {
                    public void Run(JsonElement json) => json.GetString();
                }
                """);

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM symbol_references
                    WHERE symbol_name = 'GetString';
                    UPDATE codeindex_meta
                    SET value = '7'
                    WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name = 'GetString'
                """;
            Assert.Equal(1L, referenceCmd.ExecuteScalar());

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText =
                $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            Assert.Equal(
                SymbolExtractor.CSharpContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
            Assert.Equal(10, SymbolExtractor.CSharpContractVersion);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ScopedUpdate_LoadsPersistedMemberReadTargetsAndRefreshesConsumers_Issue4894()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var valuesPath = Path.Combine(projectRoot, "Values.cs");
            var callerPath = Path.Combine(projectRoot, "Caller.cs");
            File.WriteAllText(
                valuesPath,
                """
                public static class Values
                {
                }
                """);
            File.WriteAllText(
                callerPath,
                """
                public sealed class Caller
                {
                    public int Read() => Values.Limit;
                }
                """);

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet"],
                    _jsonOptions));

            var dbPath = Path.Combine(
                projectRoot,
                ".cdidx",
                "codeindex.db");
            Assert.Equal(0L, CountMemberReadReferences(dbPath, "Limit"));

            File.WriteAllText(
                valuesPath,
                """
                public static class Values
                {
                    public const int Limit = 10;
                }
                """);
            File.SetLastWriteTimeUtc(
                valuesPath,
                DateTime.UtcNow.AddSeconds(2));

            var (addExitCode, addJson) = RunAndCaptureJson(
            [
                projectRoot,
                "--files",
                valuesPath,
                "--json",
                "--quiet",
            ]);

            Assert.Equal(CommandExitCodes.Success, addExitCode);
            Assert.Equal("success", addJson.GetProperty("status").GetString());
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Limit"));

            File.WriteAllText(
                valuesPath,
                """
                public static class Values
                {
                }
                """);
            File.SetLastWriteTimeUtc(
                valuesPath,
                DateTime.UtcNow.AddSeconds(4));

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                [
                    projectRoot,
                    "--files",
                    valuesPath,
                    "--json",
                    "--quiet",
                ],
                _jsonOptions));
            Assert.Equal(0L, CountMemberReadReferences(dbPath, "Limit"));

            File.WriteAllText(
                callerPath,
                """
                public sealed class Caller
                {
                    public int Read() =>
                        Values.Limit + Values.Other + Values.Property;
                }
                """);
            File.WriteAllText(
                valuesPath,
                """
                public static class Values
                {
                    public const int Limit = 10;
                    public static readonly int Other = 20;
                    public static int Property => 30;
                }
                """);
            File.SetLastWriteTimeUtc(
                valuesPath,
                DateTime.UtcNow.AddSeconds(6));
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                [
                    projectRoot,
                    "--files",
                    valuesPath,
                    "--json",
                    "--quiet",
                ],
                _jsonOptions));
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Limit"));
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Other"));
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Property"));

            File.WriteAllText(
                callerPath,
                """
                public sealed class Caller
                {
                    public int Read() =>
                        Values.Limit + Values.Other + Values.Property + 1;
                }
                """);
            File.SetLastWriteTimeUtc(
                callerPath,
                DateTime.UtcNow.AddSeconds(8));

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                [
                    projectRoot,
                    "--files",
                    callerPath,
                    "--json",
                    "--quiet",
                ],
                _jsonOptions));
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Limit"));
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Other"));
            Assert.Equal(1L, CountMemberReadReferences(dbPath, "Property"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }

        static long CountMemberReadReferences(
            string dbPath,
            string symbolName)
        {
            using var connection = OpenNonPoolingConnection(dbPath);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name = $symbol_name
                  AND reference_kind = 'member_read'
                """;
            command.Parameters.AddWithValue("$symbol_name", symbolName);
            return (long)command.ExecuteScalar()!;
        }
    }

    [Fact]
    public void Run_FullScan_ReclassifiesQualifiedValueReadsFromVersion8CSharpIndex_Issue4894()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Values.cs"),
                """
                public static class Values
                {
                    public const int Limit = 10;
                    public static readonly int Other = 20;
                    public static int Property => 30;
                }
                """);

            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                """
                public sealed class Caller
                {
                    private static int Limit() => 0;

                    public int Read() => Values.Limit + Values.Other + Values.Property;
                }
                """);

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    UPDATE symbol_references
                    SET reference_kind = 'call'
                    WHERE symbol_name IN ('Limit', 'Other', 'Property');
                    UPDATE codeindex_meta
                    SET value = '8'
                    WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT symbol_name, reference_kind
                FROM symbol_references
                WHERE symbol_name IN ('Limit', 'Other', 'Property')
                ORDER BY symbol_name
                """;
            using (var reader = referenceCmd.ExecuteReader())
            {
                var actual = new List<(string Name, string Kind)>();
                while (reader.Read())
                    actual.Add((reader.GetString(0), reader.GetString(1)));

                Assert.Equal(
                    [
                        ("Limit", "member_read"),
                        ("Other", "member_read"),
                        ("Property", "member_read"),
                    ],
                    actual);
            }

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText =
                $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            Assert.Equal(
                SymbolExtractor.CSharpContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
            Assert.Equal(10, SymbolExtractor.CSharpContractVersion);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RefreshesHotspotWhenAddedTargetResolvesSkippedCommonCall_Issue4867()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                """
                public class Caller
                {
                    public void Run(LocalApi api) => api.GetString();
                }
                """);

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var initial = OpenNonPoolingConnection(dbPath))
            {
                initial.Open();
                using var command = initial.CreateCommand();
                command.CommandText = """
                    SELECT resolution_state
                    FROM symbol_references
                    WHERE symbol_name = 'GetString'
                    """;
                Assert.Equal("unresolved", command.ExecuteScalar() as string);
                command.CommandText = """
                    SELECT COALESCE(SUM(reference_count), 0)
                    FROM hotspot_reference_counts
                    WHERE lang = 'csharp'
                      AND raw_symbol_name = 'GetString'
                    """;
                Assert.Equal(0L, command.ExecuteScalar());
            }

            File.WriteAllText(
                Path.Combine(projectRoot, "Target.cs"),
                """
                public class LocalApi
                {
                    public string GetString() => "";
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = """
                SELECT resolution_state
                FROM symbol_references
                WHERE symbol_name = 'GetString'
                """;
            Assert.Equal("resolved", verifyCommand.ExecuteScalar() as string);
            verifyCommand.CommandText = """
                SELECT COALESCE(SUM(reference_count), 0)
                FROM hotspot_reference_counts
                WHERE lang = 'csharp'
                  AND raw_symbol_name = 'GetString'
                """;
            Assert.Equal(1L, verifyCommand.ExecuteScalar());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_StampsRefreshedDynamicGraphContractWhenFoldContractRemainsStale_Issue4746()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "commands.tcl"),
                """
                proc helper {} { return 1 }
                proc run {} { helper }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "unchanged.cs"),
                "class Unchanged { void Run() { } }");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM codeindex_meta
                    WHERE key = '{DbContext.GetDynamicReferenceGraphContractVersionMetaKey("tcl")}';
                    UPDATE codeindex_meta
                    SET value = '0'
                    WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText =
                $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetDynamicReferenceGraphContractVersionMetaKey("tcl")}'";
            Assert.Equal(
                SymbolExtractor.DynamicReferenceGraphContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);

            using var referenceCmd = verify.CreateCommand();
            referenceCmd.CommandText = """
                SELECT COUNT(*)
                FROM symbol_references
                WHERE symbol_name = 'helper'
                  AND container_name = 'run'
                  AND reference_kind = 'call'
                """;
            Assert.Equal(1L, referenceCmd.ExecuteScalar());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ScopedUpdate_StampsFullyRefreshedDynamicGraphLanguages_Issue4746()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "initial.cs"),
                "class Initial { void Run() { } }");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var tclPath = Path.Combine(projectRoot, "commands.tcl");
            File.WriteAllText(
                tclPath,
                """
                proc helper {} { return 1 }
                proc run {} { helper }
                """);

            var (addedExitCode, addedJson) = RunAndCaptureJson(
                [projectRoot, "--files", tclPath, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, addedExitCode);
            Assert.True(addedJson.GetProperty("reference_graph_complete").GetBoolean());
            Assert.True(addedJson.GetProperty("graph_data_current").GetBoolean());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT value
                    FROM codeindex_meta
                    WHERE key = '{DbContext.GetDynamicReferenceGraphContractVersionMetaKey("tcl")}';
                    """;
                Assert.Equal(
                    SymbolExtractor.DynamicReferenceGraphContractVersion.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    cmd.ExecuteScalar() as string);

                cmd.CommandText = $"""
                    DELETE FROM codeindex_meta
                    WHERE key = '{DbContext.GetDynamicReferenceGraphContractVersionMetaKey("tcl")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            File.AppendAllText(tclPath, "\nproc second {} { helper }\n");
            File.SetLastWriteTimeUtc(tclPath, DateTime.UtcNow.AddSeconds(2));
            var (refreshedExitCode, refreshedJson) = RunAndCaptureJson(
                [projectRoot, "--files", tclPath, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, refreshedExitCode);
            Assert.True(refreshedJson.GetProperty("reference_graph_complete").GetBoolean());
            Assert.True(refreshedJson.GetProperty("graph_data_current").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ScopedUpdate_KeepsFoldReadyIndependentOfStaleDynamicGraphContract_Issue4746()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "commands.tcl"),
                """
                proc helper {} { return 1 }
                proc run {} { helper }
                """);
            var csharpPath = Path.Combine(projectRoot, "other.cs");
            File.WriteAllText(csharpPath, "class Other { void Run() { } }");

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM codeindex_meta
                    WHERE key = '{DbContext.GetDynamicReferenceGraphContractVersionMetaKey("tcl")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(csharpPath, "class Other { void Run() { int value = 1; } }");
            File.SetLastWriteTimeUtc(csharpPath, DateTime.UtcNow.AddSeconds(2));
            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--files", csharpPath, "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.False(json.GetProperty("reference_graph_complete").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            var status = new DbReader(verify).GetStatus();
            Assert.True(status.FoldReady);
            Assert.False(status.ReferenceGraphComplete);
            Assert.Contains(
                DbReader.DynamicReferenceGraphContractStaleReason,
                status.ReferenceGraphIncompleteReasons ?? []);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_StampsSuppressedDynamicGraphLanguage_Issue4746()
    {
        var projectRoot = CreateTempProject();
        using var env = EnvironmentVariableScope.Capture(
            IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        env.Set(IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable, "*.cr");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "generated.cr"),
                """
                def helper
                  1
                end
                """);

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--json", "--rebuild", "--yes"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("reference_graph_complete").GetBoolean());
            Assert.True(json.GetProperty("graph_data_current").GetBoolean());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText =
                $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetDynamicReferenceGraphContractVersionMetaKey("crystal")}'";
            Assert.Equal(
                SymbolExtractor.DynamicReferenceGraphContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_ReindexesUnchangedCSharpTupleReadonlyFieldWhenExtractorVersionChanged_Issue4616()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "tuple-fields.cs"),
                """
                public class TupleFields
                {
                    public static readonly (int Left, int Right) Pair = (1, 2);
                }
                """);

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM symbols WHERE name = 'Pair';
                    UPDATE codeindex_meta
                    SET value = '3'
                    WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}';
                    """;
                cmd.ExecuteNonQuery();
            }

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("files_skipped").GetInt32());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var symbolCmd = verify.CreateCommand();
            symbolCmd.CommandText = "SELECT kind, return_type FROM symbols WHERE name = 'Pair'";
            using (var reader = symbolCmd.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("field", reader.GetString(0));
                Assert.Equal("(int Left, int Right)", reader.GetString(1));
                Assert.False(reader.Read());
            }

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            Assert.Equal(
                SymbolExtractor.CSharpContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                versionCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_FullScan_DegradedWarningSummarizesRemainingFoldGap()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }

            var (exitCode, _, errorOutput) = RunCliInSubprocess([projectRoot], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index completed with fold-only degraded readiness (fold_ready=false).", errorOutput);
            Assert.Contains("older fold-key version", errorOutput);
            Assert.Contains("cdidx backfill-fold --db", errorOutput);
            Assert.Contains("cdidx index", errorOutput);
            Assert.Contains("--rebuild", errorOutput);
            Assert.Contains("fold_ready=false", errorOutput);
            Assert.DoesNotContain("Run `cdidx status --db", errorOutput);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotStampFoldReadyWhenLegacyRowsRemain()
    {
        // Codex #86 review regression: on a legacy DB (pre-#86) opened by a new binary, the
        // incremental default of `cdidx index .` skips unchanged files via GetUnchangedFileId.
        // Their old rows stay NULL in name_folded. Stamping FoldReady would flip readers onto
        // the folded-equality path and silently miss those rows. Verify the stamp is withheld.
        // Legacy 行が残っているときに FoldReady が stamp されないことを確認する回帰テスト。
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");

            // Initial index — writes every row with name_folded populated, stamps FoldReady.
            // 初回 index: 全行 folded 付き、FoldReady stamp される。
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            // Simulate pre-#86 legacy state: wipe folded columns + FoldReady bit on the existing
            // row to model an upgrade from a binary that did not populate name_folded yet.
            // pre-#86 を模擬: folded 列を NULL に戻し、FoldReady bit も落とす。
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL; UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL; PRAGMA user_version = 3";
                cmd.ExecuteNonQuery();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Incremental re-run skips the unchanged file — legacy rows with NULL folded columns
            // still exist, so FoldReady MUST NOT be restamped.
            // 再 index は unchanged file を skip するため legacy 行が残る → FoldReady は立てない。
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)verifyCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsHotspotFamilyReadyWhenMarkerFingerprintChanges()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"),
                """
                public partial class Api
                {
                    public void Run() { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"),
                """
                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"),
                """
                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                        api.Run(1);
                    }
                }
                """);

            var (exitCode1, json1) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            Assert.Equal("success", json1.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var seededDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            File.WriteAllText(Path.Combine(projectRoot, "Extra.csproj"), "<Project />");

            var (exitCode2, json2) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("success", json2.GetProperty("status").GetString());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_KeepsCsharpHotspotFamilyTrustWhenOnlyVbMarkersChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal("success", initialJson.GetProperty("status").GetString());

            File.WriteAllText(Path.Combine(projectRoot, "Unrelated.vbproj"), "<Project />");

            var (rerunExitCode, rerunJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, rerunExitCode);
            Assert.Equal("success", rerunJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJson(dbPath, "csharp", "function");
            Assert.True(hotspotsExitCode is CommandExitCodes.Success or CommandExitCodes.NotFound);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());
            if (hotspotsJson.TryGetProperty("degraded", out var degraded))
                Assert.False(degraded.GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsHotspotFamilyTrustWhenOnlyMetadataWasCleared()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal("success", initialJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
            }

            var (rerunExitCode, rerunJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, rerunExitCode);
            Assert.Equal("success", rerunJson.GetProperty("status").GetString());
            Assert.True(rerunJson.GetProperty("summary").GetProperty("files_skipped").GetInt32() > 0);
            Assert.True(rerunJson.GetProperty("hotspot_family_ready").GetBoolean());

            using (var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(
                    DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
                Assert.False(string.IsNullOrWhiteSpace(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"))));
            }

            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJson(dbPath, "csharp", "function");
            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Equal(2, hotspotsJson.GetProperty("count").GetInt32());
            if (hotspotsJson.TryGetProperty("degraded", out var degraded))
                Assert.False(degraded.GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_MarkerlessMultiSubtreePartialsStaySeparatedInHotspots()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "projA", "src"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "projB", "src"));

            File.WriteAllText(Path.Combine(projectRoot, "projA", "src", "Api.Part1.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run()
                    {
                    }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projA", "src", "Api.Part2.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projA", "src", "Caller.cs"),
                """
                namespace Shared;

                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                    }
                }
                """);

            File.WriteAllText(Path.Combine(projectRoot, "projB", "src", "Api.Part1.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run()
                    {
                    }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projB", "src", "Api.Part2.cs"),
                """
                namespace Shared;

                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "projB", "src", "Caller.cs"),
                """
                namespace Shared;

                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                    }
                }
                """);

            var (indexExitCode, indexJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal("success", indexJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJsonWithPaths(dbPath, "csharp", "function", ["projA/", "projB/"]);

            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Equal(0, hotspotsJson.GetProperty("count").GetInt32());
            Assert.Empty(hotspotsJson.GetProperty("hotspots").EnumerateArray());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_MarkerlessRootLevelPartialsStayVisibleInHotspots()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Api.Part1.cs"),
                """
                public partial class Api
                {
                    public void Run() { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "Api.Part2.cs"),
                """
                public partial class Api
                {
                    public void Run(int value) { }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "Caller.cs"),
                """
                public class Caller
                {
                    public void Call(Api api)
                    {
                        api.Run();
                        api.Run(1);
                    }
                }
                """);

            var (indexExitCode, indexJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal("success", indexJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (hotspotsExitCode, hotspotsJson) = RunHotspotsJson(dbPath, "csharp", "function");

            Assert.Equal(CommandExitCodes.Success, hotspotsExitCode);
            Assert.True(hotspotsJson.GetProperty("hotspot_family_ready").GetBoolean());

            var runRows = hotspotsJson.GetProperty("hotspots")
                .EnumerateArray()
                .Where(item => item.GetProperty("name").GetString() == "Run")
                .ToList();

            var runRow = Assert.Single(runRows);
            Assert.Matches(@"Api\.Part[12]\.cs", runRow.GetProperty("path").GetString() ?? string.Empty);
            Assert.Equal(2, runRow.GetProperty("reference_count").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotRestampFoldReadyWhenFoldKeyVersionMismatches()
    {
        // Normal non-rebuild `cdidx index .` is still incremental: unchanged rows are skipped.
        // If an existing DB carries old-version fold keys, a full scan must not advertise the
        // new version unless every row is rewritten (that requires --rebuild).
        // 通常の full scan も skip を使うため、旧 version key が残る DB では FoldReady を
        // restamp してはいけない。安全に昇格できるのは --rebuild のみ。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "intl.py"), "def Straße():\n    pass\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Add a new file so the next non-rebuild scan mixes freshly-written v2 rows with
            // untouched v1-style rows. The run must leave FoldReady off.
            // 新規ファイルを追加して mixed-state を作る。FoldReady は off のままであるべき。
            File.WriteAllText(Path.Combine(projectRoot, "new.cs"), "public class NewFile { }");

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.NotEqual(NameFold.Version.ToString(), storedVersion);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotRestampFoldReadyWhenFoldFingerprintMismatchesAndFilesAreSkipped()
    {
        // #97 codex review: a normal `index .` run still skips unchanged files, so a stale
        // fold_key_fingerprint must not be overwritten with the current runtime fingerprint
        // unless every row was regenerated. Otherwise skipped rows keep old physical keys.
        // #97: 通常の `index .` で unchanged 行が skip される場合、stale fingerprint を
        // current 値へ再 stamp してはいけない。全件再生成できたときだけ trusted に戻せる。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = 'DEADBEEFDEADBEEF' WHERE key = 'fold_key_fingerprint'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var fingerprintCmd = verify.CreateCommand();
            fingerprintCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_fingerprint'";
            var storedFingerprint = fingerprintCmd.ExecuteScalar() as string;
            Assert.Equal("DEADBEEFDEADBEEF", storedFingerprint);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_DoesNotRestampFoldReadyWhenSkippedRowsCarryStaleFoldKeys()
    {
        // Issue #2066: current fold metadata alone is not enough to trust skipped rows.
        // If a legacy/corrupt row carries a non-NULL folded key that no longer matches
        // NameFold.Fold(name), an unchanged full scan must keep FoldReady demoted.
        // Issue #2066: metadata が current でも、skip 行の実 folded key が現在の
        // NameFold.Fold(name) と違うなら FoldReady を回復してはいけない。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class Straße { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA user_version = 0;
                    UPDATE symbols SET name_folded = 'straße' WHERE name = 'Straße';
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var foldedCmd = verify.CreateCommand();
            foldedCmd.CommandText = "SELECT name_folded FROM symbols WHERE name = 'Straße'";
            Assert.Equal("straße", foldedCmd.ExecuteScalar() as string);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_RestampsFoldReadyWhenUserVersionWasClearedButFoldMetadataStillMatches()
    {
        // #97 codex review: if a previous refresh cleared user_version before restamping
        // FoldReady, a normal unchanged full scan should recover trust when the stored fold
        // version/fingerprint still match the current runtime and every folded column is
        // already backfilled.
        // #97: 途中中断で user_version だけ落ちた current DB は、fold metadata が current と
        // 一致していれば通常の unchanged full scan で FoldReady を回復できる必要がある。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_version'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.Equal(NameFold.Version.ToString(), storedVersion);

            using var fingerprintCmd = verify.CreateCommand();
            fingerprintCmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'fold_key_fingerprint'";
            var storedFingerprint = fingerprintCmd.ExecuteScalar() as string;
            Assert.Equal(NameFold.Fingerprint(), storedFingerprint);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    // Issue #1508: full scans must capture the current HEAD so a subsequent default
    // incremental run after `git switch <branch>` can detect that the DB no longer
    // mirrors the worktree and recommend `--rebuild`.
    // Issue #1508: full scan が HEAD を保存することで、後続の incremental が branch 切替を検知できる。
    [Fact]
    public void Run_FullScan_PersistsCurrentHeadCommit()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var expectedHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var exitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(expectedHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_AfterBranchSwitch_ConvergesWithoutRebuildWarning_Issue5054()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var firstHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            // Branch + commit to advance HEAD without changing on-disk app.cs.
            // ブランチ作成と新規コミットで HEAD だけを動かす。
            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");
            var secondHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(firstHead, secondHead);

            // A subsequent default full scan verifies and purges the complete workspace, so
            // its final response must describe the converged state without recommending rebuild.
            // 後続の既定 full scan は workspace 全体を検証・purge するため、完了レスポンスは
            // rebuild を勧めず収束済み状態を返す。
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
            Assert.Equal(firstHead, json.GetProperty("prior_indexed_head_commit").GetString());
            Assert.Equal(secondHead, json.GetProperty("current_head_commit").GetString());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("head_change_notice").ValueKind);

            // After a successful re-scan the HEAD pointer should be updated to the new value.
            // 再スキャン成功後は HEAD が新しい値に更新される。
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(secondHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
            Assert.Equal(secondHead, db.GetMetaString(DbContext.WorkspaceVerifiedHeadShaMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_LegacyDbWithoutCapturedHead_DoesNotReportHeadChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            // Simulate a legacy DB by removing the captured HEAD meta row.
            // legacy DB を再現するため HEAD メタを削除する。
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                cmd.Parameters.AddWithValue("@key", DbContext.IndexedHeadCommitMetaKey);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("prior_indexed_head_commit").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("head_change_notice").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScanJson_WritesLivenessToStderrOnly()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var (exitCode, json, stderr) = RunAndCaptureJsonWithStderr([projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Contains("cdidx: scanning files...", stderr);
            Assert.Contains("cdidx: preparing index writes...", stderr);
            Assert.Contains("cdidx: preparing C# workspace symbols...", stderr);
            Assert.Contains("cdidx: indexed 0/1 file(s)...", stderr);
            Assert.Contains("cdidx: indexed 1/1 file(s)...", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_NonGitWorkspace_DoesNotReportHeadChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_FullScan_Rebuild_DoesNotReportHeadChangeEvenIfHeadDiffers()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(Path.Combine(projectRoot, "feature.cs"), "public class Feature { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");

            // --rebuild already wipes the DB, so HEAD divergence is irrelevant on that path.
            // --rebuild は DB を消すため HEAD 差分の警告は不要。
            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--rebuild", "--yes", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("head_changed").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("head_change_notice").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }
}
