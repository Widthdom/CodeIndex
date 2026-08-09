using System.Text;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class DbReaderTests
{
    [Theory]
    [InlineData(63, 63, false)]
    [InlineData(64, 106, true)]
    [InlineData(64, 107, false)]
    [InlineData(600, 1_000, true)]
    [InlineData(599, 1_000, false)]
    [InlineData(int.MaxValue, int.MaxValue, true)]
    public void HotspotAggregateIndexDeferral_UsesBoundedSixtyPercentThreshold(
        int dirtyFileCount,
        int indexedFileCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            DbWriter.ShouldDeferHotspotAggregateSecondaryIndexes(
                dirtyFileCount,
                indexedFileCount));
    }

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
    public void DeferredAggregateRefresh_RefreshesCrossFileReferenceLineDependentsOnce()
    {
        var lineOwnerId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-context.sql",
            Lang = "sql",
            Modified = DateTime.UtcNow,
        });
        var callerId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-caller.sql",
            Lang = "sql",
            Modified = DateTime.UtcNow,
        });
        _writer.InsertReferences(
        [
            new CodeIndex.Models.ReferenceRecord
            {
                FileId = callerId,
                SymbolName = "target",
                ReferenceKind = "call",
                Line = 1,
                Column = 6,
                Context = "target",
            },
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

        var previousStatementHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var previousDirtyHook = DbWriter.DeferredHotspotDirtyFilesForTesting;
        var refreshStatements = 0;
        HashSet<long>? refreshedFileIds = null;
        try
        {
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = () =>
            {
                refreshStatements++;
                previousStatementHook?.Invoke();
            };
            DbWriter.DeferredHotspotDirtyFilesForTesting = dirtyFileIds =>
            {
                refreshedFileIds = dirtyFileIds.ToHashSet();
                previousDirtyHook?.Invoke(dirtyFileIds);
            };

            using var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh();
            using (var transaction = _writer.BeginTransaction())
            {
                _writer.DeleteFileData(lineOwnerId);
                transaction.Commit();
            }
            deferredRefresh.Complete(CancellationToken.None);

            Assert.Equal(1, refreshStatements);
            Assert.NotNull(refreshedFileIds);
            Assert.Contains(lineOwnerId, refreshedFileIds!);
            Assert.Contains(callerId, refreshedFileIds!);
            using var aggregate = _db.Connection.CreateCommand();
            aggregate.CommandText = "SELECT symbol_name FROM hotspot_reference_counts WHERE file_id = @caller";
            aggregate.Parameters.AddWithValue("@caller", callerId);
            var aggregateName = Assert.IsType<string>(aggregate.ExecuteScalar());
            aggregate.CommandText = "SELECT sql_resolve_reference_name_at(symbol_name, context, container_name, column_number) FROM symbol_references WHERE file_id = @caller";
            Assert.Equal(Assert.IsType<string>(aggregate.ExecuteScalar()), aggregateName);
        }
        finally
        {
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousStatementHook;
            DbWriter.DeferredHotspotDirtyFilesForTesting = previousDirtyHook;
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

    [Fact]
    public void DeferredAggregateRefresh_RefreshesMixedLanguageFilesOnce()
    {
        var inputs = new[]
        {
            (Path: "src/deferred.cs", Lang: "csharp", Symbol: "Target.Run", Context: "Target.Run();"),
            (Path: "src/deferred.py", Lang: "python", Symbol: "target", Context: "target()"),
            (Path: "docs/deferred.md", Lang: "markdown", Symbol: "guide#usage", Context: "[usage](guide#usage)"),
            (Path: "src/deferred.sql", Lang: "sql", Symbol: "dbo.target", Context: "EXEC dbo.target"),
        };
        var fileIds = inputs
            .Select(input => _writer.UpsertFile(new CodeIndex.Models.FileRecord
            {
                Path = input.Path,
                Lang = input.Lang,
                Modified = DateTime.UtcNow,
            }))
            .ToArray();
        var previousReadinessHook = DbWriter.HotspotAggregateReadinessCheckedForTesting;
        var previousStatementHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var previousDirtyHook = DbWriter.DeferredHotspotDirtyFilesForTesting;
        var readinessChecks = 0;
        var refreshStatements = 0;
        string[]? indexNamesDuringRefresh = null;
        HashSet<long>? refreshedFileIds = null;
        try
        {
            DbWriter.HotspotAggregateReadinessCheckedForTesting = () =>
            {
                readinessChecks++;
                previousReadinessHook?.Invoke();
            };
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = () =>
            {
                refreshStatements++;
                indexNamesDuringRefresh = ReadHotspotReferenceAggregateIndexNames();
                previousStatementHook?.Invoke();
            };
            DbWriter.DeferredHotspotDirtyFilesForTesting = dirtyFileIds =>
            {
                refreshedFileIds = dirtyFileIds.ToHashSet();
                previousDirtyHook?.Invoke(dirtyFileIds);
            };

            using var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh();
            for (var i = 0; i < inputs.Length; i++)
            {
                using var transaction = _writer.BeginTransaction();
                _writer.InsertReferencesInAtomicFileScope(
                [
                    new CodeIndex.Models.ReferenceRecord
                    {
                        FileId = fileIds[i],
                        SymbolName = inputs[i].Symbol,
                        ReferenceKind = "call",
                        Line = 1,
                        Column = 1,
                        Context = inputs[i].Context,
                    },
                ], refreshMutualRecursionFlags: false, CancellationToken.None);
                transaction.Commit();
            }
            deferredRefresh.Complete(CancellationToken.None);

            Assert.Equal(inputs.Length, readinessChecks);
            Assert.Equal(1, refreshStatements);
            Assert.Equal(GetHotspotReferenceAggregateIndexNames(), indexNamesDuringRefresh);
            Assert.NotNull(refreshedFileIds);
            Assert.True(fileIds.ToHashSet().SetEquals(refreshedFileIds!));
            Assert.NotEqual(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
            using var count = _db.Connection.CreateCommand();
            count.Parameters.Add("@file_id", SqliteType.Integer);
            foreach (var fileId in fileIds)
            {
                count.Parameters["@file_id"].Value = fileId;
                count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id = @file_id";
                var rawCount = (long)count.ExecuteScalar()!;
                count.CommandText = "SELECT COALESCE(SUM(reference_count), 0) FROM hotspot_reference_counts WHERE file_id = @file_id";
                Assert.Equal(rawCount, (long)count.ExecuteScalar()!);
            }
        }
        finally
        {
            DbWriter.HotspotAggregateReadinessCheckedForTesting = previousReadinessHook;
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousStatementHook;
            DbWriter.DeferredHotspotDirtyFilesForTesting = previousDirtyHook;
        }
    }

    [Fact]
    public void DeferredAggregateRefresh_UsesOneStatementBeyondDirtyIdInsertBatch()
    {
        const int fileCount = 1_001;
        var fileIds = new long[fileCount];
        using (var seedTransaction = _writer.BeginTransaction())
        {
            for (var i = 0; i < fileCount; i++)
            {
                fileIds[i] = _writer.InsertNewFile(new CodeIndex.Models.FileRecord
                {
                    Path = $"src/deferred-bulk-{i:D4}.py",
                    Lang = "python",
                    Modified = DateTime.UtcNow,
                });
            }
            seedTransaction.Commit();
        }
        var references = fileIds
            .Select((fileId, index) => BuildDeferredReference(fileId, $"bulk_target_{index}"))
            .ToArray();
        var previousStatementHook = DbWriter.HotspotAggregateRefreshStatementExecutingForTesting;
        var refreshStatements = 0;
        string[]? indexNamesDuringRefresh = null;
        try
        {
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = () =>
            {
                refreshStatements++;
                indexNamesDuringRefresh = ReadHotspotReferenceAggregateIndexNames();
                previousStatementHook?.Invoke();
            };

            using var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh(
                deferSecondaryIndexes: true);
            using (var transaction = _writer.BeginTransaction())
            {
                _writer.InsertReferencesInAtomicFileScope(
                    references,
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                transaction.Commit();
            }
            deferredRefresh.Complete(CancellationToken.None);

            Assert.Equal(1, refreshStatements);
            Assert.Empty(Assert.IsType<string[]>(indexNamesDuringRefresh));
            Assert.Equal(
                GetHotspotReferenceAggregateIndexNames(),
                ReadHotspotReferenceAggregateIndexNames());
            using var count = _db.Connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM symbol_references WHERE file_id >= @first_file_id AND file_id <= @last_file_id";
            count.Parameters.AddWithValue("@first_file_id", fileIds[0]);
            count.Parameters.AddWithValue("@last_file_id", fileIds[^1]);
            Assert.Equal((long)fileCount, (long)count.ExecuteScalar()!);
            count.CommandText = "SELECT COALESCE(SUM(reference_count), 0) FROM hotspot_reference_counts WHERE file_id >= @first_file_id AND file_id <= @last_file_id";
            Assert.Equal((long)fileCount, (long)count.ExecuteScalar()!);

            // Reuse the reference persistence command shapes that were prepared before
            // the DROP/CREATE cycle. SQLite must reprepare them without leaking SQLITE_SCHEMA.
            using (var reuseTransaction = _writer.BeginTransaction())
            {
                _writer.InsertReferencesInAtomicFileScope(
                    [BuildDeferredReference(fileIds[0], "after_index_rebuild")],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                reuseTransaction.Commit();
            }
            AssertAggregateAndRawReferenceCounts(fileIds[0], expected: 2);
        }
        finally
        {
            DbWriter.HotspotAggregateRefreshStatementExecutingForTesting = previousStatementHook;
        }
    }

    [Fact]
    public void DeferredAggregateRefresh_DiscardsRolledBackFileCheckpoint()
    {
        var successfulFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-success.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        var failedFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-failed.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        var previousDirtyHook = DbWriter.DeferredHotspotDirtyFilesForTesting;
        HashSet<long>? refreshedFileIds = null;
        try
        {
            DbWriter.DeferredHotspotDirtyFilesForTesting = dirtyFileIds =>
            {
                refreshedFileIds = dirtyFileIds.ToHashSet();
                previousDirtyHook?.Invoke(dirtyFileIds);
            };

            using var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh();
            using (var failedTransaction = _writer.BeginTransaction())
            {
                _writer.InsertReferencesInAtomicFileScope(
                    [BuildDeferredReference(failedFileId, "failed_target")],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
            }
            using (var successfulTransaction = _writer.BeginTransaction())
            {
                _writer.InsertReferencesInAtomicFileScope(
                    [BuildDeferredReference(successfulFileId, "successful_target")],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                successfulTransaction.Commit();
            }
            deferredRefresh.Complete(CancellationToken.None);

            Assert.NotNull(refreshedFileIds);
            Assert.Equal([successfulFileId], refreshedFileIds!.OrderBy(static id => id));
            AssertAggregateAndRawReferenceCounts(successfulFileId, expected: 1);
            AssertAggregateAndRawReferenceCounts(failedFileId, expected: 0);
            Assert.NotEqual(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
        }
        finally
        {
            DbWriter.DeferredHotspotDirtyFilesForTesting = previousDirtyHook;
        }
    }

    [Fact]
    public void DeferredAggregateRefresh_PreservesPriorFalseReadiness()
    {
        var fileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-prior-false.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        using (var demote = _db.Connection.CreateCommand())
        {
            demote.CommandText = $"PRAGMA user_version = {_db.GetUserVersion() & ~DbContext.HotspotReferenceAggregateReadyFlag}";
            demote.ExecuteNonQuery();
        }

        using (var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh())
        {
            using var transaction = _writer.BeginTransaction();
            _writer.InsertReferencesInAtomicFileScope(
                [BuildDeferredReference(fileId, "prior_false_target")],
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            transaction.Commit();
            deferredRefresh.Complete(CancellationToken.None);
        }

        Assert.Equal(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
        AssertAggregateAndRawReferenceCounts(fileId, expected: 1);
    }

    [Fact]
    public void DeferredAggregateRefresh_CancellationLeavesTrustDemoted()
    {
        var fileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-cancel.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        using var cancellation = new CancellationTokenSource();
        var previousRefreshHook = DbWriter.HotspotAggregateRefreshExecutingForTesting;
        try
        {
            using var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh(
                deferSecondaryIndexes: true);
            using (var transaction = _writer.BeginTransaction())
            {
                _writer.InsertReferencesInAtomicFileScope(
                    [BuildDeferredReference(fileId, "cancel_target")],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                transaction.Commit();
            }
            DbWriter.HotspotAggregateRefreshExecutingForTesting = () =>
            {
                previousRefreshHook?.Invoke();
                cancellation.Cancel();
            };

            using (var outerTransaction = _writer.BeginTransaction())
            {
                Assert.Throws<OperationCanceledException>(() => deferredRefresh.Complete(cancellation.Token));
            }
            Assert.Equal(0, _db.GetUserVersion() & DbContext.HotspotReferenceAggregateReadyFlag);
            Assert.Equal(
                GetHotspotReferenceAggregateIndexNames(),
                ReadHotspotReferenceAggregateIndexNames());
        }
        finally
        {
            DbWriter.HotspotAggregateRefreshExecutingForTesting = previousRefreshHook;
        }
    }

    [Fact]
    public void DeferredAggregateRefresh_TracksZeroReferenceCleanupAndStaleDelete()
    {
        var zeroReferenceFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-zero.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        var staleFileId = _writer.UpsertFile(new CodeIndex.Models.FileRecord
        {
            Path = "src/deferred-stale.py",
            Lang = "python",
            Modified = DateTime.UtcNow,
        });
        _writer.InsertReferences(
        [
            BuildDeferredReference(zeroReferenceFileId, "removed_target"),
            BuildDeferredReference(staleFileId, "stale_target"),
        ]);
        var previousDirtyHook = DbWriter.DeferredHotspotDirtyFilesForTesting;
        HashSet<long>? refreshedFileIds = null;
        try
        {
            DbWriter.DeferredHotspotDirtyFilesForTesting = dirtyFileIds =>
            {
                refreshedFileIds = dirtyFileIds.ToHashSet();
                previousDirtyHook?.Invoke(dirtyFileIds);
            };

            using var deferredRefresh = _writer.BeginDeferredHotspotReferenceAggregateRefresh();
            using (var updateTransaction = _writer.BeginTransaction())
            {
                _writer.UpsertFile(
                    new CodeIndex.Models.FileRecord
                    {
                        Path = "src/deferred-zero.py",
                        Lang = "python",
                        Modified = DateTime.UtcNow.AddSeconds(1),
                    },
                    out _);
                _writer.InsertReferencesInAtomicFileScope(
                    [],
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                updateTransaction.Commit();
            }
            Assert.True(_writer.DeleteFileByPath("src/deferred-stale.py"));
            deferredRefresh.Complete(CancellationToken.None);

            Assert.NotNull(refreshedFileIds);
            Assert.True(
                new[] { zeroReferenceFileId, staleFileId }
                    .ToHashSet()
                    .SetEquals(refreshedFileIds!));
            AssertAggregateAndRawReferenceCounts(zeroReferenceFileId, expected: 0);
            using var stale = _db.Connection.CreateCommand();
            stale.CommandText = "SELECT COUNT(*) FROM files WHERE id = @file_id";
            stale.Parameters.AddWithValue("@file_id", staleFileId);
            Assert.Equal(0L, (long)stale.ExecuteScalar()!);
        }
        finally
        {
            DbWriter.DeferredHotspotDirtyFilesForTesting = previousDirtyHook;
        }
    }

    private static CodeIndex.Models.ReferenceRecord BuildDeferredReference(long fileId, string symbolName)
        => new()
        {
            FileId = fileId,
            SymbolName = symbolName,
            ReferenceKind = "call",
            Line = 1,
            Column = 1,
            Context = symbolName + "()",
        };

    private static string[] GetHotspotReferenceAggregateIndexNames()
        => HotspotReferenceAggregateSql.Indexes
            .Select(static index => index.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private string[] ReadHotspotReferenceAggregateIndexNames()
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'index'
              AND tbl_name = 'hotspot_reference_counts'
              AND name NOT LIKE 'sqlite_autoindex_%'
            ORDER BY name
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
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
