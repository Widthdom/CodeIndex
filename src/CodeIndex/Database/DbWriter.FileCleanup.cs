using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal const string StaleChecksumCandidateSql =
        "SELECT id, path, size FROM files WHERE checksum = @checksum AND path <> @path";

    internal const string StaleRetainedPathAliasCandidateSql = """
        SELECT id, path, size
        FROM files
        WHERE path = @path COLLATE NOCASE
          AND path <> @path
        """;

    internal const string StaleDirectoryStemCandidateSql = """
        SELECT id, path, size
        FROM files
        WHERE path <> @path
          AND (
              path = @base_path
              OR (
                  path >= @base_dot_lower_bound
                  AND path < @base_dot_upper_bound
              )
        )
        """;

    internal const string StaleCSharpChecksumCandidateSql =
        "SELECT id, path, size FROM files INDEXED BY idx_files_checksum WHERE checksum = @checksum AND lang = 'csharp' AND path <> @path";

    internal const string StaleCSharpRetainedPathAliasCandidateSql = """
        SELECT id, path, size
        FROM files INDEXED BY idx_files_path_nocase
        WHERE path = @path COLLATE NOCASE
          AND path <> @path
          AND lang = 'csharp'
        """;

    internal const string StaleCSharpDirectoryStemCandidateSql = """
        SELECT id, path, size
        FROM files INDEXED BY sqlite_autoindex_files_1
        WHERE path <> @path
          AND lang = 'csharp'
          AND (
              path = @base_path
              OR (
                  path >= @base_dot_lower_bound
                  AND path < @base_dot_upper_bound
              )
          )
        """;

    /// <summary>
    /// Clean up existing file data (FTS, chunks, symbols) before re-indexing.
    /// 再インデックス前に既存ファイルデータ（FTS、チャンク、シンボル）を削除する。
    /// </summary>
    public void CleanExistingFileData(string relativePath)
    {
        var cmd = RentCommand(
            "SELECT id FROM files WHERE path = @path",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        object? result;
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            result = cmd.ExecuteScalar();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        if (result != null)
            DeleteFileData((long)result);
    }

    /// <summary>
    /// Purge stale DB rows for deleted/renamed files that still share the current file's checksum.
    /// 現在のファイルと同じ checksum を持つ削除/rename 済みの古いDB行を削除する。
    /// </summary>
    public int PurgeStaleFilesSharingChecksum(
        string projectRoot,
        string retainedRelativePath,
        string? checksum)
    {
        if (string.IsNullOrEmpty(checksum))
            return 0;

        return ApplyScopedFileCleanupPlan(
            PlanStaleFilesSharingCleanupKeysCore(
                projectRoot,
                retainedRelativePath,
                checksum,
                includeDirectoryAndStem: false,
                cancellationToken: CancellationToken.None,
                retainedPathComparison: StringComparison.Ordinal,
                restrictToCSharp: false,
                allowPathAlias: false,
                retainedFileIdentitiesByCaseFold: null,
                retainedLivePathsExact: new HashSet<string>(StringComparer.Ordinal)
                {
                    retainedRelativePath,
                }));
    }

    /// <summary>
    /// Purge stale DB rows that look like an extension-changing rename in the same directory.
    /// 同一ディレクトリ・同一stemの拡張子変更リネームに見える古いDB行を削除する。
    /// </summary>
    public int PurgeStaleFilesSharingDirectoryAndStem(string projectRoot, string retainedRelativePath)
        => ApplyScopedFileCleanupPlan(
            PlanStaleFilesSharingCleanupKeys(
                projectRoot,
                retainedRelativePath,
                checksum: null,
                includeDirectoryAndStem: true));

    /// <summary>
    /// Snapshot missing rows that share a retained file's checksum and, when requested,
    /// its exact same-directory stem. The returned IDs are immutable, sorted, and deduplicated.
    /// retained file と checksum、および指定時は同一 directory の正確な stem を共有する
    /// missing row を snapshot 化する。返却 ID は immutable・昇順・重複排除済み。
    /// </summary>
    internal FilePurgePlan PlanStaleFilesSharingCleanupKeys(
        string projectRoot,
        string retainedRelativePath,
        string? checksum,
        bool includeDirectoryAndStem,
        CancellationToken cancellationToken = default,
        StringComparison retainedPathComparison = StringComparison.Ordinal)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetainedPathComparison(retainedPathComparison);
        var retainedLivePathsExact = new HashSet<string>(StringComparer.Ordinal)
        {
            retainedRelativePath,
        };
        Dictionary<string, HashSet<FileIndexer.FileIdentity>>? retainedFileIdentitiesByCaseFold = null;
        if (TryGetWorkspaceFileIdentity(projectRoot, retainedRelativePath, out var retainedIdentity))
        {
            retainedFileIdentitiesByCaseFold = new Dictionary<
                string,
                HashSet<FileIndexer.FileIdentity>>(StringComparer.OrdinalIgnoreCase)
            {
                [retainedRelativePath] = [retainedIdentity],
            };
        }

        return PlanStaleFilesSharingCleanupKeysCore(
            projectRoot,
            retainedRelativePath,
            checksum,
            includeDirectoryAndStem,
            cancellationToken,
            retainedPathComparison,
            restrictToCSharp: false,
            allowPathAlias: true,
            retainedFileIdentitiesByCaseFold,
            retainedLivePathsExact);
    }

    /// <summary>
    /// Snapshot only stale C# rows for a set of pre-workspace cleanup targets. Shared
    /// checksums and same-directory stems are queried once, so a common checksum across
    /// K caller-selected targets does not revisit the same K candidates K times.
    /// C# workspace 構築前の cleanup target 群について stale C# row だけを snapshot 化する。
    /// 共有 checksum / 同一 directory stem は一度だけ問い合わせ、K target が同じ checksum
    /// を持つ場合に同じ K candidate を K 回走査しない。
    /// </summary>
    internal FilePurgePlan PlanStaleCSharpFilesSharingCleanupKeys(
        string projectRoot,
        IReadOnlyList<(
            string RetainedRelativePath,
            string? Checksum,
            bool IncludeDirectoryAndStem)> targets,
        CancellationToken cancellationToken = default,
        StringComparison retainedPathComparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(targets);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetainedPathComparison(retainedPathComparison);
        if (targets.Count == 0)
            return FilePurgePlan.Empty;

        var pathComparer = retainedPathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var plannedChecksums = new HashSet<string>(StringComparer.Ordinal);
        var plannedDirectoryStems = new HashSet<string>(pathComparer);
        var plannedPathAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retainedLivePathsExact = new HashSet<string>(StringComparer.Ordinal);
        var retainedFileIdentitiesByCaseFold = new Dictionary<
            string,
            HashSet<FileIndexer.FileIdentity>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (string.IsNullOrEmpty(target.RetainedRelativePath))
                continue;

            retainedLivePathsExact.Add(target.RetainedRelativePath);
            if (TryGetWorkspaceFileIdentity(
                    projectRoot,
                    target.RetainedRelativePath,
                    out var retainedIdentity))
            {
                if (!retainedFileIdentitiesByCaseFold.TryGetValue(
                        target.RetainedRelativePath,
                        out var retainedIdentities))
                {
                    retainedIdentities = [];
                    retainedFileIdentitiesByCaseFold.Add(
                        target.RetainedRelativePath,
                        retainedIdentities);
                }

                retainedIdentities.Add(retainedIdentity);
            }
        }

        var plans = new List<FilePurgePlan>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(target.RetainedRelativePath))
            {
                throw new ArgumentException(
                    "Cleanup target paths must not be empty.",
                    nameof(targets));
            }

            var checksum = !string.IsNullOrEmpty(target.Checksum)
                           && plannedChecksums.Add(target.Checksum)
                ? target.Checksum
                : null;
            var retainedStem = GetRelativeFileStem(target.RetainedRelativePath);
            var directoryStemKey = GetRelativeDirectory(target.RetainedRelativePath)
                                   + '\0'
                                   + retainedStem;
            var includeDirectoryAndStem = target.IncludeDirectoryAndStem
                                          && retainedStem.Length > 0
                                          && plannedDirectoryStems.Add(directoryStemKey);
            var allowPathAlias = plannedPathAliases.Add(target.RetainedRelativePath);
            if (checksum is null && !includeDirectoryAndStem && !allowPathAlias)
                continue;

            plans.Add(
                PlanStaleFilesSharingCleanupKeysCore(
                    projectRoot,
                    target.RetainedRelativePath,
                    checksum,
                    includeDirectoryAndStem,
                    cancellationToken,
                    retainedPathComparison,
                    restrictToCSharp: true,
                    allowPathAlias: allowPathAlias,
                    retainedFileIdentitiesByCaseFold,
                    retainedLivePathsExact));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return FilePurgePlan.Merge(plans);
    }

    private FilePurgePlan PlanStaleFilesSharingCleanupKeysCore(
        string projectRoot,
        string retainedRelativePath,
        string? checksum,
        bool includeDirectoryAndStem,
        CancellationToken cancellationToken,
        StringComparison retainedPathComparison,
        bool restrictToCSharp,
        bool allowPathAlias,
        IReadOnlyDictionary<string, HashSet<FileIndexer.FileIdentity>>?
            retainedFileIdentitiesByCaseFold,
        IReadOnlySet<string> retainedLivePathsExact)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRetainedPathComparison(retainedPathComparison);

        var includeChecksum = !string.IsNullOrEmpty(checksum);
        var includePathAlias = allowPathAlias;
        var retainedDirectory = GetRelativeDirectory(retainedRelativePath);
        var retainedStem = GetRelativeFileStem(retainedRelativePath);
        includeDirectoryAndStem &= retainedStem.Length > 0;
        if (!includeChecksum && !includeDirectoryAndStem && !includePathAlias)
            return FilePurgePlan.Empty;

        var staleFileSizes = new Dictionary<long, long?>();
        try
        {
            if (includePathAlias)
            {
                // SQLite NOCASE is only an indexed ASCII candidate prefilter. A live
                // candidate is an alias only when it resolves to the same filesystem file
                // identity as a retained target; managed whole-path casing is never an
                // authority because ancestor directories may have different case policies.
                // SQLite NOCASE は indexed ASCII candidate prefilter に限定する。live
                // candidate は retained target と filesystem identity が一致するときだけ
                // alias とし、case policy が祖先ごとに異なり得る whole-path 比較は使わない。
                CollectMissingFileCleanupCandidates(
                    projectRoot,
                    restrictToCSharp
                        ? StaleCSharpRetainedPathAliasCandidateSql
                        : StaleRetainedPathAliasCandidateSql,
                    static command => command.Parameters.Add("@path", SqliteType.Text),
                    command => command.Parameters["@path"].Value = retainedRelativePath,
                    staleFileSizes,
                    cancellationToken,
                    path => !string.Equals(path, retainedRelativePath, StringComparison.Ordinal)
                            && string.Equals(path, retainedRelativePath, StringComparison.OrdinalIgnoreCase),
                    retainedFileIdentitiesByCaseFold,
                    retainedLivePathsExact);
            }

            if (includeChecksum)
            {
                CollectMissingFileCleanupCandidates(
                    projectRoot,
                    restrictToCSharp
                        ? StaleCSharpChecksumCandidateSql
                        : StaleChecksumCandidateSql,
                    static command =>
                    {
                        command.Parameters.Add("@checksum", SqliteType.Text);
                        command.Parameters.Add("@path", SqliteType.Text);
                    },
                    command =>
                    {
                        command.Parameters["@checksum"].Value = checksum!;
                        command.Parameters["@path"].Value = retainedRelativePath;
                    },
                    staleFileSizes,
                    cancellationToken,
                    retainedFileIdentitiesByCaseFold: retainedFileIdentitiesByCaseFold,
                    retainedLivePathsExact: retainedLivePathsExact);
            }

            if (includeDirectoryAndStem)
            {
                var basePath = retainedDirectory.Length == 0
                    ? retainedStem
                    : $"{retainedDirectory}/{retainedStem}";
                // '.' and '/' are adjacent ASCII values. The half-open ordinal range therefore
                // selects only extension-prefixed paths while allowing SQLite to seek the UNIQUE
                // path index. Managed validation retains exact directory/stem semantics.
                // '.' と '/' の半開 ordinal range で拡張子候補だけを path index から seek し、
                // managed validation で同一 directory/stem の正確な意味を保つ。
                var baseDotLowerBound = basePath + ".";
                var baseDotUpperBound = basePath + "/";
                CollectMissingFileCleanupCandidates(
                    projectRoot,
                    restrictToCSharp
                        ? StaleCSharpDirectoryStemCandidateSql
                        : StaleDirectoryStemCandidateSql,
                    static command =>
                    {
                        command.Parameters.Add("@path", SqliteType.Text);
                        command.Parameters.Add("@base_path", SqliteType.Text);
                        command.Parameters.Add("@base_dot_lower_bound", SqliteType.Text);
                        command.Parameters.Add("@base_dot_upper_bound", SqliteType.Text);
                    },
                    command =>
                    {
                        command.Parameters["@path"].Value = retainedRelativePath;
                        command.Parameters["@base_path"].Value = basePath;
                        command.Parameters["@base_dot_lower_bound"].Value = baseDotLowerBound;
                        command.Parameters["@base_dot_upper_bound"].Value = baseDotUpperBound;
                    },
                    staleFileSizes,
                    cancellationToken,
                    path => string.Equals(GetRelativeDirectory(path), retainedDirectory, retainedPathComparison)
                            && string.Equals(GetRelativeFileStem(path), retainedStem, retainedPathComparison),
                    retainedFileIdentitiesByCaseFold,
                    retainedLivePathsExact);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "Scoped stale-file cleanup planning was interrupted.",
                ex,
                cancellationToken);
        }

        if (staleFileSizes.Count == 0)
            return FilePurgePlan.Empty;

        var sortedFileIds = staleFileSizes.Keys.ToArray();
        Array.Sort(sortedFileIds);
        long deletedBytes = 0;
        var byteEstimateComplete = true;
        foreach (var fileId in sortedFileIds)
        {
            var size = staleFileSizes[fileId];
            if (!size.HasValue || deletedBytes > long.MaxValue - size.Value)
            {
                byteEstimateComplete = false;
                continue;
            }

            deletedBytes += size.Value;
        }

        return new FilePurgePlan(
            Array.AsReadOnly(sortedFileIds),
            deletedBytes,
            byteEstimateComplete,
            RemainingFileCount: 0);
    }

    /// <summary>
    /// Return the first cleanup-planned DB path that exists again immediately before apply.
    /// Point lookups stay bounded by SQLite's parameter budget. A currently-live planned
    /// path is allowed only when its exact spelling is not retained, it is a case-fold
    /// candidate, and it resolves to an identity retained in that same fold bucket. Those
    /// three checks prove it is the old spelling of an intended rename rather than a
    /// reappeared exact target, an unrelated hardlink, or a cross-target identity match.
    /// apply 直前に再出現した cleanup-plan 対象 DB path を返す。ID lookup は SQLite の
    /// parameter budget 内に分割し、exact retained pathではないcase-fold候補のうち、
    /// 同じfold bucketのlive caller targetとfilesystem identityが一致するold spellingだけを
    /// 意図したrename cleanupとして許可する。
    /// </summary>
    internal string? FindReappearedFileInScopedCleanupPlan(
        string projectRoot,
        IReadOnlyList<long> sortedFileIds,
        IReadOnlySet<string>? retainedPathsExact,
        IReadOnlyDictionary<string, HashSet<FileIndexer.FileIdentity>>?
            retainedFileIdentitiesByCaseFold,
        CancellationToken cancellationToken = default)
    {
        if (sortedFileIds.Count == 0)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            for (var offset = 0; offset < sortedFileIds.Count; offset += DeleteFilesBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCount = Math.Min(DeleteFilesBatchSize, sortedFileIds.Count - offset);
                SqliteDynamicSql.EnsureParameterBudget(
                    batchCount,
                    "scoped cleanup reappearance preflight batch");
                var parameterList = SqliteDynamicSql.BuildParameterList("fileId", batchCount);
                using var command = _conn.CreateCommand();
                command.Transaction = _activeTransaction;
                command.CommandText = $"SELECT path FROM files WHERE id IN ({parameterList}) ORDER BY id";
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    command.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("fileId", parameterIndex),
                        SqliteType.Integer).Value = sortedFileIds[offset + parameterIndex];
                }

                using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = reader.GetString(0);
                    var absolutePath = Path.Combine(
                        projectRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
                    if (!File.Exists(ioPath))
                        continue;

                    if (IsProvenRetainedAlias(
                            relativePath,
                            ioPath,
                            retainedPathsExact,
                            retainedFileIdentitiesByCaseFold))
                    {
                        continue;
                    }

                    return relativePath;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "Scoped cleanup reappearance preflight was interrupted.",
                ex,
                cancellationToken);
        }
    }

    private void CollectMissingFileCleanupCandidates(
        string projectRoot,
        string sql,
        Action<SqliteCommand> configureParameterSchema,
        Action<SqliteCommand> bindParameterValues,
        IDictionary<long, long?> staleFileSizes,
        CancellationToken cancellationToken,
        Func<string, bool>? acceptPath = null,
        IReadOnlyDictionary<string, HashSet<FileIndexer.FileIdentity>>?
            retainedFileIdentitiesByCaseFold = null,
        IReadOnlySet<string>? retainedLivePathsExact = null)
    {
        var command = RentCommand(sql, configureParameterSchema);
        try
        {
            bindParameterValues(command);
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = command.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = reader.GetString(1);
                if (acceptPath?.Invoke(relativePath) == false)
                    continue;

                var absolutePath = Path.Combine(
                    projectRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
                if (File.Exists(ioPath))
                {
                    // SQLite/managed case folding only selects candidates. Bypass the live-file
                    // guard solely for a non-exact case-fold candidate that resolves to the
                    // same file identity as a retained target. Distinct Foo/foo, Unicode-folding
                    // pairs, and unrelated hardlinks therefore survive.
                    // case foldingは候補抽出だけに使い、non-exact候補かつretained targetと
                    // file identityが一致するときだけlive-file guardを迂回する。
                    var isProvenRetainedAlias = IsProvenRetainedAlias(
                        relativePath,
                        ioPath,
                        retainedLivePathsExact,
                        retainedFileIdentitiesByCaseFold);
                    if (!isProvenRetainedAlias)
                        continue;
                }

                var fileId = reader.GetInt64(0);
                if (!staleFileSizes.ContainsKey(fileId))
                {
                    var rawSize = reader.GetValue(2);
                    staleFileSizes.Add(fileId, rawSize is long size && size >= 0 ? size : null);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ReleaseCommand(command);
        }
    }

    private static bool IsProvenRetainedAlias(
        string relativePath,
        string ioPath,
        IReadOnlySet<string>? retainedPathsExact,
        IReadOnlyDictionary<string, HashSet<FileIndexer.FileIdentity>>?
            retainedFileIdentitiesByCaseFold)
    {
        if (retainedPathsExact?.Contains(relativePath) == true
            || retainedFileIdentitiesByCaseFold == null
            || !retainedFileIdentitiesByCaseFold.TryGetValue(
                relativePath,
                out var retainedIdentities)
            || retainedIdentities.Count == 0)
        {
            return false;
        }

        return FileIndexer.TryGetFileIdentity(ioPath, out var candidateIdentity)
               && retainedIdentities.Contains(candidateIdentity);
    }

    private static bool TryGetWorkspaceFileIdentity(
        string projectRoot,
        string relativePath,
        out FileIndexer.FileIdentity identity)
    {
        identity = default;
        var absolutePath = Path.Combine(
            projectRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var ioPath = LongPath.EnsureWindowsPrefix(absolutePath);
        return File.Exists(ioPath)
               && FileIndexer.TryGetFileIdentity(ioPath, out identity);
    }

    internal int ApplyScopedFileCleanupPlan(
        FilePurgePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        return DeleteStaleFileIds(plan.FileIds, cancellationToken);
    }

    private int DeleteStaleFileIds(
        IReadOnlyCollection<long> staleIds,
        CancellationToken cancellationToken = default)
    {
        if (staleIds.Count == 0)
            return 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var txn = !IsInTransaction()
            ? BeginTransaction(cancellationToken, "scoped stale-file cleanup")
            : null;
        try
        {
            using (RegisterSqliteInterrupt(cancellationToken))
                DeleteFilesByIdBatched(staleIds, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            txn?.Commit();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "Scoped stale-file cleanup was interrupted.",
                ex,
                cancellationToken);
        }

        return staleIds.Count;
    }

    private void DeleteFilesByIdBatched(
        IEnumerable<long> fileIds,
        int batchSize = DeleteFilesBatchSize,
        CancellationToken cancellationToken = default)
    {
        var batch = new List<long>(batchSize);
        foreach (var id in fileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Add(id);
            if (batch.Count == batchSize)
            {
                DeleteFileIdBatch(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileIdBatch(batch);
        }
    }

    private void DeleteFileIdBatch(IReadOnlyList<long> fileIds)
    {
        // Removing either definitions or cross-file references can change candidate
        // cardinality for retained references. Demote the identity contract inside the
        // same transaction, before any direct deletion becomes visible.
        // definition / cross-file reference の削除は retained reference の candidate
        // cardinality を変え得るため、削除前に同一 transaction 内で contract を降格する。
        TrackReferenceGraphFilesBeforeMutation(fileIds);
        InvalidateReferenceIdentityContractForMutation();
        if (_deferredHotspotReferenceRefresh is { IsCompleting: false, IsCompleted: false })
        {
            TrackDeferredHotspotReferenceFiles(fileIds);
            TrackDeferredHotspotReferenceFiles(GetReferenceFilesDependingOnLinesOwnedBy(fileIds));
        }
        DeleteCrossFileReferencesToSymbolsDefinedOnlyByFiles(fileIds);
        DeleteFileRowsByIdBatch(fileIds, offset: 0, batchCount: fileIds.Count);
    }

    private void DeleteCrossFileReferencesToSymbolsDefinedOnlyByFiles(IReadOnlyList<long> fileIds)
    {
        using var deleteCmd = _conn.CreateCommand();
        deleteCmd.Transaction = _activeTransaction;
        var parameters = SqliteDynamicSql.AddParameters(deleteCmd, "id", fileIds, SqliteType.Integer, "cross-file reference delete batch");
        var idList = string.Join(", ", parameters);
        deleteCmd.CommandText = $@"
            DELETE FROM symbol_references
            WHERE file_id NOT IN ({idList})
              AND symbol_name IS NOT NULL
              AND symbol_name <> ''
              AND EXISTS (
                  SELECT 1
                  FROM symbols deleted_symbols
                  WHERE deleted_symbols.file_id IN ({idList})
                    AND deleted_symbols.name = symbol_references.symbol_name
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM symbols retained_symbols
                  WHERE retained_symbols.file_id NOT IN ({idList})
                    AND retained_symbols.name = symbol_references.symbol_name
              )
            RETURNING id,
                      file_id,
                      source_symbol_id,
                      target_symbol_id,
                      container_name_folded,
                      symbol_name_folded";
        var affectedFileIds = new HashSet<long>();
        var deletedReferences = new List<(long Id, long FileId, long? SourceId, long? TargetId, string? ContainerNameFolded, string? SymbolNameFolded)>();
        using (var reader = deleteCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var fileId = reader.GetInt64(1);
                affectedFileIds.Add(fileId);
                deletedReferences.Add((
                    reader.GetInt64(0),
                    fileId,
                    ReadNullableInt64(reader, 2),
                    ReadNullableInt64(reader, 3),
                    ReadNullableString(reader, 4),
                    ReadNullableString(reader, 5)));
            }
        }
        TrackReferenceGraphDeletedReferences(deletedReferences);
        RefreshHotspotReferenceCounts(affectedFileIds, CancellationToken.None);
    }

    private static string GetRelativeDirectory(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex < 0 ? string.Empty : normalized[..slashIndex];
    }

    private static string GetRelativeFileStem(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var slashIndex = normalized.LastIndexOf('/');
        var fileName = slashIndex < 0 ? normalized : normalized[(slashIndex + 1)..];
        var dotIndex = fileName.LastIndexOf('.');
        return dotIndex <= 0 ? fileName : fileName[..dotIndex];
    }

    private static void ValidateRetainedPathComparison(StringComparison retainedPathComparison)
    {
        if (retainedPathComparison is StringComparison.Ordinal
            or StringComparison.OrdinalIgnoreCase)
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(retainedPathComparison),
            retainedPathComparison,
            "Filesystem paths require an ordinal comparison policy.");
    }

}
