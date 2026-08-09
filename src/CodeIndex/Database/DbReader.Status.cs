using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    public const string DynamicReferenceGraphContractStaleReason =
        "dynamic_reference_graph_contract_stale";

    private static readonly string[] DynamicReferenceGraphContractLanguages =
    [
        "crystal",
        "groovy",
        "tcl",
        "prolog",
        "ambiguous_pl",
    ];

    /// <summary>
    /// Get database statistics.
    /// データベースの統計情報を取得する。
    /// </summary>
    public StatusResult GetStatus()
        => GetStatus(includeDatabaseSizeAttribution: true);

    /// <summary>
    /// Get database statistics while allowing internal non-status consumers to skip the
    /// bounded page scan.
    /// 内部の非 status 利用で件数上限付き page scan を省略可能にして database 統計を取得する。
    /// </summary>
    /// <param name="includeDatabaseSizeAttribution">
    /// Whether to run the bounded page scan; internal consumers that do not emit the
    /// attribution block can skip it.
    /// 件数上限付き page scan を実行するかどうか。attribution block を出力しない内部利用では
    /// scan を省略できる。
    /// </param>
    internal StatusResult GetStatus(bool includeDatabaseSizeAttribution)
    {
        // Issue #180: wrap the multi-statement status read in one DEFERRED transaction so
        // every COUNT(*) / freshness / readiness query resolves against the same WAL
        // snapshot. Without this, a concurrent writer that commits between the first and
        // last statement can expose wildly inconsistent counts (e.g. `refs: 0` against a
        // steady-state 44k while an incremental update is mid-flight). DEFERRED avoids
        // acquiring a write lock — the transaction grabs a SHARED lock on the first SELECT
        // and holds one consistent snapshot until Commit releases it.
        // Issue #180: 複数 SELECT を 1 つの DEFERRED transaction で囲み、全 COUNT(*) /
        // freshness / readiness クエリを同じ WAL snapshot で解決する。これが無いと、
        // 並行 writer が途中で commit した際に「refs: 0 なのに files=836」のような不整合
        // が見える。DEFERRED は最初の SELECT で SHARED lock を取るのみで write lock を
        // 握らないため、別 writer を阻害しない。
        using var txn = _conn.BeginTransaction(deferred: true);
        var files = ExecuteScalar("SELECT COUNT(*) FROM files");
        var chunks = ExecuteScalar("SELECT COUNT(*) FROM chunks");
        var symbols = ExecuteScalar("SELECT COUNT(*) FROM symbols");
        var references = _hasReferencesTable ? ExecuteScalar("SELECT COUNT(*) FROM symbol_references") : 0L;
        var freshness = GetWorkspaceFreshness();
        var hasCSharpFiles = ScopeMayIncludeCSharpFiles("csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false, since: null);
        var csharpSymbolNameReady = !hasCSharpFiles || _csharpSymbolNameContractCurrent;
        // #435 codex review iter 3: mirror `csharp_symbol_name_ready` — the readiness flag
        // only applies when the workspace actually contains C# files, and the column +
        // stamp must match the current contract for the resolver edges to be trusted.
        // This surfaces the same flag we already emit from the CLI `index` JSON so that
        // `status --json` and MCP `status` expose a consistent trust signal (README /
        // CLAUDE.md contract).
        // #435 codex review iter 3: `csharp_symbol_name_ready` と同じ条件で expose する。
        // C# ファイルが 0 なら ready=true、そうでなければ列 + stamp の一致を要求する。
        var csharpMetadataTargetReady = !hasCSharpFiles || _csharpMetadataTargetReady;
        var csharpMetadataTargetDegradedReason = csharpMetadataTargetReady
            ? null
            : _csharpMetadataTargetDegradedReason;
        var hdlGraphContractReady = !ScopeMayIncludeHdlFiles(
            lang: null,
            pathPatterns: null,
            excludePathPatterns: null,
            excludeTests: false)
            || _hdlGraphContractCurrent;
        var sqlGraphContractSignal = GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignal = GetHotspotFamilySignal(lang: null);
        var languageReadiness = GetLanguageReadiness();
        var foldReadyReason = ResolveFoldReadyReason();
        var foldReady = _foldReady && foldReadyReason == null;

        // Language breakdown / 言語別内訳
        // Scope the reader in an inner block so it releases its statement handle before
        // we Commit() the enclosing txn — `SqliteTransaction.Commit()` fails if any
        // reader on the same connection is still open.
        // reader を内側ブロックに閉じ込め、txn.Commit() の前に statement handle を
        // 解放する。SqliteTransaction.Commit() は同じ connection 上で開いている reader
        // があると失敗する。
        var langs = GetIndexedLanguageCounts();

        var symbolsByLanguage = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(f.lang, 'unknown'), s.kind, COUNT(*)
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                GROUP BY COALESCE(f.lang, 'unknown'), s.kind
                ORDER BY COALESCE(f.lang, 'unknown'), COUNT(*) DESC, s.kind";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var lang = reader.GetString(0);
                var kind = reader.GetString(1);
                if (!symbolsByLanguage.TryGetValue(lang, out var kinds))
                {
                    kinds = new Dictionary<string, long>(StringComparer.Ordinal);
                    symbolsByLanguage[lang] = kinds;
                }

                kinds[kind] = reader.GetInt64(2);
            }
        }

        // #1509: pull persisted HEAD metadata while the SHARED snapshot is open so the
        // recorded SHA / branch / timestamp can't drift relative to the counts and freshness
        // reported by the same status call.
        // #1509: 同じ snapshot 内で HEAD metadata を引き、counts / freshness と整合させる。
        var indexedHeadSha = TryGetMetaStringInternal(DbContext.IndexedHeadShaMetaKey);
        var workspaceVerifiedHeadSha = TryGetMetaStringInternal(DbContext.WorkspaceVerifiedHeadShaMetaKey);
        var indexedHeadBranch = TryGetMetaStringInternal(DbContext.IndexedHeadBranchMetaKey);
        var indexedHeadTimestamp = ParseMetaDateTimeOffset(TryGetMetaStringInternal(DbContext.IndexedHeadTimestampMetaKey));
        var unknownExtensionFileCount = ParseMetaLong(TryGetMetaStringInternal(DbContext.UnknownExtensionFileCountMetaKey));
        var unknownExtensionFiles = ParseMetaStringList(TryGetMetaStringInternal(DbContext.UnknownExtensionFilePathsMetaKey));
        var unknownExtensionFilesTruncated = ParseMetaBool(TryGetMetaStringInternal(DbContext.UnknownExtensionFilesTruncatedMetaKey));
        var unknownExtensionFilePathLimit = ParseMetaLong(TryGetMetaStringInternal(DbContext.UnknownExtensionFilePathLimitMetaKey));
        var unknownExtensionExtensionCounts = UnknownExtensionClassifier.DeserializeCounts(TryGetMetaStringInternal(DbContext.UnknownExtensionExtensionCountsMetaKey));
        var unknownExtensionCategoryCounts = UnknownExtensionClassifier.DeserializeCounts(TryGetMetaStringInternal(DbContext.UnknownExtensionCategoryCountsMetaKey));
        var unknownExtensionGroups = UnknownExtensionClassifier.DeserializeGroups(TryGetMetaStringInternal(DbContext.UnknownExtensionGroupsMetaKey));
        if (unknownExtensionFiles != null)
        {
            unknownExtensionFilesTruncated ??= unknownExtensionFileCount.HasValue
                && unknownExtensionFileCount.Value > unknownExtensionFiles.Count;
            unknownExtensionFilePathLimit ??= unknownExtensionFiles.Count;
            if (unknownExtensionExtensionCounts == null || unknownExtensionCategoryCounts == null || unknownExtensionGroups == null)
            {
                var fallbackClassification = UnknownExtensionClassifier.Classify(unknownExtensionFiles);
                unknownExtensionExtensionCounts ??= fallbackClassification.ExtensionCounts;
                unknownExtensionCategoryCounts ??= fallbackClassification.CategoryCounts;
                unknownExtensionGroups ??= fallbackClassification.Groups;
            }
        }
        // #1546: workspace case-sensitivity stamp. Read inside the SHARED snapshot for
        // consistency with the other freshness signals; missing on legacy DBs.
        // #1546: case-sensitivity stamp も同 snapshot で読む。stamp 無し旧 DB は null。
        var pathCaseSensitive = ParseMetaBool(TryGetMetaStringInternal(DbContext.WorkspacePathCaseSensitiveMetaKey));
        var indexedFollowSymlinksPolicy = TryGetMetaStringInternal(DbContext.IndexedFollowSymlinksPolicyMetaKey);
        var dbPragmaSettings = GetDbPragmaSettings();
        var preparedCommandCache = GetPreparedCommandCacheStatus();
        var dbSizeBytes = TryGetDatabaseFileSize();
        var walSizeBytes = TryGetWalFileSize();
        var ftsIncrementalWritesSinceOptimize = ParseMetaLong(
            TryGetMetaStringInternal(DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey));
        var maintenanceSnapshotCurrent = ParseMetaBool(
            TryGetMetaStringInternal(DbContext.BatchInProgressMetaKey)) != true
            && !_indexNewerThanReader
            && !WalStaleSnapshotRisk;
        var ftsOptimization = FtsOptimizationRecommendationEvaluator.Evaluate(
            new FtsOptimizationMetrics(
                ftsIncrementalWritesSinceOptimize,
                dbPragmaSettings.PageCount,
                maintenanceSnapshotCurrent));
        var databaseSizeAttribution = includeDatabaseSizeAttribution
            ? ReadDatabaseSizeAttribution(
                dbPragmaSettings,
                dbSizeBytes,
                walSizeBytes,
                TryGetShmFileSize())
            : new StatusDatabaseSizeAttribution
            {
                Available = false,
                Measurement = "unavailable",
                UnavailableReason = "not_requested",
                TopObjectLimit = DatabaseSizeAttributionTopObjectLimit,
            };
        var maintenanceGuidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            dbPragmaSettings.PageCount,
            dbPragmaSettings.FreelistCount,
            dbPragmaSettings.PageSize,
            walSizeBytes,
            dbSizeBytes,
            dbPragmaSettings.AutoVacuum),
            ftsOptimization: ftsOptimization);
        var lastIndexRun = GetLastIndexRun();
        var referenceExtractionCapHits = GetReferenceExtractionCapHits();
        var persistedReadiness = GetPersistedIndexGenerationReadiness(
            referenceExtractionCapHits,
            langs,
            hdlGraphContractReady,
            txn);
        var batchInProgress = persistedReadiness.MigrationInProgress;
        var lastFailedOrPartialIndexRun = GetLastFailedOrPartialIndexRun(batchInProgress);

        var result = new StatusResult
        {
            Files = files,
            Chunks = chunks,
            Symbols = symbols,
            References = references,
            UnknownExtensionFileCount = unknownExtensionFileCount,
            UnknownExtensionFiles = unknownExtensionFiles,
            UnknownExtensionFilesTruncated = unknownExtensionFilesTruncated,
            UnknownExtensionFilePathLimit = unknownExtensionFilePathLimit,
            UnknownExtensionExtensionCounts = unknownExtensionExtensionCounts,
            UnknownExtensionCategoryCounts = unknownExtensionCategoryCounts,
            UnknownExtensionGroups = unknownExtensionGroups,
            IndexedAt = freshness.IndexedAt,
            LastWorkspaceFreshenedAt = lastIndexRun?.StartedAt ?? indexedHeadTimestamp?.UtcDateTime,
            LatestModified = freshness.LatestModified,
            IndexedHeadSha = indexedHeadSha,
            WorkspaceVerifiedHeadSha = workspaceVerifiedHeadSha,
            IndexedHeadBranch = indexedHeadBranch,
            IndexedHeadTimestamp = indexedHeadTimestamp,
            Languages = langs,
            SymbolsByLanguage = symbolsByLanguage.Count > 0 ? symbolsByLanguage : null,
            GraphTableAvailable = persistedReadiness.GraphTableAvailable,
            GraphDataCurrent = persistedReadiness.GraphDataCurrent,
            ReferenceExtractionLimits = ReferenceExtractor.GetSafetyLimits(),
            ReferenceGraphComplete = persistedReadiness.ReferenceGraphComplete,
            ReferenceGraphIncompleteReasons = persistedReadiness.ReferenceGraphComplete
                ? null
                : persistedReadiness.ReferenceGraphIncompleteReasons.ToList(),
            ReferenceExtractionCapHits = referenceExtractionCapHits,
            IssuesTableAvailable = _hasIssuesPhysicalTable,
            FileIssuesDataCurrent = _hasIssuesTable,
            MigrationInProgress = batchInProgress,
            IndexComplete = persistedReadiness.IndexComplete,
            IndexIncompleteReasons = persistedReadiness.IndexComplete
                ? null
                : persistedReadiness.IndexIncompleteReasons.ToList(),
            HotspotFamilyReady = hotspotFamilySignal.Ready,
            HotspotFamilyDegradedReason = hotspotFamilySignal.DegradedReason,
            LanguageReadiness = languageReadiness.Count > 0 ? languageReadiness : null,
            CSharpSymbolNameReady = csharpSymbolNameReady,
            CSharpMetadataTargetReady = csharpMetadataTargetReady,
            CSharpMetadataTargetDegradedReason = csharpMetadataTargetDegradedReason,
            SqlGraphContractReady = sqlGraphContractSignal.Ready,
            SqlGraphContractDegradedReason = sqlGraphContractSignal.DegradedReason,
            FoldReady = foldReady,
            FoldReadyReason = foldReadyReason,
            IndexWriterVersion = _indexWriterVersion,
            IndexNewerThanReader = _indexNewerThanReader,
            IndexNewerThanReaderReason = _indexNewerThanReaderReason,
            PathCaseSensitive = pathCaseSensitive,
            IndexedFollowSymlinksPolicy = indexedFollowSymlinksPolicy,
            DbPragmaSettings = dbPragmaSettings,
            PreparedCommandCache = preparedCommandCache,
            MaintenanceGuidance = maintenanceGuidance,
            DbSizeBytes = dbSizeBytes,
            WalSizeBytes = walSizeBytes,
            DatabaseSizeAttribution = databaseSizeAttribution,
            Process = StatusProcessMetrics.Capture(),
            LastIndexRun = lastIndexRun,
            LastFailedOrPartialIndexRun = lastFailedOrPartialIndexRun,
            ReadOnlyFallback = _readOnlyFallback,
            WalCheckpointAttempted = _walCheckpointAttempted,
            WalCheckpointSucceeded = _walCheckpointSucceeded,
            ReadOnlyImmutableFallback = _readOnlyImmutableFallback,
            WalCheckpointSkippedReason = _walCheckpointSkippedReason,
            WalCheckpointFailureReason = _walCheckpointFailureReason,
            WalCheckpointBusy = _walCheckpointBusy,
            WalCheckpointLogPageCount = _walCheckpointLogPageCount,
            WalCheckpointCheckpointedPageCount = _walCheckpointCheckpointedPageCount,
            WalCheckpointRemainingPageCount = _walCheckpointRemainingPageCount,
            WalStaleSnapshotRisk = WalStaleSnapshotRisk,
            WalStaleSnapshotReason = WalStaleSnapshotReason,
            DatabasePermissionPolicy = _databasePermissionPolicy,
            DatabasePermissionDiagnostics = _databasePermissionDiagnostics.Count > 0
                ? _databasePermissionDiagnostics.ToList()
                : null,
            SqliteConnectionPolicy = SqliteConnectionPolicy.BuildStatus(
                _isReadOnly,
                _readOnlyFallback,
                _walCheckpointAttempted,
                _walCheckpointSucceeded,
                _readOnlyImmutableFallback,
                _immutableReadOnly,
                _connectionPooling,
                _walCheckpointSkippedReason,
                _walCheckpointFailureReason,
                WalStaleSnapshotRisk,
                WalStaleSnapshotReason,
                _walCheckpointBusy,
                _walCheckpointLogPageCount,
                _walCheckpointCheckpointedPageCount,
                _walCheckpointRemainingPageCount),
        };
        // Commit the read-only snapshot explicitly so the SHARED lock is released promptly.
        // read-only なので rollback でも同じだが、明示 commit して SHARED lock を早期解放する。
        txn.Commit();
        return result;
    }

    private bool AreDynamicReferenceGraphContractsCurrent(
        IReadOnlyDictionary<string, long> indexedLanguages)
    {
        foreach (var language in DynamicReferenceGraphContractLanguages)
        {
            if (!indexedLanguages.ContainsKey(language))
                continue;

            var storedVersion = TryGetMetaStringInternal(
                DbContext.GetDynamicReferenceGraphContractVersionMetaKey(language));
            var currentVersion = SymbolExtractor.GetReferenceGraphContractVersion(language).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(storedVersion, currentVersion, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    internal bool IsReferenceGraphComplete(ReferenceExtractionCapHitSummary capHits) =>
        GetPersistedIndexGenerationReadiness(capHits).ReferenceGraphComplete;

    internal IReadOnlyList<string> GetReferenceGraphIncompleteReasons(
        ReferenceExtractionCapHitSummary capHits)
    {
        return GetPersistedIndexGenerationReadiness(capHits)
            .ReferenceGraphIncompleteReasons;
    }

    internal Dictionary<string, long> GetIndexedLanguageCounts()
    {
        var languages = new Dictionary<string, long>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT lang, COUNT(*) FROM files WHERE lang IS NOT NULL GROUP BY lang ORDER BY COUNT(*) DESC";
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            languages[reader.GetString(0)] = reader.GetInt64(1);
        return languages;
    }

    private StatusPreparedCommandCache? GetPreparedCommandCacheStatus()
    {
        if (_commandCache == null)
            return null;

        var diagnostics = _commandCache.GetDiagnostics();
        return new StatusPreparedCommandCache
        {
            Count = diagnostics.Count,
            Capacity = diagnostics.Capacity,
            HitCount = diagnostics.HitCount,
            MissCount = diagnostics.MissCount,
            EvictionCount = diagnostics.EvictionCount,
        };
    }
}
