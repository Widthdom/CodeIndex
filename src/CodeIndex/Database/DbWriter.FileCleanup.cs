using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal const string StaleDirectoryStemCandidateSql = """
        SELECT id, path
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
    public int PurgeStaleFilesSharingChecksum(string projectRoot, string retainedRelativePath, string? checksum)
    {
        if (string.IsNullOrEmpty(checksum))
            return 0;

        var staleIds = new List<long>();
        var cmd = RentCommand(
            "SELECT id, path FROM files WHERE checksum = @checksum AND path <> @path",
            static c =>
            {
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@path", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@checksum"].Value = checksum;
            cmd.Parameters["@path"].Value = retainedRelativePath;
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var id = reader.GetInt64(0);
                var relativePath = reader.GetString(1);
                var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                    staleIds.Add(id);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return DeleteStaleFileIds(staleIds);
    }

    /// <summary>
    /// Purge stale DB rows that look like an extension-changing rename in the same directory.
    /// 同一ディレクトリ・同一stemの拡張子変更リネームに見える古いDB行を削除する。
    /// </summary>
    public int PurgeStaleFilesSharingDirectoryAndStem(string projectRoot, string retainedRelativePath)
    {
        var retainedDirectory = GetRelativeDirectory(retainedRelativePath);
        var retainedStem = GetRelativeFileStem(retainedRelativePath);
        if (retainedStem.Length == 0)
            return 0;

        var basePath = retainedDirectory.Length == 0
            ? retainedStem
            : $"{retainedDirectory}/{retainedStem}";
        // '.' and '/' are adjacent ASCII values. The half-open ordinal range therefore selects
        // only the extension-prefixed paths while allowing SQLite to seek the UNIQUE path index.
        // '.' と '/' の半開 ordinal range で拡張子候補だけを path index から seek する。
        var baseDotLowerBound = basePath + ".";
        var baseDotUpperBound = basePath + "/";
        var staleIds = new List<long>();
        var cmd = RentCommand(
            StaleDirectoryStemCandidateSql,
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@base_path", SqliteType.Text);
                c.Parameters.Add("@base_dot_lower_bound", SqliteType.Text);
                c.Parameters.Add("@base_dot_upper_bound", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@path"].Value = retainedRelativePath;
            cmd.Parameters["@base_path"].Value = basePath;
            cmd.Parameters["@base_dot_lower_bound"].Value = baseDotLowerBound;
            cmd.Parameters["@base_dot_upper_bound"].Value = baseDotUpperBound;
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var id = reader.GetInt64(0);
                var relativePath = reader.GetString(1);
                if (!string.Equals(GetRelativeDirectory(relativePath), retainedDirectory, StringComparison.Ordinal)
                    || !string.Equals(GetRelativeFileStem(relativePath), retainedStem, StringComparison.Ordinal))
                {
                    continue;
                }

                var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(LongPath.EnsureWindowsPrefix(absolutePath)))
                    staleIds.Add(id);
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return DeleteStaleFileIds(staleIds);
    }

    private int DeleteStaleFileIds(IReadOnlyCollection<long> staleIds)
    {
        if (staleIds.Count == 0)
            return 0;

        using var txn = !IsInTransaction() ? BeginTransaction() : null;
        DeleteFilesByIdBatched(staleIds);
        txn?.Commit();

        return staleIds.Count;
    }

    private void DeleteFilesByIdBatched(IEnumerable<long> fileIds, int batchSize = DeleteFilesBatchSize)
    {
        var batch = new List<long>(batchSize);
        foreach (var id in fileIds)
        {
            batch.Add(id);
            if (batch.Count == batchSize)
            {
                DeleteFileIdBatch(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            DeleteFileIdBatch(batch);
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

}
