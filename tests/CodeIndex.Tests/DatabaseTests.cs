using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for DbContext and DbWriter integration.
/// DbContextとDbWriterの統合テスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class DatabaseTests : IDisposable
{
    private readonly string _dbDir;
    private readonly string _dbPath;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public DatabaseTests()
    {
        _dbDir = TestProjectHelper.CreateTempProject("codeindex_test");
        _dbPath = Path.Combine(_dbDir, "codeindex.db");
        _db = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void InsertSymbols_CSharpLongMalformedUsingAliasSignature_DoesNotThrow()
    {
        var fileId = UpsertTestFile("src/Alias.cs", checksum: "alias");
        var signature = "using Alias = " + new string('A', 50_000);

        var exception = Record.Exception(() => _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = "Alias",
                Line = 1,
                StartLine = 1,
                EndLine = 1,
                Signature = signature,
            },
        ]));

        Assert.Null(exception);
    }

    [Fact]
    public void SleepBeforeRetry_CancellationDuringDefaultWaitStopsPromptly_Issue3952()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() =>
            DbConnectionFactory.SleepBeforeRetry(5_000, sleep: null, cts.Token));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Retry wait took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Search_ExactSymbolBoostPrefersChunkContainingSymbol_Issue1977()
    {
        var fileId = InsertSearchFile(
            [
                new ChunkRecord { ChunkIndex = 0, StartLine = 1, EndLine = 10, Content = "UserManager usage" },
                new ChunkRecord { ChunkIndex = 1, StartLine = 20, EndLine = 30, Content = "class UserManager" },
            ],
            [
                new SymbolRecord { Kind = "class", Name = "UserManager", Line = 20, StartLine = 20, EndLine = 30 },
            ]);

        var reader = new DbReader(_db.Connection);
        var results = reader.Search("UserManager", limit: 2, deduplicate: false);

        Assert.Equal(fileId, Assert.Single(ReadFileIds()));
        Assert.Equal(20, results[0].StartLine);
    }

    [Fact]
    public void Search_SymbolKindWeightPrefersDefinitionsOverGenericMentions_Issue1958()
    {
        InsertSearchFile(
            [
                new ChunkRecord { ChunkIndex = 0, StartLine = 1, EndLine = 10, Content = "Manager" },
                new ChunkRecord { ChunkIndex = 1, StartLine = 20, EndLine = 30, Content = "Manager" },
            ],
            [
                new SymbolRecord { Kind = "reference", Name = "HelperReference", Line = 1, StartLine = 1, EndLine = 10 },
                new SymbolRecord { Kind = "function", Name = "CreateManager", Line = 20, StartLine = 20, EndLine = 30 },
            ]);

        var reader = new DbReader(_db.Connection);
        var results = reader.Search("Manager", limit: 2, deduplicate: false);

        Assert.Equal(20, results[0].StartLine);
    }

    [Fact]
    public void Search_NestingDepthPrefersScopeRootForOverlappingResults_Issue1975()
    {
        InsertSearchFile(
            [
                new ChunkRecord { ChunkIndex = 0, StartLine = 1, EndLine = 100, Content = "UserManager" },
                new ChunkRecord { ChunkIndex = 1, StartLine = 20, EndLine = 40, Content = "UserManager" },
            ],
            [
                new SymbolRecord { Kind = "class", Name = "UserManager", Line = 1, StartLine = 1, EndLine = 100 },
                new SymbolRecord { Kind = "function", Name = "Login", Line = 20, StartLine = 20, EndLine = 40, ContainerQualifiedName = "UserManager" },
            ]);

        var reader = new DbReader(_db.Connection);
        var results = reader.Search("UserManager", limit: 2);

        var result = Assert.Single(results);
        Assert.Equal(1, result.StartLine);
    }

    [Fact]
    public void Search_StructuredFieldScorePrefersSymbolNameHitsOverCommentText_Issue2000()
    {
        InsertSearchFile(
            [
                new ChunkRecord { ChunkIndex = 0, StartLine = 1, EndLine = 10, Content = "Manager" },
                new ChunkRecord { ChunkIndex = 1, StartLine = 20, EndLine = 30, Content = "Manager" },
            ],
            [
                new SymbolRecord { Kind = "reference", Name = "CommentOnly", Line = 1, StartLine = 1, EndLine = 10 },
                new SymbolRecord { Kind = "reference", Name = "BuildUserManagerValue", Line = 20, StartLine = 20, EndLine = 30 },
            ]);

        var reader = new DbReader(_db.Connection);
        var results = reader.Search("Manager", limit: 2, deduplicate: false);

        Assert.Equal(20, results[0].StartLine);
    }

    [Fact]
    public void InitializeSchema_CreatesAllTables()
    {
        // Verify tables exist by querying sqlite_master
        // sqlite_masterを問い合わせてテーブルの存在を確認
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            tables.Add(reader.GetString(0));

        Assert.Contains("files", tables);
        Assert.Contains("chunks", tables);
        Assert.Contains("symbols", tables);
        Assert.Contains("symbol_references", tables);
        Assert.Contains("fts_chunks", tables);
    }

    private long InsertSearchFile(IReadOnlyList<ChunkRecord> chunks, IReadOnlyList<SymbolRecord> symbols)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/search.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 120,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = Guid.NewGuid().ToString("N"),
        });

        foreach (var chunk in chunks)
            chunk.FileId = fileId;
        foreach (var symbol in symbols)
            symbol.FileId = fileId;

        _writer.InsertChunks(chunks);
        _writer.InsertSymbols(symbols);
        return fileId;
    }

    private List<long> ReadFileIds()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM files ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    [Fact]
    public void InitializeSchema_CreatesFoldedMutualReferenceIndex()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_symbol_refs_mutual_folded'";

        Assert.Equal("idx_symbol_refs_mutual_folded", (string?)cmd.ExecuteScalar());
    }

    [Fact]
    public void InsertReferences_UsesFoldedNamesForMutualRecursion()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/app.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 4,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "abc",
        });

        _writer.InsertReferences(
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Run",
                ReferenceKind = "call",
                Line = 1,
                Column = 1,
                Context = "Start();",
                ContainerKind = "function",
                ContainerName = "Start",
            },
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Start",
                ReferenceKind = "call",
                Line = 2,
                Column = 1,
                Context = "Run();",
                ContainerKind = "function",
                ContainerName = "Run",
            },
        ]);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1";

        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void RefreshMutualRecursionFlags_UpdatesOnlyChangedRowsAndClearsBrokenCycles()
    {
        var fileId = UpsertTestFile("src/mutual.cs", checksum: "mutual-differential");
        _writer.InsertReferences(
        [
            new ReferenceRecord { FileId = fileId, SymbolName = "Beta", ReferenceKind = "call", Line = 1, Column = 1, Context = "Beta();", ContainerName = "Alpha" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Alpha", ReferenceKind = "call", Line = 2, Column = 1, Context = "Alpha();", ContainerName = "Beta" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Delta", ReferenceKind = "call", Line = 3, Column = 1, Context = "Delta();", ContainerName = "Gamma" },
        ],
        refreshMutualRecursionFlags: false);
        ExecuteNonQuery(_db.Connection, """
            CREATE TABLE mutual_refresh_audit (reference_id INTEGER, old_value INTEGER, new_value INTEGER);
            CREATE TRIGGER audit_mutual_refresh
            AFTER UPDATE OF is_mutual_recursion ON symbol_references
            BEGIN
                INSERT INTO mutual_refresh_audit (reference_id, old_value, new_value)
                VALUES (NEW.id, OLD.is_mutual_recursion, NEW.is_mutual_recursion);
            END;
            """);

        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(2, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(2, ExecuteScalarLong("SELECT COUNT(*) FROM mutual_refresh_audit"));
        Assert.Equal(2, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1"));

        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(0, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(2, ExecuteScalarLong("SELECT COUNT(*) FROM mutual_refresh_audit"));

        ExecuteNonQuery(_db.Connection, "DELETE FROM symbol_references WHERE container_name = 'Beta' AND symbol_name = 'Alpha'");
        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(1, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(3, ExecuteScalarLong("SELECT COUNT(*) FROM mutual_refresh_audit"));
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1"));
    }

    [Fact]
    public void RefreshReferenceIdentities_UpdatesOnlyChangedRowsAndRollsBackEarlierPhases()
    {
        var fileId = UpsertTestFile(
            "src/reference-identity-differential.cs",
            checksum: "reference-identity-differential");
        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = "Invoke",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
            },
        ]);
        _writer.InsertReferences(
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Invoke",
                ReferenceKind = "call",
                Line = 2,
                Column = 9,
                Context = "Invoke();",
                ContainerKind = "function",
                ContainerName = "Invoke",
            },
        ],
        refreshMutualRecursionFlags: false);
        _writer.RefreshMutualRecursionFlags();

        ExecuteNonQuery(_db.Connection, """
            CREATE TABLE reference_identity_refresh_audit (phase TEXT NOT NULL);
            CREATE TRIGGER audit_reference_identity_source
            AFTER UPDATE OF source_symbol_id ON symbol_references
            BEGIN
                INSERT INTO reference_identity_refresh_audit VALUES ('source');
            END;
            CREATE TRIGGER audit_reference_identity_resolution
            AFTER UPDATE OF target_symbol_id, target_symbol_key, resolution_candidate_count, resolution_state
            ON symbol_references
            BEGIN
                INSERT INTO reference_identity_refresh_audit VALUES ('resolution');
            END;
            CREATE TRIGGER audit_reference_identity_self
            AFTER UPDATE OF is_self_reference ON symbol_references
            BEGIN
                INSERT INTO reference_identity_refresh_audit VALUES ('self');
            END;
            CREATE TRIGGER audit_reference_identity_mutual
            AFTER UPDATE OF is_mutual_recursion ON symbol_references
            BEGIN
                INSERT INTO reference_identity_refresh_audit VALUES ('mutual');
            END;
            """);

        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(0, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit"));

        ExecuteNonQuery(_db.Connection, """
            UPDATE symbol_references
            SET source_symbol_id = NULL,
                target_symbol_id = NULL,
                target_symbol_key = 'stale-target',
                resolution_candidate_count = 9,
                resolution_state = 'ambiguous',
                is_self_reference = 0,
                is_mutual_recursion = 2;
            DELETE FROM reference_identity_refresh_audit;
            CREATE TRIGGER fail_reference_identity_resolution
            BEFORE UPDATE OF target_symbol_id ON symbol_references
            BEGIN
                SELECT RAISE(ABORT, 'forced resolution refresh failure');
            END;
            """);

        Assert.Throws<SqliteException>(() => _writer.RefreshMutualRecursionFlags());
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE source_symbol_id IS NULL"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE target_symbol_key = 'stale-target'"));

        ExecuteNonQuery(_db.Connection, "DROP TRIGGER fail_reference_identity_resolution");
        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(1, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit WHERE phase = 'source'"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit WHERE phase = 'resolution'"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit WHERE phase = 'self'"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit WHERE phase = 'mutual'"));
        Assert.Equal(1, ExecuteScalarLong("""
            SELECT COUNT(*)
            FROM symbol_references
            WHERE source_symbol_id = target_symbol_id
              AND target_symbol_id IS NOT NULL
              AND target_symbol_key IS NOT NULL
              AND resolution_candidate_count = 1
              AND resolution_state = 'resolved'
              AND is_self_reference = 1
              AND is_mutual_recursion = 0
            """));

        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(0, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(4, ExecuteScalarLong("SELECT COUNT(*) FROM reference_identity_refresh_audit"));
    }

    [Fact]
    public void ReferenceGraphDirtyScope_GeneratedSqlUsesDirtyPrimaryKeySeeks()
    {
        using var scope = _writer.BeginReferenceGraphRefreshScope();
        _writer.RefreshMutualRecursionFlags();

        var candidateSql = DbWriter.RefreshScopedReferenceCandidatesSqlForTesting;
        Assert.DoesNotContain("AND s.name_folded IS NOT NULL", candidateSql, StringComparison.Ordinal);
        Assert.Contains(
            "FROM temp.reference_graph_lookup_names AS lookup_name",
            candidateSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CROSS JOIN symbols AS s INDEXED BY idx_symbols_name_folded",
            candidateSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "AND s.name_folded = lookup_name.name_folded",
            candidateSql,
            StringComparison.Ordinal);
        Assert.Equal(
            10,
            candidateSql.Split(
                "FROM temp.reference_graph_dirty_references AS dirty_reference",
                StringSplitOptions.None).Length - 1);

        var candidateInserts = candidateSql
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static statement => statement.StartsWith(
                "INSERT INTO symbol_reference_candidates",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(10, candidateInserts.Length);
        foreach (var statement in candidateInserts)
        {
            var plan = ReadQueryPlanDetails(_db.Connection, statement);
            Assert.Contains(plan, static detail => detail.Contains(
                "SEARCH r USING INTEGER PRIMARY KEY",
                StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(plan, static detail =>
                detail.Equals("SCAN r", StringComparison.OrdinalIgnoreCase)
                || detail.StartsWith("SCAN r ", StringComparison.OrdinalIgnoreCase));
        }

        var instantiateStatement = Assert.Single(candidateInserts.Where(static statement =>
            statement.Contains("AS unique_target", StringComparison.Ordinal)));
        var instantiatePlan = ReadQueryPlanDetails(_db.Connection, instantiateStatement);
        Assert.Contains(instantiatePlan, static detail => detail.Contains(
            "idx_symbols_name_folded",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(instantiatePlan, static detail => detail.Contains(
            "SEARCH lookup_name USING PRIMARY KEY",
            StringComparison.OrdinalIgnoreCase));

        foreach (var statement in DbWriter.ScopedReferenceGraphUpdateStatementsForTesting)
        {
            var plan = ReadQueryPlanDetails(_db.Connection, statement);
            Assert.Contains(plan, static detail => detail.Contains(
                "SEARCH r USING INTEGER PRIMARY KEY",
                StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(plan, static detail =>
                detail.Equals("SCAN r", StringComparison.OrdinalIgnoreCase)
                || detail.StartsWith("SCAN r ", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ReferenceGraphDirtyScope_TinySetSkipsGlobalReferenceCountWithoutDiagnostics()
    {
        var fileId = UpsertTestFileWithLanguage("src/tiny-dirty.cs", "csharp", "tiny-dirty-initial");
        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "Missing", ReferenceKind = "call", Line = 1, Column = 1, Context = "Missing();" },
        ], refreshMutualRecursionFlags: false);
        _writer.RefreshMutualRecursionFlags();

        var countedTables = new List<string>();
        var previousCountHook = DbWriter.ReferenceGraphRowCountForTesting;
        var previousStatsHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        try
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = null;
            DbWriter.ReferenceGraphRowCountForTesting = countedTables.Add;
            using var scope = _writer.BeginReferenceGraphRefreshScope();
            using (var transaction = _writer.BeginTransaction())
            {
                fileId = _writer.UpsertFile(new FileRecord
                {
                    Path = "src/tiny-dirty.cs",
                    Lang = "csharp",
                    Size = 100,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "tiny-dirty-updated",
                });
                _writer.InsertReferences([
                    new ReferenceRecord { FileId = fileId, SymbolName = "Missing", ReferenceKind = "call", Line = 1, Column = 1, Context = "Missing();" },
                ], refreshMutualRecursionFlags: false);
                transaction.Commit();
            }

            _writer.RefreshMutualRecursionFlags();

            Assert.Contains("temp.reference_graph_dirty_references", countedTables);
            Assert.DoesNotContain("symbol_references", countedTables);
        }
        finally
        {
            DbWriter.ReferenceGraphRowCountForTesting = previousCountHook;
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousStatsHook;
        }
    }

    [Fact]
    public void ReferenceGraphDirtyScope_TracksLanguageTransitionsAndMatchesFullRefresh()
    {
        var csharpCallerId = UpsertTestFileWithLanguage("src/caller.cs", "csharp", "dirty-csharp-caller");
        var pythonCallerId = UpsertTestFileWithLanguage("src/caller.py", "python", "dirty-python-caller");
        var targetId = UpsertTestFileWithLanguage("src/target.cs", "csharp", "dirty-target-csharp");
        var stableTargetId = UpsertTestFileWithLanguage("src/stable-target.cs", "csharp", "dirty-stable-target");
        _writer.InsertSymbols([
            new SymbolRecord { FileId = targetId, Kind = "function", Name = "Pivot", Line = 1 },
            new SymbolRecord { FileId = stableTargetId, Kind = "function", Name = "StableTarget", Line = 1 },
            new SymbolRecord { FileId = stableTargetId, Kind = "class", Name = "StableType", Line = 2 },
        ]);
        _writer.InsertReferences([
            new ReferenceRecord { FileId = csharpCallerId, SymbolName = "Pivot", ReferenceKind = "call", Line = 1, Column = 1, Context = "Pivot();" },
            new ReferenceRecord { FileId = pythonCallerId, SymbolName = "Pivot", ReferenceKind = "call", Line = 1, Column = 1, Context = "Pivot()" },
        ], refreshMutualRecursionFlags: false);
        _writer.RefreshMutualRecursionFlags();

        Assert.Equal("resolved", ReadReferenceResolutionState(csharpCallerId));
        Assert.Equal("unresolved", ReadReferenceResolutionState(pythonCallerId));

        DbWriter.ReferenceGraphRefreshScopeStats? observed = null;
        var previousHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        try
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats => observed = stats;
            using (var scope = _writer.BeginReferenceGraphRefreshScope())
            {
                using var transaction = _writer.BeginTransaction();
                targetId = _writer.UpsertFile(new FileRecord
                {
                    Path = "src/target.cs",
                    Lang = "python",
                    Size = 100,
                    Lines = 4,
                    Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "dirty-target-python",
                });
                _writer.InsertSymbols([
                    new SymbolRecord { FileId = targetId, Kind = "function", Name = "Pivot", Line = 1 },
                ]);
                var newCallerId = _writer.InsertNewFile(new FileRecord
                {
                    Path = "src/new-caller.cs",
                    Lang = "csharp",
                    Size = 100,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "dirty-new-caller",
                });
                _writer.InsertReferences([
                    new ReferenceRecord { FileId = newCallerId, SymbolName = "StableTarget", ReferenceKind = "call", Line = 1, Column = 1, Context = "StableTarget();" },
                    new ReferenceRecord { FileId = newCallerId, SymbolName = "StableType", ReferenceKind = "instantiate", Line = 2, Column = 1, Context = "new StableType();" },
                ], refreshMutualRecursionFlags: false);
                transaction.Commit();

                _writer.RefreshMutualRecursionFlags();
            }

            Assert.NotNull(observed);
            Assert.False(observed!.UsedFullRefresh);
            Assert.Equal(4, observed.DirtyReferenceCount);
            Assert.Equal(4, observed.TotalReferenceCount);
            Assert.Equal("unresolved", ReadReferenceResolutionState(csharpCallerId));
            Assert.Equal("resolved", ReadReferenceResolutionState(pythonCallerId));
            Assert.Equal(2, ExecuteScalarLong("""
                SELECT COUNT(*)
                FROM symbol_references AS r
                JOIN files AS f ON f.id = r.file_id
                WHERE f.path = 'src/new-caller.cs'
                  AND r.resolution_state = 'resolved'
                """));

            var scopedSnapshot = ReadReferenceIdentitySnapshot();
            _writer.RefreshMutualRecursionFlags();
            Assert.Equal(scopedSnapshot, ReadReferenceIdentitySnapshot());

            observed = null;
            using (var renameScope = _writer.BeginReferenceGraphRefreshScope())
            {
                using var renameTransaction = _writer.BeginTransaction();
                Assert.True(_writer.DeleteFileByPath("src/target.cs"));
                var renamedTargetId = _writer.InsertNewFile(new FileRecord
                {
                    Path = "src/renamed-target.py",
                    Lang = "python",
                    Size = 100,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "dirty-target-renamed",
                });
                _writer.InsertSymbols([
                    new SymbolRecord { FileId = renamedTargetId, Kind = "function", Name = "Pivot", Line = 1 },
                ]);
                renameTransaction.Commit();
                _writer.RefreshMutualRecursionFlags();
            }

            Assert.NotNull(observed);
            Assert.False(observed!.UsedFullRefresh);
            Assert.Equal("unresolved", ReadReferenceResolutionState(csharpCallerId));
            Assert.Equal("resolved", ReadReferenceResolutionState(pythonCallerId));
            Assert.Contains("src/renamed-target.py", ExecuteScalarString($"""
                SELECT target_symbol_key
                FROM symbol_references
                WHERE file_id = {pythonCallerId.ToString(CultureInfo.InvariantCulture)}
                """), StringComparison.Ordinal);
            var renamedSnapshot = ReadReferenceIdentitySnapshot();
            _writer.RefreshMutualRecursionFlags();
            Assert.Equal(renamedSnapshot, ReadReferenceIdentitySnapshot());
            Assert.Equal(0, ExecuteScalarLong("""
                SELECT COUNT(*)
                FROM symbol_reference_candidates AS candidate
                LEFT JOIN symbol_references AS reference ON reference.id = candidate.reference_id
                LEFT JOIN symbols AS symbol ON symbol.id = candidate.symbol_id
                WHERE reference.id IS NULL OR symbol.id IS NULL
                """));
        }
        finally
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousHook;
        }
    }

    [Fact]
    public void ReferenceGraphDirtyScope_RollbackAndCancellationPreserveRetryState()
    {
        var callerId = UpsertTestFileWithLanguage("src/retry-caller.cs", "csharp", "retry-caller");
        var originalTargetId = UpsertTestFileWithLanguage("src/retry-target.cs", "csharp", "retry-target");
        _writer.InsertSymbols([
            new SymbolRecord { FileId = originalTargetId, Kind = "function", Name = "RetryTarget", Line = 1 },
        ]);
        _writer.InsertReferences([
            new ReferenceRecord { FileId = callerId, SymbolName = "RetryTarget", ReferenceKind = "call", Line = 1, Column = 1, Context = "RetryTarget();" },
        ], refreshMutualRecursionFlags: false);
        _writer.RefreshMutualRecursionFlags();
        Assert.Equal("resolved", ReadReferenceResolutionState(callerId));

        var previousHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        using var scope = _writer.BeginReferenceGraphRefreshScope();
        try
        {
            using (var rolledBack = _writer.BeginTransaction())
            {
                var rolledBackTargetId = _writer.InsertNewFile(new FileRecord
                {
                    Path = "src/retry-rolled-back.cs",
                    Lang = "csharp",
                    Size = 100,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "retry-rolled-back",
                });
                _writer.InsertSymbols([
                    new SymbolRecord { FileId = rolledBackTargetId, Kind = "function", Name = "RetryTarget", Line = 1 },
                ]);
            }

            DbWriter.ReferenceGraphRefreshScopeStats? rollbackStats = null;
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats => rollbackStats = stats;
            _writer.RefreshMutualRecursionFlags();
            Assert.NotNull(rollbackStats);
            Assert.False(rollbackStats!.UsedFullRefresh);
            Assert.Equal(0, rollbackStats.DirtyReferenceCount);
            Assert.Equal("resolved", ReadReferenceResolutionState(callerId));

            using (var committed = _writer.BeginTransaction())
            {
                var secondTargetId = _writer.InsertNewFile(new FileRecord
                {
                    Path = "src/retry-second.cs",
                    Lang = "csharp",
                    Size = 100,
                    Lines = 1,
                    Modified = new DateTime(2025, 1, 3, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "retry-second",
                });
                _writer.InsertSymbols([
                    new SymbolRecord { FileId = secondTargetId, Kind = "function", Name = "RetryTarget", Line = 1 },
                ]);
                committed.Commit();
            }

            using var cancellation = new CancellationTokenSource();
            DbWriter.ReferenceGraphRefreshScopeForTesting = _ => cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                _writer.RefreshMutualRecursionFlags(cancellation.Token));
            Assert.Equal("resolved", ReadReferenceResolutionState(callerId));
            Assert.False(_writer.ReferenceIdentityContractMatchesCurrent());

            DbWriter.ReferenceGraphRefreshScopeStats? retryStats = null;
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats => retryStats = stats;
            _writer.RefreshMutualRecursionFlags();
            Assert.NotNull(retryStats);
            Assert.False(retryStats!.UsedFullRefresh);
            Assert.Equal(1, retryStats.DirtyReferenceCount);
            Assert.Equal("unresolved", ReadReferenceResolutionState(callerId));
            Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());
        }
        finally
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousHook;
        }
    }

    [Fact]
    public void ReferenceGraphDirtyScope_BroadSetFallsBackToFullRefresh()
    {
        const int referenceCount = 4_100;
        var fileId = UpsertTestFileWithLanguage("src/broad-dirty.cs", "csharp", "broad-dirty-initial");
        var references = Enumerable.Range(1, referenceCount)
            .Select(line => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"Missing{line}",
                ReferenceKind = "call",
                Line = line,
                Column = 1,
                Context = $"Missing{line}();",
            })
            .ToArray();
        _writer.InsertReferences(references, refreshMutualRecursionFlags: false);
        _writer.RefreshMutualRecursionFlags();

        DbWriter.ReferenceGraphRefreshScopeStats? observed = null;
        var previousHook = DbWriter.ReferenceGraphRefreshScopeForTesting;
        try
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = stats => observed = stats;
            using var scope = _writer.BeginReferenceGraphRefreshScope();
            using (var transaction = _writer.BeginTransaction())
            {
                fileId = _writer.UpsertFile(new FileRecord
                {
                    Path = "src/broad-dirty.cs",
                    Lang = "csharp",
                    Size = 100,
                    Lines = referenceCount,
                    Modified = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                    Checksum = "broad-dirty-updated",
                });
                foreach (var reference in references)
                    reference.FileId = fileId;
                _writer.InsertReferences(references, refreshMutualRecursionFlags: false);
                transaction.Commit();
            }
            _writer.RefreshMutualRecursionFlags();

            Assert.NotNull(observed);
            Assert.True(observed!.UsedFullRefresh);
            Assert.Equal(referenceCount, observed.DirtyReferenceCount);
            Assert.Equal(referenceCount, observed.TotalReferenceCount);
        }
        finally
        {
            DbWriter.ReferenceGraphRefreshScopeForTesting = previousHook;
        }
    }

    [Fact]
    public void RefreshMutualRecursionFlags_CancellationInterruptsRunningSqlAndRollsBack()
    {
        var writer = new DbWriter(_db);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/mutual-cancel.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "mutual-cancel",
        });
        writer.InsertReferences(
        [
            new ReferenceRecord { FileId = fileId, SymbolName = "Beta", ReferenceKind = "call", Line = 1, Column = 1, Context = "Beta();", ContainerName = "Alpha" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Alpha", ReferenceKind = "call", Line = 2, Column = 1, Context = "Alpha();", ContainerName = "Beta" },
        ],
        refreshMutualRecursionFlags: false);

        using var cancellation = new CancellationTokenSource();
        var cancellationFunctionCalls = 0;
        _db.Connection.CreateFunction(
            "cancel_mutual_refresh",
            () =>
            {
                cancellationFunctionCalls++;
                cancellation.Cancel();
                return 1;
            });
        ExecuteNonQuery(_db.Connection, """
            CREATE TRIGGER cancel_running_mutual_refresh
            BEFORE UPDATE OF is_mutual_recursion ON symbol_references
            BEGIN
                SELECT cancel_mutual_refresh();
            END;
            """);

        var exception = Assert.Throws<OperationCanceledException>(
            () => writer.RefreshMutualRecursionFlags(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.True(cancellationFunctionCalls > 0);
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion <> 0"));

        ExecuteNonQuery(_db.Connection, "DROP TRIGGER cancel_running_mutual_refresh");
        var hitsBeforeRetry = _db.PreparedCommands.HitCount;
        writer.RefreshMutualRecursionFlags();

        Assert.True(_db.PreparedCommands.HitCount > hitsBeforeRetry);
        Assert.Equal(2, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1"));
    }

    [Fact]
    public void RefreshMutualRecursionFlags_NormalizesUnexpectedStoredValues()
    {
        var fileId = UpsertTestFile("src/mutual-normalize.cs", checksum: "mutual-normalize");
        _writer.InsertReferences(
        [
            new ReferenceRecord { FileId = fileId, SymbolName = "Beta", ReferenceKind = "call", Line = 1, Column = 1, Context = "Beta();", ContainerName = "Alpha" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Alpha", ReferenceKind = "call", Line = 2, Column = 1, Context = "Alpha();", ContainerName = "Beta" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Delta", ReferenceKind = "call", Line = 3, Column = 1, Context = "Delta();", ContainerName = "Gamma" },
        ],
        refreshMutualRecursionFlags: false);
        ExecuteNonQuery(_db.Connection, "UPDATE symbol_references SET is_mutual_recursion = 2");

        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(3, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(2, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1"));
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 0"));
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion NOT IN (0, 1)"));
    }

    [Fact]
    public void RefreshMutualRecursionFlags_RollsBackAllChangedRowsWhenTriggerAborts()
    {
        var fileId = UpsertTestFile("src/mutual-rollback.cs", checksum: "mutual-rollback");
        _writer.InsertReferences(
        [
            new ReferenceRecord { FileId = fileId, SymbolName = "Beta", ReferenceKind = "call", Line = 1, Column = 1, Context = "Beta();", ContainerName = "Alpha" },
            new ReferenceRecord { FileId = fileId, SymbolName = "Alpha", ReferenceKind = "call", Line = 2, Column = 1, Context = "Alpha();", ContainerName = "Beta" },
        ],
        refreshMutualRecursionFlags: false);
        ExecuteNonQuery(_db.Connection, """
            CREATE TRIGGER fail_second_mutual_refresh
            BEFORE UPDATE OF is_mutual_recursion ON symbol_references
            WHEN NEW.symbol_name = 'Alpha'
            BEGIN
                SELECT RAISE(ABORT, 'boom');
            END;
            """);

        Assert.Throws<SqliteException>(() => _writer.RefreshMutualRecursionFlags());
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion <> 0"));

        ExecuteNonQuery(_db.Connection, "DROP TRIGGER fail_second_mutual_refresh");
        _writer.RefreshMutualRecursionFlags();

        Assert.Equal(2, ExecuteScalarLong("SELECT changes()"));
        Assert.Equal(2, ExecuteScalarLong("SELECT COUNT(*) FROM symbol_references WHERE is_mutual_recursion = 1"));
    }

    [Fact]
    public void DeleteFileData_WhenReferencedLineIsDeleted_PreservesReferenceWithNullLineContext()
    {
        var callerFileId = UpsertTestFile("src/caller.cs", checksum: "caller");
        var lineOwnerFileId = UpsertTestFile("src/line-owner.cs", checksum: "line-owner");

        long referenceLineId;
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO reference_lines (file_id, line, context)
                VALUES (@fileId, 3, 'Target();')
                RETURNING id";
            cmd.Parameters.AddWithValue("@fileId", lineOwnerFileId);
            referenceLineId = (long)cmd.ExecuteScalar()!;
        }

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id
                )
                VALUES (@fileId, 'Target', 'call', 1, 1, NULL, @referenceLineId)";
            cmd.Parameters.AddWithValue("@fileId", callerFileId);
            cmd.Parameters.AddWithValue("@referenceLineId", referenceLineId);
            cmd.ExecuteNonQuery();
        }

        _writer.DeleteFileData(lineOwnerFileId);

        using var readCmd = _db.Connection.CreateCommand();
        readCmd.CommandText = "SELECT COUNT(*), COUNT(reference_line_id) FROM symbol_references WHERE file_id = @fileId";
        readCmd.Parameters.AddWithValue("@fileId", callerFileId);
        using var reader = readCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
    }

    [Fact]
    public void PurgeStaleFiles_RemovesCrossFileReferencesToSymbolsDefinedOnlyByDeletedFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("purge-stale-symbol-ref");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "target.py"), "# retained rename target");

            var callerFileId = UpsertTestFile("src/caller.cs", checksum: "caller");
            var staleTargetFileId = UpsertTestFile("src/target.cs", checksum: "target");
            _ = UpsertTestFile("src/target.py", checksum: "target");
            _writer.InsertSymbols([
                new SymbolRecord
                {
                    FileId = staleTargetFileId,
                    Kind = "function",
                    Name = "DeletedTarget",
                    Line = 1,
                },
            ]);
            _writer.InsertReferences([
                new ReferenceRecord
                {
                    FileId = callerFileId,
                    SymbolName = "DeletedTarget",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = "DeletedTarget();",
                },
            ]);

            var purged = _writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, "src/target.py");

            Assert.Equal(1, purged);
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE symbol_name = 'DeletedTarget'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PurgeStaleFilesSharingDirectoryAndStem_HandlesLikeWildcardCharacters()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("purge-stale-stem-wildcards");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "target_%100.py"), "# retained rename target");
            File.WriteAllText(Path.Combine(projectRoot, "src", "targetA%100.cs"), "# similar live file");

            var retainedId = UpsertTestFile("src/target_%100.py", checksum: "retained");
            var staleId = UpsertTestFile("src/target_%100.cs", checksum: "stale");
            var extensionlessStaleId = UpsertTestFile("src/target_%100", checksum: "stale-extensionless");
            var differentStemId = UpsertTestFile("src/target_%100..cs", checksum: "different-stem");
            var similarId = UpsertTestFile("src/targetA%100.cs", checksum: "similar");
            _writer.InsertChunks([
                new() { FileId = retainedId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "retained" },
                new() { FileId = staleId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "stale" },
                new() { FileId = extensionlessStaleId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "extensionless stale" },
                new() { FileId = differentStemId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "different stem" },
                new() { FileId = similarId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "similar" },
            ]);

            var purged = _writer.PurgeStaleFilesSharingDirectoryAndStem(projectRoot, "src/target_%100.py");

            Assert.Equal(2, purged);
            Assert.True(_writer.HasFileAtPath("src/target_%100.py"));
            Assert.False(_writer.HasFileAtPath("src/target_%100.cs"));
            Assert.False(_writer.HasFileAtPath("src/target_%100"));
            Assert.True(_writer.HasFileAtPath("src/target_%100..cs"));
            Assert.True(_writer.HasFileAtPath("src/targetA%100.cs"));
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
            Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ScopedFileCleanupPlan_CombinesKeysAndMergesSortedDeduplicatedIds()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("scoped-cleanup-plan-overlap");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "target.py"), "# retained");
            File.WriteAllText(Path.Combine(projectRoot, "src", "live.cs"), "// live duplicate");

            _ = UpsertTestFileWithLanguage("src/target.py", "python", "shared-checksum");
            var overlapId = UpsertTestFile("src/target.cs", "shared-checksum");
            var checksumOnlyId = UpsertTestFile("legacy/renamed.cs", "shared-checksum");
            var stemOnlyId = UpsertTestFileWithLanguage("src/target.ts", "typescript", "stem-only");
            _ = UpsertTestFile("src/live.cs", "shared-checksum");

            var combinedPlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/target.py",
                "shared-checksum",
                includeDirectoryAndStem: true);
            var checksumPlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/target.py",
                "shared-checksum",
                includeDirectoryAndStem: false);
            var stemPlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/target.py",
                checksum: null,
                includeDirectoryAndStem: true);
            var mergedPlan = FilePurgePlan.Merge([checksumPlan, stemPlan]);
            var expectedIds = new[] { overlapId, checksumOnlyId, stemOnlyId };
            Array.Sort(expectedIds);

            Assert.Equal(expectedIds, combinedPlan.FileIds);
            Assert.Equal(expectedIds, mergedPlan.FileIds);
            Assert.Equal(expectedIds.Length, combinedPlan.FileIds.Distinct().Count());
            Assert.Equal(300L, combinedPlan.DeletedBytes);
            Assert.True(combinedPlan.ByteEstimateComplete);
            Assert.True(_writer.HasCSharpFilesInFileIds(combinedPlan.FileIds));
            Assert.False(_writer.HasCSharpFilesInFileIds([stemOnlyId]));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CSharpScopedFileCleanupPlan_CommonChecksumQueriesOnceAndVisitsOnlyCSharpRows()
    {
        const int targetCount = 16;
        const string sharedChecksum = "shared-csharp-cleanup-checksum";
        var projectRoot = TestProjectHelper.CreateTempProject("csharp-scoped-cleanup-common-checksum");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var targets = new List<(
                string RetainedRelativePath,
                string? Checksum,
                bool IncludeDirectoryAndStem)>(targetCount + 1);
            for (var index = 0; index < targetCount; index++)
            {
                var relativePath = $"src/live-{index:D2}.cs";
                File.WriteAllText(
                    Path.Combine(projectRoot, "src", $"live-{index:D2}.cs"),
                    "public class Shared { }\n");
                _ = UpsertTestFile(relativePath, sharedChecksum);
                targets.Add((relativePath, sharedChecksum, false));

                _ = UpsertTestFileWithLanguage(
                    $"legacy/non-csharp-{index:D2}.py",
                    "python",
                    sharedChecksum);
            }

            const string unicodeRetainedPath = "src/Å.cs";
            const string unicodeAliasPath = "src/å.cs";
            File.WriteAllText(Path.Combine(projectRoot, "src", "Å.cs"), "// retained alias\n");
            File.WriteAllText(Path.Combine(projectRoot, "src", "å.cs"), "// old alias\n");
            _ = UpsertTestFile(unicodeRetainedPath, sharedChecksum);
            targets.Add((unicodeRetainedPath, sharedChecksum, false));
            var unicodeAliasId = UpsertTestFile(unicodeAliasPath, sharedChecksum);
            var staleCSharpId = UpsertTestFile("legacy/stale.cs", sharedChecksum);

            FilePurgePlan plan;
            List<QueryProfileEntry> profile;
            DbDebug.BeginProfile();
            try
            {
                plan = _writer.PlanStaleCSharpFilesSharingCleanupKeys(
                    projectRoot,
                    targets,
                    retainedPathComparison: StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                profile = DbDebug.EndProfile();
            }

            var expectedIds = new List<long> { staleCSharpId };
            var unicodeRetainedAbsolutePath = Path.Combine(projectRoot, "src", "Å.cs");
            var unicodeAliasAbsolutePath = Path.Combine(projectRoot, "src", "å.cs");
            if (FileIndexer.TryGetFileIdentity(unicodeRetainedAbsolutePath, out var retainedIdentity)
                && FileIndexer.TryGetFileIdentity(unicodeAliasAbsolutePath, out var aliasIdentity)
                && retainedIdentity == aliasIdentity)
            {
                expectedIds.Add(unicodeAliasId);
            }
            expectedIds.Sort();
            Assert.Equal(expectedIds, plan.FileIds);
            var checksumQuery = Assert.Single(
                profile.Where(entry => entry.Sql == DbWriter.StaleCSharpChecksumCandidateSql));
            Assert.Equal(targetCount + 2, checksumQuery.RowsScanned);
            Assert.Contains(
                checksumQuery.QueryPlan,
                row => row.Detail.Contains("idx_files_checksum", StringComparison.Ordinal));
            Assert.DoesNotContain(
                checksumQuery.QueryPlan,
                row => row.Detail.Contains("SCAN files", StringComparison.Ordinal));
            Assert.DoesNotContain(
                profile,
                entry => entry.Sql == DbWriter.StaleChecksumCandidateSql);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                _writer.PlanStaleCSharpFilesSharingCleanupKeys(
                    projectRoot,
                    targets,
                    cancelled.Token,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _ = DbDebug.EndProfile();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ScopedFileCleanupPlan_ApplyDeletesOnlyIdsCapturedBeforeLaterMatchingRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("scoped-cleanup-plan-snapshot");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "target.py"), "# retained");
            var plannedId = UpsertTestFile("src/target.cs", "shared-checksum");

            var plan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/target.py",
                "shared-checksum",
                includeDirectoryAndStem: true);
            var laterId = UpsertTestFileWithLanguage("src/target.fs", "fsharp", "shared-checksum");

            Assert.Equal([plannedId], plan.FileIds);
            Assert.DoesNotContain(laterId, plan.FileIds);
            Assert.Equal(1, _writer.ApplyScopedFileCleanupPlan(plan));
            Assert.False(_writer.HasFileAtPath("src/target.cs"));
            Assert.True(_writer.HasFileAtPath("src/target.fs"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ScopedFileCleanupPlan_FileIdentityTreatsOnlyRetainedCaseAliasAsStale()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("scoped-cleanup-case-alias");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "Target.cs"), "// retained");
            File.WriteAllText(Path.Combine(projectRoot, "src", "live.cs"), "// live duplicate");

            var aliasId = UpsertTestFile("src/target.cs", "shared-checksum");
            var liveId = UpsertTestFile("src/live.cs", "shared-checksum");

            var caseInsensitivePlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/Target.cs",
                "shared-checksum",
                includeDirectoryAndStem: false,
                retainedPathComparison: StringComparison.OrdinalIgnoreCase);
            var ordinalPlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/Target.cs",
                "shared-checksum",
                includeDirectoryAndStem: false,
                retainedPathComparison: StringComparison.Ordinal);

            Assert.Equal([aliasId], caseInsensitivePlan.FileIds);
            Assert.Equal([aliasId], ordinalPlan.FileIds);
            Assert.DoesNotContain(liveId, caseInsensitivePlan.FileIds);
            Assert.DoesNotContain(liveId, ordinalPlan.FileIds);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ScopedFileCleanupPlan_AsciiCaseAliasDoesNotRequireMatchingChecksum()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("scoped-cleanup-case-alias-changed");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "Target.cs"), "// retained and changed");
            File.WriteAllText(Path.Combine(projectRoot, "src", "live.cs"), "// unrelated live row");

            var aliasId = UpsertTestFile("src/target.cs", "old-checksum");
            var liveId = UpsertTestFile("src/live.cs", "new-checksum");

            var caseInsensitivePlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/Target.cs",
                checksum: null,
                includeDirectoryAndStem: false,
                retainedPathComparison: StringComparison.OrdinalIgnoreCase);
            var ordinalPlan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                "src/Target.cs",
                checksum: null,
                includeDirectoryAndStem: false,
                retainedPathComparison: StringComparison.Ordinal);

            Assert.Equal([aliasId], caseInsensitivePlan.FileIds);
            Assert.Equal([aliasId], ordinalPlan.FileIds);
            Assert.DoesNotContain(liveId, caseInsensitivePlan.FileIds);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("src/target.cs", "src/Target.cs")]
    [InlineData("src/A.cs", "Src/A.cs")]
    [InlineData("src/é.cs", "src/É.cs")]
    public void ScopedFileCleanupPlan_CaseFoldedDistinctLiveFilesAreNotAliases(
        string persistedPath,
        string retainedPath)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("scoped-cleanup-distinct-identities");
        try
        {
            var persistedAbsolutePath = Path.Combine(
                projectRoot,
                persistedPath.Replace('/', Path.DirectorySeparatorChar));
            var retainedAbsolutePath = Path.Combine(
                projectRoot,
                retainedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(persistedAbsolutePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(retainedAbsolutePath)!);
            File.WriteAllText(persistedAbsolutePath, "// persisted distinct file\n");
            File.WriteAllText(retainedAbsolutePath, "// retained distinct file\n");

            if (!FileIndexer.TryGetFileIdentity(persistedAbsolutePath, out var persistedIdentity)
                || !FileIndexer.TryGetFileIdentity(retainedAbsolutePath, out var retainedIdentity)
                || persistedIdentity == retainedIdentity)
            {
                return;
            }

            var persistedId = UpsertTestFile(persistedPath, "shared-case-fold-checksum");
            var plan = _writer.PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                retainedPath,
                "shared-case-fold-checksum",
                includeDirectoryAndStem: false,
                retainedPathComparison: StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(persistedId, plan.FileIds);
            Assert.True(_writer.HasFileAtPath(persistedPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ScopedFileCleanupReappearance_FoldBucketsDoNotCrossMatchTargetIdentities()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("scoped-cleanup-fold-identity-buckets");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            var candidateAbsolutePath = Path.Combine(projectRoot, "src", "target.cs");
            var otherAbsolutePath = Path.Combine(projectRoot, "src", "other.cs");
            File.WriteAllText(candidateAbsolutePath, "// candidate identity\n");
            File.WriteAllText(otherAbsolutePath, "// other target identity\n");
            Assert.True(FileIndexer.TryGetFileIdentity(candidateAbsolutePath, out var candidateIdentity));
            Assert.True(FileIndexer.TryGetFileIdentity(otherAbsolutePath, out var otherIdentity));
            Assert.NotEqual(candidateIdentity, otherIdentity);

            var candidateId = UpsertTestFile("src/target.cs", "cross-target-checksum");
            var retainedPathsExact = new HashSet<string>(StringComparer.Ordinal)
            {
                "src/source.cs",
                "src/Target.cs",
            };
            var retainedFileIdentitiesByCaseFold = new Dictionary<
                string,
                HashSet<FileIndexer.FileIdentity>>(StringComparer.OrdinalIgnoreCase)
            {
                ["src/source.cs"] = [candidateIdentity],
                ["src/Target.cs"] = [otherIdentity],
            };

            var crossTargetMatch = _writer.FindReappearedFileInScopedCleanupPlan(
                projectRoot,
                [candidateId],
                retainedPathsExact,
                retainedFileIdentitiesByCaseFold);

            Assert.Equal("src/target.cs", crossTargetMatch);

            retainedFileIdentitiesByCaseFold["src/Target.cs"].Add(candidateIdentity);
            var sameBucketMatch = _writer.FindReappearedFileInScopedCleanupPlan(
                projectRoot,
                [candidateId],
                retainedPathsExact,
                retainedFileIdentitiesByCaseFold);

            Assert.Null(sameBucketMatch);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void StaleFilePlan_ExcludingCsharpRetainsLegacyNullLanguageRows()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("stale-plan-non-csharp");
        try
        {
            var legacyId = _writer.UpsertFile(
                new FileRecord
                {
                    Path = "legacy/unknown.ext",
                    Lang = null,
                    Size = 10,
                    Lines = 1,
                    Checksum = "legacy-null-language",
                    Modified = DateTime.UtcNow,
                },
                out _);
            var csharpId = UpsertTestFile("src/stale.cs", "stale-csharp");

            var plan = _writer.PlanStaleFilesExcludingLanguage(
                projectRoot,
                preservedMissingPaths: null,
                excludedLanguage: "csharp");

            Assert.Contains(legacyId, plan.FileIds);
            Assert.DoesNotContain(csharpId, plan.FileIds);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void InsertSymbols_UnknownKind_ThrowsBeforePersisting()
    {
        var ex = Assert.Throws<ArgumentException>(() => _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = 1,
                Kind = "metohd",
                Name = "Run",
                Line = 1,
            },
        ]));

        Assert.Contains("Unknown symbol kind", ex.Message);
    }

    [Fact]
    public void InsertChunks_CancelledBeforeBatch_ThrowsOperationCanceled_Issue3738()
    {
        var fileId = UpsertTestFile("src/cancel-chunk.cs", checksum: "cancel-chunk");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "class CancelChunk { }",
            },
        ], cts.Token));
    }

    [Fact]
    public void RebuildFtsFromChunks_AfterBulkLoadSuspension_PopulatesBothIndexesAndRestoresTriggers_Issue4725()
    {
        var bulkFileId = UpsertTestFile("src/bulk-fts.cs", checksum: "bulk-fts");

        _writer.SuspendFtsSyncTriggersForBulkLoad();
        Assert.Equal(0L, CountFtsSyncTriggers());
        Assert.Equal(0L, CountTrigramFtsSyncTriggers());

        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = bulkFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "bulkuniquetoken",
            },
        ]);

        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'bulkuniquetoken'"));
        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'bulkuniquetoken'"));

        _writer.RestoreFtsSyncTriggers();
        _writer.RebuildFtsFromChunks();

        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Equal(3L, CountTrigramFtsSyncTriggers());
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'bulkuniquetoken'"));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'bulkuniquetoken'"));

        var incrementalFileId = UpsertTestFile("src/incremental-fts.cs", checksum: "incremental-fts");
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = incrementalFileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "incrementaluniquetoken",
            },
        ]);

        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'incrementaluniquetoken'"));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'incrementaluniquetoken'"));

        ExecuteNonQuery(
            _db.Connection,
            $"UPDATE chunks SET content = 'updateduniquetoken' WHERE file_id = {incrementalFileId}");
        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'incrementaluniquetoken'"));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'updateduniquetoken'"));

        ExecuteNonQuery(_db.Connection, $"DELETE FROM chunks WHERE file_id = {incrementalFileId}");
        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'updateduniquetoken'"));
    }

    [Fact]
    public void RebuildFtsFromChunks_CanLeaveIncrementalCounterForImmediateOptimize()
    {
        _writer.SetMeta(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey, "7");
        _writer.SetMeta(DbWriter.FtsIncrementalWritesSinceMergeMetaKey, "3");

        _writer.RebuildFtsFromChunks(resetIncrementalWriteCounter: false);

        Assert.Equal(7, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(3, _writer.GetFtsIncrementalWritesSinceMerge());

        _writer.RebuildFtsFromChunks();

        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceMerge());
    }

    [Fact]
    public void SuspendFtsSyncTriggersForBulkLoad_RollsBackWithTransaction()
    {
        Assert.Equal(3L, CountFtsSyncTriggers());

        using (var txn = _writer.BeginTransaction())
        {
            _writer.SuspendFtsSyncTriggersForBulkLoad();

            Assert.Equal(0L, CountFtsSyncTriggers());
        }

        Assert.Equal(3L, CountFtsSyncTriggers());
    }

    [Fact]
    public void FtsBulkLoadOwnerMarker_RemainsLegacyReadableAndCleanupTriggersInvalidateGeneration()
    {
        var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var expectedMarker = "pid:" + pid;

        _writer.SuspendFtsSyncTriggersForBulkLoad();
        try
        {
            var marker = Assert.IsType<string>(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.Equal(expectedMarker, marker);
            Assert.True(int.TryParse(
                marker.AsSpan("pid:".Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var legacyParsedPid));
            Assert.Equal(Environment.ProcessId, legacyParsedPid);
            Assert.StartsWith(
                expectedMarker + ":",
                ReadMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey),
                StringComparison.Ordinal);
            Assert.Equal(3L, CountFtsBulkLoadGenerationCleanupTriggers());

            // Model an older writer updating, deleting, and reinserting only the primary key.
            _writer.SetMeta(DbWriter.FtsBulkLoadInProgressMetaKey, "true");
            Assert.Null(ReadMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey));

            _writer.SetMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey, expectedMarker + ":start:1");
            using (var deletePrimary = _db.Connection.CreateCommand())
            {
                deletePrimary.CommandText = "DELETE FROM codeindex_meta WHERE key = @key";
                deletePrimary.Parameters.AddWithValue("@key", DbWriter.FtsBulkLoadInProgressMetaKey);
                Assert.Equal(1, deletePrimary.ExecuteNonQuery());
            }
            Assert.Null(ReadMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey));

            _writer.SetMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey, expectedMarker + ":start:1");
            _writer.SetMeta(DbWriter.FtsBulkLoadInProgressMetaKey, expectedMarker);
            Assert.Null(ReadMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey));
        }
        finally
        {
            _writer.RestoreFtsSyncTriggers();
            _writer.ClearFtsBulkLoadInProgress();
        }
    }

    [Fact]
    public void FtsBulkLoadTriggerGuard_StartPartialDropFailureDowngradesMarkerAndRecoversSameProcess()
    {
        var fileId = UpsertTestFile("src/failed-drop-bulk-fts.cs", checksum: "failed-drop-bulk-fts");
        const string token = "faileddropcleanupbulktoken";
        var previousHook = DbWriter.FtsMaintenanceBeforeExecuteForTesting;
        var injectedException = new InvalidOperationException("simulated partial trigger-drop failure");
        var injectedCount = 0;
        FtsBulkLoadTriggerGuard? guard = null;

        try
        {
            DbWriter.FtsMaintenanceBeforeExecuteForTesting = phase =>
            {
                previousHook?.Invoke(phase);
                if (phase != DbWriter.FtsDropTriggersMaintenancePhase
                    || Interlocked.Exchange(ref injectedCount, 1) != 0)
                {
                    return;
                }

                using var dropTrigger = _db.Connection.CreateCommand();
                dropTrigger.CommandText = "DROP TRIGGER IF EXISTS fts_chunks_ai";
                dropTrigger.ExecuteNonQuery();
                throw injectedException;
            };

            var thrown = Assert.Throws<InvalidOperationException>(() =>
                guard = FtsBulkLoadTriggerGuard.Start(_writer, enabled: true));

            Assert.Same(injectedException, thrown);
            Assert.Null(guard);
            Assert.Equal(1, injectedCount);
            Assert.Equal("true", ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.Equal(2L, CountFtsSyncTriggers());

            _writer.InsertChunks(
            [
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = token,
                },
            ]);
            Assert.Equal(0L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));

            DbWriter.FtsMaintenanceBeforeExecuteForTesting = previousHook;
            Assert.True(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

            Assert.Equal(3L, CountFtsSyncTriggers());
            Assert.Equal(1L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));
            Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());
        }
        finally
        {
            DbWriter.FtsMaintenanceBeforeExecuteForTesting = previousHook;
            guard?.Dispose();
        }
    }

    [Fact]
    public void FtsBulkLoadTriggerGuard_DisposeAfterMutation_RebuildsFtsAndRestoresTriggers()
    {
        var fileId = UpsertTestFile("src/abandoned-bulk-fts.cs", checksum: "abandoned-bulk-fts");
        var ftsMutated = false;

        using (var guard = FtsBulkLoadTriggerGuard.Start(_writer, enabled: true, () => ftsMutated))
        {
            Assert.NotNull(guard);
            Assert.Equal(0L, CountFtsSyncTriggers());

            _writer.InsertChunks(
            [
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = "abandonedbulktoken",
                },
            ]);
            ftsMutated = true;
        }

        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'abandonedbulktoken'"));
    }

    [Theory]
    [InlineData(DbWriter.FtsRestoreTriggersMaintenancePhase, 0L)]
    [InlineData(DbWriter.FtsRebuildMaintenancePhase, 3L)]
    public void FtsBulkLoadTriggerGuard_DisposeCleanupFailureDowngradesMarkerAndRecoversSameProcess(
        string failurePhase,
        long expectedTriggersAfterFailure)
    {
        var fileId = UpsertTestFile(
            $"src/failed-{failurePhase}-bulk-fts.cs",
            checksum: $"failed-{failurePhase}-bulk-fts");
        var token = failurePhase == DbWriter.FtsRestoreTriggersMaintenancePhase
            ? "failedrestorecleanupbulktoken"
            : "failedrebuildcleanupbulktoken";
        var previousHook = DbWriter.FtsMaintenanceBeforeExecuteForTesting;
        var injectedException = new InvalidOperationException($"simulated {failurePhase} cleanup failure");
        var injectedCount = 0;
        var ftsMutated = false;
        var guard = FtsBulkLoadTriggerGuard.Start(_writer, enabled: true, () => ftsMutated);

        try
        {
            Assert.NotNull(guard);
            Assert.StartsWith("pid:", ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey), StringComparison.Ordinal);
            Assert.Equal(0L, CountFtsSyncTriggers());
            _writer.InsertChunks(
            [
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = token,
                },
            ]);
            ftsMutated = true;
            Assert.Equal(0L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));

            DbWriter.FtsMaintenanceBeforeExecuteForTesting = phase =>
            {
                previousHook?.Invoke(phase);
                if (phase == failurePhase && Interlocked.Exchange(ref injectedCount, 1) == 0)
                    throw injectedException;
            };

            var thrown = Assert.Throws<InvalidOperationException>(() => guard!.Dispose());

            Assert.Same(injectedException, thrown);
            Assert.Equal(1, injectedCount);
            Assert.Equal("true", ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.Equal(expectedTriggersAfterFailure, CountFtsSyncTriggers());
            Assert.Equal(0L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));

            guard.Dispose();
            Assert.Equal("true", ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.Equal(expectedTriggersAfterFailure, CountFtsSyncTriggers());
            Assert.Equal(0L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));

            DbWriter.FtsMaintenanceBeforeExecuteForTesting = previousHook;
            Assert.True(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

            Assert.Equal(3L, CountFtsSyncTriggers());
            Assert.Equal(1L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));
            Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
            Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());
        }
        finally
        {
            DbWriter.FtsMaintenanceBeforeExecuteForTesting = previousHook;
            guard?.Dispose();
        }
    }

    [Fact]
    public void FtsBulkLoadTriggerGuard_CompleteFailureKeepsDisposeRecovery()
    {
        var fileId = UpsertTestFile("src/failed-complete-bulk-fts.cs", checksum: "failed-complete-bulk-fts");
        var ftsMutated = false;

        using (var guard = FtsBulkLoadTriggerGuard.Start(_writer, enabled: true, () => ftsMutated))
        {
            Assert.NotNull(guard);
            _writer.InsertChunks(
            [
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = "failedcompletebulktoken",
                },
            ]);
            ftsMutated = true;

            Assert.Throws<InvalidOperationException>(() => guard!.Complete(
                rebuild: true,
                beforeOptimize: () => throw new InvalidOperationException("simulated optimize precheck failure")));
        }

        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'failedcompletebulktoken'"));
    }

    [Fact]
    public void FtsBulkLoadTriggerGuard_CompleteRebuildsAfterPostCommitCheckpointFailure()
    {
        var fileId = UpsertTestFile(
            "src/post-commit-checkpoint-bulk-fts.cs",
            checksum: "post-commit-checkpoint-bulk-fts");
        const string token = "postcommitcheckpointbulktoken";
        var previousHook = DbWriter.BeforePassiveWalCheckpointForTesting;
        var injectedException = new InvalidOperationException("simulated post-commit WAL checkpoint failure");
        var ftsMutated = false;
        FtsBulkLoadTriggerGuard? guard = null;

        try
        {
            guard = FtsBulkLoadTriggerGuard.Start(_writer, enabled: true, () => ftsMutated);
            Assert.NotNull(guard);
            using (var txn = _writer.BeginTransaction())
            {
                _writer.InsertChunks(
                [
                    new ChunkRecord
                    {
                        FileId = fileId,
                        ChunkIndex = 0,
                        StartLine = 1,
                        EndLine = 1,
                        Content = token,
                    },
                ]);
                DbWriter.BeforePassiveWalCheckpointForTesting = () => throw injectedException;

                var thrown = Assert.Throws<InvalidOperationException>(() => txn.Commit());

                Assert.Same(injectedException, thrown);
            }

            Assert.False(ftsMutated);
            Assert.Equal(0L, CountFtsSyncTriggers());
            Assert.Equal(0L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));
            Assert.StartsWith(
                "pid:",
                ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey),
                StringComparison.Ordinal);

            DbWriter.BeforePassiveWalCheckpointForTesting = previousHook;
            guard.Complete(rebuild: false);

            Assert.Equal(3L, CountFtsSyncTriggers());
            Assert.Equal(1L, ExecuteScalarLong($"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{token}'"));
            Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        }
        finally
        {
            DbWriter.BeforePassiveWalCheckpointForTesting = previousHook;
            guard?.Dispose();
        }
    }

    [Fact]
    public void FtsBulkLoadTriggerGuard_CancelledCompleteDefersRecoveryWithoutActiveOwner_Issue4591()
    {
        var fileId = UpsertTestFile("src/cancelled-complete-bulk-fts.cs", checksum: "cancelled-complete-bulk-fts");
        using var cts = new CancellationTokenSource();

        using (var guard = FtsBulkLoadTriggerGuard.Start(_writer, enabled: true))
        {
            Assert.NotNull(guard);
            _writer.InsertChunks(
            [
                new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = 0,
                    StartLine = 1,
                    EndLine = 1,
                    Content = "cancelledcompletebulktoken",
                },
            ]);

            Assert.Throws<OperationCanceledException>(() => guard!.Complete(
                rebuild: true,
                beforeOptimize: cts.Cancel,
                cancellationToken: cts.Token));
        }

        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Equal("true", ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.True(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'cancelledcompletebulktoken'"));
    }

    [Fact]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_RebuildsCommittedRowsAndClearsMarker()
    {
        var fileId = UpsertTestFile("src/recovered-bulk-fts.cs", checksum: "recovered-bulk-fts");

        _writer.SuspendFtsSyncTriggersForBulkLoad();
        Assert.NotNull(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Equal(0L, CountFtsSyncTriggers());

        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "recoveredbulktoken",
            },
        ]);

        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'recoveredbulktoken'"));

        _writer.SetMeta(DbWriter.FtsBulkLoadInProgressMetaKey, "true");
        Assert.True(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'recoveredbulktoken'"));
        Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());
    }

    [Fact]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_SkipsActiveOwner()
    {
        var fileId = UpsertTestFile("src/active-bulk-fts.cs", checksum: "active-bulk-fts");

        _writer.SuspendFtsSyncTriggersForBulkLoad();
        var marker = ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey);
        Assert.NotNull(marker);
        Assert.Equal(0L, CountFtsSyncTriggers());

        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "activebulktoken",
            },
        ]);

        Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

        Assert.Equal(marker, ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Equal(0L, CountFtsSyncTriggers());
        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'activebulktoken'"));

        _writer.RestoreFtsSyncTriggers();
        _writer.RebuildFtsFromChunks();
        _writer.ClearFtsBulkLoadInProgress();
    }

    [Theory]
    [InlineData("start:1")]
    [InlineData("token:00000000000000000000000000000001")]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_RebuildsWhenPidGenerationDoesNotMatch(
        string generation)
    {
        var fileId = UpsertTestFile(
            "src/reused-pid-bulk-fts.cs",
            checksum: "reused-pid-bulk-fts");

        _writer.SuspendFtsSyncTriggersForBulkLoad();
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "reusedpidbulktoken",
            },
        ]);
        var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _writer.SetMeta(
            DbWriter.FtsBulkLoadOwnerGenerationMetaKey,
            $"pid:{pid}:{generation}");

        Assert.True(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey));
        Assert.Equal(1L, ExecuteScalarLong(
            "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'reusedpidbulktoken'"));
    }

    [Fact]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_SkipsMismatchedGenerationAssociationConservatively()
    {
        _writer.SuspendFtsSyncTriggersForBulkLoad();
        _writer.SetMeta(
            DbWriter.FtsBulkLoadOwnerGenerationMetaKey,
            "pid:2147483647:start:1");

        Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

        Assert.Equal(0L, CountFtsSyncTriggers());
        Assert.Equal(
            $"pid:{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        _writer.RestoreFtsSyncTriggers();
        _writer.ClearFtsBulkLoadInProgress();
    }

    [Fact]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_SkipsGenerationWhenCleanupTriggerSetIsIncomplete()
    {
        _writer.SuspendFtsSyncTriggersForBulkLoad();
        ExecuteNonQuery(
            _db.Connection,
            $"DROP TRIGGER {DbWriter.FtsBulkLoadGenerationClearDeleteTriggerName}");
        _writer.SetMeta(
            DbWriter.FtsBulkLoadOwnerGenerationMetaKey,
            $"pid:{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:start:1");

        Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

        Assert.Equal(0L, CountFtsSyncTriggers());
        Assert.Equal(2L, CountFtsBulkLoadGenerationCleanupTriggers());
        _writer.RestoreFtsSyncTriggers();
        _writer.ClearFtsBulkLoadInProgress();
    }

    [Fact]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_WithoutMetaTableIsNoOpAndObservesPreCancellation()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var writer = new DbWriter(connection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancellationException = Assert.Throws<OperationCanceledException>(() =>
            writer.RecoverInterruptedFtsBulkLoadIfNeeded(cancellation.Token));

        Assert.Equal(cancellation.Token, cancellationException.CancellationToken);
        Assert.False(writer.RecoverInterruptedFtsBulkLoadIfNeeded());
    }

    [Fact]
    public void RecoverInterruptedFtsBulkLoadIfNeeded_SkipsLegacyPidOnlyActiveOwnerConservatively()
    {
        _writer.SuspendFtsSyncTriggersForBulkLoad();
        _writer.SetMeta(
            DbWriter.FtsBulkLoadInProgressMetaKey,
            $"pid:{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        Assert.False(_writer.RecoverInterruptedFtsBulkLoadIfNeeded());

        Assert.Equal(0L, CountFtsSyncTriggers());
        Assert.Equal(
            $"pid:{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadOwnerGenerationMetaKey));
        _writer.RestoreFtsSyncTriggers();
        _writer.ClearFtsBulkLoadInProgress();
    }

    [Fact]
    public void FtsBulkLoadOwnerGeneration_StartMarkerDoesNotMatchTokenFallbackIncarnation()
    {
        Assert.False(DbWriter.IsFtsBulkLoadOwnerGenerationMatch(
            expectedStartTimeUtcTicks: 1,
            expectedIncarnationToken: null,
            currentProcessStartTimeUtcTicks: null,
            currentProcessIncarnationToken: Guid.NewGuid()));
    }

    [Fact]
    public void DbReader_RecoversInterruptedFtsBulkLoadBeforeServingSearch()
    {
        var fileId = UpsertTestFile("src/reader-recovered-bulk-fts.cs", checksum: "reader-recovered-bulk-fts");

        _writer.SuspendFtsSyncTriggersForBulkLoad();
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "readerrecoveredbulktoken",
            },
        ]);

        Assert.Equal(0L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'readerrecoveredbulktoken'"));

        _writer.SetMeta(DbWriter.FtsBulkLoadInProgressMetaKey, "pid:2147483647");
        using var reader = new DbReader(_db.Connection);
        var results = reader.Search("readerrecoveredbulktoken");

        Assert.Contains(results, result => result.Path == "src/reader-recovered-bulk-fts.cs");
        Assert.Equal(3L, CountFtsSyncTriggers());
        Assert.Null(ReadMeta(DbWriter.FtsBulkLoadInProgressMetaKey));
        Assert.Equal(1L, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'readerrecoveredbulktoken'"));
    }

    [Fact]
    public void InsertReferences_ReportsProgressCheckpoints_Issue3738()
    {
        var fileId = UpsertTestFile("src/progress-reference.cs", checksum: "progress-reference");
        var checkpoints = new List<DbWriter.DbWriterBatchProgress>();
        DbWriter.BatchProgressCheckpointForTesting = checkpoints.Add;
        try
        {
            _writer.InsertReferences(
            [
                new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "Target",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = "Target();",
                },
            ], CancellationToken.None);
        }
        finally
        {
            DbWriter.BatchProgressCheckpointForTesting = null;
        }

        Assert.Contains(checkpoints, checkpoint =>
            checkpoint.Operation == "insert_references"
            && checkpoint.RowsProcessed == 0
            && checkpoint.RowsTotal == 1);
        Assert.Contains(checkpoints, checkpoint =>
            checkpoint.Operation == "insert_references"
            && checkpoint.RowsProcessed == 1
            && checkpoint.RowsTotal == 1);
        Assert.Contains(checkpoints, checkpoint => checkpoint.Operation == "upsert_reference_lines");
    }

    [Theory]
    [InlineData("annotation")]
    [InlineData("bcl_regex_without_timeout")]
    [InlineData("column_reference")]
    [InlineData("const_generic_reference")]
    [InlineData("cte_body_reference")]
    [InlineData("decorator")]
    [InlineData("generic_type_argument")]
    [InlineData("join_condition_reference")]
    [InlineData("lifetime_reference")]
    [InlineData("subscribe")]
    [InlineData("type_tag")]
    [InlineData("implicit_implementation")]
    public void InsertReferences_ExistingReferenceKinds_AreAccepted(string referenceKind)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = $"src/{referenceKind}.cs",
            Lang = "csharp",
            Size = 32,
            Lines = 1,
            Modified = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
            Checksum = referenceKind,
        });

        _writer.InsertReferences(
        [
            new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = "Target",
                ReferenceKind = referenceKind,
                Line = 1,
                Column = 1,
                Context = "Target();",
                ContainerKind = "function",
                ContainerName = "Caller",
            },
        ]);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = @kind";
        cmd.Parameters.AddWithValue("@kind", referenceKind);
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("accessor")]
    [InlineData("annotation")]
    [InlineData("async_function")]
    [InlineData("async_generator")]
    [InlineData("block data")]
    [InlineData("class_hook")]
    [InlineData("delegate")]
    [InlineData("generator")]
    [InlineData("object")]
    [InlineData("procedure")]
    [InlineData("program")]
    [InlineData("rule")]
    [InlineData("union")]
    [InlineData("specialization")]
    [InlineData("protocol")]
    [InlineData("file_module")]
    [InlineData("submodule")]
    [InlineData("subroutine")]
    [InlineData("trait")]
    [InlineData("associatedtype")]
    [InlineData("type_parameter")]
    [InlineData("typealias")]
    public void InsertSymbols_ExistingExtractorKinds_AreAccepted(string symbolKind)
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = $"src/{symbolKind}.txt",
            Lang = "csharp",
            Size = 32,
            Lines = 1,
            Modified = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
            Checksum = symbolKind,
        });

        _writer.InsertSymbols(
        [
            new SymbolRecord
            {
                FileId = fileId,
                Kind = symbolKind,
                Name = "Handler",
                Line = 1,
            },
        ]);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE kind = @kind";
        cmd.Parameters.AddWithValue("@kind", symbolKind);
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("type_parameter", "type")]
    [InlineData("typealias", "type")]
    public void SymbolKindCatalog_SemanticTypeKindsExposeCompatibilityFamily(string symbolKind, string compatibilityKind)
    {
        Assert.Equal(compatibilityKind, SymbolKindCatalog.CompatibilityKindFamilies[symbolKind]);
    }

    [Fact]
    public void InitializeSchema_ConstrainsKindColumns()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols (file_id, kind, name, line)
            VALUES (1, 'metohd', 'Run', 1)
            """;

        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);
    }

    [Fact]
    public void InitializeSchema_RefreshesLegacyKindCheckConstraints()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_kind_check");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            SeedLegacyKindCheckSchema(dbPath);

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            var fileId = writer.UpsertFile(new FileRecord
            {
                Path = "lib/inspect_impl.ex",
                Lang = "elixir",
                Size = 64,
                Lines = 4,
                Modified = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                Checksum = Guid.NewGuid().ToString("N"),
            });
            writer.InsertSymbols(
            [
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "protocol_impl",
                    Name = "String.Chars, for: User",
                    Line = 1,
                    ContainerKind = "protocol_impl",
                    ContainerName = "String.Chars, for: User",
                },
            ]);
            writer.InsertReferences(
            [
                new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "User",
                    ReferenceKind = "type_reference",
                    Line = 1,
                    Column = 23,
                    ContainerKind = "protocol_impl",
                    ContainerName = "String.Chars, for: User",
                },
            ]);

            Assert.Equal(1, ExecuteScalarLong(db.Connection, "SELECT COUNT(*) FROM symbols WHERE kind = 'protocol_impl'"));
            Assert.Equal(1, ExecuteScalarLong(db.Connection, "SELECT COUNT(*) FROM symbol_references WHERE container_kind = 'protocol_impl'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void InitializeSchema_ForeignKeyCheckDetectsRebuildViolations_Issue3717()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_fk_check");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            SeedLegacyKindCheckSchema(dbPath);
            DbContext.ForeignKeyValidationBeforeCheckForTesting = (connection, phase) =>
            {
                if (!string.Equals(phase, "kind_check_constraints", StringComparison.Ordinal))
                    return;

                ExecuteNonQuery(connection, """
                    INSERT INTO symbol_references (file_id, symbol_name, reference_kind, line, column_number, context)
                    VALUES (9999, 'Dangling', 'call', 1, 1, 'Dangling()')
                    """);
            };

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            var ex = Assert.Throws<CodeIndexException>(db.InitializeSchema);

            Assert.Equal(CommandErrorCodes.DbIntegrityFailed, ex.Code);
            Assert.Contains("kind_check_constraints", ex.Message, StringComparison.Ordinal);
            Assert.Contains("symbol_references", ex.Message, StringComparison.Ordinal);
            Assert.Contains("files", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(dbPath, ex.Message, StringComparison.Ordinal);
            Assert.Contains("integrity-check", ex.Hint, StringComparison.Ordinal);
        }
        finally
        {
            DbContext.ForeignKeyValidationBeforeCheckForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void OptimizeFts_ResetsIncrementalWriteCounterAndStampsTime()
    {
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceOptimize());

        Assert.Equal(1, _writer.RecordFtsIncrementalWrite());
        Assert.Equal(2, _writer.RecordFtsIncrementalWrite());
        Assert.Equal(2, _writer.GetFtsIncrementalWritesSinceOptimize());

        _writer.OptimizeFts();

        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.False(string.IsNullOrWhiteSpace(_db.GetMetaString(DbWriter.FtsLastOptimizedAtMetaKey)));
    }

    [Fact]
    public void TryCheckpointWalTruncate_OnWritableDb_ReportsStructuredSuccess()
    {
        var result = _db.TryCheckpointWalTruncate();
        var checkpoint = _db.LastWalCheckpointResult;

        Assert.True(result);
        Assert.True(_db.WalCheckpointAttempted);
        Assert.True(_db.WalCheckpointSucceeded);
        Assert.True(checkpoint.Attempted);
        Assert.True(checkpoint.Succeeded);
        Assert.Equal(0L, checkpoint.Busy);
        Assert.NotNull(checkpoint.LogPageCount);
        Assert.NotNull(checkpoint.CheckpointedPageCount);
        Assert.Equal(0L, checkpoint.RemainingPageCount);
        Assert.Null(checkpoint.SkippedReason);
        Assert.Null(checkpoint.FailureReason);
    }

    [Fact]
    public void CheckpointWalTruncate_WithBlockingReader_ReturnsBusyCounts_Issue4558()
    {
        using var blockingReader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ConnectionString);
        blockingReader.Open();
        using var readTransaction = blockingReader.BeginTransaction();
        using (var read = blockingReader.CreateCommand())
        {
            read.Transaction = readTransaction;
            read.CommandText = "SELECT COUNT(*) FROM codeindex_meta";
            _ = read.ExecuteScalar();
        }

        using (var write = _db.Connection.CreateCommand())
        {
            write.CommandText = "INSERT OR REPLACE INTO codeindex_meta(key, value) VALUES ('issue4558_busy_reader', '1')";
            write.ExecuteNonQuery();
        }
        using (var timeout = _db.Connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout=50";
            timeout.ExecuteNonQuery();
        }

        var result = _db.CheckpointWalTruncate();

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal(1L, result.Busy);
        Assert.NotNull(result.LogPageCount);
        Assert.NotNull(result.CheckpointedPageCount);
        Assert.NotNull(result.RemainingPageCount);
        Assert.True(result.LogPageCount > result.CheckpointedPageCount);
        Assert.Equal(result.LogPageCount - result.CheckpointedPageCount, result.RemainingPageCount);
        Assert.Equal(WalCheckpointResult.BusyFailureReason, result.FailureReason);
        Assert.Equal(result, _db.LastWalCheckpointResult);

        var status = new DbReader(_db).GetStatus();
        Assert.Equal(result.Busy, status.WalCheckpointBusy);
        Assert.Equal(result.LogPageCount, status.WalCheckpointLogPageCount);
        Assert.Equal(result.CheckpointedPageCount, status.WalCheckpointCheckpointedPageCount);
        Assert.Equal(result.RemainingPageCount, status.WalCheckpointRemainingPageCount);
        Assert.Equal(result.FailureReason, status.WalCheckpointFailureReason);
        Assert.Equal(result.Busy, status.SqliteConnectionPolicy.WalCheckpointBusy);
        Assert.Equal(result.LogPageCount, status.SqliteConnectionPolicy.WalCheckpointLogPageCount);
        Assert.Equal(result.CheckpointedPageCount, status.SqliteConnectionPolicy.WalCheckpointCheckpointedPageCount);
        Assert.Equal(result.RemainingPageCount, status.SqliteConnectionPolicy.WalCheckpointRemainingPageCount);
    }

    [Fact]
    public void CheckpointWalTruncate_WhenSqliteReportsReadOnly_PreservesTypedReason_Issue4558()
    {
        var previousHook = DbContext.WalCheckpointTruncateExecutedForTesting;
        DbContext.WalCheckpointTruncateExecutedForTesting = _ => throw new SqliteException("sensitive path omitted", 8);
        try
        {
            var instanceResult = _db.CheckpointWalTruncate();
            var staticResult = DbContext.CheckpointWalBeforeReadOnlyFallback(_dbPath, CancellationToken.None);

            foreach (var result in new[] { instanceResult, staticResult })
            {
                Assert.True(result.Attempted);
                Assert.False(result.Succeeded);
                Assert.Equal("sqlite_read_only", result.FailureReason);
                Assert.Null(result.Busy);
                Assert.Null(result.LogPageCount);
                Assert.Null(result.CheckpointedPageCount);
                Assert.Null(result.RemainingPageCount);
                Assert.DoesNotContain("sensitive", result.FailureReason, StringComparison.Ordinal);
            }
        }
        finally
        {
            DbContext.WalCheckpointTruncateExecutedForTesting = previousHook;
        }
    }

    [Fact]
    public void CheckpointWalTruncate_OnReadOnlyContext_PersistsSkippedResult_Issue4558()
    {
        Assert.True(_db.TryCheckpointWalTruncate());
        using var readOnlyDb = new DbContext(DbOpenIntent.QueryOnly, DbContext.ToReadOnlyUri(_dbPath));

        var result = readOnlyDb.CheckpointWalTruncate();

        Assert.False(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal(WalCheckpointResult.ReadOnlySkippedReason, result.SkippedReason);
        Assert.Null(result.FailureReason);
        Assert.Equal(result, readOnlyDb.LastWalCheckpointResult);

        using var reader = new DbReader(readOnlyDb);
        Assert.Equal(result, reader.LastWalCheckpointResult);
        var status = reader.GetStatus();
        Assert.False(status.WalCheckpointAttempted);
        Assert.False(status.WalCheckpointSucceeded);
        Assert.Equal(result.SkippedReason, status.WalCheckpointSkippedReason);
        Assert.Equal(result.SkippedReason, status.SqliteConnectionPolicy.WalCheckpointSkippedReason);
    }

    [Fact]
    public void Dispose_AfterWriteWork_AttemptsWalCheckpoint()
    {
        var dbDir = TestProjectHelper.CreateTempProject("cdidx_checkpoint");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        var checkpointAttempted = false;
        DbContext.WalCheckpointTruncateExecutedForTesting = _ => checkpointAttempted = true;
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                db.MarkWriteWork();
            }

            Assert.True(checkpointAttempted);
        }
        finally
        {
            DbContext.WalCheckpointTruncateExecutedForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void Dispose_AfterSchemaInitializationOnly_DoesNotCheckpointWal()
    {
        var dbDir = TestProjectHelper.CreateTempProject("cdidx_schema_checkpoint");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        var checkpointAttempted = false;
        DbContext.WalCheckpointTruncateExecutedForTesting = _ => checkpointAttempted = true;
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }

            Assert.False(checkpointAttempted);
        }
        finally
        {
            DbContext.WalCheckpointTruncateExecutedForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    private long UpsertTestFile(string path, string checksum)
        => UpsertTestFileWithLanguage(path, "csharp", checksum);

    private long UpsertTestFileWithLanguage(string path, string language, string checksum)
        => _writer.UpsertFile(new FileRecord
        {
            Path = path,
            Lang = language,
            Size = 100,
            Lines = 4,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = checksum,
        });

    private string ReadReferenceResolutionState(long fileId)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = "SELECT resolution_state FROM symbol_references WHERE file_id = @file_id";
        command.Parameters.AddWithValue("@file_id", fileId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private string ReadReferenceIdentitySnapshot()
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(
                       id || ':' ||
                       COALESCE(source_symbol_id, -1) || ':' ||
                       COALESCE(target_symbol_id, -1) || ':' ||
                       COALESCE(target_symbol_key, '') || ':' ||
                       resolution_candidate_count || ':' ||
                       COALESCE(resolution_state, '') || ':' ||
                       is_self_reference || ':' ||
                       is_mutual_recursion,
                       '|')
            FROM (SELECT * FROM symbol_references ORDER BY id)
            """;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [Fact]
    public void OptimizeFtsIfIncrementalWriteThresholdReached_RunsOnlyAtThreshold()
    {
        Assert.Equal(1, _writer.RecordFtsIncrementalWrite());
        Assert.False(_writer.OptimizeFtsIfIncrementalWriteThresholdReached(threshold: 2));
        Assert.Equal(1, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(1, _writer.GetFtsIncrementalWritesSinceMerge());

        Assert.Equal(2, _writer.RecordFtsIncrementalWrite());
        Assert.True(_writer.OptimizeFtsIfIncrementalWriteThresholdReached(threshold: 2));
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceMerge());
    }

    [Fact]
    public void RecordFtsIncrementalWriteAndOptimizeIfThresholdReached_BatchesMaintenance()
    {
        var optimizeCount = 0;

        Assert.False(_writer.RecordFtsIncrementalWriteAndOptimizeIfThresholdReached(
            () => optimizeCount++,
            threshold: 2));
        Assert.Equal(1, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(1, _writer.GetFtsIncrementalWritesSinceMerge());
        Assert.Equal(0, optimizeCount);

        Assert.True(_writer.RecordFtsIncrementalWriteAndOptimizeIfThresholdReached(
            () => optimizeCount++,
            threshold: 2));
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceMerge());
        Assert.Equal(1, optimizeCount);
    }

    [Fact]
    public void RecordFtsIncrementalWriteAndMergeIfThresholdReached_UsesMinimumWorkTargetAndKeepsSearchableRows()
    {
        var fileId = UpsertTestFile("src/fts-merge.cs", "fts_merge");
        _writer.InsertChunks(
        [
            new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = 0,
                StartLine = 1,
                EndLine = 1,
                Content = "adaptive_merge_token",
            },
        ]);
        var mergeCount = 0;

        Assert.False(_writer.RecordFtsIncrementalWriteAndMergeIfThresholdReached(
            () => mergeCount++,
            threshold: 2,
            mergeWorkTargetPages: 1));
        Assert.True(_writer.RecordFtsIncrementalWriteAndMergeIfThresholdReached(
            () => mergeCount++,
            threshold: 2,
            mergeWorkTargetPages: 1));

        Assert.Equal(1, mergeCount);
        Assert.Equal(2, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(0, _writer.GetFtsIncrementalWritesSinceMerge());
        using var search = _db.Connection.CreateCommand();
        search.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'adaptive_merge_token'";
        Assert.Equal(1L, (long)search.ExecuteScalar()!);
    }

    [Fact]
    public void MergeFtsSegments_PreCancelledOrInvalidWorkTargetDoesNotResetPendingWrites()
    {
        Assert.Equal(1, _writer.RecordFtsIncrementalWrite());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _writer.MergeFtsSegments(cancellationToken: cts.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => _writer.MergeFtsSegments(workTargetPages: 0));
        Assert.Equal(1, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(1, _writer.GetFtsIncrementalWritesSinceMerge());
    }

    [Fact]
    public void FtsMergeCounter_MissingOnLegacyDatabaseInheritsOptimizeCadence()
    {
        _writer.SetMeta(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey, "7");

        Assert.Equal(7, _writer.GetFtsIncrementalWritesSinceMerge());
        Assert.Equal(8, _writer.RecordFtsIncrementalWrite());
        Assert.Equal(8, _writer.GetFtsIncrementalWritesSinceOptimize());
        Assert.Equal(8, _writer.GetFtsIncrementalWritesSinceMerge());
    }

    [Theory]
    [InlineData(0, 100, false)]
    [InlineData(60, 0, false)]
    [InlineData(5, 10, false)]
    [InlineData(6, 10, true)]
    [InlineData(6, 11, false)]
    [InlineData(7, 11, true)]
    [InlineData(100, 100, true)]
    public void FtsBulkLoadDirtyBytePlanner_UsesCeilingThreeFifthsBoundary(
        long dirtyBytes,
        long totalBytes,
        bool expected)
        => Assert.Equal(expected, FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(dirtyBytes, totalBytes));

    [Fact]
    public void FtsBulkLoadDirtyBytePlanner_DoesNotOverflowLongByteCounts()
    {
        var quotient = long.MaxValue / FtsBulkLoadTriggerGuard.DirtyByteThresholdDenominator;
        var remainder = long.MaxValue % FtsBulkLoadTriggerGuard.DirtyByteThresholdDenominator;
        var threshold = quotient * FtsBulkLoadTriggerGuard.DirtyByteThresholdNumerator
            + (remainder * FtsBulkLoadTriggerGuard.DirtyByteThresholdNumerator
                + FtsBulkLoadTriggerGuard.DirtyByteThresholdDenominator - 1)
                / FtsBulkLoadTriggerGuard.DirtyByteThresholdDenominator;

        Assert.False(FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(threshold - 1, long.MaxValue));
        Assert.True(FtsBulkLoadTriggerGuard.ShouldUseForDirtyBytes(threshold, long.MaxValue));
    }

    [Fact]
    public void FtsBulkLoadKnownByteAccumulator_DetectsAdditionAndReplacementOverflow()
    {
        Assert.True(FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
            totalBytes: 100,
            previousBytes: 10,
            currentBytes: 20,
            out var replacedTotal));
        Assert.Equal(110, replacedTotal);

        Assert.False(FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
            totalBytes: long.MaxValue - 5,
            previousBytes: null,
            currentBytes: 6,
            out var addedOverflowTotal));
        Assert.Equal(long.MaxValue, addedOverflowTotal);

        Assert.False(FtsBulkLoadTriggerGuard.TryUpdateKnownByteTotal(
            totalBytes: long.MaxValue,
            previousBytes: 10,
            currentBytes: 11,
            out var replacementOverflowTotal));
        Assert.Equal(long.MaxValue, replacementOverflowTotal);
    }

    [Fact]
    public void DbContext_OpenWithBatchInProgress_Warns()
    {
        _writer.MarkBatchInProgress();

        var stderr = ConsoleCapture.CaptureError(() =>
        {
            using var reopened = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        });

        Assert.Contains("Last batch did not complete", stderr);
        Assert.Contains("cdidx index --rebuild", stderr);
    }

    [Fact]
    public void DbContext_WriteOpenWithBatchInProgress_ReportsWithoutDemotingReadiness_Issue4557()
    {
        _writer.MarkGraphReady();
        _writer.MarkIssuesReady();
        _writer.MarkBatchInProgress();
        var expectedUserVersion = _db.GetUserVersion();

        using (var reopened = new DbContext(DbOpenIntent.WriteIndex, _dbPath))
        {
            Assert.Equal(DbOpenIntent.WriteIndex, reopened.OpenIntent);
            Assert.Equal(expectedUserVersion, reopened.GetUserVersion());
        }
    }

    [Fact]
    public void DbContext_QueryOnlyWithBatchInProgress_ReportsWithoutDemotingReadiness_Issue4557()
    {
        _writer.MarkGraphReady();
        _writer.MarkIssuesReady();
        _writer.MarkBatchInProgress();
        var expectedUserVersion = _db.GetUserVersion();
        Assert.True(_db.TryCheckpointWalTruncate());

        using var reopened = new DbContext(DbOpenIntent.QueryOnly, _dbPath);

        Assert.True(reopened.IsReadOnly);
        Assert.Equal(DbOpenIntent.QueryOnly, reopened.OpenIntent);
        using var queryOnly = reopened.Connection.CreateCommand();
        queryOnly.CommandText = "PRAGMA query_only";
        Assert.Equal(1L, queryOnly.ExecuteScalar());
        Assert.Equal(expectedUserVersion, reopened.GetUserVersion());
    }

    [Fact]
    public void RepairIncompleteBatchReadiness_RequiresExplicitRepairIntent_Issue4557()
    {
        _writer.MarkGraphReady();
        _writer.MarkIssuesReady();
        _writer.MarkBatchInProgress();

        using (var writeDb = new DbContext(DbOpenIntent.WriteIndex, _dbPath))
        {
            Assert.Throws<InvalidOperationException>(() => writeDb.RepairIncompleteBatchReadiness());
        }

        using var repairDb = new DbContext(DbOpenIntent.Repair, _dbPath);
        Assert.True(repairDb.RepairIncompleteBatchReadiness());
        Assert.Equal(DbContext.HotspotReferenceAggregateFlags, repairDb.GetUserVersion());
    }

    [Fact]
    public void BatchInProgress_ClearInsideCommittedTransaction_PersistsCleanState()
    {
        _writer.MarkBatchInProgress();

        using (var txn = _writer.BeginTransaction())
        {
            _writer.ClearBatchInProgress();
            txn.Commit();
        }

        var stderr = ConsoleCapture.CaptureError(() =>
        {
            using var reopened = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        });

        Assert.DoesNotContain("Last batch did not complete", stderr);
    }

    [Fact]
    public void BatchInProgress_ClearInsideRolledBackTransaction_LeavesRecoveryWarning()
    {
        _writer.MarkBatchInProgress();

        using (var txn = _writer.BeginTransaction())
        {
            _writer.ClearBatchInProgress();
        }

        var stderr = ConsoleCapture.CaptureError(() =>
        {
            using var reopened = new DbContext(DbOpenIntent.WriteIndex, _dbPath);
        });

        Assert.Contains("Last batch did not complete", stderr);
    }

    [Fact]
    public void BeginTransaction_WhenBeginFails_RestoresTransactionDepth()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ConnectionString);
        var writer = new DbWriter(connection);

        var ex = Assert.Throws<InvalidOperationException>(() => writer.BeginTransaction());

        Assert.Contains("connection", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, GetTransactionDepth(writer));
    }

    [Fact]
    public void BeginTransaction_SameOwnerNestedScopeUsesSavepointRollback_Issue4154()
    {
        using (var outer = _writer.BeginTransaction(CancellationToken.None, "outer writer transaction"))
        {
            _writer.UpsertFile(new FileRecord
            {
                Path = "src/outer.cs",
                Lang = "csharp",
                Size = 12,
                Lines = 1,
                Modified = new DateTime(2026, 6, 29, 0, 0, 0, DateTimeKind.Utc),
                Checksum = "outer",
            });

            using (var nested = _writer.BeginTransaction(CancellationToken.None, "nested writer savepoint"))
            {
                Assert.Equal(2, GetTransactionDepth(_writer));
                _writer.UpsertFile(new FileRecord
                {
                    Path = "src/nested.cs",
                    Lang = "csharp",
                    Size = 12,
                    Lines = 1,
                    Modified = new DateTime(2026, 6, 29, 0, 0, 1, DateTimeKind.Utc),
                    Checksum = "nested",
                });
            }

            Assert.Equal(1, GetTransactionDepth(_writer));
            outer.Commit();
        }

        Assert.Equal(0, GetTransactionDepth(_writer));
        Assert.True(_writer.HasFileAtPath("src/outer.cs"));
        Assert.False(_writer.HasFileAtPath("src/nested.cs"));
    }

    [Fact]
    public async Task BeginTransaction_CancelledWhileGateHeld_ThrowsOperationCanceled_Issue3772()
    {
        using var held = _writer.BeginTransaction(CancellationToken.None, "owner operation");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Task.Run(() =>
        {
            using var waiting = _writer.BeginTransaction(cts.Token, "cancelled operation");
        }));
    }

    [Fact]
    public async Task BeginTransaction_GateTimeoutReportsOwnerAndWaiterDiagnostics_Issue3772()
    {
        var originalTimeout = DbWriter.TransactionStateContentionTimeoutForTesting;
        DbWriter.TransactionStateContentionTimeoutForTesting = TimeSpan.FromMilliseconds(25);
        try
        {
            using var held = _writer.BeginTransaction(CancellationToken.None, "owner operation");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            {
                using var waiting = _writer.BeginTransaction(CancellationToken.None, "waiting operation");
            }));

            Assert.Contains("Timed out waiting for DbWriter transaction gate", ex.Message);
            Assert.Contains("owner_operation=owner operation", ex.Message);
            Assert.Contains("waiter_operation=waiting operation", ex.Message);
            Assert.Contains("owner_thread_id=", ex.Message);
            Assert.Contains("waiter_thread_id=", ex.Message);
            Assert.Contains("transaction_depth=1", ex.Message);
        }
        finally
        {
            DbWriter.TransactionStateContentionTimeoutForTesting = originalTimeout;
        }
    }

    [Fact]
    public async Task TransactionScope_DisposeWaitsForPostCommitCheckpointFinalization()
    {
        var previousHook = DbWriter.BeforePassiveWalCheckpointForTesting;
        var previousTimeout = DbWriter.TransactionStateContentionTimeoutForTesting;
        using var checkpointEntered = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        var scope = _writer.BeginTransaction(CancellationToken.None, "post-commit finalization owner");
        Task<Exception>? commitTask = null;
        Task? disposeTask = null;

        try
        {
            DbWriter.TransactionStateContentionTimeoutForTesting = TimeSpan.FromMilliseconds(25);
            _writer.SetMeta("test_post_commit_finalization", "1");
            DbWriter.BeforePassiveWalCheckpointForTesting = () =>
            {
                checkpointEntered.Set();
                if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting to release the post-commit checkpoint hook.");
            };

            commitTask = Task.Run(() => Record.Exception(scope.Commit));
            Assert.True(
                checkpointEntered.Wait(TimeSpan.FromSeconds(2)),
                "Transaction commit did not reach post-commit checkpoint finalization.");

            var disposeStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = Task.Run(() =>
            {
                disposeStarted.TrySetResult(true);
                scope.Dispose();
            });
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotSame(
                disposeTask,
                await Task.WhenAny(disposeTask, Task.Delay(TestDeterminism.BlockedObservationWindow)));

            releaseCheckpoint.Set();
            Assert.Null(await commitTask.WaitAsync(TimeSpan.FromSeconds(2)));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

            using var next = _writer.BeginTransaction(
                CancellationToken.None,
                "post-commit finalization successor");
        }
        finally
        {
            releaseCheckpoint.Set();
            DbWriter.BeforePassiveWalCheckpointForTesting = previousHook;
            DbWriter.TransactionStateContentionTimeoutForTesting = previousTimeout;
            scope.Dispose();
            if (commitTask != null)
                await commitTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (disposeTask != null)
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task TransactionScope_RollbackClearsActiveTransactionBeforeDisposeReleasesGate()
    {
        var previousHook = DbWriter.BeforeRollbackTerminalStateForTesting;
        using var rollbackFinalizationEntered = new ManualResetEventSlim();
        using var releaseRollbackFinalization = new ManualResetEventSlim();
        var activeTransactionField = typeof(DbWriter).GetField(
            "_activeTransaction",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DbWriter._activeTransaction field was not found.");
        var scope = _writer.BeginTransaction(CancellationToken.None, "rollback finalization owner");
        Task<Exception>? rollbackTask = null;
        Task? disposeTask = null;
        Task? successorTask = null;
        object? activeTransactionAtFinalization = null;

        try
        {
            _writer.SetMeta("test_rollback_finalization", "rolled-back");
            DbWriter.BeforeRollbackTerminalStateForTesting = () =>
            {
                activeTransactionAtFinalization = activeTransactionField.GetValue(_writer);
                rollbackFinalizationEntered.Set();
                if (!releaseRollbackFinalization.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Timed out waiting to release rollback finalization.");
            };

            rollbackTask = Task.Run(() => Record.Exception(scope.Rollback));
            Assert.True(
                rollbackFinalizationEntered.Wait(TimeSpan.FromSeconds(2)),
                "Transaction rollback did not reach terminal-state finalization.");
            Assert.Null(activeTransactionAtFinalization);

            var disposeStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = Task.Run(() =>
            {
                disposeStarted.TrySetResult(true);
                scope.Dispose();
            });
            await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var successorAttempting = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var successorEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            successorTask = Task.Run(() =>
            {
                successorAttempting.TrySetResult(true);
                using var successor = _writer.BeginTransaction(
                    CancellationToken.None,
                    "rollback finalization successor");
                successorEntered.TrySetResult(true);
                _writer.SetMeta("test_rollback_finalization", "successor");
                successor.Commit();
            });
            await successorAttempting.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await Task.Delay(TestDeterminism.BlockedObservationWindow);
            Assert.False(disposeTask.IsCompleted);
            Assert.False(successorEntered.Task.IsCompleted);

            releaseRollbackFinalization.Set();
            Assert.Null(await rollbackTask.WaitAsync(TimeSpan.FromSeconds(2)));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
            await successorTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("successor", ReadMeta("test_rollback_finalization"));
        }
        finally
        {
            releaseRollbackFinalization.Set();
            DbWriter.BeforeRollbackTerminalStateForTesting = previousHook;
            scope.Dispose();
            if (rollbackTask != null)
                await rollbackTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (disposeTask != null)
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
            if (successorTask != null)
                await successorTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void TransactionScope_SavepointWithoutConnection_ThrowsExplicitInvalidOperation()
    {
        var scopeType = typeof(DbWriter).GetNestedType("TransactionScope")
            ?? throw new InvalidOperationException("TransactionScope type was not found.");
        var scope = Activator.CreateInstance(
            scopeType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: ["sp_missing_conn", null!, _writer],
            culture: null)
            ?? throw new InvalidOperationException("TransactionScope instance was not created.");

        using var disposable = (IDisposable)scope;
        var commit = scopeType.GetMethod("Commit")
            ?? throw new InvalidOperationException("Commit method was not found.");

        var ex = Assert.ThrowsAny<Exception>(() => commit.Invoke(scope, null));
        var actual = ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
            ? inner
            : ex;

        var invalidOperation = Assert.IsType<InvalidOperationException>(actual);
        Assert.Contains("SQLite connection", invalidOperation.Message);
    }

    [Fact]
    public void Constructor_NewDatabaseEnablesIncrementalAutoVacuum()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "PRAGMA auto_vacuum";

        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void RunIncrementalVacuum_ReclaimsFreelistPages()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_vacuum");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            VacuumResult result;
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                using (var cmd = db.Connection.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE vacuum_payload (id INTEGER PRIMARY KEY, payload BLOB);
                        WITH RECURSIVE n(value) AS (
                            SELECT 1
                            UNION ALL
                            SELECT value + 1 FROM n WHERE value < 128
                        )
                        INSERT INTO vacuum_payload (payload)
                        SELECT randomblob(4096) FROM n;
                        DELETE FROM vacuum_payload;";
                    cmd.ExecuteNonQuery();
                }

                result = db.RunIncrementalVacuum();
            }

            Assert.Equal("ok", result.Status);
            Assert.True(result.PageSize > 0);
            Assert.True(result.FreelistCountBefore > 0);
            Assert.True(result.FreelistCountAfter < result.FreelistCountBefore);
            Assert.True(result.PagesReclaimed > 0);
            Assert.True(result.BytesReclaimed > 0);
            Assert.False(result.DryRun);
            Assert.True(result.EstimatedBytesReclaimable > 0);
            Assert.True(result.DbSizeBytesBefore > 0);
            Assert.Equal(2, result.AutoVacuumModeAfter);
            Assert.Equal("incremental", result.AutoVacuumModeAfterName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void RunIncrementalVacuum_CancellationBeforeMetrics_ThrowsOperationCanceled_Issue3811()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_vacuum_cancel");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => db.RunIncrementalVacuum(false, cts.Token));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void RunIncrementalVacuum_ReportsProgressBoundaries_Issue3811()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_vacuum_progress");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        var progress = new List<string>();
        try
        {
            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.InitializeSchema();
            DbContext.MaintenanceProgressForTesting = (operation, phase) => progress.Add($"{operation}:{phase}");

            var result = db.RunIncrementalVacuum(dryRun: true);

            Assert.Equal("dry_run", result.Status);
            Assert.Contains("vacuum:metrics_before", progress);
            Assert.Contains("vacuum:metrics_after", progress);
        }
        finally
        {
            DbContext.MaintenanceProgressForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void MaintenanceGuidance_HighWalRecommendsCheckpoint_Issue3564()
    {
        var guidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            PageCount: 100,
            FreelistCount: 0,
            PageSize: 4096,
            WalSizeBytes: MaintenanceGuidanceBuilder.DefaultWalWarnBytes + 1,
            DbSizeBytes: 409_600,
            AutoVacuumMode: 2));

        Assert.Equal("checkpoint_recommended", guidance.WalState);
        Assert.Equal("ok", guidance.FreelistState);
        Assert.Contains("wal_checkpoint", guidance.RecommendedCommand, StringComparison.Ordinal);
        Assert.Contains("status --json", guidance.PostMaintenanceFollowUp, StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceGuidance_HighFreelistRecommendsVacuum_Issue3564()
    {
        var guidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            PageCount: 100,
            FreelistCount: 25,
            PageSize: 4096,
            WalSizeBytes: 0,
            DbSizeBytes: 409_600,
            AutoVacuumMode: 2));

        Assert.Equal("ok", guidance.WalState);
        Assert.Equal("vacuum_recommended", guidance.FreelistState);
        Assert.Equal(0.25, guidance.FreelistRatio);
        Assert.Equal(25, guidance.EstimatedPagesReclaimable);
        Assert.Equal(25 * 4096, guidance.EstimatedBytesReclaimable);
        Assert.Equal("cdidx vacuum --db <db>", guidance.RecommendedCommand);
        Assert.Equal("incremental", guidance.AutoVacuumModeName);
    }

    [Fact]
    public void MaintenanceGuidance_OverflowEstimateReportsUnknown_Issue3964()
    {
        var guidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            PageCount: long.MaxValue,
            FreelistCount: long.MaxValue,
            PageSize: 4096,
            WalSizeBytes: 0,
            DbSizeBytes: null,
            AutoVacuumMode: 2));

        Assert.Equal("vacuum_recommended", guidance.FreelistState);
        Assert.Null(guidance.EstimatedBytesReclaimable);
    }

    private static int GetTransactionDepth(DbWriter writer)
    {
        var field = typeof(DbWriter).GetField("_transactionDepth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_transactionDepth field was not found.");
        return (int)field.GetValue(writer)!;
    }

    [Fact]
    public void RunIncrementalVacuum_ConvertsLegacyNoAutoVacuumDatabase()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_legacy_vacuum");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA auto_vacuum=NONE;
                    PRAGMA application_id=1128544600;
                    CREATE TABLE files (id INTEGER PRIMARY KEY);
                    CREATE TABLE chunks (id INTEGER PRIMARY KEY);
                    CREATE TABLE symbols (id INTEGER PRIMARY KEY);
                    CREATE TABLE vacuum_payload (id INTEGER PRIMARY KEY, payload BLOB);
                    WITH RECURSIVE n(value) AS (
                        SELECT 1
                        UNION ALL
                        SELECT value + 1 FROM n WHERE value < 128
                    )
                    INSERT INTO vacuum_payload (payload)
                    SELECT randomblob(4096) FROM n;
                    DELETE FROM vacuum_payload;";
                cmd.ExecuteNonQuery();
            }

            VacuumResult result;
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                result = db.RunIncrementalVacuum();
                using var autoVacuumCmd = db.Connection.CreateCommand();
                autoVacuumCmd.CommandText = "PRAGMA auto_vacuum";
                Assert.Equal(2L, (long)autoVacuumCmd.ExecuteScalar()!);
            }

            Assert.True(result.FreelistCountBefore > 0);
            Assert.Equal(0, result.FreelistCountAfter);
            Assert.True(result.PagesReclaimed > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void Dispose_AfterWriteWork_RunsOptimizePragma()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_optimize_write");
        var dbPath = Path.Combine(dbDir, $"codeindex_optimize_write_{Guid.NewGuid():N}.db");
        var optimizeCount = 0;
        DbContext.OptimizePragmaExecutedForTesting = dataSource =>
        {
            if (dataSource.Contains(Path.GetFileName(dbPath), StringComparison.Ordinal))
                optimizeCount++;
        };
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
            }

            Assert.Equal(1, optimizeCount);
        }
        finally
        {
            DbContext.OptimizePragmaExecutedForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void Dispose_WithoutWriteWork_SkipsOptimizePragma()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_optimize_read");
        var dbPath = Path.Combine(dbDir, $"codeindex_optimize_read_{Guid.NewGuid():N}.db");
        var optimizeCount = 0;
        DbContext.OptimizePragmaExecutedForTesting = dataSource =>
        {
            if (dataSource.Contains(Path.GetFileName(dbPath), StringComparison.Ordinal))
                optimizeCount++;
        };
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
            }

            Assert.Equal(0, optimizeCount);
        }
        finally
        {
            DbContext.OptimizePragmaExecutedForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void InitializeSchema_CreatesReferenceCompositeIndexesForGraphLookups()
    {
        var symbolIndexes = ReadIndexNames(_db.Connection, "symbols");
        var indexes = ReadIndexNames(_db.Connection, "symbol_references");

        Assert.Contains("idx_symbols_file_name_folded", symbolIndexes);
        Assert.Contains("idx_symbols_file_name_nocase", symbolIndexes);
        Assert.Contains("idx_symbols_name_folded_container_name_nocase", symbolIndexes);
        Assert.Contains("idx_symbols_name_folded_container_qualified_name_nocase", symbolIndexes);
        Assert.Contains("idx_symbol_refs_name_kind", indexes);
        Assert.Contains("idx_symbol_refs_name_file", indexes);
        Assert.Contains("idx_symbol_refs_name_nocase_kind", indexes);
        Assert.Contains("idx_symbol_refs_name_nocase_file", indexes);
        Assert.Contains("idx_symbol_refs_container_nocase_kind", indexes);
        Assert.Contains("idx_symbol_refs_symbol_name_folded_kind", indexes);
        Assert.Contains("idx_symbol_refs_symbol_name_folded_file", indexes);
        Assert.Contains("idx_symbol_refs_container_name_folded_kind", indexes);
        Assert.Contains("idx_symbol_refs_resolved_source_target_kind", indexes);

        AssertIndexColumns(_db.Connection, "idx_symbols_file_name_folded", [("file_id", "BINARY"), ("name_folded", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbols_file_name_nocase", [("file_id", "BINARY"), ("name", "NOCASE")]);
        AssertIndexColumns(_db.Connection, "idx_symbols_name_folded_container_name_nocase", [("name_folded", "BINARY"), ("container_name", "NOCASE")]);
        AssertIndexColumns(_db.Connection, "idx_symbols_name_folded_container_qualified_name_nocase", [("name_folded", "BINARY"), ("container_qualified_name", "NOCASE")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_name_nocase_kind", [("symbol_name", "NOCASE"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_name_nocase_file", [("symbol_name", "NOCASE"), ("file_id", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_container_nocase_kind", [("container_name", "NOCASE"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_symbol_name_folded_kind", [("symbol_name_folded", "BINARY"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_symbol_name_folded_file", [("symbol_name_folded", "BINARY"), ("file_id", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_container_name_folded_kind", [("container_name_folded", "BINARY"), ("reference_kind", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_symbol_refs_resolved_source_target_kind", [("source_symbol_id", "BINARY"), ("target_symbol_id", "BINARY"), ("reference_kind", "BINARY")]);
        AssertIndexSqlContains(
            _db.Connection,
            "idx_symbol_refs_resolved_source_target_kind",
            "WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL");
    }

    [Fact]
    public void ReferenceResolutionLookupQueries_UseCompositeSymbolIndexes()
    {
        AssertSearchesWithIndex(
            ReadQueryPlanDetails(
                _db.Connection,
                "SELECT id FROM symbols WHERE file_id = @file_id AND name_folded = @name_folded",
                ("@file_id", 1L),
                ("@name_folded", "worker")),
            "idx_symbols_file_name_folded");

        AssertSearchesWithIndex(
            ReadQueryPlanDetails(
                _db.Connection,
                "SELECT id FROM symbols WHERE file_id = @file_id AND name = @name COLLATE NOCASE",
                ("@file_id", 1L),
                ("@name", "Worker")),
            "idx_symbols_file_name_nocase");

        AssertSearchesWithIndex(
            ReadQueryPlanDetails(
                _db.Connection,
                "SELECT id FROM symbols WHERE name_folded = @name_folded AND container_name = @container COLLATE NOCASE",
                ("@name_folded", "run"),
                ("@container", "Worker")),
            "idx_symbols_name_folded_container_name_nocase");

        AssertSearchesWithIndex(
            ReadQueryPlanDetails(
                _db.Connection,
                "SELECT id FROM symbols WHERE name_folded = @name_folded AND container_qualified_name = @container COLLATE NOCASE",
                ("@name_folded", "run"),
                ("@container", "Demo.Worker")),
            "idx_symbols_name_folded_container_qualified_name_nocase");

        var reverseEdgePlan = ReadQueryPlanDetails(
            _db.Connection,
            """
            SELECT id
            FROM symbol_references
            WHERE source_symbol_id = @source_symbol_id
              AND target_symbol_id = @target_symbol_id
              AND reference_kind = @reference_kind
            """,
            ("@source_symbol_id", 1L),
            ("@target_symbol_id", 2L),
            ("@reference_kind", "call"));
        Assert.Contains(reverseEdgePlan, detail =>
            detail.Contains("SEARCH symbol_references USING COVERING INDEX idx_symbol_refs_resolved_source_target_kind", StringComparison.Ordinal));
        Assert.DoesNotContain(reverseEdgePlan, detail =>
            detail.Contains("SCAN symbol_references", StringComparison.Ordinal));

        static void AssertSearchesWithIndex(IReadOnlyList<string> plan, string indexName)
        {
            Assert.Contains(plan, detail =>
                detail.Contains("SEARCH symbols USING", StringComparison.Ordinal)
                && detail.Contains(indexName, StringComparison.Ordinal));
            Assert.DoesNotContain(plan, detail => detail.Contains("SCAN symbols", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void InitializeSchema_CreatesBoundedMaintenanceLookupIndexes()
    {
        Assert.Contains("idx_files_checksum", ReadIndexNames(_db.Connection, "files"));
        Assert.Contains("idx_files_path_nocase", ReadIndexNames(_db.Connection, "files"));
        Assert.Contains("idx_file_issues_file_kind", ReadIndexNames(_db.Connection, "file_issues"));

        AssertIndexColumns(_db.Connection, "idx_files_checksum", [("checksum", "BINARY")]);
        AssertIndexColumns(_db.Connection, "idx_files_path_nocase", [("path", "NOCASE")]);
        AssertIndexColumns(
            _db.Connection,
            "idx_file_issues_file_kind",
            [("file_id", "BINARY"), ("kind", "BINARY")]);
    }

    [Fact]
    public void MaintenanceLookupQueries_UseBoundedIndexes()
    {
        var checksumPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.StaleChecksumCandidateSql,
            ("@checksum", "same-checksum"),
            ("@path", "src/current.cs"));
        Assert.Contains(checksumPlan, detail =>
            detail.Contains("SEARCH files USING INDEX idx_files_checksum", StringComparison.Ordinal));
        Assert.DoesNotContain(checksumPlan, detail => detail.Contains("SCAN files", StringComparison.Ordinal));

        var csharpChecksumPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.StaleCSharpChecksumCandidateSql,
            ("@checksum", "same-checksum"),
            ("@path", "src/current.cs"));
        Assert.Contains(csharpChecksumPlan, detail =>
            detail.Contains("SEARCH files USING INDEX idx_files_checksum", StringComparison.Ordinal));
        Assert.DoesNotContain(csharpChecksumPlan, detail =>
            detail.Contains("SCAN files", StringComparison.Ordinal));

        var pathAliasPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.StaleRetainedPathAliasCandidateSql,
            ("@path", "src/Target.cs"));
        Assert.Contains(pathAliasPlan, detail =>
            detail.Contains("SEARCH files USING INDEX idx_files_path_nocase", StringComparison.Ordinal));
        Assert.DoesNotContain(pathAliasPlan, detail => detail.Contains("SCAN files", StringComparison.Ordinal));

        var csharpPathAliasPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.StaleCSharpRetainedPathAliasCandidateSql,
            ("@path", "src/Target.cs"));
        Assert.Contains(csharpPathAliasPlan, detail =>
            detail.Contains("SEARCH files USING INDEX idx_files_path_nocase", StringComparison.Ordinal));
        Assert.DoesNotContain(csharpPathAliasPlan, detail =>
            detail.Contains("SCAN files", StringComparison.Ordinal));

        var issuePlan = ReadQueryPlanDetails(
            _db.Connection,
            """
            SELECT f.id
            FROM files f
            WHERE NOT EXISTS (
                SELECT 1
                FROM file_issues
                WHERE file_id = f.id AND kind = @kind
            )
            """,
            ("@kind", "symbol_count_exceeded"));
        Assert.Contains(issuePlan, detail =>
            detail.Contains("SEARCH file_issues USING COVERING INDEX idx_file_issues_file_kind", StringComparison.Ordinal));
        Assert.DoesNotContain(issuePlan, detail => detail.Contains("SCAN file_issues", StringComparison.Ordinal));

        var directoryStemPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.StaleDirectoryStemCandidateSql,
            ("@path", "src/target.py"),
            ("@base_path", "src/target"),
            ("@base_dot_lower_bound", "src/target."),
            ("@base_dot_upper_bound", "src/target/"));
        Assert.Contains(directoryStemPlan, detail =>
            detail.Contains("SEARCH files USING INDEX sqlite_autoindex_files_1", StringComparison.Ordinal));
        Assert.DoesNotContain(directoryStemPlan, detail => detail.Contains("SCAN files", StringComparison.Ordinal));

        var csharpDirectoryStemPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.StaleCSharpDirectoryStemCandidateSql,
            ("@path", "src/target.py"),
            ("@base_path", "src/target"),
            ("@base_dot_lower_bound", "src/target."),
            ("@base_dot_upper_bound", "src/target/"));
        Assert.Contains(csharpDirectoryStemPlan, detail =>
            detail.Contains("SEARCH files USING INDEX sqlite_autoindex_files_1", StringComparison.Ordinal));
        Assert.DoesNotContain(csharpDirectoryStemPlan, detail =>
            detail.Contains("SCAN files", StringComparison.Ordinal));

        var contractPathPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.BuildCSharpStaticInterfaceContractPathPreflightSql(
                batchCount: 1,
                includeInterfaceDeclarationsAsConservativeEvidence: false),
            ("@path0", "src/target.cs"));
        Assert.Contains(contractPathPlan, detail =>
            detail.Contains("SEARCH f USING INDEX sqlite_autoindex_files_1", StringComparison.Ordinal));
        Assert.Contains(contractPathPlan, detail =>
            detail.Contains("SEARCH s USING INDEX idx_symbols_file_kind", StringComparison.Ordinal));
        Assert.DoesNotContain(contractPathPlan, detail =>
            detail.Contains("SCAN f", StringComparison.Ordinal)
            || detail.Contains("SCAN s", StringComparison.Ordinal));

        var csharpFilePathPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.BuildCSharpFilePathLookupSql(batchCount: 1),
            ("@path0", "src/target.cs"));
        Assert.Contains(csharpFilePathPlan, detail =>
            detail.Contains("SEARCH files USING INDEX sqlite_autoindex_files_1", StringComparison.Ordinal));
        Assert.DoesNotContain(csharpFilePathPlan, detail =>
            detail.Contains("SCAN files", StringComparison.Ordinal));
    }

    [Fact]
    public void CSharpContractWorkspaceQueries_UseFileKindThenBoundedInterfaceNamePlans()
    {
        var memberPlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.CSharpStaticInterfaceContractMemberWorkspaceSql);
        Assert.Contains(memberPlan, detail =>
            detail.Contains("SEARCH f USING INDEX idx_files_lang", StringComparison.Ordinal));
        Assert.Contains(memberPlan, detail =>
            detail.Contains("SEARCH s USING INDEX idx_symbols_file_kind", StringComparison.Ordinal)
            && detail.Contains("file_id=? AND kind=?", StringComparison.Ordinal));
        Assert.DoesNotContain(memberPlan, detail =>
            detail.Contains("SCAN f", StringComparison.Ordinal)
            || detail.Contains("SCAN s", StringComparison.Ordinal));

        var interfacePlan = ReadQueryPlanDetails(
            _db.Connection,
            DbWriter.BuildCSharpStaticInterfaceDeclarationWorkspaceSql(batchCount: 1),
            ("@containerName0", "IContract"));
        Assert.Contains(interfacePlan, detail =>
            detail.Contains("SEARCH s USING INDEX idx_symbols_name", StringComparison.Ordinal)
            && detail.Contains("name=?", StringComparison.Ordinal));
        Assert.Contains(interfacePlan, detail =>
            detail.Contains("SEARCH f USING INTEGER PRIMARY KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(interfacePlan, detail =>
            detail.Contains("SCAN f", StringComparison.Ordinal)
            || detail.Contains("SCAN s", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadCSharpContractWorkspace_MaterializesOnlyCandidatesAndMatchingInterfaces()
    {
        const int FillerCount = 64;
        var contractFileId = UpsertTestFile("src/IContract.cs", "contract");
        var duplicateInterfaceFileId = UpsertTestFile("src/Partials/IContract.cs", "contract-partial");
        var symbols = new List<SymbolRecord>(FillerCount * 2 + 3)
        {
            new()
            {
                FileId = contractFileId,
                Kind = "interface",
                Name = "IContract",
                Line = 1,
                StartLine = 1,
                EndLine = 8,
                Signature = "public partial interface IContract<T>",
            },
            new()
            {
                FileId = duplicateInterfaceFileId,
                Kind = "interface",
                Name = "IContract",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = "public partial interface IContract<T>",
            },
            new()
            {
                FileId = contractFileId,
                Kind = "function",
                Name = "Create",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                Signature = "public static abstract T Create();",
                ContainerKind = "interface",
                ContainerName = "IContract",
                ReturnType = "T",
            },
        };
        for (var index = 0; index < FillerCount; index++)
        {
            var fileId = UpsertTestFile($"src/Filler{index:D2}.cs", $"filler-{index:D2}");
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "interface",
                Name = $"IPlain{index:D2}",
                Line = 1,
                StartLine = 1,
                EndLine = 4,
                Signature = $"public interface IPlain{index:D2}",
            });
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = $"CreatevirtualNode{index:D2}",
                Line = 2,
                StartLine = 2,
                EndLine = 2,
                Signature = $"public static AbstractFactory CreatevirtualNode{index:D2}();",
                ContainerKind = "interface",
                ContainerName = $"IPlain{index:D2}",
            });
        }
        _writer.InsertSymbols(symbols);

        var previousStatsHook = DbWriter.CSharpContractWorkspaceReadStatsForTesting;
        var reads = new List<DbWriter.CSharpContractWorkspaceReadStats>();
        try
        {
            DbWriter.CSharpContractWorkspaceReadStatsForTesting = reads.Add;

            var loaded = _writer.LoadCSharpStaticInterfaceContractSymbols();

            Assert.Contains(loaded, symbol => symbol.Kind == "function" && symbol.Name == "Create");
            Assert.Equal(2, loaded.Count(symbol => symbol.Kind == "interface" && symbol.Name == "IContract"));
            Assert.DoesNotContain(loaded, symbol => symbol.Name.StartsWith("IPlain", StringComparison.Ordinal));
            var read = Assert.Single(reads);
            Assert.Equal(FillerCount + 1, read.MemberCandidateRowsRead);
            Assert.Equal(1, read.ExactMembersRetained);
            Assert.Equal(2, read.InterfaceDeclarationRowsRead);
            Assert.Equal(1, read.InterfaceDeclarationBatchQueries);

            reads.Clear();
            var retained = _writer.LoadCSharpStaticInterfaceContractSymbols(
                new HashSet<string>(["src/IContract.cs"], StringComparer.Ordinal),
                out var excludedPathsHaveContracts);

            Assert.Empty(retained);
            Assert.True(excludedPathsHaveContracts);
            var excludedRead = Assert.Single(reads);
            Assert.Equal(FillerCount + 1, excludedRead.MemberCandidateRowsRead);
            Assert.Equal(0, excludedRead.ExactMembersRetained);
            Assert.Equal(0, excludedRead.InterfaceDeclarationRowsRead);
            Assert.Equal(0, excludedRead.InterfaceDeclarationBatchQueries);

            Assert.True(_writer.DeleteFileByPath("src/IContract.cs"));
            reads.Clear();

            var negative = _writer.LoadCSharpStaticInterfaceContractSymbols();

            Assert.Empty(negative);
            var negativeRead = Assert.Single(reads);
            Assert.Equal(FillerCount, negativeRead.MemberCandidateRowsRead);
            Assert.Equal(0, negativeRead.ExactMembersRetained);
            Assert.Equal(0, negativeRead.InterfaceDeclarationRowsRead);
            Assert.Equal(0, negativeRead.InterfaceDeclarationBatchQueries);
        }
        finally
        {
            DbWriter.CSharpContractWorkspaceReadStatsForTesting = previousStatsHook;
        }
    }

    [Fact]
    public void TryMigrateForRead_CreatesReferenceCompositeIndexesForGraphLookups()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_legacy_index");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE symbols (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind TEXT,
                        name TEXT,
                        line INTEGER
                    );
                    CREATE TABLE symbol_references (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        symbol_name TEXT,
                        reference_kind TEXT,
                        line INTEGER,
                        column_number INTEGER,
                        context TEXT,
                        container_kind TEXT,
                        container_name TEXT
                    );";
                cmd.ExecuteNonQuery();
            }

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();
            var fileIndexes = ReadIndexNames(db.Connection, "files");
            var symbolIndexes = ReadIndexNames(db.Connection, "symbols");
            var indexes = ReadIndexNames(db.Connection, "symbol_references");

            Assert.DoesNotContain("idx_files_path_nocase", fileIndexes);
            Assert.Contains("idx_symbols_file_name_folded", symbolIndexes);
            Assert.Contains("idx_symbols_file_name_nocase", symbolIndexes);
            Assert.Contains("idx_symbols_name_folded_container_name_nocase", symbolIndexes);
            Assert.Contains("idx_symbols_name_folded_container_qualified_name_nocase", symbolIndexes);
            Assert.Contains("idx_symbol_refs_name_kind", indexes);
            Assert.Contains("idx_symbol_refs_name_file", indexes);
            Assert.Contains("idx_symbol_refs_name_nocase_kind", indexes);
            Assert.Contains("idx_symbol_refs_name_nocase_file", indexes);
            Assert.Contains("idx_symbol_refs_container_nocase_kind", indexes);
            Assert.Contains("idx_symbol_refs_symbol_name_folded_kind", indexes);
            Assert.Contains("idx_symbol_refs_symbol_name_folded_file", indexes);
            Assert.Contains("idx_symbol_refs_container_name_folded_kind", indexes);
            Assert.Contains("idx_symbol_refs_resolved_source_target_kind", indexes);

            AssertIndexColumns(db.Connection, "idx_symbols_file_name_folded", [("file_id", "BINARY"), ("name_folded", "BINARY")]);
            AssertIndexColumns(db.Connection, "idx_symbols_file_name_nocase", [("file_id", "BINARY"), ("name", "NOCASE")]);
            AssertIndexColumns(db.Connection, "idx_symbols_name_folded_container_name_nocase", [("name_folded", "BINARY"), ("container_name", "NOCASE")]);
            AssertIndexColumns(db.Connection, "idx_symbols_name_folded_container_qualified_name_nocase", [("name_folded", "BINARY"), ("container_qualified_name", "NOCASE")]);
            AssertIndexColumns(db.Connection, "idx_symbol_refs_container_nocase_kind", [("container_name", "NOCASE"), ("reference_kind", "BINARY")]);
            AssertIndexColumns(db.Connection, "idx_symbol_refs_container_name_folded_kind", [("container_name_folded", "BINARY"), ("reference_kind", "BINARY")]);
            AssertIndexColumns(db.Connection, "idx_symbol_refs_resolved_source_target_kind", [("source_symbol_id", "BINARY"), ("target_symbol_id", "BINARY"), ("reference_kind", "BINARY")]);
            AssertIndexSqlContains(
                db.Connection,
                "idx_symbol_refs_resolved_source_target_kind",
                "WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void Constructor_WritableOpenRejectsNewerUserVersion()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_newer_schema");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"PRAGMA user_version = {DbContext.CurrentSchemaVersion + 1}";
                cmd.ExecuteNonQuery();
            }

            var ex = Assert.Throws<CodeIndexException>(() => new DbContext(DbOpenIntent.WriteIndex, dbPath));

            Assert.Equal(CodeIndex.Cli.CommandErrorCodes.SchemaTooNew, ex.Code);
            Assert.Equal(CodeIndexExceptionCategory.Database, ex.Category);
            Assert.Equal(dbPath, ex.Path);
            Assert.Contains("newer cdidx", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rebuild the index", ex.Hint, StringComparison.OrdinalIgnoreCase);

            using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString);
            verifyConnection.Open();
            using var verifyJournalMode = verifyConnection.CreateCommand();
            verifyJournalMode.CommandText = "PRAGMA journal_mode";
            var journalMode = Assert.IsType<string>(verifyJournalMode.ExecuteScalar());
            Assert.False(string.Equals("wal", journalMode, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void TryMigrateForRead_InsideExistingTransaction_UsesExternalOwnershipAndRollsBack_Issue4560()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_nested_migration");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE symbols (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind TEXT,
                        name TEXT,
                        line INTEGER
                    );
                    CREATE TABLE symbol_references (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        symbol_name TEXT,
                        reference_kind TEXT,
                        line INTEGER,
                        column_number INTEGER,
                        context TEXT,
                        container_kind TEXT,
                        container_name TEXT
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            using var transaction = db.Connection.BeginTransaction(deferred: true);

            db.TryMigrateForRead();

            using var check = db.Connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name = 'signature'";
            Assert.Equal(1L, (long)check.ExecuteScalar()!);

            transaction.Rollback();

            using var afterRollback = db.Connection.CreateCommand();
            afterRollback.CommandText = "SELECT COUNT(*) FROM pragma_table_info('symbols') WHERE name = 'signature'";
            Assert.Equal(0L, (long)afterRollback.ExecuteScalar()!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void TryMigrateForRead_EnforcesForeignKeysAfterAddingReferenceLineColumn()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_legacy_fk");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL UNIQUE
                    );
                    CREATE TABLE symbols (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind TEXT,
                        name TEXT,
                        line INTEGER
                    );
                    CREATE TABLE symbol_references (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        symbol_name TEXT,
                        reference_kind TEXT,
                        line INTEGER,
                        column_number INTEGER,
                        context TEXT,
                        container_kind TEXT,
                        container_name TEXT
                    );";
                cmd.ExecuteNonQuery();
            }

            using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            db.TryMigrateForRead();

            using (var fkCheck = db.Connection.CreateCommand())
            {
                fkCheck.CommandText = "PRAGMA foreign_keys";
                Assert.Equal(1L, Convert.ToInt64(fkCheck.ExecuteScalar()));
            }

            using (var insertFile = db.Connection.CreateCommand())
            {
                insertFile.CommandText = "INSERT INTO files(path) VALUES ('src/Use.cs')";
                insertFile.ExecuteNonQuery();
            }

            using var insertReference = db.Connection.CreateCommand();
            insertReference.CommandText = @"
                INSERT INTO symbol_references(file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id)
                VALUES (1, 'MissingLine', 'call', 1, 1, 'MissingLine()', 999)";
            var ex = Assert.Throws<SqliteException>(() => insertReference.ExecuteNonQuery());
            Assert.Equal(19, ex.SqliteErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void Constructor_ConfiguresWalDurabilityPragmas()
    {
        Assert.Equal("wal", ExecuteScalarString("PRAGMA journal_mode"));
        Assert.Equal(1L, ExecuteScalarLong("PRAGMA synchronous"));
        Assert.Equal(DbContext.DefaultWalAutocheckpointPages, ExecuteScalarLong("PRAGMA wal_autocheckpoint"));
    }

    [Fact]
    public void Constructor_ConfiguresConnectionPerformancePragmas()
    {
        Assert.Equal(-DbContext.DefaultCacheSizeKb, ExecuteScalarLong("PRAGMA cache_size"));
        Assert.Equal(2L, ExecuteScalarLong("PRAGMA temp_store"));
        if (Environment.Is64BitProcess)
            Assert.Equal(DbContext.DefaultMmapSizeBytes, ExecuteScalarLong("PRAGMA mmap_size"));
    }

    [Fact]
    public void Constructor_UsesSqlitePerformanceEnvironmentOverrides()
    {
        AssertSqlitePerformancePragmas("4096", "1048576", 4096, 1048576);
    }

    [Fact]
    public void Constructor_AcceptsMaximumSqlitePerformanceEnvironmentOverrides()
    {
        AssertSqlitePerformancePragmas(
            DbContext.MaxCacheSizeKb.ToString(CultureInfo.InvariantCulture),
            DbContext.MaxMmapSizeBytes.ToString(CultureInfo.InvariantCulture),
            DbContext.MaxCacheSizeKb,
            DbContext.MaxMmapSizeBytes);
    }

    [Fact]
    public void Constructor_SqlitePerformanceEnvironmentAboveMaximumUsesDefaults()
    {
        AssertSqlitePerformancePragmas(
            (DbContext.MaxCacheSizeKb + 1).ToString(CultureInfo.InvariantCulture),
            (DbContext.MaxMmapSizeBytes + 1).ToString(CultureInfo.InvariantCulture),
            DbContext.DefaultCacheSizeKb,
            DbContext.DefaultMmapSizeBytes);
    }

    [Fact]
    public void Constructor_SqlitePerformanceEnvironmentOverflowUsesDefaults()
    {
        AssertSqlitePerformancePragmas(
            "2147483648",
            "9223372036854775808",
            DbContext.DefaultCacheSizeKb,
            DbContext.DefaultMmapSizeBytes);
    }

    private static void AssertSqlitePerformancePragmas(
        string cacheSizeValue,
        string mmapSizeValue,
        long expectedCacheSizeKb,
        long expectedMmapSizeBytes)
    {
        lock (TestConsoleLock.Gate)
        {
            var dbDir = TestProjectHelper.CreateTempProject("codeindex_perf_pragmas");
            var dbPath = Path.Combine(dbDir, "codeindex.db");
            using var env = EnvironmentVariableScope.Capture(
                DbContext.CacheSizeEnvironmentVariable,
                DbContext.MmapSizeEnvironmentVariable);
            try
            {
                env.Set(DbContext.CacheSizeEnvironmentVariable, cacheSizeValue);
                env.Set(DbContext.MmapSizeEnvironmentVariable, mmapSizeValue);

                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);

                Assert.Equal(-expectedCacheSizeKb, ExecuteScalarLong(db.Connection, "PRAGMA cache_size"));
                if (Environment.Is64BitProcess)
                    Assert.Equal(expectedMmapSizeBytes, ExecuteScalarLong(db.Connection, "PRAGMA mmap_size"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                TestProjectHelper.DeleteDirectory(dbDir);
            }
        }
    }

    [Fact]
    public void Constructor_SetsCodeIndexApplicationId()
    {
        Assert.Equal(DbContext.ApplicationId, ExecuteScalarLong("PRAGMA application_id"));
    }

    [Fact]
    public void UpsertFile_InsertsAndReturnsId()
    {
        var file = new FileRecord
        {
            Path = "src/main.py",
            Lang = "python",
            Size = 100,
            Lines = 10,
            Checksum = "abc123",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var id = _writer.UpsertFile(file, out var referenceIdentityChanged);
        Assert.True(id > 0);
        Assert.False(referenceIdentityChanged);
    }

    [Fact]
    public void InsertNewFile_InsertsAndReturnsId()
    {
        var file = new FileRecord
        {
            Path = "src/new.py",
            Lang = "python",
            Size = 100,
            Lines = 10,
            Checksum = "abc123",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var id = _writer.InsertNewFile(file);

        Assert.True(id > 0);
        var (fileCount, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, fileCount);
    }

    [Fact]
    public void InsertNewFile_DuplicatePathThrows()
    {
        var file = new FileRecord
        {
            Path = "src/duplicate.py",
            Lang = "python",
            Size = 100,
            Lines = 10,
            Checksum = "abc123",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        _writer.InsertNewFile(file);

        Assert.Throws<SqliteException>(() => _writer.InsertNewFile(file));
        var (fileCount, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, fileCount);
    }

    [Fact]
    public void UpsertFile_ReplacesOnConflict()
    {
        // Same path should replace (not duplicate)
        // 同一パスは置換される（重複しない）
        var file1 = new FileRecord
        {
            Path = "src/app.py",
            Lang = "python",
            Size = 100,
            Lines = 10,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var file2 = new FileRecord
        {
            Path = "src/app.py",
            Lang = "python",
            Size = 200,
            Lines = 20,
            Modified = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        _writer.UpsertFile(file1);
        _writer.UpsertFile(file2);

        var (count, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, count);
    }

    [Fact]
    public void UpsertFile_PreservesIdCleansIndexRowsAndKeepsIssues()
    {
        var initial = new FileRecord
        {
            Path = "src/reindex.py",
            Lang = "python",
            Size = 100,
            Lines = 5,
            Checksum = "old",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var fileId = _writer.UpsertFile(initial);
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 5,
            Content = "old_upsert_token",
        }]);
        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "old_symbol",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
        }]);
        _writer.InsertReferences([new ReferenceRecord
        {
            FileId = fileId,
            SymbolName = "old_call",
            ReferenceKind = "call",
            Line = 2,
            Column = 1,
            Context = "old_call()",
            ContainerKind = "function",
            ContainerName = "old_symbol",
        }]);
        _writer.InsertIssues(fileId, [new FileIssue
        {
            Path = initial.Path,
            Kind = "old_issue",
            Line = 1,
            Message = "keep until InsertIssues owns replacement",
        }]);

        var updatedId = _writer.UpsertFile(new FileRecord
        {
            Path = initial.Path,
            Lang = "python",
            Size = 200,
            Lines = 8,
            Checksum = "new",
            Modified = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        }, out var referenceIdentityChanged);

        Assert.Equal(fileId, updatedId);
        Assert.True(referenceIdentityChanged);
        using (var command = _db.Connection.CreateCommand())
        {
            command.Parameters.AddWithValue("@fileId", fileId);
            command.CommandText = """
                SELECT
                    (SELECT size FROM files WHERE id = @fileId),
                    (SELECT COUNT(*) FROM chunks WHERE file_id = @fileId),
                    (SELECT COUNT(*) FROM symbols WHERE file_id = @fileId),
                    (SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId),
                    (SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId),
                    (SELECT COUNT(*) FROM file_issues WHERE file_id = @fileId)
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(200L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
            Assert.Equal(0L, reader.GetInt64(3));
            Assert.Equal(0L, reader.GetInt64(4));
            Assert.Equal(1L, reader.GetInt64(5));
        }

        using var ftsCommand = _db.Connection.CreateCommand();
        ftsCommand.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'old_upsert_token'";
        Assert.Equal(0L, (long)ftsCommand.ExecuteScalar()!);

        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 8,
            Content = "preserved_without_cleanup",
        }]);
        var noCleanupId = _writer.UpsertFile(new FileRecord
        {
            Path = initial.Path,
            Lang = "python",
            Size = 300,
            Lines = 9,
            Checksum = "newer",
            Modified = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        }, cleanExistingData: false);

        Assert.Equal(fileId, noCleanupId);
        Assert.Equal((1, 1, 0, 0), _writer.GetCounts());
    }

    [Fact]
    public void UpsertFile_CleanupFailureRollsBackMetadataRowsAndFts()
    {
        var initial = new FileRecord
        {
            Path = "src/rollback.py",
            Lang = "python",
            Size = 100,
            Lines = 5,
            Checksum = "old",
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var fileId = _writer.UpsertFile(initial);
        _writer.InsertChunks([new ChunkRecord
        {
            FileId = fileId,
            ChunkIndex = 0,
            StartLine = 1,
            EndLine = 5,
            Content = "rollback_upsert_token",
        }]);
        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = "rollback_symbol",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
        }]);

        using (var trigger = _db.Connection.CreateCommand())
        {
            trigger.CommandText = $"""
                CREATE TRIGGER fail_upsert_symbol_cleanup
                BEFORE DELETE ON symbols
                WHEN OLD.file_id = {fileId}
                BEGIN
                    SELECT RAISE(ABORT, 'injected cleanup failure');
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        var updated = new FileRecord
        {
            Path = initial.Path,
            Lang = initial.Lang,
            Size = 200,
            Lines = 8,
            Checksum = "new",
            Modified = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        try
        {
            using var transaction = _writer.BeginTransaction();
            Assert.Throws<SqliteException>(() => _writer.UpsertFile(updated));
        }
        finally
        {
            using var dropTrigger = _db.Connection.CreateCommand();
            dropTrigger.CommandText = "DROP TRIGGER IF EXISTS fail_upsert_symbol_cleanup";
            dropTrigger.ExecuteNonQuery();
        }

        using (var verify = _db.Connection.CreateCommand())
        {
            verify.Parameters.AddWithValue("@fileId", fileId);
            verify.CommandText = """
                SELECT
                    (SELECT size FROM files WHERE id = @fileId),
                    (SELECT COUNT(*) FROM chunks WHERE file_id = @fileId),
                    (SELECT COUNT(*) FROM symbols WHERE file_id = @fileId),
                    (SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'rollback_upsert_token')
                """;
            using var reader = verify.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(100L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }

        var retryId = _writer.UpsertFile(updated);
        Assert.Equal(fileId, retryId);
        Assert.Equal((1, 0, 0, 0), _writer.GetCounts());
    }

    [Fact]
    public void GetUnchangedFileId_ReturnIdIfUnchanged()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/lib.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);

        // Same modified time should return the ID
        // 同一更新日時ならIDを返す
        var id = _writer.GetUnchangedFileId("src/lib.py", modified);
        Assert.NotNull(id);

        // Different modified time should return null
        // 異なる更新日時ならnullを返す
        var id2 = _writer.GetUnchangedFileId("src/lib.py", modified.AddHours(1));
        Assert.Null(id2);
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenIssueMetadataMissing()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/literal.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        var fileId = _writer.UpsertFile(file);
        _writer.InsertIssues(fileId,
        [
            new FileIssue
            {
                Path = file.Path,
                Kind = "replacement_char",
                Line = 1,
                Message = "legacy replacement_char row without metadata",
            },
        ]);

        Assert.Null(_writer.GetUnchangedFileId(file.Path, modified));

        _writer.InsertIssues(fileId,
        [
            new FileIssue
            {
                Path = file.Path,
                Kind = "replacement_char",
                Line = 1,
                Message = "U+FFFD source literal at line 1",
                Origin = FileIssue.OriginSourceLiteral,
                Severity = FileIssue.SeverityInfo,
            },
        ]);

        Assert.NotNull(_writer.GetUnchangedFileId(file.Path, modified));
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenNonUtf8LikelyMetadataMissing()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/garbled.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        var fileId = _writer.UpsertFile(file);
        _writer.InsertIssues(fileId,
        [
            new FileIssue
            {
                Path = file.Path,
                Kind = "non_utf8_likely",
                Line = 0,
                Message = "legacy non_utf8_likely row without metadata",
            },
        ]);

        Assert.Null(_writer.GetUnchangedFileId(file.Path, modified));

        _writer.InsertIssues(fileId,
        [
            new FileIssue
            {
                Path = file.Path,
                Kind = "non_utf8_likely",
                Line = 0,
                Message = "Likely non-UTF8 encoding",
                Origin = FileIssue.OriginDecodeReplacement,
                Severity = FileIssue.SeverityWarning,
            },
        ]);

        Assert.NotNull(_writer.GetUnchangedFileId(file.Path, modified));
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenBomMetadataStaleOrSuppressed_Issue4068()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sourceFile = new FileRecord
        {
            Path = "src/bom.cs",
            Lang = "csharp",
            Size = 50,
            Lines = 1,
            Modified = modified,
        };
        var sourceFileId = _writer.UpsertFile(sourceFile);
        _writer.InsertIssues(sourceFileId,
        [
            new FileIssue
            {
                Path = sourceFile.Path,
                Kind = "bom",
                Line = 1,
                Message = "legacy BOM row without metadata",
            },
        ]);

        Assert.Null(_writer.GetUnchangedFileId(sourceFile.Path, modified));

        _writer.InsertIssues(sourceFileId,
        [
            new FileIssue
            {
                Path = sourceFile.Path,
                Kind = "bom",
                Line = 1,
                Message = "UTF-8 BOM marker detected",
                Origin = FileIssue.OriginByteOrderMark,
                Severity = FileIssue.SeverityWarning,
            },
        ]);

        Assert.NotNull(_writer.GetUnchangedFileId(sourceFile.Path, modified));

        var utf16File = new FileRecord
        {
            Path = "src/utf16.cs",
            Lang = "csharp",
            Size = 50,
            Lines = 1,
            Modified = modified,
        };
        var utf16FileId = _writer.UpsertFile(utf16File);
        _writer.InsertIssues(utf16FileId,
        [
            new FileIssue
            {
                Path = utf16File.Path,
                Kind = "utf16_bom",
                Line = 1,
                Message = "legacy UTF-16 BOM row without metadata",
            },
        ]);

        Assert.Null(_writer.GetUnchangedFileId(utf16File.Path, modified));

        _writer.InsertIssues(utf16FileId,
        [
            new FileIssue
            {
                Path = utf16File.Path,
                Kind = "utf16_bom",
                Line = 1,
                Message = "UTF-16 LE BOM detected (decoded as UTF-16)",
                Origin = FileIssue.OriginByteOrderMark,
                Severity = FileIssue.SeverityWarning,
            },
        ]);

        Assert.NotNull(_writer.GetUnchangedFileId(utf16File.Path, modified));

        var solutionFile = new FileRecord
        {
            Path = "CodeIndex.sln",
            Lang = "solution",
            Size = 50,
            Lines = 1,
            Modified = modified,
        };
        var solutionFileId = _writer.UpsertFile(solutionFile);
        _writer.InsertIssues(solutionFileId,
        [
            new FileIssue
            {
                Path = solutionFile.Path,
                Kind = "bom",
                Line = 1,
                Message = "UTF-8 BOM marker detected",
                Origin = FileIssue.OriginByteOrderMark,
                Severity = FileIssue.SeverityWarning,
            },
        ]);

        Assert.Null(_writer.GetUnchangedFileId(solutionFile.Path, modified));

        _writer.InsertIssues(solutionFileId, []);

        Assert.NotNull(_writer.GetUnchangedFileId(solutionFile.Path, modified));
    }

    [Fact]
    public void InsertIssues_BatchesMultipleRowsAndClearsPreviousIssues()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/issues.py",
            Lang = "python",
            Size = 20,
            Lines = 3,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertIssues(fileId,
        [
            new FileIssue
            {
                Path = "src/issues.py",
                Kind = "replacement_char",
                Line = 1,
                Message = "replacement char",
                Origin = FileIssue.OriginSourceLiteral,
                Severity = FileIssue.SeverityInfo,
            },
            new FileIssue
            {
                Path = "src/issues.py",
                Kind = "non_utf8_likely",
                Line = 2,
                Message = "non UTF-8",
                Origin = FileIssue.OriginDecodeReplacement,
                Severity = FileIssue.SeverityWarning,
            },
            new FileIssue
            {
                Path = "src/issues.py",
                Kind = "bom",
                Line = 0,
                Message = "BOM marker",
                Origin = FileIssue.OriginByteOrderMark,
                Severity = FileIssue.SeverityWarning,
            },
        ]);

        using var countCmd = _db.Connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM file_issues WHERE file_id = @file_id";
        countCmd.Parameters.AddWithValue("@file_id", fileId);
        Assert.Equal(3L, countCmd.ExecuteScalar());

        _writer.InsertIssues(fileId, []);

        Assert.Equal(0L, countCmd.ExecuteScalar());
    }

    [Fact]
    public void InsertIssuesForNewFile_DoesNotDeleteExistingIssues()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/new-file-issues.py",
            Lang = "python",
            Size = 20,
            Lines = 3,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertIssues(fileId,
        [
            new FileIssue
            {
                Path = "src/new-file-issues.py",
                Kind = "replacement_char",
                Line = 1,
                Message = "replacement char",
                Origin = FileIssue.OriginSourceLiteral,
                Severity = FileIssue.SeverityInfo,
            },
        ]);

        using var countCmd = _db.Connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM file_issues WHERE file_id = @file_id";
        countCmd.Parameters.AddWithValue("@file_id", fileId);
        Assert.Equal(1L, countCmd.ExecuteScalar());

        _writer.InsertIssuesForNewFile(fileId, []);
        Assert.Equal(1L, countCmd.ExecuteScalar());

        _writer.InsertIssues(fileId, []);
        Assert.Equal(0L, countCmd.ExecuteScalar());
    }

    [Fact]
    public void GetUnchangedFileId_WithNullChecksumUsesModifiedAndSize()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/size.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);

        var id = _writer.GetUnchangedFileId("src/size.py", modified, checksum: null, size: 50);
        Assert.NotNull(id);

        var changedSizeId = _writer.GetUnchangedFileId("src/size.py", modified, checksum: null, size: 51);
        Assert.Null(changedSizeId);
    }

    [Fact]
    public void GetUnchangedFileIdByStat_ReturnsIdOnlyWhenModifiedAndSizeMatch()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/stat.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);

        var id = _writer.GetUnchangedFileIdByStat("src/stat.py", modified, 50, language: "python");

        Assert.NotNull(id);
        Assert.Null(_writer.GetUnchangedFileIdByStat("src/stat.py", modified.AddTicks(1), 50, language: "python"));
        Assert.Null(_writer.GetUnchangedFileIdByStat("src/stat.py", modified, 51, language: "python"));
        Assert.Null(_writer.GetUnchangedFileIdByStat("src/stat.py", modified, 50, language: "python", allowReuse: false));
    }

    [Fact]
    public void GetUnchangedFileIdByStat_ReturnsNullWhenReuseGuardsFail()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var guardedFile = new FileRecord
        {
            Path = "src/legacy-issue.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        var guardedFileId = _writer.UpsertFile(guardedFile);
        _writer.InsertIssues(guardedFileId,
        [
            new FileIssue
            {
                Path = guardedFile.Path,
                Kind = "replacement_char",
                Line = 1,
                Message = "legacy replacement_char row without metadata",
            },
        ]);

        Assert.Null(_writer.GetUnchangedFileIdByStat(guardedFile.Path, modified, guardedFile.Size, language: "python"));

        var staleLanguageFile = new FileRecord
        {
            Path = "src/stale-version.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(staleLanguageFile);
        _writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("python"), "0");

        Assert.Null(_writer.GetUnchangedFileIdByStat(staleLanguageFile.Path, modified, staleLanguageFile.Size, language: "python"));
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenLanguageExtractorVersionIsStale()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/lib.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);
        _writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("python"), "0");

        var id = _writer.GetUnchangedFileId("src/lib.py", modified, language: "python");

        Assert.Null(id);
    }

    [Theory]
    [InlineData("crystal", 2)]
    [InlineData("groovy", 2)]
    [InlineData("tcl", 2)]
    [InlineData("prolog", 1)]
    [InlineData("ambiguous_pl", 1)]
    public void GetUnchangedFileId_InvalidatesPreGraphLanguageContracts_Issue4746(
        string language,
        int previousContractVersion)
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = $"src/legacy-{language}.txt",
            Lang = language,
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);
        _writer.SetMeta(
            DbContext.GetSymbolExtractorVersionMetaKey(language),
            previousContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(SymbolExtractor.GetContractVersion(language) > previousContractVersion);
        Assert.Null(_writer.GetUnchangedFileId(file.Path, modified, language: language));
    }

    [Theory]
    [InlineData("crystal")]
    [InlineData("groovy")]
    [InlineData("tcl")]
    [InlineData("prolog")]
    [InlineData("ambiguous_pl")]
    public void GetUnchangedFileId_InvalidatesMissingGraphLanguageContracts_Issue4746(
        string language)
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = $"src/missing-contract-{language}.txt",
            Lang = language,
            Size = 50,
            Lines = 5,
            Modified = modified,
        };
        _writer.UpsertFile(file);

        Assert.Null(_writer.GetUnchangedFileId(file.Path, modified, language: language));
    }

    [Theory]
    [InlineData("crystal", 2)]
    [InlineData("groovy", 2)]
    [InlineData("tcl", 2)]
    [InlineData("prolog", 1)]
    [InlineData("ambiguous_pl", 1)]
    public void GetStatus_DegradesPreGraphLanguageContractsUntilRefresh_Issue4746(
        string language,
        int previousContractVersion)
    {
        _writer.UpsertFile(new FileRecord
        {
            Path = $"src/legacy-status-{language}.txt",
            Lang = language,
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var versionKey = DbContext.GetSymbolExtractorVersionMetaKey(language);
        _writer.SetMeta(
            versionKey,
            previousContractVersion.ToString(CultureInfo.InvariantCulture));

        var staleReader = new DbReader(_db.Connection);
        var staleStatus = staleReader.GetStatus();
        var staleWorkspaceHealth = staleReader.GetWorkspaceIndexHealth();

        Assert.False(staleStatus.ReferenceGraphComplete);
        Assert.False(staleStatus.GraphDataCurrent);
        Assert.False(staleWorkspaceHealth.ReferenceGraphComplete);
        Assert.False(staleWorkspaceHealth.GraphDataCurrent);
        Assert.Contains(
            DbReader.DynamicReferenceGraphContractStaleReason,
            staleStatus.ReferenceGraphIncompleteReasons ?? []);

        _writer.SetMeta(
            versionKey,
            SymbolExtractor.GetContractVersion(language).ToString(CultureInfo.InvariantCulture));

        var refreshedReader = new DbReader(_db.Connection);
        var refreshedStatus = refreshedReader.GetStatus();
        var refreshedWorkspaceHealth = refreshedReader.GetWorkspaceIndexHealth();

        Assert.DoesNotContain(
            DbReader.DynamicReferenceGraphContractStaleReason,
            refreshedStatus.ReferenceGraphIncompleteReasons ?? []);
        Assert.Equal(
            refreshedStatus.ReferenceGraphComplete,
            refreshedWorkspaceHealth.ReferenceGraphComplete);
        Assert.Equal(
            refreshedStatus.GraphDataCurrent,
            refreshedWorkspaceHealth.GraphDataCurrent);
    }

    [Fact]
    public void GetUnchangedFileId_MatchesByChecksumWhenTimestampDiffers()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var checksum = "abc123def456";
        var file = new FileRecord
        {
            Path = "src/checksum.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
            Checksum = checksum,
        };
        _writer.UpsertFile(file);

        // Different timestamp but same checksum should return the ID (e.g. git checkout)
        // タイムスタンプ異なるがチェックサム一致ならIDを返す（例: git checkout）
        var newModified = modified.AddHours(1);
        var id = _writer.GetUnchangedFileId("src/checksum.py", newModified, checksum);
        Assert.NotNull(id);

        // Different timestamp AND different checksum should return null
        // タイムスタンプもチェックサムも異なるならnullを返す
        var id2 = _writer.GetUnchangedFileId("src/checksum.py", newModified.AddHours(1), "different_checksum");
        Assert.Null(id2);
    }

    [Fact]
    public void GetUnchangedFileId_UpdatesGeneratedMarkerOnReusableRows()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var checksum = "generated-checksum";
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/generated.g.cs",
            Lang = "csharp",
            Size = 50,
            Lines = 2,
            Modified = modified,
            Checksum = checksum,
            Generated = false,
        });

        var id = _writer.GetUnchangedFileId("src/generated.g.cs", modified, checksum, language: "csharp", generated: true);

        Assert.NotNull(id);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT generated FROM files WHERE path = 'src/generated.g.cs'";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenTimestampMatchesButChecksumDiffers()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var file = new FileRecord
        {
            Path = "src/coarse-time.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = modified,
            Checksum = "first_checksum",
        };
        _writer.UpsertFile(file);

        var id = _writer.GetUnchangedFileId("src/coarse-time.py", modified, "second_checksum");

        Assert.Null(id);
    }

    [Fact]
    public void HasExtractionCapViolationForFile_CombinesCountsAndIssues()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/caps.cs",
            Lang = "csharp",
            Size = 20,
            Lines = 2,
            Modified = modified,
            Checksum = "caps_checksum",
        });

        Assert.False(_writer.HasExtractionCapViolationForFile(fileId, maxSymbolsPerFile: 2, maxReferencesPerFile: 2));
        Assert.False(_writer.HasReusableFileBlockingIssueForFile(fileId, maxSymbolsPerFile: 2, maxReferencesPerFile: 2, generatedExtractionSuppressed: false));
        Assert.True(_writer.HasReusableFileBlockingIssueForFile(fileId, maxSymbolsPerFile: 2, maxReferencesPerFile: 2, generatedExtractionSuppressed: true));
        Assert.Equal(
            fileId,
            _writer.GetReusableUnchangedFileIdByStat(
                "src/caps.cs",
                modified,
                size: 20,
                language: "csharp",
                maxSymbolsPerFile: 2,
                maxReferencesPerFile: 2,
                generatedExtractionSuppressed: false));
        Assert.Equal(
            fileId,
            _writer.GetReusableUnchangedFileId(
                "src/caps.cs",
                modified.AddMinutes(1),
                checksum: "caps_checksum",
                size: 20,
                lines: 2,
                language: "csharp",
                generated: false,
                maxSymbolsPerFile: 2,
                maxReferencesPerFile: 2,
                generatedExtractionSuppressed: false));
        Assert.Null(_writer.GetReusableUnchangedFileIdByStat(
            "src/caps.cs",
            modified,
            size: 20,
            language: "csharp",
            maxSymbolsPerFile: 2,
            maxReferencesPerFile: 2,
            generatedExtractionSuppressed: true));

        _writer.InsertSymbols(
        [
            new SymbolRecord { FileId = fileId, Name = "One", Kind = "class", Line = 1 },
            new SymbolRecord { FileId = fileId, Name = "Two", Kind = "class", Line = 2 },
        ]);
        Assert.False(_writer.HasExtractionCapViolationForFile(fileId, maxSymbolsPerFile: 2, maxReferencesPerFile: 2));
        Assert.True(_writer.HasExtractionCapViolationForFile(fileId, maxSymbolsPerFile: 1, maxReferencesPerFile: 2));
        Assert.Null(_writer.GetReusableUnchangedFileIdByStat(
            "src/caps.cs",
            modified.AddMinutes(1),
            size: 20,
            language: "csharp",
            maxSymbolsPerFile: 1,
            maxReferencesPerFile: 2,
            generatedExtractionSuppressed: false));
        Assert.Null(_writer.GetReusableUnchangedFileId(
            "src/caps.cs",
            modified.AddMinutes(1),
            checksum: "caps_checksum",
            size: 20,
            lines: 2,
            language: "csharp",
            generated: false,
            maxSymbolsPerFile: 1,
            maxReferencesPerFile: 2,
            generatedExtractionSuppressed: false));

        var issueFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/cap-issue.cs",
            Lang = "csharp",
            Size = 20,
            Lines = 2,
            Modified = modified,
            Checksum = "cap_issue_checksum",
        });
        _writer.InsertIssues(issueFileId,
        [
            new FileIssue
            {
                Path = "src/cap-issue.cs",
                Kind = "reference_count_exceeded",
                Line = 0,
                Message = "too many references",
            },
        ]);

        Assert.True(_writer.HasExtractionCapViolationForFile(issueFileId, maxSymbolsPerFile: 10, maxReferencesPerFile: 10));
        Assert.Null(_writer.GetReusableUnchangedFileIdByStat(
            "src/cap-issue.cs",
            modified,
            size: 20,
            language: "csharp",
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10,
            generatedExtractionSuppressed: false));
        Assert.Null(_writer.GetReusableUnchangedFileId(
            "src/cap-issue.cs",
            modified,
            checksum: "cap_issue_checksum",
            size: 20,
            lines: 2,
            language: "csharp",
            generated: false,
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10,
            generatedExtractionSuppressed: false));

        var generatedFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/generated.g.cs",
            Lang = "csharp",
            Size = 20,
            Lines = 2,
            Modified = modified,
            Checksum = "generated_checksum",
        });
        _writer.InsertIssues(generatedFileId,
        [
            new FileIssue
            {
                Path = "src/generated.g.cs",
                Kind = FileIndexer.GeneratedCodeExtractionSkippedIssueKind,
                Line = 0,
                Message = "generated extraction skipped",
            },
        ]);

        Assert.False(_writer.HasReusableFileBlockingIssueForFile(generatedFileId, maxSymbolsPerFile: 10, maxReferencesPerFile: 10, generatedExtractionSuppressed: true));
        Assert.True(_writer.HasReusableFileBlockingIssueForFile(generatedFileId, maxSymbolsPerFile: 10, maxReferencesPerFile: 10, generatedExtractionSuppressed: false));
        Assert.Equal(
            generatedFileId,
            _writer.GetReusableUnchangedFileIdByStat(
                "src/generated.g.cs",
                modified,
                size: 20,
                language: "csharp",
                maxSymbolsPerFile: 10,
                maxReferencesPerFile: 10,
                generatedExtractionSuppressed: true));
        Assert.Equal(
            generatedFileId,
            _writer.GetReusableUnchangedFileId(
                "src/generated.g.cs",
                modified,
                checksum: "generated_checksum",
                size: 20,
                lines: 2,
                language: "csharp",
                generated: true,
                maxSymbolsPerFile: 10,
                maxReferencesPerFile: 10,
                generatedExtractionSuppressed: true));
        Assert.Null(_writer.GetReusableUnchangedFileIdByStat(
            "src/generated.g.cs",
            modified,
            size: 20,
            language: "csharp",
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10,
            generatedExtractionSuppressed: false));
        Assert.Null(_writer.GetReusableUnchangedFileId(
            "src/generated.g.cs",
            modified,
            checksum: "generated_checksum",
            size: 20,
            lines: 2,
            language: "csharp",
            generated: true,
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10,
            generatedExtractionSuppressed: false));

        var reusableStats = _writer.LoadReusableIndexedFileStats(
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10);
        Assert.Equal(2, reusableStats.Count);
        Assert.Equal(fileId, reusableStats["src/caps.cs"].FileId);
        Assert.Equal(modified.AddMinutes(1), reusableStats["src/caps.cs"].ModifiedUtc);
        Assert.Equal(20, reusableStats["src/caps.cs"].Size);
        Assert.Equal("csharp", reusableStats["src/caps.cs"].Language);
        Assert.False(reusableStats["src/caps.cs"].GeneratedExtractionSuppressed);
        Assert.Equal(generatedFileId, reusableStats["src/generated.g.cs"].FileId);
        Assert.True(reusableStats["src/generated.g.cs"].GeneratedExtractionSuppressed);
        Assert.DoesNotContain("src/cap-issue.cs", reusableStats.Keys);

        var reusableStatsUnderLowerCap = _writer.LoadReusableIndexedFileStats(
            maxSymbolsPerFile: 1,
            maxReferencesPerFile: 10);
        Assert.DoesNotContain("src/caps.cs", reusableStatsUnderLowerCap.Keys);
        Assert.Contains("src/generated.g.cs", reusableStatsUnderLowerCap.Keys);
    }

    [Fact]
    public void GetReferenceExtractionCapHits_ReadsInsideActiveWriterTransaction()
    {
        var file = new FileRecord
        {
            Path = "src/reference-cap.py",
            Lang = "python",
            Size = 20,
            Lines = 2,
            Modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Checksum = "reference_cap_checksum",
        };
        var fileId = _writer.UpsertFile(file);

        using (var transaction = _writer.BeginTransaction())
        {
            _writer.InsertIssues(fileId,
            [
                new FileIssue
                {
                    Path = file.Path,
                    Kind = ReferenceExtractor.ReferenceSafetyCapDiagnosticKinds[0],
                    Line = 1,
                    Message = "reference extraction safety cap reached",
                },
            ]);

            var summary = _writer.GetReferenceExtractionCapHits(issuesStateAvailable: true);

            Assert.Equal(1, summary.HitCount);
            Assert.Equal(1, summary.AffectedFileCount);
            Assert.Equal(file.Path, Assert.Single(summary.Files).File);
            transaction.Commit();
        }

        Assert.Equal(1, _writer.GetReferenceExtractionCapHits(issuesStateAvailable: true).HitCount);
    }

    [Fact]
    public void LoadReusableIndexedFileStats_FiltersStaleLanguagesAndMalformedStorageTypes()
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fixtures = new (string Path, string Language)[]
        {
            ("src/valid.cs", "csharp"),
            ("src/stale.py", "python"),
            ("src/stale-helper.py", "python"),
            ("src/null-size.cs", "csharp"),
            ("src/text-size.cs", "csharp"),
            ("src/integer-modified.cs", "csharp"),
            ("src/invalid-modified.cs", "csharp"),
        };
        foreach (var fixture in fixtures)
        {
            _writer.UpsertFile(new FileRecord
            {
                Path = fixture.Path,
                Lang = fixture.Language,
                Size = 20,
                Lines = 2,
                Modified = modified,
                Checksum = fixture.Path,
            });
        }
        _writer.SetMeta(DbContext.GetSymbolExtractorVersionMetaKey("python"), "0");

        using (var command = _db.Connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE files SET size = NULL WHERE path = 'src/null-size.cs';
                UPDATE files SET size = 'not-an-integer' WHERE path = 'src/text-size.cs';
                UPDATE files SET modified = 123 WHERE path = 'src/integer-modified.cs';
                UPDATE files SET modified = 'not-a-timestamp' WHERE path = 'src/invalid-modified.cs';
                """;
            command.ExecuteNonQuery();
        }

        var observedPersistedCSharpPaths = new List<string>();
        var reusableStats = _writer.LoadReusableIndexedFileStats(
            maxSymbolsPerFile: 10,
            maxReferencesPerFile: 10,
            initialCapacity: fixtures.Length,
            persistedCSharpPathObserver: observedPersistedCSharpPaths.Add);

        var valid = Assert.Single(reusableStats);
        Assert.Equal("src/valid.cs", valid.Key);
        Assert.Equal(modified, valid.Value.ModifiedUtc);
        Assert.Equal(20, valid.Value.Size);
        Assert.Equal("csharp", valid.Value.Language);
        Assert.Equal(
            new PersistedIndexedFileSize(Exists: true, SizeKnown: true, 20),
            reusableStats.GetPersistedSize("src/stale.py"));
        Assert.Equal(
            new PersistedIndexedFileSize(Exists: true, SizeKnown: false, 0),
            reusableStats.GetPersistedSize("src/null-size.cs"));
        Assert.Equal(
            new PersistedIndexedFileSize(Exists: true, SizeKnown: false, 0),
            reusableStats.GetPersistedSize("src/text-size.cs"));
        Assert.Equal(
            new PersistedIndexedFileSize(Exists: true, SizeKnown: true, 20),
            reusableStats.GetPersistedSize("src/invalid-modified.cs"));
        Assert.Equal(
            fixtures
                .Where(fixture => fixture.Language == "csharp")
                .Select(fixture => fixture.Path)
                .Order(StringComparer.Ordinal),
            observedPersistedCSharpPaths.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LoadReusableIndexedFileStats_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            DbWriter.ReusableStatSnapshotReadForTesting = cancellation.Cancel;

            Assert.Throws<OperationCanceledException>(() =>
                _writer.LoadReusableIndexedFileStats(
                    maxSymbolsPerFile: 10,
                    maxReferencesPerFile: 10,
                    cancellation.Token));
        }
        finally
        {
            DbWriter.ReusableStatSnapshotReadForTesting = null;
        }
    }

    [Fact]
    public void LoadReusableIndexedFileStats_FilterPreparationCancellationCleansTempTable()
    {
        using var cancellation = new CancellationTokenSource();
        var previousBatchHook = DbWriter.ReusableStatSnapshotFilterBatchForTesting;
        try
        {
            _writer.UpsertFile(new FileRecord
            {
                Path = "src/filter-cancel.cs",
                Lang = "csharp",
                Size = 20,
                Lines = 1,
                Modified = DateTime.UtcNow,
                Checksum = "filter-cancel",
            });
            DbWriter.ReusableStatSnapshotFilterBatchForTesting = () =>
            {
                previousBatchHook?.Invoke();
                cancellation.Cancel();
            };

            Assert.Throws<OperationCanceledException>(() =>
                _writer.LoadReusableIndexedFileStats(
                    maxSymbolsPerFile: 10,
                    maxReferencesPerFile: 10,
                    cancellation.Token,
                    includedPaths: new HashSet<string>(["src/filter-cancel.cs"], StringComparer.Ordinal)));

            using var command = _db.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_temp_master WHERE type = 'table' AND name = 'reusable_stat_snapshot_filter'";
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.ReusableStatSnapshotFilterBatchForTesting = previousBatchHook;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChecksumReuse_RepairsIncompleteLegacySize(bool enforceExtractionLimits)
    {
        var modified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/null-size.cs",
            Lang = "csharp",
            Size = 20,
            Lines = 2,
            Modified = modified,
            Checksum = "null_size_checksum",
        });
        using (var command = _db.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE files SET size = NULL WHERE id = @id";
            command.Parameters.AddWithValue("@id", fileId);
            command.ExecuteNonQuery();
        }

        var reusedFileId = enforceExtractionLimits
            ? _writer.GetReusableUnchangedFileId(
                "src/null-size.cs",
                modified,
                "null_size_checksum",
                size: 20,
                lines: 2,
                language: "csharp",
                generated: false,
                maxSymbolsPerFile: 10,
                maxReferencesPerFile: 10,
                generatedExtractionSuppressed: false)
            : _writer.GetUnchangedFileId(
                "src/null-size.cs",
                modified,
                "null_size_checksum",
                size: 20,
                lines: 2,
                language: "csharp",
                generated: false);

        Assert.Equal(fileId, reusedFileId);
        using var verify = _db.Connection.CreateCommand();
        verify.CommandText = "SELECT size FROM files WHERE id = @id";
        verify.Parameters.AddWithValue("@id", fileId);
        Assert.Equal(20L, verify.ExecuteScalar());
    }

    [Fact]
    public void PurgeStaleFilesSharingChecksum_RemovesDeletedRenameRowsOnly()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_checksum_purge");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src/current.py"), "print('same')\n");
            File.WriteAllText(Path.Combine(projectRoot, "src/duplicate.py"), "print('same')\n");

            var modified = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc);
            var currentId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/current.py",
                Lang = "python",
                Size = 14,
                Lines = 1,
                Checksum = "same_checksum",
                Modified = modified,
            });
            var staleId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/renamed-away.py",
                Lang = "python",
                Size = 14,
                Lines = 1,
                Checksum = "same_checksum",
                Modified = modified,
            });
            var duplicateId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/duplicate.py",
                Lang = "python",
                Size = 14,
                Lines = 1,
                Checksum = "same_checksum",
                Modified = modified,
            });
            _writer.InsertChunks([
                new() { FileId = currentId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "current" },
                new() { FileId = staleId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "stale" },
                new() { FileId = duplicateId, ChunkIndex = 0, StartLine = 1, EndLine = 1, Content = "duplicate" },
            ]);
            _writer.InsertSymbols([
                new() { FileId = staleId, Kind = "function", Name = "removed_target", Line = 1 },
            ]);
            _writer.InsertReferences([
                new()
                {
                    FileId = currentId,
                    SymbolName = "removed_target",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = "removed_target()",
                },
            ]);

            var purged = _writer.PurgeStaleFilesSharingChecksum(projectRoot, "src/current.py", "same_checksum");

            Assert.Equal(1, purged);
            Assert.True(_writer.HasFileAtPath("src/current.py"));
            Assert.False(_writer.HasFileAtPath("src/renamed-away.py"));
            Assert.True(_writer.HasFileAtPath("src/duplicate.py"));
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT COUNT(*) FROM symbol_references";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
            cmd.CommandText = "SELECT COUNT(*) FROM hotspot_reference_counts";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void InsertChunks_InsertsAndPopulatesFts()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/test.py",
            Lang = "python",
            Size = 100,
            Lines = 10,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var chunks = new List<ChunkRecord>
        {
            new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 10, Content = "def authenticate(user):" },
        };
        _writer.InsertChunks(chunks);

        // Verify FTS search works / FTS検索が動作することを確認
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT content FROM fts_chunks WHERE fts_chunks MATCH 'authenticate'";
        var result = cmd.ExecuteScalar() as string;
        Assert.NotNull(result);
        Assert.Contains("authenticate", result);
    }

    [Fact]
    public void InsertChunks_MultiRowValuesPopulatesFtsForEveryRow()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/multi.py",
            Lang = "python",
            Size = 300,
            Lines = 300,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var chunks = Enumerable.Range(0, 4)
            .Select(i => new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = i,
                StartLine = i + 1,
                EndLine = i + 1,
                Content = $"def multirow_token_{i}(): pass",
            })
            .ToList();

        _writer.InsertChunks(chunks);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'multirow_token_3'";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertSymbols_InsertsCorrectly()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/svc.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var symbols = new List<SymbolRecord>
        {
            new() { FileId = fileId, Kind = "function", Name = "process", Line = 1 },
            new() { FileId = fileId, Kind = "class", Name = "Service", Line = 5 },
        };
        _writer.InsertSymbols(symbols);

        var (_, _, symbolCount, _) = _writer.GetCounts();
        Assert.Equal(2, symbolCount);
    }

    [Fact]
    public void HasCSharpStaticInterfaceContractSymbols_PreservesContractSelectionSemantics()
    {
        Assert.False(_writer.HasCSharpStaticInterfaceContractSymbols());

        var modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var typeScriptFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ITypeScript.ts",
            Lang = "typescript",
            Size = 50,
            Lines = 3,
            Modified = modified,
        });
        var csharpFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/IShape.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 6,
            Modified = modified,
        });
        _writer.InsertSymbols([
            new SymbolRecord
            {
                FileId = typeScriptFileId,
                Kind = "interface",
                Name = "ITypeScript",
                Line = 1,
                StartLine = 1,
                EndLine = 3,
            },
            new SymbolRecord
            {
                FileId = csharpFileId,
                Kind = "function",
                Name = "CreateInClass",
                Line = 3,
                StartLine = 3,
                EndLine = 3,
                ContainerKind = "class",
                Signature = "public static abstract IShape CreateInClass();",
            },
        ]);

        Assert.False(_writer.HasCSharpStaticInterfaceContractSymbols());

        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = csharpFileId,
            Kind = "function",
            Name = "Create",
            Line = 4,
            StartLine = 4,
            EndLine = 4,
            ContainerKind = "interface",
            Signature = "public static abstract IShape Create();",
        }]);
        Assert.True(_writer.HasCSharpStaticInterfaceContractSymbols());

        _writer.DeleteFileData(csharpFileId);
        Assert.False(_writer.HasCSharpStaticInterfaceContractSymbols());

        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = csharpFileId,
            Kind = "interface",
            Name = "IShape",
            Line = 1,
            StartLine = 1,
            EndLine = 6,
            Signature = "public interface IShape",
        }]);
        Assert.True(_writer.HasCSharpStaticInterfaceContractSymbols());
    }

    [Fact]
    public void HasCSharpStaticInterfaceContractSymbols_CancellationInterruptsRunningSql()
    {
        var writer = new DbWriter(_db);
        var fileId = writer.UpsertFile(new FileRecord
        {
            Path = "src/ICancellable.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        writer.InsertSymbols(
            Enumerable.Range(0, 256)
                .Select(index => new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = $"Create{index}",
                    Line = index + 1,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    ContainerKind = "interface",
                    Signature = $"public static abstract ICancellable Create{index}();",
                })
                .ToList());

        using var cancellation = new CancellationTokenSource();
        var likeCalls = 0;
        var allowMatch = false;
        _db.Connection.CreateFunction<string?, string?, long>(
            "like",
            (_, _) =>
            {
                likeCalls++;
                if (!cancellation.IsCancellationRequested)
                    cancellation.Cancel();
                return allowMatch ? 1 : 0;
            });

        var exception = Assert.Throws<OperationCanceledException>(
            () => writer.HasCSharpStaticInterfaceContractSymbols(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.IsType<SqliteException>(exception.InnerException);
        Assert.True(likeCalls > 0);

        allowMatch = true;
        var hitsBeforeRetry = _db.PreparedCommands.HitCount;
        Assert.True(writer.HasCSharpStaticInterfaceContractSymbols());
        Assert.True(_db.PreparedCommands.HitCount > hitsBeforeRetry);
    }

    [Fact]
    public void RunInReadSnapshot_CancellationInterruptsRunningSql_Issue4544()
    {
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/resource-snapshot-cancellation.txt",
            Lang = "text",
            Size = 1,
            Lines = 1,
            Checksum = "snapshot-cancellation",
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        using var cancellation = new CancellationTokenSource();
        var callbackCount = 0;
        _db.Connection.CreateFunction<long>(
            "cancel_resource_snapshot",
            () =>
            {
                callbackCount++;
                cancellation.Cancel();
                return 1;
            });

        using var reader = new DbReader(_db, cancellation.Token);
        var exception = Assert.Throws<OperationCanceledException>(() => reader.RunInReadSnapshot(() =>
        {
            using var command = _db.Connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE sequence(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM sequence WHERE value < 1000
                )
                SELECT SUM(cancel_resource_snapshot()) FROM sequence
                """;
            return command.ExecuteScalar();
        }));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);
        Assert.True(callbackCount > 0);

        using var retry = _db.Connection.CreateCommand();
        retry.CommandText = "SELECT COUNT(*) FROM files";
        Assert.Equal(1L, retry.ExecuteScalar());
    }

    [Fact]
    public void ReferenceBatchStatements_EightFiveOneInputsUseExactRowCounts()
    {
        var writer = new DbWriter(_db);
        var statements = new List<DbWriter.DbWriterBatchStatement>();
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        var fileIds = new List<long>();
        try
        {
            DbWriter.BatchStatementExecutingForTesting = statement =>
            {
                statements.Add(statement);
                previousStatementHook?.Invoke(statement);
            };

            foreach (var rowCount in new[] { 8, 5, 1 })
            {
                var fileId = writer.UpsertFile(new FileRecord
                {
                    Path = $"src/reference-batch-{rowCount}.cs",
                    Lang = "csharp",
                    Size = rowCount * 10,
                    Lines = rowCount,
                    Checksum = $"reference-batch-{rowCount}",
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                fileIds.Add(fileId);
                writer.InsertReferencesForNewFiles(
                    Enumerable.Range(0, rowCount)
                        .Select(index => new ReferenceRecord
                        {
                            FileId = fileId,
                            SymbolName = $"Target_{rowCount}_{index}",
                            ReferenceKind = "call",
                            Line = index + 1,
                            Column = index + 1,
                            Context = $"Target_{rowCount}_{index}();",
                            ContainerKind = "function",
                            ContainerName = "caller",
                        })
                        .ToArray(),
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
        }
        finally
        {
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
        }

        Assert.Equal(
            [(8, 8), (5, 5), (1, 1)],
            statements.Where(statement => statement.Operation == "insert_reference_lines")
                .Select(statement => (statement.ActiveRows, statement.StatementRows))
                .ToArray());
        Assert.Equal(
            [(8, 8), (5, 5), (1, 1)],
            statements.Where(statement => statement.Operation == "insert_references")
                .Select(statement => (statement.ActiveRows, statement.StatementRows))
                .ToArray());
        using var countCommand = _db.Connection.CreateCommand();
        countCommand.Parameters.Add("@fileId", SqliteType.Integer);
        foreach (var (fileId, expectedRowCount) in fileIds.Zip(new[] { 8, 5, 1 }))
        {
            countCommand.Parameters["@fileId"].Value = fileId;
            countCommand.CommandText = """
                SELECT (SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId),
                       (SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId)
                """;
            using var reader = countCommand.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(expectedRowCount, reader.GetInt32(0));
            Assert.Equal(expectedRowCount, reader.GetInt32(1));
        }
    }

    [Fact]
    public void ReferenceLineLookup_BatchedInputUsesUniqueAutoIndexPlan()
    {
        const int StatementRowCount = 5;
        var sql = DbWriter.BuildReferenceLineLookupSqlForTesting(StatementRowCount);

        using var command = _db.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        for (var parameterIndex = 0; parameterIndex < StatementRowCount * 3; parameterIndex++)
            command.Parameters.AddWithValue($"@p{parameterIndex}", DBNull.Value);

        var plan = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            plan.Add(reader.GetString(3));

        Assert.Contains(
            plan,
            detail => detail.Contains(
                "sqlite_autoindex_reference_lines_1",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InsertSymbols_ChunksLargeInputUnderSqlVariableLimit()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/symbols.py",
            Lang = "python",
            Size = 1000,
            Lines = 1000,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var symbols = Enumerable.Range(0, 120)
            .Select(i => new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = $"fn_{i}",
                Line = i + 1,
                StartLine = i + 1,
                EndLine = i + 1,
            })
            .ToList();

        _writer.InsertSymbols(symbols);

        var (_, _, symbolCount, _) = _writer.GetCounts();
        Assert.Equal(120, symbolCount);
    }

    [Fact]
    public void InsertSymbols_BatchFailureSkipsOnlyBadRow()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/symbols_with_bad_row.py",
            Lang = "python",
            Size = 1000,
            Lines = 1000,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var warnings = new List<string>();
        DbWriter.BatchRowSkipWarningForTesting = warnings.Add;
        try
        {
            var symbols = Enumerable.Range(0, 100)
                .Select(i => new SymbolRecord
                {
                    FileId = i == 50 ? -1 : fileId,
                    Kind = "function",
                    Name = $"fn_with_bad_row_{i}",
                    Line = i + 1,
                    StartLine = i + 1,
                    EndLine = i + 1,
                })
                .ToList();

            _writer.InsertSymbols(symbols);
        }
        finally
        {
            DbWriter.BatchRowSkipWarningForTesting = null;
        }

        var (_, _, symbolCount, _) = _writer.GetCounts();
        Assert.Equal(99, symbolCount);
        Assert.Equal(1, _writer.BatchRowsSkipped);
        var warning = Assert.Single(warnings);
        Assert.Contains("file_id=-1", warning, StringComparison.Ordinal);
        Assert.Contains("fn_with_bad_row_50", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchRowSkipWarning_TruncatesOversizedDiagnostics_Issue3094()
    {
        var rowValue = new string('r', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);
        var batchValue = new string('b', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);
        var rowErrorValue = new string('e', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var warning = DbWriter.BuildBatchRowSkipWarningForTesting(
            $"symbol file_id=1 name={rowValue} line=42",
            new InvalidOperationException(batchValue),
            new InvalidOperationException(rowErrorValue));

        Assert.Contains("Warning: skipped failed batch row", warning, StringComparison.Ordinal);
        Assert.Contains("batch_error=", warning, StringComparison.Ordinal);
        Assert.Contains("row_error=", warning, StringComparison.Ordinal);
        Assert.Contains("<truncated; original length", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(rowValue, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(batchValue, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(rowErrorValue, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchRowSkipWarning_RedactsExceptionMessages_Issue4124()
    {
        var warning = DbWriter.BuildBatchRowSkipWarningForTesting(
            "symbol file_id=1 name=ok line=42",
            new InvalidOperationException("batch failed at /tmp/private/repo --token=ghp_abcdefghijklmnopqrstuvwxyz"),
            new InvalidOperationException("row failed at C:/Users/me/private.db password=hunter2"));

        Assert.Contains("batch_error=", warning, StringComparison.Ordinal);
        Assert.Contains("row_error=", warning, StringComparison.Ordinal);
        Assert.Contains("<path>", warning, StringComparison.Ordinal);
        Assert.Contains("--token=<redacted>", warning, StringComparison.Ordinal);
        Assert.Contains("password=<redacted>", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/private", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("C:/Users/me", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteFileData_RemovesChunksAndSymbols()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/del.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertChunks([new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 5, Content = "test" }]);
        _writer.InsertSymbols([new() { FileId = fileId, Kind = "function", Name = "test", Line = 1 }]);

        _writer.DeleteFileData(fileId);

        var (_, chunkCount, symbolCount, referenceCount) = _writer.GetCounts();
        Assert.Equal(0, chunkCount);
        Assert.Equal(0, symbolCount);
        Assert.Equal(0, referenceCount);
    }

    [Fact]
    public void InsertReferences_InsertsCorrectly()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ref.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 12, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
        ]);

        var (_, _, _, referenceCount) = _writer.GetCounts();
        Assert.Equal(1, referenceCount);
    }

    [Fact]
    public void InsertReferences_ChunksLargeInputAndDeduplicatesReferenceLines()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/refs.py",
            Lang = "python",
            Size = 1000,
            Lines = 1000,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var references = Enumerable.Range(0, 120)
            .Select(i => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"callee_{i}",
                ReferenceKind = "call",
                Line = i % 10 + 1,
                Column = 4,
                Context = $"line_{i % 10}()",
                ContainerKind = "function",
                ContainerName = "caller",
            })
            .ToList();

        _writer.InsertReferences(references);

        var (_, _, _, referenceCount) = _writer.GetCounts();
        Assert.Equal(120, referenceCount);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines";
        Assert.Equal(10L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertReferencesForNewFiles_InsertsReturningReferenceLines()
    {
        var fileId = _writer.InsertNewFile(new FileRecord
        {
            Path = "src/new_refs.py",
            Lang = "python",
            Size = 1000,
            Lines = 1000,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var references = Enumerable.Range(0, 120)
            .Select(i => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"callee_{i}",
                ReferenceKind = "call",
                Line = i % 10 + 1,
                Column = 4,
                Context = $"line_{i % 10}()",
                ContainerKind = "function",
                ContainerName = "caller",
            })
            .ToList();

        _writer.InsertReferencesForNewFiles(references, refreshMutualRecursionFlags: false, CancellationToken.None);

        var (_, _, _, referenceCount) = _writer.GetCounts();
        Assert.Equal(120, referenceCount);
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines";
        Assert.Equal(10L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_LinksMergedInterfacesOnly()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_aug");
        var previousAtomicHook = DbWriter.AtomicFileReferenceInsertForTesting;
        var previousAggregateRefreshHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var atomicCalls = new List<bool>();
        var aggregateRefreshStatements = 0;
        try
        {
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
            TestProjectHelper.CreateDirectory(projectRoot, "src");
            TestProjectHelper.WriteTextFile(projectRoot, "src/module-c.ts", "export {}\ninterface Ambient {}\n");
            TestProjectHelper.WriteTextFile(projectRoot, "src/module-d.ts", "import \"./setup\";\ninterface Ambient {}\n");
            TestProjectHelper.WriteTextFile(projectRoot, "src/express-a.ts", "declare module \"express\" { interface Request { user: string } }\n");
            TestProjectHelper.WriteTextFile(projectRoot, "src/express-b.ts", "declare module \"express\" { interface Request { account: string } }\n");

            var firstFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/a.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 4,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var secondFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/b.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 4,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var thirdFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/c.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 4,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var moduleOneFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/module-a.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 4,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var moduleTwoFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/module-b.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 4,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var moduleMarkerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/module-c.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 2,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var sideEffectImportFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/module-d.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 2,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var ambientGlobalFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/ambient-global.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var ambientModuleFirstFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/express-a.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var ambientModuleSecondFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/express-b.ts",
                Lang = "typescript",
                Size = 80,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            _writer.InsertSymbols([
                new SymbolRecord { FileId = firstFileId, Kind = "interface", Name = "Widget", Line = 1, StartLine = 1, StartColumn = 7, EndLine = 3, Signature = "interface Widget { a: number }" },
                new SymbolRecord { FileId = secondFileId, Kind = "interface", Name = "Widget", Line = 1, StartLine = 1, StartColumn = 17, EndLine = 3, Signature = "declare global { interface Widget { b: string } }" },
                new SymbolRecord { FileId = firstFileId, Kind = "import", Name = "Options", Line = 4, StartLine = 4, StartColumn = 5, EndLine = 4, Signature = "type Options = { a: number }" },
                new SymbolRecord { FileId = secondFileId, Kind = "import", Name = "Options", Line = 4, StartLine = 4, StartColumn = 5, EndLine = 4, Signature = "type Options = { b: string }" },
                new SymbolRecord { FileId = thirdFileId, Kind = "interface", Name = "LocalOnly", Line = 1, StartLine = 1, StartColumn = 11, EndLine = 1, Signature = "interface LocalOnly {}" },
                new SymbolRecord { FileId = moduleOneFileId, Kind = "interface", Name = "Props", Line = 2, StartLine = 2, StartColumn = 17, EndLine = 2, Signature = "export interface Props { a: number }", Visibility = "export" },
                new SymbolRecord { FileId = moduleTwoFileId, Kind = "interface", Name = "Props", Line = 2, StartLine = 2, StartColumn = 17, EndLine = 2, Signature = "export interface Props { b: string }", Visibility = "export" },
                new SymbolRecord { FileId = moduleMarkerFileId, Kind = "interface", Name = "Ambient", Line = 2, StartLine = 2, StartColumn = 11, EndLine = 2, Signature = "interface Ambient {}" },
                new SymbolRecord { FileId = sideEffectImportFileId, Kind = "interface", Name = "Ambient", Line = 2, StartLine = 2, StartColumn = 11, EndLine = 2, Signature = "interface Ambient {}" },
                new SymbolRecord { FileId = ambientGlobalFileId, Kind = "interface", Name = "Ambient", Line = 1, StartLine = 1, StartColumn = 11, EndLine = 1, Signature = "interface Ambient {}" },
                new SymbolRecord { FileId = ambientModuleFirstFileId, Kind = "interface", Name = "Request", Line = 1, StartLine = 1, StartColumn = 28, EndLine = 1, Signature = "interface Request { user: string }", ContainerName = "\"express\"" },
                new SymbolRecord { FileId = ambientModuleSecondFileId, Kind = "interface", Name = "Request", Line = 1, StartLine = 1, StartColumn = 28, EndLine = 1, Signature = "interface Request { account: string }", ContainerName = "\"express\"" },
            ]);

            var inserted = _writer.RebuildTypeScriptAugmentationReferences(projectRoot);

            Assert.Equal(4, inserted);
            Assert.Contains(false, atomicCalls);
            Assert.Equal(1, aggregateRefreshStatements);
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = @"
            SELECT symbol_name, container_kind, COUNT(*)
            FROM symbol_references
            WHERE reference_kind = 'augmentation'
            GROUP BY symbol_name, container_kind
            ORDER BY symbol_name, container_kind";

            using (var reader = cmd.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("Request", reader.GetString(0));
                Assert.Equal("interface", reader.GetString(1));
                Assert.Equal(2, reader.GetInt32(2));
                Assert.True(reader.Read());
                Assert.Equal("Widget", reader.GetString(0));
                Assert.Equal("interface", reader.GetString(1));
                Assert.Equal(2, reader.GetInt32(2));
                Assert.False(reader.Read());
            }

            Assert.Equal(4, _writer.RebuildTypeScriptAugmentationReferences(projectRoot));
            Assert.Equal(2, aggregateRefreshStatements);
            cmd.CommandText = "SELECT SUM(reference_count) FROM hotspot_reference_counts WHERE lang = 'typescript'";
            Assert.Equal(4L, (long)Assert.IsType<long>(cmd.ExecuteScalar()));
        }
        finally
        {
            DbWriter.AtomicFileReferenceInsertForTesting = previousAtomicHook;
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousAggregateRefreshHook;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_MaterializesOnlyMergedGroups()
    {
        const int singletonInterfaceCount = 5_000;
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/augmentation-groups.ts",
            Lang = "typescript",
            Size = singletonInterfaceCount * 32,
            Lines = singletonInterfaceCount + 2,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var symbols = new List<SymbolRecord>(singletonInterfaceCount + 2);
        for (var index = 0; index < singletonInterfaceCount; index++)
        {
            var name = $"UniqueInterface{index:D4}";
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "interface",
                Name = name,
                Line = index + 1,
                StartLine = index + 1,
                EndLine = index + 1,
                Signature = $"interface {name} {{ value: number }}",
            });
        }
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "interface",
            Name = "MergedInterface",
            Line = singletonInterfaceCount + 1,
            StartLine = singletonInterfaceCount + 1,
            EndLine = singletonInterfaceCount + 1,
            Signature = "interface MergedInterface { first: number }",
        });
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "interface",
            Name = "MergedInterface",
            Line = singletonInterfaceCount + 2,
            StartLine = singletonInterfaceCount + 2,
            EndLine = singletonInterfaceCount + 2,
            Signature = "interface MergedInterface { second: string }",
        });
        _writer.InsertSymbols(symbols);

        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        try
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };

            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences());

            Assert.NotNull(groupingStats);
            Assert.Equal(singletonInterfaceCount + 2, groupingStats!.DeclarationCount);
            Assert.Equal(singletonInterfaceCount + 1, groupingStats.GroupCount);
            Assert.Equal(1, groupingStats.MergedGroupCount);
            Assert.Equal(2, groupingStats.MaterializedDeclarationIndexCount);
            using var count = _db.Connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
            Assert.Equal(2L, (long)count.ExecuteScalar()!);

            groupingStats = null;
            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences(".", ["MergedInterface"]));
            Assert.NotNull(groupingStats);
            Assert.Equal(2, groupingStats!.DeclarationCount);
            Assert.Equal(1, groupingStats.GroupCount);
            Assert.Equal(1, groupingStats.MergedGroupCount);
            Assert.Equal(2, groupingStats.MaterializedDeclarationIndexCount);
            Assert.Equal(1, groupingStats.ScopedNameCount);

            groupingStats = null;
            var batchedDirtyNames = Enumerable.Range(0, 1_000)
                .Select(static index => $"MissingInterface{index:D4}")
                .Append("MergedInterface")
                .ToArray();
            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences(".", batchedDirtyNames));
            Assert.NotNull(groupingStats);
            Assert.Equal(2, groupingStats!.DeclarationCount);
            Assert.Equal(batchedDirtyNames.Length, groupingStats.ScopedNameCount);

            groupingStats = null;
            var broadDirtyNames = Enumerable.Range(0, singletonInterfaceCount)
                .Select(static index => $"UniqueInterface{index:D4}")
                .Append("MergedInterface")
                .ToArray();
            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences(".", broadDirtyNames));
            Assert.NotNull(groupingStats);
            Assert.Equal(singletonInterfaceCount + 2, groupingStats!.DeclarationCount);
            Assert.Null(groupingStats.ScopedNameCount);
        }
        finally
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
        }
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_ScopesOldAndNewDirtyNames()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_aug_dirty_names");
        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        try
        {
            var firstFileId = _writer.UpsertFile(CreateTypeScriptFile("src/first.ts"));
            var secondFileId = _writer.UpsertFile(CreateTypeScriptFile("src/second.ts"));
            var thirdFileId = _writer.UpsertFile(CreateTypeScriptFile("src/third.ts"));
            _writer.InsertSymbols([
                CreateInterface(firstFileId, "OldMerge", 1),
                CreateInterface(firstFileId, "RemovedOnly", 2),
                CreateInterface(secondFileId, "OldMerge", 1),
                CreateInterface(secondFileId, "NewMerge", 2),
                CreateInterface(secondFileId, "UntouchedMerge", 3),
                CreateInterface(thirdFileId, "UntouchedMerge", 1),
            ]);
            Assert.Equal(4, _writer.RebuildTypeScriptAugmentationReferences(projectRoot));

            using var dirtyNames = _writer.BeginTypeScriptAugmentationDirtyNameTracking();
            var retainedFirstFileId = _writer.UpsertFile(CreateTypeScriptFile("src/first.ts"));
            Assert.Equal(firstFileId, retainedFirstFileId);
            _writer.InsertSymbols([CreateInterface(firstFileId, "NewMerge", 1)]);
            Assert.Equal(
                ["NewMerge", "OldMerge", "RemovedOnly"],
                dirtyNames.DirtyNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray());

            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };
            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences(projectRoot, dirtyNames.DirtyNames));

            Assert.NotNull(groupingStats);
            Assert.Equal(3, groupingStats!.DeclarationCount);
            Assert.Equal(2, groupingStats.GroupCount);
            Assert.Equal(1, groupingStats.MergedGroupCount);
            Assert.Equal(2, groupingStats.MaterializedDeclarationIndexCount);
            Assert.Equal(3, groupingStats.ScopedNameCount);

            using var references = _db.Connection.CreateCommand();
            references.CommandText = """
                SELECT symbol_name, COUNT(*)
                FROM symbol_references
                WHERE reference_kind = 'augmentation'
                GROUP BY symbol_name
                ORDER BY symbol_name
                """;
            using var reader = references.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("NewMerge", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.True(reader.Read());
            Assert.Equal("UntouchedMerge", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.False(reader.Read());
        }
        finally
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static FileRecord CreateTypeScriptFile(string path) => new()
        {
            Path = path,
            Lang = "typescript",
            Size = 80,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        static SymbolRecord CreateInterface(long fileId, string name, int line) => new()
        {
            FileId = fileId,
            Kind = "interface",
            Name = name,
            Line = line,
            StartLine = line,
            EndLine = line,
            Signature = $"interface {name} {{ value: number }}",
        };
    }

    [Fact]
    public void TypeScriptAugmentationDirtyNameTracking_CapturesBatchedStaleFilePurge()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_aug_stale_purge");
        try
        {
            var staleFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/stale.ts",
                Lang = "typescript",
                Checksum = "rename-checksum",
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            var peerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/peer.ts",
                Lang = "typescript",
                Checksum = "peer-checksum",
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.InsertSymbols([
                new SymbolRecord { FileId = staleFileId, Kind = "interface", Name = "RenamedMerge", Line = 1, StartLine = 1, EndLine = 1, Signature = "interface RenamedMerge { stale: number }" },
                new SymbolRecord { FileId = peerFileId, Kind = "interface", Name = "RenamedMerge", Line = 1, StartLine = 1, EndLine = 1, Signature = "interface RenamedMerge { peer: number }" },
            ]);
            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences(projectRoot));

            using var dirtyNames = _writer.BeginTypeScriptAugmentationDirtyNameTracking();
            Assert.Equal(
                1,
                _writer.PurgeStaleFilesSharingChecksum(
                    projectRoot,
                    "src/renamed.cs",
                    "rename-checksum"));

            Assert.True(dirtyNames.RequiresRefresh);
            Assert.Equal(["RenamedMerge"], dirtyNames.DirtyNames);
            Assert.False(_writer.TypeScriptAugmentationVersionMatchesCurrent());
            Assert.Equal(
                0,
                _writer.RebuildTypeScriptAugmentationReferences(projectRoot, dirtyNames.DirtyNames));
            using var count = _db.Connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_ScopedNamesUseOtherIndexedInterfaceAsModuleMarker()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_aug_scoped_module_marker");
        var previousGroupingHook = DbWriter.TypeScriptAugmentationGroupingForTesting;
        DbWriter.TypeScriptAugmentationGroupingStats? groupingStats = null;
        try
        {
            var globalFileId = _writer.UpsertFile(CreateTypeScriptFile("src/global.ts"));
            var moduleFileId = _writer.UpsertFile(CreateTypeScriptFile("src/module.ts"));
            _writer.InsertSymbols([
                CreateInterface(globalFileId, "Shared", "interface Shared { global: number }"),
                CreateInterface(moduleFileId, "Shared", "interface Shared { local: number }"),
                CreateInterface(moduleFileId, "ModuleMarker", "export interface ModuleMarker { value: number }", "export"),
            ]);

            Assert.Equal(0, _writer.RebuildTypeScriptAugmentationReferences(projectRoot));
            DbWriter.TypeScriptAugmentationGroupingForTesting = stats =>
            {
                groupingStats = stats;
                previousGroupingHook?.Invoke(stats);
            };

            Assert.Equal(0, _writer.RebuildTypeScriptAugmentationReferences(projectRoot, ["Shared"]));
            Assert.NotNull(groupingStats);
            Assert.Equal(2, groupingStats!.DeclarationCount);
            Assert.Equal(2, groupingStats.GroupCount);
            Assert.Equal(0, groupingStats.MergedGroupCount);
            Assert.Equal(1, groupingStats.ScopedNameCount);

            using var count = _db.Connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.TypeScriptAugmentationGroupingForTesting = previousGroupingHook;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }

        static FileRecord CreateTypeScriptFile(string path) => new()
        {
            Path = path,
            Lang = "typescript",
            Size = 80,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        static SymbolRecord CreateInterface(long fileId, string name, string signature, string? visibility = null) => new()
        {
            FileId = fileId,
            Kind = "interface",
            Name = name,
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = signature,
            Visibility = visibility,
        };
    }

    [Fact]
    public void TypeScriptAugmentationDirtyNameTracking_ClearsOnceUntilRollbackAndRechecksOnceForNewFiles()
    {
        const int fileCount = 32;
        var previousClearHook = DbWriter.TypeScriptAugmentationReadyClearForTesting;
        var previousCheckHook = DbWriter.TypeScriptAugmentationReadyCheckForTesting;
        var clearCount = 0;
        var readyCheckCount = 0;
        try
        {
            var files = Enumerable.Range(0, fileCount)
                .Select(static index => new FileRecord
                {
                    Path = $"src/dirty-{index:D3}.ts",
                    Lang = "typescript",
                    Size = 80,
                    Lines = 4,
                    Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                })
                .ToArray();
            foreach (var (file, index) in files.Select(static (file, index) => (file, index)))
            {
                var fileId = _writer.UpsertFile(file);
                _writer.InsertSymbols([new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "interface",
                    Name = $"DirtyInterface{index:D3}",
                    Line = 1,
                    StartLine = 1,
                    EndLine = 1,
                    Signature = $"interface DirtyInterface{index:D3} {{ value: number }}",
                }]);
            }
            Assert.Equal(0, _writer.RebuildTypeScriptAugmentationReferences("."));
            Assert.True(_writer.TypeScriptAugmentationVersionMatchesCurrent());

            DbWriter.TypeScriptAugmentationReadyClearForTesting = () =>
            {
                clearCount++;
                previousClearHook?.Invoke();
            };
            DbWriter.TypeScriptAugmentationReadyCheckForTesting = () =>
            {
                readyCheckCount++;
                previousCheckHook?.Invoke();
            };
            using var dirtyNames = _writer.BeginTypeScriptAugmentationDirtyNameTracking();
            using (_writer.BeginTransaction())
                _writer.UpsertFile(files[0]);

            Assert.Equal(1, clearCount);
            Assert.True(_writer.TypeScriptAugmentationVersionMatchesCurrent());

            using (var transaction = _writer.BeginTransaction())
            {
                for (var index = 0; index < fileCount; index++)
                {
                    var newFileId = _writer.UpsertFile(new FileRecord
                    {
                        Path = $"src/new-after-rollback-{index:D3}.ts",
                        Lang = "typescript",
                        Size = 80,
                        Lines = 4,
                        Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    });
                    _writer.InsertSymbols([new SymbolRecord
                    {
                        FileId = newFileId,
                        Kind = "interface",
                        Name = $"NewAfterRollback{index:D3}",
                        Line = 1,
                        StartLine = 1,
                        EndLine = 1,
                        Signature = $"interface NewAfterRollback{index:D3} {{ value: number }}",
                    }]);
                }
                Assert.Equal(2, clearCount);
                Assert.Equal(1, readyCheckCount);
                transaction.Commit();
            }

            Assert.Equal(2, clearCount);
            Assert.Equal(1, readyCheckCount);
            Assert.False(_writer.TypeScriptAugmentationVersionMatchesCurrent());
            Assert.True(dirtyNames.RequiresRefresh);
            Assert.Equal(fileCount + 1, dirtyNames.DirtyNames.Count);
        }
        finally
        {
            DbWriter.TypeScriptAugmentationReadyClearForTesting = previousClearHook;
            DbWriter.TypeScriptAugmentationReadyCheckForTesting = previousCheckHook;
        }
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_CancellationBetweenNameBatchesRollsBack()
    {
        var previousBatchHook = DbWriter.TypeScriptAugmentationNameBatchForTesting;
        using var cancellation = new CancellationTokenSource();
        try
        {
            var firstFileId = _writer.UpsertFile(CreateTypeScriptFile("src/cancel-first.ts"));
            var secondFileId = _writer.UpsertFile(CreateTypeScriptFile("src/cancel-second.ts"));
            _writer.InsertSymbols([
                CreateInterface(firstFileId, "MergedInterface"),
                CreateInterface(secondFileId, "MergedInterface"),
            ]);
            Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences("."));
            Assert.True(_writer.TypeScriptAugmentationVersionMatchesCurrent());

            DbWriter.TypeScriptAugmentationNameBatchForTesting = batchNumber =>
            {
                previousBatchHook?.Invoke(batchNumber);
                if (batchNumber == 1)
                    cancellation.Cancel();
            };
            var dirtyNames = Enumerable.Range(0, 1_000)
                .Select(static index => $"ZMissingInterface{index:D4}")
                .Prepend("MergedInterface")
                .ToArray();

            var exception = Assert.Throws<OperationCanceledException>(() =>
                _writer.RebuildTypeScriptAugmentationReferences(".", dirtyNames, cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);

            using var count = _db.Connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
            Assert.Equal(2L, (long)count.ExecuteScalar()!);
            Assert.True(_writer.TypeScriptAugmentationVersionMatchesCurrent());
        }
        finally
        {
            DbWriter.TypeScriptAugmentationNameBatchForTesting = previousBatchHook;
        }

        static FileRecord CreateTypeScriptFile(string path) => new()
        {
            Path = path,
            Lang = "typescript",
            Size = 80,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        static SymbolRecord CreateInterface(long fileId, string name) => new()
        {
            FileId = fileId,
            Kind = "interface",
            Name = name,
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = $"interface {name} {{ value: number }}",
        };
    }

    [Fact]
    public void RebuildTypeScriptAugmentationReferences_CancellationInterruptsActiveDeleteStatementAndRollsBack()
    {
        using var cancellation = new CancellationTokenSource();
        var firstFileId = _writer.UpsertFile(CreateTypeScriptFile("src/interrupt-first.ts"));
        var secondFileId = _writer.UpsertFile(CreateTypeScriptFile("src/interrupt-second.ts"));
        _writer.InsertSymbols([
            CreateInterface(firstFileId, "InterruptedMerge"),
            CreateInterface(secondFileId, "InterruptedMerge"),
        ]);
        Assert.Equal(2, _writer.RebuildTypeScriptAugmentationReferences("."));
        _db.Connection.CreateFunction("cancel_ts_augmentation", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        using (var trigger = _db.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TEMP TRIGGER cancel_ts_augmentation_delete
                BEFORE DELETE ON symbol_references
                WHEN OLD.reference_kind = 'augmentation'
                BEGIN
                    SELECT cancel_ts_augmentation();
                END
                """;
            trigger.ExecuteNonQuery();
        }

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _writer.RebuildTypeScriptAugmentationReferences(
                ".",
                ["InterruptedMerge"],
                cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);

        using var count = _db.Connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_kind = 'augmentation'";
        Assert.Equal(2L, (long)count.ExecuteScalar()!);
        Assert.True(_writer.TypeScriptAugmentationVersionMatchesCurrent());

        static FileRecord CreateTypeScriptFile(string path) => new()
        {
            Path = path,
            Lang = "typescript",
            Size = 80,
            Lines = 4,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        static SymbolRecord CreateInterface(long fileId, string name) => new()
        {
            FileId = fileId,
            Kind = "interface",
            Name = name,
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = $"interface {name} {{ value: number }}",
        };
    }

    [Fact]
    public void TypeScriptAugmentationDirtyNameTracking_MixedSymbolBatchTracksOnlyTypeScriptInterfaces()
    {
        using var dirtyNames = _writer.BeginTypeScriptAugmentationDirtyNameTracking();
        var csharpFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/mixed.cs",
            Lang = "csharp",
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        var typeScriptFileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/mixed.ts",
            Lang = "typescript",
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertSymbols([
            new SymbolRecord { FileId = csharpFileId, Kind = "interface", Name = "CSharpInterface", Line = 1 },
            new SymbolRecord { FileId = typeScriptFileId, Kind = "class", Name = "TypeScriptClass", Line = 1 },
            new SymbolRecord { FileId = typeScriptFileId, Kind = "interface", Name = "TypeScriptInterface", Line = 2 },
        ]);

        Assert.True(dirtyNames.RequiresRefresh);
        Assert.Equal(["TypeScriptInterface"], dirtyNames.DirtyNames);
    }

    [Fact]
    public void TypeScriptAugmentationDirtyNameTracking_ReadinessOnlyModeSkipsInterfaceNames()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/readiness-only.ts",
            Lang = "typescript",
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([new SymbolRecord
        {
            FileId = fileId,
            Kind = "interface",
            Name = "ReadinessOnlyInterface",
            Line = 1,
            StartLine = 1,
            EndLine = 1,
            Signature = "interface ReadinessOnlyInterface { value: number }",
        }]);
        Assert.Equal(0, _writer.RebuildTypeScriptAugmentationReferences("."));

        using var readiness = _writer.BeginTypeScriptAugmentationDirtyNameTracking(collectDirtyNames: false);
        using (var transaction = _writer.BeginTransaction())
        {
            _writer.UpsertFile(new FileRecord
            {
                Path = "src/readiness-only.ts",
                Lang = "csharp",
                Modified = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            transaction.Commit();
        }

        Assert.True(readiness.RequiresRefresh);
        Assert.Empty(readiness.DirtyNames);
        Assert.False(_writer.TypeScriptAugmentationVersionMatchesCurrent());
    }

    [Fact]
    public void TypeScriptFileHasModuleSyntaxForTests_UsesBoundedFallbackRead_Issue3179()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ts_module_fallback");
        try
        {
            var normalFile = TestProjectHelper.WriteTextFile(projectRoot, "normal.ts", "// comment\nexport {}\n");
            var oversizedFile = TestProjectHelper.WriteTextFile(
                projectRoot,
                "oversized.ts",
                "export {}\n" + new string('x', (int)FileIndexer.DefaultMaxFileSizeBytes));

            var lateMarkerBuilder = new StringBuilder();
            for (var i = 0; i < 17000; i++)
                lateMarkerBuilder.Append("// filler\n");
            lateMarkerBuilder.Append("export {}\n");
            var lateMarkerFile = TestProjectHelper.WriteTextFile(projectRoot, "late-marker.ts", lateMarkerBuilder.ToString());

            Assert.True(DbWriter.TypeScriptFileHasModuleSyntaxForTests(normalFile));
            Assert.False(DbWriter.TypeScriptFileHasModuleSyntaxForTests(oversizedFile));
            Assert.False(DbWriter.TypeScriptFileHasModuleSyntaxForTests(lateMarkerFile));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static HashSet<string> ReadIndexNames(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name=@tableName";
        cmd.Parameters.AddWithValue("@tableName", tableName);

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            indexes.Add(reader.GetString(0));
        return indexes;
    }

    private static IReadOnlyList<string> ReadQueryPlanDetails(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var parameter in parameters)
            cmd.Parameters.AddWithValue(parameter.Name, parameter.Value);

        var details = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return details;
    }

    private static void AssertIndexColumns(SqliteConnection connection, string indexName, IReadOnlyList<(string Name, string Collation)> expected)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA index_xinfo('{indexName.Replace("'", "''")}')";

        var actual = new List<(string Name, string Collation)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var isKey = reader.GetInt32(5) == 1;
            if (!isKey)
                continue;
            actual.Add((reader.GetString(2), reader.GetString(4)));
        }

        Assert.Equal(expected, actual);
    }

    private static void AssertIndexSqlContains(SqliteConnection connection, string indexName, string expectedSql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = @indexName";
        cmd.Parameters.AddWithValue("@indexName", indexName);

        var sql = Assert.IsType<string>(cmd.ExecuteScalar());
        Assert.Contains(expectedSql, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertReferences_DeduplicatesReferenceLinesByFileAndLine()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/ref_lines.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 4, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
            new ReferenceRecord { FileId = fileId, SymbolName = "authorize", ReferenceKind = "call", Line = 2, Column = 16, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 28, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
        ]);

        using var cmd = _db.Connection.CreateCommand();
        cmd.Parameters.AddWithValue("@fileId", fileId);

        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId AND line = 2";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId";
        Assert.Equal(3L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId AND context IS NOT NULL";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT COUNT(DISTINCT reference_line_id) FROM symbol_references WHERE file_id = @fileId";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "SELECT context FROM reference_lines WHERE file_id = @fileId AND line = 2";
        Assert.Equal("return authenticate(user, password)", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void InsertReferences_PreservesDistinctReferenceLineContextsForSameFileAndLine()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/concurrent_ref_lines.py",
            Lang = "python",
            Size = 80,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "authenticate", ReferenceKind = "call", Line = 2, Column = 4, Context = "return authenticate(user, password)", ContainerKind = "function", ContainerName = "login" },
        ]);
        _writer.InsertReferences([
            new ReferenceRecord { FileId = fileId, SymbolName = "authorize", ReferenceKind = "call", Line = 2, Column = 11, Context = "return authorize(user)", ContainerKind = "function", ContainerName = "login" },
        ]);

        using var cmd = _db.Connection.CreateCommand();
        cmd.Parameters.AddWithValue("@fileId", fileId);

        cmd.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId AND line = 2";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = """
            SELECT r.symbol_name, rl.context
            FROM symbol_references r
            JOIN reference_lines rl ON rl.id = r.reference_line_id
            WHERE r.file_id = @fileId
            ORDER BY r.symbol_name
            """;
        var rows = new List<(string SymbolName, string Context)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));

        Assert.Equal([
            ("authenticate", "return authenticate(user, password)"),
            ("authorize", "return authorize(user)"),
        ], rows);
    }

    [Fact]
    public void InitializeSchema_MigratesReferenceLinesToContextKey()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_ref_line_context_key");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    CREATE TABLE files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL UNIQUE,
                        lang TEXT,
                        size INTEGER,
                        lines INTEGER,
                        checksum TEXT,
                        modified DATETIME,
                        generated INTEGER NOT NULL DEFAULT 0,
                        indexed_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE TABLE reference_lines (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        line INTEGER NOT NULL,
                        context TEXT NOT NULL,
                        UNIQUE(file_id, line)
                    );
                    CREATE TABLE symbol_references (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        symbol_name TEXT,
                        reference_kind TEXT,
                        line INTEGER,
                        column_number INTEGER,
                        context TEXT,
                        reference_line_id INTEGER REFERENCES reference_lines(id),
                        container_kind TEXT,
                        container_name TEXT
                    );
                    INSERT INTO files (id, path) VALUES (1, 'src/legacy.py');
                    INSERT INTO reference_lines (id, file_id, line, context) VALUES (1, 1, 2, 'return authenticate(user, password)');
                    INSERT INTO symbol_references (file_id, symbol_name, reference_kind, line, column_number, reference_line_id)
                    VALUES (1, 'authenticate', 'call', 2, 4, 1);
                    """;
                seed.ExecuteNonQuery();
            }

            using var migrated = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            migrated.InitializeSchema();
            var writer = new DbWriter(migrated.Connection);
            writer.InsertReferences([
                new ReferenceRecord { FileId = 1, SymbolName = "authorize", ReferenceKind = "call", Line = 2, Column = 11, Context = "return authorize(user)", ContainerKind = "function", ContainerName = "login" },
            ]);

            using var cmd = migrated.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = 1 AND line = 2";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

            cmd.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE reference_line_id IS NOT NULL";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }

    [Fact]
    public void InsertReferences_TypeScriptConstAssertion_RoundTripsThroughSql()
    {
        const string content = """
            const tuple = ["alpha", "beta"] as const;
            """;
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/const-assertion.ts",
            Lang = "typescript",
            Size = content.Length,
            Lines = 1,
            Modified = new DateTime(2026, 5, 18, 0, 0, 0, DateTimeKind.Utc),
        });
        var symbols = SymbolExtractor.Extract(fileId, "typescript", content);
        var references = ReferenceExtractor.Extract(fileId, "typescript", content, symbols);

        _writer.InsertReferences(references);

        using var cmd = _db.Connection.CreateCommand();
        cmd.Parameters.AddWithValue("@fileId", fileId);
        cmd.CommandText = """
            SELECT symbol_name, reference_kind
            FROM symbol_references
            WHERE file_id = @fileId
            ORDER BY line, column_number, reference_kind
            """;
        var rows = new List<(string SymbolName, string ReferenceKind)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));

        Assert.Contains(("const", "const_assertion"), rows);
        Assert.Contains(("\"alpha\"", "type_reference"), rows);
        Assert.Contains(("\"beta\"", "type_reference"), rows);
    }

    [Fact]
    public void InsertReferences_RollsBackChunkOnPartialFailureUnderOuterTransaction()
    {
        // Regression: #1518 — under an outer transaction, a mid-chunk
        // symbol_references INSERT failure must not leave orphan reference_lines.
        // 外側トランザクション下で symbol_references INSERT が失敗した場合、
        // 同じチャンク内で挿入済みの reference_lines が孤児として残ってはならない。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/partial.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        using (var trig = _db.Connection.CreateCommand())
        {
            trig.CommandText = @"CREATE TRIGGER fail_symbol_marker BEFORE INSERT ON symbol_references
                WHEN NEW.symbol_name = 'FAIL_ME' BEGIN
                    SELECT RAISE(ABORT, 'forced symbol_references failure');
                END";
            trig.ExecuteNonQuery();
        }

        try
        {
            using var outer = _writer.BeginTransaction();
            Assert.Throws<SqliteException>(() => _writer.InsertReferences([
                new ReferenceRecord { FileId = fileId, SymbolName = "ok_before", ReferenceKind = "call", Line = 1, Column = 1, Context = "ok line", ContainerKind = "function", ContainerName = "c" },
                new ReferenceRecord { FileId = fileId, SymbolName = "FAIL_ME", ReferenceKind = "call", Line = 99, Column = 1, Context = "fail line", ContainerKind = "function", ContainerName = "c" },
            ]));
            // Outer transaction must still be usable; its commit must not persist
            // any reference_lines from the rolled-back chunk.
            // 外側トランザクションはロールバック後も生存しており、commit してもチャンクの
            // reference_lines は残らないこと。
            outer.Commit();
        }
        finally
        {
            using var drop = _db.Connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER IF EXISTS fail_symbol_marker";
            drop.ExecuteNonQuery();
        }

        using var refLineCount = _db.Connection.CreateCommand();
        refLineCount.Parameters.AddWithValue("@fileId", fileId);
        refLineCount.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId";
        Assert.Equal(0L, (long)refLineCount.ExecuteScalar()!);

        using var refCount = _db.Connection.CreateCommand();
        refCount.Parameters.AddWithValue("@fileId", fileId);
        refCount.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId";
        Assert.Equal(0L, (long)refCount.ExecuteScalar()!);

        using var orphanCount = _db.Connection.CreateCommand();
        orphanCount.Parameters.AddWithValue("@fileId", fileId);
        orphanCount.CommandText = @"SELECT COUNT(*) FROM reference_lines rl
            WHERE rl.file_id = @fileId
              AND NOT EXISTS (SELECT 1 FROM symbol_references sr WHERE sr.reference_line_id = rl.id)";
        Assert.Equal(0L, (long)orphanCount.ExecuteScalar()!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertReferences_AtomicFileScopeSkipsBatchTransactionsWhilePublicApiKeepsThem(bool referenceLinesAreNew)
    {
        var publicFileId = UpsertTestFile(
            $"src/public-reference-batches-{referenceLinesAreNew}.cs",
            checksum: $"public-reference-batches-{referenceLinesAreNew}");
        var atomicFileId = UpsertTestFile(
            $"src/atomic-reference-batches-{referenceLinesAreNew}.cs",
            checksum: $"atomic-reference-batches-{referenceLinesAreNew}");
        var publicReferences = BuildAtomicScopeReferences(publicFileId, count: 143);
        var atomicReferences = BuildAtomicScopeReferences(atomicFileId, count: 143);
        var previousBatchHook = DbWriter.ReferenceBatchTransactionOpeningForTesting;
        var previousAtomicHook = DbWriter.AtomicFileReferenceInsertForTesting;
        var batchTransactionCount = 0;
        var atomicCalls = new List<bool>();
        try
        {
            DbWriter.ReferenceBatchTransactionOpeningForTesting = () =>
            {
                batchTransactionCount++;
                previousBatchHook?.Invoke();
            };
            DbWriter.AtomicFileReferenceInsertForTesting = newFiles =>
            {
                atomicCalls.Add(newFiles);
                previousAtomicHook?.Invoke(newFiles);
            };

            using (var transaction = _writer.BeginTransaction())
            {
                if (referenceLinesAreNew)
                {
                    _writer.InsertReferencesForNewFiles(
                        publicReferences,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                else
                {
                    _writer.InsertReferences(
                        publicReferences,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                transaction.Commit();
            }

            Assert.Equal(3, batchTransactionCount);
            batchTransactionCount = 0;

            using (var transaction = _writer.BeginTransaction())
            {
                if (referenceLinesAreNew)
                {
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        atomicReferences,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                else
                {
                    _writer.InsertReferencesInAtomicFileScope(
                        atomicReferences,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                transaction.Commit();
            }

            Assert.Equal(0, batchTransactionCount);
            Assert.Equal([referenceLinesAreNew], atomicCalls);
        }
        finally
        {
            DbWriter.ReferenceBatchTransactionOpeningForTesting = previousBatchHook;
            DbWriter.AtomicFileReferenceInsertForTesting = previousAtomicHook;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertReferences_AtomicFileScopeGroupsWholeBatchesWithoutMovingReferenceBoundaries(
        bool referenceLinesAreNew)
    {
        const int ReferenceCount = (71 * 5) + 1;
        var publicFileId = UpsertTestFile(
            $"src/public-reference-window-{referenceLinesAreNew}.cs",
            checksum: $"public-reference-window-{referenceLinesAreNew}");
        var atomicFileId = UpsertTestFile(
            $"src/atomic-reference-window-{referenceLinesAreNew}.cs",
            checksum: $"atomic-reference-window-{referenceLinesAreNew}");
        var publicReferences = BuildAtomicScopeReferences(publicFileId, ReferenceCount);
        var atomicReferences = BuildAtomicScopeReferences(atomicFileId, ReferenceCount);
        var statements = new List<DbWriter.DbWriterBatchStatement>();
        var progressRows = new List<int>();
        var transactionCount = 0;
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        var previousProgressHook = DbWriter.BatchProgressCheckpointForTesting;
        var previousTransactionHook = DbWriter.ReferenceBatchTransactionOpeningForTesting;
        try
        {
            DbWriter.BatchStatementExecutingForTesting = statement =>
            {
                statements.Add(statement);
                previousStatementHook?.Invoke(statement);
            };
            DbWriter.BatchProgressCheckpointForTesting = progress =>
            {
                if (progress.Operation == "insert_references")
                    progressRows.Add(progress.RowsProcessed);
                previousProgressHook?.Invoke(progress);
            };
            DbWriter.ReferenceBatchTransactionOpeningForTesting = () =>
            {
                transactionCount++;
                previousTransactionHook?.Invoke();
            };

            if (referenceLinesAreNew)
            {
                _writer.InsertReferencesForNewFiles(
                    publicReferences,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
            else
            {
                _writer.InsertReferences(
                    publicReferences,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }

            Assert.Equal(6, transactionCount);
            Assert.Equal([0, 71, 142, 213, 284, 355, 356], progressRows);
            Assert.Equal(
                [(71, 71), (71, 71), (71, 71), (71, 71), (71, 71), (1, 1)],
                statements.Where(statement => statement.Operation == "insert_references")
                    .Select(statement => (statement.ActiveRows, statement.StatementRows))
                    .ToArray());
            var lineWriteOperation = referenceLinesAreNew
                ? "insert_reference_lines"
                : "upsert_reference_lines";
            Assert.Equal(
                [(71, 71), (71, 71), (71, 71), (71, 71), (71, 71), (1, 1)],
                statements.Where(statement => statement.Operation == lineWriteOperation)
                    .Select(statement => (statement.ActiveRows, statement.StatementRows))
                    .ToArray());
            Assert.Equal(
                referenceLinesAreNew ? 0 : 6,
                statements.Count(statement => statement.Operation == "lookup_reference_lines"));

            statements.Clear();
            progressRows.Clear();
            transactionCount = 0;
            using (var transaction = _writer.BeginTransaction())
            {
                if (referenceLinesAreNew)
                {
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        atomicReferences,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                else
                {
                    _writer.InsertReferencesInAtomicFileScope(
                        atomicReferences,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                transaction.Commit();
            }

            Assert.Equal(0, transactionCount);
            Assert.Equal([0, 71, 142, 213, 284, 355, 356], progressRows);
            Assert.Equal(
                [(71, 71), (71, 71), (71, 71), (71, 71), (71, 71), (1, 1)],
                statements.Where(statement => statement.Operation == "insert_references")
                    .Select(statement => (statement.ActiveRows, statement.StatementRows))
                    .ToArray());
            Assert.Equal(
                [(284, 284), (72, 72)],
                statements.Where(statement => statement.Operation == lineWriteOperation)
                    .Select(statement => (statement.ActiveRows, statement.StatementRows))
                    .ToArray());
            Assert.Equal(
                referenceLinesAreNew ? 0 : 2,
                statements.Count(statement => statement.Operation == "lookup_reference_lines"));
        }
        finally
        {
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
            DbWriter.BatchProgressCheckpointForTesting = previousProgressHook;
            DbWriter.ReferenceBatchTransactionOpeningForTesting = previousTransactionHook;
        }
    }

    [Fact]
    public void InsertReferences_AtomicFileScopeCapsReferenceLineWindowAtThirtyTwoBatches()
    {
        const int ReferenceCount = 71 * 33;
        var fileId = UpsertTestFile(
            "src/atomic-reference-window-cap.cs",
            checksum: "atomic-reference-window-cap");
        var references = Enumerable.Range(0, ReferenceCount)
            .Select(index => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"callee_{index}",
                ReferenceKind = "call",
                Line = 1,
                Column = index + 1,
                Context = "shared context",
                ContainerKind = "function",
                ContainerName = "caller",
            })
            .ToArray();
        var statements = new List<DbWriter.DbWriterBatchStatement>();
        var previousStatementHook = DbWriter.BatchStatementExecutingForTesting;
        try
        {
            DbWriter.BatchStatementExecutingForTesting = statement =>
            {
                statements.Add(statement);
                previousStatementHook?.Invoke(statement);
            };

            using var transaction = _writer.BeginTransaction();
            _writer.InsertReferencesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            transaction.Commit();
        }
        finally
        {
            DbWriter.BatchStatementExecutingForTesting = previousStatementHook;
        }

        Assert.Equal(33, statements.Count(statement => statement.Operation == "insert_references"));
        Assert.Equal(
            [(1, 1), (1, 1)],
            statements.Where(statement => statement.Operation == "upsert_reference_lines")
                .Select(statement => (statement.ActiveRows, statement.StatementRows))
                .ToArray());
        Assert.Equal(2, statements.Count(statement => statement.Operation == "lookup_reference_lines"));
    }

    [Theory]
    [InlineData(false, 72)]
    [InlineData(true, 142)]
    public void InsertReferences_AtomicFileScopeFailureRollsBackEveryPriorBatch(
        bool referenceLinesAreNew,
        int failureIndex)
    {
        var fileId = UpsertTestFile(
            $"src/atomic-reference-rollback-{referenceLinesAreNew}.cs",
            checksum: $"atomic-reference-rollback-{referenceLinesAreNew}");
        var references = BuildAtomicScopeReferences(fileId, count: 143, failureIndex);
        using (var trigger = _db.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_atomic_file_reference_batch
                BEFORE INSERT ON symbol_references
                WHEN NEW.symbol_name = 'FAIL_ME'
                BEGIN
                    SELECT RAISE(ABORT, 'forced atomic-file reference failure');
                END
                """;
            trigger.ExecuteNonQuery();
        }

        try
        {
            var exception = Record.Exception(() =>
            {
                using var transaction = _writer.BeginTransaction();
                if (referenceLinesAreNew)
                {
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        references,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                else
                {
                    _writer.InsertReferencesInAtomicFileScope(
                        references,
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None);
                }
                transaction.Commit();
            });

            Assert.IsType<SqliteException>(exception);
        }
        finally
        {
            using var drop = _db.Connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER IF EXISTS fail_atomic_file_reference_batch";
            drop.ExecuteNonQuery();
        }

        using var countCommand = _db.Connection.CreateCommand();
        countCommand.Parameters.AddWithValue("@fileId", fileId);
        countCommand.CommandText = """
            SELECT (SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId),
                   (SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId)
            """;
        using var counts = countCommand.ExecuteReader();
        Assert.True(counts.Read());
        Assert.Equal(0L, counts.GetInt64(0));
        Assert.Equal(0L, counts.GetInt64(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InsertReferences_AtomicFileScopeReusesSameContextAcrossBatchBoundary(bool referenceLinesAreNew)
    {
        var fileId = UpsertTestFile(
            $"src/atomic-reference-boundary-{referenceLinesAreNew}.cs",
            checksum: $"atomic-reference-boundary-{referenceLinesAreNew}");
        var references = Enumerable.Range(0, 143)
            .Select(index => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = index switch
                {
                    70 => "boundary_same_first",
                    71 => "boundary_same_second",
                    72 => "boundary_different_context",
                    _ => $"callee_{index}",
                },
                ReferenceKind = "call",
                Line = index is 70 or 71 or 72 ? 500 : index + 1,
                Column = index + 1,
                Context = index switch
                {
                    70 or 71 => "shared boundary context",
                    72 => "different boundary context",
                    _ => $"line {index}",
                },
                ContainerKind = "function",
                ContainerName = "caller",
            })
            .ToArray();

        using (var transaction = _writer.BeginTransaction())
        {
            if (referenceLinesAreNew)
            {
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    references,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
            else
            {
                _writer.InsertReferencesInAtomicFileScope(
                    references,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
            transaction.Commit();
        }

        using var command = _db.Connection.CreateCommand();
        command.Parameters.AddWithValue("@fileId", fileId);
        command.CommandText = """
            SELECT COUNT(DISTINCT reference_line_id)
            FROM symbol_references
            WHERE file_id = @fileId
              AND symbol_name IN ('boundary_same_first', 'boundary_same_second')
            """;
        Assert.Equal(1L, (long)command.ExecuteScalar()!);

        command.CommandText = """
            SELECT COUNT(DISTINCT reference_line_id)
            FROM symbol_references
            WHERE file_id = @fileId
              AND symbol_name IN ('boundary_same_first', 'boundary_same_second', 'boundary_different_context')
            """;
        Assert.Equal(2L, (long)command.ExecuteScalar()!);

        command.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId AND line = 500";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task InsertReferences_AtomicFileScopeRequiresLiveOwnedTransactionBeforeEmptyOrCancellationChecks()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var previousAtomicHook = DbWriter.AtomicFileReferenceInsertForTesting;
        var atomicCallCount = 0;
        try
        {
            DbWriter.AtomicFileReferenceInsertForTesting = newFiles =>
            {
                atomicCallCount++;
                previousAtomicHook?.Invoke(newFiles);
            };

            var missingTransaction = Assert.Throws<InvalidOperationException>(() =>
                _writer.InsertReferencesInAtomicFileScope(
                    [],
                    refreshMutualRecursionFlags: false,
                    cancelled.Token));
            Assert.Contains("requires an active transaction", missingTransaction.Message, StringComparison.Ordinal);
            Assert.Equal(0, atomicCallCount);

            var publicEmptyException = Record.Exception(() =>
                _writer.InsertReferences([], refreshMutualRecursionFlags: false, cancelled.Token));
            Assert.Null(publicEmptyException);

            using (var transaction = _writer.BeginTransaction())
            {
                var copiedContextException = await Task.Run(() => Record.Exception(() =>
                    _writer.InsertReferencesInAtomicFileScope(
                        [],
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None)));
                Assert.IsType<InvalidOperationException>(copiedContextException);
                Assert.Equal(0, atomicCallCount);
            }

            using (var transaction = _writer.BeginTransaction())
            {
                _writer.InsertReferencesInAtomicFileScope(
                    [],
                    refreshMutualRecursionFlags: false,
                    cancelled.Token);
                transaction.Commit();
            }
            Assert.Equal(1, atomicCallCount);

            using (var committedTransaction = _writer.BeginTransaction())
            {
                committedTransaction.Commit();
                Assert.Throws<InvalidOperationException>(() =>
                    _writer.InsertReferencesInAtomicFileScope(
                        [],
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
            }
            Assert.Equal(1, atomicCallCount);
        }
        finally
        {
            DbWriter.AtomicFileReferenceInsertForTesting = previousAtomicHook;
        }
    }

    [Fact]
    public void InsertReferences_AtomicFileScopeCancellationRollsBackReferenceAndContextRows()
    {
        var fileId = UpsertTestFile("src/atomic-reference-cancel.cs", checksum: "atomic-reference-cancel");
        var references = BuildAtomicScopeReferences(fileId, count: 143);
        using var cancellation = new CancellationTokenSource();
        var previousProgressHook = DbWriter.BatchProgressCheckpointForTesting;
        try
        {
            DbWriter.BatchProgressCheckpointForTesting = progress =>
            {
                if (progress.Operation == "insert_references" && progress.RowsProcessed == 71)
                    cancellation.Cancel();
                previousProgressHook?.Invoke(progress);
            };

            var exception = Record.Exception(() =>
            {
                using var transaction = _writer.BeginTransaction();
                _writer.InsertReferencesInAtomicFileScope(
                    references,
                    refreshMutualRecursionFlags: false,
                    cancellation.Token);
                transaction.Commit();
            });
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        finally
        {
            DbWriter.BatchProgressCheckpointForTesting = previousProgressHook;
        }

        using var command = _db.Connection.CreateCommand();
        command.Parameters.AddWithValue("@fileId", fileId);
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM symbol_references WHERE file_id = @fileId),
                   (SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId)
            """;
        using var counts = command.ExecuteReader();
        Assert.True(counts.Read());
        Assert.Equal(0L, counts.GetInt64(0));
        Assert.Equal(0L, counts.GetInt64(1));
    }

    private static ReferenceRecord[] BuildAtomicScopeReferences(
        long fileId,
        int count,
        int failureIndex = -1)
        => Enumerable.Range(0, count)
            .Select(index => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = index == failureIndex ? "FAIL_ME" : $"callee_{index}",
                ReferenceKind = "call",
                Line = index + 1,
                Column = index + 1,
                Context = $"callee_{index}();",
                ContainerKind = "function",
                ContainerName = "caller",
            })
            .ToArray();

    [Fact]
    public void CleanExistingFileData_PreventsFtsOrphans()
    {
        // Insert a file with chunks (populates FTS) / ファイルとチャンク（FTS含む）を挿入
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/orphan.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertChunks([new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 5, Content = "def hello_orphan_test(): pass" }]);
        _writer.InsertReferences([new() { FileId = fileId, SymbolName = "hello_orphan_test", ReferenceKind = "call", Line = 1, Column = 5, Context = "def hello_orphan_test(): pass", ContainerKind = "function", ContainerName = "hello_orphan_test" }]);

        // Verify FTS has the entry / FTSにエントリがあることを確認
        using var cmd1 = _db.Connection.CreateCommand();
        cmd1.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'hello_orphan_test'";
        Assert.Equal(1L, (long)cmd1.ExecuteScalar()!);

        // Clean existing data then re-upsert (simulates re-indexing)
        // 既存データを掃除してから再upsert（再インデックスをシミュレート）
        _writer.CleanExistingFileData("src/orphan.py");
        var newId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/orphan.py",
            Lang = "python",
            Size = 60,
            Lines = 6,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(1),
        });
        _writer.InsertChunks([new() { FileId = newId, ChunkIndex = 0, StartLine = 1, EndLine = 6, Content = "def world_replacement(): pass" }]);

        // Old FTS entry should be gone, new one should exist
        // 旧FTSエントリは消え、新エントリが存在するはず
        using var cmd2 = _db.Connection.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'hello_orphan_test'";
        Assert.Equal(0L, (long)cmd2.ExecuteScalar()!);

        using var cmd3 = _db.Connection.CreateCommand();
        cmd3.CommandText = "SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH 'world_replacement'";
        Assert.Equal(1L, (long)cmd3.ExecuteScalar()!);

        using var cmd4 = _db.Connection.CreateCommand();
        cmd4.CommandText = "SELECT COUNT(*) FROM reference_lines WHERE file_id = @fileId";
        cmd4.Parameters.AddWithValue("@fileId", fileId);
        Assert.Equal(0L, (long)cmd4.ExecuteScalar()!);
    }

    [Fact]
    public void PurgeStaleFiles_RemovesDeletedFiles()
    {
        // Simulate branch switch: insert a file, then purge when file doesn't exist
        // ブランチ切り替えをシミュレート: ファイルを挿入後、存在しないファイルをパージ
        var tempDir = TestProjectHelper.CreateTempProject("codeindex_purge");

        try
        {
            // Create a real file and a "ghost" file entry
            // 実在するファイルと「ゴースト」ファイルエントリを作成
            var realFile = Path.Combine(tempDir, "real.py");
            File.WriteAllText(realFile, "x = 1");

            _writer.UpsertFile(new FileRecord
            {
                Path = "real.py",
                Lang = "python",
                Size = 5,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.UpsertFile(new FileRecord
            {
                Path = "ghost.py",
                Lang = "python",
                Size = 10,
                Lines = 2,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            var (beforeCount, _, _, _) = _writer.GetCounts();
            Assert.Equal(2, beforeCount);

            var purged = _writer.PurgeStaleFiles(tempDir);
            Assert.Equal(1, purged);

            var (afterCount, _, _, _) = _writer.GetCounts();
            Assert.Equal(1, afterCount);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PurgeStaleFiles_BatchesLargeDeletesAndCascadesChildrenAndFts()
    {
        const int fileCount = 1_201;
        var tempDir = TestProjectHelper.CreateTempProject("codeindex_purge_batch");
        try
        {
            SeedStaleFilesWithChildren(fileCount);
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks"));
            var beforeCommitCalls = 0;

            var purged = _writer.PurgeStaleFiles(tempDir, () => beforeCommitCalls++);

            Assert.Equal(fileCount, purged);
            Assert.Equal(1, beforeCommitCalls);
            Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
            Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM chunks"));
            Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM symbols"));
            Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void PurgeStaleFiles_MultipleBatchesRollBackTogetherWhenBeforeCommitFails()
    {
        const int fileCount = 1_001;
        var tempDir = TestProjectHelper.CreateTempProject("codeindex_purge_batch_rollback");
        try
        {
            SeedStaleFilesWithChildren(fileCount);
            var beforeCommitCalls = 0;

            Assert.Throws<InvalidOperationException>(() =>
                _writer.PurgeStaleFiles(
                    tempDir,
                    () =>
                    {
                        beforeCommitCalls++;
                        throw new InvalidOperationException("stop before commit");
                    }));

            Assert.Equal(1, beforeCommitCalls);
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM chunks"));
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM symbols"));
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks"));

            Assert.Equal(fileCount, _writer.PurgeStaleFiles(tempDir));
            Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void FilePurgePlan_InvalidPersistedSizeDisablesByteEstimateWithoutDeletingEarly()
    {
        _writer.UpsertFile(new FileRecord
        {
            Path = "invalid-size.py",
            Lang = "python",
            Size = 42,
            Lines = 1,
            Modified = DateTime.UtcNow,
        });
        using (var corrupt = _db.Connection.CreateCommand())
        {
            corrupt.CommandText = "UPDATE files SET size = 'invalid' WHERE path = 'invalid-size.py'";
            Assert.Equal(1, corrupt.ExecuteNonQuery());
        }

        var plan = _writer.PlanFilesOutsideRetainedSet(new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(1, plan.Count);
        Assert.False(plan.ByteEstimateComplete);
        Assert.Equal(1, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(1, _writer.ApplyFilePurgePlan(plan));
        Assert.Equal(0, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public void FilePurgePlan_CancellationAfterFirstBatchRollsBackEveryDelete()
    {
        const int fileCount = 1_201;
        SeedStaleFilesWithChildren(fileCount);
        var plan = _writer.PlanFilesOutsideRetainedSet(new HashSet<string>(StringComparer.Ordinal));
        var previousHook = DbWriter.FilePurgeBatchCompletedForTesting;
        using var cancellation = new CancellationTokenSource();
        try
        {
            DbWriter.FilePurgeBatchCompletedForTesting = processed =>
            {
                previousHook?.Invoke(processed);
                cancellation.Cancel();
            };

            Assert.Throws<OperationCanceledException>(() =>
                _writer.ApplyFilePurgePlan(plan, cancellationToken: cancellation.Token));

            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM chunks"));
            Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks"));
        }
        finally
        {
            DbWriter.FilePurgeBatchCompletedForTesting = previousHook;
        }
    }

    [Fact]
    public async Task FilePurgePlan_CancelledWhileTransactionGateHeld_StopsPromptlyWithoutDeleting()
    {
        const int fileCount = 1;
        SeedStaleFilesWithChildren(fileCount);
        var plan = _writer.PlanFilesOutsideRetainedSet(new HashSet<string>(StringComparer.Ordinal));
        using var cancellation = new CancellationTokenSource();
        var purgeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var purgeCompleted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var purgeThread = new Thread(() =>
        {
            purgeStarted.TrySetResult(true);
            purgeCompleted.TrySetResult(Record.Exception(() =>
                _writer.ApplyFilePurgePlan(plan, cancellationToken: cancellation.Token)));
        })
        {
            IsBackground = true,
            Name = "file-purge-gate-cancellation-test",
        };

        var held = _writer.BeginTransaction(CancellationToken.None, "file purge cancellation test owner");
        var completedBeforeGateRelease = false;
        var stopwatch = new Stopwatch();
        try
        {
            purgeThread.Start();
            await purgeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            stopwatch.Start();
            cancellation.Cancel();
            completedBeforeGateRelease = ReferenceEquals(
                await Task.WhenAny(purgeCompleted.Task, Task.Delay(TimeSpan.FromSeconds(2))),
                purgeCompleted.Task);
            stopwatch.Stop();
        }
        finally
        {
            held.Dispose();
        }

        var purgeException = await purgeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(purgeThread.Join(TimeSpan.FromSeconds(2)), "File purge worker did not exit.");
        Assert.True(
            completedBeforeGateRelease,
            $"File purge cancellation did not interrupt transaction gate contention within two seconds ({stopwatch.Elapsed}).");
        var cancellationException = Assert.IsAssignableFrom<OperationCanceledException>(purgeException);
        Assert.Equal(cancellation.Token, cancellationException.CancellationToken);
        Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(fileCount, ExecuteScalarLong("SELECT COUNT(*) FROM fts_chunks"));
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSetWithinListedDirectories_PurgesDeepDescendantsUnderSymlinkPrunedDirectory()
    {
        // Regression for #190 follow-up: earlier symlink-following runs can leave entries like
        // "sub/parent_loop/nested/deep.py" whose immediate parent ("sub/parent_loop/nested") is not in
        // the current scan's listedDirectories. The partial-purge walker must still remove them because
        // the symlink directory itself is authoritatively skipped in the current scan.
        // #190 追補の回帰: 過去の symlink 追従により "sub/parent_loop/nested/deep.py" のような深い
        // 子孫エントリが残るが、その immediate parent は今回の scan の listedDirectories には含まれない。
        // symlink ディレクトリ自身を authoritative に skip している以上、partial-purge は
        // この子孫を確実に削除しなければならない。
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop/shallow.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop/nested/deep.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop_sibling/keep.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });
        _writer.UpsertFile(new FileRecord { Path = "sub/foo.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });

        var retained = new HashSet<string>(StringComparer.Ordinal) { "sub/foo.py", "sub/parent_loop_sibling/keep.py" };
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal) { string.Empty, "sub", "sub/parent_loop", "sub/parent_loop_sibling" };
        var symlinkPrunedDirectories = new HashSet<string>(StringComparer.Ordinal) { "sub/parent_loop" };

        var purged = _writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retained, listedDirectories, symlinkPrunedDirectories);

        Assert.Equal(2, purged);
        Assert.False(_writer.HasFileAtPath("sub/parent_loop/shallow.py"));
        Assert.False(_writer.HasFileAtPath("sub/parent_loop/nested/deep.py"));
        Assert.True(_writer.HasFileAtPath("sub/parent_loop_sibling/keep.py"));
        Assert.True(_writer.HasFileAtPath("sub/foo.py"));
    }

    [Fact]
    public void PurgeFilesOutsideRetainedSetWithinListedDirectories_DoesNotConfuseSymlinkPrefixWithSiblingDirectory()
    {
        // Guard: "sub/parent_loop" prune prefix must not match "sub/parent_loop_x/inside.py".
        // ガード: prune prefix "sub/parent_loop" は "sub/parent_loop_x/inside.py" を巻き込まない。
        _writer.UpsertFile(new FileRecord { Path = "sub/parent_loop_x/inside.py", Lang = "python", Size = 1, Lines = 1, Modified = DateTime.UtcNow });

        var retained = new HashSet<string>(StringComparer.Ordinal) { "sub/parent_loop_x/inside.py" };
        var listedDirectories = new HashSet<string>(StringComparer.Ordinal) { "sub", "sub/parent_loop_x" };
        var symlinkPrunedDirectories = new HashSet<string>(StringComparer.Ordinal) { "sub/parent_loop" };

        var purged = _writer.PurgeFilesOutsideRetainedSetWithinListedDirectories(retained, listedDirectories, symlinkPrunedDirectories);

        Assert.Equal(0, purged);
        Assert.True(_writer.HasFileAtPath("sub/parent_loop_x/inside.py"));
    }

    [Fact]
    public void DropAll_RemovesAllTables()
    {
        // Insert some data, then drop all
        // データを挿入してから全削除
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/x.py",
            Lang = "python",
            Size = 10,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _db.DropAll();
        _db.InitializeSchema();

        var (files, chunks, symbols, references) = _writer.GetCounts();
        Assert.Equal(0, files);
        Assert.Equal(0, chunks);
        Assert.Equal(0, symbols);
        Assert.Equal(0, references);
    }

    [Fact]
    public void DeleteFileByPath_RemovesFileAndData()
    {
        // Insert a file with chunks and symbols, then delete by path
        // ファイルとチャンク・シンボルを挿入し、パスで削除
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/remove_me.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.InsertChunks([new() { FileId = fileId, ChunkIndex = 0, StartLine = 1, EndLine = 5, Content = "def foo(): pass" }]);
        _writer.InsertSymbols([new() { FileId = fileId, Kind = "function", Name = "foo", Line = 1 }]);

        var result = _writer.DeleteFileByPath("src/remove_me.py");
        Assert.True(result);

        var (files, chunks, symbols, references) = _writer.GetCounts();
        Assert.Equal(0, files);
        Assert.Equal(0, chunks);
        Assert.Equal(0, symbols);
        Assert.Equal(0, references);
    }

    [Fact]
    public void DeleteFileByPath_ReturnsFalseIfNotFound()
    {
        // Deleting a non-existent path returns false
        // 存在しないパスの削除はfalseを返す
        var result = _writer.DeleteFileByPath("nonexistent/file.py");
        Assert.False(result);
    }

    [Fact]
    public void DeleteFileByPath_DoesNotAffectOtherFiles()
    {
        // Deleting one file should not affect another
        // 1ファイルの削除は他のファイルに影響しない
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/keep.py",
            Lang = "python",
            Size = 50,
            Lines = 5,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/delete.py",
            Lang = "python",
            Size = 30,
            Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        _writer.DeleteFileByPath("src/delete.py");

        var (files, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, files);
    }

    [Fact]
    public void MarkFoldReady_StampsFoldReadyWhenAllRowsBackfilled()
    {
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/fold_ok.py",
            Lang = "python",
            Size = 30,
            Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "Straße", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        var stamped = _writer.MarkFoldReady();

        Assert.True(stamped);
        Assert.Equal(FoldReadyStampResult.Ready, _writer.MarkFoldReadyWithResult());
        Assert.Equal(DbContext.FoldReadyFlag, _db.GetUserVersion() & DbContext.FoldReadyFlag);
    }

    [Fact]
    public void MarkFoldReady_LeavesFoldReadyUnsetWhenNullFoldedRowExists()
    {
        // Reproduces issue #1535: a concurrent writer inserting a NULL-folded row between
        // an upfront verify and the FoldReady stamp can leave readers on the fold path with
        // some rows still NULL. The fix re-verifies inside MarkFoldReady's BEGIN IMMEDIATE so
        // this stamp is skipped and readers stay on NOCASE until backfill_fold is re-run.
        // issue #1535 の再現: 上位の verify 後に concurrent writer が NULL 行を差し込んだ場合、
        // 修正後の MarkFoldReady は再検証で stamp を取りやめ、reader を NOCASE に保つ。
        var fileId = _writer.UpsertFile(new FileRecord
        {
            Path = "src/fold_partial.py",
            Lang = "python",
            Size = 30,
            Lines = 3,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        _writer.InsertSymbols([
            new SymbolRecord { FileId = fileId, Kind = "function", Name = "Straße", Line = 1, StartLine = 1, EndLine = 1 },
        ]);

        // Simulate a concurrent NULL-folded insert that slipped in after the caller's
        // upfront AllFoldedColumnsBackfilled check returned true.
        // 上位の verify 直後に concurrent writer が NULL 行を入れたシナリオを再現する。
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE symbols SET name_folded = NULL";
            cmd.ExecuteNonQuery();
        }

        var stamped = _writer.MarkFoldReady();

        Assert.False(stamped);
        Assert.Equal(FoldReadyStampResult.MissingBackfill, _writer.MarkFoldReadyWithResult());
        Assert.Equal(0, _db.GetUserVersion() & DbContext.FoldReadyFlag);
        Assert.Null(_db.GetMetaString("fold_key_version"));
        Assert.Null(_db.GetMetaString("fold_key_fingerprint"));
    }

    [Fact]
    public void GetUnchangedFileId_ReturnsNullWhenStoredLineCountDiffers()
    {
        var file = new FileRecord
        {
            Path = "src/crlf.cs",
            Lang = "csharp",
            Size = 20,
            Lines = 2,
            Checksum = "same-logical-content",
            Modified = DateTime.UtcNow,
        };
        var fileId = _writer.UpsertFile(file);

        var unchanged = _writer.GetUnchangedFileId(
            file.Path,
            file.Modified.AddMinutes(1),
            file.Checksum,
            size: 24,
            lines: 2,
            language: file.Lang);
        var staleLines = _writer.GetUnchangedFileId(
            file.Path,
            file.Modified.AddMinutes(2),
            file.Checksum,
            size: 24,
            lines: 3,
            language: file.Lang);

        Assert.Equal(fileId, unchanged);
        Assert.Null(staleLines);
    }

    [Fact]
    public async Task TransactionScope_DisposeIsAtomicUnderConcurrentCalls()
    {
        var scope = _writer.BeginTransaction();
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/rolled_back.py",
            Lang = "python",
            Size = 10,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(scope.Dispose))
            .ToArray();

        await Task.WhenAll(tasks);

        var (rolledBackFiles, _, _, _) = _writer.GetCounts();
        Assert.Equal(0, rolledBackFiles);

        using var nextScope = _writer.BeginTransaction();
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/next.py",
            Lang = "python",
            Size = 10,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        nextScope.Commit();

        var (committedFiles, _, _, _) = _writer.GetCounts();
        Assert.Equal(1, committedFiles);
    }

    [Fact]
    public async Task TransactionScope_CommitDisposeRaceDoesNotSurfaceDoubleRollbackSqliteError()
    {
        for (var i = 0; i < 25; i++)
        {
            var scope = _writer.BeginTransaction();
            _writer.UpsertFile(new FileRecord
            {
                Path = $"src/race_{i}.py",
                Lang = "python",
                Size = 10,
                Lines = 1,
                Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });

            var exceptions = new List<Exception>();
            var commitTask = Task.Run(() =>
            {
                try
                {
                    scope.Commit();
                }
                catch (InvalidOperationException)
                {
                    // Dispose may win the race; that is a clear lifecycle error, not a
                    // low-level double-rollback SQLite failure.
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                        exceptions.Add(ex);
                }
            });
            var disposeTask = Task.Run(scope.Dispose);

            await Task.WhenAll(commitTask, disposeTask);

            Assert.Empty(exceptions);
        }

        using var nextScope = _writer.BeginTransaction();
        _writer.UpsertFile(new FileRecord
        {
            Path = "src/after_race.py",
            Lang = "python",
            Size = 10,
            Lines = 1,
            Modified = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        nextScope.Commit();
    }

    [Fact]
    public void TransactionScope_CommitContentionTimesOutWithDiagnostic_Issue3517()
    {
        var priorTimeout = DbWriter.TransactionStateContentionTimeoutForTesting;
        var scope = _writer.BeginTransaction();
        var stateField = typeof(DbWriter.TransactionScope).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TransactionScope._state field was not found.");
        var rollingBackState = ReadTransactionScopeStateConstant("StateRollingBack");
        var rolledBackState = ReadTransactionScopeStateConstant("StateRolledBack");
        try
        {
            DbWriter.TransactionStateContentionTimeoutForTesting = TimeSpan.FromMilliseconds(20);
            stateField.SetValue(scope, rollingBackState);
            var stopwatch = Stopwatch.StartNew();

            var ex = Assert.Throws<InvalidOperationException>(() => scope.Commit());

            stopwatch.Stop();
            Assert.Contains("Timed out waiting for transaction scope state transition", ex.Message, StringComparison.Ordinal);
            Assert.Contains("commit", ex.Message, StringComparison.Ordinal);
            Assert.Contains("rolling_back", ex.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Contention timeout took {stopwatch.Elapsed}.");
        }
        finally
        {
            stateField.SetValue(scope, rolledBackState);
            scope.Dispose();
            DbWriter.TransactionStateContentionTimeoutForTesting = priorTimeout;
        }
    }

    private static int ReadTransactionScopeStateConstant(string name)
        => (int)(typeof(DbWriter.TransactionScope)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetRawConstantValue()
            ?? throw new InvalidOperationException($"TransactionScope.{name} field was not found."));

    public void Dispose()
    {
        _db.Dispose();
        DeleteDbPath();
    }

    [Fact]
    public void DbContext_NewDatabaseRestrictsFileModeOnPosix()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal("0600", DbContext.GetUnixFileModeString(_dbPath));
    }

    [Fact]
    public void SetMeta_InsideWriterTransaction_RollsBackWithDependentRows_Issue1753()
    {
        using (var transaction = _writer.BeginTransaction())
        {
            _writer.SetMeta("schema_phase", "new");
            _writer.UpsertFile(new FileRecord
            {
                Path = "src/partial.cs",
                Lang = "csharp",
                Size = 12,
                Lines = 1,
                Modified = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                Checksum = "partial",
            });
        }

        Assert.Null(ReadMeta("schema_phase"));
        Assert.False(_writer.HasFileAtPath("src/partial.cs"));
    }

    [Fact]
    public void SetMeta_InsideRawSqlTransaction_UsesSavepointWithoutNestedBegin_Issue1753()
    {
        ExecuteNonQuery(_db.Connection, "BEGIN IMMEDIATE");
        try
        {
            _writer.SetMeta("raw_phase", "new");
            ExecuteNonQuery(_db.Connection, "ROLLBACK");
        }
        catch
        {
            ExecuteNonQuery(_db.Connection, "ROLLBACK");
            throw;
        }

        Assert.Null(ReadMeta("raw_phase"));
    }

    [Fact]
    public void SetMetaValues_UpsertsNullValuesInOneBatch()
    {
        _writer.SetMetaValues(
            ("batch_meta_a", "1"),
            ("batch_meta_b", null));

        Assert.Equal("1", ReadMeta("batch_meta_a"));
        Assert.Null(ReadMeta("batch_meta_b"));
        Assert.True(MetaRowExists("batch_meta_b"));
    }

    [Fact]
    public void GetMetaStrings_ReturnsRequestedKeysWithNullsForMissingOrDbNull()
    {
        _writer.SetMetaValues(
            ("batch_read_a", "1"),
            ("batch_read_b", null));

        var values = _db.GetMetaStrings(["batch_read_a", "batch_read_b", "batch_read_missing"]);

        Assert.Equal("1", values["batch_read_a"]);
        Assert.Null(values["batch_read_b"]);
        Assert.Null(values["batch_read_missing"]);
    }

    [Fact]
    public void SetMetaValues_InsideWriterTransaction_RollsBackWithDependentRows()
    {
        using (var transaction = _writer.BeginTransaction())
        {
            _writer.SetMetaValues(
                ("batch_phase", "new"),
                ("batch_phase_null", null));
            _writer.UpsertFile(new FileRecord
            {
                Path = "src/batch_partial.cs",
                Lang = "csharp",
                Size = 12,
                Lines = 1,
                Modified = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc),
                Checksum = "batch_partial",
            });
        }

        Assert.Null(ReadMeta("batch_phase"));
        Assert.False(MetaRowExists("batch_phase_null"));
        Assert.False(_writer.HasFileAtPath("src/batch_partial.cs"));
    }

    [Fact]
    public void ClearLastFailedIndexRunMetadata_NullsOnlyFailureMetadata()
    {
        string[] keys =
        [
            DbContext.LastFailedIndexRunStatusMetaKey,
            DbContext.LastFailedIndexRunModeMetaKey,
            DbContext.LastFailedIndexRunStartedAtMetaKey,
            DbContext.LastFailedIndexRunDurationMsMetaKey,
            DbContext.LastFailedIndexRunFilesProcessedMetaKey,
            DbContext.LastFailedIndexRunFilesTotalMetaKey,
            DbContext.LastFailedIndexRunErrorCodeMetaKey,
            DbContext.LastFailedIndexRunReasonMetaKey,
            DbContext.LastFailedIndexRunProgressPersistedMetaKey,
            DbContext.LastFailedIndexRunRecoveryHintMetaKey,
        ];
        foreach (var key in keys)
            _writer.SetMeta(key, "stale");
        _writer.SetMeta("unrelated_meta", "keep");

        _writer.ClearLastFailedIndexRunMetadata();

        foreach (var key in keys)
        {
            Assert.Null(ReadMeta(key));
            Assert.True(MetaRowExists(key), key);
        }
        Assert.Equal("keep", ReadMeta("unrelated_meta"));
    }

    [Fact]
    public void ClearHotspotFamilyReady_NullsGlobalAndLanguageMetadata()
    {
        var languages = FileIndexer.GetHotspotFamilyMarkerLanguages();
        var keys = new List<string>
        {
            DbContext.HotspotFamilyVersionMetaKey,
            DbContext.HotspotFamilyMarkerFingerprintMetaKey,
        };
        foreach (var lang in languages)
        {
            keys.Add(DbContext.GetHotspotFamilyVersionMetaKey(lang));
            keys.Add(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang));
        }
        foreach (var key in keys)
            _writer.SetMeta(key, "ready");
        _writer.SetMeta("unrelated_meta", "keep");

        _writer.ClearHotspotFamilyReady();

        foreach (var key in keys)
        {
            Assert.Null(ReadMeta(key));
            Assert.True(MetaRowExists(key), key);
        }
        Assert.Equal("keep", ReadMeta("unrelated_meta"));
    }

    private void SeedStaleFilesWithChildren(int fileCount)
    {
        using var transaction = _db.Connection.BeginTransaction();
        using (var insertFile = _db.Connection.CreateCommand())
        {
            insertFile.Transaction = transaction;
            insertFile.CommandText = """
                INSERT INTO files (path, lang, size, lines, checksum, modified)
                VALUES (@path, 'csharp', 10, 1, @checksum, @modified)
                """;
            var pathParameter = insertFile.Parameters.Add("@path", SqliteType.Text);
            var checksumParameter = insertFile.Parameters.Add("@checksum", SqliteType.Text);
            insertFile.Parameters.Add("@modified", SqliteType.Text).Value = "2026-01-01T00:00:00Z";
            insertFile.Prepare();
            for (var index = 0; index < fileCount; index++)
            {
                pathParameter.Value = $"stale/file_{index:D5}.cs";
                checksumParameter.Value = $"stale-{index}";
                insertFile.ExecuteNonQuery();
            }
        }

        using (var insertChildren = _db.Connection.CreateCommand())
        {
            insertChildren.Transaction = transaction;
            insertChildren.CommandText = """
                INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content)
                SELECT id, 0, 1, 1, 'stale_batch_payload ' || id FROM files;

                INSERT INTO symbols (file_id, kind, name, line, start_line, end_line)
                SELECT id, 'class', 'Stale' || id, 1, 1, 1 FROM files;
                """;
            insertChildren.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void DeleteDbPath() => TestProjectHelper.DeleteDirectory(_dbDir);

    private string ExecuteScalarString(string sql)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private long ExecuteScalarLong(string sql)
        => ExecuteScalarLong(_db.Connection, sql);

    private long CountFtsSyncTriggers()
        => ExecuteScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('fts_chunks_ai', 'fts_chunks_ad', 'fts_chunks_au')");

    private long CountTrigramFtsSyncTriggers()
        => ExecuteScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('fts_chunks_trigram_ai', 'fts_chunks_trigram_ad', 'fts_chunks_trigram_au')");

    private long CountFtsBulkLoadGenerationCleanupTriggers()
        => ExecuteScalarLong($"""
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'trigger'
              AND name IN (
                  '{DbWriter.FtsBulkLoadGenerationClearInsertTriggerName}',
                  '{DbWriter.FtsBulkLoadGenerationClearUpdateTriggerName}',
                  '{DbWriter.FtsBulkLoadGenerationClearDeleteTriggerName}')
            """);

    private string? ReadMeta(string key)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    private bool MetaRowExists(string key)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM codeindex_meta WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() is not null;
    }

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SeedLegacyKindCheckSchema(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        ExecuteNonQuery(conn, """
            CREATE TABLE files (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                path        TEXT NOT NULL UNIQUE,
                lang        TEXT,
                size        INTEGER,
                lines       INTEGER,
                checksum    TEXT,
                modified    DATETIME,
                generated   INTEGER NOT NULL DEFAULT 0,
                indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE chunks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                chunk_index INTEGER NOT NULL,
                start_line  INTEGER,
                end_line    INTEGER,
                content     TEXT,
                UNIQUE(file_id, chunk_index)
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE symbols (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT CHECK (kind IN ('class','function','module')),
                sub_kind        TEXT,
                name            TEXT,
                line            INTEGER,
                start_line      INTEGER,
                start_column    INTEGER,
                end_line        INTEGER,
                body_start_line INTEGER,
                body_end_line   INTEGER,
                signature       TEXT,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ('class','function','module')),
                container_name  TEXT,
                container_qualified_name TEXT,
                family_key      TEXT,
                visibility      TEXT,
                return_type     TEXT,
                is_metadata_target INTEGER,
                name_folded     TEXT
            )
            """);
        ExecuteNonQuery(conn, """
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ('call','type_reference')),
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ('class','function','module')),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )
            """);
    }
}
