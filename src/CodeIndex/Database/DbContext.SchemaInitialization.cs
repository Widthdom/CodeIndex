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

        var symbolKindCheck = SymbolKindCatalog.PersistedSymbolKindSqlCheckInList;
        var referenceKindCheck = SymbolKindCatalog.PersistedReferenceKindSqlCheckInList;

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
        is_file_local_declaration INTEGER,
        declaration_semantic_score INTEGER,
        identifier_start_column INTEGER,
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
        EnsureColumn("symbols", "is_file_local_declaration", "INTEGER");
        EnsureColumn("symbols", "declaration_semantic_score", "INTEGER");
        EnsureColumn("symbols", "identifier_start_column", "INTEGER");
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
        foreach (var index in HotspotReferenceAggregateSql.Indexes)
            Execute(index.CreateSql);
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
        foreach (var definition in CoreSecondaryIndexSql.All)
            Execute(definition.CreateSql);

        foreach (var indexName in ReferenceSecondaryIndexSql.Retired)
            Execute($"DROP INDEX IF EXISTS {indexName}");
        foreach (var definition in ReferenceSecondaryIndexSql.All)
            Execute(definition.CreateSql);
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
        var symbolKindCheck = SymbolKindCatalog.PersistedSymbolKindSqlCheckInList;
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
                    is_file_local_declaration INTEGER,
                    declaration_semantic_score INTEGER,
                    identifier_start_column INTEGER,
                    is_metadata_target INTEGER,
                    metadata_target_source TEXT,
                    name_folded     TEXT,
                    display_name_folded TEXT
                )
                """,
                "id, file_id, kind, sub_kind, name, line, start_line, start_column, end_line, body_start_line, body_end_line, signature, container_kind, container_name, container_qualified_name, family_key, visibility, return_type, is_partial_declaration, is_file_local_declaration, declaration_semantic_score, identifier_start_column, is_metadata_target, metadata_target_source, name_folded, display_name_folded");
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

        var symbolKindCheck = SymbolKindCatalog.PersistedSymbolKindSqlCheckInList;
        var referenceKindCheck = SymbolKindCatalog.PersistedReferenceKindSqlCheckInList;
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

        var symbolKindCheck = SymbolKindCatalog.PersistedSymbolKindSqlCheckInList;
        var referenceKindCheck = SymbolKindCatalog.PersistedReferenceKindSqlCheckInList;
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

        var symbolKindCheck = SymbolKindCatalog.PersistedSymbolKindSqlCheckInList;
        var referenceKindCheck = SymbolKindCatalog.PersistedReferenceKindSqlCheckInList;
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
        var symbolKindCheck = SymbolKindCatalog.PersistedSymbolKindSqlCheckInList;
        var referenceKindCheck = SymbolKindCatalog.PersistedReferenceKindSqlCheckInList;
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
                is_file_local_declaration INTEGER,
                declaration_semantic_score INTEGER,
                identifier_start_column INTEGER,
                is_metadata_target INTEGER,
                metadata_target_source TEXT,
                name_folded     TEXT,
                display_name_folded TEXT
            )
            """;
        const string symbolsColumns = "id, file_id, kind, sub_kind, name, line, start_line, start_column, end_line, body_start_line, body_end_line, signature, container_kind, container_name, container_qualified_name, family_key, visibility, return_type, is_partial_declaration, is_file_local_declaration, declaration_semantic_score, identifier_start_column, is_metadata_target, metadata_target_source, name_folded, display_name_folded";
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
            if (!TableCheckContainsAll("symbols", SymbolKindCatalog.PersistedSymbolKinds))
            {
                RebuildTableWithCurrentKindChecks("symbols", "_symbols_kind_check", symbolsCreateSql, symbolsColumns);
                rebuilt = true;
            }

            if (!TableCheckContainsAll("symbol_references", SymbolKindCatalog.PersistedSymbolKinds.Concat(SymbolKindCatalog.PersistedReferenceKinds)))
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
