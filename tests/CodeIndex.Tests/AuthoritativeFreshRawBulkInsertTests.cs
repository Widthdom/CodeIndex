using System.Globalization;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class AuthoritativeFreshRawBulkInsertTests : IDisposable
{
    private const long ExpectedLargeFileId = 5_000_000_001L;
    private readonly string _projectRoot;
    private readonly DbContext _db;
    private readonly DbWriter _writer;

    public AuthoritativeFreshRawBulkInsertTests()
    {
        _projectRoot = TestProjectHelper.CreateTempProject("cdidx_authoritative_fresh_raw");
        _db = new DbContext(
            DbOpenIntent.WriteIndex,
            Path.Combine(_projectRoot, "codeindex.db"));
        _db.InitializeSchema();
        _writer = new DbWriter(_db.Connection);
    }

    [Fact]
    public void Scope_RequiresFreshCallerOwnedTransactionAndDetachesAfterCompletion()
    {
        Assert.Null(_writer.BeginAuthoritativeFreshBulkInsertScope(
            enabled: false,
            CancellationToken.None));

        using (var transaction = _writer.BeginTransaction())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));
        }

        using (var ordinaryGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true))
        using (var transaction = _writer.BeginTransaction())
        {
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));
        }

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));

            using var transaction = _writer.BeginTransaction();
            Assert.True(_writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            Assert.Throws<InvalidOperationException>(() =>
                _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None));

            var fileId = InsertNewFile("src/scope.cs");
            Exception? foreignThreadException = null;
            using var foreignThreadFinished = new ManualResetEventSlim();
            var foreignThread = new Thread(() =>
            {
                foreignThreadException = Record.Exception(() =>
                    _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1)));
                foreignThreadFinished.Set();
            });
            foreignThread.Start();
            Assert.True(foreignThreadFinished.Wait(TimeSpan.FromSeconds(5)));
            var ownershipException = Assert.IsType<InvalidOperationException>(foreignThreadException);
            Assert.Contains(
                "owned by this DbWriter",
                ownershipException.Message,
                StringComparison.Ordinal);
            raw.Complete();
            raw.Complete();

            // The completed scope is detached before graph/index work and ordinary provider
            // writes on the same transaction remain usable.
            _writer.InsertIssuesForNewFile(fileId, [CreateIssue(1)]);
            transaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(1, observedStats.PrepareCount);
        Assert.Equal(1, observedStats.FinalizeCount);
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM file_issues"));
    }

    [Fact]
    public void Scope_CoalescesResourceGenerationAndRestoresTriggersAcrossCommitAndRollback()
    {
        var initialGeneration = ResourceListGeneration();
        Assert.Equal(3L, ResourceListGenerationTriggerCount());

        using (var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            _ = InsertNewFile("src/generation-rolled-back.cs");
        }

        Assert.Equal(initialGeneration, ResourceListGeneration());
        Assert.Equal(3L, ResourceListGenerationTriggerCount());
        Assert.Equal(
            0L,
            ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/generation-rolled-back.cs'"));

        using (var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            raw.Complete();
            transaction.Commit();
        }

        Assert.Equal(initialGeneration, ResourceListGeneration());
        Assert.Equal(3L, ResourceListGenerationTriggerCount());

        using (var freshGraph = _writer.BeginReferenceGraphRefreshScope(
                   forceFullRefresh: true,
                   useFreshReferenceResolutionDefaults: true))
        using (var transaction = _writer.BeginTransaction())
        using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                   enabled: true,
                   CancellationToken.None)!)
        {
            _ = InsertNewFile("src/generation-a.cs");
            _ = InsertNewFile("src/generation-b.cs");
            _ = InsertNewFile("src/generation-c.cs");
            raw.Complete();
            transaction.Commit();
        }

        Assert.Equal(initialGeneration + 1, ResourceListGeneration());
        Assert.Equal(3L, ResourceListGenerationTriggerCount());

        _ = _writer.UpsertFile(new FileRecord
        {
            Path = "src/generation-provider.cs",
            Lang = "csharp",
            Size = 100,
            Lines = 10,
            Checksum = "generation-provider",
            Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
        });
        Assert.Equal(initialGeneration + 2, ResourceListGeneration());
    }

    [Fact]
    public void BatchStatements_PreserveShapesUnicodeNullsInt64AndProviderExclusions()
    {
        PrimeSequencesForInt64Returning();
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        var batchWork = new List<DbWriter.DbWriterBatchStatement>();
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousBatchHook = DbWriter.BatchStatementExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.BatchStatementExecutingForTesting = work =>
            {
                batchWork.Add(work);
                previousBatchHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            Assert.True(_writer.CanUseFreshReferenceResolutionDefaultsInCurrentTransaction());
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = _writer.InsertNewFile(new FileRecord
            {
                Path = "src/raw-shapes.cs",
                Lang = null,
                Size = 5_000_000_123L,
                Lines = 100,
                Checksum = "雪😀a\0β",
                Modified = new DateTime(2026, 8, 23, 12, 34, 56, 789, DateTimeKind.Utc)
                    .AddTicks(1234),
            });
            Assert.Equal(ExpectedLargeFileId, fileId);

            var chunks = Enumerable.Range(0, 205)
                .Select(index => new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = index,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    Content = index == 0 ? "雪😀a\0β" : $"chunk_{index}",
                })
                .ToArray();
            var symbols = Enumerable.Range(0, 41)
                .Select(index => new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = $"target_{index}",
                    Line = index + 1,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    StartColumn = index == 0 ? null : index,
                    Signature = index == 0 ? null : $"void target_{index}()",
                    IsPartialDeclaration = index == 0 ? null : false,
                    IsMetadataTarget = index == 0 ? null : false,
                })
                .ToArray();
            var issues = Enumerable.Range(0, 171)
                .Select(index => CreateIssue(index + 1))
                .ToArray();
            var references = Enumerable.Range(0, 73)
                .Select(index => new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = $"target_{index % symbols.Length}",
                    ReferenceKind = "call",
                    Line = index + 1,
                    Column = index + 1,
                    SpanLength = index == 0 ? 0 : index + 1,
                    Context = index == 0
                        ? "雪😀a\0β"
                        : $"target_{index % symbols.Length}();",
                    ContainerKind = index == 0 ? null : "function",
                    ContainerName = index == 0 ? null : "caller",
                    IsSelfReference = true,
                    IsMutualRecursion = true,
                })
                .ToArray();

            _writer.InsertChunks(chunks, CancellationToken.None);
            _writer.InsertSymbols(symbols, CancellationToken.None);
            _writer.InsertIssuesForNewFile(fileId, issues);
            _writer.InsertReferencesForNewFilesInAtomicFileScope(
                references,
                refreshMutualRecursionFlags: false,
                CancellationToken.None);

            var rawCountBeforeProviderExclusions = rawWork.Count;
            var providerFileId = _writer.UpsertFile(new FileRecord
            {
                Path = "src/provider-exclusions.cs",
                Lang = "csharp",
                Size = 100,
                Lines = 100,
                Checksum = "provider-exclusions",
                Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
            });
            _writer.InsertIssues(providerFileId, [CreateIssue(1)]);
            _writer.InsertReferencesInAtomicFileScope(
                [new ReferenceRecord
                {
                    FileId = providerFileId,
                    SymbolName = "provider_target",
                    ReferenceKind = "call",
                    Line = 1,
                    Column = 1,
                    Context = "provider_target();",
                }],
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            Assert.Equal(rawCountBeforeProviderExclusions, rawWork.Count);

            raw.Complete();
            transaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.BatchStatementExecutingForTesting = previousBatchHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal([(1, 7)], RowsAndParameters("insert_files"));
        Assert.Equal([(102, 510), (102, 510), (1, 5)], RowsAndParameters("insert_chunks"));
        Assert.Equal([(20, 500), (20, 500), (1, 25)], RowsAndParameters("insert_symbols"));
        Assert.Equal([(85, 510), (85, 510), (1, 6)], RowsAndParameters("insert_issues"));
        Assert.Equal([(73, 219)], RowsAndParameters("insert_reference_lines"));
        Assert.Equal([(36, 504), (36, 504), (1, 14)], RowsAndParameters("insert_references"));
        Assert.Contains(
            batchWork,
            work => work.Operation == "insert_reference_lines" && work.StatementRows == 73);
        Assert.DoesNotContain(
            batchWork,
            work => work.Operation == "insert_files");

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(32, observedStats.Capacity);
        Assert.Equal(10, observedStats.PeakCachedStatementCount);
        Assert.Equal(14, observedStats.StatementExecutionCount);
        Assert.Equal(10, observedStats.PrepareCount);
        Assert.Equal(4, observedStats.CacheHitCount);
        Assert.Equal(0, observedStats.EvictionCount);
        Assert.Equal(0, observedStats.DiscardCount);
        Assert.Equal(10, observedStats.FinalizeCount);

        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM files WHERE lang IS NULL"));
        Assert.Equal(5_000_000_123L, ScalarLong("SELECT size FROM files WHERE path = 'src/raw-shapes.cs'"));
        Assert.Equal("323032362D30382D32332031323A33343A35362E37383931323334", ScalarString("SELECT hex(CAST(modified AS BLOB)) FROM files WHERE path = 'src/raw-shapes.cs'"));
        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(checksum AS BLOB)) FROM files WHERE path = 'src/raw-shapes.cs'"));
        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0"));
        Assert.Equal(11L, ScalarLong("SELECT length(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0"));
        Assert.Equal(41L, ScalarLong($"SELECT COUNT(*) FROM symbols WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM symbols WHERE sub_kind IS NULL AND signature IS NULL AND start_column IS NULL"));
        Assert.Equal(171L, ScalarLong($"SELECT COUNT(*) FROM file_issues WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(ExpectedLargeFileId, ScalarLong("SELECT MIN(id) FROM reference_lines"));
        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(context AS BLOB)) FROM reference_lines WHERE line = 1"));
        Assert.Equal(73L, ScalarLong($"SELECT COUNT(*) FROM symbol_references WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(73L, ScalarLong($"SELECT COUNT(*) FROM symbol_references WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)} AND context IS NULL AND is_self_reference = 0 AND is_mutual_recursion = 0"));

        (int Rows, int Parameters)[] RowsAndParameters(string operation) =>
            rawWork
                .Where(work => work.Operation == operation)
                .Select(work => (work.StatementRows, work.BoundParameterCount))
                .ToArray();
    }

    [Fact]
    public void StatementCache_EvictsLeastRecentlyUsedAndFinalizesEveryShape()
    {
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousCapacity = DbWriter.AuthoritativeFreshRawStatementCacheCapacityForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawStatementCacheCapacityForTesting = 2;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/lru.cs");

            _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 6));
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 6, count: 1));
            _writer.InsertIssuesForNewFile(
                fileId,
                Enumerable.Range(0, 5).Select(index => CreateIssue(index + 1)).ToArray());
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 7, count: 6));
            raw.Complete();
            transaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawStatementCacheCapacityForTesting = previousCapacity;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(2, observedStats.Capacity);
        Assert.Equal(2, observedStats.PeakCachedStatementCount);
        Assert.Equal(5, observedStats.PrepareCount);
        Assert.Equal(0, observedStats.CacheHitCount);
        Assert.Equal(3, observedStats.EvictionCount);
        Assert.Equal(5, observedStats.FinalizeCount);
    }

    [Fact]
    public void BatchConstraint_ReplaysOnlyBadChunkRowAndReusesStatement()
    {
        var warnings = new List<string>();
        var previousWarningHook = DbWriter.BatchRowSkipWarningForTesting;
        try
        {
            DbWriter.BatchRowSkipWarningForTesting = warning =>
            {
                warnings.Add(warning);
                previousWarningHook?.Invoke(warning);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/constraint.cs");
            var chunks = CreateChunks(fileId, startIndex: 0, count: 7);
            chunks[2].FileId = -1;

            _writer.InsertChunks(chunks, CancellationToken.None);
            var (_, persistedAfterReplay, _, _) = _writer.GetCounts();
            Assert.Equal(6, persistedAfterReplay);
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 100, count: 1));
            raw.Complete();
            transaction.Commit();
        }
        finally
        {
            DbWriter.BatchRowSkipWarningForTesting = previousWarningHook;
        }

        Assert.Equal(7L, ScalarLong("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(1, _writer.BatchRowsSkipped);
        var warning = Assert.Single(warnings);
        Assert.Contains("file_id=-1", warning, StringComparison.Ordinal);
        Assert.Contains("chunk_index=2", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Interrupt_RollsBackFinalizesAndLeavesProviderConnectionReusable()
    {
        using var cancellation = new CancellationTokenSource();
        _db.Connection.CreateFunction<long>(
            "cancel_authoritative_fresh_raw",
            () =>
            {
                cancellation.Cancel();
                return 0;
            });
        Execute("""
            CREATE TEMP TRIGGER cancel_authoritative_fresh_raw_insert
            BEFORE INSERT ON chunks
            BEGIN
                SELECT cancel_authoritative_fresh_raw();
            END
            """);

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        OperationCanceledException exception;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using (var graph = _writer.BeginReferenceGraphRefreshScope(
                       forceFullRefresh: true,
                       useFreshReferenceResolutionDefaults: true))
            using (var transaction = _writer.BeginTransaction())
            using (var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                       enabled: true,
                       cancellation.Token)!)
            {
                var fileId = InsertNewFile("src/interrupted.cs");
                exception = Assert.Throws<OperationCanceledException>(() =>
                    _writer.InsertChunks(
                        CreateChunks(fileId, startIndex: 0, count: 1),
                        cancellation.Token));
            }
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM chunks"));
        Assert.NotNull(observedStats);
        Assert.False(observedStats.Completed);
        Assert.Equal(2, observedStats.FinalizeCount);

        Execute("DROP TRIGGER cancel_authoritative_fresh_raw_insert");
        using (var transaction = _writer.BeginTransaction())
        {
            var fileId = InsertNewFile("src/reusable.cs");
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1));
            transaction.Commit();
        }

        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public void HookFailure_CleansLeaseAndDisposeHookCannotMaskBodyException()
    {
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = _ =>
                throw new InvalidOperationException("raw body hook failure");
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = _ =>
                throw new InvalidOperationException("raw dispose hook failure");

            var exception = Assert.Throws<InvalidOperationException>(() =>
            {
                using var graph = _writer.BeginReferenceGraphRefreshScope(
                    forceFullRefresh: true,
                    useFreshReferenceResolutionDefaults: true);
                using var transaction = _writer.BeginTransaction();
                using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    CancellationToken.None)!;
                var fileId = InsertNewFile("src/hook-failure.cs");
                _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1));
            });

            Assert.Equal("raw body hook failure", exception.Message);
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        using var retry = _writer.BeginTransaction();
        var retryFileId = InsertNewFile("src/hook-retry.cs");
        _writer.InsertChunks(CreateChunks(retryFileId, startIndex: 0, count: 1));
        retry.Commit();
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM chunks"));
    }

    [Fact]
    public void Complete_WhenReportingHookThrows_DoesNotMarkScopeCompleted()
    {
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                throw new InvalidOperationException("raw completion hook failure");
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var transaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            var fileId = InsertNewFile("src/completion-hook-failure.cs");
            _writer.InsertChunks(CreateChunks(fileId, startIndex: 0, count: 1));

            var exception = Assert.Throws<InvalidOperationException>(raw.Complete);
            Assert.Equal("raw completion hook failure", exception.Message);
            Assert.NotNull(observedStats);
            Assert.True(observedStats.Completed);
            Assert.Throws<ObjectDisposedException>(raw.Complete);
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM chunks"));
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("null_ordinal")]
    [InlineData("duplicate_ordinal")]
    [InlineData("out_of_range_ordinal")]
    [InlineData("duplicate_id")]
    public void ReferenceLineReturningFailure_DiscardsStatementAndFileSavepointAllowsNextFile(
        string failureMode)
    {
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        long? firstReturnedId = null;
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousRowHook = DbWriter.AuthoritativeFreshRawReturningRowForTesting;
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawReturningRowForTesting = row =>
            {
                var transformed = previousRowHook?.Invoke(row) ?? row;
                if (transformed.Operation != "insert_reference_lines")
                    return transformed;
                if (failureMode == "duplicate_id")
                {
                    if (transformed.ResultIndex == 0)
                        firstReturnedId = transformed.Id;
                    else if (transformed.ResultIndex == 1)
                        return transformed with { Id = firstReturnedId!.Value };
                }
                return failureMode switch
                {
                    "callback" => throw new InvalidOperationException("returning row callback failed"),
                    "null_ordinal" => transformed with { InputOrdinal = null },
                    "duplicate_ordinal" when transformed.ResultIndex == 1
                        => transformed with { InputOrdinal = 0 },
                    "out_of_range_ordinal" => transformed with { InputOrdinal = transformed.StatementRows },
                    _ => transformed,
                };
            };
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;

            using (var failedFile = _writer.BeginTransaction())
            {
                var failedFileId = InsertNewFile("src/failed-returning.cs");
                var exception = Record.Exception(() =>
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        CreateReferences(failedFileId, 2, "failed"),
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
                if (failureMode == "callback")
                {
                    var callbackException = Assert.IsType<InvalidOperationException>(exception);
                    Assert.Equal("returning row callback failed", callbackException.Message);
                }
                else
                {
                    var protocolException = Assert.IsType<InvalidDataException>(exception);
                    var expectedMessagePart = failureMode switch
                    {
                        "duplicate_ordinal" => "duplicate input ordinal",
                        "duplicate_id" => "duplicate ID",
                        _ => "invalid input ordinal",
                    };
                    Assert.Contains(expectedMessagePart, protocolException.Message, StringComparison.Ordinal);
                }
            }

            DbWriter.AuthoritativeFreshRawReturningRowForTesting = previousRowHook;
            using (var succeedingFile = _writer.BeginTransaction())
            {
                var succeedingFileId = InsertNewFile("src/succeeding-returning.cs");
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(succeedingFileId, 2, "succeeding"),
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                succeedingFile.Commit();
            }

            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawReturningRowForTesting = previousRowHook;
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/failed-returning.cs'"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/succeeding-returning.cs'"));
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM symbol_references"));
        var referenceLineWork = rawWork
            .Where(work => work.Operation == "insert_reference_lines")
            .ToArray();
        Assert.Equal(2, referenceLineWork.Length);
        Assert.All(referenceLineWork, work => Assert.False(work.CacheHit));
        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(1, observedStats.DiscardCount);
        Assert.Equal(observedStats.PrepareCount, observedStats.FinalizeCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    public void FileReturningRowCountFailure_DiscardsStatementAndRollsBackEveryReturnedFile(
        string failureMode)
    {
        var rawWork = new List<DbWriter.AuthoritativeFreshRawInsertWork>();
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousSqlHook = DbWriter.AuthoritativeFreshRawReturningSqlForTesting;
        var previousRawHook = DbWriter.AuthoritativeFreshRawInsertExecutingForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawReturningSqlForTesting = statement =>
            {
                if (statement.Operation != "insert_files")
                    return previousSqlHook?.Invoke(statement) ?? statement.Sql;
                return failureMode == "missing"
                    ? """
                        INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
                        SELECT ?1, ?2, ?3, ?4, ?5, ?6, ?7, CURRENT_TIMESTAMP
                        WHERE 0
                        RETURNING id
                        """
                    : """
                        INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
                        SELECT ?1, ?2, ?3, ?4, ?5, ?6, ?7, CURRENT_TIMESTAMP
                        UNION ALL
                        SELECT ?1 || '.extra', ?2, ?3, ?4, ?5, ?6, ?7, CURRENT_TIMESTAMP
                        RETURNING id
                        """;
            };
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = work =>
            {
                rawWork.Add(work);
                previousRawHook?.Invoke(work);
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            using (var failedFile = _writer.BeginTransaction())
            {
                Assert.Throws<InvalidDataException>(() =>
                    InsertNewFile("src/file-row-count-failed.cs"));
            }

            DbWriter.AuthoritativeFreshRawReturningSqlForTesting = previousSqlHook;
            using (var succeedingFile = _writer.BeginTransaction())
            {
                _ = InsertNewFile("src/file-row-count-succeeded.cs");
                succeedingFile.Commit();
            }
            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawReturningSqlForTesting = previousSqlHook;
            DbWriter.AuthoritativeFreshRawInsertExecutingForTesting = previousRawHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files WHERE path LIKE 'src/file-row-count-failed.cs%'"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM files WHERE path = 'src/file-row-count-succeeded.cs'"));
        var fileWork = rawWork.Where(work => work.Operation == "insert_files").ToArray();
        Assert.Equal(2, fileWork.Length);
        Assert.All(fileWork, work => Assert.False(work.CacheHit));
        Assert.NotNull(observedStats);
        Assert.Equal(1, observedStats.DiscardCount);
        Assert.Equal(observedStats.PrepareCount, observedStats.FinalizeCount);
    }

    [Fact]
    public void ReferenceLineReturningConstraint_DiscardsStatementBeforeRowAndCanReprepare()
    {
        long fileId;
        using (var seedTransaction = _writer.BeginTransaction())
        {
            fileId = InsertNewFile("src/reference-line-constraint.cs");
            _writer.InsertReferencesForNewFilesInAtomicFileScope(
                CreateReferences(fileId, 1, "duplicate"),
                refreshMutualRecursionFlags: false,
                CancellationToken.None);
            seedTransaction.Commit();
        }

        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        try
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };
            using var graph = _writer.BeginReferenceGraphRefreshScope(
                forceFullRefresh: true,
                useFreshReferenceResolutionDefaults: true);
            using var outerTransaction = _writer.BeginTransaction();
            using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                enabled: true,
                CancellationToken.None)!;
            using (var failedFile = _writer.BeginTransaction())
            {
                var exception = Assert.Throws<SqliteException>(() =>
                    _writer.InsertReferencesForNewFilesInAtomicFileScope(
                        CreateReferences(fileId, 1, "duplicate"),
                        refreshMutualRecursionFlags: false,
                        CancellationToken.None));
                Assert.Equal(19, exception.SqliteErrorCode);
                Assert.Equal(2067, exception.SqliteExtendedErrorCode);
            }

            using (var succeedingFile = _writer.BeginTransaction())
            {
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(fileId, 1, "unique"),
                    refreshMutualRecursionFlags: false,
                    CancellationToken.None);
                succeedingFile.Commit();
            }
            raw.Complete();
            outerTransaction.Commit();
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(2L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.NotNull(observedStats);
        Assert.Equal(1, observedStats.DiscardCount);
    }

    [Fact]
    public void ReferenceLineReturningInterruptAfterFirstRow_PreservesCancellationAndRollsBackOuterTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        DbWriter.AuthoritativeFreshRawInsertScopeStats? observedStats = null;
        var previousRowHook = DbWriter.AuthoritativeFreshRawReturningRowForTesting;
        var previousStatsHook = DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting;
        OperationCanceledException exception;
        try
        {
            DbWriter.AuthoritativeFreshRawReturningRowForTesting = row =>
            {
                var transformed = previousRowHook?.Invoke(row) ?? row;
                if (transformed.Operation == "insert_reference_lines"
                    && transformed.ResultIndex == 0)
                {
                    cancellation.Cancel();
                }
                return transformed;
            };
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = stats =>
            {
                observedStats = stats;
                previousStatsHook?.Invoke(stats);
            };

            exception = Assert.Throws<OperationCanceledException>(() =>
            {
                using var graph = _writer.BeginReferenceGraphRefreshScope(
                    forceFullRefresh: true,
                    useFreshReferenceResolutionDefaults: true);
                using var outerTransaction = _writer.BeginTransaction();
                using var raw = _writer.BeginAuthoritativeFreshBulkInsertScope(
                    enabled: true,
                    cancellation.Token)!;
                using var fileTransaction = _writer.BeginTransaction();
                var fileId = InsertNewFile("src/reference-line-cancel.cs");
                _writer.InsertReferencesForNewFilesInAtomicFileScope(
                    CreateReferences(fileId, 2, "cancel"),
                    refreshMutualRecursionFlags: false,
                    cancellation.Token);
            });
        }
        finally
        {
            DbWriter.AuthoritativeFreshRawReturningRowForTesting = previousRowHook;
            DbWriter.AuthoritativeFreshRawInsertScopeDisposedForTesting = previousStatsHook;
        }

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
        Assert.Equal(9, sqliteException.SqliteErrorCode);
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM files"));
        Assert.Equal(0L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
        Assert.NotNull(observedStats);
        Assert.False(observedStats.Completed);
        Assert.Equal(1, observedStats.DiscardCount);

        using var retry = _writer.BeginTransaction();
        var retryFileId = InsertNewFile("src/reference-line-cancel-retry.cs");
        _writer.InsertReferencesForNewFilesInAtomicFileScope(
            CreateReferences(retryFileId, 1, "retry"),
            refreshMutualRecursionFlags: false,
            CancellationToken.None);
        retry.Commit();
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM reference_lines"));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestProjectHelper.DeleteDirectory(_projectRoot);
    }

    private long InsertNewFile(string path)
        => _writer.InsertNewFile(new FileRecord
        {
            Path = path,
            Lang = "csharp",
            Size = 100,
            Lines = 100,
            Checksum = path,
            Modified = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc),
        });

    private static FileIssue CreateIssue(int line)
        => new()
        {
            Path = "src/raw-shapes.cs",
            Kind = $"raw_issue_{line}",
            Line = line,
            Message = line == 1 ? "雪😀a\0β" : $"raw issue {line}",
            Origin = line == 1 ? null : "extractor",
            Severity = line == 1 ? null : "warning",
        };

    private static ChunkRecord[] CreateChunks(long fileId, int startIndex, int count)
        => Enumerable.Range(startIndex, count)
            .Select(index => new ChunkRecord
            {
                FileId = fileId,
                ChunkIndex = index,
                StartLine = index + 1,
                EndLine = index + 1,
                Content = $"chunk_{index}",
            })
            .ToArray();

    private static ReferenceRecord[] CreateReferences(
        long fileId,
        int count,
        string contextPrefix)
        => Enumerable.Range(0, count)
            .Select(index => new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = $"target_{contextPrefix}_{index}",
                ReferenceKind = "call",
                Line = index + 1,
                Column = 1,
                Context = $"{contextPrefix}_{index}();",
            })
            .ToArray();

    private void PrimeSequencesForInt64Returning()
    {
        Execute("""
            INSERT INTO files (
                id, path, lang, size, lines, checksum, modified, generated, indexed_at)
            VALUES (
                5000000000, 'src/sequence-primer.cs', 'csharp', 0, 0,
                'sequence-primer', '2026-08-23T00:00:00Z', 0, CURRENT_TIMESTAMP);
            INSERT INTO reference_lines (id, file_id, line, context)
            VALUES (5000000000, 5000000000, 1, 'sequence-primer');
            DELETE FROM reference_lines WHERE id = 5000000000;
            DELETE FROM files WHERE id = 5000000000;
            """);
        Assert.False(_writer.HasAnyIndexedFiles());
    }

    private void Execute(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long ResourceListGeneration()
        => ScalarLong("""
            SELECT CAST(value AS INTEGER)
            FROM codeindex_meta
            WHERE key = 'resource_list_generation'
            """);

    private long ResourceListGenerationTriggerCount()
        => ScalarLong("""
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'trigger'
              AND name IN (
                  'files_resource_generation_ai',
                  'files_resource_generation_ad',
                  'files_resource_generation_au')
            """);

    private string? ScalarString(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
