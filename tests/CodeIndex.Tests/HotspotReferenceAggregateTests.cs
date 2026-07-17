using System.Text;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Fact]
    public void LimitedFileAndNameHotspots_UseBoundedMaintainedAggregate_Issue4581()
    {
        var content = new StringBuilder();
        const int distinctReferenceNames = 2_000;
        for (var i = 0; i < distinctReferenceNames; i++)
            content.AppendLine($"def target_{i}():\n    return {i}");
        content.AppendLine("def exercise():");
        for (var i = 0; i < distinctReferenceNames; i++)
            content.AppendLine($"    target_{i}()");
        InsertIndexedFile("src/issue4581_hotspots.py", "python", content.ToString());

        using (var aggregateCardinality = _db.Connection.CreateCommand())
        {
            aggregateCardinality.CommandText = "SELECT COUNT(*) FROM hotspot_reference_counts";
            Assert.True((long)aggregateCardinality.ExecuteScalar()! > 1_000);
        }

        var fileProfile = CaptureHotspotProfile(() =>
        {
            var results = _reader.GetFileSymbolHotspots(
                limit: 1,
                kind: "function",
                lang: "python",
                pathPatterns: ["src/issue4581_hotspots.py"],
                excludePathPatterns: null,
                excludeTests: false);
            Assert.Single(results);
        });
        var nameProfile = CaptureHotspotProfile(() =>
        {
            var results = _reader.GetGroupedSymbolHotspots(
                limit: 1,
                kind: "function",
                lang: "python",
                pathPatterns: ["src/issue4581_hotspots.py"],
                excludePathPatterns: null,
                excludeTests: false);
            Assert.Single(results);
        });

        AssertBoundedHotspotPlan(fileProfile, "file");
        AssertBoundedHotspotPlan(nameProfile, "name");
    }

    [Fact]
    public void AggregateAndRawFallback_DeduplicateCanonicalRawAliases_Issue4581()
    {
        var targetFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "docs/guide.md",
            Lang = "markdown",
            Modified = DateTime.UtcNow,
        });
        _writer.InsertSymbols(
        [
            new CodeIndex.Models.SymbolRecord
            {
                FileId = targetFileId,
                Kind = "function",
                Name = "guide",
                Line = 1,
            },
        ]);
        var callerFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "docs/caller.md",
            Lang = "markdown",
            Modified = DateTime.UtcNow,
        });
        _writer.InsertReferences(
        [
            new CodeIndex.Models.ReferenceRecord { FileId = callerFileId, SymbolName = "guide#first", ReferenceKind = "call", Line = 5, Column = 3, Context = "[first](guide#first)" },
            new CodeIndex.Models.ReferenceRecord { FileId = callerFileId, SymbolName = "guide#second", ReferenceKind = "call", Line = 5, Column = 3, Context = "[second](guide#second)" },
        ]);

        using var aggregateReader = new DbReader(_db.Connection);
        var aggregate = Assert.Single(aggregateReader.GetGroupedSymbolHotspots(10, "function", "markdown", null, null, false));
        Assert.Equal(1, aggregate.ReferenceCount);

        using (var demote = _db.Connection.CreateCommand())
        {
            demote.CommandText = $"PRAGMA user_version = {_db.GetUserVersion() & ~DbContext.HotspotReferenceAggregateReadyFlag}";
            demote.ExecuteNonQuery();
        }
        using var fallbackReader = new DbReader(_db.Connection);
        var fallback = Assert.Single(fallbackReader.GetGroupedSymbolHotspots(10, "function", "markdown", null, null, false));
        Assert.Equal(aggregate.ReferenceCount, fallback.ReferenceCount);
        Assert.Equal(aggregate.ReferenceScore, fallback.ReferenceScore);
    }

    [Fact]
    public void DeleteFileData_RefreshesCrossFileReferenceLineDependents_Issue4581()
    {
        var lineOwnerId = _writer.UpsertFile(new CodeIndex.Models.FileRecord { Path = "src/context.sql", Lang = "sql", Modified = DateTime.UtcNow });
        var callerId = _writer.UpsertFile(new CodeIndex.Models.FileRecord { Path = "src/caller.sql", Lang = "sql", Modified = DateTime.UtcNow });
        _writer.InsertReferences(
        [
            new CodeIndex.Models.ReferenceRecord { FileId = callerId, SymbolName = "target", ReferenceKind = "call", Line = 1, Column = 6, Context = "target" },
        ]);
        using (var crossFileLine = _db.Connection.CreateCommand())
        {
            crossFileLine.CommandText = """
                INSERT INTO reference_lines(file_id, line, context) VALUES (@owner, 1, 'EXEC dbo.target');
                UPDATE symbol_references
                SET context = NULL,
                    reference_line_id = last_insert_rowid()
                WHERE file_id = @caller;
                """;
            crossFileLine.Parameters.AddWithValue("@owner", lineOwnerId);
            crossFileLine.Parameters.AddWithValue("@caller", callerId);
            crossFileLine.ExecuteNonQuery();
        }
        using (var demote = _db.Connection.CreateCommand())
        {
            demote.CommandText = $"PRAGMA user_version = {_db.GetUserVersion() & ~DbContext.HotspotReferenceAggregateReadyFlag}";
            demote.ExecuteNonQuery();
        }
        _db.InitializeSchema();

        _writer.DeleteFileData(lineOwnerId);

        string aggregateName;
        using (var aggregate = _db.Connection.CreateCommand())
        {
            aggregate.CommandText = "SELECT symbol_name FROM hotspot_reference_counts WHERE file_id = @caller";
            aggregate.Parameters.AddWithValue("@caller", callerId);
            aggregateName = Assert.IsType<string>(aggregate.ExecuteScalar());
        }
        using (var raw = _db.Connection.CreateCommand())
        {
            raw.CommandText = "SELECT sql_resolve_reference_name_at(symbol_name, context, container_name, column_number) FROM symbol_references WHERE file_id = @caller";
            raw.Parameters.AddWithValue("@caller", callerId);
            Assert.Equal(Assert.IsType<string>(raw.ExecuteScalar()), aggregateName);
        }
    }

    [Fact]
    public void FileBatchCleanup_DemotesReferenceIdentityContract_Issue4581()
    {
        var retainedId = _writer.UpsertFile(new CodeIndex.Models.FileRecord { Path = "src/retained.cs", Lang = "csharp", Modified = DateTime.UtcNow });
        var removedId = _writer.UpsertFile(new CodeIndex.Models.FileRecord { Path = "src/removed.cs", Lang = "csharp", Modified = DateTime.UtcNow });
        _writer.InsertSymbols(
        [
            new CodeIndex.Models.SymbolRecord { FileId = retainedId, Kind = "function", Name = "Shared", Line = 1 },
            new CodeIndex.Models.SymbolRecord { FileId = removedId, Kind = "function", Name = "Shared", Line = 1 },
        ]);
        _writer.MarkReferenceIdentityContractReady();
        Assert.True(_writer.ReferenceIdentityContractMatchesCurrent());

        Assert.True(_writer.PurgeFilesOutsideRetainedSet(new HashSet<string>(StringComparer.Ordinal) { "src/retained.cs" }) >= 1);

        Assert.False(_writer.ReferenceIdentityContractMatchesCurrent());
    }

    [Fact]
    public void GroupedSqlHotspots_DoNotDoubleCountSingleSegmentReferences_Issue4581()
    {
        InsertIndexedFile(
            "src/issue4581_sql_caller.sql",
            "sql",
            """
            CREATE PROCEDURE host AS
            BEGIN
                EXEC target;
            END
            GO
            """);
        InsertIndexedFile(
            "src/issue4581_sql_target.sql",
            "sql",
            """
            CREATE PROCEDURE target AS
            BEGIN
                SELECT 1;
            END
            GO
            """);

        var hotspot = Assert.Single(
            _reader.GetGroupedSymbolHotspots(
                limit: 10,
                kind: "function",
                lang: "sql",
                pathPatterns: ["src/issue4581_sql_*.sql"],
                excludePathPatterns: null,
                excludeTests: false),
            item => item.Symbol.Name == "target");

        Assert.Equal(1, hotspot.ReferenceCount);
    }

    [Fact]
    public void CancelledAggregateRefresh_DemotesTrustUntilBackfill_Issue4581()
    {
        var fileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/issue4581_cancelled.py",
            Lang = "python",
            Size = 32,
            Lines = 1,
            Modified = DateTime.UtcNow,
        });
        using var cancellation = new CancellationTokenSource();
        DbWriter.BatchProgressCheckpointForTesting = checkpoint =>
        {
            if (checkpoint.Operation == "insert_references" && checkpoint.RowsProcessed == 1)
                cancellation.Cancel();
        };
        try
        {
            Assert.Throws<OperationCanceledException>(() => _writer.InsertReferences(
            [
                new CodeIndex.Models.ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "target",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = "target()",
                },
            ], cancellation.Token));
        }
        finally
        {
            DbWriter.BatchProgressCheckpointForTesting = null;
        }

        Assert.Equal(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
        Assert.NotEqual(
            0,
            _db.GetUserVersion() & DbContext.HotspotReferenceAggregateStorageContractFlag);
        using (var degradedReader = new DbReader(_db.Connection))
            Assert.False(degradedReader._hasHotspotReferenceCountsTable);

        var secondFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/issue4581_after_cancel.py",
            Lang = "python",
            Size = 32,
            Lines = 1,
            Modified = DateTime.UtcNow,
        });
        _writer.InsertReferences(
        [
            new CodeIndex.Models.ReferenceRecord
            {
                FileId = secondFileId,
                SymbolName = "second_target",
                ReferenceKind = "call",
                Line = 1,
                Column = 1,
                Context = "second_target()",
            },
        ]);
        Assert.Equal(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);

        _db.InitializeSchema();

        Assert.NotEqual(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
        using var check = _db.Connection.CreateCommand();
        check.CommandText = "SELECT reference_count FROM hotspot_reference_counts WHERE file_id = @file_id";
        check.Parameters.AddWithValue("@file_id", fileId);
        Assert.Equal(1L, (long)Assert.IsType<long>(check.ExecuteScalar()));
    }

    [Fact]
    public void CancellationDuringAggregateSql_InterruptsAndDemotesTrust_Issue4581()
    {
        var fileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/issue4581_interrupt.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        var seed = Enumerable.Range(1, 2_000)
            .Select(i => new CodeIndex.Models.ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"target_{i}",
                ReferenceKind = "call",
                Line = i,
                Column = 1,
                Context = $"target_{i}()",
            })
            .ToList();
        _writer.InsertReferences(seed);

        using var cancellation = new CancellationTokenSource();
        var sqliteStarted = false;
        DbWriter.HotspotAggregateRefreshExecutingForTesting = () =>
        {
            sqliteStarted = true;
            cancellation.Cancel();
        };
        try
        {
            Assert.Throws<OperationCanceledException>(() => _writer.InsertReferences(
            [
                new CodeIndex.Models.ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = "late_target",
                    ReferenceKind = "call",
                    Line = 3_000,
                    Column = 1,
                    Context = "late_target()",
                },
            ], cancellation.Token));
        }
        finally
        {
            DbWriter.HotspotAggregateRefreshExecutingForTesting = null;
        }

        Assert.True(sqliteStarted);
        Assert.Equal(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
    }

    [Fact]
    public void ReferencePurges_MaintainAggregateCounts_Issue4581()
    {
        var pythonFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/issue4581_supported.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        var obsoleteFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/issue4581_obsolete.ext",
            Lang = "obsolete",
            Modified = DateTime.UtcNow,
        });
        _writer.InsertReferences(
        [
            new CodeIndex.Models.ReferenceRecord
            {
                FileId = pythonFileId,
                SymbolName = "supported_target",
                ReferenceKind = "call",
                Line = 1,
                Column = 1,
                Context = "supported_target()",
            },
            new CodeIndex.Models.ReferenceRecord
            {
                FileId = obsoleteFileId,
                SymbolName = "obsolete_target",
                ReferenceKind = "call",
                Line = 1,
                Column = 1,
                Context = "obsolete_target()",
            },
        ]);

        using (var beforePurge = _db.Connection.CreateCommand())
        {
            beforePurge.CommandText = """
                SELECT COUNT(*)
                FROM symbol_references sr
                JOIN files f ON f.id = sr.file_id
                WHERE f.lang = @lang
                """;
            beforePurge.Parameters.AddWithValue("@lang", "python");
            Assert.Equal(1L, (long)Assert.IsType<long>(beforePurge.ExecuteScalar()));
            beforePurge.Parameters["@lang"].Value = "obsolete";
            Assert.Equal(1L, (long)Assert.IsType<long>(beforePurge.ExecuteScalar()));
        }

        var supportedLanguages = new List<string>();
        using (var languages = _db.Connection.CreateCommand())
        {
            languages.CommandText = "SELECT DISTINCT lang FROM files WHERE lang IS NOT NULL AND lang <> 'obsolete'";
            using var reader = languages.ExecuteReader();
            while (reader.Read())
                supportedLanguages.Add(reader.GetString(0));
        }

        Assert.Equal(1, _writer.PurgeUnsupportedReferences(supportedLanguages));
        AssertAggregateAndRawReferenceCounts(pythonFileId, expected: 1);
        AssertAggregateAndRawReferenceCounts(obsoleteFileId, expected: 0);

        using (var count = _db.Connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM symbol_references";
            var referencesBeforePurgeAll = checked((int)(long)count.ExecuteScalar()!);
            Assert.Equal(referencesBeforePurgeAll, _writer.PurgeAllReferences());
        }
        AssertAggregateAndRawReferenceCounts(pythonFileId, expected: 0);
        AssertAggregateAndRawReferenceCounts(obsoleteFileId, expected: 0);
    }

    private void AssertAggregateAndRawReferenceCounts(long fileId, long expected)
    {
        using var check = _db.Connection.CreateCommand();
        check.Parameters.AddWithValue("@file_id", fileId);
        check.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @file_id";
        Assert.Equal(expected, (long)Assert.IsType<long>(check.ExecuteScalar()));
        check.CommandText = "SELECT COALESCE(SUM(reference_count), 0) FROM hotspot_reference_counts WHERE file_id = @file_id";
        Assert.Equal(expected, (long)Assert.IsType<long>(check.ExecuteScalar()));
    }

    private static List<QueryProfileEntry> CaptureHotspotProfile(Action query)
    {
        DbDebug.ResetForTesting();
        try
        {
            DbDebug.BeginProfile();
            query();
            return DbDebug.EndProfile();
        }
        finally
        {
            _ = DbDebug.EndProfile();
        }
    }

    private static void AssertBoundedHotspotPlan(List<QueryProfileEntry> profile, string grouping)
    {
        var hotspotQueries = profile
            .Where(entry => entry.QueryPlan.Any(row =>
                row.Detail.Contains("hotspot_reference_counts", StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(hotspotQueries);
        Assert.DoesNotContain(
            hotspotQueries.SelectMany(entry => entry.QueryPlan),
            row => row.Detail.Contains("symbol_references", StringComparison.Ordinal));
        Assert.Contains(
            hotspotQueries.SelectMany(entry => entry.QueryPlan),
            row => row.Detail.Contains("idx_hotspot_reference_counts_rank", StringComparison.Ordinal));

        var elapsed = TimeSpan.FromMilliseconds(hotspotQueries.Sum(entry => entry.ElapsedMs));
        Assert.True(
            elapsed < TestDeterminism.DefaultTimeout,
            $"Limited {grouping} hotspot SQL took {elapsed}.");
    }
}

[Collection("SQLite pool sensitive")]
public class HotspotReferenceAggregateMigrationTests
{
    [Fact]
    public void TryMigrateForRead_BackfillsMaintainedHotspotCounts_Issue4581()
    {
        var dbDir = TestProjectHelper.CreateTempProject("codeindex_hotspot_aggregate_migration");
        var dbPath = Path.Combine(dbDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                using var seed = db.Connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO files(path, lang) VALUES ('src/legacy.py', 'python');
                    INSERT INTO symbol_references(file_id, symbol_name, reference_kind, line, column_number)
                    VALUES
                        (1, 'target', 'call', 10, 5),
                        (1, 'target', 'call', 11, 5);
                    DROP TABLE hotspot_reference_counts;
                    """;
                seed.ExecuteNonQuery();
            }

            using var migrated = new DbContext(DbOpenIntent.WriteIndex, dbPath);
            migrated.TryMigrateForRead();

            Assert.Null(migrated.LastMigrationFailure);
            using var check = migrated.Connection.CreateCommand();
            check.CommandText = """
                SELECT reference_count
                FROM hotspot_reference_counts
                WHERE file_id = 1 AND lang = 'python' AND symbol_name = 'target'
                """;
            Assert.Equal(2L, (long)Assert.IsType<long>(check.ExecuteScalar()));
            Assert.NotEqual(
                0,
                migrated.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);

            new DbWriter(migrated).ClearReadyFlags();
            Assert.Equal(
                DbContext.HotspotReferenceAggregateFlags,
                migrated.GetUserVersion());
            migrated.ClearReadyFlags();
            Assert.Equal(
                DbContext.HotspotReferenceAggregateFlags,
                migrated.GetUserVersion());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(dbDir);
        }
    }
}
