using Microsoft.Data.Sqlite;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

/// <summary>
/// Handles INSERT/UPSERT operations to the database with batch commits.
/// Transaction scopes on a writer are serialized; nested scopes are supported on the
/// owning thread and other threads wait until the active scope is disposed.
/// バッチコミットによるINSERT/UPSERT処理を担当する。
/// writer 上の transaction scope は直列化され、同一所有スレッドのネストのみ許可する。
/// </summary>
public partial class DbWriter
{
    public const string FtsIncrementalWritesSinceOptimizeMetaKey = "fts_incremental_writes_since_optimize";
    public const string FtsLastOptimizedAtMetaKey = "fts_last_optimized_at";
    public const string FtsLastOptimizeDurationMsMetaKey = "fts_last_optimize_duration_ms";
    public const string FtsBulkLoadInProgressMetaKey = "fts_bulk_load_in_progress";
    public const int DefaultFtsOptimizeIncrementalWriteThreshold = 25;

    private readonly SqliteConnection _conn;
    private readonly PreparedCommandCache? _commandCache;
    private readonly Action? _markWriteWork;
    private static readonly AsyncLocal<Action<string>?> ScopedLanguagePresenceCheckForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedIndexedLanguagesReadForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedReusableUnchangedFileLookupForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedCountsReadForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedBatchRowSkipWarningForTesting = new();
    private static readonly AsyncLocal<Action<DbWriterBatchProgress>?> ScopedBatchProgressCheckpointForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedMutualRecursionRefreshForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedCSharpContractPreflightForTesting = new();
    internal static Action<string>? LanguagePresenceCheckForTesting
    {
        get => ScopedLanguagePresenceCheckForTesting.Value;
        set => ScopedLanguagePresenceCheckForTesting.Value = value;
    }

    internal static Action? IndexedLanguagesReadForTesting
    {
        get => ScopedIndexedLanguagesReadForTesting.Value;
        set => ScopedIndexedLanguagesReadForTesting.Value = value;
    }

    internal static Action<string>? ReusableUnchangedFileLookupForTesting
    {
        get => ScopedReusableUnchangedFileLookupForTesting.Value;
        set => ScopedReusableUnchangedFileLookupForTesting.Value = value;
    }

    internal static Action? CountsReadForTesting
    {
        get => ScopedCountsReadForTesting.Value;
        set => ScopedCountsReadForTesting.Value = value;
    }

    internal static Action<string>? BatchRowSkipWarningForTesting
    {
        get => ScopedBatchRowSkipWarningForTesting.Value;
        set => ScopedBatchRowSkipWarningForTesting.Value = value;
    }

    internal static Action<DbWriterBatchProgress>? BatchProgressCheckpointForTesting
    {
        get => ScopedBatchProgressCheckpointForTesting.Value;
        set => ScopedBatchProgressCheckpointForTesting.Value = value;
    }

    internal static Action? MutualRecursionRefreshForTesting
    {
        get => ScopedMutualRecursionRefreshForTesting.Value;
        set => ScopedMutualRecursionRefreshForTesting.Value = value;
    }

    internal static Action? CSharpContractPreflightForTesting
    {
        get => ScopedCSharpContractPreflightForTesting.Value;
        set => ScopedCSharpContractPreflightForTesting.Value = value;
    }

    // Transaction ownership (#4154): the semaphore is held for the outermost writer
    // transaction lifetime. Same-stack nested calls from the owning thread and
    // AsyncLocal token skip the semaphore and become SAVEPOINTs; other flows wait even
    // when ExecutionContext copied the token into Task.Run.
    private readonly object _transactionStateLock = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly AsyncLocal<Guid?> _currentTransactionGateToken = new();
    private static readonly TimeSpan DefaultTransactionStateContentionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransactionStateContentionWaitInterval = TimeSpan.FromMilliseconds(50);
    private const int DeleteFilesBatchSize = 500;
    private const int SqliteConstraintErrorCode = 19;
    private const int SqliteInterruptErrorCode = 9;
    private const int TypeScriptModuleSyntaxFallbackMaxBytes = (int)FileIndexer.DefaultMaxFileSizeBytes;
    private const int TypeScriptModuleSyntaxFallbackMaxLines = 16384;
    private static readonly ConcurrentDictionary<int, string> ChunkInsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> SymbolInsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceInsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceLineUpsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceLineLookupSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceLineInsertSqlCache = new();
    private static readonly BoundedRegex CSharpExternAliasSignatureRegex = new(
        @"^\s*extern\s+alias\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpGlobalUsingSignatureRegex = new(
        @"^\s*global\s+using\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpUsingStaticSignatureRegex = new(
        @"^\s*(?:global\s+)?using\s+static\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpUsingAliasSignatureRegex = new(
        @"^\s*(?:global\s+)?using\s+(?<alias>@?\w+)\s*=\s*(?<target>[^;]+?)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private int _rowSkipSavepointCounter;
    private long _batchRowsSkipped;
    private int _transactionDepth;
    private int _transactionOwnerThreadId;
    private Guid _transactionOwnerToken;
    private string? _transactionOwnerOperation;
    private DateTimeOffset _transactionOwnerAcquiredAtUtc;
    private bool? _hasIssueMetadataColumns;
    internal static TimeSpan? TransactionStateContentionTimeoutForTesting { get; set; }
    // Outermost SqliteTransaction currently held open by this writer (null when no
    // transaction is active OR after the outermost transaction has been committed /
    // rolled back). Tracked so cached prepared commands can be re-pointed at the live
    // transaction on every lease — SqliteCommand validates Transaction against the
    // connection's current transaction at execute time and would throw
    // TransactionRequired / TransactionConnectionMismatch after a transaction boundary
    // if we kept a stale reference. Cleared on Commit / Rollback (and at depth-0
    // Dispose as a safety net) so a subsequent lease outside any transaction sets the
    // cached command's Transaction back to null. Issue #1566.
    // 現在 writer が保持している最外 SqliteTransaction。キャッシュ済み prepared command の
    // Transaction を毎回その時点の active transaction に同期させるため保持する。
    // Commit / Rollback / Dispose で必ず null に戻し、トランザクション外で借用したときに
    // cached command の Transaction を null に再同期できるようにする。Issue #1566.
    private SqliteTransaction? _activeTransaction;
    internal SqliteConnection Connection => _conn;
    public long BatchRowsSkipped => Volatile.Read(ref _batchRowsSkipped);

    internal sealed record DbWriterBatchProgress(string Operation, int RowsProcessed, int RowsTotal);

    public DbWriter(SqliteConnection connection)
        : this(connection, commandCache: null, markWriteWork: null)
    {
    }

    /// <summary>
    /// Construct a writer that shares its owning <see cref="DbContext"/>'s
    /// <see cref="PreparedCommandCache"/>. Hot per-file paths (`GetUnchangedFileId`,
    /// `UpsertFile`, file-data cleanup) then reuse one prepared statement per SQL text
    /// instead of constructing a fresh command per call. Issue #1566.
    /// 所属 <see cref="DbContext"/> の <see cref="PreparedCommandCache"/> を共有する writer
    /// を構築する。ファイル単位のホットパスは SQL ごとに 1 つの prepared statement を再利用する。
    /// Issue #1566.
    /// </summary>
    public DbWriter(DbContext context)
        : this(
            (context ?? throw new ArgumentNullException(nameof(context))).Connection,
            context.IsReadOnly ? null : context.PreparedCommands,
            () => context.MarkWriteWork())
    {
    }

    internal DbWriter(SqliteConnection connection, PreparedCommandCache? commandCache, Action? markWriteWork)
    {
        _conn = connection;
        _commandCache = commandCache;
        _markWriteWork = markWriteWork;
    }

    private bool IsInTransaction() => _transactionDepth > 0;

    private CancellationTokenRegistration RegisterSqliteInterrupt(CancellationToken cancellationToken)
        => cancellationToken.UnsafeRegister(
            static state =>
            {
                var connection = (SqliteConnection)state!;
                SQLitePCL.raw.sqlite3_interrupt(connection.Handle);
            },
            _conn);

    private static bool IsSqliteInterruptCancellation(SqliteException exception, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
           && exception.SqliteErrorCode == SqliteInterruptErrorCode;

}
