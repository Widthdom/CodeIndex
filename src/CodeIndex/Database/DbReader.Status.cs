namespace CodeIndex.Database;

public partial class DbReader
{
    /// <summary>
    /// Get database statistics.
    /// データベースの統計情報を取得する。
    /// </summary>
    public StatusResult GetStatus()
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
        var langs = new Dictionary<string, long>();
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT lang, COUNT(*) FROM files WHERE lang IS NOT NULL GROUP BY lang ORDER BY COUNT(*) DESC";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                langs[reader.GetString(0)] = reader.GetInt64(1);
        }

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
        var indexedHeadBranch = TryGetMetaStringInternal(DbContext.IndexedHeadBranchMetaKey);
        var indexedHeadTimestamp = ParseMetaDateTime(TryGetMetaStringInternal(DbContext.IndexedHeadTimestampMetaKey));
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
        var dbPragmaSettings = GetDbPragmaSettings();
        var preparedCommandCache = GetPreparedCommandCacheStatus();
        var dbSizeBytes = TryGetDatabaseFileSize();
        var walSizeBytes = TryGetWalFileSize();
        var maintenanceGuidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            dbPragmaSettings.PageCount,
            dbPragmaSettings.FreelistCount,
            dbPragmaSettings.PageSize,
            walSizeBytes,
            dbSizeBytes,
            dbPragmaSettings.AutoVacuum));
        var lastIndexRun = GetLastIndexRun();
        var batchInProgress = string.Equals(
            TryGetMetaStringInternal(DbContext.BatchInProgressMetaKey),
            "true",
            StringComparison.OrdinalIgnoreCase);
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
            LastWorkspaceFreshenedAt = lastIndexRun?.StartedAt ?? indexedHeadTimestamp,
            LatestModified = freshness.LatestModified,
            IndexedHeadSha = indexedHeadSha,
            IndexedHeadBranch = indexedHeadBranch,
            IndexedHeadTimestamp = indexedHeadTimestamp,
            Languages = langs,
            SymbolsByLanguage = symbolsByLanguage.Count > 0 ? symbolsByLanguage : null,
            GraphTableAvailable = _hasReferencesTable,
            IssuesTableAvailable = _hasIssuesPhysicalTable,
            FileIssuesDataCurrent = _hasIssuesTable,
            MigrationInProgress = batchInProgress,
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
            DbPragmaSettings = dbPragmaSettings,
            PreparedCommandCache = preparedCommandCache,
            MaintenanceGuidance = maintenanceGuidance,
            DbSizeBytes = dbSizeBytes,
            WalSizeBytes = walSizeBytes,
            Process = StatusProcessMetrics.Capture(),
            LastIndexRun = lastIndexRun,
            LastFailedOrPartialIndexRun = lastFailedOrPartialIndexRun,
            ReadOnlyFallback = _readOnlyFallback,
            WalCheckpointAttempted = _walCheckpointAttempted,
            WalCheckpointSucceeded = _walCheckpointSucceeded,
            ReadOnlyImmutableFallback = _readOnlyImmutableFallback,
            WalCheckpointSkippedReason = _walCheckpointSkippedReason,
            WalCheckpointFailureReason = _walCheckpointFailureReason,
            WalStaleSnapshotRisk = WalStaleSnapshotRisk,
            WalStaleSnapshotReason = WalStaleSnapshotReason,
            SqliteConnectionPolicy = SqliteConnectionPolicy.BuildStatus(
                _isReadOnly,
                _readOnlyFallback,
                _walCheckpointAttempted,
                _walCheckpointSucceeded,
                _readOnlyImmutableFallback,
                _walCheckpointSkippedReason,
                _walCheckpointFailureReason,
                WalStaleSnapshotRisk,
                WalStaleSnapshotReason),
        };
        // Commit the read-only snapshot explicitly so the SHARED lock is released promptly.
        // read-only なので rollback でも同じだが、明示 commit して SHARED lock を早期解放する。
        txn.Commit();
        return result;
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
