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
    {
        if (cleanExistingData)
        {
            // Clean up old chunks/symbols so new ones can be inserted
            // 新しいチャンク/シンボル挿入のため古いデータをクリーンアップ
            CleanExistingFileData(file.Path);
        }

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
            return reader.GetInt64(0);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
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
            return reader.GetInt64(0);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Delete old chunks and symbols for a file before re-indexing.
    /// 再インデックス前にファイルの古いチャンクとシンボルを削除する。
    /// </summary>
    public void DeleteFileData(long fileId)
    {
        // FTS cleanup is handled automatically by fts_chunks_ad trigger on chunk deletion
        // FTSクリーンアップはチャンク削除時にfts_chunks_adトリガーで自動処理される
        ExecuteFileIdDelete("DELETE FROM chunks WHERE file_id = @fid", fileId);
        ExecuteFileIdDelete("DELETE FROM symbols WHERE file_id = @fid", fileId);
        ExecuteFileIdDelete("DELETE FROM symbol_references WHERE file_id = @fid", fileId);
        ExecuteFileIdDelete("DELETE FROM reference_lines WHERE file_id = @fid", fileId);
    }

    private void ExecuteFileIdDelete(string sql, long fileId)
    {
        var cmd = RentCommand(sql, static c => c.Parameters.Add("@fid", SqliteType.Integer));
        try
        {
            cmd.Parameters["@fid"].Value = fileId;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }
}
