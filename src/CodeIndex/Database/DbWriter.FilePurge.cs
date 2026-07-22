using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal sealed record FilePurgePlan(
    IReadOnlyList<long> FileIds,
    long DeletedBytes,
    bool ByteEstimateComplete,
    long RemainingFileCount)
{
    internal static FilePurgePlan Empty { get; } = new(Array.Empty<long>(), 0, true, 0);
    internal int Count => FileIds.Count;

    internal static bool ContainsSortedFileId(IReadOnlyList<long>? sortedFileIds, long fileId)
    {
        if (sortedFileIds == null)
            return false;

        var lower = 0;
        var upper = sortedFileIds.Count - 1;
        while (lower <= upper)
        {
            var middle = lower + ((upper - lower) >> 1);
            var candidate = sortedFileIds[middle];
            if (candidate == fileId)
                return true;
            if (candidate < fileId)
                lower = middle + 1;
            else
                upper = middle - 1;
        }

        return false;
    }
}

public partial class DbWriter
{
    internal static Action<int>? FilePurgeBatchCompletedForTesting { get; set; }

    /// <summary>
    /// Delete a file and its associated data by relative path. Returns true if found.
    /// CASCADE on chunks/symbols + FTS triggers handle all cleanup automatically.
    /// 相対パスでファイルと関連データを削除する。見つかればtrueを返す。
    /// chunks/symbolsのCASCADE + FTSトリガーが全クリーンアップを自動処理する。
    /// </summary>
    public bool DeleteFileByPath(string relativePath)
    {
        using var transaction = !IsInTransaction() ? BeginTransaction() : null;
        var fileIdCmd = RentCommand(
            "SELECT id FROM files WHERE path = @path",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        long? fileId;
        try
        {
            fileIdCmd.Parameters["@path"].Value = relativePath;
            var value = fileIdCmd.ExecuteScalar();
            fileId = value == null || value == DBNull.Value ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            ReleaseCommand(fileIdCmd);
        }

        if (fileId is not long existingFileId)
        {
            transaction?.Commit();
            return false;
        }
        _typeScriptAugmentationDirtyNameScope?.TrackDeletedFiles([existingFileId]);
        TrackReferenceGraphFilesBeforeMutation([existingFileId]);
        if (HasReferenceIdentityRowsForFile(existingFileId))
            InvalidateReferenceIdentityContractForMutation();
        if (_deferredHotspotReferenceRefresh is { IsCompleting: false, IsCompleted: false })
        {
            TrackDeferredHotspotReferenceFiles([existingFileId]);
            TrackDeferredHotspotReferenceFiles(GetReferenceFilesDependingOnLinesOwnedBy(existingFileId));
        }

        var cmd = RentCommand(
            "DELETE FROM files WHERE path = @path",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            var deleted = cmd.ExecuteNonQuery() > 0;
            transaction?.Commit();
            return deleted;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Remove files from DB that no longer exist on disk (e.g. after branch switch).
    /// ディスク上に存在しなくなったファイルをDBから削除する（ブランチ切り替え対応）。
    /// </summary>
    /// <param name="projectRoot">Absolute path to project root / プロジェクトルートの絶対パス</param>
    /// <param name="preservedMissingPaths">
    /// Relative DB paths that are intentionally absent from disk /
    /// ディスク上に意図的に存在しないDB相対パス
    /// </param>
    /// <returns>Number of stale files removed / 削除された古いファイル数</returns>
    public int PurgeStaleFiles(
        string projectRoot,
        Action? beforeCommit = null,
        IReadOnlySet<string>? preservedMissingPaths = null)
        => ApplyFilePurgePlan(
            PlanStaleFiles(projectRoot, preservedMissingPaths),
            beforeCommit,
            cancellationToken: default);

    internal FilePurgePlan PlanStaleFiles(
        string projectRoot,
        IReadOnlySet<string>? preservedMissingPaths = null,
        CancellationToken cancellationToken = default)
    {
        // Identify stale files (no longer on disk) while streaming the current rows so
        // large indexes retain only deletion candidates rather than a second full path list.
        // 現在行を stream し、巨大 index でも全 path の複製ではなく削除候補だけを保持する。
        return CollectCurrentFilePurgePlan((_, relativePath) =>
        {
            var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            // Wrap with the Windows extended-length prefix before File.Exists so deep monorepo
            // paths (>= 248 chars) are not silently classified as stale and DELETED from the DB.
            // Without this wrap, the FileIndexer walker can index a long path successfully and
            // the next index run will purge it. See LongPath.cs and #1547.
            return !File.Exists(LongPath.EnsureWindowsPrefix(absolutePath))
                && (preservedMissingPaths == null || !preservedMissingPaths.Contains(relativePath));
        }, cancellationToken);
    }

    /// <summary>
    /// Remove files from DB that are not part of the current authoritative full-scan set.
    /// This covers both deleted files and files that still exist on disk but are no longer indexable.
    /// 現在の authoritative な full-scan 結果に含まれないファイルをDBから削除する。
    /// ディスク上から消えたファイルだけでなく、存在はするがインデックス対象外になったファイルも含む。
    /// </summary>
    public int PurgeFilesOutsideRetainedSet(IReadOnlySet<string> retainedRelativePaths)
        => ApplyFilePurgePlan(PlanFilesOutsideRetainedSet(retainedRelativePaths));

    internal FilePurgePlan PlanFilesOutsideRetainedSet(
        IReadOnlySet<string> retainedRelativePaths,
        CancellationToken cancellationToken = default)
        => CollectCurrentFilePurgePlan(
            (_, path) => !retainedRelativePaths.Contains(path),
            cancellationToken);

    /// <summary>
    /// Remove files from DB that are outside the retained set, but only when their immediate
    /// parent directory completed its own file listing authoritatively OR they sit anywhere
    /// under a directory that the scanner skipped because of file attributes such as symlink /
    /// reparse point or Windows Hidden/System. The pruned-directory case lets us authoritatively
    /// purge deep descendants indexed by earlier runs, because the current scan affirmatively
    /// refused to enter that subtree. Used by partial full scans so unreadable descendants do not block stale-file
    /// cleanup for already-listed siblings, while still protecting unreadable subtrees from
    /// speculative deletes.
    /// retained set の外にある DB ファイルを削除するが、即時親ディレクトリ自身の file listing が
    /// authoritative に完了した場合、または symlink / reparse point や Windows Hidden/System などの
    /// file attribute で scanner が skip したディレクトリ配下に入っている場合に限定する。後者は
    /// 「今回のスキャンが subtree 全体への進入を明示的に拒否した」ことを根拠に、過去の実行で
    /// 作られた深い子孫も authoritative に purge できる。partial full scan では、unreadable descendant のせいで既に列挙済み sibling
    /// の stale cleanup が止まらないようにしつつ、unreadable subtree 自体は推測ベースで削除しない。
    /// </summary>
    public int PurgeFilesOutsideRetainedSetWithinListedDirectories(
        IReadOnlySet<string> retainedRelativePaths,
        IReadOnlySet<string> listedDirectories,
        IReadOnlySet<string> attributePrunedDirectories)
        => ApplyFilePurgePlan(PlanFilesOutsideRetainedSetWithinListedDirectories(
            retainedRelativePaths,
            listedDirectories,
            attributePrunedDirectories));

    internal FilePurgePlan PlanFilesOutsideRetainedSetWithinListedDirectories(
        IReadOnlySet<string> retainedRelativePaths,
        IReadOnlySet<string> listedDirectories,
        IReadOnlySet<string> attributePrunedDirectories,
        IReadOnlySet<string>? explicitlyRemovedPaths = null,
        CancellationToken cancellationToken = default)
        => CollectCurrentFilePurgePlan((_, path) =>
            explicitlyRemovedPaths?.Contains(path) == true
            || (!retainedRelativePaths.Contains(path)
                && (HasListedParentDirectory(path, listedDirectories)
                    || IsUnderAttributePrunedDirectory(path, attributePrunedDirectories))),
            cancellationToken);

    internal int ApplyFilePurgePlan(
        FilePurgePlan plan,
        Action? beforeCommit = null,
        CancellationToken cancellationToken = default)
        => DeleteFilesById(plan.FileIds, beforeCommit, cancellationToken);

    private FilePurgePlan CollectCurrentFilePurgePlan(
        Func<long, string, bool> shouldCollect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep FileIds ordered so purge-aware preflight readers can exclude them with a
        // zero-allocation binary search instead of duplicating a large deletion set.
        // FileIds を昇順に保ち、purge-aware preflight reader が巨大な削除 set を
        // 複製せず、allocation なしの二分探索で除外できるようにする。
        var cmd = RentCommand("SELECT id, path, size FROM files ORDER BY id", static _ => { });
        try
        {
            var fileIds = new List<long>();
            long deletedBytes = 0;
            var byteEstimateComplete = true;
            long totalFileCount = 0;
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (totalFileCount < long.MaxValue)
                    totalFileCount++;
                var id = reader.GetInt64(0);
                if (!shouldCollect(id, reader.GetString(1)))
                    continue;

                fileIds.Add(id);
                var rawSize = reader.GetValue(2);
                if (rawSize is not long size
                    || size < 0
                    || deletedBytes > long.MaxValue - size)
                {
                    byteEstimateComplete = false;
                    continue;
                }

                deletedBytes += size;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FilePurgePlan(
                fileIds,
                deletedBytes,
                byteEstimateComplete,
                totalFileCount - fileIds.Count);
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("File purge planning was interrupted.", ex, cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private int DeleteFilesById(
        IReadOnlyList<long> fileIds,
        Action? beforeCommit = null,
        CancellationToken cancellationToken = default)
    {
        if (fileIds.Count == 0)
            return 0;

        cancellationToken.ThrowIfCancellationRequested();
        // Delete all stale files in a single transaction for atomicity and performance.
        // アトミック性とパフォーマンスのため、全古いファイルを1トランザクションで削除。
        // CASCADE on chunks/symbols + FTS triggers handle all cleanup automatically.
        // chunks/symbolsのCASCADE + FTSトリガーが全クリーンアップを自動処理する。
        try
        {
            using var txn = BeginTransaction();
            cancellationToken.ThrowIfCancellationRequested();
            using (RegisterSqliteInterrupt(cancellationToken))
            {
                TrackReferenceGraphFilesBeforeMutation(fileIds);
                InvalidateReferenceIdentityContractForMutation();
                if (_deferredHotspotReferenceRefresh is { IsCompleting: false, IsCompleted: false })
                {
                    TrackDeferredHotspotReferenceFiles(fileIds);
                    TrackDeferredHotspotReferenceFiles(GetReferenceFilesDependingOnLinesOwnedBy(fileIds));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            DeleteFileRowsByIdBatched(fileIds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            txn.Commit();
            return fileIds.Count;
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("File purge was interrupted.", ex, cancellationToken);
        }
    }

    private void DeleteFileRowsByIdBatched(
        IReadOnlyList<long> fileIds,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < fileIds.Count; offset += DeleteFilesBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchCount = Math.Min(DeleteFilesBatchSize, fileIds.Count - offset);
            using (RegisterSqliteInterrupt(cancellationToken))
                DeleteFileRowsByIdBatch(fileIds, offset, batchCount);
            FilePurgeBatchCompletedForTesting?.Invoke(offset + batchCount);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void DeleteFileRowsByIdBatch(
        IReadOnlyList<long> fileIds,
        int offset,
        int batchCount)
    {
        SqliteDynamicSql.EnsureParameterBudget(batchCount, "file id delete batch");
        if (_typeScriptAugmentationDirtyNameScope != null)
        {
            var trackedFileIds = new long[batchCount];
            for (var index = 0; index < batchCount; index++)
                trackedFileIds[index] = fileIds[offset + index];
            _typeScriptAugmentationDirtyNameScope.TrackDeletedFiles(trackedFileIds);
        }
        var parameterList = SqliteDynamicSql.BuildParameterList("id", batchCount);
        var deleteCmd = RentCommand(
            $"DELETE FROM files WHERE id IN ({parameterList})",
            command =>
            {
                for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                {
                    command.Parameters.Add(
                        SqliteDynamicSql.BuildParameterName("id", parameterIndex),
                        SqliteType.Integer);
                }
            });
        try
        {
            for (var parameterIndex = 0; parameterIndex < batchCount; parameterIndex++)
                deleteCmd.Parameters[parameterIndex].Value = fileIds[offset + parameterIndex];

            if (_commandCache is null)
                deleteCmd.Prepare();
            deleteCmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(deleteCmd);
        }
    }

    private static bool HasListedParentDirectory(string path, IReadOnlySet<string> listedDirectories)
    {
        var directory = GetDirectoryPath(path);
        return listedDirectories.Contains(directory);
    }

    // True when any proper ancestor directory of `path` is in the attribute-pruned set.
    // We walk parents via LastIndexOf('/') rather than building a substring prefix test so that
    // "sub/parent_loop" only matches "sub/parent_loop/..." and never a sibling like "sub/parent_loop_x/...".
    // path のいずれかの真の祖先ディレクトリが attribute-pruned 集合に含まれるかを判定する。
    // 単純な prefix 比較だと "sub/parent_loop" が "sub/parent_loop_x/..." まで巻き込むので、
    // LastIndexOf('/') で親を辿り、ディレクトリ境界に揃った一致のみを拾う。
    private static bool IsUnderAttributePrunedDirectory(string path, IReadOnlySet<string> attributePrunedDirectories)
    {
        if (attributePrunedDirectories.Count == 0)
            return false;

        var directory = GetDirectoryPath(path);
        while (directory.Length > 0)
        {
            if (attributePrunedDirectories.Contains(directory))
                return true;

            var separatorIndex = directory.LastIndexOf('/');
            directory = separatorIndex >= 0 ? directory[..separatorIndex] : string.Empty;
        }
        return false;
    }

    private static string GetDirectoryPath(string path)
    {
        var separatorIndex = path.LastIndexOf('/');
        return separatorIndex >= 0 ? path[..separatorIndex] : string.Empty;
    }
}
