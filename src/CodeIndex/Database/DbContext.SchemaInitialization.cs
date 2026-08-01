using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

public partial class DbContext : IDisposable
{
    public void InitializeSchema()
    {
        _rebuildTrigramFtsAfterSchemaMigration =
            !TableExists(FtsChunksTrigramTableName)
            || CountFtsChunksTrigramSyncTriggers() != 3;
        var legacyAlterTable = ExecuteScalar("PRAGMA legacy_alter_table");
        try
        {
            RunWithForeignKeysDisabledForMigration(
                "InitializeSchema",
                () => InitializeSchemaInOwnedTransaction(legacyAlterTable));
        }
        finally
        {
            _schemaCache?.Refresh();
        }
    }

    private int CountFtsChunksTrigramSyncTriggers()
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = CountFtsChunksTrigramSyncTriggersSql;
        return SqliteCommandPolicy.ReadInt32Scalar(cmd, "trigram FTS synchronization trigger count");
    }

    private void InitializeSchemaInOwnedTransaction(string legacyAlterTable)
    {
        SqliteTransaction? transaction = null;
        try
        {
            Execute("PRAGMA legacy_alter_table=ON");
            transaction = _connection.BeginTransaction(deferred: false);
            _activeMigrationTransaction = transaction;
            _migrationTransactionOwnership = MigrationTransactionOwnership.Owned;
            try
            {
                var backfillHotspotReferenceCounts = EnsureCoreSchemaTables();
                MigrateCoreTableColumns();
                InitializeReferenceGraphSchema(backfillHotspotReferenceCounts);
                CreateCoreSchemaIndexes();
                InitializeFullTextSchema();
                transaction.Commit();
            }
            finally
            {
                _activeMigrationTransaction = null;
                _migrationTransactionOwnership = MigrationTransactionOwnership.None;
            }
        }
        finally
        {
            try
            {
                transaction?.Dispose();
            }
            finally
            {
                Execute($"PRAGMA legacy_alter_table={legacyAlterTable}");
            }
        }
    }

    private bool EnsureCoreSchemaTables()
    {
        // Files table / ファイルテーブル
        Execute(@"
    CREATE TABLE IF NOT EXISTS files (
        id          INTEGER PRIMARY KEY AUTOINCREMENT,
        path        TEXT    NOT NULL UNIQUE,
        lang        TEXT,
        size        INTEGER,
        lines       INTEGER,
        checksum    TEXT,
        modified    DATETIME,
        generated   INTEGER NOT NULL DEFAULT 0,
        indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
    )");

        // Chunks table / チャンクテーブル
        Execute(@"
    CREATE TABLE IF NOT EXISTS chunks (
        id          INTEGER PRIMARY KEY AUTOINCREMENT,
        file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        chunk_index INTEGER NOT NULL,
        start_line  INTEGER,
        end_line    INTEGER,
        content     TEXT,
        UNIQUE(file_id, chunk_index)
    )");

        // Shared reference-line context table / 参照行コンテキスト共有テーブル
        Execute(@"
    CREATE TABLE IF NOT EXISTS reference_lines (
        id          INTEGER PRIMARY KEY AUTOINCREMENT,
        file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        line        INTEGER NOT NULL,
        context     TEXT NOT NULL,
        UNIQUE(file_id, line, context)
    )");

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);

        // Symbols table / シンボルテーブル
        Execute(@"
    CREATE TABLE IF NOT EXISTS symbols (
        id              INTEGER PRIMARY KEY AUTOINCREMENT,
        file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        kind            TEXT CHECK (kind IN (" + symbolKindCheck + @")),
        sub_kind        TEXT,
        name            TEXT,
        line            INTEGER,
        start_line      INTEGER,
        start_column    INTEGER,
        end_line        INTEGER,
        body_start_line INTEGER,
        body_end_line   INTEGER,
        signature       TEXT,
        container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN (" + symbolKindCheck + @")),
        container_name  TEXT,
        container_qualified_name TEXT,
        family_key      TEXT,
        visibility      TEXT,
        return_type     TEXT,
        is_partial_declaration INTEGER,
        declaration_semantic_score INTEGER,
        is_metadata_target INTEGER,
        metadata_target_source TEXT,
        name_folded     TEXT,
        display_name_folded TEXT
    )");

        // Indexed references table / 参照インデックステーブル
        Execute(@"
    CREATE TABLE IF NOT EXISTS symbol_references (
        id              INTEGER PRIMARY KEY AUTOINCREMENT,
        file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        symbol_name     TEXT,
        reference_kind  TEXT CHECK (reference_kind IN (" + referenceKindCheck + @")),
        line            INTEGER,
        column_number   INTEGER,
        span_length     INTEGER,
        context         TEXT,
        reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
        container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN (" + symbolKindCheck + @")),
        container_name  TEXT,
        source_symbol_id INTEGER,
        target_symbol_id INTEGER,
        target_symbol_key TEXT,
        target_qualifier TEXT,
        resolution_state TEXT,
        resolution_candidate_count INTEGER NOT NULL DEFAULT 0
    )");

        var backfillHotspotReferenceCounts = !TableExists(HotspotReferenceAggregateSql.TableName)
            || (GetUserVersion() & HotspotReferenceAggregateReadyFlag) == 0;
        Execute(HotspotReferenceAggregateSql.CreateTableSql);

        // File validation issues table / ファイル検証問題テーブル
        Execute(@"
    CREATE TABLE IF NOT EXISTS file_issues (
        id              INTEGER PRIMARY KEY AUTOINCREMENT,
        file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
        kind            TEXT NOT NULL,
        line            INTEGER NOT NULL DEFAULT 0,
        message         TEXT NOT NULL,
        origin          TEXT,
        severity        TEXT
    )");

        // Key-value metadata: fold algorithm version, future per-subsystem schema markers
        // that don't fit in PRAGMA user_version's readiness/storage-contract bitmap. See
        // NameFold.Version and DbReader fold-ready gate.
        // メタデータ用 key-value: fold のアルゴリズム版数など、user_version bitmap に収まらない情報。
        Execute(@"
    CREATE TABLE IF NOT EXISTS codeindex_meta (
        key    TEXT PRIMARY KEY NOT NULL,
        value  TEXT
    )");
        NormalizeCodeIndexMetaKeys();
        return backfillHotspotReferenceCounts;
    }

    private void MigrateCoreTableColumns()
    {
        // Schema migrations for existing DBs / 既存DB向けスキーマ移行
        EnsureColumn("files", "lang", "TEXT");
        EnsureColumn("files", "checksum", "TEXT");
        EnsureColumn("files", "modified", "DATETIME");
        EnsureColumn("files", "generated", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("files", "indexed_at", "DATETIME");
        EnsureColumn("symbols", "start_line", "INTEGER");
        EnsureColumn("symbols", "sub_kind", "TEXT");
        EnsureColumn("symbols", "start_column", "INTEGER");
        EnsureColumn("symbols", "end_line", "INTEGER");
        EnsureColumn("symbols", "body_start_line", "INTEGER");
        EnsureColumn("symbols", "body_end_line", "INTEGER");
        EnsureColumn("symbols", "signature", "TEXT");
        EnsureColumn("symbols", "container_kind", "TEXT");
        EnsureColumn("symbols", "container_name", "TEXT");
        EnsureColumn("symbols", "container_qualified_name", "TEXT");
        EnsureColumn("symbols", "family_key", "TEXT");
        EnsureColumn("symbols", "visibility", "TEXT");
        EnsureColumn("symbols", "return_type", "TEXT");
        EnsureColumn("symbols", "is_partial_declaration", "INTEGER");
        EnsureColumn("symbols", "declaration_semantic_score", "INTEGER");
        EnsureColumn("file_issues", "origin", "TEXT");
        EnsureColumn("file_issues", "severity", "TEXT");
        EnsureColumn("symbols", "is_metadata_target", "INTEGER");
        EnsureColumn("symbols", "metadata_target_source", "TEXT");
        var rebuildsSymbolReferences = !ColumnIsNotNull("symbol_references", "file_id");
        EnsureColumn(
            "symbol_references",
            "reference_line_id",
            rebuildsSymbolReferences ? "INTEGER" : "INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL");
        EnsureColumn("symbol_references", "span_length", "INTEGER");
        // #86: Unicode-aware folded name columns for `--exact` name matching across all
        // `--exact` command variants. Populated by the writer via NameFold.Fold; NULL on
        // legacy rows until a full reindex, in which case the reader falls back to the
        // COLLATE NOCASE path (correct for ASCII, misses non-ASCII casing — #86 fix).
        // #86: --exact 用の Unicode 折り畳み列。レガシー行は NULL のまま、再 index で埋まる。
        EnsureColumn("symbols", "name_folded", "TEXT");
        EnsureColumn("symbols", "display_name_folded", "TEXT");
        EnsureColumn("symbol_references", "symbol_name_folded", "TEXT");
        EnsureColumn("symbol_references", "container_name_folded", "TEXT");
        EnsureColumn("symbol_references", "is_self_reference", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("symbol_references", "is_mutual_recursion", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("symbol_references", "source_symbol_id", "INTEGER");
        EnsureColumn("symbol_references", "target_symbol_id", "INTEGER");
        EnsureColumn("symbol_references", "target_symbol_key", "TEXT");
        EnsureColumn("symbol_references", "target_qualifier", "TEXT");
        EnsureColumn("symbol_references", "resolution_state", "TEXT");
        EnsureColumn("symbol_references", "resolution_candidate_count", "INTEGER NOT NULL DEFAULT 0");
    }

    private void InitializeReferenceGraphSchema(bool backfillHotspotReferenceCounts)
    {
        foreach (var indexSql in HotspotReferenceAggregateSql.CreateIndexSql)
            Execute(indexSql);
        if (backfillHotspotReferenceCounts)
        {
            Execute(HotspotReferenceAggregateSql.BuildRefreshSql(singleFile: false));
            MarkHotspotReferenceAggregateReady();
        }
        EnforceRequiredFileIdConstraints();
        EnforceReferenceLineSetNullConstraint();
        EnsureReferenceLinesContextKey();
        EnsureKindCheckConstraintsCurrent();
        Execute(@"
    CREATE TABLE IF NOT EXISTS symbol_reference_candidates (
        reference_id INTEGER NOT NULL,
        symbol_id    INTEGER NOT NULL,
        scope_rank   INTEGER NOT NULL,
        PRIMARY KEY(reference_id, symbol_id)
    )");
    }

    private void CreateCoreSchemaIndexes()
    {
        // Indexes / インデックス
        Execute("CREATE INDEX IF NOT EXISTS idx_files_lang     ON files(lang)");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified)");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_generated ON files(generated)");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_checksum ON files(checksum)");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_path_nocase ON files(path COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_file_issues_file_kind ON file_issues(file_id, kind)");
        // The UNIQUE path constraint supplies the BINARY exact index. The separate
        // NOCASE index is only for bounded ASCII case-alias candidate lookups.
        // path の UNIQUE 制約が BINARY exact index を作り、別の NOCASE index は
        // bounded ASCII case-alias candidate lookup 専用に使う。
        Execute("CREATE INDEX IF NOT EXISTS idx_chunks_file    ON chunks(file_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_chunks_file_end_start_nonnull ON chunks(file_id, end_line, start_line, chunk_index) WHERE content IS NOT NULL");
        Execute("CREATE INDEX IF NOT EXISTS idx_chunks_file_start_chunk_nonnull ON chunks(file_id, start_line, chunk_index, end_line) WHERE content IS NOT NULL");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name   ON symbols(name)");
        // Case-insensitive exact-match index for `symbols --exact` (and MCP `symbols` exact=true).
        // Without this, `name = @q COLLATE NOCASE` falls back to a full symbols scan per query name,
        // which on multi-name exact lookups becomes O(names × symbols).
        // `symbols --exact` 用の大文字小文字無視 index。無いと multi-name exact でフルスキャンが N 回走る。
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_nocase ON symbols(name COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file   ON symbols(file_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_start  ON symbols(start_line)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name      ON symbol_references(symbol_name)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_file      ON symbol_references(file_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container ON symbol_references(container_name)");
        // Compound indexes for common query patterns / よくあるクエリパターン用の複合インデックス
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file_kind      ON symbols(file_id, kind)");
        Execute("CREATE INDEX IF NOT EXISTS idx_files_lang_modified     ON files(lang, modified)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)");
        // Indexes for new query patterns: --kind filter, visibility ranking, hotspot/unused analysis
        // 新しいクエリパターン用: --kind フィルタ、可視性ランキング、ホットスポット/未使用分析
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_kind            ON symbols(kind)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_visibility      ON symbols(visibility)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_kind   ON symbol_references(symbol_name, reference_kind)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_file   ON symbol_references(symbol_name, file_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_mutual_folded ON symbol_references(container_name_folded, symbol_name_folded, reference_kind, is_self_reference)");
        Execute("CREATE INDEX IF NOT EXISTS idx_reference_lines_file_line ON reference_lines(file_id, line)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_reference_line ON symbol_references(reference_line_id)");
        // Case-insensitive exact-match indexes for `references --exact` / `callers --exact` / `callees --exact` (#83).
        // Mirror idx_symbols_name_nocase so `= @q COLLATE NOCASE` stays O(log n) per name across graph commands.
        // `references / callers / callees --exact` 用の NOCASE index。idx_symbols_name_nocase と対になる。
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase      ON symbol_references(symbol_name COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase ON symbol_references(container_name COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)");
        // #86: Indexes on the Unicode-folded columns. Used when FoldReadyFlag is set on the
        // DB (= the write path filled every folded column). Legacy / partial DBs keep using
        // the NOCASE indexes above. Both sets coexist so mixed-state DBs cannot regress.
        // #86: 折り畳み列のインデックス。FoldReadyFlag が立っている DB でだけ使う。
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded                ON symbols(name_folded)");
        // Explicit-interface identities occupy name_folded, while unqualified discovery uses
        // the separately persisted display-name fold. Both predicates stay indexed.
        // 明示的 interface identity は name_folded、非修飾 discovery は別途永続化した
        // display-name fold を使い、両方の predicate を index 対応に保つ。
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_display_name_folded ON symbols(display_name_folded) WHERE display_name_folded IS NOT NULL");
        // Reference-source and ranked-candidate resolution repeatedly combines the folded
        // symbol name with file or container scope. Keep those probes bounded for every
        // indexed language, including the NOCASE fallback used by partially migrated DBs.
        // 参照元・rank 候補解決は folded 名と file/container scope を繰り返し組み合わせる。
        // 全言語と部分 migration DB の NOCASE fallback を複合 index で bounded に保つ。
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file_name_folded ON symbols(file_id, name_folded)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file_name_nocase ON symbols(file_id, name COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded_container_name_nocase ON symbols(name_folded, container_name COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded_container_qualified_name_nocase ON symbols(name_folded, container_qualified_name COLLATE NOCASE)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded     ON symbol_references(symbol_name_folded)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded  ON symbol_references(container_name_folded)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)");
        // Mutual-recursion refresh probes the reverse of every resolved edge. Restrict the
        // covering index to rows that can participate so unresolved references add no write
        // or storage cost during ordinary extraction.
        // 相互再帰 refresh は解決済み edge ごとに逆辺を探す。参加可能な行だけを covering
        // index に含め、通常抽出中の未解決参照には書き込み・容量コストを加えない。
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_resolved_source_target_kind ON symbol_references(source_symbol_id, target_symbol_id, reference_kind) WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL");
        Execute("CREATE INDEX IF NOT EXISTS idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)");
    }

    private void InitializeFullTextSchema()
    {
        // Full-text search / 全文検索
        Execute(@"
    CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
        content,
        content='chunks',
        content_rowid='id'
    )");
        Execute($@"
    CREATE VIRTUAL TABLE IF NOT EXISTS {FtsChunksTrigramTableName} USING fts5(
        content,
        content='chunks',
        content_rowid='id',
        tokenize='trigram'
    )");
        if (_rebuildFtsAfterSchemaMigration)
        {
            Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('rebuild')");
            _rebuildFtsAfterSchemaMigration = false;
        }
        if (_rebuildTrigramFtsAfterSchemaMigration)
        {
            Execute($"INSERT INTO {FtsChunksTrigramTableName}({FtsChunksTrigramTableName}) VALUES('rebuild')");
            _rebuildTrigramFtsAfterSchemaMigration = false;
        }

        // FTS5 content-synced triggers — keep both FTS indexes in sync with chunks.
        // Without these, CASCADE DELETEs on chunks leave orphan entries in fts_chunks.
        // FTS5 content-synced トリガー — 両方の FTS index を chunks と同期する。
        // これがないと chunks の CASCADE DELETE で FTS に孤立エントリが残る。
        Execute(CreateAllFtsChunksSyncTriggersSql);
        // Keep MCP resources/list cursors tied to the exact indexed-file snapshot.
        // MCP resources/list カーソルをインデックス済みファイルのスナップショットに結び付ける。
        Execute(EnsureResourceListGenerationSql);
        Execute(CreateResourceListGenerationInsertTriggerSql);
        Execute(CreateResourceListGenerationDeleteTriggerSql);
        Execute(CreateResourceListGenerationUpdateTriggerSql);
    }

    private void EnforceRequiredFileIdConstraints()
    {
        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var legacyAlterTable = ExecuteScalar("PRAGMA legacy_alter_table");
        RunWithForeignKeysDisabledForMigration(
            "EnforceRequiredFileIdConstraints",
            () => EnforceRequiredFileIdConstraintsCore(symbolKindCheck, legacyAlterTable));
    }

    private void EnforceRequiredFileIdConstraintsCore(string symbolKindCheck, string legacyAlterTable)
    {
        try
        {
            Execute("PRAGMA legacy_alter_table=ON");
            RebuildTableWithRequiredFileId(
                "chunks",
                """
                CREATE TABLE chunks (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    chunk_index INTEGER NOT NULL,
                    start_line  INTEGER,
                    end_line    INTEGER,
                    content     TEXT,
                    UNIQUE(file_id, chunk_index)
                )
                """,
                "id, file_id, chunk_index, start_line, end_line, content");
            RebuildTableWithRequiredFileId(
                "symbols",
                $"""
                CREATE TABLE symbols (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    kind            TEXT CHECK (kind IN ({symbolKindCheck})),
                    sub_kind        TEXT,
                    name            TEXT,
                    line            INTEGER,
                    start_line      INTEGER,
                    start_column    INTEGER,
                    end_line        INTEGER,
                    body_start_line INTEGER,
                    body_end_line   INTEGER,
                    signature       TEXT,
                    container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                    container_name  TEXT,
                    container_qualified_name TEXT,
                    family_key      TEXT,
                    visibility      TEXT,
                    return_type     TEXT,
                    is_partial_declaration INTEGER,
                    declaration_semantic_score INTEGER,
                    is_metadata_target INTEGER,
                    metadata_target_source TEXT,
                    name_folded     TEXT,
                    display_name_folded TEXT
                )
                """,
                "id, file_id, kind, sub_kind, name, line, start_line, start_column, end_line, body_start_line, body_end_line, signature, container_kind, container_name, container_qualified_name, family_key, visibility, return_type, is_partial_declaration, declaration_semantic_score, is_metadata_target, metadata_target_source, name_folded, display_name_folded");
            RebuildReferenceLineTablesWithRequiredFileId();
            RebuildTableWithRequiredFileId(
                "file_issues",
                """
                CREATE TABLE file_issues (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    kind            TEXT NOT NULL,
                    line            INTEGER NOT NULL DEFAULT 0,
                    message         TEXT NOT NULL,
                    origin          TEXT,
                    severity        TEXT
                )
                """,
                "id, file_id, kind, line, message, origin, severity");
        }
        finally
        {
            Execute($"PRAGMA legacy_alter_table={legacyAlterTable}");
        }
    }

    private void RebuildReferenceLineTablesWithRequiredFileId()
    {
        if (ColumnIsNotNull("reference_lines", "file_id") &&
            ColumnIsNotNull("symbol_references", "file_id"))
        {
            return;
        }

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        const string referenceLinesCreateSql =
            """
            CREATE TABLE reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )
            """;
        const string referenceLinesColumns = "id, file_id, line, context";
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                span_length     INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0,
                source_symbol_id INTEGER,
                target_symbol_id INTEGER,
                target_symbol_key TEXT,
                target_qualifier TEXT,
                resolution_state TEXT,
                resolution_candidate_count INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, span_length, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion, source_symbol_id, target_symbol_id, target_symbol_key, target_qualifier, resolution_state, resolution_candidate_count";

        const string oldReferenceLines = "_reference_lines_nullable_file_id";
        const string oldSymbolReferences = "_symbol_references_nullable_file_id";
        var quotedOldReferenceLines = SqliteIdentifier.Quote(oldReferenceLines);
        var quotedOldSymbolReferences = SqliteIdentifier.Quote(oldSymbolReferences);
        Execute($"DROP TABLE IF EXISTS {quotedOldSymbolReferences}");
        Execute($"DROP TABLE IF EXISTS {quotedOldReferenceLines}");
        Execute("DELETE FROM symbol_references WHERE file_id IS NULL");
        Execute("DELETE FROM reference_lines WHERE file_id IS NULL");
        Execute($"ALTER TABLE symbol_references RENAME TO {quotedOldSymbolReferences}");
        Execute($"ALTER TABLE reference_lines RENAME TO {quotedOldReferenceLines}");
        Execute(referenceLinesCreateSql);
        Execute($"INSERT INTO reference_lines ({referenceLinesColumns}) SELECT {referenceLinesColumns} FROM {quotedOldReferenceLines}");
        Execute(symbolReferencesCreateSql);
        Execute($"INSERT INTO symbol_references ({symbolReferencesColumns}) SELECT {symbolReferencesColumns} FROM {quotedOldSymbolReferences}");
        Execute($"DROP TABLE {quotedOldSymbolReferences}");
        Execute($"DROP TABLE {quotedOldReferenceLines}");
    }

    private void EnforceReferenceLineSetNullConstraint()
    {
        if (SymbolReferencesReferenceLineDeletesSetNull())
            return;

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                span_length     INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0,
                source_symbol_id INTEGER,
                target_symbol_id INTEGER,
                target_symbol_key TEXT,
                target_qualifier TEXT,
                resolution_state TEXT,
                resolution_candidate_count INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, span_length, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion, source_symbol_id, target_symbol_id, target_symbol_key, target_qualifier, resolution_state, resolution_candidate_count";
        const string oldSymbolReferences = "_symbol_references_reference_line_delete";
        var quotedOldSymbolReferences = SqliteIdentifier.Quote(oldSymbolReferences);

        Execute($"DROP TABLE IF EXISTS {quotedOldSymbolReferences}");
        Execute(@"
            UPDATE symbol_references
            SET reference_line_id = NULL
            WHERE reference_line_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM reference_lines
                  WHERE reference_lines.id = symbol_references.reference_line_id
              )");
        Execute($"ALTER TABLE symbol_references RENAME TO {quotedOldSymbolReferences}");
        Execute(symbolReferencesCreateSql);
        Execute($"INSERT INTO symbol_references ({symbolReferencesColumns}) SELECT {symbolReferencesColumns} FROM {quotedOldSymbolReferences}");
        Execute($"DROP TABLE {quotedOldSymbolReferences}");
    }

    private bool SymbolReferencesReferenceLineDeletesSetNull()
    {
        using var cmd = _connection.CreateCommand();
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "PRAGMA foreign_key_list('symbol_references')";

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var table = reader.GetString(2);
            var from = reader.GetString(3);
            var onDelete = reader.GetString(6);
            if (string.Equals(table, "reference_lines", StringComparison.OrdinalIgnoreCase)
                && string.Equals(from, "reference_line_id", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(onDelete, "SET NULL", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private void EnsureReferenceLinesContextKey()
    {
        if (ReferenceLinesHasContextUniqueKey())
            return;

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        const string referenceLinesCreateSql =
            """
            CREATE TABLE reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )
            """;
        const string referenceLinesColumns = "id, file_id, line, context";
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                span_length     INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0,
                source_symbol_id INTEGER,
                target_symbol_id INTEGER,
                target_symbol_key TEXT,
                target_qualifier TEXT,
                resolution_state TEXT,
                resolution_candidate_count INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, span_length, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion, source_symbol_id, target_symbol_id, target_symbol_key, target_qualifier, resolution_state, resolution_candidate_count";

        const string oldReferenceLines = "_reference_lines_file_line_key";
        const string oldSymbolReferences = "_symbol_references_file_line_key";
        var quotedOldReferenceLines = SqliteIdentifier.Quote(oldReferenceLines);
        var quotedOldSymbolReferences = SqliteIdentifier.Quote(oldSymbolReferences);
        RunWithForeignKeysDisabledForMigration("EnsureReferenceLinesContextKey", () =>
        {
            Execute($"DROP TABLE IF EXISTS {quotedOldSymbolReferences}");
            Execute($"DROP TABLE IF EXISTS {quotedOldReferenceLines}");
            Execute($"ALTER TABLE symbol_references RENAME TO {quotedOldSymbolReferences}");
            Execute($"ALTER TABLE reference_lines RENAME TO {quotedOldReferenceLines}");
            Execute(referenceLinesCreateSql);
            Execute($"INSERT INTO reference_lines ({referenceLinesColumns}) SELECT {referenceLinesColumns} FROM {quotedOldReferenceLines}");
            Execute(symbolReferencesCreateSql);
            Execute($"INSERT INTO symbol_references ({symbolReferencesColumns}) SELECT {symbolReferencesColumns} FROM {quotedOldSymbolReferences}");
            Execute($"DROP TABLE {quotedOldSymbolReferences}");
            Execute($"DROP TABLE {quotedOldReferenceLines}");
            InvokeForeignKeyValidationBeforeCheckForTesting("reference_lines_context_key");
        });

        ValidateForeignKeysAfterMigration("reference_lines_context_key");
        _schemaCache?.Refresh();
    }

    private bool ReferenceLinesHasContextUniqueKey()
    {
        using var listCmd = SqliteConnectionPolicy.CreateCommand(_connection);
        listCmd.CommandText = "PRAGMA index_list('reference_lines')";
        using var indexReader = listCmd.ExecuteReader();
        var indexNames = new List<string>();
        while (indexReader.Read())
        {
            var isUnique = indexReader.GetInt32(2) == 1;
            if (isUnique)
                indexNames.Add(indexReader.GetString(1));
        }

        foreach (var indexName in indexNames)
        {
            using var infoCmd = SqliteConnectionPolicy.CreateCommand(_connection);
            infoCmd.CommandText = $"PRAGMA index_info('{indexName.Replace("'", "''")}')";
            using var infoReader = infoCmd.ExecuteReader();
            var columns = new List<string>();
            while (infoReader.Read())
                columns.Add(infoReader.GetString(2));

            if (columns.SequenceEqual(["file_id", "line", "context"], StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    private void EnsureKindCheckConstraintsCurrent()
    {
        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        var symbolsCreateSql =
            $"""
            CREATE TABLE symbols (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT CHECK (kind IN ({symbolKindCheck})),
                sub_kind        TEXT,
                name            TEXT,
                line            INTEGER,
                start_line      INTEGER,
                start_column    INTEGER,
                end_line        INTEGER,
                body_start_line INTEGER,
                body_end_line   INTEGER,
                signature       TEXT,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                container_qualified_name TEXT,
                family_key      TEXT,
                visibility      TEXT,
                return_type     TEXT,
                is_partial_declaration INTEGER,
                declaration_semantic_score INTEGER,
                is_metadata_target INTEGER,
                metadata_target_source TEXT,
                name_folded     TEXT,
                display_name_folded TEXT
            )
            """;
        const string symbolsColumns = "id, file_id, kind, sub_kind, name, line, start_line, start_column, end_line, body_start_line, body_end_line, signature, container_kind, container_name, container_qualified_name, family_key, visibility, return_type, is_partial_declaration, declaration_semantic_score, is_metadata_target, metadata_target_source, name_folded, display_name_folded";
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                span_length     INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0,
                source_symbol_id INTEGER,
                target_symbol_id INTEGER,
                target_symbol_key TEXT,
                target_qualifier TEXT,
                resolution_state TEXT,
                resolution_candidate_count INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, span_length, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion, source_symbol_id, target_symbol_id, target_symbol_key, target_qualifier, resolution_state, resolution_candidate_count";

        var rebuilt = false;
        RunWithForeignKeysDisabledForMigration("EnsureKindCheckConstraintsCurrent", () =>
        {
            if (!TableCheckContainsAll("symbols", SymbolKindCatalog.SymbolKinds))
            {
                RebuildTableWithCurrentKindChecks("symbols", "_symbols_kind_check", symbolsCreateSql, symbolsColumns);
                rebuilt = true;
            }

            if (!TableCheckContainsAll("symbol_references", SymbolKindCatalog.SymbolKinds.Concat(SymbolKindCatalog.ReferenceKinds)))
            {
                RebuildTableWithCurrentKindChecks("symbol_references", "_symbol_references_kind_check", symbolReferencesCreateSql, symbolReferencesColumns);
                rebuilt = true;
            }

            if (rebuilt)
                InvokeForeignKeyValidationBeforeCheckForTesting("kind_check_constraints");
        });

        if (rebuilt)
            ValidateForeignKeysAfterMigration("kind_check_constraints");
    }

}
