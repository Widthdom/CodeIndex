using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Reflection;
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
    public void Run_UpdateMode_AmbiguousProjectMarkerChangeReclassifiesExistingFiles_Issue4612()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "source.pl"), string.Empty);
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal("ambiguous_pl", ReadLanguage("source.pl"));

            File.WriteAllText(Path.Combine(projectRoot, "Makefile.PL"), string.Empty);
            var (updateExitCode, _) = RunAndCaptureJson(
                [projectRoot, "--files", "Makefile.PL", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("perl", ReadLanguage("source.pl"));

            string? ReadLanguage(string path)
            {
                SqliteConnection.ClearAllPools();
                using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT lang FROM files WHERE path = @path";
                command.Parameters.AddWithValue("@path", path);
                return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_PreservesUnchangedWorkspacePluginReferences_Issue4602()
    {
        var projectRoot = TestProjectHelper.CreateExecutableExtensionTestProject("cdidx_index_update_plugin_4602");
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable);
            try
            {
                ExtractorPluginRegistry.ResetForTests();
                env.Set(ExtractorPluginRegistry.TrustWorkspacePluginsEnvironmentVariable, "1");
                AssertUpdatePreservesUnchangedWorkspacePluginReferences(projectRoot);
            }
            finally
            {
                ExtractorPluginRegistry.ResetForTests();
                DeleteDirectory(projectRoot);
            }
        }

    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AssertUpdatePreservesUnchangedWorkspacePluginReferences(string projectRoot)
    {
        var pluginPath = Path.Combine(projectRoot, ".cdidx", "plugins", "workspace-plugin.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
        TestProjectHelper.CopyAssemblyFixtureWithDependencies(Assembly.GetExecutingAssembly().Location, pluginPath);
        File.WriteAllText(Path.Combine(projectRoot, "stable.collectible"), "workspace reference\n");
        var changedPath = Path.Combine(projectRoot, "changed.cs");
        File.WriteAllText(changedPath, "public class Changed { }\n");

        var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json", "--force"]);
        Assert.Equal(CommandExitCodes.Success, initialExitCode);
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        Assert.Equal(1, ReadWorkspacePluginReferenceCount(dbPath));

        File.WriteAllText(changedPath, "public class Changed { public void Updated() { } }\n");
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(2));
        var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "changed.cs", "--json"]);

        Assert.Equal(CommandExitCodes.Success, updateExitCode);
        Assert.Equal(1, ReadWorkspacePluginReferenceCount(dbPath));
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.TryMigrateForRead();
            var reader = new DbReader(db.Connection, db.IsReadOnly);
            var reference = Assert.Single(reader.SearchReferences(
                "WorkspacePluginTarget",
                lang: "collectibledsl",
                exact: true));
            Assert.Equal("stable.collectible", reference.Path);
        }
        var (statusExitCode, status) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
        Assert.Equal(CommandExitCodes.Success, statusExitCode);
        Assert.Contains(
            status.GetProperty("graph_supported_languages").EnumerateArray(),
            language => language.GetString() == "collectibledsl");
        Assert.Equal(1, ExtractorPluginRegistry.WorkspacePluginWorkerCountForTests(projectRoot));
    }

    private static int ReadWorkspacePluginReferenceCount(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM symbol_references r
            JOIN files f ON f.id = r.file_id
            WHERE f.lang = 'collectibledsl'
              AND r.symbol_name = 'WorkspacePluginTarget'
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Run_UpdateMode_NoOpRepairsMissingReferenceIdentityContract()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                "public class Caller { public void Run() { Target.Execute(); } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "Target.cs"),
                "public static class Target { public static void Execute() { } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM symbol_reference_candidates;
                    UPDATE symbol_references
                    SET target_symbol_id = NULL,
                        target_symbol_key = NULL,
                        resolution_state = NULL,
                        resolution_candidate_count = 0;
                    DELETE FROM codeindex_meta WHERE key = @key;
                    """;
                command.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
                command.ExecuteNonQuery();
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Caller.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verification = new SqliteConnection($"Data Source={dbPath}");
            verification.Open();
            using var markerCommand = verification.CreateCommand();
            markerCommand.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            markerCommand.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
            Assert.Equal(
                DbContext.ReferenceIdentityContractVersion.ToString(CultureInfo.InvariantCulture),
                Convert.ToString(markerCommand.ExecuteScalar(), CultureInfo.InvariantCulture));

            using var candidateCommand = verification.CreateCommand();
            candidateCommand.CommandText = "SELECT COUNT(*) FROM symbol_reference_candidates";
            Assert.True(Convert.ToInt32(candidateCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpRepairsPriorReferenceIdentityContract_Issue4845()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                "public class Caller { public void Run() { Target.Execute(); } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "Target.cs"),
                "public static class Target { public static void Execute() { } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM symbol_reference_candidates;
                    UPDATE symbol_references
                    SET target_symbol_id = NULL,
                        target_symbol_key = NULL,
                        resolution_state = NULL,
                        resolution_candidate_count = 0;
                    UPDATE codeindex_meta
                    SET value = @priorVersion
                    WHERE key = @key;
                    """;
                command.Parameters.AddWithValue(
                    "@priorVersion",
                    (DbContext.ReferenceIdentityContractVersion - 1).ToString(CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
                command.ExecuteNonQuery();
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Caller.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verification = new SqliteConnection($"Data Source={dbPath}");
            verification.Open();
            using var markerCommand = verification.CreateCommand();
            markerCommand.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            markerCommand.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
            Assert.Equal(
                DbContext.ReferenceIdentityContractVersion.ToString(CultureInfo.InvariantCulture),
                Convert.ToString(markerCommand.ExecuteScalar(), CultureInfo.InvariantCulture));

            using var candidateCommand = verification.CreateCommand();
            candidateCommand.CommandText = """
                SELECT COUNT(*)
                FROM symbol_reference_candidates candidate
                JOIN symbol_references reference ON reference.id = candidate.reference_id
                WHERE reference.resolution_state IN ('resolved', 'resolved_group')
                """;
            Assert.True(Convert.ToInt32(candidateCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpRepairsVersion4MarkdownCandidates_Issue4846()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "source.md"),
                "# Source\n\n[target](target.md#error-codes)\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "target.md"),
                "# Error codes\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "unrelated.md"),
                "# Error codes\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(8, DbContext.ReferenceIdentityContractVersion);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM symbol_reference_candidates
                    WHERE reference_id = (
                        SELECT r.id
                        FROM symbol_references AS r
                        JOIN files AS source_file ON source_file.id = r.file_id
                        WHERE source_file.path = 'source.md'
                          AND r.reference_kind = 'reference'
                          AND r.symbol_name = 'error-codes'
                    );

                    INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
                    SELECT r.id, target.id, 0
                    FROM symbol_references AS r
                    JOIN files AS source_file ON source_file.id = r.file_id
                    CROSS JOIN symbols AS target
                    JOIN files AS target_file ON target_file.id = target.file_id
                    WHERE source_file.path = 'source.md'
                      AND r.reference_kind = 'reference'
                      AND r.symbol_name = 'error-codes'
                      AND target.kind = 'heading'
                      AND target.name_folded = 'error-codes'
                      AND target_file.path IN ('target.md', 'unrelated.md');

                    UPDATE symbol_references
                    SET target_symbol_id = NULL,
                        target_symbol_key = NULL,
                        resolution_state = 'ambiguous',
                        resolution_candidate_count = 2
                    WHERE id = (
                        SELECT r.id
                        FROM symbol_references AS r
                        JOIN files AS source_file ON source_file.id = r.file_id
                        WHERE source_file.path = 'source.md'
                          AND r.reference_kind = 'reference'
                          AND r.symbol_name = 'error-codes'
                    );

                    UPDATE codeindex_meta
                    SET value = '4'
                    WHERE key = @key;
                    """;
                command.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
                command.ExecuteNonQuery();
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "source.md", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verification = new SqliteConnection($"Data Source={dbPath}");
            verification.Open();
            using var markerCommand = verification.CreateCommand();
            markerCommand.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            markerCommand.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
            Assert.Equal("8", Convert.ToString(markerCommand.ExecuteScalar(), CultureInfo.InvariantCulture));

            using var resolutionCommand = verification.CreateCommand();
            resolutionCommand.CommandText = """
                SELECT target_file.path || '|' || reference.resolution_state || '|' ||
                       reference.resolution_candidate_count
                FROM symbol_references AS reference
                JOIN files AS source_file ON source_file.id = reference.file_id
                JOIN symbols AS target ON target.id = reference.target_symbol_id
                JOIN files AS target_file ON target_file.id = target.file_id
                WHERE source_file.path = 'source.md'
                  AND reference.reference_kind = 'reference'
                  AND reference.symbol_name = 'error-codes'
                """;
            Assert.Equal(
                "target.md|resolved|1",
                Convert.ToString(resolutionCommand.ExecuteScalar(), CultureInfo.InvariantCulture));

            using var candidateCommand = verification.CreateCommand();
            candidateCommand.CommandText = """
                SELECT target_file.path
                FROM symbol_reference_candidates AS candidate
                JOIN symbol_references AS reference ON reference.id = candidate.reference_id
                JOIN files AS source_file ON source_file.id = reference.file_id
                JOIN symbols AS target ON target.id = candidate.symbol_id
                JOIN files AS target_file ON target_file.id = target.file_id
                WHERE source_file.path = 'source.md'
                  AND reference.reference_kind = 'reference'
                  AND reference.symbol_name = 'error-codes'
                """;
            Assert.Equal(
                "target.md",
                Convert.ToString(candidateCommand.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpRepairsPriorConstructorIdentityContract_Issue4850()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "Caller.cs"),
                "public class Caller { public object Create() => new Target(); }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "Target.cs"),
                "public class Target { public Target() { } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM symbol_reference_candidates
                    WHERE reference_id IN (
                        SELECT id
                        FROM symbol_references
                        WHERE reference_kind = 'instantiate'
                          AND symbol_name = 'Target'
                    );
                    INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
                    SELECT reference.id, symbol.id, 5
                    FROM symbol_references AS reference
                    JOIN symbols AS symbol ON symbol.name = reference.symbol_name
                    WHERE reference.reference_kind = 'instantiate'
                      AND reference.symbol_name = 'Target';
                    UPDATE codeindex_meta
                    SET value = '5'
                    WHERE key = @key;
                    """;
                command.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
                command.ExecuteNonQuery();
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Caller.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());

            using var verification = new SqliteConnection($"Data Source={dbPath}");
            verification.Open();
            using var markerCommand = verification.CreateCommand();
            markerCommand.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            markerCommand.Parameters.AddWithValue("@key", DbContext.ReferenceIdentityContractVersionMetaKey);
            Assert.Equal(
                DbContext.ReferenceIdentityContractVersion.ToString(CultureInfo.InvariantCulture),
                Convert.ToString(markerCommand.ExecuteScalar(), CultureInfo.InvariantCulture));

            using var candidateCommand = verification.CreateCommand();
            candidateCommand.CommandText = """
                SELECT symbol.kind
                FROM symbol_reference_candidates AS candidate
                JOIN symbol_references AS reference ON reference.id = candidate.reference_id
                JOIN symbols AS symbol ON symbol.id = candidate.symbol_id
                WHERE reference.reference_kind = 'instantiate'
                  AND reference.symbol_name = 'Target'
                """;
            using var candidateReader = candidateCommand.ExecuteReader();
            var candidateKinds = new List<string>();
            while (candidateReader.Read())
                candidateKinds.Add(candidateReader.GetString(0));
            Assert.Equal(["function"], candidateKinds);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_TargetOnlyMarkdownAnchorChangesRefreshExactReferences_Issue4846()
    {
        var projectRoot = CreateTempProject();
        var previousScopeHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        var observedScopes = new List<DbWriter.ReferenceGraphRefreshScopeStats>();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "source.md"),
                "# Source\n\n[case](target.md#CaseID)\n[punctuation](target.md#api.v2)\n");
            var targetPath = Path.Combine(projectRoot, "target.md");
            File.WriteAllText(targetPath, "# Target\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            AssertMarkdownReferenceResolution(dbPath, "CaseID", "unresolved", 0);
            AssertMarkdownReferenceResolution(dbPath, "api.v2", "unresolved", 0);

            DbWriter.ReferenceGraphRefreshScopeForTesting = stats =>
            {
                observedScopes.Add(stats);
                previousScopeHook?.Invoke(stats);
            };

            File.WriteAllText(
                targetPath,
                "# Target\n\n<a id=\"CaseID\"></a>\n<a id=\"api.v2\"></a>\n");
            File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow.AddSeconds(2));
            var (addExitCode, addJson) = RunAndCaptureJson(
                [projectRoot, "--files", "target.md", "--json"]);

            Assert.Equal(CommandExitCodes.Success, addExitCode);
            Assert.Equal(1, addJson.GetProperty("summary").GetProperty("updated").GetInt32());
            var addScope = Assert.Single(observedScopes);
            Assert.False(addScope.UsedFullRefresh);
            Assert.Equal(2, addScope.DirtyReferenceCount);
            AssertMarkdownReferenceResolution(dbPath, "CaseID", "resolved", 1);
            AssertMarkdownReferenceResolution(dbPath, "api.v2", "resolved", 1);

            observedScopes.Clear();
            File.WriteAllText(targetPath, "# Target\n");
            File.SetLastWriteTimeUtc(targetPath, DateTime.UtcNow.AddSeconds(4));
            var (removeExitCode, removeJson) = RunAndCaptureJson(
                [projectRoot, "--files", "target.md", "--json"]);

            Assert.Equal(CommandExitCodes.Success, removeExitCode);
            Assert.Equal(1, removeJson.GetProperty("summary").GetProperty("updated").GetInt32());
            var removeScope = Assert.Single(observedScopes);
            Assert.False(removeScope.UsedFullRefresh);
            Assert.Equal(2, removeScope.DirtyReferenceCount);
            AssertMarkdownReferenceResolution(dbPath, "CaseID", "unresolved", 0);
            AssertMarkdownReferenceResolution(dbPath, "api.v2", "unresolved", 0);
        }
        finally
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousScopeHook;
            DeleteDirectory(projectRoot);
        }
    }

    private static void AssertMarkdownReferenceResolution(
        string dbPath,
        string symbolName,
        string expectedState,
        int expectedCandidateCount)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reference.resolution_state || '|' || reference.resolution_candidate_count
            FROM symbol_references AS reference
            JOIN files AS source_file ON source_file.id = reference.file_id
            WHERE source_file.path = 'source.md'
              AND reference.reference_kind = 'reference'
              AND reference.symbol_name = @symbolName
            """;
        command.Parameters.AddWithValue("@symbolName", symbolName);
        Assert.Equal(
            $"{expectedState}|{expectedCandidateCount.ToString(CultureInfo.InvariantCulture)}",
            Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Run_UpdateMode_RefreshesMutualRecursionOncePerBatchIncludingDeleteOnly()
    {
        var projectRoot = CreateTempProject();
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        var previousScopeHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        var refreshCount = 0;
        var scopeStats = new List<DbWriter.ReferenceGraphRefreshScopeStats>();
        try
        {
            var firstPath = Path.Combine(projectRoot, "MutualRecursionA.cs");
            var secondPath = Path.Combine(projectRoot, "MutualRecursionB.cs");
            File.WriteAllText(
                firstPath,
                "public static class MutualRecursionA { public static void CrossCycleA() { CrossCycleB(); } }\n");
            File.WriteAllText(
                secondPath,
                "public static class MutualRecursionB { public static void CrossCycleB() { CrossCycleA(); } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(2, CountMutualRecursionReferences(dbPath));

            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats =>
            {
                scopeStats.Add(stats);
                previousScopeHook?.Invoke(stats);
            };

            File.AppendAllText(firstPath, "// changed A\n");
            File.AppendAllText(secondPath, "// changed B\n");
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(2));
            File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "MutualRecursionA.cs", "MutualRecursionB.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(2, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, refreshCount);
            var updateScope = Assert.Single(scopeStats);
            Assert.False(updateScope.UsedFullRefresh);
            Assert.Equal(2, updateScope.DirtyReferenceCount);
            Assert.Equal(2, CountMutualRecursionReferences(dbPath));

            refreshCount = 0;
            scopeStats.Clear();
            File.Delete(secondPath);

            var (deleteExitCode, deleteJson) = RunAndCaptureJson(
                [projectRoot, "--files", "MutualRecursionB.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.Equal(1, deleteJson.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, refreshCount);
            Assert.False(Assert.Single(scopeStats).UsedFullRefresh);
            Assert.Equal(0, CountMutualRecursionReferences(dbPath));

            refreshCount = 0;
            scopeStats.Clear();
            var graphNeutralPath = Path.Combine(projectRoot, "graph-neutral.py");
            File.WriteAllText(graphNeutralPath, "# text-only source\n");

            var (neutralInsertExitCode, neutralInsertJson) = RunAndCaptureJson(
                [projectRoot, "--files", "graph-neutral.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, neutralInsertExitCode);
            Assert.Equal(1, neutralInsertJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, refreshCount);
            Assert.Empty(scopeStats);

            File.WriteAllText(graphNeutralPath, "# changed text-only source\n");
            File.SetLastWriteTimeUtc(graphNeutralPath, DateTime.UtcNow.AddSeconds(2));
            var (neutralUpdateExitCode, neutralUpdateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "graph-neutral.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, neutralUpdateExitCode);
            Assert.Equal(1, neutralUpdateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, refreshCount);
            Assert.Empty(scopeStats);
        }
        finally
        {
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousScopeHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_CancelledDuringMutualRecursionRefresh_LeavesReadinessDegraded()
    {
        var projectRoot = CreateTempProject();
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        using var cancellation = new CancellationTokenSource();
        var hookInvoked = false;
        try
        {
            var firstPath = Path.Combine(projectRoot, "MutualRecursionA.cs");
            var secondPath = Path.Combine(projectRoot, "MutualRecursionB.cs");
            File.WriteAllText(
                firstPath,
                "public static class MutualRecursionA { public static void CrossCycleA() { CrossCycleB(); } }\n");
            File.WriteAllText(
                secondPath,
                "public static class MutualRecursionB { public static void CrossCycleB() { CrossCycleA(); } }\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(firstPath, "// changed A\n");
            File.AppendAllText(secondPath, "// changed B\n");
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(2));
            File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddSeconds(2));
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                hookInvoked = true;
                cancellation.Cancel();
                previousRefreshHook?.Invoke();
            };

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--files", "MutualRecursionA.cs", "MutualRecursionB.cs", "--json"],
                cancellation);

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
    public void Run_UpdateMode_RejectsNewSymbolKindFilterPolicy()
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
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(Path.Combine(projectRoot, "app.py"), "\n# touched\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "app.py"), DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "function", "--files", "app.py", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("full index refresh", json.GetProperty("hint").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_RejectsRemovedSymbolKindFilterPolicy()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(sourcePath, """
                class App:
                    pass

                def helper():
                    return App()
                """);
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--exclude-symbol-kind", "function", "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(sourcePath, "\n# touched\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.py", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("symbol-kind filter policy cannot change", json.GetProperty("message").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateNonCSharpFile_DoesNotResolveCSharpMetadataTargets()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_update_non_csharp_no_csharp_metadata");
        var ranCSharpPrepass = false;
        var resolvedMetadataTargets = false;
        var rebuiltTypeScriptAugmentation = false;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.ts"), "interface AppApi { run(): void; }\n");
            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 1\n");

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "tool.py"), "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "tool.py"), DateTime.UtcNow.AddSeconds(2));

            IndexCommandRunner.UpdateCSharpPrepassForTesting = () => ranCSharpPrepass = true;
            IndexCommandRunner.UpdateCSharpMetadataResolveForTesting = () => resolvedMetadataTargets = true;
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "tool.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.False(ranCSharpPrepass);
            Assert.False(resolvedMetadataTargets);
            Assert.False(rebuiltTypeScriptAugmentation);
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = null;
            IndexCommandRunner.UpdateCSharpMetadataResolveForTesting = null;
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_HardlinkedTargets_SkipsDuplicatePathWithWarning()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var original = Path.Combine(projectRoot, "original.cs");
            var duplicate = Path.Combine(projectRoot, "duplicate.cs");
            File.WriteAllText(original, "public class HardlinkFixture { }\n");
            CreateHardLink(original, duplicate);

            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.AppendAllText(original, "public class HardlinkFixture2 { }\n");
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "original.cs", "duplicate.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("update", json.GetProperty("mode").GetString());
            var summary = json.GetProperty("summary");
            Assert.Equal(1, summary.GetProperty("updated").GetInt32());
            Assert.Equal(1, summary.GetProperty("warnings").GetInt32());
            var warning = Assert.Single(json.GetProperty("warnings").EnumerateArray());
            Assert.Contains("hardlinked", warning.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Single(ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_UnrelatedHardlinkIsNotTreatedAsCaseAliasCleanup()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var retainedPath = Path.Combine(projectRoot, "target.py");
            var hardlinkPath = Path.Combine(projectRoot, "unrelated.py");
            File.WriteAllText(retainedPath, "print('retained')\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            CreateHardLink(retainedPath, hardlinkPath);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var checksum = Assert.IsType<string>(ReadIndexedChecksum(dbPath, "target.py"));
            var fileInfo = new FileInfo(retainedPath);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db);
                writer.UpsertFile(new FileRecord
                {
                    Path = "unrelated.py",
                    Lang = "python",
                    Size = fileInfo.Length,
                    Lines = 1,
                    Checksum = checksum,
                    Modified = fileInfo.LastWriteTimeUtc,
                });
            }
            File.SetLastWriteTimeUtc(retainedPath, DateTime.UtcNow.AddSeconds(2));

            var updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", "target.py", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("target.py", indexedPaths);
            Assert.Contains("unrelated.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_JsonWritesLivenessToStderrWithoutPollutingStdout()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "public class Program { }\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "public class Program { public void Run() { } }\n");

            var (exitCode, json, stderr) = RunAndCaptureJsonWithStderr([projectRoot, "--files", "Program.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("update", json.GetProperty("mode").GetString());
            Assert.Contains("cdidx: checking C# workspace contracts", stderr);
            Assert.Contains("cdidx: updating", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpAgainstSharedExplicitDb_DoesNotRewriteIndexedProjectRoot()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_shared_root_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRootA, "readme.md"), "# from a\n");
            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            Directory.CreateDirectory(Path.Combine(projectRootB, "docs"));
            File.WriteAllText(Path.Combine(projectRootB, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.True(updateJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("fold_ready_reason").ValueKind);
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("degraded_reason").ValueKind);
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("recommended_action").ValueKind);
            Assert.Equal(JsonValueKind.Null, updateJson.GetProperty("alternative_action").ValueKind);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var statusExitCode = QueryCommandRunner.RunStatus(["--db", dbPath, "--json"], _jsonOptions);
                    Assert.Equal(CommandExitCodes.Success, statusExitCode);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal(Path.GetFullPath(projectRootA), document.RootElement.GetProperty("project_root").GetString());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_NoOpAgainstSharedExplicitDb_PurgesUnsupportedReferencesWithoutRewritingIndexedProjectRoot()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_shared_stale_refs_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRootA, "app.py"), "print('from a')\n");
            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            long CountReferences()
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM symbol_references";
                return (long)cmd.ExecuteScalar()!;
            }

            var baselineReferenceCount = CountReferences();
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "docs/stale.txt",
                    Lang = "text",
                    Size = 12,
                    Lines = 1,
                    Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "stale-edge",
                });
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "LegacyLink",
                        ReferenceKind = "call",
                        Line = 1,
                        Column = 1,
                        Context = "LegacyLink",
                    },
                ]);
            }
            Assert.Equal(baselineReferenceCount + 1, CountReferences());

            Directory.CreateDirectory(Path.Combine(projectRootB, "docs"));
            File.WriteAllText(Path.Combine(projectRootB, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(baselineReferenceCount, updateJson.GetProperty("summary").GetProperty("references_total").GetInt32());
            Assert.True(updateJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(updateJson.GetProperty("fold_ready").GetBoolean());

            Assert.Equal(baselineReferenceCount, CountReferences());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    var statusExitCode = QueryCommandRunner.RunStatus(["--db", dbPath, "--json"], _jsonOptions);
                    Assert.Equal(CommandExitCodes.Success, statusExitCode);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    Assert.Equal(Path.GetFullPath(projectRootA), document.RootElement.GetProperty("project_root").GetString());
                    Assert.Equal(baselineReferenceCount, document.RootElement.GetProperty("references").GetInt32());
                }
                finally
                {
                    Console.SetOut(originalOut);
                }
            }
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_UnsupportedReferencePurgeAndReadinessDemotionRollBackTogether()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_stale_refs_atomic_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('hello')\n");
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "legacy/old.toml",
                    Lang = "toml",
                    Size = 12,
                    Lines = 1,
                    Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "stale-edge-atomic",
                });
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "LegacyLink",
                        ReferenceKind = "call",
                        Line = 1,
                        Column = 1,
                        Context = "LegacyLink",
                    },
                ]);
            }

            long ReadScalar(string sql)
            {
                using var connection = OpenNonPoolingConnection(dbPath);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return (long)command.ExecuteScalar()!;
            }

            var readyVersion = ReadScalar("PRAGMA user_version");
            Assert.NotEqual(0, readyVersion & DbContext.GraphReadyFlag);
            Assert.NotEqual(0, readyVersion & DbContext.IssuesReadyFlag);
            Assert.NotEqual(0, readyVersion & DbContext.FoldReadyFlag);
            Assert.Equal(1, ReadScalar("SELECT COUNT(*) FROM symbol_references WHERE symbol_name = 'LegacyLink'"));

            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fail_readiness_demotion
                    BEFORE INSERT ON codeindex_meta
                    BEGIN
                        SELECT RAISE(FAIL, 'boom');
                    END;
                    """;
                command.ExecuteNonQuery();
            }

            Assert.Throws<SqliteException>(() =>
                IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--files", "missing.txt", "--json"], _jsonOptions));

            Assert.Equal(readyVersion, ReadScalar("PRAGMA user_version"));
            Assert.Equal(1, ReadScalar("SELECT COUNT(*) FROM symbol_references WHERE symbol_name = 'LegacyLink'"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_ExplicitDb_RealMutationRewritesIndexedProjectRootMetadata()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_shared_rewrite_root_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRootA, "init");
            File.WriteAllText(Path.Combine(projectRootA, "app.cs"), "public class App { public void Run() { } }\n");
            RunGit(projectRootA, "add", ".");
            RunGit(projectRootA, "commit", "-m", "init-a");
            var headA = RunGitCaptureStdOut(projectRootA, "rev-parse", "HEAD").Trim();

            RunGit(projectRootB, "init");
            var sourcePathB = Path.Combine(projectRootB, "app.cs");
            File.WriteAllText(sourcePathB, "public class App { public void Run() { } public void Extra() { } }\n");
            RunGit(projectRootB, "add", ".");
            RunGit(projectRootB, "commit", "-m", "init-b");
            var headB = RunGitCaptureStdOut(projectRootB, "rev-parse", "HEAD").Trim();
            File.SetLastWriteTimeUtc(sourcePathB, DateTime.UtcNow.AddSeconds(2));

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootA), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRootB), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRootB), statusJson.GetProperty("project_root").GetString());
            Assert.Equal(headB, statusJson.GetProperty("git_head").GetString());
            Assert.NotEqual(headA, statusJson.GetProperty("git_head").GetString());
            Assert.False(statusJson.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacySharedExplicitDb_NoOpDoesNotHijackMissingIndexedProjectRootMetadata()
    {
        var projectRootA = CreateTempProject();
        var projectRootB = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_noop_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRootA, "init");
            File.WriteAllText(Path.Combine(projectRootA, "app.py"), "print('hello')\n");
            RunGit(projectRootA, "add", ".");
            RunGit(projectRootA, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRootA, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);

            Directory.CreateDirectory(Path.Combine(projectRootB, "docs"));
            File.WriteAllText(Path.Combine(projectRootB, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRootB, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Null(db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Null(statusJson.GetProperty("project_root").GetString());
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRootA);
            DeleteDirectory(projectRootB);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacyExplicitDb_SuccessfulFileUpdateBackfillsIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_update_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);
            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(Path.GetFullPath(projectRoot), db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(Path.GetFullPath(projectRoot), statusJson.GetProperty("project_root").GetString());
            Assert.False(string.IsNullOrWhiteSpace(statusJson.GetProperty("git_head").GetString()));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacyExplicitDb_PurgeOnlyNoOpDoesNotBackfillIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_purge_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "readme.md"), "# hello\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);
            int CountReferences()
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM symbol_references";
                return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            var baselineReferenceCount = CountReferences();
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = "docs/stale.txt",
                    Lang = "text",
                    Size = 12,
                    Lines = 1,
                    Modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "stale-edge",
                });
                writer.InsertReferences([
                    new ReferenceRecord
                    {
                        FileId = fileId,
                        SymbolName = "LegacyLink",
                        ReferenceKind = "call",
                        Line = 1,
                        Column = 1,
                        Context = "LegacyLink",
                    },
                ]);
            }
            Assert.Equal(baselineReferenceCount + 1, CountReferences());

            Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
            File.WriteAllText(Path.Combine(projectRoot, "docs", "readme.txt"), "not indexable\n");

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--files", "docs/readme.txt", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(baselineReferenceCount, updateJson.GetProperty("summary").GetProperty("references_total").GetInt32());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Null(db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Null(statusJson.GetProperty("project_root").GetString());
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateMode_LegacyExplicitDb_RollbackedFirstMutationDoesNotBackfillIndexedProjectRootMetadata()
    {
        var projectRoot = CreateTempProject();
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_legacy_explicit_rollback_{Guid.NewGuid():N}.db");
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", dbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            DeleteIndexedProjectRootMetadata(dbPath);
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

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--db", dbPath, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Null(db.GetMetaString(DbContext.IndexedProjectRootMetaKey));
            }

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Null(statusJson.GetProperty("project_root").GetString());
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_head").ValueKind);
            Assert.Equal(JsonValueKind.Null, statusJson.GetProperty("git_is_dirty").ValueKind);
        }
        finally
        {
            DeleteDirectory(projectRoot);
            DeleteFile(dbPath);
        }
    }

    [Fact]
    public void Run_UpdateFiles_UnchangedStatMatch_SkipsWithoutOpeningFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.py");
            const string content = "def run():\n    return 1\n";
            File.WriteAllText(sourcePath, content);
            File.SetLastWriteTimeUtc(sourcePath, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.SetUnixFileMode(sourcePath, UnixFileMode.None);
            try
            {
                var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.py", "--json"]);

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal("success", json.GetProperty("status").GetString());
                Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
                Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            }
            finally
            {
                File.SetUnixFileMode(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void Run_UpdateFiles_RemovesOldPathWhenExtensionChanges()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.md");
            File.WriteAllText(oldPath, "# Title\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--files", "foo.py", "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(IndexedFileExists(projectRoot, "foo.py"));

            File.Move(oldPath, newPath);
            File.AppendAllText(newPath, "Updated during rename\n");
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.md", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.True(IndexedFileExists(projectRoot, "foo.md"));
            Assert.False(IndexedFileExists(projectRoot, "foo.py"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_RemovesOldPathWhenExtensionChangesToUnsupported()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var oldPath = Path.Combine(projectRoot, "foo.py");
            var newPath = Path.Combine(projectRoot, "foo.bin");
            File.WriteAllText(oldPath, "print('hello')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--files", "foo.py", "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(IndexedFileExists(projectRoot, "foo.py"));

            File.Move(oldPath, newPath);

            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "foo.bin", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(IndexedFileExists(projectRoot, "foo.py"));
            Assert.False(IndexedFileExists(projectRoot, "foo.bin"));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_FailedFirstMutation_DemotesReadinessBeforeRollback()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (_, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.True(initialJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, initialJson.GetProperty("fold_ready_reason").ValueKind);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
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

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("graph_data_current").GetBoolean());
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.Equal(CommandErrorCodes.IndexPartial, json.GetProperty("error_code").GetString());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());

            var (_, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());

            using var verify = OpenNonPoolingConnection(dbPath);
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)verifyCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.GraphReadyFlag);
            Assert.Equal(0, userVersion & DbContext.IssuesReadyFlag);
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AllowsProjectRelativePathsStartingWithDotDotName()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var hiddenDir = Path.Combine(projectRoot, "..hidden");
            Directory.CreateDirectory(hiddenDir);
            File.WriteAllText(Path.Combine(hiddenDir, "sample.cs"), "class Sample {}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "..hidden/sample.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_TypeScriptConfigChangeFallsBackToFullScanForAliasSymbols()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "app", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "pages"));
            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["src/*"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "components", "Button.tsx"), "export const Button = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "app", "components", "Button.tsx"), "export const UpdatedButton = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "pages", "Page.tsx"), "import { Button } from \"@/components/Button\";\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("src/components/Button.tsx", ReadImportSymbolNames(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "tsconfig.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "@/*": ["app/*"]
                    }
                  }
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "tsconfig.json", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            var imports = ReadImportSymbolNames(dbPath);
            Assert.Contains("app/components/Button.tsx", imports);
            Assert.DoesNotContain("src/components/Button.tsx", imports);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_JavaScriptExtendedConfigChangeFallsBackToFullScanForAliasSymbols()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "app", "components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "pages"));
            File.WriteAllText(Path.Combine(projectRoot, "jsconfig.json"), """
                {
                  "extends": "./jsconfig.base.json"
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "jsconfig.base.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~/*": ["src/*"]
                    }
                  }
                }
                """);
            File.WriteAllText(Path.Combine(projectRoot, "src", "components", "Card.js"), "export const Card = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "app", "components", "Card.js"), "export const UpdatedCard = 1;\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "pages", "Page.js"), "import { Card } from \"~/components/Card\";\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("src/components/Card.js", ReadImportSymbolNames(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, "jsconfig.base.json"), """
                {
                  "compilerOptions": {
                    "baseUrl": ".",
                    "paths": {
                      "~/*": ["app/*"]
                    }
                  }
                }
                """);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "jsconfig.base.json", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            var imports = ReadImportSymbolNames(dbPath);
            Assert.Contains("app/components/Card.js", imports);
            Assert.DoesNotContain("src/components/Card.js", imports);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_SkipsPathsOutsideProjectRoot()
    {
        var projectRoot = CreateTempProject();
        var outsideFile = Path.Combine(Directory.GetParent(projectRoot)!.FullName, $"outside_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllText(outsideFile, "class Outside {}\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", $"../{Path.GetFileName(outsideFile)}", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
        }
        finally
        {
            DeleteFile(outsideFile);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpStaticInterfaceContractChanges_ReindexImplementers()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: false);
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
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));

            WriteParseableInterface(interfacePath, hasStaticContract: true);

            var updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            WriteParseableInterface(interfacePath, hasStaticContract: false);

            updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));

            WriteParseableInterface(interfacePath, hasStaticContract: true);
            updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.Delete(interfacePath);

            updateExitCode = IndexCommandRunner.Run([projectRoot, "--files", "IParseable.cs", "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task Run_UpdateFiles_AuthoritativeCSharpParallelWindowsAreBoundedOrderedAndReuseWorkers()
    {
        var projectRoot = CreateTempProject();
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        using var releaseFirstExtraction = new ManualResetEventSlim();
        using var firstWindowFilled = new ManualResetEventSlim();
        Task<(int ExitCode, JsonElement Json)>? runTask = null;
        try
        {
            CreateAuthoritativeParallelUpdateProject(
                projectRoot,
                implementationCount: 8);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));

            var touchedPath = Path.Combine(projectRoot, "Source00.cs");
            File.AppendAllText(touchedPath, "// parallel update\n");
            File.SetLastWriteTimeUtc(touchedPath, DateTime.UtcNow.AddSeconds(2));

            (bool Enabled, string? Reason, int Workers, int Capacity) scheduling = default;
            var extractionStarts = 0;
            var extractionCompleted = new ConcurrentDictionary<int, byte>();
            var persistenceCompleted = new ConcurrentDictionary<int, byte>();
            var persistenceOrder = new ConcurrentQueue<int>();
            var violations = new ConcurrentQueue<string>();
            var workerIndexes = new ConcurrentDictionary<int, byte>();
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                (enabled, reason, workers, capacity) =>
                    scheduling = (enabled, reason, workers, capacity);
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                switch (item.Kind)
                {
                    case IndexCommandRunner.UpdateParallelExtractionEventKind.WorkerStarted:
                        workerIndexes.TryAdd(item.WorkerIndex, 0);
                        break;
                    case IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted:
                        {
                            var started = Interlocked.Increment(ref extractionStarts);
                            if (item.TargetIndex >= 4
                                && !Enumerable.Range(0, 4).All(
                                    persistenceCompleted.ContainsKey))
                            {
                                violations.Enqueue(
                                    "The next window started before the prior window was persisted.");
                            }
                            if (started == 1)
                            {
                                releaseFirstExtraction.Wait(TimeSpan.FromSeconds(30));
                            }
                            if (started == 4)
                                firstWindowFilled.Set();
                            break;
                        }
                    case IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted:
                        extractionCompleted.TryAdd(item.TargetIndex, 0);
                        break;
                    case IndexCommandRunner.UpdateParallelExtractionEventKind.PersistenceStarted:
                        {
                            var windowStart = item.TargetIndex < 4 ? 0 : 4;
                            if (!Enumerable.Range(windowStart, 4).All(
                                    extractionCompleted.ContainsKey))
                            {
                                violations.Enqueue(
                                    "Persistence started before every extraction in its window completed.");
                            }
                            persistenceOrder.Enqueue(item.TargetIndex);
                            break;
                        }
                    case IndexCommandRunner.UpdateParallelExtractionEventKind.PersistenceCompleted:
                        persistenceCompleted.TryAdd(item.TargetIndex, 0);
                        break;
                }
            };

            runTask = Task.Run(() => RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ]));

            Assert.True(
                firstWindowFilled.Wait(TimeSpan.FromSeconds(30)),
                "The bounded extraction window did not fill.");
            Assert.Equal(4, Volatile.Read(ref extractionStarts));
            Assert.Empty(persistenceOrder);
            releaseFirstExtraction.Set();

            var (exitCode, json) = await runTask.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(scheduling.Enabled);
            Assert.Null(scheduling.Reason);
            Assert.Equal(2, scheduling.Workers);
            Assert.Equal(4, scheduling.Capacity);
            Assert.Equal(2, workerIndexes.Count);
            Assert.Equal(8, extractionCompleted.Count);
            Assert.Equal(Enumerable.Range(0, 8), persistenceOrder.ToArray());
            Assert.Empty(violations);
        }
        finally
        {
            releaseFirstExtraction.Set();
            var runCompleted = runTask == null;
            if (runTask != null)
            {
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(30));
                    runCompleted = true;
                }
                catch (TimeoutException)
                {
                }
                catch
                {
                    runCompleted = true;
                    // The body owns the primary assertion/run failure. Cleanup only waits
                    // for the static test seam to stop being observed.
                }
            }
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                previousSchedulingHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            if (runCompleted)
                DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("parallelism_one")]
    [InlineData("active_symbol_kind_filter")]
    [InlineData("content_load_test_hook")]
    [InlineData("non_authoritative_csharp_workspace")]
    [InlineData("insufficient_authoritative_csharp_targets")]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelExtractionUsesRequiredFallbacks(
        string expectedReason)
    {
        var projectRoot = CreateTempProject();
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousContentLoadHook =
            IndexCommandRunner.UpdateFileContentLoadForTesting;
        try
        {
            var touchedRelativePath = "Source00.cs";
            if (expectedReason == "non_authoritative_csharp_workspace")
            {
                touchedRelativePath = "Plain00.cs";
                File.WriteAllText(
                    Path.Combine(projectRoot, touchedRelativePath),
                    "public sealed class Plain00 { }\n");
                File.WriteAllText(
                    Path.Combine(projectRoot, "Plain01.cs"),
                    "public sealed class Plain01 { }\n");
            }
            else if (expectedReason == "insufficient_authoritative_csharp_targets")
            {
                touchedRelativePath = "IParseable.cs";
                WriteParseableInterface(
                    Path.Combine(projectRoot, touchedRelativePath),
                    hasStaticContract: true);
            }
            else
            {
                CreateAuthoritativeParallelUpdateProject(
                    projectRoot,
                    implementationCount: 2);
            }
            var initialArgs = new List<string>
            {
                projectRoot,
                "--json",
                "--quiet",
                "--parallelism",
                "1",
            };
            if (expectedReason == "active_symbol_kind_filter")
            {
                initialArgs.Add("--include-symbol-kind");
                initialArgs.Add("function");
            }
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(initialArgs.ToArray(), _jsonOptions));
            var touchedPath = Path.Combine(projectRoot, touchedRelativePath);
            File.AppendAllText(touchedPath, "// fallback update\n");
            File.SetLastWriteTimeUtc(touchedPath, DateTime.UtcNow.AddSeconds(2));

            (bool Enabled, string? Reason) scheduling = default;
            var parallelEvents = 0;
            var contentLoads = 0;
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                (enabled, reason, _, _) => scheduling = (enabled, reason);
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = _ =>
                Interlocked.Increment(ref parallelEvents);
            if (expectedReason == "content_load_test_hook")
            {
                IndexCommandRunner.UpdateFileContentLoadForTesting = _ =>
                    Interlocked.Increment(ref contentLoads);
            }

            var args = new List<string>
            {
                projectRoot,
                "--files",
                touchedRelativePath,
                "--json",
                "--quiet",
                "--parallelism",
                expectedReason == "parallelism_one" ? "1" : "2",
            };
            if (expectedReason == "active_symbol_kind_filter")
            {
                args.Add("--include-symbol-kind");
                args.Add("function");
            }
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(args.ToArray(), _jsonOptions));
            Assert.False(scheduling.Enabled);
            Assert.Equal(expectedReason, scheduling.Reason);
            Assert.Equal(0, Volatile.Read(ref parallelEvents));
            if (expectedReason == "content_load_test_hook")
                Assert.True(contentLoads > 0);
        }
        finally
        {
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                previousSchedulingHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateFileContentLoadForTesting =
                previousContentLoadHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("io")]
    [InlineData("operation_canceled")]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelWindowProbeFailureFallsBackToSerialBoundary(
        string failureKind)
    {
        var projectRoot = CreateTempProject();
        var previousProbeFailureHook =
            IndexCommandRunner.UpdateParallelWindowProbeFailureForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        try
        {
            CreateAuthoritativeParallelUpdateProject(projectRoot, implementationCount: 3);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            foreach (var relativePath in new[] { "Source00.cs", "Source01.cs" })
            {
                var path = Path.Combine(projectRoot, relativePath);
                File.AppendAllText(path, "// serial probe fallback\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
            }

            var probeFailures = 0;
            var parallelEvents = 0;
            IndexCommandRunner.UpdateParallelWindowProbeFailureForTesting = path =>
                path == "Source00.cs"
                    && Interlocked.Increment(ref probeFailures) == 1
                        ? failureKind == "operation_canceled"
                            ? new OperationCanceledException(
                                "injected non-requested parallel window probe cancellation")
                            : new IOException(
                                "injected parallel window probe failure")
                        : null;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = _ =>
                Interlocked.Increment(ref parallelEvents);

            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "Source01.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, probeFailures);
            Assert.Equal(0, parallelEvents);
            Assert.NotEqual(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.NotEqual(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
        }
        finally
        {
            IndexCommandRunner.UpdateParallelWindowProbeFailureForTesting =
                previousProbeFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelExtractionRevalidatesBeforePersist()
    {
        var projectRoot = CreateTempProject();
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        try
        {
            CreateAuthoritativeParallelUpdateProject(
                projectRoot,
                implementationCount: 3);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var mutatedTarget = string.Empty;
            string? checksumBefore = null;
            var mutationCount = 0;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                        != IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted
                    || Interlocked.Increment(ref mutationCount) != 1)
                {
                    return;
                }
                mutatedTarget = item.RelativePath;
                checksumBefore = ReadIndexedChecksum(dbPath, mutatedTarget);
                var path = Path.Combine(
                    projectRoot,
                    mutatedTarget.Replace('/', Path.DirectorySeparatorChar));
                File.AppendAllText(path, "// changed after extraction\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
            };

            var sourcePath = Path.Combine(projectRoot, "Source00.cs");
            File.AppendAllText(sourcePath, "// trigger authoritative update\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));
            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.NotEmpty(mutatedTarget);
            Assert.Equal(checksumBefore, ReadIndexedChecksum(dbPath, mutatedTarget));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelWorkerFailureIsIsolatedWithPhase()
    {
        var projectRoot = CreateTempProject();
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        try
        {
            CreateAuthoritativeParallelUpdateProject(
                projectRoot,
                implementationCount: 3);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var failingPath = "Source00.cs";
            var succeedingPath = "Source01.cs";
            var failingChecksumBefore = ReadIndexedChecksum(dbPath, failingPath);
            var succeedingChecksumBefore = ReadIndexedChecksum(dbPath, succeedingPath);
            File.AppendAllText(Path.Combine(projectRoot, failingPath), "// fail symbols\n");
            File.AppendAllText(Path.Combine(projectRoot, succeedingPath), "// still persist\n");
            File.SetLastWriteTimeUtc(
                Path.Combine(projectRoot, failingPath),
                DateTime.UtcNow.AddSeconds(2));
            File.SetLastWriteTimeUtc(
                Path.Combine(projectRoot, succeedingPath),
                DateTime.UtcNow.AddSeconds(2));
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                (path, phase) => path == failingPath && phase == "symbols"
                    ? new InvalidOperationException("parallel symbols failure")
                    : null;

            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    failingPath,
                    succeedingPath,
                    "--json",
                    "--parallelism",
                    "2",
                ]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(failingChecksumBefore, ReadIndexedChecksum(dbPath, failingPath));
            Assert.NotEqual(succeedingChecksumBefore, ReadIndexedChecksum(dbPath, succeedingPath));
            var failure = Assert.Single(
                json.GetProperty("file_errors").EnumerateArray(),
                item => item.GetProperty("file").GetString() == failingPath);
            Assert.Equal("symbols", failure.GetProperty("phase").GetString());
            Assert.False(json.GetProperty("csharp_metadata_target_ready").GetBoolean());
        }
        finally
        {
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("generated")]
    [InlineData("symbol_cap")]
    [InlineData("reference_cap")]
    [InlineData("oversize")]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelPersistenceMatchesSerialProjection(
        string scenario)
    {
        var serialRoot = CreateTempProject();
        var parallelRoot = CreateTempProject();
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        using var generatedPatterns = EnvironmentVariableScope.Capture(
            IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        try
        {
            generatedPatterns.Set(
                IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable,
                scenario == "generated" ? "Source02.cs" : null);
            CreateAuthoritativeParallelUpdateProject(serialRoot, implementationCount: 3);
            CreateAuthoritativeParallelUpdateProject(parallelRoot, implementationCount: 3);
            var initialModifiedUtc = DateTime.UtcNow.AddSeconds(-4);
            foreach (var root in new[] { serialRoot, parallelRoot })
            {
                File.SetLastWriteTimeUtc(
                    Path.Combine(root, "IParseable.cs"),
                    initialModifiedUtc);
                for (var index = 0; index < 3; index++)
                {
                    File.SetLastWriteTimeUtc(
                        Path.Combine(root, $"Source{index:00}.cs"),
                        initialModifiedUtc);
                }
            }
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [serialRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [parallelRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));

            var modifiedUtc = DateTime.UtcNow.AddSeconds(4);
            foreach (var root in new[] { serialRoot, parallelRoot })
            {
                File.AppendAllText(Path.Combine(root, "Source00.cs"), "// parity zero\n");
                File.AppendAllText(Path.Combine(root, "Source01.cs"), "// parity one\n");
                if (scenario == "oversize")
                {
                    File.AppendAllText(
                        Path.Combine(root, "Source02.cs"),
                        "// " + new string('x', 2048) + "\n");
                }
                File.SetLastWriteTimeUtc(Path.Combine(root, "Source00.cs"), modifiedUtc);
                File.SetLastWriteTimeUtc(Path.Combine(root, "Source01.cs"), modifiedUtc);
                File.SetLastWriteTimeUtc(Path.Combine(root, "Source02.cs"), modifiedUtc);
            }

            var serialArgs = BuildArgs(serialRoot, "1");
            var parallelArgs = BuildArgs(parallelRoot, "2");
            var (serialExitCode, serialJson) = RunAndCaptureJson(serialArgs);
            var parallelScheduled = false;
            var completedPayloads = new ConcurrentQueue<
                (string Path, int RetainedSymbols, bool HasSourceContract)>();
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                (enabled, _, _, _) => parallelScheduled = enabled;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedPayloads.Enqueue(
                        (
                            item.RelativePath,
                            item.RetainedSymbolCount,
                            item.HasSourceContractEvidence));
                }
            };
            var (parallelExitCode, parallelJson) = RunAndCaptureJson(parallelArgs);

            Assert.Equal(CommandExitCodes.Success, serialExitCode);
            Assert.True(
                serialExitCode == parallelExitCode,
                $"Serial result: {serialJson}; parallel result: {parallelJson}");
            Assert.True(parallelScheduled);
            Assert.NotEmpty(completedPayloads);
            if (scenario == "symbol_cap")
            {
                Assert.All(
                    completedPayloads,
                    payload => Assert.Equal(0, payload.RetainedSymbols));
                Assert.Contains(
                    completedPayloads,
                    payload => payload.Path == "IParseable.cs"
                        && payload.HasSourceContract);
            }
            foreach (var property in new[]
                     {
                         "updated",
                         "removed",
                         "skipped",
                         "warnings",
                         "errors",
                     })
            {
                Assert.Equal(
                    serialJson.GetProperty("summary").GetProperty(property).GetInt32(),
                    parallelJson.GetProperty("summary").GetProperty(property).GetInt32());
            }
            var serialProjection = ReadStableUpdateProjection(
                Path.Combine(serialRoot, ".cdidx", "codeindex.db"));
            var parallelProjection = ReadStableUpdateProjection(
                Path.Combine(parallelRoot, ".cdidx", "codeindex.db"));
            Assert.Equal(serialProjection, parallelProjection);
            var (serialStatusExitCode, serialStatus) = RunStatusAndCaptureJson(
                [
                    "--db",
                    Path.Combine(serialRoot, ".cdidx", "codeindex.db"),
                    "--json",
                ]);
            var (parallelStatusExitCode, parallelStatus) = RunStatusAndCaptureJson(
                [
                    "--db",
                    Path.Combine(parallelRoot, ".cdidx", "codeindex.db"),
                    "--json",
                ]);
            Assert.Equal(CommandExitCodes.Success, serialStatusExitCode);
            Assert.Equal(serialStatusExitCode, parallelStatusExitCode);
            foreach (var property in new[]
                     {
                         "graph_table_available",
                         "graph_data_current",
                         "reference_graph_complete",
                         "issues_table_available",
                         "file_issues_data_current",
                         "migration_in_progress",
                         "index_complete",
                         "hotspot_family_ready",
                         "csharp_symbol_name_ready",
                         "csharp_metadata_target_ready",
                         "sql_graph_contract_ready",
                         "fold_ready",
                     })
            {
                Assert.Equal(
                    serialStatus.GetProperty(property).GetBoolean(),
                    parallelStatus.GetProperty(property).GetBoolean());
            }

            string[] BuildArgs(string root, string parallelism)
            {
                var args = new List<string>
                {
                    root,
                    "--files",
                    "Source00.cs",
                    "Source01.cs",
                    "--json",
                    "--parallelism",
                    parallelism,
                };
                switch (scenario)
                {
                    case "symbol_cap":
                        args.Add("--max-symbols-per-file");
                        args.Add("1");
                        break;
                    case "reference_cap":
                        args.Add("--max-references-per-file");
                        args.Add("1");
                        break;
                    case "oversize":
                        args.Add("--max-file-bytes");
                        args.Add("512");
                        break;
                }
                return args.ToArray();
            }
        }
        finally
        {
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                previousSchedulingHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            DeleteDirectory(serialRoot);
            DeleteDirectory(parallelRoot);
        }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("file_too_large")]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelPreservesNullableHeaderLanguageReuse(
        string scenario)
    {
        var serialRoot = CreateTempProject();
        var parallelRoot = CreateTempProject();
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        var previousContentLoadHook =
            IndexCommandRunner.UpdateFileContentLoadForTesting;
        var previousSkippedRecordHook =
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting;
        try
        {
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            var initialModifiedUtc = DateTime.UtcNow.AddSeconds(-4);
            foreach (var root in new[] { serialRoot, parallelRoot })
            {
                var languageMapPath = Path.Combine(
                    root,
                    LanguageMapOverrides.WorkspaceFileName);
                File.WriteAllText(
                    languageMapPath,
                    "entries:\n"
                    + "  - extension: \".h\"\n"
                    + "    language: \"csharp\"\n");
                File.SetLastWriteTimeUtc(languageMapPath, initialModifiedUtc);
                WriteParseableInterface(
                    Path.Combine(root, "IParseable.cs"),
                    hasStaticContract: true);
                for (var index = 0; index < 2; index++)
                {
                    File.WriteAllText(
                        Path.Combine(root, $"Header{index:00}.h"),
                        $"public readonly struct Header{index:00} : IParseable<Header{index:00}>\n"
                        + "{\n"
                        + $"    public static Header{index:00} Parse(string value) => new();\n"
                        + "}\n");
                    File.SetLastWriteTimeUtc(
                        Path.Combine(root, $"Header{index:00}.h"),
                        initialModifiedUtc);
                }
                File.SetLastWriteTimeUtc(
                    Path.Combine(root, "IParseable.cs"),
                    initialModifiedUtc);
            }

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [serialRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [parallelRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));

            var modifiedUtc = DateTime.UtcNow.AddSeconds(4);
            foreach (var root in new[] { serialRoot, parallelRoot })
            {
                for (var index = 0; index < 2; index++)
                {
                    var path = Path.Combine(root, $"Header{index:00}.h");
                    File.AppendAllText(path, "// nullable header language parity\n");
                    File.SetLastWriteTimeUtc(path, modifiedUtc);
                }
            }

            var serialSkippedLanguages = new ConcurrentQueue<
                (string Path, string KnownLanguage)>();
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting =
                (path, knownLanguage) => serialSkippedLanguages.Enqueue(
                    (path, knownLanguage ?? "<null>"));
            if (scenario == "file_too_large")
            {
                IndexCommandRunner.UpdateFileContentLoadForTesting = path =>
                {
                    if (path == "Header00.h")
                        throw CreateInjectedTooLargeException(path);
                };
            }
            var (serialExitCode, serialJson) = RunAndCaptureJson(
                [
                    serialRoot,
                    "--files",
                    "Header00.h",
                    "Header01.h",
                    "--json",
                    "--quiet",
                    "--parallelism",
                    "1",
                ]);
            IndexCommandRunner.UpdateFileContentLoadForTesting =
                previousContentLoadHook;
            var parallelScheduled = false;
            var queuedKnownLanguages = new ConcurrentQueue<
                (string Path, string KnownLanguage)>();
            var parallelSkippedLanguages = new ConcurrentQueue<
                (string Path, string KnownLanguage)>();
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                (enabled, _, _, _) => parallelScheduled = enabled;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    queuedKnownLanguages.Enqueue(
                        (item.RelativePath, item.KnownLanguage ?? "<null>"));
                }
            };
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting =
                (path, knownLanguage) => parallelSkippedLanguages.Enqueue(
                    (path, knownLanguage ?? "<null>"));
            if (scenario == "file_too_large")
            {
                IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                    (path, phase) => path == "Header00.h" && phase == "reading"
                        ? CreateInjectedTooLargeException(path)
                        : null;
            }
            var (parallelExitCode, parallelJson) = RunAndCaptureJson(
                [
                    parallelRoot,
                    "--files",
                    "Header00.h",
                    "Header01.h",
                    "--json",
                    "--quiet",
                    "--parallelism",
                    "2",
                ]);

            Assert.Equal(CommandExitCodes.Success, serialExitCode);
            Assert.Equal(serialExitCode, parallelExitCode);
            foreach (var property in new[]
                     {
                         "updated",
                         "removed",
                         "skipped",
                         "warnings",
                         "errors",
                     })
            {
                Assert.Equal(
                    serialJson.GetProperty("summary").GetProperty(property).GetInt32(),
                    parallelJson.GetProperty("summary").GetProperty(property).GetInt32());
            }
            Assert.True(parallelScheduled);
            var queuedHeaders = queuedKnownLanguages
                .Where(item => item.Path.EndsWith(".h", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(2, queuedHeaders.Length);
            Assert.All(
                queuedHeaders,
                item => Assert.Equal("<null>", item.KnownLanguage));
            if (scenario == "file_too_large")
            {
                var serialSkip = Assert.Single(
                    serialSkippedLanguages,
                    item => item.Path == "Header00.h");
                var parallelSkip = Assert.Single(
                    parallelSkippedLanguages,
                    item => item.Path == "Header00.h");
                Assert.Equal("<null>", serialSkip.KnownLanguage);
                Assert.Equal(serialSkip.KnownLanguage, parallelSkip.KnownLanguage);
            }
            else
            {
                Assert.Empty(serialSkippedLanguages);
                Assert.Empty(parallelSkippedLanguages);
            }
            var serialDbPath = Path.Combine(serialRoot, ".cdidx", "codeindex.db");
            var parallelDbPath = Path.Combine(parallelRoot, ".cdidx", "codeindex.db");
            Assert.Equal("csharp", ReadLanguage(parallelDbPath, "Header00.h"));
            Assert.Equal("csharp", ReadLanguage(parallelDbPath, "Header01.h"));
            Assert.Equal(
                ReadStableUpdateProjection(serialDbPath),
                ReadStableUpdateProjection(parallelDbPath));
            var (serialStatusExitCode, serialStatus) = RunStatusAndCaptureJson(
                ["--db", serialDbPath, "--json"]);
            var (parallelStatusExitCode, parallelStatus) = RunStatusAndCaptureJson(
                ["--db", parallelDbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, serialStatusExitCode);
            Assert.Equal(serialStatusExitCode, parallelStatusExitCode);
            foreach (var property in new[]
                     {
                         "graph_table_available",
                         "graph_data_current",
                         "reference_graph_complete",
                         "issues_table_available",
                         "file_issues_data_current",
                         "migration_in_progress",
                         "index_complete",
                         "hotspot_family_ready",
                         "csharp_symbol_name_ready",
                         "csharp_metadata_target_ready",
                         "sql_graph_contract_ready",
                         "fold_ready",
                     })
            {
                Assert.Equal(
                    serialStatus.GetProperty(property).GetBoolean(),
                    parallelStatus.GetProperty(property).GetBoolean());
            }
        }
        finally
        {
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                previousSchedulingHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateFileContentLoadForTesting =
                previousContentLoadHook;
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting =
                previousSkippedRecordHook;
            LanguageMapOverrides.ClearEffectiveMapCacheForTesting();
            DeleteDirectory(serialRoot);
            DeleteDirectory(parallelRoot);
        }

        static string? ReadLanguage(string dbPath, string path)
        {
            SqliteConnection.ClearAllPools();
            using var connection = new SqliteConnection(
                $"Data Source={dbPath};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT lang FROM files WHERE path = @path";
            command.Parameters.AddWithValue("@path", path);
            return Convert.ToString(
                command.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        static FileIndexer.FileTooLargeSkippedException CreateInjectedTooLargeException(
            string relativePath)
            => new(
                relativePath,
                actualBytes: 1024,
                limitBytes: 512,
                "injected nullable-language file-too-large skip");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelCancellationKeepsExpectedPrefix(
        bool cancelDuringExtraction)
    {
        var projectRoot = CreateTempProject();
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousWorkersStoppedHook =
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting;
        using var cancellation = new CancellationTokenSource();
        using var workersStopped = new ManualResetEventSlim();
        var startedExtractions = new ConcurrentDictionary<string, byte>();
        var completedExtractions = new ConcurrentDictionary<string, byte>();
        var parallelPipelineUsed = 0;
        try
        {
            CreateAuthoritativeParallelUpdateProject(
                projectRoot,
                implementationCount: 4);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            File.AppendAllText(Path.Combine(projectRoot, "Source00.cs"), "// cancel zero\n");
            File.AppendAllText(Path.Combine(projectRoot, "Source01.cs"), "// cancel one\n");
            var modifiedUtc = DateTime.UtcNow.AddSeconds(3);
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "Source00.cs"), modifiedUtc);
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "Source01.cs"), modifiedUtc);
            var cancelled = 0;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                workersStopped.Set;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    Interlocked.Exchange(ref parallelPipelineUsed, 1);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    startedExtractions.TryAdd(item.RelativePath, 0);
                }
                else if (item.Kind
                         == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedExtractions.TryAdd(item.RelativePath, 0);
                }
                var shouldCancel = cancelDuringExtraction
                    ? item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted
                    : item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.PersistenceCompleted;
                if (shouldCancel && Interlocked.Exchange(ref cancelled, 1) == 0)
                    cancellation.Cancel();
            };

            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "Source01.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ],
                cancellation);

            Assert.Equal(CommandExitCodes.Interrupted, exitCode);
            Assert.Equal(CommandErrorCodes.Interrupted, json.GetProperty("error_code").GetString());
            Assert.Contains(
                cancelDuringExtraction ? "(0 of " : "(1 of ",
                json.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            if (cancelDuringExtraction)
            {
                Assert.Equal(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            }
            else
            {
                Assert.NotEqual(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            }
            Assert.Equal(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            if (cancelDuringExtraction)
            {
                Assert.True(
                    workersStopped.Wait(TimeSpan.FromSeconds(30)),
                    "The cancelled parallel pipeline did not stop.");
                Assert.True(startedExtractions.Keys.All(completedExtractions.ContainsKey));
            }
        }
        finally
        {
            var cleanupSafe = Volatile.Read(ref parallelPipelineUsed) == 0
                || workersStopped.Wait(TimeSpan.FromSeconds(30));
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                previousWorkersStoppedHook;
            if (cleanupSafe)
                DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("symbols")]
    [InlineData("completed")]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelCancellationAfterLoadDemotesUntilRetry(
        string cancellationPoint)
    {
        var projectRoot = CreateTempProject();
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousWorkersStoppedHook =
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting;
        using var cancellation = new CancellationTokenSource();
        using var cancelledWorkerStarted = new ManualResetEventSlim();
        using var cancelledWorkerCompleted = new ManualResetEventSlim();
        using var workersStopped = new ManualResetEventSlim();
        var startedExtractions = new ConcurrentDictionary<string, byte>();
        var completedExtractions = new ConcurrentDictionary<string, byte>();
        var parallelPipelineUsed = 0;
        try
        {
            CreateAuthoritativeParallelUpdateProject(projectRoot, implementationCount: 3);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            foreach (var relativePath in new[] { "Source00.cs", "Source01.cs" })
            {
                var path = Path.Combine(projectRoot, relativePath);
                File.AppendAllText(path, "// cancel after validated load\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
            }
            var cancelled = 0;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                workersStopped.Set;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                (path, phase) =>
                {
                    if (path == "Source00.cs"
                        && cancellationPoint == "symbols"
                        && phase == "symbols"
                        && Interlocked.Exchange(ref cancelled, 1) == 0)
                    {
                        cancellation.Cancel();
                    }
                    return null;
                };
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    Interlocked.Exchange(ref parallelPipelineUsed, 1);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    startedExtractions.TryAdd(item.RelativePath, 0);
                    if (item.RelativePath == "Source00.cs")
                        cancelledWorkerStarted.Set();
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedExtractions.TryAdd(item.RelativePath, 0);
                    if (item.RelativePath == "Source00.cs"
                        && cancellationPoint == "completed"
                        && Interlocked.Exchange(ref cancelled, 1) == 0)
                    {
                        cancellation.Cancel();
                    }
                    if (item.RelativePath == "Source00.cs")
                        cancelledWorkerCompleted.Set();
                }
            };

            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "Source01.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ],
                cancellation);

            Assert.Equal(CommandExitCodes.Interrupted, exitCode);
            Assert.Equal(
                CommandErrorCodes.Interrupted,
                json.GetProperty("error_code").GetString());
            Assert.Equal(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.Equal(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            Assert.True(
                cancelledWorkerCompleted.Wait(TimeSpan.FromSeconds(30)),
                "The cancelled extraction worker did not converge.");
            Assert.True(
                workersStopped.Wait(TimeSpan.FromSeconds(30)),
                "The cancelled parallel pipeline did not stop.");
            Assert.True(startedExtractions.Keys.All(completedExtractions.ContainsKey));
            using (var interruptedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.NotEqual(
                    "true",
                    interruptedDb.GetMetaString(DbContext.BatchInProgressMetaKey));
            }
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(
                ["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            // No file transaction committed before interruption, so the general
            // completeness bit remains true while C# derived-data readiness is
            // deliberately degraded.
            Assert.True(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.False(
                statusJson.GetProperty("csharp_metadata_target_ready").GetBoolean());

            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            Assert.NotEqual(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.NotEqual(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            var (repairedStatusExitCode, repairedStatusJson) = RunStatusAndCaptureJson(
                ["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, repairedStatusExitCode);
            Assert.True(repairedStatusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(
                repairedStatusJson.GetProperty("csharp_metadata_target_ready").GetBoolean());
        }
        finally
        {
            var cleanupSafe = Volatile.Read(ref parallelPipelineUsed) == 0
                || workersStopped.Wait(TimeSpan.FromSeconds(30));
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                previousWorkersStoppedHook;
            if (cleanupSafe)
                DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_MixedLanguageBoundaryKeepsNonCSharpOnSerialConsumer()
    {
        var projectRoot = CreateTempProject();
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        try
        {
            CreateAuthoritativeParallelUpdateProject(
                projectRoot,
                implementationCount: 4);
            var pythonPath = Path.Combine(projectRoot, "script.py");
            File.WriteAllText(pythonPath, "def before():\n    return 1\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var pythonChecksumBefore = ReadIndexedChecksum(dbPath, "script.py");
            File.AppendAllText(Path.Combine(projectRoot, "Source00.cs"), "// mixed C#\n");
            File.WriteAllText(pythonPath, "def after():\n    return 2\n");
            var modifiedUtc = DateTime.UtcNow.AddSeconds(3);
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "Source00.cs"), modifiedUtc);
            File.SetLastWriteTimeUtc(pythonPath, modifiedUtc);
            var parallelPaths = new ConcurrentQueue<string>();
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    parallelPaths.Enqueue(item.RelativePath);
                }
            };

            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "script.py",
                    "--json",
                    "--parallelism",
                    "2",
                ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.NotEmpty(parallelPaths);
            Assert.All(
                parallelPaths,
                path => Assert.EndsWith(".cs", path, StringComparison.Ordinal));
            Assert.NotEqual(pythonChecksumBefore, ReadIndexedChecksum(dbPath, "script.py"));
        }
        finally
        {
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_PostExtractionHooksForceParallelFallback()
    {
        var projectRoot = CreateTempProject();
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        using var extensionProject =
            TestProjectHelper.CreateExecutableExtensionTestProjectScope(
                "cdidx_parallel_update_hook_fallback");
        using var env = EnvironmentVariableScope.Capture(
            PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
        lock (TestConsoleLock.Gate)
        {
            try
            {
                var hooksDir = Path.Combine(extensionProject.Root, "hooks");
                Directory.CreateDirectory(hooksDir);
                File.Copy(
                    typeof(CodeIndex.HookIsolationFixture.PathSelectivePostExtractionHook)
                        .Assembly.Location,
                    Path.Combine(hooksDir, "CodeIndex.HookIsolationFixture.dll"));
                CreateAuthoritativeParallelUpdateProject(
                    projectRoot,
                    implementationCount: 3);
                Assert.Equal(
                    CommandExitCodes.Success,
                    IndexCommandRunner.Run(
                        [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                        _jsonOptions));
                env.Set(
                    PostExtractionHookRunner.HooksDirectoryEnvironmentVariable,
                    hooksDir);
                File.WriteAllText(
                    Path.Combine(
                        projectRoot,
                        CodeIndex.HookIsolationFixture.HookIsolationFixtureEnvironment
                            .RemoveCSharpStaticInterfaceMemberMarkerFileName),
                    string.Empty);
                File.AppendAllText(
                    Path.Combine(projectRoot, "Source00.cs"),
                    "// hook fallback\n");
                File.SetLastWriteTimeUtc(
                    Path.Combine(projectRoot, "Source00.cs"),
                    DateTime.UtcNow.AddSeconds(3));
                (bool Enabled, string? Reason) scheduling = default;
                var parallelEvents = 0;
                IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                    (enabled, reason, _, _) => scheduling = (enabled, reason);
                IndexCommandRunner.UpdateParallelExtractionEventForTesting = _ =>
                    Interlocked.Increment(ref parallelEvents);

                var (exitCode, json) = RunAndCaptureJson(
                    [
                        projectRoot,
                        "--files",
                        "Source00.cs",
                        "--json",
                        "--quiet",
                        "--parallelism",
                        "2",
                    ]);
                Assert.True(
                    exitCode == CommandExitCodes.Success,
                    $"Unexpected hook fallback result: {json}");
                Assert.False(scheduling.Enabled);
                Assert.Equal("post_extraction_hooks", scheduling.Reason);
                Assert.Equal(0, parallelEvents);
            }
            finally
            {
                IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                    previousSchedulingHook;
                IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                    previousEventHook;
                DeleteDirectory(projectRoot);
            }
        }
    }

    [Fact]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelStallPreservesTerminalSideEffects()
    {
        var projectRoot = CreateTempProject();
        var previousTimeout =
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousWorkersStoppedHook =
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting;
        using var sourceZeroCompleted = new ManualResetEventSlim();
        using var stalledWorkerStarted = new ManualResetEventSlim();
        using var stalledWorkerEntered = new ManualResetEventSlim();
        using var releaseStalledWorker = new ManualResetEventSlim();
        using var stalledWorkerCompleted = new ManualResetEventSlim();
        using var workersStopped = new ManualResetEventSlim();
        var startedExtractions = new ConcurrentDictionary<string, byte>();
        var completedExtractions = new ConcurrentDictionary<string, byte>();
        var parallelPipelineUsed = 0;
        using var generatedPatterns = EnvironmentVariableScope.Capture(
            IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable);
        try
        {
            generatedPatterns.Set(
                IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable,
                "Source00.cs");
            CreateAuthoritativeParallelUpdateProject(
                projectRoot,
                implementationCount: 3);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroPath = Path.Combine(projectRoot, "Source00.cs");
            var sourceOnePath = Path.Combine(projectRoot, "Source01.cs");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            var sourceTwoChecksum = ReadIndexedChecksum(dbPath, "Source02.cs");
            File.AppendAllText(sourceZeroPath, "// persist before stalled target\n");
            File.AppendAllText(sourceOnePath, "// force stalled refresh\n");
            var modifiedUtc = DateTime.UtcNow.AddSeconds(3);
            File.SetLastWriteTimeUtc(sourceZeroPath, modifiedUtc);
            File.SetLastWriteTimeUtc(sourceOnePath, modifiedUtc);
            var parallelScheduled = false;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                workersStopped.Set;
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                (enabled, _, _, _) => parallelScheduled = enabled;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                (path, phase) =>
                {
                    if (path != "Source01.cs" || phase != "symbols")
                        return null;

                    stalledWorkerStarted.Set();
                    sourceZeroCompleted.Wait(TimeSpan.FromSeconds(30));
                    stalledWorkerEntered.Set();
                    releaseStalledWorker.Wait(TimeSpan.FromSeconds(30));
                    return null;
                };
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    Interlocked.Exchange(ref parallelPipelineUsed, 1);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    startedExtractions.TryAdd(item.RelativePath, 0);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedExtractions.TryAdd(item.RelativePath, 0);
                }
                if (item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted
                    && item.RelativePath == "Source00.cs")
                {
                    sourceZeroCompleted.Set();
                }
                if (item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted
                    && item.RelativePath == "Source01.cs")
                {
                    stalledWorkerCompleted.Set();
                }
            };
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = () =>
                TimeSpan.FromSeconds(3);

            var stalledRunStopwatch = Stopwatch.StartNew();
            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ]);
            stalledRunStopwatch.Stop();

            Assert.True(parallelScheduled);
            Assert.True(
                stalledRunStopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"The stalled update took {stalledRunStopwatch.Elapsed} to return.");
            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(
                CommandErrorCodes.IndexExtractionStalled,
                json.GetProperty("error_code").GetString());
            Assert.NotEqual(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.Equal(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            Assert.Equal(sourceTwoChecksum, ReadIndexedChecksum(dbPath, "Source02.cs"));
            var stalledMessage = json.GetProperty("message").GetString();
            Assert.True(
                stalledMessage?.Contains(" files processed)", StringComparison.Ordinal) == true
                && !stalledMessage.Contains("(0 of ", StringComparison.Ordinal),
                $"Unexpected stalled progress message: {stalledMessage}");
            Assert.Contains(
                "Source01.cs (symbols)",
                stalledMessage,
                StringComparison.Ordinal);
            Assert.True(stalledWorkerEntered.IsSet);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(
                    "true",
                    db.GetMetaString(DbContext.BatchInProgressMetaKey));
            }
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(
                ["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.False(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.False(
                statusJson.GetProperty("csharp_metadata_target_ready").GetBoolean());

            releaseStalledWorker.Set();
            Assert.True(
                stalledWorkerCompleted.Wait(TimeSpan.FromSeconds(30)),
                "The cancelled peer worker did not converge after its test block was released.");
            Assert.True(
                workersStopped.Wait(TimeSpan.FromSeconds(30)),
                "The stalled parallel pipeline did not stop.");
            Assert.True(startedExtractions.Keys.All(completedExtractions.ContainsKey));
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = previousTimeout;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [
                        projectRoot,
                        "--files",
                        "Source01.cs",
                        "--json",
                        "--quiet",
                        "--parallelism",
                        "1",
                    ],
                    _jsonOptions));
            Assert.NotEqual(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            using var repairedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.NotEqual(
                "true",
                repairedDb.GetMetaString(DbContext.BatchInProgressMetaKey));
            var (repairedStatusExitCode, repairedStatusJson) = RunStatusAndCaptureJson(
                ["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, repairedStatusExitCode);
            Assert.True(repairedStatusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(
                repairedStatusJson.GetProperty("csharp_metadata_target_ready").GetBoolean());
        }
        finally
        {
            releaseStalledWorker.Set();
            var cleanupSafe = Volatile.Read(ref parallelPipelineUsed) == 0
                || workersStopped.Wait(TimeSpan.FromSeconds(30));
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting =
                previousTimeout;
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                previousSchedulingHook;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                previousWorkersStoppedHook;
            if (cleanupSafe)
                DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelFatalResultCancelsBlockedPeerImmediately()
    {
        var projectRoot = CreateTempProject();
        var previousTimeout =
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousWorkersStoppedHook =
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting;
        using var blockedWorkerStarted = new ManualResetEventSlim();
        using var releaseBlockedWorker = new ManualResetEventSlim();
        using var blockedWorkerCompleted = new ManualResetEventSlim();
        using var workersStopped = new ManualResetEventSlim();
        var startedExtractions = new ConcurrentDictionary<string, byte>();
        var completedExtractions = new ConcurrentDictionary<string, byte>();
        var parallelPipelineUsed = 0;
        try
        {
            CreateAuthoritativeParallelUpdateProject(projectRoot, implementationCount: 3);
            File.WriteAllText(
                Path.Combine(projectRoot, "Source01.cs"),
                "public interface IAdditionalContract<T>\n"
                + "{\n"
                + "    static abstract T Create();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            var sourceTwoChecksum = ReadIndexedChecksum(dbPath, "Source02.cs");
            foreach (var relativePath in new[] { "Source00.cs", "Source01.cs", "Source02.cs" })
            {
                var path = Path.Combine(projectRoot, relativePath);
                File.AppendAllText(path, "// fatal-result cancellation\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
            }

            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = () =>
                TimeSpan.FromMinutes(5);
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                workersStopped.Set;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                (path, phase) =>
                {
                    if (path == "Source00.cs" && phase == "symbols")
                    {
                        blockedWorkerStarted.Set();
                        releaseBlockedWorker.Wait(TimeSpan.FromSeconds(30));
                        return null;
                    }
                    if (path == "Source01.cs" && phase == "references")
                    {
                        blockedWorkerStarted.Wait(TimeSpan.FromSeconds(30));
                        return new IndexCommandRunner.IndexExtractionStalledException(
                            0,
                            null,
                            TimeSpan.FromMilliseconds(10),
                            "Source01.cs [references]",
                            "injected fatal result");
                    }
                    return null;
                };
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    Interlocked.Exchange(ref parallelPipelineUsed, 1);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    startedExtractions.TryAdd(item.RelativePath, 0);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedExtractions.TryAdd(item.RelativePath, 0);
                }
                if (item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted
                    && item.RelativePath == "Source00.cs")
                {
                    blockedWorkerCompleted.Set();
                }
            };

            var stopwatch = Stopwatch.StartNew();
            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ]);
            stopwatch.Stop();

            Assert.True(blockedWorkerStarted.IsSet);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"The fatal worker result waited for its blocked peer for {stopwatch.Elapsed}.");
            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(
                CommandErrorCodes.IndexExtractionStalled,
                json.GetProperty("error_code").GetString());
            Assert.Contains(
                "Source01.cs [references]",
                json.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "injected fatal result",
                json.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.Equal(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            Assert.Equal(sourceTwoChecksum, ReadIndexedChecksum(dbPath, "Source02.cs"));

            releaseBlockedWorker.Set();
            Assert.True(
                blockedWorkerCompleted.Wait(TimeSpan.FromSeconds(30)),
                "The blocked peer did not converge after fatal-window cancellation.");
            Assert.True(
                workersStopped.Wait(TimeSpan.FromSeconds(30)),
                "The fatal parallel pipeline did not stop.");
            Assert.True(startedExtractions.Keys.All(completedExtractions.ContainsKey));
        }
        finally
        {
            releaseBlockedWorker.Set();
            var cleanupSafe = Volatile.Read(ref parallelPipelineUsed) == 0
                || workersStopped.Wait(TimeSpan.FromSeconds(30));
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting =
                previousTimeout;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                previousWorkersStoppedHook;
            if (cleanupSafe)
                DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelFatalDefersToEarlierSourceContractEvidence()
    {
        var projectRoot = CreateTempProject();
        var previousTimeout =
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        var previousSchedulingHook =
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting;
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousWorkersStoppedHook =
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting;
        using var blockedWorkerStarted = new ManualResetEventSlim();
        using var lateContractCompleted = new ManualResetEventSlim();
        using var releaseBlockedWorker = new ManualResetEventSlim();
        using var blockedWorkerCompleted = new ManualResetEventSlim();
        using var workersStopped = new ManualResetEventSlim();
        var startedExtractions = new ConcurrentDictionary<string, byte>();
        var completedExtractions = new ConcurrentDictionary<string, byte>();
        var parallelPipelineUsed = 0;
        try
        {
            CreateAuthoritativeParallelUpdateProject(projectRoot, implementationCount: 3);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            var sourceTwoChecksum = ReadIndexedChecksum(dbPath, "Source02.cs");

            // Preserve conservative prior contract evidence while making the new
            // authoritative preflight source-negative.
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: false);
            foreach (var relativePath in new[] { "Source00.cs", "Source02.cs" })
            {
                var path = Path.Combine(projectRoot, relativePath);
                File.AppendAllText(path, "// ordered fatal recovery\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
            }

            var sourceOnePath = Path.Combine(projectRoot, "Source01.cs");
            var sourceOneOriginal = File.ReadAllText(sourceOnePath);
            var sourceOneModifiedUtc = File.GetLastWriteTimeUtc(sourceOnePath);
            const string lateContractCore =
                "public interface ILateContract<T>\n"
                + "{\n"
                + "    static abstract T Create();\n"
                + "}\n";
            Assert.True(lateContractCore.Length <= sourceOneOriginal.Length);
            var lateContractSource = lateContractCore.PadRight(sourceOneOriginal.Length);
            Assert.Equal(
                Encoding.UTF8.GetByteCount(sourceOneOriginal),
                Encoding.UTF8.GetByteCount(lateContractSource));

            var parallelScheduled = false;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                workersStopped.Set;
            var lateContractMutated = 0;
            var sourceZeroBlocked = 0;
            var lateContractPayloads = new ConcurrentQueue<
                (int RetainedSymbols, bool HasSourceContract)>();
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                (enabled, _, workers, _) =>
                {
                    parallelScheduled = enabled;
                    if (enabled)
                        Assert.Equal(4, workers);
                };
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = () =>
                TimeSpan.FromMinutes(5);
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                (path, phase) =>
                {
                    if (path == "Source00.cs"
                        && phase == "symbols"
                        && Interlocked.Exchange(ref sourceZeroBlocked, 1) == 0)
                    {
                        blockedWorkerStarted.Set();
                        releaseBlockedWorker.Wait(TimeSpan.FromSeconds(30));
                        return null;
                    }
                    if (path == "Source02.cs" && phase == "symbols")
                    {
                        if (!lateContractCompleted.Wait(TimeSpan.FromSeconds(30)))
                        {
                            return new TimeoutException(
                                "The earlier source-contract extraction did not complete.");
                        }
                        return new IndexCommandRunner.IndexExtractionStalledException(
                            0,
                            null,
                            TimeSpan.FromMilliseconds(10),
                            "Source02.cs [symbols]",
                            "injected later fatal result");
                    }
                    return null;
                };
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    Interlocked.Exchange(ref parallelPipelineUsed, 1);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    startedExtractions.TryAdd(item.RelativePath, 0);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedExtractions.TryAdd(item.RelativePath, 0);
                }
                if (item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted
                    && item.RelativePath == "Source01.cs"
                    && Interlocked.Exchange(ref lateContractMutated, 1) == 0)
                {
                    File.WriteAllText(sourceOnePath, lateContractSource);
                    File.SetLastWriteTimeUtc(sourceOnePath, sourceOneModifiedUtc);
                }
                if (item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted
                    && item.RelativePath == "Source01.cs"
                    && item.HasSourceContractEvidence)
                {
                    lateContractPayloads.Enqueue(
                        (item.RetainedSymbolCount, item.HasSourceContractEvidence));
                    lateContractCompleted.Set();
                }
                if (item.Kind
                        == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted
                    && item.RelativePath == "Source00.cs")
                {
                    blockedWorkerCompleted.Set();
                }
            };

            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "Source01.cs",
                    "Source02.cs",
                    "--json",
                    "--parallelism",
                    "4",
                    "--max-symbols-per-file",
                    "1",
                ]);

            Assert.True(parallelScheduled);
            Assert.True(blockedWorkerStarted.IsSet);
            Assert.True(lateContractCompleted.IsSet);
            var lateContractPayload = Assert.Single(lateContractPayloads);
            Assert.Equal(0, lateContractPayload.RetainedSymbols);
            Assert.True(lateContractPayload.HasSourceContract);
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.NotEqual(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.Equal(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            Assert.NotEqual(sourceTwoChecksum, ReadIndexedChecksum(dbPath, "Source02.cs"));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.NotEqual(
                    "true",
                    db.GetMetaString(DbContext.BatchInProgressMetaKey));
            }

            releaseBlockedWorker.Set();
            Assert.True(
                blockedWorkerCompleted.Wait(TimeSpan.FromSeconds(30)),
                "The abandoned first-gap worker did not converge.");
            Assert.True(
                workersStopped.Wait(TimeSpan.FromSeconds(30)),
                "The source-evidence recovery pipeline did not stop.");
            Assert.True(startedExtractions.Keys.All(completedExtractions.ContainsKey));
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = previousTimeout;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            Assert.NotEqual(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            releaseBlockedWorker.Set();
            var cleanupSafe = Volatile.Read(ref parallelPipelineUsed) == 0
                || workersStopped.Wait(TimeSpan.FromSeconds(30));
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = previousTimeout;
            IndexCommandRunner.UpdateParallelExtractionSchedulingForTesting =
                previousSchedulingHook;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                previousWorkersStoppedHook;
            if (cleanupSafe)
                DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_AuthoritativeCSharpParallelFatalDefersWhileEarlierContractCandidateIsExtracting()
    {
        var projectRoot = CreateTempProject();
        var previousTimeout =
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting;
        var previousFailureHook =
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting;
        var previousEventHook =
            IndexCommandRunner.UpdateParallelExtractionEventForTesting;
        var previousWorkersStoppedHook =
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting;
        using var candidateWorkerEntered = new ManualResetEventSlim();
        using var releaseCandidateWorker = new ManualResetEventSlim();
        using var workersStopped = new ManualResetEventSlim();
        var startedExtractions = new ConcurrentDictionary<string, byte>();
        var completedExtractions = new ConcurrentDictionary<string, byte>();
        var parallelPipelineUsed = 0;
        try
        {
            CreateAuthoritativeParallelUpdateProject(projectRoot, implementationCount: 2);
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var sourceZeroChecksum = ReadIndexedChecksum(dbPath, "Source00.cs");
            var sourceOneChecksum = ReadIndexedChecksum(dbPath, "Source01.cs");
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: false);
            var sourceOnePath = Path.Combine(projectRoot, "Source01.cs");
            File.AppendAllText(sourceOnePath, "// candidate-order recovery\n");
            File.SetLastWriteTimeUtc(sourceOnePath, DateTime.UtcNow.AddSeconds(3));

            var sourceZeroPath = Path.Combine(projectRoot, "Source00.cs");
            var sourceZeroOriginal = File.ReadAllText(sourceZeroPath);
            var sourceZeroModifiedUtc = File.GetLastWriteTimeUtc(sourceZeroPath);
            const string candidateContractCore =
                "public interface ICandidateContract<T>\n"
                + "{\n"
                + "    static abstract T Create();\n"
                + "}\n";
            Assert.True(candidateContractCore.Length <= sourceZeroOriginal.Length);
            var candidateContractSource =
                candidateContractCore.PadRight(sourceZeroOriginal.Length);
            Assert.Equal(
                Encoding.UTF8.GetByteCount(sourceZeroOriginal),
                Encoding.UTF8.GetByteCount(candidateContractSource));

            var sourceZeroMutated = 0;
            var sourceZeroBlocked = 0;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                workersStopped.Set;
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = () =>
                TimeSpan.FromMinutes(5);
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                (path, phase) =>
                {
                    if (path == "Source00.cs"
                        && phase == "symbols"
                        && Interlocked.Exchange(ref sourceZeroBlocked, 1) == 0)
                    {
                        candidateWorkerEntered.Set();
                        releaseCandidateWorker.Wait(TimeSpan.FromSeconds(30));
                        return null;
                    }
                    if (path == "Source01.cs" && phase == "symbols")
                    {
                        if (!candidateWorkerEntered.Wait(TimeSpan.FromSeconds(30)))
                        {
                            return new TimeoutException(
                                "The earlier contract candidate did not reach symbol extraction.");
                        }
                        return new IndexCommandRunner.IndexExtractionStalledException(
                            0,
                            null,
                            TimeSpan.FromMilliseconds(10),
                            "Source01.cs [symbols]",
                            "injected fatal behind candidate");
                    }
                    return null;
                };
            IndexCommandRunner.UpdateParallelExtractionEventForTesting = item =>
            {
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionQueued)
                {
                    Interlocked.Exchange(ref parallelPipelineUsed, 1);
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionStarted)
                {
                    startedExtractions.TryAdd(item.RelativePath, 0);
                    if (item.RelativePath == "Source00.cs"
                        && Interlocked.Exchange(ref sourceZeroMutated, 1) == 0)
                    {
                        File.WriteAllText(sourceZeroPath, candidateContractSource);
                        File.SetLastWriteTimeUtc(
                            sourceZeroPath,
                            sourceZeroModifiedUtc);
                    }
                }
                if (item.Kind
                    == IndexCommandRunner.UpdateParallelExtractionEventKind.ExtractionCompleted)
                {
                    completedExtractions.TryAdd(item.RelativePath, 0);
                }
            };

            var stopwatch = Stopwatch.StartNew();
            var (exitCode, json) = RunAndCaptureJson(
                [
                    projectRoot,
                    "--files",
                    "Source00.cs",
                    "Source01.cs",
                    "--json",
                    "--parallelism",
                    "2",
                ]);
            stopwatch.Stop();

            Assert.True(candidateWorkerEntered.IsSet);
            Assert.DoesNotContain("Source00.cs", completedExtractions.Keys);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"The candidate-ordered serial recovery took {stopwatch.Elapsed}.");
            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.NotEqual(sourceOneChecksum, ReadIndexedChecksum(dbPath, "Source01.cs"));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.NotEqual(
                    "true",
                    db.GetMetaString(DbContext.BatchInProgressMetaKey));
            }

            releaseCandidateWorker.Set();
            Assert.True(
                workersStopped.Wait(TimeSpan.FromSeconds(30)),
                "The candidate-ordered parallel pipeline did not stop.");
            Assert.True(startedExtractions.Keys.All(completedExtractions.ContainsKey));
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = previousTimeout;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(
                    [projectRoot, "--json", "--quiet", "--parallelism", "1"],
                    _jsonOptions));
            Assert.NotEqual(sourceZeroChecksum, ReadIndexedChecksum(dbPath, "Source00.cs"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            releaseCandidateWorker.Set();
            var cleanupSafe = Volatile.Read(ref parallelPipelineUsed) == 0
                || workersStopped.Wait(TimeSpan.FromSeconds(30));
            IndexCommandRunner.IndexExtractionStallTimeoutForTesting = previousTimeout;
            IndexCommandRunner.UpdateParallelExtractionFailureForTesting =
                previousFailureHook;
            IndexCommandRunner.UpdateParallelExtractionEventForTesting =
                previousEventHook;
            IndexCommandRunner.UpdateParallelExtractionWorkersStoppedForTesting =
                previousWorkersStoppedHook;
            if (cleanupSafe)
                DeleteDirectory(projectRoot);
        }
    }

    private static IReadOnlyList<string> ReadStableUpdateProjection(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        var projections = new List<string>();
        Read(
            "files",
            "SELECT path, lang, size, lines, checksum, modified, generated FROM files ORDER BY path");
        Read(
            "chunks",
            "SELECT f.path, c.chunk_index, c.start_line, c.end_line, c.content FROM chunks c JOIN files f ON f.id = c.file_id ORDER BY f.path, c.chunk_index");
        Read(
            "symbols",
            "SELECT f.path, s.kind, s.sub_kind, s.name, s.line, s.start_line, s.start_column, s.end_line, s.body_start_line, s.body_end_line, s.signature, s.container_kind, s.container_name, s.container_qualified_name, s.family_key, s.visibility, s.return_type, s.is_partial_declaration, s.is_file_local_declaration, s.declaration_semantic_score, s.identifier_start_column, s.is_metadata_target, s.metadata_target_source, s.name_folded, s.display_name_folded FROM symbols s JOIN files f ON f.id = s.file_id ORDER BY f.path, s.line, s.start_column, s.kind, s.name, s.signature");
        Read(
            "reference_lines",
            "SELECT f.path, l.line, l.context FROM reference_lines l JOIN files f ON f.id = l.file_id ORDER BY f.path, l.line, l.context");
        Read(
            "references",
            "SELECT f.path, r.symbol_name, r.reference_kind, r.line, r.column_number, r.span_length, r.context, rl.line, rl.context, r.container_kind, r.container_name, r.symbol_name_folded, r.container_name_folded, r.is_self_reference, r.is_mutual_recursion, sf.path, ss.kind, ss.name, ss.line, ss.start_column, ss.signature, tf.path, ts.kind, ts.name, ts.line, ts.start_column, ts.signature, r.target_symbol_key, r.target_qualifier, r.resolution_state, r.resolution_candidate_count FROM symbol_references r JOIN files f ON f.id = r.file_id LEFT JOIN reference_lines rl ON rl.id = r.reference_line_id LEFT JOIN symbols ss ON ss.id = r.source_symbol_id LEFT JOIN files sf ON sf.id = ss.file_id LEFT JOIN symbols ts ON ts.id = r.target_symbol_id LEFT JOIN files tf ON tf.id = ts.file_id ORDER BY f.path, r.line, r.column_number, r.reference_kind, r.symbol_name, r.target_symbol_key, sf.path, ss.line, ss.start_column, tf.path, ts.line, ts.start_column");
        Read(
            "reference_candidates",
            "SELECT rf.path, r.symbol_name, r.reference_kind, r.line, r.column_number, r.span_length, r.context, sf.path, s.kind, s.name, s.line, s.start_column, s.signature, s.container_kind, s.container_name, s.container_qualified_name, s.family_key, s.is_metadata_target, s.metadata_target_source, c.scope_rank FROM symbol_reference_candidates c JOIN symbol_references r ON r.id = c.reference_id JOIN files rf ON rf.id = r.file_id JOIN symbols s ON s.id = c.symbol_id JOIN files sf ON sf.id = s.file_id ORDER BY rf.path, r.line, r.column_number, r.reference_kind, r.symbol_name, sf.path, s.line, s.start_column, s.kind, s.name, s.signature, c.scope_rank");
        Read(
            "hotspot_reference_counts",
            "SELECT f.path, h.lang, h.raw_symbol_name, h.symbol_name, h.symbol_segment_count, h.allow_leaf_fallback, h.reference_count, h.reference_score FROM hotspot_reference_counts h JOIN files f ON f.id = h.file_id ORDER BY f.path, h.lang, h.raw_symbol_name, h.symbol_name, h.symbol_segment_count, h.allow_leaf_fallback");
        Read(
            "issues",
            "SELECT f.path, i.kind, i.line, i.message, i.origin, i.severity FROM file_issues i JOIN files f ON f.id = i.file_id ORDER BY f.path, i.line, i.kind, i.message");
        Read("user_version", "PRAGMA user_version");
        Read(
            "meta",
            "SELECT key, value FROM codeindex_meta WHERE key IN ('batch_in_progress', 'index_completeness', 'index_incomplete_reasons_json', 'csharp_static_interface_source_evidence', 'csharp_symbol_name_contract_version', 'metadata_target_version_csharp', 'reference_identity_contract_version', 'fold_key_version', 'fold_key_fingerprint', 'fold_backfill_graph_refresh_pending', 'sql_graph_contract_version', 'hdl_graph_contract_version', 'symbols_only_graph_omitted', 'last_index_run_mode', 'last_index_run_files_scanned', 'last_index_run_files_skipped', 'last_index_run_parse_errors', 'last_index_run_bytes_read', 'last_index_run_bytes_read_skipped_file_count', 'last_index_run_bytes_read_incomplete', 'last_index_run_rows_upserted', 'last_index_run_rows_deleted') ORDER BY key");
        return projections;

        void Read(string table, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    values[index] = reader.IsDBNull(index)
                        ? "<null>"
                        : Convert.ToString(
                            reader.GetValue(index),
                            CultureInfo.InvariantCulture) ?? string.Empty;
                }
                projections.Add($"{table}:{string.Join('\u001f', values)}");
            }
        }
    }

    private static void CreateAuthoritativeParallelUpdateProject(
        string projectRoot,
        int implementationCount)
    {
        WriteParseableInterface(
            Path.Combine(projectRoot, "IParseable.cs"),
            hasStaticContract: true);
        for (var index = 0; index < implementationCount; index++)
        {
            File.WriteAllText(
                Path.Combine(projectRoot, $"Source{index:00}.cs"),
                $"public readonly struct Source{index:00} : IParseable<Source{index:00}>\n"
                + "{\n"
                + $"    public static Source{index:00} Parse(string value) => new();\n"
                + "}\n");
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpContractPreflightAvoidsRedundantWorkspacePasses()
    {
        var projectRoot = CreateTempProject();
        var previousPreflightHook = DbWriter.CSharpContractPreflightForTesting;
        var previousWorkspaceReadHook = DbWriter.CSharpContractWorkspaceReadForTesting;
        var previousUpdatePrepassHook = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: false);
            var implementationPath = Path.Combine(projectRoot, "Money.cs");
            File.WriteAllText(
                implementationPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));

            var preflightCount = 0;
            var workspaceReadCount = 0;
            var prepassCount = 0;
            DbWriter.CSharpContractPreflightForTesting = () => preflightCount++;
            DbWriter.CSharpContractWorkspaceReadForTesting = () => workspaceReadCount++;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = () => prepassCount++;
            File.AppendAllText(implementationPath, "// touched\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, scopedUpdateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Money.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, scopedUpdateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0, preflightCount);
            Assert.Equal(1, prepassCount);
            Assert.Equal(0, workspaceReadCount);

            preflightCount = 0;
            workspaceReadCount = 0;
            prepassCount = 0;
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));

            updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", "IParseable.cs", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0, preflightCount);
            Assert.Equal(2, prepassCount);
            Assert.Equal(1, workspaceReadCount);

            preflightCount = 0;
            workspaceReadCount = 0;
            prepassCount = 0;
            File.AppendAllText(implementationPath, "// persisted contract update\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(4));

            updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", "Money.cs", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0, preflightCount);
            Assert.Equal(1, prepassCount);
            Assert.Equal(1, workspaceReadCount);
        }
        finally
        {
            DbWriter.CSharpContractPreflightForTesting = previousPreflightHook;
            DbWriter.CSharpContractWorkspaceReadForTesting = previousWorkspaceReadHook;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousUpdatePrepassHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_InPlaceCdidxignoreChangeDuringExpandedPrepassDefersUntilCleanRetry()
    {
        var projectRoot = CreateTempProject();
        var previousPrepassHook = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        var ignorePath = Path.Combine(projectRoot, ".cdidxignore");
        try
        {
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            var moneyPath = Path.Combine(projectRoot, "Money.cs");
            File.WriteAllText(
                moneyPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "IHidden.cs"),
                "public interface IHidden<T> { static abstract T Create(); }\n");
            File.WriteAllText(ignorePath, "IHidden.cs\n");

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var moneyChecksumBefore = ReadIndexedChecksum(dbPath, "Money.cs");
            Assert.False(IndexedFileExists(projectRoot, "IHidden.cs"));

            File.AppendAllText(moneyPath, "// scoped update\n");
            File.SetLastWriteTimeUtc(moneyPath, DateTime.UtcNow.AddSeconds(2));
            var rootModifiedUtc = Directory.GetLastWriteTimeUtc(projectRoot);
            var prepassCalls = 0;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = () =>
            {
                Assert.Equal(1, Interlocked.Increment(ref prepassCalls));
                File.WriteAllText(ignorePath, "XHidden.cs\n");
                Directory.SetLastWriteTimeUtc(projectRoot, rootModifiedUtc);
            };

            var (partialExitCode, partialJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Money.cs", "--json"]);

            Assert.Equal(rootModifiedUtc, Directory.GetLastWriteTimeUtc(projectRoot));
            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal("partial", partialJson.GetProperty("status").GetString());
            Assert.False(partialJson.GetProperty("index_complete").GetBoolean());
            Assert.False(partialJson.GetProperty("graph_data_current").GetBoolean());
            Assert.True(partialJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, prepassCalls);
            Assert.Equal(moneyChecksumBefore, ReadIndexedChecksum(dbPath, "Money.cs"));
            Assert.False(IndexedFileExists(projectRoot, "IHidden.cs"));
            // The config mutation invalidates the expanded scan snapshot at its first
            // barrier, before any index-data mutation, so prior authoritative trust remains.
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            var recoveryExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.True(IndexedFileExists(projectRoot, "IHidden.cs"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("IParseable.py")]
    [InlineData("renamed/contract.py")]
    public void Run_UpdateFiles_PreHookSourceEvidencePreservesHiddenStaticInterfaceReferences(
        string transitionRelativePath)
    {
        var projectRoot = CreateTempProject();
        using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
            "cdidx_csharp_source_evidence_hook");
        using var env = EnvironmentVariableScope.Capture(
            PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
        try
        {
            var hooksDir = Path.Combine(extensionProject.Root, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(
                typeof(CodeIndex.HookIsolationFixture.PathSelectivePostExtractionHook).Assembly.Location,
                Path.Combine(hooksDir, "CodeIndex.HookIsolationFixture.dll"));
            env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);
            File.WriteAllText(
                Path.Combine(
                    projectRoot,
                    CodeIndex.HookIsolationFixture.HookIsolationFixtureEnvironment
                        .RemoveCSharpStaticInterfaceMemberMarkerFileName),
                string.Empty);

            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            var implementationPath = Path.Combine(projectRoot, "Money.cs");
            File.WriteAllText(
                implementationPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0L, ReadPersistedContractMemberCount());
            Assert.True(ReadSourceEvidence());

            File.AppendAllText(implementationPath, "// implementation-only update\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(2));

            var implementationUpdateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", "Money.cs", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, implementationUpdateExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0L, ReadPersistedContractMemberCount());
            Assert.True(ReadSourceEvidence());

            File.AppendAllText(implementationPath, "// full refresh update\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(3));

            var fullRefreshExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, fullRefreshExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Equal(0L, ReadPersistedContractMemberCount());
            Assert.True(ReadSourceEvidence());

            var transitionPath = Path.Combine(
                projectRoot,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(transitionRelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(transitionPath)!);
            File.Move(interfacePath, transitionPath);

            var transitionExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", transitionRelativePath, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, transitionExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.False(ReadSourceEvidence());

            long ReadPersistedContractMemberCount()
            {
                using var db = new DbContext(
                    DbOpenIntent.WriteIndex,
                    Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
                using var command = db.Connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*)
                    FROM symbols s
                    JOIN files f ON f.id = s.file_id
                    WHERE f.path = 'IParseable.cs'
                      AND s.container_kind = 'interface'
                      AND s.name = 'Parse'
                    """;
                return (long)command.ExecuteScalar()!;
            }

            bool ReadSourceEvidence()
            {
                using var db = new DbContext(
                    DbOpenIntent.WriteIndex,
                    Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
                return bool.Parse(
                    db.GetMetaString(DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey)!);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("IParseable.py")]
    [InlineData("renamed/contract.py")]
    public void Run_UpdateFiles_OneSidedCsharpContractRenameRefreshesVisibleReferences(
        string retainedRelativePath)
    {
        var projectRoot = CreateTempProject();
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            var retainedPath = Path.Combine(
                projectRoot,
                FileIndexer.NormalizeRelativePathForCurrentPlatform(retainedRelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
            File.Move(interfacePath, retainedPath);

            var updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", retainedRelativePath, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            using var command = db.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE path = 'IParseable.cs'";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
            Assert.Equal(
                bool.FalseString,
                db.GetMetaString(DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_ChangedExistingRetainedTargetPreplansMatchingCsharpAlias()
    {
        var projectRoot = CreateTempProject();
        var previousCleanupChecksumHook = IndexCommandRunner.UpdateCleanupChecksumReadForTesting;
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            var contractContent = File.ReadAllText(interfacePath);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            var retainedPath = Path.Combine(projectRoot, "retained.py");
            File.WriteAllText(retainedPath, "print('before')\n");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            File.Delete(interfacePath);
            File.WriteAllText(retainedPath, contractContent);
            File.SetLastWriteTimeUtc(retainedPath, DateTime.UtcNow.AddSeconds(2));
            var cleanupChecksumReads = 0;
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = path =>
            {
                Assert.Equal("retained.py", path);
                cleanupChecksumReads++;
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "retained.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, cleanupChecksumReads);
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.False(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.True(IndexedFileExists(projectRoot, "retained.py"));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.False(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = previousCleanupChecksumHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_CommonChecksumPreWorkspaceCleanupQueriesCSharpCandidatesOnce()
    {
        const int targetCount = 16;
        var projectRoot = CreateTempProject();
        try
        {
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            var relativePaths = new string[targetCount];
            for (var index = 0; index < targetCount; index++)
            {
                var relativePath = $"shared-{index:D2}.cs";
                relativePaths[index] = relativePath;
                File.WriteAllText(
                    Path.Combine(projectRoot, relativePath),
                    "// identical C# cleanup target\n");
            }

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            var updateArgs = new List<string>(targetCount + 5)
            {
                projectRoot,
                "--files",
            };
            updateArgs.AddRange(relativePaths);
            updateArgs.Add("--json");
            updateArgs.Add("--quiet");

            int updateExitCode;
            List<QueryProfileEntry> profile;
            DbDebug.BeginProfile();
            try
            {
                updateExitCode = IndexCommandRunner.Run([.. updateArgs], _jsonOptions);
            }
            finally
            {
                profile = DbDebug.EndProfile();
            }

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            var checksumQuery = Assert.Single(
                profile.Where(entry => entry.Sql == DbWriter.StaleCSharpChecksumCandidateSql));
            Assert.Equal(targetCount - 1, checksumQuery.RowsScanned);
            Assert.Contains(
                checksumQuery.QueryPlan,
                row => row.Detail.Contains("idx_files_checksum", StringComparison.Ordinal));
        }
        finally
        {
            _ = DbDebug.EndProfile();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_ChangedContentAsciiCaseOnlyRenameRemovesOldAlias()
    {
        var projectRoot = CreateTempProject();
        try
        {
            if (PathCasing.ComparisonFor(projectRoot) != StringComparison.OrdinalIgnoreCase)
                return;

            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);
            var oldPath = Path.Combine(sourceDirectory, "target.cs");
            var temporaryPath = Path.Combine(sourceDirectory, "rename.tmp");
            var retainedPath = Path.Combine(sourceDirectory, "Target.cs");
            File.WriteAllText(oldPath, "public class Target { public int Before => 1; }\n");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var oldChecksum = ReadIndexedChecksum(dbPath, "src/target.cs");

            File.Move(oldPath, temporaryPath);
            File.WriteAllText(temporaryPath, "public class Target { public int After => 222; }\n");
            File.Move(temporaryPath, retainedPath);

            var updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", "src/Target.cs", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(IndexedFileExists(projectRoot, "src/target.cs"));
            Assert.True(IndexedFileExists(projectRoot, "src/Target.cs"));
            Assert.NotEqual(oldChecksum, ReadIndexedChecksum(dbPath, "src/Target.cs"));
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE path = 'src/Target.cs' COLLATE NOCASE";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("日本", "target.cs", "Target.cs")]
    [InlineData("src", "é.cs", "É.cs")]
    public void Run_UpdateCommits_CaseOnlyRenameUsesExactGitSourceWithoutChecksumRead(
        string relativeDirectory,
        string oldFileName,
        string retainedFileName)
    {
        var projectRoot = CreateTempProject();
        var previousCleanupChecksumHook = IndexCommandRunner.UpdateCleanupChecksumReadForTesting;
        try
        {
            if (PathCasing.ComparisonFor(projectRoot) != StringComparison.OrdinalIgnoreCase)
                return;

            RunGit(projectRoot, "init");
            var sourceDirectory = Path.Combine(projectRoot, relativeDirectory);
            Directory.CreateDirectory(sourceDirectory);
            var oldPath = Path.Combine(sourceDirectory, oldFileName);
            var retainedPath = Path.Combine(sourceDirectory, retainedFileName);
            File.WriteAllText(oldPath, "public class Target { public int Value => 1; }\n");
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial casing");

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var oldIndexPath = FileIndexer.NormalizePathSeparators(
                $"{relativeDirectory}/{oldFileName}");
            var retainedIndexPath = FileIndexer.NormalizePathSeparators(
                $"{relativeDirectory}/{retainedFileName}");
            var oldChecksum = ReadIndexedChecksum(dbPath, oldIndexPath);

            var temporaryGitPath = $"{relativeDirectory}/rename.tmp";
            RunGit(projectRoot, "mv", oldIndexPath, temporaryGitPath);
            RunGit(projectRoot, "mv", temporaryGitPath, retainedIndexPath);
            File.WriteAllText(retainedPath, "public class Target { public int Value => 222; }\n");
            // Some generally case-insensitive filesystems do not alias every Unicode case
            // pair. The ASCII filename case below a non-ASCII directory always exercises
            // this contract; skip only the optional Unicode-pair row when it is distinct.
            if (!File.Exists(oldPath))
                return;
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "rename casing");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            var changedPaths = GitHelper.GetChangedFilesFromCommit(projectRoot, commitId);
            Assert.Contains(oldIndexPath, changedPaths);
            Assert.Contains(retainedIndexPath, changedPaths);
            var cleanupChecksumReads = new List<string>();
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = cleanupChecksumReads.Add;

            var updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--commits", commitId, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Empty(cleanupChecksumReads);
            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.DoesNotContain(oldIndexPath, indexedPaths);
            Assert.Contains(retainedIndexPath, indexedPaths);
            Assert.NotEqual(oldChecksum, ReadIndexedChecksum(dbPath, retainedIndexPath));
            var indexedAliases = indexedPaths
                .Where(path => string.Equals(path, retainedIndexPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(new[] { retainedIndexPath }, indexedAliases);
            using var db = new DbContext(
                DbOpenIntent.WriteIndex,
                dbPath);
            using var command = db.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM files WHERE path = @old OR path = @retained";
            command.Parameters.AddWithValue("@old", oldIndexPath);
            command.Parameters.AddWithValue("@retained", retainedIndexPath);
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = previousCleanupChecksumHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateCommits_CaseFoldedDistinctLiveFilesPreserveBothExactRows()
    {
        var projectRoot = CreateTempProject();
        var previousCleanupChecksumHook = IndexCommandRunner.UpdateCleanupChecksumReadForTesting;
        try
        {
            RunGit(projectRoot, "init");
            var sourceDirectory = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(sourceDirectory);
            var persistedPath = Path.Combine(sourceDirectory, "é.cs");
            var retainedPath = Path.Combine(sourceDirectory, "É.cs");
            File.WriteAllText(persistedPath, "public class LowerAccent { public int Value => 1; }\n");
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial folded path");

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            File.WriteAllText(persistedPath, "public class LowerAccent { public int Value => 22; }\n");
            File.WriteAllText(retainedPath, "public class UpperAccent { public int Value => 333; }\n");
            if (!FileIndexer.TryGetFileIdentity(persistedPath, out var persistedIdentity)
                || !FileIndexer.TryGetFileIdentity(retainedPath, out var retainedIdentity)
                || persistedIdentity == retainedIdentity)
            {
                return;
            }

            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "retain distinct folded paths");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            var cleanupChecksumReads = new List<string>();
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = cleanupChecksumReads.Add;

            var updateExitCode = IndexCommandRunner.Run(
                [projectRoot, "--commits", commitId, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Empty(cleanupChecksumReads);
            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("src/é.cs", indexedPaths);
            Assert.Contains("src/É.cs", indexedPaths);
        }
        finally
        {
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = previousCleanupChecksumHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_FatalCsharpExpansionScanPreservesHookHiddenContractWorkspace()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var previousExtractionHook = IndexCommandRunner.UpdateExtractionWorkStartedForTesting;
        using var extensionProject = TestProjectHelper.CreateExecutableExtensionTestProjectScope(
            "cdidx_csharp_incomplete_expansion_hook");
        using var env = EnvironmentVariableScope.Capture(
            PostExtractionHookRunner.HooksDirectoryEnvironmentVariable);
        var unreadableDirectory = Path.Combine(projectRoot, "unreadable");
        try
        {
            var hooksDir = Path.Combine(extensionProject.Root, "hooks");
            Directory.CreateDirectory(hooksDir);
            File.Copy(
                typeof(CodeIndex.HookIsolationFixture.PathSelectivePostExtractionHook).Assembly.Location,
                Path.Combine(hooksDir, "CodeIndex.HookIsolationFixture.dll"));
            env.Set(PostExtractionHookRunner.HooksDirectoryEnvironmentVariable, hooksDir);
            File.WriteAllText(
                Path.Combine(
                    projectRoot,
                    CodeIndex.HookIsolationFixture.HookIsolationFixtureEnvironment
                        .RemoveCSharpStaticInterfaceMemberMarkerFileName),
                string.Empty);

            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            var implementationPath = Path.Combine(projectRoot, "Money.cs");
            File.WriteAllText(
                implementationPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            var stalePythonPath = Path.Combine(projectRoot, "legacy.py");
            var retainedPythonPath = Path.Combine(projectRoot, "retained.py");
            File.WriteAllText(stalePythonPath, "print('retained during C# deferral')\n");
            Directory.CreateDirectory(unreadableDirectory);
            File.WriteAllText(
                Path.Combine(unreadableDirectory, "Blocked.cs"),
                "public class Blocked { }\n");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var originalChecksum = ReadIndexedChecksum(dbPath, "Money.cs");
            Assert.NotNull(originalChecksum);

            File.AppendAllText(implementationPath, "// must remain pending\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(2));
            File.Move(stalePythonPath, retainedPythonPath);
            File.SetUnixFileMode(unreadableDirectory, UnixFileMode.None);
            var extractionStarts = 0;
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = () => extractionStarts++;

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Money.cs", "retained.py", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.False(updateJson.GetProperty("index_complete").GetBoolean());
            Assert.Equal(0, extractionStarts);
            Assert.Equal(originalChecksum, ReadIndexedChecksum(dbPath, "Money.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.True(IndexedFileExists(projectRoot, "legacy.py"));
            Assert.False(IndexedFileExists(projectRoot, "retained.py"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = previousExtractionHook;
            if (Directory.Exists(unreadableDirectory))
            {
                File.SetUnixFileMode(
                    unreadableDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_ExpandedScanValidatesInputExactlyBeforeWriteAndReadiness()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var previousBarrierHook = IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting;
        var phases = new List<string>();
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            File.AppendAllText(interfacePath, "// selected update\n");
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = phases.Add;

            var exitCode = IndexCommandRunner.Run(
                [projectRoot, "--files", "IParseable.cs", "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(["before_write", "before_readiness"], phases);
        }
        finally
        {
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_FirstExpandedSnapshotBarrierDriftPreservesAllRowsAndTrust()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var toolPath = Path.Combine(projectRoot, "tool.py");
        var obsoletePath = Path.Combine(projectRoot, "obsolete.md");
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousBarrierHook = IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting;
        var phases = new List<string>();
        try
        {
            File.WriteAllText(ignorePath, "never-match-a\n");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            File.WriteAllText(toolPath, "def run():\n    return 1\n");
            File.WriteAllText(obsoletePath, "# Obsolete\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var priorInterfaceChecksum = ReadIndexedChecksum(dbPath, "IParseable.cs");
            var priorToolChecksum = ReadIndexedChecksum(dbPath, "tool.py");
            int priorReadiness;
            string? priorIndexComplete;
            string? priorFtsRecoveryMarker;
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                priorReadiness = db.GetUserVersion();
                priorIndexComplete = db.GetMetaString(DbContext.IndexCompletenessMetaKey);
                var priorWriter = new DbWriter(db);
                priorWriter.MarkFtsBulkLoadRecoveryNeeded();
                priorFtsRecoveryMarker = db.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey);
            }

            File.AppendAllText(interfacePath, "// selected update\n");
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            File.WriteAllText(toolPath, "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddSeconds(3));
            File.Delete(obsoletePath);
            var ignoreModifiedUtc = File.GetLastWriteTimeUtc(ignorePath);
            var rootModifiedUtc = Directory.GetLastWriteTimeUtc(projectRoot);
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = phase =>
            {
                phases.Add(phase);
                if (phase != "before_write")
                    return;
                File.WriteAllText(ignorePath, "never-match-b\n");
                File.SetLastWriteTimeUtc(ignorePath, ignoreModifiedUtc);
                Directory.SetLastWriteTimeUtc(projectRoot, rootModifiedUtc);
            };

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--files", "IParseable.cs", "tool.py", "obsolete.md", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(["before_write"], phases);
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            using var preservedDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(priorReadiness, preservedDb.GetUserVersion());
            Assert.Equal(priorIndexComplete, preservedDb.GetMetaString(DbContext.IndexCompletenessMetaKey));
            Assert.Equal(
                priorFtsRecoveryMarker,
                preservedDb.GetMetaString(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.Equal(priorInterfaceChecksum, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.Equal(priorToolChecksum, ReadIndexedChecksum(dbPath, "tool.py"));
            Assert.True(IndexedFileExists(projectRoot, "obsolete.md"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_FinalExpandedSnapshotBarrierDriftPersistsFilesButBlocksReadiness()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var toolPath = Path.Combine(projectRoot, "tool.py");
        var stablePath = Path.Combine(projectRoot, "stable.md");
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        var previousBarrierHook = IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting;
        var phases = new List<string>();
        try
        {
            File.WriteAllText(ignorePath, "never-match-a\n");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            File.WriteAllText(toolPath, "def run():\n    return 1\n");
            File.WriteAllText(stablePath, "# Stable\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));

            var priorInterfaceChecksum = ReadIndexedChecksum(dbPath, "IParseable.cs");
            var priorToolChecksum = ReadIndexedChecksum(dbPath, "tool.py");
            var priorStableChecksum = ReadIndexedChecksum(dbPath, "stable.md");
            File.AppendAllText(interfacePath, "// selected update\n");
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            File.WriteAllText(toolPath, "def run():\n    return 2\n");
            File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddSeconds(3));
            var ignoreModifiedUtc = File.GetLastWriteTimeUtc(ignorePath);
            var rootModifiedUtc = Directory.GetLastWriteTimeUtc(projectRoot);
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = phase =>
            {
                phases.Add(phase);
                if (phase != "before_readiness")
                    return;
                File.WriteAllText(ignorePath, "never-match-b\n");
                File.SetLastWriteTimeUtc(ignorePath, ignoreModifiedUtc);
                Directory.SetLastWriteTimeUtc(projectRoot, rootModifiedUtc);
            };

            var (exitCode, json) = RunAndCaptureJson(
                [projectRoot, "--files", "IParseable.cs", "tool.py", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(["before_write", "before_readiness"], phases);
            Assert.Equal(3, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.True(json.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.False(json.GetProperty("graph_data_current").GetBoolean());
            Assert.NotEqual(priorInterfaceChecksum, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.NotEqual(priorToolChecksum, ReadIndexedChecksum(dbPath, "tool.py"));
            Assert.Equal(priorStableChecksum, ReadIndexedChecksum(dbPath, "stable.md"));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.NotEqual(0, db.GetUserVersion() & DbContext.GraphReadyFlag);
            Assert.Equal(0, db.GetUserVersion() & DbContext.IssuesReadyFlag);
            Assert.Equal("incomplete", db.GetMetaString(DbContext.IndexCompletenessMetaKey));
        }
        finally
        {
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_UpdateFiles_CsharpTargetDriftAfterWorkspaceReadPreservesRowsUntilCleanRetry(
        bool deleteImplementation)
    {
        var projectRoot = CreateTempProject();
        var previousWorkspaceReadHook = DbWriter.CSharpContractWorkspaceReadForTesting;
        try
        {
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            var implementationPath = Path.Combine(projectRoot, "Money.cs");
            File.WriteAllText(
                implementationPath,
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var originalChecksum = ReadIndexedChecksum(dbPath, "Money.cs");
            Assert.NotNull(originalChecksum);

            File.AppendAllText(implementationPath, "// selected update\n");
            File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(2));
            var workspaceReadCount = 0;
            DbWriter.CSharpContractWorkspaceReadForTesting = () =>
            {
                if (Interlocked.Increment(ref workspaceReadCount) != 1)
                    return;

                if (deleteImplementation)
                {
                    File.Delete(implementationPath);
                    return;
                }

                File.AppendAllText(implementationPath, "// changed after workspace read\n");
                File.SetLastWriteTimeUtc(implementationPath, DateTime.UtcNow.AddSeconds(4));
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "Money.cs", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, workspaceReadCount);
            Assert.Equal(originalChecksum, ReadIndexedChecksum(dbPath, "Money.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            if (deleteImplementation)
            {
                // Deletion changes directory membership, so the first scan-input barrier
                // preserves prior trust before mutation. In-place content drift leaves the
                // directory snapshot stable and is demoted later in the mutation phase.
                Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            }
            else
            {
                Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            }

            DbWriter.CSharpContractWorkspaceReadForTesting = null;
            var retryExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, retryExitCode);
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            if (deleteImplementation)
            {
                Assert.False(IndexedFileExists(projectRoot, "Money.cs"));
                Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            }
            else
            {
                Assert.True(IndexedFileExists(projectRoot, "Money.cs"));
                Assert.NotEqual(originalChecksum, ReadIndexedChecksum(dbPath, "Money.cs"));
                Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            }
        }
        finally
        {
            DbWriter.CSharpContractWorkspaceReadForTesting = previousWorkspaceReadHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpExpandedScanLateContractInEnumeratedDirectoryDefersUntilRetry()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var lateContractPath = Path.Combine(projectRoot, "LateContract.cs");
        var previousPrepassHook = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
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

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var indexedChecksumBefore = ReadIndexedChecksum(dbPath, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: false);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            var prepassCount = 0;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = () =>
            {
                if (Interlocked.Increment(ref prepassCount) != 1)
                    return;

                File.WriteAllText(
                    lateContractPath,
                    "public interface ILateContract<T>\n"
                    + "{\n"
                    + "    static abstract T Parse(string value);\n"
                    + "}\n");
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "IParseable.cs", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, prepassCount);
            Assert.Equal(indexedChecksumBefore, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.False(IndexedFileExists(projectRoot, "LateContract.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.True(IndexedFileExists(projectRoot, "LateContract.cs"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpExpandedScanLateContractDuringTargetLoopRefusesFinalStamp()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var lateContractPath = Path.Combine(projectRoot, "LateContract.cs");
        var previousCommittedHook = IndexCommandRunner.UpdateFileCommittedForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            WriteParseableInterface(interfacePath, hasStaticContract: false);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            var hookCount = 0;
            IndexCommandRunner.UpdateFileCommittedForTesting = (_, _) =>
            {
                if (Interlocked.Increment(ref hookCount) != 1)
                    return;

                File.WriteAllText(
                    lateContractPath,
                    "public interface ILateContract<T>\n"
                    + "{\n"
                    + "    static abstract T Parse(string value);\n"
                    + "}\n");
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "IParseable.cs", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.True(hookCount > 0);
            Assert.False(IndexedFileExists(projectRoot, "LateContract.cs"));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            IndexCommandRunner.UpdateFileCommittedForTesting = previousCommittedHook;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.True(IndexedFileExists(projectRoot, "LateContract.cs"));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateFileCommittedForTesting = previousCommittedHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_CsharpExpandedScanIgnoresChurnInsideSkippedDirectory()
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var ignoredDirectory = Path.Combine(projectRoot, "ignored");
        var previousPrepassHook = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        try
        {
            Directory.CreateDirectory(ignoredDirectory);
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "ignored/\n");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            WriteParseableInterface(interfacePath, hasStaticContract: false);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            var prepassCount = 0;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = () =>
            {
                if (Interlocked.Increment(ref prepassCount) == 1)
                {
                    File.WriteAllText(
                        Path.Combine(ignoredDirectory, "IgnoredLateContract.cs"),
                        "public interface IIgnored<T> { static abstract T Parse(string value); }\n");
                }
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "IParseable.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, prepassCount);
            Assert.False(IndexedFileExists(projectRoot, "ignored/IgnoredLateContract.cs"));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.False(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateFiles_IgnoredContractIsExcludedFromExpandedCsharpWorkspace()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
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
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "IParseable.cs\n");
            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "IParseable.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.False(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.False(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_UpdateFiles_CsharpIntentionalSkipAfterPrepassDefersChangedContractUntilRetry(
        bool oversized)
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var previousContentLoadHook = IndexCommandRunner.UpdateFileContentLoadForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
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

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var indexedChecksumBefore = ReadIndexedChecksum(dbPath, "IParseable.cs");
            var mutationCount = 0;
            IndexCommandRunner.UpdateFileContentLoadForTesting = path =>
            {
                if (!string.Equals(path, "IParseable.cs", StringComparison.Ordinal)
                    || Interlocked.Increment(ref mutationCount) != 1)
                {
                    return;
                }

                if (oversized)
                    File.WriteAllText(interfacePath, new string('x', 2048));
                else
                    File.WriteAllBytes(interfacePath, [0, 1, 2, 3]);
                File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));
            };

            string[] updateArgs = oversized
                ? [projectRoot, "--files", "Money.cs", "--max-file-bytes", "1024", "--json"]
                : [projectRoot, "--files", "Money.cs", "--json"];
            var (updateExitCode, updateJson) = RunAndCaptureJson(updateArgs);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, mutationCount);
            Assert.Equal(indexedChecksumBefore, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            using (var partialDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.NotEqual(
                    bool.TrueString,
                    partialDb.GetMetaString(DbContext.BatchInProgressMetaKey));

            IndexCommandRunner.UpdateFileContentLoadForTesting = previousContentLoadHook;
            string[] retryArgs = oversized
                ? [projectRoot, "--max-file-bytes", "1024", "--json", "--quiet"]
                : [projectRoot, "--json", "--quiet"];
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(retryArgs, _jsonOptions));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.False(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_UpdateFiles_CsharpIntentionalSkipRecordDriftRollsBackBeforeUpsert(
        bool oversized)
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var previousSkippedRecordHook = IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
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

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var indexedChecksumBefore = ReadIndexedChecksum(dbPath, "IParseable.cs");
            if (oversized)
                File.WriteAllText(interfacePath, new string('x', 2048));
            else
                File.WriteAllBytes(interfacePath, [0, 1, 2, 3]);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));

            var mutationCount = 0;
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting = (path, _) =>
            {
                if (!string.Equals(path, "IParseable.cs", StringComparison.Ordinal)
                    || Interlocked.Increment(ref mutationCount) != 1)
                {
                    return;
                }

                WriteParseableInterface(interfacePath, hasStaticContract: true);
                File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(6));
            };

            string[] updateArgs = oversized
                ? [projectRoot, "--files", "IParseable.cs", "--max-file-bytes", "1024", "--json"]
                : [projectRoot, "--files", "IParseable.cs", "--json"];
            var (updateExitCode, updateJson) = RunAndCaptureJson(updateArgs);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, mutationCount);
            Assert.Equal(indexedChecksumBefore, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            using (var partialDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.NotEqual(
                    bool.TrueString,
                    partialDb.GetMetaString(DbContext.BatchInProgressMetaKey));

            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting = previousSkippedRecordHook;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting = previousSkippedRecordHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_UpdateFiles_IntentionalSkipUnexpectedWriteFailureClearsBatchMarker(
        bool oversized)
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var previousSkippedRecordHook = IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
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

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var indexedChecksumBefore = ReadIndexedChecksum(dbPath, "IParseable.cs");
            if (oversized)
                File.WriteAllText(interfacePath, new string('x', 2048));
            else
                File.WriteAllBytes(interfacePath, [0, 1, 2, 3]);
            File.SetLastWriteTimeUtc(interfacePath, DateTime.UtcNow.AddSeconds(3));

            var hookCount = 0;
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting = (path, _) =>
            {
                Assert.Equal("IParseable.cs", path);
                Interlocked.Increment(ref hookCount);
                throw new InvalidOperationException("injected skipped-record write failure");
            };

            string[] updateArgs = oversized
                ? [projectRoot, "--files", "IParseable.cs", "--max-file-bytes", "1024", "--json"]
                : [projectRoot, "--files", "IParseable.cs", "--json"];
            var (updateExitCode, updateJson) = RunAndCaptureJson(updateArgs);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, hookCount);
            Assert.Equal(indexedChecksumBefore, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            using var partialDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.NotEqual(
                bool.TrueString,
                partialDb.GetMetaString(DbContext.BatchInProgressMetaKey));
        }
        finally
        {
            IndexCommandRunner.UpdateSkippedFileRecordBuiltForTesting = previousSkippedRecordHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_UpdateFiles_IntentionalSkipCleanupDriftClearsBatchMarkerAndPreservesContract(
        bool oversized)
    {
        var projectRoot = CreateTempProject();
        var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
        var retainedPath = Path.Combine(projectRoot, "IParseable.py");
        var previousPrepassHook = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        try
        {
            WriteParseableInterface(interfacePath, hasStaticContract: true);
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

            if (oversized)
                File.WriteAllText(retainedPath, new string('x', 2048));
            else
                File.WriteAllBytes(retainedPath, [0, 1, 2, 3]);

            var prepassCount = 0;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = () =>
            {
                if (Interlocked.Increment(ref prepassCount) == 1)
                    File.Delete(interfacePath);
            };

            string[] updateArgs = oversized
                ? [projectRoot, "--files", "Money.cs", "IParseable.py", "--max-file-bytes", "1024", "--json"]
                : [projectRoot, "--files", "Money.cs", "IParseable.py", "--json"];
            var (updateExitCode, updateJson) = RunAndCaptureJson(updateArgs);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, prepassCount);
            Assert.True(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.False(IndexedFileExists(projectRoot, "IParseable.py"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            // Removing the interface changes directory membership and fails the first
            // scan-input barrier, so no batch/evidence mutation precedes the retry.
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var partialDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.NotEqual(
                    bool.TrueString,
                    partialDb.GetMetaString(DbContext.BatchInProgressMetaKey));

            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            string[] retryArgs = oversized
                ? [projectRoot, "--max-file-bytes", "1024", "--json", "--quiet"]
                : [projectRoot, "--json", "--quiet"];
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run(retryArgs, _jsonOptions));
            Assert.False(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.True(IndexedFileExists(projectRoot, "IParseable.py"));
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.False(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("stable.py", "print('stable')\n")]
    [InlineData("stable.js", "export const stable = true;\n")]
    public void Run_UpdateFiles_UnchangedNonCsharpTargetWithPositiveContractEvidenceNeedsNoContentRead(
        string relativePath,
        string content)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var previousExtractionHook = IndexCommandRunner.UpdateExtractionWorkStartedForTesting;
        var previousCleanupChecksumHook = IndexCommandRunner.UpdateCleanupChecksumReadForTesting;
        var targetPath = Path.Combine(projectRoot, relativePath);
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
            File.WriteAllText(targetPath, content);

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var originalChecksum = ReadIndexedChecksum(dbPath, relativePath);
            Assert.NotNull(originalChecksum);

            File.SetUnixFileMode(targetPath, UnixFileMode.None);
            var extractionStarts = 0;
            var cleanupChecksumReads = 0;
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = () => extractionStarts++;
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = _ => cleanupChecksumReads++;

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", relativePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, updateJson.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, extractionStarts);
            Assert.Equal(0, cleanupChecksumReads);
            Assert.Equal(originalChecksum, ReadIndexedChecksum(dbPath, relativePath));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateExtractionWorkStartedForTesting = previousExtractionHook;
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = previousCleanupChecksumHook;
            if (File.Exists(targetPath))
                File.SetUnixFileMode(targetPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    private static bool? ReadCSharpStaticInterfaceSourceEvidence(string projectRoot)
    {
        using var db = new DbContext(
            DbOpenIntent.WriteIndex,
            Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
        var raw = db.GetMetaString(DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey);
        return bool.TryParse(raw, out var value) ? value : null;
    }

    private static void WriteParseableInterface(string path, bool hasStaticContract)
    {
        var contract = hasStaticContract ? "    static abstract T Parse(string s);\n" : string.Empty;
        var decoys = hasStaticContract
            ? string.Empty
            : "    AbstractFactory StaticValue();\n"
              + "    static AbstractFactory CreatevirtualNode();\n";
        File.WriteAllText(path, $"public interface IParseable<T>\n{{\n{contract}{decoys}}}\n");
    }

    [Fact]
    public void Run_UpdateModeFallbackFullScan_CancelledAfterReadinessDemotion_ReportsRolledBackProgress()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "bin/\n");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            File.AppendAllText(Path.Combine(projectRoot, ".gitignore"), "obj/\n");
            File.WriteAllText(Path.Combine(projectRoot, "later.cs"), "public class Later { }\n");
            using var cancellation = new CancellationTokenSource();
            IndexCommandRunner.FullScanWritePhaseStartedForTesting = () => cancellation.Cancel();

            int interruptedExitCode;
            JsonElement interruptedJson;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--files", ".gitignore", "--json"], _jsonOptions, cancellation);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    interruptedJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.FullScanWritePhaseStartedForTesting = null;
                }
            }

            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            Assert.Contains("full-scan progress was rolled back", interruptedJson.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("rolled back", interruptedJson.GetProperty("hint").GetString(), StringComparison.Ordinal);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRun = statusJson.GetProperty("last_failed_or_partial_index_run");
            Assert.Equal("partial", lastRun.GetProperty("status").GetString());
            Assert.Equal("incremental", lastRun.GetProperty("mode").GetString());
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
    public void Run_UpdateMode_CancelledAfterCommittedFile_ReportsPersistedProgress()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public int Version => 1; }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var initialChecksum = ReadIndexedChecksum(dbPath, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public int Version => 2; }\n");

            var hookInvoked = false;
            IndexCommandRunner.UpdateFileCommittedForTesting = (filesProcessed, filesTotal) =>
            {
                Assert.Equal(1, filesProcessed);
                Assert.Equal(1, filesTotal);
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
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--files", "app.cs", "--json"], _jsonOptions, cancellation);
                    using var document = JsonDocument.Parse(stdout.ToString());
                    interruptedJson = document.RootElement.Clone();
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.UpdateFileCommittedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            Assert.Equal("error", interruptedJson.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, interruptedJson.GetProperty("error_code").GetString());
            Assert.Contains("completed update progress was saved", interruptedJson.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("completed update-mode file transactions remain", interruptedJson.GetProperty("hint").GetString(), StringComparison.Ordinal);
            Assert.Contains("app.cs", ReadIndexedPaths(dbPath));
            Assert.NotEqual(initialChecksum, ReadIndexedChecksum(dbPath, "app.cs"));

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRun = statusJson.GetProperty("last_failed_or_partial_index_run");
            Assert.Equal("partial", lastRun.GetProperty("status").GetString());
            Assert.Equal("update", lastRun.GetProperty("mode").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, lastRun.GetProperty("error_code").GetString());
            Assert.Equal(1, lastRun.GetProperty("files_processed").GetInt64());
            Assert.Equal(1, lastRun.GetProperty("files_total").GetInt64());
            Assert.True(lastRun.GetProperty("progress_persisted").GetBoolean());
            Assert.Contains("remain in the index", lastRun.GetProperty("recovery_hint").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            IndexCommandRunner.UpdateFileCommittedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_CancelledAfterTypeScriptCommit_ClearsAugmentationVersion()
    {
        var projectRoot = CreateTempProject();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "types.ts");
            File.WriteAllText(sourcePath, "export interface Options { value: string }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.Equal(
                    DbContext.TypeScriptAugmentationVersion.ToString(CultureInfo.InvariantCulture),
                    db.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
            }

            File.WriteAllText(sourcePath, "export interface Options { value: string; enabled: boolean }\n");

            var hookInvoked = false;
            IndexCommandRunner.UpdateFileCommittedForTesting = (filesProcessed, filesTotal) =>
            {
                Assert.Equal(1, filesProcessed);
                Assert.Equal(1, filesTotal);
                hookInvoked = true;
                cancellation.Cancel();
            };

            int interruptedExitCode;
            lock (TestConsoleLock.Gate)
            {
                var originalOut = Console.Out;
                using var stdout = new StringWriter();
                try
                {
                    Console.SetOut(stdout);
                    interruptedExitCode = IndexCommandRunner.Run([projectRoot, "--files", "types.ts", "--json"], _jsonOptions, cancellation);
                }
                finally
                {
                    Console.SetOut(originalOut);
                    IndexCommandRunner.UpdateFileCommittedForTesting = null;
                }
            }

            Assert.True(hookInvoked);
            Assert.Equal(CommandExitCodes.Interrupted, interruptedExitCode);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.Null(db.GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey));
        }
        finally
        {
            IndexCommandRunner.UpdateFileCommittedForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DeleteTypeScriptFile_RebuildsAugmentationReferences()
    {
        var projectRoot = CreateTempProject();
        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        var previousRefreshHook = DbWriter.MutualRecursionRefreshForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        var refreshCount = 0;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "types.ts");
            File.WriteAllText(sourcePath, "export interface Options { value: string }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(sourcePath);
            var rebuiltTypeScriptAugmentation = false;
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = () => rebuiltTypeScriptAugmentation = true;
            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };
            DbWriter.MutualRecursionRefreshForTesting = () =>
            {
                refreshCount++;
                previousRefreshHook?.Invoke();
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "types.ts", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(1, updateJson.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.True(rebuiltTypeScriptAugmentation);
            Assert.NotNull(groupingStats);
            Assert.Equal(0, groupingStats!.DeclarationCount);
            Assert.Equal(1, groupingStats.ScopedNameCount);
            Assert.Equal(1, refreshCount);
        }
        finally
        {
            IndexCommandRunner.UpdateTypeScriptAugmentationRebuildForTesting = null;
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
            DbWriter.MutualRecursionRefreshForTesting = previousRefreshHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_TypeScriptToCSharpLanguageTransitionRemovesAugmentation()
    {
        var projectRoot = CreateTempProject();
        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        try
        {
            var changedPath = Path.Combine(projectRoot, "changed.cs");
            File.WriteAllText(changedPath, "public interface SharedTransition { int Changed { get; } }\n");
            File.WriteAllText(
                Path.Combine(projectRoot, "peer.ts"),
                "interface SharedTransition { peer: number }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Equal(
                2,
                TestProjectHelper.ReclassifyIndexedFileAsTypeScriptAndRebuildAugmentations(
                    dbPath,
                    projectRoot,
                    "changed.cs"));

            File.WriteAllText(changedPath, "public class Changed { }\n");
            File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(2));
            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "changed.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.NotNull(groupingStats);
            Assert.Equal(1, groupingStats!.DeclarationCount);
            Assert.Equal(1, groupingStats.ScopedNameCount);
            using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_UpdateMode_WithOversizedFile_PrintsSkipWarningWithoutRecoveryWarning()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            TestProjectHelper.WriteSparseFile(projectRoot, "huge.py", 10 * 1024 * 1024 + 1L);

            var (exitCode, _, stderr) = RunCliInSubprocess([projectRoot, "--files", "huge.py"], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index generation is incomplete: file_too_large.", stderr);
            Assert.Contains("Reference graph is incomplete: file_too_large.", stderr);
            Assert.DoesNotContain("Some files failed to update", stderr);
            Assert.DoesNotContain("rerun `cdidx index", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_CapPersistsIncompleteWhenIssueReadinessWasUnset_Issue4826()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, "huge.py"),
                "print('start')\n" + new string('a', 256));
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    $"PRAGMA user_version = {DbContext.CurrentSchemaVersion & ~DbContext.IssuesReadyFlag}";
                command.ExecuteNonQuery();
            }

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "huge.py", "--max-file-bytes", "128", "--json", "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(updateJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(updateJson.GetProperty("index_complete").GetBoolean());
            AssertCompletenessReason(updateJson, "index_incomplete_reasons", "file_too_large");
            Assert.False(updateJson.GetProperty("reference_graph_complete").GetBoolean());
            AssertCompletenessReason(
                updateJson,
                "reference_graph_incomplete_reasons",
                "file_too_large");

            var (statusExitCode, statusJson) =
                RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            AssertCompletenessSignalsEqual(updateJson, statusJson);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_VerboseRedirectedOutput_DoesNotRepeatUpdatingBanner()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(Path.Combine(projectRoot, "app.cs"), DateTime.UtcNow.AddSeconds(2));

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--files", "app.cs", "--verbose"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, CountOccurrences(stdout, "Updating 1 file..."));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_VerboseJson_WritesStatusToStderrAndKeepsStdoutJson()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, stdout, stderr) = RunAndCaptureStreams([projectRoot, "--files", "app.cs", "--verbose", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var json = JsonDocument.Parse(stdout);
            Assert.Equal("success", json.RootElement.GetProperty("Status").GetString());
            Assert.DoesNotContain("[OK  ]", stdout);
            Assert.Contains("[OK  ] app.cs", stderr);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_JsonKeepsGraphAndIssuesReadyAfterHealthyScopedRefresh()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedFileThatIsNowIgnored()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "generated.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedFileThatMatchesLeadingRightBracketCharacterClass()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "].cs"), "class Ignored { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("].cs", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[]].cs\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "].cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("].cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_RemovesIndexedFileThatMatchesPosixPunctCharacterClass()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "!.cs"), "class Ignored { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("!.cs", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[[:punct:]].cs\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "!.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("!.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DoesNotDeleteIndexedFileForMalformedBracketIgnoreRule()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "[a.py"), "print('keep literal')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("[a.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "[a.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "[a.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore:1", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.Contains("Invalid ignore rule skipped", json.GetProperty("warnings")[0].GetProperty("message").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("[a.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_FallsBackToFullScanWhenIgnoreFilesChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");
            RunGit(projectRoot, "add", ".gitignore");
            RunGit(projectRoot, "commit", "-m", "ignore generated");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_FallsBackToFullScanWhenIgnoreFilesChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ".gitignore", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SkipsMutationWhenIgnoreRulesAreUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.DoesNotContain("secret.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "secret.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("secret.py", indexedPaths);
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_SkipsMutationWhenIgnoreRulesAreUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        UnixFileMode? originalMode = null;
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret v1')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("secret.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(ignorePath, "secret.py\n");
            RunGit(projectRoot, "add", ".gitignore");
            RunGit(projectRoot, "commit", "-m", "ignore secret");

            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret v2')\n");
            RunGit(projectRoot, "add", "secret.py");
            RunGit(projectRoot, "commit", "-m", "update secret");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("secret.py", indexedPaths);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_UnreadableIgnoreRulesDemoteReadinessForUnchangedIndexedFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("keep.py", ReadIndexedPaths(dbPath));

            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "keep.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DemotesReadinessWhenIgnoreFileChangedThenBecameUnreadable()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        UnixFileMode? originalMode = null;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            File.WriteAllText(sourcePath, "public class A { }\n");
            File.WriteAllText(Path.Combine(projectRoot, "b.cs"), "public class B { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("a.cs", ReadIndexedPaths(dbPath));

            File.WriteAllText(ignorePath, "a.cs\n");
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());
            Assert.Contains("a.cs", ReadIndexedPaths(dbPath));

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_UnreadableIgnoreRulesDemoteReadinessForChangedIndexedFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        var ignorePath = Path.Combine(projectRoot, ".gitignore");
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        UnixFileMode? originalMode = null;
        try
        {
            File.WriteAllText(ignorePath, "secret.py\n");
            File.WriteAllText(Path.Combine(projectRoot, "secret.py"), "print('secret')\n");
            var keepPath = Path.Combine(projectRoot, "keep.py");
            File.WriteAllText(keepPath, "print('keep v1')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("keep.py", ReadIndexedPaths(dbPath));

            File.WriteAllText(keepPath, "print('keep v2')\n");
            originalMode = File.GetUnixFileMode(ignorePath);
            SetUnixPermissions(ignorePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "keep.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("warnings").GetInt32());
            Assert.Equal(".gitignore", json.GetProperty("warnings")[0].GetProperty("file").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.True(json.GetProperty("fold_ready").GetBoolean());

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            if (originalMode.HasValue && File.Exists(ignorePath))
                SetUnixPermissions(ignorePath, originalMode.Value);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WhenIgnoreFileChanges_FullScanPurgesAndRestoresMembership_Issue4592()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("generated.py", ReadIndexedPaths(dbPath));

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "*.py\n!keep.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ".gitignore", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("incremental", json.GetProperty("mode").GetString());

            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), string.Empty);

            var (unignoreExitCode, unignoreJson) = RunAndCaptureJson([projectRoot, "--files", ".gitignore", "--json"]);

            Assert.Equal(CommandExitCodes.Success, unignoreExitCode);
            Assert.Equal("success", unignoreJson.GetProperty("status").GetString());
            Assert.True(unignoreJson.GetProperty("summary").TryGetProperty("files_total", out _));
            Assert.Contains("generated.py", ReadIndexedPaths(dbPath));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WhenPatternConfigIsAddedOrEdited_FallsBackToFullScan_Issue4592()
    {
        var projectRoot = CreateTempProject();
        try
        {
            ExtractorPluginRegistry.ReloadForTests();
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('app')\n");
            var sourcePath = Path.Combine(projectRoot, "sample.issue4592");
            File.WriteAllText(sourcePath, "type AddedPattern\nentity EditedPattern\n");
            var (initialExitCode, _) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var patternPath = Path.Combine(projectRoot, ".cdidx", "patterns", "issue4592.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(patternPath)!);
            File.WriteAllText(
                patternPath,
                "language: \"issue4592dsl\"\nextensions:\n  - extension: \".issue4592\"\npatterns:\n  - kind: \"class\"\n    regex: \"^type (?<name>[A-Za-z]+)\"\n");

            var (addExitCode, addJson) = RunAndCaptureJson([projectRoot, "--files", patternPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, addExitCode);
            Assert.True(addJson.GetProperty("summary").TryGetProperty("files_total", out _));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("sample.issue4592", ReadIndexedPaths(dbPath));
            Assert.Single(ReadIndexedSymbolNames(dbPath, "AddedPattern"));

            File.WriteAllText(
                patternPath,
                "language: \"issue4592dsl\"\nextensions:\n  - extension: \".issue4592\"\npatterns:\n  - kind: \"class\"\n    regex: \"^entity (?<name>[A-Za-z]+)\"\n");

            var (editExitCode, editJson) = RunAndCaptureJson([projectRoot, "--files", patternPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, editExitCode);
            Assert.True(editJson.GetProperty("summary").TryGetProperty("files_total", out _));
            Assert.DoesNotContain(".cdidx/patterns/issue4592.yaml", ReadIndexedPaths(dbPath));
            Assert.Contains("sample.issue4592", ReadIndexedPaths(dbPath));
            Assert.Empty(ReadIndexedSymbolNames(dbPath, "AddedPattern"));
            Assert.Single(ReadIndexedSymbolNames(dbPath, "EditedPattern"));

            File.Delete(patternPath);

            var (deleteExitCode, deleteJson) = RunAndCaptureJson([projectRoot, "--files", patternPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, deleteExitCode);
            Assert.True(deleteJson.GetProperty("summary").TryGetProperty("files_total", out _));
            Assert.DoesNotContain("sample.issue4592", ReadIndexedPaths(dbPath));
            Assert.Empty(ReadIndexedSymbolNames(dbPath, "EditedPattern"));
        }
        finally
        {
            ExtractorPluginRegistry.ReloadForTests();
            DeleteDirectory(projectRoot);
        }
    }

    private static List<SymbolResult> ReadIndexedSymbolNames(string dbPath, string name)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        db.TryMigrateForRead();
        var reader = new DbReader(db.Connection, db.IsReadOnly);
        return reader.SearchSymbols(name, limit: 10, exact: true);
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_RespectsAncestorGitignore()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "ignored.py"), "print('indexed first')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("ignored.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/ignored.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "ignored.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("ignored.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_RespectsAncestorDirectoryGitignoreRule()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.py"), "print('indexed first')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Contains("app.py", ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.py", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("app.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_FallsBackToFullScanWhenAncestorIgnoreFileChanges()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var ancestorIgnorePath = Path.Combine(repoRoot, ".gitignore");
            File.WriteAllText(ancestorIgnorePath, "subproj/generated.py\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ancestorIgnorePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_SubdirectoryProjectRoot_FallsBackToFullScanWhenAncestorDirectoryIgnoreRuleChanges()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var ancestorIgnorePath = Path.Combine(repoRoot, ".gitignore");
            File.WriteAllText(ancestorIgnorePath, "subproj/\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", ancestorIgnorePath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_ProjectRootNamedNodeModules_UpdatesIndexedFile()
    {
        var tempRoot = CreateTempProject();
        var projectRoot = Path.Combine(tempRoot, "node_modules");
        try
        {
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "app.js"), "console.log('ignored root dir');\n");

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "app.js", "javascript", "console.log('stale');\n");
            Assert.Contains("app.js", ReadIndexedPaths(dbPath));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.js", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.Contains("app.js", indexedPaths);
        }
        finally
        {
            DeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_SubdirectoryProjectRoot_UsesRepositoryRelativePaths()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            var appPath = Path.Combine(projectRoot, "app.py");
            File.WriteAllText(appPath, "print('v1')\n");
            RunGit(repoRoot, "add", ".");
            RunGit(repoRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var initialChecksum = ReadIndexedChecksum(dbPath, "app.py");

            File.WriteAllText(appPath, "print('v2 with more content')\n");
            RunGit(repoRoot, "add", "subproj/app.py");
            RunGit(repoRoot, "commit", "-m", "update app");
            var commitId = RunGitCaptureStdOut(repoRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("skipped").GetInt32());
            Assert.NotEqual(initialChecksum, ReadIndexedChecksum(dbPath, "app.py"));
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_SubdirectoryProjectRoot_FallsBackToFullScanWhenAncestorIgnoreFileChanges()
    {
        var repoRoot = CreateTempProject();
        var projectRoot = Path.Combine(repoRoot, "subproj");
        try
        {
            RunGit(repoRoot, "init");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");
            RunGit(repoRoot, "add", ".");
            RunGit(repoRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "subproj/generated.py\n");
            RunGit(repoRoot, "add", ".gitignore");
            RunGit(repoRoot, "commit", "-m", "ignore generated");
            var commitId = RunGitCaptureStdOut(repoRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
        }
        finally
        {
            DeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DoesNotPurgeOldRenamePathUnlessExplicitlyListed()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var oldPath = Path.Combine(srcDir, "OldName.cs");
            var newPath = Path.Combine(srcDir, "NewName.cs");

            File.WriteAllText(oldPath, "public class OldName { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(oldPath, newPath);
            File.WriteAllText(newPath, "public class NewName { }\n");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "src/NewName.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("src/OldName.cs", indexedPaths);
            Assert.Contains("src/NewName.cs", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_PurgesOldRenamePath()
    {
        var projectRoot = CreateTempProject();
        var previousCleanupChecksumHook = IndexCommandRunner.UpdateCleanupChecksumReadForTesting;
        try
        {
            RunGit(projectRoot, "init");
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var oldPath = Path.Combine(srcDir, "OldName.cs");
            var newPath = Path.Combine(srcDir, "NewName.cs");

            File.WriteAllText(oldPath, "public class OldName { }\n");
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(oldPath, newPath);
            File.WriteAllText(newPath, "public class NewName { }\n");
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "rename");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();
            var cleanupChecksumReads = 0;
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = _ => cleanupChecksumReads++;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, cleanupChecksumReads);

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/OldName.cs", indexedPaths);
            Assert.Contains("src/NewName.cs", indexedPaths);
        }
        finally
        {
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = previousCleanupChecksumHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_UpdatesNewPathAndRemovesRenamedOldPath()
    {
        var projectRoot = CreateTempProject();
        var previousCleanupChecksumHook = IndexCommandRunner.UpdateCleanupChecksumReadForTesting;
        try
        {
            RunGit(projectRoot, "init");
            var srcDir = Path.Combine(projectRoot, "src");
            Directory.CreateDirectory(srcDir);
            var oldPath = Path.Combine(srcDir, "OldName.cs");
            var newPath = Path.Combine(srcDir, "NewName.cs");

            File.WriteAllText(oldPath, "public class SameName { }\n");
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-switch");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Move(oldPath, newPath);
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "rename");
            RunGit(projectRoot, "branch", "after-switch");
            var cleanupChecksumReads = 0;
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = _ => cleanupChecksumReads++;

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-switch", "after-switch", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(2, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(0, cleanupChecksumReads);

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("src/OldName.cs", indexedPaths);
            Assert.Contains("src/NewName.cs", indexedPaths);
        }
        finally
        {
            IndexCommandRunner.UpdateCleanupChecksumReadForTesting = previousCleanupChecksumHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_StaleCsharpContractOutsideRangeRefreshesReferences()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            var unrelatedPath = Path.Combine(projectRoot, "unrelated.py");
            File.WriteAllText(unrelatedPath, "print('before')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "base without contract");
            RunGit(projectRoot, "branch", "without-contract");

            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            var staleNonCsharpPath = Path.Combine(projectRoot, "outside-range.py");
            File.WriteAllText(staleNonCsharpPath, "print('indexed branch only')\n");
            RunGit(projectRoot, "add", "IParseable.cs", "outside-range.py");
            RunGit(projectRoot, "commit", "-m", "add indexed branch files");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));

            RunGit(projectRoot, "checkout", "without-contract");
            RunGit(projectRoot, "branch", "range-before");
            File.WriteAllText(unrelatedPath, "print('after')\n");
            RunGit(projectRoot, "add", "unrelated.py");
            RunGit(projectRoot, "commit", "-m", "unrelated range change");
            RunGit(projectRoot, "branch", "range-after");

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--changed-between", "range-before", "range-after", "--json"]);

            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.Equal("success", updateJson.GetProperty("status").GetString());
            Assert.Equal(0, CountMoneyParseImplicitImplementationReferences(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.DoesNotContain("IParseable.cs", ReadIndexedPaths(dbPath));
            Assert.Contains("outside-range.py", ReadIndexedPaths(dbPath));
            Assert.False(File.Exists(staleNonCsharpPath));
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(
                bool.FalseString,
                db.GetMetaString(DbContext.CSharpStaticInterfaceSourceEvidenceMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_CleanupPathReappearingAfterScanIsDeferredUntilRetry()
    {
        var projectRoot = CreateTempProject();
        var previousPrepassHook = IndexCommandRunner.UpdateCSharpPrepassForTesting;
        try
        {
            RunGit(projectRoot, "init");
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            var unrelatedPath = Path.Combine(projectRoot, "unrelated.py");
            File.WriteAllText(unrelatedPath, "print('before')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "base with contract");
            RunGit(projectRoot, "branch", "range-before");

            File.WriteAllText(unrelatedPath, "print('after')\n");
            RunGit(projectRoot, "add", "unrelated.py");
            RunGit(projectRoot, "commit", "-m", "unrelated range change");
            RunGit(projectRoot, "branch", "range-after");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var originalContractChecksum = ReadIndexedChecksum(dbPath, "IParseable.cs");
            Assert.NotNull(originalContractChecksum);

            File.Delete(interfacePath);
            var prepassCalls = 0;
            IndexCommandRunner.UpdateCSharpPrepassForTesting = () =>
            {
                if (Interlocked.Increment(ref prepassCalls) == 1)
                    WriteParseableInterface(interfacePath, hasStaticContract: true);
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--changed-between", "range-before", "range-after", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.False(updateJson.GetProperty("index_complete").GetBoolean());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, prepassCalls);
            Assert.Equal(originalContractChecksum, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.True(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            // Reappearance changes the directory listing captured by expanded discovery;
            // the first barrier rejects it without replacing prior authoritative evidence.
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            IndexCommandRunner.UpdateCSharpPrepassForTesting = null;
            var retryExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);

            Assert.Equal(CommandExitCodes.Success, retryExitCode);
            Assert.True(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.True(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpPrepassForTesting = previousPrepassHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_ExpandedExactCleanupPathReappearancePreservesPriorRow()
    {
        var projectRoot = CreateTempProject();
        var previousExpansionHook = IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting;
        var previousContentLoadHook = IndexCommandRunner.UpdateFileContentLoadForTesting;
        try
        {
            RunGit(projectRoot, "init");
            WriteParseableInterface(
                Path.Combine(projectRoot, "IParseable.cs"),
                hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            const string reappearedContent = "public sealed class Reappeared { }\n";
            var reappearedPath = Path.Combine(projectRoot, "Reappeared.cs");
            File.WriteAllText(reappearedPath, reappearedContent);
            var unrelatedPath = Path.Combine(projectRoot, "unrelated.py");
            File.WriteAllText(unrelatedPath, "print('before')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "base with reappearance candidate");
            RunGit(projectRoot, "branch", "range-before");

            File.WriteAllText(unrelatedPath, "print('after')\n");
            RunGit(projectRoot, "add", "unrelated.py");
            RunGit(projectRoot, "commit", "-m", "unrelated range change");
            RunGit(projectRoot, "branch", "range-after");

            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var originalChecksum = ReadIndexedChecksum(dbPath, "Reappeared.cs");
            Assert.NotNull(originalChecksum);

            File.Delete(reappearedPath);
            var expansionHookCalls = 0;
            IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = () =>
            {
                Assert.Equal(1, Interlocked.Increment(ref expansionHookCalls));
                File.WriteAllText(reappearedPath, reappearedContent);
            };
            var reappearedContentLoads = 0;
            IndexCommandRunner.UpdateFileContentLoadForTesting = path =>
            {
                if (!string.Equals(path, "Reappeared.cs", StringComparison.Ordinal))
                    return;

                Interlocked.Increment(ref reappearedContentLoads);
                throw new IOException("Injected extraction failure after cleanup planning.");
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--changed-between", "range-before", "range-after", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.False(updateJson.GetProperty("index_complete").GetBoolean());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, expansionHookCalls);
            Assert.Equal(0, reappearedContentLoads);
            Assert.Equal(originalChecksum, ReadIndexedChecksum(dbPath, "Reappeared.cs"));
            Assert.True(IndexedFileExists(projectRoot, "Reappeared.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            Assert.Null(ReadCSharpStaticInterfaceSourceEvidence(projectRoot));

            IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = null;
            IndexCommandRunner.UpdateFileContentLoadForTesting = null;
            Assert.Equal(
                CommandExitCodes.Success,
                IndexCommandRunner.Run([projectRoot, "--json", "--quiet"], _jsonOptions));
            Assert.True(IndexedFileExists(projectRoot, "Reappeared.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateCSharpExpansionScanStartingForTesting = previousExpansionHook;
            IndexCommandRunner.UpdateFileContentLoadForTesting = previousContentLoadHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateCommits_ExactCleanupPathReappearingAfterSnapshotBarrierPreservesPriorRow()
    {
        var projectRoot = CreateTempProject();
        var previousBarrierHook = IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting;
        try
        {
            RunGit(projectRoot, "init");
            var interfacePath = Path.Combine(projectRoot, "IParseable.cs");
            WriteParseableInterface(interfacePath, hasStaticContract: true);
            File.WriteAllText(
                Path.Combine(projectRoot, "Money.cs"),
                "public readonly struct Money : IParseable<Money>\n"
                + "{\n"
                + "    public static Money Parse(string s) => new();\n"
                + "}\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "base with contract");

            var initialExitCode = IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var originalContractChecksum = ReadIndexedChecksum(dbPath, "IParseable.cs");
            Assert.NotNull(originalContractChecksum);

            File.Delete(interfacePath);
            RunGit(projectRoot, "add", "IParseable.cs");
            RunGit(projectRoot, "commit", "-m", "delete contract");
            var reappearanceHookCalls = 0;
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = phase =>
            {
                if (phase != "before_cleanup_apply")
                    return;

                Assert.Equal(1, Interlocked.Increment(ref reappearanceHookCalls));
                WriteParseableInterface(interfacePath, hasStaticContract: true);
            };

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--commits", "HEAD", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, updateExitCode);
            Assert.Equal("partial", updateJson.GetProperty("status").GetString());
            Assert.False(updateJson.GetProperty("index_complete").GetBoolean());
            Assert.True(updateJson.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
            Assert.Equal(1, reappearanceHookCalls);
            Assert.True(File.Exists(interfacePath));
            Assert.Equal(originalContractChecksum, ReadIndexedChecksum(dbPath, "IParseable.cs"));
            Assert.True(IndexedFileExists(projectRoot, "IParseable.cs"));
            Assert.Equal(1, CountMoneyParseImplicitImplementationReferences(projectRoot));
        }
        finally
        {
            IndexCommandRunner.UpdateScanInputSnapshotBarrierForTesting = previousBarrierHook;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_RemovesDeletedPath_2987()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var changelogDir = Path.Combine(projectRoot, "changelog.d", "unreleased");
            Directory.CreateDirectory(changelogDir);
            var deletedPath = Path.Combine(changelogDir, "+trimmed-release-json.fixed.md");

            File.WriteAllText(
                deletedPath,
                """
                ---
                category: fixed
                ---

                ## English

                - Placeholder.

                ## 日本語

                - プレースホルダー。
                """);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-delete");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.Delete(deletedPath);
            RunGit(projectRoot, "add", "-A");
            RunGit(projectRoot, "commit", "-m", "delete fragment");
            RunGit(projectRoot, "branch", "after-delete");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-delete", "after-delete", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("changelog.d/unreleased/+trimmed-release-json.fixed.md", indexedPaths);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", Path.Combine(projectRoot, ".cdidx", "codeindex.db"), "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_FallsBackToFullScanWhenIgnoreFilesChange()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            File.WriteAllText(Path.Combine(projectRoot, "generated.py"), "print('generated')\n");
            File.WriteAllText(Path.Combine(projectRoot, "keep.py"), "print('keep')\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-switch");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(Path.Combine(projectRoot, ".gitignore"), "generated.py\n");
            RunGit(projectRoot, "add", ".gitignore");
            RunGit(projectRoot, "commit", "-m", "ignore generated");
            RunGit(projectRoot, "branch", "after-switch");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-switch", "after-switch", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("generated.py", indexedPaths);
            Assert.Contains("keep.py", indexedPaths);
            Assert.Contains(".gitignore", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetweenMissingRef_ReturnsUsageError()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(projectRoot);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "HEAD", "--json"]);

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
    public void Run_UpdateMode_WithFiles_RemovesIndexedScriptThatLosesShebang()
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

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "tool", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithCommits_RemovesIndexedScriptThatLosesShebang()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            File.WriteAllText(toolPath, "plain text now\n");
            RunGit(projectRoot, "add", "tool");
            RunGit(projectRoot, "commit", "-m", "remove shebang");
            var commitId = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--commits", commitId, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.DoesNotContain("tool", indexedPaths);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DoesNotRemoveUnreadableExtensionlessScript()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var toolPath = Path.Combine(projectRoot, "tool");
            File.WriteAllText(toolPath, "#!/usr/bin/env bash\necho hi\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(toolPath, UnixFileMode.None);
            File.SetLastWriteTimeUtc(toolPath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "tool", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("removed").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("graph_data_current").GetBoolean());
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("tool", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal("Could not probe file for indexability/language.", json.GetProperty("errors")[0].GetProperty("message").GetString());

            var indexedPaths = ReadIndexedPaths(Path.Combine(projectRoot, ".cdidx", "codeindex.db"));
            Assert.Contains("tool", indexedPaths);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            var toolPath = Path.Combine(projectRoot, "tool");
            if (File.Exists(toolPath))
                SetUnixPermissions(toolPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithFiles_DemotesReadinessForUnreadableKnownExtensionFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "a.cs");
            File.WriteAllText(sourcePath, "public class A { }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            SetUnixPermissions(sourcePath, UnixFileMode.None);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("graph_data_current").GetBoolean());
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("a.cs", json.GetProperty("errors")[0].GetProperty("file").GetString());
            Assert.Equal(CommandErrorCodes.IndexPartial, json.GetProperty("error_code").GetString());
            Assert.Equal("a.cs", json.GetProperty("file_errors")[0].GetProperty("file").GetString());
            Assert.Equal("file_read_error", json.GetProperty("file_errors")[0].GetProperty("category").GetString());
            Assert.Equal("reading", json.GetProperty("file_errors")[0].GetProperty("phase").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("a.cs", statusJson.GetProperty("last_failed_or_partial_index_run").GetProperty("file_errors")[0].GetProperty("file").GetString());

            // A scoped mutation cannot clear a failure that it did not revisit. The runner
            // promotes this attempt to an incremental full scan, which sees the unreadable
            // file and preserves both the partial result and recovery metadata.
            var (unrelatedExitCode, unrelatedJson) = RunAndCaptureJson([projectRoot, "--files", "b.cs", "--json"]);
            Assert.Equal(CommandExitCodes.PartialResult, unrelatedExitCode);
            Assert.Equal("partial", unrelatedJson.GetProperty("status").GetString());
            Assert.Equal("a.cs", unrelatedJson.GetProperty("file_errors")[0].GetProperty("file").GetString());

            SetUnixPermissions(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(4));

            // Retrying the same scoped command also promotes to a normal full scan so every
            // workspace readiness contract is restored without requiring --rebuild.
            var (recoveryExitCode, recoveryJson) = RunAndCaptureJson([projectRoot, "--files", "a.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, recoveryExitCode);
            Assert.Equal("success", recoveryJson.GetProperty("status").GetString());
            Assert.True(recoveryJson.GetProperty("graph_data_current").GetBoolean());
            Assert.True(recoveryJson.GetProperty("index_complete").GetBoolean());
            Assert.True(recoveryJson.GetProperty("issues_table_available").GetBoolean());
            Assert.True(recoveryJson.GetProperty("fold_ready").GetBoolean());

            var (recoveredStatusExitCode, recoveredStatusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, recoveredStatusExitCode);
            Assert.True(recoveredStatusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.True(recoveredStatusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(recoveredStatusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.True(recoveredStatusJson.GetProperty("fold_ready").GetBoolean());
            Assert.False(recoveredStatusJson.TryGetProperty("last_failed_or_partial_index_run", out _));
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
    public void Run_UpdateMode_WithFiles_DemotesReadinessForUnreadableNewKnownExtensionFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = CreateTempProject();
        try
        {
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var sourcePath = Path.Combine(projectRoot, "b.cs");
            File.WriteAllText(sourcePath, "public class B { }\n");
            SetUnixPermissions(sourcePath, UnixFileMode.None);

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "b.cs", "--json"]);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal("partial", json.GetProperty("status").GetString());
            Assert.Equal(0, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("errors").GetInt32());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("graph_data_current").GetBoolean());
            Assert.False(json.GetProperty("index_complete").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("b.cs", json.GetProperty("errors")[0].GetProperty("file").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("graph_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("graph_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("index_complete").GetBoolean());
            Assert.True(statusJson.GetProperty("issues_table_available").GetBoolean());
            Assert.False(statusJson.GetProperty("file_issues_data_current").GetBoolean());
            Assert.False(statusJson.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            var sourcePath = Path.Combine(projectRoot, "b.cs");
            if (File.Exists(sourcePath))
                SetUnixPermissions(sourcePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_JsonReportsDegradedReadinessWhenBitsStayDown()
    {
        var projectRoot = CreateTempProject();
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var (_, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.True(initialJson.GetProperty("fold_ready").GetBoolean());
            Assert.Equal(JsonValueKind.Null, initialJson.GetProperty("fold_ready_reason").ValueKind);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var conn = OpenNonPoolingConnection(dbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("graph_table_available").GetBoolean());
            Assert.False(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_HumanOutputShowsDegradedReadinessWhenBitsStayDown()
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
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, output) = RunAndCaptureOutput([projectRoot, "--files", "app.cs"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Graph    : degraded", output);
            Assert.Contains("Issues   : degraded", output);
            Assert.Contains("SQL graph: ready", output);
            Assert.Contains("Fold     : degraded", output);
            var readinessLines = output.Split('\n')
                .Where(line => line.Contains(": ready", StringComparison.Ordinal) || line.Contains(": degraded", StringComparison.Ordinal))
                .ToList();
            Assert.All(readinessLines, line => Assert.Equal(readinessLines[0].IndexOf(':', StringComparison.Ordinal), line.IndexOf(':', StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_JsonPreservesGraphAndIssuesWhenOnlyFoldIsMissing()
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
                cmd.CommandText = """
                    UPDATE symbols SET name_folded = NULL;
                    UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL;
                    PRAGMA user_version = 3
                    """;
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("graph_table_available").GetBoolean());
            Assert.True(json.GetProperty("issues_table_available").GetBoolean());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_UpdateMode_DegradedWarningUsesResolvedProjectDbPathWhenCwdDiffers()
    {
        var projectRoot = CreateTempProject();
        var otherCwd = TestProjectHelper.CreateTempProject("cdidx_other_cwd");
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
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, _, errorOutput) = RunCliInSubprocess([projectRoot, "--files", "app.cs"], otherCwd);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index completed with degraded readiness", errorOutput);
            Assert.Contains("graph_table_available=false", errorOutput);
            Assert.Contains("issues_table_available=false", errorOutput);
            Assert.Contains("fold_ready=false", errorOutput);
            Assert.Contains($"cdidx status --db \"{dbPath}\" --json", errorOutput);
        }
        finally
        {
            DeleteDirectory(otherCwd);
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_UpdateMode_DegradedWarningUsesExplicitDbPath()
    {
        var projectRoot = CreateTempProject();
        var customDbDir = TestProjectHelper.CreateTempProject("cdidx_custom_db");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            var customDbPath = Path.Combine(customDbDir, "custom-index.db");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--db", customDbPath, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            using (var conn = OpenNonPoolingConnection(customDbPath))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 0";
                cmd.ExecuteNonQuery();
            }

            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

            var (exitCode, _, errorOutput) = RunCliInSubprocess([projectRoot, "--db", customDbPath, "--files", "app.cs"], projectRoot);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Index completed with degraded readiness", errorOutput);
            Assert.Contains("graph_table_available=false", errorOutput);
            Assert.Contains("issues_table_available=false", errorOutput);
            Assert.Contains("fold_ready=false", errorOutput);
            Assert.Contains($"cdidx status --db \"{customDbPath}\" --json", errorOutput);
        }
        finally
        {
            DeleteDirectory(customDbDir);
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_Json_ReportsFoldOnlyRemediation()
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

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal("update", json.GetProperty("mode").GetString());
            Assert.False(json.GetProperty("fold_ready").GetBoolean());
            Assert.Equal("stale_fold_key_version", json.GetProperty("fold_ready_reason").GetString());
            Assert.Contains("older fold-key version", json.GetProperty("degraded_reason").GetString());
            Assert.Contains("cdidx backfill-fold --db", json.GetProperty("recommended_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("recommended_action").GetString());
            Assert.Contains("--rebuild", json.GetProperty("alternative_action").GetString());
            Assert.Contains(dbPath, json.GetProperty("alternative_action").GetString());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DegradedIssuesKeepsLastRunReferenceCapSnapshotUnavailable()
    {
        // A scoped update preserves readiness that existed before the run. Its lightweight
        // finalization read must use that same snapshot: physical file_issues rows alone do
        // not make reference-cap state authoritative when IssuesReady is absent.
        // scoped update の lightweight finalize でも事前の IssuesReady を尊重し、物理 row の
        // 存在だけで last-run reference-cap snapshot を authoritative に昇格させない。
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }\n");
            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            SqliteConnection.ClearAllPools();
            using (var connection = OpenNonPoolingConnection(dbPath))
            {
                connection.Open();
                using var readVersion = connection.CreateCommand();
                readVersion.CommandText = "PRAGMA user_version";
                var userVersion = Convert.ToInt64(readVersion.ExecuteScalar(), CultureInfo.InvariantCulture);
                using var clearIssuesReady = connection.CreateCommand();
                clearIssuesReady.CommandText = $"PRAGMA user_version = {userVersion & ~DbContext.IssuesReadyFlag}";
                clearIssuesReady.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var (updateExitCode, updateJson) = RunAndCaptureJson(
                [projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(updateJson.GetProperty("issues_table_available").GetBoolean());

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            var lastRunCapHits = statusJson
                .GetProperty("last_index_run")
                .GetProperty("reference_extraction_cap_hits");
            Assert.False(lastRunCapHits.GetProperty("state_available").GetBoolean());
            Assert.Contains(
                DegradationReasonCodes.ReferenceExtractionCapStateUnavailable,
                lastRunCapHits.GetProperty("reasons").EnumerateArray().Select(value => value.GetString()));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_PreservesGraphAndIssuesOnPre86Db_WithoutStampingFold()
    {
        // Codex #86 second-pass regression: pre-#86 DB has user_version=3 (Graph|Issues).
        // Before this fix, `wasFullyReady = user_version == CurrentSchemaVersion (=7)` returned
        // false, so update mode cleared all 3 bits and restamped none — silently breaking
        // references/callers/callees/impact for the whole workspace even though only the
        // Fold bit was missing. After the fix, Graph/Issues must survive a partial update on
        // a pre-#86 DB; only Fold stays off (needs full rebuild).
        // pre-#86 DB (user_version=3) に対する partial update で Graph/Issues が落ちず、
        // Fold だけが未 stamp のまま残ることを確認する回帰テスト。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            // Initial full scan stamps user_version = CurrentSchemaVersion (7 = Graph|Issues|Fold).
            // 初回 full scan で user_version = 7（全 bit stamp）。
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            // Simulate a pre-#86 DB by stripping the Fold bit (and wiping name_folded rows to
            // reflect a pre-#86 writer that did not populate them). User_version = 3.
            // pre-#86 DB を模擬: Fold bit を落とし、name_folded も NULL に戻す。
            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE symbols SET name_folded = NULL; UPDATE symbol_references SET symbol_name_folded = NULL, container_name_folded = NULL; PRAGMA user_version = 3";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Partial update via --files. Must NOT strip Graph/Issues trust just because Fold
            // was missing. After run: Graph+Issues still stamped, Fold stays off.
            // --files で partial update。Graph/Issues は維持、Fold は未 stamp のまま。
            var targetFile = Path.Combine(projectRoot, "app.cs");
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var verifyCmd = verify.CreateCommand();
            verifyCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)verifyCmd.ExecuteScalar()!;
            Assert.NotEqual(0, userVersion & DbContext.GraphReadyFlag);
            Assert.NotEqual(0, userVersion & DbContext.IssuesReadyFlag);
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampFoldReadyWhenFoldKeyVersionMismatches()
    {
        // Codex #86 fourth-pass regression: when a future NameFold.Version bump ships, the
        // stored fold_key_version on existing DBs becomes stale. A partial --files / --commits
        // update can only re-fold touched rows with the new version; untouched rows keep the
        // OLD folded keys. Restamping FoldReady + overwriting fold_key_version to the new
        // version would let the reader advertise full Unicode-exact readiness while silently
        // mismatching on untouched rows. The correct behavior is to leave FoldReady off until
        // a full --rebuild regenerates every row at the current version.
        // Simulate by writing an older fold_key_version into codeindex_meta before the update.
        // 将来の version bump 後の partial update で FoldReady を restamp しないことを確認する。
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");

            // Initial index stamps the current fold-key version.
            // 初回 index で現在の fold-key version が stamp される。
            var exitCode1 = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");

            // Simulate a future version bump: the DB was stamped by a binary that wrote
            // fold_key_version=0 (pretend old). The current binary expects the latest
            // NameFold.Version
            // so the reader sees a mismatch and falls back to NOCASE. A partial update must
            // preserve that state, not silently restamp the current version on mixed-state rows.
            // version 不一致を模擬: codeindex_meta の fold_key_version を 0 に書き換え。
            SqliteConnection.ClearAllPools();
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE codeindex_meta SET value = '0' WHERE key = 'fold_key_version'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // Run a partial update. FoldReady bit AND version must NOT advance to the new state
            // because untouched rows still carry the old version's fold keys.
            // partial update 実行。FoldReady bit も version も新状態に進めてはいけない。
            var targetFile = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(targetFile, "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
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
            // Stored version may stay at "0" (what we wrote) or be unset; critically it must
            // NOT have advanced to the current NameFold.Version because that would let the
            // reader treat mixed-state rows as fully fold-ready.
            // version は "0" のままで OK。現在の NameFold.Version に昇格してはいけない。
            Assert.NotEqual(NameFold.Version.ToString(), storedVersion);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampFoldReadyWhenSymbolExtractorVersionMismatches()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            RunGit(projectRoot, "config", "user.email", "test@example.com");
            RunGit(projectRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { }");
            File.WriteAllText(Path.Combine(projectRoot, "untouched.cs"), "public class Untouched { }");
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
                cmd.CommandText = $"UPDATE codeindex_meta SET value = '0' WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var targetFile = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(targetFile, "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, exitCode2);

            using var verify = new SqliteConnection($"Data Source={dbPath}");
            verify.Open();
            using var userVerCmd = verify.CreateCommand();
            userVerCmd.CommandText = "PRAGMA user_version";
            var userVersion = (long)userVerCmd.ExecuteScalar()!;
            Assert.Equal(0, userVersion & DbContext.FoldReadyFlag);

            using var versionCmd = verify.CreateCommand();
            versionCmd.CommandText = $"SELECT value FROM codeindex_meta WHERE key = '{DbContext.GetSymbolExtractorVersionMetaKey("csharp")}'";
            var storedVersion = versionCmd.ExecuteScalar() as string;
            Assert.NotEqual(SymbolExtractor.GetContractVersion("csharp").ToString(System.Globalization.CultureInfo.InvariantCulture), storedVersion);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_RestampsHotspotFamilyTrustOnOversizedFileSkip()
    {
        var projectRoot = CreateTempProject();
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "app.cs"), "public class App { public void Run() { } }");

            var (exitCode1, json1) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            Assert.Equal("success", json1.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var seededDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            WriteOversizedAsciiFile(Path.Combine(projectRoot, "app.cs"));

            var (exitCode2, json2) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("success", json2.GetProperty("status").GetString());
            Assert.Equal(0, json2.GetProperty("summary").GetProperty("errors").GetInt32());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampHotspotFamilyReadyWhenMarkerFingerprintChanges()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Caller.cs"), "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (exitCode1, json1) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode1);
            Assert.Equal("success", json1.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var seededDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                Assert.Equal(DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), seededDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));

            File.WriteAllText(Path.Combine(projectRoot, "Extra.csproj"), "<Project />");

            var (exitCode2, json2) = RunAndCaptureJson([projectRoot, "--files", "Extra.csproj", "--json"]);
            Assert.Equal(CommandExitCodes.Success, exitCode2);
            Assert.Equal("success", json2.GetProperty("status").GetString());

            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp")));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [ProductionRuntimeFact]
    public void Run_Update_WhenHotspotFamilyMetadataCannotBeRestamped_KeepsReferenceIdentityStale_Issue4914()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            var callerPath = Path.Combine(projectRoot, "src", "Caller.cs");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.Equal("success", initialJson.GetProperty("status").GetString());

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.GetHotspotFamilyVersionMetaKey("csharp"), null);
                writer.SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp"), null);
                writer.SetMeta(
                    DbContext.ReferenceIdentityContractVersionMetaKey,
                    (DbContext.ReferenceIdentityContractVersion - 1).ToString(
                        CultureInfo.InvariantCulture));
            }

            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); api.Run(); } }");
            File.SetLastWriteTimeUtc(callerPath, DateTime.UtcNow.AddSeconds(2));

            var (updateExitCode, updateJson) = RunAndCaptureJson([projectRoot, "--files", "src/Caller.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);
            Assert.False(updateJson.GetProperty("hotspot_family_ready").GetBoolean());
            Assert.Contains("hotspot_family_support_not_indexed=csharp", updateJson.GetProperty("hotspot_family_degraded_reason").GetString());

            using (var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                Assert.NotEqual(
                    DbContext.ReferenceIdentityContractVersion.ToString(CultureInfo.InvariantCulture),
                    verifyDb.GetMetaString(DbContext.ReferenceIdentityContractVersionMetaKey));
            }

            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); api.Run(); api.Run(1); } }");
            File.SetLastWriteTimeUtc(callerPath, DateTime.UtcNow.AddSeconds(4));

            var (subprocessExitCode, _, errorOutput) = RunCliInSubprocess([projectRoot, "--files", "src/Caller.cs"], projectRoot);
            Assert.Equal(CommandExitCodes.Success, subprocessExitCode);
            Assert.Contains("Index completed with degraded readiness", errorOutput);
            Assert.Contains("hotspot_family_ready=false", errorOutput);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_Update_RollsBackHotspotFamilyRestampWhenCommitIsInterrupted()
    {
        var projectRoot = CreateTempProject();
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), "<Project />");
            var callerPath = Path.Combine(projectRoot, "src", "Caller.cs");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part1.cs"), "public partial class Api { public void Run() { } }");
            File.WriteAllText(Path.Combine(projectRoot, "src", "Api.Part2.cs"), "public partial class Api { public void Run(int value) { } }");
            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); } }");

            var (initialExitCode, initialJson) = RunAndCaptureJson([projectRoot, "--json"]);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            Assert.True(initialJson.GetProperty("hotspot_family_ready").GetBoolean());

            File.WriteAllText(callerPath, "public class Caller { public void Call(Api api) { api.Run(); api.Run(1); api.Run(); } }");
            File.SetLastWriteTimeUtc(callerPath, DateTime.UtcNow.AddSeconds(2));

            IndexCommandRunner.HotspotFamilyUpdateRestampReadyForCommitForTesting = () =>
                throw new InvalidOperationException("simulate crash after hotspot restamp");

            Assert.Throws<InvalidOperationException>(() =>
                RunAndCaptureJson([projectRoot, "--files", "src/Caller.cs", "--json"]));

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var verifyDb = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyVersionMetaKey("csharp")));
            Assert.Null(verifyDb.GetMetaString(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey("csharp")));
        }
        finally
        {
            IndexCommandRunner.HotspotFamilyUpdateRestampReadyForCommitForTesting = null;
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotRestampFoldReadyWhenFoldFingerprintMismatches()
    {
        // #97: partial update must not restamp FoldReady when the stored runtime canary
        // fingerprint differs from the current binary/runtime, even if NameFold.Version is
        // unchanged. Untouched rows still carry keys generated under the old runtime tables.
        // #97: version が同じでも fingerprint がズレた DB は partial update で restamp しない。
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

            var targetFile = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(targetFile, "public class App { public void Run() { } }\n");
            File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));
            var exitCode2 = IndexCommandRunner.Run([projectRoot, "--files", targetFile, "--json"], _jsonOptions);
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
            Assert.NotEqual(NameFold.Fingerprint(), storedFingerprint);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_DoesNotOverwriteIndexedHeadCommit()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "init");
            var initialHead = RunGitCaptureStdOut(projectRoot, "rev-parse", "HEAD").Trim();

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);

            RunGit(projectRoot, "checkout", "-b", "feature");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } public void Extra() { } }\n");
            File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "feature");

            // `--files` is a user-driven partial update. It must NOT republish the captured
            // HEAD; the next default full scan is what advances the stale marker. Issue #1508.
            // `--files` は利用者指定の部分更新。HEAD を進めず、次の full scan で初めて更新する。
            var (updateExitCode, _) = RunAndCaptureJson([projectRoot, "--files", "app.cs", "--json"]);
            Assert.Equal(CommandExitCodes.Success, updateExitCode);

            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            Assert.Equal(initialHead, db.GetMetaString(DbContext.IndexedHeadCommitMetaKey));
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_UpdateMode_WithChangedBetween_PurgesMissingIndexedPathOutsideDiff_4056()
    {
        var projectRoot = CreateTempProject();
        try
        {
            RunGit(projectRoot, "init");
            var sourcePath = Path.Combine(projectRoot, "app.cs");
            File.WriteAllText(sourcePath, "public class App { }\n");
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "initial");
            RunGit(projectRoot, "branch", "before-main");

            RunGit(projectRoot, "checkout", "-b", "stale-index");
            var changelogDir = Path.Combine(projectRoot, "changelog.d", "unreleased");
            Directory.CreateDirectory(changelogDir);
            var stalePath = Path.Combine(changelogDir, "+changelog-empty-release-guard.fixed.md");
            File.WriteAllText(
                stalePath,
                """
                ---
                category: fixed
                ---

                ## English

                - Placeholder.

                ## 日本語

                - プレースホルダー。
                """);
            RunGit(projectRoot, "add", ".");
            RunGit(projectRoot, "commit", "-m", "add stale branch fragment");

            var initialExitCode = IndexCommandRunner.Run([projectRoot, "--json"], _jsonOptions);
            Assert.Equal(CommandExitCodes.Success, initialExitCode);
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            Assert.Contains("changelog.d/unreleased/+changelog-empty-release-guard.fixed.md", ReadIndexedPaths(dbPath));

            RunGit(projectRoot, "checkout", "-b", "main-update", "before-main");
            File.WriteAllText(sourcePath, "public class App { public void Run() { } }\n");
            RunGit(projectRoot, "add", "app.cs");
            RunGit(projectRoot, "commit", "-m", "update app");
            RunGit(projectRoot, "branch", "after-main");

            var (exitCode, json) = RunAndCaptureJson([projectRoot, "--changed-between", "before-main", "after-main", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("success", json.GetProperty("status").GetString());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("updated").GetInt32());
            Assert.Equal(1, json.GetProperty("summary").GetProperty("removed").GetInt32());

            var indexedPaths = ReadIndexedPaths(dbPath);
            Assert.DoesNotContain("changelog.d/unreleased/+changelog-empty-release-guard.fixed.md", indexedPaths);
            Assert.Contains("app.cs", indexedPaths);

            var (statusExitCode, statusJson) = RunStatusAndCaptureJson(["--db", dbPath, "--check", "--json"]);
            Assert.Equal(CommandExitCodes.Success, statusExitCode);
            Assert.True(statusJson.GetProperty("workspace_check").GetProperty("matches_workspace").GetBoolean());
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }
}
