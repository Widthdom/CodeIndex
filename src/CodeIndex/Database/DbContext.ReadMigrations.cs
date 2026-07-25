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
    public DbMigrationFailure? LastMigrationFailure { get; private set; }

    /// <summary>
    /// Attempt opportunistic schema migration for read-only query paths.
    /// Failures are captured on <see cref="LastMigrationFailure"/> and a single
    /// actionable warning is written to <see cref="Console.Error"/> so a later
    /// "no such column" error can be tied back to the failing migration step.
    /// 読み取り専用クエリパス向けの機会的スキーマ移行を試みる。
    /// 失敗時は <see cref="LastMigrationFailure"/> に記録し、stderr に 1 行の警告を出す。
    /// </summary>
    public void TryMigrateForRead()
    {
        // Skip migration entirely on read-only connections. Even CREATE TABLE IF NOT EXISTS
        // fails with SQLITE_CANTOPEN on sandboxes that cannot create -journal side files —
        // previously only SQLITE_READONLY was caught, so the normal --db /path flow threw
        // on restricted mounts even after the constructor had already degraded to read-only.
        // read-only 接続ではマイグレーション DDL 自体を走らせない。CANTOPEN が漏れて落ちるため。
        if (_isReadOnly) return;

        LastMigrationFailure = null;
        if (ReadMigrationSchemaIsCurrent())
            return;

        try
        {
            if (IsSqliteTransactionActive())
            {
                RunReadMigrationSteps(MigrationTransactionOwnership.External);
                return;
            }

            EnsureForeignKeysEnabled();
            SqliteTransaction transaction;
            try
            {
                transaction = ReadMigrationTransactionFactoryForTesting?.Invoke(_connection)
                    ?? _connection.BeginTransaction(deferred: false);
            }
            catch (SqliteException ex) when (IsReadOnlyOpenError(ex, _connection.DataSource))
            {
                RecordMigrationFailure("BEGIN IMMEDIATE schema migration", ex);
                return;
            }

            using (transaction)
            {
                _activeMigrationTransaction = transaction;
                try
                {
                    if (!RunReadMigrationSteps(MigrationTransactionOwnership.Owned))
                        return;
                    transaction.Commit();
                }
                finally
                {
                    _activeMigrationTransaction = null;
                }
            }

            EnsureForeignKeysEnabled();
        }
        finally
        {
            _activeMigrationTransaction = null;
            // Migration may have added columns or indexes the schema cache had already
            // resolved as missing; drop the cache so the next DbReader sees the new shape.
            // マイグレーションで列・index が追加された可能性があるためキャッシュを破棄する。
            _schemaCache?.Refresh();
        }
    }

    private bool RunReadMigrationSteps(MigrationTransactionOwnership ownership)
    {
        if (ownership == MigrationTransactionOwnership.None)
            throw new InvalidOperationException("Read migration transaction ownership must be explicit.");

        var previousOwnership = _migrationTransactionOwnership;
        _migrationTransactionOwnership = ownership;
        try
        {
            foreach (var (description, action) in BuildReadMigrationSteps())
            {
                try
                {
                    action();
                }
                catch (SqliteException ex)
                {
                    RecordMigrationFailure(description, ex);

                    // Read-only DB / filesystem / sandbox — stop further steps and degrade.
                    // Catches SQLITE_READONLY (8) and compatible SQLITE_CANTOPEN (14):
                    // some restricted environments report CANTOPEN when SQLite tries to create
                    // -journal side files for the DDL. DbReader.LoadColumns() / table-detection
                    // will drive the degraded read path; later read queries that hit a still-
                    // missing column will now have a single clear preceding diagnostic to refer to.
                    // 読み取り専用 DB・FS・サンドボックスでの DDL 失敗は縮退扱いで打ち切る。
                    if (IsReadOnlyOpenError(ex, _connection.DataSource)) return false;

                    // Other SQLite errors (e.g. corruption, full disk) are not opportunistic-
                    // migration concerns — preserve the existing surface-the-exception behavior.
                    // それ以外の SQLite エラーは従来通り上位に伝播させる。
                    throw;
                }
            }

            return true;
        }
        finally
        {
            _migrationTransactionOwnership = previousOwnership;
        }
    }

    private void RecordMigrationFailure(string description, SqliteException exception)
    {
        var failure = new DbMigrationFailure(
            description,
            exception.SqliteErrorCode,
            FormatMigrationSqliteMessage(exception),
            BuildMigrationSuggestedAction(exception.SqliteErrorCode));
        LastMigrationFailure = failure;
        EmitMigrationFailureWarning(failure);
    }

    private IEnumerable<(string Description, Action Action)> BuildReadMigrationSteps()
    {
        // The order here matches the legacy inline migration: tables before the columns and
        // indexes that reference them, and fold columns before the folded indexes (#86).
        // 並び順は legacy インラインマイグレーションと同じ。テーブル→列→index、fold 列→folded index。
        yield return ("CREATE INDEX bounded resource read chunk indexes", EnsureBoundedResourceReadChunkIndexes);
        yield return ("CREATE TABLE reference_lines", () => Execute(@"
            CREATE TABLE IF NOT EXISTS reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )"));
        yield return ("CREATE TABLE symbol_references", () => Execute(@"
            CREATE TABLE IF NOT EXISTS symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT,
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT,
                container_name  TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )"));
        yield return ("EnsureColumn symbol_references.reference_line_id",
            () => EnsureColumn("symbol_references", "reference_line_id", "INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL"));
        yield return ("EnsureColumn symbol_references.is_self_reference",
            () => EnsureColumn("symbol_references", "is_self_reference", "INTEGER NOT NULL DEFAULT 0"));
        yield return ("EnsureColumn symbol_references.is_mutual_recursion",
            () => EnsureColumn("symbol_references", "is_mutual_recursion", "INTEGER NOT NULL DEFAULT 0"));
        yield return ("CREATE TABLE hotspot_reference_counts",
            () => Execute(HotspotReferenceAggregateSql.CreateTableSql));
        foreach (var indexSql in HotspotReferenceAggregateSql.CreateIndexSql)
            yield return ("CREATE INDEX hotspot_reference_counts", () => Execute(indexSql));
        yield return ("EnsureColumn symbol_references.source_symbol_id",
            () => EnsureColumn("symbol_references", "source_symbol_id", "INTEGER"));
        yield return ("EnsureColumn symbol_references.target_symbol_id",
            () => EnsureColumn("symbol_references", "target_symbol_id", "INTEGER"));
        yield return ("EnsureColumn symbol_references.target_symbol_key",
            () => EnsureColumn("symbol_references", "target_symbol_key", "TEXT"));
        yield return ("EnsureColumn symbol_references.target_qualifier",
            () => EnsureColumn("symbol_references", "target_qualifier", "TEXT"));
        yield return ("EnsureColumn symbol_references.resolution_state",
            () => EnsureColumn("symbol_references", "resolution_state", "TEXT"));
        yield return ("EnsureColumn symbol_references.resolution_candidate_count",
            () => EnsureColumn("symbol_references", "resolution_candidate_count", "INTEGER NOT NULL DEFAULT 0"));
        yield return ("CREATE TABLE symbol_reference_candidates", () => Execute(@"
            CREATE TABLE IF NOT EXISTS symbol_reference_candidates (
                reference_id INTEGER NOT NULL,
                symbol_id    INTEGER NOT NULL,
                scope_rank   INTEGER NOT NULL,
                 PRIMARY KEY(reference_id, symbol_id)
             )"));
        yield return ("CREATE INDEX idx_symbol_refs_name",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name      ON symbol_references(symbol_name)"));
        yield return ("CREATE INDEX idx_symbol_refs_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_file      ON symbol_references(file_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_container",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container ON symbol_references(container_name)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_kind   ON symbol_references(symbol_name, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_file   ON symbol_references(symbol_name, file_id)"));
        yield return ("CREATE INDEX idx_reference_lines_file_line",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_reference_lines_file_line ON reference_lines(file_id, line)"));
        yield return ("CREATE INDEX idx_symbol_refs_reference_line",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_reference_line ON symbol_references(reference_line_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase      ON symbol_references(symbol_name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase ON symbol_references(container_name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_nocase_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_nocase_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_nocase_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_source_symbol",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_source_symbol ON symbol_references(source_symbol_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_target_symbol",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_target_symbol ON symbol_references(target_symbol_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_resolved_source_target_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_resolved_source_target_kind ON symbol_references(source_symbol_id, target_symbol_id, reference_kind) WHERE source_symbol_id IS NOT NULL AND target_symbol_id IS NOT NULL"));
        yield return ("CREATE INDEX idx_symbol_ref_candidates_symbol",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_ref_candidates_symbol ON symbol_reference_candidates(symbol_id, reference_id)"));

        yield return ("EnsureColumn files.lang", () => EnsureColumn("files", "lang", "TEXT"));
        yield return ("EnsureColumn files.checksum", () => EnsureColumn("files", "checksum", "TEXT"));
        yield return ("EnsureColumn files.modified", () => EnsureColumn("files", "modified", "DATETIME"));
        yield return ("EnsureColumn files.indexed_at", () => EnsureColumn("files", "indexed_at", "DATETIME"));
        yield return ("EnsureColumn symbols.start_line", () => EnsureColumn("symbols", "start_line", "INTEGER"));
        yield return ("EnsureColumn symbols.end_line", () => EnsureColumn("symbols", "end_line", "INTEGER"));
        yield return ("EnsureColumn symbols.body_start_line", () => EnsureColumn("symbols", "body_start_line", "INTEGER"));
        yield return ("EnsureColumn symbols.body_end_line", () => EnsureColumn("symbols", "body_end_line", "INTEGER"));
        yield return ("EnsureColumn symbols.signature", () => EnsureColumn("symbols", "signature", "TEXT"));
        yield return ("EnsureColumn symbols.container_kind", () => EnsureColumn("symbols", "container_kind", "TEXT"));
        yield return ("EnsureColumn symbols.container_name", () => EnsureColumn("symbols", "container_name", "TEXT"));
        yield return ("EnsureColumn symbols.container_qualified_name", () => EnsureColumn("symbols", "container_qualified_name", "TEXT"));
        yield return ("EnsureColumn symbols.family_key", () => EnsureColumn("symbols", "family_key", "TEXT"));
        yield return ("EnsureColumn symbols.visibility", () => EnsureColumn("symbols", "visibility", "TEXT"));
        yield return ("EnsureColumn symbols.return_type", () => EnsureColumn("symbols", "return_type", "TEXT"));
        yield return ("EnsureColumn symbols.is_metadata_target", () => EnsureColumn("symbols", "is_metadata_target", "INTEGER"));
        yield return ("EnsureColumn symbols.metadata_target_source", () => EnsureColumn("symbols", "metadata_target_source", "TEXT"));
        yield return ("CREATE INDEX idx_symbols_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_nocase ON symbols(name COLLATE NOCASE)"));

        // #86: fold columns must be ensured BEFORE the folded indexes so CREATE INDEX does
        // not fail on legacy DBs where the column did not exist yet.
        // #86: folded 列を追加してから folded index を作らないと legacy DB でクラッシュする。
        yield return ("EnsureColumn symbols.name_folded", () => EnsureColumn("symbols", "name_folded", "TEXT"));
        yield return ("EnsureColumn symbol_references.symbol_name_folded", () => EnsureColumn("symbol_references", "symbol_name_folded", "TEXT"));
        yield return ("EnsureColumn symbol_references.container_name_folded", () => EnsureColumn("symbol_references", "container_name_folded", "TEXT"));
        yield return ("CREATE INDEX idx_symbols_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded                ON symbols(name_folded)"));
        yield return ("CREATE INDEX idx_symbols_file_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file_name_folded ON symbols(file_id, name_folded)"));
        yield return ("CREATE INDEX idx_symbols_file_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file_name_nocase ON symbols(file_id, name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbols_name_folded_container_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded_container_name_nocase ON symbols(name_folded, container_name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbols_name_folded_container_qualified_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded_container_qualified_name_nocase ON symbols(name_folded, container_qualified_name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbol_refs_symbol_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded     ON symbol_references(symbol_name_folded)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded  ON symbol_references(container_name_folded)"));
        yield return ("CREATE INDEX idx_symbol_refs_symbol_name_folded_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_symbol_name_folded_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_name_folded_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)"));
        yield return ("Backfill hotspot_reference_counts",
            () => Execute(HotspotReferenceAggregateSql.BuildRefreshSql(singleFile: false)));
        yield return ("Stamp hotspot_reference_counts readiness", MarkHotspotReferenceAggregateReady);

        yield return ("CREATE TABLE file_issues", () => Execute(@"
            CREATE TABLE IF NOT EXISTS file_issues (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT NOT NULL,
                line            INTEGER NOT NULL DEFAULT 0,
                message         TEXT NOT NULL
            )"));
        yield return ("CREATE TABLE codeindex_meta", () => Execute(@"
            CREATE TABLE IF NOT EXISTS codeindex_meta (
                key    TEXT PRIMARY KEY NOT NULL,
                value  TEXT
            )"));
        yield return ("Initialize resources/list generation", () => Execute(EnsureResourceListGenerationSql));
        yield return ("CREATE TRIGGER files_resource_generation_ai", () => Execute(CreateResourceListGenerationInsertTriggerSql));
        yield return ("CREATE TRIGGER files_resource_generation_ad", () => Execute(CreateResourceListGenerationDeleteTriggerSql));
        yield return ("CREATE TRIGGER files_resource_generation_au", () => Execute(CreateResourceListGenerationUpdateTriggerSql));
    }

    private void EnsureBoundedResourceReadChunkIndexes()
    {
        if (!TableExists("chunks"))
            return;

        Execute("CREATE INDEX IF NOT EXISTS idx_chunks_file_end_start_nonnull ON chunks(file_id, end_line, start_line, chunk_index) WHERE content IS NOT NULL");
        Execute("CREATE INDEX IF NOT EXISTS idx_chunks_file_start_chunk_nonnull ON chunks(file_id, start_line, chunk_index, end_line) WHERE content IS NOT NULL");
    }

    private bool ReadMigrationSchemaIsCurrent()
    {
        if ((GetUserVersion() & HotspotReferenceAggregateReadyFlag) == 0)
            return false;

        foreach (var table in ReadMigrationRequiredTables)
        {
            if (!TableExists(table))
                return false;
        }

        foreach (var (table, column) in ReadMigrationRequiredColumns)
        {
            if (!ColumnExists(table, column))
                return false;
        }

        foreach (var index in ReadMigrationRequiredIndexes)
        {
            if (!IndexExists(index))
                return false;
        }

        foreach (var trigger in ResourceListGenerationTriggerNames)
        {
            if (!TriggerExists(trigger))
                return false;
        }

        return true;
    }

    private string BuildMigrationSuggestedAction(int sqliteErrorCode)
    {
        // 8 = SQLITE_READONLY, 10 = SQLITE_IOERR, 14 = SQLITE_CANTOPEN: classic restricted-
        // mount signatures (network share, sandbox, WORM). Point the user at the same fix
        // we already document for the read-only fallback so the message is actionable.
        // 8/10/14 は restricted mount 系の典型シグネチャ。書き込み可能な場所での再実行を案内する。
        if (sqliteErrorCode is 8 or 10 or 14)
        {
            return "Re-run cdidx on writable storage, or grant write access to <db directory> (for example, chmod +w <db directory>), so the schema migration can complete.";
        }

        // Unknown SQLite codes — surface the code itself and point at integrity check.
        // それ以外の SQLite エラーは integrity_check と error code を案内する。
        return $"Inspect the database with 'sqlite3 <db> \"PRAGMA integrity_check\"' (SQLite error code {sqliteErrorCode}).";
    }

    private static string FormatMigrationSqliteMessage(SqliteException exception)
        => DiagnosticRedactor.FormatExceptionMessage(exception, MigrationDiagnosticTextLimit);

    private static void EmitMigrationFailureWarning(DbMigrationFailure failure)
    {
        // Single line so the next read attempt only sees one clear "migration partial" record
        // even if multiple commands share the same process / log stream.
        // 1 行に集約し、後続 read エラーと混在しても拾いやすい形にする。
        CommandErrorWriter.WriteStderr(
            $"Warning: cdidx schema migration step \"{failure.Step}\" failed " +
            $"(SQLite error {failure.SqliteErrorCode}: {failure.SqliteMessage.TrimEnd('.')}). " +
            "Subsequent read queries may fail with 'no such column' until the migration completes. " +
            failure.SuggestedAction);
    }

    private void EnsureColumn(string tableName, string columnName, string definition)
    {
        var quotedTableName = SqliteIdentifier.Quote(tableName);
        var quotedColumnName = SqliteIdentifier.Quote(columnName);
        if (_migrationTransactionOwnership != MigrationTransactionOwnership.None)
        {
            DbColumnEnsurer.EnsureColumn(
                () => ColumnExists(tableName, columnName),
                () => Execute($"ALTER TABLE {quotedTableName} ADD COLUMN {quotedColumnName} {definition}"));
            return;
        }

        DbColumnEnsurer.EnsureColumn(
            () => ColumnExists(tableName, columnName),
            beginImmediate: () => Execute("BEGIN IMMEDIATE"),
            commit: () => Execute("COMMIT"),
            rollback: () => Execute("ROLLBACK"),
            () => Execute($"ALTER TABLE {quotedTableName} ADD COLUMN {quotedColumnName} {definition}"));
    }

    private bool ColumnExists(string tableName, string columnName)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IndexExists(string name)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        return cmd.ExecuteScalar() != null;
    }

    private bool TriggerExists(string name)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'trigger' AND name = @name";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        return cmd.ExecuteScalar() != null;
    }

    private string ExecuteScalar(string sql)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

}
