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
    public void Run_DryRun_ReadOnlyUriDbPath_ReturnsDryRunSummary()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var readOnlyUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", readOnlyUri, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("languages").GetProperty("csharp").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_skips").GetInt32());
            Assert.Equal(1, json.GetProperty("parse_estimate_files_processed").GetInt32());
            Assert.False(json.GetProperty("parse_estimate_files_truncated").GetBoolean());
            var mutations = json.GetProperty("estimated_table_mutations");
            Assert.True(mutations.GetProperty("chunks").GetInt64() > 0);
            Assert.True(mutations.GetProperty("symbols").GetInt64() > 0);
            Assert.Equal(0, mutations.GetProperty("symbol_references").GetInt64());
            var symbolDetail = json
                .GetProperty("estimated_table_mutation_details")
                .GetProperty("symbols");
            Assert.Equal("parse_only_and_index_snapshot", symbolDetail.GetProperty("source").GetString());
            Assert.Equal("estimate", symbolDetail.GetProperty("confidence").GetString());
            Assert.Empty(symbolDetail.GetProperty("unknown_reasons").EnumerateArray());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_CustomInWorkspaceDbDoesNotProbeDatabase_Issue4611()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "class App {}\n");
            var dbPath = Path.Combine(projectRoot, "index.json");
            var (indexExitCode, _) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(0, json.GetProperty("errors_total").GetInt32());
            Assert.Equal(0, json.GetProperty("unknown_extension_total").GetInt32());
            Assert.Equal(1, json.GetProperty("languages").GetProperty("csharp").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRunWithRebuildAndMemoryTrace_SkipsConfirmationAndPreservesWorkspace_Issue4580()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SqliteConnection.ClearAllPools();
            var before = Directory
                .EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToDictionary(
                    static path => path,
                    static path => File.ReadAllBytes(path),
                    StringComparer.Ordinal);

            IndexCommandRunner.IsInputRedirectedForTesting = () => true;
            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--rebuild",
                "--dry-run",
                "--memory-trace",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            var timeline = json.GetProperty("memory_timeline");
            var samples = timeline.GetProperty("samples").EnumerateArray().ToArray();
            Assert.Equal(["start", "snapshot", "scan", "finalize"], samples.Select(sample => sample.GetProperty("phase").GetString()));
            Assert.All(samples, sample =>
            {
                Assert.True(sample.GetProperty("elapsed_ms").GetInt64() >= 0);
                Assert.True(sample.GetProperty("heap_bytes").GetInt64() >= 0);
                Assert.True(sample.GetProperty("working_set_bytes").GetInt64() > 0);
            });
            Assert.True(timeline.GetProperty("peak_working_set_bytes").GetInt64() > 0);
            Assert.True(timeline.GetProperty("peak_heap_bytes").GetInt64() >= 0);

            SqliteConnection.ClearAllPools();
            var afterPaths = Directory
                .EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(before.Keys, afterPaths);
            foreach (var (path, bytes) in before)
                Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            IndexCommandRunner.IsInputRedirectedForTesting = () => Console.IsInputRedirected;
            IndexCommandRunner.ReadLineForTesting = Console.ReadLine;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithChangedBetweenMissingRef_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(projectRoot);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "HEAD", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("--changed-between requires exactly two refs", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithChangedBetweenInvalidRef_ReturnsUsageError_3046()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "HEAD", "missing-ref", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("failed to resolve changed files between git refs", json.GetProperty("message").GetString());
            Assert.Contains("cdidx index <projectPath> --changed-between <old-ref> <new-ref>", json.GetProperty("hint").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRunWithInvalidCommitRange_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "tracked.cs"), "class Sample {}\n");
            RunGit(projectRoot, "add", "tracked.cs");
            RunGit(projectRoot, "commit", "-m", "initial");
            File.WriteAllText(Path.Combine(projectRoot, "other.cs"), "class Other {}\n");
            RunGit(projectRoot, "add", "other.cs");
            RunGit(projectRoot, "commit", "-m", "other");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--commits", "HEAD~1..HEAD", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("ranges and tag refs are not accepted", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_DryRun_IgnoresUnixFifoWithoutHanging()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            CreateUnixFifo(Path.Combine(projectRoot, "tool"));
            CreateUnixFifo(Path.Combine(projectRoot, "tool.sh"));
            CreateUnixFifo(Path.Combine(projectRoot, "Dockerfile"));

            var result = RunCliInSubprocessWithTimeout([projectRoot, "--dry-run", "--json"], projectRoot, TimeSpan.FromSeconds(10));

            Assert.False(result.TimedOut, "cdidx index --dry-run hung on a FIFO entry.");
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);

            using var document = JsonDocument.Parse(result.StdOut);
            Assert.Equal("dry_run", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("files_total").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_JsonCapsFileSamples()
    {
        var projectRoot = CreateTempProject();
        var fileCount = IndexCommandRunner.DryRunFileSampleLimit + 3;
        try
        {
            foreach (var i in Enumerable.Range(0, fileCount))
                File.WriteAllText(Path.Combine(projectRoot, $"sample{i:D3}.cs"), $"public class Sample{i} {{ }}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(fileCount, json.GetProperty("files_total").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("languages").GetProperty("csharp").GetInt32());
            Assert.Equal(IndexCommandRunner.DryRunFileSampleLimit, json.GetProperty("file_sample_limit").GetInt32());
            Assert.Equal(IndexCommandRunner.DefaultDryRunPathLimit, json.GetProperty("candidate_path_limit").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("candidate_paths_processed").GetInt32());
            Assert.False(json.GetProperty("candidate_paths_truncated").GetBoolean());
            Assert.False(json.GetProperty("totals_lower_bound").GetBoolean());
            Assert.True(json.GetProperty("file_samples_truncated").GetBoolean());
            Assert.Equal(IndexCommandRunner.DryRunFileSampleLimit, json.GetProperty("file_samples").GetArrayLength());
            Assert.Equal(0, json.GetProperty("errors_total").GetInt32());
            Assert.False(json.GetProperty("errors_truncated").GetBoolean());
            Assert.Equal(IndexCommandRunner.DryRunParseEstimateFileLimit, json.GetProperty("parse_estimate_file_limit").GetInt32());
            Assert.Equal(IndexCommandRunner.DryRunParseEstimateFileLimit, json.GetProperty("parse_estimate_files_processed").GetInt32());
            Assert.True(json.GetProperty("parse_estimate_files_truncated").GetBoolean());
            Assert.Equal(
                JsonValueKind.Null,
                json.GetProperty("estimated_table_mutations").GetProperty("chunks").ValueKind);
            Assert.Contains(
                "parse_estimate_file_limit_reached",
                json.GetProperty("estimated_table_mutation_details")
                    .GetProperty("chunks")
                    .GetProperty("unknown_reasons")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_PathLimitTruncatesCandidateProcessing()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "sample001.cs"), "public class Sample001 { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "sample002.cs"), "public class Sample002 { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "sample003.cs"), "public class Sample003 { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--dry-run-path-limit", "2", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(2, json.GetProperty("files_total").GetInt32());
            Assert.Equal(2, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(2, json.GetProperty("candidate_path_limit").GetInt32());
            Assert.Equal(2, json.GetProperty("candidate_paths_processed").GetInt32());
            Assert.True(json.GetProperty("candidate_paths_truncated").GetBoolean());
            Assert.True(json.GetProperty("totals_lower_bound").GetBoolean());
            Assert.True(json.GetProperty("file_samples_truncated").GetBoolean());
            Assert.Equal(0, json.GetProperty("errors_total").GetInt32());
            Assert.Equal(
                JsonValueKind.Null,
                json.GetProperty("estimated_table_mutations").GetProperty("symbols").ValueKind);
            var detail = json
                .GetProperty("estimated_table_mutation_details")
                .GetProperty("symbols");
            Assert.Equal("unknown", detail.GetProperty("confidence").GetString());
            Assert.Contains(
                "candidate_path_limit_reached",
                detail.GetProperty("unknown_reasons").EnumerateArray().Select(value => value.GetString()));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_JsonCapsErrorSamples()
    {
        var projectRoot = CreateTempProject();
        var fileCount = IndexCommandRunner.DryRunErrorSampleLimit + 3;
        try
        {
            foreach (var i in Enumerable.Range(0, fileCount))
                File.WriteAllText(Path.Combine(projectRoot, $"large{i:D3}.cs"), $"public class Large{i} {{ }}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--max-file-bytes", "1", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(fileCount, json.GetProperty("files_total").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("projected_policy_skips").GetInt32());
            Assert.Equal(0, json.GetProperty("parse_estimate_files_processed").GetInt32());
            var mutations = json.GetProperty("estimated_table_mutations");
            Assert.Equal(fileCount, mutations.GetProperty("files").GetInt32());
            Assert.Equal(0, mutations.GetProperty("chunks").GetInt32());
            Assert.Equal(0, mutations.GetProperty("symbols").GetInt32());
            Assert.Equal(0, mutations.GetProperty("symbol_references").GetInt32());
            Assert.Equal(0, mutations.GetProperty("reference_lines").GetInt32());
            Assert.Equal(fileCount, mutations.GetProperty("file_issues").GetInt32());
            Assert.Equal(fileCount, json.GetProperty("errors_total").GetInt32());
            Assert.Equal(IndexCommandRunner.DryRunErrorSampleLimit, json.GetProperty("error_limit").GetInt32());
            Assert.True(json.GetProperty("errors_truncated").GetBoolean());
            Assert.Equal(IndexCommandRunner.DryRunErrorSampleLimit, json.GetProperty("errors").GetArrayLength());
            Assert.Equal(IndexCommandRunner.DryRunFileSampleLimit, json.GetProperty("file_sample_limit").GetInt32());
            Assert.True(json.GetProperty("file_samples_truncated").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_EstimatesParseOnlyMutationsAndCapOutcomes_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                """
                public class App
                {
                    public void First()
                    {
                        Second();
                        Third();
                    }

                    public void Second() { }
                    public void Third() { }
                }
                """);

            var (humanExitCode, humanOutput, _) = RunAndCaptureStreams([projectRoot, "--dry-run"]);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("projected updates", humanOutput);
            Assert.Contains("estimated chunks", humanOutput);
            Assert.Contains("estimated symbols", humanOutput);
            Assert.Contains("parse_only_and_index_snapshot", humanOutput);
            Assert.Contains("estimate", humanOutput);

            var (normalExitCode, normal) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);
            Assert.Equal(CommandExitCodes.Success, normalExitCode);
            Assert.Equal(1, normal.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, normal.GetProperty("projected_file_skips").GetInt32());
            Assert.Equal(0, normal.GetProperty("projected_symbol_cap_hits").GetInt32());
            Assert.Equal(0, normal.GetProperty("projected_reference_cap_hits").GetInt32());
            var normalMutations = normal.GetProperty("estimated_table_mutations");
            Assert.True(normalMutations.GetProperty("chunks").GetInt64() > 0);
            Assert.True(normalMutations.GetProperty("symbols").GetInt64() > 1);
            Assert.True(normalMutations.GetProperty("symbol_references").GetInt64() > 1);
            Assert.True(normalMutations.GetProperty("reference_lines").GetInt64() > 0);

            var (symbolCapExitCode, symbolCap) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--max-symbols-per-file",
                "1",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, symbolCapExitCode);
            Assert.Equal(1, symbolCap.GetProperty("projected_symbol_cap_hits").GetInt32());
            var symbolCapMutations = symbolCap.GetProperty("estimated_table_mutations");
            Assert.Equal(0, symbolCapMutations.GetProperty("chunks").GetInt32());
            Assert.Equal(0, symbolCapMutations.GetProperty("symbols").GetInt32());
            Assert.Equal(0, symbolCapMutations.GetProperty("symbol_references").GetInt32());
            Assert.True(symbolCapMutations.GetProperty("file_issues").GetInt32() > 0);

            var (referenceCapExitCode, referenceCap) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--max-references-per-file",
                "1",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, referenceCapExitCode);
            Assert.Equal(1, referenceCap.GetProperty("projected_reference_cap_hits").GetInt32());
            var referenceCapMutations = referenceCap.GetProperty("estimated_table_mutations");
            Assert.Equal(0, referenceCapMutations.GetProperty("symbol_references").GetInt32());
            Assert.Equal(0, referenceCapMutations.GetProperty("reference_lines").GetInt32());
            Assert.True(referenceCapMutations.GetProperty("file_issues").GetInt32() > 0);

            Assert.False(Directory.Exists(Path.Combine(projectRoot, ".cdidx")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_ReestimatesUnchangedFileWithPersistedCapIssue_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                """
                public class App
                {
                    public void First() { }
                    public void Second() { }
                }
                """);
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--max-symbols-per-file",
                "1",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--max-symbols-per-file",
                "1",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_skips").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_symbol_cap_hits").GetInt32());
            Assert.Equal(
                0,
                json.GetProperty("estimated_table_mutations")
                    .GetProperty("symbols")
                    .GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_ReusesUnchangedIndexedBinaryFile_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllBytes(
                Path.Combine(projectRoot, "binary.cs"),
                [0x70, 0x00, 0x71]);
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var dbPath = Path.Combine(
                projectRoot,
                ".cdidx",
                "codeindex.db");
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(dbPath);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(0, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_skips").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_policy_skips").GetInt32());
            Assert.Equal(0, json.GetProperty("errors_total").GetInt32());
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(dbPath));

            var (refreshExitCode, refreshJson) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, refreshExitCode);
            Assert.Equal(
                1,
                refreshJson.GetProperty("summary")
                    .GetProperty("files_skipped")
                    .GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_DoesNotReuseAcrossFilterOrExtractorContractChange_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { public void Run() { } }\n");
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var (filterExitCode, filterJson) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--include-symbol-kind",
                "class",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, filterExitCode);
            Assert.Equal(
                1,
                filterJson.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(
                0,
                filterJson.GetProperty("projected_file_skips").GetInt32());

            var dbPath = Path.Combine(
                projectRoot,
                ".cdidx",
                "codeindex.db");
            using (var connection = new SqliteConnection(
                $"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO codeindex_meta(key, value)
                    VALUES (@key, 'stale')
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value
                    """;
                command.Parameters.AddWithValue(
                    "@key",
                    DbContext.GetSymbolExtractorVersionMetaKey("csharp"));
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (contractExitCode, contractJson) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, contractExitCode);
            Assert.Equal(
                1,
                contractJson.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(
                0,
                contractJson.GetProperty("projected_file_skips").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_RejectsScopedSymbolFilterChange_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { }\n");
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "app.cs",
                "--dry-run",
                "--include-symbol-kind",
                "class",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains(
                "symbol-kind filter policy cannot change",
                json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_ExtractorConfigCommitForcesFullRefresh_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "initial");
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            var patternDirectory = Path.Combine(
                projectRoot,
                ".cdidx",
                "patterns");
            Directory.CreateDirectory(patternDirectory);
            File.WriteAllText(
                Path.Combine(patternDirectory, "custom.json"),
                "{}\n");
            RunGit(
                projectRoot,
                "add",
                "-f",
                ".cdidx/patterns/custom.json");
            RunGit(projectRoot, "commit", "-m", "change extractor config");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--commits",
                "HEAD",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(
                1,
                json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(
                0,
                json.GetProperty("projected_file_skips").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_CSharpContractRefreshesCrossFileSkips_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "contract.cs"),
                "public interface IContract<T> { }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "consumer.cs"),
                "public class Consumer { }\n");
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            File.WriteAllText(
                Path.Combine(projectRoot, "contract.cs"),
                """
                public interface IContract<T>
                    where T : IContract<T>
                {
                    static abstract T Parse(string value);
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(
                2,
                json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(
                0,
                json.GetProperty("projected_file_skips").GetInt32());
            var symbolDetails = json
                .GetProperty("estimated_table_mutation_details")
                .GetProperty("symbols");
            Assert.Equal(
                "unknown",
                symbolDetails.GetProperty("confidence").GetString());
            Assert.Contains(
                "csharp_workspace_augmentation_required",
                symbolDetails.GetProperty("unknown_reasons")
                    .EnumerateArray()
                    .Select(static value => value.GetString()));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_HotspotMarkerChangeInvalidatesFamilyReuse_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "one.targets"),
                "<Project />\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "two.targets"),
                "<Project />\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "sample.csproj"),
                "<Project />\n");
            var (indexExitCode, _) = RunAndCaptureJson([
                projectRoot,
                "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, indexExitCode);

            File.WriteAllText(
                Path.Combine(projectRoot, "second.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(
                4,
                json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(
                0,
                json.GetProperty("projected_file_skips").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_SnapshotReadFailureReturnsExplicitUnknown_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "app.cs"),
                "public class App { }\n");
            var indexDirectory = Path.Combine(projectRoot, ".cdidx");
            Directory.CreateDirectory(indexDirectory);
            var dbPath = Path.Combine(indexDirectory, "codeindex.db");
            File.WriteAllText(dbPath, "not a sqlite database\n");
            var before = File.ReadAllBytes(dbPath);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            foreach (var metric in new[]
                     {
                         "files",
                         "chunks",
                         "symbols",
                         "symbol_references",
                         "reference_lines",
                         "file_issues",
                     })
            {
                Assert.Equal(
                    JsonValueKind.Null,
                    json.GetProperty("estimated_table_mutations")
                        .GetProperty(metric)
                        .ValueKind);
                Assert.Contains(
                    "index_snapshot_unavailable",
                    json.GetProperty("estimated_table_mutation_details")
                        .GetProperty(metric)
                        .GetProperty("unknown_reasons")
                        .EnumerateArray()
                        .Select(static value => value.GetString()));
            }
            Assert.Equal(before, File.ReadAllBytes(dbPath));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_ParseEstimateFailureReturnsExplicitUnknown_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            IndexCommandRunner.DryRunParseEstimateFailureForTesting = static _ =>
                new InvalidOperationException("injected parse-only estimate failure");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("parse_estimate_files_processed").GetInt32());
            Assert.Equal(
                JsonValueKind.Null,
                json.GetProperty("estimated_table_mutations").GetProperty("symbols").ValueKind);
            var detail = json
                .GetProperty("estimated_table_mutation_details")
                .GetProperty("symbols");
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("value").ValueKind);
            Assert.Equal("unknown", detail.GetProperty("confidence").GetString());
            Assert.Contains(
                "parse_estimation_failed",
                detail.GetProperty("unknown_reasons").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(1, json.GetProperty("errors_total").GetInt32());
            Assert.Contains(
                "Parse-only mutation estimate unavailable",
                json.GetProperty("errors")[0].GetProperty("message").GetString());
            Assert.False(Directory.Exists(Path.Combine(projectRoot, ".cdidx")));
        }
        finally
        {
            IndexCommandRunner.DryRunParseEstimateFailureForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_PartialIndexSchemaReturnsExplicitUnknownWithoutMutation_Issue4893()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var indexDirectory = Path.Combine(projectRoot, ".cdidx");
            Directory.CreateDirectory(indexDirectory);
            var dbPath = Path.Combine(indexDirectory, "codeindex.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY,
                        path TEXT NOT NULL,
                        checksum TEXT
                    );
                    INSERT INTO files(id, path, checksum) VALUES (1, 'app.cs', 'stale');
                    """;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var before = File.ReadAllBytes(dbPath);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--db",
                dbPath,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(1, json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt32());
            Assert.Equal(
                JsonValueKind.Null,
                json.GetProperty("estimated_table_mutations").GetProperty("chunks").ValueKind);
            var detail = json
                .GetProperty("estimated_table_mutation_details")
                .GetProperty("chunks");
            Assert.Equal("unknown", detail.GetProperty("confidence").GetString());
            Assert.Contains(
                "existing_table_unavailable",
                detail.GetProperty("unknown_reasons").EnumerateArray().Select(value => value.GetString()));
            SqliteConnection.ClearAllPools();
            Assert.Equal(before, File.ReadAllBytes(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsProjectedUpdatesDeletesAndUnknowns()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "changed.cs"), "public class Changed { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "deleted.cs"), "public class Deleted { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(2, CountRows(dbPath, "files"));

            File.AppendAllText(Path.Combine(projectRoot, "changed.cs"), "public class ChangedAgain { }\n");
            File.Delete(Path.Combine(projectRoot, "deleted.cs"));
            File.WriteAllText(Path.Combine(projectRoot, "notes.unknownext"), "plain text\n");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "changed.cs",
                "deleted.cs",
                "notes.unknownext",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("estimates").GetBoolean());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(1, json.GetProperty("unknown_extension_total").GetInt32());
            Assert.Equal(0, json.GetProperty("unsupported_total").GetInt32());
            var mutations = json.GetProperty("estimated_table_mutations");
            Assert.True(mutations.GetProperty("files").GetInt64() >= 2);
            Assert.True(mutations.GetProperty("chunks").GetInt64() > 0);
            Assert.True(mutations.GetProperty("symbols").GetInt64() > 0);
            Assert.True(mutations.TryGetProperty("file_issues", out _));
            Assert.Equal(2, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_HeaderLanguageDetectionExposesSourceAndConfidence_Issue4608()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "ambiguous.h"),
                "/* class CommentOnly {}; */\nstruct real_c_type { int value; };\n");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "ambiguous.h",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("language_detections_total").GetInt32());
            Assert.False(json.GetProperty("language_detections_truncated").GetBoolean());
            Assert.Equal(IndexCommandRunner.DryRunLanguageDetectionLimit, json.GetProperty("language_detection_limit").GetInt32());
            var detection = Assert.Single(json.GetProperty("language_detections").EnumerateArray().ToArray());
            Assert.Equal("ambiguous.h", detection.GetProperty("path").GetString());
            Assert.Equal("c", detection.GetProperty("language").GetString());
            Assert.Equal("header_lexical_fallback", detection.GetProperty("source").GetString());
            Assert.Equal("low", detection.GetProperty("confidence").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(
        "Widget.M",
        "#import <Foundation/Foundation.h>\n@interface Widget : NSObject\n@end\n",
        "objc",
        "content")]
    [InlineData(
        "tool.M",
        "#!/usr/bin/env ruby\nputs 'hi'\n",
        "ruby",
        "shebang")]
    public void Run_DryRun_AmbiguousExtensionReportsSelectedLanguageAndSharedReason_Issue4901(
        string fileName,
        string content,
        string expectedLanguage,
        string expectedSource)
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, fileName),
                content);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                fileName,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(1, json.GetProperty("language_detections_total").GetInt32());
            var detection = Assert.Single(json.GetProperty("language_detections").EnumerateArray().ToArray());
            Assert.Equal(fileName, detection.GetProperty("path").GetString());
            Assert.Equal(expectedLanguage, detection.GetProperty("language").GetString());
            Assert.Equal(expectedSource, detection.GetProperty("source").GetString());
            Assert.Equal("high", detection.GetProperty("confidence").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScanPreservesBoundedAmbiguousProjectReason_Issue4901()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "App.xcodeproj"), string.Empty);
            var contentBeyondProbe = new string(' ', FileIndexer.AmbiguousLanguageProbeByteLimit + 1024);
            File.WriteAllText(
                Path.Combine(projectRoot, "Widget.M"),
                contentBeyondProbe + "\n#import <Foundation/Foundation.h>\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "late.M"),
                contentBeyondProbe + "\nfunction result = late()\nend\n");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(2, json.GetProperty("language_detections_total").GetInt32());
            var detections = json.GetProperty("language_detections").EnumerateArray()
                .OrderBy(detection => detection.GetProperty("path").GetString(), StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["Widget.M", "late.M"], detections.Select(detection => detection.GetProperty("path").GetString()));
            Assert.All(detections, detection =>
            {
                Assert.Equal("objc", detection.GetProperty("language").GetString());
                Assert.Equal("project", detection.GetProperty("source").GetString());
                Assert.Equal("medium", detection.GetProperty("confidence").GetString());
            });
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_NormalizesUnicodeDbPathForEstimates()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var nfdFileName = "cafe\u0301.py";
            File.WriteAllText(Path.Combine(projectRoot, nfdFileName), "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", nfdFileName, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", nfdFileName, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_skips").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(0, json.GetProperty("estimated_table_mutations").GetProperty("chunks").GetInt64());
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_SymbolKindFiltersAdjustSymbolEstimateAndExposeResolvedPolicy_4470()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "sample.cs"),
                "public class Sample { public void First() { } public void Second() { } }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (_, unfiltered) = RunAndCaptureJson([projectRoot, "--rebuild", "--dry-run", "--json"]);
            var (exitCode, filtered) = RunAndCaptureJson([
                projectRoot,
                "--rebuild",
                "--dry-run",
                "--include-symbol-kind",
                "method,class,method",
                "--exclude-symbol-kind",
                "class",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var unfilteredMutations = unfiltered.GetProperty("estimated_table_mutations");
            var filteredMutations = filtered.GetProperty("estimated_table_mutations");
            Assert.True(filteredMutations.GetProperty("symbols").GetInt64() < unfilteredMutations.GetProperty("symbols").GetInt64());
            Assert.Equal(
                JsonValueKind.Number,
                filteredMutations.GetProperty("symbol_references").ValueKind);
            Assert.True(filtered.GetProperty("symbols_dropped_by_kind_filter").GetInt64() > 0);
            var policy = filtered.GetProperty("symbol_kind_filter");
            Assert.Equal(["class", "method"], policy.GetProperty("include").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal(["class"], policy.GetProperty("exclude").EnumerateArray().Select(value => value.GetString()).ToArray());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsChecksumRenamePurgeWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "old.py");
            var newPath = Path.Combine(projectRoot, "new.py");
            File.WriteAllText(oldPath, "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "old.py", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.Move(oldPath, newPath);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "new.py", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 2);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsSupportedExtensionRenamePurgeWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.md");
            File.WriteAllText(oldPath, "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.py", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.Move(oldPath, newPath);
            File.AppendAllText(newPath, "# Updated during rename\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "foo.md", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 2);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_ReportsUnsupportedExtensionRenamePurgeWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.bin");
            File.WriteAllText(oldPath, "print('hello')\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.py", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(1, CountRows(dbPath, "files"));

            File.Move(oldPath, newPath);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "foo.bin", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.True(json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64() >= 1);
            Assert.Equal(1, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScan_ReportsProjectedPurgesWithoutWriting()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "kept.cs"), "public class Kept { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "removed.cs"), "public class Removed { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            File.Delete(Path.Combine(projectRoot, "removed.cs"));
            File.WriteAllText(Path.Combine(projectRoot, "notes.unknownext"), "plain text\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("files_total").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_skips").GetInt32());
            Assert.Equal(0, json.GetProperty("projected_file_deletes").GetInt32());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(1, json.GetProperty("unknown_extension_total").GetInt32());
            Assert.True(json.TryGetProperty("unsupported_total", out _));
            Assert.Equal(1, json.GetProperty("estimated_table_mutations").GetProperty("files").GetInt64());
            Assert.Equal(2, CountRows(dbPath, "files"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScan_ReportsUnreadableDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal("secret", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not scan directory due to permissions.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var (humanExitCode, _, stderr) = RunAndCaptureStreams([projectRoot, "--dry-run"]);
            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("secret", stderr);
            Assert.Contains("Could not scan directory due to permissions.", stderr);
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_FullScan_DoesNotProjectUnreadableSubtreePurge()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var secretDir = Path.Combine(projectRoot, "secret");
        try
        {
            Directory.CreateDirectory(secretDir);
            File.WriteAllText(Path.Combine(secretDir, "a.cs"), "public class A { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "stale.cs"), "public class Stale { }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(2, CountRows(dbPath, "files"));

            File.Delete(Path.Combine(projectRoot, "stale.cs"));
            SetUnixPermissions(secretDir, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("projected_file_purges").GetInt32());
            Assert.Equal(2, CountRows(dbPath, "files"));
        }
        finally
        {
            if (Directory.Exists(secretDir))
                SetUnixPermissions(secretDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_DryRun_WithFiles_IgnoresUnixFifoKnownFilename()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            CreateUnixFifo(Path.Combine(projectRoot, "Dockerfile"));

            var result = RunCliInSubprocessWithTimeout([projectRoot, "--files", "Dockerfile", "--dry-run", "--json"], projectRoot, TimeSpan.FromSeconds(10));

            Assert.False(result.TimedOut, "cdidx index --dry-run --files hung on a FIFO entry.");
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);

            using var document = JsonDocument.Parse(result.StdOut);
            Assert.Equal("dry_run", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("files_total").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_RejectsAbsolutePathOutsideProjectRoot_4471()
    {
        var projectRoot = CreateTempProject();
        var outsidePath = Path.Combine(Path.GetTempPath(), $"cdidx_dryrun_outside_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(outsidePath, "public class Outside { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", outsidePath, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("none of the paths supplied to --files resolved", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteFile(outsidePath);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_RejectsTraversalOutsideProjectRoot_4471()
    {
        var parentDir = TestProjectHelper.CreateTempProject("cdidx_dryrun_parent");
        var projectRoot = Path.Combine(parentDir, "project");
        var outsidePath = Path.Combine(parentDir, "outside.cs");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(outsidePath, "public class Outside { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "../outside.cs", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("none of the paths supplied to --files resolved", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(parentDir);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_RejectsNonexistentUnindexedProjectPath_4471()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "missing.cs",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("none of the paths supplied to --files resolved", json.GetProperty("message").GetString());
            Assert.Contains("--files <path>", json.GetProperty("hint").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_RejectsNonexistentUnindexedIgnoreFile_4471()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "sample.cs"), "public class Sample { }\n");

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "missing/.gitignore",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("none of the paths supplied to --files resolved", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_RejectsSymlinkSkippedByPolicy_4471()
    {
        var projectRoot = CreateTempProject();
        var outsidePath = Path.Combine(Path.GetTempPath(), $"cdidx_dryrun_symlink_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(outsidePath, "public class Outside { }\n");
            try
            {
                File.CreateSymbolicLink(Path.Combine(projectRoot, "link.cs"), outsidePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "link.cs",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("none of the paths supplied to --files resolved", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(outsidePath);
        }
    }

    [Fact]
    public void Run_DryRunAndFullScan_FollowSymlinksAllAgreeForExternalFileLink_Issue4829()
    {
        var projectRoot = CreateTempProject();
        var outsideRoot = CreateTempProject();
        try
        {
            var targetPath = Path.Combine(outsideRoot, "Outside.cs");
            var linkPath = Path.Combine(projectRoot, "OutsideLink.cs");
            File.WriteAllText(targetPath, "public class Outside4829 { }\n");
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var (dryRunExitCode, dryRunJson) = RunAndCaptureJson([
                projectRoot,
                "--follow-symlinks",
                "all",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, dryRunExitCode);
            Assert.Equal(1, dryRunJson.GetProperty("files_total").GetInt32());
            Assert.Equal(1, dryRunJson.GetProperty("projected_file_updates").GetInt32());
            Assert.Equal(0, dryRunJson.GetProperty("warnings_total").GetInt32());
            Assert.Equal(0, dryRunJson.GetProperty("errors_total").GetInt32());
            Assert.Equal(1, dryRunJson.GetProperty("languages").GetProperty("csharp").GetInt32());

            var (indexExitCode, indexJson) = RunAndCaptureJson([
                projectRoot,
                "--follow-symlinks",
                "all",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal("success", indexJson.GetProperty("status").GetString());
            Assert.Equal(1, indexJson.GetProperty("summary").GetProperty("files_total").GetInt32());
            Assert.Equal(0, indexJson.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Contains(
                "OutsideLink.cs",
                ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
            DeleteDirectory(outsideRoot);
        }
    }

    [Fact]
    public void Run_DryRunAndFullScan_ClassifyDanglingSymlinkAsWarning_Issue4829()
    {
        var projectRoot = CreateTempProject();
        try
        {
            try
            {
                File.CreateSymbolicLink(
                    Path.Combine(projectRoot, "Dangling.cs"),
                    Path.Combine(projectRoot, "Missing.cs"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var (dryRunExitCode, dryRunJson) = RunAndCaptureJson([
                projectRoot,
                "--follow-symlinks",
                "all",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, dryRunExitCode);
            Assert.Equal(0, dryRunJson.GetProperty("errors_total").GetInt32());
            Assert.Equal(1, dryRunJson.GetProperty("warnings_total").GetInt32());
            Assert.Contains(
                "dangling symlink",
                Assert.Single(dryRunJson.GetProperty("warnings").EnumerateArray())
                    .GetProperty("message")
                    .GetString(),
                StringComparison.OrdinalIgnoreCase);

            var (indexExitCode, indexJson) = RunAndCaptureJson([
                projectRoot,
                "--follow-symlinks",
                "all",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(0, indexJson.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, indexJson.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Contains(
                "dangling symlink",
                Assert.Single(indexJson.GetProperty("warnings").EnumerateArray())
                    .GetProperty("message")
                    .GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_AllowsDeletedIndexedProjectPath_4471()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "deleted.cs");
            File.WriteAllText(sourcePath, "public class Deleted { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            File.Delete(sourcePath);

            var (exitCode, json) = RunAndCaptureJson([
                projectRoot,
                "--files",
                "deleted.cs",
                "--dry-run",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("projected_file_deletes").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_WithFiles_DoesNotCountUnreadableKnownExtensionFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            File.WriteAllText(sourcePath, "public class A { }\n");
            SetUnixPermissions(sourcePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("files_total").GetInt32());
            Assert.Equal("a.cs", json.GetProperty("errors")[0].GetProperty("file").GetString());
        }
        finally
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            if (File.Exists(sourcePath))
                SetUnixPermissions(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_DryRun_DoesNotAcquireLock()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_lock_dryrun_{Guid.NewGuid():N}.db");
        var lockPath = dbPath + ".lock";
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hi')\n");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

            // Hold the lockfile while running --dry-run to prove dry-run never tries to acquire.
            using (var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--dry-run", "--json"]);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("dry_run", json.GetProperty("status").GetString());
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
            DeleteFile(lockPath);
        }
    }
}
