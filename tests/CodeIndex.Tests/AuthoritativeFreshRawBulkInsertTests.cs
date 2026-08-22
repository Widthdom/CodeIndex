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
        Assert.Equal(0, observedStats.PrepareCount);
        Assert.Equal(0, observedStats.FinalizeCount);
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM file_issues"));
    }

    [Fact]
    public void BatchStatements_PreserveShapesUnicodeNullsInt64AndProviderExclusions()
    {
        PrimeFileSequenceForInt64Binding();
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
            var fileId = InsertNewFile("src/raw-shapes.cs");
            Assert.Equal(ExpectedLargeFileId, fileId);

            var chunks = Enumerable.Range(0, 13)
                .Select(index => new ChunkRecord
                {
                    FileId = fileId,
                    ChunkIndex = index,
                    StartLine = index + 1,
                    EndLine = index + 1,
                    Content = index == 0 ? "雪😀a\0β" : $"chunk_{index}",
                })
                .ToArray();
            var symbols = Enumerable.Range(0, 3)
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
            var issues = Enumerable.Range(0, 11)
                .Select(index => CreateIssue(index + 1))
                .ToArray();
            var references = Enumerable.Range(0, 5)
                .Select(index => new ReferenceRecord
                {
                    FileId = fileId,
                    SymbolName = $"target_{index % symbols.Length}",
                    ReferenceKind = "call",
                    Line = index + 1,
                    Column = index + 1,
                    SpanLength = index == 0 ? 0 : index + 1,
                    Context = $"target_{index % symbols.Length}();",
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
            var providerFileId = InsertNewFile("src/provider-exclusions.cs");
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

        Assert.Equal([(6, 30), (6, 30), (1, 5)], RowsAndParameters("insert_chunks"));
        Assert.Equal([(1, 25), (1, 25), (1, 25)], RowsAndParameters("insert_symbols"));
        Assert.Equal([(5, 30), (5, 30), (1, 6)], RowsAndParameters("insert_issues"));
        Assert.Equal([(2, 28), (2, 28), (1, 14)], RowsAndParameters("insert_references"));
        Assert.DoesNotContain(rawWork, work => work.Operation == "insert_reference_lines");
        Assert.Contains(
            batchWork,
            work => work.Operation == "insert_reference_lines" && work.StatementRows == 5);

        Assert.NotNull(observedStats);
        Assert.True(observedStats.Completed);
        Assert.Equal(16, observedStats.Capacity);
        Assert.Equal(7, observedStats.PeakCachedStatementCount);
        Assert.Equal(12, observedStats.StatementExecutionCount);
        Assert.Equal(7, observedStats.PrepareCount);
        Assert.Equal(5, observedStats.CacheHitCount);
        Assert.Equal(0, observedStats.EvictionCount);
        Assert.Equal(0, observedStats.DiscardCount);
        Assert.Equal(7, observedStats.FinalizeCount);

        Assert.Equal("E99BAAF09F98806100CEB2", ScalarString("SELECT hex(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0"));
        Assert.Equal(11L, ScalarLong("SELECT length(CAST(content AS BLOB)) FROM chunks WHERE chunk_index = 0"));
        Assert.Equal(3L, ScalarLong($"SELECT COUNT(*) FROM symbols WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(1L, ScalarLong("SELECT COUNT(*) FROM symbols WHERE sub_kind IS NULL AND signature IS NULL AND start_column IS NULL"));
        Assert.Equal(11L, ScalarLong($"SELECT COUNT(*) FROM file_issues WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(5L, ScalarLong($"SELECT COUNT(*) FROM symbol_references WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)}"));
        Assert.Equal(5L, ScalarLong($"SELECT COUNT(*) FROM symbol_references WHERE file_id = {ExpectedLargeFileId.ToString(CultureInfo.InvariantCulture)} AND context IS NULL AND is_self_reference = 0 AND is_mutual_recursion = 0"));

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
        Assert.Equal(4, observedStats.PrepareCount);
        Assert.Equal(0, observedStats.CacheHitCount);
        Assert.Equal(2, observedStats.EvictionCount);
        Assert.Equal(4, observedStats.FinalizeCount);
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
        Assert.Equal(1, observedStats.FinalizeCount);

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

    private void PrimeFileSequenceForInt64Binding()
    {
        Execute("""
            INSERT INTO files (
                id, path, lang, size, lines, checksum, modified, generated, indexed_at)
            VALUES (
                5000000000, 'src/sequence-primer.cs', 'csharp', 0, 0,
                'sequence-primer', '2026-08-23T00:00:00Z', 0, CURRENT_TIMESTAMP);
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
