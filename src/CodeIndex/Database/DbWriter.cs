using Microsoft.Data.Sqlite;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Database;

/// <summary>
/// Handles INSERT/UPSERT operations to the database with batch commits.
/// バッチコミットによるINSERT/UPSERT処理を担当する。
/// </summary>
public class DbWriter
{
    private readonly SqliteConnection _conn;
    private const int BatchSize = 500;
    private int _transactionDepth;
    internal SqliteConnection Connection => _conn;

    public DbWriter(SqliteConnection connection)
    {
        _conn = connection;
    }

    /// <summary>
    /// Begin a transaction or savepoint for grouping multiple operations atomically.
    /// SQLite does not support nested BEGIN TRANSACTION, so nested calls use SAVEPOINT.
    /// 複数操作をアトミックにまとめるためのトランザクションまたはセーブポイントを開始する。
    /// SQLiteはネストされたBEGIN TRANSACTIONをサポートしないため、ネスト時はSAVEPOINTを使用する。
    /// </summary>
    public TransactionScope BeginTransaction()
    {
        if (_transactionDepth == 0)
        {
            _transactionDepth++;
            var txn = _conn.BeginTransaction();
            return new TransactionScope(txn, this);
        }
        else
        {
            // Nested: use SAVEPOINT instead of BEGIN TRANSACTION
            // ネスト: BEGIN TRANSACTIONの代わりにSAVEPOINTを使用
            var name = $"sp_{_transactionDepth}";
            _transactionDepth++;
            Execute($"SAVEPOINT {name}");
            return new TransactionScope(name, _conn, this);
        }
    }

    /// <summary>
    /// RAII wrapper for transactions and savepoints.
    /// Ensures _transactionDepth is decremented and uncommitted changes are rolled back on Dispose.
    /// トランザクションとセーブポイントのRAIIラッパー。
    /// Dispose時に_transactionDepthを確実に減算し、未コミットの変更をロールバックする。
    /// </summary>
    public sealed class TransactionScope : IDisposable
    {
        private readonly SqliteTransaction? _transaction;
        private readonly string? _savepointName;
        private readonly SqliteConnection? _conn;
        private readonly DbWriter _writer;
        private bool _committed;
        private bool _disposed;

        // Real transaction / 実トランザクション
        internal TransactionScope(SqliteTransaction transaction, DbWriter writer)
        {
            _transaction = transaction;
            _writer = writer;
        }

        // Savepoint / セーブポイント
        internal TransactionScope(string savepointName, SqliteConnection conn, DbWriter writer)
        {
            _savepointName = savepointName;
            _conn = conn;
            _writer = writer;
        }

        public void Commit()
        {
            if (_transaction != null)
                _transaction.Commit();
            else
                ExecuteSql($"RELEASE SAVEPOINT {_savepointName}");
            // Set _committed after success so Dispose() will rollback if Commit/Release throws
            // コミット/リリース成功後にフラグを立て、失敗時はDispose()でロールバックされるようにする
            _committed = true;
        }

        public void Rollback()
        {
            _committed = true;
            if (_transaction != null)
                _transaction.Rollback();
            else
                ExecuteSql($"ROLLBACK TO SAVEPOINT {_savepointName}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Rollback uncommitted changes / 未コミットの変更をロールバック
            if (!_committed)
            {
                try
                {
                    if (_transaction != null)
                        _transaction.Rollback();
                    else
                        ExecuteSql($"ROLLBACK TO SAVEPOINT {_savepointName}");
                }
                catch { /* Best effort during dispose / Dispose中はベストエフォート */ }
            }

            _transaction?.Dispose();
            if (_writer._transactionDepth > 0) _writer._transactionDepth--;
        }

        private void ExecuteSql(string sql)
        {
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Check if a file needs re-indexing by comparing modified time and checksum.
    /// 更新日時とチェックサムを比較してファイルの再インデックスが必要か判定する。
    /// Returns the existing file ID if unchanged, or null if indexing is needed.
    /// If the timestamp differs but the checksum matches, updates the timestamp
    /// in the DB and returns the ID (content unchanged, e.g. after git checkout).
    /// 変更なしなら既存ファイルIDを返し、インデックスが必要ならnullを返す。
    /// タイムスタンプが異なってもチェックサムが一致すればDB側を更新しIDを返す。
    /// </summary>
    public long? GetUnchangedFileId(string relativePath, DateTime modified, string? checksum = null, bool allowReuse = true)
    {
        if (!allowReuse)
            return null;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, modified, checksum FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", relativePath);

        using var reader = cmd.ExecuteTrackedReader();
        if (reader.TrackedRead())
        {
            var id = reader.GetInt64(0);
            var existingModified = reader.GetDateTime(1);

            // Fast path: timestamp unchanged / 高速パス: タイムスタンプ一致
            if (existingModified == modified)
                return id;

            // Slow path: timestamp changed but content may be the same (e.g. git checkout)
            // 低速パス: タイムスタンプは変わったが内容は同じ可能性（例: git checkout）
            if (checksum != null && !reader.IsDBNull(2))
            {
                var existingChecksum = reader.GetString(2);
                if (existingChecksum == checksum)
                {
                    // Update timestamp so next run takes the fast path
                    // 次回実行で高速パスを通るようタイムスタンプを更新
                    using var updateCmd = _conn.CreateCommand();
                    updateCmd.CommandText = "UPDATE files SET modified = @modified WHERE id = @id";
                    updateCmd.Parameters.AddWithValue("@modified", modified);
                    updateCmd.Parameters.AddWithValue("@id", id);
                    updateCmd.ExecuteNonQuery();
                    return id;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Check whether the DB currently contains any indexed files for the given language.
    /// 指定言語の indexed file が DB に存在するか確認する。
    /// </summary>
    public bool HasAnyFilesWithLanguage(string lang)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE lang = @lang LIMIT 1";
        cmd.Parameters.AddWithValue("@lang", lang);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Clean up existing file data (FTS, chunks, symbols) before re-indexing.
    /// 再インデックス前に既存ファイルデータ（FTS、チャンク、シンボル）を削除する。
    /// </summary>
    public void CleanExistingFileData(string relativePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", relativePath);
        var result = cmd.ExecuteScalar();
        if (result != null)
            DeleteFileData((long)result);
    }

    /// <summary>
    /// Check whether a file row already exists for the given relative path.
    /// 指定した相対パスの file 行が既に存在するか確認する。
    /// </summary>
    public bool HasFileAtPath(string relativePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE path = @path LIMIT 1";
        cmd.Parameters.AddWithValue("@path", relativePath);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Upsert a file record and return its ID.
    /// Uses ON CONFLICT DO UPDATE to preserve the existing file ID (avoids
    /// unnecessary AUTOINCREMENT growth from INSERT OR REPLACE's delete+insert).
    /// Cleans up old chunks/symbols before re-indexing.
    /// ファイルレコードをUPSERTしてIDを返す。
    /// ON CONFLICT DO UPDATEで既存IDを保持する（INSERT OR REPLACEの
    /// delete+insertによる不要なAUTOINCREMENT増加を回避）。
    /// 再インデックス前に古いチャンク/シンボルをクリーンアップする。
    /// </summary>
    public long UpsertFile(FileRecord file)
    {
        // Clean up old chunks/symbols so new ones can be inserted
        // 新しいチャンク/シンボル挿入のため古いデータをクリーンアップ
        CleanExistingFileData(file.Path);

        using var cmd = _conn.CreateCommand();
        // ON CONFLICT DO UPDATE preserves the existing row ID
        // ON CONFLICT DO UPDATEで既存の行IDを保持する
        cmd.CommandText = @"
            INSERT INTO files (path, lang, size, lines, checksum, modified, indexed_at)
            VALUES (@path, @lang, @size, @lines, @checksum, @modified, CURRENT_TIMESTAMP)
            ON CONFLICT(path) DO UPDATE SET
                lang = excluded.lang,
                size = excluded.size,
                lines = excluded.lines,
                checksum = excluded.checksum,
                modified = excluded.modified,
                indexed_at = CURRENT_TIMESTAMP
            RETURNING id";
        cmd.Parameters.AddWithValue("@path", file.Path);
        cmd.Parameters.AddWithValue("@lang", (object?)file.Lang ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", file.Size);
        cmd.Parameters.AddWithValue("@lines", file.Lines);
        cmd.Parameters.AddWithValue("@checksum", (object?)file.Checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modified", file.Modified);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Delete old chunks and symbols for a file before re-indexing.
    /// 再インデックス前にファイルの古いチャンクとシンボルを削除する。
    /// </summary>
    public void DeleteFileData(long fileId)
    {
        // FTS cleanup is handled automatically by fts_chunks_ad trigger on chunk deletion
        // FTSクリーンアップはチャンク削除時にfts_chunks_adトリガーで自動処理される
        using var cmd1 = _conn.CreateCommand();
        cmd1.CommandText = "DELETE FROM chunks WHERE file_id = @fid";
        cmd1.Parameters.AddWithValue("@fid", fileId);
        cmd1.ExecuteNonQuery();

        using var cmd2 = _conn.CreateCommand();
        cmd2.CommandText = "DELETE FROM symbols WHERE file_id = @fid";
        cmd2.Parameters.AddWithValue("@fid", fileId);
        cmd2.ExecuteNonQuery();

        using var cmd3 = _conn.CreateCommand();
        cmd3.CommandText = "DELETE FROM symbol_references WHERE file_id = @fid";
        cmd3.Parameters.AddWithValue("@fid", fileId);
        cmd3.ExecuteNonQuery();

        using var cmd4 = _conn.CreateCommand();
        cmd4.CommandText = "DELETE FROM reference_lines WHERE file_id = @fid";
        cmd4.Parameters.AddWithValue("@fid", fileId);
        cmd4.ExecuteNonQuery();
    }

    /// <summary>
    /// Insert chunks in batches (FTS index is populated automatically by triggers).
    /// Reuses a prepared statement per batch to avoid per-row command overhead.
    /// チャンクをバッチ挿入する（FTSインデックスはトリガーにより自動で反映される）。
    /// バッチごとにプリペアドステートメントを再利用し、行ごとのコマンド生成コストを回避する。
    /// </summary>
    public void InsertChunks(IReadOnlyList<ChunkRecord> chunks)
    {
        if (chunks.Count == 0) return;

        for (int i = 0; i < chunks.Count; i += BatchSize)
        {
            int end = Math.Min(i + BatchSize, chunks.Count);
            // Only create a batch transaction when not already inside an outer transaction
            // 外部トランザクション内でない場合のみバッチトランザクションを作成
            using var transaction = !IsInTransaction() ? BeginTransaction() : null;

            // Prepare after transaction starts so the command inherits the connection's transaction state
            // トランザクション開始後に準備し、接続のトランザクション状態を引き継ぐ
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content)
                VALUES (@fid, @idx, @start, @end, @content)";
            var pFid = cmd.Parameters.Add("@fid", SqliteType.Integer);
            var pIdx = cmd.Parameters.Add("@idx", SqliteType.Integer);
            var pStart = cmd.Parameters.Add("@start", SqliteType.Integer);
            var pEnd = cmd.Parameters.Add("@end", SqliteType.Integer);
            var pContent = cmd.Parameters.Add("@content", SqliteType.Text);
            cmd.Prepare();

            for (int j = i; j < end; j++)
            {
                var chunk = chunks[j];
                pFid.Value = chunk.FileId;
                pIdx.Value = chunk.ChunkIndex;
                pStart.Value = chunk.StartLine;
                pEnd.Value = chunk.EndLine;
                pContent.Value = chunk.Content;
                // FTS index is populated automatically by fts_chunks_ai trigger
                // FTSインデックスはfts_chunks_aiトリガーにより自動で反映される
                cmd.ExecuteNonQuery();
            }

            transaction?.Commit();
        }
    }

    /// <summary>
    /// Insert symbols in batches.
    /// Reuses a prepared statement per batch to avoid per-row command overhead.
    /// シンボルをバッチ挿入する。
    /// バッチごとにプリペアドステートメントを再利用し、行ごとのコマンド生成コストを回避する。
    /// </summary>
    public void InsertSymbols(IReadOnlyList<SymbolRecord> symbols)
    {
        if (symbols.Count == 0) return;

        for (int i = 0; i < symbols.Count; i += BatchSize)
        {
            int end = Math.Min(i + BatchSize, symbols.Count);
            // Only create a batch transaction when not already inside an outer transaction
            // 外部トランザクション内でない場合のみバッチトランザクションを作成
            using var transaction = !IsInTransaction() ? BeginTransaction() : null;

            // Prepare after transaction starts so the command inherits the connection's transaction state
            // トランザクション開始後に準備し、接続のトランザクション状態を引き継ぐ
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO symbols (
                    file_id, kind, name, line, start_line, end_line,
                    body_start_line, body_end_line, signature,
                    container_kind, container_name, container_qualified_name, family_key,
                    visibility, return_type,
                    is_metadata_target,
                    name_folded
                )
                VALUES (
                    @fid, @kind, @name, @line, @startLine, @endLine,
                    @bodyStartLine, @bodyEndLine, @signature,
                    @containerKind, @containerName, @containerQualifiedName, @familyKey,
                    @visibility, @returnType,
                    @isMetadataTarget,
                    @nameFolded
                )";
            var pFid = cmd.Parameters.Add("@fid", SqliteType.Integer);
            var pKind = cmd.Parameters.Add("@kind", SqliteType.Text);
            var pName = cmd.Parameters.Add("@name", SqliteType.Text);
            var pLine = cmd.Parameters.Add("@line", SqliteType.Integer);
            var pStartLine = cmd.Parameters.Add("@startLine", SqliteType.Integer);
            var pEndLine = cmd.Parameters.Add("@endLine", SqliteType.Integer);
            var pBodyStartLine = cmd.Parameters.Add("@bodyStartLine", SqliteType.Integer);
            var pBodyEndLine = cmd.Parameters.Add("@bodyEndLine", SqliteType.Integer);
            var pSignature = cmd.Parameters.Add("@signature", SqliteType.Text);
            var pContainerKind = cmd.Parameters.Add("@containerKind", SqliteType.Text);
            var pContainerName = cmd.Parameters.Add("@containerName", SqliteType.Text);
            var pContainerQualifiedName = cmd.Parameters.Add("@containerQualifiedName", SqliteType.Text);
            var pFamilyKey = cmd.Parameters.Add("@familyKey", SqliteType.Text);
            var pVisibility = cmd.Parameters.Add("@visibility", SqliteType.Text);
            var pReturnType = cmd.Parameters.Add("@returnType", SqliteType.Text);
            var pIsMetadataTarget = cmd.Parameters.Add("@isMetadataTarget", SqliteType.Integer);
            var pNameFolded = cmd.Parameters.Add("@nameFolded", SqliteType.Text);
            cmd.Prepare();

            for (int j = i; j < end; j++)
            {
                var symbol = symbols[j];
                var startLine = symbol.StartLine > 0 ? symbol.StartLine : symbol.Line;
                var endLine = symbol.EndLine > 0 ? symbol.EndLine : startLine;
                pFid.Value = symbol.FileId;
                pKind.Value = symbol.Kind;
                pName.Value = symbol.Name;
                pLine.Value = symbol.Line;
                pStartLine.Value = startLine;
                pEndLine.Value = endLine;
                pBodyStartLine.Value = (object?)symbol.BodyStartLine ?? DBNull.Value;
                pBodyEndLine.Value = (object?)symbol.BodyEndLine ?? DBNull.Value;
                pSignature.Value = (object?)symbol.Signature ?? DBNull.Value;
                pContainerKind.Value = (object?)symbol.ContainerKind ?? DBNull.Value;
                pContainerName.Value = (object?)symbol.ContainerName ?? DBNull.Value;
                pContainerQualifiedName.Value = (object?)symbol.ContainerQualifiedName ?? DBNull.Value;
                pFamilyKey.Value = (object?)symbol.FamilyKey ?? DBNull.Value;
                pVisibility.Value = (object?)symbol.Visibility ?? DBNull.Value;
                pReturnType.Value = (object?)symbol.ReturnType ?? DBNull.Value;
                pIsMetadataTarget.Value = symbol.IsMetadataTarget.HasValue
                    ? (symbol.IsMetadataTarget.Value ? 1 : 0)
                    : (object)DBNull.Value;
                pNameFolded.Value = (object?)NameFold.Fold(symbol.Name) ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            transaction?.Commit();
        }
    }

    /// <summary>
    /// Insert indexed references in batches.
    /// インデックス済み参照をバッチ挿入する。
    /// </summary>
    public void InsertReferences(IReadOnlyList<ReferenceRecord> references)
    {
        if (references.Count == 0) return;

        var referenceLineIds = new Dictionary<(long FileId, int Line), long>();
        for (int i = 0; i < references.Count; i += BatchSize)
        {
            int end = Math.Min(i + BatchSize, references.Count);
            using var transaction = !IsInTransaction() ? BeginTransaction() : null;

            using var lineCmd = _conn.CreateCommand();
            lineCmd.CommandText = @"
                INSERT INTO reference_lines (file_id, line, context)
                VALUES (@fid, @line, @context)
                ON CONFLICT(file_id, line) DO UPDATE SET
                    context = excluded.context
                RETURNING id";
            var pReferenceLineFid = lineCmd.Parameters.Add("@fid", SqliteType.Integer);
            var pReferenceLineNumber = lineCmd.Parameters.Add("@line", SqliteType.Integer);
            var pReferenceLineContext = lineCmd.Parameters.Add("@context", SqliteType.Text);
            lineCmd.Prepare();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded
                )
                VALUES (
                    @fid, @symbolName, @referenceKind, @line, @columnNumber,
                    @context, @referenceLineId, @containerKind, @containerName,
                    @symbolNameFolded, @containerNameFolded
                )";
            var pFid = cmd.Parameters.Add("@fid", SqliteType.Integer);
            var pSymbolName = cmd.Parameters.Add("@symbolName", SqliteType.Text);
            var pReferenceKind = cmd.Parameters.Add("@referenceKind", SqliteType.Text);
            var pLine = cmd.Parameters.Add("@line", SqliteType.Integer);
            var pColumnNumber = cmd.Parameters.Add("@columnNumber", SqliteType.Integer);
            var pContext = cmd.Parameters.Add("@context", SqliteType.Text);
            var pReferenceLineId = cmd.Parameters.Add("@referenceLineId", SqliteType.Integer);
            var pContainerKind = cmd.Parameters.Add("@containerKind", SqliteType.Text);
            var pContainerName = cmd.Parameters.Add("@containerName", SqliteType.Text);
            var pSymbolNameFolded = cmd.Parameters.Add("@symbolNameFolded", SqliteType.Text);
            var pContainerNameFolded = cmd.Parameters.Add("@containerNameFolded", SqliteType.Text);
            cmd.Prepare();

            for (int j = i; j < end; j++)
            {
                var reference = references[j];
                var referenceLineKey = (reference.FileId, reference.Line);
                if (!referenceLineIds.TryGetValue(referenceLineKey, out var referenceLineId))
                {
                    pReferenceLineFid.Value = reference.FileId;
                    pReferenceLineNumber.Value = reference.Line;
                    pReferenceLineContext.Value = reference.Context;
                    referenceLineId = (long)lineCmd.ExecuteScalar()!;
                    referenceLineIds[referenceLineKey] = referenceLineId;
                }

                pFid.Value = reference.FileId;
                pSymbolName.Value = reference.SymbolName;
                pReferenceKind.Value = reference.ReferenceKind;
                pLine.Value = reference.Line;
                pColumnNumber.Value = reference.Column;
                pContext.Value = DBNull.Value;
                pReferenceLineId.Value = referenceLineId;
                pContainerKind.Value = (object?)reference.ContainerKind ?? DBNull.Value;
                pContainerName.Value = (object?)reference.ContainerName ?? DBNull.Value;
                pSymbolNameFolded.Value = (object?)NameFold.Fold(reference.SymbolName) ?? DBNull.Value;
                pContainerNameFolded.Value = (object?)NameFold.Fold(reference.ContainerName) ?? DBNull.Value;
                cmd.ExecuteNonQuery();
            }

            transaction?.Commit();
        }
    }

    /// <summary>
    /// Insert file validation issues.
    /// ファイル検証問題を挿入する。
    /// </summary>
    public void InsertIssues(long fileId, IReadOnlyList<CodeIndex.Models.FileIssue> issues)
    {
        // Always delete existing issues — if the file is now clean, old issues must be removed
        // 常に既存問題を削除 — ファイルが修正済みなら古い問題を残さない
        using var delCmd = _conn.CreateCommand();
        delCmd.CommandText = "DELETE FROM file_issues WHERE file_id = @fid";
        delCmd.Parameters.AddWithValue("@fid", fileId);
        delCmd.ExecuteNonQuery();

        if (issues.Count == 0) return;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO file_issues (file_id, kind, line, message) VALUES (@fid, @kind, @line, @message)";
        var pFid = cmd.Parameters.Add("@fid", SqliteType.Integer);
        var pKind = cmd.Parameters.Add("@kind", SqliteType.Text);
        var pLine = cmd.Parameters.Add("@line", SqliteType.Integer);
        var pMessage = cmd.Parameters.Add("@message", SqliteType.Text);

        foreach (var issue in issues)
        {
            pFid.Value = fileId;
            pKind.Value = issue.Kind;
            pLine.Value = issue.Line;
            pMessage.Value = issue.Message;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Delete a file and its associated data by relative path. Returns true if found.
    /// CASCADE on chunks/symbols + FTS triggers handle all cleanup automatically.
    /// 相対パスでファイルと関連データを削除する。見つかればtrueを返す。
    /// chunks/symbolsのCASCADE + FTSトリガーが全クリーンアップを自動処理する。
    /// </summary>
    public bool DeleteFileByPath(string relativePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", relativePath);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Remove files from DB that no longer exist on disk (e.g. after branch switch).
    /// ディスク上に存在しなくなったファイルをDBから削除する（ブランチ切り替え対応）。
    /// </summary>
    /// <param name="projectRoot">Absolute path to project root / プロジェクトルートの絶対パス</param>
    /// <returns>Number of stale files removed / 削除された古いファイル数</returns>
    public int PurgeStaleFiles(string projectRoot)
    {
        // Collect all paths currently in DB / DB内の全パスを収集
        var dbPaths = new List<(long id, string path)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path FROM files";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                dbPaths.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        // Identify stale files (no longer on disk) / ディスク上に存在しないファイルを特定
        var staleIds = new List<long>();
        foreach (var (id, relativePath) in dbPaths)
        {
            var absolutePath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
                staleIds.Add(id);
        }

        if (staleIds.Count == 0)
            return 0;

        // Delete all stale files in a single transaction for atomicity and performance
        // アトミック性とパフォーマンスのため、全古いファイルを1トランザクションで削除
        // CASCADE on chunks/symbols + FTS triggers handle all cleanup automatically
        // chunks/symbolsのCASCADE + FTSトリガーが全クリーンアップを自動処理する
        using var txn = BeginTransaction();
        using var deleteCmd = _conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM files WHERE id = @id";
        var pId = deleteCmd.Parameters.Add("@id", SqliteType.Integer);
        deleteCmd.Prepare();
        foreach (var id in staleIds)
        {
            pId.Value = id;
            deleteCmd.ExecuteNonQuery();
        }
        txn.Commit();

        return staleIds.Count;
    }

    /// <summary>
    /// Remove files from DB that are not part of the current authoritative full-scan set.
    /// This covers both deleted files and files that still exist on disk but are no longer indexable.
    /// 現在の authoritative な full-scan 結果に含まれないファイルをDBから削除する。
    /// ディスク上から消えたファイルだけでなく、存在はするがインデックス対象外になったファイルも含む。
    /// </summary>
    public int PurgeFilesOutsideRetainedSet(IReadOnlySet<string> retainedRelativePaths)
    {
        var staleIds = new List<long>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path FROM files";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var id = reader.GetInt64(0);
                var path = reader.GetString(1);
                if (!retainedRelativePaths.Contains(path))
                    staleIds.Add(id);
            }
        }

        if (staleIds.Count == 0)
            return 0;

        using var txn = BeginTransaction();
        using var deleteCmd = _conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM files WHERE id = @id";
        var pId = deleteCmd.Parameters.Add("@id", SqliteType.Integer);
        deleteCmd.Prepare();
        foreach (var id in staleIds)
        {
            pId.Value = id;
            deleteCmd.ExecuteNonQuery();
        }
        txn.Commit();

        return staleIds.Count;
    }

    /// <summary>
    /// Remove files from DB that are outside the retained set, but only when their immediate
    /// parent directory completed its own file listing authoritatively OR they sit anywhere
    /// under a directory that the scanner skipped because it is a symlink / reparse point.
    /// The symlink case lets us authoritatively purge deep descendants indexed by earlier
    /// symlink-following runs, because the current scan affirmatively refused to enter that
    /// subtree. Used by partial full scans so unreadable descendants do not block stale-file
    /// cleanup for already-listed siblings, while still protecting unreadable subtrees from
    /// speculative deletes.
    /// retained set の外にある DB ファイルを削除するが、即時親ディレクトリ自身の file listing が
    /// authoritative に完了した場合、または symlink / reparse point として scanner が skip した
    /// ディレクトリ配下に入っている場合に限定する。後者は「今回のスキャンが subtree 全体への
    /// 進入を明示的に拒否した」ことを根拠に、過去の symlink 追従で作られた深い子孫も authoritative
    /// に purge できる。partial full scan では、unreadable descendant のせいで既に列挙済み sibling
    /// の stale cleanup が止まらないようにしつつ、unreadable subtree 自体は推測ベースで削除しない。
    /// </summary>
    public int PurgeFilesOutsideRetainedSetWithinListedDirectories(
        IReadOnlySet<string> retainedRelativePaths,
        IReadOnlySet<string> listedDirectories,
        IReadOnlySet<string> symlinkPrunedDirectories)
    {
        var staleIds = new List<long>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path FROM files";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var id = reader.GetInt64(0);
                var path = reader.GetString(1);
                if (retainedRelativePaths.Contains(path))
                    continue;

                if (HasListedParentDirectory(path, listedDirectories)
                    || IsUnderSymlinkPrunedDirectory(path, symlinkPrunedDirectories))
                    staleIds.Add(id);
            }
        }

        if (staleIds.Count == 0)
            return 0;

        using var txn = BeginTransaction();
        using var deleteCmd = _conn.CreateCommand();
        deleteCmd.CommandText = "DELETE FROM files WHERE id = @id";
        var pId = deleteCmd.Parameters.Add("@id", SqliteType.Integer);
        deleteCmd.Prepare();
        foreach (var id in staleIds)
        {
            pId.Value = id;
            deleteCmd.ExecuteNonQuery();
        }
        txn.Commit();

        return staleIds.Count;
    }

    private static bool HasListedParentDirectory(string path, IReadOnlySet<string> listedDirectories)
    {
        var directory = GetDirectoryPath(path);
        return listedDirectories.Contains(directory);
    }

    // True when any proper ancestor directory of `path` is in the symlink-pruned set.
    // We walk parents via LastIndexOf('/') rather than building a substring prefix test so that
    // "sub/parent_loop" only matches "sub/parent_loop/..." and never a sibling like "sub/parent_loop_x/...".
    // path のいずれかの真の祖先ディレクトリが symlink-pruned 集合に含まれるかを判定する。
    // 単純な prefix 比較だと "sub/parent_loop" が "sub/parent_loop_x/..." まで巻き込むので、
    // LastIndexOf('/') で親を辿り、ディレクトリ境界に揃った一致のみを拾う。
    private static bool IsUnderSymlinkPrunedDirectory(string path, IReadOnlySet<string> symlinkPrunedDirectories)
    {
        if (symlinkPrunedDirectories.Count == 0)
            return false;

        var directory = GetDirectoryPath(path);
        while (directory.Length > 0)
        {
            if (symlinkPrunedDirectories.Contains(directory))
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

    /// <summary>
    /// Count symbol_references for files whose language is no longer graph-supported.
    /// Used by update mode to demote readiness before the first real mutation commits.
    /// グラフ非対応になった言語の symbol_references 件数を数える。
    /// update mode が最初の実 mutation 前に readiness を落とす判断に使う。
    /// </summary>
    /// <param name="supportedLanguages">Currently supported languages / 現在対応している言語</param>
    /// <returns>Number of stale reference rows present / 存在している古い参照行数</returns>
    public int CountUnsupportedReferences(IReadOnlyCollection<string> supportedLanguages)
    {
        if (supportedLanguages.Count == 0)
            return 0;

        using var cmd = _conn.CreateCommand();
        var inParams = BuildSupportedLanguageParameters(cmd, supportedLanguages);
        cmd.CommandText = $@"
            SELECT COUNT(*)
            FROM symbol_references
            WHERE file_id IN (
                SELECT f.id FROM files f
                WHERE f.lang IS NOT NULL
                  AND f.lang NOT IN ({string.Join(", ", inParams)})
            )";
        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : (raw is int i ? i : 0);
    }

    /// <summary>
    /// Delete symbol_references for files whose language is no longer graph-supported.
    /// Prevents stale call edges from surviving after a language is removed from graph support.
    /// グラフ非対応になった言語のファイルから symbol_references を削除する。
    /// グラフ対応が外された言語の古いコールエッジが残存するのを防止する。
    /// </summary>
    /// <param name="supportedLanguages">Currently supported languages / 現在対応している言語</param>
    /// <returns>Number of stale reference rows deleted / 削除された古い参照行数</returns>
    public int PurgeUnsupportedReferences(IReadOnlyCollection<string> supportedLanguages)
    {
        if (supportedLanguages.Count == 0)
            return 0;

        using var cmd = _conn.CreateCommand();
        var inParams = BuildSupportedLanguageParameters(cmd, supportedLanguages);

        cmd.CommandText = $@"
            DELETE FROM symbol_references
            WHERE file_id IN (
                SELECT f.id FROM files f
                WHERE f.lang IS NOT NULL
                  AND f.lang NOT IN ({string.Join(", ", inParams)})
            )";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get total counts for the summary output.
    /// サマリー出力用の合計件数を取得する。
    /// </summary>
    public (long files, long chunks, long symbols, long references) GetCounts()
    {
        long files = ExecuteScalar("SELECT COUNT(*) FROM files");
        long chunks = ExecuteScalar("SELECT COUNT(*) FROM chunks");
        long symbols = ExecuteScalar("SELECT COUNT(*) FROM symbols");
        long references = ExecuteScalar("SELECT COUNT(*) FROM symbol_references");
        return (files, chunks, symbols, references);
    }

    /// <summary>
    /// Optimize FTS5 index to merge internal b-tree segments for better query performance.
    /// FTS5インデックスを最適化して内部b-treeセグメントを統合し、クエリ性能を改善する。
    /// </summary>
    public void OptimizeFts()
    {
        Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('optimize')");
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
    public void MarkGraphReady()    => SetReadyBit(DbContext.GraphReadyFlag);
    public void MarkIssuesReady()   => SetReadyBit(DbContext.IssuesReadyFlag);

    /// <summary>
    /// Stamp FoldReadyFlag AND write the current <see cref="NameFold.Version"/> plus the
    /// runtime-sensitive <see cref="NameFold.Fingerprint"/> into `codeindex_meta`.
    /// Readers require the bit, a version match, and a fingerprint match before trusting
    /// folded columns, so both intentional fold changes and runtime ICU / invariant-casing
    /// drift degrade safely to NOCASE until `--rebuild`. Issue #97.
    /// FoldReady bit + fold_key_version + fold_key_fingerprint を書く。runtime drift を含む
    /// silent mismatch を防ぎ、ズレた場合は `--rebuild` まで NOCASE fallback に降格する。
    /// </summary>
    public void MarkFoldReady()
    {
        SetReadyBit(DbContext.FoldReadyFlag);
        SetMeta("fold_key_version", NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta("fold_key_fingerprint", NameFold.Fingerprint());
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

    /// <summary>
    /// Stamp the current authoritative version for hotspot family grouping semantics.
    /// Only fully authoritative DB states should call this; mixed legacy/current DBs must
    /// stay unstamped so readers degrade to conservative same-file counting.
    /// hotspots family grouping の current authoritative version を stamp する。
    /// </summary>
    public void MarkHotspotFamilyReady(string lang, string? markerFingerprint = null)
    {
        SetMeta(
            DbContext.GetHotspotFamilyVersionMetaKey(lang),
            DbContext.HotspotFamilyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), markerFingerprint);
        // Clear the superseded global keys so mixed-version DBs don't leave confusing stale metadata behind.
        // 廃止した global key を掃除し、混在 DB に紛らわしい古い metadata を残さない。
        SetMeta(DbContext.HotspotFamilyVersionMetaKey, null);
        SetMeta(DbContext.HotspotFamilyMarkerFingerprintMetaKey, null);
    }

    /// <summary>
    /// Demote hotspot-family trust. Called at the start of any indexing run that may leave
    /// a mixed legacy/current symbol set so readers fall back conservatively unless the run
    /// completes and restamps the current version.
    /// hotspot-family trust を縮退させる。index 開始時に呼び、成功時だけ再 stamp する。
    /// </summary>
    public void ClearHotspotFamilyReady()
    {
        if (!TableExists("codeindex_meta"))
            return;

        SetMeta(DbContext.HotspotFamilyVersionMetaKey, null);
        SetMeta(DbContext.HotspotFamilyMarkerFingerprintMetaKey, null);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            SetMeta(DbContext.GetHotspotFamilyVersionMetaKey(lang), null);
            SetMeta(DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang), null);
        }
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
    /// Recompute `symbols.is_metadata_target` for every C# class-like row by parsing the
    /// signature column for inheritance clauses and running a fixed-point iteration that
    /// promotes any class transitively deriving from `System.Attribute`. Out-of-repo bases
    /// whose name ends with `Attribute` (the BCL convention) are also treated as targets so
    /// `class FooAttribute : SomeBaseAttribute` is captured even when `SomeBaseAttribute`
    /// itself is in the BCL. Non-target rows are written as 0 so reader switching does not
    /// confuse "no resolver pass yet" with "resolver decided not a target". Issue #435.
    /// C# class-like 行の `is_metadata_target` を signature の継承句から再計算する。
    /// `System.Attribute` 由来は再帰的に target、リポ外で末尾が `Attribute` の base 型も
    /// target 扱い。target でない行は明示的に 0 で書き、reader で「未解決」と区別する。
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

        var targets = new HashSet<long>();
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
        using var update = _conn.CreateCommand();
        update.CommandText = "UPDATE symbols SET is_metadata_target = @flag WHERE id = @id";
        var pFlag = update.Parameters.Add("@flag", SqliteType.Integer);
        var pId = update.Parameters.Add("@id", SqliteType.Integer);
        update.Prepare();
        foreach (var row in rows)
        {
            pFlag.Value = targets.Contains(row.Id) ? 1 : 0;
            pId.Value = row.Id;
            update.ExecuteNonQuery();
        }
        txn?.Commit();
    }

    private List<(long Id, long FileId, string Name, string? Signature, string? QualifiedName)> LoadCSharpClassRows()
    {
        var rows = new List<(long Id, long FileId, string Name, string? Signature, string? QualifiedName)>();
        if (!ColumnExists("symbols", "signature") || !ColumnExists("symbols", "is_metadata_target"))
            return rows;
        bool hasQualified = ColumnExists("symbols", "container_qualified_name");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = hasQualified
            ? @"SELECT s.id, s.file_id, s.name, s.signature, s.container_qualified_name
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind = 'class' AND s.name IS NOT NULL"
            : @"SELECT s.id, s.file_id, s.name, s.signature, NULL
                FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.lang = 'csharp' AND s.kind = 'class' AND s.name IS NOT NULL";
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return rows;
    }

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

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT s.file_id, s.name, s.signature
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp' AND s.kind = 'import' AND s.name IS NOT NULL";
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
            && System.Text.RegularExpressions.Regex.IsMatch(signature, @"^\s*extern\s+alias\b"))
        {
            return;
        }
        bool isGlobal = signature != null
            && System.Text.RegularExpressions.Regex.IsMatch(signature, @"^\s*global\s+using\b");
        bool isStatic = signature != null
            && System.Text.RegularExpressions.Regex.IsMatch(signature, @"^\s*(?:global\s+)?using\s+static\b");
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
            var m = System.Text.RegularExpressions.Regex.Match(
                signature,
                @"^\s*(?:global\s+)?using\s+(?<alias>@?\w+)\s*=\s*(?<target>[^;]+?)\s*;");
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
        cmd.CommandText = $"PRAGMA table_info({table})";
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
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO codeindex_meta (key, value) VALUES (@key, @value)
                            ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public void ClearReadyFlags()   => Execute("PRAGMA user_version = 0");

    private bool TableExists(string name)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteScalar() != null;
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
    public bool AllFoldedColumnsBackfilled()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                (SELECT COUNT(*) FROM symbols WHERE name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE container_name IS NOT NULL AND container_name_folded IS NULL)";
        var raw = cmd.ExecuteScalar();
        long missing = raw is long l ? l : (raw is int i ? i : 0);
        return missing == 0;
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
    public (int Symbols, int SymbolReferences) BackfillFoldedColumns(bool rewriteAll = false)
    {
        using var txn = !IsInTransaction() ? BeginTransaction() : null;
        var symbols = BackfillSymbolFoldedRows(rewriteAll);
        var symbolReferences = BackfillReferenceFoldedRows(rewriteAll);
        txn?.Commit();
        return (symbols, symbolReferences);
    }

    private int BackfillSymbolFoldedRows(bool rewriteAll)
    {
        var rows = new List<(long Id, string Name)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = rewriteAll
                ? "SELECT id, name FROM symbols WHERE name IS NOT NULL"
                : "SELECT id, name FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        if (rows.Count == 0)
            return 0;

        using var update = _conn.CreateCommand();
        update.CommandText = "UPDATE symbols SET name_folded = @folded WHERE id = @id";
        var pFolded = update.Parameters.Add("@folded", SqliteType.Text);
        var pId = update.Parameters.Add("@id", SqliteType.Integer);
        update.Prepare();

        foreach (var row in rows)
        {
            pFolded.Value = (object?)NameFold.Fold(row.Name) ?? DBNull.Value;
            pId.Value = row.Id;
            update.ExecuteNonQuery();
        }

        return rows.Count;
    }

    private int BackfillReferenceFoldedRows(bool rewriteAll)
    {
        var rows = new List<(long Id, string? SymbolName, string? ContainerName)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = rewriteAll
                ? "SELECT id, symbol_name, container_name FROM symbol_references WHERE symbol_name IS NOT NULL OR container_name IS NOT NULL"
                : @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                       OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        if (rows.Count == 0)
            return 0;

        using var update = _conn.CreateCommand();
        update.CommandText = @"UPDATE symbol_references
                               SET symbol_name_folded = @symbolNameFolded,
                                   container_name_folded = @containerNameFolded
                               WHERE id = @id";
        var pSymbolNameFolded = update.Parameters.Add("@symbolNameFolded", SqliteType.Text);
        var pContainerNameFolded = update.Parameters.Add("@containerNameFolded", SqliteType.Text);
        var pId = update.Parameters.Add("@id", SqliteType.Integer);
        update.Prepare();

        foreach (var row in rows)
        {
            pSymbolNameFolded.Value = (object?)NameFold.Fold(row.SymbolName) ?? DBNull.Value;
            pContainerNameFolded.Value = (object?)NameFold.Fold(row.ContainerName) ?? DBNull.Value;
            pId.Value = row.Id;
            update.ExecuteNonQuery();
        }

        return rows.Count;
    }

    private void SetReadyBit(int flag)
    {
        // The ready bits share a single PRAGMA user_version word, so two parallel
        // cdidx writers (e.g. CI + a local rebuild) can each read the same prior
        // value, OR in their own flag, and the slower writer's PRAGMA write clobbers
        // the faster writer's flag. Wrap the read-modify-write in BEGIN IMMEDIATE so
        // SQLite's reserved write lock serialises it across processes (issue #1513).
        bool ownTransaction = !IsInTransaction();
        if (ownTransaction)
            Execute("BEGIN IMMEDIATE");
        try
        {
            int current;
            using (var read = _conn.CreateCommand())
            {
                read.CommandText = "PRAGMA user_version";
                var raw = read.ExecuteScalar();
                current = raw is long l ? (int)l : (raw is int i ? i : 0);
            }
            int next = current | flag;
            if (next != current)
                Execute($"PRAGMA user_version = {next}");
            if (ownTransaction)
            {
                Execute("COMMIT");
                ownTransaction = false;
            }
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); } catch { /* best effort */ }
            }
            throw;
        }
    }

    private static List<string> BuildSupportedLanguageParameters(SqliteCommand cmd, IReadOnlyCollection<string> supportedLanguages)
    {
        var inParams = new List<string>(supportedLanguages.Count);
        for (int i = 0; i < supportedLanguages.Count; i++)
        {
            var paramName = $"@lang{i}";
            inParams.Add(paramName);
            cmd.Parameters.AddWithValue(paramName, supportedLanguages.ElementAt(i));
        }

        return inParams;
    }

    private bool IsInTransaction() => _transactionDepth > 0;

    private long ExecuteScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
