using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const int AuthoritativeFreshRawStatementCacheCapacity = 32;
    private static readonly AsyncLocal<Action<AuthoritativeFreshRawInsertWork>?>
        ScopedAuthoritativeFreshRawInsertExecutingForTesting = new();
    private static readonly AsyncLocal<Action<AuthoritativeFreshRawInsertScopeStats>?>
        ScopedAuthoritativeFreshRawInsertScopeDisposedForTesting = new();
    private static readonly AsyncLocal<int?>
        ScopedAuthoritativeFreshRawStatementCacheCapacityForTesting = new();
    private static readonly AsyncLocal<Func<AuthoritativeFreshRawReturningRow, AuthoritativeFreshRawReturningRow>?>
        ScopedAuthoritativeFreshRawReturningRowForTesting = new();
    private static readonly AsyncLocal<Func<AuthoritativeFreshRawReturningSql, string>?>
        ScopedAuthoritativeFreshRawReturningSqlForTesting = new();
    private AuthoritativeFreshBulkInsertScope? _authoritativeFreshBulkInsertScope;

    internal sealed record AuthoritativeFreshRawInsertWork(
        string Operation,
        int StatementRows,
        int BoundParameterCount,
        bool CacheHit,
        int CachedStatementCount);

    internal sealed record AuthoritativeFreshRawInsertScopeStats(
        int Capacity,
        int PeakCachedStatementCount,
        long StatementExecutionCount,
        long PrepareCount,
        long CacheHitCount,
        long EvictionCount,
        long DiscardCount,
        long FinalizeCount,
        bool Completed);

    internal readonly record struct AuthoritativeFreshRawReturningRow(
        string Operation,
        int StatementRows,
        int ResultIndex,
        long Id,
        int? InputOrdinal);

    internal sealed record AuthoritativeFreshRawReturningSql(
        string Operation,
        int StatementRows,
        string Sql);

    internal static Action<AuthoritativeFreshRawInsertWork>?
        AuthoritativeFreshRawInsertExecutingForTesting
    {
        get => ScopedAuthoritativeFreshRawInsertExecutingForTesting.Value;
        set => ScopedAuthoritativeFreshRawInsertExecutingForTesting.Value = value;
    }

    internal static Action<AuthoritativeFreshRawInsertScopeStats>?
        AuthoritativeFreshRawInsertScopeDisposedForTesting
    {
        get => ScopedAuthoritativeFreshRawInsertScopeDisposedForTesting.Value;
        set => ScopedAuthoritativeFreshRawInsertScopeDisposedForTesting.Value = value;
    }

    internal static int? AuthoritativeFreshRawStatementCacheCapacityForTesting
    {
        get => ScopedAuthoritativeFreshRawStatementCacheCapacityForTesting.Value;
        set => ScopedAuthoritativeFreshRawStatementCacheCapacityForTesting.Value = value;
    }

    internal static Func<AuthoritativeFreshRawReturningRow, AuthoritativeFreshRawReturningRow>?
        AuthoritativeFreshRawReturningRowForTesting
    {
        get => ScopedAuthoritativeFreshRawReturningRowForTesting.Value;
        set => ScopedAuthoritativeFreshRawReturningRowForTesting.Value = value;
    }

    internal static Func<AuthoritativeFreshRawReturningSql, string>?
        AuthoritativeFreshRawReturningSqlForTesting
    {
        get => ScopedAuthoritativeFreshRawReturningSqlForTesting.Value;
        set => ScopedAuthoritativeFreshRawReturningSqlForTesting.Value = value;
    }

    internal AuthoritativeFreshBulkInsertScope? BeginAuthoritativeFreshBulkInsertScope(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!enabled)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        RequireCallerOwnedTransaction(nameof(BeginAuthoritativeFreshBulkInsertScope));
        if (_conn.State != ConnectionState.Open)
            throw new InvalidOperationException("The SQLite connection must remain open for raw bulk inserts.");
        if (_referenceGraphRefreshScope is not
            {
                IsDisposed: false,
                FreshReferenceResolutionDefaultsPending: true,
            })
        {
            throw new InvalidOperationException(
                "Raw bulk inserts require authoritative fresh reference defaults.");
        }
        if (_authoritativeFreshBulkInsertScope != null)
            throw new InvalidOperationException(
                "An authoritative fresh bulk insert scope is already active for this writer.");
        var database = _conn.Handle
            ?? throw new InvalidOperationException("The SQLite connection handle is unavailable.");
        if (raw.sqlite3_get_autocommit(database) != 0)
            throw new InvalidOperationException(
                "Raw bulk inserts require an active SQLite transaction.");

        var capacity = AuthoritativeFreshRawStatementCacheCapacityForTesting
            ?? AuthoritativeFreshRawStatementCacheCapacity;
        if (capacity is <= 0 or > AuthoritativeFreshRawStatementCacheCapacity)
        {
            throw new InvalidOperationException(
                $"The raw statement cache capacity must be between 1 and {AuthoritativeFreshRawStatementCacheCapacity}.");
        }

        var scope = new AuthoritativeFreshBulkInsertScope(
            this,
            database,
            cancellationToken,
            capacity);
        _authoritativeFreshBulkInsertScope = scope;
        return scope;
    }

    private enum AuthoritativeFreshRawInsertKind
    {
        Files,
        Chunks,
        Symbols,
        Issues,
        ReferenceLines,
        References,
    }

    private readonly record struct AuthoritativeFreshRawStatementKey(
        AuthoritativeFreshRawInsertKind Kind,
        int Rows);

    internal sealed partial class AuthoritativeFreshBulkInsertScope : IDisposable
    {
        private readonly DbWriter _writer;
        // Microsoft.Data.Sqlite owns this SafeHandle. The scope is nested inside the
        // connection and caller-owned transaction lifetimes and never closes the handle.
        // SafeHandleはprovider所有。scopeはconnection/transactionより短命でcloseしない。
        private readonly sqlite3 _database;
        private readonly CancellationToken _cancellationToken;
        private readonly int _capacity;
        private readonly Dictionary<AuthoritativeFreshRawStatementKey, CachedStatement> _statements = [];
        private readonly LinkedList<AuthoritativeFreshRawStatementKey> _leastRecentlyUsed = [];
        private int _peakCachedStatementCount;
        private long _statementExecutionCount = 0;
        private long _prepareCount;
        private long _cacheHitCount;
        private long _evictionCount;
        private long _discardCount = 0;
        private long _finalizeCount;
        private bool _completed;
        private bool _disposed;

        internal AuthoritativeFreshBulkInsertScope(
            DbWriter writer,
            sqlite3 database,
            CancellationToken cancellationToken,
            int capacity)
        {
            _writer = writer;
            _database = database;
            _cancellationToken = cancellationToken;
            _capacity = capacity;
        }

        internal void InsertChunks(
            IReadOnlyList<CodeIndex.Models.ChunkRecord> chunks,
            int start,
            int end)
        {
            EnsureCanExecute();
            var rows = end - start;
            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var sql = ChunkInsertSqlCache.GetOrAdd(
                rows,
                static count => BuildChunkInsertSql(count));
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.Chunks,
                rows,
                sql,
                expectedParameterCount: rows * 5);
            try
            {
                for (var index = start; index < end; index++)
                {
                    var chunk = chunks[index];
                    lease.BindInt64(chunk.FileId);
                    lease.BindInt64(chunk.ChunkIndex);
                    lease.BindInt64(chunk.StartLine);
                    lease.BindInt64(chunk.EndLine);
                    lease.BindText(chunk.Content);
                }

                ReportStatementExecution("insert_chunks", rows, lease);
                ReportBatchStatementForTesting("insert_chunks", rows, rows);
                lease.ExecuteDone();
            }
            finally
            {
                lease.Dispose();
            }
        }

        internal void InsertSymbols(
            IReadOnlyList<CodeIndex.Models.SymbolRecord> symbols,
            int start,
            int end,
            Dictionary<string, string?> foldedNameCache)
        {
            EnsureCanExecute();
            var rows = end - start;
            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var sql = SymbolInsertSqlCache.GetOrAdd(
                rows,
                static count => BuildSymbolInsertSql(count));
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.Symbols,
                rows,
                sql,
                expectedParameterCount: rows * 25);
            try
            {
                for (var index = start; index < end; index++)
                {
                    var symbol = symbols[index];
                    ValidateSymbolKinds(symbol);
                    var startLine = symbol.StartLine > 0 ? symbol.StartLine : symbol.Line;
                    var endLine = symbol.EndLine > 0 ? symbol.EndLine : startLine;
                    lease.BindInt64(symbol.FileId);
                    lease.BindText(symbol.Kind);
                    lease.BindNullableText(symbol.SubKind);
                    lease.BindText(symbol.Name);
                    lease.BindInt64(symbol.Line);
                    lease.BindInt64(startLine);
                    lease.BindNullableInt64(symbol.StartColumn);
                    lease.BindInt64(endLine);
                    lease.BindNullableInt64(symbol.BodyStartLine);
                    lease.BindNullableInt64(symbol.BodyEndLine);
                    lease.BindNullableText(symbol.Signature);
                    lease.BindNullableText(symbol.ContainerKind);
                    lease.BindNullableText(symbol.ContainerName);
                    lease.BindNullableText(symbol.ContainerQualifiedName);
                    lease.BindNullableText(symbol.FamilyKey);
                    lease.BindNullableText(symbol.Visibility);
                    lease.BindNullableText(symbol.ReturnType);
                    lease.BindNullableInt64(symbol.IsPartialDeclaration.HasValue
                        ? symbol.IsPartialDeclaration.Value ? 1 : 0
                        : null);
                    lease.BindInt64(symbol.IsFileLocalDeclaration ? 1 : 0);
                    lease.BindNullableInt64(symbol.DeclarationSemanticScore);
                    lease.BindNullableInt64(symbol.IdentifierStartColumn);
                    lease.BindNullableInt64(symbol.IsMetadataTarget.HasValue
                        ? symbol.IsMetadataTarget.Value ? 1 : 0
                        : null);
                    lease.BindNullableText(symbol.MetadataTargetSource);
                    lease.BindNullableText(FoldedNameValue(
                        symbol.Name,
                        symbol.IdentityNameFolded,
                        foldedNameCache));
                    lease.BindNullableText(symbol.DisplayNameFolded);
                }

                ReportStatementExecution("insert_symbols", rows, lease);
                ReportBatchStatementForTesting("insert_symbols", rows, rows);
                lease.ExecuteDone();
            }
            finally
            {
                lease.Dispose();
            }
        }

        internal void InsertIssues(
            long fileId,
            IReadOnlyList<CodeIndex.Models.FileIssue> issues,
            int start,
            int end,
            bool reportBatchStatement)
        {
            EnsureCanExecute();
            var rows = end - start;
            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var sql = IssueInsertSqlCache.GetOrAdd(
                rows,
                static count => BuildIssueInsertSql(count));
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.Issues,
                rows,
                sql,
                expectedParameterCount: rows * 6);
            try
            {
                for (var index = start; index < end; index++)
                {
                    var issue = issues[index];
                    lease.BindInt64(fileId);
                    lease.BindText(issue.Kind);
                    lease.BindInt64(issue.Line);
                    lease.BindText(issue.Message);
                    lease.BindNullableText(issue.Origin);
                    lease.BindNullableText(issue.Severity);
                }

                ReportStatementExecution("insert_issues", rows, lease);
                if (reportBatchStatement)
                    ReportBatchStatementForTesting("insert_issues", rows, rows);
                lease.ExecuteDone();
            }
            finally
            {
                lease.Dispose();
            }
        }

        internal void InsertReferences(
            IReadOnlyList<CodeIndex.Models.ReferenceRecord> references,
            int start,
            int end,
            ReferenceLineBatchMap referenceLineIds,
            Dictionary<string, string?> foldedNameCache)
        {
            EnsureCanExecute();
            var rows = end - start;
            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var sql = ReferenceInsertSqlCache.GetOrAdd(
                (Rows: rows, FreshResolutionDefaults: true),
                static key => BuildReferenceInsertSql(
                    key.Rows,
                    key.FreshResolutionDefaults));
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.References,
                rows,
                sql,
                expectedParameterCount: rows * ReferenceInsertParameterCountPerRow);
            try
            {
                for (var index = start; index < end; index++)
                {
                    var reference = references[index];
                    ValidateReferenceKinds(reference);
                    lease.BindInt64(reference.FileId);
                    lease.BindText(reference.SymbolName);
                    lease.BindText(reference.ReferenceKind);
                    lease.BindInt64(reference.Line);
                    lease.BindInt64(reference.Column);
                    lease.BindNullableInt64(reference.SpanLength > 0
                        ? reference.SpanLength
                        : null);
                    lease.BindInt64(referenceLineIds.GetReferenceLineId(index));
                    lease.BindNullableText(reference.ContainerKind);
                    lease.BindNullableText(reference.ContainerName);
                    lease.BindNullableText(FoldedNameValue(
                        reference.SymbolName,
                        reference.IdentitySymbolNameFolded,
                        foldedNameCache));
                    lease.BindNullableText(FoldedNameValue(
                        reference.ContainerName,
                        reference.IdentityContainerNameFolded,
                        foldedNameCache));
                    lease.BindInt64(0);
                    lease.BindInt64(0);
                    lease.BindNullableText(ExtractTargetQualifier(reference));
                }

                ReferenceInsertBindingWorkForTesting?.Invoke(
                    new ReferenceInsertBindingWork(
                        rows,
                        lease.ParameterCount,
                        referenceLineIds.ReferenceCount,
                        referenceLineIds.ReferenceLineCount,
                        UsesFreshResolutionDefaults: true));
                ReportStatementExecution("insert_references", rows, lease);
                ReportBatchStatementForTesting("insert_references", rows, rows);
                lease.ExecuteDone();
            }
            finally
            {
                lease.Dispose();
            }
        }

        internal void Complete()
        {
            if (_disposed)
            {
                if (_completed)
                    return;
                throw new ObjectDisposedException(nameof(AuthoritativeFreshBulkInsertScope));
            }

            _writer.RequireCallerOwnedTransaction(nameof(Complete));
            Exception? failure = null;
            if (raw.sqlite3_get_autocommit(_database) != 0)
            {
                failure = new InvalidOperationException(
                    "The caller-owned SQLite transaction ended before raw statements were finalized.");
            }

            failure = FinalizeAll(failure);
            if (failure == null)
            {
                try
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception cancellationException)
                {
                    failure = cancellationException;
                }
            }

            Detach();
            failure = ReportDisposed(failure, completed: failure == null);
            if (failure != null)
                throw failure;
            _completed = true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _ = FinalizeAll(existingFailure: null);
            Detach();
            // Disposal is the exceptional-path cleanup for the outer transaction;
            // test hooks must not replace the original indexing failure.
            try
            {
                _ = ReportDisposed(existingFailure: null, completed: false);
            }
            catch
            {
                // Keep disposal best-effort even if a future reporting implementation
                // stops converting hook failures into returned exceptions.
            }
        }

        private CachedStatement RentStatement(
            AuthoritativeFreshRawInsertKind kind,
            int rows,
            string sql,
            int expectedParameterCount,
            int expectedColumnCount,
            out bool cacheHit)
        {
            EnsureCanExecute();
            var key = new AuthoritativeFreshRawStatementKey(kind, rows);
            if (_statements.TryGetValue(key, out var cached))
            {
                _leastRecentlyUsed.Remove(cached.RecencyNode);
                _leastRecentlyUsed.AddFirst(cached.RecencyNode);
                _cacheHitCount++;
                cacheHit = true;
                return cached;
            }

            if (_statements.Count == _capacity)
                EvictLeastRecentlyUsed();

            var prepareResult = raw.sqlite3_prepare_v2(
                _database,
                sql,
                out var statement,
                out var tail);
            if (prepareResult != raw.SQLITE_OK)
            {
                DisposeUncachedStatement(statement);
                throw CreateExecutionException(prepareResult);
            }

            _prepareCount++;
            if (statement == null)
                throw new InvalidOperationException("SQLite prepared no statement for a non-empty INSERT.");
            var actualParameterCount = raw.sqlite3_bind_parameter_count(statement);
            var actualColumnCount = raw.sqlite3_column_count(statement);
            if (!string.IsNullOrWhiteSpace(tail)
                || actualParameterCount != expectedParameterCount
                || actualColumnCount != expectedColumnCount)
            {
                DisposeUncachedStatement(statement);
                throw new InvalidOperationException(
                    "Raw SQLite statement preparation changed the statement tail, parameter shape, or result shape "
                    + $"(tail_length={tail?.Length ?? 0}, expected_parameters={expectedParameterCount}, "
                    + $"actual_parameters={actualParameterCount}, expected_columns={expectedColumnCount}, "
                    + $"actual_columns={actualColumnCount}).");
            }

            var node = _leastRecentlyUsed.AddFirst(key);
            cached = new CachedStatement(statement, actualParameterCount, node);
            _statements.Add(key, cached);
            _peakCachedStatementCount = Math.Max(_peakCachedStatementCount, _statements.Count);
            cacheHit = false;
            return cached;
        }

        private StatementLease RentStatementLease(
            AuthoritativeFreshRawInsertKind kind,
            int rows,
            string sql,
            int expectedParameterCount,
            int expectedColumnCount = 0)
        {
            var statement = RentStatement(
                kind,
                rows,
                sql,
                expectedParameterCount,
                expectedColumnCount,
                out var cacheHit);
            return new StatementLease(this, statement, cacheHit);
        }

        private void ReportStatementExecution(
            string operation,
            int rows,
            StatementLease lease)
        {
            _statementExecutionCount++;
            AuthoritativeFreshRawInsertExecutingForTesting?.Invoke(
                new AuthoritativeFreshRawInsertWork(
                    operation,
                    rows,
                    lease.ParameterCount,
                    lease.CacheHit,
                    _statements.Count));
        }

        private void EnsureCanExecute()
        {
            if (_disposed || !ReferenceEquals(_writer._authoritativeFreshBulkInsertScope, this))
                throw new ObjectDisposedException(nameof(AuthoritativeFreshBulkInsertScope));
            _cancellationToken.ThrowIfCancellationRequested();
            _writer.RequireCallerOwnedTransaction(nameof(AuthoritativeFreshBulkInsertScope));
            if (raw.sqlite3_get_autocommit(_database) != 0)
                throw new InvalidOperationException("Raw bulk inserts require an active SQLite transaction.");
        }

        private void EvictLeastRecentlyUsed()
        {
            var node = _leastRecentlyUsed.Last
                ?? throw new InvalidOperationException("The raw statement LRU is empty.");
            var cached = _statements[node.Value];
            _leastRecentlyUsed.Remove(node);
            _statements.Remove(node.Value);
            _evictionCount++;
            var result = FinalizeStatement(cached.Statement);
            if (result != raw.SQLITE_OK)
                throw CreateExecutionException(result);
        }

        private void DiscardStatement(CachedStatement cached)
        {
            var key = cached.RecencyNode.Value;
            if (!_statements.Remove(key))
                return;
            _leastRecentlyUsed.Remove(cached.RecencyNode);
            _discardCount++;
            _ = FinalizeStatement(cached.Statement);
        }

        private Exception? FinalizeAll(Exception? existingFailure)
        {
            while (_leastRecentlyUsed.Last is { } node)
            {
                var cached = _statements[node.Value];
                _leastRecentlyUsed.Remove(node);
                _statements.Remove(node.Value);
                var result = FinalizeStatement(cached.Statement);
                if (result != raw.SQLITE_OK && existingFailure == null)
                    existingFailure = CreateExecutionException(result);
            }

            return existingFailure;
        }

        private int FinalizeStatement(sqlite3_stmt statement)
        {
            try
            {
                return raw.sqlite3_finalize(statement);
            }
            finally
            {
                // sqlite3_finalize invalidates SQLitePCLRaw's SafeHandle. Dispose then
                // closes only the managed wrapper and does not finalize a second time.
                // finalize後のDisposeはwrapperだけを閉じ、native finalizeを重複しない。
                statement.Dispose();
                _finalizeCount++;
            }
        }

        private void DisposeUncachedStatement(sqlite3_stmt? statement)
        {
            if (statement == null)
                return;
            if (!statement.IsInvalid)
                _ = FinalizeStatement(statement);
            else
                statement.Dispose();
        }

        private Exception CreateExecutionException(int resultCode)
        {
            var primaryCode = resultCode & 0xff;
            var extendedCode = raw.sqlite3_extended_errcode(_database);
            if (extendedCode == raw.SQLITE_OK)
                extendedCode = resultCode;
            var detail = raw.sqlite3_errmsg(_database).utf8_to_string();
            var sqliteException = new SqliteException(
                $"SQLite Error {primaryCode}: '{detail}'.",
                primaryCode,
                extendedCode);
            return primaryCode == raw.SQLITE_INTERRUPT
                && _cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(
                    "The authoritative fresh SQLite insert was canceled.",
                    sqliteException,
                    _cancellationToken)
                : sqliteException;
        }

        private void Detach()
        {
            _disposed = true;
            if (ReferenceEquals(_writer._authoritativeFreshBulkInsertScope, this))
                _writer._authoritativeFreshBulkInsertScope = null;
        }

        private Exception? ReportDisposed(Exception? existingFailure, bool completed)
        {
            try
            {
                AuthoritativeFreshRawInsertScopeDisposedForTesting?.Invoke(
                    new AuthoritativeFreshRawInsertScopeStats(
                        _capacity,
                        _peakCachedStatementCount,
                        _statementExecutionCount,
                        _prepareCount,
                        _cacheHitCount,
                        _evictionCount,
                        _discardCount,
                        _finalizeCount,
                        completed));
            }
            catch (Exception) when (existingFailure != null)
            {
                return existingFailure;
            }
            catch (Exception hookException)
            {
                return hookException;
            }

            return existingFailure;
        }

        private sealed class CachedStatement(
            sqlite3_stmt statement,
            int parameterCount,
            LinkedListNode<AuthoritativeFreshRawStatementKey> recencyNode)
        {
            internal sqlite3_stmt Statement { get; } = statement;
            internal int ParameterCount { get; } = parameterCount;
            internal LinkedListNode<AuthoritativeFreshRawStatementKey> RecencyNode { get; } = recencyNode;
        }

        private struct StatementLease : IDisposable
        {
            private readonly AuthoritativeFreshBulkInsertScope _scope;
            private readonly CachedStatement _cached;
            private int _boundParameterCount;
            private bool _cleaned;

            internal StatementLease(
                AuthoritativeFreshBulkInsertScope scope,
                CachedStatement cached,
                bool cacheHit)
            {
                _scope = scope;
                _cached = cached;
                CacheHit = cacheHit;
            }

            internal int ParameterCount => _cached.ParameterCount;
            internal bool CacheHit { get; }

            internal void BindInt64(long value)
                => CheckBind(raw.sqlite3_bind_int64(
                    _cached.Statement,
                    NextParameterOrdinal(),
                    value));

            internal void BindNullableInt64(long? value)
            {
                var ordinal = NextParameterOrdinal();
                CheckBind(value.HasValue
                    ? raw.sqlite3_bind_int64(_cached.Statement, ordinal, value.Value)
                    : raw.sqlite3_bind_null(_cached.Statement, ordinal));
            }

            internal void BindText(string value)
            {
                ArgumentNullException.ThrowIfNull(value);
                CheckBind(raw.sqlite3_bind_text(
                    _cached.Statement,
                    NextParameterOrdinal(),
                    value));
            }

            internal void BindNullableText(string? value)
            {
                var ordinal = NextParameterOrdinal();
                CheckBind(value == null
                    ? raw.sqlite3_bind_null(_cached.Statement, ordinal)
                    : raw.sqlite3_bind_text(_cached.Statement, ordinal, value));
            }

            internal void BindDateTimeText(DateTime value)
            {
                Span<char> chars = stackalloc char[27];
                if (!value.TryFormat(
                        chars,
                        out var charCount,
                        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
                        CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException("The file modification timestamp could not be formatted for SQLite.");
                }

                Span<byte> bytes = stackalloc byte[27];
                var byteCount = Encoding.UTF8.GetBytes(chars[..charCount], bytes);
                CheckBind(raw.sqlite3_bind_text(
                    _cached.Statement,
                    NextParameterOrdinal(),
                    bytes[..byteCount]));
            }

            internal void ExecuteDone()
            {
                if (_boundParameterCount != _cached.ParameterCount)
                {
                    throw new InvalidOperationException(
                        "Raw SQLite binding did not fill the prepared statement "
                        + $"(expected={_cached.ParameterCount}, actual={_boundParameterCount}).");
                }

                _scope._cancellationToken.ThrowIfCancellationRequested();
                var stepResult = raw.sqlite3_step(_cached.Statement);
                Exception? executionFailure = stepResult switch
                {
                    raw.SQLITE_DONE => null,
                    raw.SQLITE_ROW => new InvalidOperationException(
                        "A raw SQLite INSERT unexpectedly returned a row."),
                    _ => _scope.CreateExecutionException(stepResult),
                };

                var resetResult = raw.sqlite3_reset(_cached.Statement);
                Exception? cleanupFailure = null;
                var resetIsReusable = resetResult == raw.SQLITE_OK
                    || (stepResult != raw.SQLITE_DONE && resetResult == stepResult);
                if (!resetIsReusable)
                    cleanupFailure = _scope.CreateExecutionException(resetResult);

                var clearResult = raw.sqlite3_clear_bindings(_cached.Statement);
                if (clearResult != raw.SQLITE_OK && cleanupFailure == null)
                    cleanupFailure = _scope.CreateExecutionException(clearResult);

                if (!resetIsReusable || clearResult != raw.SQLITE_OK)
                    _scope.DiscardStatement(_cached);
                _cleaned = true;

                if (executionFailure != null)
                    throw executionFailure;
                if (cleanupFailure != null)
                    throw cleanupFailure;
                _scope._cancellationToken.ThrowIfCancellationRequested();
            }

            internal void ExecuteReturningRows(
                string operation,
                int expectedRowCount,
                Span<long> idsByInputOrdinal,
                bool returnsInputOrdinal)
            {
                if (_boundParameterCount != _cached.ParameterCount)
                {
                    throw new InvalidOperationException(
                        "Raw SQLite binding did not fill the prepared statement "
                        + $"(expected={_cached.ParameterCount}, actual={_boundParameterCount}).");
                }
                if (expectedRowCount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(expectedRowCount));
                if (idsByInputOrdinal.Length != expectedRowCount)
                    throw new ArgumentException("The raw RETURNING ID buffer must match the expected row count.", nameof(idsByInputOrdinal));

                _scope._cancellationToken.ThrowIfCancellationRequested();
                Exception? executionFailure = null;
                var returnedRowCount = 0;
                var terminalResult = raw.SQLITE_OK;
                var returningRowTransform = AuthoritativeFreshRawReturningRowForTesting;
                HashSet<long>? returnedIds = expectedRowCount > 1
                    ? new HashSet<long>(expectedRowCount)
                    : null;
                try
                {
                    while (true)
                    {
                        terminalResult = raw.sqlite3_step(_cached.Statement);
                        if (terminalResult == raw.SQLITE_ROW)
                        {
                            if (returnedRowCount >= expectedRowCount)
                            {
                                throw new InvalidDataException(
                                    $"Raw SQLite RETURNING produced more than {expectedRowCount} rows.");
                            }

                            if (raw.sqlite3_column_type(_cached.Statement, 0) != raw.SQLITE_INTEGER)
                            {
                                throw new InvalidDataException(
                                    $"Raw SQLite {operation} RETURNING produced a non-integer ID.");
                            }
                            var id = raw.sqlite3_column_int64(_cached.Statement, 0);
                            int? inputOrdinal = null;
                            if (returnsInputOrdinal)
                            {
                                var ordinalType = raw.sqlite3_column_type(_cached.Statement, 1);
                                if (ordinalType == raw.SQLITE_INTEGER)
                                {
                                    var ordinalValue = raw.sqlite3_column_int64(_cached.Statement, 1);
                                    if (ordinalValue is >= int.MinValue and <= int.MaxValue)
                                        inputOrdinal = (int)ordinalValue;
                                }
                                else if (ordinalType != raw.SQLITE_NULL)
                                {
                                    throw new InvalidDataException(
                                        $"Raw SQLite {operation} RETURNING produced a non-integer input ordinal.");
                                }
                            }

                            if (returningRowTransform is { } transform)
                            {
                                var returnedRow = transform(new AuthoritativeFreshRawReturningRow(
                                    operation,
                                    expectedRowCount,
                                    returnedRowCount,
                                    id,
                                    inputOrdinal));
                                id = returnedRow.Id;
                                inputOrdinal = returnedRow.InputOrdinal;
                            }

                            var resolvedOrdinal = returnsInputOrdinal
                                ? inputOrdinal
                                : returnedRowCount;
                            if (id <= 0)
                            {
                                throw new InvalidDataException(
                                    $"Raw SQLite {operation} RETURNING produced a non-positive ID.");
                            }
                            if (resolvedOrdinal is not { } ordinal
                                || (uint)ordinal >= (uint)expectedRowCount)
                            {
                                throw new InvalidDataException(
                                    $"Raw SQLite {operation} RETURNING produced invalid input ordinal "
                                    + $"{resolvedOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "NULL"} "
                                    + $"for {expectedRowCount} rows.");
                            }
                            if (idsByInputOrdinal[ordinal] != 0)
                            {
                                throw new InvalidDataException(
                                    $"Raw SQLite {operation} RETURNING produced duplicate input ordinal {ordinal}.");
                            }
                            if (returnedIds != null && !returnedIds.Add(id))
                            {
                                throw new InvalidDataException(
                                    $"Raw SQLite {operation} RETURNING produced duplicate ID {id}.");
                            }
                            idsByInputOrdinal[ordinal] = id;
                            returnedRowCount++;
                            // Do not throw only from the managed token between ROWs. A pending
                            // sqlite3_interrupt must reach the next step so SQLite preserves its
                            // transaction rollback semantics before the cancellation escapes.
                            // ROW間ではmanaged tokenだけでthrowせず、pending interruptを次stepへ渡す。
                            continue;
                        }

                        if (terminalResult != raw.SQLITE_DONE)
                            throw _scope.CreateExecutionException(terminalResult);
                        if (returnedRowCount != expectedRowCount)
                        {
                            throw new InvalidDataException(
                                "Raw SQLite RETURNING produced an incomplete result "
                                + $"(expected={expectedRowCount}, actual={returnedRowCount}).");
                        }
                        break;
                    }

                    for (var ordinal = 0; ordinal < idsByInputOrdinal.Length; ordinal++)
                    {
                        if (idsByInputOrdinal[ordinal] == 0)
                        {
                            throw new InvalidDataException(
                                $"Raw SQLite {operation} RETURNING did not materialize input ordinal {ordinal}.");
                        }
                    }
                }
                catch (Exception exception)
                {
                    executionFailure = exception;
                }

                var resetResult = raw.sqlite3_reset(_cached.Statement);
                Exception? cleanupFailure = null;
                var resetIsReusable = resetResult == raw.SQLITE_OK
                    || (terminalResult is not (raw.SQLITE_OK or raw.SQLITE_DONE)
                        && resetResult == terminalResult);
                if (!resetIsReusable)
                    cleanupFailure = _scope.CreateExecutionException(resetResult);

                var clearResult = raw.sqlite3_clear_bindings(_cached.Statement);
                if (clearResult != raw.SQLITE_OK && cleanupFailure == null)
                    cleanupFailure = _scope.CreateExecutionException(clearResult);

                var cancellationPending = _scope._cancellationToken.IsCancellationRequested;
                if (executionFailure != null
                    || cleanupFailure != null
                    || cancellationPending)
                {
                    // SQLite applies a DML RETURNING statement before yielding its first ROW.
                    // Reset is cleanup, not rollback. Never reuse a statement after any ROW
                    // protocol failure; the caller's per-file SAVEPOINT owns data rollback.
                    // DML RETURNINGは最初のROW前に適用済み。resetはrollbackではない。
                    _scope.DiscardStatement(_cached);
                }
                _cleaned = true;

                if (executionFailure != null)
                    throw executionFailure;
                if (cleanupFailure != null)
                    throw cleanupFailure;
                _scope._cancellationToken.ThrowIfCancellationRequested();
            }

            internal void Discard()
            {
                _scope.DiscardStatement(_cached);
                _cleaned = true;
            }

            public void Dispose()
            {
                if (_cleaned)
                    return;

                var resetResult = raw.sqlite3_reset(_cached.Statement);
                var clearResult = raw.sqlite3_clear_bindings(_cached.Statement);
                _cleaned = true;
                if (resetResult != raw.SQLITE_OK || clearResult != raw.SQLITE_OK)
                    _scope.DiscardStatement(_cached);
            }

            private int NextParameterOrdinal() => ++_boundParameterCount;

            private void CheckBind(int resultCode)
            {
                if (resultCode != raw.SQLITE_OK)
                    throw _scope.CreateExecutionException(resultCode);
            }
        }
    }
}
