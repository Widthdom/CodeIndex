using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Check whether a file row already exists for the given relative path.
    /// 指定した相対パスの file 行が既に存在するか確認する。
    /// </summary>
    public bool HasFileAtPath(string relativePath)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM files WHERE path = @path LIMIT 1",
            static c => c.Parameters.Add("@path", SqliteType.Text));
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public IReadOnlyList<string> GetIndexedJavaScriptTypeScriptConfigPaths()
    {
        var cmd = RentCommand(
            """
            SELECT path
            FROM files
            WHERE lower(path) = 'jsconfig.json'
               OR lower(path) = 'tsconfig.json'
               OR lower(path) LIKE '%/jsconfig.json'
               OR lower(path) LIKE '%/tsconfig.json'
               OR lower(path) LIKE 'jsconfig.%.json'
               OR lower(path) LIKE 'tsconfig.%.json'
               OR lower(path) LIKE '%/jsconfig.%.json'
               OR lower(path) LIKE '%/tsconfig.%.json'
            ORDER BY path
            """,
            static _ => { });
        try
        {
            var paths = new List<string>();
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                paths.Add(reader.GetString(0));
            return paths;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Upsert a file record and return its ID.
    /// Uses ON CONFLICT DO UPDATE to preserve the existing file ID (avoids
    /// unnecessary AUTOINCREMENT growth from INSERT OR REPLACE's delete+insert).
    /// Cleans up old chunks/symbols before re-indexing unless the caller knows
    /// the path cannot already exist in the current database.
    /// ファイルレコードをUPSERTしてIDを返す。
    /// ON CONFLICT DO UPDATEで既存IDを保持する（INSERT OR REPLACEの
    /// delete+insertによる不要なAUTOINCREMENT増加を回避）。
    /// 呼び出し元が現在のDBに同じ path が存在しないと保証できる場合を除き、
    /// 再インデックス前に古いチャンク/シンボルをクリーンアップする。
    /// </summary>
    public long UpsertFile(FileRecord file, bool cleanExistingData = true)
        => UpsertFile(file, out _, cleanExistingData);

    /// <summary>
    /// Upsert a file record and report whether cleanup changed reference-identity rows.
    /// ファイルレコードをUPSERTし、cleanupでreference identity行が変化したかも返す。
    /// </summary>
    public long UpsertFile(
        FileRecord file,
        out bool referenceIdentityChanged,
        bool cleanExistingData = true)
    {
        var typeScriptDirtyNameScope = _typeScriptAugmentationDirtyNameScope;
        var wasExistingTypeScript = cleanExistingData
            && typeScriptDirtyNameScope?.TrackExistingFile(file.Path) == true;
        TrackReferenceGraphFileAtPathBeforeMutation(file.Path);
        // ON CONFLICT DO UPDATE preserves the existing row ID
        // ON CONFLICT DO UPDATEで既存の行IDを保持する
        var cmd = RentCommand(
            @"
            INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
            VALUES (@path, @lang, @size, @lines, @checksum, @modified, @generated, CURRENT_TIMESTAMP)
            ON CONFLICT(path) DO UPDATE SET
                lang = excluded.lang,
                size = excluded.size,
                lines = excluded.lines,
                checksum = excluded.checksum,
                modified = excluded.modified,
                generated = excluded.generated,
                indexed_at = CURRENT_TIMESTAMP
            RETURNING id",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@lang", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                c.Parameters.Add("@lines", SqliteType.Integer);
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@generated", SqliteType.Integer);
            });
        long fileId;
        try
        {
            cmd.Parameters["@path"].Value = file.Path;
            cmd.Parameters["@lang"].Value = (object?)file.Lang ?? DBNull.Value;
            cmd.Parameters["@size"].Value = file.Size;
            cmd.Parameters["@lines"].Value = file.Lines;
            cmd.Parameters["@checksum"].Value = (object?)file.Checksum ?? DBNull.Value;
            cmd.Parameters["@modified"].Value = file.Modified;
            cmd.Parameters["@generated"].Value = file.Generated ? 1 : 0;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("SQLite RETURNING id produced no row for file upsert.");
            fileId = reader.GetInt64(0);
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        TrackReferenceGraphFileIds([fileId]);

        // Release the RETURNING reader and its prepared command before leasing the
        // cleanup command. Existing rows keep the same ID, while new rows pay only a
        // harmless no-op delete. Fresh bulk loads use InsertNewFile and skip this path.
        // RETURNING reader と prepared command を解放してから cleanup command を借りる。
        // 既存行は同じIDを保ち、新規行のDELETEはno-op。fresh bulk loadはInsertNewFileを使う。
        typeScriptDirtyNameScope?.TrackCurrentFile(fileId, file.Lang, wasExistingTypeScript);
        referenceIdentityChanged = cleanExistingData && DeleteFileDataCore(fileId, trackTypeScriptInterfaceNames: false);

        return fileId;
    }

    /// <summary>
    /// Insert a new file record and return its ID.
    /// Use only when the caller knows the path cannot already exist in the
    /// current database, such as a full scan that started from an empty index.
    /// 新規ファイルレコードをINSERTしてIDを返す。
    /// 空インデックスから開始したfull scanなど、呼び出し元が現在のDBに
    /// 同じpathが存在し得ないと保証できる場合だけ使う。
    /// </summary>
    public long InsertNewFile(FileRecord file)
    {
        var cmd = RentCommand(
            @"
            INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
            VALUES (@path, @lang, @size, @lines, @checksum, @modified, @generated, CURRENT_TIMESTAMP)
            RETURNING id",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@lang", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                c.Parameters.Add("@lines", SqliteType.Integer);
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@generated", SqliteType.Integer);
            });
        long fileId;
        try
        {
            cmd.Parameters["@path"].Value = file.Path;
            cmd.Parameters["@lang"].Value = (object?)file.Lang ?? DBNull.Value;
            cmd.Parameters["@size"].Value = file.Size;
            cmd.Parameters["@lines"].Value = file.Lines;
            cmd.Parameters["@checksum"].Value = (object?)file.Checksum ?? DBNull.Value;
            cmd.Parameters["@modified"].Value = file.Modified;
            cmd.Parameters["@generated"].Value = file.Generated ? 1 : 0;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("SQLite RETURNING id produced no row for file insert.");
            fileId = reader.GetInt64(0);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        _typeScriptAugmentationDirtyNameScope?.TrackCurrentFile(fileId, file.Lang);
        TrackReferenceGraphFileIds([fileId]);
        return fileId;
    }

    /// <summary>
    /// Delete old chunks and symbols for a file before re-indexing.
    /// 再インデックス前にファイルの古いチャンクとシンボルを削除する。
    /// </summary>
    public bool DeleteFileData(long fileId) =>
        DeleteFileDataCore(fileId, trackTypeScriptInterfaceNames: true);

    private bool DeleteFileDataCore(long fileId, bool trackTypeScriptInterfaceNames)
    {
        using var transaction = !IsInTransaction() ? BeginTransaction() : null;
        if (trackTypeScriptInterfaceNames)
            _typeScriptAugmentationDirtyNameScope?.TrackDeletedFiles([fileId]);
        TrackReferenceGraphFilesBeforeMutation([fileId]);
        var dependentReferenceFileIds = GetReferenceFilesDependingOnLinesOwnedBy(fileId);
        var hasIdentityRows = HasReferenceIdentityRowsForFile(fileId);
        var referenceIdentityChanged = hasIdentityRows || dependentReferenceFileIds.Count > 0;
        if (referenceIdentityChanged)
            InvalidateReferenceIdentityContractForMutation();

        // FTS cleanup is handled automatically by fts_chunks_ad trigger on chunk deletion
        // FTSクリーンアップはチャンク削除時にfts_chunks_adトリガーで自動処理される
        var aggregateWasReady = ClearHotspotReferenceAggregateReady();
        TrackDeferredHotspotReferenceFiles([fileId]);
        var cmd = RentCommand(
            """
            DELETE FROM chunks WHERE file_id = @fid;
            DELETE FROM symbols WHERE file_id = @fid;
            DELETE FROM hotspot_reference_counts WHERE file_id = @fid;
            DELETE FROM symbol_references WHERE file_id = @fid;
            DELETE FROM reference_lines WHERE file_id = @fid;
            """,
            static c => c.Parameters.Add("@fid", SqliteType.Integer));
        try
        {
            cmd.Parameters["@fid"].Value = fileId;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        // ON DELETE SET NULL can change COALESCE(sr.context, rl.context) for references
        // owned by another file. Refresh those source files before restoring aggregate trust.
        // 別 file 所有の reference は reference_line 削除で effective context が変わるため、
        // aggregate trust 復元前に依存元 file を再集計する。
        RefreshHotspotReferenceCounts(dependentReferenceFileIds, CancellationToken.None);
        RestoreHotspotReferenceAggregateReady(aggregateWasReady);
        transaction?.Commit();
        return referenceIdentityChanged;
    }

    private HashSet<long> GetReferenceFilesDependingOnLinesOwnedBy(long fileId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = _activeTransaction;
        cmd.CommandText = """
            SELECT DISTINCT sr.file_id
            FROM symbol_references sr
            JOIN reference_lines rl ON rl.id = sr.reference_line_id
            WHERE rl.file_id = @fid
              AND sr.file_id <> @fid
            """;
        cmd.Parameters.Add("@fid", SqliteType.Integer).Value = fileId;
        var fileIds = new HashSet<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            fileIds.Add(reader.GetInt64(0));
        return fileIds;
    }

    private HashSet<long> GetReferenceFilesDependingOnLinesOwnedBy(IReadOnlyList<long> fileIds)
    {
        var dependentFileIds = new HashSet<long>();
        for (var offset = 0; offset < fileIds.Count; offset += DeleteFilesBatchSize)
        {
            var batchCount = Math.Min(DeleteFilesBatchSize, fileIds.Count - offset);
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _activeTransaction;
            var batch = new long[batchCount];
            for (var i = 0; i < batchCount; i++)
                batch[i] = fileIds[offset + i];
            var parameters = SqliteDynamicSql.AddParameters(
                cmd,
                "line_owner_file_id",
                batch,
                SqliteType.Integer,
                "cross-file reference-line dependency batch");
            cmd.CommandText = $"""
                SELECT DISTINCT sr.file_id
                FROM symbol_references sr
                JOIN reference_lines rl ON rl.id = sr.reference_line_id
                WHERE rl.file_id IN ({string.Join(", ", parameters)})
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                dependentFileIds.Add(reader.GetInt64(0));
        }
        return dependentFileIds;
    }

    private bool HasReferenceIdentityRowsForFile(long fileId)
    {
        // This probe is conditional cleanup bookkeeping rather than a hot reusable write.
        // Keep it outside the prepared-command cache so UpsertFile's established cache
        // footprint remains stable for bulk indexing.
        // この probe は conditional cleanup 用の補助queryであり、hot writeではない。
        // prepared-command cache の外に置き、bulk index 時の UpsertFile cache footprintを保つ。
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (SELECT 1 FROM symbols WHERE file_id = @fid)
                OR EXISTS (SELECT 1 FROM symbol_references WHERE file_id = @fid)
            """;
        cmd.Parameters.Add("@fid", SqliteType.Integer).Value = fileId;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }
}
