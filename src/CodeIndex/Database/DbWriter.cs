using Microsoft.Data.Sqlite;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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
    private const string FoldBackfillPhaseMetaKey = "fold_backfill_phase";
    private const string FoldBackfillLastSymbolIdMetaKey = "fold_backfill_last_symbol_id";
    private const string FoldBackfillLastReferenceIdMetaKey = "fold_backfill_last_reference_id";

    public const string FtsIncrementalWritesSinceOptimizeMetaKey = "fts_incremental_writes_since_optimize";
    public const string FtsLastOptimizedAtMetaKey = "fts_last_optimized_at";
    public const string FtsBulkLoadInProgressMetaKey = "fts_bulk_load_in_progress";
    public const int DefaultFtsOptimizeIncrementalWriteThreshold = 25;

    private readonly SqliteConnection _conn;
    private readonly PreparedCommandCache? _commandCache;
    private readonly Action? _markWriteWork;
    private static readonly AsyncLocal<Action?> ScopedFoldBackfillRowUpdatedForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedFoldBackfillVerificationForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedLanguagePresenceCheckForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedIndexedLanguagesReadForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedReusableUnchangedFileLookupForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedCountsReadForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedBatchRowSkipWarningForTesting = new();
    private static readonly AsyncLocal<Action<DbWriterBatchProgress>?> ScopedBatchProgressCheckpointForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedMutualRecursionRefreshForTesting = new();
    internal static Action? FoldBackfillRowUpdatedForTesting
    {
        get => ScopedFoldBackfillRowUpdatedForTesting.Value;
        set => ScopedFoldBackfillRowUpdatedForTesting.Value = value;
    }

    internal static Action? FoldBackfillVerificationForTesting
    {
        get => ScopedFoldBackfillVerificationForTesting.Value;
        set => ScopedFoldBackfillVerificationForTesting.Value = value;
    }

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

    // Transaction ownership (#4154): the semaphore is held for the outermost writer
    // transaction lifetime. Same-stack nested calls from the owning thread and
    // AsyncLocal token skip the semaphore and become SAVEPOINTs; other flows wait even
    // when ExecutionContext copied the token into Task.Run.
    private readonly object _transactionStateLock = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly AsyncLocal<Guid?> _currentTransactionGateToken = new();
    private static readonly TimeSpan DefaultTransactionStateContentionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransactionStateContentionWaitInterval = TimeSpan.FromMilliseconds(50);
    private const int BatchSize = 500;
    private const int DeleteFilesBatchSize = 500;
    private const int SqliteConstraintErrorCode = 19;
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

    // End-of-successful-index trust markers. The ready bits live in PRAGMA user_version so
    // that a reader can tell which subset of the index has been fully populated:
    //   bit 0 (GraphReadyFlag)  — symbol_references fully backfilled
    //   bit 1 (IssuesReadyFlag) — file_issues produced by ValidateContent
    //   bit 2 (FoldReadyFlag)   — name_folded columns populated for Unicode --exact (#86)
    // CLI and MCP full-scan indexing set graph + fold; CLI additionally sets issues (MCP
    // now persists file_issues too after bdbb2bd, so both can stamp it). The index runner
    // ClearReadyFlags() first so partial / aborted runs demote trust until a successful
    // end-of-run commit. Fold is only stamped after a full scan because a partial update
    // leaves legacy rows without folded values.
    // CLI / MCP 共に full-scan で graph + fold を立てる。fold は部分更新では立てない。
    public void MarkGraphReady() => SetReadyBit(DbContext.GraphReadyFlag);
    public void MarkIssuesReady() => SetReadyBit(DbContext.IssuesReadyFlag);

    /// <summary>
    /// Stamp FoldReadyFlag AND write the current <see cref="NameFold.Version"/> plus the
    /// runtime-sensitive <see cref="NameFold.Fingerprint"/> into `codeindex_meta`.
    /// Readers require the bit, a version match, and a fingerprint match before trusting
    /// folded columns, so both intentional fold changes and runtime ICU / invariant-casing
    /// drift degrade safely to NOCASE until `--rebuild`. Issue #97.
    ///
    /// Re-verifies <see cref="AllFoldedColumnsBackfilled"/> inside a BEGIN IMMEDIATE
    /// transaction so a concurrent writer cannot insert NULL-folded rows between the
    /// caller's pre-check and this stamp. Returns false (and writes nothing) when the
    /// re-verify fails, so callers can surface a friendly retry message instead of
    /// silently advertising fold-trust to readers. Issue #1535.
    /// FoldReady bit + fold_key_version + fold_key_fingerprint を書く。runtime drift を含む
    /// silent mismatch を防ぎ、ズレた場合は `--rebuild` まで NOCASE fallback に降格する。
    /// BEGIN IMMEDIATE で囲んだうえで再検証し、concurrent writer による NULL 行差し込みで
    /// fold_ready が嘘になるのを防ぐ。Issue #1535。
    /// </summary>
    /// <returns>True when the bit was actually stamped; false when re-verification failed.</returns>
    public bool MarkFoldReady(
        bool stampCurrentSymbolExtractorVersions = false,
        IReadOnlyCollection<string>? symbolExtractorLanguagesToStamp = null)
    {
        var gateLease = EnterTransactionGate();
        try
        {
            bool ownTransaction = !IsInTransaction();
            if (ownTransaction)
                Execute("BEGIN IMMEDIATE");
            try
            {
                if (stampCurrentSymbolExtractorVersions)
                    StampSymbolExtractorVersions(symbolExtractorLanguagesToStamp);

                if (!AllFoldedColumnsBackfilledCore(
                        requireCurrentSymbolExtractorVersions: false,
                        requireCurrentFoldKeys: true))
                {
                    if (ownTransaction)
                    {
                        Execute("COMMIT");
                        ownTransaction = false;
                    }
                    return false;
                }

                ApplyReadyBitToUserVersion(DbContext.FoldReadyFlag, ownTransaction ? null : _activeTransaction);

                SetMetaValues(
                    ("fold_key_version", NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("fold_key_fingerprint", NameFold.Fingerprint()));
                StampSymbolExtractorVersions(symbolExtractorLanguagesToStamp);

                if (ownTransaction)
                {
                    Execute("COMMIT");
                    ownTransaction = false;
                }
                return true;
            }
            catch (Exception)
            {
                if (ownTransaction)
                {
                    try { Execute("ROLLBACK"); } catch (SqliteException) { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    public void StampSymbolExtractorVersions(IReadOnlyCollection<string>? languagesToStamp = null)
    {
        var languages = languagesToStamp ?? GetIndexedLanguages();
        var values = new List<(string Key, string? Value)>(languages.Count);
        foreach (var lang in languages)
        {
            if (string.IsNullOrWhiteSpace(lang))
                continue;

            values.Add((
                DbContext.GetSymbolExtractorVersionMetaKey(lang),
                SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        SetMetaValues(values.ToArray());
    }

    /// <summary>
    /// Stamp the current C# symbol-name contract version. Readers and indexers use this to
    /// detect canonical-name upgrades such as operator/conversion/indexer renames.
    /// C# canonical symbol name 契約の current version を stamp する。
    /// </summary>
    public void MarkCSharpSymbolNameContractReady()
    {
        SetMeta(
            DbContext.CSharpSymbolNameContractVersionMetaKey,
            DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Stamp the current SQL graph storage contract version. Readers use this to distinguish
    /// pre-fix SQL graph rows (stale call columns / symbol names) from rows rewritten by the
    /// current extractor/name-resolution contract.
    /// SQL graph 保存契約の current version を stamp する。
    /// </summary>
    public void MarkSqlGraphContractReady()
    {
        SetMeta(
            DbContext.SqlGraphContractVersionMetaKey,
            DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void MarkIndexReaderContractsReady(bool symbolsOnlyGraphOmitted)
    {
        var csharpVersion = DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sqlVersion = DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (symbolsOnlyGraphOmitted)
        {
            SetMetaValues(
                (DbContext.CSharpSymbolNameContractVersionMetaKey, csharpVersion),
                (DbContext.SymbolsOnlyGraphOmittedMetaKey, "true"));
            return;
        }

        SetMetaValues(
            (DbContext.CSharpSymbolNameContractVersionMetaKey, csharpVersion),
            (DbContext.SqlGraphContractVersionMetaKey, sqlVersion),
            (DbContext.SymbolsOnlyGraphOmittedMetaKey, null));
    }

    public void ClearSqlGraphContractReady()
    {
        SetMeta(DbContext.SqlGraphContractVersionMetaKey, null);
    }

    /// <summary>
    /// Stamp the current authoritative version for hotspot family grouping semantics.
    /// Only fully authoritative DB states should call this; mixed legacy/current DBs must
    /// stay unstamped so readers degrade to conservative same-file counting.
    /// hotspots family grouping の current authoritative version を stamp する。
    /// </summary>
    public void MarkHotspotFamilyReady(string lang, string? markerFingerprint = null)
    {
        // Clear the superseded global keys so mixed-version DBs don't leave confusing stale metadata behind.
        // 廃止した global key を掃除し、混在 DB に紛らわしい古い metadata を残さない。
        SetMetaValues(
            (DbContext.GetHotspotFamilyVersionMetaKey(lang), DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), markerFingerprint),
            (DbContext.HotspotFamilyVersionMetaKey, null),
            (DbContext.HotspotFamilyMarkerFingerprintMetaKey, null));
    }

    public void MarkHotspotFamilyMarkerFingerprintIncomplete(string lang, string? markerFingerprint)
    {
        SetMetaValues(
            (DbContext.GetHotspotFamilyVersionMetaKey(lang), DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), DbContext.BuildIncompleteHotspotFamilyMarkerFingerprint(markerFingerprint)),
            (DbContext.HotspotFamilyVersionMetaKey, null),
            (DbContext.HotspotFamilyMarkerFingerprintMetaKey, null));
    }

    /// <summary>
    /// Demote hotspot-family trust. Called at the start of any indexing run that may leave
    /// a mixed legacy/current symbol set so readers fall back conservatively unless the run
    /// completes and restamps the current version.
    /// hotspot-family trust を縮退させる。index 開始時に呼び、成功時だけ再 stamp する。
    /// </summary>
    public void ClearHotspotFamilyReady()
    {
        var languages = FileIndexer.GetHotspotFamilyMarkerLanguages();
        var keys = new string[2 + (languages.Count * 2)];
        var index = 0;

        keys[index++] = DbContext.HotspotFamilyVersionMetaKey;
        keys[index++] = DbContext.HotspotFamilyMarkerFingerprintMetaKey;
        foreach (var lang in languages)
        {
            keys[index++] = DbContext.GetHotspotFamilyVersionMetaKey(lang);
            keys[index++] = DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang);
        }

        ClearMetaKeys(keys);
    }

    /// <summary>
    /// Stamp the per-language metadata-target version once the writer's resolver has finished
    /// classifying every class-like row for that language. Readers consult this stamp before
    /// trusting `symbols.is_metadata_target`. Issue #435.
    /// 言語別 metadata-target version を stamp する。reader はこの stamp 一致時のみ
    /// `symbols.is_metadata_target` を信頼する。Issue #435。
    /// </summary>
    public void MarkMetadataTargetReady(string lang)
    {
        SetMeta(
            DbContext.GetMetadataTargetVersionMetaKey(lang),
            DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public bool TypeScriptAugmentationVersionMatchesCurrent()
    {
        return string.Equals(
            GetMetaString(DbContext.TypeScriptAugmentationVersionMetaKey),
            DbContext.TypeScriptAugmentationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    public void ClearTypeScriptAugmentationReady()
    {
        SetMeta(DbContext.TypeScriptAugmentationVersionMetaKey, null);
    }

    /// <summary>
    /// Stamp the cdidx version string that wrote the most recent successful end-of-index
    /// pass. Readers compare this against their own binary version (and each persisted
    /// contract version) to surface forward-compatibility warnings when an older cdidx
    /// opens a DB last written by a newer cdidx. Issue #1515.
    /// 成功 index 末尾で書き込みを行った cdidx の version を stamp する。reader は自身の
    /// version と各 contract version と突き合わせて forward-compat 警告を出す。Issue #1515。
    /// </summary>
    public void WriteCdidxWriterVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;
        SetMeta(DbContext.CdidxWriterVersionMetaKey, version);
    }

    public void ClearLastFailedIndexRunMetadata()
    {
        ClearMetaKeys(
            DbContext.LastFailedIndexRunStatusMetaKey,
            DbContext.LastFailedIndexRunModeMetaKey,
            DbContext.LastFailedIndexRunStartedAtMetaKey,
            DbContext.LastFailedIndexRunDurationMsMetaKey,
            DbContext.LastFailedIndexRunFilesProcessedMetaKey,
            DbContext.LastFailedIndexRunFilesTotalMetaKey,
            DbContext.LastFailedIndexRunErrorCodeMetaKey,
            DbContext.LastFailedIndexRunReasonMetaKey,
            DbContext.LastFailedIndexRunProgressPersistedMetaKey,
            DbContext.LastFailedIndexRunRecoveryHintMetaKey);
    }

    /// <summary>
    /// Stamp unknown-extension scan coverage from the latest successful full-worktree scan.
    /// Stores the total count plus a bounded path sample so status callers can identify the
    /// first files that need a language mapping or ignore rule without unbounded metadata.
    /// 未知拡張子の scan coverage を保存する。件数と上限付き path sample を status で返す。
    /// </summary>
    public void WriteUnknownExtensionFileMetadata(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            SetMetaValues(
                (DbContext.UnknownExtensionFileCountMetaKey, "0"),
                (DbContext.UnknownExtensionFilePathsMetaKey, "[]"),
                (DbContext.UnknownExtensionFilesTruncatedMetaKey, false.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.UnknownExtensionFilePathLimitMetaKey, DbContext.UnknownExtensionFilePathSampleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                (DbContext.UnknownExtensionExtensionCountsMetaKey, "{}"),
                (DbContext.UnknownExtensionCategoryCountsMetaKey, "{}"),
                (DbContext.UnknownExtensionGroupsMetaKey, "[]"));
            return;
        }

        var sample = JsonStringListCodec.TakeSerializableSample(
            paths,
            DbContext.UnknownExtensionFilePathSampleLimit);
        var classification = UnknownExtensionClassifier.Classify(paths);
        SetMetaValues(
            (DbContext.UnknownExtensionFileCountMetaKey, paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionFilePathsMetaKey, JsonStringListCodec.Serialize(sample)),
            (DbContext.UnknownExtensionFilesTruncatedMetaKey, (paths.Count > sample.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionFilePathLimitMetaKey, DbContext.UnknownExtensionFilePathSampleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DbContext.UnknownExtensionExtensionCountsMetaKey, UnknownExtensionClassifier.SerializeCounts(classification.ExtensionCounts)),
            (DbContext.UnknownExtensionCategoryCountsMetaKey, UnknownExtensionClassifier.SerializeCounts(classification.CategoryCounts)),
            (DbContext.UnknownExtensionGroupsMetaKey, UnknownExtensionClassifier.SerializeGroups(classification.Groups)));
    }

    /// <summary>
    /// Demote metadata-target trust for every known language. Called at the start of any
    /// indexing run that may leave the resolver output partially stale so readers fall back
    /// to the legacy heuristic until a successful run restamps the current version.
    /// metadata-target trust を全言語まとめて縮退させる。index 開始時に呼び、成功時のみ
    /// 再 stamp する。Issue #435。
    /// </summary>
    public void ClearMetadataTargetReady()
    {
        if (!TableExists("codeindex_meta"))
            return;

        SetMeta(DbContext.GetMetadataTargetVersionMetaKey("csharp"), null);
    }

    /// <summary>
    /// Recompute `symbols.is_metadata_target` for every C# class-like row from extractor-owned
    /// direct facts plus a fixed-point resolver for transitive `System.Attribute` inheritance.
    /// Out-of-repo bases whose name ends with `Attribute` (the BCL convention) remain a bounded
    /// resolver fallback. Non-target rows are written as 0 so reader switching does not confuse
    /// "no resolver pass yet" with "resolver decided not a target". Issue #3524.
    /// C# class-like 行の `is_metadata_target` を extractor 由来の直接 fact と transitive
    /// resolver から再計算する。リポ外で末尾が `Attribute` の base 型は bounded fallback として
    /// target 扱いを残す。target でない行は明示的に 0 で書き、reader で「未解決」と区別する。
    /// </summary>
    public void ResolveCSharpMetadataTargets()
    {
        var rows = LoadCSharpClassRows();
        if (rows.Count == 0)
            return;

        // Fully-qualified-name index: `Namespace.TypeName` -> ids. Used when the base type
        // in a signature is qualified (`: A.BaseAttr`) so we do not resolve against an
        // unrelated same-simple-name class in another namespace. A LIST is required here
        // because C# `partial class` can split a single logical type across multiple
        // rows (one row per declaration site): with a single-id map, whichever row was
        // inserted first wins and any sibling partial carrying the real `: Attribute`
        // base list is dropped, making metadata-target resolution file-order dependent.
        // Issue #435 codex review iter 2.
        // 完全修飾名 `Namespace.TypeName` -> ids の索引。`partial class` で同一 FQN が複数行に
        // 分割されても、どのファイルが先に読まれても解決が安定するように List で保持する。
        var qualifiedToIds = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        // Scope-aware simple-name index: (enclosing scope, simple name) -> ids. Unqualified
        // bases must resolve through the deriving class's own namespace / nesting chain so
        // a non-attribute impostor in an UNRELATED namespace does not falsely promote the
        // deriving class to `is_metadata_target=1` just because another namespace happens
        // to contain a same-named real attribute. A global simple-name bucket was the
        // earlier design and was rejected in #435 codex review iter 4 with a reproducible
        // false-positive: `A.BaseAttr : Attribute` + `B.BaseAttr : BaseService` + deriving
        // `namespace B { class FooAttribute : BaseAttr {} }` previously returned a false
        // metadata edge for `[Foo] class Svc {}`. Issue #435 codex review iter 4.
        // スコープ対応の単純名索引。(外側スコープ, 単純名) -> ids。非修飾基底は deriving の
        // 名前空間 / 入れ子チェーンを辿って解決し、無関係な名前空間に同名の本物 attribute が
        // 存在するだけで非 attribute impostor が `is_metadata_target=1` に昇格するのを防ぐ。
        var scopeNameToIds = new Dictionary<(string Scope, string Name), List<long>>();
        var rowScope = new Dictionary<long, string>();
        var rowFileId = new Dictionary<long, long>();
        var bases = new Dictionary<long, List<string>>();
        foreach (var row in rows)
        {
            foreach (var fq in EnumerateQualifiedKeys(row.QualifiedName, row.Name))
            {
                if (!qualifiedToIds.TryGetValue(fq, out var qbucket))
                {
                    qbucket = new List<long>();
                    qualifiedToIds[fq] = qbucket;
                }
                qbucket.Add(row.Id);
            }
            string scope = GetEnclosingScope(row.QualifiedName, row.Name);
            rowScope[row.Id] = scope;
            rowFileId[row.Id] = row.FileId;
            var scopeKey = (scope, row.Name);
            if (!scopeNameToIds.TryGetValue(scopeKey, out var sbucket))
            {
                sbucket = new List<long>();
                scopeNameToIds[scopeKey] = sbucket;
            }
            sbucket.Add(row.Id);
            bases[row.Id] = ParseCSharpBaseIdentifiers(row.Signature);
        }

        // Per-file import tables so unqualified bases that come from `using Namespace;` /
        // `using Alias = FQN;` directives resolve to the right in-repo class, and repo-wide
        // aggregated `global using` so C# 10+ global directives still widen every file's
        // lookup set. Aliases can also target a qualified type (`using AliasAttr = A.BaseAttr;`)
        // whose target itself lives in a sibling file. Issue #435 codex review iter 5.
        // ファイル別 import テーブル。非修飾基底が `using Namespace;` や `using Alias = FQN;` 経由
        // で別ファイルの実体に解決される C# の一般パターンをカバーする。`global using` は全ファイルで
        // 集約して、ファイルを跨ぐ拡張も拾う。Issue #435 codex review iter 5。
        var (perFileImports, globalImports) = LoadCSharpImportsByFile();

        var extractorTargets = rows
            .Where(row => row.ExtractorMetadataTarget)
            .Select(row => row.Id)
            .ToHashSet();
        var targets = new HashSet<long>(extractorTargets);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in rows)
            {
                if (targets.Contains(row.Id))
                    continue;
                FileImportSet? fileImports = null;
                if (rowFileId.TryGetValue(row.Id, out var fid) && perFileImports.TryGetValue(fid, out var perFile))
                    fileImports = perFile;
                if (IsMetadataTargetByBases(bases[row.Id], rowScope[row.Id], targets, scopeNameToIds, qualifiedToIds, fileImports, globalImports))
                {
                    targets.Add(row.Id);
                    changed = true;
                }
            }
        }

        using var txn = !IsInTransaction() ? BeginTransaction() : null;
        bool hasMetadataTargetSource = ColumnExists("symbols", "metadata_target_source");
        string updateSql = hasMetadataTargetSource
            ? "UPDATE symbols SET is_metadata_target = @flag, metadata_target_source = @source WHERE id = @id"
            : "UPDATE symbols SET is_metadata_target = @flag WHERE id = @id";
        var update = RentCommand(
            updateSql,
            c =>
            {
                c.Parameters.Add("@flag", SqliteType.Integer);
                if (hasMetadataTargetSource)
                    c.Parameters.Add("@source", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            if (_commandCache == null)
                update.Prepare();

            var pFlag = update.Parameters["@flag"];
            var pSource = hasMetadataTargetSource ? update.Parameters["@source"] : null;
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                bool target = targets.Contains(row.Id);
                pFlag.Value = target ? 1 : 0;
                if (pSource != null)
                {
                    pSource.Value = extractorTargets.Contains(row.Id)
                        ? SymbolRecord.MetadataTargetSourceExtractor
                        : target
                            ? SymbolRecord.MetadataTargetSourceResolver
                            : DBNull.Value;
                }
                pId.Value = row.Id;
                update.ExecuteNonQuery();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }
        txn?.Commit();
    }

    private List<CSharpClassRow> LoadCSharpClassRows()
    {
        var rows = new List<CSharpClassRow>();
        if (!ColumnExists("symbols", "signature") || !ColumnExists("symbols", "is_metadata_target"))
            return rows;
        bool hasQualified = ColumnExists("symbols", "container_qualified_name");
        bool hasMetadataTargetSource = ColumnExists("symbols", "metadata_target_source");

        string sql = hasQualified
            ? $@"SELECT s.id, s.file_id, s.name, s.signature, s.container_qualified_name,
                    s.is_metadata_target, {(hasMetadataTargetSource ? "s.metadata_target_source" : "NULL")}
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind = 'class' AND s.name IS NOT NULL"
            : $@"SELECT s.id, s.file_id, s.name, s.signature, NULL,
                    s.is_metadata_target, {(hasMetadataTargetSource ? "s.metadata_target_source" : "NULL")}
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind = 'class' AND s.name IS NOT NULL";
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                bool extractorMetadataTarget = !reader.IsDBNull(5)
                    && reader.GetInt64(5) == 1
                    && !reader.IsDBNull(6)
                    && string.Equals(reader.GetString(6), SymbolRecord.MetadataTargetSourceExtractor, StringComparison.Ordinal);
                rows.Add(new CSharpClassRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    extractorMetadataTarget));
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        return rows;
    }

    private sealed record CSharpClassRow(
        long Id,
        long FileId,
        string Name,
        string? Signature,
        string? QualifiedName,
        bool ExtractorMetadataTarget);

    // Per-file import set for C# unqualified-base resolution. `Namespaces` lists each
    // `using Foo.Bar;` target so `class X : Base` can probe `Foo.Bar.Base` in the qualified
    // index; `Aliases` maps `using Alias = Foo.Bar.Type;` directives so `class X : Alias`
    // resolves to `Foo.Bar.Type`. `using static Foo.Bar;` and `extern alias Foo;` are out
    // of scope — they do not introduce a plain namespace-prefix lookup that a C# base
    // clause would use. Issue #435 codex review iter 5.
    // C# 非修飾基底解決用のファイル別 import セット。`Namespaces` は `using Foo.Bar;` の集合。
    // `Aliases` は `using Alias = Foo.Bar.Type;` のエイリアス -> ターゲット写像。`using static`
    // と `extern alias` は base 句が引けない文脈なので対象外。Issue #435 codex review iter 5。
    private sealed class FileImportSet
    {
        public List<string> Namespaces { get; } = new();
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);
    }

    // Load `symbols.kind='import'` rows for every C# file and partition each row into either
    // a namespace import or an alias import. `global using` directives (C# 10+) are aggregated
    // into a repo-wide set because they widen the import lookup in every file, even ones that
    // do not contain them literally. The split is driven by the stored signature — `using X =
    // Y.Z;` contains `=` before the terminating `;`, which distinguishes alias form from plain
    // namespace form even when both names tokenise as a single identifier.
    // `symbols.kind='import'` 行を C# ファイル別に読み、namespace 用 / alias 用に分ける。
    // `global using`（C# 10+）はリポジトリ全体に効くので、別途集約した集合として返す。判定は
    // 保存済み signature から行い、`=` があれば alias と認識する。
    private (Dictionary<long, FileImportSet> PerFile, FileImportSet Global) LoadCSharpImportsByFile()
    {
        var perFile = new Dictionary<long, FileImportSet>();
        var global = new FileImportSet();
        if (!TableExists("symbols"))
            return (perFile, global);

        const string sql = @"SELECT s.file_id, s.name, s.signature
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp' AND s.kind = 'import' AND s.name IS NOT NULL";
        var cmd = RentCommand(sql, static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                long fileId = reader.GetInt64(0);
                string rawName = reader.GetString(1);
                string? signature = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!perFile.TryGetValue(fileId, out var bag))
                {
                    bag = new FileImportSet();
                    perFile[fileId] = bag;
                }
                RegisterCSharpImport(bag, global, rawName, signature);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        return (perFile, global);
    }

    private static void RegisterCSharpImport(FileImportSet perFile, FileImportSet global, string rawName, string? signature)
    {
        string name = rawName.Trim();
        if (name.Length == 0)
            return;
        // `extern alias X;` surfaces as an `import` row too (see SymbolExtractor). We skip
        // it — extern aliases map to assemblies, not to a type/namespace the writer has
        // indexed, and the qualified-name index is unaware of the alias identity.
        // `extern alias X;` も import 行として現れるがアセンブリ別名でしかなく resolver 側の
        // qualified 索引には載らないので対象外。
        if (signature != null && signature.IndexOf("extern", StringComparison.Ordinal) >= 0
            && CSharpExternAliasSignatureRegex.IsMatch(signature))
        {
            return;
        }
        bool isGlobal = signature != null
            && CSharpGlobalUsingSignatureRegex.IsMatch(signature);
        bool isStatic = signature != null
            && CSharpUsingStaticSignatureRegex.IsMatch(signature);
        // `using static Foo.Bar;` imports the static members of `Foo.Bar` into the file's
        // scope — NOT a namespace that a base clause `class X : Base` could pull from.
        // Drop it so we don't confuse the alias/namespace paths.
        // `using static` は静的メンバーを取り込むだけで base 句の解決経路には使えない。
        if (isStatic)
            return;
        string? aliasTarget = null;
        string? aliasName = null;
        if (signature != null)
        {
            // `@?\w+` so verbatim alias names (`using @AliasAttr = A.BaseAttr;`) are captured
            // just like the SymbolExtractor side; the leading `@` is stripped below before
            // the alias enters the per-file map.
            // SymbolExtractor 側と同じく verbatim 識別子も `@?\w+` で受け、下の正規化で
            // 先頭 `@` を剥がしてから alias map に載せる。
            var m = CSharpUsingAliasSignatureRegex.Match(signature);
            if (m.Success)
            {
                aliasName = m.Groups["alias"].Value.Trim();
                aliasTarget = m.Groups["target"].Value.Trim();
            }
        }
        if (aliasName != null && aliasTarget != null && aliasName.Length > 0 && aliasTarget.Length > 0)
        {
            // Normalize `global::` prefix off the alias target so the downstream qualified
            // lookup sees the same key shape (`A.BaseAttr`) regardless of source syntax.
            // Then strip the C# verbatim `@` prefix from each identifier segment in both
            // the alias name and target — `using @AliasAttr = @Foo.@Bar.BaseAttr;` must
            // resolve identically to `using AliasAttr = Foo.Bar.BaseAttr;` because the two
            // forms are semantically equivalent in C#. Issue #435 codex review iter 6.
            // alias の target 先頭の `global::` は剥がして qualified 索引のキー形に合わせる。
            // さらに alias 名・target の各 dotted segment 先頭の verbatim `@` も剥がし、
            // `using @AliasAttr = @Foo.@Bar.BaseAttr;` が非 verbatim 形と同じキーで解決されるよう
            // 整える（C# では両者は同義）。Issue #435 codex review iter 6.
            if (aliasTarget.StartsWith("global::", StringComparison.Ordinal))
                aliasTarget = aliasTarget.Substring("global::".Length);
            aliasName = StripCSharpVerbatimPrefixes(aliasName);
            aliasTarget = StripCSharpVerbatimPrefixes(aliasTarget);
            if (aliasName.Length == 0 || aliasTarget.Length == 0)
                return;
            perFile.Aliases[aliasName] = aliasTarget;
            if (isGlobal)
                global.Aliases[aliasName] = aliasTarget;
            return;
        }
        // Fall-through: plain `using Foo.Bar;`. `name` is captured as `Foo.Bar` by the
        // SymbolExtractor regex, so we can use it directly. Trailing `global::` can sneak
        // through in exotic files (`using global::System.Linq;`) — strip it for parity
        // with the alias path so every downstream probe sees one consistent prefix.
        // Strip the C# verbatim `@` prefix from each dotted segment too so `using @Foo.@Bar;`
        // resolves identically to `using Foo.Bar;` (semantically equivalent in C#).
        // Issue #435 codex review iter 6.
        // 通常の `using Foo.Bar;` は name 側に `Foo.Bar` が入っているのでそれを使う。
        // 稀な `using global::X;` も prefix を剥がして qualified 索引と揃える。さらに
        // `using @Foo.@Bar;` のような verbatim 表記も先頭 `@` を剥がし、非 verbatim 形と
        // 同じキーで解決されるよう整える。Issue #435 codex review iter 6.
        string ns = name;
        if (ns.StartsWith("global::", StringComparison.Ordinal))
            ns = ns.Substring("global::".Length);
        ns = StripCSharpVerbatimPrefixes(ns);
        if (ns.Length == 0)
            return;
        perFile.Namespaces.Add(ns);
        if (isGlobal)
            global.Namespaces.Add(ns);
    }

    // Strip the C# verbatim-identifier `@` prefix from each identifier segment of a
    // qualified name. Segment boundaries are the start of the string, every `.`, and
    // every `::` (the alias-qualifier boundary that produces `global::Foo`,
    // `Alias::Foo`, etc.). `@Foo.@Bar.BaseAttr` → `Foo.Bar.BaseAttr`;
    // `global::@Foo.@Bar.BaseAttr` → `global::Foo.Bar.BaseAttr`; `Foo.Bar` → unchanged.
    // Runs on the writer side so every qualified-index key and every scope/import entry
    // shares one canonical form regardless of whether the source used verbatim syntax.
    // The `@` escape is purely syntactic in C# (`@class` is the identifier `class`
    // escaping a keyword), so stripping it never changes identity. Issue #435 codex
    // review iter 6 + iter 7 (the `::` boundary was missing in iter 6 so
    // `global::@Foo.@Bar.BaseAttr` stayed as `global::@Foo.Bar.BaseAttr` and did not
    // match the canonical qualified index key).
    // 修飾名の各識別子セグメント先頭に付く C# verbatim 識別子 `@` を剥がす。セグメント境界は
    // 文字列の先頭、`.`、`::`（`global::Foo` や `Alias::Foo` を作る alias 修飾境界）。
    // `@Foo.@Bar.BaseAttr` → `Foo.Bar.BaseAttr`、`global::@Foo.@Bar.BaseAttr`
    // → `global::Foo.Bar.BaseAttr`、`Foo.Bar` → そのまま。書き込み側で正規化することで、
    // qualified 索引キーと scope / import エントリをソース表記に依らない単一の canonical 形に
    // 統一する。`@` エスケープは C# では純粋に構文上のものなので（`@class` は識別子
    // `class`）、剥がしても同一性は変わらない。Issue #435 codex review iter 6 + iter 7
    // （iter 6 は `::` 境界を処理していなかったため `global::@Foo.@Bar.BaseAttr` が
    // `global::@Foo.Bar.BaseAttr` のまま残り、canonical な qualified 索引キーと一致しなかった）。
    private static string StripCSharpVerbatimPrefixes(string qualified)
    {
        if (qualified.Length == 0 || qualified.IndexOf('@') < 0)
            return qualified;
        var sb = new System.Text.StringBuilder(qualified.Length);
        bool atBoundary = true;
        for (int i = 0; i < qualified.Length; i++)
        {
            char c = qualified[i];
            if (atBoundary && c == '@'
                && i + 1 < qualified.Length
                && IsCSharpIdentifierStartChar(qualified[i + 1]))
            {
                // Skip the verbatim prefix; the next iteration emits the escaped identifier.
                atBoundary = false;
                continue;
            }
            sb.Append(c);
            if (c == '.')
            {
                atBoundary = true;
            }
            else if (c == ':' && i + 1 < qualified.Length && qualified[i + 1] == ':')
            {
                sb.Append(':');
                i++;
                atBoundary = true;
            }
            else
            {
                atBoundary = false;
            }
        }
        return sb.Length == qualified.Length ? qualified : sb.ToString();
    }

    private static bool IsCSharpIdentifierStartChar(char c) =>
        c == '_' || char.IsLetter(c);

    // Yield every qualified-name variant that callers might write against this class:
    // `Namespace.TypeName`, `global::Namespace.TypeName`, and (for nested classes whose
    // `container_qualified_name` is itself `Outer.Inner.Name`) each dotted tail so
    // `class Foo : Outer.Inner.BaseAttr` can match. Issue #435 codex review.
    // 修飾名ルックアップで match させたい表記をすべて列挙する。`Namespace.TypeName`、
    // `global::Namespace.TypeName`、および container が `Outer.Inner.Name` のような入れ子のとき
    // `Inner.Name` のような dotted tail も入れる。
    private static IEnumerable<string> EnumerateQualifiedKeys(string? containerQualifiedName, string simpleName)
    {
        var container = containerQualifiedName?.Trim();
        if (string.IsNullOrEmpty(container))
            yield break;
        // container_qualified_name in our extractor already includes the simple type name
        // at the tail, e.g. `A.FooAttribute` for `namespace A { class FooAttribute { } }`.
        // Some callers may also reference the type via `global::A.FooAttribute`, so emit
        // both forms. Defensive check: if the tail segment does not match simpleName, also
        // append simpleName as an extra candidate so we still index a usable qualified key.
        // container_qualified_name は末尾に自身の単純名を含む想定（例: `A.FooAttribute`）。
        // `global::` 付きでも参照され得るため両形を yield する。末尾が simpleName と一致しない
        // 非想定 DB でも simpleName を補った候補を 1 つ追加で出し、ルックアップ漏れを防ぐ。
        string fq = container;
        int lastDot = fq.LastIndexOf('.');
        string tail = lastDot >= 0 ? fq.Substring(lastDot + 1) : fq;
        if (!string.Equals(tail, simpleName, StringComparison.Ordinal))
            fq = container + "." + simpleName;

        yield return fq;
        yield return "global::" + fq;
        // Also yield dotted suffixes so `Outer.Inner.Name` can match a base reference of
        // just `Inner.Name`. Skip the leaf-only `Name` form — that overlaps with the
        // simple-name map and we do not want qualified-base lookup to silently resolve
        // an unqualified match. / `Outer.Inner.Name` の末尾 `Inner.Name` のような表記を
        // qualified ルックアップで当てるために dotted suffix も yield する。`Name` 単独は
        // simple-name map 側と重複するので除外する。
        int searchFrom = 0;
        while (true)
        {
            int dot = fq.IndexOf('.', searchFrom);
            if (dot < 0) break;
            var suffix = fq.Substring(dot + 1);
            if (suffix.IndexOf('.') < 0) break; // leaf-only — skip
            yield return suffix;
            searchFrom = dot + 1;
        }
    }

    // Derive the deriving class's enclosing scope from its container_qualified_name.
    // For `namespace A.B { class Foo { } }` the QualifiedName is `A.B.Foo`, and stripping
    // the trailing simple name yields `A.B`. Nested types (`namespace A { class Outer {
    // class Inner { } } }` → `A.Outer.Inner`) yield `A.Outer`. Top-level non-namespaced
    // types yield `""`. A null / empty QualifiedName also yields `""`, which matches
    // the implicit "global" scope bucket populated in `ResolveCSharpMetadataTargets`.
    // Issue #435 codex review iter 4.
    // 非修飾基底解決で使う deriving の外側スコープを container_qualified_name から導く。
    // `namespace A.B { class Foo { } }` の QualifiedName `A.B.Foo` からは末尾の Foo を
    // 除いた `A.B` を返し、ネストした `A.Outer.Inner` は `A.Outer`、トップレベル型や
    // null / 空の場合は `""`（グローバルスコープ）を返す。
    private static string GetEnclosingScope(string? qualifiedName, string simpleName)
    {
        var fq = qualifiedName?.Trim();
        if (string.IsNullOrEmpty(fq))
            return string.Empty;
        int lastDot = fq.LastIndexOf('.');
        string tail = lastDot >= 0 ? fq.Substring(lastDot + 1) : fq;
        if (string.Equals(tail, simpleName, StringComparison.Ordinal))
            return lastDot >= 0 ? fq.Substring(0, lastDot) : string.Empty;
        // container_qualified_name does not end with the row's simple name (unexpected
        // shape from older extractors). Treat the whole container as the enclosing scope
        // so at least exact-same-scope matches still work; the chain walk will still
        // climb outward. / 想定外の container 形状では container 全体をスコープ扱いする。
        return fq;
    }

    private static bool IsMetadataTargetByBases(
        List<string> baseIdentifiers,
        string derivingScope,
        HashSet<long> resolvedTargets,
        Dictionary<(string Scope, string Name), List<long>> scopeNameToIds,
        Dictionary<string, List<long>> qualifiedToIds,
        FileImportSet? fileImports,
        FileImportSet? globalImports)
    {
        foreach (var rawBaseName in baseIdentifiers)
        {
            if (rawBaseName.Length == 0)
                continue;
            // Normalize verbatim `@` prefixes in the base identifier so `class Foo : @BaseAttr`
            // and `class Foo : @Bar.@BaseAttr` share the same lookup key with their
            // non-verbatim counterparts. Import maps are already normalized by
            // `RegisterCSharpImport`, so we only need to canonicalize the deriving side here.
            // Issue #435 codex review iter 6.
            // base 識別子側の verbatim `@` も剥がし、import map と揃える（import 側は
            // `RegisterCSharpImport` で正規化済み）。Issue #435 codex review iter 6.
            var baseName = StripCSharpVerbatimPrefixes(rawBaseName);
            if (baseName.Length == 0)
                continue;
            // Direct System.Attribute / Attribute reference / 直接 Attribute 派生
            if (baseName == "Attribute"
                || baseName == "System.Attribute"
                || baseName == "global::System.Attribute"
                || baseName == "global::Attribute")
                return true;

            // Split qualified vs unqualified. Qualified bases (containing `.` or `::`)
            // resolve against the fully-qualified index so we do not leak into unrelated
            // same-simple-name classes in another namespace. Unqualified bases resolve
            // against the deriving class's own scope chain (same namespace / nesting
            // chain only) — NOT against a global simple-name bucket, because that bucket
            // would false-match a real attribute in an unrelated namespace when the
            // deriving file has the same simple name for a non-attribute class. Issue
            // #435 codex review iter 4.
            // 修飾名（`.` または `::` を含む）は完全修飾索引で解決し、別名前空間の同名 class
            // に解決してしまうのを防ぐ。非修飾名は deriving 自身のスコープチェーン（同一
            // 名前空間 / 入れ子チェーン）のみで解決し、グローバル単純名索引は使わない。
            bool isQualified = baseName.IndexOf('.') >= 0 || baseName.IndexOf("::", StringComparison.Ordinal) >= 0;
            var head = baseName;
            int lastDot = head.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < head.Length)
                head = head.Substring(lastDot + 1);

            if (isQualified)
            {
                // Normalize `global::` prefix — always try both forms against the qualified index.
                // `global::` を剥がした形と元の両方で修飾索引を引く。
                var normalized = baseName.StartsWith("global::", StringComparison.Ordinal)
                    ? baseName.Substring("global::".Length)
                    : baseName;
                // Alias expansion for qualified bases: `using Alias = A.B;` followed by
                // `class Foo : Alias.C` must resolve to `A.B.C` per C# lookup rules. The
                // earlier unqualified alias path only handles `class Foo : Alias` — it
                // cannot see `Alias.C` because `Alias.C` was already routed into the
                // qualified branch by the `.` check. Without this expansion the resolver
                // silently drops every `class FooAttribute : Alias.MetaBase` pattern
                // where `MetaBase : Attribute` lives under the alias target namespace.
                // File-local aliases take precedence over global usings per C# rules.
                // Alias target strings in the import map are already canonicalized (no
                // `global::`, no verbatim `@`), so we only need to splice the first
                // segment of the qualified base with the alias target.
                // Issue #435 codex review iter 8.
                // 修飾基底の alias 展開: `using Alias = A.B;` の下で `class Foo : Alias.C` は
                // `A.B.C` に解決される。非修飾 alias 経路（上方）は `class Foo : Alias` しか
                // 扱えないため、この展開が無いと `class FooAttribute : Alias.MetaBase` のような
                // 実運用パターンで `MetaBase : Attribute` が同 repo にあっても edge が落ちる。
                // alias target は RegisterCSharpImport 時に canonical 化済みなので、qualified
                // の先頭セグメントを alias target に差し替えるだけで良い。Issue #435 iter 8。
                string? aliasExpanded = ExpandCSharpAliasQualifiedBase(normalized, fileImports)
                                      ?? ExpandCSharpAliasQualifiedBase(normalized, globalImports);
                // If the alias itself points to `System.Attribute` (e.g.
                // `using Sys = System; class Foo : Sys.Attribute`), honor the direct-attr rule.
                // alias 展開先が BCL `Attribute` そのものなら直接 attribute とみなす。
                if (aliasExpanded == "Attribute" || aliasExpanded == "System.Attribute")
                    return true;
                if (qualifiedToIds.TryGetValue(baseName, out var qids)
                    || qualifiedToIds.TryGetValue(normalized, out qids)
                    || (aliasExpanded != null && qualifiedToIds.TryGetValue(aliasExpanded, out qids)))
                {
                    bool anyResolved = false;
                    foreach (var qid in qids)
                    {
                        if (resolvedTargets.Contains(qid))
                        {
                            anyResolved = true;
                            break;
                        }
                    }
                    if (anyResolved)
                        return true;
                    // Matched specific qualified in-repo classes but none (yet) resolved.
                    // Wait for the next iteration instead of falling to the BCL heuristic —
                    // promoting it would contradict the user's explicit qualified reference.
                    // A list is needed here because `partial class` can split a single FQN
                    // across multiple rows; if only the declaration carrying `: Attribute`
                    // is the real target, we must still iterate to promote it.
                    // 修飾名で具体的な class 群に当たったが未確定。次回反復に委ねる。partial
                    // で同一 FQN が複数行に分かれている場合も、どれか 1 つでも target になれば
                    // この if で拾えるよう list で保持している。
                    continue;
                }
                // Qualified base did not match any in-repo class — treat as external and
                // fall through to the BCL suffix fallback below without consulting the
                // simple-name map (which could false-match an unrelated class).
                // 修飾名が repo 内で見つからない場合は外部基底として扱い、単純名索引は引かず
                // 末尾サフィックス規約のフォールバックに任せる。
                if (head.Length > "Attribute".Length && head.EndsWith("Attribute", StringComparison.Ordinal))
                    return true;
                continue;
            }

            // Scope-aware unqualified resolution: walk the deriving class's scope chain
            // from innermost outward, stopping at the first level that has a same-name
            // row. Only that bucket is consulted — we do NOT fall back to a global
            // simple-name bucket, because that would false-promote when a same-named
            // real attribute happens to live in an unrelated namespace. The chain walk
            // also naturally handles nested types (e.g. `Outer.Inner : Base` checks
            // `Outer` before `""`) and top-level types (scope starts at `""`). Issue
            // #435 codex review iter 4.
            // 非修飾基底の解決は deriving のスコープチェーンを内側から外側へ辿り、最初に
            // 同名行が見つかった階層のバケットだけで判定する。グローバル単純名へのフォール
            // バックは行わない（無関係な名前空間の本物 attribute で偽昇格するため）。
            List<long>? scopedIds = null;
            string? scope = derivingScope;
            while (scope != null)
            {
                if (scopeNameToIds.TryGetValue((scope, head), out var found))
                {
                    scopedIds = found;
                    break;
                }
                if (scope.Length == 0)
                    break;
                int lastDotInScope = scope.LastIndexOf('.');
                scope = lastDotInScope >= 0 ? scope.Substring(0, lastDotInScope) : string.Empty;
            }

            if (scopedIds != null)
            {
                bool anyResolved = false;
                foreach (var id in scopedIds)
                {
                    if (resolvedTargets.Contains(id))
                    {
                        anyResolved = true;
                        break;
                    }
                }
                if (anyResolved)
                    return true;
                // Same-scope in-repo class exists but is not (yet) a target — wait for
                // the next fixed-point iteration. Don't fall through to the BCL
                // heuristic because that would incorrectly promote a non-attribute
                // in-repo class that literally shadows the base name.
                // 同スコープに in-repo class があるなら BCL ヒューリスティックに落とさず、次回反復に委ねる。
                continue;
            }

            // Import-aware fallback: the deriving file may bring the base type into scope via
            // `using Namespace;` (plain namespace import) or `using Alias = FQN;` (alias
            // import). The C# compiler considers these before concluding a base is external,
            // and production codebases routinely split `A.BaseAttr : Attribute` and
            // `B.FooAttribute : BaseAttr` across sibling files with a `using A;` at the top.
            // Without this path, iter 4's strict same-scope rule false-negatives every such
            // file and emits zero metadata edges. Issue #435 codex review iter 5.
            // ファイルが持つ `using Namespace;` / `using Alias = FQN;` を経由した解決。C# の
            // 一般的な `using A; class FooAttribute : BaseAttr {}` パターンで、`A.BaseAttr :
            // Attribute` が別ファイルにある場合に、これが無いと iter 4 は false-negative になる。
            bool anyImportInRepoMatch = false;
            // 1. Alias imports: `using AliasAttr = A.BaseAttr;` → probe qualified index with
            //    the alias target. Alias matches take precedence over namespace imports per
            //    C# lookup rules.
            // 1. alias import: `using AliasAttr = A.BaseAttr;` は qualified 索引を target で引く。
            if (TryResolveAliasImport(head, fileImports, qualifiedToIds, resolvedTargets, out var aliasMatched, out var aliasResolved))
            {
                if (aliasResolved)
                    return true;
                if (aliasMatched)
                    anyImportInRepoMatch = true;
            }
            if (TryResolveAliasImport(head, globalImports, qualifiedToIds, resolvedTargets, out aliasMatched, out aliasResolved))
            {
                if (aliasResolved)
                    return true;
                if (aliasMatched)
                    anyImportInRepoMatch = true;
            }
            // 2. Namespace imports: for every `using Ns;` probe `Ns.head` in the qualified
            //    index. A single file often has several namespace imports; any one that hits
            //    an in-repo class is enough to stop the BCL suffix fallback from firing.
            // 2. namespace import: `using Ns;` ごとに `Ns.head` を qualified 索引で引く。
            if (TryResolveNamespaceImport(head, fileImports, qualifiedToIds, resolvedTargets, out var nsMatched, out var nsResolved))
            {
                if (nsResolved)
                    return true;
                if (nsMatched)
                    anyImportInRepoMatch = true;
            }
            if (TryResolveNamespaceImport(head, globalImports, qualifiedToIds, resolvedTargets, out nsMatched, out nsResolved))
            {
                if (nsResolved)
                    return true;
                if (nsMatched)
                    anyImportInRepoMatch = true;
            }

            if (anyImportInRepoMatch)
            {
                // An import resolved to a concrete in-repo class that is not (yet) a
                // target — wait for the next fixed-point iteration. Falling through to the
                // BCL suffix heuristic would contradict the user's explicit import and
                // false-promote when the imported class is genuinely not an Attribute.
                // import 経由で in-repo class には当たったが未確定。次回反復に委ねる。
                continue;
            }

            // No in-scope same-name row AND no import match — treat as external and use the
            // BCL suffix fallback. Intentionally does NOT consult a global simple-name
            // bucket; that was the iter 4 false-positive. / スコープチェーンにも import にも
            // 同名行が無ければ外部基底として扱い、末尾サフィックス規約のみにフォールバックする。
            if (head.Length > "Attribute".Length && head.EndsWith("Attribute", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Expand a qualified C# base name against `using Alias = Target;` entries so that
    // `Alias.C` and `Alias::C` both resolve to `Target.C`. The C# spec allows either
    // `.` (member access) or `::` (qualified-alias-member, §7.8) as the alias
    // separator for using-alias directives when the alias names a namespace.
    // Returns null when the first segment is not an alias in the given import set.
    // Alias targets are pre-canonicalized by `RegisterCSharpImport` (no `global::`,
    // no verbatim `@`); a leading `global::` in the stored target is still stripped
    // defensively for older migrations. The rest of the qualified name after the
    // alias separator is spliced with `.` so `Alias::Outer.Inner` collapses to
    // `Target.Outer.Inner` — that matches how `qualifiedToIds` keys are stored.
    // Issue #435 codex review iter 8 + iter 9 (`::` separator).
    // qualified 基底名を alias import で展開。`Alias.C` と `Alias::C` のいずれも
    // `Target.C` に書き換える。C# の仕様では using alias が名前空間を指す場合、
    // alias 区切りとして `.`（メンバ アクセス）または `::`（qualified-alias-member、
    // §7.8）が使える。先頭セグメントが alias でなければ null。alias target は登録時
    // に canonical 化済み（`global::` なし・`@` なし）だが、旧マイグレーション対応で
    // `global::` を剥がす。alias 区切り以降は `.` で繋ぎ直すので `Alias::Outer.Inner`
    // も `Target.Outer.Inner` に畳める — `qualifiedToIds` のキー形式に合わせる。
    // Issue #435 iter 8 + iter 9（`::` 区切り）。
    private static string? ExpandCSharpAliasQualifiedBase(string qualified, FileImportSet? imports)
    {
        if (imports == null)
            return null;
        if (qualified.Length == 0)
            return null;
        // Find the earliest alias separator: either `.` or `::`, whichever comes first.
        // alias 区切り（`.` または `::`）の先頭出現位置を採用する。
        int firstDot = qualified.IndexOf('.');
        int firstColonColon = qualified.IndexOf("::", StringComparison.Ordinal);
        int boundary;
        int sepLen;
        if (firstDot < 0 && firstColonColon < 0)
            return null;
        if (firstDot < 0)
        {
            boundary = firstColonColon;
            sepLen = 2;
        }
        else if (firstColonColon < 0)
        {
            boundary = firstDot;
            sepLen = 1;
        }
        else if (firstDot < firstColonColon)
        {
            boundary = firstDot;
            sepLen = 1;
        }
        else
        {
            boundary = firstColonColon;
            sepLen = 2;
        }
        if (boundary <= 0)
            return null;
        string prefix = qualified.Substring(0, boundary);
        if (!imports.Aliases.TryGetValue(prefix, out var target))
            return null;
        if (target.StartsWith("global::", StringComparison.Ordinal))
            target = target.Substring("global::".Length);
        if (target.Length == 0)
            return null;
        string suffix = qualified.Substring(boundary + sepLen);
        return suffix.Length == 0 ? target : target + "." + suffix;
    }

    private static bool TryResolveAliasImport(
        string head,
        FileImportSet? imports,
        Dictionary<string, List<long>> qualifiedToIds,
        HashSet<long> resolvedTargets,
        out bool matchedAnyInRepoClass,
        out bool resolvedToTarget)
    {
        matchedAnyInRepoClass = false;
        resolvedToTarget = false;
        if (imports == null)
            return false;
        if (!imports.Aliases.TryGetValue(head, out var target))
            return false;
        // Alias may point to BCL `Attribute` directly — honor the direct-attribute rule.
        // alias の先が BCL Attribute そのものなら直接 attribute とみなす。
        if (target == "System.Attribute" || target == "Attribute"
            || target == "global::System.Attribute" || target == "global::Attribute")
        {
            resolvedToTarget = true;
            return true;
        }
        if (target.StartsWith("global::", StringComparison.Ordinal))
            target = target.Substring("global::".Length);
        if (qualifiedToIds.TryGetValue(target, out var ids))
        {
            matchedAnyInRepoClass = true;
            foreach (var id in ids)
            {
                if (resolvedTargets.Contains(id))
                {
                    resolvedToTarget = true;
                    return true;
                }
            }
        }
        // Alias target did not match an in-repo class. If the target's simple-name tail
        // ends with `Attribute` we still trust the BCL convention for an external base.
        // alias 先が repo 内に無くても simple tail が `Attribute` で終わるなら BCL 規約で attribute 扱い。
        int lastDotInTarget = target.LastIndexOf('.');
        string tail = lastDotInTarget >= 0 ? target.Substring(lastDotInTarget + 1) : target;
        if (tail.Length > "Attribute".Length && tail.EndsWith("Attribute", StringComparison.Ordinal))
        {
            resolvedToTarget = true;
            return true;
        }
        return true;
    }

    private static bool TryResolveNamespaceImport(
        string head,
        FileImportSet? imports,
        Dictionary<string, List<long>> qualifiedToIds,
        HashSet<long> resolvedTargets,
        out bool matchedAnyInRepoClass,
        out bool resolvedToTarget)
    {
        matchedAnyInRepoClass = false;
        resolvedToTarget = false;
        if (imports == null)
            return false;
        bool any = false;
        foreach (var ns in imports.Namespaces)
        {
            if (ns.Length == 0)
                continue;
            var key = ns + "." + head;
            if (!qualifiedToIds.TryGetValue(key, out var ids))
                continue;
            any = true;
            matchedAnyInRepoClass = true;
            foreach (var id in ids)
            {
                if (resolvedTargets.Contains(id))
                {
                    resolvedToTarget = true;
                    return true;
                }
            }
        }
        return any;
    }

    /// <summary>
    /// Extract base-type head identifiers from a C# class signature, respecting generic depth
    /// so that `Foo<Bar, Baz> : IBase, IOther<Bar>` yields ["IBase", "IOther"]. Stops at the
    /// first `where` clause (generic constraints are not bases) and trims modifiers like
    /// `public sealed`.
    /// C# class signature から基底/インターフェース識別子の頭を抜き出す。`<...>` の depth を
    /// 数えて generic argument 内の `,` を区切りに誤認しないようにし、`where` 制約は除外する。
    /// </summary>
    internal static List<string> ParseCSharpBaseIdentifiers(string? signature)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(signature))
            return result;

        int colonIdx = FindBaseListColon(signature);
        if (colonIdx < 0)
            return result;

        int start = colonIdx + 1;
        int genericDepth = 0;
        var current = new System.Text.StringBuilder();
        for (int i = start; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                genericDepth++;
                current.Append(c);
                continue;
            }
            if (c == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                current.Append(c);
                continue;
            }
            if (c == '{')
                break;
            if (genericDepth == 0 && c == ',')
            {
                AddBaseIfPresent(result, current.ToString());
                current.Clear();
                continue;
            }
            // `where T : ...` ends the base list
            if (genericDepth == 0 && (c == 'w' || c == 'W'))
            {
                if (LooksLikeWhereKeyword(signature, i))
                {
                    AddBaseIfPresent(result, current.ToString());
                    current.Clear();
                    return result;
                }
            }
            current.Append(c);
        }
        AddBaseIfPresent(result, current.ToString());
        return result;
    }

    private static int FindBaseListColon(string signature)
    {
        int genericDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<') { genericDepth++; continue; }
            if (c == '>') { if (genericDepth > 0) genericDepth--; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { if (parenDepth > 0) parenDepth--; continue; }
            if (c == '{')
                return -1;
            // `class Foo<T> where T : IBar {}` has no base list — only a generic constraint.
            // If we reach a top-level `where` before finding `:`, treat that `:` as a
            // constraint separator, not a base list opener. Issue #435 codex review.
            // `class Foo<T> where T : IBar {}` のように base list を持たない場合、ここで遭遇する
            // `:` は generic constraint の区切りなので base list colon として採用しない。
            if (genericDepth == 0 && parenDepth == 0 && (c == 'w' || c == 'W')
                && LooksLikeWhereKeyword(signature, i))
            {
                return -1;
            }
            if (c == ':' && genericDepth == 0 && parenDepth == 0)
            {
                // Skip `::` namespace alias separator / `::` 名前空間エイリアスは除外
                if (i + 1 < signature.Length && signature[i + 1] == ':')
                {
                    i++;
                    continue;
                }
                if (i > 0 && signature[i - 1] == ':')
                    continue;
                return i;
            }
        }
        return -1;
    }

    private static bool LooksLikeWhereKeyword(string signature, int i)
    {
        if (i + 5 > signature.Length)
            return false;
        if (string.Compare(signature, i, "where", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (i > 0)
        {
            char prev = signature[i - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_')
                return false;
        }
        if (i + 5 < signature.Length)
        {
            char next = signature[i + 5];
            if (char.IsLetterOrDigit(next) || next == '_')
                return false;
        }
        return true;
    }

    private static void AddBaseIfPresent(List<string> result, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return;
        // Take the head identifier (everything before `<` or whitespace) but preserve
        // any namespace prefix so the caller can treat `System.Attribute` directly.
        // `<` 以前と空白以前を頭とし、`System.Attribute` などの名前空間付きはそのまま残す。
        int cut = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '<' || char.IsWhiteSpace(c))
            {
                cut = i;
                break;
            }
        }
        var head = trimmed.Substring(0, cut);
        if (head.Length > 0)
            result.Add(head);
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Upsert a metadata key/value into `codeindex_meta`.
    /// codeindex_meta への key/value の upsert。
    /// </summary>
    public void SetMeta(string key, string? value)
    {
        if (!HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT set_meta_atomic");
            try
            {
                SetMetaCore(key, value);
                Execute("RELEASE SAVEPOINT set_meta_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT set_meta_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT set_meta_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaCore(key, value);
    }

    private void SetMetaCore(string key, string? value)
    {
        var cmd = RentCommand(
            @"INSERT INTO codeindex_meta (key, value) VALUES (@key, @value)
                            ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            static c =>
            {
                c.Parameters.Add("@key", SqliteType.Text);
                c.Parameters.Add("@value", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@value"].Value = (object?)value ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public void SetMetaValues(params (string Key, string? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0 || !HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT set_meta_values_atomic");
            try
            {
                SetMetaValuesCore(values);
                Execute("RELEASE SAVEPOINT set_meta_values_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT set_meta_values_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT set_meta_values_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaValuesCore(values);
    }

    private void SetMetaValuesCore(IReadOnlyList<(string Key, string? Value)> values)
    {
        var keyParameterNames = new string[values.Count];
        var valueParameterNames = new string[values.Count];
        var rows = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            keyParameterNames[i] = "@meta_key" + suffix;
            valueParameterNames[i] = "@meta_value" + suffix;
            rows[i] = "(" + keyParameterNames[i] + ", " + valueParameterNames[i] + ")";
        }

        var sql = "INSERT INTO codeindex_meta (key, value) VALUES " + string.Join(", ", rows)
            + " ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        var cmd = RentCommand(
            sql,
            c =>
            {
                for (var i = 0; i < keyParameterNames.Length; i++)
                {
                    c.Parameters.Add(keyParameterNames[i], SqliteType.Text);
                    c.Parameters.Add(valueParameterNames[i], SqliteType.Text);
                }
            });
        try
        {
            for (var i = 0; i < values.Count; i++)
            {
                cmd.Parameters[keyParameterNames[i]].Value = values[i].Key;
                cmd.Parameters[valueParameterNames[i]].Value = (object?)values[i].Value ?? DBNull.Value;
            }
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private void ClearMetaKeys(params string[] keys)
    {
        if (keys.Length == 0 || !HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT clear_meta_keys_atomic");
            try
            {
                ClearMetaKeysCore(keys);
                Execute("RELEASE SAVEPOINT clear_meta_keys_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT clear_meta_keys_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT clear_meta_keys_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        ClearMetaKeysCore(keys);
    }

    private void ClearMetaKeysCore(IReadOnlyList<string> keys)
    {
        var values = new (string Key, string? Value)[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            values[i] = (keys[i], null);

        SetMetaValuesCore(values);
    }

    private string? GetMetaString(string key)
    {
        var cmd = RentCommand(
            "SELECT value FROM codeindex_meta WHERE key = @key",
            static c => c.Parameters.Add("@key", SqliteType.Text));
        try
        {
            cmd.Parameters["@key"].Value = key;
            return cmd.ExecuteScalar() as string;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }
    public void ClearReadyFlags() => Execute("PRAGMA user_version = 0");

    public bool HasMetaTable() => TableExists("codeindex_meta");

    private bool TableExists(string name)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            static c => c.Parameters.Add("@name", SqliteType.Text));
        try
        {
            cmd.Parameters["@name"].Value = name;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// True only when every existing row in symbols / symbol_references has a populated folded
    /// value for each source name that is itself non-NULL. Callers use this before stamping
    /// `FoldReadyFlag` on a full scan because the default incremental path skips unchanged files
    /// — their pre-#86 rows still carry NULL folded columns, so a naive stamp would flip readers
    /// onto the folded equality path and silently miss those legacy rows. Codex #86 review.
    /// full scan 成功時でも、incremental で skip された legacy 行が NULL のまま残っていれば
    /// fold-ready にしてはならない。stamp 前にこの実検証を通す。
    /// </summary>
    public bool AllFoldedColumnsBackfilled(
        bool requireCurrentSymbolExtractorVersions = false,
        bool requireCurrentFoldKeys = false)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(
        bool requireCurrentSymbolExtractorVersions,
        bool requireCurrentFoldKeys)
    {
        if (requireCurrentSymbolExtractorVersions && !SymbolExtractorVersionsMatchCurrent())
            return false;

        FoldBackfillVerificationForTesting?.Invoke();
        var cmd = RentCommand(
            @"
            SELECT
                (SELECT COUNT(*) FROM symbols WHERE name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE container_name IS NOT NULL AND container_name_folded IS NULL)",
            static _ => { });
        try
        {
            var raw = cmd.ExecuteScalar();
            long missing = raw is long l ? l : (raw is int i ? i : 0);
            if (missing != 0)
                return false;
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return !requireCurrentFoldKeys || AllFoldedColumnValuesMatchCurrentFold();
    }

    public bool AllFoldedColumnValuesMatchCurrentFold()
    {
        var symbols = RentCommand(
            "SELECT name, name_folded FROM symbols WHERE name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = symbols.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var expected = NameFold.Fold(reader.GetString(0));
                var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    return false;
            }
        }
        finally
        {
            ReleaseCommand(symbols);
        }

        var references = RentCommand(
            @"
                SELECT symbol_name, symbol_name_folded, container_name, container_name_folded
                FROM symbol_references
                WHERE symbol_name IS NOT NULL OR container_name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = references.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (!reader.IsDBNull(0))
                {
                    var expected = NameFold.Fold(reader.GetString(0));
                    var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }

                if (!reader.IsDBNull(2))
                {
                    var expected = NameFold.Fold(reader.GetString(2));
                    var actual = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }
            }
        }
        finally
        {
            ReleaseCommand(references);
        }

        return true;
    }

    public bool AllFoldedColumnsBackfilled(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (requireCurrentSymbolExtractorLanguages.Count > 0
            && !SymbolExtractorVersionsMatchCurrent(requireCurrentSymbolExtractorLanguages))
        {
            return false;
        }

        return AllFoldedColumnsBackfilledCore(
            requireCurrentSymbolExtractorVersions: false,
            requireCurrentFoldKeys: false);
    }

    public bool SymbolExtractorVersionsMatchCurrent()
    {
        foreach (var lang in GetIndexedLanguages())
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    public bool SymbolExtractorVersionsMatchCurrent(IEnumerable<string> languages)
    {
        foreach (var lang in languages)
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    private bool SymbolExtractorVersionMatchesCurrent(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return true;

        var stored = GetMetaString(DbContext.GetSymbolExtractorVersionMetaKey(lang));
        if (stored == null)
            return true;

        var current = SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return stored == current;
    }

    /// <summary>
    /// Recompute persisted folded-name keys from existing symbol / reference rows without
    /// reparsing source files. This is used to upgrade legacy DBs (NULL folded columns) and
    /// to refresh stored keys after a future <see cref="NameFold.Version"/> bump.
    /// ソース再解析なしで既存行から folded key を再計算する。legacy DB の NULL 埋めと、
    /// 将来の <see cref="NameFold.Version"/> 変更時の key 再生成に使う。
    /// </summary>
    /// <param name="rewriteAll">
    /// When true, rewrite every non-null source name even if the folded column is already
    /// populated. Needed when the stored fold metadata does not match the current binary/runtime.
    /// true のとき、既に埋まっている folded 列も含めて全行再計算する（fold metadata 不一致時）。
    /// </param>
    /// <returns>Counts of symbol rows and reference rows rewritten.</returns>
    public (int Symbols, int SymbolReferences) BackfillFoldedColumns(
        bool rewriteAll = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foldBackfillPhase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var symbols = BackfillSymbolFoldedRows(rewriteAll, cancellationToken);
        if (rewriteAll && foldBackfillPhase != "references")
        {
            SetMeta(FoldBackfillPhaseMetaKey, "references");
            SetMeta(FoldBackfillLastReferenceIdMetaKey, "0");
        }

        var symbolReferences = BackfillReferenceFoldedRows(rewriteAll, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (rewriteAll)
            ClearFoldBackfillCheckpoint();

        return (symbols, symbolReferences);
    }

    public (int Symbols, int SymbolReferences) CountBackfillFoldedColumns(bool rewriteAll = false)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;

        var symbolsSql = rewriteAll && phase != "references"
            ? "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId"
            : rewriteAll
            ? "SELECT 0"
            : "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
        var symbolsUsesCheckpoint = rewriteAll && phase != "references";
        var symbols = RentCommand(
            symbolsSql,
            symbolsUsesCheckpoint
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });

        var referencesSql = rewriteAll
            ? @"SELECT COUNT(*)
                FROM symbol_references
                WHERE id > @lastReferenceId
                  AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)"
            : @"SELECT COUNT(*)
                FROM symbol_references
                WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                   OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var references = RentCommand(
            referencesSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });

        try
        {
            if (symbolsUsesCheckpoint)
                symbols.Parameters["@lastSymbolId"].Value = lastSymbolId;
            if (rewriteAll)
                references.Parameters["@lastReferenceId"].Value = phase == "references" ? lastReferenceId : 0;

            return (ToInt32Count(symbols.ExecuteScalar()), ToInt32Count(references.ExecuteScalar()));
        }
        finally
        {
            ReleaseCommand(references);
            ReleaseCommand(symbols);
        }
    }

    private static int ToInt32Count(object? value)
    {
        var count = value is long l ? l : (value is int i ? i : 0);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private int BackfillSymbolFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        if (phase == "references")
            return 0;

        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var rows = new List<(long Id, string Name)>();
        var selectSql = rewriteAll
            ? "SELECT id, name FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId ORDER BY id"
            : "SELECT id, name FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastSymbolId"].Value = lastSymbolId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            "UPDATE symbols SET name_folded = @folded WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@folded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pFolded = update.Parameters["@folded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pFolded.Value = (object?)NameFold.Fold(row.Name) ?? DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastSymbolIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private int BackfillReferenceFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;
        var rows = new List<(long Id, string? SymbolName, string? ContainerName)>();
        var selectSql = rewriteAll
            ? @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE id > @lastReferenceId
                      AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)
                    ORDER BY id"
            : @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                       OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastReferenceId"].Value = lastReferenceId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            @"UPDATE symbol_references
                               SET symbol_name_folded = @symbolNameFolded,
                                   container_name_folded = @containerNameFolded
                               WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@symbolNameFolded", SqliteType.Text);
                c.Parameters.Add("@containerNameFolded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pSymbolNameFolded = update.Parameters["@symbolNameFolded"];
            var pContainerNameFolded = update.Parameters["@containerNameFolded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pSymbolNameFolded.Value = (object?)NameFold.Fold(row.SymbolName) ?? DBNull.Value;
                pContainerNameFolded.Value = (object?)NameFold.Fold(row.ContainerName) ?? DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastReferenceIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private long GetFoldBackfillCheckpoint(string key)
    {
        var value = GetMetaString(key);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private void ClearFoldBackfillCheckpoint()
    {
        SetMeta(FoldBackfillPhaseMetaKey, null);
        SetMeta(FoldBackfillLastSymbolIdMetaKey, null);
        SetMeta(FoldBackfillLastReferenceIdMetaKey, null);
    }

    private static object FoldedNameDbValue(string? name, Dictionary<string, string?> cache)
    {
        if (name == null)
            return DBNull.Value;

        if (!cache.TryGetValue(name, out var folded))
        {
            folded = NameFold.Fold(name);
            cache[name] = folded;
        }

        return (object?)folded ?? DBNull.Value;
    }

    private static Dictionary<string, string?> CreateFoldedNameCache(int rowCount, int namesPerRow)
    {
        if (rowCount <= 0 || namesPerRow <= 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var capacity = rowCount > int.MaxValue / namesPerRow
            ? int.MaxValue
            : rowCount * namesPerRow;
        return new Dictionary<string, string?>(capacity, StringComparer.Ordinal);
    }

    private static StringBuilder CreateBatchSqlBuilder(int rowCount, int estimatedCharsPerRow)
    {
        const int BaseCapacity = 256;
        if (rowCount <= 0 || estimatedCharsPerRow <= 0)
            return new StringBuilder(BaseCapacity);

        var rowCapacity = rowCount > (int.MaxValue - BaseCapacity) / estimatedCharsPerRow
            ? int.MaxValue - BaseCapacity
            : rowCount * estimatedCharsPerRow;
        return new StringBuilder(BaseCapacity + rowCapacity);
    }

    private static int GetRowsPerInsertStatement(int columnCount)
    {
        if (columnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));

        return Math.Max(1, Math.Min(BatchSize, SqliteDynamicSql.MaxSqlVariables / columnCount));
    }

    private void SetReadyBit(int flag)
    {
        var gateLease = EnterTransactionGate();
        try
        {
            // The ready bits share a single PRAGMA user_version word, so two parallel
            // cdidx writers (e.g. CI + a local rebuild) can each read the same prior
            // value, OR in their own flag, and the slower writer's PRAGMA write clobbers
            // the faster writer's flag. Wrap the read-modify-write in BEGIN IMMEDIATE so
            // SQLite's reserved write lock serialises it across processes (issue #1513).
            // Use raw BEGIN/COMMIT instead of a provider-managed transaction object here:
            // PRAGMA user_version updates are connection-level metadata, and keeping this
            // path to plain SQL avoids provider transaction state leaking across pooled
            // connections under highly parallel release tests.
            bool ownTransaction = !IsInTransaction();
            bool beganTransaction = false;
            if (ownTransaction)
            {
                Execute("BEGIN IMMEDIATE");
                beganTransaction = true;
            }
            var transaction = ownTransaction ? null : _activeTransaction;
            try
            {
                ApplyReadyBitToUserVersion(flag, transaction);
                if (ownTransaction)
                {
                    Execute("COMMIT");
                    beganTransaction = false;
                }
            }
            catch (Exception)
            {
                if (beganTransaction)
                {
                    try { Execute("ROLLBACK"); } catch (SqliteException) { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    private void ApplyReadyBitToUserVersion(int flag, SqliteTransaction? transaction)
    {
        int current;
        using (var read = _conn.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA user_version";
            var raw = read.ExecuteScalar();
            current = raw is long l ? (int)l : (raw is int i ? i : 0);
        }

        int next = current | flag;
        if (next != current)
            Execute($"PRAGMA user_version = {next}", transaction);
    }

    private bool IsInTransaction() => _transactionDepth > 0;

}
